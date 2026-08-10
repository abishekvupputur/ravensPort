using Microsoft.Extensions.Logging.Abstractions;
using RavensPort.Core.Auth;
using RavensPort.Core.Diagnostics;
using RavensPort.Core.Models;
using RavensPort.Core.Storage;
using RavensPort.Core.Vault;

namespace RavensPort.Core.Tests.Vault;

/// <summary>
/// What happens while the password manager is locked: everything keeps working, and the changes
/// queue up in memory until it is reachable again.
///
/// The trade this pins is deliberate and one-directional. Nothing is spilled to disk, so a change
/// that never reaches the vault dies with the process — and a credential whose token rotated in
/// that window has to be reconnected. In exchange, a locked manager never stops an edit, never
/// stops a token refresh, and never takes a route down.
/// </summary>
public class DeferredSyncTests : IDisposable
{
    private readonly string _logPath = Path.Combine(Path.GetTempPath(), $"ravensport-sync-{Guid.NewGuid()}");

    // ---- Edits ----------------------------------------------------------------------------------

    [Fact]
    public async Task AnEditAppliesImmediatelyEvenWhenTheVaultIsLocked()
    {
        var (cache, _) = await LockedAsync();

        await cache.MutateAsync(store =>
            store.Credentials.Add(new CredentialRecord { Name = "added", ClientId = "id", ClientSecret = "s" }));

        Assert.Contains(cache.Current.Credentials, c => c.Name == "added");
        Assert.True(cache.HasPendingChanges);
    }

    [Fact]
    public async Task AnEditDoesNotWaitForTheVault()
    {
        // The point of the whole design: MutateAsync returns as soon as the change is live, so a
        // locked manager cannot freeze the UI behind a subprocess that is never going to answer.
        var (cache, _) = await LockedAsync();

        var mutate = cache.MutateAsync(store => store.Settings.ListenPort = 6000);

        Assert.True(mutate.IsCompleted);
        Assert.Equal(6000, cache.Current.Settings.ListenPort);
    }

    [Fact]
    public async Task PendingChangesAreWrittenOnceTheVaultIsAvailable()
    {
        var vault = new SwitchableVault();
        var cache = new ConfigStoreCache(vault);
        await cache.InitializeAsync();

        var queue = NewQueue(cache, vault);

        vault.IsLocked = true;
        await cache.MutateAsync(store =>
            store.Credentials.Add(new CredentialRecord { Name = "queued", ClientId = "id", ClientSecret = "s" }));

        Assert.False(await queue.TrySyncAsync());
        Assert.True(cache.HasPendingChanges);
        Assert.Equal(VaultSyncState.WaitingForUnlock, queue.State);

        vault.IsLocked = false;

        Assert.True(await queue.TrySyncAsync());
        Assert.False(cache.HasPendingChanges);
        Assert.Equal(VaultSyncState.Synced, queue.State);
        Assert.Contains((await vault.LoadAsync()).Credentials, c => c.Name == "queued");
    }

    [Fact]
    public async Task ManyEditsDuringALockCostOneWrite()
    {
        // Coalescing falls out of the whole-document contract: the only thing worth writing is the
        // newest state. Fifty edits during a lock must not become fifty writes when it lifts.
        var vault = new SwitchableVault();
        var cache = new ConfigStoreCache(vault);
        await cache.InitializeAsync();

        var queue = NewQueue(cache, vault);
        vault.IsLocked = true;

        for (var i = 0; i < 50; i++)
        {
            var index = i;
            await cache.MutateAsync(store =>
                store.Credentials.Add(new CredentialRecord { Name = $"c{index}", ClientId = "id", ClientSecret = "s" }));
        }

        vault.IsLocked = false;
        await queue.TrySyncAsync();

        Assert.Equal(1, vault.SaveCount);
        Assert.Equal(50, (await vault.LoadAsync()).Credentials.Count);
    }

