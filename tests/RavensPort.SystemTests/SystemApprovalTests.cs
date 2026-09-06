using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using RavensPort.Core.Models;
using RavensPort.Core.Proxy;
using RavensPort.Core.Tests.Mcp;
using RavensPort.Core.Vault;
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
/// timestamps that would have to be scrubbed before every comparison. What is worth pinning is
/// behaviour, and behaviour is what is asserted.
///
/// What this does not cover, said plainly: the installer and the GUI. Both are unreachable
/// unattended -- see SystemTestHost for why a service-account token cannot be handed to an installed
/// RavensPort.exe without a human. Everything between the vault and the wire is covered here.
/// </summary>
public sealed class SystemApprovalTests(ITestOutputHelper output) : IAsyncLifetime
{
    private const string RouteUpstreamHeader = "X-Api-Key";
    private const string StaticApiKey = "MOCK-UPSTREAM-API-KEY"; // gitleaks:allow

    private WebApplication _routeUpstream = null!;
    private readonly List<FakeMcpServer> _mcpServers = [];

    /// <summary>Where the exported certificate lands, as the Settings tab's Download button would put it.</summary>
    private readonly string _certDirectory =
        Path.Combine(Path.GetTempPath(), $"ravensport-approval-{Guid.NewGuid():n}");

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_certDirectory);

        // The thing a route forwards to. Echoes back what it was sent, which is the only way to
        // prove the credential transform actually put the key on the wire.
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        _routeUpstream = builder.Build();
        _routeUpstream.Run(async context =>
        {
            var key = context.Request.Headers.TryGetValue(RouteUpstreamHeader, out var v) ? v.ToString() : "";
            await context.Response.WriteAsync($"upstream-saw:{key}");
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

        await using var host = await SystemTestHost.StartAsync(token);

        // ---- 1. the vault starts empty -------------------------------------------------------
        Stage("1. empty vault at startup");

        await ClearVaultAsync(host);

        Assert.Empty(host.Cache.Current.Credentials);
        Assert.Empty(host.Cache.Current.Routes);
        Assert.Empty(host.Cache.Current.McpFunnels);
        Assert.Empty(host.Cache.Current.McpSources);
        output.WriteLine("vault holds no credentials, routes, sources or funnels");

        // ---- 2. mock servers, credentials, routes, funnels ------------------------------------
        Stage("2. seeding credentials, mock MCP servers, a route and two funnels");

        var alpha = await StartMcpServerAsync();
        var beta = await StartMcpServerAsync();

        var apiKey = new CredentialRecord
        {
            Name = "mock-upstream-key",
            Kind = CredentialKind.ApiKey,
            ApiKey = StaticApiKey,
            DefaultPlacement = CredentialPlacement.Header,
            DefaultParameterName = RouteUpstreamHeader,
            DefaultValuePrefix = "",
        };

        var upstream = new UpstreamRecord { Name = "mock-upstream", BaseUrl = RouteUpstreamUrl };

        var route = new RouteMapping
        {
            PathPrefix = "/app/mock",
            UpstreamId = upstream.Id,
            StripPrefix = true,
            Key = new ProxyKey { Value = SystemTestHost.ApiKey },
            Credentials =
            [
                new RouteCredential
                {
                    CredentialId = apiKey.Id,
                    Placement = CredentialPlacement.Header,
                    ParameterName = RouteUpstreamHeader,
                    ValuePrefix = "",
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

        await host.Cache.MutateAsync(store =>
        {
            store.Settings.McpFunnelEnabled = true;
            store.Credentials.Add(apiKey);
            store.Upstreams.Add(upstream);
            store.Routes.Add(route);
            store.McpSources.Add(sourceAlpha);
            store.McpSources.Add(sourceBeta);

            // Two funnels: one pooling both sources, one exposing a single source. The pair is what
            // proves a key opens its own endpoint and no other.
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
        });

        host.RebuildProxyConfig();
        output.WriteLine("seeded: 1 credential, 1 route, 2 MCP sources, 2 funnels");

        // ---- 3. everything works over plain HTTP ---------------------------------------------
        Stage("3. route and funnels over http");

        await AssertRouteForwardsTheCredentialAsync(host);
        await AssertFunnelsWorkAsync(host, "before mTLS");

        // ---- 4. mTLS on, certificate exported --------------------------------------------------
        Stage("4. enabling mTLS and exporting the certificate");

        var pfx = MtlsCertificateFactory.GenerateClientCertificatePfx(SystemTestHost.PfxPassword);

        await host.Cache.MutateAsync(store =>
        {
            store.Settings.MtlsEnabled = true;
            store.Settings.MtlsClientCertificatePfx = pfx;
            store.Settings.MtlsClientCertificatePassword = SystemTestHost.PfxPassword;
        });

        // What the Settings tab's download does: the PFX to disk, for a client to present.
        var pfxPath = Path.Combine(_certDirectory, "RavensPort_ClientCert.pfx");
        await File.WriteAllBytesAsync(pfxPath, Convert.FromBase64String(pfx));

        Assert.True(new FileInfo(pfxPath).Length > 0);
        output.WriteLine($"certificate stored in the vault and written to {pfxPath}");

        // ---- 5. restart -------------------------------------------------------------------------
        Stage("5. restarting; everything below came back out of 1Password");

        await host.RestartAsync(mtls: true);

        Assert.True(host.IsMtls);
        Assert.StartsWith("https://", host.BaseUrl, StringComparison.Ordinal);

        // The proof that the restart was real: this host was built from nothing but the token.
        var reloaded = host.Cache.Current;
        Assert.Single(reloaded.Routes);
        Assert.Equal(2, reloaded.McpFunnels.Count);
        Assert.Equal(2, reloaded.McpSources.Count);
        Assert.Single(reloaded.Credentials);
        Assert.True(reloaded.Settings.MtlsEnabled);
        output.WriteLine($"listener is {host.BaseUrl}, config survived the restart");

        // ---- 6. everything works again, now over mTLS -------------------------------------------
        Stage("6. route and funnels over https with the client certificate");

        await AssertRouteForwardsTheCredentialAsync(host);
        await AssertFunnelsWorkAsync(host, "after restart, over mTLS");

        // ---- 7. leave the vault as it was found -------------------------------------------------
        Stage("7. clearing the vault");

        await ClearVaultAsync(host);
        Assert.Empty(host.Cache.Current.McpFunnels);
        output.WriteLine("vault emptied; approval run complete");
    }

    // ---- the checks, run identically before and after the restart ------------------------------

    /// <summary>
    /// The route forwards, and the credential is on the request when it lands. Asserting the status
    /// alone would pass for a route that forwarded nothing at all.
    /// </summary>
    private async Task AssertRouteForwardsTheCredentialAsync(SystemTestHost host)
    {
        using var client = host.CreateHttpClient();
        client.DefaultRequestHeaders.Add(LocalAccessGuard.ApiKeyHeaderName, SystemTestHost.ApiKey);

        var response = await client.GetAsync("/app/mock/anything");
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(response.IsSuccessStatusCode, $"route returned {(int)response.StatusCode}: {body}");
        Assert.Equal($"upstream-saw:{StaticApiKey}", body);
        output.WriteLine($"route /app/mock -> {(int)response.StatusCode}, upstream received the API key");
    }

    /// <summary>
    /// Both funnels, on both protocol revisions. The revision is asserted before anything else: a
    /// pin that quietly failed would leave the client on the current one and every check below would
    /// still pass, which would make the old-protocol half of this suite decorative.
    /// </summary>
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

            // The name/arguments overload rather than CallToolRequestParams: it serialises the
            // dictionary for us, and the protocol revision under test is a property of the client
            // above, not of the call.
            var call = await both.CallToolAsync(
                "alpha__echo",
                new Dictionary<string, object?> { ["value"] = "hello" }!);
            Assert.Equal("hello", call.Content.OfType<TextContentBlock>().First().Text);

            // The single-source funnel must not expose the other source, whatever the revision.
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

    /// <summary>
    /// Empties the vault through the product's own store, not by deleting 1Password items directly:
    /// what is being asserted is that RavensPort sees an empty vault, and going around the mapper
    /// would prove something weaker.
    /// </summary>
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
