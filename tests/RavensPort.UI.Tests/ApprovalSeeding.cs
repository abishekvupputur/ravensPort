using Microsoft.Extensions.DependencyInjection;
using RavensPort.Core.Models;
using RavensPort.Core.Storage;
using RavensPort.UI.ViewModels;
using RavensPort.Views;

namespace RavensPort.UI.Tests;

/// <summary>
/// Builds the approval suite's stage-2 configuration by pressing the buttons a person presses.
///
/// The approval suite writes this straight into the store with a MutateAsync — reasonably, since it
/// is testing the proxy rather than the tabs, and every vault write it spends counts against a
/// hundred-an-hour ceiling. Here the store is free and the tabs are the point, so every credential,
/// upstream, route, source and funnel below arrives through its own form.
///
/// The one thing that cannot: nothing in this file selects a credential kind the app does not offer
/// in that combo. Where the approval suite constructs a CredentialRecord with a token already in
/// it, this seeds the same shape through the editor and then hands the token to the credential
/// directly — the Credentials tab has no "paste an access token" field, because acquiring one is
/// what its buttons are for, and inventing a form here to avoid touching a record would be testing
/// a screen that does not exist.
/// </summary>
internal static class ApprovalSeeding
{
    /// <summary>The two fixtures the assertions look for, matching ApprovalRun's.</summary>
    public const string OAuthToken = "MOCK-OAUTH-ACCESS-TOKEN"; // gitleaks:allow
    public const string ProjectKey = "MOCK-PROJECT-KEY";        // gitleaks:allow

    public const string OAuthCredential = "mock oauth2";
    public const string KeyCredential = "mock project key";

    /// <summary>
    /// The eight routes of the approval suite's credential matrix, named by what each proves.
    ///
    /// Kept as data rather than eight blocks of clicking: the shapes are the interesting part, and a
    /// reader comparing this against BuildRouteMatrix in the approval suite should be able to see
    /// the same eight lines.
    /// </summary>
    public static readonly (string Prefix, (string Credential, string Placement, string Parameter, string Prefix)[] Credentials)[] RouteMatrix =
    [
        // Nothing: a plain forwarding hop to an upstream that needs no token.
        ("/app/none", []),

        // One credential: the usual case.
        ("/app/one", [(OAuthCredential, "Header", "Authorization", "Bearer ")]),

        // Two headers.
        ("/app/two-headers",
        [
            (OAuthCredential, "Header", "Authorization", "Bearer "),
            (KeyCredential, "Header", "X-Project-Key", ""),
        ]),

        // Several headers, one of them with a prefix that is not "Bearer ".
        ("/app/several-headers",
        [
            (OAuthCredential, "Header", "Authorization", "Bearer "),
            (KeyCredential, "Header", "X-Api-Key", ""),
            (KeyCredential, "Header", "PRIVATE-TOKEN", "token "),
        ]),

        // Header plus body.
        ("/app/header-body",
        [
            (OAuthCredential, "Header", "Authorization", "Bearer "),
            (KeyCredential, "Body", "auth_token", ""),
        ]),

        // Two body fields, which have to arrive in one rewrite rather than two.
        ("/app/two-body",
        [
            (OAuthCredential, "Body", "access_token", ""),
            (KeyCredential, "Body", "project_token", ""),
        ]),

        // An OAuth user grant plus a project key, which plenty of APIs demand together.
        ("/app/oauth-plus-key",
        [
            (OAuthCredential, "Header", "Authorization", "Bearer "),
            (KeyCredential, "Header", "X-Api-Key", ""),
        ]),
    ];

