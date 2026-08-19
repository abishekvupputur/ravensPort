using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using RavensPort.Core.Diagnostics;
using RavensPort.Core.Mcp;
using RavensPort.Core.Models;
using RavensPort.Core.Storage;

namespace RavensPort.Core.Proxy;

/// <summary>
/// The only thing standing between a local caller and the user's OAuth grant.
///
/// Binding Kestrel to 127.0.0.1 keeps off-host traffic out but is *not* an authorization
/// boundary: every process on the machine, under any user account, can reach loopback. Since
/// the proxy attaches a live access token to whatever it forwards, an unguarded listener is a
/// confused deputy that lends the user's Google/Nextcloud session to the first caller who asks.
///
/// Three checks, each closing a different door:
///   1. Endpoint key   — a caller must know a value it cannot guess from the port alone, and the
///                       value is specific to the route or funnel it is calling. There is no
///                       proxy-wide key: the key that opens /app/mail does not open /app/drive
///                       and does not open /mcp/coding-agent.
///   2. Host allowlist — blocks DNS rebinding, where a page on evil.com re-resolves that name
///                       to 127.0.0.1 so the browser treats proxied responses as same-origin
///                       and lets attacker JavaScript read the user's data.
///   3. No Origin      — a browser only sends Origin on cross-site requests; a legitimate
///                       local client (MCP host, curl, a script) never does.
/// </summary>
public static class LocalAccessGuard
{
    public const string ApiKeyHeaderName = "X-Proxy-Key";

    /// <summary>
    /// The name this key was once *also* accepted under, as a fallback for clients that cannot
    /// set headers. No longer accepted: a query string is the one part of a request that gets
    /// written down everywhere — browser history, proxy and server access logs, Referer headers
    /// on any outbound link — so a key sent this way is a key that leaks by design, and this one
    /// is what stands between a local caller and the user's OAuth grant.
    ///
    /// The name is kept because the parameter must still be *stripped* from every request before
    /// forwarding, so a caller that sends it anyway does not hand it to the upstream's access log
    /// as well. See <see cref="StripInternalHeadersFromRequest"/>.
    /// </summary>
    public const string ApiKeyQueryName = "proxy_key";

    /// <summary>
    /// Stamped by the MCP funnel on every request it makes to one of its own sources. A funnel
    /// legitimately calls a route; a route must never lead back into a funnel, which would loop.
    /// Recorded into <see cref="FunnelHopItemKey"/> and stripped here, before forwarding, for the
    /// same reason as the API key: it is this proxy's private signalling and the upstream would
    /// log it.
    /// </summary>
    public const string FunnelHopHeaderName = "X-Proxy-Funnel-Hop";

    /// <summary>
    /// Where the stripped hop marker is preserved for later middleware. The funnel gate runs
    /// after this guard, so by the time it looks the header is already gone.
    /// </summary>
    public const string FunnelHopItemKey = "RavensPort.FunnelHop";

    private static readonly string[] AllowedHosts = ["127.0.0.1", "localhost", "[::1]", "::1"];

