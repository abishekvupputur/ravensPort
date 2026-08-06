using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace RavensPort.Tray;

/// <summary>
/// What the app is currently doing, as far as the tray is concerned. The proxy no longer starts
/// unconditionally — it waits for a password manager — so "there is an icon" stopped meaning
/// "requests are being served", and the tooltip has to say which.
/// </summary>
public enum TrayState
{
    Starting,
    SetupRequired,
    Running,
    VaultLocked,
}

/// <summary>
/// Still plain WinForms NotifyIcon, now next to Avalonia rather than WPF.
///
/// Avalonia has a TrayIcon of its own and it was considered. It draws the icon and a native menu,
/// but that menu cannot be themed — so the dark palette below would be a light Win32 popup — and it
/// has no balloon tip, which is how <see cref="NotifyIdleWhileGated"/> tells someone who closed the
/// setup window that nothing is being served. Neither is worth losing while the app is
/// Windows-only, so this is the one deliberately Windows-bound file in the project. A second
/// platform replaces this class and nothing else.
/// </summary>
public sealed class TrayIconManager : IDisposable
{
    private NotifyIcon? _notifyIcon;
    private Window? _mainWindow;
    private ToolStripItem? _openItem;
    private TrayState _state = TrayState.Starting;

    /// <param name="confirmExit">
    /// Asked before shutting down, and may refuse. Exit is the moment an unsaved change stops
    /// existing, so it is the one thing here that needs a way to say no — and it has to happen
    /// before shutdown, which is a point of no return.
    ///
    /// A Task now rather than a bool: the question is asked by a dialog, and Avalonia has no
    /// synchronous modal to ask it with.
    /// </param>
    public void Initialize(
        Window mainWindow,
        Func<Task<bool>>? confirmExit = null)
    {
        _mainWindow = mainWindow;

        var contextMenu = new ContextMenuStrip
        {
            Renderer = new DarkMenuRenderer(),
            BackColor = Color.FromArgb(0x1A, 0x1A, 0x1A),
            ForeColor = Color.FromArgb(0xEB, 0xEB, 0xEB),
        };
        _openItem = contextMenu.Items.Add("Open Settings", null, (_, _) => ShowMainWindow());

        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add("Exit", null, (_, _) => BeginExit(confirmExit));

        _notifyIcon = new NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Text = "RavensPort",
            Visible = true,
            ContextMenuStrip = contextMenu,
        };
        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) ShowMainWindow();
        };

        SetState(_state);
    }

    /// <summary>
    /// Asks, then quits — on the UI thread, and without blocking the WinForms click handler that
    /// started it.
    ///
    /// The menu click arrives on the UI thread already, but the answer does not come back until the
    /// user has given it, and the handler cannot be held open that long: the context menu stays on
    /// screen over the dialog until its handler returns. So this hands the whole sequence to the
    /// dispatcher and lets the click complete.
    /// </summary>
    private static void BeginExit(Func<Task<bool>>? confirmExit) =>
        Dispatcher.UIThread.Post(async void () =>
        {
            try
            {
                if (confirmExit is not null && !await confirmExit()) return;

                (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)
                    ?.Shutdown();
            }
            catch
            {
                // Nothing above this to catch it — this is a top-level async void. A failure to ask
                // must not become a failure to quit, so fall through to shutting down: the
                // alternative is an Exit menu item that silently does nothing.
                (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)
                    ?.Shutdown();
            }
        });

    /// <summary>
    /// Updates the tooltip and the first menu item. Tooltip-only rather than a second icon: a
    /// distinct overlay would be better, but the wrong-looking icon is a worse first impression
    /// than a clear tooltip, and this can be read without hovering over a 16px glyph.
    /// </summary>
    public void SetState(TrayState state)
    {
        _state = state;

        if (_notifyIcon is null) return;

        // NotifyIcon.Text throws above 63 characters, which is short enough that a well-meaning
        // longer message would crash the tray at runtime rather than at build time.
        _notifyIcon.Text = state switch
        {
            TrayState.Starting => "RavensPort — starting",
            TrayState.SetupRequired => "RavensPort — setup required",
            TrayState.VaultLocked => "RavensPort — vault locked",
            _ => "RavensPort",
        };

        if (_openItem is not null)
        {
            _openItem.Text = state is TrayState.SetupRequired or TrayState.Starting
                ? "Set up RavensPort…"
                : "Open Settings";
        }
    }

    /// <summary>
    /// A balloon for the one case the user cannot otherwise see: they closed the setup window
    /// without finishing, so the app is sitting in the tray serving nothing.
    /// </summary>
    public void NotifyIdleWhileGated()
    {
        _notifyIcon?.ShowBalloonTip(
            5000,
            "RavensPort is idle",
            "No proxy is running until a password manager is set up. Click the tray icon to finish.",
            ToolTipIcon.Info);
    }

    private static Icon LoadTrayIcon()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("tray.ico", StringComparison.OrdinalIgnoreCase));

        if (resourceName is not null)
        {
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream is not null) return new Icon(stream);
        }

        return SystemIcons.Application;
    }

    /// <summary>
    /// Brings the window up. Posted to the dispatcher because the tray's click handlers run on the
    /// WinForms message loop, which is not Avalonia's UI thread — touching a Window from there
    /// throws.
    /// </summary>
    private void ShowMainWindow() => Dispatcher.UIThread.Post(() =>
    {
        if (_mainWindow is null) return;

        _mainWindow.Show();
        _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
    });

    public void Dispose() => _notifyIcon?.Dispose();
}
