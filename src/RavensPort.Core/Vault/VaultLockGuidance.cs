namespace RavensPort.Core.Vault;

/// <summary>
/// What to tell a user whose password manager keeps locking, and how to sign in in the first place.
///
/// Written carefully on purpose. The obvious advice — lengthen the auto-lock timeout — is advice to
/// weaken a security control: that timeout exists precisely to limit how long an unattended machine
/// holds decrypted secrets. So the options that cost nothing come first, and where the trade-off is
/// real it is stated rather than buried. The app never changes these settings itself; they are the
/// user's to decide.
/// </summary>
public static class VaultLockGuidance
{
    public static string DisplayName(VaultBackendKind kind) => kind switch
    {
        VaultBackendKind.OnePassword => "1Password",
        VaultBackendKind.ProtonPass => "Proton Pass",

        // Reads correctly in the sentences this feeds — "Everything is saved to this session's
        // memory" — which is exactly the thing a single-use user needs reminding of.
        VaultBackendKind.SingleUse => "this session's memory",

        _ => "your password manager",
    };

    public static string InstallCommand(VaultBackendKind kind) => kind switch
    {
        VaultBackendKind.OnePassword => "",
        VaultBackendKind.ProtonPass => "winget install Proton.PassCLI",
        _ => "",
    };

    // There is deliberately no download URL here, and no in-app installer anywhere. Getting a
    // password manager onto this PC is the user's own business: the winget line above is the whole
    // of the help RavensPort offers, and it neither fetches software nor sends anyone to a page to
    // fetch it. See ManagerCardViewModel.ShowInstall for what the setup page shows instead.

    /// <summary>How to get from "installed" to "signed in".</summary>
    public static string SignInSteps(VaultBackendKind kind) => kind switch
    {
        VaultBackendKind.OnePassword =>
            "In the 1Password desktop app, open Settings → Developer and turn on "
            + "\"Integrate with 1Password CLI\". Then unlock 1Password.",

        // No terminal instructions here, deliberately. RavensPort keeps a pass-cli session of its
        // own — its own session directory, encrypted with a key only it holds — so a `pass-cli
        // login` typed in a terminal signs in that terminal and leaves this card exactly as it is.
        // Saying so is the point: the failure it prevents is someone signing in successfully,
        // twice, and concluding the app is broken.
        // Only the one thing the steps below cannot say for themselves. What to actually do is
        // rendered as the next action on the card, in the state the user is in — repeating it here
        // put "sign in below" above a box that could not sign anyone in yet.
        VaultBackendKind.ProtonPass =>
            "This session belongs to RavensPort alone. Signing in with \"pass-cli\" in a terminal "
            + "does not sign in here, and signing out there will not interrupt the proxy.",

        _ => "",
    };

    /// <summary>
    /// How to stop the vault locking between saves.
    ///
    /// Deliberately only the steps taken in the password manager itself. The token option lives in
    /// <see cref="UnattendedTokenSteps"/> and is shown on the Settings tab instead: this text
    /// appears in the banner over the tabs, where a user is being interrupted mid-task and needs
    /// the thing they can do in the next thirty seconds — not a walkthrough of creating,
    /// scoping and installing a long-lived credential.
    /// </summary>
    public static string StayingUnlockedSteps(VaultBackendKind kind) => kind switch
    {
        VaultBackendKind.OnePassword =>
            "Longer unlock window:\n"
            + "1Password → Settings → Security, and raise \"Lock after\". You can also turn off "
            + "\"Lock on sleep\" and \"Lock when the screen saver starts\".\n\n"
            + "That is a real trade: the timeout exists to limit how long this machine holds your "
            + "secrets decrypted while you are away from it. Raise it only on a machine you would "
            + "be comfortable leaving unlocked for that long.\n\n"
            + "There is also a way to keep the vault reachable without leaving anything unlocked — "
            + "see \"Running unattended\" on the Settings tab.",

        VaultBackendKind.ProtonPass =>
            "RavensPort's Proton Pass session lasts until you sign out. The key that opens it lives "
            + "in Windows Credential Manager, encrypted so that only a Windows Hello gesture on this "
            + "PC can decrypt it — so after RavensPort restarts, a gesture unlocks the session. The "
            + "key is never displayed to you, and RavensPort cannot read it without you.\n\n"
            + "If a gesture stops working — Hello reset, or a new PC — the setup page offers to "
            + "discard the locked session so you can sign in again. That costs you the session and "
            + "nothing else: every credential, route and key lives in Proton Pass, not in RavensPort.\n\n"
            + "There is also a way to keep the vault reachable with no gesture at all — "
            + "see \"Running unattended\" on the Settings tab.",

        _ => "",
    };

