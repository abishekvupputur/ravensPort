using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using RavensPort.Core.Diagnostics;
using RavensPort.Core.Models;
using RavensPort.Core.Proxy;
using RavensPort.Core.Storage;
using RavensPort.Core.Vault;

namespace RavensPort.Core.Tests;

/// <summary>
/// End-to-end through the real YARP forwarder, against a real upstream on a real socket.
///
/// LocalAccessGuardTests stops at the middleware, which cannot prove what YARP actually
/// forwards: the guard removes the API key from HttpContext.Request, and whether that removal
/// is visible to the forwarder depends on YARP reading the live request rather than a snapshot
/// taken earlier in the pipeline. The only way to know is to look at what the upstream sees.
///
/// The credential placements are here for the same reason. Query and body injection both edit
/// state the forwarder consumes later — the query collection, and the request body stream or the
/// outgoing HttpContent — so only the upstream's view proves the edit survived.
/// </summary>
public class ProxyForwardingTests : IAsyncLifetime
{
    // Fixtures, not credentials.
    private const string ApiKey = "forwarding-test-key-0123456789"; // gitleaks:allow
    private const string Token = "UPSTREAM-ACCESS-TOKEN";

    private readonly string _logPath = Path.Combine(Path.GetTempPath(), $"ravensport-fwd-logs-{Guid.NewGuid()}");

    private WebApplication _upstream = null!;
    private WebApplication _proxy = null!;
    private HttpClient _client = null!;

    /// <summary>What the upstream saw on a request.</summary>
    private sealed record ReceivedRequest(
        string Path,
        string Query,
        Dictionary<string, string> Headers,
        string? ContentType,
        string Body)
    {
        public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;
        public string? Authorization => Header("Authorization");
        public string? Cookie => Header("Cookie");
    }

    private static readonly List<ReceivedRequest> Received = [];

    /// <summary>YARP's own explanation for the most recent failed forward, if there was one.</summary>
    private static string? LastForwarderError;

