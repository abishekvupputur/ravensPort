using ModelContextProtocol.Protocol;
using RavensPort.Core.Models;

namespace RavensPort.Core.Tests.Mcp;

/// <summary>
/// A bridge pooled by a funnel: the chain an agent actually gets pointed at.
///
/// funnel to bridge to route to upstream, three of those hops through this app's own listener, and
/// the credential still attached in exactly one place. The funnel treats a bridge as an ordinary
/// MCP source, which is the whole reason a bridge is served over HTTP rather than short-circuited
/// in process — filtering, prefixing and pooling all work with the code the funnel already had.
/// </summary>
public class McpApiBridgeInFunnelTests : IAsyncLifetime
{
    private const string Token = "FUNNELLED-BRIDGE-TOKEN";

    private const string Manifest = """
        {
          "version": 1,
          "tools": [
            { "name": "list_tasks", "request": { "method": "GET", "path": "/tasks" } },
            { "name": "get_task",
              "inputSchema": { "type": "object", "properties": { "id": {"type":"string"} }, "required": ["id"] },
              "request": { "method": "GET", "path": "/tasks/{id}" } }
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
    private McpSourceRecord _source = null!;

    public async Task InitializeAsync()
    {
        _upstream = await FakeRestApi.StartAsync();
        _host = await FunnelTestHost.StartAsync();

        var credential = new CredentialRecord
        {
            Name = "c",
            ClientId = "id",
            ClientSecret = "secret",
            Token = new TokenSet(Token, "refresh", DateTimeOffset.UtcNow.AddHours(1), "Bearer", DateTimeOffset.UtcNow),
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

        var bridge = await _host.AddApiBridgeAsync("tracker", route.Id, Manifest);
        _source = await _host.AddBridgeSourceAsync("tk", bridge.Id);

        await _host.AddFunnelAsync("agent", new McpFunnelSource { SourceId = _source.Id });
    }

    public async Task DisposeAsync()
    {
        await _host.DisposeAsync();
        await _upstream.DisposeAsync();
    }

    [Fact]
    public async Task TheFunnelListsTheBridgesToolsUnderItsAlias()
    {
        var client = await _host.ConnectAsync("agent");

        var tools = (await client.ListToolsAsync()).Select(t => t.Name).ToList();

        Assert.Equal(["tk__get_task", "tk__list_tasks"], tools.OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public async Task ACallThroughTheFunnelReachesTheUpstreamWithTheCredential()
    {
        var client = await _host.ConnectAsync("agent");

        var result = await client.CallToolAsync("tk__get_task", new Dictionary<string, object?> { ["id"] = "7" });

        Assert.True(result.IsError != true);

        var request = _upstream.Single();

        Assert.Equal("/tasks/7", request.Path);
        Assert.Equal($"Bearer {Token}", request.Header("Authorization"));
    }

    /// <summary>
    /// The funnel stamps its own hop marker on the way to the bridge. The bridge gate has to let
    /// that through — a funnel calling a bridge is the feature — while still refusing a bridge's
    /// own marker.
    /// </summary>
    [Fact]
    public async Task TheFunnelsHopMarkerDoesNotCloseTheBridge()
    {
        var client = await _host.ConnectAsync("agent");

        Assert.NotEmpty(await client.ListToolsAsync());
    }

    [Fact]
    public async Task TheFunnelsSelectionAppliesToBridgeToolsLikeAnyOthers()
    {
        await _host.MutateAsync(store =>
        {
            var link = store.McpFunnels.Single().Sources.Single();
            link.ToolMode = McpSelectionMode.Include;
            link.Tools = ["list_tasks"];
        });

        var client = await _host.ConnectAsync("agent");

        Assert.Equal("tk__list_tasks", Assert.Single(await client.ListToolsAsync()).Name);

        // Enforced on the call path too, or an agent that learned the name earlier keeps using it.
        var result = await client.CallToolAsync("tk__get_task", new Dictionary<string, object?> { ["id"] = "7" });
        Assert.True(result.IsError);
        Assert.Empty(_upstream.Received);
    }

    [Fact]
    public async Task TheBridgesPromptsAndSkillsComeThroughTheFunnelToo()
    {
        var client = await _host.ConnectAsync("agent");

        Assert.Equal("tk__review", Assert.Single(await client.ListPromptsAsync()).Name);

        var resource = Assert.Single(await client.ListResourcesAsync());

        // Re-encoded by the funnel's own name mapper, so the skill stays readable through the hop.
        Assert.StartsWith("funnel://tk/", resource.Uri, StringComparison.Ordinal);

        var read = await client.ReadResourceAsync(resource.Uri);
        var contents = Assert.IsType<TextResourceContents>(Assert.Single(read.Contents));

        Assert.Contains("Start with list_tasks", contents.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASourcePointingAtADeletedBridgeDegradesOnlyItself()
    {
        await _host.MutateAsync(store => store.McpApiBridges.Clear());

        var client = await _host.ConnectAsync("agent");

        // The funnel answers, with nothing from the broken source, rather than failing the call.
        Assert.Empty(await client.ListToolsAsync());
    }
}
