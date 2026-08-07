namespace RavensPort.Core.Vault;

/// <summary>
/// The portable build's session-key store: there isn't one yet.
///
/// <see cref="IsAvailableAsync"/> answers false, which is the whole point — the setup page reads
/// that and does not offer to keep a Proton Pass session, so nobody is invited to create a key that
/// could not be brought back. Everything else here follows from that: nothing was ever stored, so
/// there is nothing to find and nothing to forget.
///
/// <see cref="ProtectAsync"/> throws rather than quietly doing nothing. A caller that was told a
/// key had been stored would let the user finish a sign-in believing their session survives a
/// restart, and they would find out otherwise at the worst moment.
///
/// Replaced by a keyring-backed implementation in Phase L3 — see .claude/LINUX-PORT-PLAN.md. Until
/// then a Linux build reaches its vault through 1Password or runs in single use.
/// </summary>
internal sealed class UnavailableSessionKeyProtector : ISessionKeyProtector
{
    private const string Message =
        "RavensPort cannot store a Proton Pass session key on this platform yet, so signing in "
        + "would produce a session that could not be reopened after a restart. Use 1Password, or "
        + "run in single use.";

    public Task<bool> IsAvailableAsync() => Task.FromResult(false);

    public bool HasProtectedKey(string sessionDirectory) => false;

    public Task ProtectAsync(string sessionDirectory, string sessionKey) =>
        throw new PlatformNotSupportedException(Message);

    public Task<string?> UnprotectAsync(string sessionDirectory) => Task.FromResult<string?>(null);

    public Task ForgetAsync(string sessionDirectory) => Task.CompletedTask;
}
