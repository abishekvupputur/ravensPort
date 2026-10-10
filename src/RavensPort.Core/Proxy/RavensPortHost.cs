using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using RavensPort.Core.Diagnostics;
using RavensPort.Core.Mcp;
using RavensPort.Core.Storage;

namespace RavensPort.Core.Proxy;

/// <summary>
/// The parts of starting the proxy that every host shares — the desktop app and the headless
/// <c>ravensport serve</c>. They used to live in the desktop app's startup, which is where they
/// were first needed; a second host copying them would be two answers to "how does RavensPort
/// listen", and the order of the middleware below is load-bearing enough that two copies would
/// drift into a security bug.
/// </summary>
public static class RavensPortHost
{
    /// <summary>
    /// Named, machine-wide, and the same in every host: the desktop app and the headless server
    /// would otherwise fight over the proxy port and the fixed OAuth loopback ports, and the loser
    /// fails with "conflicts with an existing registration on the machine".
    /// </summary>
    public const string SingleInstanceMutexName = "RavensPort_SingleInstance";

    /// <summary>
    /// Kestrel settings: long keep-alives for MCP sessions, and the mTLS handshake rules.
    ///
    /// Deliberately no listen URL. The port lives in the vault, which cannot be read until a
    /// password manager is unlocked, so <see cref="WebApplication.Urls"/> is set once the store has
    /// loaded — it stays writable right up until Start().
    /// </summary>
    public static WebApplicationBuilder ConfigureRavensPortKestrel(this WebApplicationBuilder builder)
    {
        builder.WebHost.ConfigureKestrel(options =>
        {
            // Long-lived MCP SSE/streamable-HTTP sessions shouldn't be dropped by Kestrel.
            options.Limits.KeepAliveTimeout = TimeSpan.FromHours(2);

            var kestrelMtls = options.ApplicationServices.GetRequiredService<KestrelMtlsState>();

            // Runs when the https endpoint is bound, which is inside Start() — after
            // ApplyMtlsDecision has read the vault and settled the state below. Reading it out here
            // instead would settle nothing: at this point the vault has not been unlocked.
            options.ConfigureHttpsDefaults(https =>
            {
                if (kestrelMtls.Certificate is not { } certificate) return;

                https.ServerCertificate = certificate;
                https.ClientCertificateMode = Microsoft.AspNetCore.Server.Kestrel.Https.ClientCertificateMode.RequireCertificate;

                // The certificate is self-signed and shared by both ends, so there is no chain to
                // validate and the default handling — which rejects on any SslPolicyError — would
                // refuse every caller including this app's own funnel. The thumbprint is the check.
                //
                // Plus the validity window, put back by hand: turning off chain validation turns
                // off the platform's expiry check with it, and without this line a certificate
                // that expired months ago would still open the proxy. That is the whole of what
                // retires one — there is no CA here, so no CRL and no OCSP.
                https.ClientCertificateValidation = (clientCert, _, _) =>
                    string.Equals(clientCert.Thumbprint, certificate.Thumbprint, StringComparison.OrdinalIgnoreCase)
                    && MtlsCertificateFactory.IsWithinValidity(clientCert, DateTimeOffset.UtcNow);
            });
        });

        return builder;
    }

