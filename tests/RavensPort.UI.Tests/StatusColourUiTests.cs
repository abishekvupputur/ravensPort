using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using RavensPort.UI.ViewModels;
using RavensPort.Views;

namespace RavensPort.UI.Tests;

/// <summary>
/// A line whose colour says whether it is good news.
///
/// The manifest editor's status is red while it reports a problem and should turn green when the
/// manifest validates. It carried a colour of its own written on the element, and a value set on
/// an element beats any style — so the class that was meant to recolour it never could, and
/// "Valid — 4 tool(s)…" was drawn in the error colour. Nothing but looking at it shows that.
/// </summary>
public class StatusColourUiTests
{
    private static Color ColourOf(IBrush? brush) => (brush as ISolidColorBrush)?.Color ?? Colors.Magenta;

    private static TextBlock Status(Window window) =>
        window.GetVisualDescendants().OfType<TextBlock>()
            .First(text => text.Classes.Contains("status"));

    [Fact]
    public Task AValidManifestIsNotReportedInTheErrorColour() => UiSession.RunAsync(async () =>
    {
        await using var harness = await SingleUseHarness.StartAsync();
        await harness.StartSingleUseThroughTheUiAsync();

        var bridges = harness.Services.GetRequiredService<ApiBridgeViewModel>();
        var window = UiDriver.Show<ApiBridgeView>(bridges);

        // Empty, it is a prompt to do something, and red.
        Assert.Equal(Color.Parse("#E5534B"), ColourOf(Status(window).Foreground));

        bridges.LoadSampleCommand.Execute(null);
        await UiDriver.UntilAsync(() => bridges.ManifestIsValid, "the sample to validate");

        var status = Status(window);
        Assert.StartsWith("Valid", status.Text);
        Assert.Equal(Color.Parse("#3FB950"), ColourOf(status.Foreground));
    });
}
