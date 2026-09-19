using Microsoft.Extensions.DependencyInjection;
using RavensPort.Core.Proxy;
using RavensPort.Core.Storage;
using RavensPort.Core.Vault;
using RavensPort.UI.ViewModels;
using RavensPort.Views;
using System.Net;

namespace RavensPort.UI.Tests;

/// <summary>
/// The approval suite's pre-mTLS ground, walked through the views in a single-use session.
///
/// Each test is the whole journey rather than a stage of a shared one, which is the difference
/// worth naming against RavensPort.SystemTests. That suite is a sequence over one real vault
/// because every write costs an API call against a hundred-an-hour ceiling, so it seeds once and
/// orders its stages. Nothing here costs anything — the vault is a dictionary in this process — so
/// each test builds what it needs and can be read on its own.
/// </summary>
public class SingleUseUiTests
{
    /// <summary>A value no upstream would produce by accident, so finding it proves injection.</summary>
    private const string ApiKeyValue = "ui-suite-injected-value";

    [Fact]
    public Task PressingStartInSingleUseOpensTheGateOnAnEmptyStore() => UiSession.RunAsync(async () =>
    {
        await using var harness = await SingleUseHarness.StartAsync();

        var gate = harness.Services.GetRequiredService<VaultGateService>();
        Assert.False(gate.IsSingleUse, "the gate should be shut before anything is pressed");

        await harness.StartSingleUseThroughTheUiAsync();

        Assert.True(gate.IsSingleUse);

        // Stage 1's assertion, against the store the button just selected: a session that begins
        // with anything in it would make every later "it is there now" meaningless.
        var store = harness.Services.GetRequiredService<ConfigStoreCache>().Current;
        Assert.Empty(store.Credentials);
        Assert.Empty(store.Routes);
        Assert.Empty(store.McpSources);
    });

    /// <summary>
    /// Stages 2 and 3 in one pass: everything is created by pressing the buttons a person presses,
    /// then proved from the far side of the proxy — which is the only place that can say a header
    /// was really injected rather than merely recorded as an intention.
    /// </summary>
    [Fact]
    public Task ACredentialAndRouteBuiltInTheUiInjectOnTheWayThrough() => UiSession.RunAsync(async () =>
    {
        await using var harness = await SingleUseHarness.StartAsync();
        await harness.StartSingleUseThroughTheUiAsync();

        var store = harness.Services.GetRequiredService<ConfigStoreCache>();

        var credentials = harness.Services.GetRequiredService<CredentialsViewModel>();
        var credentialsView = UiDriver.Show<CredentialsView>(credentials);

        await UiDriver.SelectAsync(credentialsView, AutomationIds.CredentialKind, "API key");
        await UiDriver.TypeAsync(credentialsView, AutomationIds.CredentialName, "Echo key");
        await UiDriver.TypeAsync(credentialsView, AutomationIds.CredentialApiKey, ApiKeyValue);
        await UiDriver.SelectAsync(credentialsView, AutomationIds.CredentialPlacement, "Header");
        await UiDriver.TypeAsync(credentialsView, AutomationIds.CredentialParameter, "X-Api-Key");
        await UiDriver.ClickAsync(credentialsView, AutomationIds.SaveCredential);

        await UiDriver.UntilAsync(() => store.Current.Credentials.Count == 1, "the credential to be saved");

        await SeedEchoRouteAsync(harness, withCredential: "Echo key");

        var route = store.Current.Routes.Single();
        Assert.Equal("/app/echo", route.PathPrefix);

        using var client = harness.CreateClientFor(route.Key.Value);
        var response = await client.GetAsync("/app/echo/whatever");

        response.EnsureSuccessStatusCode();

        var echo = SingleUseHarness.ReadEcho(await response.Content.ReadAsStringAsync());

        Assert.True(echo.Headers.ContainsKey("X-Api-Key"),
            "the upstream should have been sent the credential under the header the UI named. It saw: "
            + string.Join(", ", echo.Headers.Keys));
        Assert.Equal(ApiKeyValue, echo.Headers["X-Api-Key"]);

        // The guard's own header must not survive the hop: it authenticates the caller to
        // RavensPort, and forwarding it would hand the upstream a key to this proxy.
        Assert.False(echo.Headers.ContainsKey(LocalAccessGuard.ApiKeyHeaderName));
    });

