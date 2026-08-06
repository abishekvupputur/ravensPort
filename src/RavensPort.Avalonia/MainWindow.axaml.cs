using Avalonia.Controls;
using RavensPort.Platform;
using RavensPort.UI.ViewModels;

namespace RavensPort;

public partial class MainWindow : Window
{
    private readonly RoutesViewModel? _routes;
    private readonly McpFunnelViewModel? _funnels;
    private readonly SettingsViewModel? _settings;

    /// <summary>
    /// Parameterless, for the XAML previewer and for Avalonia's own loader. The app always uses the
    /// injected constructor below; nothing here may assume a view model is present.
    /// </summary>
    public MainWindow() => InitializeComponent();

    public MainWindow(
        MainWindowViewModel mainWindowViewModel,
        SetupViewModel setupViewModel,
        RoutesViewModel routesViewModel,
        McpFunnelViewModel mcpFunnelViewModel,
        SettingsViewModel settingsViewModel)
        : this()
    {
        DataContext = mainWindowViewModel;

        // Each page gets its own view model rather than reaching through the shell's, which is how
        // the real views will be wired too.
        SetupViewControl.DataContext = setupViewModel;
        SettingsViewControl.DataContext = settingsViewModel;

        _routes = routesViewModel;
        _funnels = mcpFunnelViewModel;
        _settings = settingsViewModel;

        // Opened, where WPF used SourceInitialized: both mean "the native window now exists", and
        // the DWM call needs an HWND.
        Opened += (_, _) => WindowHelper.ApplyDarkTitleBar(this);
        Closing += MainWindow_Closing;
    }

    private void TabControl_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        // Only react to the TabControl itself; controls inside the tabs raise this event too and
        // would otherwise trigger a reload on every dropdown change.
        if (!ReferenceEquals(e.Source, sender)) return;

        // The Routes tab shows credentials owned by the Credentials tab, so re-read them
        // on every switch — otherwise a newly added credential is missing from the dropdown.
        _routes?.Reload();

        // The MCP Funnel tab lists routes owned by the Routes tab, and its endpoint URLs embed
        // the listen port owned by Settings — both can change while this tab is off screen.
        _funnels?.Reload();

        // Settings shows state the tray menu can also change.
        _settings?.Reload();
    }

    private void MainWindow_Closing(object? sender, WindowClosingEventArgs e)
    {
        // Never actually close from the X button — only the tray "Exit" command shuts the app down.
        // ShutdownMode is OnExplicitShutdown, so hiding the last window does not end the process.
        e.Cancel = true;
        Hide();

        // Closing the setup page before finishing leaves a tray icon and no proxy, which looks
        // exactly like a working install until the first request fails. Said once, so it reads as
        // information rather than nagging.
        if (DataContext is MainWindowViewModel { IsGated: true } && !_warnedAboutBeingIdle)
        {
            _warnedAboutBeingIdle = true;
            HiddenWhileGated?.Invoke();
        }
    }

    /// <summary>Raised the first time the window is hidden with setup still incomplete.</summary>
    public event Action? HiddenWhileGated;

    private bool _warnedAboutBeingIdle;
}
