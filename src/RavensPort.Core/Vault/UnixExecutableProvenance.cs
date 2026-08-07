using System.Runtime.Versioning;

namespace RavensPort.Core.Vault;

/// <summary>
/// The Linux answer to "who published this binary", for a platform that has no Authenticode.
///
/// **The threat is unchanged.** <see cref="VaultProbe"/> takes the first match on PATH, and
/// <see cref="ProtonPassSession.BuildEnvironment"/> puts the vault session key in the environment of
/// whatever it launches. So the question is the same one Windows answers with a signature: could an
/// unprivileged process have put this file here?
///
/// **What is checked instead.** Not who signed it — nothing to check — but who could have written
/// it. A binary qualifies when it sits in a system location and neither it nor any directory on the
/// way to it is writable by group or other. That is the standard Unix definition of a path only an
/// administrator controls, and it is exactly the property that makes planting a file impossible for
/// the attacker this defends against. A package manager installing to <c>/usr/bin</c> satisfies it;
/// a file dropped in <c>~/.local/bin</c> or a world-writable directory earlier on PATH does not.
///
/// **What this deliberately does not claim.** It says the file was placed by someone with root, not
/// that AgileBits or Proton produced it. Windows gets the stronger statement because it can. Anyone
/// who can write to <c>/usr/bin</c> can already run code as root, so requiring more here would
/// defend against an attacker who has already won.
///
/// The environment overrides still bypass this, as they do on Windows and for the same reason:
/// a portable or self-built copy is a supported thing to have, and someone who can set your
/// environment can already run code as you.
/// </summary>
[UnsupportedOSPlatform("windows")]
internal static class UnixExecutableProvenance
{
    /// <summary>
    /// Directories a package manager or an administrator installs into. Everything else is treated
    /// as user-controlled, whatever its permissions happen to say today.
    ///
    /// <c>/snap/bin</c> and <c>/nix/store</c> are here because both are managed by a daemon running
    /// as root and are the normal way to have these CLIs on the distributions that use them.
    /// </summary>
    private static readonly string[] SystemRoots =
    [
        "/usr/bin", "/usr/sbin", "/usr/local/bin", "/usr/local/sbin",
        "/bin", "/sbin", "/opt", "/snap/bin", "/nix/store",
    ];

    /// <param name="reason">Why not, in words the setup page can show. Empty when trusted.</param>
    public static bool IsAdministratorInstalled(string resolvedPath, out string reason)
    {
        try
        {
            var full = Path.GetFullPath(resolvedPath);

            if (!SystemRoots.Any(root => full.StartsWith(root + "/", StringComparison.Ordinal)))
            {
                reason = $"it is not in a system location ({string.Join(", ", SystemRoots)}), so any "
                         + "program running as you could have put it there";
                return false;
            }

            if (!File.Exists(full))
            {
                reason = "it is not there any more";
                return false;
            }

            // The file, then every directory above it. A binary that cannot itself be rewritten is
            // still swappable if the directory holding it can be written — the attacker replaces
            // the entry rather than the contents.
            if (IsWritableByOthers(File.GetUnixFileMode(full)))
            {
                reason = "it can be modified by users other than its owner";
                return false;
            }

            for (var dir = Path.GetDirectoryName(full); !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
            {
                if (IsWritableByOthers(File.GetUnixFileMode(dir)))
                {
                    reason = $"the directory '{dir}' can be written to by users other than its owner, "
                             + "so the file in it could have been replaced";
                    return false;
                }

                if (dir == "/") break;
            }

            reason = "";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Unreadable metadata is not permission to proceed. This is the path that decides
            // whether to hand over a vault session key, so the unknown answer is no.
            reason = $"its permissions could not be read ({ex.Message})";
            return false;
        }
    }

    private static bool IsWritableByOthers(UnixFileMode mode) =>
        mode.HasFlag(UnixFileMode.GroupWrite) || mode.HasFlag(UnixFileMode.OtherWrite);
}
