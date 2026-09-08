using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using RavensPort.Core.Diagnostics;
using RavensPort.Core.Models;
using RavensPort.Core.Storage;

namespace RavensPort.Core.Mcp;

/// <summary>
/// Turns one funnel into a set of MCP server handlers that fan out to its sources.
///
/// Everything is resolved from the config store at request time, never captured when the endpoint
/// was mapped. That is what makes an edit in the GUI — unticking a tool, adding a source —
/// visible on the agent's very next call, with no session to invalidate and no
/// notifications/tools/list_changed to plumb.
///
/// The two rules that matter:
///   • A source that is down degrades only itself. Listing catches per source and returns what
///     the healthy ones offered, because one unreachable server must not blank an agent's whole
///     toolset.
///   • Filtering is enforced on the call path as well as the list path. An agent that learned a
///     tool name before it was unticked would otherwise keep calling it successfully.
///   • Nothing a funnel returns is cacheable by anyone else, and its lists are not cacheable at
///     all. That is the same rule as the one above seen from the client's side: a cached
///     tools/list would survive the untick that the call path is there to enforce. See
///     McpProtocolExtensions.AsFunnelCacheable.
/// </summary>
public sealed class McpFunnelHandlerFactory
{
    private readonly ConfigStoreCache _configStoreCache;
    private readonly McpSourceConnectionPool _connectionPool;
    private readonly ActivityLog _activityLog;

    public McpFunnelHandlerFactory(
        ConfigStoreCache configStoreCache,
        McpSourceConnectionPool connectionPool,
        ActivityLog activityLog)
    {
        _configStoreCache = configStoreCache;
        _connectionPool = connectionPool;
        _activityLog = activityLog;
    }

    /// <summary>A funnel's membership row paired with the source record it points at.</summary>
    private readonly record struct SourceLink(McpFunnelSource Link, McpSourceRecord Source);

    public McpFunnelRecord? FindFunnel(string? slug) =>
        string.IsNullOrEmpty(slug)
            ? null
            : _configStoreCache.Current.McpFunnels
                .FirstOrDefault(f => f.Enabled && string.Equals(f.Slug, slug, StringComparison.OrdinalIgnoreCase));

    public McpServerHandlers Create(Guid funnelId, string funnelName) => new()
    {
        ListToolsHandler = (_, ct) => ListToolsAsync(funnelId, funnelName, ct),
        CallToolHandler = (request, ct) => CallToolAsync(funnelId, funnelName, request, ct),
        ListPromptsHandler = (_, ct) => ListPromptsAsync(funnelId, funnelName, ct),
        GetPromptHandler = (request, ct) => GetPromptAsync(funnelId, request, ct),
        ListResourcesHandler = (_, ct) => ListResourcesAsync(funnelId, funnelName, ct),
        ListResourceTemplatesHandler = (_, ct) => ListResourceTemplatesAsync(funnelId, ct),
        ReadResourceHandler = (request, ct) => ReadResourceAsync(funnelId, request, ct),
    };

    private List<SourceLink> ResolveSources(Guid funnelId)
    {
        var store = _configStoreCache.Current;
        var funnel = store.McpFunnels.FirstOrDefault(f => f.Id == funnelId);
        if (funnel is null) return [];

        var resolved = new List<SourceLink>();

        foreach (var link in funnel.Sources)
        {
            var source = store.McpSources.FirstOrDefault(s => s.Id == link.SourceId);
            if (source is null || !source.Enabled) continue;

            resolved.Add(new SourceLink(link, source));
        }

        return resolved;
    }

    private SourceLink? ResolveByAlias(Guid funnelId, string alias) =>
        ResolveSources(funnelId)
            .Cast<SourceLink?>()
            .FirstOrDefault(s => string.Equals(s!.Value.Source.Alias, alias, StringComparison.OrdinalIgnoreCase));

    // ---- tools -----------------------------------------------------------------------------