    public static IApplicationBuilder UseLocalAccessGuard(this IApplicationBuilder app)
    {
        var configStoreCache = app.ApplicationServices.GetService(typeof(ConfigStoreCache)) as ConfigStoreCache
                               ?? throw new InvalidOperationException("ConfigStoreCache is not registered.");
        var activityLog = app.ApplicationServices.GetService(typeof(ActivityLog)) as ActivityLog
                          ?? throw new InvalidOperationException("ActivityLog is not registered.");

        return app.Use(async (context, next) =>
        {
            var rejection = Reject(context, configStoreCache);

            // Read before stripping, since the funnel gate downstream needs to know.
            context.Items[FunnelHopItemKey] = context.Request.Headers.ContainsKey(FunnelHopHeaderName);

            // Unconditionally, whether or not the request was allowed: the key authenticates
            // the caller to *this* proxy and has no business reaching the upstream, which will
            // happily write it to its own access log. YARP forwards both headers and the query
            // string verbatim, so it has to come off here, before the request goes anywhere.
            StripInternalHeadersFromRequest(context.Request);

            if (rejection is { } reason)
            {
                activityLog.Log($"DENIED {context.Request.Method} {context.Request.Path} — {reason}");
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsync(
                    "Forbidden. This endpoint requires its own proxy key — copy it from the row for "
                    + "this route on RavensPort's Routes tab, or for this funnel on the MCP Funnel tab — "
                    + $"sent as the '{ApiKeyHeaderName}' request header. The key is only ever read from "
                    + $"that header; a '{ApiKeyQueryName}' query parameter is ignored and stripped.");
                return;
            }

            // Upstreams that answer with permissive CORS (Access-Control-Allow-Origin: *) would
            // otherwise hand any web page the ability to read proxied responses directly, since
            // YARP copies response headers through verbatim. Strip them on the way out.
            context.Response.OnStarting(() =>
            {
                foreach (var header in context.Response.Headers.Keys
                             .Where(k => k.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase))
                             .ToList())
                {
                    context.Response.Headers.Remove(header);
                }
                return Task.CompletedTask;
            });

            await next();
        });
    }

    /// <summary>
    /// Removes this proxy's own signalling headers — the local API key in both forms it can
    /// arrive in, and the funnel hop marker — so none of it is forwarded upstream or reaches the
    /// activity log. Everything else is preserved untouched: callers legitimately pass their own
    /// headers and parameters (an upstream's own <c>?token=</c>, for instance), and those still
    /// have to arrive intact.
    /// </summary>
    private static void StripInternalHeadersFromRequest(HttpRequest request)
    {
        request.Headers.Remove(ApiKeyHeaderName);
        request.Headers.Remove(FunnelHopHeaderName);

        if (!request.Query.ContainsKey(ApiKeyQueryName)) return;

        var remaining = request.Query
            .Where(parameter => !string.Equals(parameter.Key, ApiKeyQueryName, StringComparison.OrdinalIgnoreCase))
            .SelectMany(parameter => parameter.Value.Select(value =>
                new KeyValuePair<string, string?>(parameter.Key, value)));

        // Assigning QueryString also invalidates the cached parsed Query collection, so later
        // readers (the transform's logging, YARP's forwarder) see the trimmed version.
        request.QueryString = QueryString.Create(remaining);
    }

    /// <summary>Returns null when the request is allowed, or a short reason when it is not.</summary>
    private static string? Reject(HttpContext context, ConfigStoreCache configStoreCache)
    {
        var request = context.Request;

        // Host carries the port; compare only the name part so any listen port works.
        var host = request.Host.Host;
        if (!AllowedHosts.Contains(host, StringComparer.OrdinalIgnoreCase))
        {
            return $"host '{host}' is not loopback (possible DNS-rebinding attempt)";
        }

        if (request.Headers.ContainsKey("Origin"))
        {
            return "request carries an Origin header, so it came from a web page";
        }

        // Defense in depth, not a live hole. Kestrel percent-decodes the target and *then*
        // removes dot segments, so "%2e%2e%2f" is already resolved by the time routing runs —
        // verified end to end in ProxyForwardingTests over a raw socket, since System.Uri
        // normalizes this away client-side before HttpClient can even send it.
        //
        // Kept because the confinement of a caller to one upstream area rests entirely on that
        // normalization order, and nothing else in this pipeline would notice if it changed:
        // a surviving "../" would let a caller climb above an upstream's base path with the
        // user's access token attached. Costs one string scan per request.
        if (HasDotSegment(request.Path))
        {
            return "path contains a '..' segment";
        }

        // Header only. The query-parameter form used to be accepted for clients that cannot set
        // headers; it is refused now because a query string is copied into places the header
        // never reaches — browser history, the access log of every intermediary, and the Referer
        // of any link the response goes on to load — and this key is the whole of the proxy's
        // authorization. A caller that sends one is told so by name rather than left to guess,
        // since the parameter used to work and the answer would otherwise be a bare 403.
        var presented = request.Headers[ApiKeyHeaderName].ToString();
        if (string.IsNullOrEmpty(presented) && request.Query.ContainsKey(ApiKeyQueryName))
        {
            return $"proxy key was sent as the '{ApiKeyQueryName}' query parameter, which is no "
                   + $"longer accepted — send it as the '{ApiKeyHeaderName}' header";
        }

        // Only the key belonging to the endpoint being called is accepted. A path that belongs to
        // no route and no funnel has no key, and is refused with the same 403 as a wrong key
        // rather than a distinguishable answer — otherwise an unauthenticated caller could map
        // which prefixes exist by watching status codes.
        var target = ResolveTarget(configStoreCache.Current, request.Path);
        if (target is null || !target.Key.IsConfigured)
        {
            return "no proxy key is configured for this path";
        }

        if (!FixedTimeEquals(presented, target.Key.Value))
        {
            return $"missing or incorrect proxy key for {target.Description}";
        }

        // Checked after the value, so an expired key is only named as such to a caller that
        // proved it holds it. Someone guessing gets the generic answer.
        return target.Key.IsExpired(DateTimeOffset.UtcNow)
            ? $"the proxy key for {target.Description} expired on {target.Key.ExpiresUtc:u}"
            : null;
    }

    /// <summary>
    /// The route or funnel a request is addressed to, and therefore whose key it must present.
    /// Null when the path belongs to neither.
    /// </summary>
    public sealed record ProxyTarget(string Description, ProxyKey Key);

    /// <summary>
    /// Resolves a request path to the endpoint that owns it.
    ///
    /// Everything under <see cref="McpFunnelEndpoints.BasePath"/> belongs to the funnel named by
    /// the first segment after it — routes are forbidden from claiming that space, so there is no
    /// overlap to arbitrate. Everything else is matched against route prefixes, longest first:
    /// with routes at "/app" and "/app/mail" a request to /app/mail/x is the second route's, which
    /// is the same choice ASP.NET routing makes when it later picks the endpoint.
    ///
    /// Disabled records still resolve. A disabled route or funnel is served by nothing downstream
    /// and answers 404, and letting it fall through to "unknown path" here would instead turn its
    /// own clients' 404 into a 403 the moment it was switched off — a confusing way to learn that
    /// a checkbox changed.
    /// </summary>
    public static ProxyTarget? ResolveTarget(ConfigStore store, PathString path)
    {
        if (path.StartsWithSegments(McpFunnelEndpoints.BasePath))
        {
            var slug = McpFunnelEndpoints.ExtractSlug(path);
            if (slug is null) return null;

            var funnel = store.McpFunnels.FirstOrDefault(f =>
                string.Equals(f.Slug, slug, StringComparison.OrdinalIgnoreCase));

            return funnel is null ? null : new ProxyTarget($"funnel '{funnel.Name}'", funnel.Key);
        }

        RouteMapping? match = null;
        var matchedLength = -1;

        foreach (var route in store.Routes)
        {
            // A prefix that is empty or does not start with '/' cannot be turned into a PathString
            // at all (it throws), and ProxyConfigBuilder refuses to serve such a route anyway.
            var prefix = route.PathPrefix.TrimEnd('/');
            if (prefix.Length == 0 || prefix[0] != '/') continue;
            if (!path.StartsWithSegments(prefix)) continue;
            if (prefix.Length <= matchedLength) continue;

            match = route;
            matchedLength = prefix.Length;
        }

        return match is null ? null : new ProxyTarget($"route '{match.PathPrefix}'", match.Key);
    }

    /// <summary>
    /// Whole-segment match only. A file legitimately named "notes..txt" is not traversal, and
    /// rejecting every path that merely contains two dots would break real upstream URLs.
    /// </summary>
    private static bool HasDotSegment(PathString path)
    {
        if (path.Value is not { } value || !value.Contains("..", StringComparison.Ordinal)) return false;

        foreach (var segment in value.Split('/'))
        {
            if (segment == "..") return true;
        }

        return false;
    }

    /// <summary>Length-independent, content-constant-time comparison — no early exit to time against.</summary>
    private static bool FixedTimeEquals(string? presented, string expected)
    {
        if (string.IsNullOrEmpty(presented)) return false;

        var presentedBytes = Encoding.UTF8.GetBytes(presented);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);

        // CryptographicOperations.FixedTimeEquals returns early on a length mismatch, which
        // leaks the expected length. Hashing both sides first makes every comparison run over
        // the same 32 bytes regardless of input size.
        Span<byte> presentedHash = stackalloc byte[32];
        Span<byte> expectedHash = stackalloc byte[32];
        SHA256.HashData(presentedBytes, presentedHash);
        SHA256.HashData(expectedBytes, expectedHash);

        return CryptographicOperations.FixedTimeEquals(presentedHash, expectedHash);
    }
}
