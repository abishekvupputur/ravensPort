using RavensPort.UI.Services;
using RavensPort.Views;

namespace RavensPort.Platform;

/// <summary>
/// <see cref="IHelloConsentPrompt"/> over <see cref="HelloConsentWindow"/>.
///
/// A straight pass-through, unlike the WPF implementation it replaces: that one had to wrap a
/// blocking ShowDialog() in a completed Task to satisfy this interface. Avalonia's modal is
/// asynchronous to begin with, which is why the interface was written this way.
/// </summary>
internal sealed class AvaloniaHelloConsentPrompt : IHelloConsentPrompt
{
    public Task<bool> RequestUnlockAsync(Func<Task> unlockAsync) =>
        HelloConsentWindow.RequestUnlockAsync(unlockAsync);

    public Task<bool> RequestSetupAsync(Func<Task> prepareAsync) =>
        HelloConsentWindow.RequestSetupAsync(prepareAsync);

    public Task<bool> RequestTokenSaveAsync(Func<Task> protectAsync) =>
        HelloConsentWindow.RequestTokenSaveAsync(protectAsync);

    public Task<bool> RequestTokenUnlockAsync(Func<Task> unlockAsync) =>
        HelloConsentWindow.RequestTokenUnlockAsync(unlockAsync);
}
