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
    public const string SetupStatus = "setup.status";

    // Credentials
    public const string AddCredential = "credentials.add";
    public const string CredentialName = "credentials.editor.name";
    public const string CredentialKind = "credentials.editor.kind";
    public const string CredentialApiKey = "credentials.editor.apiKey";
    public const string CredentialPlacement = "credentials.editor.placement";
    public const string CredentialParameter = "credentials.editor.parameter";
    public const string SaveCredential = "credentials.editor.save";

    // Routes
    public const string AddRoute = "routes.add";
    public const string RouteName = "routes.editor.name";
    public const string RoutePrefix = "routes.editor.prefix";
    public const string RouteUpstream = "routes.editor.upstream";
    public const string RouteCredential = "routes.editor.credential";
    public const string RouteKey = "routes.editor.key";
    public const string SaveRoute = "routes.editor.save";

    // MCP funnels
    public const string AddFunnel = "funnels.add";
    public const string FunnelName = "funnels.editor.name";
    public const string FunnelSlug = "funnels.editor.slug";
    public const string FunnelUpstream = "funnels.editor.upstream";
    public const string FunnelKey = "funnels.editor.key";
    public const string SaveFunnel = "funnels.editor.save";

    // Settings
    public const string Disconnect = "settings.disconnect";
    public const string SingleUseBanner = "settings.singleUseBanner";
}
