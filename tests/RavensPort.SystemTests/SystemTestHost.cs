using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using RavensPort.Core.Diagnostics;
using RavensPort.Core.Mcp;
using RavensPort.Core.Proxy;
using RavensPort.Core.Storage;
using RavensPort.Core.Vault;

namespace RavensPort.SystemTests;

/// <summary>
/// The real pipeline against the real vault.
///
/// The same shape as Core.Tests' FunnelTestHost -- guard, funnel gate, funnel endpoints, YARP, in
/// that order, because the order is load-bearing -- with one deliberate difference: it does not
/// replace <see cref="IConfigVault"/>. Configuration is read from and written to an actual
/// 1Password vault through the service-account path, which is the half of the product no other test
/// exercises.
///
/// **Why not the installed EXE.** The obvious reading of "system test" is to install RavensPort and
/// drive it, and that cannot be automated -- by design, not by omission. OnePasswordSession refuses
/// to persist a service-account token ("an install that starts at login serves nothing until
/// someone types the token in"), and the one place a token can be kept, HelloKeyProtector, needs a
/// Windows Hello gesture to give it back and "cannot do so quietly". So an unattended RavensPort.exe
/// has no vault and serves nothing. Hosting the same pipeline here keeps every part of the product
/// under test except the installer and the GUI, and needs no human at the keyboard.
///
/// **Restart is real.** <see cref="RestartAsync"/> disposes the host and builds a new one that reads
/// the same vault from scratch. Nothing is carried over in memory, so anything the second host knows
/// came back out of 1Password -- which is exactly what the restart step is meant to prove.
/// </summary>
internal sealed class SystemTestHost : IAsyncDisposable
{
    /// <summary>
    /// One key for every endpoint this suite creates. Per-endpoint isolation is real and is pinned
    /// by LocalAccessGuardTests; here the subject is the system, and inventing a key per route would
    /// only add bookkeeping to every call.
    /// </summary>
    public const string ApiKey = "system-approval-key-0123456789"; // gitleaks:allow

    /// <summary>
    /// Drawn fresh per run rather than written down, so no password-shaped literal sits in the
    /// source or in the built assembly. Nothing outside this process needs it: the certificate is
    /// minted, stored in the vault and opened again within the same run.
    /// </summary>
    public static readonly string PfxPassword =
        Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

    private readonly string _token;
    private readonly List<McpClient> _clients = [];
    private WebApplication _proxy;

    private SystemTestHost(string token, WebApplication proxy, string baseUrl)
    {
        _token = token;
        _proxy = proxy;
        BaseUrl = baseUrl;
    }

    public string BaseUrl { get; private set; }

    public ConfigStoreCache Cache => _proxy.Services.GetRequiredService<ConfigStoreCache>();

    public ActivityLog ActivityLog => _proxy.Services.GetRequiredService<ActivityLog>();

    public bool IsMtls => _proxy.Services.GetRequiredService<KestrelMtlsState>().IsEnabled;

    public static Task<SystemTestHost> StartAsync(string token, bool mtls = false) =>
        StartInternalAsync(token, mtls);

    private static async Task<SystemTestHost> StartInternalAsync(string token, bool mtls)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"{(mtls ? "https" : "http")}://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddRavensPort();

        // IConfigVault is deliberately left alone. This is the one host in the repository that
        // talks to a real 1Password vault.

        builder.WebHost.ConfigureKestrel(options => options.ConfigureHttpsDefaults(https =>
        {
            var state = options.ApplicationServices.GetRequiredService<KestrelMtlsState>();
            if (state.Certificate is not { } certificate) return;

            https.ServerCertificate = certificate;
            https.ClientCertificateMode =
                Microsoft.AspNetCore.Server.Kestrel.Https.ClientCertificateMode.RequireCertificate;
            https.ClientCertificateValidation = (clientCert, _, _) =>
                string.Equals(clientCert.Thumbprint, certificate.Thumbprint, StringComparison.OrdinalIgnoreCase);
        }));

        var proxy = builder.Build();

        // The service-account path in full: the token goes into the session, and the gate connects
        // the 1Password backend with it. No desktop app, no integration channel, no Hello -- which
        // is what makes this runnable unattended at all.
        proxy.Services.GetRequiredService<OnePasswordSession>().Unlock(token);
        var status = await proxy.Services.GetRequiredService<VaultGateService>()
            .ConnectAsync(VaultBackendKind.OnePassword);

