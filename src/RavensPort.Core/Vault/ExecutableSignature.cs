using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace RavensPort.Core.Vault;

/// <summary>
/// What Windows thinks of the Authenticode signature on a file this app is about to execute.
///
/// **Why WinVerifyTrust and not <c>X509Certificate.CreateFromSignedFile</c>.** That method only
/// pulls the certificate out of the file. It does not check that the file's contents still hash to
/// what the signature covers, so a signed binary with bytes changed afterwards hands back the
/// original publisher quite happily — which would make a signature check that used it alone worse
/// than none, because it would report "signed by Proton AG" about an edited file. WinVerifyTrust
/// does the whole job: digest, certificate chain, and trust policy.
///
/// **Revocation is deliberately not checked online.** <c>WTD_REVOKE_NONE</c> keeps this off the
/// network. This runs on the startup probe and on the setup page's "Check again", and a CRL fetch
/// on a machine with no route to the CA stalls for as long as the HTTP stack allows — turning a
/// hardening measure into a hang. A revoked-but-otherwise-valid signing certificate is a much
/// smaller risk than an app that will not start on an offline laptop.
///
/// **Embedded signatures only, not catalogs.** Most of Windows' own binaries — <c>cmd.exe</c> among
/// them — carry no signature in the file at all; they are vouched for by a system catalog, and this
/// reports them as unsigned. That is fine for what this is used for: <c>op.exe</c> and
/// <c>pass-cli.exe</c> are third-party binaries signed in the file, which is the only case
/// <see cref="AuthenticodeTrustPolicy"/> asks about. Anything that later needs to verify a Windows
/// component would have to add the catalog lookup as well.
/// </summary>
public static class ExecutableSignature
{
    /// <summary>WINTRUST_ACTION_GENERIC_VERIFY_V2 — the standard Authenticode policy.</summary>
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

    private const uint UiNone = 2;
    private const uint RevokeNone = 0;
    private const uint ChoiceFile = 1;
    private const uint StateActionVerify = 1;
    private const uint StateActionClose = 2;
    private const uint SaferFlag = 0x100;

    /// <summary>
    /// Verifies <paramref name="path"/> and, when it verifies, names who signed it. Never throws:
    /// an unreadable or malformed file is an untrusted one, which is what the caller acts on.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static SignatureInfo Read(string path)
    {
        // Both halves must look at the same bytes. WinVerifyTrust follows a reparse point and
        // CreateFromSignedFile does not, so on a symlink the verification and the publisher would
        // otherwise describe two different files. This is not hypothetical: WinGet installs op.exe
        // as a symlink in its Links directory, which is what most 1Password CLI installs put on
        // PATH, and reading the signature off the link fails outright.
        // Said plainly rather than passed off as "unsigned". A symlink is a zero-byte reparse point
        // with no signature of its own, so verifying one produces "is not signed at all" — a
        // sentence that accuses a validly signed vendor binary of being tampered with, and sends
        // the user hunting for a file that does not exist. A user hit exactly that: following the
        // link failed for a moment, most likely while a scanner held it, and RavensPort refused to
        // run 1Password's own CLI. Naming the real reason keeps the refusal honest and retryable.
        if (IsUnresolvedLink(path))
        {
            return new SignatureInfo(false, null,
                "is a link that could not be followed just now, so its signature could not be "
                + "checked — this is usually temporary, so try again");
        }

        path = ResolveFinalTarget(path);

        var filePathText = Marshal.StringToCoTaskMemUni(path);
        var fileInfoBlock = Marshal.AllocCoTaskMem(Marshal.SizeOf<WinTrustFileInfo>());
        var dataBlock = Marshal.AllocCoTaskMem(Marshal.SizeOf<WinTrustData>());

        try
        {
            Marshal.StructureToPtr(
                new WinTrustFileInfo
                {
                    cbStruct = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
                    pcwszFilePath = filePathText,
                },
                fileInfoBlock,
                fDeleteOld: false);

            var data = new WinTrustData
            {
                cbStruct = (uint)Marshal.SizeOf<WinTrustData>(),
                dwUIChoice = UiNone,
                fdwRevocationChecks = RevokeNone,
                dwUnionChoice = ChoiceFile,
                pFile = fileInfoBlock,
                dwStateAction = StateActionVerify,
                dwProvFlags = SaferFlag,
            };

            Marshal.StructureToPtr(data, dataBlock, fDeleteOld: false);

            int result;
            try
            {
                result = WinVerifyTrust(IntPtr.Zero, GenericVerifyV2, dataBlock);
            }
            finally
            {
                // The verify call allocates state hanging off hWVTStateData. Closing it is a second
                // call with the same block and a different action, and skipping it leaks per check.
                var closing = Marshal.PtrToStructure<WinTrustData>(dataBlock);
                closing.dwStateAction = StateActionClose;
                Marshal.StructureToPtr(closing, dataBlock, fDeleteOld: false);

                WinVerifyTrust(IntPtr.Zero, GenericVerifyV2, dataBlock);
            }

            return result == 0
                ? new SignatureInfo(true, PublisherOf(path), "is validly signed")
                : new SignatureInfo(false, null, Describe(result));
        }
        catch (Exception ex)
        {
            return new SignatureInfo(false, null, $"could not be checked for a signature ({ex.Message})");
        }
        finally
        {
            Marshal.FreeCoTaskMem(dataBlock);
            Marshal.FreeCoTaskMem(fileInfoBlock);
            Marshal.FreeCoTaskMem(filePathText);
        }
    }

