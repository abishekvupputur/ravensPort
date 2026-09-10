using System.Net;
using System.Text;
using RavensPort.Core.Mcp;
using RavensPort.Core.Models;
using RavensPort.Core.Proxy;

namespace RavensPort.Core.Tests.Mcp;

/// <summary>
/// A bridge is a third kind of endpoint with a key of its own, so the matrix the funnel and the
/// routes already satisfy has to hold across all three.
///
/// The claim being pinned: a key handed to an agent opens exactly the endpoint it was issued for.
/// Not the route that endpoint calls — which is the one that would matter most, since that key
/// spends the OAuth grant on every path the route serves — and not a funnel, and not another
/// bridge.
/// </summary>
public class McpApiBridgeKeyIsolationTests : IAsyncLifetime
{
    private const string RouteKey = "key-for-the-route-aaaaaaaaaaaaaaaaaaaa";
    private const string BridgeKey = "key-for-the-bridge-bbbbbbbbbbbbbbbbbb";
    private const string OtherBridgeKey = "key-for-other-bridge-cccccccccccccccc";
    private const string FunnelKey = "key-for-the-funnel-dddddddddddddddddd";

    private const string Manifest = """
        {
          "version": 1,
          "tools": [
            { "name": "ping", "request": { "method": "GET", "path": "/ping" } }
          ]
        }
        """;

    private FakeRestApi _upstream = null!;
    private FunnelTestHost _host = null!;
    private HttpClient _client = null!;
    private McpApiBridgeRecord _bridge = null!;

    public async Task InitializeAsync()
    {
        _upstream = await FakeRestApi.StartAsync();
        _host = await FunnelTestHost.StartAsync();

        var upstream = new UpstreamRecord { Name = "u", BaseUrl = _upstream.Url };

        var route = new RouteMapping
        {
            PathPrefix = "/api",
            UpstreamId = upstream.Id,
            StripPrefix = true,
            Key = new ProxyKey { Value = RouteKey },
        };

        await _host.MutateAsync(store =>
        {
            store.Upstreams.Add(upstream);
            store.Routes.Add(route);
        });
        _host.RebuildProxyConfig();

        _bridge = await _host.AddApiBridgeAsync("tracker", route.Id, Manifest);
        var other = await _host.AddApiBridgeAsync("other", route.Id, Manifest);
        var funnel = await _host.AddFunnelAsync("agent");

        await _host.MutateAsync(_ =>
        {
            _bridge.Key.Value = BridgeKey;
            other.Key.Value = OtherBridgeKey;
            funnel.Key.Value = FunnelKey;
        });

        _client = new HttpClient { BaseAddress = new Uri(_host.BaseUrl) };
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _host.DisposeAsync();
        await _upstream.DisposeAsync();
    }

    /// <summary>
    /// Posts an MCP initialize to a path with a given key. 403 is the guard's refusal, 404 the
    /// gate's, and anything else means the request got through to the MCP machinery.
    /// </summary>
    private async Task<HttpStatusCode> InitializeAsync(string path, string? key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(
                """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"t","version":"1"}}}""",
                Encoding.UTF8,
                "application/json"),
        };

        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
        if (key is not null) request.Headers.TryAddWithoutValidation(LocalAccessGuard.ApiKeyHeaderName, key);

