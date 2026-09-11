using System.Collections.Concurrent;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using RavensPort.Core.Models;

namespace RavensPort.Core.Tests.Mcp;

/// <summary>
/// A bridge with agents of every vintage on it, and several of them at once.
///
/// Both questions have the same root. The bridge endpoint runs stateless — that is what makes a
/// re-imported manifest land on the next call — so every request carries its own negotiation and
/// nothing is remembered between them. That should mean agents on different revisions can share
/// one endpoint, simultaneously, without either noticing the other.
///
/// "Should" is why these exist. The rest of the suite lets both ends negotiate the newest revision
/// they share and connects one client, so it only ever exercises one version and one caller, which
/// is exactly the case that never breaks.
/// </summary>
public class McpApiBridgeCompatibilityTests : IAsyncLifetime
{
    /// <summary>The revision this build targets, reached through discovery rather than initialize.</summary>
    private const string CurrentRevision = "2026-07-28";

    /// <summary>The revision before it.</summary>
    private const string PreviousRevision = "2025-11-25";

    /// <summary>
    /// Older still, and the one that matters most in practice: it is what shipping agents
    /// negotiate today.
    /// </summary>
    private const string WidelyDeployedRevision = "2025-06-18";

    /// <summary>The oldest revision the handshake admits to, and so the widest claim worth making.</summary>
    private const string OldestSupportedRevision = "2024-11-05";

    private const string Manifest = """
        {
          "version": 1,
          "instructions": "Start with list_tasks.",
          "tools": [
            {
              "name": "get_task",
              "description": "Fetches one task.",
              "readOnly": true,
              "inputSchema": {
                "type": "object",
                "properties": { "id": { "type": "string" } },
                "required": ["id"]
              },
              "request": { "method": "GET", "path": "/tasks/{id}" }
            },
            {
              "name": "list_by_state",
              "inputSchema": {
                "type": "object",
                "properties": { "state": { "type": "string", "enum": ["open", "archived"] } },
                "required": ["state"]
              },
              "variantBy": "state",
              "variants": {
                "open": { "description": "Live", "method": "GET", "path": "/tasks" },
                "archived": { "description": "Archived", "method": "GET", "path": "/archive/tasks" }
              }
            }
          ],
          "prompts": [
            {
              "name": "review",
              "arguments": [ { "name": "focus", "required": false } ],
              "messages": [ { "role": "user", "content": "Review the tasks, weighted towards {focus}." } ]
            }
          ],
          "skills": [
            { "name": "triage", "content": "# Triage\n\nStart with list_tasks." }
          ]
        }
        """;

    private FakeRestApi _upstream = null!;
    private FunnelTestHost _host = null!;

    /// <summary>
    /// Every revision this bridge answers on, including unpinned.
    ///
    /// Null means "let the client negotiate", which is what a real agent does and which lands on
    /// <see cref="CurrentRevision"/>. The rest are exactly the four the initialize handshake admits
    /// to. If the SDK ever drops one, the case for it fails rather than quietly disappearing.
    /// </summary>
    public static TheoryData<string?> EveryRevision
    {
        get
        {
            var data = new TheoryData<string?>();

            data.Add(null);
            data.Add(CurrentRevision);
            data.Add(PreviousRevision);
            data.Add(WidelyDeployedRevision);
            data.Add("2025-03-26");
            data.Add(OldestSupportedRevision);

            return data;
        }
    }

    public async Task InitializeAsync()
    {
        _upstream = await FakeRestApi.StartAsync();
        _host = await FunnelTestHost.StartAsync();

        var credential = new CredentialRecord
        {
            Name = "c",
            ClientId = "id",
            ClientSecret = "secret",
            Token = new TokenSet("TOKEN", "refresh", DateTimeOffset.UtcNow.AddHours(1), "Bearer", DateTimeOffset.UtcNow),
        };

        var upstream = new UpstreamRecord { Name = "u", BaseUrl = _upstream.Url };

        var route = new RouteMapping
        {
            PathPrefix = "/tasks-api",
            UpstreamId = upstream.Id,
            Credentials = [RouteCredential.For(credential.Id, CredentialPlacement.Header)],
            StripPrefix = true,
        };

        await _host.MutateAsync(store =>
        {
            store.Credentials.Add(credential);
            store.Upstreams.Add(upstream);
            store.Routes.Add(route);
        });
        _host.RebuildProxyConfig();

        await _host.AddApiBridgeAsync("tracker", route.Id, Manifest);
    }