        if (!status.IsReady)
        {
            await proxy.DisposeAsync();
            var detail = status.For(VaultBackendKind.OnePassword);
            throw new InvalidOperationException(
                "Could not open the 1Password vault with the service-account token. "
                + $"availability={detail?.Availability.ToString() ?? "unknown"}, "
                + $"vault={detail?.VaultName ?? "<none>"}, detail={detail?.Detail ?? "<none>"}. "
                + "The token must reach a vault named 'RavensPort' (VaultConstants.VaultName).");
        }

        // Normally the hosted service does this at startup. Called here because the mTLS decision
        // below reads the store, and it has to be settled before Kestrel binds.
        await proxy.Services.GetRequiredService<ConfigStoreCache>().InitializeAsync();

        if (mtls)
        {
            var settings = proxy.Services.GetRequiredService<ConfigStoreCache>().Current.Settings;
            if (string.IsNullOrEmpty(settings.MtlsClientCertificatePfx))
            {
                await proxy.DisposeAsync();
                throw new InvalidOperationException(
                    "Asked for an mTLS listener, but the vault holds no certificate. The run that "
                    + "enabled mTLS is what stores one.");
            }

            proxy.Services.GetRequiredService<KestrelMtlsState>()
                .Enable(settings.MtlsClientCertificatePfx, settings.MtlsClientCertificatePassword);
        }

        proxy.UseLocalAccessGuard();
        proxy.UseMcpFunnelGate();
        proxy.MapMcpFunnel();
        proxy.MapReverseProxy();

        await proxy.StartAsync();

        var baseUrl = proxy.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First();

        var host = new SystemTestHost(token, proxy, baseUrl);

        // A route-backed funnel source dials 127.0.0.1:{ListenPort}, so the stored port has to be
        // the one actually bound. In the app they agree by construction; here the port is ephemeral
        // and changes on every restart, so it is written back each time.
        await host.Cache.MutateAsync(store => store.Settings.ListenPort = new Uri(baseUrl).Port);

