using RavensPort.Core.Models;
using RavensPort.Core.Proxy;

namespace RavensPort.Core.Tests.Mcp;

/// <summary>
/// mTLS end to end, on the pipeline the app actually runs.
///
/// The awkward part of switching the listener to https is not the listener: it is that this app is
/// its own client. A funnel source of kind ProxyRoute is dialled back through the very port
/// Kestrel just bound, so the moment that port demands a client certificate, the funnel is a
/// caller that has to present one — and it has to dial https, because a plaintext request at a
/// TLS listener is a dropped connection with no status code to explain it. These tests hold both
/// halves of that agreement in place.
/// </summary>
public class McpFunnelMtlsTests : IAsyncLifetime
{
    private const string Token = "MTLS-ROUTE-ACCESS-TOKEN";

    private FakeMcpServer _upstream = null!;
    private FunnelTestHost _host = null!;

    public async Task InitializeAsync()
    {
        _upstream = await FakeMcpServer.StartAsync();
        _host = await FunnelTestHost.StartAsync(mtls: true);

        var credential = new CredentialRecord
        {
            Name = "mcp-credential",
            ClientId = "id",
            ClientSecret = "secret",
            Token = new TokenSet(Token, "refresh", DateTimeOffset.UtcNow.AddHours(1), "Bearer", DateTimeOffset.UtcNow),
        };

        var upstreamRecord = new UpstreamRecord { Name = "mcp-upstream", BaseUrl = _upstream.Url };

        var route = new RouteMapping
        {
            PathPrefix = "/mcpsrv",
            UpstreamId = upstreamRecord.Id,
            Credentials = [RouteCredential.For(credential.Id, CredentialPlacement.Header)],
            StripPrefix = true,
        };

        await _host.MutateAsync(store =>
        {
            store.Credentials.Add(credential);
            store.Upstreams.Add(upstreamRecord);
            store.Routes.Add(route);
        });
        _host.RebuildProxyConfig();

        var source = await _host.AddRouteSourceAsync("auth", route.Id);
        await _host.AddFunnelAsync("agent", new McpFunnelSource { SourceId = source.Id });
    }

    public async Task DisposeAsync()
    {
        await _host.DisposeAsync();
        await _upstream.DisposeAsync();
    }

    [Fact]
    public void TheListenerIsHttps()
    {
        Assert.StartsWith("https://", _host.BaseUrl);
    }

    [Fact]
    public async Task TheFunnelReachesItsOwnRouteThroughTheMtlsListener()
    {
        // The regression this file exists for. Everything downstream of the hop — the guard, YARP,
        // the credential transform — is unchanged by mTLS; the hop itself is not, and when it got
        // the scheme or the certificate wrong the failure surfaced as a funnel with no tools.
        var client = await _host.ConnectAsync("agent");

        Assert.Contains("auth__echo", (await client.ListToolsAsync()).Select(t => t.Name));
        Assert.Equal("over-mtls", await FunnelTestHost.CallTextAsync(client, "auth__echo", "over-mtls"));
    }

    [Fact]
    public async Task TheRoutesOAuthTokenStillArrives()
    {
        // mTLS authenticates the caller to this proxy. It replaces nothing downstream: the
        // upstream still gets the route's bearer token and only that.
        var client = await _host.ConnectAsync("agent");
        await FunnelTestHost.CallTextAsync(client, "auth__echo", "still-credentialed");

        Assert.NotEmpty(_upstream.ReceivedAuthorization);
        Assert.All(_upstream.ReceivedAuthorization, header => Assert.Equal($"Bearer {Token}", header));
    }

    [Fact]
    public async Task DiscoveryOfARouteBackedSourceWorksOverMtls()
    {
        // What the MCP Funnel tab's Refresh does, and the first thing a user sees fail.
        var source = _host.Cache.Current.McpSources.Single();

        var catalog = await _host.Pool.DiscoverAsync(source);

        Assert.Null(catalog.Error);
        Assert.Contains("echo", catalog.Tools);
    }

    [Fact]
    public async Task ACallerWithNoClientCertificateCannotConnectAtAll()
    {
        // The point of the feature: the proxy key is no longer the only thing between a local
        // process and the user's OAuth grant. Refusal happens in the handshake, so it is not a
        // 403 — there is no HTTP exchange to carry one.
        using var client = new HttpClient(new SocketsHttpHandler
        {
            SslOptions = new System.Net.Security.SslClientAuthenticationOptions
            {
                // Accept whatever the server presents, so the only thing under test is the
                // missing client certificate.
                RemoteCertificateValidationCallback = (_, _, _, _) => true,
            },
        });

        client.DefaultRequestHeaders.Add(LocalAccessGuard.ApiKeyHeaderName, FunnelTestHost.ApiKey);

        await Assert.ThrowsAnyAsync<HttpRequestException>(
            () => client.GetAsync($"{_host.BaseUrl}/mcpsrv/anything"));
    }

    [Fact]
    public async Task ACallerPresentingTheCertificateStillNeedsTheRoutesProxyKey()
    {
        // The two checks are independent, and the certificate is the weaker of them for this
        // purpose: it says "a permitted machine", not "a caller entitled to this route". Holding
        // it must not skip the per-endpoint key.
        using var client = new HttpClient(_host.CreateClientHandler()!);

        var response = await client.GetAsync($"{_host.BaseUrl}/mcpsrv/anything");

        Assert.Equal(System.Net.HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public void TheGeneratedCertificateCarriesTheLoopbackNames()
    {
        // Without a SAN the certificate is rejected by every client that validates a hostname
        // before it reaches the pinning question — which is every MCP host that is not this app.
        using var certificate = MtlsCertificateFactory.Load(
            _host.Cache.Current.Settings.MtlsClientCertificatePfx, FunnelTestHost.PfxPassword);

        var subjectAlternativeNames = certificate.Extensions
            .OfType<System.Security.Cryptography.X509Certificates.X509SubjectAlternativeNameExtension>()
            .Single();

        Assert.Contains("localhost", subjectAlternativeNames.EnumerateDnsNames());
        Assert.Contains(System.Net.IPAddress.Loopback, subjectAlternativeNames.EnumerateIPAddresses());
    }
}
