using System.Net;
using System.Text;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using RavensPort.Core.Diagnostics;
using RavensPort.Core.Models;
using RavensPort.Core.Proxy;
using RavensPort.Core.Storage;

namespace RavensPort.Core.Mcp;

/// <summary>
/// Turns one bridge's manifest into a set of MCP server handlers.
///
/// Everything is resolved from the config store at request time, never captured when the endpoint
/// was mapped — the same rule as <see cref="McpFunnelHandlerFactory"/>, and for the same reason:
/// a manifest re-imported in the GUI takes effect on the agent's very next call, with no session
/// to invalidate and no notification to plumb.
///
/// Three rules worth stating, because each is load-bearing:
///   • Only tools make an HTTP call. Prompts and skills are answered from the manifest itself, so
///     a skill can never be a request to the upstream.
///   • Argument problems come back as a tool result with IsError, not as a protocol error. The
///     model can act on "id is required"; it can do nothing with "invalid request".
///   • Nothing this endpoint returns is cacheable by anyone else, and its lists are not cacheable
///     at all. A manifest edit raises no notification of any kind, so a cached tools/list would
///     survive the edit that removed the tool.
/// </summary>
public sealed class McpApiBridgeHandlerFactory
{
    /// <summary>
    /// Ceiling on how much of an upstream answer is carried back into the agent's context.
    /// Streamed into a bounded buffer rather than read whole: an upstream export of a few hundred
    /// megabytes must not be materialised in a tray app's heap.
    /// </summary>
    internal const int MaxResponseBytes = 1024 * 1024;

    /// <summary>
    /// Per-call budget. Deliberately shorter than the connection pool's two minutes: that is a
    /// *connect* budget for cold-starting serverless MCP servers, while this is one REST call, and
    /// holding an agent for two minutes on a request that is not coming back helps nobody.
    /// </summary>
    internal static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(100);

    private readonly ConfigStoreCache _configStoreCache;
    private readonly LoopbackHttpClient _loopback;
    private readonly ActivityLog _activityLog;

    public McpApiBridgeHandlerFactory(
        ConfigStoreCache configStoreCache,
        LoopbackHttpClient loopback,
        ActivityLog activityLog)
    {
        _configStoreCache = configStoreCache;
        _loopback = loopback;
        _activityLog = activityLog;
    }

    public McpApiBridgeRecord? FindBridge(string? slug) =>
        string.IsNullOrEmpty(slug)
            ? null
            : _configStoreCache.Current.McpApiBridges
                .FirstOrDefault(b => b.Enabled && string.Equals(b.Slug, slug, StringComparison.OrdinalIgnoreCase));

    public McpServerHandlers Create(Guid bridgeId, string bridgeName) => new()
    {
        ListToolsHandler = (_, _) => ValueTask.FromResult(ListTools(bridgeId)),
        CallToolHandler = (request, ct) => CallToolAsync(bridgeId, bridgeName, request, ct),
        ListPromptsHandler = (_, _) => ValueTask.FromResult(ListPrompts(bridgeId)),
        GetPromptHandler = (request, _) => ValueTask.FromResult(GetPrompt(bridgeId, request)),
        ListResourcesHandler = (_, _) => ValueTask.FromResult(ListResources(bridgeId)),
        ReadResourceHandler = (request, _) => ValueTask.FromResult(ReadResource(bridgeId, request)),
    };

    private McpApiBridgeManifest? Manifest(Guid bridgeId) =>
        _configStoreCache.Current.McpApiBridges.FirstOrDefault(b => b.Id == bridgeId && b.Enabled)?.Manifest;

    // ---- tools ------------------------------------------------------------------------------

    private ListToolsResult ListTools(Guid bridgeId)
    {
        var manifest = Manifest(bridgeId);

        var tools = manifest is null
            ? []
            : manifest.Tools.Select(Describe).OrderBy(t => t.Name, StringComparer.Ordinal).ToList();

        return new ListToolsResult { Tools = tools }.AsFunnelCacheable();
    }

    private static Tool Describe(McpApiBridgeTool tool) => new()
    {
        Name = tool.Name,
        Title = tool.Title,
        Description = DescribeWithVariants(tool),

        // Normalized again here, not only at import: a manifest hand-edited straight into the
        // vault note never saw import validation, and the SDK's setter throws on a schema that is
        // not an object — which would blank this whole endpoint rather than one tool.
        InputSchema = McpApiBridgeSchema.Normalize(tool.InputSchema),

        Annotations = new ToolAnnotations { ReadOnlyHint = tool.ReadOnly },
    };

    /// <summary>
    /// The tool's description with its variants spelled out underneath.
    ///
    /// The schema's enum tells a model that "state" is one of three strings. What it cannot say is
    /// what each of them does, and that is exactly the judgement the model is being asked to make
    /// — so each variant's own description is appended as a labelled line.
    /// </summary>
    private static string? DescribeWithVariants(McpApiBridgeTool tool)
    {
        if (!tool.HasVariants) return tool.Description;

        var described = tool.Variants
            .Where(v => !string.IsNullOrWhiteSpace(v.Value.Description))
            .Select(v => $"  {v.Key}: {v.Value.Description}")
            .ToList();

        if (described.Count == 0) return tool.Description;

        var header = string.IsNullOrWhiteSpace(tool.Description) ? "" : tool.Description + "\n\n";

        return $"{header}'{tool.VariantBy}' selects one of:\n{string.Join("\n", described)}";
    }