        using var response = await _client.SendAsync(request);
        return response.StatusCode;
    }

    [Fact]
    public async Task OnlyTheBridgesOwnKeyOpensIt()
    {
        Assert.NotEqual(HttpStatusCode.Forbidden, await InitializeAsync("/api-mcp/tracker", BridgeKey));

        Assert.Equal(HttpStatusCode.Forbidden, await InitializeAsync("/api-mcp/tracker", OtherBridgeKey));
        Assert.Equal(HttpStatusCode.Forbidden, await InitializeAsync("/api-mcp/tracker", FunnelKey));
        Assert.Equal(HttpStatusCode.Forbidden, await InitializeAsync("/api-mcp/tracker", null));

        // The one that matters most: the key of the route this bridge calls does not open the
        // bridge, and the bridge's key does not open the route — so an agent given the bridge's
        // key can reach the manifest's operations and nothing else the route serves.
        Assert.Equal(HttpStatusCode.Forbidden, await InitializeAsync("/api-mcp/tracker", RouteKey));
        Assert.Equal(HttpStatusCode.Forbidden, await InitializeAsync("/api/anything", BridgeKey));
    }

    [Fact]
    public async Task ABridgeKeyDoesNotOpenAFunnelAndViceVersa()
    {
        Assert.Equal(HttpStatusCode.Forbidden, await InitializeAsync("/mcp/agent", BridgeKey));
        Assert.Equal(HttpStatusCode.Forbidden, await InitializeAsync("/api-mcp/tracker", FunnelKey));
    }

    [Fact]
    public async Task RegeneratingTheKeyRevokesTheOldOneImmediately()
    {
        Assert.NotEqual(HttpStatusCode.Forbidden, await InitializeAsync("/api-mcp/tracker", BridgeKey));

        await _host.MutateAsync(store => store.McpApiBridges.Single(b => b.Id == _bridge.Id).Key.Regenerate());

        Assert.Equal(HttpStatusCode.Forbidden, await InitializeAsync("/api-mcp/tracker", BridgeKey));
    }

    [Fact]
    public async Task AnExpiredKeyStopsOpeningTheEndpoint()
    {
        await _host.MutateAsync(store =>
        {
            var key = store.McpApiBridges.Single(b => b.Id == _bridge.Id).Key;
            key.CreatedUtc = DateTimeOffset.UtcNow.AddHours(-2);
            key.SetLifetime(TimeSpan.FromHours(1));
        });

        Assert.Equal(HttpStatusCode.Forbidden, await InitializeAsync("/api-mcp/tracker", BridgeKey));
    }

    /// <summary>
    /// A slug naming no bridge belongs to no endpoint, so the guard refuses it as an unrecognised
    /// path before any gate sees it — the same answer a wrong key gets, and the same behaviour the
    /// funnel has. Nothing a caller sends distinguishes "no such bridge" from "not your key".
    ///
    /// A bridge that exists but is switched off is different: it still resolves, so its own key
    /// still passes the guard and the gate answers 404. Only the holder of that bridge's key can
    /// see the difference, and it is their bridge.
    /// </summary>
    [Fact]
    public async Task AnUnknownBridgeIsRefusedAndADisabledOneAnswers404ToItsOwner()
    {
        Assert.Equal(HttpStatusCode.Forbidden, await InitializeAsync("/api-mcp/never-existed", BridgeKey));
        Assert.Equal(HttpStatusCode.Forbidden, await InitializeAsync("/api-mcp/never-existed", "guessing"));

        await _host.MutateAsync(store => store.McpApiBridges.Single(b => b.Id == _bridge.Id).Enabled = false);

        Assert.Equal(HttpStatusCode.NotFound, await InitializeAsync("/api-mcp/tracker", BridgeKey));
        Assert.Equal(HttpStatusCode.Forbidden, await InitializeAsync("/api-mcp/tracker", FunnelKey));
    }

    [Fact]
    public async Task WithTheFeatureOffTheWholePathLooksLikeItDoesNotExist()
    {
        await _host.MutateAsync(store => store.Settings.McpApiBridgeEnabled = false);

        Assert.Equal(HttpStatusCode.NotFound, await InitializeAsync("/api-mcp/tracker", BridgeKey));
        Assert.Equal(HttpStatusCode.Forbidden, await InitializeAsync("/api-mcp/tracker", "not-a-key"));
    }

    /// <summary>
    /// The guard resolves by whole segment, so a route that merely starts with the same letters is
    /// still the route's.
    /// </summary>
    [Fact]
    public void TheGuardResolvesBridgePathsWithoutSwallowingSimilarRoutes()
    {
        var store = new ConfigStore();
        var bridge = new McpApiBridgeRecord { Name = "b", Slug = "tracker", Key = new ProxyKey { Value = BridgeKey } };
        var route = new RouteMapping { PathPrefix = "/api-mcpish", Key = new ProxyKey { Value = RouteKey } };

        store.McpApiBridges.Add(bridge);
        store.Routes.Add(route);

        Assert.Equal(BridgeKey, LocalAccessGuard.ResolveTarget(store, "/api-mcp/tracker")?.Key.Value);
        Assert.Equal(BridgeKey, LocalAccessGuard.ResolveTarget(store, "/api-mcp/tracker/sse")?.Key.Value);
        Assert.Equal(RouteKey, LocalAccessGuard.ResolveTarget(store, "/api-mcpish/x")?.Key.Value);

        Assert.Null(LocalAccessGuard.ResolveTarget(store, "/api-mcp"));
        Assert.Null(LocalAccessGuard.ResolveTarget(store, "/api-mcp/unknown"));
    }

    /// <summary>
    /// A route claiming the bridges' own path space would sit beside them in one routing table,
    /// and a bridge whose route was that path would call into itself.
    /// </summary>
    [Fact]
    public void ARouteCannotClaimTheBridgePathSpace()
    {
        Assert.NotNull(RouteValidation.ValidatePathPrefix(McpApiBridgeEndpoints.BasePath));
        Assert.NotNull(RouteValidation.ValidatePathPrefix("/api-mcp/something"));

        // A different segment that merely starts the same way is fine.
        Assert.Null(RouteValidation.ValidatePathPrefix("/api-mcpish"));
    }
}
