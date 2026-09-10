using System.Text.Json;
using RavensPort.Core.Models;
using RavensPort.Core.Vault;

namespace RavensPort.Core.Storage;

/// <summary>
/// The app's config, in memory, and the single place anything reads or changes it.
///
/// Memory is authoritative while the app runs; the vault is where it is kept between runs. That
/// split is what lets edits, token refreshes, and key rotations all go ahead when the password
/// manager happens to be locked — the change takes effect immediately and
/// <see cref="VaultSyncQueue"/> writes it out when the manager is reachable again.
///
/// Nothing is ever written to disk in the meantime. A pending change lives in this object and
/// nowhere else, so the cost of never reaching the vault is bounded and stated: the change is lost
/// when the app exits, and a credential whose token rotated in that window has to be reconnected.
/// Only the newest token is ever useful, so there is nothing a local cache could usefully hold
/// that would not also be a copy of the user's secrets sitting outside their password manager.
/// </summary>
public sealed class ConfigStoreCache
{
    private readonly IConfigVault _vault;
    private ConfigStore _current = new();
    private bool _initialized;

    /// <summary>
    /// Guards the object graph while it is read or changed. Three threads touch it — the WPF
    /// dispatcher (UI edits), the token refresh loop, and thread-pool threads serving proxied
    /// requests. Without it, a UI-thread Credentials.Add() landing mid-serialization throws
    /// "Collection was modified" out of the refresh loop and stops the entire host.
    ///
    /// Deliberately *not* held across a vault write. That takes seconds — a subprocess, a network
    /// round trip, sometimes a biometric prompt — and holding the lock through it would freeze
    /// every edit and every proxied request behind it. Writers take a snapshot under the lock and
    /// hand that to the vault instead.
    /// </summary>
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>Bumped on every change; compared against <see cref="_syncedVersion"/>.</summary>
    private long _version;
    private long _syncedVersion;

    public ConfigStoreCache(IConfigVault vault)
    {
        _vault = vault;
    }

    public ConfigStore Current => _current;

    public bool IsInitialized => _initialized;

    /// <summary>
    /// True while the vault is being read into memory — the first load, or a later
    /// <see cref="ReloadAsync"/>.
    ///
    /// Read by the Settings tab, which must not offer its vault-maintenance actions during this
    /// window. Those actions are driven by an integrity check that compares memory against the
    /// vault and reports the difference as orphaned items and missing records. Run mid-load, the
    /// comparison is against a store that is empty or half-replaced, so real items look orphaned
    /// and real records look missing — and every button next to that list deletes something.
    /// </summary>
    public bool IsLoading => Volatile.Read(ref _loading) != 0;

    /// <summary>
    /// Loaded, and not currently being reloaded. The one condition under which the vault-maintenance
    /// actions are answering a question about the real state of the vault.
    /// </summary>
    public bool IsSettled => _initialized && !IsLoading;

    /// <summary>
    /// An int rather than a bool so <see cref="Volatile"/> can be used on it. The load runs on a
    /// thread-pool thread and the flag is read on the dispatcher, so it needs to be published
    /// rather than merely written.
    /// </summary>
    private int _loading;

    /// <summary>
    /// Which backend and which vault the store in memory was read from, so a change of either is
    /// noticed. Null until the first successful load.
    /// </summary>
    private string? _loadedFrom;

    /// <summary>
    /// Backend and vault as one comparable value. Both halves matter: the user can move between
    /// 1Password and Proton Pass, and between two vaults inside either — and a configuration from
    /// one has no business being served, shown, or written back under the other.
    /// </summary>
    private string CurrentSource() => $"{_vault.Kind}:{_vault.VaultName}";

    /// <summary>
    /// True when the store in memory came from somewhere other than the vault now selected — so
    /// callers know a reload is owed rather than assuming the tabs are showing the right thing.
    /// </summary>
    public bool IsFromAnotherVault => _initialized && _loadedFrom != CurrentSource();

