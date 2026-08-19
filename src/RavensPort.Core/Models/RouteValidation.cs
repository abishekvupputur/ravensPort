namespace RavensPort.Core.Models;

/// <summary>
/// One place deciding whether a route's path prefix is usable, so the UI and the YARP config
/// builder cannot disagree about it.
///
/// The prefix is interpolated into an ASP.NET route template ("{prefix}/{**catch-all}"), which
/// gives some ordinary-looking characters structural meaning. A prefix containing '{' produces
/// a template RoutePatternFactory cannot parse; YARP then rejects the *entire* config update
/// and silently keeps the previous one, while the activity log has already announced the route
/// as active. One bad character therefore made every subsequent route edit appear to apply and
/// do nothing.
/// </summary>
public static class RouteValidation
{
    /// <summary>
    /// Structural characters in a route template, plus the ones that would terminate the path
    /// portion of a URL outright.
    /// </summary>
    private static readonly char[] ForbiddenCharacters = ['{', '}', '?', '#', '\\'];

    /// <summary>
    /// Path space this app serves itself. A route claiming "/mcp" would sit next to the funnel
    /// endpoints in the same routing table — and, worse, a funnel source pointing at that route
    /// would forward straight back into the funnel, so a single tools/list would recurse until
    /// something ran out. Endpoint routing already prefers the funnel's literal segments over a
    /// catch-all, so this is about removing the ambiguity rather than resolving it.
    /// </summary>
    public static readonly string[] ReservedPathPrefixes = ["/mcp"];

