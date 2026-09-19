using Avalonia.Controls;
using Avalonia.Interactivity;
using RavensPort.Platform;

namespace RavensPort.Dialogs;

public partial class MessageWindow : Window
{
    private bool _confirmed;

    public MessageWindow() => InitializeComponent();

    /// <summary>
    /// Shows the window and resolves to what the user chose.
    ///
    /// Owned when there is a window to own it, so it centres on the app and cannot be lost behind
    /// it. There is often not: the startup-failure and already-running messages both happen before
    /// any window exists, and those are exactly the ones that must still be seen. So an unowned
    /// dialog falls back to a plain Show, with the close awaited by hand — <see cref="ShowDialog"/>
    /// requires an owner and would throw on the paths that need it most.
    /// </summary>
    private async Task<bool> ShowAndWaitAsync()
    {
        if (MainWindowAccessor.Current is { IsVisible: true } owner && !ReferenceEquals(owner, this))
        {
            await ShowDialog(owner);
            return _confirmed;
        }

        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var closed = new TaskCompletionSource();
        Closed += (_, _) => closed.TrySetResult();

        Show();
        await closed.Task;

        return _confirmed;
    }

    internal static Task<bool> AskAsync(
        string title, string heading, string body, string confirmText, string? cancelText)
    {
        var window = new MessageWindow();

        window.Title = title;
        window.HeadingText.Text = heading;
        window.BodyText.Text = body;
        window.ConfirmButton.Content = confirmText;

        // A message rather than a question: one button, and it is the only way out, so it may be
        // the default. The confirmations deliberately have no default at all.
        if (cancelText is null)
        {
            window.CancelButton.IsVisible = false;
            window.ConfirmButton.IsDefault = true;
        }
        else
        {
            window.CancelButton.Content = cancelText;
        }

        return window.ShowAndWaitAsync();
    }

    private void ConfirmButton_Click(object? sender, RoutedEventArgs e)
    {
        _confirmed = true;
        Close();
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e) => Close();
}