    public async Task InitializeAsync()
    {
        Received.Clear();

        // A genuine upstream listening on a loopback port, recording exactly what arrives.
        var upstreamBuilder = WebApplication.CreateBuilder();
        upstreamBuilder.WebHost.UseUrls("http://127.0.0.1:0");
        upstreamBuilder.Logging.ClearProviders();
        _upstream = upstreamBuilder.Build();
        _upstream.Run(async context =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync();

            Received.Add(new ReceivedRequest(
                context.Request.Path,
                context.Request.QueryString.Value ?? "",
                context.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase),
                context.Request.ContentType,
                body));

            await context.Response.WriteAsync("upstream-ok");
        });
        await _upstream.StartAsync();

        var upstreamUrl = _upstream.Services
            .GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!
            .Addresses.First();

        var proxyBuilder = WebApplication.CreateBuilder();
        proxyBuilder.WebHost.UseUrls("http://127.0.0.1:0");
        proxyBuilder.Logging.ClearProviders();
        proxyBuilder.Services.AddRavensPort();

        // An in-memory vault and a temp log path, so the test never reaches the password manager
        // or the log folder belonging to whoever runs the suite.
        proxyBuilder.Services.Replace(ServiceDescriptor.Singleton<IConfigVault>(_ => new InMemoryVault()));
        proxyBuilder.Services.Replace(ServiceDescriptor.Singleton(_ => new ActivityLog(_logPath)));

        _proxy = proxyBuilder.Build();

        // A forwarding failure surfaces to the client as a bare 502 with no detail, which makes
        // a broken transform very hard to tell apart from an upstream that fell over. YARP
        // records the real reason here; SendAsync puts it in the assertion message.
        _proxy.Use(async (context, next) =>
        {
            await next();
            if (context.Features.Get<Yarp.ReverseProxy.Forwarder.IForwarderErrorFeature>() is { } error)
            {
                LastForwarderError = $"{error.Error}: {error.Exception}";
            }
        });

        _proxy.UseLocalAccessGuard();
        _proxy.MapReverseProxy();
        await _proxy.StartAsync();

        // Configure routes now that the hosted-service initialization has run — one per
        // credential placement, so each test just picks the prefix it needs.
        var cache = _proxy.Services.GetRequiredService<ConfigStoreCache>();
        var credential = new CredentialRecord
        {
            Name = "test-credential",
            ClientId = "id",
            ClientSecret = "secret",
            Token = new TokenSet(Token, "refresh", DateTimeOffset.UtcNow.AddHours(1), "Bearer", DateTimeOffset.UtcNow),
        };
        var upstreamRecord = new UpstreamRecord { Name = "echo", BaseUrl = upstreamUrl };

        await cache.MutateAsync(store =>
        {
            store.Credentials.Add(credential);
            store.Upstreams.Add(upstreamRecord);

            // Every route here is given the same key value on purpose: these tests are about
            // what reaches the upstream, not about which key opens which route (that is
            // LocalAccessGuardTests), so one constant keeps the requests below readable.
            void AddRoute(string prefix, CredentialPlacement placement, string name, string valuePrefix) =>
                store.Routes.Add(new RouteMapping
                {
                    PathPrefix = prefix,
                    UpstreamId = upstreamRecord.Id,
                    StripPrefix = true,
                    Key = new ProxyKey { Value = ApiKey },
                    Credentials =
                    [
                        new RouteCredential
                        {
                            CredentialId = credential.Id,
                            Placement = placement,
                            ParameterName = name,
                            ValuePrefix = valuePrefix,
                        },
                    ],
                });

            // The default shape, spelled out rather than relying on the record's defaults.
            AddRoute("/app/echo", CredentialPlacement.Header, "Authorization", "Bearer ");
            AddRoute("/app/custom-header", CredentialPlacement.Header, "X-Api-Key", "");
            AddRoute("/app/prefixed-header", CredentialPlacement.Header, "X-Auth", "token ");
            AddRoute("/app/query", CredentialPlacement.Query, "access_token", "");
            AddRoute("/app/body", CredentialPlacement.Body, "access_token", "");
        });
        _proxy.Services.GetRequiredService<ProxyConfigChangeNotifier>().Rebuild();

        var proxyUrl = _proxy.Services
            .GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!
            .Addresses.First();

        _client = new HttpClient { BaseAddress = new Uri(proxyUrl) };
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _proxy.StopAsync();
        await _upstream.StopAsync();
        try { Directory.Delete(_logPath, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task KeyInHeader_ReachesUpstreamStrippedAndTokenInjected()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/app/echo/resource?token=abc");
        request.Headers.Add(LocalAccessGuard.ApiKeyHeaderName, ApiKey);

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var seen = Assert.Single(Received);
        Assert.Null(seen.Header(LocalAccessGuard.ApiKeyHeaderName));
        Assert.Equal("/resource", seen.Path);
        Assert.Equal("?token=abc", seen.Query);
        Assert.Equal($"Bearer {Token}", seen.Authorization);
    }

    [Fact]
    public async Task KeyInQuery_ReachesUpstreamStripped()
    {
        var response = await _client.GetAsync(
            $"/app/echo/resource?token=abc&{LocalAccessGuard.ApiKeyQueryName}={ApiKey}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var seen = Assert.Single(Received);
        Assert.DoesNotContain(ApiKey, seen.Query);
        Assert.DoesNotContain(LocalAccessGuard.ApiKeyQueryName, seen.Query);
        Assert.Equal("?token=abc", seen.Query);
    }

    [Fact]
    public async Task CallerSuppliedAuthorizationAndCookies_AreNotPassedThrough()
    {
        // A caller must not be able to use the proxy as a courier for its own credentials;
        // the only Authorization the upstream sees is the one this app attaches.
        var request = new HttpRequestMessage(HttpMethod.Get, "/app/echo/resource");
        request.Headers.Add(LocalAccessGuard.ApiKeyHeaderName, ApiKey);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "CALLER-SUPPLIED-TOKEN");
        request.Headers.Add("Cookie", "session=caller-session");

        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var seen = Assert.Single(Received);
        Assert.Equal($"Bearer {Token}", seen.Authorization);
        Assert.Null(seen.Cookie);
    }

    [Fact]
    public async Task CustomHeaderPlacement_SendsBareTokenInThatHeader()
    {
        // The X-Api-Key/PRIVATE-TOKEN shape: no prefix, no Authorization header at all.
        var seen = await SendAsync(HttpMethod.Get, "/app/custom-header/resource");

        Assert.Equal(Token, seen.Header("X-Api-Key"));
        Assert.Null(seen.Authorization);
    }

    [Fact]
    public async Task HeaderPlacement_WorksOnARequestWithABody()
    {
        // Every header-placement test above uses GET, and that hid a real failure: clearing a
        // caller-supplied header of the same name also probed HttpContent.Headers, which throws
        // "Misused header name" for a request-header name — and a GET has no content, so the
        // faulty call was never reached. Any POST through such a route died as a bare 502.
        // MCP traffic is entirely POST, so this was not a corner case for long.
        var seen = await SendAsync(
            HttpMethod.Post, "/app/echo/rpc",
            new StringContent("""{"jsonrpc":"2.0","id":1,"method":"ping"}""", Encoding.UTF8, "application/json"));

        Assert.Equal($"Bearer {Token}", seen.Authorization);
        Assert.Contains("ping", seen.Body);
    }

    [Fact]
    public async Task HeaderPlacement_WithNonBearerPrefix_UsesIt()
    {
        var seen = await SendAsync(HttpMethod.Get, "/app/prefixed-header/resource");

        Assert.Equal($"token {Token}", seen.Header("X-Auth"));
    }

    [Fact]
    public async Task HeaderPlacement_ReplacesACallerSuppliedHeaderOfTheSameName()
    {
        // Headers append rather than replace, so without an explicit removal the upstream would
        // receive both values and pick whichever it liked.
        var request = new HttpRequestMessage(HttpMethod.Get, "/app/custom-header/resource");
        request.Headers.Add(LocalAccessGuard.ApiKeyHeaderName, ApiKey);
        request.Headers.Add("X-Api-Key", "CALLER-SUPPLIED-KEY");

        await _client.SendAsync(request);

        var seen = Assert.Single(Received);
        Assert.Equal(Token, seen.Header("X-Api-Key"));
    }

    [Fact]
    public async Task QueryPlacement_AddsTheParameterAndKeepsTheCallersOwn()
    {
        var seen = await SendAsync(HttpMethod.Get, "/app/query/resource?page=2");

        Assert.Contains($"access_token={Token}", seen.Query);
        Assert.Contains("page=2", seen.Query);
        Assert.Null(seen.Authorization);
    }

    [Fact]
    public async Task QueryPlacement_OverwritesACallerSuppliedParameterOfTheSameName()
    {
        var seen = await SendAsync(HttpMethod.Get, "/app/query/resource?access_token=CALLER-SUPPLIED");

        Assert.DoesNotContain("CALLER-SUPPLIED", seen.Query);
        Assert.Contains($"access_token={Token}", seen.Query);
    }

    [Fact]
    public async Task BodyPlacement_AddsTheFieldToAJsonObjectAndKeepsTheRest()
    {
        var seen = await SendAsync(
            HttpMethod.Post, "/app/body/rpc",
            new StringContent("""{"jsonrpc":"2.0","method":"ping"}""", Encoding.UTF8, "application/json"));

        using var json = JsonDocument.Parse(seen.Body);
        Assert.Equal(Token, json.RootElement.GetProperty("access_token").GetString());
        Assert.Equal("ping", json.RootElement.GetProperty("method").GetString());

        // The rewritten body has to arrive with a matching length, or the upstream would either
        // block waiting for bytes that never come or truncate the JSON.
        Assert.Equal(seen.Body.Length.ToString(), seen.Header("Content-Length"));
        Assert.Null(seen.Authorization);
    }

    [Fact]
    public async Task BodyPlacement_OverwritesACallerSuppliedFieldOfTheSameName()
    {
        var seen = await SendAsync(
            HttpMethod.Post, "/app/body/rpc",
            new StringContent("""{"access_token":"CALLER-SUPPLIED"}""", Encoding.UTF8, "application/json"));

        using var json = JsonDocument.Parse(seen.Body);
        Assert.Equal(Token, json.RootElement.GetProperty("access_token").GetString());
    }

    [Fact]
    public async Task BodyPlacement_AddsTheFieldToAFormBody()
    {
        var seen = await SendAsync(
            HttpMethod.Post, "/app/body/form",
            new FormUrlEncodedContent([new KeyValuePair<string, string>("grant", "value")]));

        Assert.Contains("application/x-www-form-urlencoded", seen.ContentType);
        Assert.Contains("grant=value", seen.Body);
        Assert.Contains($"access_token={Token}", seen.Body);
    }

    [Fact]
    public async Task BodyPlacement_LeavesABodyItCannotParseUntouched()
    {
        // A half-rewritten body reaching an upstream is worse than an unauthenticated request:
        // this forwards the bytes exactly as sent and says so in the activity log.
        const string plain = "not a structured body";
        var seen = await SendAsync(
            HttpMethod.Post, "/app/body/raw", new StringContent(plain, Encoding.UTF8, "text/plain"));

        Assert.Equal(plain, seen.Body);
        Assert.DoesNotContain(Token, seen.Body);
        Assert.Null(seen.Authorization);
    }

    [Fact]
    public async Task RejectedRequest_NeverReachesTheUpstream()
    {
        var response = await _client.GetAsync("/app/echo/resource");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(Received);
    }

    [Fact]
    public async Task EncodedDotSegments_CannotClimbOutOfTheRoutePrefix()
    {
        // Sent over a raw socket on purpose: System.Uri decodes "%2e" to "." and removes the
        // dot segments client-side, so HttpClient physically cannot put this on the wire.
        // Anything speaking HTTP directly (curl --path-as-is, a socket) has no such difficulty.
        //
        // Kestrel percent-decodes and *then* removes dot segments, so this arrives at routing
        // as "/escaped", matches no route, and never reaches an upstream. Pinned as a test
        // because the whole confinement story rests on it: if that normalization order ever
        // changed, a caller could climb above an upstream's base path with the user's token
        // attached and nothing else in the pipeline would notice.
        var statusLine = await SendRawAsync("GET /app/echo/%2e%2e/%2e%2e/escaped HTTP/1.1");

        Assert.DoesNotContain("200", statusLine);
        Assert.Empty(Received);
    }

    [Fact]
    public async Task EncodedDotSegments_AreResolvedBeforeTheRequestIsForwarded()
    {
        // The other half of the same behavior, and the part that proves it is normalization
        // rather than a coincidental 404: a ".." that resolves back inside the prefix is
        // forwarded, with the upstream seeing the already-collapsed path.
        var statusLine = await SendRawAsync("GET /app/echo/sub/%2e%2e/resource HTTP/1.1");

        Assert.Contains("200", statusLine);
        Assert.Equal("/resource", Assert.Single(Received).Path);
    }

    [Fact]
    public async Task RawRequestWithoutDotSegments_StillWorks()
    {
        // Guards against the raw-socket helper above passing for the wrong reason.
        var statusLine = await SendRawAsync("GET /app/echo/resource HTTP/1.1");

        Assert.Contains("200", statusLine);
        Assert.Equal("/resource", Assert.Single(Received).Path);
    }

    /// <summary>Sends an authenticated request and returns what the upstream saw.</summary>
    private async Task<ReceivedRequest> SendAsync(HttpMethod method, string url, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, url) { Content = content };
        request.Headers.Add(LocalAccessGuard.ApiKeyHeaderName, ApiKey);

        var response = await _client.SendAsync(request);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode} / {LastForwarderError}");

        return Assert.Single(Received);
    }

    /// <summary>
    /// Sends a request line verbatim, with no client-side URI normalization, and returns the
    /// response's status line.
    /// </summary>
    private async Task<string> SendRawAsync(string requestLine)
    {
        var address = _client.BaseAddress!;

        using var tcp = new System.Net.Sockets.TcpClient();
        await tcp.ConnectAsync(address.Host, address.Port);

        await using var stream = tcp.GetStream();
        var raw = $"{requestLine}\r\n"
                  + $"Host: {address.Host}:{address.Port}\r\n"
                  + $"{LocalAccessGuard.ApiKeyHeaderName}: {ApiKey}\r\n"
                  + "Connection: close\r\n\r\n";
        await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(raw));

        using var reader = new StreamReader(stream, System.Text.Encoding.ASCII);
        return await reader.ReadLineAsync() ?? "";
    }

    [Fact]
    public async Task TwoDotsInsideASegment_AreStillForwarded()
    {
        // Only whole ".." segments are traversal. A file legitimately named "a..b" is not, and
        // rejecting it would break real upstream URLs.
        var request = new HttpRequestMessage(HttpMethod.Get, "/app/echo/files/a..b");
        request.Headers.Add(LocalAccessGuard.ApiKeyHeaderName, ApiKey);

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("/files/a..b", Assert.Single(Received).Path);
    }
}
