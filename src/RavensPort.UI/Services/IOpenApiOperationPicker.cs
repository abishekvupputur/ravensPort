using RavensPort.Core.Models;
using RavensPort.UI.ViewModels;

namespace RavensPort.UI.Services;

/// <summary>
/// Asking the user which operations of an imported OpenAPI document become tools.
///
/// A modal dialog is the one thing a view model cannot raise on its own, and the picker used to be
/// reached by naming the WPF window from the view model — which is precisely the reference this
/// project exists to forbid. The dialog is the view; the view model it shows is the contract.
/// </summary>
public interface IOpenApiOperationPicker
{
    /// <summary>
    /// Shows the picker and returns the built result, or null if the user cancelled.
    /// </summary>
    Task<OpenApiImportResult?> PickAsync(OpenApiOperationPickerViewModel viewModel);
}
