using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using RavensPort.Core.Models;
using RavensPort.Core.Auth;
using RavensPort.Core.Proxy;
using RavensPort.Core.Tests.Mcp;
using Xunit.Abstractions;

namespace RavensPort.SystemTests;

/// <summary>
/// One ordered pass over the product, against a real 1Password vault, from empty to signed off.
///
/// Written as a single test rather than a dozen, because it is a sequence and not a set: the vault
/// is empty only before anything is added, mTLS can only be switched on after there is something to
/// protect, and the restart only means something once there is state that had to survive it. xUnit
/// does not order tests, so splitting this would either need shared mutable state between them or
/// would silently stop testing the thing the order exists to test.
///
/// Assertions are direct rather than snapshots. Golden files are the better tool when the output is
/// large, stable and hard to predict; here it is small, and full of ephemeral ports, fresh GUIDs and
/// timestamps that would have to be scrubbed before every comparison.
///
/// The credential matrix and the mTLS refusals overlap MultiCredentialForwardingTests and
/// McpFunnelMtlsTests deliberately. Those prove the transform and the listener are right; this
/// proves they are still right when every record came out of a real 1Password vault and survived a
/// restart, which is the one thing no other suite can show.
///
/// What this does not cover, said plainly: the installer and the GUI. Both are unreachable
/// unattended -- see SystemTestHost for why a service-account token cannot be handed to an installed
/// RavensPort.exe without a human.
/// </summary>
public sealed class SystemApprovalTests(ITestOutputHelper output) : IAsyncLifetime
{
    // Fixtures, not credentials.
    private const string OAuthToken = "MOCK-OAUTH-ACCESS-TOKEN";  // gitleaks:allow
    private const string ProjectKey = "MOCK-PROJECT-KEY";         // gitleaks:allow

    private WebApplication _routeUpstream = null!;
    private readonly List<FakeMcpServer> _mcpServers = [];

    private readonly string _certDirectory =
        Path.Combine(Path.GetTempPath(), $"ravensport-approval-{Guid.NewGuid():n}");

