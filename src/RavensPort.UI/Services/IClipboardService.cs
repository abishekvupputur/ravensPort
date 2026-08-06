namespace RavensPort.UI.Services;

/// <summary>
/// Puts text on the system clipboard.
///
/// Asynchronous because Avalonia's clipboard is, and there is no synchronous door into it — the
/// call goes through the windowing layer. WPF's is synchronous, so its implementation completes
/// before it returns; writing the callers against a Task now means they do not have to be rewritten
/// when the implementation changes underneath them.
/// </summary>
public interface IClipboardService
{
    /// <summary>
    /// Throws on failure rather than reporting it. The clipboard is genuinely flaky — another
    /// process can hold it open — and each caller already has somewhere sensible to put the
    /// message, next to the value the user was trying to copy.
    /// </summary>
    Task SetTextAsync(string text);
}
