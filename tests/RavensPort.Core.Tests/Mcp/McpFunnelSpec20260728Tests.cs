using System.Text.Json;
using ModelContextProtocol.Protocol;
using RavensPort.Core.Models;

namespace RavensPort.Core.Tests.Mcp;

/// <summary>
/// The parts of the 2026-07-28 revision the funnel has to answer for itself, rather than the ones
/// the SDK already handles underneath it.
///
/// Three things are being pinned. Cacheability, because the spec now requires ttlMs and cacheScope
/// on every list and read, and because the values this funnel has to give are not the obvious ones
/// — its lists are not cacheable at all, and nothing it returns is shareable. Ordering, because a
/// deterministic tools/list is what makes caching on the client worth anything. And multi
/// round-trip requests, because a funnel that swallowed an input_required from a source would turn
/// a question the agent could have answered into a dead call.
/// </summary>
public class McpFunnelSpec20260728Tests : IAsyncLifetime
{
    private FakeMcpServer _upstream = null!;
    private FunnelTestHost _host = null!;

    public async Task InitializeAsync()
    {
        _upstream = await FakeMcpServer.StartAsync();
        _host = await FunnelTestHost.StartAsync();

        var source = await _host.AddRemoteSourceAsync("up", _upstream.Url);
        await _host.AddFunnelAsync("agent", new McpFunnelSource { SourceId = source.Id });
    }

    public async Task DisposeAsync()
    {
        await _host.DisposeAsync();
        await _upstream.DisposeAsync();
    }

    // ---- cacheability ---------------------------------------------------------------------

    [Fact]
    public async Task EveryListResultIsPrivateAndNotCacheable()
    {
        // ttlMs of zero is the point, not an oversight. The funnel resolves its selection from the
        // config store on every request so that unticking a tool takes effect on the next call; a
        // client that had been told it could cache tools/list for even a minute would go on
        // offering the tool for that minute, and there is no session or listChanged notification
        // here that could tell it otherwise.
        var client = await _host.ConnectAsync("agent");

        var tools = await client.ListToolsAsync(new ListToolsRequestParams());
        var prompts = await client.ListPromptsAsync(new ListPromptsRequestParams());
        var resources = await client.ListResourcesAsync(new ListResourcesRequestParams());
        var templates = await client.ListResourceTemplatesAsync(new ListResourceTemplatesRequestParams());

        foreach (var (name, result) in new (string, ICacheableResult)[]
                 {
                     ("tools/list", tools),
                     ("prompts/list", prompts),
                     ("resources/list", resources),
                     ("resources/templates/list", templates),
                 })
        {
            Assert.Equal(CacheScope.Private, result.CacheScope);
            Assert.True(
                result.TimeToLive == TimeSpan.Zero,
                $"{name} promised a ttl of {result.TimeToLive}; a cached list survives the untick the call path exists to enforce");
        }
    }

    [Fact]
    public async Task AResourceReadIsPrivate()
    {
        // Scope, not ttl, is the assertion here. How long the bytes stay good is the upstream's
        // call and gets relayed; who may hold them is not, because a route-backed source fetched
        // them with the user's own credential attached.
        var client = await _host.ConnectAsync("agent");

        var listed = await client.ListResourcesAsync(new ListResourcesRequestParams());
        var read = await client.ReadResourceAsync(
            new ReadResourceRequestParams { Uri = listed.Resources[0].Uri });

        Assert.Equal(CacheScope.Private, read.CacheScope);
    }

    // ---- deterministic ordering -----------------------------------------------------------

    [Fact]
    public async Task ToolOrderSurvivesTheUpstreamReorderingItself()
    {
        // Source order alone would not survive this. The funnel walks its sources in a fixed
        // order, but nothing stops one of them from listing its own tools differently on the next
        // call, and a client caching on the list would see it move for no reason.
        var client = await _host.ConnectAsync("agent");

        var first = (await client.ListToolsAsync(new ListToolsRequestParams()))
            .Tools.Select(t => t.Name).ToList();

        _upstream.Tools.Reverse();

        var second = (await client.ListToolsAsync(new ListToolsRequestParams()))
            .Tools.Select(t => t.Name).ToList();

        Assert.Equal(first, second);
        Assert.Equal(first.OrderBy(n => n, StringComparer.Ordinal), first);
    }

    // ---- multi round-trip requests ---------------------------------------------------------

    [Fact]
    public async Task AnAgentsAnswersAndRequestStateReachTheSource()
    {
        // The direction a funnel can serve. Something further along asked the agent for input; the
        // agent retries through this funnel with its answers, and both the answers and the state
        // the source minted have to arrive unaltered, or the source is being asked again rather
        // than answered.
        _upstream.Tools.Add(FakeMcpServer.MultiRoundTripTool);

        var client = await _host.ConnectAsync("agent");
        var answer = JsonSerializer.Deserialize<JsonElement>("""{"action":"accept","content":{"ok":true}}""");

        var result = await client.CallToolAsync(new CallToolRequestParams
        {
            Name = $"up__{FakeMcpServer.MultiRoundTripTool}",
            RequestState = FakeMcpServer.MultiRoundTripState,
            InputResponses = new Dictionary<string, InputResponse>
            {
                ["confirm"] = new() { RawValue = answer },
            },
        });

        Assert.Equal("input accepted", result.Content.OfType<TextContentBlock>().First().Text);

        Assert.Equal([FakeMcpServer.MultiRoundTripState], _upstream.ReceivedRequestState);
        Assert.Collection(
            _upstream.ReceivedInputResponses,
            responses => Assert.Contains("\"ok\"", responses));
    }

    [Fact]
    public async Task ASourceThatAsksForInputFailsWithAMessageNamingIt()
    {
        // The direction it cannot, pinned deliberately. The client SDK answers an input_required
        // from its own handlers rather than returning it, so a funnel has nothing to forward to
        // the agent and the call cannot finish -- see ExplainInputRequired. What is being asserted
        // is only that the failure says which source did it, because the alternative an operator
        // would otherwise see is the SDK's unattributed "an error occurred invoking".
        _upstream.Tools.Add(FakeMcpServer.MultiRoundTripTool);

        var client = await _host.ConnectAsync("agent");

        var result = await FunnelTestHost.CallAsync(client, $"up__{FakeMcpServer.MultiRoundTripTool}");

        Assert.True(result.IsError);

        var text = string.Join(" ", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
        Assert.Contains("'up'", text);
        Assert.Contains("asked for input", text);
    }
}