    [Fact]
    public async Task AnEditMadeWhileAWriteIsInFlightStaysPending()
    {
        // Otherwise the later edit would be marked saved because an earlier one succeeded, and it
        // would silently never reach the vault at all.
        var vault = new SwitchableVault();
        var cache = new ConfigStoreCache(vault);
        await cache.InitializeAsync();

        var queue = NewQueue(cache, vault);

        vault.BeforeSave = () => cache.MutateAsync(store => store.Settings.ListenPort = 7777);

        await cache.MutateAsync(store => store.Settings.McpFunnelEnabled = true);
        await queue.TrySyncAsync();

        Assert.True(cache.HasPendingChanges);

        vault.BeforeSave = null;
        await queue.TrySyncAsync();

        Assert.False(cache.HasPendingChanges);
        Assert.Equal(7777, (await vault.LoadAsync()).Settings.ListenPort);
    }

    [Fact]
    public async Task ReloadFromVaultDiscardsPendingChanges()
    {
        var vault = new SwitchableVault();
        var cache = new ConfigStoreCache(vault);
        await cache.InitializeAsync();
        vault.IsLocked = true;

        await cache.MutateAsync(store =>
            store.Credentials.Add(new CredentialRecord { Name = "doomed", ClientId = "id", ClientSecret = "s" }));

        vault.IsLocked = false;
        await cache.ReloadAsync();

        Assert.Empty(cache.Current.Credentials);
        Assert.False(cache.HasPendingChanges);
    }

    // ---- Declined authorization -----------------------------------------------------------------

    [Fact]
    public async Task ADeclinedAuthorizationIsNotRetriedOnATimer()
    {
        // Reaching the manager is what raises its prompt, so retrying a decline asks the same
        // question again. A user reported this as the app pushing notifications at them: three
        // "Denied authorization for SDK client" failures inside a minute, each one a prompt they
        // had already answered.
        var vault = new SwitchableVault();
        var cache = new ConfigStoreCache(vault);
        await cache.InitializeAsync();

        var queue = NewQueue(cache, vault);
        vault.DeclinesAuthorization = true;

        await cache.MutateAsync(store =>
            store.Credentials.Add(new CredentialRecord { Name = "queued", ClientId = "id", ClientSecret = "s" }));

        Assert.False(await queue.TrySyncAsync());
        Assert.Equal(VaultSyncState.AuthorizationDeclined, queue.State);

        var attemptsAfterTheDecline = vault.SaveAttempts;

        // The pump ticking, and an unrelated edit landing. Neither may go near the vault.
        Assert.False(await queue.TrySyncAsync());
        await cache.MutateAsync(store => store.Settings.ListenPort = 6001);
        Assert.False(await queue.TrySyncAsync());

        Assert.Equal(attemptsAfterTheDecline, vault.SaveAttempts);
        Assert.True(cache.HasPendingChanges);
    }

    [Fact]
    public async Task AskingToSaveAfterADeclineTriesAgain()
    {
        // The way back. Nothing else clears the latch, so this is the only thing standing between
        // the user's pending changes and losing them on exit.
        var vault = new SwitchableVault();
        var cache = new ConfigStoreCache(vault);
        await cache.InitializeAsync();

        var queue = NewQueue(cache, vault);
        vault.DeclinesAuthorization = true;

        await cache.MutateAsync(store =>
            store.Credentials.Add(new CredentialRecord { Name = "queued", ClientId = "id", ClientSecret = "s" }));

        await queue.TrySyncAsync();
        Assert.Equal(VaultSyncState.AuthorizationDeclined, queue.State);

        // The user unlocks 1Password and presses "I've unlocked it — save now".
        vault.DeclinesAuthorization = false;

        Assert.True(await queue.FlushAsync(TimeSpan.FromSeconds(5)));
        Assert.False(cache.HasPendingChanges);
        Assert.Equal(VaultSyncState.Synced, queue.State);
        Assert.Contains((await vault.LoadAsync()).Credentials, c => c.Name == "queued");
    }

