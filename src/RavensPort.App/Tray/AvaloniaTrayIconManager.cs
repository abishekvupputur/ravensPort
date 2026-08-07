using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace RavensPort.Tray;

/// <summary>
/// <see cref="ITrayIcon"/> on Avalonia's own tray icon, for every platform but Windows.
///
/// Two things the WinForms one does are simply not available here, and both are losses rather than
/// trade-offs. The menu is drawn by the desktop, so the app's dark palette does not reach it. And
/// there is no balloon tip — <see cref="NotifyIdleWhileGated"/> has nothing to call — so a user who
/// closes the setup window without finishing gets no notification that the proxy is serving
/// nothing. The tooltip still says so, but only to someone who hovers.
///
/// Worth knowing before relying on this: tray support across Linux desktops is uneven. GNOME needs
/// an extension for the tray to exist at all, and this app is tray-resident by design.
/// </summary>
internal sealed class AvaloniaTrayIconManager : ITrayIcon
{
    private TrayIcon? _icon;
    private NativeMenuItem? _openItem;
    private Window? _mainWindow;
    private TrayState _state = TrayState.Starting;

    public void Initialize(Window mainWindow, Func<Task<bool>>? confirmExit = null)
    {
        _mainWindow = mainWindow;

        _openItem = new NativeMenuItem("Open Settings");
        _openItem.Click += (_, _) => ShowMainWindow();

        var exitItem = new NativeMenuItem("Exit");
        exitItem.Click += (_, _) => BeginExit(confirmExit);

        var menu = new NativeMenu();
        menu.Add(_openItem);
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(exitItem);

        _icon = new TrayIcon
        {
            Icon = LoadIcon(),
            ToolTipText = "RavensPort",
            IsVisible = true,
            Menu = menu,
        };

        // Left-click. The menu is bound to the platform's own gesture for it, usually right-click.
        _icon.Clicked += (_, _) => ShowMainWindow();

        SetState(_state);
    }

    public void SetState(TrayState state)
    {
        _state = state;

        if (_icon is null) return;

        _icon.ToolTipText = state switch
        {
            TrayState.Starting => "RavensPort — starting",
            TrayState.SetupRequired => "RavensPort — setup required",
            TrayState.VaultLocked => "RavensPort — vault locked",
            _ => "RavensPort",
        };

        if (_openItem is not null)
        {
            _openItem.Header = state is TrayState.SetupRequired or TrayState.Starting
                ? "Set up RavensPort…"
                : "Open Settings";
        }
    }

    /// <summary>
    /// Nothing to do. Avalonia's tray icon has no notification of any kind, so the one case this
    /// exists for — the app sitting idle after the setup window was closed — goes unannounced here.
    /// Left as a no-op rather than removed from the interface, because on Windows it still matters.
    /// </summary>
    public void NotifyIdleWhileGated()
    {
    }

    /// <summary>
    /// Asks, then quits, without holding the menu's click handler open while the user reads a
    /// dialog. Same shape as the WinForms implementation and for the same reason.
    /// </summary>
    private static void BeginExit(Func<Task<bool>>? confirmExit) =>
        Dispatcher.UIThread.Post(async void () =>
        {
            try
            {
                if (confirmExit is not null && !await confirmExit()) return;
            }
            catch
            {
                // Top-level async void, so nothing above catches this. A failure to ask must not
                // become a failure to quit: fall through and shut down, because the alternative is
                // an Exit menu item that silently does nothing.
            }

            (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
        });

    private static WindowIcon? LoadIcon()
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://RavensPort/Assets/tray.ico"));
            return new WindowIcon(new Bitmap(stream));
        }
        catch
        {
            // An icon-less tray entry is still a tray entry, and losing it is not worth failing
            // startup over — this runs while the host is being composed.
            return null;
        }
    }

    private void ShowMainWindow() => Dispatcher.UIThread.Post(() =>
    {
        if (_mainWindow is null) return;

        _mainWindow.Show();
        _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
    });

    public void Dispose()
    {
        _icon?.Dispose();
        _icon = null;
    }
}
