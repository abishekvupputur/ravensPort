using System.Text.Json.Serialization;

namespace RavensPort.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum McpSourceKind
{
    /// <summary>
    /// An MCP server reached through one of this proxy's own routes, so the route's OAuth
    /// credential is attached on the way out. Dialled back through the loopback listener rather
    /// than short-circuited in process — that reuses LocalAccessGuard, YARP, and the credential
    /// transform exactly as an external client would experience them, with no second code path
    /// to keep in sync.
    /// </summary>
    ProxyRoute,

    /// <summary>An MCP server that needs no credential, addressed directly by URL.</summary>
    RemoteUrl,

    /// <summary>
    /// One of this proxy's own API to MCP bridges, dialled back through the loopback listener with
    /// the bridge's key so it passes LocalAccessGuard exactly as an agent's own client would.
    ///
    /// Two hops rather than one — funnel to bridge, then bridge to route — and the credential is
    /// still attached in the one place it has ever been attached, by the transform on the second
    /// hop. Nothing about tokens is duplicated here; what the bridge does duplicate is the
    /// loopback dialling itself, which is why that lives in LoopbackHttpClient rather than being
    /// copied.
    /// </summary>
    ApiBridge,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum McpTransportPreference
{
    /// <summary>
    /// Let the SDK probe for streamable HTTP and fall back to HTTP+SSE. Still the right default:
    /// the fallback only ever fires against a server that offers nothing else.
    /// </summary>
    Auto,

    /// <summary>The current transport, and the one to prefer for anything new.</summary>
    StreamableHttp,

    /// <summary>
    /// The HTTP+SSE transport. Deprecated by the spec since 2025-03-26 and reclassified as
    /// Deprecated proper under the feature lifecycle policy in 2026-07-28, which puts it on a
    /// removal clock of at least twelve months.
    ///
    /// Kept, and deliberately not marked [Obsolete]: the setting exists precisely for servers
    /// that implement nothing else, and a build warning on an enum member the user picked in the
    /// GUI would land on us rather than on them. Pick it only when a source leaves no choice.
    /// </summary>
    Sse,
}

/// <summary>
/// One connectable MCP server. Reusable across funnels: the same source can appear in several
/// funnels with different tool selections, and each of those gets its own upstream session (see
/// McpSourceConnectionPool).
/// </summary>
public sealed class McpSourceRecord
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Display name only. <see cref="Alias"/> is what appears in tool names.</summary>
    public required string Name { get; set; }

    /// <summary>
    /// Prefix stamped onto every name this source contributes, so two upstreams offering a
    /// "search" tool stay distinguishable and a call can be routed back to the right one.
    /// </summary>
    public required string Alias { get; set; }

    public McpSourceKind Kind { get; set; }

    /// <summary>Set when <see cref="Kind"/> is <see cref="McpSourceKind.ProxyRoute"/>.</summary>
    public Guid RouteId { get; set; }

    /// <summary>Set when <see cref="Kind"/> is <see cref="McpSourceKind.RemoteUrl"/>.</summary>
    public string Url { get; set; } = "";

    /// <summary>
    /// Set when <see cref="Kind"/> is <see cref="McpSourceKind.ApiBridge"/>. Its own field rather
    /// than a reuse of <see cref="RouteId"/>: one field meaning two things reads correctly and
    /// behaves wrongly the first time someone switches a source's kind.
    /// </summary>
    public Guid BridgeId { get; set; }

    public McpTransportPreference Transport { get; set; } = McpTransportPreference.Auto;

    public bool Enabled { get; set; } = true;
}
