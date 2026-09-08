using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace RavensPort.Core.Vault;

/// <summary>
/// The Windows Credential Manager, as much of it as RavensPort needs: write, read, delete one
/// generic credential by name.
///
/// **What this is and is not.** Credential Manager encrypts what it holds at rest under the user's
/// DPAPI master key, so nothing readable sits in a file. It does not, however, ask anyone anything:
/// <c>CredRead</c> from any process running as that user returns the bytes silently. So this class
/// is storage, not protection — the protection is that
/// <see cref="HelloKeyProtector"/> only ever puts ciphertext here, keyed to a Windows Hello
/// signature it cannot obtain without the user. Anything that reads this credential behind the
/// user's back gets a blob it cannot open.
///
/// There is no managed API for this, which is why it is P/Invoke. The alternative — a file — is
/// what this replaces, and a file is worse in the one way that matters to a user reading the UI:
/// it is a secret-shaped thing sitting in their profile with no OS ownership of it at all.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class WindowsCredentialStore : ISecretStore
{
    private const int GenericCredential = 1;

    /// <summary>
    /// This machine only, never roamed to the domain. A session is bound to the TPM of the PC that
    /// created it, so copying the credential to another machine could only ever produce a blob that
    /// does not decrypt — enterprise persistence would spread that around for no benefit.
    /// </summary>
    private const int PersistLocalMachine = 2;

    private const int ErrorNotFound = 1168;

    /// <summary>
    /// Shown in the Credential Manager control panel, so it should read as an explanation rather
    /// than an identifier. The user seeing "RavensPort" there and wondering what it is, is the
    /// case this exists for.
    /// </summary>
    private const string CredentialComment =
        "Encrypted Proton Pass session key for RavensPort. Only a Windows Hello gesture can decrypt it.";

    /// <summary>
    /// Stores <paramref name="blob"/> under <paramref name="target"/>, replacing whatever was
    /// there. Throws on any failure other than the credential not existing, because a write that
    /// silently did nothing would leave the caller believing a key was saved when it was not —
    /// which is the failure that strands a user after a restart.
    /// </summary>
    public void Write(string target, byte[] blob)
    {
        var targetPtr = IntPtr.Zero;
        var userPtr = IntPtr.Zero;
        var commentPtr = IntPtr.Zero;
        var blobPtr = IntPtr.Zero;

        try
        {
            targetPtr = Marshal.StringToCoTaskMemUni(target);
            userPtr = Marshal.StringToCoTaskMemUni(Environment.UserName);
            commentPtr = Marshal.StringToCoTaskMemUni(CredentialComment);
            blobPtr = Marshal.AllocCoTaskMem(blob.Length);
            Marshal.Copy(blob, 0, blobPtr, blob.Length);

            var credential = new Credential
            {
                Type = GenericCredential,
                TargetName = targetPtr,
                Comment = commentPtr,
                CredentialBlobSize = blob.Length,
                CredentialBlob = blobPtr,
                Persist = PersistLocalMachine,
                UserName = userPtr,
            };

            if (!CredWriteW(ref credential, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }
        finally
        {
            if (blobPtr != IntPtr.Zero)
            {
                // The unmanaged copy outlives the managed array unless it is cleared here, and
                // CoTaskMemFree does not zero what it releases.
                for (var i = 0; i < blob.Length; i++) Marshal.WriteByte(blobPtr, i, 0);
                Marshal.FreeCoTaskMem(blobPtr);
            }

            if (targetPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(targetPtr);
            if (userPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(userPtr);
            if (commentPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(commentPtr);
        }
    }

    /// <summary>Returns the stored bytes, or null when there is no such credential.</summary>
    public byte[]? Read(string target)
    {
        if (!CredReadW(target, GenericCredential, 0, out var handle))
        {
            var error = Marshal.GetLastWin32Error();

            // Not found is an answer, not a fault: it is what a first run looks like.
            if (error == ErrorNotFound) return null;

            throw new Win32Exception(error);
        }

        try
        {
            var credential = Marshal.PtrToStructure<Credential>(handle);

            if (credential.CredentialBlobSize <= 0 || credential.CredentialBlob == IntPtr.Zero)
            {
                return null;
            }

            var blob = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, blob, 0, blob.Length);

            return blob;
        }
        finally
        {
            CredFree(handle);
        }
    }

    /// <summary>
    /// Whether a credential is stored under this name. Reads and discards rather than using a
    /// cheaper probe, because there is no cheaper probe — and this runs on a property getter that
    /// the setup page binds, so it must never prompt. <c>CredRead</c> never does.
    /// </summary>
    public bool Exists(string target)
    {
        try
        {
            var blob = Read(target);
            if (blob is null) return false;

            CryptographicOperations.ZeroMemory(blob);
            return true;
        }
        catch (Win32Exception)
        {
            // A binding cannot show an error. Treat an unreadable store as nothing stored: the
            // user is then offered a fresh sign-in, which is the only useful thing to offer.
            return false;
        }
    }

    /// <summary>Removes it. Silent when there was nothing there — the caller wanted it gone.</summary>
    public void Delete(string target)
    {
        if (CredDeleteW(target, GenericCredential, 0)) return;

        var error = Marshal.GetLastWin32Error();
        if (error == ErrorNotFound) return;

        throw new Win32Exception(error);
    }

    // ---- CREDENTIALW and the three calls that use it -------------------------------------------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public int Flags;
        public int Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public long LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    // The W entry points are named explicitly, so StringMarshalling.Utf16 is describing what
    // those functions already are rather than choosing between an A and a W variant.
    [LibraryImport("advapi32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredWriteW(ref Credential credential, int flags);

    [LibraryImport("advapi32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredReadW(string target, int type, int flags, out IntPtr credential);

    [LibraryImport("advapi32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredDeleteW(string target, int type, int flags);

    [LibraryImport("advapi32.dll")]
    private static partial void CredFree(IntPtr buffer);
}
