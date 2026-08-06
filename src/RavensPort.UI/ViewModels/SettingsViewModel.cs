using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RavensPort.UI.Services;
using RavensPort.Core.Diagnostics;
using RavensPort.Core.Proxy;
using RavensPort.Core.Storage;
using RavensPort.Core.Vault;
using RavensPort.Core.Mcp;

namespace RavensPort.UI.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private const int VisibleLogLines = 150;

    private readonly ConfigStoreCache _configStoreCache;
    private readonly ActivityLog _activityLog;
    private readonly VaultGateService _gate;
    private readonly VaultSyncQueue _syncQueue;
    private readonly VaultIntegrityService _integrity;
    private readonly ProtonPassAuthenticator _protonAuthenticator;
    private readonly OnePasswordSession _onePasswordSession;
    private readonly ProxyConfigChangeNotifier _proxyConfigChangeNotifier;
    private readonly McpSourceConnectionPool _mcpSourceConnectionPool;
    private readonly IPlatformLauncher _launcher;
    private readonly IFileSavePicker _fileSavePicker;

    /// <summary>
    /// Held only so the timer is not collected out from under the view model. Never stopped — the
    /// tab lives as long as the process does.
    /// </summary>
    private readonly IDisposable _logTimer;

    [ObservableProperty] private int _listenPort;
    [ObservableProperty] private bool _mtlsEnabled;
    [ObservableProperty] private string _recentActivity = "";
    [ObservableProperty] private string _statusMessage = "Ready.";

    /// <summary>Which manager is in use and which vault in it — "Proton Pass — vault 'RavensPort'".</summary>
    [ObservableProperty] private string _passwordManagerSummary = "";

    /// <summary>Where the CLI is and what version answered, so a wrong binary is visible.</summary>
    [ObservableProperty] private string _passwordManagerDetail = "";

    /// <summary>Whether everything in memory has reached the vault, in one line.</summary>
    [ObservableProperty] private string _vaultSyncSummary = "";

    /// <summary>The token option, kept off the lock banner — see <see cref="VaultLockGuidance"/>.</summary>
    [ObservableProperty] private string _unattendedTokenSteps = "";

    /// <summary>
    /// 1Password only: the desktop app must stay running, and recovering from a restart has an
    /// order to it. Shown on this tab as well as the setup page because this is the tab someone is
    /// on when saves start failing, and the setup page is not reachable from here.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDesktopAppRequirement))]
    private string _desktopAppRequirement = "";

    public bool HasDesktopAppRequirement => DesktopAppRequirement.Length > 0;

    /// <summary>
    /// Whether a backend of any kind is in use — including single use, which is why Disconnect
    /// binds to this rather than to <see cref="IsVaultConnected"/>. Leaving single use has to stay
    /// reachable, since it is the only thing that purges the configuration on demand.
    /// </summary>
    [ObservableProperty] private bool _isConnected;

    /// <summary>
    /// Whether the backend is an actual password manager, so there is a vault to sync with, rewrite,
    /// re-read or check.
    ///
    /// Every one of those controls needs a vault behind it, and in single use there is none — the
    /// store is this process's memory. They are shown disabled rather than hidden: a Settings tab
    /// whose contents change shape between modes makes the user wonder what else is missing,
    /// whereas greyed controls beside "Single use" say plainly which mode they are in and what it
    /// costs them.
    /// </summary>
    [ObservableProperty] private bool _isVaultConnected;

    /// <summary>Running on memory alone — see <see cref="VaultBackendKind.SingleUse"/>.</summary>
    [ObservableProperty] private bool _isSingleUse;

    /// <summary>
    /// Set once the user has asked to disconnect. Always asked, not only when something is
    /// unsaved: disconnecting drops every route and funnel the proxy is serving, so a client
    /// mid-request gets an error — that is worth one confirmation on its own.
    /// </summary>
    [ObservableProperty] private bool _isConfirmingDisconnect;

    /// <summary>Extra line on the disconnect confirmation when changes would actually be lost.</summary>
    [ObservableProperty] private string _disconnectWarning = "";

    /// <summary>Set once the user has asked to reload everything from the vault.</summary>
    [ObservableProperty] private bool _isConfirmingReinitialise;

    // ---- Vault integrity -------------------------------------------------------------------------

    /// <summary>Items in the vault the configuration does not account for. The user chooses what goes.</summary>
    public ObservableCollection<VaultOrphanItem> Orphans { get; } = [];

    /// <summary>Records whose vault item is missing. Rewriting restores them; dropping removes them.</summary>
    public ObservableCollection<VaultMissingItem> MissingItems { get; } = [];

    /// <summary>
    /// Everything else living in the vault. Listed so the check accounts for the whole vault rather
    /// than only the items this app can recognise — and so a RavensPort item someone renamed is
    /// visible somewhere. Never touched by anything automatic.
    /// </summary>
    public ObservableCollection<VaultItemEntry> OtherItems { get; } = [];

    public bool HasOtherItems => OtherItems.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIntegrityResult))]
    private string _integritySummary = "";

    [ObservableProperty] private bool _isCheckingIntegrity;

    public bool HasIntegrityResult => IntegritySummary.Length > 0;

    public bool HasOrphans => Orphans.Count > 0;
    public bool HasMissingItems => MissingItems.Count > 0;

    /// <summary>
    /// Keys are per endpoint and live on the row that owns them, so this tab only says where to
    /// find them. The counts make an empty install say something useful rather than pointing at
    /// two tabs that have nothing on them yet.
    /// </summary>
    public string KeyLocationSummary
    {
        get
        {
            var store = _configStoreCache.Current;
            var routes = store.Routes.Count;
            var funnels = store.McpFunnels.Count;

            return routes == 0 && funnels == 0
                ? "No endpoints yet. Add a route on the Routes tab (or a funnel on the MCP Funnel tab) "
                  + "and it is issued its own key."
                : $"{routes} route(s) and {funnels} funnel(s), each with its own key. "
                  + "Open the Routes or MCP Funnel tab and use the key controls on the row.";
        }
    }

    public SettingsViewModel(
        ConfigStoreCache configStoreCache,
        ActivityLog activityLog,
        VaultGateService gate,
        VaultSyncQueue syncQueue,
        VaultIntegrityService integrity,
        ProtonPassAuthenticator protonAuthenticator,
        ProxyConfigChangeNotifier proxyConfigChangeNotifier,
        McpSourceConnectionPool mcpSourceConnectionPool,
        OnePasswordSession onePasswordSession,
        IUiTimerFactory uiTimerFactory,
        IPlatformLauncher launcher,
        IFileSavePicker fileSavePicker)
    {
        _fileSavePicker = fileSavePicker;
        _onePasswordSession = onePasswordSession;
        _protonAuthenticator = protonAuthenticator;
        _configStoreCache = configStoreCache;
        _activityLog = activityLog;
        _gate = gate;
        _syncQueue = syncQueue;
        _integrity = integrity;
        _proxyConfigChangeNotifier = proxyConfigChangeNotifier;
        _mcpSourceConnectionPool = mcpSourceConnectionPool;
        _launcher = launcher;

        var settings = _configStoreCache.Current.Settings;
        _listenPort = settings.ListenPort;

        // Backing field, not the property: the generated setter runs OnMtlsEnabledChanged, and a
        // write here is loading what is already stored, not the user asking for a change.
        _mtlsEnabled = settings.MtlsEnabled;

        RefreshActivity();
        RefreshVaultStatus();
        RefreshCertificateExpiry();

        // One timer for both: the sync queue and the gate both change state from background
        // threads, and polling them on the UI thread's own tick avoids marshalling a stream of
        // events into a tab that is usually not even visible.
        _logTimer = uiTimerFactory.StartRepeating(TimeSpan.FromSeconds(2), () =>
        {
            RefreshActivity();
            RefreshVaultStatus();
        });
    }

    /// <summary>Raised after the user disconnects, so the shell can go back to the setup page.</summary>
    public event Action? Disconnected;

    // ---- Vault maintenance, and when it is allowed to run ------------------------------------
    //
    // Everything in this section is driven by the integrity check, which compares what is in
    // memory against what is in the vault and reports the difference two ways: items in the vault
    // that no record points at (orphans, offered with a Delete button), and records whose vault
    // item is gone (missing, offered with a Drop button).
    //
    // Both readings are only meaningful once the vault has actually been read. While a load is in
    // flight the store is empty or half-replaced, so the comparison inverts: every real item in
    // the vault looks orphaned, and every real record looks missing. The buttons beside those
    // lists delete things, and a user acting on that list would be deleting live credentials on
    // the strength of a picture that was never true.
    //
    // Deliberately scoped to this section rather than the whole tab. Disconnect, Sign out, the
    // listen port and the logs all stay reachable — none of them reads the integrity view, and
    // they are the controls someone needs when a vault is slow, locked, or wedged. Disabling the
    // tab wholesale would take away the recovery surface at exactly the moment it is wanted.

    /// <summary>
    /// Whether the vault-maintenance actions may run: there is a vault, the store has been loaded,
    /// and no load is in flight. Bound by the section's <c>IsEnabled</c> and enforced again on
    /// every command, so the guard does not depend on the UI honouring it.
    ///
    /// The vault test is not redundant with the UI hiding these in single use. Every one of them
    /// compares memory against a backend, and in single use the backend <em>is</em> memory — so
    /// "check the vault" would report a clean bill of health about nothing, and "re-initialise"
    /// would throw the session's configuration away to reload it from itself.
    /// </summary>
    public bool CanMaintainVault => IsVaultConnected && _configStoreCache.IsSettled;

    /// <summary>
    /// The inverse, for the explanation shown in the section's place — but only while there is a
    /// vault to be waiting on.
    ///
    /// Disconnecting empties the store and clears the loaded flag, which satisfies "not settled"
    /// just as a load in flight does. Without the connection test, the moment Disconnect was
    /// confirmed the tab announced "Still reading your vault" about a vault it had just let go of.
    /// </summary>
    public bool IsWaitingForVaultLoad => IsVaultConnected && !CanMaintainVault;

    /// <summary>
    /// Polled from the same timer as the rest of this tab rather than driven by an event. The load
    /// flag is written on a thread-pool thread, and this file already avoids marshalling a stream
    /// of background events into a tab that is usually not even visible.
    /// </summary>
    private void RefreshMaintenanceAvailability()
    {
        OnPropertyChanged(nameof(CanMaintainVault));
        OnPropertyChanged(nameof(IsWaitingForVaultLoad));

        // A bound IsEnabled greys the buttons; this is what actually stops them running. Without
        // it a command is still reachable by keyboard, by automation, and by a click that lands in
        // the same tick as the state change.
        CheckIntegrityCommand.NotifyCanExecuteChanged();
        DeleteOtherItemCommand.NotifyCanExecuteChanged();
        DeleteOrphanCommand.NotifyCanExecuteChanged();
        DeleteAllOrphansCommand.NotifyCanExecuteChanged();
        DropMissingRecordCommand.NotifyCanExecuteChanged();
        RewriteAllItemsCommand.NotifyCanExecuteChanged();
        WriteMissingItemsCommand.NotifyCanExecuteChanged();
        ReinitialiseCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Re-reads state the tray menu can also change, so the two never disagree. Called when
    /// the Settings tab is shown.
    /// </summary>
    public void Reload()
    {
        var settings = _configStoreCache.Current.Settings;
        ListenPort = settings.ListenPort;

        // Another vault can have been unlocked since this tab was last shown, and it carries its
        // own answer. OnMtlsEnabledChanged is a no-op on a value that already matches the store,
        // so this re-reads without claiming a restart is due.
        MtlsEnabled = settings.MtlsEnabled;

        // Routes and funnels can have been added on another tab since this one was last shown.
        OnPropertyChanged(nameof(KeyLocationSummary));

        if (StatusMessage == "Disconnected.")
        {
            StatusMessage = "Ready.";
        }

        RefreshVaultStatus();

        // Re-read on every visit rather than once. Two things move underneath it: another vault can
        // have been unlocked since this tab was last shown, and — for a certificate that is simply
        // sitting there — the remaining days count down without anything in this app happening.
        RefreshCertificateExpiry();
    }

    /// <summary>
    /// What the password manager is doing, in the two lines a user actually needs: which vault the
    /// configuration is in, and whether what they see on screen has reached it.
    /// </summary>
    private void RefreshVaultStatus()
    {
        var kind = _gate.Status.Selected;
        var manager = VaultLockGuidance.DisplayName(kind);
        var status = _gate.Status.For(kind);

        IsSingleUse = kind == VaultBackendKind.SingleUse;
        IsVaultConnected = kind is VaultBackendKind.OnePassword or VaultBackendKind.ProtonPass;
        IsConnected = kind != VaultBackendKind.None;
        UnattendedTokenSteps = VaultLockGuidance.UnattendedTokenSteps(kind);
        DesktopAppRequirement = VaultLockGuidance.DesktopAppRequirement(kind);

        // Computed from two things the timer refreshes rather than stored, so it has to be told.
        OnPropertyChanged(nameof(CanSignOutOfProtonPass));

        RefreshMaintenanceAvailability();

        if (IsSingleUse)
        {
            // Named on the tab, not merely implied by everything else being disabled. Someone who
            // set this up an hour ago needs to be able to tell at a glance that the routes on
            // screen are held in memory and go when the app does.
            PasswordManagerSummary = "Single use — no password manager.";
            PasswordManagerDetail =
                "This configuration is held in memory only. Disconnecting discards it, and so does "
                + "closing RavensPort. Connect a password manager from the setup page to keep one.";
            VaultSyncSummary = "Nothing is being saved anywhere, by design.";
            return;
        }

        if (!IsConnected)
        {
            PasswordManagerSummary = "Not connected to a password manager.";
            PasswordManagerDetail = "";
            VaultSyncSummary = "";
            return;
        }

        // The vault name is worth stating even when it is the default: once a user has pointed
        // RavensPort at a vault of their own, nothing else on screen says which one it went to.
        PasswordManagerSummary = $"{manager} — vault '{_gate.Selected.VaultName}'";

        PasswordManagerDetail = status?.ExePath is { Length: > 0 } path
            ? status.Version is { Length: > 0 } version ? $"{path}  (v{version})" : path
            : "";

        VaultSyncSummary = DescribeSync(manager);
    }

    private string DescribeSync(string manager)
    {
        if (!_configStoreCache.HasPendingChanges) return $"Everything is saved to {manager}.";

        return _syncQueue.State switch
        {
            VaultSyncState.WaitingForUnlock =>
                $"Waiting for {manager} — changes are in memory only and are lost if RavensPort exits first.",

            VaultSyncState.Failed =>
                $"{manager} refused the last save: {_syncQueue.LastError ?? "no reason given"}. Retrying.",

            _ => $"Saving to {manager}…",
        };
    }

    /// <summary>
    /// Set by the host: re-reads the vault and reloads the tabs. Held as a hook rather than a
    /// reference because the view model that owns that work depends on this one.
    /// </summary>
    public Func<Task>? ReloadFromVaultRequested { get; set; }

    /// <summary>
    /// Pushes now, for when the manager has just been unlocked — and when there is nothing to
    /// push, checks the other direction instead.
    ///
    /// That second half is the point. An item deleted in the password manager's own UI is
    /// invisible here until something re-reads the vault, so "sync now" on an app with no pending
    /// changes used to be a no-op that left a credential on screen the vault no longer had. The
    /// re-read drops it and queues the corrected configuration, which the push below then writes.
    /// </summary>
    public bool CanSyncNow => false;

    [RelayCommand(CanExecute = nameof(CanSyncNow))]
    private async Task SyncNowAsync()
    {
        if (!_configStoreCache.HasPendingChanges && ReloadFromVaultRequested is { } reload)
        {
            StatusMessage = "Checking the vault…";

            try
            {
                // Safe precisely because nothing is pending: there is no in-memory state a re-read
                // could throw away.
                await reload();
            }
            catch (Exception ex)
            {
                _activityLog.LogError("Could not re-read the vault", ex);
                StatusMessage = $"Could not read the vault: {ex.Message}";
                RefreshVaultStatus();
                return;
            }
        }

        if (!_configStoreCache.HasPendingChanges)
        {
            StatusMessage = _configStoreCache.LastLoadNotice ?? "Checked — the vault already has everything.";
            RefreshVaultStatus();
            return;
        }

        StatusMessage = "Saving to your password manager…";

        var saved = await _syncQueue.FlushAsync(TimeSpan.FromSeconds(30));

        StatusMessage = saved
            ? _configStoreCache.LastLoadNotice is { } notice ? $"Saved. {notice}" : "Saved."
            : _syncQueue.LastError ?? "Could not save — the password manager is locked or unavailable.";

        RefreshVaultStatus();
    }

    // ---- Integrity, rewrite, re-initialise -------------------------------------------------------

    /// <summary>
    /// Compares the vault against the configuration. Reports only — every repair below is a loss
    /// of something, so it is the user's to choose.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanMaintainVault))]
    private async Task CheckIntegrityAsync()
    {
        if (IsCheckingIntegrity) return;

        IsCheckingIntegrity = true;
        StatusMessage = "Checking the vault against the configuration…";

        try
        {
            var report = await _integrity.CheckAsync();

            Orphans.Clear();
            foreach (var orphan in report.Orphans) Orphans.Add(orphan);

            MissingItems.Clear();
            foreach (var missing in report.Missing) MissingItems.Add(missing);

            OtherItems.Clear();
            foreach (var other in report.Others) OtherItems.Add(other);

            IntegritySummary = report.Summary;
            StatusMessage = report.IsHealthy ? "Vault is healthy." : "Vault needs attention.";
        }
        catch (Exception ex)
        {
            _activityLog.LogError("Vault integrity check failed", ex);
            IntegritySummary = $"Could not check the vault: {ex.Message}";
            StatusMessage = ex.Message;
        }
        finally
        {
            IsCheckingIntegrity = false;
            OnPropertyChanged(nameof(HasOrphans));
            OnPropertyChanged(nameof(HasMissingItems));
            OnPropertyChanged(nameof(HasOtherItems));
        }
    }

    /// <summary>
    /// Deletes an item that is not RavensPort's. One at a time and never in bulk: the rest of the
    /// vault is the user's, and this app has no business sweeping it.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanMaintainVault))]
    private async Task DeleteOtherItemAsync(VaultItemEntry? item)
    {
        if (item is null) return;

        try
        {
            await _integrity.DeleteOtherItemAsync(item);
            OtherItems.Remove(item);
            OnPropertyChanged(nameof(HasOtherItems));

            StatusMessage = $"Deleted '{item.Title}'.";
        }
        catch (Exception ex)
        {
            _activityLog.LogError($"Could not delete '{item.Title}'", ex);
            StatusMessage = ex.Message;
        }
    }

    /// <summary>Deletes one item the check found. Per item, because each one is the user's data.</summary>
    [RelayCommand(CanExecute = nameof(CanMaintainVault))]
    private async Task DeleteOrphanAsync(VaultOrphanItem? orphan)
    {
        if (orphan is null) return;

        try
        {
            await _integrity.DeleteItemsAsync([orphan]);
            Orphans.Remove(orphan);
            OnPropertyChanged(nameof(HasOrphans));

            StatusMessage = $"Deleted '{orphan.Title}'.";
        }
        catch (Exception ex)
        {
            _activityLog.LogError($"Could not delete '{orphan.Title}'", ex);
            StatusMessage = ex.Message;
        }
    }

    [RelayCommand(CanExecute = nameof(CanMaintainVault))]
    private async Task DeleteAllOrphansAsync()
    {
        if (Orphans.Count == 0) return;

        var doomed = Orphans.ToList();

        try
        {
            var deleted = await _integrity.DeleteItemsAsync(doomed);

            Orphans.Clear();
            OnPropertyChanged(nameof(HasOrphans));

            StatusMessage = $"Deleted {deleted} item(s).";
        }
        catch (Exception ex)
        {
            // Partial success is normal here — each delete is its own call. Re-check rather than
            // guess which ones went.
            _activityLog.LogError("Could not delete every orphaned item", ex);
            StatusMessage = $"{ex.Message} Run the check again to see what is left.";
            await CheckIntegrityAsync();
        }
    }

    /// <summary>
    /// Drops a record whose item is gone. The destructive answer to "missing" — rewriting is the
    /// other one, and it is the better one while the secret is still in memory.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanMaintainVault))]
    private async Task DropMissingRecordAsync(VaultMissingItem? missing)
    {
        if (missing is null) return;

        try
        {
            await _integrity.DropRecordsAsync([missing]);
            MissingItems.Remove(missing);
            OnPropertyChanged(nameof(HasMissingItems));

            // Routes and funnels can have gone with it.
            _proxyConfigChangeNotifier.Rebuild();
            RecordsDropped?.Invoke();

            StatusMessage = $"Removed '{missing.Title}' from the configuration.";
        }
        catch (Exception ex)
        {
            _activityLog.LogError($"Could not remove '{missing.Title}'", ex);
            StatusMessage = ex.Message;
        }
    }

    /// <summary>
    /// Writes every item and the configuration again from what is in memory — the way back from a
    /// vault that has been edited by hand.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanMaintainVault))]
    private async Task RewriteAllItemsAsync()
    {
        StatusMessage = "Writing every item to your password manager…";

        var written = await _integrity.RewriteAllItemsAsync(TimeSpan.FromMinutes(2));

        StatusMessage = written
            ? "Wrote every item and the configuration to the vault."
            : _syncQueue.LastError ?? "Could not write to the password manager.";

        RefreshVaultStatus();

        if (written && HasIntegrityResult) await CheckIntegrityAsync();
    }

    /// <summary>
    /// Puts back what the vault is missing, from memory — the answer to a missing item that keeps
    /// it. Costs one write per absent item rather than rewriting the whole vault.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanMaintainVault))]
    private async Task WriteMissingItemsAsync()
    {
        StatusMessage = "Writing the missing items to your password manager…";

        var written = await _integrity.WriteMissingToVaultAsync(TimeSpan.FromMinutes(2));

        StatusMessage = written
            ? "Wrote them to the vault."
            : _syncQueue.LastError ?? "Could not write to the password manager.";

        RefreshVaultStatus();

        if (written) await CheckIntegrityAsync();
    }

    /// <summary>Raised after records were dropped, so the other tabs can rebuild their rows.</summary>
    public event Action? RecordsDropped;

    /// <summary>
    /// Set by the host: empties the in-memory configuration and loads it again from the vault.
    /// </summary>
    public Func<Task>? ReinitialiseRequested { get; set; }

    /// <summary>
    /// Throws away everything in memory and rebuilds it from the vault — the escape hatch for a
    /// configuration that has drifted, and the way to pick up a vault edited elsewhere.
    ///
    /// Asked first, because it is a real interruption: every route and funnel is rebuilt, so a
    /// client mid-request sees an error, and anything not yet saved is gone.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanMaintainVault))]
    private async Task ReinitialiseAsync()
    {
        if (!IsConfirmingReinitialise)
        {
            IsConfirmingReinitialise = true;
            return;
        }

        IsConfirmingReinitialise = false;

        if (ReinitialiseRequested is not { } reinitialise)
        {
            StatusMessage = "Nothing to re-initialise from yet.";
            return;
        }

        StatusMessage = "Reloading everything from your password manager…";

        try
        {
            await reinitialise();
            StatusMessage = _configStoreCache.LastLoadNotice ?? "Reloaded from the vault.";
        }
        catch (Exception ex)
        {
            _activityLog.LogError("Could not re-initialise from the vault", ex);
            StatusMessage = $"Could not reload: {ex.Message}";
        }

        RefreshVaultStatus();
    }

    [RelayCommand]
    private void CancelReinitialise()
    {
        IsConfirmingReinitialise = false;
        StatusMessage = "Left as it is.";
    }

    /// <summary>
    /// Stops using the password manager and empties the store, which leaves the proxy serving
    /// nothing until one is connected again — and lets a different vault be connected in its
    /// place, which is how one install keeps several separate sets of credentials and routes.
    ///
    /// Tries to save first: the manager is often unlocked by now — the user may have unlocked it
    /// for something else entirely — and discarding changes that could simply have been written
    /// would be a poor way to find that out. Then it always asks, because disconnecting takes down
    /// every route the proxy is serving whether or not anything was pending.
    /// </summary>
    [RelayCommand]
    private async Task DisconnectAsync()
    {
        if (!IsConfirmingDisconnect)
        {
            if (_configStoreCache.HasPendingChanges)
            {
                StatusMessage = "Saving pending changes before disconnecting…";
                await _syncQueue.FlushAsync(TimeSpan.FromSeconds(15));
            }

            DisconnectWarning = _configStoreCache.HasPendingChanges
                ? "Some changes have still not reached the vault. Disconnecting discards them, and any "
                  + "credential whose token was refreshed since the last save will need reconnecting."
                : "";

            IsConfirmingDisconnect = true;
            StatusMessage = "Confirm to disconnect.";
            return;
        }

        IsConfirmingDisconnect = false;
        DisconnectWarning = "";

        // Nothing may be mid-write when the backend changes underneath it. A save resolves its
        // target as it runs, so one still in flight here would finish against whichever vault is
        // connected next — which is how a user's Proton Pass items were deleted by a configuration
        // that belonged to 1Password.
        if (!await _syncQueue.WaitForQuietAsync(TimeSpan.FromSeconds(30)))
        {
            IsConfirmingDisconnect = false;
            StatusMessage = "A save to the vault is still running. Wait for it to finish, then try again.";
            return;
        }

        var wasSingleUse = IsSingleUse;

        // The service-account token goes with the connection it opened. Keeping it would leave the
        // next Connect silently authenticating as a service account the user thought they had just
        // disconnected — and the whole promise of this mode is that the credential lives no longer
        // than the session using it.
        //
        // Only the in-memory copy. A token the user deliberately saved behind Windows Hello is not
        // this session's to throw away — disconnecting is about which vault is in use, and quietly
        // destroying a stored credential would make it impossible to disconnect without also having
        // to fetch the token again. Forgetting it is its own button, on the setup page.
        _onePasswordSession.Clear();
        NativeCliRunner.ResetInitialization();

        _gate.Disconnect();
        await TearDownAsync(
            wasSingleUse
                ? "VAULT left single use from the Settings tab — the in-memory configuration has been purged"
                : "VAULT disconnected from the Settings tab — the proxy is serving nothing until reconnected",
            wasSingleUse ? "Single-use configuration purged." : "Disconnected.");
    }

    [RelayCommand]
    private void CancelDisconnect()
    {
        IsConfirmingDisconnect = false;
        DisconnectWarning = "";
        StatusMessage = "Left connected.";
    }

    /// <summary>
    /// Whether to offer signing out of Proton Pass — only when Proton Pass is the backend in use,
    /// since it is the only one whose session RavensPort owns.
    /// </summary>
    public bool CanSignOutOfProtonPass =>
        IsConnected && _gate.Status.Selected == VaultBackendKind.ProtonPass;

    [ObservableProperty] private bool _isConfirmingSignOut;

    /// <summary>
    /// Ends RavensPort's Proton Pass session outright, rather than only letting go of the vault.
    ///
    /// Stronger than Disconnect, and asked separately for that reason. Disconnect can be undone by
    /// choosing the manager again; this cannot — it tells Proton to invalidate the session, deletes
    /// it, and forgets the key. Coming back means signing in through the browser again.
    /// </summary>
    [RelayCommand]
    private async Task SignOutOfProtonPassAsync()
    {
        if (!IsConfirmingSignOut)
        {
            if (_configStoreCache.HasPendingChanges)
            {
                StatusMessage = "Saving pending changes before signing out…";
                await _syncQueue.FlushAsync(TimeSpan.FromSeconds(15));
            }

            IsConfirmingSignOut = true;
            StatusMessage = "Confirm to sign out of Proton Pass. You will need to sign in again through your browser.";
            return;
        }

        IsConfirmingSignOut = false;

        // Nothing may be mid-write when the backend changes underneath it. A save resolves its
        // target as it runs, so one still in flight here would finish against whichever vault is
        // connected next — which is how a user's Proton Pass items were deleted by a configuration
        // that belonged to 1Password.
        if (!await _syncQueue.WaitForQuietAsync(TimeSpan.FromSeconds(30)))
        {
            IsConfirmingSignOut = false;
            StatusMessage = "A save to the vault is still running. Wait for it to finish, then try again.";
            return;
        }

        // Ends the session and disconnects the gate in one step — see ProtonPassAuthenticator.
        await _protonAuthenticator.SignOutAsync();

        await TearDownAsync(
            "VAULT signed out of Proton Pass from the Settings tab",
            "Signed out of Proton Pass.");
    }

    [RelayCommand]
    private void CancelSignOut()
    {
        IsConfirmingSignOut = false;
        StatusMessage = "Left signed in.";
    }

    /// <summary>
    /// Everything that has to happen once the app no longer has a vault, whichever way it got
    /// there. Shared so a sign-out cannot quietly skip a step a disconnect does.
    /// </summary>
    /// <summary>
    /// Returns the app to its first-run state: nothing of the vault being left survives anywhere.
    ///
    /// Every list here is one the user could otherwise still be looking at — or worse, still
    /// editing — after disconnecting. The store is emptied, the proxy is rebuilt from it, the four
    /// tabs are rebuilt from it, and the integrity results are dropped. Anything skipped is a row
    /// belonging to one vault presented under another, which is exactly what makes a switch between
    /// password managers, or between two vaults in one, look like it half-worked.
    /// </summary>
    private async Task TearDownAsync(string logMessage, string statusMessage)
    {
        await _configStoreCache.ResetAsync();

        // Drop active MCP sessions before rebuilding the proxy, so they don't hit 403s on their
        // background transports when the proxy routes disappear and throw unobserved exceptions.
        await _mcpSourceConnectionPool.InvalidateAllAsync();

        // Routes come from the store, so the proxy has to be rebuilt from the now-empty one —
        // otherwise it would keep forwarding with the credentials of a vault this app has just
        // disconnected from.
        _proxyConfigChangeNotifier.Rebuild();

        _activityLog.Log(logMessage);

        // Credentials, Routes and MCP Funnel hold their own row collections built from the store.
        // Emptying the store does not empty those, and only Routes, Funnel and Settings rebuild on
        // a tab switch — so without this the Credentials tab kept showing the disconnected vault's
        // credentials until something else happened to reload it.
        _rebuildTabs?.Invoke();

        Orphans.Clear();
        MissingItems.Clear();
        OtherItems.Clear();
        IntegritySummary = "";

        // Confirmation flags too: leaving one set means the next visit to this tab opens already
        // asking a question about a vault that is no longer connected.
        IsConfirmingDisconnect = false;
        IsConfirmingReinitialise = false;
        IsConfirmingSignOut = false;
        DisconnectWarning = "";

        StatusMessage = statusMessage;
        RefreshVaultStatus();

        Disconnected?.Invoke();
    }

    /// <summary>
    /// Rebuilds all four tabs from the store. Supplied by the host rather than resolved here,
    /// because the view models this needs are the ones that own this one — see
    /// <c>VaultStatusViewModel.ReloadTabs</c>.
    /// </summary>
    private Action? _rebuildTabs;

    /// <summary>Wired at startup, once every tab's view model exists.</summary>
    public void UseTabRebuilder(Action rebuildTabs) => _rebuildTabs = rebuildTabs;

    private void RefreshActivity()
    {
        var lines = _activityLog.GetRecent(VisibleLogLines);
        RecentActivity = lines.Count == 0
            ? "(no activity yet)"
            : string.Join(Environment.NewLine, lines);
    }


    [ObservableProperty] private bool _isMtlsRestartRequired;

    /// <summary>Whether there is a certificate at all, and so anything to say about its dates.</summary>
    [ObservableProperty] private bool _hasCertificate;

    /// <summary>When the stored certificate stops being accepted, in words. Empty when there is none.</summary>
    [ObservableProperty] private string _certificateExpirySummary = "";

    /// <summary>
    /// True once the certificate is expired, or close enough to it to be worth colouring. Has to
    /// appear well before the day it stops working rather than on it: rotating means exporting,
    /// visiting every client that holds a copy, and restarting this app, and none of that is
    /// something to discover at the moment the proxy starts turning callers away.
    /// </summary>
    [ObservableProperty] private bool _isCertificateExpiryUrgent;

    /// <summary>
    /// Read out of the stored PFX rather than remembered from the moment it was generated: the
    /// certificate outlives the process that minted it, and every later session has to be able to
    /// answer the same question.
    ///
    /// Called where the answer can have changed — construction, tab switch, generating, and the
    /// switch minting one — and deliberately not on the two-second timer that drives the rest of
    /// this tab. Each call parses a PFX and imports an RSA key into a CryptoAPI container, which is
    /// not something to do thirty times a minute for a date that moves once a day.
    /// </summary>
    private void RefreshCertificateExpiry()
    {
        var settings = _configStoreCache.Current.Settings;
        var pfx = settings.MtlsClientCertificatePfx;

        HasCertificate = !string.IsNullOrWhiteSpace(pfx);
        if (!HasCertificate)
        {
            CertificateExpirySummary = "";
            IsCertificateExpiryUrgent = false;
            return;
        }

        try
        {
            // Disposed: X509Certificate2 holds an OS key handle, and the container is only removed
            // when the last one closes. A tab that leaked one per visit would leave key files
            // behind for the life of the process.
            using var certificate = MtlsCertificateFactory.Load(pfx!, settings.MtlsClientCertificatePassword);

            var remaining = certificate.NotAfter.ToUniversalTime() - DateTime.UtcNow;
            var expiresOn = certificate.NotAfter.ToLocalTime().ToString("d MMMM yyyy");

            if (remaining <= TimeSpan.Zero)
            {
                IsCertificateExpiryUrgent = true;
                CertificateExpirySummary =
                    $"This certificate expired on {expiresOn}. The proxy now refuses every caller that "
                    + "presents it — including its own MCP funnel — and the failure happens during the TLS "
                    + "handshake, so clients see a dropped connection rather than an error. Generate a new "
                    + "certificate, export it, install it on every client, and restart RavensPort.";
                return;
            }

            // Ceiling, not truncation: with eleven hours left, "0 days" reads as already gone and
            // "1 day" is the answer to the question actually being asked, which is how long there is.
            var days = (int)Math.Ceiling(remaining.TotalDays);
            var howLong = days == 1 ? "1 day" : $"{days} days";

            IsCertificateExpiryUrgent = remaining <= MtlsCertificateFactory.ExpiryWarningWindow;
            CertificateExpirySummary = IsCertificateExpiryUrgent
                ? $"This certificate expires on {expiresOn} — {howLong} from now. After that the proxy "
                  + "refuses every client presenting it. Replacing it means installing the new one "
                  + "everywhere this one is used, so start before the day it stops working."
                : $"This certificate expires on {expiresOn}, {howLong} from now.";
        }
        catch (Exception ex)
        {
            // A certificate that cannot be opened is the same problem wearing a different face: the
            // app will not be able to present it either, so it is said in the same place and in the
            // same colour rather than swallowed into an empty line.
            IsCertificateExpiryUrgent = true;
            CertificateExpirySummary = $"The stored certificate could not be read: {ex.Message}";
        }
    }

    partial void OnMtlsEnabledChanged(bool value)
    {
        if (_configStoreCache.Current.Settings.MtlsEnabled == value) return;

        _ = PersistMtlsAsync(value);
    }

    private async Task PersistMtlsAsync(bool value)
    {
        var minted = false;

        try
        {
            await _configStoreCache.MutateAsync(store =>
            {
                // Switching mTLS on with no certificate would leave the app unable to start at
                // all: the listener has nothing to present, and there is no sane fallback —
                // quietly binding plain HTTP instead would tell the user their proxy is
                // certificate-protected when anything on the machine can call it. Generating one
                // here makes the switch mean what it says on the first click; the user still has
                // to export it to whatever will be calling the proxy, which the status says.
                //
                // No password is asked for here, because this click is not the one that offers a
                // box to type it in. The certificate takes the built-in password and the status
                // below points at Generate, which is where a password of the user's own is set.
                if (value && string.IsNullOrWhiteSpace(store.Settings.MtlsClientCertificatePfx))
                {
                    store.Settings.MtlsClientCertificatePfx = MtlsCertificateFactory.GenerateClientCertificatePfx();
                    store.Settings.MtlsClientCertificatePassword = "";
                    minted = true;
                }

                store.Settings.MtlsEnabled = value;
            });

            IsMtlsRestartRequired = true;
            RefreshCertificateExpiry();
            StatusMessage = value
                ? minted
                    ? "mTLS enabled, and a certificate was generated so the listener has one to present. It carries the built-in password; use Generate new certificate to set your own before exporting. Restart RavensPort for the change to take effect."
                    : "mTLS enabled. Restart RavensPort for the change to take effect."
                : "mTLS disabled. Restart RavensPort for the change to take effect.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not save mTLS setting: {ex.Message}";
        }
    }

    [ObservableProperty] private bool _isConfirmingGenerateCertificate;

    /// <summary>
    /// What the next generated PFX will be encrypted with. Only ever holds a password the user is
    /// in the middle of typing: it is cleared the moment the certificate is written, so nothing
    /// keeps it alive in a view model that outlives the dialog. The stored copy lives in the vault
    /// beside the certificate, because the app has to reopen its own PFX at every start.
    /// </summary>
    [ObservableProperty] private string _newCertificatePassword = "";

    [RelayCommand]
    private async Task GenerateMtlsCertificateAsync()
    {
        if (!IsConfirmingGenerateCertificate)
        {
            IsConfirmingGenerateCertificate = true;
            NewCertificatePassword = "";
            StatusMessage = "Choose a password for the new certificate, then confirm. You will need to install the new one and restart RavensPort.";
            return;
        }

        // Required rather than defaulted, because an empty box has to mean something and both
        // readings are worse than asking again: minting with the built-in password would hand back
        // a certificate whose password is not the one the user thinks they set, and minting with no
        // password at all produces a PFX that Windows' certificate import and curl's Schannel
        // backend both refuse. The box is right there; a second click costs nothing.
        var password = NewCertificatePassword;
        if (string.IsNullOrEmpty(password))
        {
            StatusMessage = "Enter a password for the new certificate. Clients will need it to load the exported file.";
            return;
        }

        IsConfirmingGenerateCertificate = false;

        try
        {
            var pfx = MtlsCertificateFactory.GenerateClientCertificatePfx(password);
            await _configStoreCache.MutateAsync(store =>
            {
                store.Settings.MtlsClientCertificatePfx = pfx;
                store.Settings.MtlsClientCertificatePassword = password;
            });

            // The same banner the switch raises, for the same reason: KestrelMtlsState settles the
            // certificate once, before Kestrel binds, so the running process is still presenting
            // and demanding the old one. Until the restart, every client that installs the new
            // certificate is refused — which looks like the export was broken rather than like a
            // restart being owed.
            IsMtlsRestartRequired = true;
            RefreshCertificateExpiry();

            StatusMessage = "New client certificate generated. Export it, install it on every client, and restart RavensPort — until then the proxy still demands the old certificate.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not generate certificate: {ex.Message}";
        }
        finally
        {
            NewCertificatePassword = "";
        }
    }

    [RelayCommand]
    private void CancelGenerateCertificate()
    {
        IsConfirmingGenerateCertificate = false;
        NewCertificatePassword = "";
        StatusMessage = "Left current certificate intact.";
    }

    [RelayCommand]
    private async Task ExportMtlsCertificateAsync()
    {
        var pfx = _configStoreCache.Current.Settings.MtlsClientCertificatePfx;
        if (string.IsNullOrWhiteSpace(pfx))
        {
            StatusMessage = "No certificate has been generated yet.";
            return;
        }

        // Asked for, rather than dropped on the Desktop. The file has to end up wherever the thing
        // that will call the proxy can read it, and that is rarely the Desktop -- and the Desktop
        // itself may be redirected into OneDrive, which is a synced folder this credential has no
        // business being copied into without the user saying so.
        var path = await _fileSavePicker.PickSavePathAsync(
            "Export client certificate",
            "RavensPort_ClientCert.pfx",
            "pfx",
            "Certificate");

        if (path is null)
        {
            StatusMessage = "Export cancelled. The certificate is still stored in the vault.";
            return;
        }

        try
        {
            File.WriteAllBytes(path, Convert.FromBase64String(pfx));
            // The password is deliberately not echoed here. The status line is the one part of this
            // window that is read aloud in a screen share and captured in a screenshot of an
            // unrelated problem, and a password printed there outlives the export by however long
            // that image does.
            StatusMessage = $"Certificate saved to {path}. It opens with the password you chose when it was generated.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not export certificate: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task SavePortAsync()
    {
        if (ListenPort is < 1 or > 65535)
        {
            StatusMessage = "Listen port must be between 1 and 65535.";
            return;
        }

        await _configStoreCache.MutateAsync(store => store.Settings.ListenPort = ListenPort);
        StatusMessage = "Saved. Restart RavensPort for the new port to take effect.";
    }

    [RelayCommand]
    private async Task OpenErrorLogAsync()
    {
        if (!File.Exists(_activityLog.ErrorLogPath))
        {
            StatusMessage = "No error log yet — nothing has failed.";
            return;
        }
        await OpenInShellAsync(_activityLog.ErrorLogPath);
    }

    [RelayCommand]
    private async Task OpenActivityLogAsync()
    {
        if (!File.Exists(_activityLog.CurrentLogPath))
        {
            StatusMessage = "No activity log file yet.";
            return;
        }
        await OpenInShellAsync(_activityLog.CurrentLogPath);
    }

    [RelayCommand]
    private Task OpenLogFolderAsync() => OpenInShellAsync(_activityLog.LogDirectory);

    [RelayCommand]
    private void PruneLogs()
    {
        var deleted = _activityLog.PruneAll();
        StatusMessage = deleted == 0
            ? "Nothing to prune — only the current log exists."
            : $"Pruned {deleted} log file(s). The current activity log was kept.";
    }

    private async Task OpenInShellAsync(string path)
    {
        try
        {
            await _launcher.OpenPathAsync(path);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not open '{path}': {ex.Message}";
        }
    }
}
