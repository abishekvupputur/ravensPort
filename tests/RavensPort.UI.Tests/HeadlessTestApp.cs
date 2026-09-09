using Avalonia;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using RavensPort.UI.Tests;

[assembly: AvaloniaTestApplication(typeof(HeadlessTestAppBuilder))]

namespace RavensPort.UI.Tests;

/// <summary>
/// The application these tests run inside, and deliberately not <c>RavensPort.App.App</c>.
///
/// That one is the whole product: it takes a single-instance mutex, purges the pre-2.0 store,
/// builds Kestrel, installs a tray icon and hangs the process lifetime off an explicit shutdown.
/// None of it is what a view is being tested for, and the mutex alone would make two test runs on
/// one machine fight each other.
///
/// What a view does need is the styling, because that is what turns a Button into a control with a
/// template, a hit-testable bounds and a Click that does something. So this loads exactly the two
/// things App.axaml loads — the Fluent theme and the app's own styles — and nothing else. A view
/// that renders here renders in the product for the same reasons.
/// </summary>
public sealed class HeadlessTestApp : Application
{
    public override void Initialize()
    {
        // The same four, in the same order, as App.axaml: Fluent for the control templates, the
        // DataGrid’s own theme because it ships separately and renders as nothing without it, then
        // the app’s Controls over the top. Loaded from the RavensPort assembly rather than restated
        // here, so a style the app changes is picked up by these tests without being told.
        Styles.Add(new FluentTheme());
        Styles.Add(Include("avares://Avalonia.Controls.DataGrid/Themes/Fluent.xaml"));
        Styles.Add(Include("avares://RavensPort/Theme/Controls.axaml"));

        Resources.MergedDictionaries.Add(Resource("avares://RavensPort/Theme/Palette.axaml"));
        Resources.MergedDictionaries.Add(Resource("avares://RavensPort/Theme/Shared.axaml"));

        // Dark, because the app requests it and the brushes in Palette are written for it.
        RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
    }

    private static StyleInclude Include(string uri) =>
        new(new Uri("avares://RavensPort/")) { Source = new Uri(uri) };

    private static ResourceInclude Resource(string uri) =>
        new(new Uri("avares://RavensPort/")) { Source = new Uri(uri) };
}

/// <summary>The builder the headless runner starts <see cref="HeadlessTestApp"/> with.</summary>
public static class HeadlessTestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<HeadlessTestApp>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions
        {
            // No text rendering. Nothing here asserts on glyphs, and asking for a real font stack
            // is what would otherwise make this suite need a font on the Linux runner — the one
            // platform difference Program.BuildAvaloniaApp already warns about.
            UseHeadlessDrawing = true,
        });
}