    [Fact]
    public async Task ADeclineFollowedByAnotherDeclineStaysStopped()
    {
        // Pressing the button is the user asking, so the prompt is raised once more — and if they
        // decline that one too, everything goes quiet again rather than resuming the timer.
        var vault = new SwitchableVault();
        var cache = new ConfigStoreCache(vault);
        await cache.InitializeAsync();

        var queue = NewQueue(cache, vault);
        vault.DeclinesAuthorization = true;

        await cache.MutateAsync(store => store.Settings.ListenPort = 6002);
        await queue.TrySyncAsync();

        Assert.False(await queue.FlushAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(VaultSyncState.AuthorizationDeclined, queue.State);

        var attempts = vault.SaveAttempts;
        Assert.False(await queue.TrySyncAsync());
        Assert.Equal(attempts, vault.SaveAttempts);
    }

    [Fact]
    public async Task AnOrdinaryLockIsStillRetriedAutomatically()
    {
        // The decline latch must not swallow the common case: a manager that is simply locked lets
        // us back in without the user doing anything in RavensPort, and that has to keep working.
        var vault = new SwitchableVault();
        var cache = new ConfigStoreCache(vault);
        await cache.InitializeAsync();

        var queue = NewQueue(cache, vault);
        vault.IsLocked = true;

        await cache.MutateAsync(store => store.Settings.ListenPort = 6003);

        Assert.False(await queue.TrySyncAsync());
        Assert.Equal(VaultSyncState.WaitingForUnlock, queue.State);

        vault.IsLocked = false;

        Assert.True(await queue.TrySyncAsync());
        Assert.Equal(VaultSyncState.Synced, queue.State);
    }

    // ---- Token refresh --------------------------------------------------------------------------

    [Fact]
    public async Task TokensStillRefreshWhileTheVaultIsLocked()
    {
        // The alternative — pausing refresh — breaks every OAuth route as its token ages out,
        // which is far more common than the exit-during-a-lock case that this risks.
        var (cache, vault) = await LockedAsync(WithExpiringCredential());

        var activityLog = NewLog();
        var refresher = new TokenRefreshService(
            cache, NewOAuth2Service(), activityLog, NullLogger<TokenRefreshService>.Instance);

        await refresher.RefreshDueCredentialsAsync(CancellationToken.None);

        // The endpoint is unreachable, so the refresh fails — but it was attempted, which is the
        // behaviour under test. A gate would have skipped it before the scan.
        Assert.Contains(activityLog.GetRecent(200), line => line.Contains("expiring soon"));
        Assert.True(vault.IsLocked);
    }

    [Fact]
    public async Task AProxyKeyCanBeRotatedWhileTheVaultIsLocked()
    {
        var store = new ConfigStore();
        store.Routes.Add(new RouteMapping { PathPrefix = "/api", Key = ProxyKey.Generate() });

        var (cache, _) = await LockedAsync(store);
        var before = cache.Current.Routes[0].Key.Value;

        await cache.MutateAsync(s => s.Routes[0].Key.Regenerate());

        Assert.NotEqual(before, cache.Current.Routes[0].Key.Value);
        Assert.True(cache.HasPendingChanges);
    }

    // ---- Nothing on disk ------------------------------------------------------------------------

    [Fact]
    public async Task NothingIsWrittenToDiskWhileChangesArePending()
    {
        // The rule the whole design rests on: a pending change lives in memory and nowhere else.
        // A spill file would be a copy of the user's secrets sitting outside their password
        // manager, which is the thing this app exists to avoid.
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RavensPort");

        var before = SnapshotFiles(appData);

        var (cache, _) = await LockedAsync();
        await cache.MutateAsync(store =>
            store.Credentials.Add(new CredentialRecord
            {
                Name = "secret-bearing", ClientId = "id", ClientSecret = "SENTINEL-NOT-ON-DISK",
            }));

        var after = SnapshotFiles(appData);

        // Logs are the one thing that legitimately changes, and they never carry secrets.
        var added = after.Except(before).Where(p => !p.Contains(@"\logs\", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(added);
    }

    // ---- Helpers ---------------------------------------------------------------------------------

    private static string[] SnapshotFiles(string directory) =>
        Directory.Exists(directory)
            ? Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
            : [];

    private async Task<(ConfigStoreCache Cache, SwitchableVault Vault)> LockedAsync(ConfigStore? seed = null)
    {
        var vault = new SwitchableVault();
        if (seed is not null) await vault.SaveAsync(seed);

        var cache = new ConfigStoreCache(vault);
        await cache.InitializeAsync();

        vault.IsLocked = true;
        return (cache, vault);
    }

    private static ConfigStore WithExpiringCredential()
    {
        var store = new ConfigStore();
        store.Credentials.Add(new CredentialRecord
        {
            Name = "oauth",
            ClientId = "id",
            ClientSecret = "secret",
            // Unreachable on purpose: the assertion is that a refresh was attempted, not that it
            // succeeded, and a test that made real network calls would be proving something else.
            TokenEndpoint = "https://token-endpoint.invalid/token",
            Token = new TokenSet("ACCESS", "REFRESH",
                DateTimeOffset.UtcNow.AddMinutes(1), "Bearer", DateTimeOffset.UtcNow),
        });

        return store;
    }

    private VaultSyncQueue NewQueue(ConfigStoreCache cache, IConfigVault vault) =>
        new(cache, vault, ReadyGate(vault), NewLog());

    /// <summary>A gate that has already settled on a backend, so the queue will actually write.</summary>
    private VaultGateService ReadyGate(IConfigVault vault)
    {
        var gate = new VaultGateService(
            new OnePasswordVaultProvider(new FakeCliRunner(), NewLog(), "does-not-exist"),
            new ProtonPassVaultProvider(new FakeCliRunner(), NewLog(), "does-not-exist"),
            NewLog());

        gate.SelectBackend(VaultBackendKind.ProtonPass);
        return gate;
    }

    private OAuth2Service NewOAuth2Service() => new(
        new GoogleOAuthService(NewLog()),
        new GoogleServiceAccountService(NewLog()),
        new ClientCredentialsService(NewLog()),
        new DeviceCodeService(NewLog()),
        NewLog());

    // ---- Disconnecting --------------------------------------------------------------------------

    [Fact]
    public async Task DisconnectingEmptiesTheStoreAndLeavesNothingPending()
    {
        // Disconnecting is the one place unsaved changes are discarded on purpose. Leaving them
        // pending afterwards would have the UI promising to save them to a vault nobody is
        // connected to; leaving the records loaded would keep the proxy spending the user's tokens.
        var vault = new SwitchableVault();
        var cache = new ConfigStoreCache(vault);
        await cache.InitializeAsync();

        await cache.MutateAsync(store =>
            store.Credentials.Add(new CredentialRecord { Name = "saved", ClientId = "id", ClientSecret = "s" }));
        await NewQueue(cache, vault).TrySyncAsync();

        // Unsaved on top of what the vault already has, which is what makes the discard visible.
        await cache.MutateAsync(store =>
        {
            store.Settings.ListenPort = 6000;
            store.Credentials.Add(new CredentialRecord { Name = "pending", ClientId = "id", ClientSecret = "s" });
        });

        await cache.ResetAsync();

        Assert.Empty(cache.Current.Credentials);
        Assert.False(cache.HasPendingChanges);
        Assert.Null(cache.PendingSince);

        // The port survives: Kestrel is already bound to it, so showing anything else would be a lie.
        Assert.Equal(6000, cache.Current.Settings.ListenPort);

        // And the vault is untouched — disconnecting discards what is in memory, it is not a delete.
        var inTheVault = (await vault.LoadAsync()).Credentials;
        Assert.Contains(inTheVault, c => c.Name == "saved");
        Assert.DoesNotContain(inTheVault, c => c.Name == "pending");
    }

    [Fact]
    public async Task ConnectingAgainLoadsTheNewVault()
    {
        var vault = new SwitchableVault();
        var cache = new ConfigStoreCache(vault);
        await cache.InitializeAsync();

        await cache.MutateAsync(store =>
            store.Credentials.Add(new CredentialRecord { Name = "saved", ClientId = "id", ClientSecret = "s" }));
        await NewQueue(cache, vault).TrySyncAsync();

        await cache.ResetAsync();
        Assert.False(cache.IsInitialized);

        // The same call the startup path makes — a reset store is a first load, not a stale one.
        await cache.InitializeAsync();

        Assert.Contains(cache.Current.Credentials, c => c.Name == "saved");
    }

    private ActivityLog NewLog() => new(_logPath);

    public void Dispose()
    {
        try { Directory.Delete(_logPath, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>
    /// A vault that can be locked and unlocked at will, so the queue's retry behaviour can be
    /// driven without a real password manager.
    /// </summary>
    private sealed class SwitchableVault : IConfigVault
    {
        private readonly InMemoryVault _inner = InMemoryVault.Empty();

        public bool IsLocked { get; set; }

        /// <summary>
        /// The manager reporting that the user dismissed its authorization prompt, rather than that
        /// it happens to be locked. Worded as 1Password's SDK words it.
        /// </summary>
        public bool DeclinesAuthorization { get; set; }

        public int SaveCount { get; private set; }

        /// <summary>
        /// Every save that reached this vault, successful or not — which is what counts as an
        /// interruption, because reaching the manager is what raises its prompt.
        /// </summary>
        public int SaveAttempts { get; private set; }

        /// <summary>Runs inside SaveAsync, to land an edit while a write is in flight.</summary>
        public Func<Task>? BeforeSave { get; set; }

        public VaultBackendKind Kind => VaultBackendKind.ProtonPass;

        public string VaultName => _inner.VaultName;

        public string? LastLoadWarning => _inner.LastLoadWarning;

        public IReadOnlyList<string> LastLoadRemovals => _inner.LastLoadRemovals;

        public Task<VaultStatus> ProbeAsync(CancellationToken ct = default) =>
            Task.FromResult(new VaultStatus(Kind,
                IsLocked ? VaultAvailability.NotSignedIn : VaultAvailability.Ready, VaultId: "switchable"));

        /// <summary>Depth is irrelevant here: nothing about this fake can prompt anyone.</summary>
        public Task<VaultStatus> ProbeAsync(VaultProbeDepth depth, CancellationToken ct = default) =>
            ProbeAsync(ct);

        public Task CreateVaultAsync(string vaultName, CancellationToken ct = default) =>
            _inner.CreateVaultAsync(vaultName, ct);

        public Task UseExistingVaultAsync(string vaultName, CancellationToken ct = default) =>
            _inner.UseExistingVaultAsync(vaultName, ct);

        public void Forget() => _inner.Forget();

        public Task RewriteAllAsync(ConfigStore store, CancellationToken ct = default) =>
            SaveAsync(store, ct);

        public Task<IReadOnlyList<VaultItemEntry>> ListLiveItemsAsync(CancellationToken ct = default) =>
            IsLocked ? throw new VaultLockedException(Kind) : _inner.ListLiveItemsAsync(ct);

        public Task DeleteItemAsync(string itemId, CancellationToken ct = default) =>
            IsLocked ? throw new VaultLockedException(Kind) : _inner.DeleteItemAsync(itemId, ct);

        public Task<ConfigStore> LoadAsync(CancellationToken ct = default) =>
            IsLocked ? throw new VaultLockedException(Kind) : _inner.LoadAsync(ct);

        public async Task SaveAsync(ConfigStore store, CancellationToken ct = default)
        {
            SaveAttempts++;

            if (DeclinesAuthorization)
            {
                throw new VaultCliException(
                    "Could not list the 'RavensPort' vault: An error occurred when processing SDK "
                    + "request: Error { msg: Denied authorization for SDK client, inner: None }");
            }

            if (IsLocked) throw new VaultLockedException(Kind);

            if (BeforeSave != null)
            {
                await BeforeSave();
            }
            SaveCount++;

            await _inner.SaveAsync(store, ct);
        }
    }
}
