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
/// Routes carrying several credentials at once, and routes carrying none, end-to-end through
/// the real YARP forwarder against a real upstream.
///
/// One credential per route was the only shape this proxy ever forwarded, and the single-slot
/// assumption ran through the metadata, the transform, and the body rewriter alike. The
/// combinations here are the ones real upstreams actually ask for — two query parameters, a
/// header plus a project key in a second header, a header plus a body field — plus the two
/// degenerate ends: no credential at all, and the same credential in two different places.
///
/// Only the upstream's view proves any of it: query and body injection both edit state the
/// forwarder consumes later, and with several credentials in flight a rewrite that clobbers an
/// earlier one looks identical to a successful one from inside the transform.
/// </summary>
public class MultiCredentialForwardingTests : IAsyncLifetime
{
    private const string ApiKey = "multi-cred-test-key-0123456789";
    private const string TokenA = "TOKEN-ALPHA";
    private const string TokenB = "TOKEN-BRAVO";

    private readonly string _logPath = Path.Combine(Path.GetTempPath(), $"ravensport-multi-logs-{Guid.NewGuid()}");

    private WebApplication _upstream = null!;
    private WebApplication _proxy = null!;
    private HttpClient _client = null!;

    private sealed record Seen(string Path, string Query, Dictionary<string, string> Headers, string? ContentType, string Body)
    {
        public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;
        public string? Authorization => Header("Authorization");
    }

    private readonly List<Seen> _received = [];
    private string? _lastForwarderError;

    // Prefixes, one per combination under test.
    private const string TwoHeaders = "/c/two-headers";
    private const string ManyHeaders = "/c/many-headers";
    private const string BodyAndHeader = "/c/body-header";
    private const string TwoBodyFields = "/c/two-body-fields";
    private const string SameCredentialTwice = "/c/same-credential-twice";

    /// <summary>
    /// A route as an older build could have stored it, with a query-string placement among its
    /// credentials. Nothing may create one now; this is here to pin what happens to the ones that
    /// already exist.
    /// </summary>
    private const string LegacyQuery = "/c/legacy-query";
    private const string Everything = "/c/everything";
    private const string NoCredential = "/c/none";
    private const string OneGoodOneDeleted = "/c/one-deleted";

