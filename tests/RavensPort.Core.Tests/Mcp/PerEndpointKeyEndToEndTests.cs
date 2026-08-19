using System.Net;
using System.Text;
using RavensPort.Core.Models;
using RavensPort.Core.Proxy;

namespace RavensPort.Core.Tests.Mcp;

/// <summary>
/// Per-endpoint proxy keys, driven through the real pipeline on a real socket: guard, funnel gate,
/// funnel endpoints, YARP, and the credential transform, with an MCP client on one end and an MCP
/// server on the other.
///
/// <see cref="LocalAccessGuardTests"/> covers the guard's decision in isolation. What is proved
/// here is that the decision survives the whole stack — that a key really does open exactly one
/// endpoint when the request is an ordinary proxied call, an MCP handshake, or the funnel's own
/// hop back into a route, and that expiry and regeneration take effect on the next request rather
/// than at some restart.
/// </summary>
public class PerEndpointKeyEndToEndTests : IAsyncLifetime
{
    private const string Token = "PER-ENDPOINT-ACCESS-TOKEN";

    private const string RouteOneKey = "key-for-route-one-aaaaaaaaaaaaaaaaaaaa";
    private const string RouteTwoKey = "key-for-route-two-bbbbbbbbbbbbbbbbbbbb";
    private const string SourceRouteKey = "key-for-source-route-cccccccccccccccc";
    private const string AlphaFunnelKey = "key-for-funnel-alpha-dddddddddddddddd";
    private const string BetaFunnelKey = "key-for-funnel-beta-eeeeeeeeeeeeeeeee";

    private FakeMcpServer _upstream = null!;
    private FunnelTestHost _host = null!;
    private HttpClient _client = null!;

    private RouteMapping _routeOne = null!;
    private RouteMapping _routeTwo = null!;
    private RouteMapping _sourceRoute = null!;
    private McpFunnelRecord _alpha = null!;
    private McpFunnelRecord _beta = null!;

    public async Task InitializeAsync()
    {
        _upstream = await FakeMcpServer.StartAsync();
        _host = await FunnelTestHost.StartAsync();

        var credential = new CredentialRecord
        {
            Name = "per-endpoint-credential",
            ClientId = "id",
            ClientSecret = "secret",
            Token = new TokenSet(Token, "refresh", DateTimeOffset.UtcNow.AddHours(1), "Bearer", DateTimeOffset.UtcNow),
        };
        var upstreamRecord = new UpstreamRecord { Name = "u", BaseUrl = _upstream.Url };

        _routeOne = Route("/api-one", upstreamRecord.Id, credential.Id, RouteOneKey);
        _routeTwo = Route("/api-two", upstreamRecord.Id, credential.Id, RouteTwoKey);
        _sourceRoute = Route("/mcpsrv", upstreamRecord.Id, credential.Id, SourceRouteKey);

        await _host.MutateAsync(store =>
        {
            store.Credentials.Add(credential);
            store.Upstreams.Add(upstreamRecord);
            store.Routes.Add(_routeOne);
            store.Routes.Add(_routeTwo);
            store.Routes.Add(_sourceRoute);
        });
        _host.RebuildProxyConfig();

        var source = await _host.AddRouteSourceAsync("auth", _sourceRoute.Id);

        // Two funnels over the same source, each with its own key — the arrangement the whole
        // feature exists for: two agents, one credentialed upstream, no shared secret between them.
        _alpha = await _host.AddFunnelAsync("alpha", new McpFunnelSource { SourceId = source.Id });
        _beta = await _host.AddFunnelAsync("beta", new McpFunnelSource { SourceId = source.Id });

        await _host.MutateAsync(_ =>
        {
            _alpha.Key.Value = AlphaFunnelKey;
            _beta.Key.Value = BetaFunnelKey;
        });

        _client = new HttpClient { BaseAddress = new Uri(_host.BaseUrl) };
    }