    /// <summary>
    /// Settles whether the listener is https with a client certificate, from the loaded store, and
    /// says in the log what was decided. Must run after the store has loaded and before the URL is
    /// chosen, because the state decides both the scheme Kestrel listens on and the scheme the MCP
    /// funnel dials its own routes on — anything that read the setting a second time could disagree.
    /// </summary>
    public static void ApplyMtlsDecision(IServiceProvider services)
    {
        var store = services.GetRequiredService<ConfigStoreCache>().Current;
        var activityLog = services.GetService<ActivityLog>();

#if STORE_BUILD
        // The store build has no mTLS: no listener certificate, no Generate button, no
        // MtlsCertificateFactory.GenerateClientCertificatePfx to call. See BuildProfile.
        //
        // The setting is still read, because the vault is shared. Someone running the EXE on one
        // machine and the Store package on another has one store between them, and it may well say
        // mTLS is on. Silently binding plain HTTP would be the one outcome worth avoiding here —
        // the user believes the proxy demands a certificate — so the listener still comes up on
        // http:// (there is no alternative that starts) and the log says plainly that this build
        // ignored the setting.
        if (store.Settings.MtlsEnabled)
        {
            activityLog?.Log(
                "STARTUP mTLS is switched on in this vault, but the Microsoft Store build of "
                + "RavensPort does not support it — the proxy is listening on http://127.0.0.1 "
                + "and every caller still needs its endpoint's proxy key. Install RavensPort "
                + "from the releases page if you need client certificates.");
        }
#else
        if (!store.Settings.MtlsEnabled) return;

        var kestrelMtls = services.GetRequiredService<KestrelMtlsState>();

        // A store can say "mTLS on" and hold nothing this build can open: no certificate at all,
        // because earlier builds let the checkbox be ticked without generating one, or a
        // certificate written before the password box existed, whose built-in password no longer
        // exists to try.
        //
        // Minting one here is not an answer. Generating means choosing the PFX password, every
        // certificate this app writes carries a password the user typed, and startup is precisely
        // the moment there is nobody to ask. Refusing to start would strand them with no way back
        // to the setting — so the listener comes up on http:// and the log says plainly that it
        // did, which is the same trade the store build makes above.
        var storedPfx = store.Settings.MtlsClientCertificatePfx;
        var storedPfxPassword = store.Settings.MtlsClientCertificatePassword;

        if (string.IsNullOrWhiteSpace(storedPfx) || string.IsNullOrEmpty(storedPfxPassword))
        {
            activityLog?.Log(
                "STARTUP mTLS is switched on, but there is no client certificate this build can open "
                + "— either none is stored, or the stored one predates the password box and has no "
                + "password recorded for it. The proxy is listening on http://127.0.0.1 and every "
                + "caller still needs its endpoint's proxy key. Use Generate new certificate on the "
                + "Settings tab, choose a password, install the export on every client, and restart.");
            return;
        }

        kestrelMtls.Enable(storedPfx, storedPfxPassword);

        // Either way the thumbprint is recorded at the moment it is decided. When the funnel later
        // refuses this listener it logs what was presented; without that there is nothing to
        // compare against, and "the remote certificate was rejected" is equally consistent with a
        // stale certificate, a certificate regenerated since the last start, and Kestrel never
        // having received one.
        //
        // An expired certificate does not get the line saying mTLS is up. The date is enforced
        // rather than warned about — pinning a thumbprint turns the platform's own expiry check
        // off, so both validation callbacks put it back by hand — and the listener binds and then
        // refuses everyone, this app's own funnel included. "mTLS enabled" would be the last thing
        // read before a connection that closes with no status code to explain it.
        var thumbprintTail = kestrelMtls.Certificate!.Thumbprint[^8..];

        if (kestrelMtls.IsExpired)
        {
            activityLog?.Log(
                $"STARTUP the mTLS certificate expired on {kestrelMtls.ExpiresUtc:yyyy-MM-dd}. The "
                + $"listener is bound on https and presenting certificate …{thumbprintTail}, but the "
                + "expiry date is enforced at both ends: every caller is refused during the TLS "
                + "handshake — no status code reaches them, the connection simply closes — and the MCP "
                + "funnel cannot reach its own routes either. Generate a new certificate on the "
                + "Settings tab, export it, install it on every client, and restart RavensPort.");
        }
        else
        {
            activityLog?.Log(
                $"mTLS enabled — serving certificate …{thumbprintTail} "
                + "and requiring the same one from every caller.");
        }
#endif
    }

    /// <summary>
    /// Points Kestrel at the loopback address on the stored port, with the scheme the mTLS decision
    /// chose. Loopback only, in every host: LocalAccessGuard's model assumes nothing off this
    /// machine can reach the listener at all.
    /// </summary>
    public static int UseStoredListenUrl(this WebApplication app)
    {
        var port = app.Services.GetRequiredService<ConfigStoreCache>().Current.Settings.ListenPort;
        var scheme = app.Services.GetRequiredService<KestrelMtlsState>().Scheme;

        app.Urls.Clear();
        app.Urls.Add($"{scheme}://127.0.0.1:{port}");

        return port;
    }

    /// <summary>
    /// The request pipeline, in the one order that is safe.
    /// </summary>
    public static WebApplication MapRavensPortPipeline(this WebApplication app)
    {
        // Must sit ahead of MapReverseProxy: it rejects callers that cannot present the endpoint's
        // proxy key, and blocks DNS-rebinding and browser-originated requests. Without it, any
        // process on this machine can spend the user's OAuth grant.
        app.UseLocalAccessGuard();

        // After the guard, so funnel callers must present a proxy key like anyone else, and before
        // MapReverseProxy so /mcp is unambiguously the funnel's — routes are forbidden from
        // claiming that prefix.
        app.UseMcpFunnelGate();
        app.MapMcpFunnel();

        // Same arrangement, same reasons, for the API bridges: after the guard so a caller must
        // hold the bridge's own key, and before MapReverseProxy so /api-mcp is unambiguously theirs.
        app.UseMcpApiBridgeGate();
        app.MapMcpApiBridge();

        app.MapReverseProxy();

        return app;
    }
}
