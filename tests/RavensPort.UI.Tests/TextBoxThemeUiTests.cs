using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace RavensPort.UI.Tests;

/// <summary>
/// What a text box looks like once it is clicked, which is the state nobody screenshots until a
/// user does.
///
/// Fluent repaints a box's inner border on hover and focus from its own resources, which overrides
/// the Background the box was given — so the copyable snippets, which are transparent, borderless
/// and set in the accent colour, turned black when clicked. And the selection is painted in the
/// accent colour too, so selected accent text was gold on gold. Both need the real theme to show,
/// which is why they are asserted here rather than reasoned about from the XAML.
/// </summary>
public class TextBoxThemeUiTests
{
    private static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#EB990A"));

    private static TextBox Snippet() => new()
    {
        Text = "http://127.0.0.1:5559/oauth/callback",
        IsReadOnly = true,
        Background = Brushes.Transparent,
        BorderThickness = new Thickness(0),
        Foreground = Accent,
        Width = 400,
    };

    private static Border InnerBorder(TextBox box) =>
        box.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "PART_BorderElement");

    private static Color ColourOf(IBrush? brush) => (brush as ISolidColorBrush)?.Color ?? Colors.Magenta;

    [Fact]
    public Task AFocusedTransparentBoxStaysTransparent() => UiSession.RunAsync(() =>
    {
        var box = Snippet();
        var window = UiDriver.ShowWindow(new Window { Content = box, Width = 500, Height = 200 });

        box.Focus();
        Dispatcher.UIThread.RunJobs();

        Assert.True(box.IsFocused);
        Assert.Equal(Colors.Transparent, ColourOf(InnerBorder(box).Background));

        window.Close();
        return Task.CompletedTask;
    });

    [Fact]
    public Task AnOrdinaryBoxKeepsItsOwnBackgroundWhenFocused() => UiSession.RunAsync(() =>
    {
        // The other half: the fix hands back the box's own Background rather than forcing
        // transparency, so a box with a panel colour must still have that colour, not Fluent's.
        var box = new TextBox { Text = "name" };
        var window = UiDriver.ShowWindow(new Window { Content = box, Width = 500, Height = 200 });

        box.Focus();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(ColourOf(box.Background), ColourOf(InnerBorder(box).Background));

        window.Close();
        return Task.CompletedTask;
    });

    [Fact]
    public Task SelectedTextIsNotTheSelectionColour() => UiSession.RunAsync(() =>
    {
        var box = Snippet();
        var window = UiDriver.ShowWindow(new Window { Content = box, Width = 500, Height = 200 });

        // Unset is what it was: selected text then keeps its own colour, which for these boxes is
        // the selection's colour too. A brush has to be there at all before it can differ.
        Assert.IsAssignableFrom<ISolidColorBrush>(box.SelectionForegroundBrush);

        // Whatever colour the text is, the selection behind it must not be the same one.
        Assert.NotEqual(ColourOf(box.SelectionBrush), ColourOf(box.SelectionForegroundBrush));
        Assert.NotEqual(ColourOf(box.Foreground), ColourOf(box.SelectionForegroundBrush));

        window.Close();
        return Task.CompletedTask;
    });
}
