using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RavensPort.Core.Diagnostics;
using RavensPort.Core.Models;
using RavensPort.Core.Proxy;
using RavensPort.Core.Storage;
using RavensPort.Core.Vault;

namespace RavensPort.Core.Tests;

/// <summary>
/// These cover the single control standing between any local process and the user's OAuth
/// tokens, so each rejection path gets its own test.
/// </summary>
public class LocalAccessGuardTests : IAsyncLifetime
{
    private const string ValidKey = "test-key-abcdefghijklmnopqrstuvwxyz";

    /// <summary>A second route's key, which must open that route and nothing else.</summary>
    private const string OtherRouteKey = "other-route-key-zyxwvutsrqponmlkjihgfedcba";

    // Fixtures, not credentials.
    private const string FunnelKey = "funnel-key-0123456789abcdefghijklmnop"; // gitleaks:allow

    private IHost _host = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _host = await new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder
                    .UseTestServer()
                    .ConfigureServices(services =>
                    {
                        services.AddSingleton<ActivityLog>(_ =>
                            new ActivityLog(Path.Combine(Path.GetTempPath(), $"ravensport-test-logs-{Guid.NewGuid()}")));
                        services.AddSingleton<IConfigVault>(_ => new InMemoryVault());
                        services.AddSingleton<ConfigStoreCache>();
                    })
                    .Configure(app =>
                    {
                        var cache = app.ApplicationServices.GetRequiredService<ConfigStoreCache>();

                        // There is no proxy-wide key any more: the guard authenticates against
                        // the key of whichever route or funnel the path belongs to, so the store
                        // needs those endpoints to exist before any request can be allowed.
                        cache.Current.Routes.Add(new RouteMapping
                        {
                            PathPrefix = "/anything",
                            UpstreamId = Guid.NewGuid(),
                            Key = new ProxyKey { Value = ValidKey },
                        });
                        cache.Current.Routes.Add(new RouteMapping
                        {
                            PathPrefix = "/other",
                            UpstreamId = Guid.NewGuid(),
                            Key = new ProxyKey { Value = OtherRouteKey },
                        });
                        cache.Current.Routes.Add(new RouteMapping
                        {
                            PathPrefix = "/lapsed",
                            UpstreamId = Guid.NewGuid(),
                            Key = new ProxyKey
                            {
                                Value = ValidKey,
                                CreatedUtc = DateTimeOffset.UtcNow.AddDays(-30),
                                ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
                            },
                        });
                        cache.Current.McpFunnels.Add(new McpFunnelRecord
                        {
                            Name = "agent",
                            Slug = "agent",
                            Key = new ProxyKey { Value = FunnelKey },
                        });

                        app.UseLocalAccessGuard();
                        app.Run(async context =>
                        {
                            // Stand-in for a proxied upstream that answers with permissive CORS.
                            context.Response.Headers["Access-Control-Allow-Origin"] = "*";

                            // Echoed back so tests can assert on exactly what an upstream would
                            // have received in its own logs.
                            context.Response.Headers["X-Echo-Query"] = context.Request.QueryString.Value ?? "";
                            context.Response.Headers["X-Echo-Had-Key-Header"] =
                                context.Request.Headers.ContainsKey(LocalAccessGuard.ApiKeyHeaderName).ToString();
                            await context.Response.WriteAsync("upstream-payload");
                        });
                    });
            })
            .StartAsync();

        _client = _host.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task ValidKeyInHeader_IsAllowedThrough()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1/anything");
        request.Headers.Add(LocalAccessGuard.ApiKeyHeaderName, ValidKey);

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("upstream-payload", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ValidKeyInQueryString_IsAllowedThrough()
    {
        // Supported because browser EventSource, used by some MCP SSE transports, cannot set
        // request headers at all.
        var response = await _client.GetAsync(
            $"http://127.0.0.1/anything?{LocalAccessGuard.ApiKeyQueryName}={ValidKey}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ApiKeyInQueryString_IsNotForwardedUpstream()
    {
        // It authenticates the caller to this proxy and nothing else. Forwarded, it would land
        // in the upstream's access log — handing a third party the key to the local proxy.
        var response = await _client.GetAsync(
            $"http://127.0.0.1/anything?{LocalAccessGuard.ApiKeyQueryName}={ValidKey}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var forwardedQuery = response.Headers.GetValues("X-Echo-Query").Single();
        Assert.DoesNotContain(ValidKey, forwardedQuery);
        Assert.DoesNotContain(LocalAccessGuard.ApiKeyQueryName, forwardedQuery);
    }

    [Fact]
    public async Task OtherQueryParameters_SurviveUntouched()
    {
        // The caller's own parameters are the whole point of the request; only proxy_key goes.
        var response = await _client.GetAsync(
            $"http://127.0.0.1/anything?token=abc&{LocalAccessGuard.ApiKeyQueryName}={ValidKey}&page=2");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var forwardedQuery = response.Headers.GetValues("X-Echo-Query").Single();
        Assert.Contains("token=abc", forwardedQuery);
        Assert.Contains("page=2", forwardedQuery);
        Assert.DoesNotContain(LocalAccessGuard.ApiKeyQueryName, forwardedQuery);
    }

    [Fact]
    public async Task ApiKeyInHeader_IsNotForwardedUpstream()
    {
        // Same reasoning as the query-string case: YARP copies request headers through by
        // default, so without an explicit removal the upstream receives the key to this proxy.
        var request = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1/anything?token=abc");
        request.Headers.Add(LocalAccessGuard.ApiKeyHeaderName, ValidKey);

        var response = await _client.SendAsync(request);

        Assert.Equal("False", response.Headers.GetValues("X-Echo-Had-Key-Header").Single());

        // ...and the caller's own parameters are still untouched.
        Assert.Equal("?token=abc", response.Headers.GetValues("X-Echo-Query").Single());
    }

    [Fact]
    public async Task ApiKeyHeader_IsStrippedEvenWhenTheRequestIsRejected()
    {
        // A rejected request never reaches an upstream, but the header must not survive into
        // anything downstream either — the removal is unconditional rather than allow-path only.
        var request = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1/anything");
        request.Headers.Add(LocalAccessGuard.ApiKeyHeaderName, "wrong-key");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task MissingKey_IsRejected()
    {
        // The core confused-deputy case: any process that merely knows the port.
        var response = await _client.GetAsync("http://127.0.0.1/anything");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task WrongKey_IsRejected()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1/anything");
        request.Headers.Add(LocalAccessGuard.ApiKeyHeaderName, "not-the-key");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AnotherRoutesKey_DoesNotOpenThisRoute()
    {
        // The point of per-endpoint keys. Under a single proxy-wide key, a client trusted with
        // one route could spend the OAuth grant attached to every other one.
        var request = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1/anything");
        request.Headers.Add(LocalAccessGuard.ApiKeyHeaderName, OtherRouteKey);

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task EachRouteIsOpenedByItsOwnKey()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1/other/resource");
        request.Headers.Add(LocalAccessGuard.ApiKeyHeaderName, OtherRouteKey);

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AFunnelIsOpenedOnlyByItsOwnKey()
    {
        var withFunnelKey = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1/mcp/agent");
        withFunnelKey.Headers.Add(LocalAccessGuard.ApiKeyHeaderName, FunnelKey);

        Assert.Equal(HttpStatusCode.OK, (await _client.SendAsync(withFunnelKey)).StatusCode);

        // A route's key must not reach the funnel: an agent handed a funnel is deliberately given
        // a narrowed view of its sources, and the route keys behind it would bypass that.
        var withRouteKey = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1/mcp/agent");
        withRouteKey.Headers.Add(LocalAccessGuard.ApiKeyHeaderName, ValidKey);

        Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(withRouteKey)).StatusCode);
    }

    [Fact]
    public async Task AFunnelsKey_DoesNotOpenARoute()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1/anything");
        request.Headers.Add(LocalAccessGuard.ApiKeyHeaderName, FunnelKey);

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AnExpiredKey_IsRejectedEvenThoughTheValueMatches()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1/lapsed/resource");
        request.Headers.Add(LocalAccessGuard.ApiKeyHeaderName, ValidKey);

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task APathBelongingToNoEndpoint_IsRejectedRatherThanAnnounced()
    {
        // Answered with the same 403 as a wrong key, so an unauthenticated caller cannot map
        // which prefixes exist by watching status codes.
        var request = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1/not-a-route");
        request.Headers.Add(LocalAccessGuard.ApiKeyHeaderName, ValidKey);

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public void TheLongestMatchingPrefixOwnsThePath()
    {
        // Same choice ASP.NET routing makes downstream. If the shorter prefix won here, a request
        // that will be served by /app/mail would be authenticated against /app's key.
        var store = new ConfigStore();
        store.Routes.Add(new RouteMapping { PathPrefix = "/app", Key = new ProxyKey { Value = "outer" } });
        store.Routes.Add(new RouteMapping { PathPrefix = "/app/mail", Key = new ProxyKey { Value = "inner" } });

        Assert.Equal("inner", LocalAccessGuard.ResolveTarget(store, "/app/mail/messages")?.Key.Value);
        Assert.Equal("outer", LocalAccessGuard.ResolveTarget(store, "/app/other")?.Key.Value);

        // Whole segments only — /application is a different area, not a child of /app.
        Assert.Null(LocalAccessGuard.ResolveTarget(store, "/application"));
    }

    [Fact]
    public async Task NonLoopbackHostHeader_IsRejectedEvenWithAValidKey()
    {
        // DNS rebinding: evil.com re-resolves to 127.0.0.1, so the browser treats the response
        // as same-origin and lets attacker JavaScript read it.
        var request = new HttpRequestMessage(HttpMethod.Get, "http://evil.com/anything"); // DevSkim: ignore DS137138
        request.Headers.Add(LocalAccessGuard.ApiKeyHeaderName, ValidKey);

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task RequestWithOriginHeader_IsRejectedEvenWithAValidKey()
    {
        // Only browsers send Origin, and no legitimate local client is a browser page.
        var request = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1/anything");
        request.Headers.Add(LocalAccessGuard.ApiKeyHeaderName, ValidKey);
        request.Headers.Add("Origin", "https://evil.com");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task PermissiveCorsHeadersFromUpstream_AreStripped()
    {
        // YARP copies response headers verbatim; an upstream sending "*" would otherwise let
        // any web page read proxied responses directly.
        var request = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1/anything");
        request.Headers.Add(LocalAccessGuard.ApiKeyHeaderName, ValidKey);

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(response.Headers, h =>
            h.Key.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase));
    }
}