    /// <summary>
    /// Validates a route path prefix. Returns null when acceptable, or a message suitable for
    /// showing in the UI footer.
    /// </summary>
    public static string? ValidatePathPrefix(string? pathPrefix)
    {
        if (string.IsNullOrWhiteSpace(pathPrefix))
        {
            return "Path prefix is required.";
        }

        var prefix = pathPrefix.Trim();

        if (!prefix.StartsWith('/'))
        {
            return "Path prefix must start with '/'.";
        }

        // A bare "/" builds the pattern "/{**catch-all}", which swallows every request to the
        // proxy and points all of it at one upstream with one credential attached — almost
        // certainly not what someone typing a single slash intended.
        if (prefix.TrimEnd('/').Length == 0)
        {
            return "'/' would capture every request to the proxy. Use a specific prefix such as '/gmail'.";
        }

        if (prefix.Split('/').Any(segment => segment == ".."))
        {
            return "Path prefix may not contain '..' segments.";
        }

        if (prefix.IndexOfAny(ForbiddenCharacters) >= 0)
        {
            return "Path prefix may not contain any of: { } ? # \\ — these have special meaning "
                   + "in a route template and would stop the route from loading.";
        }

        if (prefix.Any(char.IsControl) || prefix.Any(char.IsWhiteSpace))
        {
            return "Path prefix may not contain spaces or control characters.";
        }

        // Whole-segment comparison: "/mcpstuff" is a different area and stays allowed, while
        // both "/mcp" and "/mcp/anything" are refused.
        var normalized = prefix.TrimEnd('/');
        foreach (var reserved in ReservedPathPrefixes)
        {
            if (normalized.Equals(reserved, StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith(reserved + "/", StringComparison.OrdinalIgnoreCase))
            {
                return $"'{reserved}' is reserved for this proxy's own MCP funnel endpoints. Pick another prefix.";
            }
        }

        return null;
    }

    /// <summary>Convenience for callers that only need the yes/no answer.</summary>
    public static bool IsValidPathPrefix(string? pathPrefix) => ValidatePathPrefix(pathPrefix) is null;

    /// <summary>
    /// Characters allowed in an HTTP field name (RFC 9110 "token"). Anything outside this set
    /// cannot be sent as a header at all, so it has to be rejected before it reaches a route.
    /// </summary>
    private const string HeaderNameSpecials = "!#$%&'*+-.^_`|~";

    /// <summary>
    /// Header names whose value this pipeline owns. Letting a route write them would not attach
    /// a credential, it would break the forward: a rewritten Host lands the request on the wrong
    /// virtual host, and a hand-written Content-Length or Transfer-Encoding desynchronizes the
    /// message framing.
    /// </summary>
    private static readonly string[] ReservedHeaderNames =
        ["host", "content-length", "transfer-encoding", "connection", "upgrade"];

    /// <summary>
    /// Validates the "where does the token go" settings of a route. Returns null when
    /// acceptable, or a message suitable for showing in the UI footer.
    /// </summary>
    public static string? ValidateCredentialInjection(CredentialPlacement placement, string? name, string? valuePrefix)
    {
        // Checked before anything else, including the empty-name case: there is no name that
        // makes this placement acceptable, so a "name is required" answer would only send the
        // user off to fill in a field that cannot help. A store written by an older build can
        // still hold one of these, which is why the check lives here and not only in the picker.
        if (!CredentialPlacements.IsPermitted(placement))
        {
            return "Sending a credential in the query string is not permitted — query strings are "
                   + "written to the upstream's access log, to every intermediary's, and to browser "
                   + "history. Use a header placement, or a body field.";
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return placement switch
            {
                CredentialPlacement.Body => "Body field name is required.",
                _ => "Header name is required.",
            };
        }

        // A CR or LF in the prefix would end the header line and let the rest be read as further
        // headers — the classic response/request splitting trick, here aimed at the upstream.
        // The same characters are meaningless in a query value or body field, so they are
        // rejected for every placement rather than only for headers.
        if (valuePrefix is not null && valuePrefix.Any(char.IsControl))
        {
            return "Value prefix may not contain control characters (including newlines).";
        }

        var trimmed = name.Trim();

        switch (placement)
        {
            case CredentialPlacement.Header:
                if (trimmed.Any(c => !char.IsAsciiLetterOrDigit(c) && !HeaderNameSpecials.Contains(c)))
                {
                    return "Header name may only contain letters, digits, and " + HeaderNameSpecials + ".";
                }

                if (ReservedHeaderNames.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
                {
                    return $"'{trimmed}' is set by the proxy itself and cannot carry a credential. "
                           + "Use a header such as 'Authorization' or 'X-Api-Key'.";
                }

                return null;

            default:
                return trimmed.Any(char.IsControl)
                    ? "Body field name may not contain control characters."
                    : null;
        }
    }

    /// <summary>Convenience for callers that only need the yes/no answer.</summary>
    public static bool IsValidCredentialInjection(CredentialInjection injection) =>
        ValidateCredentialInjection(injection.Placement, injection.Name, injection.ValuePrefix) is null;

    /// <summary>
    /// Validates one entry of a route's credential list. Returns null when acceptable, or a
    /// message suitable for showing in the UI footer.
    /// </summary>
    public static string? ValidateCredential(RouteCredential credential)
    {
        if (credential.CredentialId == Guid.Empty)
        {
            return "Pick a credential for this entry, or remove it.";
        }

        return ValidateCredentialInjection(credential.Placement, credential.ParameterName, credential.ValuePrefix);
    }

    /// <summary>
    /// Validates a route's whole credential list.
    ///
    /// An empty list is accepted: a route that attaches nothing is a plain forwarding hop, which
    /// is a real configuration rather than a half-finished one.
    ///
    /// Beyond each entry being usable on its own, no two entries may target the same slot. Two
    /// credentials writing one header (or one query parameter, or one body field) cannot both
    /// arrive — the second silently overwrites the first, so the upstream sees one token while
    /// the UI shows two. There is no reading of that config which does what it says, so it is
    /// refused rather than resolved.
    /// </summary>
    public static string? ValidateCredentials(IReadOnlyList<RouteCredential> credentials)
    {
        foreach (var credential in credentials)
        {
            if (ValidateCredential(credential) is { } error) return error;
        }

        var seen = new Dictionary<string, RouteCredential>(StringComparer.Ordinal);
        foreach (var credential in credentials)
        {
            if (seen.TryGetValue(credential.Slot, out var existing))
            {
                return $"Two credentials are both set to {existing.ToCredentialInjection().Describe()}. "
                       + "Each header, query parameter, and body field can carry only one credential — "
                       + "change one of them to a different name or placement.";
            }

            seen[credential.Slot] = credential;
        }

        return null;
    }

    /// <summary>Convenience for callers that only need the yes/no answer.</summary>
    public static bool IsValidCredentialSet(IReadOnlyList<RouteCredential> credentials) =>
        ValidateCredentials(credentials) is null;
}
