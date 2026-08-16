using System.Diagnostics.CodeAnalysis;

namespace RavensPort.Core.Models;

/// <summary>
/// One place deciding which URLs this app is willing to talk to or hand to the shell.
///
/// Every field these guard carries something sensitive: token endpoints receive the client
/// secret and refresh token, upstream base URLs receive the access token. A mistyped or
/// pasted "http://" in any of them puts those on the wire in cleartext, and nothing in the
/// app noticed before. Loopback is exempted because local development upstreams over plain
/// HTTP never leave the machine.
/// </summary>
public static class UrlValidation
{
    private static readonly string[] LoopbackHosts = ["127.0.0.1", "localhost", "::1"];

    /// <summary>
    /// Validates a URL destined for network use. Returns null when acceptable, or a message
    /// suitable for showing in the UI footer.
    /// </summary>
    public static string? ValidateEndpoint(string? url, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            return $"{fieldName} is not a valid absolute URL.";
        }

        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
        {
            return $"{fieldName} must be an http or https URL (got '{uri.Scheme}').";
        }

        if (uri.Scheme == Uri.UriSchemeHttp && !IsLoopback(uri))
        {
            return $"{fieldName} uses plain http, which would send tokens and secrets over the "
                   + "network in cleartext. Use https (plain http is allowed only for localhost).";
        }

        return null;
    }

    /// <summary>
    /// Gate for anything about to be handed to Process.Start with UseShellExecute, where a
    /// non-http value would launch a program or protocol handler rather than a browser.
    /// </summary>
    public static bool IsSafeToOpenInBrowser(string? url) => IsSafeToOpenInBrowser(url, out _);

    /// <summary>
    /// As above, and hands back what it parsed.
    ///
    /// Callers shell out <c>uri.AbsoluteUri</c> rather than the string they passed in, so the
    /// value Windows resolves is the one this method actually inspected. Checking a string and
    /// then launching that same string leaves a gap wherever Uri's parse differs from what the
    /// shell makes of it; passing the parsed form closes it by construction. AbsoluteUri is the
    /// documented fully-escaped form, so nothing is decoded on the way out.
    /// </summary>
    public static bool IsSafeToOpenInBrowser(string? url, [NotNullWhen(true)] out Uri? uri) =>
        Uri.TryCreate(url, UriKind.Absolute, out uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    private static bool IsLoopback(Uri uri) =>
        uri.IsLoopback || LoopbackHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase);
}