    public async Task InitializeAsync()
    {
        var upstreamBuilder = WebApplication.CreateBuilder();
        upstreamBuilder.WebHost.UseUrls("http://127.0.0.1:0");
        upstreamBuilder.Logging.ClearProviders();
        _upstream = upstreamBuilder.Build();
        _upstream.Run(async context =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync();

            lock (_received)
            {
                _received.Add(new Seen(
                    context.Request.Path,
                    context.Request.QueryString.Value ?? "",
                    context.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase),
                    context.Request.ContentType,
                    body));
            }

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
        proxyBuilder.Services.Replace(ServiceDescriptor.Singleton<IConfigVault>(_ => new InMemoryVault()));
        proxyBuilder.Services.Replace(ServiceDescriptor.Singleton(_ => new ActivityLog(_logPath)));

        _proxy = proxyBuilder.Build();

        _proxy.Use(async (context, next) =>
        {
            await next();
            if (context.Features.Get<Yarp.ReverseProxy.Forwarder.IForwarderErrorFeature>() is { } error)
            {
                _lastForwarderError = $"{error.Error}: {error.Exception?.Message}";
            }
        });

        _proxy.UseLocalAccessGuard();
        _proxy.MapReverseProxy();
        await _proxy.StartAsync();

        var cache = _proxy.Services.GetRequiredService<ConfigStoreCache>();
        var alpha = Credential("alpha", TokenA);
        var bravo = Credential("bravo", TokenB);
        var upstreamRecord = new UpstreamRecord { Name = "echo", BaseUrl = upstreamUrl };

        // Never added to the store: stands in for a credential the user deleted while a route
        // still referenced it.
        var deletedId = Guid.NewGuid();

        await cache.MutateAsync(store =>
        {
            store.Credentials.Add(alpha);
            store.Credentials.Add(bravo);
            store.Upstreams.Add(upstreamRecord);

            // One key value across every route here: these tests are about what reaches the
            // upstream, not about which key opens which route (that is LocalAccessGuardTests).
            void AddRoute(string prefix, params RouteCredential[] credentials) =>
                store.Routes.Add(new RouteMapping
                {
                    PathPrefix = prefix,
                    UpstreamId = upstreamRecord.Id,
                    StripPrefix = true,
                    Credentials = [.. credentials],
                    Key = new ProxyKey { Value = ApiKey },
                });

            AddRoute(TwoHeaders,
                Header(alpha.Id, "Authorization", "Bearer "),
                Header(bravo.Id, "X-Project-Key", ""));

            AddRoute(ManyHeaders,
                Header(alpha.Id, "Authorization", "Bearer "),
                Header(alpha.Id, "X-Alpha-Token", ""),
                Header(bravo.Id, "X-Api-Key", ""),
                Header(bravo.Id, "PRIVATE-TOKEN", "token "));

            AddRoute(BodyAndHeader,
                Body(bravo.Id, "auth_token"),
                Header(alpha.Id, "Authorization", "Bearer "));

            AddRoute(TwoBodyFields,
                Body(alpha.Id, "access_token"),
                Body(bravo.Id, "project_token"));

            // The same credential landing in two different slots.
            AddRoute(SameCredentialTwice,
                Header(alpha.Id, "Authorization", "Bearer "),
                Body(alpha.Id, "access_token"));

            AddRoute(Everything,
                Header(alpha.Id, "Authorization", "Bearer "),
                Header(bravo.Id, "X-Api-Key", ""),
                Body(alpha.Id, "auth_token"),
                Body(bravo.Id, "project_token"));

            // Three entries that would work and one that may not exist any more. The whole route
            // has to go, not just the offending entry - see TheWholeRouteGoesForOneQueryEntry.
            AddRoute(LegacyQuery,
                Header(alpha.Id, "Authorization", "Bearer "),
                Query(alpha.Id, "access_token"),
                Body(bravo.Id, "auth_token"));

            // No credentials at all — a plain forwarding hop.
            AddRoute(NoCredential);

            AddRoute(OneGoodOneDeleted,
                Header(alpha.Id, "Authorization", "Bearer "),
                Header(deletedId, "X-Gone", ""));
        });
        _proxy.Services.GetRequiredService<ProxyConfigChangeNotifier>().Rebuild();

        var proxyUrl = _proxy.Services
            .GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!
            .Addresses.First();

        _client = new HttpClient { BaseAddress = new Uri(proxyUrl) };
    }

    private static CredentialRecord Credential(string name, string token) => new()
    {
        Name = name,
        ClientId = "id",
        ClientSecret = "secret",
        Token = new TokenSet(token, "refresh", DateTimeOffset.UtcNow.AddHours(1), "Bearer", DateTimeOffset.UtcNow),
    };

    private static RouteCredential Header(Guid id, string name, string prefix) =>
        new() { CredentialId = id, Placement = CredentialPlacement.Header, ParameterName = name, ValuePrefix = prefix };

    private static RouteCredential Query(Guid id, string name) =>
        new() { CredentialId = id, Placement = CredentialPlacement.Query, ParameterName = name, ValuePrefix = "" };

    private static RouteCredential Body(Guid id, string name) =>
        new() { CredentialId = id, Placement = CredentialPlacement.Body, ParameterName = name, ValuePrefix = "" };

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _proxy.StopAsync();
        await _upstream.StopAsync();


