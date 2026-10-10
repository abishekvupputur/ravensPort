using RavensPort.Core.Diagnostics;

namespace RavensPort.Core.Vault;

/// <summary>
/// A pass-cli session opened with a Proton Pass personal access token, for the headless host.
///
/// **What a PAT is.** A scoped, revocable credential made with <c>pass-cli pat create</c> and
/// granted to particular vaults, so a server never holds the user's whole account. The documented
/// way to use one is <c>pass-cli login</c> with the token in <c>PROTON_PASS_PERSONAL_ACCESS_TOKEN</c>;
/// that writes a session, and every later command runs against the session.
///
/// **Its own session, nowhere near the desktop's.** The session directory is per process and
/// thrown away on exit, and the key that encrypts it is random and held only in memory
/// (<c>PROTON_PASS_KEY_PROVIDER=env</c>, through the <see cref="ProtonPassSession"/> this wraps).
/// A desktop RavensPort on the same machine keeps its own session under its own directory, and
/// neither can sign the other out.
///
/// **Why the token is kept in memory.** PAT sessions last two hours and cannot be locked, so a
/// server that runs longer has to log in again — and logging in again needs the token. It is held
/// for exactly that and leaves only through a child process's environment: never an argument,
/// where any process on the machine could read it from the command line, and never a file.
/// </summary>
public sealed class ProtonPassPatSession(
    ProtonPassSession session,
    ICliRunner cliRunner,
    ActivityLog activityLog,
    string? exePathOverride = null,
    TimeProvider? timeProvider = null)
{
    /// <summary>What Proton Pass personal access tokens start with.</summary>
    private const string ExpectedPrefix = "pst_";

    /// <summary>
    /// How long a session is trusted before the next vault call logs in again. Proton's limit is
    /// two hours; the margin is for a call that starts just before it.
    /// </summary>
    public static readonly TimeSpan RefreshAfter = TimeSpan.FromMinutes(100);

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim _loginLock = new(1, 1);

    private string? _token;
    private DateTimeOffset? _loggedInAt;

    /// <summary>
    /// Where a headless session goes: under the runtime directory on Linux, which is tmpfs and
    /// private to the user, and under the app's own folder elsewhere. Per process, so two hosts
    /// never share one.
    /// </summary>
    public static string DefaultDirectory()
    {
        var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        var root = !OperatingSystem.IsWindows() && !string.IsNullOrEmpty(runtime)
            ? Path.Combine(runtime, "ravensport")
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RavensPort");

        return Path.Combine(root, $"pass-pat-{Environment.ProcessId}");
    }

    public bool HasToken => _token is { Length: > 0 };

    /// <summary>
    /// Accepts the token. Whitespace-trimmed, because copying from a terminal adds a newline and
    /// that is not a reason to tell someone their token is wrong.
    /// </summary>
    public void SetToken(string? token)
    {
        var trimmed = (token ?? "").Trim();

        if (trimmed.Length == 0)
        {
            throw new VaultCliException("A Proton Pass personal access token is required.");
        }

        if (!trimmed.StartsWith(ExpectedPrefix, StringComparison.Ordinal))
        {
            throw new VaultCliException(
                "That does not look like a Proton Pass personal access token — they begin with "
                + $"\"{ExpectedPrefix}\". Create one with `pass-cli pat create`, and grant it access to "
                + "the vault RavensPort should use.");
        }

        _token = trimmed;
    }

    /// <summary>
    /// Logs in unless a session younger than <see cref="RefreshAfter"/> is already open.
    /// </summary>
    public async Task EnsureLoggedInAsync(CancellationToken ct = default)
    {
        if (_loggedInAt is { } at && _time.GetUtcNow() - at < RefreshAfter) return;

        await LoginAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Opens a fresh session. Any previous one is wiped first: pass-cli refuses to log in over a
    /// session it already holds, and an expired one is no use to anybody.
    /// </summary>
    public async Task LoginAsync(CancellationToken ct = default)
    {
        if (_token is not { Length: > 0 } token)
        {
            throw new VaultCliException("No Proton Pass personal access token has been given.");
        }

        await _loginLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var exePath = exePathOverride ?? ProtonPassAuthenticator.RequireInstalledCli();

            if (Directory.Exists(session.SessionDirectory)) session.Wipe();

            // A fresh key per session, so nothing written under an earlier one can be opened with
            // what is in memory now.
            session.Unlock(ProtonPassSession.GenerateKey());

            var env = new Dictionary<string, string>(session.BuildEnvironment())
            {
                ["PROTON_PASS_PERSONAL_ACCESS_TOKEN"] = token,
            };

            var result = await cliRunner.RunAsync(exePath, ["login"], env: env, ct: ct).ConfigureAwait(false);

            if (!result.Succeeded)
            {
                _loggedInAt = null;
                var detail = result.FirstErrorLine();
                activityLog.Log($"VAULT Proton Pass personal-access-token login failed with exit {result.ExitCode}");

                throw new VaultCliException(detail.Length > 0
                    ? $"Logging in to Proton Pass with the personal access token failed: {detail}"
                    : "Logging in to Proton Pass with the personal access token failed.");
            }

            _loggedInAt = _time.GetUtcNow();
            activityLog.Log("VAULT logged in to Proton Pass with a personal access token");
        }
        finally
        {
            _loginLock.Release();
        }
    }

    /// <summary>
    /// Ends the session: a best-effort <c>logout</c>, then the directory is deleted and the token
    /// and key forgotten. Never throws — this runs on the way out.
    /// </summary>
    public async Task EndAsync()
    {
        try
        {
            var exePath = exePathOverride ?? VaultProbe.FindProtonPass();

            if (_loggedInAt is not null && exePath is not null && session.HasKey)
            {
                await cliRunner.RunAsync(exePath, ["logout"], env: session.BuildEnvironment(),
                    timeout: TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            activityLog.Log($"VAULT Proton Pass logout failed, removing the session locally: {ex.Message}");
        }

        session.Wipe();
        session.Clear();
        _token = null;
        _loggedInAt = null;
    }
}
