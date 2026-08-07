using System.Security.Cryptography;
using RavensPort.Core.Diagnostics;

namespace RavensPort.Core.Vault;

/// <summary>
/// Holds the Proton Pass session key in the Windows Credential Manager, encrypted so that only a
/// Windows Hello gesture can decrypt it. The user never sees the key: it is generated, protected
/// and used without ever being rendered, copied, or typed.
///
/// **Two stores, and neither one alone is enough.** That is the whole design, so it is worth
/// stating exactly:
///
/// <list type="bullet">
/// <item>The <b>Credential Manager</b> (<see cref="ISecretStore"/>) holds a blob —
/// <c>version ‖ challenge ‖ nonce ‖ tag ‖ ciphertext</c>. Windows encrypts it at rest under the
/// user's DPAPI master key. It is not, however, protected from the user's own processes:
/// <c>CredRead</c> returns those bytes silently, with no prompt and no trace.</item>
/// <item><b>Hello</b> (<see cref="IHelloSigner"/>) holds an RSA-2048 private key in the TPM. It
/// cannot be exported. The only operation permitted is a signature, and asking for one always
/// raises the prompt.</item>
/// </list>
///
/// The AES key is in neither store. It is derived, every single time, as
/// <c>SHA-256(signature over the challenge)</c> — so reading the blob without the gesture yields
/// ciphertext, and holding the Hello credential without the blob yields nothing to decrypt. Skipping
/// the prompt does not produce the wrong answer; it produces no answer.
///
/// That binding is the point. <c>UserConsentVerifier</c> would also show a Hello prompt, but it
/// returns a boolean and protects nothing — a patched binary, or a caller that simply does not ask,
/// reaches the data anyway. Verifying and decrypting have to be the same operation.
///
/// **Two limits worth stating plainly, because the UI must not overclaim.**
///
/// The Hello credential is scoped to the Windows account, not to RavensPort. RavensPort is an
/// unpackaged Win32 app, so the credential service finds no AppContainer boundary and falls back to
/// user-level scoping; another program running as the same user that knows both names could read
/// the blob and ask to sign for it. It cannot do so quietly — the user sees a Hello prompt they did
/// not initiate — but the boundary is "you would notice", not "it cannot happen".
///
/// And this relies on the signature over a fixed challenge being the same every time, which holds
/// because Hello signs with PKCS#1 v1.5. That is an implementation detail of Windows, not a
/// contract. If it ever became randomised, every key stored this way would stop opening — which is
/// why <see cref="ProtonPassAuthenticator.DiscardLocalSessionAsync"/> exists and is offered on the
/// setup page. Losing the key costs the session and never the data.
/// </summary>
public sealed class HelloKeyProtector : ISessionKeyProtector, IServiceTokenProtector
{
    private readonly ActivityLog _activityLog;
    private readonly IHelloSigner _signer;
    private readonly ISecretStore _store;

    /// <summary>What the app builds: the real TPM and the real Credential Manager.</summary>
    public HelloKeyProtector(ActivityLog activityLog)
        : this(activityLog, new KeyCredentialHelloSigner(), NewDefaultStore())
    {
    }

    /// <summary>
    /// For the tests, which cannot raise a Hello prompt on a CI runner and must still be able to
    /// assert everything either side of it. Internal, and the DI container never selects it —
    /// <c>ServiceProvider</c> only considers public constructors.
    /// </summary>
    internal HelloKeyProtector(ActivityLog activityLog, IHelloSigner signer, ISecretStore store)
    {
        _activityLog = activityLog;
        _signer = signer;
        _store = store;
    }

