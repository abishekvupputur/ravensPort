namespace RavensPort.Dialogs;

/// <summary>How loudly a message should present itself.</summary>
public enum DialogSeverity
{
    Information,
    Warning,
    Error,
}

/// <summary>
/// What <c>MessageBox.Show</c> was.
///
/// Avalonia has no message box, by design — it would have to be one of three native ones. So this
/// is the app's own, which is not a loss: the dialogs here carry the warning about losing unsaved
/// vault changes, and that one is worth having in the app's own type rather than a system grey box.
///
/// Everything is asynchronous because Avalonia has no synchronous modal at all. That reaches the
/// tray's Exit item, which used to ask this question on the way past and now awaits it.
/// </summary>
public interface IDialogService
{
    /// <summary>Tells the user something. Resolves when they dismiss it.</summary>
    Task ShowMessageAsync(string title, string message, DialogSeverity severity);

    /// <summary>
    /// Asks a question with a way out. True when the user chose to go ahead.
    ///
    /// Cancel is the default — every caller is about to destroy something, and the dialog exists
    /// because they might not have meant to.
    /// </summary>
    Task<bool> ConfirmAsync(string title, string message, string confirmText, string cancelText, DialogSeverity severity);
}
