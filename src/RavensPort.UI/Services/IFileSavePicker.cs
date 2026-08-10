namespace RavensPort.UI.Services;

/// <summary>
/// Asking the user where to write a file.
///
/// Behind the interface is the desktop's own save dialog, which is the point: a path typed into a
/// text box would not carry the permission to write it on the platforms that sandbox one, and the
/// only caller — exporting the mTLS client certificate — has to put its file wherever the thing that
/// will call the proxy can read it.
/// </summary>
public interface IFileSavePicker
{
    /// <summary>
    /// Shows the dialog and returns the chosen path, or null if the user cancelled.
    ///
    /// Overwriting is confirmed by the dialog itself, so a returned path is one the user has agreed
    /// to write over.
    /// </summary>
    /// <param name="title">The dialog's own title.</param>
    /// <param name="suggestedFileName">Filled into the name box, extension included.</param>
    /// <param name="extension">Extension without the dot, appended if the user types none.</param>
    /// <param name="filterName">What to call that extension in the type list, e.g. "Certificate".</param>
    Task<string?> PickSavePathAsync(string title, string suggestedFileName, string extension, string filterName);
}