    /// <summary>
    /// Stage 11's question, which needs no mTLS to ask: a caller without the route's key is
    /// refused. The route works — the test above proves it — so a refusal here is the guard doing
    /// its job rather than a route that was never reachable in the first place.
    /// </summary>
    [Fact]
    public Task TheListenerRefusesACallerWithNoKey() => UiSession.RunAsync(async () =>
    {
        await using var harness = await SingleUseHarness.StartAsync();
        await harness.StartSingleUseThroughTheUiAsync();

        await SeedEchoRouteAsync(harness, withCredential: null);

        using var stranger = harness.CreateClientWithNoKey();
        var refused = await stranger.GetAsync("/app/echo/whatever");

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    });

    /// <summary>
    /// Disconnecting a single-use session takes the configuration with it, which is the promise the
    /// Settings tab makes in so many words. Worth a test precisely because it is destructive and
    /// irreversible: nothing else would notice if it quietly stopped emptying the store.
    /// </summary>
    [Fact]
    public Task DisconnectingASingleUseSessionPurgesEverything() => UiSession.RunAsync(async () =>
    {
        await using var harness = await SingleUseHarness.StartAsync();
        await harness.StartSingleUseThroughTheUiAsync();

        await SeedEchoRouteAsync(harness, withCredential: null);

        var store = harness.Services.GetRequiredService<ConfigStoreCache>();
        Assert.NotEmpty(store.Current.Routes);

        var settings = harness.Services.GetRequiredService<SettingsViewModel>();
        var settingsView = UiDriver.Show<SettingsView>(settings);

        await UiDriver.UntilAsync(() => settings.IsSingleUse, "Settings to notice this is a single-use session");

        // Twice, because the UI asks. The first press only opens the warning that says nothing
        // comes back; a test that could purge a session in one press would be testing a product
        // that does not exist.
        await UiDriver.ClickAsync(settingsView, AutomationIds.Disconnect);
        await UiDriver.UntilAsync(() => settings.IsConfirmingDisconnect, "the confirmation to appear");
        await UiDriver.ClickAsync(settingsView, AutomationIds.ConfirmDisconnect);

        await UiDriver.UntilAsync(
            () => store.Current.Routes.Count == 0 && store.Current.Upstreams.Count == 0,
            "the single-use configuration to be purged");
    });

    /// <summary>
    /// Adds the echo upstream and a route to it, through the Routes tab. Shared because three tests
    /// need a working route and none of them is about how one is added — the second test is.
    /// </summary>
    private static async Task SeedEchoRouteAsync(SingleUseHarness harness, string? withCredential)
    {
        var routes = harness.Services.GetRequiredService<RoutesViewModel>();
        var routesView = UiDriver.Show<RoutesView>(routes);

        var store = harness.Services.GetRequiredService<ConfigStoreCache>();

        await UiDriver.TypeAsync(routesView, AutomationIds.UpstreamName, "Echo");
        await UiDriver.TypeAsync(routesView, AutomationIds.UpstreamBaseUrl, harness.UpstreamUrl);
        await UiDriver.ClickAsync(routesView, AutomationIds.AddUpstream);
        await UiDriver.UntilAsync(() => store.Current.Upstreams.Count == 1, "the upstream to be added");

        await UiDriver.TypeAsync(routesView, AutomationIds.RoutePrefix, "/app/echo");
        await UiDriver.SelectAsync(routesView, AutomationIds.RouteUpstream, "Echo");

        if (withCredential is not null)
        {
            await UiDriver.SelectAsync(routesView, AutomationIds.RouteCredential, withCredential);
        }

        await UiDriver.ClickAsync(routesView, AutomationIds.AddRoute);
        await UiDriver.UntilAsync(() => store.Current.Routes.Count == 1, "the route to be added");
    }
}
