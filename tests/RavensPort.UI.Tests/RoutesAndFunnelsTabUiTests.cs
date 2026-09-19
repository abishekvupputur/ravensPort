using Microsoft.Extensions.DependencyInjection;
using RavensPort.Core.Models;
using RavensPort.Core.Storage;
using RavensPort.Core.Tests.Mcp;
using RavensPort.UI.ViewModels;
using RavensPort.Views;

namespace RavensPort.UI.Tests;

/// <summary>
/// Taking things away, and the refusals that guard the rest.
///
/// Everything else in this suite builds a configuration up. These are the paths a user reaches when
/// they have made a mistake or changed their mind, which are exactly the ones that fail quietly:
/// nothing downstream notices a delete that half-happened, or a route that was accepted when it
/// should have been refused.
/// </summary>
public class RoutesAndFunnelsTabUiTests
{
    /// <summary>
    /// A route the tab should not have accepted. Two prefixes cannot be the same — every request to
    /// one would fail with an ambiguous match — and a route has to point somewhere.
    /// </summary>
    [Fact]
    public Task TheRoutesTabRefusesADuplicatePrefixAndAnEmptyOne() => UiSession.RunAsync(async () =>
    {
        await using var harness = await SingleUseHarness.StartAsync();
        await harness.StartSingleUseThroughTheUiAsync();

        var routes = harness.Services.GetRequiredService<RoutesViewModel>();
        var view = UiDriver.Show<RoutesView>(routes);
        var store = harness.Services.GetRequiredService<ConfigStoreCache>();

        await UiDriver.TypeAsync(view, AutomationIds.UpstreamName, "echo");
        await UiDriver.TypeAsync(view, AutomationIds.UpstreamBaseUrl, harness.UpstreamUrl);
        await UiDriver.ClickAsync(view, AutomationIds.AddUpstream);
        await UiDriver.UntilAsync(() => store.Current.Upstreams.Count == 1, "the upstream");

        await UiDriver.TypeAsync(view, AutomationIds.RoutePrefix, "/app/only");
        await UiDriver.SelectAsync(view, AutomationIds.RouteUpstream, "echo");
        await UiDriver.ClickAsync(view, AutomationIds.AddRoute);
        await UiDriver.UntilAsync(() => store.Current.Routes.Count == 1, "the first route");

        // The same prefix again.
        await UiDriver.TypeAsync(view, AutomationIds.RoutePrefix, "/app/only");
        await UiDriver.SelectAsync(view, AutomationIds.RouteUpstream, "echo");
        await UiDriver.ClickAsync(view, AutomationIds.AddRoute);
        await UiDriver.PumpAsync();

        Assert.Single(store.Current.Routes);
        Assert.Contains("already exists", routes.StatusMessage, StringComparison.OrdinalIgnoreCase);

        // No prefix at all.
        await UiDriver.TypeAsync(view, AutomationIds.RoutePrefix, "");
        await UiDriver.ClickAsync(view, AutomationIds.AddRoute);
        await UiDriver.PumpAsync();

        Assert.Single(store.Current.Routes);
    });