    /// <summary>True when changes have been made that the vault does not have yet.</summary>
    public bool HasPendingChanges => Interlocked.Read(ref _version) > Interlocked.Read(ref _syncedVersion);

    /// <summary>When the oldest still-unsynced change was made, for "waiting since" in the UI.</summary>
    public DateTimeOffset? PendingSince { get; private set; }

    /// <summary>
    /// What the last load had to say — a credential dropped because its vault item was deleted, a
    /// note written by a newer version. Null when the load was clean. Shown in the UI rather than
    /// only logged: a configuration that quietly changed under the user is exactly the thing they
    /// need told.
    /// </summary>
    public string? LastLoadNotice { get; private set; }

    /// <summary>
    /// Publishes the load flag and tells the UI, on the same event the pending state already uses —
    /// so a tab that is open while the vault is read re-evaluates rather than staying disabled
    /// until something else happens to poke it.
    /// </summary>
    private void SetLoading(bool loading)
    {
        Volatile.Write(ref _loading, loading ? 1 : 0);
        PendingChanged?.Invoke();
    }

    /// <summary>Clears the notice once the user has read it.</summary>
    public void DismissLoadNotice()
    {
        LastLoadNotice = null;
        PendingChanged?.Invoke();
    }

    /// <summary>
    /// Raised whenever the pending state changes. The queue and the UI both listen; Core stays
    /// MVVM-free, so this is a plain event and the UI marshals it.
    /// </summary>
    public event Action? PendingChanged;

    /// <summary>
    /// Loads the store and issues any missing endpoint keys. Idempotent: startup calls this
    /// directly (it needs the listen port before Kestrel can bind) and
    /// <see cref="ConfigStoreInitializerHostedService"/> calls it again as the host starts, which
    /// must not re-read the vault and discard edits made in between.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        // Identity, not a bare flag. The old check was "have I loaded anything", which is the wrong
        // question the moment the backend can change underneath this object — and it can, because
        // the vault here is GatedConfigVault, forwarding to whatever the gate has settled on.
        //
        // Picking a different password manager, or a different vault in the same one, left
        // _initialized true, so this returned immediately and the app kept serving the previous
        // vault's configuration. Worse, the next save then wrote that configuration into the newly
        // chosen vault. Comparing where the store actually came from makes a switch a reload.
        if (_initialized && _loadedFrom == CurrentSource()) return;

        SetLoading(true);

        try
        {
            _current = await _vault.LoadAsync(ct);
            _initialized = true;
            _loadedFrom = CurrentSource();
        }
        finally
        {
            // In a finally so a failed load does not leave the app believing it is still reading
            // the vault forever, which would disable vault maintenance with no way back.
            SetLoading(false);
        }

        LastLoadNotice = _vault.LastLoadWarning;

        // A route or funnel can reach the vault without a key — created by hand in the password
        // manager, or restored from an item whose key item is gone. Without this the access guard
        // has nothing to compare against and every request to that endpoint is refused.
        if (BackfillKeys(_current) > 0) MarkChanged();

