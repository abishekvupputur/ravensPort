using System.Runtime.Versioning;
using System.Text;
using RavensPort.Core.Diagnostics;

namespace RavensPort.Core.Vault;

/// <summary>
/// Keeps the Proton Pass session key in the desktop's own keyring, for platforms that have no
/// Windows Hello.
///
/// **This makes a weaker promise than <see cref="HelloKeyProtector"/>, and the difference is the
/// point rather than a detail.** On Windows the key is sealed so that only a Hello gesture can
/// unseal it: the gesture is not a check in front of the decryption, it produces the key that
/// performs it, so there is no branch to skip and no copy sitting anywhere usable. Here the key is
/// handed to the keyring as-is. The keyring encrypts it at rest, which defeats someone reading the
/// disk — but it is unlocked by the login password at session start and normally stays unlocked, so
/// any process running as this user can ask for it back without anyone being prompted.
///
/// Whatever asks the user's permission before this runs must therefore say *that*, and must not
/// reuse the Hello wording. Telling someone a gesture protects their key, on a platform where no
/// gesture is involved, is worse than saying nothing.
///
/// **Why there is no signing step to mirror.** The Hello design needs one: the gesture produces a
/// signature, the signature derives a key, and that key decrypts a stored blob. A keyring simply
/// returns what it was given, so the sealed-blob machinery has nothing to do and drops out. There
/// is no weaker version of it to write; there is a different arrangement.
/// </summary>
[UnsupportedOSPlatform("windows")]
internal sealed class KeyringSessionKeyProtector(ActivityLog activityLog) : ISessionKeyProtector
{
    private readonly SecretServiceStore _store = new();

    public Task<bool> IsAvailableAsync() => Task.FromResult(SecretServiceStore.IsAvailable());

    public bool HasProtectedKey(string sessionDirectory) => _store.Exists(NameFor(sessionDirectory));

    public Task ProtectAsync(string sessionDirectory, string sessionKey)
    {
        // Throws on failure rather than returning quietly, per the interface: a caller told this
        // succeeded lets the user finish a sign-in believing the session survives a restart.
        _store.Write(NameFor(sessionDirectory), Encoding.UTF8.GetBytes(sessionKey));

        activityLog.Log(
            "VAULT stored the Proton Pass session key in the system keyring — it is encrypted at "
            + "rest and readable by anything running as this user while the keyring is unlocked");

        return Task.CompletedTask;
    }

    public Task<string?> UnprotectAsync(string sessionDirectory)
    {
        var stored = _store.Read(NameFor(sessionDirectory));

        return Task.FromResult(stored is null ? null : Encoding.UTF8.GetString(stored));
    }

    public Task ForgetAsync(string sessionDirectory)
    {
        _store.Delete(NameFor(sessionDirectory));

        return Task.CompletedTask;
    }

    /// <summary>
    /// Keyed on the session directory, so two RavensPort profiles pointing at different sessions do
    /// not overwrite one another's key. Matches what <see cref="HelloKeyProtector"/> does.
    /// </summary>
    private static string NameFor(string sessionDirectory) =>
        $"RavensPort.ProtonPassSessionKey:{sessionDirectory}";
}