    private async ValueTask<ListToolsResult> ListToolsAsync(Guid funnelId, string funnelName, CancellationToken ct)
    {
        var tools = new List<Tool>();
        var failures = new List<string>();

        foreach (var (link, source) in ResolveSources(funnelId))
        {
            try
            {
                foreach (var tool in await DrainAsync(
                             funnelId, source,
                             (client, cursor, token) => client.ListToolsAsync(new ListToolsRequestParams { Cursor = cursor }, token),
                             page => (page.Tools, page.NextCursor),
                             ct))
                {
                    if (!link.AllowsTool(tool.Name)) continue;

                    if (McpNameMapper.IsTruncated(source.Alias, tool.Name))
                    {
                        // Exposing it would produce a name that cannot be routed back.
                        _activityLog.Log($"MCP funnel '{funnelName}' skipped '{source.Alias}' tool '{tool.Name}' — prefixed name exceeds {McpNameMapper.MaxNameLength} characters");
                        continue;
                    }

                    tools.Add(tool.WithName(McpNameMapper.Encode(source.Alias, tool.Name)));
                }
            }
            catch (Exception ex)
            {
                failures.Add(source.Alias);
                _activityLog.Log($"MCP funnel '{funnelName}' could not list tools from '{source.Name}' — {ex.Message}");
            }
        }

        _activityLog.Log($"MCP funnel '{funnelName}' tools/list -> {tools.Count} tools{DescribeFailures(failures)}");

        return new ListToolsResult { Tools = Ordered(tools, t => t.Name) }.AsFunnelCacheable();
    }

    private async ValueTask<CallToolResult> CallToolAsync(
        Guid funnelId, string funnelName, RequestContext<CallToolRequestParams> request, CancellationToken ct)
    {
        var exposedName = request.Params?.Name ?? "";

        if (!McpNameMapper.TryDecode(exposedName, out var alias, out var upstreamName))
        {
            throw new McpException($"Unknown tool '{exposedName}'.");
        }

        if (ResolveByAlias(funnelId, alias) is not { } match)
        {
            throw new McpException($"Unknown tool '{exposedName}'.");
        }

        // Deliberately the same message as "unknown": whether a tool exists upstream but was
        // filtered out is not something a caller of this funnel is entitled to learn.
        if (!match.Link.AllowsTool(upstreamName))
        {
            throw new McpException($"Unknown tool '{exposedName}'.");
        }

        try
        {
            var result = await _connectionPool.ExecuteAsync(
                funnelId,
                match.Source,
                (client, token) => client.CallToolAsync(
                    new CallToolRequestParams
                    {
                        Name = upstreamName,
                        Arguments = request.Params?.Arguments,
                        // Multi round-trip requests, 2026-07-28. An agent that was asked for
                        // more input retries the same call carrying the answers, and both fields
                        // ride through untouched: requestState is the source's own and means
                        // nothing here, and there is nothing for the funnel to correlate, because
                        // the alias on the tool name already routes the retry to the source that
                        // asked. See ExplainInputRequired for the direction that does not work.
                        InputResponses = request.Params?.InputResponses,
                        RequestState = request.Params?.RequestState,
                    },
                    token),
                isIdempotent: false,
                ct).ConfigureAwait(false);

            // Arguments are never logged — they routinely carry the user's own data.
            _activityLog.Log($"MCP funnel '{funnelName}' call {exposedName} -> {(result.IsError == true ? "tool error" : "ok")}");

            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var explained = ExplainInputRequired(ex, match.Source.Alias);

            _activityLog.Log($"MCP funnel '{funnelName}' call {exposedName} -> failed: {explained ?? ex.Message}");

            if (explained is not null) throw new McpException(explained);

            throw;
        }
    }

    // ---- prompts ---------------------------------------------------------------------------

    private async ValueTask<ListPromptsResult> ListPromptsAsync(Guid funnelId, string funnelName, CancellationToken ct)
    {
        var prompts = new List<Prompt>();
        var failures = new List<string>();

        foreach (var (link, source) in ResolveSources(funnelId))
        {
            try
            {
                foreach (var prompt in await DrainAsync(
                             funnelId, source,
                             (client, cursor, token) => client.ListPromptsAsync(new ListPromptsRequestParams { Cursor = cursor }, token),
                             page => (page.Prompts, page.NextCursor),
                             ct))
                {
                    if (!link.AllowsPrompt(prompt.Name)) continue;
                    if (McpNameMapper.IsTruncated(source.Alias, prompt.Name)) continue;

                    prompts.Add(prompt.WithName(McpNameMapper.Encode(source.Alias, prompt.Name)));
                }
            }
            catch (Exception ex)
            {
                failures.Add(source.Alias);
                _activityLog.Log($"MCP funnel '{funnelName}' could not list prompts from '{source.Name}' — {ex.Message}");
            }
        }

        return new ListPromptsResult { Prompts = Ordered(prompts, p => p.Name) }.AsFunnelCacheable();
    }

