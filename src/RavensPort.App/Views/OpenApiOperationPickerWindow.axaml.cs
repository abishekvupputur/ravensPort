using Avalonia.Controls;
using RavensPort.Platform;
using RavensPort.UI.ViewModels;

namespace RavensPort.Views;

/// <summary>
/// Lets the user pick which operations of an imported OpenAPI document become tools, before
/// <see cref="ApiBridgeViewModel"/> fills the manifest editor.
/// </summary>
public partial class OpenApiOperationPickerWindow : Window
{
    /// <summary>True once the user pressed Import rather than closing the window.</summary>
    public bool Confirmed { get; private set; }

    /// <summary>Parameterless, for Avalonia's loader and the previewer.</summary>
    public OpenApiOperationPickerWindow()
    {
        InitializeComponent();

        Opened += (_, _) =>
        {
            if (OperatingSystem.IsWindows()) WindowHelper.ApplyDarkTitleBar(this);
        };
    }

    public OpenApiOperationPickerWindow(OpenApiOperationPickerViewModel viewModel)
        : this()
    {
        DataContext = viewModel;

        viewModel.CloseRequested += confirmed =>
        {
            Confirmed = confirmed;
            Close();
        };
    }
}
