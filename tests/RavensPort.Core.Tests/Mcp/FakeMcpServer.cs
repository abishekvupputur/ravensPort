using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace RavensPort.Core.Tests.Mcp;

/// <summary>
/// A minimal but genuine MCP server over streamable HTTP, on a real loopback socket.
///
/// Hand-written rather than built from the SDK's server, for two reasons. It has to be able to
/// misbehave on demand — hang on a barrier, start failing, expire a session — which a well-behaved
/// server implementation will not do. And it keeps the tests honest: if both sides of the funnel
/// were the same SDK, a shared misreading of the protocol would pass unnoticed.
///
/// Every response reports the session it was served under, which is what the isolation tests
/// assert on.
/// </summary>
internal sealed class FakeMcpServer : IAsyncDisposable
{
    public const string SessionHeader = "Mcp-Session-Id";

    private readonly WebApplication _app;
    private readonly ConcurrentDictionary<string, byte> _liveSessions = new();

    private FakeMcpServer(WebApplication app, string url)
    {
        _app = app;
        Url = url;
    }

    public string Url { get; }

    /// <summary>Tools this server offers. Mutable so a test can change what it advertises.</summary>
    public List<string> Tools { get; } = ["echo", "whoami", "slow", "alpha", "beta"];

    public List<string> Prompts { get; } = ["greeting"];

    /// <summary>Resource URIs offered, in the upstream's own scheme.</summary>
    public List<string> Resources { get; } = ["mem://doc/one", "mem://doc/two"];

    public int InitializeCount;

    /// <summary>Every session id ever issued, oldest first.</summary>
    public ConcurrentQueue<string> IssuedSessions { get; } = new();

    /// <summary>Every JSON-RPC method received, in arrival order. Diagnostic.</summary>
    public ConcurrentQueue<string> ReceivedMethods { get; } = new();

    /// <summary>
    /// Authorization header seen on each request. A source reached through a proxy route should
    /// arrive here carrying the route's OAuth token, injected by the ordinary credential
    /// transform — this is what proves the loopback hop really took the credentialed path.
    /// </summary>
    public ConcurrentQueue<string?> ReceivedAuthorization { get; } = new();

    /// <summary>Full header set of each request, for asserting on what did *not* arrive.</summary>
    public ConcurrentQueue<Dictionary<string, string>> ReceivedHeaders { get; } = new();

    /// <summary>Distinct sessions that have carried a tools/call, and how many each carried.</summary>
    public ConcurrentDictionary<string, int> CallsPerSession { get; } = new();

    /// <summary>
    /// How many times each tool was actually invoked here. A funnel refusing a filtered-out tool
    /// has to refuse it locally, so the count for that tool must not move at all.
    /// </summary>
    public ConcurrentDictionary<string, int> CallsByTool { get; } = new();

    /// <summary>When set, every request is answered with 503 — a server that has fallen over.</summary>
    public volatile bool IsDown;

    /// <summary>
    /// When cleared, prompts/list and resources/list answer "method not found", like the many
    /// real servers that implement tools and nothing else.
    /// </summary>
    public volatile bool SupportsPromptsAndResources = true;

    /// <summary>
    /// When set, answers 200 with an HTML landing page instead of JSON — what a restricted
    /// serverless deployment does when it decides to show a sign-in or consent page rather than
    /// run the handler.
    /// </summary>
    public volatile bool RespondWithHtml;

    /// <summary>
    /// When set, any request presenting a session id that is not live is answered with 404, the
    /// signal a real server sends for an expired session.
    /// </summary>
    public volatile bool EnforceSessions = true;

    /// <summary>
    /// The name of the tool that answers with a 2026-07-28 input_required result the first time
    /// it is called, and with a real result once the answers come back. Not in <see cref="Tools"/>
    /// by default: a test that wants multi round-trip behaviour adds it, so the tools every other
    /// test sees stay as they were.
    /// </summary>
    public const string MultiRoundTripTool = "needs_input";

    /// <summary>The requestState this fake mints when it asks for input.</summary>
    public const string MultiRoundTripState = "fake-state-1";

    /// <summary>
    /// requestState seen on each tools/call, in arrival order. The funnel is supposed to relay
    /// the one the source minted back to it untouched, so the second entry is the assertion.
    /// </summary>
    public ConcurrentQueue<string?> ReceivedRequestState { get; } = new();

    /// <summary>The inputResponses seen on each tools/call, serialised; null when absent.</summary>
    public ConcurrentQueue<string?> ReceivedInputResponses { get; } = new();

