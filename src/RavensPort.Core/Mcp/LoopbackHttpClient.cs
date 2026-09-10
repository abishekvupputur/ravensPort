using System.Security.Cryptography.X509Certificates;
using RavensPort.Core.Diagnostics;
using RavensPort.Core.Models;
using RavensPort.Core.Proxy;
using RavensPort.Core.Storage;

namespace RavensPort.Core.Mcp;

/// <summary>
/// The one definition of how this app dials its own listener: the pinned mTLS handler, the
/// loopback test that decides whether the pin applies, and the base address of a route.
///
/// Both callers that hop back in — the funnel's connection pool, and the API bridge's tool calls —
/// go through here. The alternative was copying <see cref="CreateHandler"/> into the second one,
/// and the reasoning in it is not the kind that survives being duplicated: it is thirty lines
/// about which leg of a redirect chain may see the user's private certificate, learned from one
/// upstream that answers 302 to a public host.
/// </summary>
public sealed class LoopbackHttpClient : IDisposable
{
    private readonly KestrelMtlsState _kestrelMtls;
    private readonly ConfigStoreCache _configStoreCache;
    private readonly ActivityLog _activityLog;

    private readonly Lazy<HttpClient> _client;

    public LoopbackHttpClient(
        KestrelMtlsState kestrelMtls,
        ConfigStoreCache configStoreCache,
        ActivityLog activityLog)
    {
        _kestrelMtls = kestrelMtls;
        _configStoreCache = configStoreCache;
        _activityLog = activityLog;

        // Lazily, and once. The certificate is not loaded when the container is built — Kestrel
        // decides that during startup — so a handler created in the constructor would be the
        // plain one for the life of the process even after mTLS came up.
        _client = new Lazy<HttpClient>(CreateClient, isThreadSafe: true);
    }

    /// <summary>
    /// The shared client the API bridge issues tool calls on.
    ///
    /// Redirects are deliberately <em>not</em> followed. A tool call is one hop to this app's own
    /// listener, and the credential is attached there by the transform; if this client followed a
    /// 302 it would re-issue the request itself, off loopback, without that credential and
    /// possibly replaying a body to a third party. A 3xx is reported to the agent instead.
    /// </summary>
    public HttpClient Client => _client.Value;