    /// <summary>What the upstream saw. The only way to prove a credential reached the wire.</summary>
    private sealed record Seen(Dictionary<string, string> Headers, string Body)
    {
        public string? Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;
    }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_certDirectory);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        _routeUpstream = builder.Build();
        _routeUpstream.Run(async context =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync();

            var headers = context.Request.Headers
                .ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);

            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(JsonSerializer.Serialize(new { headers, body }));
        });
        await _routeUpstream.StartAsync();
    }

    public async Task DisposeAsync()
    {
        foreach (var server in _mcpServers) await server.DisposeAsync();
        await _routeUpstream.StopAsync();
        await _routeUpstream.DisposeAsync();
        try { Directory.Delete(_certDirectory, recursive: true); } catch { /* best effort */ }
    }

    private string RouteUpstreamUrl => _routeUpstream.Services
        .GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();

    private void Stage(string text) => output.WriteLine($"\n=== {text} ===");

    [RequiresServiceAccountFact]
    public async Task TheWholeProductFromAnEmptyVaultToMtlsAndBack()
    {
        var token = SystemTestEnvironment.Token!;

        // Purged before the store is ever read. An item the vault will not hand back -- archived,
        // or half-deleted by a sweep that hit the write rate limit -- fails the load, and a host
        // that cannot start cannot run the sweep that would have fixed it.
        await using var host = await SystemTestHost.StartAsync(token, purgeBeforeLoading: true);

        // ---- 1. the vault starts empty -------------------------------------------------------
        Stage("1. empty vault at startup");

        var purged = await host.PurgeVaultAsync();
        await ClearVaultAsync(host);

        Assert.Empty(host.Cache.Current.Credentials);
        Assert.Empty(host.Cache.Current.Routes);
        Assert.Empty(host.Cache.Current.McpFunnels);
        Assert.Empty(host.Cache.Current.McpSources);
        output.WriteLine($"deleted {purged} pre-existing vault item(s); store holds nothing");

        // ---- 2. seeding ------------------------------------------------------------------------
        Stage("2. seeding credentials, mock MCP servers, routes and two funnels");

        var alpha = await StartMcpServerAsync();
        var beta = await StartMcpServerAsync();

        // An OAuth grant and a static project key: the two kinds a route can carry, and the pair
        // plenty of APIs demand together.
        var oauth = new CredentialRecord
        {
            Name = "mock oauth2",
            ClientId = "id",
            ClientSecret = "secret",
            Token = new TokenSet(OAuthToken, "refresh", DateTimeOffset.UtcNow.AddHours(1), "Bearer", DateTimeOffset.UtcNow),
        };

        var apiKey = new CredentialRecord
        {
            Name = "mock project key",
            Kind = CredentialKind.ApiKey,
            ApiKey = ProjectKey,
            DefaultPlacement = CredentialPlacement.Header,
            DefaultParameterName = "X-Api-Key",
            DefaultValuePrefix = "",
        };

        var upstream = new UpstreamRecord { Name = "mock-upstream", BaseUrl = RouteUpstreamUrl };

        // A real OAuth2 exchange, against a mock authorization server. Client credentials is the
        // only grant that can run here: the browser flow needs a person at a consent screen and the
        // device flow needs one at a second device, while this one is the app signing in as itself.
        var clientCredentials = new CredentialRecord
        {
            Name = "mock client credentials",
            Kind = CredentialKind.ClientCredentials,
            ClientId = "ravensport-approval",
            ClientSecret = "mock-client-secret", // gitleaks:allow
            TokenEndpoint = SystemTestEnvironment.OAuthTokenEndpoint,
        };

        var outcome = await host.Service<ClientCredentialsService>().AcquireAsync(clientCredentials);

        Assert.True(outcome.Success,
            $"the token endpoint {SystemTestEnvironment.OAuthTokenEndpoint} refused the grant: "
            + $"{outcome.Error} {outcome.ErrorDescription}");

        var issuedToken = clientCredentials.Token?.AccessToken;
        Assert.False(string.IsNullOrWhiteSpace(issuedToken));
        output.WriteLine(
            $"client credentials grant -> token of {issuedToken!.Length} chars from "
            + SystemTestEnvironment.OAuthTokenEndpoint);

        // An MCP server reached the way a real one is: through one of this proxy's own routes, so
        // the route's credential transform attaches the token on the way out. This is the only
        // arrangement that shows an OAuth token reaching an MCP server rather than just an
        // ordinary upstream.
        var secured = await StartMcpServerAsync();
        var securedUpstream = new UpstreamRecord { Name = "mcp-secured", BaseUrl = secured.Url };

        var securedRoute = new RouteMapping
        {
            PathPrefix = "/mcpsecured",
            UpstreamId = securedUpstream.Id,
            StripPrefix = true,
            Key = new ProxyKey { Value = SystemTestHost.ApiKey },
            Credentials =
            [
                new RouteCredential
                {
                    CredentialId = clientCredentials.Id,
                    Placement = CredentialPlacement.Header,
                    ParameterName = "Authorization",
                    ValuePrefix = "Bearer ",
                },
            ],
        };

        var securedSource = new McpSourceRecord
        {
            Name = "secured", Alias = "secured", Kind = McpSourceKind.ProxyRoute,
            RouteId = securedRoute.Id, Transport = McpTransportPreference.StreamableHttp,
        };

        var sourceAlpha = new McpSourceRecord
        {
            Name = "alpha", Alias = "alpha", Kind = McpSourceKind.RemoteUrl,
            Url = alpha.Url, Transport = McpTransportPreference.StreamableHttp,
        };
        var sourceBeta = new McpSourceRecord
        {
            Name = "beta", Alias = "beta", Kind = McpSourceKind.RemoteUrl,
            Url = beta.Url, Transport = McpTransportPreference.StreamableHttp,
        };

        await host.Cache.MutateAsync(store =>
        {
            store.Settings.McpFunnelEnabled = true;
            store.Credentials.Add(oauth);
            store.Credentials.Add(apiKey);
            store.Credentials.Add(clientCredentials);
            store.Upstreams.Add(upstream);
            store.Upstreams.Add(securedUpstream);
            store.Routes.Add(securedRoute);
            store.McpSources.Add(sourceAlpha);
            store.McpSources.Add(sourceBeta);
            store.McpSources.Add(securedSource);

            foreach (var route in BuildRouteMatrix(upstream.Id, oauth.Id, apiKey.Id))
            {
                store.Routes.Add(route);
            }

            store.McpFunnels.Add(new McpFunnelRecord
            {
                Name = "both", Slug = "both",
                Key = new ProxyKey { Value = SystemTestHost.ApiKey },
                Sources =
                [
                    new McpFunnelSource { SourceId = sourceAlpha.Id },
                    new McpFunnelSource { SourceId = sourceBeta.Id },
                ],
            });
            store.McpFunnels.Add(new McpFunnelRecord
            {
                Name = "solo", Slug = "solo",
                Key = new ProxyKey { Value = SystemTestHost.ApiKey },
                Sources = [new McpFunnelSource { SourceId = sourceAlpha.Id }],
            });
            store.McpFunnels.Add(new McpFunnelRecord
            {
                Name = "oauth", Slug = "oauth",
                Key = new ProxyKey { Value = SystemTestHost.ApiKey },
                Sources = [new McpFunnelSource { SourceId = securedSource.Id }],
            });
        });

        host.RebuildProxyConfig();
        output.WriteLine("seeded: 3 credentials, 8 routes, 3 MCP sources, 3 funnels");

        // ---- 3. everything works over plain HTTP ---------------------------------------------
        Stage("3. credential matrix and funnels over http");

        await AssertCredentialMatrixAsync(host, "before mTLS");
        await AssertFunnelsWorkAsync(host, "before mTLS");
        await AssertOAuthTokenReachesTheMcpServerAsync(host, secured, issuedToken, "before mTLS");

        // ---- 4. mTLS on, certificate exported --------------------------------------------------
        Stage("4. enabling mTLS and exporting the certificate");

        var pfx = MtlsCertificateFactory.GenerateClientCertificatePfx(SystemTestHost.PfxPassword);

        await host.Cache.MutateAsync(store =>
        {
            store.Settings.MtlsEnabled = true;
            store.Settings.MtlsClientCertificatePfx = pfx;
            store.Settings.MtlsClientCertificatePassword = SystemTestHost.PfxPassword;
        });

        var pfxPath = Path.Combine(_certDirectory, "RavensPort_ClientCert.pfx");
        await File.WriteAllBytesAsync(pfxPath, Convert.FromBase64String(pfx));

        Assert.True(new FileInfo(pfxPath).Length > 0);
        output.WriteLine($"certificate stored in the vault and written to {pfxPath}");

        // ---- 5. restart -------------------------------------------------------------------------
        Stage("5. restarting; everything below came back out of 1Password");

        await host.RestartAsync(mtls: true);

        Assert.True(host.IsMtls);
        Assert.StartsWith("https://", host.BaseUrl, StringComparison.Ordinal);

        var reloaded = host.Cache.Current;
        Assert.Equal(8, reloaded.Routes.Count);
        Assert.Equal(3, reloaded.McpFunnels.Count);
        Assert.Equal(3, reloaded.McpSources.Count);
        Assert.Equal(3, reloaded.Credentials.Count);

        // The token survived too, which is what lets the OAuth check below run against a host that
        // never performed the exchange.
        var restoredToken = reloaded.Credentials
            .Single(c => c.Kind == CredentialKind.ClientCredentials).Token?.AccessToken;
        Assert.Equal(issuedToken, restoredToken);
        Assert.True(reloaded.Settings.MtlsEnabled);
        output.WriteLine($"listener is {host.BaseUrl}, config survived the restart");

        // ---- 6. everything works again, now over mTLS -------------------------------------------
        Stage("6. credential matrix and funnels over https with the client certificate");

        await AssertCredentialMatrixAsync(host, "after restart, over mTLS");
        await AssertFunnelsWorkAsync(host, "after restart, over mTLS");

        // The same token, after the restart. It came back out of 1Password rather than out of the
        // exchange, which is the half a token acquisition on its own never shows.
        await AssertOAuthTokenReachesTheMcpServerAsync(host, secured, issuedToken, "after restart, over mTLS");

        // ---- 7. the listener actually refuses the wrong caller ----------------------------------
        Stage("7. mTLS refusals");

        await AssertMtlsRefusalsAsync(host, pfxPath);

        // ---- 8. leave the vault empty -----------------------------------------------------------
        Stage("8. clearing the vault");

        await ClearVaultAsync(host);
        var swept = await host.PurgeVaultAsync(tolerateFailures: true);
        output.WriteLine($"store cleared and {swept} vault item(s) deleted; approval run complete");
    }

    // ---- the credential matrix -----------------------------------------------------------------

    /// <summary>
    /// Every shape a route can attach, one route each. The upstream echoes what it received, so
    /// each row is checked on what actually reached the wire rather than on a status code.
    ///
    /// No query placement, and that is not an omission: CredentialPlacement.Query is no longer
    /// permitted, because a secret in a URL is written to the upstream's access log, every
    /// intermediary's, and browser history.
    /// </summary>
    private static IEnumerable<RouteMapping> BuildRouteMatrix(Guid upstreamId, Guid oauthId, Guid keyId)
    {
        RouteMapping Route(string prefix, params RouteCredential[] credentials) => new()
        {
            PathPrefix = prefix,
            UpstreamId = upstreamId,
            StripPrefix = true,
            Key = new ProxyKey { Value = SystemTestHost.ApiKey },
            Credentials = [.. credentials],
        };

        RouteCredential Header(Guid id, string name, string prefix) =>
            new() { CredentialId = id, Placement = CredentialPlacement.Header, ParameterName = name, ValuePrefix = prefix };

        RouteCredential Body(Guid id, string field) =>
            new() { CredentialId = id, Placement = CredentialPlacement.Body, ParameterName = field, ValuePrefix = "" };

        // Nothing: a plain forwarding hop to an upstream that needs no token.
        yield return Route("/app/none");

        // One credential: the usual case.
        yield return Route("/app/one", Header(oauthId, "Authorization", "Bearer "));

        // Two headers.
        yield return Route("/app/two-headers",
            Header(oauthId, "Authorization", "Bearer "),
            Header(keyId, "X-Project-Key", ""));

        // Several headers, one of them with a prefix that is not "Bearer ".
        yield return Route("/app/several-headers",
            Header(oauthId, "Authorization", "Bearer "),
            Header(keyId, "X-Api-Key", ""),
            Header(keyId, "PRIVATE-TOKEN", "token "));

        // Header plus body.
        yield return Route("/app/header-body",
            Header(oauthId, "Authorization", "Bearer "),
            Body(keyId, "auth_token"));

        // Two body fields, which have to arrive in one rewrite rather than two.
        yield return Route("/app/two-body",
            Body(oauthId, "access_token"),
            Body(keyId, "project_token"));

        // An OAuth user grant plus a project key, which plenty of APIs demand together.
        yield return Route("/app/oauth-plus-key",
            Header(oauthId, "Authorization", "Bearer "),
            Header(keyId, "X-Api-Key", ""));
    }

    private async Task AssertCredentialMatrixAsync(SystemTestHost host, string phase)
    {
        // Nothing attached: the hop still forwards, and the caller's own Authorization is stripped
        // rather than passed through -- a route with no credential must not become a way to smuggle
        // one to the upstream.
        var none = await PostAsync(host, "/app/none");
        Assert.Null(none.Header("Authorization"));

        var one = await PostAsync(host, "/app/one");
        Assert.Equal($"Bearer {OAuthToken}", one.Header("Authorization"));

        var two = await PostAsync(host, "/app/two-headers");
        Assert.Equal($"Bearer {OAuthToken}", two.Header("Authorization"));
        Assert.Equal(ProjectKey, two.Header("X-Project-Key"));

        var several = await PostAsync(host, "/app/several-headers");
        Assert.Equal($"Bearer {OAuthToken}", several.Header("Authorization"));
        Assert.Equal(ProjectKey, several.Header("X-Api-Key"));
        Assert.Equal($"token {ProjectKey}", several.Header("PRIVATE-TOKEN"));

        var headerBody = await PostAsync(host, "/app/header-body");
        Assert.Equal($"Bearer {OAuthToken}", headerBody.Header("Authorization"));
        Assert.Equal(ProjectKey, FieldOf(headerBody.Body, "auth_token"));

        // Both fields in one rewrite: two separate rewrites would each start from the original body
        // and the second would drop the first.
        var twoBody = await PostAsync(host, "/app/two-body");
        Assert.Equal(OAuthToken, FieldOf(twoBody.Body, "access_token"));
        Assert.Equal(ProjectKey, FieldOf(twoBody.Body, "project_token"));

        var both = await PostAsync(host, "/app/oauth-plus-key");
        Assert.Equal($"Bearer {OAuthToken}", both.Header("Authorization"));
        Assert.Equal(ProjectKey, both.Header("X-Api-Key"));

        output.WriteLine($"credential matrix: 7 routes, every placement arrived as configured ({phase})");
    }

    /// <summary>
    /// POSTs a small JSON object, because body placements need something to rewrite.
    ///
    /// The caller sends an Authorization of its own every time. It must never survive: the
    /// transform replaces it where a credential is configured and strips it where none is.
    /// </summary>
    private static async Task<Seen> PostAsync(SystemTestHost host, string path)
    {
        using var client = host.CreateHttpClient();
        client.DefaultRequestHeaders.Add(LocalAccessGuard.ApiKeyHeaderName, SystemTestHost.ApiKey);
        client.DefaultRequestHeaders.Add("Authorization", "Bearer CALLER-SUPPLIED-VALUE");

        var response = await client.PostAsJsonAsync(path + "/anything", new { hello = "world" });
        var payload = await response.Content.ReadAsStringAsync();

        Assert.True(response.IsSuccessStatusCode, $"{path} returned {(int)response.StatusCode}: {payload}");

        using var doc = JsonDocument.Parse(payload);
        var headers = doc.RootElement.GetProperty("headers").EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.GetString() ?? "", StringComparer.OrdinalIgnoreCase);

        return new Seen(headers, doc.RootElement.GetProperty("body").GetString() ?? "");
    }

    private static string? FieldOf(string json, string field)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty(field, out var v) ? v.GetString() : null;
    }

    // ---- mTLS refusals -------------------------------------------------------------------------

    /// <summary>
    /// The listener refusing the wrong caller, which is the only thing that shows mTLS is enforced
    /// rather than merely switched on. A run where every client is correct cannot tell a listener
    /// that demands a certificate from one that ignores it.
    ///
    /// Failures are asserted by kind, not by message. A Node client sees
    /// DEPTH_ZERO_SELF_SIGNED_CERT, ERR_SSL_SSLV3_ALERT_CERTIFICATE_UNKNOWN and "mac verify
    /// failure" -- those are OpenSSL's strings surfaced by Node, and .NET's stack words the same
    /// three refusals differently. Pinning them would pin the client library rather than this
    /// product, so what is asserted is that each wrong caller is refused, and the actual message is
    /// written to the log where it can be read.
    /// </summary>
    private async Task AssertMtlsRefusalsAsync(SystemTestHost host, string pfxPath)
    {
        // As configured: accepted.
        using (var good = host.CreateHttpClient())
        {
            good.DefaultRequestHeaders.Add(LocalAccessGuard.ApiKeyHeaderName, SystemTestHost.ApiKey);
            var ok = await good.GetAsync("/app/none/anything");
            Assert.True(ok.IsSuccessStatusCode);
            output.WriteLine($"correct client                 -> {(int)ok.StatusCode}");
        }

        // No client certificate. The listener demands one on every connection, so this dies in the
        // handshake -- a transport failure, not a 403, because no request is ever sent.
        using (var noCert = host.CreateHttpClient(presentClientCertificate: false))
        {
            noCert.DefaultRequestHeaders.Add(LocalAccessGuard.ApiKeyHeaderName, SystemTestHost.ApiKey);
            var raised = await Assert.ThrowsAnyAsync<HttpRequestException>(
                () => noCert.GetAsync("/app/none/anything"));
            output.WriteLine($"no client certificate          -> refused: {Innermost(raised)}");
        }

        // Not trusting the listener: the same handshake from the other side. A self-signed
        // certificate cannot satisfy default chain validation, so the caller refuses the server.
        using (var noTrust = host.CreateHttpClient(trustTheListener: false))
        {
            noTrust.DefaultRequestHeaders.Add(LocalAccessGuard.ApiKeyHeaderName, SystemTestHost.ApiKey);
            var raised = await Assert.ThrowsAnyAsync<HttpRequestException>(
                () => noTrust.GetAsync("/app/none/anything"));
            output.WriteLine($"listener not trusted           -> refused: {Innermost(raised)}");
        }

        // A wrong passphrase never reaches a socket at all: opening the PFX fails first, which is
        // why a client with a bad password reports something about the file rather than about TLS.
        var wrongPassword = Assert.ThrowsAny<CryptographicException>(
            () => X509CertificateLoader.LoadPkcs12FromFile(pfxPath, "not-the-password"));
        output.WriteLine($"wrong PFX passphrase           -> refused before connecting: {wrongPassword.GetType().Name}");

        // And the right one still opens it, so the failure above was the password and not the file.
        using var opened = X509CertificateLoader.LoadPkcs12FromFile(pfxPath, SystemTestHost.PfxPassword);
        Assert.False(string.IsNullOrEmpty(opened.Thumbprint));
    }

    private static string Innermost(Exception ex)
    {
        while (ex.InnerException is { } inner) ex = inner;
        return $"{ex.GetType().Name}: {ex.Message}";
    }

    // ---- the OAuth token, all the way to an MCP server -------------------------------------------

    /// <summary>
    /// The token the authorization server issued, arriving at an MCP server.
    ///
    /// This is the arrangement the product exists for and the one nothing else here covers. The
    /// funnel's source is a ProxyRoute, so reaching it means dialling one of this proxy's own routes
    /// through the loopback listener, which puts the route's credential transform in the path. The
    /// fake records the Authorization of every request it is sent, so what is asserted is the header
    /// that actually arrived rather than the configuration that should have produced it.
    ///
    /// Run before mTLS and again after the restart. The second time the host never performed an
    /// exchange -- the token came back out of 1Password -- which is the half a token acquisition on
    /// its own can never show.
    /// </summary>
    private async Task AssertOAuthTokenReachesTheMcpServerAsync(
        SystemTestHost host, FakeMcpServer secured, string expectedToken, string phase)
    {
        secured.ReceivedAuthorization.Clear();
        secured.ReceivedHeaders.Clear();

        var client = await host.ConnectMcpAsync("oauth");

        var tools = (await client.ListToolsAsync()).Select(t => t.Name).ToList();
        Assert.Contains("secured__echo", tools);

        var call = await client.CallToolAsync(
            "secured__echo",
            new Dictionary<string, object?> { ["value"] = "through oauth" }!);
        Assert.Equal("through oauth", call.Content.OfType<TextContentBlock>().First().Text);

        var authorizations = secured.ReceivedAuthorization
            .Where(a => !string.IsNullOrEmpty(a))
            .Distinct()
            .ToList();

        Assert.NotEmpty(authorizations);

        // Every request, not merely one of them. A funnel makes several -- discovery, the list, the
        // call -- and a transform that attached the token to only some would still satisfy a
        // "contains" check while leaving real calls unauthenticated.
        Assert.All(authorizations, a => Assert.Equal($"Bearer {expectedToken}", a));

        // And this proxy's own key is not among what it forwarded. It authenticates a caller to
        // RavensPort and has no business at the far end, where the upstream would log it.
        Assert.All(
            secured.ReceivedHeaders,
            headers => Assert.False(headers.ContainsKey(LocalAccessGuard.ApiKeyHeaderName)));

        output.WriteLine(
            $"oauth funnel -> MCP server saw 'Bearer <issued token>' on all "
            + $"{authorizations.Count} distinct authorization(s), proxy key stripped ({phase})");
    }

    // ---- funnels --------------------------------------------------------------------------------

    private async Task AssertFunnelsWorkAsync(SystemTestHost host, string phase)
    {
        foreach (var version in new string?[] { null, "2025-11-25" })
        {
            var label = version ?? "current";

            var both = await host.ConnectMcpAsync("both", version);
            if (version is not null) Assert.Equal(version, both.NegotiatedProtocolVersion);

            var tools = (await both.ListToolsAsync()).Select(t => t.Name).ToList();
            Assert.Contains("alpha__echo", tools);
            Assert.Contains("beta__echo", tools);

            var call = await both.CallToolAsync(
                "alpha__echo",
                new Dictionary<string, object?> { ["value"] = "hello" }!);
            Assert.Equal("hello", call.Content.OfType<TextContentBlock>().First().Text);

            var solo = await host.ConnectMcpAsync("solo", version);
            var soloTools = (await solo.ListToolsAsync()).Select(t => t.Name).ToList();
            Assert.Contains("alpha__echo", soloTools);
            Assert.DoesNotContain(soloTools, n => n.StartsWith("beta__", StringComparison.Ordinal));

            output.WriteLine(
                $"funnel 'both' [{label}] -> {tools.Count} tools, call ok; "
                + $"funnel 'solo' [{label}] -> {soloTools.Count} tools, isolated ({phase})");
        }
    }

    // ---- helpers ---------------------------------------------------------------------------------

    private async Task<FakeMcpServer> StartMcpServerAsync()
    {
        var server = await FakeMcpServer.StartAsync();
        _mcpServers.Add(server);
        return server;
    }

    private static Task ClearVaultAsync(SystemTestHost host) => host.Cache.MutateAsync(store =>
    {
        store.McpFunnels.Clear();
        store.McpSources.Clear();
        store.Routes.Clear();
        store.Upstreams.Clear();
        store.Credentials.Clear();
        store.Settings.MtlsEnabled = false;
        store.Settings.MtlsClientCertificatePfx = "";
        store.Settings.MtlsClientCertificatePassword = "";
    });
}