    /// <summary>
    /// Where a symlink actually leads, so the file that gets verified is the file that would run.
    /// Left as-is when it is not a link, or when the target cannot be resolved — an unreadable path
    /// then fails verification below, which is the safe direction.
    ///
    /// Public because anyone deciding whether a previous answer about this binary still holds has
    /// to ask about the same file: a symlink's own size and timestamp say nothing about what it
    /// points at, and do not change when that is replaced.
    /// </summary>
    public static string ResolveFinalTarget(string path)
    {
        try
        {
            return File.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName ?? path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return path;
        }
    }

    /// <summary>
    /// Whether this path is a reparse point whose target could not be reached.
    ///
    /// The two halves both matter. Not a link at all means an ordinary file, which is fine to verify
    /// directly. A link that resolves is fine too — the target is what gets verified. Only a link
    /// that will not follow is a problem, and it is a problem worth naming rather than verifying
    /// anyway: what sits behind an unfollowed link is zero bytes, and zero bytes are never signed.
    /// </summary>
    private static bool IsUnresolvedLink(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Attributes.HasFlag(FileAttributes.ReparsePoint)) return false;

            var target = File.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName;
            return target is null || !File.Exists(target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // The link is there and cannot be followed, which is precisely the case this reports.
            return true;
        }
    }

    /// <summary>
    /// The signer's common name. Read only after <see cref="WinVerifyTrust"/> has already accepted
    /// the file, which is what makes the certificate in it worth reading at all.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static string? PublisherOf(string path)
    {
        try
        {
            // SYSLIB0057 obsoletes CreateFromSignedFile along with the certificate constructors,
            // and points at X509CertificateLoader. That replacement does not cover this case:
            // X509CertificateLoader loads a certificate *file* -- LoadCertificate,
            // LoadCertificateFromFile, LoadPkcs12 and friends, and that is the whole of its surface
            // -- whereas this pulls the Authenticode signer back out of a signed executable, which
            // has no managed equivalent. The alternative is P/Invoking CryptQueryObject by hand, in
            // the one file that decides whether a binary is allowed to run at all, to silence a
            // warning rather than to fix a defect.
            //
            // Scoped to the single call, so the rest of the file still reports the obsoletion. Worth
            // revisiting if a loader for signed files ever appears.
#pragma warning disable SYSLIB0057
            using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            var name = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);

            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    /// <summary>
    /// Plain wording for the handful of results a user might actually hit. The distinction that
    /// matters to them is "you have not installed it properly" versus "this file has been tampered
    /// with", and a bare 0x800B0100 says neither.
    /// </summary>
    private static string Describe(int result) => (uint)result switch
    {
        0x800B0100 => "is not signed at all",
        0x800B0101 => "has an expired signing certificate",
        0x800B010C => "has a revoked signing certificate",
        0x800B0111 => "is signed by a certificate this machine does not trust",
        0x80096010 => "has been modified since it was signed",
        0x800B0004 => "is signed, but not in a way this trust policy accepts",
        _ => $"failed signature verification (0x{(uint)result:X8})",
    };

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int WinVerifyTrust(
        IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid pgActionID, IntPtr pWVTData);

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustFileInfo
    {
        public uint cbStruct;
        public IntPtr pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
    }
}

/// <param name="IsTrusted">Whether Windows accepts the signature over the file as it stands now.</param>
/// <param name="Publisher">The signer's common name, when there is a valid signature to read one from.</param>
/// <param name="Detail">A phrase that completes "this file ...", for a message shown to the user.</param>
public sealed record SignatureInfo(bool IsTrusted, string? Publisher, string Detail);
