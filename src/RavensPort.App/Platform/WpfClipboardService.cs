using RavensPort.UI.Services;

namespace RavensPort.App.Platform;

/// <summary>
/// <see cref="IClipboardService"/> over the WPF clipboard, which is synchronous — so this completes
/// before it returns and the Task is a formality the interface's other implementation needs.
/// </summary>
internal sealed class WpfClipboardService : IClipboardService
{
    public Task SetTextAsync(string text)
    {
        System.Windows.Clipboard.SetText(text);
        return Task.CompletedTask;
    }
}
