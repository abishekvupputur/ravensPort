using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RavensPort.UI.Services;
using RavensPort.Core.Storage;
using RavensPort.Core.Vault;

namespace RavensPort.UI.ViewModels;

/// <summary>
/// The banner above the tabs: whether everything is in the vault, and if not, what that means.
///
/// Its whole job is to keep one promise honest. Edits and token refreshes succeed immediately
/// whether or not the password manager is reachable, so without this the UI would show changes as
/// applied while the vault had never heard of them — and exiting would lose them with no warning.
/// </summary>
public sealed partial class VaultStatusViewModel : ObservableObject
{
    private readonly ConfigStoreCache _configStoreCache;
    private readonly VaultSyncQueue _syncQueue;
    private readonly VaultGateService _gate;
    private readonly IUiDispatcher _uiDispatcher;

    private readonly CredentialsViewModel _credentials;
    private readonly RoutesViewModel _routes;
    private readonly McpFunnelViewModel _funnels;
    private readonly SettingsViewModel _settings;

    public VaultStatusViewModel(
        ConfigStoreCache configStoreCache,
        VaultSyncQueue syncQueue,
        VaultGateService gate,
        CredentialsViewModel credentials,
        RoutesViewModel routes,
        McpFunnelViewModel funnels,
        SettingsViewModel settings,
        IUiDispatcher uiDispatcher)
    {
        _configStoreCache = configStoreCache;
        _syncQueue = syncQueue;
        _gate = gate;
        _credentials = credentials;
        _routes = routes;
        _funnels = funnels;
        _settings = settings;
        _uiDispatcher = uiDispatcher;

        // Both fire from thread-pool threads — the sync pump and the refresh loop — so every
        // touch of a bound property has to be marshalled. A UI framework throws on a cross-thread
        // property change, and it would do it from inside a background service where nothing is
        // watching.
        _syncQueue.StateChanged += _ => _uiDispatcher.Post(Apply);
        _configStoreCache.PendingChanged += () => _uiDispatcher.Post(Apply);

        Apply();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDegraded))]
    [NotifyPropertyChangedFor(nameof(CanSyncNow))]
    [NotifyCanExecuteChangedFor(nameof(SyncNowCommand))]
    private bool _hasPendingChanges;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDegraded))]
    [NotifyPropertyChangedFor(nameof(IsWaitingForUnlock))]
    private VaultSyncState _state = VaultSyncState.Synced;

    [ObservableProperty] private string _headline = "";
    [ObservableProperty] private string _detail = "";

    /// <summary>The per-manager advice, shown behind "How do I stop this happening?".</summary>
    [ObservableProperty] private string _guidance = "";

    /// <summary>
    /// What the last load changed on its own — a credential dropped because its vault item was
    /// deleted. Its own banner, because it is news about the configuration rather than a state the
    /// user has to act on, and it must not disappear the moment the sync catches up.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    private string _notice = "";

    public bool HasNotice => Notice.Length > 0;

    [RelayCommand]
    private void DismissNotice()
    {
        _configStoreCache.DismissLoadNotice();
        Apply();
    }

    /// <summary>
    /// Shown only while something is actually unsaved. A banner that appeared for a syncing state
    /// nobody needs to act on would train the user to ignore it.
    /// </summary>
    public bool IsDegraded => HasPendingChanges && State != VaultSyncState.Syncing;

    /// <summary>
    /// Whether the per-manager "how do I stop this happening" advice is worth showing. Both states
    /// it covers are the manager being unavailable rather than the vault objecting to the content,
    /// and the advice — keep it unlocked longer, allow the integration — answers both.
    /// </summary>
    public bool IsWaitingForUnlock =>
        State is VaultSyncState.WaitingForUnlock or VaultSyncState.AuthorizationDeclined;

    /// <summary>True while a "save now" the user asked for is still running.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSyncNow))]
    [NotifyCanExecuteChangedFor(nameof(SyncNowCommand))]
    private bool _isSyncingNow;

    /// <summary>
    /// Whether "save now" has anything to do and anywhere to put it.
    ///
    /// This was hard-coded to false as an interim guard while the cross-vault destruction bugs were
    /// being fixed, and left that way — which made the app unusable with a password manager the user
    /// keeps locked. Once the manager had refused a write there was no way to ask for another
    /// attempt, so an unlock later in the day did nothing until the vault was disconnected and
    /// reconnected, and everything pending was lost on exit.
    ///
    /// The guard is now the specific one it should always have been: the store must have come from
    /// the vault it is about to be written to. That is the condition the sync queue itself refuses
    /// on, so enabling the button in that state would only produce a press that silently did
    /// nothing — the way out of a switched vault is "discard and reload", which sits beside it.
    /// </summary>
    public bool CanSyncNow =>
        HasPendingChanges
        && !IsSyncingNow
        && !_configStoreCache.IsFromAnotherVault
        && _gate.Status.Selected != VaultBackendKind.None;

    /// <summary>
    /// Pushes now, for when the user has just unlocked their manager.
    ///
    /// Long timeout because this is the one save the user is watching: it can sit on an unlock
    /// prompt, and for 1Password it may also have to rebuild a connection the desktop app
    /// invalidated while it was locked — which is another prompt, in series with the first.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSyncNow))]
    private async Task SyncNowAsync()
    {
        IsSyncingNow = true;

        try
        {
            // Off the dispatcher: FlushAsync blocks on vault I/O, and running it inline would
            // freeze the window for as long as the password manager took to answer.
            await Task.Run(() => _syncQueue.FlushAsync(TimeSpan.FromMinutes(2)));
        }
        finally
        {
            IsSyncingNow = false;
            Apply();
        }
    }

    /// <summary>
    /// Discards in-memory state and re-reads the vault — the way out of a secret edited in the
    /// password manager's own UI, which nothing here can be notified about.
    /// </summary>
    [RelayCommand]
    public async Task ReloadFromVaultAsync()
    {
        await _configStoreCache.ReloadAsync();

        ReloadTabs();
        Apply();
    }

    /// <summary>
    /// Loads the store of a password manager that has just been connected again after a
    /// disconnect. Separate from <see cref="ReloadFromVaultAsync"/> because the cache was reset
    /// rather than merely stale, so this is a first load — key backfill and all.
    /// </summary>
    public async Task ReconnectAsync()
    {
        // Off the dispatcher: this is vault I/O, and it can sit on an unlock prompt.
        await Task.Run(() => _configStoreCache.InitializeAsync());

        ReloadTabs();
        Apply();
    }

    /// <summary>
    /// Rebuilds all four tabs from the store. Public because the first load is not driven from
    /// here: the tabs are constructed while the vault is still locked, so their rows are built
    /// from an empty store and only the load that follows can fill them in.
    /// </summary>
    public void ReloadTabs()
    {
        _credentials.Reload();
        _routes.Reload();
        _funnels.Reload();
        _settings.Reload();
    }

    private void Apply()
    {
        HasPendingChanges = _configStoreCache.HasPendingChanges;
        State = _syncQueue.State;
        Notice = _configStoreCache.LastLoadNotice ?? "";

        // Explicitly, because two of the three things CanSyncNow reads are not observable — the
        // cache's view of which vault the store came from, and which backend the gate has settled
        // on. Both move without HasPendingChanges changing, and a button that stayed disabled
        // through a reconnect would be the original bug wearing a different hat.
        OnPropertyChanged(nameof(CanSyncNow));
        SyncNowCommand.NotifyCanExecuteChanged();

        var manager = VaultLockGuidance.DisplayName(_gate.Status.Selected);

        if (!HasPendingChanges)
        {
            Headline = "";
            Detail = "";
            Guidance = "";
            return;
        }

        Guidance = VaultLockGuidance.StayingUnlockedSteps(_gate.Status.Selected);

        Headline = State switch
        {
            VaultSyncState.WaitingForUnlock => $"Waiting for {manager} — your changes are not saved yet.",
            VaultSyncState.AuthorizationDeclined => $"{manager} was not authorized — your changes are not saved yet.",
            VaultSyncState.Failed => $"{manager} refused the last save.",
            _ => "Saving to your password manager…",
        };

        // The consequence, not just the state. "Not saved" alone reads as a spinner; what the user
        // needs to know is that quitting now throws the changes away.
        Detail = State switch
        {
            VaultSyncState.WaitingForUnlock =>
                $"Unlock {manager} and they will be saved automatically — or press \"I've unlocked "
                + "it — save now\" to do it straight away. "
                + "Everything keeps working in the meantime — but if RavensPort exits first, these "
                + "changes are lost, and any credential whose token was refreshed will need reconnecting."
                + PendingFor(),

            // Says outright that nothing is happening, because nothing is. Repeating "they will be
            // saved automatically" here would be a lie the user could only discover by quitting —
            // this is the one state where the button is the only thing that saves their work.
            VaultSyncState.AuthorizationDeclined =>
                $"Nothing more will be tried automatically — each attempt asks {manager} for "
                + "permission again, and you have already been asked. Unlock it whenever suits you, "
                + "then press \"I've unlocked it — save now\". "
                + "Everything keeps working in the meantime — but if RavensPort exits first, these "
                + "changes are lost, and any credential whose token was refreshed will need reconnecting."
                + PendingFor(),

            VaultSyncState.Failed =>
                (_syncQueue.LastError ?? "The vault rejected the change.")
                + " Your changes are still here and will be retried.",

            _ => "",
        };
    }

    private string PendingFor()
    {
        if (_configStoreCache.PendingSince is not { } since) return "";

        var waiting = DateTimeOffset.UtcNow - since;
        if (waiting < TimeSpan.FromMinutes(1)) return "";

        var span = waiting.TotalHours >= 1
            ? $"{(int)waiting.TotalHours} hour(s)"
            : $"{(int)waiting.TotalMinutes} minute(s)";

        return $" Waiting for {span}.";
    }
}
