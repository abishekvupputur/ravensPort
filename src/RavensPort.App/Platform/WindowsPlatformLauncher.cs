using System.Diagnostics;
using RavensPort.UI.Services;

namespace RavensPort.App.Platform;

/// <summary>
/// <see cref="IPlatformLauncher"/> for Windows.
///
/// <c>UseShellExecute</c> hands the string to Windows to resolve, and Windows will happily run a
/// registered protocol handler, a UNC path, or an executable — a browser is only one of the things
/// it might pick. That is why <see cref="OpenUriAsync"/> is documented as taking an address the
/// caller has already validated.
/// </summary>
internal sealed class WindowsPlatformLauncher : IPlatformLauncher
{
    public Task OpenUriAsync(string uri) => StartAsync(uri);

    public Task OpenPathAsync(string path) => StartAsync(path);

    private static Task StartAsync(string target)
    {
        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        return Task.CompletedTask;
    }
}
