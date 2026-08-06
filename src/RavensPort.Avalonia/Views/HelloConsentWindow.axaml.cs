using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using RavensPort.Core.Vault;
using RavensPort.Platform;

namespace RavensPort.Views;

/// <summary>
/// The consent step in front of every Windows Hello prompt RavensPort raises. See the comment in
/// the XAML for why it has no exceptions.
/// </summary>
public partial class HelloConsentWindow : Window
{
    /// <summary>
    /// The Hello-backed operation. It must signal failure by throwing — a method that catches its
    /// own errors and returns false reports success here, which is how a session key that was never
    /// stored once looked to the user like one that was.
    /// </summary>
    private readonly Func<Task>? _action;

    /// <summary>True once the Hello-backed operation actually succeeded.</summary>
    public bool Confirmed { get; private set; }

    /// <summary>Parameterless, for Avalonia's loader and the previewer.</summary>
    public HelloConsentWindow()
    {
        InitializeComponent();

        // Re-anchored on every size change, not just once at load. The window is SizeToContent, so
        // it grows when Report() reveals the status line — anchored only at startup, the bottom
        // edge would then push down past the taskbar and take the buttons with it.
        SizeChanged += (_, _) => AnchorAboveTray();
        Opened += (_, _) => AnchorAboveTray();
    }

    private HelloConsentWindow(string heading, string body, string detail, string confirmText, Func<Task> action)
        : this()
    {
        _action = action;

        HeadingText.Text = heading;
        BodyText.Text = body;
        DetailText.Text = detail;
        ConfirmButton.Content = confirmText;
    }

    /// <summary>
    /// The caption is the app's own, so dragging it has to be wired by hand — there is no system
    /// title bar to do it.
    /// </summary>
    private void Caption_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
    }

    /// <summary>
    /// Puts the window in the bottom-right corner, above the tray.
    ///
    /// Centred, it landed exactly on top of the Windows Hello prompt it exists to explain — which
    /// defeats the point of showing it at all, since the user cannot read what they are approving
    /// while approving it. Down here both are visible at once, and it sits beside the tray icon
    /// that represents the app doing the asking.
    ///
    /// The working area rather than the screen bounds, so this lands above the taskbar wherever the
    /// user keeps it — including left or top, where a hard-coded corner would be wrong. Avalonia
    /// reports it in physical pixels and positions windows in them too, while Width and Height are
    /// device-independent, so the size is scaled across; WPF's WorkArea needed no such conversion.
    /// </summary>
    private void AnchorAboveTray()
    {
        const double margin = 12;

        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null) return;

        var scale = screen.Scaling;
        var work = screen.WorkingArea;

        // Bounds are 0 until the first layout pass; Width is fixed in XAML, so it is the reliable
        // one to fall back on before then.
        var width = (int)((Bounds.Width > 0 ? Bounds.Width : Width) * scale);
        var height = (int)((Bounds.Height > 0 ? Bounds.Height : MinHeight) * scale);
        var gap = (int)(margin * scale);

        // Clamped to the top of the work area: a window taller than the screen would otherwise be
        // positioned at a negative Y, hiding its heading off the top edge rather than its buttons
        // off the bottom.
        Position = new PixelPoint(
            work.Right - width - gap,
            Math.Max(work.Y, work.Bottom - height - gap));
    }

    /// <summary>Asks before unlocking the session with Hello.</summary>
    public static Task<bool> RequestUnlockAsync(Func<Task> unlockAsync) => ShowAsync(new HelloConsentWindow(
        "Unlock RavensPort",
        "RavensPort wants to ask Windows Hello to unlock its Proton Pass session on this PC, so the "
        + "proxy can start.",
        "Your session key is held in Windows Credential Manager, encrypted so that only a Windows Hello "
        + "gesture on this PC can decrypt it. The gesture is not a check RavensPort could skip — the key "
        + "it produces is what performs the decryption. Nothing is sent to Proton, nothing is read from "
        + "your vault by this step, and your Proton password is not involved.",
        "Unlock with Windows Hello",
        unlockAsync));

    /// <summary>
    /// Asks before creating the session key — which happens before the Proton sign-in, not after
    /// it. Declining here means no key and no sign-in, which is the point: there is then nothing
    /// left on this PC that only a lost key could open.
    /// </summary>
    public static Task<bool> RequestSetupAsync(Func<Task> prepareAsync) => ShowAsync(new HelloConsentWindow(
        "Protect this session with Windows Hello",
        "Before signing in to Proton Pass, RavensPort will create a session key and store it in "
        + "Windows Credential Manager, encrypted so that only Windows Hello can bring it back.",
        "The key is generated by RavensPort, never displayed, never typed, and never copied to the "
        + "clipboard — it goes straight into Credential Manager encrypted, and from there only into the "
        + "Proton Pass CLI. It is not your Proton password and Proton never receives it. Cancel and "
        + "nothing is created and no sign-in happens.",
        "Continue with Windows Hello",
        prepareAsync));

    private static async Task<bool> ShowAsync(HelloConsentWindow window)
    {
        // Owned by the main window when there is a visible one, so it cannot be lost behind it. At
        // startup there is none — which is the case this window exists to cover — and Avalonia's
        // ShowDialog requires an owner, so that path shows it unowned and waits for the close.
        if (MainWindowAccessor.Current is { IsVisible: true } owner && !ReferenceEquals(owner, window))
        {
            await window.ShowDialog(owner);
            return window.Confirmed;
        }

        var closed = new TaskCompletionSource();
        window.Closed += (_, _) => closed.TrySetResult();

        window.Show();
        window.Activate();
        await closed.Task;

        return window.Confirmed;
    }

    private async void ConfirmButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_action is null) return;

        ConfirmButton.IsEnabled = false;
        CancelButton.IsEnabled = false;

        Report("Waiting for Windows Hello…", isError: false);

        try
        {
            await _action();

            Confirmed = true;
            Close();
        }
        catch (VaultCliException ex)
        {
            // Cancelled at the Hello prompt, locked out after too many attempts, or a stored key
            // that no longer opens. All of them leave pasting the key as the way through, and the
            // message already says so — so this window stays open to be retried or dismissed.
            Report(ex.Message, isError: true);
        }
        catch (Exception ex)
        {
            Report($"Windows Hello failed: {ex.Message}", isError: true);
        }
        finally
        {
            ConfirmButton.IsEnabled = true;
            CancelButton.IsEnabled = true;
        }
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e) => Close();

    private void Report(string message, bool isError)
    {
        StatusText.Text = message;
        StatusText.IsVisible = true;
        StatusText.Foreground = this.TryFindResource(isError ? "ErrorBrush" : "MutedTextBrush", out var brush)
            && brush is IBrush found
            ? found
            : StatusText.Foreground;
    }
}