    private static ISecretStore NewDefaultStore()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("RavensPort stores its session key in the Windows Credential Manager.");
        }

        return new WindowsCredentialStore();
    }

    /// <summary>
    /// Names both halves — the Hello credential and the Credential Manager entry. Deliberately the
    /// same name for both: they are two parts of one arrangement, and a mismatch between them is
    /// the failure mode that leaves a prompt which opens nothing.
    ///
    /// Also what another app would have to guess to reach either, which is no defence and is not
    /// treated as one.
    /// </summary>
    private const string BaseName = "RavensPort.ProtonPassSessionKey";

    /// <summary>
    /// The credential holding a 1Password service-account token, for a user who has asked to keep
    /// one between runs.
    ///
    /// A separate name from the Proton Pass key on purpose. They are different secrets belonging to
    /// different managers with different lifetimes — a rotated service account has to be forgettable
    /// without disturbing a Proton session, and vice versa — and one name for both would make
    /// "forget the token" quietly sign the user out of the other manager.
    ///
    /// Unscoped by anything else because there is only one: the token is not tied to a directory
    /// the way a pass-cli session is.
    /// </summary>
    private const string OnePasswordTokenName = "RavensPort.OnePasswordServiceToken";

    /// <summary>
    /// Where the blob used to live, before it moved into the Credential Manager. Read once by
    /// <see cref="MigrateLegacyBlob"/> and then deleted; nothing writes it any more.
    ///
    /// The result is canonicalised and asserted to be a direct child of the session directory. The
    /// file name is a constant, so nothing can escape today — but this path is handed to
    /// <c>File.Delete</c>, and the whole point of checking here is that the assertion survives
    /// whatever a later change does to how the directory or the name is chosen.
    /// </summary>
    internal static string LegacyBlobPath(string sessionDirectory)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sessionDirectory));
        var path = Path.GetFullPath(Path.Combine(root, LegacyBlobFileName));

        if (Path.GetDirectoryName(path) is not { } parent
            || !string.Equals(Path.TrimEndingDirectorySeparator(parent), root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The legacy session blob path escaped the session directory.");
        }

        return path;
    }

    private const string LegacyBlobFileName = "hello.bin";

    /// <summary>
    /// The credential name for a given session directory.
    ///
    /// The default directory keeps the bare name so that installs predating this change still find
    /// their own credential. Anything else — a test, an override — gets a suffix, so two sessions
    /// on one machine cannot silently overwrite each other's key.
    /// </summary>
    internal static string NameFor(string sessionDirectory)
    {
        var normalised = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sessionDirectory));
        var standard = Path.TrimEndingDirectorySeparator(Path.GetFullPath(ProtonPassSession.DefaultDirectory));

        if (string.Equals(normalised, standard, StringComparison.OrdinalIgnoreCase)) return BaseName;

        var digest = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(normalised.ToUpperInvariant()));
        return $"{BaseName}.{Convert.ToHexString(digest)[..8]}";
    }

    /// <summary>Whether this machine can do it at all — Hello enrolled, with a PIN at minimum.</summary>
    public Task<bool> IsAvailableAsync() => _signer.IsAvailableAsync();

    /// <summary>
    /// Whether a protected key is stored for this session. Never prompts — it is read from a
    /// property getter the setup page binds — and never returns the ciphertext to the caller.
    /// </summary>
    public bool HasProtectedKey(string sessionDirectory)
    {
        var name = NameFor(sessionDirectory);
        MigrateLegacyBlob(sessionDirectory, name);

        return _store.Exists(name);
    }

    /// <summary>
    /// Stores <paramref name="sessionKey"/> so a Hello gesture can retrieve it. Prompts once, now,
    /// to create the credential and take the signature the encryption key comes from.
    ///
    /// Throws rather than reporting failure, and callers must let it: a sign-in that proceeds after
    /// this failed produces a session on disk whose key exists only in memory and is shown to
    /// nobody — unopenable the moment the app closes.
    /// </summary>
    public async Task ProtectAsync(string sessionDirectory, string sessionKey)
    {
        await ProtectNamedAsync(NameFor(sessionDirectory), sessionKey, "Proton Pass session key");

        // Belt and braces for an install being upgraded mid-flight: the old file is superseded the
        // moment the store succeeds, and leaving it would be a stale copy of a single secret.
        TryDeleteLegacyBlob(sessionDirectory);
    }

    // ---- 1Password service-account token ---------------------------------------------------------

    /// <summary>
    /// Whether a service-account token has been kept for next time. Never prompts and never returns
    /// the ciphertext — the setup page binds this to decide which buttons to offer.
    /// </summary>
    public bool HasProtectedOnePasswordToken() => _store.Exists(OnePasswordTokenName);

    /// <summary>
    /// Keeps the service-account token so the user need not paste it again after a restart.
    ///
    /// This is the one place the "never stored" rule bends, and only because the user asked for it.
    /// What is written is ciphertext whose key exists nowhere: it is derived from a Hello signature
    /// each time, so the bytes in Credential Manager open only to a gesture on this PC. A copy of
    /// the token in plain text — an environment variable, a settings file, a note — would be a
    /// bearer credential for every vault the service account can reach, sitting outside the password
    /// manager it came from. This is not that, and the UI must not describe the two as equivalent.
    /// </summary>
    public Task ProtectOnePasswordTokenAsync(string token) =>
        ProtectNamedAsync(OnePasswordTokenName, token, "1Password service account token");

    /// <summary>
    /// Brings the token back, prompting for Hello. Null when nothing is stored; throws when
    /// something is stored and would not open, because those need different things said about them.
    /// </summary>
    public Task<string?> UnprotectOnePasswordTokenAsync() =>
        UnprotectNamedAsync(OnePasswordTokenName, "1Password service account token");

    /// <summary>
    /// Forgets the stored token.
    ///
    /// Needed rather than merely tidy: service-account tokens are rotated, and a stored one that has
    /// been revoked is a credential that fails every startup with no way to clear it from inside the
    /// app. It is also what a user reaches for when they realise they saved it on a machine they
    /// would rather not have.
    /// </summary>
    public Task ForgetOnePasswordTokenAsync()
    {
        try
        {
            SafeDeleteStored(OnePasswordTokenName);
            _activityLog.Log("VAULT removed the saved 1Password service account token from Windows Credential Manager");
        }
        catch (Exception ex)
        {
            // Never allowed to fail the action that follows. Without the blob there is nothing to
            // open, whatever is left in the Hello store.
            _activityLog.Log($"VAULT could not fully remove the saved service account token: {ex.Message}");
        }

        return Task.CompletedTask;
    }

    // ---- The shared mechanism --------------------------------------------------------------------

    /// <param name="what">
    /// What is being protected, for the log. The secret itself is never logged; this names the kind
    /// so an activity log makes sense when a machine holds both a Proton session and a token.
    /// </param>
    private async Task ProtectNamedAsync(string name, string secret, string what)
    {
        // Reused when it is already there. Creating prompts and signing prompts, so a credential
        // made fresh on every sign-in cost two gestures where one is needed.
        var ensured = await _signer.EnsureAsync(name);
        if (!ensured.Succeeded) throw new VaultCliException(Explain(ensured.Failure, "set up"));

        // The challenge is generated before the signature is taken over it, and stored with the
        // ciphertext. It is not a secret — its job is to be the fixed input only the TPM can sign.
        var challenge = HelloSealedKey.NewChallenge();

        var signed = await _signer.SignAsync(name, challenge);

        if (!signed.Succeeded)
        {
            // Only if this call made it. A credential that was already here belongs to an earlier
            // sign-in, and removing it would charge the user a create prompt on every retry — which
            // is exactly the two-gesture bug this method used to have.
            await DeleteIfWeCreatedItAsync(ensured, name);

            throw new VaultCliException(Explain(signed.Failure, "set up"));
        }

        var derived = HelloSealedKey.DeriveKey(signed.Signature!);
        byte[] blob;

        try
        {
            blob = HelloSealedKey.Seal(derived, challenge, secret);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(derived);
        }

        try
        {
            _store.Write(name, blob);
        }
        catch (Exception ex)
        {
            await DeleteIfWeCreatedItAsync(ensured, name);

            throw new VaultCliException(
                $"RavensPort could not store the {what} in Windows Credential Manager: {ex.Message}");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(blob);
        }

        _activityLog.Log(
            $"VAULT stored the {what} in Windows Credential Manager behind Windows Hello");
    }

    /// <summary>
    /// Retrieves the key, prompting for Hello. Returns null when there is nothing stored; throws
    /// when there is but it could not be opened, since those need different things said about them.
    /// </summary>
    public Task<string?> UnprotectAsync(string sessionDirectory)
    {
        var name = NameFor(sessionDirectory);
        MigrateLegacyBlob(sessionDirectory, name);

        return UnprotectNamedAsync(name, "Proton Pass session key");
    }

    private async Task<string?> UnprotectNamedAsync(string name, string what)
    {
        var blob = _store.Read(name);
        if (blob is null) return null;

        try
        {
            // Reading the challenge is the only thing done before the gesture, and it is not
            // secret. Nothing about the session key is recoverable from this point without a
            // signature — there is no branch below that returns a key without one.
            var challenge = HelloSealedKey.ChallengeOf(blob);

            var signed = await _signer.SignAsync(name, challenge);

            if (!signed.Succeeded)
            {
                // A missing Hello credential makes this blob permanently unopenable. Clearing it is
                // what puts the setup page back into its "sign in again" state instead of offering
                // a gesture that can only ever fail. Every other failure is retryable, so the blob
                // stays exactly where it is.
                if (signed.Failure is HelloFailure.NotFound) SafeDeleteStored(name);

                throw new VaultCliException(Explain(signed.Failure, "unlock"));
            }

            var derived = HelloSealedKey.DeriveKey(signed.Signature!);

            try
            {
                var secret = HelloSealedKey.Open(derived, blob);

                _activityLog.Log($"VAULT unlocked the {what} with Windows Hello");
                return secret;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(derived);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(blob);
        }
    }

    /// <summary>
    /// Removes the stored key. Called on sign-out, on discard, and on a sign-in that did not
    /// finish, so that nothing is left that a later "sign in" could resume.
    ///
    /// **The Hello credential is deliberately kept.** It used to go too, on the reasoning that
    /// "signed out" should not leave a credential in the user's Hello store — tidiness, not
    /// security. The cost turned out to be severe: this runs on every path back to the sign-in
    /// button, including a cancelled login, so every attempt found no credential, had to create one,
    /// and charged the user a create prompt on top of the signature. Two Hello prompts to sign in
    /// once, every single time.
    ///
    /// Keeping it gives up nothing. The credential is an RSA key with no data attached — the blob
    /// it once keyed is gone, and <c>TheHelloCredentialAlone_HasNothingToOpen</c> pins exactly that.
    /// It cannot be used to reach the vault, the session, or anything else; it can only make the
    /// next sign-in a single gesture.
    /// </summary>
    public Task ForgetAsync(string sessionDirectory)
    {
        try
        {
            SafeDeleteStored(NameFor(sessionDirectory));
            TryDeleteLegacyBlob(sessionDirectory);
        }
        catch (Exception ex)
        {
            // Never allowed to fail a sign-out. Without the blob there is nothing to open, whatever
            // is left in the Hello store.
            _activityLog.Log($"VAULT could not fully remove the stored session key: {ex.Message}");
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Undoes a credential this call created, and leaves alone one it merely borrowed. The
    /// difference is the whole reason <see cref="HelloResult.Created"/> exists.
    /// </summary>
    private async Task DeleteIfWeCreatedItAsync(HelloResult ensured, string name)
    {
        if (!ensured.Created) return;

        try
        {
            await _signer.DeleteAsync(name);
        }
        catch
        {
            // Best-effort cleanup of a credential that is already useless.
        }
    }

    /// <summary>
    /// Moves a blob written by an earlier version out of the session directory and into the
    /// Credential Manager.
    ///
    /// No gesture is needed for this and none is asked for: the bytes are ciphertext either way,
    /// and the format is unchanged, so the same Hello credential still opens it afterwards. Silent
    /// on every failure — a migration that cannot run leaves the user exactly where they were,
    /// which the "discard and sign in again" path already covers.
    /// </summary>
    private void MigrateLegacyBlob(string sessionDirectory, string name)
    {
        try
        {
            // Inside the try, not above it: resolving the path can now fail on a malformed session
            // directory, and this method's contract is to stay silent on every failure.
            var path = LegacyBlobPath(sessionDirectory);

            if (!File.Exists(path)) return;

            if (!_store.Exists(name))
            {
                var blob = File.ReadAllBytes(path);

                try
                {
                    _store.Write(name, blob);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(blob);
                }
            }

            File.Delete(path);
        }
        catch
        {
            // Deliberately silent, and deliberately not deleting the file on a failed write: the
            // old location still works for the version that wrote it.
        }
    }

    private static void TryDeleteLegacyBlob(string sessionDirectory)
    {
        try
        {
            var path = LegacyBlobPath(sessionDirectory);
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Nothing useful to say. The file cannot be decrypted without the Hello credential,
            // which ForgetAsync removes anyway.
        }
    }

    private void SafeDeleteStored(string name)
    {
        try
        {
            _store.Delete(name);
        }
        catch
        {
            // See TryDeleteLegacyBlob: a blob nothing can open is not worth failing over.
        }
    }

    private static string Explain(HelloFailure failure, string verb) => failure switch
    {
        HelloFailure.Cancelled =>
            $"Windows Hello was cancelled, so RavensPort did not {verb} the session key.",
        HelloFailure.NotFound =>
            "There is no Windows Hello key for RavensPort on this PC. Discard this session and sign in again.",
        HelloFailure.NotEnrolled =>
            "Windows Hello is not set up for this account. Set up Windows Hello to continue.",
        HelloFailure.DeviceLocked =>
            "Windows Hello is locked after too many failed attempts. Sign in to Windows again to unlock it.",
        _ => $"Windows Hello could not {verb} the session key.",
    };
}