    /// <summary>
    /// Seeds the two credentials the matrix attaches, through the Credentials tab.
    /// </summary>
    public static async Task SeedCredentialsAsync(SingleUseHarness harness)
    {
        var credentials = harness.Services.GetRequiredService<CredentialsViewModel>();
        var view = UiDriver.Show<CredentialsView>(credentials);
        var store = harness.Services.GetRequiredService<ConfigStoreCache>();

        // The API key, whole: kind, name, secret, and where it goes by default.
        await UiDriver.SelectAsync(view, AutomationIds.CredentialKind, "API key");
        await UiDriver.TypeAsync(view, AutomationIds.CredentialName, KeyCredential);
        await UiDriver.TypeAsync(view, AutomationIds.CredentialApiKey, ProjectKey);
        await UiDriver.SelectAsync(view, AutomationIds.CredentialPlacement, "Header");
        await UiDriver.TypeAsync(view, AutomationIds.CredentialParameter, "X-Api-Key");
        await UiDriver.ClickAsync(view, AutomationIds.SaveCredential);
        await UiDriver.UntilAsync(() => store.Current.Credentials.Count == 1, "the API key to be saved");

        // The OAuth credential's shape, through the same editor.
        await UiDriver.SelectAsync(view, AutomationIds.CredentialKind, "OAuth 2.0");
        await UiDriver.TypeAsync(view, AutomationIds.CredentialName, OAuthCredential);
        await UiDriver.ClickAsync(view, AutomationIds.SaveCredential);
        await UiDriver.UntilAsync(() => store.Current.Credentials.Count == 2, "the OAuth credential to be saved");

        // The grant itself is not something this tab can be clicked into producing — the browser
        // flow wants a person at a consent screen. The approval suite constructs the token in the
        // record for the same reason, and the matrix below is about where a token is *put*, not
        // about how it was obtained.
        await store.MutateAsync(s =>
        {
            var oauth = s.Credentials.Single(c => c.Name == OAuthCredential);
            oauth.Token = new TokenSet(
                OAuthToken, "refresh", DateTimeOffset.UtcNow.AddHours(1), "Bearer", DateTimeOffset.UtcNow);
        });
    }

    /// <summary>
    /// Adds the echo upstream and every route of the matrix, attaching each route's credentials
    /// through that route's own editor in the grid's row details.
    /// </summary>
    public static async Task SeedRouteMatrixAsync(SingleUseHarness harness)
    {
        var routes = harness.Services.GetRequiredService<RoutesViewModel>();
        var view = UiDriver.Show<RoutesView>(routes);
        var store = harness.Services.GetRequiredService<ConfigStoreCache>();

        await UiDriver.TypeAsync(view, AutomationIds.UpstreamName, "mock-upstream");
        await UiDriver.TypeAsync(view, AutomationIds.UpstreamBaseUrl, harness.UpstreamUrl);
        await UiDriver.ClickAsync(view, AutomationIds.AddUpstream);
        await UiDriver.UntilAsync(() => store.Current.Upstreams.Count == 1, "the upstream to be added");

        foreach (var (prefix, credentials) in RouteMatrix)
        {
            await UiDriver.TypeAsync(view, AutomationIds.RoutePrefix, prefix);
            await UiDriver.SelectAsync(view, AutomationIds.RouteUpstream, "mock-upstream");
            await UiDriver.ClickAsync(view, AutomationIds.AddRoute);
            await UiDriver.UntilAsync(
                () => store.Current.Routes.Any(r => r.PathPrefix == prefix), $"{prefix} to be added");

            if (credentials.Length == 0) continue;

            // Selecting the row is what brings its editor into existence, so it has to happen
            // before anything below can be found.
            await UiDriver.SelectRowAsync<RouteItemViewModel>(
                view, AutomationIds.RoutesGrid, r => r.PathPrefix == prefix);

            for (var i = 0; i < credentials.Length; i++)
            {
                var (credential, placement, parameter, valuePrefix) = credentials[i];

                await UiDriver.ClickAsync(view, AutomationIds.AddRouteCredential);
                await UiDriver.UntilAsync(
                    () => UiDriver.FindAll<Avalonia.Controls.ComboBox>(view, AutomationIds.RouteDetailCredential).Count > i,
                    $"credential editor #{i} on {prefix}");

                await UiDriver.SelectNthAsync(view, AutomationIds.RouteDetailCredential, i, credential);
                await UiDriver.SelectNthAsync(view, AutomationIds.RouteDetailPlacement, i, placement);
                await UiDriver.TypeNthAsync(view, AutomationIds.RouteDetailParameter, i, parameter);
                await UiDriver.TypeNthAsync(view, AutomationIds.RouteDetailValuePrefix, i, valuePrefix);
            }

            await UiDriver.UntilAsync(
                () => store.Current.Routes.Single(r => r.PathPrefix == prefix).Credentials.Count == credentials.Length,
                $"{prefix} to carry {credentials.Length} credentials");
        }
    }

