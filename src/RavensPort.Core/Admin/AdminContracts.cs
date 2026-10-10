using RavensPort.Core.Models;

namespace RavensPort.Core.Admin;

// The shapes that cross the admin socket. Shared by the server, in whichever RavensPort process is
// running, and by ravensport-cli, which references this assembly — so the two ends cannot disagree
// about a field name.

/// <summary>The reply to anything that changes the store, or the reason it was refused.</summary>
/// <param name="Warning">
/// Set while the session is read-only: the change took effect, and it will be gone when the
/// process exits. Said on every edit rather than once, because the one time it is not said is the
/// time somebody relies on it.
/// </param>
public sealed record AdminMessage(string Message, string? Warning = null);

public sealed record AdminStatus(
    string Backend,
    string VaultName,
    bool ReadOnly,
    int ListenPort,
    string Scheme,
    bool McpFunnelEnabled,
    bool McpApiBridgeEnabled,
    bool MtlsEnabled,
    bool PendingChanges,
    string SyncState,
    int Credentials,
    int Upstreams,
    int Routes,
    int Sources,
    int Funnels,
    int Bridges,
    string Host,
    string Version);

public sealed record CredentialSummary(
    Guid Id, string Name, CredentialKind Kind, bool HasToken, string TokenExpiry, bool NeedsReconnect);

public sealed record UpstreamSummary(Guid Id, string Name, string BaseUrl, int Routes);

public sealed record RouteSummary(
    Guid Id, string PathPrefix, string Upstream, bool Enabled, bool StripPrefix, string Credentials, string KeyExpiry);

public sealed record SourceSummary(
    Guid Id, string Name, string Alias, McpSourceKind Kind, string Target, bool Enabled, string Catalog);

public sealed record FunnelSummary(
    Guid Id, string Name, string Slug, bool Enabled, IReadOnlyList<string> Sources, string KeyExpiry);

public sealed record BridgeSummary(
    Guid Id, string Name, string Slug, string Route, bool Enabled, int Tools, string KeyExpiry);

public sealed record KeyInfo(string Value, string Expiry);

public sealed record CredentialTestReply(bool Success, int? StatusCode, string Message);

public sealed record SettingsReply(
    int ListenPort, bool McpFunnelEnabled, bool McpApiBridgeEnabled, bool MtlsEnabled, bool HasCertificate);

public sealed record AddUpstreamRequest(string Name, string BaseUrl);

/// <param name="KeyLifetimeDays">Null or 0 for a key that never expires.</param>
public sealed record AddRouteRequest(
    string PathPrefix,
    string Upstream,
    bool StripPrefix = true,
    string? Credential = null,
    CredentialPlacement? Placement = null,
    string? ParameterName = null,
    string? ValuePrefix = null,
    int? KeyLifetimeDays = null);

public sealed record AddSourceRequest(
    string Name,
    McpSourceKind Kind,
    string? Alias = null,
    string? Route = null,
    string? Url = null,
    string? Bridge = null,
    McpTransportPreference Transport = McpTransportPreference.Auto);

public sealed record AddFunnelRequest(string Name, string? Slug = null, int? KeyLifetimeDays = null);

/// <summary>Adds a source to a funnel, or replaces its tool selection if it is already there.</summary>
public sealed record FunnelSourceRequest(string Source, McpSelectionMode ToolMode = McpSelectionMode.All, List<string>? Tools = null);

/// <summary>
/// A bridge from a manifest. OpenAPI documents are converted to a manifest by the CLI before they
/// are sent, with the same importer the desktop tab uses, so the server only ever sees manifests.
/// </summary>
public sealed record ImportBridgeRequest(
    string Name, string Route, string ManifestJson, string? Slug = null, string Origin = "", int? KeyLifetimeDays = null);

public sealed record SettingsPatch(
    int? ListenPort = null, bool? McpFunnelEnabled = null, bool? McpApiBridgeEnabled = null, bool? MtlsEnabled = null);

public sealed record GenerateCertificateRequest(string Password);

public sealed record GenerateCertificateReply(string Message, string PfxBase64);

public sealed record ReloadRequest(bool Force = false);
