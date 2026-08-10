using Avalonia.Controls;

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
/// The tray icon, which is the app's only presence most of the time: the window is hidden rather
/// than closed, and this is the way back to it.
///
/// Two implementations, and the difference is not cosmetic. Windows uses WinForms
/// <c>NotifyIcon</c>, whose menu this app themes and whose balloon tip is the only way to tell
/// someone who closed the setup window that nothing is being served. Everywhere else uses
/// Avalonia's <c>TrayIcon</c>, which draws a native menu that cannot be themed and offers no
/// notification at all — so <see cref="NotifyIdleWhileGated"/> has nowhere to go and says nothing.
/// </summary>
public interface ITrayIcon : IDisposable
{
    /// <param name="confirmExit">
    /// Asked before shutting down, and may refuse. Exit is the moment an unsaved change stops
    /// existing, so it is the one thing here that needs a way to say no — and it has to happen
    /// before shutdown, which is a point of no return.
    /// </param>
    void Initialize(Window mainWindow, Func<Task<bool>>? confirmExit = null);

    /// <summary>Updates the tooltip and the first menu item to match what the app is doing.</summary>
    void SetState(TrayState state);

    /// <summary>
    /// Tells the user the app is sitting in the tray serving nothing, because they closed the setup
    /// window without finishing. A no-op where the platform has no notification to show it with.
    /// </summary>
    void NotifyIdleWhileGated();
}
