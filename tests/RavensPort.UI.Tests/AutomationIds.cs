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
    public const string CredentialClientId = "credentials.editor.clientId";
    public const string CredentialClientSecret = "credentials.editor.clientSecret";
    public const string CredentialTokenEndpoint = "credentials.editor.tokenEndpoint";
    public const string CredentialScopes = "credentials.editor.scopes";
    public const string CredentialTestEndpoint = "credentials.editor.testEndpoint";
    public const string SaveCredential = "credentials.editor.save";
    public const string CancelCredentialEdit = "credentials.editor.cancel";
    public const string EditCredentialRow = "credentials.row.edit";
    public const string DeleteCredentialRow = "credentials.row.delete";
    public const string TestCredentialRow = "credentials.row.test";

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
    public const string RefreshFunnels = "funnels.refresh";
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
    public const string RefreshAllSources = "funnels.refreshAllSources";

    // Per-source tool selection, inside an expanded source row on the selected funnel.
    public const string ExpandSource = "funnels.source.expand";
    public const string ExpandGroup = "funnels.group.expand";
    public const string GroupMode = "funnels.group.mode";
    public const string GroupItem = "funnels.group.item";

    // Settings. Disconnect is two presses: the first asks, the second does it — and the second is a
    // different button, which is the point of the confirmation rather than an accident of layout.
    public const string Disconnect = "settings.disconnect";
    public const string ConfirmDisconnect = "settings.disconnect.confirm";
}
