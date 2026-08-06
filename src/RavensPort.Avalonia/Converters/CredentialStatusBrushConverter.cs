using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;
using RavensPort.UI.ViewModels;

// WinForms is in scope for the tray, and it has a Brushes of its own.
using Brushes = Avalonia.Media.Brushes;

namespace RavensPort.Converters;

/// <summary>
/// Turns a <see cref="CredentialStatusKind"/> into the palette brush that says it.
///
/// Here rather than on the view model, which is the point of the enum: the row reports what is
/// true about a credential, and the theme decides what that looks like. Resolved out of the
/// application's resources so the colours come from the same dictionary as everything else on the
/// page — a literal colour here would be the one thing that did not change when the theme did.
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

        return Application.Current?.TryFindResource(key, out var brush) is true && brush is IBrush found
            ? found
            : Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
