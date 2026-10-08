namespace RavensPort.Core.Vault;

/// <summary>
/// Everything the user is told about where RavensPort's Proton Pass session key lives, and how to
/// get pass-cli onto the machine, in the words of the platform actually running.
///
/// Written once, here, because <see cref="ISessionKeyProtector"/> requires that the UI describe the
/// guarantee of the implementation in use. On Windows that is <see cref="HelloKeyProtector"/>: the
/// key is sealed to a Windows Hello gesture, and the gesture produces the key that decrypts it. On
/// Linux it is <see cref="KeyringSessionKeyProtector"/>: the key sits in the desktop keyring, which
/// encrypts it at rest but is normally unlocked for the whole login session. Saying the Windows
/// sentence on Linux promises a gesture that never happens, and teaches the user to trust a prompt
/// that does not exist — so every string with "Hello" in it that a Linux user can reach comes from
/// here instead.
///
/// Two complete sets rather than a ternary per string, so both are built — and can be read by a
/// test — on whichever platform is running, and neither can quietly fall out of step with the
/// other. <see cref="Current"/> is the one choice.
///
/// The 1Password token-saving copy is not here: it is Windows-only, because Linux offers no way to
/// keep the token at all (see <see cref="UnavailableServiceTokenProtector"/>).
/// </summary>
public sealed record SessionKeyWording
{
    /// <summary>The button that opens an existing session.</summary>
    public required string UnlockButton { get; init; }

    public required string UnlockHeading { get; init; }

    public required string UnlockExplanation { get; init; }

    /// <summary>Said before the first sign-in, ahead of the prompt it announces.</summary>
    public required string FirstSignInHeading { get; init; }

    public required string FirstSignInExplanation { get; init; }

    /// <summary>The tail of "RavensPort stores none of your data here".</summary>
    public required string WhatIsKeptHere { get; init; }

    /// <summary>What Connect on the Proton Pass card will do.</summary>
    public required string ConnectPrompt { get; init; }

    public required string NotUnlocked { get; init; }

    public required string SignInCancelled { get; init; }

    /// <summary>Why in-app sign-in cannot be offered on this machine.</summary>
    public required string Required { get; init; }

    public required string NoKeySaved { get; init; }

    public required string SessionKeyUnreachable { get; init; }

    /// <summary>The standing rule on the consent window, under whatever it is asking for.</summary>
    public required string ConsentSecurityCheck { get; init; }

    public required string ConsentWaiting { get; init; }

    public required string ConsentFailedPrefix { get; init; }

    public string ConsentFailed(string message) => $"{ConsentFailedPrefix}: {message}";

    /// <summary>For the activity log, once the key is stored.</summary>
    public required string KeyCreatedLog { get; init; }

    /// <summary>
    /// The one line that installs pass-cli. On Linux there is no package to name, so it is the line
    /// that puts a downloaded pass-cli where <see cref="UnixExecutableProvenance"/> will accept it —
    /// a system location only an administrator can write to. Anywhere else, ~/.local/bin included,
    /// the probe refuses to run it.
    /// </summary>
    public required string PassCliInstallCommand { get; init; }

    /// <summary>The sentence above that command on the setup page.</summary>
    public required string PassCliInstallHint { get; init; }

    /// <summary>What to say when the machine has no pass-cli. RavensPort installs nothing.</summary>
    public required string PassCliMissing { get; init; }

    /// <summary>How the Proton Pass session survives a restart — the lock banner's text.</summary>
    public required string StayingUnlocked { get; init; }

    public static SessionKeyWording WindowsHello { get; } = new()
    {
        UnlockButton = "Unlock with Windows Hello",
        UnlockHeading = "Unlock with Windows Hello.",
        UnlockExplanation =
            " Your session key is held in Windows Credential Manager, encrypted so that only a Hello "
            + "gesture on this PC can decrypt it. RavensPort cannot read it without you, and it is never "
            + "shown to you either.",
        FirstSignInHeading = "Windows Hello will ask before the sign-in starts.",
        FirstSignInExplanation =
            " RavensPort generates a session key, encrypts it so that only a Hello gesture on this PC can "
            + "decrypt it, and keeps it in Windows Credential Manager. You never see the key, and after a "
            + "restart a gesture is all it takes to reopen the session. Cancel that prompt and nothing is "
            + "created.",
        WhatIsKeptHere =
            " Every credential, route, funnel and key lives in your Proton Pass vault and nowhere else. "
            + "The one thing kept here is the Proton Pass sign-in session, and the key that encrypts it "
            + "never leaves Windows Credential Manager unencrypted. "
            + "It is not your Proton password, and Proton never receives it.",
        ConnectPrompt =
            "RavensPort will open its own Proton Pass session, which means a Windows Hello gesture if you "
            + "have signed in here before. Nothing is read from your vaults until you press this.",
        NotUnlocked = "Not unlocked. Try Windows Hello again, or discard this session and sign in.",
        SignInCancelled =
            "Sign-in cancelled. Nothing was created — RavensPort needs Windows Hello to hold its Proton "
            + "Pass session key, because the key is never shown to you.",
        Required =
            "RavensPort needs Windows Hello to sign in to Proton Pass. The session key is never shown "
            + "to you, so Windows Hello is what stores it and what brings it back — without it there "
            + "would be no way to reopen the session after a restart. Set up Windows Hello in Windows "
            + "Settings → Accounts → Sign-in options, then try again.",
        NoKeySaved = "There is no Windows Hello key saved for this session. Discard the session and sign in again.",
        SessionKeyUnreachable =
            "There is already a Proton Pass session on this PC, encrypted with a key RavensPort cannot "
            + "reach. Unlock it with Windows Hello, or discard it and sign in again.",
        ConsentSecurityCheck =
            "Security check: only continue when you just asked RavensPort to use Windows Hello. Otherwise choose Not now.",
        ConsentWaiting = "Waiting for Windows Hello…",
        ConsentFailedPrefix = "Windows Hello failed",
        KeyCreatedLog = "VAULT created a Proton Pass session key and protected it with Windows Hello",
        PassCliInstallCommand = "winget install Proton.PassCLI",
        PassCliInstallHint = "Install it, then choose Check again.",
        PassCliMissing =
            "The Proton Pass CLI is not installed on this PC. Install it with "
            + "\"winget install Proton.PassCLI\", then choose Check again.",
        StayingUnlocked =
            "RavensPort's Proton Pass session lasts until you sign out. The key that opens it lives "
            + "in Windows Credential Manager, encrypted so that only a Windows Hello gesture on this "
            + "PC can decrypt it — so after RavensPort restarts, a gesture unlocks the session. The "
            + "key is never displayed to you, and RavensPort cannot read it without you.\n\n"
            + "If a gesture stops working — Hello reset, or a new PC — the setup page offers to "
            + "discard the locked session so you can sign in again. That costs you the session and "
            + "nothing else: every credential, route and key lives in Proton Pass, not in RavensPort.\n\n"
            + "There is also a way to keep the vault reachable with no gesture at all — "
            + "see \"Running unattended\" on the Settings tab.",
    };

