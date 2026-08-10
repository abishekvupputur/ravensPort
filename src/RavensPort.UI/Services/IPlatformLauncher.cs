namespace RavensPort.UI.Services;

/// <summary>
/// Hands a URL or a local path to the desktop to open in whatever it thinks should handle it.
///
/// <c>Process.Start(… UseShellExecute = true)</c> compiles anywhere but only <em>works</em> on
/// Windows; on Linux the same call needs <c>xdg-open</c> and on macOS <c>open</c>. That makes it a
/// platform detail rather than a general one, so it belongs behind this interface with the rest of
/// them — Avalonia has a first-class <c>Launcher</c> that already knows all three answers.
/// </summary>
public interface IPlatformLauncher
{
    /// <summary>
    /// Opens an http/https address. Callers are expected to have validated the scheme first — see
    /// <see cref="RavensPort.Core.Models.UrlValidation"/> — because the shell will happily run a
    /// registered protocol handler or an executable given the chance.
    /// </summary>
    Task OpenUriAsync(string uri);

    /// <summary>Opens a file or folder on this machine in the desktop's file manager or viewer.</summary>
    Task OpenPathAsync(string path);
}
