using Avalonia.Controls;
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

        // The OAuth credential's shape, through the same editor. The client id is not optional:
        // the editor refuses a save without one, and says so on the status line.
        await UiDriver.SelectAsync(view, AutomationIds.CredentialKind, "OAuth2 (user login)");
        await UiDriver.TypeAsync(view, AutomationIds.CredentialName, OAuthCredential);
        await UiDriver.TypeAsync(view, AutomationIds.CredentialClientId, "id");
        await UiDriver.TypeAsync(view, AutomationIds.CredentialClientSecret, "secret"); // gitleaks:allow
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

            // Selecting the row is what brings its editor into existence, and the row is what
            // everything below is searched inside — the grid keeps more than one row’s details
            // realized, so the whole view would offer several of each of these.
            var row = await UiDriver.SelectAndOpenRowAsync<RouteItemViewModel>(
                view, AutomationIds.RoutesGrid, r => r.PathPrefix == prefix);

            for (var i = 0; i < credentials.Length; i++)
            {
                var (credential, placement, parameter, valuePrefix) = credentials[i];

                await UiDriver.ClickAsync(row, AutomationIds.AddRouteCredential);
                await UiDriver.UntilAsync(
                    () => UiDriver.FindAll<Avalonia.Controls.ComboBox>(row, AutomationIds.RouteDetailCredential).Count > i,
                    $"credential editor #{i} on {prefix}");

                // Name before placement, and that order is the product’s rather than a preference.
                // "Add credential" takes the next free slot, so a second entry for the same
                // credential opens on a placement the route is not already using — Body, where a
                // Header is taken. Moving it to Header while it still carries the default name would
                // collide with the entry already there, and the view model refuses that outright.
                // Renaming it first makes the slot free, which is the same sequence a person is
                // walked through by the refusal message.
                await UiDriver.SelectNthAsync(row, AutomationIds.RouteDetailCredential, i, credential);
                await UiDriver.TypeNthAsync(row, AutomationIds.RouteDetailParameter, i, parameter);
                await UiDriver.SelectNthAsync(row, AutomationIds.RouteDetailPlacement, i, placement);
                await UiDriver.TypeNthAsync(row, AutomationIds.RouteDetailParameter, i, parameter);
                await UiDriver.TypeNthAsync(row, AutomationIds.RouteDetailValuePrefix, i, valuePrefix);
            }

            // Counting the editors is not enough: adding one produces an empty row, so a selection
            // that failed to stick would still count. Every field is compared, and a mismatch says
            // what was actually saved rather than leaving it to surface as a missing header later.
            await UiDriver.UntilAsync(
                () => Describe(store, prefix) == Expected(store, credentials),
                $"{prefix} to carry what was configured — wanted [{Expected(store, credentials)}] "
                + $"but saved [{Describe(store, prefix)}]");
        }
    }

    /// <summary>
    /// Cuts one source of one funnel down to the named tools, through that source's own list.
    ///
    /// Three things have to happen in order, and each is a real step a person takes: the funnel is
    /// selected so its sources are shown, every source is refreshed so the tab knows what tools
    /// exist to tick — the list is read from the server, not guessed — and the source row is
    /// expanded so its groups are on screen at all.
    /// </summary>
    public static async Task SelectToolsAsync(
        SingleUseHarness harness, string funnelSlug, string sourceName, params string[] tools)
    {
        var funnels = harness.Services.GetRequiredService<McpFunnelViewModel>();
        var view = UiDriver.Show<McpFunnelView>(funnels);
        var store = harness.Services.GetRequiredService<ConfigStoreCache>();

        await UiDriver.SelectRowAsync<McpFunnelItemViewModel>(
            view, AutomationIds.FunnelsGrid, f => f.Slug == funnelSlug);

        // Nothing can be ticked until the catalogue exists, and the catalogue comes from asking the
        // servers. This is the button that asks.
        await UiDriver.ClickAsync(view, AutomationIds.RefreshAllSources);
        await UiDriver.UntilAsync(
            () => funnels.FunnelSources.Any(s => s.Name == sourceName && s.Tools.Items.Count > 0),
            $"the tools of '{sourceName}' to be discovered");

        // Re-resolved before every interaction rather than held. Expanding a row rebuilds the
        // controls under it, so a container captured a moment ago can be detached from the tree by
        // the time the next click needs it — which shows up as "the view has 0" of something that
        // is plainly on screen.
        Control Source() => UiDriver.Container<McpFunnelSourceItemViewModel>(view, s => s.Name == sourceName);

        // The model is looked up again every time, never held. Refreshing the sources rewrites the
        // catalogue, which reloads the tab and replaces every item in FunnelSources — so a captured
        // instance goes on reporting "expanded" while the one actually on screen is collapsed, and
        // the groups that should be under it are not in the tree at all.
        McpFunnelSourceItemViewModel Model() => funnels.FunnelSources.Single(s => s.Name == sourceName);

        await ExpandAsync(() => Model().IsExpanded, () => UiDriver.ClickAsync(Source(), AutomationIds.ExpandSource),
            $"'{sourceName}' to expand");

        await ExpandAsync(() => Model().Tools.IsExpanded,
            () => UiDriver.ClickNthAsync(Source(), AutomationIds.ExpandGroup, 0),
            $"the tools list of '{sourceName}' to open");

        await UiDriver.SelectNthAsync(Source(), AutomationIds.GroupMode, 0, "Include");

        // The ticks are hidden under "All" — deliberately, so nobody sets a selection that is then
        // silently ignored — so they only exist once the mode above has changed and the list has
        // been laid out.
        await UiDriver.UntilAsync(
            () => Model().Tools.Items.Count > 0
                  && UiDriver.FindAll<CheckBox>(Source(), AutomationIds.GroupItem).Count
                     >= Model().Tools.Items.Count,
            $"the tool ticks of '{sourceName}' to appear once its mode is Include");

        foreach (var tool in tools)
        {
            var index = Model().Tools.Items
                .Select((item, i) => (item, i))
                .First(x => x.item.Name == tool).i;

            await UiDriver.CheckNthAsync(Source(), AutomationIds.GroupItem, index, true);
        }

        await UiDriver.UntilAsync(
            () =>
            {
                var saved = store.Current.McpFunnels.Single(f => f.Slug == funnelSlug).Sources
                    .SingleOrDefault(s => s.SourceId == store.Current.McpSources.Single(m => m.Name == sourceName).Id);

                return saved is not null
                       && saved.ToolMode == McpSelectionMode.Include
                       && tools.All(saved.Tools.Contains)
                       && saved.Tools.Count == tools.Length;
            },
            $"'{funnelSlug}' to serve exactly [{string.Join(", ", tools)}] from '{sourceName}'");
    }

    /// <summary>
    /// Presses an expander until it is open, re-reading the state each time.
    ///
    /// A plain "if collapsed, click" races the reload a refresh triggers: the click lands on a row
    /// that is replaced a moment later, and the next step looks for controls that are no longer
    /// there. Asking again is what makes it settle.
    /// </summary>
    private static async Task ExpandAsync(Func<bool> isOpen, Func<Task> toggle, string because)
    {
        for (var attempt = 0; attempt < 5 && !isOpen(); attempt++)
        {
            await toggle();
            await UiDriver.PumpAsync();
        }

        await UiDriver.UntilAsync(isOpen, because);
    }

    /// <summary>What a route actually carries, in a form a failure message can print.</summary>
    private static string Describe(ConfigStoreCache store, string prefix)
    {
        var route = store.Current.Routes.SingleOrDefault(r => r.PathPrefix == prefix);
        if (route is null) return "(no such route)";

        var names = store.Current.Credentials.ToDictionary(c => c.Id, c => c.Name);

        return string.Join(" | ", route.Credentials.Select(c =>
            $"{(names.TryGetValue(c.CredentialId, out var n) ? n : c.CredentialId.ToString())}"
            + $"/{c.Placement}/{c.ParameterName}/'{c.ValuePrefix}'"));
    }

    /// <summary>The same form, built from what the seeding asked for.</summary>
    private static string Expected(
        ConfigStoreCache store, (string Credential, string Placement, string Parameter, string Prefix)[] wanted) =>
        string.Join(" | ", wanted.Select(w => $"{w.Credential}/{w.Placement}/{w.Parameter}/'{w.Prefix}'"));

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

        var securedRow = await UiDriver.SelectAndOpenRowAsync<RouteItemViewModel>(
            routesView, AutomationIds.RoutesGrid, r => r.PathPrefix == "/mcpsecured");

        await UiDriver.ClickAsync(securedRow, AutomationIds.AddRouteCredential);
        await UiDriver.UntilAsync(
            () => UiDriver.FindAll<Avalonia.Controls.ComboBox>(securedRow, AutomationIds.RouteDetailCredential).Count > 0,
            "the credential editor on /mcpsecured");

        await UiDriver.SelectNthAsync(securedRow, AutomationIds.RouteDetailCredential, 0, OAuthCredential);
        await UiDriver.SelectNthAsync(securedRow, AutomationIds.RouteDetailPlacement, 0, "Header");
        await UiDriver.TypeNthAsync(securedRow, AutomationIds.RouteDetailParameter, 0, "Authorization");
        await UiDriver.TypeNthAsync(securedRow, AutomationIds.RouteDetailValuePrefix, 0, "Bearer ");

        await UiDriver.UntilAsync(
            () => store.Current.Routes.Single(r => r.PathPrefix == "/mcpsecured").Credentials.Count == 1,
            "/mcpsecured to carry its credential");

        var funnels = harness.Services.GetRequiredService<McpFunnelViewModel>();
        var funnelView = UiDriver.Show<McpFunnelView>(funnels);

        await UiDriver.ClickAsync(funnelView, AutomationIds.RefreshFunnels);
        await UiDriver.UntilAsync(
            () => funnels.Routes.Any(r => r.PathPrefix == "/mcpsecured"),
            "the funnel tab to notice the route added on the Routes tab");

        await UiDriver.TypeAsync(funnelView, AutomationIds.SourceName, "secured");
        await UiDriver.TypeAsync(funnelView, AutomationIds.SourceAlias, "secured");
        await UiDriver.SelectAsync(funnelView, AutomationIds.SourceKind, "ProxyRoute");
        await UiDriver.SelectAsync(funnelView, AutomationIds.SourceTransport, "StreamableHttp");
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
    /// Adds one MCP source per server, through the funnel tab.
    ///
    /// Separate from the funnel below, because a source is not a funnel’s property: two funnels can
    /// pool the same one, which is exactly what "both" and "solo" do. Adding a second copy under a
    /// different name is not a way to have it twice — the alias is what a source’s tools are
    /// prefixed with, so a duplicate one is refused on the way in.
    /// </summary>
    public static async Task SeedSourcesAsync(
        SingleUseHarness harness, params (string Name, string Alias, string Url)[] sources)
    {
        var funnels = harness.Services.GetRequiredService<McpFunnelViewModel>();
        var view = UiDriver.Show<McpFunnelView>(funnels);
        var store = harness.Services.GetRequiredService<ConfigStoreCache>();

        foreach (var (name, alias, url) in sources)
        {
            await UiDriver.TypeAsync(view, AutomationIds.SourceName, name);
            await UiDriver.TypeAsync(view, AutomationIds.SourceAlias, alias);
            await UiDriver.SelectAsync(view, AutomationIds.SourceKind, "RemoteUrl");
            await UiDriver.SelectAsync(view, AutomationIds.SourceTransport, "StreamableHttp");
            await UiDriver.TypeAsync(view, AutomationIds.SourceUrl, url);
            await UiDriver.ClickAsync(view, AutomationIds.AddSource);

            await UiDriver.UntilAsync(
                () => store.Current.McpSources.Any(s => s.Name == name),
                $"source '{name}' to be added — the tab says: {funnels.StatusMessage}");
        }
    }

    /// <summary>
    /// Adds a funnel over sources that already exist, through the funnel tab.
    /// </summary>
    public static async Task SeedFunnelAsync(
        SingleUseHarness harness, string funnelName, params string[] sourceNames)
    {
        var funnels = harness.Services.GetRequiredService<McpFunnelViewModel>();
        var view = UiDriver.Show<McpFunnelView>(funnels);
        var store = harness.Services.GetRequiredService<ConfigStoreCache>();

        await UiDriver.TypeAsync(view, AutomationIds.FunnelName, funnelName);
        await UiDriver.TypeAsync(view, AutomationIds.FunnelSlug, funnelName);
        await UiDriver.ClickAsync(view, AutomationIds.AddFunnel);
        await UiDriver.UntilAsync(
            () => store.Current.McpFunnels.Any(f => f.Slug == funnelName), $"funnel '{funnelName}' to be added");

        // A funnel is created empty; which sources it pools is ticked afterwards, against the
        // selected funnel.
        await UiDriver.SelectRowAsync<McpFunnelItemViewModel>(
            view, AutomationIds.FunnelsGrid, f => f.Slug == funnelName);

        foreach (var name in sourceNames)
        {
            var index = funnels.FunnelSources
                .Select((s, i) => (s, i))
                .First(x => x.s.Name == name).i;

            await UiDriver.CheckNthAsync(view, AutomationIds.IncludeSource, index, true);
        }

        await UiDriver.UntilAsync(
            () => store.Current.McpFunnels.Single(f => f.Slug == funnelName).Sources.Count == sourceNames.Length,
            $"funnel '{funnelName}' to pool {sourceNames.Length} sources");
    }
}
