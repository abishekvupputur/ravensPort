using Microsoft.Extensions.DependencyInjection;
using RavensPort.Core.Storage;
using RavensPort.UI.ViewModels;
using RavensPort.Views;

namespace RavensPort.UI.Tests;

/// <summary>
/// The token buttons on a credential row: get one, refresh it, throw it away.
///
/// Driven with the client-credentials grant, which is the one a headless test can complete — the
/// browser flow wants a person at a consent screen and the device flow wants one at a second
/// device. The harness issues the tokens itself, so this needs no secrets and no internet, and each
/// one is distinct so a refresh can be told from a value the app had already cached.
///
/// These are the rows' most consequential buttons and nothing else covers them: the approval suite
/// acquires its token by calling the service directly, so the path from a click to a stored token
/// has never run.
/// </summary>
public class CredentialTokenFlowsUiTests
{
    /// <summary>
    /// Getting a token, refreshing it, and disconnecting — the whole life of one in three presses.
    /// </summary>
    [Fact]
    public Task ATokenIsAcquiredRefreshedAndDiscardedFromTheRow() => UiSession.RunAsync(async () =>
    {
        await using var harness = await SingleUseHarness.StartAsync();
        await harness.StartSingleUseThroughTheUiAsync();

        var vm = harness.Services.GetRequiredService<CredentialsViewModel>();
        var view = UiDriver.Show<CredentialsView>(vm);
        var store = harness.Services.GetRequiredService<ConfigStoreCache>();

        await UiDriver.SelectAsync(view, AutomationIds.CredentialKind, "OAuth2 client credentials (app login)");
        await UiDriver.TypeAsync(view, AutomationIds.CredentialName, "app login");
        await UiDriver.TypeAsync(view, AutomationIds.CredentialClientId, "ravensport-ui");
        await UiDriver.TypeAsync(view, AutomationIds.CredentialClientSecret, "ui-secret"); // gitleaks:allow
        await UiDriver.TypeAsync(view, AutomationIds.CredentialTokenEndpoint, harness.TokenEndpoint);
        await UiDriver.ClickAsync(view, AutomationIds.SaveCredential);

        await UiDriver.UntilAsync(
            () => store.Current.Credentials.Count == 1,
            () => $"the credential to be saved — the tab says: {vm.StatusMessage}");

        var id = store.Current.Credentials.Single().Id;

        // Saved with no token: an app login has everything it needs and simply has not been asked.
        Assert.Null(store.Current.Credentials.Single().Token);

        // Ask.
        await UiDriver.ClickForItemAsync<CredentialItemViewModel>(
            view, AutomationIds.ConnectCredentialRow, c => c.Record.Id == id);

        await UiDriver.UntilAsync(
            () => store.Current.Credentials.Single().Token is not null,
            () => $"a token to be issued — the tab says: {vm.StatusMessage}");

        var first = store.Current.Credentials.Single().Token!.AccessToken;
        Assert.StartsWith("issued-token-", first);

        // Refresh, and get a different one — the endpoint never repeats itself, so an unchanged
        // value here would mean the button did nothing and the old token was simply still there.
        await UiDriver.ClickForItemAsync<CredentialItemViewModel>(
            view, AutomationIds.RefreshCredentialRow, c => c.Record.Id == id);

        await UiDriver.UntilAsync(
            () => store.Current.Credentials.Single().Token?.AccessToken != first,
            () => $"the token to be replaced — the tab says: {vm.StatusMessage}");

        Assert.True(harness.TokensIssued >= 2, "the refresh should have asked the endpoint again");

        // And throw it away.
        await UiDriver.ClickForItemAsync<CredentialItemViewModel>(
            view, AutomationIds.DisconnectCredentialRow, c => c.Record.Id == id);

        await UiDriver.UntilAsync(
            () => store.Current.Credentials.Single().Token is null,
            () => $"the token to be discarded — the tab says: {vm.StatusMessage}");

        // The credential itself stays: disconnecting is not deleting.
        Assert.Single(store.Current.Credentials);
        Assert.Equal("app login", store.Current.Credentials.Single().Name);
    });

