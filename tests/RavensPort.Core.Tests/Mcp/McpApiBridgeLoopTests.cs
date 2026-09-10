using System.Net;
using System.Text;
using ModelContextProtocol.Protocol;
using RavensPort.Core.Models;
using RavensPort.Core.Proxy;

namespace RavensPort.Core.Tests.Mcp;

/// <summary>
/// What stops a bridge's tool call from coming back around into an MCP endpoint.
///
/// Three things do, and they are layered deliberately. Routes cannot claim /mcp or /api-mcp, so a
/// first hop can never land on one. The guard strips its own signalling before forwarding, so an
/// upstream that points back at this app arrives with no key and is refused. And each gate refuses
/// the hop markers that would mean a loop — which is the layer that would still hold if either of
/// the other two were ever weakened.
/// </summary>
public class McpApiBridgeLoopTests : IAsyncLifetime
{
    private const string Manifest = """
        {
          "version": 1,
          "tools": [
            { "name": "back_to_the_bridge", "request": { "method": "POST", "path": "/api-mcp/tracker" } },
            { "name": "back_to_the_funnel", "request": { "method": "POST", "path": "/mcp/agent" } }
          ]
        }
        """;

    private FunnelTestHost _host = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _host = await FunnelTestHost.StartAsync();

        // The upstream is this app itself, which is the arrangement a loop would need.
        var upstream = new UpstreamRecord { Name = "self", BaseUrl = _host.BaseUrl };

        var route = new RouteMapping
        {
            PathPrefix = "/self",
            UpstreamId = upstream.Id,
            StripPrefix = true,
        };

        await _host.MutateAsync(store =>
        {
            store.Upstreams.Add(upstream);
            store.Routes.Add(route);
        });
        _host.RebuildProxyConfig();

        await _host.AddApiBridgeAsync("tracker", route.Id, Manifest);
        await _host.AddFunnelAsync("agent");

        _client = new HttpClient { BaseAddress = new Uri(_host.BaseUrl) };
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _host.DisposeAsync();
    }

    [Fact]
    public async Task AToolAimedBackAtItsOwnBridgeFailsAtTheSecondHop()
    {
        var client = await _host.ConnectBridgeAsync("tracker");

        var result = await client.CallToolAsync("back_to_the_bridge", new Dictionary<string, object?>());

        // The second hop arrives without the key the guard stripped, so it is refused there rather
        // than being served and calling back in again.
        Assert.True(result.IsError);
        Assert.Contains("403", Text(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AToolAimedAtAFunnelFailsTheSameWay()
    {
        var client = await _host.ConnectBridgeAsync("tracker");

        var result = await client.CallToolAsync("back_to_the_funnel", new Dictionary<string, object?>());

        Assert.True(result.IsError);
        Assert.Contains("403", Text(result), StringComparison.Ordinal);
    }

    /// <summary>
    /// The gate layer on its own: a request carrying a bridge's hop marker is refused by both MCP
    /// endpoints even when it presents a perfectly good key.
    /// </summary>
    [Fact]
    public async Task BothGatesRefuseARequestThatAlreadyPassedThroughABridge()
    {
        Assert.Equal(HttpStatusCode.NotFound,
            await InitializeAsync("/api-mcp/tracker", LocalAccessGuard.BridgeHopHeaderName));

        Assert.Equal(HttpStatusCode.NotFound,
            await InitializeAsync("/mcp/agent", LocalAccessGuard.BridgeHopHeaderName));
    }

    /// <summary>
    /// The funnel refuses its own marker as it always has; the bridge tolerates it, because a
    /// funnel pooling a bridge carries exactly that header and is the feature.
    /// </summary>
    [Fact]
    public async Task OnlyTheFunnelRefusesAFunnelsMarker()
    {
        Assert.Equal(HttpStatusCode.NotFound,
            await InitializeAsync("/mcp/agent", LocalAccessGuard.FunnelHopHeaderName));

        Assert.NotEqual(HttpStatusCode.NotFound,
            await InitializeAsync("/api-mcp/tracker", LocalAccessGuard.FunnelHopHeaderName));
    }

    private async Task<HttpStatusCode> InitializeAsync(string path, string hopHeader)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(
                """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"t","version":"1"}}}""",
                Encoding.UTF8,
                "application/json"),
        };

        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
        request.Headers.TryAddWithoutValidation(LocalAccessGuard.ApiKeyHeaderName, FunnelTestHost.ApiKey);
        request.Headers.TryAddWithoutValidation(hopHeader, "1");

        using var response = await _client.SendAsync(request);
        return response.StatusCode;
    }

    private static string Text(CallToolResult result) =>
        string.Join("\n", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
}
