using Microsoft.Extensions.DependencyInjection;
using RavensPort.Core.Models;
using RavensPort.UI.ViewModels;
using RavensPort.Views;

namespace RavensPort.UI.Tests;

/// <summary>
/// The API to MCP tab, rendered.
///
/// It is the one view that came across in the port rather than being written for Avalonia, so it is
/// the one whose markup nothing had ever loaded. A build proves the XAML parses and that compiled
/// bindings resolve; it says nothing about whether an app-level resource or a style class exists,
/// and both fail quietly — a missing class leaves text the wrong colour, a missing resource throws
/// when the view is first realised. Showing it is what settles that.
/// </summary>
public class ApiBridgeTabUiTests
{
    [Fact]
    public Task TheTabRendersAndTakesAManifest() => UiSession.RunAsync(async () =>
    {
        await using var harness = await SingleUseHarness.StartAsync();
        await harness.StartSingleUseThroughTheUiAsync();

        var bridges = harness.Services.GetRequiredService<ApiBridgeViewModel>();
        var window = UiDriver.Show<ApiBridgeView>(bridges);

        // Realising the view is the assertion: an app-level resource this markup names but does not
        // own — the proxy key editor's template, its help text — would have thrown by now.
        UiDriver.Find<Avalonia.Controls.TextBox>(window, "bridges.manifest");

        // The sample ships in the app and is the first manifest most people edit. Loading it proves
        // the command is wired and the validator behind it agrees with what shipped.
        bridges.LoadSampleCommand.Execute(null);

        await UiDriver.UntilAsync(
            () => bridges.ManifestIsValid,
            () => $"the sample to validate (status='{bridges.ManifestStatus}')");

        // The preview is what a user reads before saving, so it has to say something. Variant tools
        // contribute a line per variant, which is why this is more than a tool count.
        Assert.NotEmpty(bridges.ManifestPreview);
        Assert.Contains(bridges.ManifestPreview, line => line.Contains("→", StringComparison.Ordinal));
    });

    /// <summary>
    /// The save button does both jobs, and the heading has to say which. Pressing Edit used to hide
    /// the new-bridge fields under a heading still reading "Add bridge", which looked like the way
    /// to add one had disappeared.
    /// </summary>
    [Fact]
    public Task EditingABridgeSaysSoAndCanBeLeft() => UiSession.RunAsync(async () =>
    {
        await using var harness = await SingleUseHarness.StartAsync();
        await harness.StartSingleUseThroughTheUiAsync();

        var route = new RouteMapping { PathPrefix = "/tasks-api" };
        await harness.Services.GetRequiredService<RavensPort.Core.Storage.ConfigStoreCache>()
            .MutateAsync(store => store.Routes.Add(route));

        var bridges = harness.Services.GetRequiredService<ApiBridgeViewModel>();
        bridges.Reload();

        UiDriver.Show<ApiBridgeView>(bridges);

        bridges.NewBridgeName = "tracker";
        bridges.NewBridgeRoute = bridges.Routes.First();
        bridges.LoadSampleCommand.Execute(null);
        await bridges.SaveBridgeCommand.ExecuteAsync(null);

        var bridge = Assert.Single(bridges.Bridges);
        Assert.Equal("Add bridge", bridges.SaveSectionTitle);

        // Selecting a row is what reveals its key, one at a time.
        bridges.SelectedBridge = bridge;
        Assert.True(bridges.HasSelectedBridge);
        Assert.Contains(bridge.Name, bridges.SelectedBridgeTitle, StringComparison.Ordinal);

        // Editing switches the card's job, and the heading names the bridge rather than saying
        // "Add bridge" over a gap where the fields used to be.
        bridges.EditManifestCommand.Execute(bridge);
        Assert.True(bridges.IsEditingExisting);
        Assert.Contains(bridge.Name, bridges.SaveSectionTitle, StringComparison.Ordinal);

        // And there is a way back to adding, which is the part that was missing.
        bridges.CancelEditCommand.Execute(null);
        Assert.False(bridges.IsEditingExisting);
        Assert.Equal("Add bridge", bridges.SaveSectionTitle);
    });

    /// <summary>
    /// Importing reads through the picker seam rather than a Windows dialog, which is what lets
    /// this tab exist on Linux at all. The file's name is recorded on the bridge, so a user with
    /// several can tell which file to re-import after editing it.
    /// </summary>
    [Fact]
    public Task ImportingAManifestFillsTheEditorFromThePicker() => UiSession.RunAsync(async () =>
    {
        await using var harness = await SingleUseHarness.StartAsync();
        await harness.StartSingleUseThroughTheUiAsync();

        var picker = (RecordingOpenPicker)harness.Services.GetRequiredService<UI.Services.IFileOpenPicker>();
        picker.Next = new UI.Services.PickedFile("my-api.json", McpApiBridgeSample.Read());

        var bridges = harness.Services.GetRequiredService<ApiBridgeViewModel>();
        UiDriver.Show<ApiBridgeView>(bridges);

        await bridges.ImportManifestCommand.ExecuteAsync(null);

        Assert.True(bridges.ManifestIsValid, bridges.ManifestStatus);
        Assert.Equal("my-api.json", bridges.NewBridgeManifestOrigin);
        Assert.Contains("my-api.json", bridges.StatusMessage, StringComparison.Ordinal);

        // A cancelled dialog leaves the editor exactly as it was.
        var before = bridges.ManifestJson;
        picker.Next = null;
        await bridges.ImportManifestCommand.ExecuteAsync(null);

        Assert.Equal(before, bridges.ManifestJson);
    });
}
