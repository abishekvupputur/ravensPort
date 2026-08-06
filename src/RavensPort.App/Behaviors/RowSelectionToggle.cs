using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

// Named here rather than project-wide, unlike Application and UserControl: these four are the only
// place any C# in this project says them, and the tray has a legitimate claim on the WinForms ones.
using Button = Avalonia.Controls.Button;
using CheckBox = Avalonia.Controls.CheckBox;
using ComboBox = Avalonia.Controls.ComboBox;
using TextBox = Avalonia.Controls.TextBox;

namespace RavensPort.Behaviors;

/// <summary>
/// Makes a second click on an already-selected row deselect it, so whatever that selection
/// reveals — the row-details editor — can be put away again with the same gesture that opened it.
///
/// A single-selection DataGrid keeps exactly one row selected once anything has been picked, so
/// there was no closing gesture at all: an editor opened by a click stayed open for the rest of
/// the session, and the page kept its full height whether or not the user still wanted it. The
/// hint above the grid promises "Click it again to collapse", so this is what keeps that true.
///
/// Usage: &lt;DataGrid behaviors:RowSelectionToggle.Enable="True"&gt;
/// </summary>
public static class RowSelectionToggle
{
    public static readonly AttachedProperty<bool> EnableProperty =
        AvaloniaProperty.RegisterAttached<DataGrid, bool>("Enable", typeof(RowSelectionToggle));

    public static void SetEnable(DataGrid element, bool value) => element.SetValue(EnableProperty, value);
    public static bool GetEnable(DataGrid element) => element.GetValue(EnableProperty);

    static RowSelectionToggle() => EnableProperty.Changed.AddClassHandler<DataGrid>(OnEnableChanged);

    private static void OnEnableChanged(DataGrid grid, AvaloniaPropertyChangedEventArgs args)
    {
        // Tunnelling, because Avalonia has no Preview* events: the WPF original used
        // PreviewMouseLeftButtonDown to get in before the grid's own handler set the selection
        // straight back. RoutingStrategies.Tunnel is the same position in the event's journey.
        if (args.GetNewValue<bool>())
        {
            grid.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
        }
        else
        {
            grid.RemoveHandler(InputElement.PointerPressedEvent, OnPointerPressed);
        }
    }

    private static void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Handled || sender is not DataGrid grid) return;
        if (!e.GetCurrentPoint(grid).Properties.IsLeftButtonPressed) return;

        var current = e.Source as Visual;

        while (current is not null and not DataGridRow)
        {
            // Cells carry controls that do their own thing with a click — the Delete button, the
            // enabled checkbox, the selectable endpoint URL. Collapsing the row instead would
            // swallow the click that was meant for them.
            if (current is Button or TextBox or ComboBox or CheckBox or ToggleButton) return;

            current = current.GetVisualParent();
        }

        if (current is not DataGridRow { IsSelected: true }) return;

        // Handled, or the grid's own handler puts the selection straight back and the panel never
        // closes.
        grid.SelectedItem = null;
        e.Handled = true;
    }
}
