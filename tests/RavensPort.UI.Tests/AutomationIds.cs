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

    // Settings. Disconnect is two presses: the first asks, the second does it — and the second is a
    // different button, which is the point of the confirmation rather than an accident of layout.
    public const string Disconnect = "settings.disconnect";
    public const string ConfirmDisconnect = "settings.disconnect.confirm";
}