    /// <summary>
    /// Ticks "Enable MCP funnel", which is what makes anything under /mcp answer at all.
    /// </summary>
    public static async Task EnableFunnelAsync(SingleUseHarness harness)
    {
        var funnels = harness.Services.GetRequiredService<McpFunnelViewModel>();
        var view = UiDriver.Show<McpFunnelView>(funnels);
        var store = harness.Services.GetRequiredService<ConfigStoreCache>();

        await UiDriver.CheckAsync(view, AutomationIds.EnableFunnel, true);
        await UiDriver.UntilAsync(
            () => store.Current.Settings.McpFunnelEnabled, "the funnel to be switched on");
    }

    /// <summary>
    /// A funnel whose source is one of the proxy's own routes, which is the arrangement that puts a
    /// credential transform between the funnel and the MCP server behind it.
    ///
    /// Everything here goes through a form: the upstream and route on the Routes tab, the route's
    /// OAuth credential in that route's own editor, then a ProxyRoute source and the funnel over it
    /// on the funnel tab.
    /// </summary>
    public static async Task SeedSecuredRouteFunnelAsync(SingleUseHarness harness, string mcpServerUrl)
    {
        var store = harness.Services.GetRequiredService<ConfigStoreCache>();

        var routes = harness.Services.GetRequiredService<RoutesViewModel>();
        var routesView = UiDriver.Show<RoutesView>(routes);

        await UiDriver.TypeAsync(routesView, AutomationIds.UpstreamName, "mcp-secured");
        await UiDriver.TypeAsync(routesView, AutomationIds.UpstreamBaseUrl, mcpServerUrl);
        await UiDriver.ClickAsync(routesView, AutomationIds.AddUpstream);
        await UiDriver.UntilAsync(
            () => store.Current.Upstreams.Any(u => u.Name == "mcp-secured"), "the secured upstream");

        await UiDriver.TypeAsync(routesView, AutomationIds.RoutePrefix, "/mcpsecured");
        await UiDriver.SelectAsync(routesView, AutomationIds.RouteUpstream, "mcp-secured");
        await UiDriver.ClickAsync(routesView, AutomationIds.AddRoute);
        await UiDriver.UntilAsync(
            () => store.Current.Routes.Any(r => r.PathPrefix == "/mcpsecured"), "the secured route");

        await UiDriver.SelectRowAsync<RouteItemViewModel>(
            routesView, AutomationIds.RoutesGrid, r => r.PathPrefix == "/mcpsecured");

        await UiDriver.ClickAsync(routesView, AutomationIds.AddRouteCredential);
        await UiDriver.UntilAsync(
            () => UiDriver.FindAll<Avalonia.Controls.ComboBox>(routesView, AutomationIds.RouteDetailCredential).Count > 0,
            "the credential editor on /mcpsecured");

        await UiDriver.SelectNthAsync(routesView, AutomationIds.RouteDetailCredential, 0, OAuthCredential);
        await UiDriver.SelectNthAsync(routesView, AutomationIds.RouteDetailPlacement, 0, "Header");
        await UiDriver.TypeNthAsync(routesView, AutomationIds.RouteDetailParameter, 0, "Authorization");
        await UiDriver.TypeNthAsync(routesView, AutomationIds.RouteDetailValuePrefix, 0, "Bearer ");

        await UiDriver.UntilAsync(
            () => store.Current.Routes.Single(r => r.PathPrefix == "/mcpsecured").Credentials.Count == 1,
            "/mcpsecured to carry its credential");

        var funnels = harness.Services.GetRequiredService<McpFunnelViewModel>();
        var funnelView = UiDriver.Show<McpFunnelView>(funnels);

        await UiDriver.TypeAsync(funnelView, AutomationIds.SourceName, "secured");
        await UiDriver.TypeAsync(funnelView, AutomationIds.SourceAlias, "secured");
        await UiDriver.SelectAsync(funnelView, AutomationIds.SourceKind, "Proxy route");
        await UiDriver.SelectAsync(funnelView, AutomationIds.SourceTransport, "Streamable HTTP");
        await UiDriver.SelectAsync(funnelView, AutomationIds.SourceRoute, "/mcpsecured");
        await UiDriver.ClickAsync(funnelView, AutomationIds.AddSource);
        await UiDriver.UntilAsync(
            () => store.Current.McpSources.Any(s => s.Name == "secured"), "the secured source");

        await UiDriver.TypeAsync(funnelView, AutomationIds.FunnelName, "oauth");
        await UiDriver.TypeAsync(funnelView, AutomationIds.FunnelSlug, "oauth");
        await UiDriver.ClickAsync(funnelView, AutomationIds.AddFunnel);
        await UiDriver.UntilAsync(
            () => store.Current.McpFunnels.Any(f => f.Slug == "oauth"), "the oauth funnel");

        await UiDriver.SelectRowAsync<McpFunnelItemViewModel>(
            funnelView, AutomationIds.FunnelsGrid, f => f.Slug == "oauth");

        var index = funnels.FunnelSources.Select((s, i) => (s, i)).First(x => x.s.Name == "secured").i;
        await UiDriver.CheckNthAsync(funnelView, AutomationIds.IncludeSource, index, true);

        await UiDriver.UntilAsync(
            () => store.Current.McpFunnels.Single(f => f.Slug == "oauth").Sources.Count == 1,
            "the oauth funnel to pool its source");
    }

