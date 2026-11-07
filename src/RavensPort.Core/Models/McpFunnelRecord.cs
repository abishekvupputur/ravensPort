using System.Text.Json.Serialization;

namespace RavensPort.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum McpSelectionMode
{
    /// <summary>Everything the source offers, including anything it gains later.</summary>
    All,

    /// <summary>Only the listed names. New upstream tools stay hidden until picked.</summary>
    Include,

    /// <summary>Everything except the listed names.</summary>
    Exclude,
}

/// <summary>
/// One MCP endpoint, served at <c>/mcp/{Slug}</c>. The unit an AI agent is pointed at: it decides
/// which sources that agent can reach and, within each, exactly which tools, resources, and
/// prompts it can see.
/// </summary>
public sealed class McpFunnelRecord
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public required string Name { get; set; }

    /// <summary>Last path segment of the endpoint. Unique across funnels.</summary>
    public required string Slug { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// The secret a caller must present to use this endpoint. Only this key opens /mcp/{Slug} —
    /// the keys of the routes the funnel pools do not, and neither does another funnel's. An
    /// agent handed this key can reach exactly the tools this funnel exposes and nothing else.
    /// </summary>
    public ProxyKey Key { get; set; } = new();

    public List<McpFunnelSource> Sources { get; set; } = [];

    /// <summary>
    /// The filter profile this funnel's calls are checked with, unless the upstream a call goes to
    /// has its own in this funnel (<see cref="McpFunnelSource.FilterProfileId"/>). Null: none.
    /// </summary>
    public Guid? FilterProfileId { get; set; }

    /// <summary>
    /// Filters set on the funnel itself, before profiles existed. Still honoured when the funnel has
    /// no profile; the Filters tab turns them into a profile the first time it loads them.
    /// </summary>
    public List<FunnelFilter> Filters { get; set; } = [];
}

/// <summary>
/// A source's membership in one funnel, plus what of it that funnel exposes. Selections are
/// stored as the upstream's own names/URIs — never the prefixed form — so renaming a source's
/// alias does not silently empty every list.
/// </summary>
public sealed class McpFunnelSource
{
    public Guid SourceId { get; set; }

    public McpSelectionMode ToolMode { get; set; } = McpSelectionMode.All;
    public List<string> Tools { get; set; } = [];

    public McpSelectionMode ResourceMode { get; set; } = McpSelectionMode.All;
    public List<string> Resources { get; set; } = [];

    public McpSelectionMode PromptMode { get; set; } = McpSelectionMode.All;
    public List<string> Prompts { get; set; } = [];

    /// <summary>
    /// A filter profile for calls to this upstream through this funnel, overriding the funnel's
    /// own. Null: the funnel's profile applies. Per funnel, so the same upstream can be trusted by
    /// one agent's funnel and not by another's.
    /// </summary>
    public Guid? FilterProfileId { get; set; }

    /// <summary>
    /// Whether <paramref name="name"/> survives this source's selection for a given primitive
    /// kind. Enforced on both the list and the call path: a tool filtered out of tools/list must
    /// also be refused by tools/call, or an agent that learned the name earlier keeps using it.
    /// </summary>
    public static bool Allows(McpSelectionMode mode, List<string> selection, string name) => mode switch
    {
        McpSelectionMode.Include => selection.Contains(name, StringComparer.Ordinal),
        McpSelectionMode.Exclude => !selection.Contains(name, StringComparer.Ordinal),
        _ => true,
    };

    public bool AllowsTool(string name) => Allows(ToolMode, Tools, name);
    public bool AllowsResource(string uri) => Allows(ResourceMode, Resources, uri);
    public bool AllowsPrompt(string name) => Allows(PromptMode, Prompts, name);
}
