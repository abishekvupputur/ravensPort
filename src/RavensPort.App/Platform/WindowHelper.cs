using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Controls;

namespace RavensPort.Platform;

internal static class WindowHelper
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    /// <summary>
    /// Forces a dark title bar via the DWM API.
    ///
    /// Still hand-rolled: Avalonia's dark theme variant styles what it draws, and the title bar is
    /// drawn by Windows. Call from <c>Opened</c> — the HWND does not exist before then, and this
    /// silently does nothing without one.
    ///
    /// The platform handle is null on any backend that is not Win32, which is the whole check
    /// needed to make this a no-op elsewhere.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static void ApplyDarkTitleBar(Window window)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (window.TryGetPlatformHandle()?.Handle is not { } hwnd || hwnd == IntPtr.Zero) return;

        var value = 1;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int));
    }
}
