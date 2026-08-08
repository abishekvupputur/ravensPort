using System.Text.RegularExpressions;
using RavensPort.Core.Diagnostics;

namespace RavensPort.Core.Vault;

/// <summary>
/// Signs RavensPort in and out of Proton Pass without the user leaving the app.
///
/// The flow this wraps is <c>pass-cli login</c>, which prints a URL and then blocks until the user
/// has opened it and finished authenticating in their browser. So this is the one place the app
/// runs a CLI it expects to sit there for minutes, and the one place it reads a child's output
/// while the child is still alive.
///
/// **The URL is not logged.** Its <c>payload</c> fragment is a live, single-use authentication
/// handle — anyone who opens that link before the user does completes the sign-in as them. It goes
/// to the caller's callback and nowhere else, which is why
/// <see cref="ICliRunner.RunStreamingAsync"/> hands lines back instead of writing them down.
///
/// There is no equivalent for 1Password, and this class deliberately does not pretend otherwise:
/// <c>op</c> has no browser sign-in to drive — it wants a Secret Key and an account password on a
/// terminal — and its licence does not permit RavensPort to ship it. 1Password keeps the desktop-app
/// integration and service-account paths described in <see cref="VaultLockGuidance"/>.
/// </summary>
public sealed partial class ProtonPassAuthenticator(
    ICliRunner cliRunner,
    ProtonPassSession session,
    ProtonPassInstaller installer,
    ISessionKeyProtector helloKeyProtector,
    VaultGateService gate,
    ActivityLog activityLog)
{
    /// <summary>Whether this machine can hold the session key behind a Hello gesture.</summary>
    public Task<bool> IsHelloAvailableAsync() => helloKeyProtector.IsAvailableAsync();

    /// <summary>Whether a key is already stored that way, so the page can offer to use it.</summary>
    public bool HasHelloKey => helloKeyProtector.HasProtectedKey(session.SessionDirectory);

    /// <summary>
    /// Prompts for Hello and unlocks the session with the key it returns. The only way in, on a
    /// machine that has signed in before.
    ///
    /// **Must be called on the UI thread**, for the same reason as
    /// <see cref="PrepareSessionKeyAsync"/>.
    /// </summary>
    public async Task UnlockWithHelloAsync()
    {
        if (await helloKeyProtector.UnprotectAsync(session.SessionDirectory) is not { Length: > 0 } key)
        {
            throw new VaultCliException(
                "There is no Windows Hello key saved for this session. Discard the session and sign in again.");
        }

        session.Unlock(key);

        // Proton Pass only, deliberately. A full evaluation here probes 1Password too, and on a
        // machine that has both installed that turned one Hello gesture into a gesture followed by
        // a stack of 1Password desktop approvals — for a manager the user was not connecting.
        // The session that just opened is this one, so this is the only one with a new answer.
        await gate.ConnectAsync(VaultBackendKind.ProtonPass).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates the session key and puts it behind Hello — before <see cref="SignInAsync"/> runs,
    /// and before there is any session for it to open.
    ///
    /// **The order is the point.** Protecting the key afterwards, as an offer, meant a user who
    /// declined ended up with a live session on disk whose key existed only in this process's
    /// memory and was shown to nobody. That session became unopenable the moment the app closed,
    /// and nothing in the UI said so. Doing it first makes the failure harmless: no key was
    /// protected, so no sign-in happens, so there is nothing left behind to be stranded.
    ///
    /// It throws for the same reason. Every failure here — cancelled gesture, locked-out Hello, a
    /// Credential Manager write that did not take — has to stop the sign-in, not be logged past.
    ///
    /// **Must be called on the UI thread.** The Hello prompt parents itself to the foreground
    /// window; from a thread-pool thread the credential service returns UserCanceled without ever
    /// showing anything, which is indistinguishable from a refusal. Nothing in here uses
    /// ConfigureAwait(false) for that reason.
    /// </summary>
    public async Task PrepareSessionKeyAsync()
    {
        // Already holding one: a retry after a sign-in that failed for its own reasons, where the
        // key is protected and re-prompting would be asking for a gesture that changes nothing.
        if (session.HasKey && HasHelloKey) return;

        // A new key would make an existing session unopenable — it is what encrypts it. Refused
        // outright rather than warned about: the recovery is to discard deliberately, and a
        // confirmation dialog here would be one careless click away from the same damage.
        if (session.HasSessionOnDisk)
        {
            throw new VaultCliException(
                "There is already a Proton Pass session on this PC, encrypted with a key RavensPort "
                + "cannot reach. Unlock it with Windows Hello, or discard it and sign in again.");
        }

        if (!await helloKeyProtector.IsAvailableAsync())
        {
            throw new VaultCliException(HelloRequired);
        }

        // Generated here and never returned. It reaches exactly two places: the Hello-encrypted
        // blob in the Credential Manager, and the environment of the pass-cli child process.
        var key = ProtonPassSession.GenerateKey();

        await helloKeyProtector.ProtectAsync(session.SessionDirectory, key);

        // Only once the key is safely stored. Unlocking first would leave a window in which the
        // app holds a key it could sign in with but could never recover.
        session.Unlock(key);

        activityLog.Log("VAULT created a Proton Pass session key and protected it with Windows Hello");
    }

    /// <summary>
    /// What the setup page says when in-app sign-in cannot be offered. Public so the message is
    /// written once — the rule it states is a security decision, not UI copy.
    /// </summary>
    public const string HelloRequired =
        "RavensPort needs Windows Hello to sign in to Proton Pass. The session key is never shown "
        + "to you, so Windows Hello is what stores it and what brings it back — without it there "
        + "would be no way to reopen the session after a restart. Set up Windows Hello in Windows "
        + "Settings → Accounts → Sign-in options, then try again.";

    /// <summary>
    /// Finds pass-cli, downloading the pinned release if the machine has none. Returns its path.
    /// </summary>
    public async Task<string> EnsureInstalledAsync(
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        // An existing install always wins — the user's own pass-cli, at whatever version they
        // maintain, is not something the app should quietly route around.
        if (VaultProbe.FindProtonPass() is { } existing && File.Exists(existing)) return existing;

        return await installer.InstallAsync(progress, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs the browser sign-in. Calls <paramref name="onUrl"/> once, with the URL the user has to
    /// open, then returns when they have finished — or throws if they did not.
    /// </summary>
    /// <param name="onUrl">
    /// Raised as soon as the URL appears, which is well before this method returns. That is the
    /// whole point: the user cannot complete a sign-in they have not been shown.
    /// </param>
    public async Task SignInAsync(
        Action<string> onUrl,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        if (!session.HasKey)
        {
            throw new VaultCliException(
                "RavensPort has no session key yet. Call PrepareSessionKeyAsync first — it creates one "
                + "and protects it with Windows Hello before any session exists to open.");
        }

        var exePath = await EnsureInstalledAsync(progress, ct).ConfigureAwait(false);

        progress?.Report("Starting sign-in…");

        var urlSeen = false;
        CliResult result;

        try
        {
            result = await cliRunner.RunStreamingAsync(
                exePath,
                ["login"],
                line =>
                {
                    if (urlSeen || ExtractUrl(line) is not { } url) return;

                    urlSeen = true;
                    onUrl(url);
                    progress?.Report("Waiting for you to finish signing in…");
                },
                session.BuildEnvironment(),
                CliRunner.InteractiveTimeout,
                ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Cancelled or timed out. `login` writes its session files before the browser step
            // completes, so a run that did not finish still leaves a half-session behind — and
            // pass-cli's own `logout` refuses that state ("Session is some but is not logged in"),
            // which would block the next attempt. Clear it here instead.
            await AbandonAsync().ConfigureAwait(false);
            throw;
        }

        if (!result.Succeeded)
        {
            await AbandonAsync().ConfigureAwait(false);

            var detail = result.FirstErrorLine();
            activityLog.Log($"VAULT Proton Pass sign-in failed with exit {result.ExitCode}");

            throw new VaultCliException(detail.Length > 0
                ? $"Signing in to Proton Pass failed: {detail}"
                : "Signing in to Proton Pass failed.");
        }

        if (!urlSeen)
        {
            // Succeeded without ever printing a URL: possible if a session was already valid.
            // Worth a log line, because it means the UI showed the user nothing to do and they
            // may reasonably wonder what happened.
            activityLog.Log("VAULT Proton Pass sign-in completed without showing a URL");
        }

        activityLog.Log("VAULT signed in to Proton Pass");
        progress?.Report("Signed in. Loading your vault…");

        // No Hello prompt here, and none needed: the key was created and protected before this
        // method ran. That also sidesteps the thread problem — every await above uses
        // ConfigureAwait(false), so by this line execution is on a thread-pool thread, and the
        // Hello prompt needs a foreground window to parent itself to. Without one the credential
        // service does not prompt at all, it returns UserCanceled immediately.
        // This manager only, and in full: the sign-in just happened, so asking pass-cli everything
        // costs the user nothing. Evaluating both would spend that goodwill on 1Password, which
        // nobody has signed into and which answers by prompting.
        await gate.ConnectAsync(VaultBackendKind.ProtonPass, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Undoes a sign-in that did not complete: the half-written session, the protected key, and the
    /// copy in memory.
    ///
    /// All three, because any one left behind is a trap. Session files without a key are unopenable
    /// and block the next <c>login</c>. A protected key without a session is a Hello prompt that
    /// unlocks nothing. And a key still in memory would let the next attempt sign in without
    /// protecting anything — which is the exact state this class is arranged to prevent.
    /// </summary>
    private async Task AbandonAsync()
    {
        session.Wipe();

        // Safe from any thread: removing the stored key does not prompt. The Hello credential is
        // left in place on purpose, so the retry this user is likely about to make asks for one
        // gesture rather than two.
        await helloKeyProtector.ForgetAsync(session.SessionDirectory).ConfigureAwait(false);

        session.Clear();
    }

    /// <summary>
    /// Ends the session: remotely if Proton can be reached, locally regardless, and then puts the
    /// app back to its disconnected state.
    /// </summary>
    public async Task SignOutAsync(CancellationToken ct = default)
    {
        var exePath = VaultProbe.FindProtonPass();

        if (exePath is not null && File.Exists(exePath) && session.HasKey)
        {
            var result = await TryLogoutAsync(exePath, force: false, ct).ConfigureAwait(false);

            if (result?.Succeeded != true)
            {
                // --force skips the remote call. The session then stays listed in the user's Proton
                // account until it expires, so this is the fallback rather than the default — but a
                // sign-out that cannot proceed because Proton is unreachable is worse.
                activityLog.Log("VAULT Proton Pass remote logout failed; clearing the local session");
                await TryLogoutAsync(exePath, force: true, ct).ConfigureAwait(false);
            }
        }

        // The stored key, which no longer lives inside the session directory and so would not go
        // with Wipe. The Hello credential itself deliberately stays — it keys nothing once this
        // returns, and removing it would cost the user an extra gesture on the next sign-in. See
        // HelloKeyProtector.ForgetAsync.
        await helloKeyProtector.ForgetAsync(session.SessionDirectory).ConfigureAwait(false);

        // Unconditional, and in this order: the files are worthless without the key, but leaving
        // either behind would let a later "sign in" resume a session the user just ended.
        session.Wipe();
        session.Clear();

        gate.Disconnect();
    }

    /// <summary>
    /// Throws away the local session without telling Proton — the only recovery available to
    /// someone who has lost their session key.
    ///
    /// <see cref="SignOutAsync"/> cannot help there. Every pass-cli call needs the key: it is what
    /// decrypts the session, so <c>logout</c> without it cannot reach the session it is meant to
    /// end. Running it anyway would be worse than useless — with no session directory to point at,
    /// pass-cli would fall back to the user's own default session and sign *that* out instead.
    ///
    /// So this deletes the files and stops. The session stays live at Proton until it expires, and
    /// the user can revoke it under their Proton account's sessions list if they want it gone
    /// sooner. Nothing in the vault is touched.
    /// </summary>
    public async Task DiscardLocalSessionAsync()
    {
        // The stored key goes. The Hello credential stays and is reused by the sign-in this user is
        // about to do — it is not what failed, and recreating it would ask for a second gesture.
        await helloKeyProtector.ForgetAsync(session.SessionDirectory).ConfigureAwait(false);

        session.Wipe();
        session.Clear();

        activityLog.Log("VAULT discarded the local Proton Pass session — the key that opened it was lost");
    }

    private async Task<CliResult?> TryLogoutAsync(string exePath, bool force, CancellationToken ct)
    {
        try
        {
            return await cliRunner.RunAsync(
                exePath,
                force ? ["logout", "--force"] : ["logout"],
                stdin: null,
                session.BuildEnvironment(),
                CliRunner.WriteTimeout,
                ct).ConfigureAwait(false);
        }
        catch (VaultCliException ex)
        {
            // Never allowed to fail a sign-out. The local state is cleared by the caller either
            // way, and the user asked to be signed out, not to be told why the CLI would not
            // cooperate.
            activityLog.Log($"VAULT Proton Pass logout could not run: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Pulls the sign-in URL out of a line of CLI output.
    ///
    /// Matched by shape rather than by the surrounding prose ("Please open the following URL…"),
    /// which is wording a future pass-cli release is free to change. The host is checked, so a
    /// stray link in a warning or a deprecation notice cannot be mistaken for the one the user is
    /// supposed to open.
    /// </summary>
    internal static string? ExtractUrl(string line)
    {
        var match = UrlPattern().Match(line ?? "");
        if (!match.Success) return null;

        var url = match.Value.TrimEnd('.', ',', ')');

        return Uri.TryCreate(url, UriKind.Absolute, out var parsed)
               && parsed.Scheme == Uri.UriSchemeHttps
               && (parsed.Host == "account.proton.me" || parsed.Host.EndsWith(".proton.me", StringComparison.Ordinal))
            ? url
            : null;
    }

    [GeneratedRegex(@"https://\S+")]
    private static partial Regex UrlPattern();
}
