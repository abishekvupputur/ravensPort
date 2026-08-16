using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using ComboBox = System.Windows.Controls.ComboBox;
using DataGrid = System.Windows.Controls.DataGrid;
using TextBoxBase = System.Windows.Controls.Primitives.TextBoxBase;

namespace RavensPort.App.Helpers;

/// <summary>
/// Makes a second click on an already-selected row deselect it, so whatever that selection
/// reveals — inline row details, or a card bound to "something is selected" — can be put away
/// again with the same gesture that opened it.
///
/// A single-selection DataGrid keeps exactly one row selected once anything has been picked, so
/// there was no closing gesture at all: an editor opened by a click stayed open for the rest of
/// the session, and the page kept its full height whether or not the user still wanted it.
///
/// Usage: &lt;DataGrid helpers:RowSelectionToggle.Enable="True"&gt;
/// </summary>
public static class RowSelectionToggle
{
    public static readonly DependencyProperty EnableProperty = DependencyProperty.RegisterAttached(
        "Enable", typeof(bool), typeof(RowSelectionToggle), new PropertyMetadata(false, OnEnableChanged));

    public static void SetEnable(DependencyObject element, bool value) => element.SetValue(EnableProperty, value);
    public static bool GetEnable(DependencyObject element) => (bool)element.GetValue(EnableProperty);

    private static void OnEnableChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not DataGrid grid) return;

        if ((bool)e.NewValue)
        {
            grid.PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
        }
        else
        {
            grid.PreviewMouseLeftButtonDown -= OnPreviewMouseLeftButtonDown;
        }
    }

    private static void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.Handled || sender is not DataGrid grid) return;

        var current = e.OriginalSource as DependencyObject;

        while (current is not null and not DataGridRow)
        {
            // Cells carry controls that do their own thing with a click — the Delete button, the
            // enabled checkbox, the selectable endpoint URL. Collapsing the row instead would
            // swallow the click that was meant for them.
            if (current is ButtonBase or TextBoxBase or ComboBox) return;

            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        if (current is not DataGridRow { IsSelected: true }) return;

        // Handled, or the grid's own handler puts the selection straight back and the panel never
        // closes.
        grid.SelectedItem = null;
        e.Handled = true;
    }
}
