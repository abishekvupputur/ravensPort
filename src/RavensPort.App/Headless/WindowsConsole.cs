using System.Runtime.InteropServices;

namespace RavensPort.Headless;

/// <summary>
/// Gives the command line somewhere to write on Windows.
///
/// RavensPort.exe is a GUI-subsystem executable — it has to be, or every launch from the Start menu
/// would flash a console window — and Windows starts those with no console at all. So when it is
/// run with a command, it borrows one:
///
/// - Handles the parent already redirected — a pipe from PowerShell, a file, a CI runner — are
///   inherited and work as they are. Nothing is attached for those.
/// - Otherwise it attaches to the console of the shell that launched it, and points the standard
///   handles at it.
/// - With no parent console either — a shortcut with arguments — it opens one of its own.
///
/// The cost of borrowing: cmd and PowerShell do not wait for a GUI-subsystem program unless its
/// output is piped or redirected, so a bare <c>RavensPort status</c> can print after the prompt has
/// already come back. Piping (<c>| Out-Host</c>, or a token piped into <c>serve</c>) makes the
/// shell wait. Linux has no such distinction, and this does nothing there.
/// </summary>
internal static partial class WindowsConsole
{
    private const int StdInput = -10;
    private const int StdOutput = -11;
    private const int StdError = -12;
    private const uint AttachParentProcess = unchecked((uint)-1);

    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x1;
    private const uint FileShareWrite = 0x2;
    private const uint OpenExisting = 3;

    /// <summary>
    /// Must run before anything touches <see cref="Console"/>: it caches its streams on first use,
    /// and a stream opened while there was no console stays a null stream for the life of the process.
    /// </summary>
    public static void Attach()
    {
        if (!OperatingSystem.IsWindows()) return;

        var needIn = !IsUsable(GetStdHandle(StdInput));
        var needOut = !IsUsable(GetStdHandle(StdOutput));
        var needError = !IsUsable(GetStdHandle(StdError));

        if (!needIn && !needOut && !needError) return;

        if (!AttachConsole(AttachParentProcess) && !AllocConsole()) return;

        if (needOut) SetStdHandle(StdOutput, Open("CONOUT$"));
        if (needError) SetStdHandle(StdError, Open("CONOUT$"));
        if (needIn) SetStdHandle(StdInput, Open("CONIN$"));
    }

    private static bool IsUsable(IntPtr handle) => handle != IntPtr.Zero && handle != new IntPtr(-1);

    private static IntPtr Open(string device) =>
        CreateFileW(device, GenericRead | GenericWrite, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllocConsole();

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetStdHandle(int stdHandle);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetStdHandle(int stdHandle, IntPtr handle);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr CreateFileW(
        string fileName, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
}