    private async ValueTask<GetPromptResult> GetPromptAsync(
        Guid funnelId, RequestContext<GetPromptRequestParams> request, CancellationToken ct)
    {
        var exposedName = request.Params?.Name ?? "";

        if (!McpNameMapper.TryDecode(exposedName, out var alias, out var upstreamName) ||
            ResolveByAlias(funnelId, alias) is not { } match ||
            !match.Link.AllowsPrompt(upstreamName))
        {
            throw new McpException($"Unknown prompt '{exposedName}'.");
        }

        return await _connectionPool.ExecuteAsync(
            funnelId,
            match.Source,
            (client, token) => client.GetPromptAsync(
                new GetPromptRequestParams
                {
                    Name = upstreamName,
                    Arguments = request.Params?.Arguments,
                    InputResponses = request.Params?.InputResponses,
                    RequestState = request.Params?.RequestState,
                },
                token),
            isIdempotent: true,
            ct).ConfigureAwait(false);
    }

    // ---- resources -------------------------------------------------------------------------

    private async ValueTask<ListResourcesResult> ListResourcesAsync(Guid funnelId, string funnelName, CancellationToken ct)
    {
        var resources = new List<Resource>();
        var failures = new List<string>();

        foreach (var (link, source) in ResolveSources(funnelId))
        {
            try
            {
                foreach (var resource in await DrainAsync(
                             funnelId, source,
                             (client, cursor, token) => client.ListResourcesAsync(new ListResourcesRequestParams { Cursor = cursor }, token),
                             page => (page.Resources, page.NextCursor),
                             ct))
                {
                    if (!link.AllowsResource(resource.Uri)) continue;

                    resources.Add(resource.WithNameAndUri(
                        McpNameMapper.Encode(source.Alias, resource.Name),
                        McpNameMapper.EncodeResourceUri(source.Alias, resource.Uri)));
                }
            }
            catch (Exception ex)
            {
                failures.Add(source.Alias);
                _activityLog.Log($"MCP funnel '{funnelName}' could not list resources from '{source.Name}' — {ex.Message}");
            }
        }

        return new ListResourcesResult { Resources = Ordered(resources, r => r.Uri) }.AsFunnelCacheable();
    }

    private async ValueTask<ListResourceTemplatesResult> ListResourceTemplatesAsync(Guid funnelId, CancellationToken ct)
    {
        var templates = new List<ResourceTemplate>();

        foreach (var (link, source) in ResolveSources(funnelId))
        {
            try
            {
                foreach (var template in await DrainAsync(
                             funnelId, source,
                             (client, cursor, token) => client.ListResourceTemplatesAsync(new ListResourceTemplatesRequestParams { Cursor = cursor }, token),
                             page => (page.ResourceTemplates, page.NextCursor),
                             ct))
                {
                    if (!link.AllowsResource(template.UriTemplate)) continue;

                    templates.Add(template.WithNameAndTemplate(
                        McpNameMapper.Encode(source.Alias, template.Name),
                        McpNameMapper.EncodeResourceUriTemplate(source.Alias, template.UriTemplate)));
                }
            }
            catch
            {
                // Templates are optional and many servers have none; a failure here is not worth
                // a log line of its own, since listing resources against the same source already
                // reported it.
            }
        }

        return new ListResourceTemplatesResult
        {
            ResourceTemplates = Ordered(templates, t => t.UriTemplate),
        }.AsFunnelCacheable();
    }

    private async ValueTask<ReadResourceResult> ReadResourceAsync(
        Guid funnelId, RequestContext<ReadResourceRequestParams> request, CancellationToken ct)
    {
        var exposedUri = request.Params?.Uri ?? "";

        if (!McpNameMapper.TryDecodeResourceUri(exposedUri, out var alias, out var upstreamUri) ||
            ResolveByAlias(funnelId, alias) is not { } match)
        {
            throw new McpException($"Unknown resource '{exposedUri}'.");
        }

        // A template-derived URI is not in the selection list by its expanded form, so a strict
        // membership test would break every templated read. Only an explicit Exclude entry —
        // which names a URI the user deliberately blocked — is enforced here.
        if (match.Link.ResourceMode == McpSelectionMode.Exclude && !match.Link.AllowsResource(upstreamUri))
        {
            throw new McpException($"Unknown resource '{exposedUri}'.");
        }

        var result = await _connectionPool.ExecuteAsync(
            funnelId,
            match.Source,
            (client, token) => client.ReadResourceAsync(
                new ReadResourceRequestParams
                {
                    Uri = upstreamUri,
                    InputResponses = request.Params?.InputResponses,
                    RequestState = request.Params?.RequestState,
                },
                token),
            isIdempotent: true,
            ct).ConfigureAwait(false);

        // The one place an upstream ttl is worth relaying: how long the bytes of a resource stay
        // good is the source's judgement and nothing the funnel knows better. Its scope is still
        // forced to Private -- see AsFunnelCacheable.
        return result.AsFunnelCacheable(result.TimeToLive);
    }

