using Avalonia.Platform.Storage;
using RavensPort.UI.Services;

namespace RavensPort.Platform;

/// <summary>
/// <see cref="IFileSavePicker"/> over Avalonia's storage provider, which raises whichever save
/// dialog the desktop has — where the WPF implementation it replaces named Microsoft.Win32 directly.
/// </summary>
internal sealed class AvaloniaFileSavePicker : IFileSavePicker
{
    public async Task<string?> PickSavePathAsync(
        string title,
        string suggestedFileName,
        string extension,
        string filterName)
    {
        var file = await MainWindowAccessor.Required.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedFileName,
            DefaultExtension = extension,
            ShowOverwritePrompt = true,
            SuggestedStartLocation = await MainWindowAccessor.Required.StorageProvider
                .TryGetWellKnownFolderAsync(WellKnownFolder.Desktop),
            FileTypeChoices =
            [
                new FilePickerFileType(filterName) { Patterns = [$"*.{extension}"] },
                new FilePickerFileType("All files") { Patterns = ["*"] }
            ]
        });

        // A file the app cannot address by path is one it cannot write with File.WriteAllBytes —
        // which is every non-local provider. Treated as a cancellation rather than reported as an
        // error, because there is nothing the user could do differently except pick somewhere else.
        return file?.TryGetLocalPath();
    }
}