        // The load dropped something the note still lists, so the note is now wrong. Queueing it
        // is what stops the same ghost coming back on every launch.
        if (_vault.LastLoadRemovals.Count > 0) MarkChanged();
    }

    /// <summary>
    /// Issues a never-expiring key to every route, funnel, and API bridge that has none. Returns
    /// how many were issued, so the caller can skip the write when there is nothing to do.
    /// </summary>
    private static int BackfillKeys(ConfigStore store)
    {
        var issued = 0;

        var keys = store.Routes.Select(r => r.Key)
            .Concat(store.McpFunnels.Select(f => f.Key))
            .Concat(store.McpApiBridges.Select(b => b.Key));

        foreach (var key in keys)
        {
            if (key.IsConfigured) continue;

            var fresh = ProxyKey.Generate();
            key.Value = fresh.Value;
            key.CreatedUtc = fresh.CreatedUtc;
            key.ExpiresUtc = null;
            issued++;
        }

        return issued;
    }

    /// <summary>
    /// Applies a change and queues it for the vault. Returns as soon as the change is live in
    /// memory — it does not wait for the write.
    ///
    /// This is the difference between the proxy working and not working while a password manager
    /// is locked. Blocking here would mean an agent's route could not be edited, a proxy key could
    /// not be rotated, and an expiring token could not be refreshed, all because of something the
    /// user may be nowhere near. The change takes effect now; the queue makes it durable when it
    /// can, and the UI says plainly that it has not yet.
    /// </summary>
    public async Task MutateAsync(Action<ConfigStore> mutate, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            mutate(_current);
        }
        finally
        {
            _lock.Release();
        }

        MarkChanged();
    }

    /// <summary>
    /// Queues the current store for the vault, for callers that changed <see cref="Current"/> in
    /// place — the OAuth flows, which mutate a credential over minutes and cannot hold a lock.
    /// </summary>
    public Task SaveAsync(CancellationToken ct = default)
    {
        MarkChanged();
        return Task.CompletedTask;
    }

    /// <summary>Records that the store has moved on, and wakes the queue.</summary>
    private void MarkChanged()
    {
        Interlocked.Increment(ref _version);
        PendingSince ??= DateTimeOffset.UtcNow;

        PendingChanged?.Invoke();
        SyncRequested?.Invoke();
    }

    /// <summary>Raised when there is something new to write. <see cref="VaultSyncQueue"/> listens.</summary>
    internal event Action? SyncRequested;

    /// <summary>
    /// A detached copy of the store, plus the version it represents. Taken under the lock and
    /// deep-cloned so the vault write that follows cannot see a half-applied edit, and so callers
    /// can keep editing while it is in flight.
    /// </summary>
    internal async Task<(ConfigStore Store, long Version)> SnapshotForSyncAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var json = JsonSerializer.Serialize(_current, VaultRedaction.FullOptions);
            var clone = JsonSerializer.Deserialize<ConfigStore>(json, VaultRedaction.FullOptions) ?? new ConfigStore();

            return (clone, Interlocked.Read(ref _version));
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Records that everything up to <paramref name="version"/> is now in the vault. Edits made
    /// while the write was in flight have a higher version and stay pending, so a change is never
    /// marked saved because an earlier one succeeded.
    /// </summary>
    internal void MarkSynced(long version)
    {
        Interlocked.Exchange(ref _syncedVersion, version);

        if (!HasPendingChanges) PendingSince = null;

        PendingChanged?.Invoke();
    }

    /// <summary>
    /// Discards the in-memory store and re-reads the vault — the escape hatch for a secret edited
    /// in the password manager's own UI, which nothing here can be notified about.
    ///
    /// Callers must reload their view models afterwards: this replaces the contents of the
    /// existing lists, so bindings to the store itself survive but bindings to individual records
    /// do not.
    /// </summary>
    public async Task ReloadAsync(CancellationToken ct = default)
    {
        SetLoading(true);

        try
        {
            var loaded = await _vault.LoadAsync(ct).ConfigureAwait(false);

            await _lock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                RestoreInto(_current, Snapshot(loaded));
            }
            finally
            {
                _lock.Release();
            }
        }
        finally
        {
            // Covers the read *and* the replacement. RestoreInto empties the existing lists before
            // refilling them, so a integrity check landing between the two would see a store with
            // nothing in it and call every item in the vault an orphan.
            SetLoading(false);
        }

        // Whatever was just read defines where the store came from. A reload is also how a
        // deliberate switch to another vault lands, so this has to move with it.
        _loadedFrom = CurrentSource();
        _initialized = true;

        // Memory now matches the vault exactly, so anything that was pending has been deliberately
        // thrown away rather than saved.
        Interlocked.Exchange(ref _syncedVersion, Interlocked.Read(ref _version));
        PendingSince = null;
        LastLoadNotice = _vault.LastLoadWarning;
        PendingChanged?.Invoke();

        // Ordered after the reset above on purpose: what the load dropped is not "memory the vault
        // has not got", it is the note being out of date, and it has to stay queued.
        if (_vault.LastLoadRemovals.Count > 0) MarkChanged();
    }

    /// <summary>
    /// Empties the store and forgets that it was ever loaded — for disconnecting the password
    /// manager.
    ///
    /// Everything goes: with no vault behind it there is no configuration, and leaving routes and
    /// credentials in memory would keep the proxy spending the user's tokens on a configuration
    /// they have just disconnected from. The listen port survives because Kestrel is already bound
    /// to it and the Settings tab would otherwise show a port that is not the one in use.
    ///
    /// Callers must reload their view models afterwards, and rebuild the proxy's route table.
    /// </summary>
    public async Task ResetAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            RestoreInto(_current, Snapshot(new ConfigStore()) with { ListenPort = _current.Settings.ListenPort });
            _initialized = false;

            // Both of these belonged to the vault being left. _loadedFrom especially: leaving it
            // set would let a later load against the same vault be skipped as already done, when
            // the store has in fact been emptied.
            _loadedFrom = null;
            LastLoadNotice = null;
        }
        finally
        {
            _lock.Release();
        }

        // Nothing is pending: the changes were not saved, they were deliberately discarded along
        // with the vault they belonged to.
        Interlocked.Exchange(ref _syncedVersion, Interlocked.Increment(ref _version));
        PendingSince = null;
        PendingChanged?.Invoke();
    }

    /// <summary>
    /// Membership of every list plus the settings scalars, restored <em>into</em> the existing
    /// instances rather than by swapping <see cref="Current"/> for a clone. The view models hold
    /// direct references to individual records and to the store itself; handing back a fresh
    /// object graph would leave every one of those bindings pointing at an orphan.
    /// </summary>
    private sealed record StoreSnapshot(
        List<CredentialRecord> Credentials,
        List<UpstreamRecord> Upstreams,
        List<RouteMapping> Routes,
        List<McpSourceRecord> McpSources,
        List<McpFunnelRecord> McpFunnels,
        List<McpApiBridgeRecord> McpApiBridges,
        int ListenPort,
        bool McpFunnelEnabled,
        bool McpApiBridgeEnabled,
        bool MtlsEnabled,
        string MtlsClientCertificatePfx,
        string MtlsClientCertificatePassword);

    private static StoreSnapshot Snapshot(ConfigStore store) => new(
        [.. store.Credentials],
        [.. store.Upstreams],
        [.. store.Routes],
        [.. store.McpSources],
        [.. store.McpFunnels],
        [.. store.McpApiBridges],
        store.Settings.ListenPort,
        store.Settings.McpFunnelEnabled,
        store.Settings.McpApiBridgeEnabled,
        store.Settings.MtlsEnabled,
        store.Settings.MtlsClientCertificatePfx,
        store.Settings.MtlsClientCertificatePassword);

    private static void RestoreInto(ConfigStore store, StoreSnapshot snapshot)
    {
        ReplaceAll(store.Credentials, snapshot.Credentials);
        ReplaceAll(store.Upstreams, snapshot.Upstreams);
        ReplaceAll(store.Routes, snapshot.Routes);
        ReplaceAll(store.McpSources, snapshot.McpSources);
        ReplaceAll(store.McpFunnels, snapshot.McpFunnels);
        ReplaceAll(store.McpApiBridges, snapshot.McpApiBridges);

        store.Settings.ListenPort = snapshot.ListenPort;
        store.Settings.McpFunnelEnabled = snapshot.McpFunnelEnabled;
        store.Settings.McpApiBridgeEnabled = snapshot.McpApiBridgeEnabled;
        store.Settings.MtlsEnabled = snapshot.MtlsEnabled;
        store.Settings.MtlsClientCertificatePfx = snapshot.MtlsClientCertificatePfx;
        store.Settings.MtlsClientCertificatePassword = snapshot.MtlsClientCertificatePassword;
    }

    private static void ReplaceAll<T>(List<T> target, List<T> source)
    {
        target.Clear();
        target.AddRange(source);
    }

    public CredentialRecord? GetCredential(Guid id) =>
        _current.Credentials.FirstOrDefault(c => c.Id == id);
}
