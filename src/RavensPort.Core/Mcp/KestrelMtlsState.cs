using System.Security.Cryptography.X509Certificates;
using RavensPort.Core.Proxy;

namespace RavensPort.Core.Mcp;

/// <summary>
/// The one mTLS decision, settled once at startup and read by everything that has to agree with
/// it: Kestrel (which certificate to present, and to demand), and the funnel's connection pool
/// (which scheme to dial this app's own routes on, and which certificate to present doing it).
/// The two must never disagree — a pool that dials http:// at an https:// listener gets a
/// connection reset with no explanation, which is exactly what "the funnel cannot reach its own
/// routes" looks like from the GUI.
///
/// Holds the <em>parsed</em> certificate rather than the PFX string. X509Certificate2 owns an OS
/// key handle, so re-parsing per connection — which is what the pool used to do — allocated one
/// key per MCP session and never released any of them.
/// </summary>
public sealed class KestrelMtlsState : IDisposable
{
    private X509Certificate2? _certificate;

    /// <summary>
    /// The certificate presented by Kestrel and by the funnel's hop back into its own routes, or
    /// null when mTLS is off. Both ends hold the same one: this is a pinned self-signed pair, not
    /// a PKI, so there is no issuer for the two sides to share instead.
    /// </summary>
    public X509Certificate2? Certificate => _certificate;

    public bool IsEnabled => _certificate is not null;

    /// <summary>
    /// When the certificate in use stops being accepted, or null when mTLS is off. Both ends check
    /// this window on every handshake, so past this moment the proxy refuses its own callers —
    /// including the funnel's hop into its own routes — until a new certificate is generated,
    /// installed everywhere, and the app restarted.
    /// </summary>
    public DateTimeOffset? ExpiresUtc => _certificate?.NotAfter.ToUniversalTime();

    /// <summary>
    /// True once the loaded certificate is outside its validity window. Deliberately not a reason
    /// to refuse to bind: dropping to plain HTTP would tell the user their proxy is certificate-
    /// protected while anything on the machine can call it, and refusing to start would strand
    /// them with no way to reach the button that fixes it. The listener comes up and turns callers
    /// away, which is a state the log and the Settings tab both explain.
    /// </summary>
    public bool IsExpired =>
        _certificate is not null && !MtlsCertificateFactory.IsWithinValidity(_certificate, DateTimeOffset.UtcNow);

    /// <summary>The scheme this app's own listener answers on. The single source of that answer.</summary>
    public string Scheme => IsEnabled ? "https" : "http";

    /// <summary>
    /// Turns mTLS on for the life of the process, from the PFX held in settings. Must be called
    /// before Kestrel binds: the listener's scheme and its client-certificate demand are both
    /// fixed at bind time, which is why toggling the setting in the GUI asks for a restart.
    /// </summary>
    /// <param name="password">
    /// The password the stored PFX was written with, from settings. Required: every certificate
    /// this app writes carries a password the user typed, so there is no built-in one to fall back
    /// to — see <see cref="Models.AppSettings.MtlsClientCertificatePassword"/>.
    /// </param>
    public void Enable(string base64Pfx, string password)
    {
        if (string.IsNullOrWhiteSpace(base64Pfx))
        {
            throw new InvalidOperationException(
                "mTLS is enabled but no client certificate has been generated. Generate one on the Settings tab.");
        }

        var loaded = MtlsCertificateFactory.Load(base64Pfx, password);

        _certificate?.Dispose();
        _certificate = loaded;
    }

    public void Dispose()
    {
        _certificate?.Dispose();
        _certificate = null;
    }
}