    /// <summary>
    /// The keyring set. No gesture to describe anywhere in it, and no "cannot read it without you"
    /// either: an unlocked keyring hands the key to anything running as this user, and it says so.
    /// </summary>
    public static SessionKeyWording Keyring { get; } = new()
    {
        UnlockButton = "Unlock from the keyring",
        UnlockHeading = "Unlock from your keyring.",
        UnlockExplanation =
            " Your session key is held in your desktop's keyring, encrypted on disk. The keyring is "
            + "normally unlocked when you log in, so this usually opens without a prompt. The key is "
            + "never shown to you.",
        FirstSignInHeading = "RavensPort will ask before the sign-in starts.",
        FirstSignInExplanation =
            " RavensPort generates a session key and keeps it in your desktop's keyring. You never see "
            + "the key, and after a restart the session reopens from the keyring. While the keyring is "
            + "unlocked, any program running as you could read it too. Cancel that prompt and nothing is "
            + "created.",
        WhatIsKeptHere =
            " Every credential, route, funnel and key lives in your Proton Pass vault and nowhere else. "
            + "The one thing kept here is the Proton Pass sign-in session, and the key that encrypts it "
            + "is kept in your keyring. It is not your Proton password, and Proton never receives it.",
        ConnectPrompt =
            "RavensPort will open its own Proton Pass session, with the key kept in your keyring if you "
            + "have signed in here before. Nothing is read from your vaults until you press this.",
        NotUnlocked =
            "Not unlocked. Check that your keyring is unlocked and try again, or discard this session and sign in.",
        SignInCancelled =
            "Sign-in cancelled. Nothing was created — RavensPort needs your keyring to hold its Proton "
            + "Pass session key, because the key is never shown to you.",
        Required =
            "RavensPort needs a system keyring to sign in to Proton Pass. The session key is never shown "
            + "to you, so the keyring is what stores it and what brings it back — without one there "
            + "would be no way to reopen the session after a restart. Install and unlock a Secret Service "
            + "keyring (GNOME Keyring, or KWallet on KDE), then try again.",
        NoKeySaved = "There is no key for this session in your keyring. Discard the session and sign in again.",
        SessionKeyUnreachable =
            "There is already a Proton Pass session on this computer, encrypted with a key RavensPort "
            + "cannot reach. Unlock it from the keyring, or discard it and sign in again.",
        ConsentSecurityCheck =
            "Security check: only continue when you just asked RavensPort to connect or sign in. Otherwise choose Not now.",
        ConsentWaiting = "Waiting for the keyring…",
        ConsentFailedPrefix = "The keyring failed",
        KeyCreatedLog = "VAULT created a Proton Pass session key and stored it in the system keyring",
        PassCliInstallCommand = "sudo install -m 0755 pass-cli /usr/local/bin/pass-cli",
        PassCliInstallHint =
            "Download pass-cli from Proton, then install it where only an administrator can write — "
            + "RavensPort will not run a copy from your home folder. Then choose Check again.",
        PassCliMissing =
            "The Proton Pass CLI is not installed on this computer. Download pass-cli from Proton and "
            + "install it with \"sudo install -m 0755 pass-cli /usr/local/bin/pass-cli\", then choose "
            + "Check again.",
        StayingUnlocked =
            "RavensPort's Proton Pass session lasts until you sign out. The key that opens it lives "
            + "in your desktop's keyring, so after RavensPort restarts the session reopens from there "
            + "— usually without a prompt, because the keyring is unlocked when you log in. The key is "
            + "never displayed to you.\n\n"
            + "If the key is lost — a reset keyring, or a new computer — the setup page offers to "
            + "discard the locked session so you can sign in again. That costs you the session and "
            + "nothing else: every credential, route and key lives in Proton Pass, not in RavensPort.\n\n"
            + "There is also a way to keep the vault reachable with no session at all — "
            + "see \"Running unattended\" on the Settings tab.",
    };

    /// <summary>
    /// The set for the platform this process is running on. Declared after both sets on purpose:
    /// static initialisers run in source order, and above them it would read two nulls.
    /// </summary>
    public static SessionKeyWording Current { get; } = OperatingSystem.IsWindows() ? WindowsHello : Keyring;
}
