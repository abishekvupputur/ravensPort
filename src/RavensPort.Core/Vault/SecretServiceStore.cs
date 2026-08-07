using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace RavensPort.Core.Vault;

/// <summary>
/// The freedesktop Secret Service — gnome-keyring, KWallet, or whatever else answers on the session
/// bus — as much of it as RavensPort needs: write, read, delete one secret by name.
///
/// **What this is and is not.** The keyring encrypts what it holds at rest, so nothing readable
/// sits in a file. It does not, however, ask anyone anything: on a normal desktop it is unlocked by
/// the login password at session start and stays unlocked, so any process running as this user can
/// read back what was stored here without a prompt.
///
/// That is weaker than the Windows arrangement this parallels, where the secret is sealed to a
/// Windows Hello gesture and the gesture produces the key that opens it. The difference is not an
/// implementation gap to be closed later — Linux has no equivalent primitive — and it is why the
/// consent copy shown before using this must not be the Hello copy. See
/// <see cref="KeyringSessionKeyProtector"/>.
///
/// P/Invoke into libsecret rather than shelling out to <c>secret-tool</c>: the CLI is not installed
/// everywhere, and passing a secret as a process argument would put it in the environment of a
/// process list.
/// </summary>
[UnsupportedOSPlatform("windows")]
internal sealed class SecretServiceStore : ISecretStore
{
    private const string Lib = "libsecret-1.so.0";

    /// <summary>
    /// Describes the lookup attributes to libsecret. One schema, one attribute — the entry's name —
    /// because that is the whole of what this stores: a single blob under a single key.
    /// </summary>
    private static readonly IntPtr Schema = CreateSchema();

    [DllImport(Lib, EntryPoint = "secret_password_store_sync", CallingConvention = CallingConvention.Cdecl)]
    private static extern bool StoreSync(
        IntPtr schema, string collection, string label, string password,
        IntPtr cancellable, out IntPtr error, string attribute, string value, IntPtr terminator);

    [DllImport(Lib, EntryPoint = "secret_password_lookup_sync", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr LookupSync(
        IntPtr schema, IntPtr cancellable, out IntPtr error, string attribute, string value, IntPtr terminator);

    [DllImport(Lib, EntryPoint = "secret_password_clear_sync", CallingConvention = CallingConvention.Cdecl)]
    private static extern bool ClearSync(
        IntPtr schema, IntPtr cancellable, out IntPtr error, string attribute, string value, IntPtr terminator);

    /// <summary>Frees the returned secret <em>and wipes the memory it was in</em>, unlike g_free.</summary>
    [DllImport(Lib, EntryPoint = "secret_password_free", CallingConvention = CallingConvention.Cdecl)]
    private static extern void PasswordFree(IntPtr password);

    [DllImport(Lib, EntryPoint = "secret_schema_new", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr SchemaNew(string name, int flags, string attribute, int type, IntPtr terminator);

    /// <summary>The default collection, which is the one a desktop unlocks at login.</summary>
    private const string DefaultCollection = "default";

    private const string AttributeName = "name";

    private static IntPtr CreateSchema() =>
        // SECRET_SCHEMA_NONE, then one attribute of SECRET_SCHEMA_ATTRIBUTE_STRING.
        SchemaNew("org.ravensport.Secret", 0, AttributeName, 0, IntPtr.Zero);

    /// <summary>
    /// Never prompts, per the interface's contract — the setup page binds a property that calls
    /// this. A lookup against an unlocked keyring does not prompt; against a locked one it fails,
    /// and "cannot tell" is reported as "nothing there" rather than by throwing out of a getter.
    /// </summary>
    public bool Exists(string target)
    {
        try
        {
            return Read(target) is not null;
        }
        catch (VaultCliException)
        {
            return false;
        }
    }

    public void Write(string name, byte[] contents)
    {
        // Base64 because libsecret's password API is null-terminated text and the payload is
        // ciphertext, which contains zero bytes by definition.
        var encoded = Convert.ToBase64String(contents);

        if (!StoreSync(Schema, DefaultCollection, $"RavensPort — {name}", encoded,
                IntPtr.Zero, out var error, AttributeName, name, IntPtr.Zero)
            || error != IntPtr.Zero)
        {
            throw new VaultCliException(
                "The system keyring refused to store RavensPort's session key. It may be locked, or "
                + "no keyring service may be running on this session.");
        }
    }

    public byte[]? Read(string name)
    {
        var handle = LookupSync(Schema, IntPtr.Zero, out var error, AttributeName, name, IntPtr.Zero);

        if (error != IntPtr.Zero)
        {
            throw new VaultCliException(
                "The system keyring could not be read. It may be locked — unlock it and try again.");
        }

        if (handle == IntPtr.Zero) return null;

        try
        {
            var encoded = Marshal.PtrToStringUTF8(handle);
            return string.IsNullOrEmpty(encoded) ? null : Convert.FromBase64String(encoded);
        }
        finally
        {
            // Not Marshal.FreeHGlobal: this is libsecret's allocation, and its own free is the one
            // that scrubs the buffer rather than merely releasing it.
            PasswordFree(handle);
        }
    }

    public void Delete(string name)
    {
        // Deliberately ignores both the result and any error. Nothing to delete is the normal case
        // — ForgetAsync runs on paths where a key may never have existed — and a keyring that
        // cannot be reached is not a reason to fail a sign-out.
        ClearSync(Schema, IntPtr.Zero, out _, AttributeName, name, IntPtr.Zero);
    }

    /// <summary>
    /// Whether a Secret Service is actually reachable. Called before offering to keep a session, so
    /// that a machine with no keyring says so up front instead of failing after the sign-in.
    /// </summary>
    public static bool IsAvailable()
    {
        try
        {
            // A lookup for a name nothing uses. Success with no result is the answer wanted: the
            // service is there and answering.
            var probe = LookupSync(Schema, IntPtr.Zero, out var error, AttributeName,
                "ravensport.availability.probe", IntPtr.Zero);

            if (probe != IntPtr.Zero) PasswordFree(probe);

            return error == IntPtr.Zero;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // libsecret is not installed. Common enough on a server or a minimal container, and not
            // an error — it means "no, do not offer to keep a session here".
            return false;
        }
    }
}
