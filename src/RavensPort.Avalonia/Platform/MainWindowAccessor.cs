using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

namespace RavensPort.Platform;

/// <summary>
/// Finds the main window.
///
/// Three of the adapters below need one, because Avalonia hangs the clipboard, the launcher and
/// modal ownership off a <see cref="TopLevel"/> rather than offering them as ambient statics the
/// way WPF does. This is that lookup, written once.
///
/// It can legitimately answer null: the app is tray-resident and the window is hidden rather than
/// closed when the user dismisses it, but there is also a window between process start and the
/// lifetime being handed its MainWindow. Callers are expected to say so rather than crash.
/// </summary>
internal static class MainWindowAccessor
{
    public static Window? Current =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    /// <summary>For the paths that genuinely cannot proceed without one, with a message that says why.</summary>
    public static Window Required =>
        Current ?? throw new InvalidOperationException(
            "RavensPort has no window yet, so the desktop cannot be asked to do this.");
}
