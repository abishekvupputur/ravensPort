using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using RavensPort.Core.Models;
using RavensPort.Core.Storage;
using RavensPort.UI.ViewModels;
using RavensPort.Views;

namespace RavensPort.UI.Tests;

/// <summary>
/// The screenshots in media/, which the README and the wiki show: every tab filled with a
/// configuration someone might actually run — real API hosts, the shipped API to MCP templates,
/// funnels that pool them — rather than the approval suite's mock matrix the screen tour uses.
///
/// Draws only when <see cref="HeadlessTestAppBuilder.ScreenshotDirectoryVariable"/> is set, like
/// the tour, and writes each image under the name media/ already uses, so refreshing them is a copy:
///
/// <code>RAVENSPORT_SCREENSHOT_DIR=shots dotnet test tests/RavensPort.UI.Tests --filter DocsScreenshots</code>
///
/// docs-screenshots.yml runs it on Windows, for the fonts the app is normally seen in. Every secret
/// below is an obvious placeholder; nothing here reaches a network.
/// </summary>
public class DocsScreenshotsUiTests
{
    private const int Width = 1240;
    private const int Height = 1040;

    private static string? Directory =>
        Environment.GetEnvironmentVariable(HeadlessTestAppBuilder.ScreenshotDirectoryVariable);

