using System.Collections.Concurrent;
using ModelContextProtocol.Protocol;
using RavensPort.Core.Models;

namespace RavensPort.Core.Tests.Mcp;

/// <summary>
/// A bridge with agents of different vintages on it, and several of them at once.
///
/// Both questions have the same root. The bridge endpoint runs stateless — that is what makes a
/// re-imported manifest land on the next call — so every request carries its own negotiation and
/// nothing is remembered between them. That should mean an old client and a new one can use the
/// same endpoint, at the same time, without either noticing the other. "Should" is why these
/// exist: the rest of the suite lets both ends negotiate the newest revision they share, so it
/// only ever exercises one version and one caller.
/// </summary>
public class McpApiBridgeCompatibilityTests : IAsyncLifetime
{
    /// <summary>The revision before the one this build targets.</summary>
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
            { "name": "review", "messages": [ { "role": "user", "content": "Review the tasks." } ] }
          ],
          "skills": [
            { "name": "triage", "content": "# Triage\n\nStart with list_tasks." }
          ]
        }
        """;

    private FakeRestApi _upstream = null!;
    private FunnelTestHost _host = null!;

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

    // ---- older clients -----------------------------------------------------------------------

    [Theory]
    [InlineData(PreviousRevision)]
    [InlineData(WidelyDeployedRevision)]
    [InlineData(OldestSupportedRevision)]
    public async Task AnAgentPinnedToAnOlderRevisionGetsAWorkingBridge(string revision)
    {
        var client = await _host.ConnectBridgeAsync("tracker", protocolVersion: revision);

        // Without this the rest proves nothing: a pin that quietly failed would leave the client
        // on the current revision and every assertion below would still pass.
        Assert.Equal(revision, client.NegotiatedProtocolVersion);

        var tools = (await client.ListToolsAsync()).Select(t => t.Name).ToList();
        Assert.Contains("get_task", tools);
        Assert.Contains("list_by_state", tools);

        var result = await client.CallToolAsync("get_task", new Dictionary<string, object?> { ["id"] = "7" });

        Assert.True(result.IsError != true);
        Assert.Equal("/tasks/7", _upstream.Received[^1].Path);

        // The parts of a manifest that are not tools go through the same negotiation.
        Assert.Equal("review", (await client.ListPromptsAsync()).Single().Name);
        Assert.Single(await client.ListResourcesAsync());
    }

    /// <summary>
    /// Variant selection is the bridge's own logic rather than the SDK's, so it is worth proving
    /// separately that an old client can still drive it.
    /// </summary>
    [Fact]
    public async Task AnOlderAgentCanStillChooseAVariant()
    {
        var client = await _host.ConnectBridgeAsync("tracker", protocolVersion: WidelyDeployedRevision);

        await client.CallToolAsync("list_by_state", new Dictionary<string, object?> { ["state"] = "archived" });
        Assert.Equal("/archive/tasks", _upstream.Received[^1].Path);

        var refused = await client.CallToolAsync("list_by_state", new Dictionary<string, object?> { ["state"] = "nope" });

        Assert.True(refused.IsError);
        Assert.Contains("archived, open", Text(refused), StringComparison.Ordinal);
    }

    /// <summary>
    /// The fields a newer revision added ride on results the bridge builds itself — the cache
    /// hints on every list. An older client has to survive receiving them, which is the half of
    /// compatibility that fails silently: it looks like a working connection returning nothing.
    /// </summary>
    [Fact]
    public async Task AnOlderAgentSurvivesTheFieldsANewerRevisionAdded()
    {
        var client = await _host.ConnectBridgeAsync("tracker", protocolVersion: WidelyDeployedRevision);

        Assert.NotEmpty(await client.ListToolsAsync());
        Assert.NotEmpty(await client.ListPromptsAsync());
        Assert.NotEmpty(await client.ListResourcesAsync());
    }

    // ---- newer clients -----------------------------------------------------------------------

    /// <summary>
    /// A client from after this build, asking for a revision the server has never heard of.
    ///
    /// The answer is not a silent downgrade: the bridge replies with a JSON-RPC error that names
    /// every revision it does support, which is what lets an unpinned client pick one and retry.
    /// That is the SDK's negotiation, shared with the funnel, and the property worth pinning here
    /// is that the bridge does not turn it into something worse — a 500, a hang, or an error that
    /// says only "no".
    ///
    /// Driven over raw HTTP because the SDK client refuses to *pin* to a version the server lacks,
    /// so a pinned client never gets far enough to show what the server said.
    /// </summary>
    [Fact]
    public async Task ARevisionFromTheFutureIsRefusedWithTheListOfOnesThatWork()
    {
        using var http = new HttpClient { BaseAddress = new Uri(_host.BaseUrl) };

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api-mcp/tracker")
        {
            Content = new StringContent(
                """
                {"jsonrpc":"2.0","id":1,"method":"initialize","params":{
                  "protocolVersion":"2099-01-01","capabilities":{},
                  "clientInfo":{"name":"from-the-future","version":"1"}}}
                """,
                System.Text.Encoding.UTF8,
                "application/json"),
        };

        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
        request.Headers.TryAddWithoutValidation(
            RavensPort.Core.Proxy.LocalAccessGuard.ApiKeyHeaderName, FunnelTestHost.ApiKey);

        using var response = await http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        // Answered, not dropped: the transport is fine and the endpoint is alive.
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);

        // And the refusal carries what a client needs to recover on its own.
        Assert.Contains("\"supported\"", body, StringComparison.Ordinal);
        Assert.Contains(WidelyDeployedRevision, body, StringComparison.Ordinal);
    }

    /// <summary>
    /// What a real agent from the future actually does: it does not pin, so it takes whichever
    /// revision this build offers and carries on. Every other test in the suite connects this way;
    /// this one says out loud that the negotiated result is a revision the bridge supports and
    /// that the tools work on it.
    /// </summary>
    [Fact]
    public async Task AnUnpinnedAgentNegotiatesSomethingThatWorks()
    {
        var client = await _host.ConnectBridgeAsync("tracker");

        Assert.False(string.IsNullOrEmpty(client.NegotiatedProtocolVersion));

        var result = await client.CallToolAsync("get_task", new Dictionary<string, object?> { ["id"] = "9" });

        Assert.True(result.IsError != true);
        Assert.Equal("/tasks/9", _upstream.Received[^1].Path);
    }

    // ---- several clients at once ---------------------------------------------------------------

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

        var clients = await Task.WhenAll(
            Enumerable.Range(0, agents).Select(_ => _host.ConnectBridgeAsync("tracker")));

        var seen = new ConcurrentBag<string>();

        await Task.WhenAll(clients.Select(async (client, index) =>
        {
            var id = $"task-{index}";
            var result = await client.CallToolAsync("get_task", new Dictionary<string, object?> { ["id"] = id });

            Assert.True(result.IsError != true, Text(result));

            // The fake echoes nothing back, so the proof that this call carried this caller's
            // argument is the path the upstream recorded for it.
            seen.Add(id);
        }));

        Assert.Equal(agents, seen.Count);

        var paths = _upstream.Received.Select(r => r.Path).OrderBy(p => p, StringComparer.Ordinal).ToList();

        Assert.Equal(
            Enumerable.Range(0, agents).Select(i => $"/tasks/task-{i}").OrderBy(p => p, StringComparer.Ordinal),
            paths);

        // Every one of them carried the route's credential, not just the first.
        Assert.All(_upstream.Received, request => Assert.Equal("Bearer TOKEN", request.Header("Authorization")));
    }

    /// <summary>
    /// The same endpoint, at the same moment, speaking two revisions. Stateless is what makes this
    /// possible — each request negotiates for itself — and it is the arrangement a household with
    /// one updated agent and one old one actually has.
    /// </summary>
    [Fact]
    public async Task OldAndNewAgentsShareOneBridgeSimultaneously()
    {
        var oldAgent = await _host.ConnectBridgeAsync("tracker", protocolVersion: WidelyDeployedRevision);
        var newAgent = await _host.ConnectBridgeAsync("tracker");

        Assert.Equal(WidelyDeployedRevision, oldAgent.NegotiatedProtocolVersion);
        Assert.NotEqual(WidelyDeployedRevision, newAgent.NegotiatedProtocolVersion);

        var calls = await Task.WhenAll(
            oldAgent.CallToolAsync("get_task", new Dictionary<string, object?> { ["id"] = "old" }).AsTask(),
            newAgent.CallToolAsync("get_task", new Dictionary<string, object?> { ["id"] = "new" }).AsTask());

        Assert.All(calls, call => Assert.True(call.IsError != true));

        var paths = _upstream.Received.Select(r => r.Path).OrderBy(p => p, StringComparer.Ordinal).ToList();
        Assert.Equal(["/tasks/new", "/tasks/old"], paths);

        // And an edit reaches both of them on their next call, whichever revision they speak.
        await _host.MutateAsync(store =>
            store.McpApiBridges.Single().Manifest.Tools.RemoveAll(t => t.Name == "get_task"));

        Assert.DoesNotContain("get_task", (await oldAgent.ListToolsAsync()).Select(t => t.Name));
        Assert.DoesNotContain("get_task", (await newAgent.ListToolsAsync()).Select(t => t.Name));
    }

    private static string Text(CallToolResult result) =>
        string.Join("\n", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
}
