using Avalonia.Input.Platform;
using RavensPort.UI.Services;

namespace RavensPort.Platform;

/// <summary>
/// <see cref="IClipboardService"/> over Avalonia's clipboard, which hangs off a window rather than
/// being ambient — this is the reason the interface is asynchronous at all.
/// </summary>
internal sealed class AvaloniaClipboardService : IClipboardService
{
    public async Task SetTextAsync(string text)
    {
        var clipboard = MainWindowAccessor.Required.Clipboard
            ?? throw new InvalidOperationException("This desktop offers no clipboard.");

        await clipboard.SetTextAsync(text);
    }
}
