namespace RavensPort.Core.Vault;

/// <param name="Allowed">Whether the binary may be launched.</param>
/// <param name="Summary">
/// Why, in words fit for the activity log when allowed and for the setup page when not.
/// </param>
public sealed record TrustDecision(bool Allowed, string Summary);

/// <summary>
/// Decides whether a resolved executable is one this app is willing to run. A seam rather than a
/// static call so tests can launch <c>cmd.exe</c> without a signing certificate for it.
/// </summary>
public interface IExecutableTrustPolicy
{
    TrustDecision Decide(string resolvedPath);
}

/// <summary>
/// Requires the password-manager CLIs to carry a valid Authenticode signature from the publisher
/// who actually makes them.
///
/// **What this is defending.** <see cref="VaultProbe"/> takes the first match on PATH, and PATH on
/// a normal Windows machine includes several directories writable without any privilege — WinGet's
/// Links folder, npm and pip script directories, whatever someone added by hand. Anything able to
/// drop a file called <c>op.exe</c> into one of those gets it launched by RavensPort, and
/// <see cref="ProtonPassSession.BuildEnvironment"/> puts the vault session key in the environment
/// block of what it launches. Requiring a signature means winning the race for a spot on PATH is
/// no longer enough; the attacker also needs Proton's or AgileBits' signing key.
///
/// **Only the two names it knows.** Anything else is allowed through unchecked. That is not a gap:
/// the probe searches for exactly <c>op.exe</c> and <c>pass-cli.exe</c>, so a planted file has to
/// use one of those names to be found in the first place. It does mean tests, and any future
/// helper, are not held to a rule written for two specific vendors' binaries.
///
/// **The environment override is honoured unsigned, and says so.** <c>RAVENSPORT_OP_PATH</c> and
/// <c>RAVENSPORT_PASSCLI_PATH</c> exist for portable copies and self-built binaries — pass-cli is
/// GPL, so building it yourself is a supported thing to do, and the result is unsigned. Enforcing
/// there would break that for no security gain: anyone who can set your environment variables can
/// already run code as you, which is strictly more than swapping a binary.
/// </summary>
public sealed class AuthenticodeTrustPolicy : IExecutableTrustPolicy
{
    public static readonly AuthenticodeTrustPolicy Default = new();

    /// <summary>
    /// The certificate common names these binaries are signed with, as observed on the shipping
    /// artifacts: 1Password's <c>op.exe</c> and both the pass-cli in Proton's desktop install and
    /// the one in its GitHub release zip. More than one entry per binary because a common name is
    /// the vendor's to change, and a rename must not lock users out of their own vault.
    /// </summary>
    private static readonly Dictionary<string, string[]> ExpectedPublishers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["op"] = ["Agilebits", "AgileBits, Inc.", "AgileBits Inc."],
        ["pass-cli"] = ["Proton AG", "Proton Technologies AG"],
    };

    /// <summary>
    /// The name without its extension, so that <c>op</c> and <c>op.exe</c> are the same binary as
    /// far as this policy is concerned.
    ///
    /// Not cosmetic. The map used to be keyed on the Windows filenames, and on any platform where
    /// the CLI is called <c>op</c> a lookup missed — which fell through to "not a password-manager
    /// CLI, so not signature-checked" and allowed it. The refusal further down was unreachable
    /// there, so the effect of adding a portable build would have been to disable this gate on
    /// exactly the platform that cannot verify a signature.
    /// </summary>
    private static string PolicyKey(string fileName) => Path.GetFileNameWithoutExtension(fileName);

    private static readonly string[] OverrideVariables =
        [VaultProbe.OnePasswordPathVariable, VaultProbe.ProtonPassPathVariable];

    public TrustDecision Decide(string resolvedPath)
    {
        var name = Path.GetFileName(resolvedPath);

        if (!ExpectedPublishers.TryGetValue(PolicyKey(name), out var expected))
        {
            return new TrustDecision(true, "not a password-manager CLI, so not signature-checked");
        }

        if (IsExplicitlyChosen(resolvedPath))
        {
            return new TrustDecision(true, "chosen by an environment override, so not signature-checked");
        }

        if (!OperatingSystem.IsWindows())
        {
            // Refuses, where this used to allow. The old answer was written when this branch could
            // not be reached — the app was Windows-only — so "Authenticode is Windows-only" was a
            // true statement with no consequence. A portable build makes it reachable, and as an
            // allow it would have meant: on Linux, run whatever file called 'op' turns up first on
            // the PATH, and hand it the vault session key. That is the exact attack this class
            // exists to stop, so the honest answer off Windows is no.
            //
            // Not a permanent position. Linux has no Authenticode, so the replacement is a
            // deliberate choice — package-manager provenance, a pinned hash allowlist — and it is
            // Phase L2 of .claude/LINUX-PORT-PLAN.md. Until then the override checked just above
            // is the way through for someone who knows exactly which binary they mean.
            return new TrustDecision(false,
                $"RavensPort cannot verify who published '{name}' on this platform, and will not "
                + "hand a vault session key to a program it cannot identify. Point "
                + $"{VariableFor(name)} at a binary you trust to run it anyway.");
        }

        var signature = ExecutableSignature.Read(resolvedPath);

        if (!signature.IsTrusted)
        {
            return new TrustDecision(false,
                $"'{name}' at {resolvedPath} {signature.Detail}, so RavensPort will not run it — this is the "
                + "program your vault session key gets handed to. Install it from the vendor, or point "
                + $"{VariableFor(name)} at a copy you trust.");
        }

        if (!expected.Any(publisher => string.Equals(publisher, signature.Publisher, StringComparison.OrdinalIgnoreCase)))
        {
            return new TrustDecision(false,
                $"'{name}' at {resolvedPath} is signed by '{signature.Publisher}' rather than "
                + $"{string.Join(" or ", expected.Select(p => $"'{p}'"))}, so RavensPort will not run it — this is "
                + $"the program your vault session key gets handed to. Point {VariableFor(name)} at a copy you "
                + "trust if this is deliberate.");
        }

        return new TrustDecision(true, $"signed by {signature.Publisher}");
    }

    /// <summary>
    /// Whether this exact file is the one an override names. Compared by full path rather than by
    /// "an override is set", so an override pointing somewhere else does not quietly excuse a
    /// different binary the probe happened to find first.
    /// </summary>
    private static bool IsExplicitlyChosen(string resolvedPath) =>
        OverrideVariables.Any(variable =>
            Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value
            && SamePath(value, resolvedPath));

    private static bool SamePath(string left, string right)
    {
        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static string VariableFor(string exeName) =>
        string.Equals(PolicyKey(exeName), "op", StringComparison.OrdinalIgnoreCase)
            ? VaultProbe.OnePasswordPathVariable
            : VaultProbe.ProtonPassPathVariable;
}