    private static RouteMapping Route(string prefix, Guid upstreamId, Guid credentialId, string key) => new()
    {
        PathPrefix = prefix,
        UpstreamId = upstreamId,
        Credentials = [RouteCredential.For(credentialId, CredentialPlacement.Header)],
        StripPrefix = true,
        Key = new ProxyKey { Value = key },
    };

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _host.DisposeAsync();
        await _upstream.DisposeAsync();
    }

    // ---- routes ---------------------------------------------------------------------------

    [Fact]
    public async Task EachRouteIsServedByItsOwnKeyAndNoOther()
    {
        // The full matrix, because "a key opens one endpoint" is only meaningful if the same key
        // is checked against an endpoint it must not open.
        Assert.Equal(HttpStatusCode.OK, await PostAsync("/api-one", RouteOneKey));
        Assert.Equal(HttpStatusCode.OK, await PostAsync("/api-two", RouteTwoKey));

        Assert.Equal(HttpStatusCode.Forbidden, await PostAsync("/api-one", RouteTwoKey));
        Assert.Equal(HttpStatusCode.Forbidden, await PostAsync("/api-two", RouteOneKey));

        // A funnel's key is not a route key either, in either direction.
        Assert.Equal(HttpStatusCode.Forbidden, await PostAsync("/api-one", AlphaFunnelKey));
    }

    [Fact]
    public async Task AKeyOpensEveryPathBeneathItsOwnRoute()
    {
        // The key belongs to the route, not to one URL under it, so everything the route serves
        // takes the same key.
        Assert.Equal(HttpStatusCode.OK, await PostAsync("/api-one", RouteOneKey));

        // 404 rather than 200 because the fake upstream implements one endpoint and this is not
        // it — which is the proof wanted here. A path this proxy refuses answers 403, and a path
        // it does not recognise answers 403 too, so only a forwarded request can come back 404.
        Assert.Equal(HttpStatusCode.NotFound, await PostAsync("/api-one/nested/deeper?page=2", RouteOneKey));

        // ...and the same deep path with a different route's key is refused before forwarding.
        Assert.Equal(HttpStatusCode.Forbidden, await PostAsync("/api-one/nested/deeper", RouteTwoKey));
    }

    [Fact]
    public async Task AServedRequestStillCarriesTheOAuthTokenAndNotTheProxyKey()
    {
        // Per-endpoint keys must not have disturbed what the upstream actually receives: the
        // route's credential arrives, and the key that authenticated the caller to this proxy
        // does not.
        Assert.Equal(HttpStatusCode.OK, await PostAsync("/api-one", RouteOneKey));

        var headers = Assert.Single(_upstream.ReceivedHeaders);
        Assert.Equal($"Bearer {Token}", headers["Authorization"]);
        Assert.False(headers.ContainsKey(LocalAccessGuard.ApiKeyHeaderName));
    }

    [Fact]
    public async Task AKeyInTheQueryDoesNotOpenAnEndpointItWouldOtherwiseOpen()
    {
        // The right key for the right endpoint, in the wrong place. Accepted once, refused now:
        // a URL is written down where a header is not, and this key is all that stands between a
        // local caller and the user's grant.
        Assert.Equal(HttpStatusCode.Forbidden,
            await PostAsync($"/api-one?{LocalAccessGuard.ApiKeyQueryName}={RouteOneKey}", presentedKey: null));

        Assert.Empty(_upstream.ReceivedHeaders);
    }

    [Fact]
    public async Task RegeneratingARoutesKeyRevokesTheOldOneImmediatelyAndOnlyForThatRoute()
    {
        Assert.Equal(HttpStatusCode.OK, await PostAsync("/api-one", RouteOneKey));

        string replacement = "";
        await _host.MutateAsync(_ =>
        {
            _routeOne.Key.Regenerate();
            replacement = _routeOne.Key.Value;
        });

        // No restart, no config rebuild: the guard reads the store live.
        Assert.Equal(HttpStatusCode.Forbidden, await PostAsync("/api-one", RouteOneKey));
        Assert.Equal(HttpStatusCode.OK, await PostAsync("/api-one", replacement));

        // The neighbouring route is untouched — that is the point of revoking one row.
        Assert.Equal(HttpStatusCode.OK, await PostAsync("/api-two", RouteTwoKey));
    }

    [Fact]
    public async Task AnExpiredRouteKeyIsRefusedUntilItsLifetimeIsExtended()
    {
        await _host.MutateAsync(_ => _routeOne.Key.ExpiresUtc = DateTimeOffset.UtcNow.AddSeconds(-1));

        Assert.Equal(HttpStatusCode.Forbidden, await PostAsync("/api-one", RouteOneKey));

        // Extending is enough; the value itself never had to change.
        await _host.MutateAsync(_ => _routeOne.Key.SetLifetime(TimeSpan.FromDays(30)));

        Assert.Equal(HttpStatusCode.OK, await PostAsync("/api-one", RouteOneKey));

        // And "never expires" is genuinely unbounded rather than a very long period.
        await _host.MutateAsync(_ => _routeOne.Key.SetLifetime(null));
        Assert.Equal(HttpStatusCode.OK, await PostAsync("/api-one", RouteOneKey));
    }

    [Fact]
    public async Task ADeletedRoutesKeyStopsOpeningAnything()
    {
        await _host.MutateAsync(store => store.Routes.RemoveAll(r => r.Id == _routeOne.Id));
        _host.RebuildProxyConfig();

        Assert.Equal(HttpStatusCode.Forbidden, await PostAsync("/api-one", RouteOneKey));
    }

    // ---- funnels --------------------------------------------------------------------------

    [Fact]
    public async Task AFunnelServesAnAgentHoldingItsOwnKey()
    {
        var client = await _host.ConnectAsync("alpha", AlphaFunnelKey);

        Assert.Contains("auth__echo", (await client.ListToolsAsync()).Select(t => t.Name));
        Assert.Equal("hello", await FunnelTestHost.CallTextAsync(client, "auth__echo", "hello"));
    }

    [Fact]
    public async Task AFunnelRefusesEveryKeyButItsOwn()
    {
        // Raw HTTP rather than the MCP client, so the refusal can be asserted as a status code
        // instead of whatever the transport chooses to throw.
        Assert.Equal(HttpStatusCode.Forbidden, await PostAsync("/mcp/alpha", BetaFunnelKey));
        Assert.Equal(HttpStatusCode.Forbidden, await PostAsync("/mcp/alpha", SourceRouteKey));
        Assert.Equal(HttpStatusCode.Forbidden, await PostAsync("/mcp/alpha", RouteOneKey));

        // ...and the endpoint is genuinely alive for the key that belongs to it.
        Assert.NotEqual(HttpStatusCode.Forbidden, await PostAsync("/mcp/alpha", AlphaFunnelKey));
    }

    [Fact]
    public async Task AnAgentHoldingTheWrongFunnelsKeyCannotConnect()
    {
        // What an agent actually experiences: the handshake fails rather than yielding a session
        // with someone else's tools.
        await Assert.ThrowsAnyAsync<Exception>(() => _host.ConnectAsync("alpha", BetaFunnelKey));

        // The refusal is specific to the pairing, not to the endpoint being broken.
        var client = await _host.ConnectAsync("beta", BetaFunnelKey);
        Assert.NotEmpty(await client.ListToolsAsync());
    }

    [Fact]
    public async Task TwoAgentsOnTwoFunnelsOverOneRouteEachUseTheirOwnKey()
    {
        // The end state the feature is for: two agents, one credentialed upstream, three different
        // secrets in play (two funnel keys and the route's), none of which opens the others.
        var alpha = await _host.ConnectAsync("alpha", AlphaFunnelKey);
        var beta = await _host.ConnectAsync("beta", BetaFunnelKey);

        Assert.Equal("from-alpha", await FunnelTestHost.CallTextAsync(alpha, "auth__echo", "from-alpha"));
        Assert.Equal("from-beta", await FunnelTestHost.CallTextAsync(beta, "auth__echo", "from-beta"));

        // Separate upstream sessions, as before — per-endpoint keys did not collapse the pooling.
        Assert.NotEqual(
            await FunnelTestHost.CallTextAsync(alpha, "auth__whoami"),
            await FunnelTestHost.CallTextAsync(beta, "auth__whoami"));

        // Every hop upstream carried the route's OAuth token, and none of them carried any of this
        // proxy's own secrets.
        Assert.NotEmpty(_upstream.ReceivedAuthorization);
        Assert.All(_upstream.ReceivedAuthorization, header => Assert.Equal($"Bearer {Token}", header));
        Assert.All(_upstream.ReceivedHeaders, headers =>
        {
            Assert.False(headers.ContainsKey(LocalAccessGuard.ApiKeyHeaderName));
            Assert.False(headers.ContainsKey(LocalAccessGuard.FunnelHopHeaderName));
        });
    }

    [Fact]
    public async Task AFunnelsKeyDoesNotReachTheRouteBehindIt()
    {
        // An agent given a funnel is deliberately shown a narrowed view of its sources. If the
        // funnel's key also opened the route the funnel pools, the agent could step around the
        // filtering by calling the route directly.
        Assert.Equal(HttpStatusCode.Forbidden, await PostAsync("/mcpsrv", AlphaFunnelKey));

        // The route is reachable — with its own key, which the agent is not given.
        Assert.Equal(HttpStatusCode.OK, await PostAsync("/mcpsrv", SourceRouteKey));
    }

    [Fact]
    public async Task RegeneratingAFunnelsKeyLeavesItsSourcesWorking()
    {
        // The funnel's key authenticates the agent to the funnel; the hop into the route uses the
        // route's. Rotating one must not disturb the other.
        string replacement = "";
        await _host.MutateAsync(_ =>
        {
            _alpha.Key.Regenerate();
            replacement = _alpha.Key.Value;
        });

        Assert.Equal(HttpStatusCode.Forbidden, await PostAsync("/mcp/alpha", AlphaFunnelKey));

        var client = await _host.ConnectAsync("alpha", replacement);
        Assert.Equal("still-working", await FunnelTestHost.CallTextAsync(client, "auth__echo", "still-working"));
    }

    [Fact]
    public async Task AnExpiredFunnelKeyClosesTheEndpointForItsAgent()
    {
        await _host.MutateAsync(_ => _alpha.Key.ExpiresUtc = DateTimeOffset.UtcNow.AddSeconds(-1));

        Assert.Equal(HttpStatusCode.Forbidden, await PostAsync("/mcp/alpha", AlphaFunnelKey));
        await Assert.ThrowsAnyAsync<Exception>(() => _host.ConnectAsync("alpha", AlphaFunnelKey));

        // One funnel lapsing says nothing about the other.
        Assert.NotEqual(HttpStatusCode.Forbidden, await PostAsync("/mcp/beta", BetaFunnelKey));
    }

    [Fact]
    public async Task AnExpiredRouteKeyCutsOffTheFunnelsThatPoolThatRoute()
    {
        // Expiry is meant to cascade: the funnel's hop is an ordinary call to the route and is
        // refused like any other. The funnel itself stays up — a dead source degrades only
        // itself — so the agent sees the source's tools disappear rather than a broken endpoint.
        var before = await _host.ConnectAsync("alpha", AlphaFunnelKey);
        Assert.Contains("auth__echo", (await before.ListToolsAsync()).Select(t => t.Name));

        await _host.MutateAsync(_ => _sourceRoute.Key.ExpiresUtc = DateTimeOffset.UtcNow.AddSeconds(-1));
        await _host.Pool.InvalidateAllAsync();

        var during = await _host.ConnectAsync("alpha", AlphaFunnelKey);
        Assert.DoesNotContain("auth__echo", (await during.ListToolsAsync()).Select(t => t.Name));

        // ...and comes back on the next call once the key is valid again, with nothing else to do.
        await _host.MutateAsync(_ => _sourceRoute.Key.SetLifetime(null));
        await _host.Pool.InvalidateAllAsync();

        var after = await _host.ConnectAsync("alpha", AlphaFunnelKey);
        Assert.Contains("auth__echo", (await after.ListToolsAsync()).Select(t => t.Name));
    }

    [Fact]
    public async Task ADeletedFunnelsKeyStopsOpeningAnything()
    {
        await _host.MutateAsync(store => store.McpFunnels.RemoveAll(f => f.Id == _alpha.Id));

        Assert.Equal(HttpStatusCode.Forbidden, await PostAsync("/mcp/alpha", AlphaFunnelKey));
        Assert.NotEqual(HttpStatusCode.Forbidden, await PostAsync("/mcp/beta", BetaFunnelKey));
    }

    [Fact]
    public async Task EveryEndpointGotItsOwnDistinctKey()
    {
        // Two endpoints that happened to share a key would pass every test above while providing
        // none of the isolation they claim.
        var store = _host.Cache.Current;

        var keys = store.Routes.Select(r => r.Key.Value)
            .Concat(store.McpFunnels.Select(f => f.Key.Value))
            .ToList();

        Assert.All(keys, key => Assert.False(string.IsNullOrEmpty(key)));
        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
    }

    // ---- helpers --------------------------------------------------------------------------

    /// <summary>
    /// Posts a JSON-RPC ping, presenting <paramref name="presentedKey"/> as the proxy key header.
    /// Pass null to send none — used when the key is in the query string instead.
    /// </summary>
    private async Task<HttpStatusCode> PostAsync(string path, string? presentedKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"ping"}""", Encoding.UTF8, "application/json"),
        };

        if (presentedKey is not null)
        {
            request.Headers.Add(LocalAccessGuard.ApiKeyHeaderName, presentedKey);
        }

        request.Headers.Accept.ParseAdd("application/json, text/event-stream");

        var response = await _client.SendAsync(request);
        return response.StatusCode;
    }
}
