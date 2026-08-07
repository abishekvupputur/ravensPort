namespace RavensPort.Core.Vault;

/// <summary>
/// Where the session key is kept: the Windows Credential Manager on Windows, the freedesktop
/// Secret Service elsewhere.
///
/// Deliberately dumb. It stores bytes under a name; it does not know what they are, does not
/// encrypt or decrypt, and cannot prompt.
///
/// **That last point is what differs by platform, and it is not this interface's business.** On
/// Windows the bytes are ciphertext sealed to a Hello gesture, so the store holding them is worth
/// nothing on its own. On Linux there is no such gesture, so the key goes in as-is and the keyring
/// itself is the protection — which means the guarantee lives in the *caller*, not here. See
/// <see cref="ISessionKeyProtector"/> for which caller makes which promise, and why the UI has to
/// say different things about them.
/// </summary>
internal interface ISecretStore
{
    /// <summary>Whether something is stored under this name. Must never prompt: it is read from a
    /// property getter that the setup page binds.</summary>
    bool Exists(string target);

    /// <summary>The stored bytes, or null when there is nothing there. Null is an answer — a first
    /// run — and callers distinguish it from a failure to open something that is there.</summary>
    byte[]? Read(string target);

    /// <summary>Stores bytes, replacing whatever was there. Throws if the write did not take.</summary>
    void Write(string target, byte[] blob);

    /// <summary>Removes it, silently when there was nothing to remove.</summary>
    void Delete(string target);
}
