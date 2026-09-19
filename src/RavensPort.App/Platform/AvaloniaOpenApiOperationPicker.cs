using RavensPort.Core.Models;
using RavensPort.UI.Services;
using RavensPort.UI.ViewModels;
using RavensPort.Views;

namespace RavensPort.Platform;

/// <summary>
/// <see cref="IOpenApiOperationPicker"/> over <see cref="OpenApiOperationPickerWindow"/>.
/// Owned by the main window when there is a visible one, matching the Hello consent window.
/// </summary>
internal sealed class AvaloniaOpenApiOperationPicker : IOpenApiOperationPicker
{
    public async Task<OpenApiImportResult?> PickAsync(OpenApiOperationPickerViewModel viewModel)
    {
        var window = new OpenApiOperationPickerWindow(viewModel);

        if (MainWindowAccessor.Current is { IsVisible: true } owner)
        {
            await window.ShowDialog(owner);
        }
        else
        {
            // A tray-resident app has no window to own a dialog while the main one is hidden, and
            // Avalonia cannot show a modal without an owner — so this one is simply a window.
            var closed = new TaskCompletionSource();
            window.Closed += (_, _) => closed.TrySetResult();
            window.Show();
            await closed.Task;
        }

        return window.Confirmed ? viewModel.Result : null;
    }
}
