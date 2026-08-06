using CommunityToolkit.Mvvm.ComponentModel;
using RavensPort.Core.Vault;

namespace RavensPort.UI.ViewModels;

/// <summary>
/// One password manager as the setup page shows it: what was found, what state it is in, and the
/// single next thing the user has to do about it.
/// </summary>
public sealed partial class ManagerCardViewModel(VaultStatus status) : ObservableObject
{
    public VaultBackendKind Kind { get; } = status.Kind;

    public string Name { get; } = VaultLockGuidance.DisplayName(status.Kind);

    public VaultAvailability Availability { get; } = status.Availability;

    /// <summary>Short state chip: the one-word answer to "where am I with this one".</summary>
    public string StateLabel { get; } = status.Availability switch
    {
        VaultAvailability.NotInstalled => "Not installed",

        // Says what RavensPort has done, not what the manager is: a discovery probe found the
        // binary and asked it nothing else, so "locked" or "signed out" would be a guess — and the
        // command that would settle it is the one that raises the prompt.
        VaultAvailability.NotConnected => "Installed — not connected",

        // "Locked or signed out" hedges because for 1Password it genuinely could be either, and
        // only the CLI knows which. RavensPort owns its Proton Pass session, so there it does know:
        // not signed in, or signed in and waiting for the key — and the Detail line says which.
        VaultAvailability.NotSignedIn when status.Kind == VaultBackendKind.ProtonPass =>
            "Not signed in",
        VaultAvailability.NotSignedIn => "Locked or signed out",
        VaultAvailability.VaultMissing => $"No '{VaultConstants.VaultName}' vault",
        VaultAvailability.VaultChoiceNeeded => "Choose a vault",
        VaultAvailability.Ready => $"Ready — vault '{status.VaultName ?? VaultConstants.VaultName}'",
        _ => "Not working",
    };

    /// <summary>
    /// The vaults this card offers: named after RavensPort, and either empty or already
    /// holding a RavensPort configuration — the same test that decides whether picking one is
    /// accepted, so the list never offers something it will then refuse.
    ///
    /// Not every vault in the account. Listing all of them invites pointing a credential store at
    /// a personal vault, and an app that recites the contents of someone's password manager back
    /// at them is not one to trust with tokens. A vault adopted under some other name is not
    /// stranded by this: it is found by the configuration in it, which is what
    /// <see cref="VaultChoices"/> below offers.
    /// </summary>
    public IReadOnlyList<string> Vaults { get; } = status.AdoptableVaults ?? [];

    /// <summary>Vaults that already hold a RavensPort configuration — one per profile.</summary>
    public IReadOnlyList<VaultChoiceViewModel> VaultChoices { get; } =
        [.. (status.ConfiguredVaults ?? []).Select(name => new VaultChoiceViewModel(status.Kind, name))];

    /// <summary>
    /// The same vaults as buttons, for the "which one should RavensPort use" card. Offering the
    /// vaults rather than the manager answers the question the user actually has — which set of
    /// credentials am I opening — in one click instead of two.
    /// </summary>
    public IReadOnlyList<VaultChoiceViewModel> VaultButtons =>
        [.. Vaults.Select(name => new VaultChoiceViewModel(Kind, name))];

    public bool IsReady { get; } = status.IsReady;

    public string DetectedAt { get; } = status.ExePath is { Length: > 0 } path
        ? status.Version is { Length: > 0 } version ? $"{path}  (v{version})" : path
        : "Not found on this machine.";

    /// <summary>
    /// Whatever the CLI itself said. Preferred over anything this app could infer: it
    /// distinguishes locked from signed out from integration-disabled, and it is the only text
    /// here that reflects the actual reason.
    /// </summary>
    public string? Detail { get; } = status.Detail;

