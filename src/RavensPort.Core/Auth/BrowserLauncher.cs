using System.Diagnostics;

namespace RavensPort.Core.Auth;

/// <summary>
/// Opens an authorization URL — in the desktop's browser, or wherever the current flow says
/// instead.
///
/// The override is for the headless host, which has no browser to open. A sign-in started from
/// <c>ravensport credentials signin</c> sets it for the duration of that one request, and the
/// URL is printed to the person at the terminal instead; the redirect still lands on this
/// machine's loopback port, which is what the SSH forward in the docs is for.
///
/// AsyncLocal rather than a plain static, so the override follows the one sign-in it was set for
/// through every await, and a desktop sign-in running at the same moment still opens a browser.
/// </summary>
public static class BrowserLauncher
{
    private static readonly AsyncLocal<Action<Uri>?> Override = new();

    /// <summary>Routes every URL opened in this async flow to <paramref name="open"/> until disposed.</summary>
    public static IDisposable Redirect(Action<Uri> open)
    {
        var previous = Override.Value;
        Override.Value = open;
        return new Restore(previous);
    }

    /// <summary>
    /// Callers check the scheme first: UseShellExecute resolves whatever the string is, not
    /// necessarily a browser.
    /// </summary>
    public static void Open(Uri uri)
    {
        if (Override.Value is { } open)
        {
            open(uri);
            return;
        }

        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    private sealed class Restore(Action<Uri>? previous) : IDisposable
    {
        public void Dispose() => Override.Value = previous;
    }
}