    /// <summary>
    /// A fresh handler for a caller that owns its own <see cref="HttpClient"/> — the funnel's
    /// connection pool does, because its transport takes ownership.
    ///
    /// Both ends hold the same self-signed certificate, so neither can validate the other by
    /// chain: the server has no issuer the client trusts, and vice versa. Pinning replaces that —
    /// on loopback, the only certificate accepted is the one the user generated, and chain and
    /// name errors are expected and deliberately not consulted.
    ///
    /// The word "loopback" is doing real work there, because this handler does not only ever see
    /// this app. The hop is one HTTP request and the upstream behind the route is free to answer
    /// with a redirect somewhere else entirely — Google Apps Script always answers 302 to
    /// script.googleusercontent.com — and HttpClient follows it on this same handler. Applying the
    /// pin to that leg rejected a perfectly good public certificate, which is what turned every
    /// Apps Script MCP source into "The SSL connection could not be established"; offering the
    /// user's private client certificate on it was the quieter half of the same mistake. Off
    /// loopback this is therefore an ordinary HTTPS client and nothing more.
    /// </summary>
    public SocketsHttpHandler CreateHandler(bool followRedirects = true)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = followRedirects,
        };

        if (!_kestrelMtls.IsEnabled) return handler;

        // Not null: IsEnabled is exactly "a certificate is loaded".
        var certificate = _kestrelMtls.Certificate!;
        var expectedThumbprint = certificate.Thumbprint;

        handler.SslOptions = new System.Net.Security.SslClientAuthenticationOptions
        {
            ClientCertificates = [certificate],

            // targetHost is the name being dialled on this connection, so it still says
            // "somewhere else" after a redirect, when the endpoint this handler was built for
            // no longer does.
            LocalCertificateSelectionCallback = (_, targetHost, _, _, _) =>
                IsLoopback(targetHost) ? certificate : null!,

            RemoteCertificateValidationCallback = (_, presented, _, errors) =>
                Validate(presented, errors, expectedThumbprint),
        };

        return handler;
    }

    private bool Validate(X509Certificate? presented, System.Net.Security.SslPolicyErrors errors, string expectedThumbprint)
    {
        // GetCertHashString on the base type rather than X509Certificate2.Thumbprint: both are
        // the SHA-1 hash in the same hex form, but SslStream is only contracted to hand back an
        // X509Certificate, and a type check that failed would read as "wrong certificate" — the
        // one conclusion that is definitely not what happened.
        var actual = presented?.GetCertHashString();

        if (string.Equals(actual, expectedThumbprint, StringComparison.OrdinalIgnoreCase))
        {
            // The pin does not exempt it from its own dates. Pinning replaces chain validation,
            // which is what would ordinarily have caught this; leaving it out would mean the
            // listener refuses an expired certificate while the caller accepts one, and the two
            // are the same certificate.
            if (MtlsCertificateFactory.IsWithinValidity(presented!, DateTimeOffset.UtcNow)) return true;

            _activityLog.Log(
                $"RavensPort refused the stored mTLS certificate {Redact(actual)}: it is outside its "
                + "validity window. Generate a new one on the Settings tab, install it on every "
                + "client, and restart RavensPort.");

            return false;
        }

        // Anything that is not the pinned certificate has to earn trust the ordinary way. On
        // loopback nothing can: a public CA does not issue for 127.0.0.1, so this stays a pin
        // there and only relaxes where it has to.
        if (errors == System.Net.Security.SslPolicyErrors.None) return true;

        _activityLog.Log(
            $"RavensPort refused a TLS certificate: expected the stored mTLS certificate "
            + $"{Redact(expectedThumbprint)}, was offered {Redact(actual) ?? "no certificate"} ({errors}).");

        return false;
    }

    /// <summary>
    /// Whether a host being dialled is this machine. Only these get the client certificate and the
    /// pinned server certificate; a redirect anywhere else is ordinary public HTTPS.
    /// </summary>
    public static bool IsLoopback(string? host)
    {
        if (string.IsNullOrEmpty(host)) return false;
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)) return true;

        // Strips the brackets an IPv6 literal carries in a URI authority; IPAddress.TryParse does
        // not accept them.
        var trimmed = host.StartsWith('[') && host.EndsWith(']') ? host[1..^1] : host;

        return System.Net.IPAddress.TryParse(trimmed, out var address) && System.Net.IPAddress.IsLoopback(address);
    }

    /// <summary>
    /// The address a route answers on, from this app's point of view.
    ///
    /// The scheme comes from the listener's own state, never from settings. The setting is what
    /// the user asked for; this is what Kestrel actually bound, and only the second one is
    /// dialable — they differ for the whole of a session where mTLS was switched on or off and the
    /// restart it asks for has not happened yet.
    ///
    /// The trailing slash matters: this is used as a base for a relative path, and without it the
    /// last segment of the prefix would be replaced rather than extended.
    /// </summary>
    public Uri BaseFor(RouteMapping route)
    {
        var prefix = route.PathPrefix.TrimEnd('/');
        var port = _configStoreCache.Current.Settings.ListenPort;

        return new Uri($"{_kestrelMtls.Scheme}://127.0.0.1:{port}{prefix}/");
    }

    private HttpClient CreateClient() =>
        new(CreateHandler(followRedirects: false))
        {
            // Per-call budgets are set by the caller on a linked token, so this only has to be
            // higher than any of them rather than meaningful on its own.
            Timeout = Timeout.InfiniteTimeSpan,
        };

    /// <summary>Enough to tell two certificates apart in a log without writing a full identifier.</summary>
    private static string? Redact(string? thumbprint) =>
        thumbprint is null ? null : $"…{thumbprint[^8..]}";

    public void Dispose()
    {
        if (_client.IsValueCreated) _client.Value.Dispose();
    }
}