    private static void Snap(TopLevel window, string name)
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }

        var frame = window.CaptureRenderedFrame()
                    ?? throw new InvalidOperationException($"nothing rendered for {name}");

        System.IO.Directory.CreateDirectory(Directory!);
        frame.Save(Path.Combine(Directory!, name + ".png"));
    }

    private static MainWindow Open(SingleUseHarness harness)
    {
        var window = harness.Services.GetRequiredService<MainWindow>();
        window.Width = Width;
        window.Height = Height;

        return (MainWindow)UiDriver.ShowWindow(window);
    }

    private static void SelectTab(Window window, int index)
    {
        window.FindControl<TabControl>("TabsControl")!.SelectedIndex = index;
        Dispatcher.UIThread.RunJobs();
    }

    // No setup page: rendered here it can only offer single use, because a test machine has no
    // password manager to find. media/setupPage.png is a capture from a real machine and stays one.

    [Fact]
    public Task EveryTab() => UiSession.RunAsync(async () =>
    {
        if (Directory is null) return;

        await using var harness = await SingleUseHarness.StartAsync();
        await harness.StartSingleUseThroughTheUiAsync();
        var bridgeIds = await SeedAsync(harness);

        try
        {
            await CaptureEveryTabAsync(harness);
        }
        finally
        {
            foreach (var id in bridgeIds) ManifestLocalStore.Delete(id);
        }
    });

    private static async Task CaptureEveryTabAsync(SingleUseHarness harness)
    {
        var store = harness.Services.GetRequiredService<ConfigStoreCache>();
        var window = Open(harness);
        var shell = harness.Services.GetRequiredService<MainWindowViewModel>();
        shell.EnterNormalMode();
        await UiDriver.UntilAsync(() => !shell.IsGated, "the shell to leave the setup page");

        // Every source asks its bridge for a catalogue first, while the store still names the port
        // the harness really listens on — the funnel reaches its sources through that port.
        var funnels = harness.Services.GetRequiredService<McpFunnelViewModel>();
        funnels.RefreshCommand.Execute(null);
        await funnels.RefreshAllSourcesCommand.ExecuteAsync(null);
        var unreachable = funnels.Sources.Where(s => s.Source.Kind == McpSourceKind.ApiBridge && s.Catalog is null).ToList();
        if (unreachable.Count > 0)
            throw new InvalidOperationException(
                $"sources not reached: {string.Join("; ", unreachable.Select(s => $"{s.Name}: {s.Status}"))} — {funnels.StatusMessage}");

        // Then the port everyone actually sees, so no screenshot shows the harness's random one.
        // Only the text changes: nothing is listening there, and nothing below calls it.
        await store.MutateAsync(s => s.Settings.ListenPort = 5559);
        harness.Services.GetRequiredService<RoutesViewModel>().RefreshCommand.Execute(null);
        harness.Services.GetRequiredService<ApiBridgeViewModel>().RefreshCommand.Execute(null);
        funnels.RefreshCommand.Execute(null);

        // Every write above goes through the save pipeline, and its banner is not what a reader of
        // the docs should see.
        var vault = harness.Services.GetRequiredService<VaultStatusViewModel>();
        await UiDriver.UntilAsync(() => !vault.HasPendingChanges, "the last save to finish");

        SelectTab(window, 0);
        Snap(window, "credentialsScreen");

        // A route with its credential editor open, which is the part of the tab worth seeing.
        SelectTab(window, 1);
        var routesView = window.GetVisualDescendants().OfType<RoutesView>().First();
        await UiDriver.SelectAndOpenRowAsync<RouteItemViewModel>(
            routesView, AutomationIds.RoutesGrid, r => r.PathPrefix == "/places");
        Snap(window, "routesScreen");

        // A bridge opened in the editor, so the manifest and the calls it makes are on screen.
        SelectTab(window, 2);
        var bridges = harness.Services.GetRequiredService<ApiBridgeViewModel>();
        bridges.RefreshCommand.Execute(null);
        bridges.EditManifestCommand.Execute(bridges.Bridges.First(b => b.Bridge.Slug == "weather"));
        Snap(window, "apiMcpBridgeScreen");

        // One funnel selected, one source opened to show the tools it was cut down to.
        SelectTab(window, 3);
        funnels.SelectedFunnel = funnels.Funnels.First(f => f.Slug == "assistant");
        await UiDriver.UntilAsync(
            () => funnels.FunnelSources.Any(s => s.Name == "Google Drive" && s.Tools.Items.Count > 0),
            "the Drive source's tools to be listed");
        var drive = funnels.FunnelSources.First(s => s.Name == "Google Drive");
        drive.IsExpanded = true;
        drive.Tools.IsExpanded = true;
        Snap(window, "mcpFunnelScreen");

        // Recent activity as a day with these routes would log it, in the app's own line formats.
        // The real log at this point holds only the harness's startup, which no reader would
        // recognise. Set straight on the view model: the 2s refresh does not run before the snap.
        SelectTab(window, 4);
        var settingsView = window.GetVisualDescendants().OfType<SettingsView>().First();
        var day = DateTime.Today.AddHours(9).AddMinutes(41);
        string At(int seconds) => day.AddSeconds(seconds).ToString("yyyy-MM-dd HH:mm:ss");
        harness.Services.GetRequiredService<SettingsViewModel>().RecentActivity = string.Join(Environment.NewLine,
        [
            $"{At(0)} ROUTES reloaded — 9 active route(s)",
            $"{At(4)} REFRESH 'GitHub' OK — expires in 8h",
            $"{At(12)} PROXY GET /github/repos/octocat/hello-world -> https://api.github.com [tokens: header Authorization]",
            $"{At(13)} PROXY GET /github/repos/octocat/hello-world/issues -> https://api.github.com [tokens: header Authorization]",
            $"{At(31)} PROXY POST /places/v1/places:searchText -> https://places.googleapis.com [tokens: header X-Goog-Api-Key]",
            $"{At(33)} MCP bridge 'Google Weather' call get_weather -> HTTP 200",
            $"{At(34)} PROXY GET /weather/v1/currentConditions:lookup -> https://weather.googleapis.com [tokens: header X-Goog-Api-Key]",
            $"{At(58)} PROXY GET /brightsky/weather -> https://api.brightsky.dev [no credential configured - forwarded unauthenticated]",
            $"{At(75)} REFRESH 'Google Drive' OK — expires in 1h",
            $"{At(76)} PROXY GET /drive/files -> https://www.googleapis.com/drive/v3 [tokens: header Authorization]",
            $"{At(102)} PROXY GET /tailscale/tailnet/-/devices -> https://api.tailscale.com/api/v2 [tokens: header Authorization]",
            $"{At(140)} DENIED GET /billing/invoices — missing or incorrect proxy key for route '/billing'",
        ]);

        // Exactly as tall as the cards, so the image ends under the last one instead of in space.
        // Measured from the last card rather than the scroll extent: the page's panel stretches to
        // fill the viewport, so the extent is always the window.
        window.UpdateLayout();
        var scroller = settingsView.GetVisualDescendants().OfType<ScrollViewer>().First();
        var lastCard = settingsView.GetVisualDescendants().OfType<ProjectLinksView>().First();
        var lastCardBottom = lastCard.TranslatePoint(new Point(0, lastCard.Bounds.Height), scroller)!.Value.Y;
        window.Height -= scroller.Viewport.Height - lastCardBottom - 16;
        Snap(window, "settingsScreen");
    }

    [Fact]
    public Task TheOperationPicker() => UiSession.RunAsync(() =>
    {
        if (Directory is null) return Task.CompletedTask;

        OpenApiOperationSummary[] operations =
        [
            new("/repos/{owner}/{repo}", "GET", "repos_get", "Get a repository", ["repos"], 300),
            new("/repos/{owner}/{repo}/contents/{path}", "GET", "repos_get_content", "Get repository content", ["repos"], 340),
            new("/repos/{owner}/{repo}/commits", "GET", "repos_list_commits", "List commits", ["repos"], 330),
            new("/repos/{owner}/{repo}/issues", "GET", "issues_list_for_repo", "List repository issues", ["issues"], 320),
            new("/repos/{owner}/{repo}/issues", "POST", "issues_create", "Create an issue", ["issues"], 380),
            new("/repos/{owner}/{repo}/issues/{issue_number}/comments", "POST", "issues_create_comment", "Create an issue comment", ["issues"], 360),
            new("/repos/{owner}/{repo}/pulls", "GET", "pulls_list", "List pull requests", ["pulls"], 340),
            new("/repos/{owner}/{repo}/pulls/{pull_number}", "GET", "pulls_get", "Get a pull request", ["pulls"], 310),
            new("/repos/{owner}/{repo}/pulls/{pull_number}/files", "GET", "pulls_list_files", "List pull request files", ["pulls"], 320),
            new("/search/code", "GET", "search_code", "Search code", ["search"], 290),
            new("/search/issues", "GET", "search_issues_and_pull_requests", "Search issues and pull requests", ["search"], 300),
            new("/user", "GET", "users_get_authenticated", "Get the authenticated user", ["users"], 220),
        ];

        var window = new OpenApiOperationPickerWindow(
            new OpenApiOperationPickerViewModel("{}", "github-api.json", operations));

        UiDriver.ShowWindow(window);
        Snap(window, "openAPIPicker");

        return Task.CompletedTask;
    });

    /// <summary>
    /// Writes the configuration straight into the store, as the approval suite does: the forms are
    /// what the other tests here are for, and this is only about what the result looks like.
    /// </summary>
    /// <returns>The bridges whose manifests were written to disk, for the caller to delete.</returns>
    private static async Task<List<Guid>> SeedAsync(SingleUseHarness harness)
    {
        var now = DateTimeOffset.UtcNow;
        TokenSet Token(TimeSpan left, string? refresh = "refresh-placeholder") => // gitleaks:allow
            new("access-placeholder", refresh, now + left, "Bearer", now - TimeSpan.FromMinutes(5)); // gitleaks:allow

        var github = new CredentialRecord
        {
            Name = "GitHub",
            ClientId = "Iv1.docs0example0id",
            ClientSecret = "placeholder-secret", // gitleaks:allow
            AuthorizationEndpoint = "https://github.com/login/oauth/authorize",
            TokenEndpoint = "https://github.com/login/oauth/access_token",
            Scopes = ["repo", "read:org"],
            Token = Token(TimeSpan.FromHours(7)),
        };
        var google = new CredentialRecord
        {
            Name = "Google Drive",
            IsGoogleProvider = true,
            ClientId = "1234567890-docs.apps.googleusercontent.com",
            ClientSecret = "placeholder-secret", // gitleaks:allow
            Scopes = ["https://www.googleapis.com/auth/drive.readonly"],
            Token = Token(TimeSpan.FromMinutes(48)),
        };
        var maps = new CredentialRecord
        {
            Name = "Google Maps Platform",
            Kind = CredentialKind.ApiKey,
            ApiKey = "placeholder-maps-key", // gitleaks:allow
            DefaultParameterName = "X-Goog-Api-Key",
            DefaultValuePrefix = "",
        };
        var tailscale = new CredentialRecord
        {
            Name = "Tailscale",
            Kind = CredentialKind.ApiKey,
            ApiKey = "placeholder-tailscale-key", // gitleaks:allow
        };
        var reports = new CredentialRecord
        {
            Name = "Reporting service",
            Kind = CredentialKind.ClientCredentials,
            ClientId = "reports-agent",
            ClientSecret = "placeholder-secret", // gitleaks:allow
            TokenEndpoint = "https://auth.example.com/oauth2/token",
            Scopes = ["reports.read"],
            Token = Token(TimeSpan.FromMinutes(55), refresh: null),
        };
        var billing = new CredentialRecord
        {
            Name = "Billing API",
            Kind = CredentialKind.TokenExchange,
            ExchangeEndpoint = "https://billing.example.com/api/login",
            ExchangeApiKey = "placeholder-billing-key", // gitleaks:allow
            Token = Token(TimeSpan.FromMinutes(20), refresh: null),
        };

        var upstreams = new Dictionary<string, UpstreamRecord>();
        var routes = new List<RouteMapping>();

        void Route(string prefix, string name, string baseUrl, TimeSpan? keyLifetime, params RouteCredential[] credentials)
        {
            var upstream = new UpstreamRecord { Name = name, BaseUrl = baseUrl };
            upstreams[prefix] = upstream;
            routes.Add(new RouteMapping
            {
                PathPrefix = prefix,
                UpstreamId = upstream.Id,
                Key = ProxyKey.Generate(keyLifetime),
                Credentials = [.. credentials],
            });
        }

        static RouteCredential Bearer(CredentialRecord c) => new() { CredentialId = c.Id };
        static RouteCredential Header(CredentialRecord c, string name) =>
            new() { CredentialId = c.Id, ParameterName = name, ValuePrefix = "" };

        Route("/github", "github", "https://api.github.com", null, Bearer(github));
        Route("/drive", "google-drive", "https://www.googleapis.com/drive/v3", null, Bearer(google));
        Route("/places", "google-places", "https://places.googleapis.com", TimeSpan.FromDays(90), Header(maps, "X-Goog-Api-Key"));
        Route("/weather", "google-weather", "https://weather.googleapis.com", TimeSpan.FromDays(90), Header(maps, "X-Goog-Api-Key"));
        Route("/routes", "google-routes", "https://routes.googleapis.com", TimeSpan.FromDays(90), Header(maps, "X-Goog-Api-Key"));
        Route("/tailscale", "tailscale", "https://api.tailscale.com/api/v2", TimeSpan.FromDays(30), Bearer(tailscale));
        Route("/brightsky", "brightsky", "https://api.brightsky.dev", null);
        Route("/reports", "reports", "https://reports.example.com/api", TimeSpan.FromDays(7), Bearer(reports));
        Route("/billing", "billing", "https://billing.example.com/api", TimeSpan.FromDays(7), Bearer(billing), Header(maps, "X-Client-Key"));

        // The shipped templates, each behind the route SETUP.md says it needs.
        var templates = FindTemplates();
        var bridges = new List<McpApiBridgeRecord>();
        var sources = new List<McpSourceRecord>();

        void Bridge(string name, string slug, string file, string prefix)
        {
            var error = McpApiBridgeValidation.TryReadManifest(
                File.ReadAllText(Path.Combine(templates, file)), out var manifest);
            if (error is not null) throw new InvalidOperationException($"{file}: {error}");

            var bridge = new McpApiBridgeRecord
            {
                Name = name,
                Slug = slug,
                RouteId = routes.Single(r => r.PathPrefix == prefix).Id,
                Manifest = manifest!,
                ManifestOrigin = file,
                Key = ProxyKey.Generate(),
            };

            // Where the app keeps a manifest. The store strips it on the way to the vault and reads
            // it back from here, so a bridge whose manifest is only in the record comes back empty.
            if (ManifestLocalStore.Save(bridge.Id, manifest!) is { } saveError)
                throw new InvalidOperationException($"{file}: {saveError}");

            bridges.Add(bridge);
            sources.Add(new McpSourceRecord
            {
                Name = name,
                Alias = slug,
                Kind = McpSourceKind.ApiBridge,
                BridgeId = bridge.Id,
            });
        }

        Bridge("Google Drive", "drive", "google-drive-readonly.json", "/drive");
        Bridge("Google Weather", "weather", "google-weather.json", "/weather");
        Bridge("Google Places", "places", "google-places.json", "/places");
        Bridge("Google Routes", "routes", "google-routes.json", "/routes");
        Bridge("DWD weather", "dwd", "brightsky-dwd.json", "/brightsky");
        Bridge("Tailscale", "tailscale", "tailscale-readonly.json", "/tailscale");

        McpFunnelSource Pool(string name, int includeFirst = 0)
        {
            var source = sources.Single(s => s.Name == name);
            if (includeFirst == 0) return new McpFunnelSource { SourceId = source.Id };

            var bridge = bridges.Single(b => b.Id == source.BridgeId);
            return new McpFunnelSource
            {
                SourceId = source.Id,
                ToolMode = McpSelectionMode.Include,
                Tools = [.. bridge.Manifest.Tools.Take(includeFirst).Select(t => t.Name)],
            };
        }

        McpFunnelRecord[] funnels =
        [
            new()
            {
                Name = "Assistant", Slug = "assistant", Key = ProxyKey.Generate(),
                Sources = [Pool("Google Drive", includeFirst: 4), Pool("Google Weather"), Pool("Google Places")],
            },
            new()
            {
                Name = "Travel planner", Slug = "travel", Key = ProxyKey.Generate(TimeSpan.FromDays(90)),
                Sources = [Pool("Google Routes"), Pool("Google Places"), Pool("DWD weather")],
            },
            new()
            {
                Name = "Ops", Slug = "ops", Key = ProxyKey.Generate(TimeSpan.FromDays(30)),
                Sources = [Pool("Tailscale")],
            },
        ];

        await harness.Services.GetRequiredService<ConfigStoreCache>().MutateAsync(s =>
        {
            s.Credentials.AddRange([github, google, maps, tailscale, reports, billing]);
            s.Upstreams.AddRange(upstreams.Values);
            s.Routes.AddRange(routes);
            s.McpApiBridges.AddRange(bridges);
            s.McpSources.AddRange(sources);
            s.McpFunnels.AddRange(funnels);
            s.Settings.McpFunnelEnabled = true;
            s.Settings.McpApiBridgeEnabled = true;
        });

        return [.. bridges.Select(b => b.Id)];
    }

    private static string FindTemplates()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "templates", "api-mcp");
            if (System.IO.Directory.Exists(candidate)) return candidate;
        }

        throw new DirectoryNotFoundException("templates/api-mcp, above " + AppContext.BaseDirectory);
    }
}
