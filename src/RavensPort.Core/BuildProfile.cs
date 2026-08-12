namespace RavensPort.Core;

/// <summary>
/// Which of the two builds this is, and what that build is allowed to do.
///
/// There is one codebase and two shipped artifacts. The EXE — the installer, the GitHub release
/// asset — is the whole app and always has been. The Microsoft Store MSIX is the same app with two
/// features compiled out, because Store certification rejects them:
///
/// <list type="bullet">
///   <item>
///     <b>Proton Pass</b>, under 10.1.5 Software Distribution. The setup card names a CLI the user
///     acquires outside the Store, which the policy reads as promoting it.
///   </item>
///   <item>
///     <b>mTLS and certificate generation</b>, under 10.2.10 / 10.2.10.1 Security. "Settings &gt;
///     Generate New Certificate" was read as certificate installation and as the app producing an
///     executable artefact for the user to deploy.
///   </item>
/// </list>
///
/// The flags below are <c>const</c> on purpose. The compiler folds every <c>if</c> that reads one,
/// so a store build genuinely does not carry the branches — this is a build switch wearing a
/// property's clothes, not a runtime setting, and there is nothing a user or a config file can do
/// to turn a removed feature back on. Where a whole method would otherwise be dead weight in the
/// package (certificate minting, most of all) the <c>#if STORE_BUILD</c> is used directly instead,
/// so the IL is not there at all.
///
/// The vault schema is deliberately <em>not</em> forked. <see cref="Models.AppSettings"/> keeps its
/// mTLS fields in both builds, because both read the same vault: a user with the EXE on one machine
/// and the Store package on another must not have one of them discard the other's settings.
/// </summary>
public static class BuildProfile
{
#if STORE_BUILD
    /// <summary>True in the Microsoft Store MSIX, false in the EXE.</summary>
    public const bool IsStoreBuild = true;

    /// <summary>Whether Proton Pass is offered as a vault backend at all.</summary>
    public const bool ProtonPassEnabled = false;

    /// <summary>Whether the proxy can serve mTLS, and the Settings tab offer certificates.</summary>
    public const bool MtlsEnabled = false;

    /// <summary>Names the build in logs and in the Settings tab, so a bug report says which it is.</summary>
    public const string Name = "store";
#else
    /// <summary>True in the Microsoft Store MSIX, false in the EXE.</summary>
    public const bool IsStoreBuild = false;

    /// <summary>Whether Proton Pass is offered as a vault backend at all.</summary>
    public const bool ProtonPassEnabled = true;

    /// <summary>Whether the proxy can serve mTLS, and the Settings tab offer certificates.</summary>
    public const bool MtlsEnabled = true;

    /// <summary>Names the build in logs and in the Settings tab, so a bug report says which it is.</summary>
    public const string Name = "full";
#endif
}