    /// <summary>
    /// The token option, for a machine that should never show an unlock prompt. Kept out of the
    /// lock banner (see <see cref="StayingUnlockedSteps"/>) and shown on the Settings tab, where
    /// setting up a long-lived credential is a decision being made rather than an interruption.
    /// </summary>
    public static string UnattendedTokenSteps(VaultBackendKind kind) => kind switch
    {
        // Names the setup page rather than OP_SERVICE_ACCOUNT_TOKEN, which is what this used to say
        // and which nothing ever read. An environment variable would also defeat the point: it
        // survives restarts, which is the same as storing the credential, in a place every process
        // the user runs can read.
        VaultBackendKind.OnePassword =>
            "Create a 1Password service account, grant it access to this vault specifically, and "
            + "paste its token on the setup page under \"Service account token\". Nothing then has "
            + "to stay unlocked, and 1Password itself does not need to be installed on this PC at "
            + "all. A service account cannot see your Private vault, so it reaches only what you "
            + "gave it.\n\n"
            + "The token is never written anywhere — not to disk, not to the vault — so it has to "
            + "be pasted again after every restart. That is the trade: an install set to start at "
            + "login serves nothing until someone enters it.",

        VaultBackendKind.ProtonPass =>
            "Create a Proton Pass personal access token scoped to this vault and put it in the "
            + "PROTON_PASS_PERSONAL_ACCESS_TOKEN environment variable. Nothing then has to stay "
            + "signed in interactively. However, PERSONAL ACCESS TOKENS are read only.",

        _ => "",
    };

    /// <summary>
    /// The one thing a 1Password user has to know that nothing on screen would otherwise tell them:
    /// the desktop app has to stay running, and getting back from a restart has an order to it.
    ///
    /// The reason, established by experiment and reported as
    /// <see href="https://github.com/1Password/onepassword-ipc-client/issues/9">ipc-client#9</see>:
    /// 1Password stages its own <c>op_sdk_ipc_client.dll</c> to an unprotected location when the app
    /// starts, and a DLL mapped by another process cannot be moved on Windows. The move fails with a
    /// sharing violation and 1Password treats that as fatal to its whole SDK IPC server, so the
    /// integration channel is never created — silently, with no retry, for the life of that app
    /// process. RavensPort loads that DLL on its first vault read and the SDK never releases it, so
    /// RavensPort is the process in the way.
    ///
    /// Hence the sequence, and hence spelling it out rather than hinting: the obvious repair —
    /// restart 1Password — is the one that does not work.
    ///
    /// This applies to desktop app integration only. A service account never loads that library, so
    /// the whole problem is absent from that mode, which is why the text below points at it.
    ///
    /// Empty for Proton Pass, which owns its session outright and has no such dependency.
    /// </summary>
    public static string DesktopAppRequirement(VaultBackendKind kind) => kind switch
    {
        // One line, because it sits on two screens the user reads past constantly and the long
        // version was mostly explanation. What survives is the part they cannot guess: restarting
        // 1Password by itself does not work, and the recovery has an order.
        VaultBackendKind.OnePassword =>
            "Keep 1Password running. If it restarts, restarting it alone will not reconnect — "
            + "quit both, start 1Password, then RavensPort. A service account token avoids this "
            + "entirely and needs no desktop app.",

        _ => "",
    };

    /// <summary>
    /// The known defect behind desktop app integration, in one line, shown the moment that mode is
    /// chosen rather than after it fails.
    ///
    /// Said up front because the failure is silent, permanent for the life of the 1Password process,
    /// and the obvious repair does not work — so a user who meets it without warning concludes
    /// RavensPort is broken. Naming it as reported and pending keeps that honest in both directions:
    /// it is not RavensPort's to fix, and it is not being ignored.
    /// </summary>
    public static string DesktopAppKnownIssue(VaultBackendKind kind) => kind switch
    {
        VaultBackendKind.OnePassword =>
            "Known issue: if 1Password starts while RavensPort is already running, it will not open "
            + "its integration channel and this mode stops working until both are restarted in order "
            + "— reported to 1Password, fix pending on their side.",

        _ => "",
    };

    /// <summary>
    /// What a service-account token actually is, said before the user pastes one in.
    ///
    /// A scoped vault is not the same as a scoped risk. The token is a bearer credential: whoever
    /// holds the string is the service account, from any machine, until it is rotated. None of that
    /// is obvious from a box labelled "token", and the mistakes it invites — a text file, a chat
    /// message, a borrowed laptop — are the kind that are not noticed until much later.
    /// </summary>
    public static string ServiceTokenWarning(VaultBackendKind kind) => kind switch
    {
        VaultBackendKind.OnePassword =>
            "This token is a key to every vault the service account can reach, from any machine, "
            + "until you rotate it — scoping the vault limits what it opens, not who can use it. "
            + "Never keep it in plain text. Never enter it on a PC you do not own and trust. "
            + "Never share it with anyone.",

        _ => "",
    };

    /// <summary>
    /// Why a 1Password service account still cannot see anything by default. Left out of the
    /// generic text because getting this wrong produces an empty vault list and no clue why.
    /// </summary>
    public static string? TokenCaveat(VaultBackendKind kind) => kind switch
    {
        VaultBackendKind.OnePassword =>
            $"A service account must be granted access to \"{VaultConstants.VaultName}\" explicitly — "
            + "it cannot use your built-in Private vault, and without the grant it sees no vaults at all.",

        _ => null,
    };
}
