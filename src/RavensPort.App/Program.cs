using Avalonia;

namespace RavensPort;

internal static class Program
{
    /// <summary>
    /// Avalonia needs a real entry point, where WPF generated one. Everything that used to live in
    /// App.OnStartup is in <see cref="App.OnFrameworkInitializationCompleted"/> instead — this must
    /// stay free of anything that touches the UI or the host, because none of it exists yet.
    ///
    /// One executable, two ways in. Launched plainly it is the desktop app. Given a command —
    /// <c>RavensPort serve</c>, <c>RavensPort routes list</c> — it is the command line, and Avalonia
    /// is never started: no window, no display needed, so the same binary runs on a headless
    /// server. The decision is made here, before anything touches the UI, for exactly that reason.
    /// </summary>
    [STAThread]
    public static int Main(string[] args)
    {
        if (Headless.Cli.Wants(args)) return Headless.Cli.Run(args);

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    /// <summary>Also used by the XAML previewer and designer tooling, which is why it is public.</summary>
    ///
    /// <remarks>
    /// No embedded font yet. The theme asks for Segoe UI, which every Windows this app supports
    /// has — but no other platform does, so a font to fall back on is something the theme work
    /// has to answer before this runs anywhere else.
    /// </remarks>
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .LogToTrace();
}
