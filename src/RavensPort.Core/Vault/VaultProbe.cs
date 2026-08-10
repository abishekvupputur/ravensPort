using System.Text.RegularExpressions;

namespace RavensPort.Core.Vault;

/// <summary>
/// Finds a password-manager CLI on this machine.
///
/// Deliberately does not shell out to <c>where.exe</c>: that is a process launch to answer a
/// question about the filesystem, it inherits whatever PATH quirks the shell has, and it runs on
/// the startup path where the app is already blocking on a gate check. Walking PATH in-process is
/// both faster and easier to test.
/// </summary>
public static partial class VaultProbe
{
    /// <summary>
    /// Escape hatch for an install this does not know about — a portable copy, a package manager
    /// that puts binaries somewhere new, or a build under test. Cheaper for a user than waiting
    /// for a release that adds their path to the list below.
    /// </summary>
    public const string OnePasswordPathVariable = "RAVENSPORT_OP_PATH";
    public const string ProtonPassPathVariable = "RAVENSPORT_PASSCLI_PATH";

    /// <summary>
    /// `op` gained the `item`/`vault` nouns and --format json in 2.0. Anything older cannot do
    /// what this app needs, and failing here is clearer than a confusing parse error later.
    /// </summary>
    public static readonly Version MinimumOnePasswordVersion = new(0, 4);

    public static string? FindOnePassword() => Find(
        OnePasswordPathVariable,
        "op.exe",
        [
            Path.Combine(Env("ProgramFiles"), "1Password CLI", "op.exe"),
            Path.Combine(Env("LOCALAPPDATA"), "Microsoft", "WinGet", "Links", "op.exe"),
            Path.Combine(Env("LOCALAPPDATA"), "Programs", "1Password CLI", "op.exe"),
        ]);

    public static string? FindProtonPass() => Find(
        ProtonPassPathVariable,
        "pass-cli.exe",
        [
            // Where the Proton Pass installer actually puts it.
            Path.Combine(Env("LOCALAPPDATA"), "Programs", "ProtonPass", "pass-cli.exe"),
            Path.Combine(Env("LOCALAPPDATA"), "Microsoft", "WinGet", "Links", "pass-cli.exe"),
            Path.Combine(Env("ProgramFiles"), "Proton", "Pass CLI", "pass-cli.exe"),
            Path.Combine(Env("USERPROFILE"), ".cargo", "bin", "pass-cli.exe"),

            // Last: a copy left behind by RavensPort 4.3.0 or earlier, which could fetch pass-cli
            // itself. That feature is gone — the app installs no software now — but an existing
            // copy still runs, and refusing to see it would break working setups on upgrade. Behind
            // every real install, so a user who manages their own pass-cli keeps control of which
            // one runs.
            LegacyDownloadedProtonPass,
        ]);

    /// <summary>
    /// Where the removed in-app installer used to unpack pass-cli. Read-only history: nothing
    /// writes here any more.
    /// </summary>
    private static string LegacyDownloadedProtonPass { get; } = Path.Combine(
        Env("LOCALAPPDATA"), "RavensPort", "cli", "pass-cli", "2.2.4", "pass-cli.exe");

    /// <summary>
    /// Env override, then PATH, then the places installers actually use.
    /// </summary>
    /// <param name="pathValue">
    /// The PATH to search, defaulting to this process's plus the User and Machine ones. A
    /// parameter so tests can point it at a directory of stubs without mutating the real one —
    /// which the rest of the suite, and the .NET runtime underneath it, are entitled to rely on.
    /// </param>
    public static string? Find(
        string environmentVariable,
        string exeName,
        IReadOnlyList<string> wellKnownPaths,
        string? pathValue = null)
    {
        if (Environment.GetEnvironmentVariable(environmentVariable) is { Length: > 0 } overridden)
        {
            // Honoured even when it does not exist: silently falling back would leave the user
            // staring at "not installed" while their override sat there being ignored.
            return File.Exists(overridden) ? overridden : null;
        }

        foreach (var directory in SearchDirectories(pathValue))
        {
            string candidate;
            try
            {
                candidate = Path.Combine(directory.Trim('"'), exeName);
            }
            catch (ArgumentException)
            {
                // A PATH entry with invalid path characters. Common enough on a well-used machine
                // that it must not take the whole probe down.
                continue;
            }

            if (File.Exists(candidate)) return RealBinary(candidate);
        }

        return wellKnownPaths.FirstOrDefault(File.Exists) is { } known ? RealBinary(known) : null;
    }

