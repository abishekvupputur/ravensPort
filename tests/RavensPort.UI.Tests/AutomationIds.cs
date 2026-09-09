namespace RavensPort.UI.Tests;

/// <summary>
/// The automation ids this suite drives, named once here and set once in the views.
///
/// Kept in the test project on purpose. An id exists because a test presses the thing, so the list
/// of them is a list of what is covered — and putting it beside the views would invite ids that no
/// longer drive anything to accumulate unnoticed.
/// </summary>
internal static class AutomationIds
{
    // Setup
    public const string StartSingleUse = "setup.startSingleUse";

    // Credentials
    public const string CredentialKind = "credentials.editor.kind";
    public const string CredentialName = "credentials.editor.name";
    public const string CredentialApiKey = "credentials.editor.apiKey";
    public const string CredentialPlacement = "credentials.editor.placement";
    public const string CredentialParameter = "credentials.editor.parameter";
    public const string SaveCredential = "credentials.editor.save";

    // Routes — an upstream first, then a route pointing at it.
    public const string UpstreamName = "routes.upstream.name";
    public const string UpstreamBaseUrl = "routes.upstream.baseUrl";
    public const string AddUpstream = "routes.upstream.add";
    public const string RoutePrefix = "routes.editor.prefix";
    public const string RouteUpstream = "routes.editor.upstream";
    public const string RouteCredential = "routes.editor.credential";
    public const string AddRoute = "routes.editor.add";

    // A route's own editor, in the grid's row details — realized only for the selected row, which
    // is what keeps these unambiguous across eight routes. Repeated once per attached credential.
    public const string RoutesGrid = "routes.grid";
    public const string AddRouteCredential = "routes.detail.addCredential";
    public const string RouteDetailCredential = "routes.detail.credential";
    public const string RouteDetailPlacement = "routes.detail.placement";
    public const string RouteDetailParameter = "routes.detail.parameter";
    public const string RouteDetailValuePrefix = "routes.detail.valuePrefix";

    // MCP sources and the funnels that pool them.
    public const string EnableFunnel = "funnels.enable";
    public const string SourceName = "funnels.source.name";
    public const string SourceAlias = "funnels.source.alias";
    public const string SourceKind = "funnels.source.kind";
    public const string SourceTransport = "funnels.source.transport";
    public const string SourceUrl = "funnels.source.url";
    public const string SourceRoute = "funnels.source.route";
    public const string AddSource = "funnels.source.add";
    public const string FunnelName = "funnels.funnel.name";
    public const string FunnelSlug = "funnels.funnel.slug";
    public const string AddFunnel = "funnels.funnel.add";
    public const string FunnelsGrid = "funnels.grid";
    public const string IncludeSource = "funnels.funnel.includeSource";

    // Settings. Disconnect is two presses: the first asks, the second does it — and the second is a
    // different button, which is the point of the confirmation rather than an accident of layout.
    public const string Disconnect = "settings.disconnect";
    public const string ConfirmDisconnect = "settings.disconnect.confirm";
}