        return host;
    }

    /// <summary>
    /// Stops this host and starts a new one over the same vault, as a restart of the app would.
    ///
    /// Everything in memory goes: the config cache, the connection pool and its upstream sessions,
    /// the loaded certificate. What the new host knows, it re-read from 1Password.
    /// </summary>
    public async Task RestartAsync(bool mtls)
    {
        foreach (var client in _clients) await client.DisposeAsync();
        _clients.Clear();

        await FlushVaultAsync();
        await _proxy.StopAsync();
        await _proxy.DisposeAsync();

        var replacement = await StartInternalAsync(_token, mtls);
        _proxy = replacement._proxy;
        BaseUrl = replacement.BaseUrl;
    }

    /// <summary>
    /// Pushes route changes into YARP. Funnel edits need no equivalent -- the funnel reads config
    /// per request -- but a route is only reachable once the proxy has been told about it.
    /// </summary>
    public void RebuildProxyConfig() =>
        _proxy.Services.GetRequiredService<ProxyConfigChangeNotifier>().Rebuild();

    /// <summary>
    /// An HTTP client for this listener.
    ///
    /// The two switches exist to build callers that are deliberately wrong, which is the only way to
    /// show mTLS is enforced rather than merely configured. A run where every client is correct
    /// cannot tell a listener that demands a certificate from one that ignores it.
    /// </summary>
    /// <param name="presentClientCertificate">
    /// False omits the client certificate. The listener asks for one on every connection, so this
    /// is refused at the handshake -- before any request, which is why it fails as a transport
    /// error rather than a 403.
    /// </param>
    /// <param name="trustTheListener">
    /// False drops the thumbprint check and leaves .NET's default chain validation, which a
    /// self-signed certificate cannot satisfy. The client-side half of the same handshake: this is
    /// the caller refusing the server, not the server refusing the caller.
    /// </param>
    public HttpClient CreateHttpClient(bool presentClientCertificate = true, bool trustTheListener = true)
    {
        var handler = new SocketsHttpHandler();

        if (_proxy.Services.GetRequiredService<KestrelMtlsState>().Certificate is { } certificate)
        {
            if (presentClientCertificate)
            {
                handler.SslOptions.ClientCertificates = [certificate];
            }

            if (trustTheListener)
            {
                // The pair is pinned and self-signed, so there is no chain to build and nothing a
                // CA check could consult. Thumbprint equality is what the listener enforces in the
                // other direction too.
                handler.SslOptions.RemoteCertificateValidationCallback = (_, presented, _, _) =>
                    presented is X509Certificate2 c &&
                    string.Equals(c.Thumbprint, certificate.Thumbprint, StringComparison.OrdinalIgnoreCase);
            }
        }

        return new HttpClient(handler) { BaseAddress = new Uri(BaseUrl) };
    }

    /// <summary>Connects an MCP client to one funnel, optionally pinned to an older revision.</summary>
    public async Task<McpClient> ConnectMcpAsync(string slug, string? protocolVersion = null)
    {
        var options = new HttpClientTransportOptions
        {
            Endpoint = new Uri($"{BaseUrl}{McpFunnelEndpoints.BasePath}/{slug}"),
            TransportMode = HttpTransportMode.StreamableHttp,
            ConnectionTimeout = TimeSpan.FromSeconds(30),
            AdditionalHeaders = new Dictionary<string, string> { [LocalAccessGuard.ApiKeyHeaderName] = ApiKey },
        };

        var transport = new HttpClientTransport(options, CreateHttpClient(), null, ownsHttpClient: true);

        var client = await McpClient.CreateAsync(
            transport,
            protocolVersion is null ? null : new McpClientOptions { ProtocolVersion = protocolVersion });

        _clients.Add(client);
        return client;
    }

    /// <summary>
    /// Deletes every item in the vault, item by item, and reports how many went.
    ///
    /// Clearing the store's collections and saving is not the same thing and is not enough. A save
    /// reconciles only what the store knows about, so anything the store never loaded survives it:
    /// items left by a run that failed before its cleanup, items from an older schema, and anything
    /// added by hand while testing. Those accumulate, and the next run's "the vault is empty at
    /// startup" then asserts against a vault that is nothing of the kind.
    ///
    /// Everything ListLiveItemsAsync returns, not only the prefixed ones. The product deliberately
    /// touches nothing it does not own -- that is what makes a shared vault safe -- but this suite
    /// is pointed at a throwaway account by an acknowledgement variable that says so, and "empty"
    /// there means empty. The Config item goes with the rest; the next save writes a new one, which
    /// is how an adopted empty vault gets stamped in the first place.
    /// </summary>
    public async Task<int> PurgeVaultAsync()
    {
        var vault = _proxy.Services.GetRequiredService<IConfigVault>();

        var items = await vault.ListLiveItemsAsync();
        foreach (var item in items)
        {
            await vault.DeleteItemAsync(item.ItemId);
        }

        return items.Count;
    }

    /// <summary>
    /// Waits for the vault to catch up, which is what App.ShutDown does before the process ends.
    ///
    /// Not optional, and the first run of this suite is what proved it. MutateAsync changes the
    /// store in memory and only wakes the sync queue -- the write to 1Password is asynchronous. A
    /// restart that did not wait tore the host down mid-write and the replacement read an empty
    /// vault, which looked exactly like "configuration does not survive a restart" and was in fact
    /// this harness being unfaithful to the shutdown it was meant to simulate.
    /// </summary>
    private async Task FlushVaultAsync()
    {
        if (_proxy.Services.GetService<VaultSyncQueue>() is { } queue)
        {
            // The app allows 15 seconds and carries on regardless. Longer here, and asserted:
            // a system test that quietly continued past a half-written vault would go on to blame
            // whatever failed next.
            var flushed = await queue.FlushAsync(TimeSpan.FromSeconds(30));
            if (!flushed)
            {
                throw new InvalidOperationException(
                    "The vault sync queue did not drain within 30 seconds, so anything asserted "
                    + "after this point would be racing an unfinished write.");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients)
        {
            try { await client.DisposeAsync(); } catch { /* a torn-down host takes its clients with it */ }
        }

        try { await FlushVaultAsync(); } catch { /* teardown: the run's verdict is already decided */ }

        await _proxy.StopAsync();
        await _proxy.DisposeAsync();
    }
}
