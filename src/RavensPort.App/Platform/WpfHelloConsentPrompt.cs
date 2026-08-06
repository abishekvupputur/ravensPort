using RavensPort.UI.Services;
using RavensPort.App.Views;

namespace RavensPort.App.Platform;

/// <summary>
/// <see cref="IHelloConsentPrompt"/> over <see cref="HelloConsentWindow"/>.
///
/// WPF's <c>ShowDialog()</c> blocks the calling thread until the window closes, so the answer is
/// already known by the time this returns and the Task is completed. That blocking is not
/// incidental — see the comment in <see cref="HelloConsentWindow"/> on why the whole sequence stays
/// on the UI thread — but it is also the part Avalonia cannot reproduce, which is what the async
/// signature is here to absorb.
/// </summary>
internal sealed class WpfHelloConsentPrompt : IHelloConsentPrompt
{
    public Task<bool> RequestUnlockAsync(Func<Task> unlockAsync) =>
        Task.FromResult(HelloConsentWindow.RequestUnlock(unlockAsync));

    public Task<bool> RequestSetupAsync(Func<Task> prepareAsync) =>
        Task.FromResult(HelloConsentWindow.RequestSetup(prepareAsync));

    public Task<bool> RequestTokenSaveAsync(Func<Task> protectAsync) =>
        Task.FromResult(HelloConsentWindow.RequestTokenSave(protectAsync));

    public Task<bool> RequestTokenUnlockAsync(Func<Task> unlockAsync) =>
        Task.FromResult(HelloConsentWindow.RequestTokenUnlock(unlockAsync));
}
