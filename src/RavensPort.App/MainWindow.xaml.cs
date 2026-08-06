using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using RavensPort.App.Helpers;
using RavensPort.UI.ViewModels;

namespace RavensPort.App;

public partial class MainWindow : Window
{
    public MainWindow(
        MainWindowViewModel mainWindowViewModel,
        SetupViewModel setupViewModel,
        CredentialsViewModel credentialsViewModel,
        RoutesViewModel routesViewModel,
        McpFunnelViewModel mcpFunnelViewModel,
        SettingsViewModel settingsViewModel)
    {
        InitializeComponent();

        // Set here rather than in XAML, because the number comes from the assembly the build
        // stamped and markup has nothing to read it from. This is the one place a user can see
        // which build they are running without opening the exe's file properties, which matters
        // when a bug report says "latest".
        // "v" here rather than baked into AppVersion.Display: the bare number is what matches the
        // installer name and the exe's file properties, and a prefix belongs to how it is shown.
        Title = $"RavensPort v{AppVersion.Display}";

        DataContext = mainWindowViewModel;

        SetupViewControl.DataContext = setupViewModel;
        CredentialsViewControl.DataContext = credentialsViewModel;
        RoutesViewControl.DataContext = routesViewModel;
        McpFunnelViewControl.DataContext = mcpFunnelViewModel;
        SettingsViewControl.DataContext = settingsViewModel;

        SourceInitialized += (_, _) => WindowHelper.ApplyDarkTitleBar(this);
        Closing += MainWindow_Closing;
    }

    private void TabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Only react to the TabControl itself; ComboBoxes inside the tabs raise the same
        // routed event and would otherwise trigger a reload on every dropdown change.
        if (!ReferenceEquals(e.OriginalSource, sender)) return;

        // The Routes tab shows credentials owned by the Credentials tab, so re-read them
        // on every switch — otherwise a newly added credential is missing from the dropdown.
        if (RoutesViewControl.DataContext is RoutesViewModel routesViewModel)
        {
            routesViewModel.Reload();
        }

        // The MCP Funnel tab lists routes owned by the Routes tab, and its endpoint URLs embed
        // the listen port owned by Settings — both can change while this tab is off screen.
        if (McpFunnelViewControl.DataContext is McpFunnelViewModel mcpFunnelViewModel)
        {
            mcpFunnelViewModel.Reload();
        }

        // Settings shows the autostart state, which the tray menu can also change.
        if (SettingsViewControl.DataContext is SettingsViewModel settingsViewModel)
        {
            settingsViewModel.Reload();
        }
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        // Never actually close from the X button — only the tray "Exit" command shuts the app down.
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
