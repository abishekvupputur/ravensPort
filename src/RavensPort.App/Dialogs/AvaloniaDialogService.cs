using Avalonia.Threading;

namespace RavensPort.Dialogs;

/// <summary>
/// <see cref="IDialogService"/> over <see cref="MessageWindow"/>.
///
/// Every call is marshalled onto the UI thread. The callers are not all on it: the unhandled- and
/// unobserved-exception handlers report from wherever the fault happened, and a window constructed
/// off the UI thread fails in Avalonia rather than being quietly wrong. InvokeAsync unwraps an
/// async delegate for us, so awaiting these waits for the user's answer rather than for the window
/// to have been created.
/// </summary>
internal sealed class AvaloniaDialogService : IDialogService
{
    public Task ShowMessageAsync(string title, string message, DialogSeverity severity) =>
        Dispatcher.UIThread.InvokeAsync(() =>
            MessageWindow.AskAsync(title, Heading(severity), message, "OK", cancelText: null));

    public Task<bool> ConfirmAsync(
        string title, string message, string confirmText, string cancelText, DialogSeverity severity) =>
        Dispatcher.UIThread.InvokeAsync(() =>
            MessageWindow.AskAsync(title, Heading(severity), message, confirmText, cancelText));

    /// <summary>
    /// The severity, said rather than drawn. An icon would need three more assets and would say
    /// less: these dialogs are rare and each one is about a specific thing that has gone wrong.
    /// </summary>
    private static string Heading(DialogSeverity severity) => severity switch
    {
        DialogSeverity.Error => "Something went wrong",
        DialogSeverity.Warning => "Are you sure?",
        _ => "RavensPort",
    };
}