    /// <summary>
    /// Adds one MCP source per server, then a funnel over them, through the funnel tab.
    /// </summary>
    public static async Task SeedFunnelAsync(
        SingleUseHarness harness, string funnelName, params (string Name, string Alias, string Url)[] sources)
    {
        var funnels = harness.Services.GetRequiredService<McpFunnelViewModel>();
        var view = UiDriver.Show<McpFunnelView>(funnels);
        var store = harness.Services.GetRequiredService<ConfigStoreCache>();

        var before = store.Current.McpSources.Count;

        for (var i = 0; i < sources.Length; i++)
        {
            var (name, alias, url) = sources[i];

            await UiDriver.TypeAsync(view, AutomationIds.SourceName, name);
            await UiDriver.TypeAsync(view, AutomationIds.SourceAlias, alias);
            await UiDriver.SelectAsync(view, AutomationIds.SourceKind, "Remote URL");
            await UiDriver.SelectAsync(view, AutomationIds.SourceTransport, "Streamable HTTP");
            await UiDriver.TypeAsync(view, AutomationIds.SourceUrl, url);
            await UiDriver.ClickAsync(view, AutomationIds.AddSource);

            var expected = before + i + 1;
            await UiDriver.UntilAsync(() => store.Current.McpSources.Count == expected, $"source '{name}' to be added");
        }

        await UiDriver.TypeAsync(view, AutomationIds.FunnelName, funnelName);
        await UiDriver.TypeAsync(view, AutomationIds.FunnelSlug, funnelName);
        await UiDriver.ClickAsync(view, AutomationIds.AddFunnel);
        await UiDriver.UntilAsync(
            () => store.Current.McpFunnels.Any(f => f.Slug == funnelName), $"funnel '{funnelName}' to be added");

        // A funnel is created empty; which sources it pools is ticked afterwards, against the
        // selected funnel.
        await UiDriver.SelectRowAsync<McpFunnelItemViewModel>(
            view, AutomationIds.FunnelsGrid, f => f.Slug == funnelName);

        foreach (var (name, _, _) in sources)
        {
            var index = funnels.FunnelSources
                .Select((s, i) => (s, i))
                .First(x => x.s.Name == name).i;

            await UiDriver.CheckNthAsync(view, AutomationIds.IncludeSource, index, true);
        }

        await UiDriver.UntilAsync(
            () => store.Current.McpFunnels.Single(f => f.Slug == funnelName).Sources.Count == sources.Length,
            $"funnel '{funnelName}' to pool {sources.Length} sources");
    }
}
