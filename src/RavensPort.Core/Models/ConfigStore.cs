namespace RavensPort.Core.Models;

/// <summary>
/// Everything the app knows, as one object graph. Versioning lives on the vault document that
/// wraps this rather than here — the store no longer has a file format of its own to version.
/// </summary>
public sealed class ConfigStore
{
    public List<CredentialRecord> Credentials { get; set; } = [];
    public List<UpstreamRecord> Upstreams { get; set; } = [];
    public List<RouteMapping> Routes { get; set; } = [];
    public List<McpSourceRecord> McpSources { get; set; } = [];
    public List<McpFunnelRecord> McpFunnels { get; set; } = [];
    public List<McpApiBridgeRecord> McpApiBridges { get; set; } = [];
    public AppSettings Settings { get; set; } = new();
}
