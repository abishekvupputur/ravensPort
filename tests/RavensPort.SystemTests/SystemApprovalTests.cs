using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using RavensPort.Core.Auth;
using RavensPort.Core.Models;
using RavensPort.Core.Proxy;
using RavensPort.Core.Tests.Mcp;
using Xunit.Abstractions;

namespace RavensPort.SystemTests;

/// <summary>
/// One ordered pass over the product, against a real 1Password vault, from an empty vault to mTLS
/// and the refusals that prove it.
///
/// Eleven stages, each its own test, run in the order they are numbered. This was a single method
/// once. It ran the same sequence and reported "Total tests: 1" -- one pass or fail that said
/// nothing about what had been exercised, and on a failure nothing about how far it had got. Now
/// each stage names what it proves, and a reader of the CI log gets that list whether or not
/// anything failed.
///
/// The order is real rather than incidental, which is why the stages share a fixture instead of
/// standing alone: the vault is empty only before anything is added, mTLS can be switched on only
/// once there is something to protect, and the restart proves nothing until there is state that had
/// to survive it. When one stage fails the rest do not pile on -- see <see cref="ApprovalRun.Abort"/>.
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
[TestCaseOrderer("RavensPort.SystemTests.StageOrderer", "RavensPort.SystemTests")]
public sealed class SystemApprovalTests(ApprovalRun run, ITestOutputHelper output)
    : IClassFixture<ApprovalRun>
{
    /// <summary>What the upstream saw. The only way to prove a credential reached the wire.</summary>
    private sealed record Seen(Dictionary<string, string> Headers, string Body)
    {
        public string? Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;
    }

    /// <summary>
    /// Runs one stage, recording the first failure so that the stages after it report the
    /// prerequisite they are waiting on instead of failing on its wreckage.
    /// </summary>
    private async Task StageAsync(string name, Func<Task> body)
    {
        run.RequireEarlierStagesPassed();

        try
        {
            await body();
        }
        catch (Exception ex)
        {
            run.Abort(name, ex);
            throw;
        }
    }

    // ---- 1. an empty vault ---------------------------------------------------------------------

    [RequiresServiceAccountFact]
    public Task Stage01_TheVaultStartsEmpty() => StageAsync(nameof(Stage01_TheVaultStartsEmpty), async () =>
    {
        var accounts = SystemTestEnvironment.Accounts;

        // Whichever vault has gone longest without being written. With one account this is a no-op;
        // with two it halves the write rate each account sees, which is what keeps consecutive runs
        // off 1Password's per-account limit.
        output.WriteLine($"choosing between {accounts.Count} configured account(s)");
        run.Account = await SystemTestHost.ChooseLeastRecentlyUsedAsync(accounts, output.WriteLine);
        output.WriteLine($"using {run.Account.Label}, vault '{run.Account.VaultName}'");

        // Purged before the store is ever read. An item the vault will not hand back -- archived, or
        // half-deleted by a sweep that hit the write rate limit -- fails the load, and a host that
        // cannot start cannot run the sweep that would have fixed it.
        //
        // This sweep is also the only one. The run used to empty the vault again at the end, which
        // wrote a cleared store and then deleted every item a second time -- work this opening sweep
        // does anyway. A service account gets 100 writes an hour and a full pass spends a good share
        // of them, so the cleanup nobody is waiting on is the one to drop.
        run.Host = await SystemTestHost.StartAsync(run.Account, purgeBeforeLoading: true);

        // Nothing swept or cleared here, and both omissions are deliberate.
        //
        // A second PurgeVaultAsync would re-list the vault to delete the nothing the first sweep
        // left. A ClearStoreAsync would then mutate an already-empty store, and a mutation is a
        // write whether or not it changes anything -- so it spent one of the hundred writes an hour
        // this account gets to assert something the sweep had already made true.
        //
        // What makes the store empty is the sweep, which deletes every item and then rewrites the
        // note to index nothing. The load that follows has no ids to chase, so the assertions below
        // are reading the result of that rather than of a second pass.
        var purged = run.Host.PurgedAtStartup;

        Assert.Empty(run.Host.Cache.Current.Credentials);
        Assert.Empty(run.Host.Cache.Current.Routes);
        Assert.Empty(run.Host.Cache.Current.McpFunnels);
        Assert.Empty(run.Host.Cache.Current.McpSources);

        output.WriteLine($"deleted {purged} item(s) left by the previous run; the store holds nothing");
    });

    // ---- 2. seeding, including a real OAuth2 grant ----------------------------------------------

    [RequiresServiceAccountFact]
    public Task Stage02_CredentialsRoutesAndFunnelsAreSeeded() =>
        StageAsync(nameof(Stage02_CredentialsRoutesAndFunnelsAreSeeded), async () =>
    {
        var host = run.RequireHost();

        var alpha = await run.StartMcpServerAsync();
        var beta = await run.StartMcpServerAsync();

        // An OAuth grant and a static project key: the two kinds a route can carry, and the pair
        // plenty of APIs demand together.
        var oauth = new CredentialRecord
        {
            Name = "mock oauth2",
            ClientId = "id",
            ClientSecret = "secret",
            Token = new TokenSet(ApprovalRun.OAuthToken, "refresh", DateTimeOffset.UtcNow.AddHours(1), "Bearer", DateTimeOffset.UtcNow),
        };

        var apiKey = new CredentialRecord
        {
            Name = "mock project key",
            Kind = CredentialKind.ApiKey,
            ApiKey = ApprovalRun.ProjectKey,
            DefaultPlacement = CredentialPlacement.Header,
            DefaultParameterName = "X-Api-Key",
            DefaultValuePrefix = "",
        };

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

        run.IssuedToken = clientCredentials.Token?.AccessToken;
        Assert.False(string.IsNullOrWhiteSpace(run.IssuedToken));
        output.WriteLine(
            $"client credentials grant -> token of {run.IssuedToken!.Length} chars from "
            + SystemTestEnvironment.OAuthTokenEndpoint);

        // An MCP server reached the way a real one is: through one of this proxy's own routes, so
        // the route's credential transform attaches the token on the way out. This is the only
        // arrangement that shows an OAuth token reaching an MCP server rather than just an ordinary
        // upstream.
        run.SecuredMcpServer = await run.StartMcpServerAsync();

        var upstream = new UpstreamRecord { Name = "mock-upstream", BaseUrl = run.RouteUpstreamUrl };
        var securedUpstream = new UpstreamRecord { Name = "mcp-secured", BaseUrl = run.SecuredMcpServer.Url };

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
        var securedSource = new McpSourceRecord
        {
            Name = "secured", Alias = "secured", Kind = McpSourceKind.ProxyRoute,
            RouteId = securedRoute.Id, Transport = McpTransportPreference.StreamableHttp,
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
    });

    // ---- 3-5. everything works over plain HTTP --------------------------------------------------

    [RequiresServiceAccountFact]
    public Task Stage03_EveryCredentialPlacementReachesTheUpstream() =>
        StageAsync(nameof(Stage03_EveryCredentialPlacementReachesTheUpstream),
            () => AssertCredentialMatrixAsync(run.RequireHost()));

    [RequiresServiceAccountFact]
    public Task Stage04_BothFunnelsAnswerOnBothProtocolRevisions() =>
        StageAsync(nameof(Stage04_BothFunnelsAnswerOnBothProtocolRevisions),
            () => AssertFunnelsWorkAsync(run.RequireHost()));

    [RequiresServiceAccountFact]
    public Task Stage05_TheIssuedOAuthTokenReachesTheMcpServer() =>
        StageAsync(nameof(Stage05_TheIssuedOAuthTokenReachesTheMcpServer),
            () => AssertOAuthTokenReachesTheMcpServerAsync(run.RequireHost()));

    // ---- 6. mTLS on, certificate exported -------------------------------------------------------

    [RequiresServiceAccountFact]
    public Task Stage06_MtlsIsEnabledAndTheCertificateExported() =>
        StageAsync(nameof(Stage06_MtlsIsEnabledAndTheCertificateExported), async () =>
    {
        var host = run.RequireHost();
        var pfx = MtlsCertificateFactory.GenerateClientCertificatePfx(SystemTestHost.PfxPassword);

        await host.Cache.MutateAsync(store =>
        {
            store.Settings.MtlsEnabled = true;
            store.Settings.MtlsClientCertificatePfx = pfx;
            store.Settings.MtlsClientCertificatePassword = SystemTestHost.PfxPassword;
        });

        // What the Settings tab's download button does: the PFX to disk, for a client to present.
        run.PfxPath = Path.Combine(run.CertDirectory, "RavensPort_ClientCert.pfx");
        await File.WriteAllBytesAsync(run.PfxPath, Convert.FromBase64String(pfx));

        Assert.True(new FileInfo(run.PfxPath).Length > 0);
        output.WriteLine($"certificate stored in the vault and written to {run.PfxPath}");
    });

    // ---- 7. the restart -------------------------------------------------------------------------

    [RequiresServiceAccountFact]
    public Task Stage07_TheConfigurationSurvivesARestart() =>
        StageAsync(nameof(Stage07_TheConfigurationSurvivesARestart), async () =>
    {
        var host = run.RequireHost();

        await host.RestartAsync(mtls: true);

        Assert.True(host.IsMtls);
        Assert.StartsWith("https://", host.BaseUrl, StringComparison.Ordinal);

        // Nothing was carried over in memory, so all of this came back out of 1Password.
        var reloaded = host.Cache.Current;
        Assert.Equal(8, reloaded.Routes.Count);
        Assert.Equal(3, reloaded.McpFunnels.Count);
        Assert.Equal(3, reloaded.McpSources.Count);
        Assert.Equal(3, reloaded.Credentials.Count);
        Assert.True(reloaded.Settings.MtlsEnabled);

        // The token survived too, which is what lets stage 10 run against a host that never
        // performed the exchange.
        var restoredToken = reloaded.Credentials
            .Single(c => c.Kind == CredentialKind.ClientCredentials).Token?.AccessToken;
        Assert.Equal(run.IssuedToken, restoredToken);

        output.WriteLine($"listener is {host.BaseUrl}; routes, funnels, sources, credentials, the "
                         + "mTLS setting and the issued token all came back out of the vault");
    });

    // ---- 8-10. the same checks again, now over mTLS ---------------------------------------------

    [RequiresServiceAccountFact]
    public Task Stage08_EveryCredentialPlacementStillArrivesOverMtls() =>
        StageAsync(nameof(Stage08_EveryCredentialPlacementStillArrivesOverMtls),
            () => AssertCredentialMatrixAsync(run.RequireHost()));

    [RequiresServiceAccountFact]
    public Task Stage09_BothFunnelsStillAnswerOverMtls() =>
        StageAsync(nameof(Stage09_BothFunnelsStillAnswerOverMtls),
            () => AssertFunnelsWorkAsync(run.RequireHost()));

    /// <summary>
    /// The same check as stage 5, but this host never performed the exchange -- the token came back
    /// out of 1Password. That is the half a token acquisition on its own can never show.
    /// </summary>
    [RequiresServiceAccountFact]
    public Task Stage10_TheRestoredOAuthTokenStillReachesTheMcpServer() =>
        StageAsync(nameof(Stage10_TheRestoredOAuthTokenStillReachesTheMcpServer),
            () => AssertOAuthTokenReachesTheMcpServerAsync(run.RequireHost()));

    // ---- 11. and the listener refuses the wrong caller ------------------------------------------

    [RequiresServiceAccountFact]
    public Task Stage11_TheListenerRefusesTheWrongCaller() =>
        StageAsync(nameof(Stage11_TheListenerRefusesTheWrongCaller), () =>
        {
            var pfxPath = run.PfxPath
                ?? throw new InvalidOperationException("Skipped -- no certificate was exported.");
            return AssertMtlsRefusalsAsync(run.RequireHost(), pfxPath);
        });

    // ---- the credential matrix -------------------------------------------------------------------

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

    private async Task AssertCredentialMatrixAsync(SystemTestHost host)
    {
        // Nothing attached: the hop still forwards, and the caller's own Authorization is stripped
        // rather than passed through -- a route with no credential must not become a way to smuggle
        // one to the upstream.
        var none = await PostAsync(host, "/app/none");
        Assert.Null(none.Header("Authorization"));
        output.WriteLine("  /app/none            -> nothing attached, caller's Authorization stripped");

        var one = await PostAsync(host, "/app/one");
        Assert.Equal($"Bearer {ApprovalRun.OAuthToken}", one.Header("Authorization"));
        output.WriteLine("  /app/one             -> Authorization: Bearer <token>");

        var two = await PostAsync(host, "/app/two-headers");
        Assert.Equal($"Bearer {ApprovalRun.OAuthToken}", two.Header("Authorization"));
        Assert.Equal(ApprovalRun.ProjectKey, two.Header("X-Project-Key"));
        output.WriteLine("  /app/two-headers     -> Authorization + X-Project-Key");

        var several = await PostAsync(host, "/app/several-headers");
        Assert.Equal($"Bearer {ApprovalRun.OAuthToken}", several.Header("Authorization"));
        Assert.Equal(ApprovalRun.ProjectKey, several.Header("X-Api-Key"));
        Assert.Equal($"token {ApprovalRun.ProjectKey}", several.Header("PRIVATE-TOKEN"));
        output.WriteLine("  /app/several-headers -> Authorization + X-Api-Key + PRIVATE-TOKEN (non-Bearer prefix)");

        var headerBody = await PostAsync(host, "/app/header-body");
        Assert.Equal($"Bearer {ApprovalRun.OAuthToken}", headerBody.Header("Authorization"));
        Assert.Equal(ApprovalRun.ProjectKey, FieldOf(headerBody.Body, "auth_token"));
        output.WriteLine("  /app/header-body     -> Authorization + auth_token in the body");

        // Both fields in one rewrite: two separate rewrites would each start from the original body
        // and the second would drop the first.
        var twoBody = await PostAsync(host, "/app/two-body");
        Assert.Equal(ApprovalRun.OAuthToken, FieldOf(twoBody.Body, "access_token"));
        Assert.Equal(ApprovalRun.ProjectKey, FieldOf(twoBody.Body, "project_token"));
        output.WriteLine("  /app/two-body        -> access_token + project_token, both in one rewrite");

        var both = await PostAsync(host, "/app/oauth-plus-key");
        Assert.Equal($"Bearer {ApprovalRun.OAuthToken}", both.Header("Authorization"));
        Assert.Equal(ApprovalRun.ProjectKey, both.Header("X-Api-Key"));
        output.WriteLine("  /app/oauth-plus-key  -> Authorization + X-Api-Key");
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

    // ---- funnels ----------------------------------------------------------------------------------

    private async Task AssertFunnelsWorkAsync(SystemTestHost host)
    {
        foreach (var version in new string?[] { null, "2025-11-25" })
        {
            var label = version ?? "current";

            var both = await host.ConnectMcpAsync("both", version);

            // Asserted before anything else: a pin that quietly failed would leave the client on the
            // current revision and every check below would still pass.
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
                $"  [{label}] funnel 'both' -> {tools.Count} tools from two sources, call ok; "
                + $"funnel 'solo' -> {soloTools.Count} tools, beta not visible");
        }
    }

    // ---- the OAuth token, all the way to an MCP server ---------------------------------------------

    /// <summary>
    /// The token the authorization server issued, arriving at an MCP server.
    ///
    /// This is the arrangement the product exists for and the one nothing else here covers. The
    /// funnel's source is a ProxyRoute, so reaching it means dialling one of this proxy's own routes
    /// through the loopback listener, which puts the route's credential transform in the path. The
    /// fake records the Authorization of every request it is sent, so what is asserted is the header
    /// that actually arrived rather than the configuration that should have produced it.
    /// </summary>
    private async Task AssertOAuthTokenReachesTheMcpServerAsync(SystemTestHost host)
    {
        var secured = run.SecuredMcpServer
            ?? throw new InvalidOperationException("Skipped -- the secured MCP server was never started.");

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
        Assert.All(authorizations, a => Assert.Equal($"Bearer {run.IssuedToken}", a));

        // And this proxy's own key is not among what it forwarded. It authenticates a caller to
        // RavensPort and has no business at the far end, where the upstream would log it.
        Assert.All(
            secured.ReceivedHeaders,
            headers => Assert.False(headers.ContainsKey(LocalAccessGuard.ApiKeyHeaderName)));

        output.WriteLine(
            $"  MCP server saw 'Bearer <issued token>' on all {authorizations.Count} distinct "
            + "authorization(s); the proxy key was stripped");
    }

    // ---- mTLS refusals ------------------------------------------------------------------------------

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
            output.WriteLine($"  correct client        -> {(int)ok.StatusCode}");
        }

        // No client certificate. The listener demands one on every connection, so this dies in the
        // handshake -- a transport failure, not a 403, because no request is ever sent.
        using (var noCert = host.CreateHttpClient(presentClientCertificate: false))
        {
            noCert.DefaultRequestHeaders.Add(LocalAccessGuard.ApiKeyHeaderName, SystemTestHost.ApiKey);
            var raised = await Assert.ThrowsAnyAsync<HttpRequestException>(
                () => noCert.GetAsync("/app/none/anything"));
            output.WriteLine($"  no client certificate -> refused: {Innermost(raised)}");
        }

        // Not trusting the listener: the same handshake from the other side. A self-signed
        // certificate cannot satisfy default chain validation, so the caller refuses the server.
        using (var noTrust = host.CreateHttpClient(trustTheListener: false))
        {
            noTrust.DefaultRequestHeaders.Add(LocalAccessGuard.ApiKeyHeaderName, SystemTestHost.ApiKey);
            var raised = await Assert.ThrowsAnyAsync<HttpRequestException>(
                () => noTrust.GetAsync("/app/none/anything"));
            output.WriteLine($"  listener not trusted  -> refused: {Innermost(raised)}");
        }

        // A wrong passphrase never reaches a socket at all: opening the PFX fails first, which is
        // why a client with a bad password reports something about the file rather than about TLS.
        var wrongPassword = Assert.ThrowsAny<CryptographicException>(
            () => X509CertificateLoader.LoadPkcs12FromFile(pfxPath, "not-the-password"));
        output.WriteLine($"  wrong PFX passphrase  -> refused before connecting: {wrongPassword.GetType().Name}");

        // And the right one still opens it, so the failure above was the password and not the file.
        using var opened = X509CertificateLoader.LoadPkcs12FromFile(pfxPath, SystemTestHost.PfxPassword);
        Assert.False(string.IsNullOrEmpty(opened.Thumbprint));
    }

    private static string Innermost(Exception ex)
    {
        while (ex.InnerException is { } inner) ex = inner;
        return $"{ex.GetType().Name}: {ex.Message}";
    }
}