    // ---- plumbing --------------------------------------------------------------------------

    /// <summary>
    /// Pulls every page a source will give for one primitive kind and returns them as one list.
    ///
    /// The funnel cannot forward cursors: they are opaque strings minted by whichever upstream
    /// issued them, and several sources' cursors cannot be combined into one the client could
    /// send back. Draining here is what lets the funnel answer as a single unpaginated page.
    /// </summary>
    private async ValueTask<List<TItem>> DrainAsync<TPage, TItem>(
        Guid funnelId,
        McpSourceRecord source,
        Func<McpClient, string?, CancellationToken, ValueTask<TPage>> fetchPage,
        Func<TPage, (IList<TItem> Items, string? NextCursor)> readPage,
        CancellationToken ct)
    {
        var items = new List<TItem>();
        string? cursor = null;

        for (var page = 0; page < McpProtocolExtensions.MaxPages; page++)
        {
            var captured = cursor;

            var response = await _connectionPool.ExecuteAsync(
                funnelId,
                source,
                (client, token) => fetchPage(client, captured, token),
                isIdempotent: true,
                ct).ConfigureAwait(false);

            var (pageItems, nextCursor) = readPage(response);
            items.AddRange(pageItems);

            if (string.IsNullOrEmpty(nextCursor)) break;
            cursor = nextCursor;
        }

        return items;
    }

    /// <summary>
    /// Turns the one confusing failure the 2026-07-28 round-trip rules can produce into a sentence
    /// naming the source that caused it.
    ///
    /// A funnel is a proxy, and the one thing it cannot proxy is a question. When a source answers
    /// input_required, the client SDK does not hand that back to its caller -- it answers the
    /// question itself from the handlers registered on the client, and throws if none is. So the
    /// funnel cannot simply forward the request to the agent and forward the answer back: by the
    /// time it could, the upstream call has already failed. Doing it properly would mean holding
    /// the half-finished upstream call open across two separate agent requests, and minting a
    /// requestState of our own to pair them with, which is precisely the per-call session this
    /// funnel is built not to have.
    ///
    /// What the funnel does instead is not offer. It registers no elicitation, sampling, or roots
    /// handler, so it advertises none of those capabilities upstream, and a source that respects
    /// them will never ask. A source that asks anyway gets this message, which at least says which
    /// source it was and why the call could not be completed, rather than the SDK's generic
    /// "an error occurred invoking" -- an operator can then untick the offending tool.
    ///
    /// The other direction does work and is handled above: an agent that was asked for input by
    /// something further along retries through this funnel, and its answers are relayed on.
    /// </summary>
    private static string? ExplainInputRequired(Exception ex, string alias) =>
        ex is InvalidOperationException && ex.Message.Contains("input request", StringComparison.OrdinalIgnoreCase)
            ? $"Source '{alias}' asked for input mid-call, which this funnel cannot relay to the agent. " +
              "The tool needs to be driven directly rather than through a funnel."
            : null;

    /// <summary>
    /// Fixes the order of a list result.
    ///
    /// 2026-07-28 asks servers to answer tools/list deterministically, so that clients can cache
    /// the list and so that an unchanged toolset keeps producing an unchanged prompt prefix.
    /// Source order alone does not get there: the funnel walks its sources in a stable order, but
    /// each upstream is free to return its own tools in a different order from one call to the
    /// next, and a single reordered source would shift everything after it. Sorting on the
    /// exposed name pins the whole list regardless of what the sources do.
    ///
    /// Applied to all four list endpoints rather than tools alone, because the same caching
    /// argument holds for each and one rule is easier to keep true than four.
    /// </summary>
    private static List<T> Ordered<T>(List<T> items, Func<T, string> key) =>
        [.. items.OrderBy(key, StringComparer.Ordinal)];

    private static string DescribeFailures(List<string> failures) =>
        failures.Count == 0 ? "" : $" ({failures.Count} source(s) unavailable: {string.Join(", ", failures)})";
}
