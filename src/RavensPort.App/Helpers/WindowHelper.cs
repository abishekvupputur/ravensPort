using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace RavensPort.App.Helpers;

internal static partial class WindowHelper
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    /// <summary>
    /// Forces a dark title bar on the given window via the DWM API.
    /// Call from <c>SourceInitialized</c> so the HWND is already available.
    /// </summary>
    internal static void ApplyDarkTitleBar(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        int value = 1;
        // Discarded deliberately: the title bar is cosmetic, and this attribute is unsupported on
        // Windows 10 builds before 1809, where the call returns a failure and the light title bar
        // stays. There is nothing to tell the user and nothing to retry.
        _ = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int));
    }
}
