namespace RavensPort.Core.Vault;

/// <summary>
/// Everything the user is told about where RavensPort's Proton Pass session key lives, in the words
/// of the platform actually running.
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
/// The 1Password token-saving copy is not here: it is Windows-only, because Linux offers no way to
/// keep the token at all (see <see cref="UnavailableServiceTokenProtector"/>).
/// </summary>
public static class SessionKeyWording
{
    private static readonly bool Hello = OperatingSystem.IsWindows();

    /// <summary>The button that opens an existing session.</summary>
    public static string UnlockButton { get; } = Hello ? "Unlock with Windows Hello" : "Unlock from the keyring";

    public static string UnlockHeading { get; } = Hello ? "Unlock with Windows Hello." : "Unlock from your keyring.";

    public static string UnlockExplanation { get; } = Hello
        ? " Your session key is held in Windows Credential Manager, encrypted so that only a Hello "
          + "gesture on this PC can decrypt it. RavensPort cannot read it without you, and it is never "
          + "shown to you either."
        : " Your session key is held in your desktop's keyring, encrypted on disk. The keyring is "
          + "normally unlocked when you log in, so this usually opens without a prompt. The key is "
          + "never shown to you.";

    /// <summary>Said before the first sign-in, ahead of the prompt it announces.</summary>
    public static string FirstSignInHeading { get; } = Hello
        ? "Windows Hello will ask before the sign-in starts."
        : "RavensPort will ask before the sign-in starts.";

    public static string FirstSignInExplanation { get; } = Hello
        ? " RavensPort generates a session key, encrypts it so that only a Hello gesture on this PC can "
          + "decrypt it, and keeps it in Windows Credential Manager. You never see the key, and after a "
          + "restart a gesture is all it takes to reopen the session. Cancel that prompt and nothing is "
          + "created."
        : " RavensPort generates a session key and keeps it in your desktop's keyring. You never see "
          + "the key, and after a restart the session reopens from the keyring. While the keyring is "
          + "unlocked, any program running as you could read it too. Cancel that prompt and nothing is "
          + "created.";

    /// <summary>The tail of "RavensPort stores none of your data here".</summary>
    public static string WhatIsKeptHere { get; } = Hello
        ? " Every credential, route, funnel and key lives in your Proton Pass vault and nowhere else. "
          + "The one thing kept here is the Proton Pass sign-in session, and the key that encrypts it "
          + "never leaves Windows Credential Manager unencrypted. It is not your Proton password, and "
          + "Proton never receives it."
        : " Every credential, route, funnel and key lives in your Proton Pass vault and nowhere else. "
          + "The one thing kept here is the Proton Pass sign-in session, and the key that encrypts it "
          + "is kept in your keyring. It is not your Proton password, and Proton never receives it.";

    /// <summary>What Connect on the Proton Pass card will do.</summary>
    public static string ConnectPrompt { get; } = Hello
        ? "RavensPort will open its own Proton Pass session, which means a Windows Hello gesture if you "
          + "have signed in here before. Nothing is read from your vaults until you press this."
        : "RavensPort will open its own Proton Pass session, with the key kept in your keyring if you "
          + "have signed in here before. Nothing is read from your vaults until you press this.";

    public static string NotUnlocked { get; } = Hello
        ? "Not unlocked. Try Windows Hello again, or discard this session and sign in."
        : "Not unlocked. Check that your keyring is unlocked and try again, or discard this session and sign in.";

    public static string SignInCancelled { get; } = Hello
        ? "Sign-in cancelled. Nothing was created — RavensPort needs Windows Hello to hold its Proton "
          + "Pass session key, because the key is never shown to you."
        : "Sign-in cancelled. Nothing was created — RavensPort needs your keyring to hold its Proton "
          + "Pass session key, because the key is never shown to you.";

    /// <summary>Why in-app sign-in cannot be offered on this machine.</summary>
    public static string Required { get; } = Hello
        ? "RavensPort needs Windows Hello to sign in to Proton Pass. The session key is never shown "
          + "to you, so Windows Hello is what stores it and what brings it back — without it there "
          + "would be no way to reopen the session after a restart. Set up Windows Hello in Windows "
          + "Settings → Accounts → Sign-in options, then try again."
        : "RavensPort needs a system keyring to sign in to Proton Pass. The session key is never shown "
          + "to you, so the keyring is what stores it and what brings it back — without one there "
          + "would be no way to reopen the session after a restart. Install and unlock a Secret Service "
          + "keyring (GNOME Keyring, or KWallet on KDE), then try again.";

    public static string NoKeySaved { get; } = Hello
        ? "There is no Windows Hello key saved for this session. Discard the session and sign in again."
        : "There is no key for this session in your keyring. Discard the session and sign in again.";

    public static string SessionKeyUnreachable { get; } = Hello
        ? "There is already a Proton Pass session on this PC, encrypted with a key RavensPort cannot "
          + "reach. Unlock it with Windows Hello, or discard it and sign in again."
        : "There is already a Proton Pass session on this computer, encrypted with a key RavensPort "
          + "cannot reach. Unlock it from the keyring, or discard it and sign in again.";

    /// <summary>The standing rule on the consent window, under whatever it is asking for.</summary>
    public static string ConsentSecurityCheck { get; } = Hello
        ? "Security check: only continue when you just asked RavensPort to use Windows Hello. Otherwise choose Not now."
        : "Security check: only continue when you just asked RavensPort to connect or sign in. Otherwise choose Not now.";

    public static string ConsentWaiting { get; } = Hello ? "Waiting for Windows Hello…" : "Waiting for the keyring…";

    public static string ConsentFailed(string message) =>
        Hello ? $"Windows Hello failed: {message}" : $"The keyring failed: {message}";

    /// <summary>For the activity log, once the key is stored.</summary>
    public static string KeyCreatedLog { get; } = Hello
        ? "VAULT created a Proton Pass session key and protected it with Windows Hello"
        : "VAULT created a Proton Pass session key and stored it in the system keyring";
}