    /// <summary>
    /// Deleting an upstream a route still points at, which the tab allows and reports.
    ///
    /// Worth pinning because the alternative would be defensible too, and the choice is invisible
    /// from the grid: the delete goes through, and what protects the user is being told how many
    /// routes it just broke. A silent success here would leave routes failing at their next request
    /// rather than at the click that caused it.
    /// </summary>
    [Fact]
    public Task DeletingAnUpstreamInUseSaysHowManyRoutesItBreaks() => UiSession.RunAsync(async () =>
    {
        await using var harness = await SingleUseHarness.StartAsync();
        await harness.StartSingleUseThroughTheUiAsync();

        var routes = harness.Services.GetRequiredService<RoutesViewModel>();
        var view = UiDriver.Show<RoutesView>(routes);
        var store = harness.Services.GetRequiredService<ConfigStoreCache>();

        await UiDriver.TypeAsync(view, AutomationIds.UpstreamName, "echo");
        await UiDriver.TypeAsync(view, AutomationIds.UpstreamBaseUrl, harness.UpstreamUrl);
        await UiDriver.ClickAsync(view, AutomationIds.AddUpstream);
        await UiDriver.UntilAsync(() => store.Current.Upstreams.Count == 1, "the upstream");

        await UiDriver.TypeAsync(view, AutomationIds.RoutePrefix, "/app/doomed");
        await UiDriver.SelectAsync(view, AutomationIds.RouteUpstream, "echo");
        await UiDriver.ClickAsync(view, AutomationIds.AddRoute);
        await UiDriver.UntilAsync(() => store.Current.Routes.Count == 1, "the route");

        // The upstream goes even though a route still points at it — and the tab says so.
        await UiDriver.ClickForItemAsync<UpstreamRecord>(
            view, AutomationIds.DeleteUpstreamRow, u => u.Name == "echo");

        await UiDriver.UntilAsync(
            () => store.Current.Upstreams.Count == 0, "the upstream to be deleted");

        Assert.Contains("1 route", routes.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not be served", routes.StatusMessage, StringComparison.OrdinalIgnoreCase);

        // The route is still there, now pointing at nothing.
        Assert.Single(store.Current.Routes);

        await UiDriver.ClickForItemAsync<RouteItemViewModel>(
            view, AutomationIds.DeleteRouteRow, r => r.PathPrefix == "/app/doomed");
        await UiDriver.UntilAsync(() => store.Current.Routes.Count == 0, "the route to be deleted");
    });

    /// <summary>
    /// Removing a credential from a route it was attached to, leaving the route itself alone.
    /// </summary>
    [Fact]
    public Task ACredentialCanBeTakenOffARouteWithoutDeletingIt() => UiSession.RunAsync(async () =>
    {
        await using var harness = await SingleUseHarness.StartAsync();
        await harness.StartSingleUseThroughTheUiAsync();

        await ApprovalSeeding.SeedCredentialsAsync(harness);
        await ApprovalSeeding.SeedRouteMatrixAsync(harness);

        var store = harness.Services.GetRequiredService<ConfigStoreCache>();
        var routes = harness.Services.GetRequiredService<RoutesViewModel>();
        var view = UiDriver.Show<RoutesView>(routes);

        var row = await UiDriver.SelectAndOpenRowAsync<RouteItemViewModel>(
            view, AutomationIds.RoutesGrid, r => r.PathPrefix == "/app/two-headers");

        Assert.Equal(2, store.Current.Routes.Single(r => r.PathPrefix == "/app/two-headers").Credentials.Count);

        await UiDriver.ClickNthAsync(row, AutomationIds.RemoveRouteCredential, 0);

        await UiDriver.UntilAsync(
            () => store.Current.Routes.Single(r => r.PathPrefix == "/app/two-headers").Credentials.Count == 1,
            "the credential to come off the route");

        Assert.Contains(store.Current.Routes, r => r.PathPrefix == "/app/two-headers");
    });

    /// <summary>
    /// Deleting a funnel, and a source that a funnel still pools.
    ///
    /// The funnel goes; the sources it pooled do not, because a source is its own thing and another
    /// funnel may be using it.
    /// </summary>
    [Fact]
    public Task DeletingAFunnelLeavesItsSourcesBehind() => UiSession.RunAsync(async () =>
    {
        await using var harness = await SingleUseHarness.StartAsync();
        await harness.StartSingleUseThroughTheUiAsync();

        await using var one = await FakeMcpServer.StartAsync();

        await ApprovalSeeding.EnableFunnelAsync(harness);
        await ApprovalSeeding.SeedSourcesAsync(harness, ("one", "one", one.Url));
        await ApprovalSeeding.SeedFunnelAsync(harness, "doomed", "one");

        var store = harness.Services.GetRequiredService<ConfigStoreCache>();
        var funnels = harness.Services.GetRequiredService<McpFunnelViewModel>();
        var view = UiDriver.Show<McpFunnelView>(funnels);

        await UiDriver.ClickForItemAsync<McpFunnelItemViewModel>(
            view, AutomationIds.DeleteFunnelRow, f => f.Slug == "doomed");

        await UiDriver.UntilAsync(
            () => store.Current.McpFunnels.Count == 0, "the funnel to be deleted");

        Assert.Single(store.Current.McpSources);

        // And now the source, which nothing pools any more.
        await UiDriver.ClickForItemAsync<McpSourceItemViewModel>(
            view, AutomationIds.DeleteSourceRow, s => s.Name == "one");

        await UiDriver.UntilAsync(
            () => store.Current.McpSources.Count == 0, "the source to be deleted");
    });

    /// <summary>
    /// Switching the funnel off. Every /mcp endpoint stops answering, and the funnels themselves
    /// stay exactly where they were — this is a switch, not a delete.
    /// </summary>
    [Fact]
    public Task TurningTheFunnelOffStopsItAnsweringWithoutLosingIt() => UiSession.RunAsync(async () =>
    {
        await using var harness = await SingleUseHarness.StartAsync();
        await harness.StartSingleUseThroughTheUiAsync();

        await using var one = await FakeMcpServer.StartAsync();

        await ApprovalSeeding.EnableFunnelAsync(harness);
        await ApprovalSeeding.SeedSourcesAsync(harness, ("one", "one", one.Url));
        await ApprovalSeeding.SeedFunnelAsync(harness, "live", "one");

        var store = harness.Services.GetRequiredService<ConfigStoreCache>();
        var funnels = harness.Services.GetRequiredService<McpFunnelViewModel>();
        var view = UiDriver.Show<McpFunnelView>(funnels);

        await UiDriver.CheckAsync(view, AutomationIds.EnableFunnel, false);

        await UiDriver.UntilAsync(
            () => !store.Current.Settings.McpFunnelEnabled, "the funnel to be switched off");

        Assert.Single(store.Current.McpFunnels);

        using var client = harness.CreateClientFor(store.Current.McpFunnels.Single().Key.Value);
        var response = await client.GetAsync("/mcp/live");

        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    });
}
