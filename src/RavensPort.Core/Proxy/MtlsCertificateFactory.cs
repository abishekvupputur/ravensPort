using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace RavensPort.Core.Proxy;

/// <summary>
/// The self-signed certificate that both ends of the mTLS listener present.
///
/// One certificate, used twice: Kestrel serves it and demands it back, and the only accepted
/// client is whoever holds the same private key. There is no CA and no chain to validate — both
/// sides pin the thumbprint — so a client certificate is neither more nor less than proof of
/// holding this file, which is what the user copies to the machine that may call the proxy.
/// </summary>
public static class MtlsCertificateFactory
{
    /// <summary>
    /// The password used when the user was not asked for one: certificates minted by the mTLS
    /// switch itself, by startup repairing a store that says "on" and holds none, and every
    /// certificate written before the Settings tab offered a password box.
    ///
    /// Not a secret and not pretending to be one. The file is the credential: anyone holding the
    /// PFX holds the access it grants regardless of what is typed here, which is why a user-chosen
    /// password changes who can open the exported copy and nothing about what it can reach.
    /// </summary>
    public const string DefaultPfxPassword = "ravensport";

    /// <summary>
    /// How long a generated certificate stays inside its validity window. 90 days, so a copy that
    /// leaked — off a machine it was installed on, out of a backup of one — stops being a working
    /// credential within a quarter rather than within a decade. There is no revocation here: no CA,
    /// no CRL, no OCSP, so expiry is the only thing that retires a certificate the user cannot get
    /// back.
    ///
    /// Note what this does not do. Both ends pin the thumbprint and accept it in spite of chain
    /// errors — see the validation callbacks in App.xaml.cs and McpSourceConnectionPool — so an
    /// expired certificate is not refused by this app, and the curl and Node recipes on the
    /// Settings tab disable verification too. The date is a prompt to rotate, not an enforced
    /// cut-off, and rotating means generating, exporting, reinstalling on every client, and
    /// restarting.
    /// </summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(90);

    /// <summary>
    /// How close to expiry the Settings tab starts saying so. Long enough that a certificate can be
    /// rotated between one use of the app and the next rather than in the moment it stops working,
    /// which matters because rotating means visiting every client that holds a copy.
    /// </summary>
    public static readonly TimeSpan ExpiryWarningWindow = TimeSpan.FromDays(14);

    /// <summary>
    /// Whether <paramref name="certificate"/> is inside its validity window. The one place that is
    /// decided, so the listener, the funnel, and the Settings tab cannot disagree about whether a
    /// certificate is still good.
    ///
    /// Both ends pin a thumbprint and accept the certificate in spite of chain errors, because a
    /// self-signed pair on loopback has no chain to validate. That deliberately turns off the
    /// platform's own expiry check along with everything else, so it has to be put back by hand —
    /// otherwise <see cref="Lifetime"/> would be decoration and a leaked copy would stay a working
    /// credential for as long as anyone kept it.
    /// </summary>
    public static bool IsWithinValidity(X509Certificate2 certificate, DateTimeOffset now) =>
        now >= certificate.NotBefore.ToUniversalTime() && now <= certificate.NotAfter.ToUniversalTime();

    /// <inheritdoc cref="IsWithinValidity(X509Certificate2, DateTimeOffset)"/>
    /// <remarks>
    /// The X509Certificate overload, for the SslStream callbacks: those are contracted to hand back
    /// the base type, and a cast that failed would read as "wrong certificate" — which is the one
    /// conclusion that would definitely not be what happened.
    /// </remarks>
    public static bool IsWithinValidity(X509Certificate certificate, DateTimeOffset now)
    {
        // Both are the certificate's own dates in local time; DateTime.Parse is what the framework
        // itself uses to surface them off the base type.
        if (!DateTime.TryParse(certificate.GetEffectiveDateString(), out var notBefore) ||
            !DateTime.TryParse(certificate.GetExpirationDateString(), out var notAfter))
        {
            return false;
        }

        return now >= notBefore.ToUniversalTime() && now <= notAfter.ToUniversalTime();
    }