    /// <summary>
    /// The actual executable behind whatever was found, following a symlink to its target.
    ///
    /// WinGet installs both CLIs as symlinks in its Links directory, which is the copy that lands on
    /// PATH and therefore the one this probe finds first. A symlink is a zero-byte reparse point
    /// carrying no signature of its own, so everything downstream has to see through it — and
    /// resolving here means it is seen through once, at the point the answer is decided, rather than
    /// separately by the signature check, the trust cache and the launcher.
    ///
    /// That matters because the fallback when resolution fails is not neutral. A signature read
    /// against the link itself reports "not signed at all" and RavensPort refuses to run it, which
    /// is what a user hit: 1Password's CLI is validly signed by AgileBits, and the app declined it
    /// anyway because a momentary failure to follow the link left it inspecting an empty file.
    ///
    /// When the link cannot be followed the link is still returned, because reporting an installed
    /// CLI as missing would be a worse lie than reporting it unverified. What stops that becoming
    /// the same wrong refusal is <see cref="ExecutableSignature"/>, which distinguishes "this file
    /// carries no signature" from "this is a link I could not follow".
    /// </summary>
    private static string RealBinary(string path)
    {
        var resolved = ExecutableSignature.ResolveFinalTarget(path);

        return File.Exists(resolved) ? resolved : path;
    }

    /// <summary>
    /// Every directory worth looking in, in order.
    ///
    /// The process PATH alone is not enough, and getting this wrong produces the worst possible
    /// version of the setup page. Installing a CLI updates the User PATH, but a process that was
    /// already running — Explorer, and so everything launched from it — keeps the PATH it started
    /// with. So the app says "not installed" about a binary that is plainly installed, and
    /// "Check again" cannot fix it because the stale PATH outlives the check. Reading the User and
    /// Machine values directly is what makes that button work without a sign-out.
    /// </summary>
    private static IEnumerable<string> SearchDirectories(string? pathValue)
    {
        if (pathValue is not null)
        {
            foreach (var directory in Split(pathValue)) yield return directory;
            yield break;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var source in new[]
                 {
                     Environment.GetEnvironmentVariable("PATH"),
                     ReadPath(EnvironmentVariableTarget.User),
                     ReadPath(EnvironmentVariableTarget.Machine),
                 })
        {
            foreach (var directory in Split(source))
            {
                if (seen.Add(directory)) yield return directory;
            }
        }
    }

    private static string? ReadPath(EnvironmentVariableTarget target)
    {
        try
        {
            return OperatingSystem.IsWindows() ? Environment.GetEnvironmentVariable("PATH", target) : null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            // Reading the Machine value touches the registry, which a locked-down profile can
            // refuse. The process PATH is still there to fall back on.
            return null;
        }
    }

    private static string[] Split(string? value) =>
        (value ?? "").Split(Path.PathSeparator,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// Pulls a version out of whatever the CLI prints for --version. Both print a bare semver
    /// today, but "op 2.30.0 (build ...)" is the sort of thing that changes without warning, so
    /// this looks for the first version-shaped run of digits rather than parsing the whole line.
    /// </summary>
    public static Version? ParseVersion(string output)
    {
        var match = VersionPattern().Match(output ?? "");
        return match.Success && Version.TryParse(match.Value, out var version) ? version : null;
    }

    private static string Env(string name) => Environment.GetEnvironmentVariable(name) ?? "";

    [GeneratedRegex(@"\d+\.\d+(\.\d+)?")]
    private static partial Regex VersionPattern();
}
