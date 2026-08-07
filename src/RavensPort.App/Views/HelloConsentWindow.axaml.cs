using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
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

    private HelloConsentWindow(
        string heading,
        string body,
        string detail,
        string confirmText,
        Func<Task> action,
        string vendorLogo = ProtonPassLogo)
        : this()
    {
        _action = action;

        HeadingText.Text = heading;
        BodyText.Text = body;
        DetailText.Text = detail;
        ConfirmButton.Content = confirmText;
        // Qualified: UseWindowsForms is on for the tray icon, so System.Drawing.Bitmap is in every
        // file's implicit usings and the bare name is ambiguous.
        VendorLogo.Source = new Avalonia.Media.Imaging.Bitmap(AssetLoader.Open(new Uri(vendorLogo)));
    }

    private const string ProtonPassLogo = "avares://RavensPort/Assets/proton-pass-logo.png";
    private const string OnePasswordLogo = "avares://RavensPort/Assets/onepassword-logo.png";

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

    /// <summary>
    /// Asks before unlocking the session.
    ///
    /// The wording differs by platform because the guarantee does, and saying the Windows sentence
    /// on Linux would be a straightforward lie — see the remarks on <see cref="RequestSetupAsync"/>.
    /// </summary>
    public static Task<bool> RequestUnlockAsync(Func<Task> unlockAsync) => ShowAsync(OperatingSystem.IsWindows()
        ? new HelloConsentWindow(
            "Unlock RavensPort",
            "RavensPort wants to ask Windows Hello to unlock its Proton Pass session on this PC, so the "
            + "proxy can start.",
            "Your session key is held in Windows Credential Manager, encrypted so that only a Windows Hello "
            + "gesture on this PC can decrypt it. The gesture is not a check RavensPort could skip — the key "
            + "it produces is what performs the decryption. Nothing is sent to Proton, nothing is read from "
            + "your vault by this step, and your Proton password is not involved.",
            "Unlock with Windows Hello",
            unlockAsync)
        : new HelloConsentWindow(
            "Unlock RavensPort",
            "RavensPort wants to read its Proton Pass session key back out of your system keyring, so "
            + "the proxy can start.",
            "The key is held in your desktop's keyring, encrypted on disk. Your keyring is normally "
            + "unlocked when you log in and stays unlocked, so this will usually not prompt you for "
            + "anything — and while it is unlocked, any program running as you could read the key the "
            + "same way. Nothing is sent to Proton by this step and your Proton password is not involved.",
            "Unlock from the keyring",
            unlockAsync));

    /// <summary>
    /// Asks before creating the session key — which happens before the Proton sign-in, not after
    /// it. Declining here means no key and no sign-in, which is the point: there is then nothing
    /// left on this machine that only a lost key could open.
    /// </summary>
    /// <remarks>
    /// Two sets of words for two different arrangements, and the difference is deliberate.
    ///
    /// On Windows the key is sealed to a Hello gesture that produces the key performing the
    /// decryption, so "only Windows Hello can bring it back" is literally true. On Linux the key
    /// goes into an ordinarily-unlocked keyring, and the honest version of that sentence is much
    /// weaker. Reusing the Windows copy would teach a Linux user to trust something that is not
    /// there — including the rule the rest of this window exists to teach, that a prompt appearing
    /// without RavensPort in front of it is one to refuse. There is no such prompt here.
    /// </remarks>
    public static Task<bool> RequestSetupAsync(Func<Task> prepareAsync) => ShowAsync(OperatingSystem.IsWindows()
        ? new HelloConsentWindow(
            "Protect this session with Windows Hello",
            "Before signing in to Proton Pass, RavensPort will create a session key and store it in "
            + "Windows Credential Manager, encrypted so that only Windows Hello can bring it back.",
            "The key is generated by RavensPort, never displayed, never typed, and never copied to the "
            + "clipboard — it goes straight into Credential Manager encrypted, and from there only into the "
            + "Proton Pass CLI. It is not your Proton password and Proton never receives it. Cancel and "
            + "nothing is created and no sign-in happens.",
            "Continue with Windows Hello",
            prepareAsync)
        : new HelloConsentWindow(
            "Keep this session in your keyring",
            "Before signing in to Proton Pass, RavensPort will create a session key and store it in "
            + "your desktop's keyring, so that you do not have to sign in through a browser again "
            + "after every restart.",
            "The key is generated by RavensPort, never displayed, never typed, and never copied to the "
            + "clipboard — it goes into the keyring and from there only into the Proton Pass CLI. It is "
            + "not your Proton password and Proton never receives it.\n\n"
            + "Worth knowing: the keyring encrypts it on disk, but it is unlocked when you log in and "
            + "stays unlocked, so any program running as you can read it back without prompting anyone. "
            + "This is weaker than the Windows version of RavensPort, which can bind the key to a "
            + "fingerprint or PIN. Cancel and nothing is created and no sign-in happens.",
            "Store it in the keyring",
            prepareAsync));

    /// <summary>
    /// Asks before keeping a 1Password service-account token between runs.
    ///
    /// Its own consent step, and deliberately not folded into a checkbox that saves silently. Up to
    /// this point the token has lived only in memory and the app has said so plainly; storing one is
    /// a change to that promise, and the user should be the one making it, having read what it does
    /// and does not protect.
    /// </summary>
    public static Task<bool> RequestTokenSaveAsync(Func<Task> protectAsync) => ShowAsync(new HelloConsentWindow(
        "Keep this token on this PC?",
        "RavensPort will store your 1Password service account token in Windows Credential Manager, "
        + "encrypted so that only a Windows Hello gesture on this PC can bring it back.",
        "The token is encrypted with a key that is not stored anywhere: it is derived from your Hello "
        + "signature each time, so the saved bytes open only to a gesture on this PC. It is never "
        + "written in plain text and never leaves this machine. Anyone who can already sign in as you "
        + "here could still ask for that gesture, so only do this on a PC you own and trust — and "
        + "remember the token stays valid until you rotate it in 1Password. Cancel and nothing is "
        + "saved; you will simply paste it again next time.",
        "Save with Windows Hello",
        protectAsync,
        OnePasswordLogo));

    /// <summary>
    /// Asks before bringing a saved token back at startup.
    /// </summary>
    public static Task<bool> RequestTokenUnlockAsync(Func<Task> unlockAsync) => ShowAsync(new HelloConsentWindow(
        "Unlock your 1Password token",
        "RavensPort wants to ask Windows Hello to unlock the saved 1Password service account token, "
        + "so it can connect without you pasting it again.",
        "The token is held in Windows Credential Manager, encrypted so that only a Windows Hello "
        + "gesture on this PC can decrypt it. The gesture is not a check RavensPort could skip — the "
        + "key it produces is what performs the decryption. Cancel and nothing is decrypted; you can "
        + "paste a token by hand instead, or forget the saved one on the Settings tab.",
        "Unlock with Windows Hello",
        unlockAsync,
        OnePasswordLogo));

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
