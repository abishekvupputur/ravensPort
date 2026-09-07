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
using RavensPort.Core.Models;
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

    private readonly SystemTestEnvironment.Account _account;
    private readonly List<McpClient> _clients = [];
    private WebApplication _proxy;

    private SystemTestHost(SystemTestEnvironment.Account account, WebApplication proxy, string baseUrl)
    {
        _account = account;
        _proxy = proxy;
        BaseUrl = baseUrl;
    }

    public string BaseUrl { get; private set; }

    public ConfigStoreCache Cache => _proxy.Services.GetRequiredService<ConfigStoreCache>();

    public ActivityLog ActivityLog => _proxy.Services.GetRequiredService<ActivityLog>();

    public bool IsMtls => _proxy.Services.GetRequiredService<KestrelMtlsState>().IsEnabled;

    /// <summary>
    /// One of the app's own services, so the suite drives the real thing rather than reimplementing
    /// it. The OAuth stage uses this to run the actual token exchange the app would run.
    /// </summary>
    public T Service<T>() where T : notnull => _proxy.Services.GetRequiredService<T>();

    /// <param name="purgeBeforeLoading">
    /// Empties the vault before the store is read, rather than after.
    ///
    /// The order matters and the wrong one is not merely inelegant. Loading maps every item in the
    /// vault, so a single unreadable one -- archived, or left in a half-deleted state by a purge
    /// that hit 1Password's write rate limit -- fails the load and the host never starts, which
    /// puts the vault beyond the reach of the very sweep meant to clean it. Purging first breaks
    /// that: nothing has been read yet, so nothing can refuse to be read.
    /// </param>
    /// <summary>
    /// The configured account whose vault was written to longest ago, and how each was judged.
    ///
    /// The point is spreading writes. 1Password rate-limits them per account and a pass of this
    /// suite spends a couple of dozen, so running twice against one account runs it out -- which is
    /// exactly what happened while this suite was being written. Alternating blindly would be
    /// simpler and wrong: CI runners keep no state between runs, so there is nowhere to remember
    /// whose turn it is. The vault itself remembers, in the timestamps of the items already in it.
    ///
    /// Judged on the newest item in each vault, which is when that vault was last written. Reads
    /// only -- listing costs no write quota, so choosing cannot itself consume the thing it is
    /// trying to conserve. An empty vault has no timestamp at all and wins outright: nothing has
    /// been written there, so its quota is untouched.
    ///
    /// A candidate that cannot be reached is skipped rather than fatal. One account being expired
    /// or misconfigured should cost its turn, not the run.
    /// </summary>
    public static async Task<SystemTestEnvironment.Account> ChooseLeastRecentlyUsedAsync(
        IReadOnlyList<SystemTestEnvironment.Account> accounts, Action<string> log)
    {
        if (accounts.Count == 1) return accounts[0];

        SystemTestEnvironment.Account? best = null;
        var bestWrittenAt = DateTimeOffset.MaxValue;
        var seenVaults = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var account in accounts)
        {
            DateTimeOffset writtenAt;
            string resolved;
            try
            {
                (writtenAt, resolved) = await LastWrittenAtAsync(account);
            }
            catch (Exception ex)
            {
                log($"  {account.Label} ({account.VaultName}): unreachable, skipped -- {ex.Message}");
                continue;
            }

            log(writtenAt == DateTimeOffset.MinValue
                ? $"  {account.Label}: {resolved} -- never written"
                : $"  {account.Label}: {resolved} -- last written {writtenAt:u}");

            // Two accounts on one vault spread nothing that this can see. Their timestamps are the
            // same by construction, so every comparison ties and the first always wins -- which
            // looks like the selection being broken and is really the vaults being one. Said out
            // loud, because the alternative is a mechanism that quietly does nothing.
            // Only vaults that actually resolved. An unadopted one reports a placeholder rather
            // than an identity, and comparing placeholders said two different empty vaults were
            // the same vault -- a warning that was wrong in exactly the situation it was added to
            // explain.
            if (writtenAt == DateTimeOffset.MinValue)
            {
                // Nothing to compare, and nothing to warn about.
            }
            else if (seenVaults.TryGetValue(resolved, out var already))
            {
                log($"  WARNING: {account.Label} and {already} resolve to the SAME vault. Grant each "
                    + "service account its own, or this cannot spread anything.");
            }
            else
            {
                seenVaults[resolved] = account.Label;
            }

            if (writtenAt >= bestWrittenAt) continue;

            best = account;
            bestWrittenAt = writtenAt;
        }

        return best ?? throw new InvalidOperationException(
            "None of the configured accounts could be reached. Check the tokens, and that each "
            + "reaches a vault named by its RAVENSPORT_SYSTEM_TEST_VAULT entry.");
    }

    /// <summary>
    /// When this account's vault was last written, from the newest item in it, or
    /// <see cref="DateTimeOffset.MinValue"/> when it holds nothing.
    ///
    /// The host is built but never started: Kestrel binds in StartAsync, and none of this needs a
    /// listener. Nothing is loaded either -- item titles and timestamps are all this reads, so no
    /// item contents are fetched and nothing is decrypted.
    /// </summary>
    private static async Task<(DateTimeOffset WrittenAt, string Resolved)> LastWrittenAtAsync(
        SystemTestEnvironment.Account account)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddRavensPort();

        await using var probe = builder.Build();

        // Without this the probe reads the wrong vault, and silently. NativeCliRunner guards
        // initialisation with a *static* flag, so the first token to reach the SDK in this process
        // is the only one that ever does: every later Unlock is accepted, every later
        // EnsureInitialized returns early, and the SDK keeps answering as whoever went first. Two
        // accounts then report the same vault, which is exactly what it looked like.
        NativeCliRunner.ResetInitialization();
        probe.Services.GetRequiredService<OnePasswordSession>().Unlock(account.Token);

        var gate = probe.Services.GetRequiredService<VaultGateService>();
        var status = await gate.ConnectAsync(VaultBackendKind.OnePassword);

        // Deliberately no adoption here, unlike the real host. Adoption writes the Config stamp,
        // and a probe that writes is not a probe: it spends the quota it exists to conserve, and it
        // set both vaults' timestamps to the same instant, which made every comparison a tie that
        // the first account won. An unstamped vault is not a problem to fix while choosing -- it is
        // the answer. Nothing has ever been written there, so it is first in line, and the host that
        // is actually chosen does the adopting.
        if (!status.IsReady &&
            status.For(VaultBackendKind.OnePassword)?.Availability is VaultAvailability.VaultMissing)
        {
            return (DateTimeOffset.MinValue, "unadopted");
        }

        if (!status.IsReady)
        {
            var detail = status.For(VaultBackendKind.OnePassword);
            throw new InvalidOperationException(
                $"availability={detail?.Availability.ToString() ?? "unknown"}, vault={account.VaultName}");
        }

        var items = await probe.Services.GetRequiredService<IConfigVault>().ListLiveItemsAsync();

        // Every item, Config included, and Config is the one that matters. An earlier version of
        // this excluded it, reasoning that a stamp is not data -- which made the whole mechanism a
        // no-op: this suite empties the vault before it finishes, so the timestamps it was judging
        // by were precisely the ones it had just deleted. Both vaults then looked untouched, the
        // tie broke to the first every time, and the second account was never chosen.
        //
        // Config survives the purge and is rewritten by every save, so it records when a vault was
        // last used rather than what happens to be sitting in it. A vault with no Config at all has
        // never been adopted, which is genuinely untouched and genuinely first in line.
        var written = items.Select(i => i.UpdatedUtc).Where(t => t is not null).ToList();

        // The name the provider settled on, and the id behind it. Reported rather than assumed
        // because the provider finds its vault by the Config stamp rather than by name, so two
        // accounts can be configured with different names and still resolve to the same vault --
        // which looks exactly like a broken comparison and is not one.
        var resolved = status.For(VaultBackendKind.OnePassword) is { } s
            ? $"{s.VaultName ?? "?"} [{s.VaultId ?? "?"}]"
            : "?";

        return (written.Count == 0 ? DateTimeOffset.MinValue : written.Max()!.Value, resolved);
    }

    public static Task<SystemTestHost> StartAsync(
        SystemTestEnvironment.Account account, bool mtls = false, bool purgeBeforeLoading = false) =>
        StartInternalAsync(account, mtls, purgeBeforeLoading);

    private static async Task<SystemTestHost> StartInternalAsync(
        SystemTestEnvironment.Account account, bool mtls, bool purgeBeforeLoading = false)
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
        // As in the probe: the static initialisation guard has to be cleared, or this host talks
        // to whichever account happened to initialise the SDK first.
        NativeCliRunner.ResetInitialization();
        proxy.Services.GetRequiredService<OnePasswordSession>().Unlock(account.Token);
        var status = await proxy.Services.GetRequiredService<VaultGateService>()
            .ConnectAsync(VaultBackendKind.OnePassword);

        // A vault that has lost its stamp -- cleared by hand, or by an older version of the purge
        // here that deleted the Config item -- is present but unrecognised. Adoption is what puts
        // the stamp back, and it accepts an empty vault for exactly this case. Tried once, and only
        // for that one availability.
        if (!status.IsReady &&
            status.For(VaultBackendKind.OnePassword)?.Availability is VaultAvailability.VaultMissing)
        {
            status = await proxy.Services.GetRequiredService<VaultGateService>()
                .UseExistingVaultAsync(VaultBackendKind.OnePassword, account.VaultName);
        }

        if (!status.IsReady)
        {
            await proxy.DisposeAsync();
            var detail = status.For(VaultBackendKind.OnePassword);
            throw new InvalidOperationException(
                "Could not open the 1Password vault with the service-account token. "
                + $"availability={detail?.Availability.ToString() ?? "unknown"}, "
                + $"vault={detail?.VaultName ?? "<none>"}, detail={detail?.Detail ?? "<none>"}. "
                + $"{account.Label} must reach a vault named '{account.VaultName}'.");
        }

        if (purgeBeforeLoading)
        {
            await PurgeAsync(proxy.Services.GetRequiredService<IConfigVault>(), tolerateFailures: false);
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

        var host = new SystemTestHost(account, proxy, baseUrl);

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

        var replacement = await StartInternalAsync(_account, mtls, purgeBeforeLoading: false);
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
    /// Everything ListLiveItemsAsync returns except the Config item. The product deliberately
    /// touches nothing it does not own -- that is what makes a shared vault safe -- but this suite
    /// is pointed at a throwaway account by an acknowledgement variable that says so, and "empty"
    /// there means empty.
    ///
    /// The Config item is the exception, and it is not data. It is the stamp that identifies the
    /// vault as RavensPort's, and connecting reads it long before anything is saved -- so deleting
    /// it does not leave an empty vault, it leaves an unrecognisable one, and the next run fails at
    /// startup with VaultMissing. Learned by doing exactly that.
    ///
    /// It is emptied rather than deleted, though: its index is rewritten to reference nothing, so
    /// the load that follows has no item ids to chase. Deleting items is not enough on its own,
    /// because an archived item is not in the listing this sweeps and is still in the note. See the
    /// comment on the rewrite below.
    /// </summary>
    /// <param name="tolerateFailures">
    /// True for the cleanup at the end, false for the sweep at the start, and the asymmetry is
    /// deliberate. 1Password rate-limits vault writes, and this suite spends a couple of dozen of
    /// them per run; when the run is over the verdict is already decided and an item left behind
    /// costs nothing, because the next run's opening sweep takes it. Before the run it costs
    /// everything -- "the vault is empty at startup" would be asserting against a vault that is
    /// not, so a failure there has to stop the run rather than be reported and passed over.
    /// </param>
    public Task<int> PurgeVaultAsync(bool tolerateFailures = false) =>
        PurgeAsync(_proxy.Services.GetRequiredService<IConfigVault>(), tolerateFailures);

    private static async Task<int> PurgeAsync(IConfigVault vault, bool tolerateFailures)
    {
        var items = await vault.ListLiveItemsAsync();
        var deletable = items.Where(i => !(i.IsOwned && i.Role == VaultItemRole.Config)).ToList();

        var deleted = 0;
        foreach (var item in deletable)
        {
            try
            {
                await vault.DeleteItemAsync(item.ItemId);
                deleted++;
            }
            catch (VaultSaveException) when (tolerateFailures)
            {
                // Almost always the rate limit. Left for the next run's opening sweep.
            }
            catch (VaultSaveException ex) when (ex.Message.Contains("rate limit", StringComparison.OrdinalIgnoreCase))
            {
                // The opening sweep, throttled. Reported as itself rather than as the raw CLI
                // error, because the raw one reads like a broken product and this is a quota:
                // 1Password limits vault writes, and a pass of this suite spends a couple of dozen
                // of them, so runs in quick succession run out.
                throw new InvalidOperationException(
                    $"1Password is rate-limiting vault writes, with {deleted} of {deletable.Count} "
                    + "item(s) deleted. The suite cannot assert an empty vault against a vault it "
                    + "was not allowed to empty. Wait for the quota to reset and run again; the "
                    + "next opening sweep takes what this one left.",
                    ex);
            }
        }

        // The note's index is cleared as well, and this is the half of the sweep that deleting
        // items cannot do.
        //
        // ListLiveItemsAsync returns active items only, so an item that has been archived rather
        // than deleted -- by a hand-run cleanup in the 1Password UI, or by a sweep that died
        // partway -- is invisible here and cannot be deleted. It is not invisible to the loader:
        // the Config note still indexes it, and the load that follows this sweep fetches every id
        // the note names. 1Password answers that fetch with "item is not in an active state",
        // which is not one of the phrasings GetItemAsync reads as "gone", so it throws rather than
        // shrugging -- correctly, because a product that treated an unreadable item as a deleted
        // one would erase a user's credential over a transient fault.
        //
        // The result was a run that purged the vault successfully and then failed Stage01 anyway,
        // on a reference the purge had no way to reach, and stayed failing until someone emptied
        // the archive by hand. Rewriting the note with an empty store drops every reference, so
        // the load that follows fetches nothing and finds nothing.
        //
        // The stamp survives, which is the point of doing it this way rather than deleting the
        // Config item: an unstamped vault is an unrecognisable one, and the adoption path that
        // repairs it costs a round trip and a write on every subsequent run.
        //
        // Safe to call here despite what ReconcileDeletionsAsync does with an emptied store: that
        // sweep only ever deletes ids that are both in the previous index and in the live listing,
        // and it declines entirely until a session has completed a full read -- which, at the point
        // this runs, it has not.
        try
        {
            await vault.RewriteAllAsync(new ConfigStore());
        }
        catch (VaultSaveException) when (tolerateFailures)
        {
            // The rate limit again. The next run's opening sweep rewrites it.
        }

        return deleted;
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
