using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using RavensPort.Core.Mcp;
using RavensPort.Core.Proxy;

namespace RavensPort.Core.Tests;

/// <summary>
/// What an expired certificate does to the listener, pinned because the answer is deliberate and
/// reads like a bug from the outside.
///
/// The proxy still binds https and still presents the expired certificate. Both handshake callbacks
/// then refuse it — see the validation callbacks in App.xaml.cs and McpSourceConnectionPool — so the
/// listener turns away every caller including this app's own funnel. That is the enforcement: it
/// fails closed. The two alternatives are worse. Dropping to plain HTTP would leave the user
/// believing the proxy is certificate-protected while anything on the machine can call it, and
/// refusing to start would strand them with no way to reach the button that fixes it.
///
/// Expiry has to be checked by hand at both ends: pinning a thumbprint replaces chain building, and
/// chain building is what would ordinarily have enforced the dates. If that ever came out,
/// <see cref="MtlsCertificateFactory.Lifetime"/> would be decoration and a leaked copy would stay a
/// working credential forever.
/// </summary>
public class MtlsCertificateExpiryTests
{
    private const string Password = "expiry-test-password"; // gitleaks:allow

    /// <summary>
    /// Built here rather than through <c>GenerateClientCertificatePfx</c>, which always mints a
    /// certificate that is valid right now — there is deliberately no way to ask it for an expired
    /// one. Everything else matches what it produces.
    /// </summary>
    private static string SelfSignedPfx(DateTimeOffset notBefore, DateTimeOffset notAfter)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=RavensPort MCP Client", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        using var certificate = request.CreateSelfSigned(notBefore, notAfter);
        return Convert.ToBase64String(certificate.Export(X509ContentType.Pfx, Password));
    }

    [Fact]
    public void AnExpiredCertificate_StillBindsHttps_AndIsReportedExpired()
    {
        using var state = new KestrelMtlsState();

        state.Enable(
            SelfSignedPfx(DateTimeOffset.UtcNow.AddDays(-120), DateTimeOffset.UtcNow.AddDays(-30)),
            Password);

        // Loaded and serving: the listener comes up rather than falling back to http, which is what
        // stops an expired certificate from quietly removing the protection the user switched on.
        Assert.True(state.IsEnabled);
        Assert.Equal("https", state.Scheme);

        // And reported for what it is, so the startup log and the Settings tab can say why every
        // caller is about to be dropped during the handshake with no status code to explain it.
        Assert.True(state.IsExpired);
    }

    [Fact]
    public void ACurrentCertificate_IsNotReportedExpired()
    {
        using var state = new KestrelMtlsState();

        state.Enable(
            SelfSignedPfx(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(89)),
            Password);

        Assert.True(state.IsEnabled);
        Assert.False(state.IsExpired);
    }

    /// <summary>
    /// The window is closed at both ends. A certificate whose start date has not arrived is as
    /// unusable as one that has run out — the backdated start in the factory exists so a client
    /// whose clock runs fast does not land here, not so this case can be ignored.
    /// </summary>
    [Fact]
    public void ACertificateThatIsNotYetValid_IsReportedExpired()
    {
        using var state = new KestrelMtlsState();

        state.Enable(
            SelfSignedPfx(DateTimeOffset.UtcNow.AddDays(30), DateTimeOffset.UtcNow.AddDays(120)),
            Password);

        Assert.True(state.IsExpired);
    }

    /// <summary>
    /// <see cref="KestrelMtlsState.ExpiresUtc"/> is what the log and the Settings tab print, so it
    /// has to be the certificate's own date rather than anything derived from when it was loaded.
    /// </summary>
    [Fact]
    public void ExpiresUtc_IsTheCertificatesOwnNotAfter()
    {
        var notAfter = DateTimeOffset.UtcNow.AddDays(45);

        using var state = new KestrelMtlsState();
        state.Enable(SelfSignedPfx(DateTimeOffset.UtcNow.AddDays(-1), notAfter), Password);

        Assert.NotNull(state.ExpiresUtc);

        // To the second: X509 encodes whole seconds, so a round-trip drops sub-second precision.
        Assert.Equal(notAfter.ToUnixTimeSeconds(), state.ExpiresUtc!.Value.ToUnixTimeSeconds());
    }

    /// <summary>
    /// With mTLS off there is nothing to be expired about, and <c>IsExpired</c> must not read as
    /// "yes" on a null certificate — the startup path branches on it before anything else.
    /// </summary>
    [Fact]
    public void WithNoCertificateLoaded_NothingIsReportedExpired()
    {
        using var state = new KestrelMtlsState();

        Assert.False(state.IsEnabled);
        Assert.False(state.IsExpired);
        Assert.Null(state.ExpiresUtc);
        Assert.Equal("http", state.Scheme);
    }
}
