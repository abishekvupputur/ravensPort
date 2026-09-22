using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using RavensPort.Core.Models;
using RavensPort.UI.ViewModels;
using RavensPort.Views;

namespace RavensPort.UI.Tests;

/// <summary>
/// Every screen, drawn for real and written out as a PNG, so what the app looks like can be read
/// rather than guessed from the markup.
///
/// A build proves the XAML parses; the other UI tests prove a control is wired. Neither says that a
/// field is the wrong colour, that a label is a different size from the one beside it, or that a
/// button sits on a background that swallows it — which is what a person notices first.
///
/// It draws only when <see cref="HeadlessTestAppBuilder.ScreenshotDirectoryVariable"/> names a
/// directory, because real drawing needs a font stack and every other run here deliberately does
/// without one. Unset, each test returns at once, before any session starts:
///
/// <code>RAVENSPORT_SCREENSHOT_DIR=/tmp/tour dotnet test tests/RavensPort.UI.Tests -f net10.0 --filter ScreenTour</code>
/// </summary>
public class ScreenTourUiTests
{
    private const int Width = 920;
    private const int Height = 760;

    /// <summary>Tall enough to lay a whole tab out without scrolling, for reading every field.</summary>
    private const int TallHeight = 2600;

    private static string? Directory =>
        Environment.GetEnvironmentVariable(HeadlessTestAppBuilder.ScreenshotDirectoryVariable);

    private static void Snap(TopLevel window, string name)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException(
            $"nothing rendered for {name} (real={HeadlessTestAppBuilder.IsRenderingForReal}, "
            + $"bounds={window.Bounds}, visible={window.IsVisible}, clientSize={window.ClientSize}, "
            + $"lastFrame={(window.GetLastRenderedFrame() is null ? "null" : "set")})");

        System.IO.Directory.CreateDirectory(Directory!);
        frame.Save(Path.Combine(Directory!, name + ".png"));
    }

    private static MainWindow Open(SingleUseHarness harness, int height = Height)
    {
        var window = harness.Services.GetRequiredService<MainWindow>();
        window.Width = Width;
        window.Height = height;

        return (MainWindow)UiDriver.ShowWindow(window);
    }

    /// <summary>
    /// The shell, once it has left the setup page. Snapping before that draws the setup page under
    /// every tab's name, which is what the first run of this tour did.
    /// </summary>
    private static async Task<MainWindow> OpenReadyAsync(SingleUseHarness harness, int height = Height)
    {
        var window = Open(harness, height);
        var shell = harness.Services.GetRequiredService<MainWindowViewModel>();

        // What App does once the vault gate opens; the harness has no App, so it is done here.
        shell.EnterNormalMode();
        await UiDriver.UntilAsync(() => !shell.IsGated, "the shell to leave the setup page");
        Dispatcher.UIThread.RunJobs();

        return window;
    }

    private static void SelectTab(Window window, int index)
    {
        window.FindControl<TabControl>("TabsControl")!.SelectedIndex = index;
        Dispatcher.UIThread.RunJobs();
    }

    [Fact]
    public Task TheSetupPage() => UiSession.RunAsync(async () =>
    {
        if (Directory is null) return;

        await using var harness = await SingleUseHarness.StartAsync();

        var window = Open(harness);
        Snap(window, "01-setup");

        window.Height = TallHeight;
        Snap(window, "01-setup-tall");
    });

    [Fact]
    public Task EveryTab() => UiSession.RunAsync(async () =>
    {
        if (Directory is null) return;

        await using var harness = await SingleUseHarness.StartAsync();
        await harness.StartSingleUseThroughTheUiAsync();

        await ApprovalSeeding.SeedCredentialsAsync(harness);
        await ApprovalSeeding.SeedRouteMatrixAsync(harness);

        var window = await OpenReadyAsync(harness);
        var names = new[] { "credentials", "routes", "api-to-mcp", "funnel", "settings" };

        for (var index = 0; index < names.Length; index++)
        {
            window.Height = Height;
            SelectTab(window, index);
            Snap(window, $"1{index + 1}-{names[index]}");

            window.Height = TallHeight;
            Dispatcher.UIThread.RunJobs();
            Snap(window, $"1{index + 1}-{names[index]}-tall");
        }
    });

    [Fact]
    public Task EveryCredentialForm() => UiSession.RunAsync(async () =>
    {
        if (Directory is null) return;

        await using var harness = await SingleUseHarness.StartAsync();
        await harness.StartSingleUseThroughTheUiAsync();

        var window = await OpenReadyAsync(harness, TallHeight);
        SelectTab(window, 0);

        var number = 0;
        foreach (var kind in CredentialKindInfo.All)
        {
            await UiDriver.SelectAsync(window, AutomationIds.CredentialKind, kind.Label);
            Snap(window, $"2{number++}-credential-{kind.Kind}");
        }
    });

    [Fact]
    public Task TheApiBridgeEditorWithASample() => UiSession.RunAsync(async () =>
    {
        if (Directory is null) return;

        await using var harness = await SingleUseHarness.StartAsync();
        await harness.StartSingleUseThroughTheUiAsync();

        var window = await OpenReadyAsync(harness, TallHeight);
        SelectTab(window, 2);

        harness.Services.GetRequiredService<ApiBridgeViewModel>().LoadSampleCommand.Execute(null);
        Snap(window, "30-api-bridge-sample");
    });

    [Fact]
    public Task TheOperationPicker() => UiSession.RunAsync(() =>
    {
        if (Directory is null) return Task.CompletedTask;

        OpenApiOperationSummary[] operations =
        [
            new("/repos/{owner}/{repo}", "GET", "repos_get", "Get a repository", ["repos"], 300),
            new("/repos/{owner}/{repo}/issues", "GET", "issues_list", "List repository issues", ["issues"], 320),
            new("/repos/{owner}/{repo}/issues", "POST", "issues_create", "Create an issue", ["issues"], 380),
            new("/repos/{owner}/{repo}/pulls", "GET", "pulls_list", "List pull requests", ["pulls"], 340),
            new("/user", "GET", "users_get_authenticated", "Get the authenticated user", ["users"], 220),
        ];

        var viewModel = new OpenApiOperationPickerViewModel("{}", "github.json", operations);
        var window = new OpenApiOperationPickerWindow(viewModel);

        UiDriver.ShowWindow(window);
        Snap(window, "40-operation-picker");

        return Task.CompletedTask;
    });
}
