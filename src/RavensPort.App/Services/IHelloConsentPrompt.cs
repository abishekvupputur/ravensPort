namespace RavensPort.App.Services;

/// <summary>
/// The consent step in front of every Windows Hello prompt RavensPort raises.
///
/// Behind the interface sits a modal window that explains what is about to be asked for and why,
/// then runs the Hello-backed operation itself and reports how it went — so consent and the act it
/// consents to are one thing, and there is no window in which the user has agreed to something that
/// then quietly did not happen. The rule has no exceptions, including the buttons that already say
/// "Windows Hello" on them; a rule with an exception protects nobody.
///
/// Asynchronous throughout because Avalonia has no synchronous modal: <c>ShowDialog</c> returns a
/// Task and there is no blocking door into it. WPF's <c>ShowDialog()</c> does block, so its
/// implementation returns an already-completed task — the callers are written against the harder
/// contract so that the port does not have to unpick them.
///
/// Windows-only for now, and honestly named for it. When RavensPort grows a second platform, this
/// is the seam the answer arrives at: what stands in for Hello on macOS and Linux is a different
/// question for <see cref="RavensPort.Core.Vault.HelloKeyProtector"/> and not one this interface
/// prejudges.
/// </summary>
public interface IHelloConsentPrompt
{
    /// <summary>
    /// Asks before unlocking the session with Hello, then runs <paramref name="unlockAsync"/>.
    /// True only once that operation has actually succeeded — a decline, a cancelled gesture and a
    /// stored key that no longer opens all come back false.
    /// </summary>
    Task<bool> RequestUnlockAsync(Func<Task> unlockAsync);

    /// <summary>
    /// Asks before creating the session key, then runs <paramref name="prepareAsync"/>. This
    /// happens before the Proton sign-in, not after it: declining means no key and no sign-in,
    /// which is the point — there is then nothing left on this PC that only a lost key could open.
    /// </summary>
    Task<bool> RequestSetupAsync(Func<Task> prepareAsync);

    /// <summary>
    /// Asks before keeping a 1Password service-account token between runs, then runs
    /// <paramref name="protectAsync"/>. Its own step rather than a checkbox that saves silently:
    /// up to that point the token has lived in memory only, and storing one is a change to what the
    /// app has promised.
    /// </summary>
    Task<bool> RequestTokenSaveAsync(Func<Task> protectAsync);

    /// <summary>
    /// Asks before bringing a saved 1Password token back, then runs <paramref name="unlockAsync"/>.
    /// </summary>
    Task<bool> RequestTokenUnlockAsync(Func<Task> unlockAsync);
}
