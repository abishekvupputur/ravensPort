namespace RavensPort.Core.Models;

/// <summary>
/// One API turned into an MCP endpoint, served at <c>/api-mcp/{Slug}</c>.
///
/// A bridge is the other half of a route. The route already knows how to reach an upstream with a
/// credential attached; the manifest says which of that upstream's operations are worth offering
/// to an agent and what to call them. Tool calls are issued back through this app's own listener
/// so they take the ordinary proxied path — LocalAccessGuard, YARP, then the credential transform
/// — and no token is ever attached anywhere but there.
/// </summary>
public sealed class McpApiBridgeRecord
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public required string Name { get; set; }

    /// <summary>Last path segment of the endpoint. Unique across bridges.</summary>
    public required string Slug { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// The secret a caller must present to use this endpoint. Only this key opens
    /// /api-mcp/{Slug} — not the key of the route the bridge calls, and not a funnel's. An agent
    /// handed this key can reach exactly the operations this manifest declares.
    /// </summary>
    public ProxyKey Key { get; set; } = new();

    /// <summary>
    /// The one route every tool path is relative to, and whose credential every call therefore
    /// carries. One route per bridge keeps "which token did this call spend" answerable by
    /// looking at the bridge rather than at the tool.
    /// </summary>
    public Guid RouteId { get; set; }

    public McpApiBridgeManifest Manifest { get; set; } = new();

    /// <summary>
    /// Where the manifest came from — a file name, or "pasted". Shown on the row so a user with
    /// several bridges can tell which file to re-import after editing it. Never read as data.
    /// </summary>
    public string ManifestOrigin { get; set; } = "";

    public DateTimeOffset ImportedUtc { get; set; } = DateTimeOffset.UtcNow;
}
