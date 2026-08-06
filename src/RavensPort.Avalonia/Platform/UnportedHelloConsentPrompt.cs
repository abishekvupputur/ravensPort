using RavensPort.UI.Services;

namespace RavensPort.Platform;

/// <summary>
/// Placeholder while the Avalonia port is in progress. The real consent window is a view, and the
/// views are being ported after the shell.
///
/// It throws rather than returning false. False would mean "the user declined", which is a lie the
/// setup page would then repeat back to them — and a consent prompt that silently reports refusal
/// is the exact failure this whole mechanism exists to make impossible. Throwing fails closed and
/// says why: the caller logs it and the Proton path stops, which is correct for a build where the
/// window does not exist yet.
///
/// The WPF app is still the one that ships, and its prompt is intact. Delete this class when
/// HelloConsentWindow lands.
/// </summary>
internal sealed class UnportedHelloConsentPrompt : IHelloConsentPrompt
{
    private const string Message =
        "The Windows Hello consent window has not been ported to Avalonia yet, and RavensPort will "
        + "not raise a Hello prompt without one. Use the WPF build for Proton Pass.";

    public Task<bool> RequestUnlockAsync(Func<Task> unlockAsync) => throw new NotSupportedException(Message);

    public Task<bool> RequestSetupAsync(Func<Task> prepareAsync) => throw new NotSupportedException(Message);
}