    private async ValueTask<CallToolResult> CallToolAsync(
        Guid bridgeId, string bridgeName, RequestContext<CallToolRequestParams> request, CancellationToken ct)
    {
        var name = request.Params?.Name ?? "";
        var store = _configStoreCache.Current;
        var bridge = store.McpApiBridges.FirstOrDefault(b => b.Id == bridgeId && b.Enabled);
        var tool = bridge?.Manifest.Tools.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.Ordinal));

        // A tool that is not in this manifest does not exist as far as this caller is concerned,
        // which is the one case a protocol error is for.
        if (bridge is null || tool is null) throw new McpException($"Unknown tool '{name}'.");

        if (store.Routes.FirstOrDefault(r => r.Id == bridge.RouteId) is not { } route)
        {
            // Not a 404 on the endpoint and not a protocol error: the endpoint is fine and the
            // tool exists — the configuration behind it is incomplete, and only the user can fix
            // that. Saying so beats a connection error they would have to decode.
            return Failure($"This bridge's route no longer exists. Pick one on the API to MCP tab.");
        }

        var (built, error) = McpApiBridgeRequestBuilder.Build(tool, request.Params?.Arguments);
        if (error is not null || built is null) return Failure(error ?? "This call could not be built.");

        try
        {
            using var message = new HttpRequestMessage(built.Method, new Uri(_loopback.BaseFor(route), built.PathAndQuery.TrimStart('/')));

            // The route's own key, read live rather than captured, so regenerating it on the
            // Routes tab does not leave every bridge authenticating with a stale one. A route
            // whose key has expired fails here with 403 exactly as any other client would, which
            // is the point of an expiry the user set.
            message.Headers.TryAddWithoutValidation(LocalAccessGuard.ApiKeyHeaderName, route.Key.Value);

            // Marks this as a bridge's own hop. Both gates refuse it, which is what stops a route
            // that resolves back into /api-mcp or /mcp from recursing.
            message.Headers.TryAddWithoutValidation(LocalAccessGuard.BridgeHopHeaderName, "1");

            foreach (var (header, value) in built.Headers)
            {
                message.Headers.TryAddWithoutValidation(header, value);
            }

            if (built.Body is { } body)
            {
                message.Content = new StringContent(body, Encoding.UTF8, built.ContentType);
            }

            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(CallTimeout);

            using var response = await _loopback.Client
                .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, budget.Token)
                .ConfigureAwait(false);

            // Neither the arguments nor the body are logged, and neither is the expanded URL — the
            // expansion *is* the arguments, and both routinely carry the user's own data. The
            // template that identifies the call is already in the manifest.
            _activityLog.Log($"MCP bridge '{bridgeName}' call {tool.Name} -> HTTP {(int)response.StatusCode}");

            if (IsRedirect(response.StatusCode))
            {
                // Not followed, deliberately: re-issuing this request would happen off loopback,
                // without the route's credential, and could replay the body to a third party.
                var location = response.Headers.Location?.ToString() ?? "somewhere else";
                return Failure($"The upstream answered {(int)response.StatusCode} redirecting to {location}. "
                               + "RavensPort does not follow that: the second request would leave this "
                               + "route and would not carry its credential.");
            }

            var text = await ReadCappedAsync(response, budget.Token).ConfigureAwait(false);

            return new CallToolResult
            {
                Content =
                [
                    new TextContentBlock
                    {
                        Text = response.IsSuccessStatusCode
                            ? text
                            : $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}\n{text}",
                    },
                ],
                IsError = !response.IsSuccessStatusCode,
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            _activityLog.Log($"MCP bridge '{bridgeName}' call {tool.Name} -> timed out after {CallTimeout.TotalSeconds:0}s");
            return Failure($"The upstream did not answer within {CallTimeout.TotalSeconds:0} seconds.");
        }
        catch (Exception ex)
        {
            // Summarized, because an upstream's error text is frequently an entire HTML page and
            // this line goes to the activity log.
            var summary = McpSourceCatalog.Summarize(ex.Message);

            _activityLog.Log($"MCP bridge '{bridgeName}' call {tool.Name} -> failed: {summary}");

            return Failure($"The call failed: {summary}");
        }
    }

    private static bool IsRedirect(HttpStatusCode status) => (int)status is >= 300 and < 400;

    /// <summary>
    /// The response body, up to <see cref="MaxResponseBytes"/>.
    ///
    /// Truncation is not an error: the call did succeed, and hiding the whole answer would be
    /// worse than handing back the part that fits with a line saying so.
    /// </summary>
    private static async Task<string> ReadCappedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

        var buffer = new byte[MaxResponseBytes];
        var filled = 0;

        while (filled < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(filled), ct).ConfigureAwait(false);
            if (read == 0) break;

            filled += read;
        }

        var text = Encoding.UTF8.GetString(buffer, 0, filled);

        if (filled < buffer.Length) return text;

        // Whether anything remains is only knowable by reading one more byte, and a body exactly
        // at the cap is not worth a second round trip to describe precisely.
        return text + $"\n\n[truncated at {MaxResponseBytes / 1024} KB by RavensPort]";
    }

    private static CallToolResult Failure(string message) => new()
    {
        Content = [new TextContentBlock { Text = message }],
        IsError = true,
    };

    // ---- prompts ------------------------------------------------------------------------------

    private ListPromptsResult ListPrompts(Guid bridgeId)
    {
        var manifest = Manifest(bridgeId);

        var prompts = manifest is null
            ? []
            : manifest.Prompts
                .Select(prompt => new Prompt
                {
                    Name = prompt.Name,
                    Title = prompt.Title,
                    Description = prompt.Description,
                    Arguments =
                    [
                        .. prompt.Arguments.Select(argument => new PromptArgument
                        {
                            Name = argument.Name,
                            Description = argument.Description,
                            Required = argument.Required,
                        }),
                    ],
                })
                .OrderBy(p => p.Name, StringComparer.Ordinal)
                .ToList();

        return new ListPromptsResult { Prompts = prompts }.AsFunnelCacheable();
    }

    private GetPromptResult GetPrompt(Guid bridgeId, RequestContext<GetPromptRequestParams> request)
    {
        var name = request.Params?.Name ?? "";

        var prompt = Manifest(bridgeId)?.Prompts
            .FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal));

        if (prompt is null) throw new McpException($"Unknown prompt '{name}'.");

        IReadOnlyDictionary<string, JsonElement> arguments = request.Params?.Arguments is { } supplied
            ? supplied.AsReadOnly()
            : new Dictionary<string, JsonElement>();

        var missing = prompt.Arguments
            .FirstOrDefault(a => a.Required && !arguments.ContainsKey(a.Name));

        if (missing is not null)
        {
            throw new McpException($"'{missing.Name}' is required by prompt '{name}'.");
        }

        return new GetPromptResult
        {
            Description = prompt.Description,
            Messages =
            [
                .. prompt.Messages.Select(message => new PromptMessage
                {
                    Role = string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase)
                        ? Role.Assistant
                        : Role.User,
                    Content = new TextContentBlock { Text = Fill(message.Content, arguments) },
                }),
            ],
        };
    }

    /// <summary>
    /// Substitutes a prompt's arguments into its text. An argument that was not supplied leaves
    /// nothing behind — the required ones have already been checked, so anything still missing
    /// here is optional, and "since " reads better than "since {since}".
    /// </summary>
    private static string Fill(string template, IReadOnlyDictionary<string, JsonElement> arguments)
    {
        var builder = new StringBuilder();

        foreach (var segment in McpApiBridgePlaceholders.Parse(template))
        {
            if (!segment.IsPlaceholder)
            {
                builder.Append(segment.Text);
                continue;
            }

            if (!arguments.TryGetValue(segment.Text, out var value)) continue;

            builder.Append(value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText());
        }

        return builder.ToString();
    }

    // ---- skills, as resources -------------------------------------------------------------------

    /// <summary>Scheme for a skill document's URI: <c>skill://{bridge-slug}/{skill-name}</c>.</summary>
    public const string SkillUriScheme = "skill";

    public static string SkillUri(string bridgeSlug, string skillName) =>
        $"{SkillUriScheme}://{bridgeSlug}/{skillName}";

    private ListResourcesResult ListResources(Guid bridgeId)
    {
        var bridge = _configStoreCache.Current.McpApiBridges.FirstOrDefault(b => b.Id == bridgeId && b.Enabled);

        var resources = bridge is null
            ? []
            : bridge.Manifest.Skills
                .Select(skill => new Resource
                {
                    Uri = SkillUri(bridge.Slug, skill.Name),
                    Name = skill.Name,
                    Title = skill.Title,
                    Description = skill.Description,
                    MimeType = "text/markdown",
                })
                .OrderBy(r => r.Uri, StringComparer.Ordinal)
                .ToList();

        return new ListResourcesResult { Resources = resources }.AsFunnelCacheable();
    }

    private ReadResourceResult ReadResource(Guid bridgeId, RequestContext<ReadResourceRequestParams> request)
    {
        var uri = request.Params?.Uri ?? "";
        var bridge = _configStoreCache.Current.McpApiBridges.FirstOrDefault(b => b.Id == bridgeId && b.Enabled);

        var skill = bridge?.Manifest.Skills.FirstOrDefault(s =>
            string.Equals(SkillUri(bridge.Slug, s.Name), uri, StringComparison.Ordinal));

        if (skill is null) throw new McpException($"Unknown resource '{uri}'.");

        return new ReadResourceResult
        {
            Contents =
            [
                new TextResourceContents
                {
                    Uri = uri,
                    MimeType = "text/markdown",
                    Text = skill.Content,
                },
            ],
        }.AsFunnelCacheable();
    }
}