    public async Task DisposeAsync()
    {
        await _host.DisposeAsync();
        await _upstream.DisposeAsync();
    }

    // ---- every revision, the whole surface ----------------------------------------------------

    /// <summary>
    /// What the agent can see. One theory rather than a test per revision, because a surface
    /// covered on one version and not the others is how a regression hides.
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryRevision))]
    public async Task EveryRevisionSeesTheWholeManifest(string? revision)
    {
        var client = await Connect(revision);

        // Without this the rest proves nothing: a pin that quietly failed would leave the client
        // on the newest revision and every assertion below would still pass.
        Assert.Equal(revision ?? CurrentRevision, client.NegotiatedProtocolVersion);

        // Instructions reach the agent either way — through initialize on the older revisions,
        // through discovery on the current one.
        Assert.Equal("Start with list_tasks.", client.ServerInstructions);

        var tools = await client.ListToolsAsync();

        Assert.Equal(
            ["get_task", "list_by_state"],
            tools.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal));

        // The schema has to survive intact, or the model cannot call the tool it can see.
        var getTask = tools.Single(t => t.Name == "get_task");
        Assert.Contains("id", getTask.JsonSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name));
        Assert.True(getTask.ProtocolTool.Annotations?.ReadOnlyHint);

        // And the variant descriptions are folded into the tool's own description.
        Assert.Contains("open: Live", tools.Single(t => t.Name == "list_by_state").Description, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(EveryRevision))]
    public async Task EveryRevisionCanCallToolsAndChooseVariants(string? revision)
    {
        var client = await Connect(revision);

        var plain = await client.CallToolAsync("get_task", new Dictionary<string, object?> { ["id"] = "7" });
        Assert.True(plain.IsError != true, Text(plain));
        Assert.Equal("/tasks/7", _upstream.Received[^1].Path);
        Assert.Equal("Bearer TOKEN", _upstream.Received[^1].Header("Authorization"));

        // Variant selection is the bridge's own logic rather than the SDK's, so it is driven on
        // each revision rather than assumed to travel.
        var open = await client.CallToolAsync("list_by_state", new Dictionary<string, object?> { ["state"] = "open" });
        Assert.True(open.IsError != true, Text(open));
        Assert.Equal("/tasks", _upstream.Received[^1].Path);

        var archived = await client.CallToolAsync("list_by_state", new Dictionary<string, object?> { ["state"] = "archived" });
        Assert.True(archived.IsError != true, Text(archived));
        Assert.Equal("/archive/tasks", _upstream.Received[^1].Path);
    }

    /// <summary>
    /// The refusals. An error that stopped being readable on an older client — or stopped being an
    /// error at all — would send that agent's call upstream with a hole in it.
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryRevision))]
    public async Task EveryRevisionGetsUsableErrors(string? revision)
    {
        var client = await Connect(revision);
        var before = _upstream.Received.Count;

        var missing = await client.CallToolAsync("get_task", new Dictionary<string, object?>());
        Assert.True(missing.IsError);
        Assert.Contains("id", Text(missing), StringComparison.Ordinal);

        var unknown = await client.CallToolAsync("list_by_state", new Dictionary<string, object?> { ["state"] = "nope" });
        Assert.True(unknown.IsError);
        Assert.Contains("archived, open", Text(unknown), StringComparison.Ordinal);

        // Neither reached the upstream, which is the half that matters: a refused call must cost
        // the user's API nothing.
        Assert.Equal(before, _upstream.Received.Count);
    }

    [Theory]
    [MemberData(nameof(EveryRevision))]
    public async Task EveryRevisionGetsPromptsAndSkills(string? revision)
    {
        var client = await Connect(revision);

        Assert.Equal("review", Assert.Single(await client.ListPromptsAsync()).Name);

        // prompts/get rather than only the listing: the substitution is the bridge's own work.
        var got = await client.GetPromptAsync("review", new Dictionary<string, object?> { ["focus"] = "billing" });
        var message = got.Messages.Select(m => m.Content).OfType<TextContentBlock>().Single().Text;
        Assert.Equal("Review the tasks, weighted towards billing.", message);

        var resource = Assert.Single(await client.ListResourcesAsync());
        Assert.Equal("skill://tracker/triage", resource.Uri);

        var read = await client.ReadResourceAsync(resource.Uri);
        var contents = Assert.IsType<TextResourceContents>(Assert.Single(read.Contents));
        Assert.Contains("Start with list_tasks", contents.Text, StringComparison.Ordinal);

        // And none of it touched the API behind the route.
        Assert.Empty(_upstream.Received);
    }

    /// <summary>
    /// An upstream failure has to arrive as a tool error rather than a protocol one, on every
    /// revision. A client that reads it as a transport fault drops the session instead of letting
    /// the model see the status and try something else.
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryRevision))]
    public async Task EveryRevisionSeesAnUpstreamFailureAsAToolError(string? revision)
    {
        _upstream.StatusCode = 422;
        _upstream.ResponseBody = """{"error":"nope"}""";

        var client = await Connect(revision);

        var result = await client.CallToolAsync("get_task", new Dictionary<string, object?> { ["id"] = "7" });

        Assert.True(result.IsError);
        Assert.Contains("422", Text(result), StringComparison.Ordinal);
        Assert.Contains("nope", Text(result), StringComparison.Ordinal);
    }

    /// <summary>
    /// A manifest edit lands on the next call whatever revision the agent speaks. The endpoint
    /// holds no session and caches nothing, and this is the property that depends on it.
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryRevision))]
    public async Task EveryRevisionSeesAManifestEditOnItsNextCall(string? revision)
    {
        var client = await Connect(revision);
        Assert.Equal(2, (await client.ListToolsAsync()).Count);

        await _host.MutateAsync(store =>
            store.McpApiBridges.Single().Manifest.Tools.RemoveAll(t => t.Name == "get_task"));

        var tools = (await client.ListToolsAsync()).Select(t => t.Name).ToList();

        Assert.DoesNotContain("get_task", tools);
        Assert.Contains("list_by_state", tools);
    }

    // ---- the current revision's own surface -----------------------------------------------------

    /// <summary>
    /// The 2026-07-28 discovery surface, answered by the bridge rather than by a source.
    ///
    /// SEP-2575 made <c>server/discover</c> the canonical way to learn what a server supports
    /// without handshaking, which is why the initialize refusal below lists only the four older
    /// revisions. The manifest's instructions ride on this answer, making them the first thing an
    /// agent reads.
    /// </summary>
    [Fact]
    public async Task DiscoveryAnswersWithTheCurrentRevisionAndTheManifestsInstructions()
    {
        // The discover RPC carries no payload of its own. What it needs is the per-request
        // metadata the revision defines, and the protocol version is the part the server insists
        // on: without it there is no way to know which shape of answer the caller can read.
        var (status, body) = await PostAsync("server/discover", """
            {"jsonrpc":"2.0","id":1,"method":"server/discover","params":{"_meta":{
              "io.modelcontextprotocol/protocolVersion":"2026-07-28",
              "io.modelcontextprotocol/clientInfo":{"name":"probe","version":"1"},
              "io.modelcontextprotocol/clientCapabilities":{}}}}
            """);

        Assert.Equal(System.Net.HttpStatusCode.OK, status);
        Assert.DoesNotContain("\"error\"", body, StringComparison.Ordinal);
        Assert.Contains(CurrentRevision, body, StringComparison.Ordinal);
        Assert.Contains("Start with list_tasks.", body, StringComparison.Ordinal);

        // The capabilities a bridge always declares, so a client that discovers before connecting
        // is not told this endpoint has no prompts merely because none existed at that moment.
        Assert.Contains("\"tools\"", body, StringComparison.Ordinal);
        Assert.Contains("\"prompts\"", body, StringComparison.Ordinal);
        Assert.Contains("\"resources\"", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// The revision's cache fields, on the results the bridge builds itself.
    ///
    /// 2026-07-28 requires a ttl and a scope on every list, and for a bridge the only honest ttl is
    /// zero: a re-imported manifest raises no notification of any kind, so anything a client cached
    /// would survive the edit that removed the tool.
    /// </summary>
    [Fact]
    public async Task EveryListTheBridgeReturnsIsPrivateAndNotCacheable()
    {
        var client = await Connect(null);

        var tools = await client.ListToolsAsync(new ListToolsRequestParams());
        Assert.Equal(TimeSpan.Zero, tools.TimeToLive);
        Assert.Equal(CacheScope.Private, tools.CacheScope);

        var prompts = await client.ListPromptsAsync(new ListPromptsRequestParams());
        Assert.Equal(TimeSpan.Zero, prompts.TimeToLive);
        Assert.Equal(CacheScope.Private, prompts.CacheScope);

        var resources = await client.ListResourcesAsync(new ListResourcesRequestParams());
        Assert.Equal(TimeSpan.Zero, resources.TimeToLive);
        Assert.Equal(CacheScope.Private, resources.CacheScope);
    }

    // ---- revisions this build does not know -----------------------------------------------------

    /// <summary>
    /// A client from after this build, asking for a revision the server has never heard of.
    ///
    /// The answer is not a silent downgrade: the bridge replies with a JSON-RPC error naming every
    /// revision it does support, which is what lets an unpinned client pick one and retry. That is
    /// the SDK's negotiation, shared with the funnel; what is pinned here is that the bridge does
    /// not turn it into something worse — a 500, a hang, or a refusal carrying no way forward.
    ///
    /// Driven over raw HTTP because the SDK client refuses to *pin* to a version the server lacks,
    /// so a pinned client never gets far enough to show what the server said.
    /// </summary>
    [Fact]
    public async Task ARevisionFromTheFutureIsRefusedWithTheListOfOnesThatWork()
    {
        var (status, body) = await PostAsync("initialize", """
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{
              "protocolVersion":"2099-01-01","capabilities":{},
              "clientInfo":{"name":"from-the-future","version":"1"}}}
            """, version: null);

        // Answered rather than rejected: the endpoint is alive and the refusal is inside the
        // JSON-RPC envelope, where a client can read it.
        Assert.Equal(System.Net.HttpStatusCode.OK, status);
        Assert.Contains("\"supported\"", body, StringComparison.Ordinal);

        // Every revision the handshake can still reach, named in the refusal.
        Assert.Contains(PreviousRevision, body, StringComparison.Ordinal);
        Assert.Contains(WidelyDeployedRevision, body, StringComparison.Ordinal);
        Assert.Contains(OldestSupportedRevision, body, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same question one layer down: a caller that states an unknown revision in the
    /// transport header rather than in the handshake body.
    ///
    /// This is refused earlier and harder — an HTTP 400 rather than a JSON-RPC error — because the
    /// transport cannot frame a conversation in a revision it does not implement. It still names
    /// what it does support, which is the part that matters: a client gets told what to retry
    /// with rather than merely that it failed.
    /// </summary>
    [Fact]
    public async Task ARevisionFromTheFutureInTheTransportHeaderIsRefusedWithWhatIsSupported()
    {
        var (status, body) = await PostAsync("server/discover", """
            {"jsonrpc":"2.0","id":1,"method":"server/discover","params":{"_meta":{
              "io.modelcontextprotocol/protocolVersion":"2099-01-01",
              "io.modelcontextprotocol/clientInfo":{"name":"probe","version":"1"},
              "io.modelcontextprotocol/clientCapabilities":{}}}}
            """, version: "2099-01-01");

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, status);
        Assert.Contains(CurrentRevision, body, StringComparison.Ordinal);
    }

    // ---- several agents at once -------------------------------------------------------------------

    /// <summary>
    /// Eight agents on one bridge at the same time, each asking for a different task.
    ///
    /// The endpoint holds no session, and the one thing that *is* shared — the HttpClient the
    /// bridge issues calls on — is used concurrently by every one of them. The property worth
    /// proving is not that it survives, but that no answer lands on the wrong caller.
    /// </summary>
    [Fact]
    public async Task ManyAgentsCanUseOneBridgeAtOnceWithoutCrossingWires()
    {
        const int agents = 8;

        var clients = await Task.WhenAll(Enumerable.Range(0, agents).Select(_ => Connect(null)));

        var answered = new ConcurrentBag<string>();

        await Task.WhenAll(clients.Select(async (client, index) =>
        {
            var id = $"task-{index}";
            var result = await client.CallToolAsync("get_task", new Dictionary<string, object?> { ["id"] = id });

            Assert.True(result.IsError != true, Text(result));
            answered.Add(id);
        }));

        Assert.Equal(agents, answered.Count);

        Assert.Equal(
            Enumerable.Range(0, agents).Select(i => $"/tasks/task-{i}").OrderBy(p => p, StringComparer.Ordinal),
            _upstream.Received.Select(r => r.Path).OrderBy(p => p, StringComparer.Ordinal));

        // Every one of them carried the route's credential, not just the first.
        Assert.All(_upstream.Received, request => Assert.Equal("Bearer TOKEN", request.Header("Authorization")));
    }

    /// <summary>
    /// One bridge, one moment, every revision at once — the arrangement a machine with several
    /// agents of different ages actually has. Stateless is what makes it possible: each request
    /// negotiates for itself.
    /// </summary>
    [Fact]
    public async Task AgentsOnEveryRevisionShareOneBridgeSimultaneously()
    {
        var revisions = EveryRevision.Select(row => (string?)row[0]).ToList();

        var clients = await Task.WhenAll(revisions.Select(Connect));

        var calls = await Task.WhenAll(clients.Select((client, index) =>
            client.CallToolAsync(
                "get_task",
                new Dictionary<string, object?> { ["id"] = $"caller-{index}" }).AsTask()));

        Assert.All(calls, call => Assert.True(call.IsError != true, Text(call)));

        // Each caller's own argument reached the upstream, none lost and none duplicated.
        Assert.Equal(
            revisions.Select((_, i) => $"/tasks/caller-{i}").OrderBy(p => p, StringComparer.Ordinal),
            _upstream.Received.Select(r => r.Path).OrderBy(p => p, StringComparer.Ordinal));

        // And an edit reaches all of them on their next call, whichever revision they speak.
        await _host.MutateAsync(store =>
            store.McpApiBridges.Single().Manifest.Tools.RemoveAll(t => t.Name == "get_task"));

        foreach (var client in clients)
        {
            Assert.DoesNotContain("get_task", (await client.ListToolsAsync()).Select(t => t.Name));
        }
    }

    // ---- plumbing ----------------------------------------------------------------------------------

    private Task<McpClient> Connect(string? revision) =>
        _host.ConnectBridgeAsync("tracker", protocolVersion: revision);

    /// <summary>
    /// Posts one JSON-RPC message to the bridge and returns the raw response body.
    ///
    /// The 2026-07-28 transport wants the method and the protocol version restated as headers
    /// alongside the body, so a hand-built probe has to send all three — and the header has to
    /// agree with the version inside the body, or the transport refuses the pair before anything
    /// looks at what was asked. The SDK client does all of this for its callers; these tests go
    /// around it precisely because the point is to see what the server says rather than what the
    /// client is willing to ask.
    /// </summary>
    private async Task<(System.Net.HttpStatusCode Status, string Body)> PostAsync(
        string method, string payload, string? version = CurrentRevision)
    {
        using var http = new HttpClient { BaseAddress = new Uri(_host.BaseUrl) };

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api-mcp/tracker")
        {
            Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json"),
        };

        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
        request.Headers.TryAddWithoutValidation(
            RavensPort.Core.Proxy.LocalAccessGuard.ApiKeyHeaderName, FunnelTestHost.ApiKey);

        // Omitted entirely when null, which is how a client that predates the header behaves —
        // and the only way to reach the handshake's own negotiation, since the transport refuses a
        // header it does not know before anything reads the body.
        if (version is not null) request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", version);
        request.Headers.TryAddWithoutValidation("Mcp-Method", method);

        using var response = await http.SendAsync(request);

        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static string Text(CallToolResult result) =>
        string.Join("\n", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
}