        try { Directory.Delete(_logPath, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task TheWholeRouteGoesForOneQueryEntry()
    {
        // Fail closed, and closed for the whole route: the config builder will not serve a
        // credential set it cannot put on the wire as written. Attaching the two usable entries
        // and dropping the third would leave a route that looks configured, authenticates
        // differently from what the tab shows, and never says so.
        var request = new HttpRequestMessage(HttpMethod.Get, $"{LegacyQuery}/resource?page=2");
        request.Headers.Add(LocalAccessGuard.ApiKeyHeaderName, ApiKey);

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(_received);
    }

    [Fact]
    public async Task TwoHeaders_BothArrive()
    {
        var seen = await GetAsync($"{TwoHeaders}/resource");

        Assert.Equal($"Bearer {TokenA}", seen.Authorization);
        Assert.Equal(TokenB, seen.Header("X-Project-Key"));
    }

    [Fact]
    public async Task SeveralHeaders_AllArrive()
    {
        var seen = await GetAsync($"{ManyHeaders}/resource");

        Assert.Equal($"Bearer {TokenA}", seen.Authorization);
        Assert.Equal(TokenA, seen.Header("X-Alpha-Token"));
        Assert.Equal(TokenB, seen.Header("X-Api-Key"));
        Assert.Equal($"token {TokenB}", seen.Header("PRIVATE-TOKEN"));
    }

    [Fact]
    public async Task BodyPlusHeader_BothArrive()
    {
        var seen = await PostJsonAsync($"{BodyAndHeader}/rpc", """{"jsonrpc":"2.0","method":"ping"}""");

        Assert.Equal($"Bearer {TokenA}", seen.Authorization);
        Assert.DoesNotContain(TokenA, seen.Query);

        using var json = JsonDocument.Parse(seen.Body);
        Assert.Equal(TokenB, json.RootElement.GetProperty("auth_token").GetString());
        Assert.Equal("ping", json.RootElement.GetProperty("method").GetString());
    }

    [Fact]
    public async Task TwoBodyFields_BothArriveInOneRewrite()
    {
        // Injecting them one at a time would buffer and re-serialize the body twice, and would
        // leave the request half-authenticated if the second pass failed.
        var seen = await PostJsonAsync($"{TwoBodyFields}/rpc", """{"jsonrpc":"2.0","method":"ping"}""");

        using var json = JsonDocument.Parse(seen.Body);
        Assert.Equal(TokenA, json.RootElement.GetProperty("access_token").GetString());
        Assert.Equal(TokenB, json.RootElement.GetProperty("project_token").GetString());
        Assert.Equal("ping", json.RootElement.GetProperty("method").GetString());

        // A rewritten body must arrive with a matching length or the upstream truncates or hangs.
        Assert.Equal(seen.Body.Length.ToString(), seen.Header("Content-Length"));
    }

    [Fact]
    public async Task TwoBodyFields_BothArriveInAFormBody()
    {
        var seen = await PostAsync(
            $"{TwoBodyFields}/form",
            new FormUrlEncodedContent([new KeyValuePair<string, string>("grant", "value")]));

        Assert.Contains("application/x-www-form-urlencoded", seen.ContentType);
        Assert.Contains("grant=value", seen.Body);
        Assert.Contains($"access_token={TokenA}", seen.Body);
        Assert.Contains($"project_token={TokenB}", seen.Body);
    }

    [Fact]
    public async Task TheSameCredentialCanBeSentInTwoPlacesAtOnce()
    {
        var seen = await PostJsonAsync($"{SameCredentialTwice}/rpc", """{"method":"ping"}""");

        Assert.Equal($"Bearer {TokenA}", seen.Authorization);
        Assert.DoesNotContain(TokenA, seen.Query);

        using var json = JsonDocument.Parse(seen.Body);
        Assert.Equal(TokenA, json.RootElement.GetProperty("access_token").GetString());
    }

    [Fact]
    public async Task FourCredentialsAcrossBothPlacements_AllArrive()
    {
        var seen = await PostJsonAsync($"{Everything}/rpc", """{"method":"ping"}""");

        Assert.Equal($"Bearer {TokenA}", seen.Authorization);
        Assert.Equal(TokenB, seen.Header("X-Api-Key"));
        Assert.DoesNotContain(TokenA, seen.Query);
        Assert.DoesNotContain(TokenB, seen.Query);

        using var json = JsonDocument.Parse(seen.Body);
        Assert.Equal(TokenA, json.RootElement.GetProperty("auth_token").GetString());
        Assert.Equal(TokenB, json.RootElement.GetProperty("project_token").GetString());
        Assert.Equal("ping", json.RootElement.GetProperty("method").GetString());
    }

    [Fact]
    public async Task ARouteWithNoCredentials_StillForwards()
    {
        var seen = await GetAsync($"{NoCredential}/resource?page=2");

        Assert.Equal("/resource", seen.Path);
        Assert.Equal("?page=2", seen.Query);
        Assert.Null(seen.Authorization);
        Assert.DoesNotContain(TokenA, seen.Query);
        Assert.DoesNotContain(TokenB, seen.Query);
    }

    [Fact]
    public async Task ARouteWithNoCredentials_StillStripsTheCallersOwnAuthorizationAndCookies()
    {
        // Attaching nothing is not a licence to forward the caller's own credentials: a local
        // caller must never be able to use this proxy as a courier for a token or an ambient
        // browser session it should not have been able to reach.
        var request = new HttpRequestMessage(HttpMethod.Get, $"{NoCredential}/resource");
        request.Headers.Add(LocalAccessGuard.ApiKeyHeaderName, ApiKey);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "CALLER-SUPPLIED-TOKEN");
        request.Headers.Add("Cookie", "session=caller-session");

        var response = await _client.SendAsync(request);
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode} / {_lastForwarderError}");

        var seen = Single();
        Assert.Null(seen.Authorization);
        Assert.Null(seen.Header("Cookie"));
    }