    /// <summary>
    /// Deliberately <em>not</em> <see cref="X509KeyStorageFlags.EphemeralKeySet"/>, tempting as it
    /// is for a key that only has to outlive the process. Schannel cannot acquire server
    /// credentials from an in-memory key, so an ephemeral certificate binds and listens perfectly
    /// and then fails every single handshake with "the platform does not support ephemeral keys" —
    /// which reaches the client as a connection closed mid-handshake, with no status and nothing
    /// in the app's own log.
    ///
    /// Nor <see cref="X509KeyStorageFlags.Exportable"/>: nothing here re-exports (the PFX in
    /// settings is already the copy the user exports), and it only widens where the key can go.
    ///
    /// The default set imports the key into a container that CryptoAPI removes when the last
    /// handle closes, so <see cref="Mcp.KestrelMtlsState"/> disposing its certificate is what
    /// keeps this from leaving a key file behind on every start.
    /// </summary>
    private const X509KeyStorageFlags StorageFlags = X509KeyStorageFlags.DefaultKeySet;

#if !STORE_BUILD
    /// <param name="password">
    /// What the exported PFX will ask for. Empty or null takes <see cref="DefaultPfxPassword"/>,
    /// so the callers that have nobody to ask do not each have to name it.
    /// </param>
    /// <remarks>
    /// Absent from the Microsoft Store build. Certification failed the package under 10.2.10 and
    /// 10.2.10.1 for exactly this — "Location of Download: Settings &gt; Generate New Certificate" —
    /// so the store build does not hide the button, it does not carry the code that mints the file.
    /// <see cref="Load"/> stays in both builds: reading a certificate is not producing one, and the
    /// vault is shared with the EXE, which may well have written one.
    /// </remarks>
    public static string GenerateClientCertificatePfx(string? password = null)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=RavensPort MCP Client", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.2"), new Oid("1.3.6.1.5.5.7.3.1")], false)); // Client Auth & Server Auth

        // Without a SAN the certificate is a server certificate in name only: every client that
        // does ordinary hostname validation — a browser, curl, an MCP host that is not this app —
        // rejects it before it ever gets to the pinning question, and CN has not been consulted
        // for that purpose in years. The proxy only ever binds loopback, so those are the names.
        var subjectAlternativeNames = new SubjectAlternativeNameBuilder();
        subjectAlternativeNames.AddDnsName("localhost");
        subjectAlternativeNames.AddIpAddress(IPAddress.Loopback);
        subjectAlternativeNames.AddIpAddress(IPAddress.IPv6Loopback);
        request.CertificateExtensions.Add(subjectAlternativeNames.Build());

        // The backdated start is not padding: a client whose clock runs a few minutes ahead of this
        // machine would otherwise reject a certificate minted seconds ago as not yet valid.
        var expire = DateTimeOffset.UtcNow.Add(Lifetime);
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), expire);

        var pfxBytes = cert.Export(X509ContentType.Pfx, Resolve(password));
        return Convert.ToBase64String(pfxBytes);
    }
#endif

    /// <summary>
    /// Empty means "the store predates the password box", not "no password" — see
    /// <see cref="Models.AppSettings.MtlsClientCertificatePassword"/>. Both readings load an
    /// existing certificate; only this one loads the ones already on disk.
    /// </summary>
    private static string Resolve(string? password) =>
        string.IsNullOrEmpty(password) ? DefaultPfxPassword : password;

    /// <summary>
    /// Reads back a certificate stored by <see cref="GenerateClientCertificatePfx"/>. The single
    /// place the storage flags and the password are applied, so the copy Kestrel serves and the
    /// copy the funnel presents are loaded identically and their thumbprints match.
    /// </summary>
    public static X509Certificate2 Load(string base64Pfx, string? password = null)
    {
        try
        {
            // X509CertificateLoader, not the X509Certificate2 constructor: that overload is
            // obsolete as of .NET 9 (SYSLIB0057) because it guessed at the format of the bytes it
            // was handed. This says PKCS#12 outright, which is what a PFX is and the only thing
            // this ever loads -- so a blob that is something else now fails as one rather than
            // being sniffed into a certificate with no private key and failing at the handshake.
            return X509CertificateLoader.LoadPkcs12(
                Convert.FromBase64String(base64Pfx), Resolve(password), StorageFlags);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            throw new InvalidOperationException(
                "The stored mTLS certificate could not be read. Generate a new one on the Settings tab.", ex);
        }
    }
}
