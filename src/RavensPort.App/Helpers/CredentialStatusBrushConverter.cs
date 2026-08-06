using System.Globalization;
using System.Windows;
using System.Windows.Data;
using RavensPort.App.ViewModels;

// UseWindowsForms is on for the tray icon, so both frameworks' types are in scope under these names.
using Application = System.Windows.Application;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;

namespace RavensPort.App.Helpers;

/// <summary>
/// Turns a <see cref="CredentialStatusKind"/> into the palette brush that says it.
///
/// Here rather than on the view model, which is the point of the enum: the row reports what is
/// true about a credential, and the theme decides what that looks like. Resolved through
/// <see cref="Application.Current"/> so the brushes come from the same dictionary as everything
/// else on the page — a literal colour here would be the one thing that did not change when the
/// theme did.
/// </summary>
public sealed class CredentialStatusBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            CredentialStatusKind.Healthy => "SuccessBrush",
            CredentialStatusKind.Broken => "ErrorBrush",
            _ => "MutedTextBrush",
        };

        return Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
