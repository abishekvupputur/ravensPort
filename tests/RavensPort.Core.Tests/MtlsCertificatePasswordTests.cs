using System.Security.Cryptography.X509Certificates;
using RavensPort.Core.Proxy;

namespace RavensPort.Core.Tests;

/// <summary>
/// The password on the exported PFX, and the rule the whole flow now rests on: RavensPort never
/// writes a certificate under a password it chose itself. A built-in password is one every install
/// shares, so it protects the exported file against nobody who has read the source — and the file
/// is what the user copies to every machine allowed to call the proxy.
///
/// The cost of that rule is paid here too. A store written before the password box existed records
/// no password, its blob carries the old built-in one, and nothing re-encrypts it — so it can no
/// longer be opened, and the only way forward is a new certificate reinstalled on every client.
/// These tests pin both halves: generation refuses to invent a password, and loading refuses to
/// guess one.
/// </summary>
public class MtlsCertificatePasswordTests
{
    [Fact]
    public void ACertificateGeneratedWithAChosenPassword_OpensWithThatPassword()
    {
        var pfx = MtlsCertificateFactory.GenerateClientCertificatePfx("a chosen password");

        using var loaded = MtlsCertificateFactory.Load(pfx, "a chosen password");

        Assert.True(loaded.HasPrivateKey);
    }

    [Fact]
    public void ACertificateGeneratedWithAChosenPassword_DoesNotOpenWithAnother()
    {
        var pfx = MtlsCertificateFactory.GenerateClientCertificatePfx("a chosen password");

        // Settings holding the wrong password is indistinguishable from a corrupt blob at this
        // level, and both surface as the same "generate a new one" instruction.
        Assert.Throws<InvalidOperationException>(
            () => MtlsCertificateFactory.Load(pfx, "some other password"));
    }

    /// <summary>
    /// No password means no certificate. The alternatives are both worse than failing: minting
    /// under a built-in password hands back a certificate whose password is not the one the caller
    /// believes it set, and minting with none produces a PFX that Windows' certificate import and
    /// curl's Schannel backend both refuse.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void GeneratingWithoutAPassword_IsRefused(string? password)
    {
        Assert.Throws<ArgumentException>(
            () => MtlsCertificateFactory.GenerateClientCertificatePfx(password!));
    }

    /// <summary>
    /// The upgrade case, and the one behaviour that deliberately changed. An empty stored password
    /// marks a store written before the box existed — see AppSettings.MtlsClientCertificatePassword
    /// — whose certificate carries a built-in password this build no longer knows. Refused outright
    /// rather than guessed at, so the failure names itself instead of surfacing later as a
    /// handshake that closes with no status.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void LoadingWithoutAPassword_IsRefused(string? stored)
    {
        var pfx = MtlsCertificateFactory.GenerateClientCertificatePfx("a chosen password");

        Assert.Throws<InvalidOperationException>(() => MtlsCertificateFactory.Load(pfx, stored!));
    }

    /// <summary>
    /// Expiry is the only thing that retires a certificate here: there is no CA, so no CRL and no
    /// OCSP, and a leaked copy is a working credential until its validity window closes. Pinned so
    /// that a lifetime cannot quietly grow back into the ten years it started at.
    /// </summary>
    [Fact]
    public void AGeneratedCertificate_IsValidForNinetyDays()
    {
        using var cert = MtlsCertificateFactory.Load(MtlsCertificateFactory.GenerateClientCertificatePfx("p"), "p");

        Assert.Equal(TimeSpan.FromDays(90), MtlsCertificateFactory.Lifetime);

        // Against NotBefore rather than "now": the start is backdated a day so a client whose clock
        // runs fast does not reject a certificate minted seconds ago, and measuring from now would
        // fold that day into the assertion.
        var lifetime = cert.NotAfter - cert.NotBefore;
        Assert.Equal(91, Math.Round(lifetime.TotalDays));
    }

    /// <summary>
    /// Two certificates are two credentials. Regenerating has to invalidate the old one — which is
    /// what the confirmation step in the GUI warns about — and that only holds if the key is new.
    /// </summary>
    [Fact]
    public void EachGeneration_ProducesADifferentCertificate()
    {
        using var first = MtlsCertificateFactory.Load(MtlsCertificateFactory.GenerateClientCertificatePfx("p"), "p");
        using var second = MtlsCertificateFactory.Load(MtlsCertificateFactory.GenerateClientCertificatePfx("p"), "p");

        Assert.NotEqual(first.Thumbprint, second.Thumbprint);
    }

    /// <summary>
    /// The check both handshake callbacks reach for. Pinning a thumbprint replaces chain building,
    /// and chain building is what would ordinarily have enforced the dates — so if this answered
    /// "valid" outside the window, <see cref="MtlsCertificateFactory.Lifetime"/> would be a comment
    /// and an expired certificate would keep working forever.
    /// </summary>
    [Fact]
    public void ACertificate_IsRefusedOutsideItsValidityWindow()
    {
        using var cert = MtlsCertificateFactory.Load(MtlsCertificateFactory.GenerateClientCertificatePfx("p"), "p");

        var notBefore = cert.NotBefore.ToUniversalTime();
        var notAfter = cert.NotAfter.ToUniversalTime();

        Assert.True(MtlsCertificateFactory.IsWithinValidity(cert, DateTimeOffset.UtcNow));
        Assert.False(MtlsCertificateFactory.IsWithinValidity(cert, notBefore.AddMinutes(-1)));
        Assert.False(MtlsCertificateFactory.IsWithinValidity(cert, notAfter.AddMinutes(1)));

        // The X509Certificate overload separately, because it is the one the SslStream callbacks
        // actually get handed and it reaches the dates by parsing strings rather than reading them.
        Assert.True(MtlsCertificateFactory.IsWithinValidity((X509Certificate)cert, DateTimeOffset.UtcNow));
        Assert.False(MtlsCertificateFactory.IsWithinValidity((X509Certificate)cert, notAfter.AddDays(1)));
    }
}