    /// <summary>Released to let a "slow" tool call complete; a test controls the timing.</summary>
    public TaskCompletionSource SlowToolGate { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Signalled every time a "slow" call arrives, so a test can wait for N to be in flight.</summary>
    public ConcurrentQueue<string> SlowCallsInFlight { get; } = new();

    public void ResetSlowGate() => SlowToolGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Forgets every issued session, so the next request on an old one looks expired.</summary>
    public void ExpireAllSessions() => _liveSessions.Clear();

    public static async Task<FakeMcpServer> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        var app = builder.Build();
        FakeMcpServer? server = null;

        app.MapPost("/mcp", async context => await server!.HandlePostAsync(context));

        // No standalone SSE stream; 405 is the documented way to say so.
        app.MapGet("/mcp", context =>
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return Task.CompletedTask;
        });

        app.MapDelete("/mcp", context =>
        {
            if (context.Request.Headers.TryGetValue(SessionHeader, out var id))
            {
                server!._liveSessions.TryRemove(id.ToString(), out _);
            }

            return Task.CompletedTask;
        });

        await app.StartAsync();

        var url = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First();

        server = new FakeMcpServer(app, $"{url}/mcp");
        return server;
    }

    private async Task HandlePostAsync(HttpContext context)
    {
        if (IsDown)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        if (RespondWithHtml)
        {
            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.WriteAsync("<!doctype html><html><title>Sign in</title></html>");
            return;
        }

        ReceivedAuthorization.Enqueue(
            context.Request.Headers.TryGetValue("Authorization", out var auth) ? auth.ToString() : null);
        ReceivedHeaders.Enqueue(context.Request.Headers.ToDictionary(
            h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase));

        using var document = await JsonDocument.ParseAsync(context.Request.Body);
        var root = document.RootElement;

        var method = root.TryGetProperty("method", out var m) ? m.GetString() ?? "" : "";
        var hasId = root.TryGetProperty("id", out var idElement);
        var sessionId = context.Request.Headers.TryGetValue(SessionHeader, out var header) ? header.ToString() : null;

        if (method == "initialize")
        {
            var newSession = Guid.NewGuid().ToString("n");
            _liveSessions[newSession] = 0;
            IssuedSessions.Enqueue(newSession);
            Interlocked.Increment(ref InitializeCount);

            context.Response.Headers[SessionHeader] = newSession;

            // Echo the client's protocol version rather than pinning one, so this fake keeps
            // working when the SDK moves to a newer revision.
            var version = root.TryGetProperty("params", out var p) && p.TryGetProperty("protocolVersion", out var v)
                ? v.GetString()
                : "2024-11-05";

            await WriteResultAsync(context, idElement, new JsonObject
            {
                ["protocolVersion"] = version,
                ["capabilities"] = new JsonObject
                {
                    ["tools"] = new JsonObject(),
                    ["resources"] = new JsonObject(),
                    ["prompts"] = new JsonObject(),
                },
                ["serverInfo"] = new JsonObject { ["name"] = "fake-mcp", ["version"] = "1.0.0" },
            });
            return;
        }

        ReceivedMethods.Enqueue(method);

        // A session id the server does not recognise is the "expired session" signal, and 404 is
        // how a real streamable-HTTP server says so. A request with *no* session id is left
        // alone: the SDK probes the server before it initializes, and answering that with 404
        // would make the fake reject clients a real server accepts.
        if (EnforceSessions && sessionId is not null && !_liveSessions.ContainsKey(sessionId))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (!hasId)
        {
            // A notification. Nothing to answer.
            context.Response.StatusCode = StatusCodes.Status202Accepted;
            return;
        }

        switch (method)
        {
            case "ping":
                await WriteResultAsync(context, idElement, new JsonObject());
                return;

            case "tools/list":
                await WriteResultAsync(context, idElement, new JsonObject
                {
                    ["tools"] = new JsonArray([.. Tools.Select(ToolDefinition)]),
                });
                return;

            case "prompts/list" or "resources/list" or "resources/templates/list"
                when !SupportsPromptsAndResources:
                await WriteErrorAsync(context, idElement, -32601, $"Method '{method}' not found.");
                return;

            case "prompts/list":
                await WriteResultAsync(context, idElement, new JsonObject
                {
                    ["prompts"] = new JsonArray([.. Prompts.Select(name => (JsonNode)new JsonObject { ["name"] = name })]),
                });
                return;

            case "prompts/get":
                await WriteResultAsync(context, idElement, new JsonObject
                {
                    ["messages"] = new JsonArray(new JsonObject
                    {
                        ["role"] = "user",
                        ["content"] = new JsonObject
                        {
                            ["type"] = "text",
                            ["text"] = $"prompt {root.GetProperty("params").GetProperty("name").GetString()}",
                        },
                    }),
                });
                return;

            case "resources/list":
                await WriteResultAsync(context, idElement, new JsonObject
                {
                    ["resources"] = new JsonArray([.. Resources.Select(uri => (JsonNode)new JsonObject
                    {
                        ["uri"] = uri,
                        ["name"] = uri.Split('/').Last(),
                    })]),
                });
                return;

            case "resources/templates/list":
                await WriteResultAsync(context, idElement, new JsonObject
                {
                    ["resourceTemplates"] = new JsonArray(new JsonObject
                    {
                        ["uriTemplate"] = "mem://doc/{id}",
                        ["name"] = "any document",
                    }),
                });
                return;

            case "resources/read":
                await WriteResultAsync(context, idElement, new JsonObject
                {
                    ["contents"] = new JsonArray(new JsonObject
                    {
                        ["uri"] = root.GetProperty("params").GetProperty("uri").GetString(),
                        ["mimeType"] = "text/plain",
                        ["text"] = $"contents of {root.GetProperty("params").GetProperty("uri").GetString()}",
                    }),
                });
                return;

            case "tools/call":
                await HandleToolCallAsync(context, idElement, root, sessionId ?? "none");
                return;

            default:
                await WriteErrorAsync(context, idElement, -32601, $"Unknown method '{method}'.");
                return;
        }
    }

    private async Task HandleToolCallAsync(HttpContext context, JsonElement id, JsonElement root, string sessionId)
    {
        var parameters = root.GetProperty("params");
        var name = parameters.GetProperty("name").GetString() ?? "";
        var arguments = parameters.TryGetProperty("arguments", out var a) ? a : default;

        CallsPerSession.AddOrUpdate(sessionId, 1, (_, count) => count + 1);
        CallsByTool.AddOrUpdate(name, 1, (_, count) => count + 1);

        ReceivedRequestState.Enqueue(
            parameters.TryGetProperty("requestState", out var stateElement) ? stateElement.GetString() : null);

        var hasInputResponses = parameters.TryGetProperty("inputResponses", out var responsesElement)
            && responsesElement.ValueKind == JsonValueKind.Object;

        ReceivedInputResponses.Enqueue(hasInputResponses ? responsesElement.GetRawText() : null);

        if (name == MultiRoundTripTool && !hasInputResponses)
        {
            // 2026-07-28 multi round-trip: not an error and not a result, but a request for more
            // from the client, which retries the same call carrying inputResponses. Written out
            // by hand rather than through the SDK for the reason the whole fake is hand-written --
            // if both ends agreed on a misreading of the shape, the test would still pass.
            await WriteResultAsync(context, id, new JsonObject
            {
                ["resultType"] = "input_required",
                ["requestState"] = MultiRoundTripState,
                ["inputRequests"] = new JsonObject
                {
                    // Each entry is a request in its own right and carries the method it stands
                    // for; the SDK refuses one without it.
                    ["confirm"] = new JsonObject
                    {
                        ["method"] = "elicitation/create",
                        ["params"] = new JsonObject
                        {
                            ["message"] = "Confirm?",
                            ["requestedSchema"] = new JsonObject
                            {
                                ["type"] = "object",
                                ["properties"] = new JsonObject
                                {
                                    ["ok"] = new JsonObject { ["type"] = "boolean" },
                                },
                            },
                        },
                    },
                },
            });
            return;
        }

        string text;

        switch (name)
        {
            case "whoami":
                // The session is the interesting part: two funnels sharing this source must not
                // be able to produce the same answer here.
                text = sessionId;
                break;

            case "echo":
                text = arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("value", out var value)
                    ? value.GetString() ?? ""
                    : "";
                break;

            case "slow":
                var label = arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("value", out var slowValue)
                    ? slowValue.GetString() ?? ""
                    : "";
                SlowCallsInFlight.Enqueue(label);
                await SlowToolGate.Task.WaitAsync(TimeSpan.FromSeconds(30));
                text = label;
                break;

            case MultiRoundTripTool:
                text = "input accepted";
                break;

            default:
                text = $"called {name}";
                break;
        }

        await WriteResultAsync(context, id, new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
            ["isError"] = false,
        });
    }

    private static JsonNode ToolDefinition(string name) => new JsonObject
    {
        ["name"] = name,
        ["description"] = $"fake tool {name}",
        ["inputSchema"] = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject { ["value"] = new JsonObject { ["type"] = "string" } },
        },
    };

    private static async Task WriteResultAsync(HttpContext context, JsonElement id, JsonObject result)
    {
        var envelope = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = JsonNode.Parse(id.GetRawText()),
            ["result"] = result,
        };

        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(envelope.ToJsonString());
    }

    private static async Task WriteErrorAsync(HttpContext context, JsonElement id, int code, string message)
    {
        var envelope = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = JsonNode.Parse(id.GetRawText()),
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
        };

        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(envelope.ToJsonString());
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
