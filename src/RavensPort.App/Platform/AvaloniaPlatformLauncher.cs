using Avalonia.Platform.Storage;
using RavensPort.UI.Services;

namespace RavensPort.Platform;

/// <summary>
/// <see cref="IPlatformLauncher"/> over Avalonia's launcher, which knows what each desktop wants —
/// ShellExecute on Windows, <c>open</c> on macOS, <c>xdg-open</c> on Linux. That is the whole
/// reason this went behind an interface: the WPF implementation it replaces was a
/// <c>Process.Start</c> that compiled everywhere and worked in one place.
/// </summary>
internal sealed class AvaloniaPlatformLauncher : IPlatformLauncher
{
    public async Task OpenUriAsync(string uri)
    {
        if (!await MainWindowAccessor.Required.Launcher.LaunchUriAsync(new Uri(uri)))
        {
            // The launcher reports refusal by returning false rather than throwing, and a caller
            // that only catches exceptions would report success having opened nothing.
            throw new InvalidOperationException($"The desktop declined to open '{uri}'.");
        }
    }

    public async Task OpenPathAsync(string path)
    {
        var launcher = MainWindowAccessor.Required.Launcher;

        // Directories and files are separate calls, and handing one the other's argument fails.
        var launched = Directory.Exists(path)
            ? await launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(path))
            : await launcher.LaunchFileInfoAsync(new FileInfo(path));

        if (!launched) throw new InvalidOperationException($"The desktop declined to open '{path}'.");
    }
}
