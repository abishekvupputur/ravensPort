using System.Windows;
using RavensPort.App.ViewModels;
using RavensPort.Core.Models;

// UseWindowsForms is on for the tray icon, so both frameworks' Application types are in scope.
using Application = System.Windows.Application;

namespace RavensPort.App.Views;

/// <summary>
/// Lets the user pick which operations of an imported OpenAPI document become tools, before
/// <see cref="ApiBridgeViewModel.ImportOpenApi"/> fills the manifest editor.
/// </summary>
public partial class OpenApiOperationPickerWindow : Window
{
    private readonly OpenApiOperationPickerViewModel _viewModel;

    public OpenApiOperationPickerWindow(OpenApiOperationPickerViewModel viewModel)
    {
        _viewModel = viewModel;

        InitializeComponent();
        DataContext = viewModel;

        viewModel.CloseRequested += confirmed =>
        {
            DialogResult = confirmed;
            Close();
        };
    }

    /// <summary>
    /// Shows the picker and returns the built result, or null if the user cancelled.
    /// Owned by the main window when there is a visible one, matching <see cref="HelloConsentWindow"/>.
    /// </summary>
    public static OpenApiImportResult? Show(OpenApiOperationPickerViewModel viewModel)
    {
        var window = new OpenApiOperationPickerWindow(viewModel);

        var owner = Application.Current?.MainWindow;
        if (owner is { IsVisible: true } && !ReferenceEquals(owner, window)) window.Owner = owner;

        return window.ShowDialog() == true ? viewModel.Result : null;
    }
}
