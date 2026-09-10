using RavensPort.Core.Mcp;

namespace RavensPort.Core.Models;

/// <summary>
/// One place deciding whether a funnel or source is usable, shared by the UI and the funnel
/// server for the same reason <see cref="RouteValidation"/> is: the two must not disagree about
/// what a valid slug or alias looks like, or the UI accepts something the endpoint then refuses
/// to serve.
/// </summary>
public static class McpFunnelValidation
{
    /// <summary>
    /// Ceiling on an alias, and therefore on how much of a 128-character MCP name is left for the
    /// tool itself. Public because McpApiBridgeValidation derives its own tool-name cap from it:
    /// a name that no longer fits once prefixed is dropped from a funnel's listing entirely, and
    /// deriving is what stops raising this from silently deleting tools.
    /// </summary>
    public const int MaxAliasLength = 32;

    /// <summary>
    /// The slug becomes a literal path segment in "/mcp/{slug}". Restricting it to lowercase
    /// letters, digits, and hyphens keeps it unambiguous in a URL with no escaping anywhere.
    /// </summary>
    public static string? ValidateSlug(string? slug, IEnumerable<McpFunnelRecord> existing, Guid? editingId = null)
    {
        if (string.IsNullOrWhiteSpace(slug))
        {
            return "Endpoint slug is required.";
        }

        var trimmed = slug.Trim();

        if (trimmed.Length > 64)
        {
            return "Endpoint slug may be at most 64 characters.";
        }

        if (!trimmed.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-'))
        {
            return "Endpoint slug may only contain lowercase letters, digits, and hyphens.";
        }

        if (existing.Any(f => f.Id != editingId && string.Equals(f.Slug, trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            return $"Another funnel already uses the slug '{trimmed}'.";
        }

        return null;
    }

    /// <summary>
    /// The alias is stamped onto every tool and prompt name the source contributes, so it has to
    /// survive as an MCP name: letters, digits, underscore, hyphen. It also has to be unique
    /// across sources, since a call arriving as "alias__tool" is routed by that prefix alone —
    /// two sources sharing an alias would make the destination ambiguous.
    /// </summary>
    public static string? ValidateAlias(string? alias, IEnumerable<McpSourceRecord> existing, Guid? editingId = null)
    {
        if (string.IsNullOrWhiteSpace(alias))
        {
            return "Alias is required — it prefixes every tool this source contributes.";
        }

        var trimmed = alias.Trim();

        if (trimmed.Length > MaxAliasLength)
        {
            return $"Alias may be at most {MaxAliasLength} characters.";
        }

        if (!trimmed.All(c => char.IsAsciiLetterOrDigit(c) || c == '_' || c == '-'))
        {
            return "Alias may only contain letters, digits, underscores, and hyphens.";
        }

        // The separator is "__", so an alias containing it would split in the wrong place and
        // route a call to a source that never offered the tool.
        if (trimmed.Contains(McpNameMapper.Separator, StringComparison.Ordinal))
        {
            return $"Alias may not contain '{McpNameMapper.Separator}' — that separates the alias from the tool name.";
        }

        if (existing.Any(s => s.Id != editingId && string.Equals(s.Alias, trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            return $"Another source already uses the alias '{trimmed}'.";
        }

        return null;
    }

    /// <summary>
    /// Validates the "where does this source live" half of a record: a route-backed source needs
    /// a route that still exists, a bridge-backed one a bridge that still exists, and a URL-backed
    /// one a URL this app is willing to talk to.
    /// </summary>
    public static string? ValidateTarget(
        McpSourceKind kind,
        Guid routeId,
        string? url,
        IEnumerable<RouteMapping> routes,
        Guid bridgeId = default,
        IEnumerable<McpApiBridgeRecord>? bridges = null)
    {
        if (kind == McpSourceKind.ProxyRoute)
        {
            return routes.Any(r => r.Id == routeId)
                ? null
                : "Pick the route this MCP server is reached through.";
        }

        if (kind == McpSourceKind.ApiBridge)
        {
            return bridges?.Any(b => b.Id == bridgeId) == true
                ? null
                : "Pick the API bridge this source exposes.";
        }

        if (string.IsNullOrWhiteSpace(url))
        {
            return "Server URL is required.";
        }

        return UrlValidation.ValidateEndpoint(url, "Server URL");
    }
}
