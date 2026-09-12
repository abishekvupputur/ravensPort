using Avalonia.Platform.Storage;
using RavensPort.UI.Services;

namespace RavensPort.Platform;

/// <summary>
/// <see cref="IFileOpenPicker"/> over Avalonia's storage provider, which raises whichever open
/// dialog the desktop has — where the WPF implementation it replaces named Microsoft.Win32 directly.
/// </summary>
internal sealed class AvaloniaFileOpenPicker : IFileOpenPicker
{
    public async Task<PickedFile?> PickFileAsync(string title, string extension, string filterName)
    {
        var files = await MainWindowAccessor.Required.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(filterName) { Patterns = [$"*.{extension}"] },
                new FilePickerFileType("All files") { Patterns = ["*"] }
            ]
        });

        if (files.Count == 0) return null;

        var file = files[0];

        // Read through the storage provider rather than by path, so a file the platform hands over
        // without a local path — a sandboxed or remote one — still works.
        await using var stream = await file.OpenReadAsync();
        using var reader = new StreamReader(stream);

        return new PickedFile(file.Name, await reader.ReadToEndAsync());
    }
}