    public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);

    public string InstallCommand { get; } = VaultLockGuidance.InstallCommand(status.Kind);

    public string DownloadUrl { get; } = VaultLockGuidance.DownloadUrl(status.Kind);

    public string SignInSteps { get; } = VaultLockGuidance.SignInSteps(status.Kind);

    public string? TokenCaveat { get; } = VaultLockGuidance.TokenCaveat(status.Kind);

    public bool HasTokenCaveat => !string.IsNullOrWhiteSpace(TokenCaveat);

    /// <summary>
    /// Shown in every state this card can be in, not just the broken one. By the time the
    /// connection has dropped the user is looking at a failure and guessing, and the repair they
    /// will reach for first — restart 1Password — is the one that does not work.
    /// </summary>
    public string DesktopAppRequirement { get; } = VaultLockGuidance.DesktopAppRequirement(status.Kind);

    public bool HasDesktopAppRequirement => DesktopAppRequirement.Length > 0;

    /// <summary>
    /// The vault picked from the list, for "use one I already have".
    ///
    /// A plain selection rather than an editable combo: the dark theme's ComboBox template has no
    /// PART_EditableTextBox, so an editable one renders — and stays — blank no matter what is
    /// bound to Text. Picking from the list and typing a new name are different actions anyway.
    /// </summary>
    [ObservableProperty] private string? _selectedVaultName =
        status.VaultName is { Length: > 0 } current
        && (status.AdoptableVaults ?? []).Contains(current, StringComparer.OrdinalIgnoreCase)
            ? current
            : null;

    /// <summary>
    /// The optional profile for a vault to create: blank makes RavensPort, "Work" makes
    /// "RavensPort Work".
    ///
    /// A profile rather than a free-text vault name. The name carries meaning — it is what marks
    /// the vault as this app's, and what the picker filters on — so letting someone type anything
    /// produced vaults that neither they nor RavensPort could recognise later.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NewVaultName))]
    private string _profile = "";

    /// <summary>The vault the profile above would create. Shown, because a name assembled out of
    /// sight is one the user cannot check against what their password manager will show them.</summary>
    public string NewVaultName => VaultProfile.NameFor(Profile);

    // Exactly one section is shown per card, so the page never asks the user to read past advice
    // that does not apply to the state they are actually in.
    public bool ShowInstall => Availability == VaultAvailability.NotInstalled;
    public bool ShowSignIn => Availability is VaultAvailability.NotSignedIn or VaultAvailability.Faulted;
    public bool ShowVaultChoice => Availability == VaultAvailability.VaultChoiceNeeded;

    /// <summary>
    /// Whether to offer the Connect button — the one action on this card that is allowed to raise
    /// an authentication prompt, and the only thing offered while nothing has been asked yet.
    /// </summary>
    public bool ShowConnect => Availability == VaultAvailability.NotConnected;

    /// <summary>Names the manager on the button, so two cards do not both say "Connect".</summary>
    public string ConnectLabel => $"Connect to {Name}";

    /// <summary>
    /// What pressing it will actually do, per manager. Said in advance because the prompt that
    /// follows is the app asking for the user's credentials, and one that arrives unannounced is
    /// one people learn to click through.
    /// </summary>
    public string ConnectPrompt => Kind switch
    {
        // Said before the button rather than after: a service account raises no prompt at all, so
        // the thing worth warning about is not an interruption but the absence of one -- and that
        // the token has to be typed again after every restart.
        VaultBackendKind.OnePassword when UsesServiceToken =>
            "RavensPort will sign in with the token, over the network. No prompt, and 1Password does "
            + "not need to be installed here. The token is never saved, so you will be asked for it "
            + "again after every restart.",

        VaultBackendKind.OnePassword =>
            "1Password will ask you to unlock — its desktop app approves each command RavensPort "
            + "runs. Nothing is read from your vaults until you press this.",

        VaultBackendKind.ProtonPass =>
            "RavensPort will open its own Proton Pass session, which means a Windows Hello gesture "
            + "if you have signed in here before. Nothing is read from your vaults until you press this.",

        _ => "Nothing is read from your vaults until you press this.",
    };

    /// <summary>
    /// Whether RavensPort can install the CLI and drive the sign-in itself, rather than only
    /// telling the user how.
    ///
    /// True for Proton Pass alone. pass-cli signs in through a URL it prints, which the app can
    /// show; and it is open source, so the app may fetch it. 1Password's CLI has neither property —
    /// it authenticates with a Secret Key and account password at a terminal, and its licence does
    /// not permit redistribution — so its card keeps the written instructions.
    /// </summary>
    public bool SupportsInAppSignIn => Kind == VaultBackendKind.ProtonPass;

    public bool ShowInAppInstall => ShowInstall && SupportsInAppSignIn;
    public bool ShowInAppSignIn => ShowSignIn && SupportsInAppSignIn;
    
    public bool IsOnePassword => Kind == VaultBackendKind.OnePassword;

    /// <summary>
    /// Whether to show the account-name box in the <em>failed-connect</em> section. Deliberately not
    /// including <see cref="VaultAvailability.NotConnected"/>: that state has its own copy of the
    /// box beside the Connect button, and a card showing two would be asking which one counts.
    /// </summary>
    public bool ShowOnePasswordSettings => IsOnePassword && Availability is
        VaultAvailability.NotSignedIn or VaultAvailability.Faulted;

    /// <summary>
    /// Which way in the user has chosen for 1Password. Desktop app by default, because that is the
    /// mode that needs no preparation — a service account has to be created and granted access to a
    /// vault before it can be used at all.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UsesServiceToken))]
    [NotifyPropertyChangedFor(nameof(UsesDesktopApp))]
    [NotifyPropertyChangedFor(nameof(ShowDesktopAppRequirement))]
    [NotifyPropertyChangedFor(nameof(ShowDesktopAppKnownIssue))]
    [NotifyPropertyChangedFor(nameof(ShowRememberToken))]
    [NotifyPropertyChangedFor(nameof(ShowSavedToken))]
    [NotifyPropertyChangedFor(nameof(ConnectPrompt))]
    private bool _useServiceToken;

    public bool UsesServiceToken => IsOnePassword && UseServiceToken;

    public bool UsesDesktopApp => IsOnePassword && !UseServiceToken;

    /// <summary>
    /// The token the user has typed, held here and nowhere else until Connect hands it to
    /// <see cref="OnePasswordSession"/>.
    ///
    /// Deliberately not routed through <see cref="LocalSettings"/> the way the account name above
    /// is. The account name is a label; this is a bearer credential for every vault the service
    /// account can reach, and writing it to local_settings.json would put a copy of the user's
    /// access outside their password manager — the one thing this app exists not to do.
    /// </summary>
    [ObservableProperty]
    private string _serviceToken = "";

    /// <summary>
    /// Whether the desktop-app warning applies. It does not in token mode: nothing is loaded from
    /// the 1Password install, so there is no library to be in the way and no ordering to observe.
    /// </summary>
    public bool ShowDesktopAppRequirement => HasDesktopAppRequirement && !UsesServiceToken;

    /// <summary>The known 1Password defect, shown against the mode it affects.</summary>
    public string DesktopAppKnownIssue { get; } = VaultLockGuidance.DesktopAppKnownIssue(status.Kind);

    public bool ShowDesktopAppKnownIssue => UsesDesktopApp && DesktopAppKnownIssue.Length > 0;

    /// <summary>What a bearer token actually is, shown before the box it goes in.</summary>
    public string ServiceTokenWarning { get; } = VaultLockGuidance.ServiceTokenWarning(status.Kind);

    /// <summary>
    /// Whether to offer keeping the token between runs. False when Windows Hello is unavailable,
    /// because the offer is only ever "encrypted behind a gesture" — there is no plain-text fallback
    /// and there must not be one.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRememberToken))]
    private bool _canRememberToken;

    public bool ShowRememberToken => UsesServiceToken && CanRememberToken;

    /// <summary>
    /// Whether the user has asked for the token to be kept. Off by default: storing a bearer
    /// credential is a decision to make deliberately, not one to arrive at by leaving a box ticked.
    /// </summary>
    [ObservableProperty] private bool _rememberToken;

    /// <summary>Whether a token is already saved, so the card offers to use or forget it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSavedToken))]
    private bool _hasSavedToken;

    public bool ShowSavedToken => UsesServiceToken && HasSavedToken;

    public string OnePasswordAccountName
    {
        get => LocalSettings.Current.OnePasswordAccountName;
        set
        {
            if (LocalSettings.Current.OnePasswordAccountName != value)
            {
                LocalSettings.Current.OnePasswordAccountName = value;
                LocalSettings.Save();

                // The SDK client is initialised once per account name and then cached, so a name
                // corrected after a failed connect would otherwise keep reconnecting as the old
                // one until something else happened to reset it.
                NativeCliRunner.ResetInitialization();

                OnPropertyChanged(nameof(OnePasswordAccountName));
            }
        }
    }

    /// <summary>
    /// Picking a vault and creating one are offered together, in every state where either is
    /// possible — including on a card that is already Ready, which is the only way to move to a
    /// different vault without editing anything by hand. Each vault is its own set of credentials,
    /// routes and funnels.
    /// </summary>
    public bool ShowVaultActions =>
        Availability is VaultAvailability.VaultMissing or VaultAvailability.VaultChoiceNeeded
            or VaultAvailability.Ready;

    /// <summary>True when the account has no vaults to pick from, so the list is not shown empty.</summary>
    public bool HasVaults => Vaults.Count > 0;
}

/// <summary>One vault offered as a choice, carrying the manager it belongs to.</summary>
public sealed record VaultChoiceViewModel(VaultBackendKind Kind, string Name);