    /// <summary>
    /// A token that arrives really is the one a route then sends.
    ///
    /// The two halves are configured in different tabs and joined only by the vault, so a token
    /// acquired on the Credentials tab that never reaches the transform would look correct in both
    /// places and fail only at the upstream.
    /// </summary>
    [Fact]
    public Task AnAcquiredTokenIsWhatTheRouteSends() => UiSession.RunAsync(async () =>
    {
        await using var harness = await SingleUseHarness.StartAsync();
        await harness.StartSingleUseThroughTheUiAsync();

        var vm = harness.Services.GetRequiredService<CredentialsViewModel>();
        var view = UiDriver.Show<CredentialsView>(vm);
        var store = harness.Services.GetRequiredService<ConfigStoreCache>();

        await UiDriver.SelectAsync(view, AutomationIds.CredentialKind, "OAuth2 client credentials (app login)");
        await UiDriver.TypeAsync(view, AutomationIds.CredentialName, "app login");
        await UiDriver.TypeAsync(view, AutomationIds.CredentialClientId, "ravensport-ui");
        await UiDriver.TypeAsync(view, AutomationIds.CredentialClientSecret, "ui-secret"); // gitleaks:allow
        await UiDriver.TypeAsync(view, AutomationIds.CredentialTokenEndpoint, harness.TokenEndpoint);
        await UiDriver.ClickAsync(view, AutomationIds.SaveCredential);

        await UiDriver.UntilAsync(() => store.Current.Credentials.Count == 1, "the credential");

        var id = store.Current.Credentials.Single().Id;

        await UiDriver.ClickForItemAsync<CredentialItemViewModel>(
            view, AutomationIds.ConnectCredentialRow, c => c.Record.Id == id);

        await UiDriver.UntilAsync(
            () => store.Current.Credentials.Single().Token is not null,
            () => $"a token to be issued — the tab says: {vm.StatusMessage}");

        var token = store.Current.Credentials.Single().Token!.AccessToken;

        // Now a route that attaches it, built on the Routes tab.
        var routes = harness.Services.GetRequiredService<RoutesViewModel>();
        var routesView = UiDriver.Show<RoutesView>(routes);

        await UiDriver.TypeAsync(routesView, AutomationIds.UpstreamName, "echo");
        await UiDriver.TypeAsync(routesView, AutomationIds.UpstreamBaseUrl, harness.UpstreamUrl);
        await UiDriver.ClickAsync(routesView, AutomationIds.AddUpstream);
        await UiDriver.UntilAsync(() => store.Current.Upstreams.Count == 1, "the upstream");

        await UiDriver.TypeAsync(routesView, AutomationIds.RoutePrefix, "/app/token");
        await UiDriver.SelectAsync(routesView, AutomationIds.RouteUpstream, "echo");
        await UiDriver.SelectAsync(routesView, AutomationIds.RouteCredential, "app login");
        await UiDriver.ClickAsync(routesView, AutomationIds.AddRoute);

        await UiDriver.UntilAsync(
            () => store.Current.Routes.Any(r => r.PathPrefix == "/app/token"), "the route");

        var route = store.Current.Routes.Single(r => r.PathPrefix == "/app/token");

        using var client = harness.CreateClientFor(route.Key.Value);
        var response = await client.GetAsync("/app/token/anything");

        response.EnsureSuccessStatusCode();

        var echo = SingleUseHarness.ReadEcho(await response.Content.ReadAsStringAsync());

        Assert.True(echo.Headers.TryGetValue("Authorization", out var sent),
            $"the upstream saw no Authorization; it got [{string.Join(", ", echo.Headers.Keys)}]");
        Assert.Equal($"Bearer {token}", sent);
    });

    /// <summary>
    /// The device flow, run end to end from the row.
    ///
    /// The provider hands back a short code and a URL for the person to open somewhere else, and the
    /// app polls until they have. What the tab has to do with that code is show it and offer to copy
    /// it — a flow that acquired a token without ever displaying one would have left the user with
    /// nothing to type.
    /// </summary>
    [Fact]
    public Task TheDeviceFlowShowsItsCodeAndThenGetsAToken() => UiSession.RunAsync(async () =>
    {
        await using var harness = await SingleUseHarness.StartAsync();
        await harness.StartSingleUseThroughTheUiAsync();

        var vm = harness.Services.GetRequiredService<CredentialsViewModel>();
        var view = UiDriver.Show<CredentialsView>(vm);
        var store = harness.Services.GetRequiredService<ConfigStoreCache>();

        await UiDriver.SelectAsync(view, AutomationIds.CredentialKind, "OAuth2 device code");
        await UiDriver.TypeAsync(view, AutomationIds.CredentialName, "on the telly");
        await UiDriver.TypeAsync(view, AutomationIds.CredentialClientId, "device-client");
        await UiDriver.TypeAsync(
            view, AutomationIds.CredentialDeviceEndpoint, harness.DeviceAuthorizationEndpoint);
        await UiDriver.TypeAsync(view, AutomationIds.CredentialTokenEndpoint, harness.TokenEndpoint);
        await UiDriver.ClickAsync(view, AutomationIds.SaveCredential);

        await UiDriver.UntilAsync(
            () => store.Current.Credentials.Count == 1,
            () => $"the credential to be saved — the tab says: {vm.StatusMessage}");

        var id = store.Current.Credentials.Single().Id;

        await UiDriver.ClickForItemAsync<CredentialItemViewModel>(
            view, AutomationIds.ConnectCredentialRow, c => c.Record.Id == id);

        // The code has to reach the screen, because typing it elsewhere is what makes the token
        // arrive at all. It goes to the status line, which is the only place a user could read it.
        var showedTheCode = false;

        await UiDriver.UntilAsync(
            () =>
            {
                showedTheCode |= vm.StatusMessage.Contains("WDJB-MJHT", StringComparison.Ordinal);
                return showedTheCode && store.Current.Credentials.Single().Token is not null;
            },
            () => $"the device code to be shown and then approved — the tab says: {vm.StatusMessage}");

        Assert.True(showedTheCode, "the flow never put the user code on screen");

        await UiDriver.UntilAsync(
            () => store.Current.Credentials.Single().Token is not null,
            () => $"the poll to come back with a token — the tab says: {vm.StatusMessage}");

        Assert.StartsWith("issued-token-", store.Current.Credentials.Single().Token!.AccessToken);
    });
}
