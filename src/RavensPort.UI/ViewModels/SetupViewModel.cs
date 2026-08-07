using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RavensPort.UI.Services;
using RavensPort.Core.Diagnostics;
using RavensPort.Core.Models;
using RavensPort.Core.Vault;

namespace RavensPort.UI.ViewModels;

/// <summary>
/// The only page the app shows when it cannot reach a password manager.
///
/// It is a whole page rather than a dialog because there is genuinely nothing else to display:
/// every credential, route, key, and setting lives in the vault, so without one the tabs would be
/// four empty grids whose every button fails.
/// </summary>
public sealed partial class SetupViewModel(
    VaultGateService gate,
    ProtonPassSession protonSession,
    ProtonPassAuthenticator protonAuthenticator,
    ActivityLog activityLog,
    OnePasswordSession onePasswordSession,
    IServiceTokenProtector tokenProtector,
    IClipboardService clipboard,
    IPlatformLauncher launcher,
    IHelloConsentPrompt helloConsent) : ObservableObject
{
    /// <summary>The pre-vault store, kept only so the page can offer to delete it.</summary>
    private static readonly string LegacyStorePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "RavensPort",
        "store.dat");

    public ObservableCollection<ManagerCardViewModel> Managers { get; } = [];

    [ObservableProperty] private string _statusMessage = "Checking for a password managerâ€¦";
    [ObservableProperty] private bool _isBusy;

    /// <summary>Whether a fresh manager check can run without interrupting another setup flow.</summary>
    public bool CanCheck => !IsBusy && !IsSigningIn;

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanCheck));

    /// <summary>Set when both managers qualify and neither can be shown to hold the configuration.</summary>
    [ObservableProperty] private bool _needsAChoice;

    /// <summary>Set while the user has deliberately disconnected, so the page says so rather than
    /// presenting itself as a first-run setup.</summary>
    [ObservableProperty] private bool _isDisconnected;

    /// <summary>Set when the port could not be bound, which is fixable without a working proxy.</summary>
    [ObservableProperty] private bool _hasPortConflict;
    [ObservableProperty] private string _listenPort = "5559";

    [ObservableProperty] private bool _hasLegacyStore;

    /// <summary>Raised when the gate opens, so the host can start the proxy.</summary>
    public event Func<Task>? ReadyToStart;

    /// <summary>Set by the host when a vault connected after a disconnect could not be read.</summary>
    public void ReportReconnectFailure(string message) =>
        StatusMessage = $"Connected, but the vault could not be read: {message}";

    /// <summary>Set by the host when binding the listen port failed.</summary>
    public void ReportPortConflict(int port, string message)
    {
        ListenPort = port.ToString();
        HasPortConflict = true;
        StatusMessage = message;
    }

    /// <summary>
    /// Looks at what is installed, and stops there.
    ///
    /// Deliberately a discovery probe. The version that ran a full probe on both managers opened the
    /// app with a queue of authentication prompts â€” a 1Password desktop approval per CLI call, a
    /// Proton Pass unlock â€” before the user had said which manager they wanted, or whether they
    /// wanted one at all. Connecting is now a button per card, and the prompts belong to it.
    /// </summary>
    [RelayCommand]
    public async Task CheckAsync()
    {
        if (IsBusy || IsSigningIn) return;

        NativeCliRunner.ResetInitialization();

        IsBusy = true;
        StatusMessage = "Looking for password managersâ€¦";

        try
        {
            // Asked once and cached: a WinRT capability check that cannot change while the app runs.
            // It raises no prompt of its own â€” it reports whether Hello could prompt.
            if (!_helloChecked)
            {
                _isHelloAvailable = await protonAuthenticator.IsHelloAvailableAsync();
                _helloChecked = true;
            }

            var status = await Task.Run(() => gate.EvaluateAsync(VaultProbeDepth.Discovery));
            Apply(status);

            if (status.IsReady) await StartAsync("Loading your configuration from the vaultâ€¦");
        }
        catch (Exception ex)
        {
            // The setup page is the last thing standing between the user and an app that does
            // nothing without explaining itself, so it absorbs everything.
            activityLog.LogError("Vault check failed", ex);
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Asks one password manager for real â€” the button that owns the authentication prompt.
    ///
    /// Everything the startup check refuses to do happens here, for this manager only: the Hello
    /// gesture that opens RavensPort's Proton Pass session, the 1Password desktop approval, the
    /// vault and item listings behind them. The user pressed a button that says "connect", so a
    /// prompt is the expected answer rather than an ambush.
    /// </summary>
    [RelayCommand]
    private async Task ConnectAsync(ManagerCardViewModel card)
    {
        if (IsBusy || IsSigningIn) return;

        // Answered here rather than by attempting the connection: the SDK opens against a named
        // account, so a blank name fails inside the runner and comes back as a CLI error on the
        // card â€” which reads as "1Password refused" rather than "you have not filled the box in".
        if (card.Kind == VaultBackendKind.OnePassword && !card.UsesServiceToken
            && string.IsNullOrWhiteSpace(card.OnePasswordAccountName))
        {
            StatusMessage = "Enter your 1Password account name first â€” it is at the top of the "
                            + "1Password desktop app's sidebar.";
            return;
        }

        // The token has to reach the session before anything probes, because its presence is what
        // selects the whole authentication mode â€” the runner reads it to decide whether to open the
        // desktop app's channel or go straight to 1Password over the network.
        if (card.Kind == VaultBackendKind.OnePassword && card.UsesServiceToken)
        {
            try
            {
                onePasswordSession.Unlock(card.ServiceToken);
            }
            catch (VaultCliException ex)
            {
                StatusMessage = ex.Message;
                return;
            }

            // The SDK client is cached per authentication mode, so a switch between the desktop app
            // and a token â€” or a corrected token after a refusal â€” would otherwise keep using the
            // connection made for the previous one.
            NativeCliRunner.ResetInitialization();

            // Saved only once the token has been accepted, further down. Storing first would leave
            // a typo behind Windows Hello and offer it back on every restart.
            _saveTokenAfterConnect = card.RememberToken;
        }
        else if (card.Kind == VaultBackendKind.OnePassword && onePasswordSession.HasToken)
        {
            // Switching back to the desktop app. The token has to go, because holding one is what
            // puts the runner in service-account mode â€” leaving it would mean pressing "Connect" on
            // the desktop option and silently connecting as the service account instead.
            onePasswordSession.Clear();
            NativeCliRunner.ResetInitialization();
        }

        IsBusy = true;
        StatusMessage = $"Connecting to {card.Name}â€¦";

        try
        {
            // Proton Pass first, and on this thread: the session key is what every pass-cli call
            // below needs, it lives behind a Hello gesture, and that gesture needs a foreground
            // window to attach to. Declining leaves the probe to report an unopened session, which
            // is the truth and comes with its own buttons.
            var unlocked = false;

            if (card.Kind == VaultBackendKind.ProtonPass && CanUnlockWithHello)
            {
                if (!await helloConsent.RequestUnlockAsync(protonAuthenticator.UnlockWithHelloAsync))
                {
                    StatusMessage = "Not unlocked. Try Windows Hello again, or discard this session and sign in.";
                    NotifySessionStateChanged();
                    Apply(gate.Status);
                    return;
                }

                unlocked = true;
                NotifySessionStateChanged();
            }

            // A successful unlock has already connected this manager â€” see
            // ProtonPassAuthenticator.UnlockWithHelloAsync â€” so probing again would be a second
            // round of CLI calls for an answer already on hand.
            var status = unlocked ? gate.Status : await Task.Run(() => gate.ConnectAsync(card.Kind));
            Apply(status);

            // After the connection worked, never before. A token that 1Password refused is a typo or
            // a revoked account, and storing one behind a Hello gesture would offer it back on every
            // restart as though it were good.
            if (status.IsReady && _saveTokenAfterConnect) await SaveTokenWithConsentAsync(card);

            if (status.IsReady) await StartAsync($"Loading your configuration from {card.Name}â€¦");
        }
        catch (VaultCliException ex)
        {
            StatusMessage = ex.Message;
            NotifySessionStateChanged();
        }
        catch (Exception ex)
        {
            activityLog.LogError($"Could not connect to {card.Name}", ex);
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Starts the app on memory alone â€” every tab, every route, every funnel, and no password
    /// manager anywhere near it.
    ///
    /// The point is to be able to try the whole thing, or test a change to it, without unlocking a
    /// vault first. What it costs is stated on the card and again on the Settings tab: nothing is
    /// written anywhere, so disconnecting or exiting takes the configuration with it.
    /// </summary>
    /// <summary>
    /// Set by <see cref="ConnectAsync"/> when the user asked to keep the token, and acted on only
    /// once the connection has actually worked.
    /// </summary>
    private bool _saveTokenAfterConnect;

    /// <summary>
    /// Stores the token behind Windows Hello, asking first.
    ///
    /// Failing here is deliberately not failing the connection: the user is connected either way,
    /// and the difference is only whether they type the token again next time. Saying so beats
    /// tearing down a working session over a declined gesture.
    /// </summary>
    /// <param name="card">
    /// Where the token is read from. Deliberately not from <see cref="OnePasswordSession"/>, which
    /// hands its token out through nothing but an environment block â€” a property that returned it
    /// would be a second way to get at the credential, and a test pins that there is none.
    /// </param>
    private async Task SaveTokenWithConsentAsync(ManagerCardViewModel card)
    {
        _saveTokenAfterConnect = false;

        if (string.IsNullOrWhiteSpace(card.ServiceToken)) return;

        var token = card.ServiceToken;

        try
        {
            if (await helloConsent.RequestTokenSaveAsync(() => tokenProtector.ProtectOnePasswordTokenAsync(token)))
            {
                StatusMessage = "Connected. The token is saved on this PC behind Windows Hello.";
            }
            else
            {
                StatusMessage = "Connected. The token was not saved â€” you will be asked for it again next time.";
            }
        }
        catch (Exception ex)
        {
            activityLog.LogError("Could not save the 1Password service account token", ex);
            StatusMessage = $"Connected, but the token could not be saved: {ex.Message}";
        }

        NotifySessionStateChanged();
    }

    /// <summary>
    /// Connects using the token kept from a previous run, after a Hello gesture.
    /// </summary>
    [RelayCommand]
    private async Task UseSavedTokenAsync(ManagerCardViewModel card)
    {
        if (IsBusy || IsSigningIn) return;

        string? token = null;

        // The gesture runs on this thread: Hello needs a foreground window to attach to, and the
        // consent prompt is the thing that owns it.
        if (!await helloConsent.RequestTokenUnlockAsync(async () =>
                token = await tokenProtector.UnprotectOnePasswordTokenAsync()))
        {
            StatusMessage = "Not unlocked. Paste a token instead, or forget the saved one.";
            return;
        }

        if (token is null)
        {
            StatusMessage = "There is no saved token any more. Paste one to connect.";
            card.HasSavedToken = false;
            return;
        }

        IsBusy = true;
        StatusMessage = $"Connecting to {card.Name}â€¦";

        try
        {
            onePasswordSession.Unlock(token);
            NativeCliRunner.ResetInitialization();

            var status = await Task.Run(() => gate.ConnectAsync(card.Kind));
            Apply(status);

            if (status.IsReady) await StartAsync($"Loading your configuration from {card.Name}â€¦");
        }
        catch (VaultCliException ex)
        {
            StatusMessage = ex.Message;
        }
        catch (Exception ex)
        {
            activityLog.LogError($"Could not connect to {card.Name}", ex);
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Removes the saved token.
    ///
    /// Needed rather than tidy: service accounts are rotated, and a saved token that has been
    /// revoked fails every startup with nothing in the UI to clear it. It is also what someone
    /// reaches for on a machine they have decided they should not have saved it on.
    /// </summary>
    [RelayCommand]
    private async Task ForgetSavedTokenAsync(ManagerCardViewModel card)
    {
        await tokenProtector.ForgetOnePasswordTokenAsync();

        card.HasSavedToken = false;
        card.RememberToken = false;

        StatusMessage = "The saved token has been removed from this PC.";
    }

    [RelayCommand]
    private async Task StartSingleUseAsync()
    {
        if (IsBusy || IsSigningIn) return;

        IsBusy = true;

        try
        {
            Apply(gate.UseSingleUse());

            await StartAsync("Starting in single use â€” nothing will be saved to a password managerâ€¦");
        }
        catch (Exception ex)
        {
            activityLog.LogError("Could not start in single use", ex);
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ChooseAsync(ManagerCardViewModel card)
    {
        if (IsBusy) return;

        IsBusy = true;

        try
        {
            // Asked on every launch when both managers qualify, by design: the choice is the one
            // piece of state that cannot live in the vault, and this app deliberately stores
            // nothing locally.
            Apply(gate.SelectBackend(card.Kind));
            activityLog.Log($"STARTUP using {card.Name} for this session");

            await StartAsync($"Loading the vault from {card.Name}â€¦");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Creates a vault with the name the user chose, and starts using it.</summary>
    [RelayCommand]
    private async Task CreateVaultAsync(ManagerCardViewModel card)
    {
        if (IsBusy) return;

        var name = card.NewVaultName;

        // Caught here as well as in the provider so the answer is instant and says what to do
        // instead: a second vault of the same name is the one thing this page must not produce â€”
        // two vaults called RavensPort are indistinguishable in the picker, and the app would
        // pick between them by list order.
        if (card.Vaults.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            StatusMessage = card.Profile.Trim().Length == 0
                ? $"'{name}' already exists in {card.Name}. Choose it above, or name a profile to make a separate one."
                : $"'{name}' already exists in {card.Name}. Choose it above, or use a different profile name.";
            return;
        }

        IsBusy = true;
        StatusMessage = $"Creating the '{name}' vault in {card.Name}â€¦";

        try
        {
            var status = await Task.Run(() => gate.CreateVaultAsync(card.Kind, name));
            Apply(status);

            if (status.IsReady) await StartAsync($"Loading the '{name}' vaultâ€¦");
        }
        catch (VaultAdoptionException ex)
        {
            // A name that is already taken, or blank. The user's answer is wrong rather than
            // broken, so the name stays in the box to be corrected.
            StatusMessage = ex.Message;
        }
        catch (Exception ex)
        {
            activityLog.LogError($"Could not create the '{name}' vault", ex);
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Uses a vault the user already has instead of creating RavensPort. The gate refuses
    /// anything that is neither empty nor already RavensPort's, and says why â€” see
    /// <see cref="VaultAdoption"/>.
    /// </summary>
    [RelayCommand]
    private async Task UseExistingVaultAsync(ManagerCardViewModel card)
    {
        if (IsBusy) return;

        var name = card.SelectedVaultName?.Trim() ?? "";
        if (name.Length == 0)
        {
            StatusMessage = "Choose a vault from the list first.";
            return;
        }

        IsBusy = true;
        StatusMessage = $"Checking the '{name}' vault in {card.Name}â€¦";

        try
        {
            var status = await Task.Run(() => gate.UseExistingVaultAsync(card.Kind, name));
            Apply(status);

            if (status.IsReady) await StartAsync($"Loading the '{name}' vaultâ€¦");
        }
        catch (VaultAdoptionException ex)
        {
            // The user's answer is wrong rather than broken â€” a typo, or a vault with their own
            // things in it. Says which, and leaves the name in the box to be corrected.
            StatusMessage = ex.Message;
        }
        catch (Exception ex)
        {
            activityLog.LogError($"Could not use the '{name}' vault", ex);
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Opens one of the vaults that already holds a configuration. Offered when more than one
    /// does â€” separate profiles, where guessing would open one and overwrite the other.
    /// </summary>
    [RelayCommand]
    private async Task UseNamedVaultAsync(VaultChoiceViewModel choice)
    {
        if (IsBusy) return;

        IsBusy = true;
        StatusMessage = $"Opening the '{choice.Name}' vaultâ€¦";

        try
        {
            var status = await Task.Run(() => gate.UseExistingVaultAsync(choice.Kind, choice.Name));
            Apply(status);

            if (status.IsReady) await StartAsync($"Loading the '{choice.Name}' vaultâ€¦");
        }
        catch (Exception ex)
        {
            activityLog.LogError($"Could not open the '{choice.Name}' vault", ex);
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RetryPortAsync()
    {
        if (!int.TryParse(ListenPort, out var port) || port is < 1 or > 65535)
        {
            StatusMessage = "Enter a port between 1 and 65535.";
            return;
        }

        if (IsBusy) return;

        IsBusy = true;
        StatusMessage = $"Saving port {port} to the vaultâ€¦";

        // Written straight to the vault: the proxy is not running, so there is no other way to
        // change it â€” which is precisely why the old "edit the file in %APPDATA%" advice had to go.
        try
        {
            var vault = gate.Selected;
            var store = await vault.LoadAsync();
            store.Settings.ListenPort = port;
            await vault.SaveAsync(store);

            HasPortConflict = false;
            await StartAsync($"Starting the proxy on port {port}â€¦");
        }
        catch (Exception ex)
        {
            activityLog.LogError("Could not save the new listen port", ex);
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private Task OpenDownloadPageAsync(ManagerCardViewModel card) => OpenUrlAsync(card.DownloadUrl);

    // ---- Proton Pass: install, unlock, sign in, sign out ------------------------------------
    //
    // All of this is Proton Pass only, and the asymmetry is not an oversight. 1Password's CLI has
    // no browser sign-in to drive â€” it wants a Secret Key and an account password typed at a
    // terminal â€” and its licence does not allow RavensPort to ship it. Offering a "Sign in" button
    // that could only ever open a text box asking for someone's 1Password master credentials would
    // be worse than the honest instructions the card already shows.

    /// <summary>The URL pass-cli printed. Shown, never launched â€” see <see cref="SignInProtonAsync"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSignInUrl))]
    private string? _signInUrl;

    public bool HasSignInUrl => SignInUrl is { Length: > 0 };

    [ObservableProperty] private bool _isSigningIn;

    partial void OnIsSigningInChanged(bool value) => OnPropertyChanged(nameof(CanCheck));

    /// <summary>Cancels an in-flight sign-in, which kills the pass-cli process tree.</summary>
    private CancellationTokenSource? _signInCts;

    public bool HasSessionKey => protonSession.HasKey;

    /// <summary>
    /// Whether to show the Sign in button.
    ///
    /// Gated on Hello for a first sign-in, because signing in is what creates the session key and
    /// Hello is the only thing that can store it â€” the key is never shown, so there is no other way
    /// back into the session after a restart. A button that could only produce an unopenable
    /// session is worse than the explanation shown in its place.
    /// </summary>
    public bool CanShowSignInButton => HasSessionKey || (IsFirstSignIn && _isHelloAvailable);

    /// <summary>Whether to explain that Hello has to be set up before Proton Pass can be used here.</summary>
    public bool NeedsHelloSetup => IsFirstSignIn && !_isHelloAvailable;

    /// <summary>
    /// The one place that message is written â€” see <see cref="ProtonPassAuthenticator.HelloRequired"/>.
    /// An instance property despite being constant: WPF resolves binding paths through
    /// <c>TypeDescriptor</c>, which does not enumerate static members, so a static one would bind
    /// to nothing and show an empty block where the explanation should be.
    /// </summary>
    public string HelloRequiredMessage => ProtonPassAuthenticator.HelloRequired;

    /// <summary>
    /// Returning: a session is sitting on disk and only needs the key that opens it.
    ///
    /// Split from <see cref="IsFirstSignIn"/> because the two need opposite advice and opposite
    /// buttons. Showing Unlock and Generate side by side asked the user to know which of two
    /// situations they were in â€” and picking Generate in this one destroys the session they were
    /// trying to open.
    /// </summary>
    public bool NeedsSessionKey => !protonSession.HasKey && protonSession.HasSessionOnDisk;

    /// <summary>First time here: nothing to unlock, so a key has to be made before signing in.</summary>
    public bool IsFirstSignIn => !protonSession.HasKey && !protonSession.HasSessionOnDisk;

    /// <summary>
    /// Whether a Hello gesture can open this session â€” a key is stored and this PC can still do it.
    ///
    /// The availability half is cached rather than awaited per binding: it is an async WinRT call,
    /// and a property getter that blocks on one is a deadlock waiting for a slow TPM.
    /// </summary>
    public bool CanUnlockWithHello => _isHelloAvailable && protonAuthenticator.HasHelloKey;

    /// <summary>Whether this PC can do Hello at all, for the first-run explanation.</summary>
    public bool IsHelloAvailable => _isHelloAvailable;

    private bool _isHelloAvailable;
    private bool _helloChecked;

    /// <summary>The key-state flags move together and none of them is settable.</summary>
    private void NotifySessionStateChanged()
    {
        OnPropertyChanged(nameof(HasSessionKey));
        OnPropertyChanged(nameof(CanShowSignInButton));
        OnPropertyChanged(nameof(NeedsSessionKey));
        OnPropertyChanged(nameof(IsFirstSignIn));
        OnPropertyChanged(nameof(CanUnlockWithHello));
        OnPropertyChanged(nameof(NeedsHelloSetup));
        OnPropertyChanged(nameof(IsHelloAvailable));
    }

    /// <summary>Opens the session with a Hello gesture instead of a pasted key.</summary>
    [RelayCommand]
    private async Task UnlockWithHelloAsync()
    {
        if (IsBusy) return;

        IsBusy = true;

        try
        {
            // Through the consent prompt even though the button the user just pressed says
            // "Windows Hello" on it. The rule only protects anyone if it has no exceptions â€” see
            // IHelloConsentPrompt.
            if (!await helloConsent.RequestUnlockAsync(protonAuthenticator.UnlockWithHelloAsync))
            {
                StatusMessage = "Not unlocked. Discard this session and sign in again, or try Windows Hello again.";
                return;
            }

            NotifySessionStateChanged();
            Apply(gate.Status);

            if (gate.Status.IsReady) await StartAsync("Loading your configuration from the vaultâ€¦");
        }
        catch (VaultCliException ex)
        {
            StatusMessage = ex.Message;
            NotifySessionStateChanged();
        }
        catch (Exception ex)
        {
            activityLog.LogError("Windows Hello unlock failed", ex);
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Downloads pass-cli when the machine has none.</summary>
    [RelayCommand]
    private async Task InstallProtonCliAsync()
    {
        if (IsBusy) return;

        IsBusy = true;

        try
        {
            var progress = new Progress<string>(message => StatusMessage = message);
            await protonAuthenticator.EnsureInstalledAsync(progress);

            await CheckAsync();
        }
        catch (Exception ex)
        {
            activityLog.LogError("Could not install the Proton Pass CLI", ex);
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Set once the user has asked to throw away a session they can no longer open.</summary>
    [ObservableProperty] private bool _isConfirmingDiscard;

    /// <summary>
    /// The way out for someone who has lost their session key.
    ///
    /// It has to live here, on the setup page. Sign out is on the Settings tab, which is only
    /// reachable once a vault is open â€” so pointing a locked-out user at it sent them to the far
    /// side of the door they could not open.
    /// </summary>
    [RelayCommand]
    private async Task DiscardSessionAsync()
    {
        if (!IsConfirmingDiscard)
        {
            IsConfirmingDiscard = true;
            StatusMessage = "Confirm to discard the locked session and start again.";
            return;
        }

        IsConfirmingDiscard = false;

        try
        {
            await protonAuthenticator.DiscardLocalSessionAsync();

            NotifySessionStateChanged();
            await CheckAsync();

            StatusMessage = "Session discarded. Choose Sign in to start again.";
        }
        catch (Exception ex)
        {
            activityLog.LogError("Could not discard the Proton Pass session", ex);
            StatusMessage = ex.Message;
        }
    }

    [RelayCommand]
    private void CancelDiscard()
    {
        IsConfirmingDiscard = false;
        StatusMessage = "Left as it is.";
    }

    /// <summary>
    /// Asks consent, then creates the session key and protects it â€” before any sign-in runs.
    ///
    /// Asked, not assumed: it is the moment RavensPort begins keeping something on this PC that was
    /// not there before. Cancelling leaves nothing behind, which is only true because this happens
    /// first. Offering it *after* a sign-in, as this used to, meant declining produced a live
    /// session whose key was in memory only and displayed nowhere â€” gone at the next restart, with
    /// nothing in the UI admitting it.
    ///
    /// Awaited on the UI thread throughout: the consent prompt is modal, and the Hello prompt it
    /// raises needs a foreground window to attach to. Nothing here may be pushed onto a background
    /// thread to "keep the UI responsive" â€” that is exactly what would leave the gesture with
    /// nothing to attach to.
    /// </summary>
    private async Task<bool> ProtectSessionKeyWithHelloAsync()
    {
        if (protonSession.HasKey && protonAuthenticator.HasHelloKey) return true;

        if (!_isHelloAvailable)
        {
            StatusMessage = HelloRequiredMessage;
            return false;
        }

        var consented = await helloConsent.RequestSetupAsync(protonAuthenticator.PrepareSessionKeyAsync);

        NotifySessionStateChanged();

        if (!consented)
        {
            StatusMessage =
                "Sign-in cancelled. Nothing was created â€” RavensPort needs Windows Hello to hold its "
                + "Proton Pass session key, because the key is never shown to you.";
        }

        return consented;
    }

    /// <summary>
    /// Runs the browser sign-in and shows the URL.
    ///
    /// The URL is deliberately not opened for the user. It carries a live single-use
    /// authentication handle, and launching it fires it at whichever browser happens to be default
    /// â€” quite possibly a profile signed in as someone else. Showing it lets them choose.
    /// </summary>
    [RelayCommand]
    private async Task SignInProtonAsync()
    {
        if (IsBusy || IsSigningIn) return;

        // Before IsSigningIn, so the consent window is not shown over a page already claiming a
        // sign-in is under way â€” cancelling here means none ever started.
        if (!await ProtectSessionKeyWithHelloAsync()) return;

        IsSigningIn = true;
        SignInUrl = null;

        _signInCts = new CancellationTokenSource();

        try
        {
            var progress = new Progress<string>(message => StatusMessage = message);

            await protonAuthenticator.SignInAsync(
                url => SignInUrl = url,
                progress,
                _signInCts.Token);

            SignInUrl = null;

            var status = gate.Status;
            Apply(status);

            if (status.IsReady) await StartAsync("Loading your configuration from the vaultâ€¦");
        }
        catch (OperationCanceledException)
        {
            SignInUrl = null;
            StatusMessage = "Sign-in cancelled.";
        }
        catch (Exception ex)
        {
            SignInUrl = null;
            activityLog.LogError("Proton Pass sign-in failed", ex);
            StatusMessage = ex.Message;
        }
        finally
        {
            _signInCts?.Dispose();
            _signInCts = null;
            IsSigningIn = false;

            // A failed sign-in takes the key and the protected copy with it â€” see
            // ProtonPassAuthenticator.AbandonAsync â€” so the buttons this page shows have changed.
            NotifySessionStateChanged();
        }
    }

    [RelayCommand]
    private void CancelSignIn() => _signInCts?.Cancel();

    /// <summary>Copies a shown value â€” the sign-in URL, or a freshly generated key.</summary>
    [RelayCommand]
    private async Task CopyToClipboardAsync(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        try
        {
            await clipboard.SetTextAsync(text);
            StatusMessage = "Copied.";
        }
        catch (Exception ex)
        {
            // The clipboard is genuinely flaky â€” another process can hold it open â€” and this is
            // never worth failing anything over. The text is on screen to select by hand.
            StatusMessage = $"Could not copy: {ex.Message}";
        }
    }

    /// <summary>
    /// Deletes the pre-vault store. Offered rather than done automatically: it is an encrypted
    /// file full of the user's secrets, and this version can no longer read it â€” silently
    /// destroying it on their behalf is not this app's call to make.
    /// </summary>
    [RelayCommand]
    private void DeleteLegacyStore()
    {
        try
        {
            if (File.Exists(LegacyStorePath)) File.Delete(LegacyStorePath);

            HasLegacyStore = false;
            StatusMessage = "Deleted the old configuration file.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not delete it: {ex.Message}";
        }
    }

    /// <summary>
    /// Hands off to the host, which reads the whole vault and starts the proxy â€” a CLI round trip
    /// per item, so seconds rather than an instant. The message says so: <see cref="Apply"/> has
    /// just written "Ready.", which would otherwise be the last thing on screen while the window
    /// sat there looking finished and doing nothing.
    /// </summary>
    private async Task StartAsync(string workingMessage)
    {
        if (ReadyToStart is not { } handler) return;

        StatusMessage = workingMessage;
        await handler();
    }

    private void Apply(VaultGateStatus status)
    {
        Managers.Clear();

        // Read once per rebuild rather than per card: both answers are the same for every card, and
        // HasProtectedOnePasswordToken touches Credential Manager.
        // Both conditions, because they are different questions: whether this platform has anywhere
        // to keep a bearer token, and whether the gesture that would seal it is enrolled here.
        var canKeepToken = _isHelloAvailable && tokenProtector.CanKeepToken;
        var hasSavedToken = canKeepToken && tokenProtector.HasProtectedOnePasswordToken();

        foreach (var manager in status.Statuses)
        {
            var card = new ManagerCardViewModel(manager);

            if (card.IsOnePassword)
            {
                // Keeping a token is only ever offered as "encrypted behind a gesture". Without
                // Hello there is no offer, because the alternative would be plain text and there
                // must not be one.
                card.CanRememberToken = canKeepToken;
                card.HasSavedToken = hasSavedToken;

                // A saved token means the user already chose this mode; starting the card on the
                // desktop-app option would hide the button that uses it.
                if (hasSavedToken) card.UseServiceToken = true;
            }

            Managers.Add(card);
        }

        NeedsAChoice = status.NeedsAChoice;
        IsDisconnected = gate.IsDisconnected;
        HasLegacyStore = File.Exists(LegacyStorePath);

        // Re-read on every evaluation: signing out happens on the Settings tab, which deletes the
        // session and clears the key without this page hearing about it directly.
        NotifySessionStateChanged();

        StatusMessage = status switch
        {
            { NeedsAChoice: true } when gate.IsDisconnected =>
                "Disconnected. Choose a password manager to connect to it again.",
            { NeedsAChoice: true } => "Both password managers are set up. Choose which one RavensPort should use.",
            { IsReady: true } => "Ready.",
            _ when status.Statuses.Any(s => s.Availability == VaultAvailability.VaultChoiceNeeded) =>
                "More than one vault holds a configuration. Choose which one to open.",
            _ when status.Statuses.Any(s => s.CanCreateVault) =>
                $"Almost there â€” create the '{VaultConstants.VaultName}' vault to finish.",
            _ when status.Statuses.All(s => s.Availability == VaultAvailability.NotInstalled) =>
                "No supported password manager found. Install 1Password or Proton Pass to continue, "
                + "or try RavensPort in single use.",

            // Nothing has been asked yet, which is the normal state at startup now â€” say so, rather
            // than reporting a lock nobody has actually run into.
            _ when status.Statuses.Any(s => s.Availability == VaultAvailability.NotConnected) =>
                gate.IsDisconnected
                    ? "Disconnected. Connect a password manager to load a configuration again."
                    : "Choose a password manager and connect to it â€” that is when it will ask you to unlock.",

            _ => "Unlock or sign in to your password manager, then choose Check again.",
        };
    }

    private async Task OpenUrlAsync(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;

        // The launcher hands the string to the desktop to resolve, and every desktop will happily
        // run a registered protocol handler, a UNC path, or an executable â€” a browser is only one
        // of the things it might pick. Today every caller passes a compile-time constant, so this
        // changes nothing; it is here so that stays true if a URL ever arrives from config or a
        // vault item.
        if (!UrlValidation.IsSafeToOpenInBrowser(url))
        {
            StatusMessage = "Could not open the browser: that link is not an http/https address.";
            return;
        }

        try
        {
            await launcher.OpenUriAsync(url);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not open the browser: {ex.Message}";
        }
    }
}

