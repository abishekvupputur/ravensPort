namespace RavensPort.Core.Vault;

/// <summary>
/// Keeps a 1Password service-account token on this machine, so an install that starts at login can
/// reach the vault without the token being pasted again.
///
/// Separate from <see cref="ISessionKeyProtector"/> though the Windows implementation is one class:
/// a session key is RavensPort's own and scoped to a session directory, while this is the user's
/// bearer credential for every vault the service account can reach, and there is exactly one of it.
///
/// **The platforms differ, and the UI is required to say so.** On Windows,
/// <see cref="HelloKeyProtector"/> binds the token to a Windows Hello gesture that produces the key
/// performing the decryption, so there is no check to skip. Where no such store exists, the answer
/// is to keep nothing — see <see cref="UnavailableServiceTokenProtector"/> — rather than to write a
/// bearer credential somewhere weaker while reusing the Hello wording for it.
/// </summary>
public interface IServiceTokenProtector
{
    /// <summary>
    /// Whether a token has been kept for next time. Never prompts and never returns the ciphertext —
    /// the setup page binds this to decide which buttons to offer.
    /// </summary>
    bool HasProtectedOnePasswordToken();

    /// <summary>
    /// Keeps the token. Must throw if it cannot: a caller told this succeeded will report to the
    /// user that their token is saved when nothing was written.
    /// </summary>
    Task ProtectOnePasswordTokenAsync(string token);

    /// <summary>
    /// Brings the token back. Null when nothing is stored; throws when something is stored and would
    /// not open, because those need different things said about them.
    /// </summary>
    Task<string?> UnprotectOnePasswordTokenAsync();

    /// <summary>Forgets the stored token. Must not throw when there was nothing to forget.</summary>
    Task ForgetOnePasswordTokenAsync();
}