    [Fact]
    public async Task ARouteWithNoCredentials_StillStripsTheLocalApiKey()
    {
        var seen = await GetAsync($"{NoCredential}/resource?{LocalAccessGuard.ApiKeyQueryName}={ApiKey}&page=2");

        Assert.DoesNotContain(ApiKey, seen.Query);
        Assert.DoesNotContain(LocalAccessGuard.ApiKeyQueryName, seen.Query);
        Assert.Contains("page=2", seen.Query);
    }

    [Fact]
    public async Task ARouteWithNoCredentials_IsStillGuardedByTheLocalApiKey()
    {
        var response = await _client.GetAsync($"{NoCredential}/resource");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(_received);
    }

    [Fact]
    public async Task ADeletedCredentialDoesNotStopTheOthersFromBeingAttached()
    {
        // One entry pointing at a credential that no longer exists must cost only that entry.
        // Dropping the whole route's authentication would turn a stale row in the UI into a
        // sudden wave of 401s from an upstream that was working a moment ago.
        var seen = await GetAsync($"{OneGoodOneDeleted}/resource");

        Assert.Equal($"Bearer {TokenA}", seen.Authorization);
        Assert.Null(seen.Header("X-Gone"));
    }

    [Fact]
    public async Task CallerSuppliedValuesAreReplacedInEverySlotAtOnce()
    {
        // Each placement replaces rather than appends, and that has to hold when several are
        // written on the same request — a leftover caller value alongside ours lets the upstream
        // pick whichever it likes.
        var request = new HttpRequestMessage(HttpMethod.Post, $"{Everything}/rpc")
        {
            Content = new StringContent(
                """{"auth_token":"CALLER-A","project_token":"CALLER-B"}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(LocalAccessGuard.ApiKeyHeaderName, ApiKey);
        request.Headers.Add("X-Api-Key", "CALLER-KEY");

        var response = await _client.SendAsync(request);
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode} / {_lastForwarderError}");

        var seen = Single();
        Assert.Equal(TokenB, seen.Header("X-Api-Key"));
        Assert.DoesNotContain("CALLER-", seen.Body);

        using var json = JsonDocument.Parse(seen.Body);
        Assert.Equal(TokenA, json.RootElement.GetProperty("auth_token").GetString());
        Assert.Equal(TokenB, json.RootElement.GetProperty("project_token").GetString());
    }

    [Fact]
    public async Task BodyCredentialsAreSkippedOnARequestWithNoBodyWithoutAffectingTheOthers()
    {
        // A GET has no body to put a field in, and inventing one would change the request's
        // meaning. The header entries on the same route must still arrive.
        var seen = await GetAsync($"{Everything}/resource");

        Assert.Equal($"Bearer {TokenA}", seen.Authorization);
        Assert.Equal(TokenB, seen.Header("X-Api-Key"));
        Assert.DoesNotContain("auth_token", seen.Body);
    }

    [Fact]
    public async Task BodyCredentialsAreSkippedOnAnUnparseableBodyWithoutAffectingTheOthers()
    {
        const string plain = "not a structured body";

        var seen = await PostAsync(
            $"{Everything}/raw", new StringContent(plain, Encoding.UTF8, "text/plain"));

        Assert.Equal(plain, seen.Body);
        Assert.Equal($"Bearer {TokenA}", seen.Authorization);
        Assert.Equal(TokenB, seen.Header("X-Api-Key"));
    }

    private async Task<Seen> GetAsync(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add(LocalAccessGuard.ApiKeyHeaderName, ApiKey);

        var response = await _client.SendAsync(request);
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode} / {_lastForwarderError}");

        return Single();
    }

    private Task<Seen> PostJsonAsync(string url, string body) =>
        PostAsync(url, new StringContent(body, Encoding.UTF8, "application/json"));

    private async Task<Seen> PostAsync(string url, HttpContent content)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        request.Headers.Add(LocalAccessGuard.ApiKeyHeaderName, ApiKey);

        var response = await _client.SendAsync(request);
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode} / {_lastForwarderError}");

        return Single();
    }

    private Seen Single()
    {
        lock (_received)
        {
            return Assert.Single(_received);
        }
    }
}
