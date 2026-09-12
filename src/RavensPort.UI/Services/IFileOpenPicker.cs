namespace RavensPort.UI.Services;

/// <summary>
/// Asking the user which file to read.
///
/// The mirror of <see cref="IFileSavePicker"/>, and behind the interface for the same reason: a
/// path typed into a text box carries no permission to read it on the platforms that sandbox one,
/// and the desktop's own dialog is what grants it.
///
/// Its only caller imports an API to MCP manifest, which is why the result is the file's text
/// rather than its path — nothing downstream wants the path, and returning one would make every
/// caller repeat the read and its error handling.
/// </summary>
public interface IFileOpenPicker
{
    /// <summary>
    /// Shows the dialog and returns what was chosen, or null if the user cancelled.
    ///
    /// A file the app cannot read — on a provider it has no local path for — is a cancellation
    /// too: there is nothing the caller could do about it except ask for another.
    /// </summary>
    /// <param name="title">The dialog's own title.</param>
    /// <param name="extension">Extension without the dot, e.g. "json".</param>
    /// <param name="filterName">What to call that extension in the type list, e.g. "Manifest".</param>
    Task<PickedFile?> PickFileAsync(string title, string extension, string filterName);
}

/// <summary>
/// One chosen file: what it is called, and what is in it.
///
/// The name travels alongside the text because the one caller records where a manifest came from,
/// and a manifest that says "pasted" when it came from a file is a small lie that costs somebody
/// a re-import later.
/// </summary>
/// <param name="Name">File name only, no directory.</param>
/// <param name="Text">The whole file, read as text.</param>
public sealed record PickedFile(string Name, string Text);
