using RavensPort.Core.Models;
using RavensPort.UI.Services;
using RavensPort.UI.ViewModels;

namespace RavensPort.Core.Tests.App;

/// <summary>
/// Every seam a bridge view model reaches the desktop through, answered as if the person walked
/// away: nothing copied, every dialog cancelled. These tests are about what the view model does with
/// its own state, so none of them should be asking the desktop anything.
/// </summary>
internal sealed class NoDesktop : IClipboardService, IFileOpenPicker, IFileSavePicker, IOpenApiOperationPicker
{
    public Task SetTextAsync(string text) => Task.CompletedTask;

    public Task<PickedFile?> PickFileAsync(string title, IReadOnlyList<string> extensions, string filterName) =>
        Task.FromResult<PickedFile?>(null);

    public Task<string?> PickSavePathAsync(
        string title, string suggestedFileName, string extension, string filterName) =>
        Task.FromResult<string?>(null);

    public Task<OpenApiImportResult?> PickAsync(OpenApiOperationPickerViewModel viewModel) =>
        Task.FromResult<OpenApiImportResult?>(null);
}
