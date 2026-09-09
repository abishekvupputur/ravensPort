using Microsoft.Extensions.DependencyInjection;
using RavensPort.Core.Storage;
using RavensPort.Core.Vault;
using RavensPort.UI.ViewModels;

namespace RavensPort.UI.Tests;

/// <summary>
/// The window the product actually opens, with all five tabs wired the way the container wires
/// them.
///
/// Every other test in this suite hosts one view at a time, which is the right shape for asking
/// what a tab does. This one asks the question those cannot: does the shell hold together —
/// resolving MainWindow means resolving the six view models it takes, applying every tab's template
/// at once, and running the status line that summarises all of them.
/// </summary>
public class ShellUiTests
{
    [Fact]
    public Task TheShellOpensWithEveryTabWiredAndTheStatusLineLive() => UiSession.RunAsync(async () =>
    {
        await using var harness = await SingleUseHarness.StartAsync();
        await harness.StartSingleUseThroughTheUiAsync();

        var window = UiDriver.ShowWindow(harness.Services.GetRequiredService<MainWindow>());

        // The title carries the build number, which is the one place a user can read it without
        // opening the exe's properties.
        Assert.StartsWith("RavensPort v", window.Title);

        var shell = harness.Services.GetRequiredService<MainWindowViewModel>();
        var status = harness.Services.GetRequiredService<VaultStatusViewModel>();

        // The status line has to have noticed the single-use session the harness opened.
        await UiDriver.UntilAsync(
            () => status.Headline.Length > 0,
            () => $"the status line to say something (headline='{status.Headline}')");

        // Degraded means "there are changes the vault has not taken yet", and the harness itself
        // makes one when it writes the bound port back — so the honest assertion is that it
        // settles, not that it was never true.
        await UiDriver.UntilAsync(
            () => !status.IsDegraded,
            () => $"the queue to drain (pending={status.HasPendingChanges}, state={status.State})");

        Assert.NotNull(shell);
    });

    /// <summary>
    /// The status line's own buttons: re-read the vault, and dismiss whatever it is telling you.
    ///
    /// Reload is the one worth driving through the shell rather than the view model — it is offered
    /// precisely when someone suspects the tabs are stale, so "it runs and the tabs still hold what
    /// the vault holds" is the whole of what it promises.
    /// </summary>
    [Fact]
    public Task ReloadingFromTheVaultKeepsWhatTheVaultHolds() => UiSession.RunAsync(async () =>
    {
        await using var harness = await SingleUseHarness.StartAsync();
        await harness.StartSingleUseThroughTheUiAsync();

        UiDriver.ShowWindow(harness.Services.GetRequiredService<MainWindow>());

        await ApprovalSeeding.SeedCredentialsAsync(harness);

        var store = harness.Services.GetRequiredService<ConfigStoreCache>();
        var before = store.Current.Credentials.Count;
        Assert.Equal(2, before);

        var status = harness.Services.GetRequiredService<VaultStatusViewModel>();

        // Waited for on purpose, and it is the point of the test rather than a delay. Reload takes
        // the vault’s word for what the configuration is; run it while a write is still queued and
        // the vault has not heard about those credentials yet, so it answers with the store as it
        // was and the pending ones are gone. The status line says so — that is what "degraded"
        // means — and a person watching it would wait for the same thing.
        await UiDriver.UntilAsync(
            () => !status.HasPendingChanges,
            () => $"the queue to drain before reloading (state={status.State})");

        await UiDriver.RunOnUiAsync(() => status.ReloadFromVaultCommand.Execute(null));
        await UiDriver.UntilAsync(
            () => !status.IsSyncingNow, () => $"the reload to finish (state={status.State})");

        Assert.Equal(before, store.Current.Credentials.Count);

        // Whatever it had to say, dismissing it is how the line goes quiet.
        await UiDriver.RunOnUiAsync(() => status.DismissNoticeCommand.Execute(null));
        Assert.False(status.HasNotice);
    });

    /// <summary>
    /// A single-use session reports itself as one, everywhere it is asked.
    ///
    /// Three view models answer this question independently — the gate holds the truth, Settings
    /// decides which controls to grey out, and the status line decides what to say — so a session
    /// that only some of them recognise is the bug worth pinning.
    /// </summary>
    [Fact]
    public Task EveryViewModelAgreesTheSessionIsSingleUse() => UiSession.RunAsync(async () =>
    {
        await using var harness = await SingleUseHarness.StartAsync();
        await harness.StartSingleUseThroughTheUiAsync();

        UiDriver.ShowWindow(harness.Services.GetRequiredService<MainWindow>());

        var gate = harness.Services.GetRequiredService<VaultGateService>();
        var settings = harness.Services.GetRequiredService<SettingsViewModel>();
        var status = harness.Services.GetRequiredService<VaultStatusViewModel>();

        Assert.True(gate.IsSingleUse);

        await UiDriver.UntilAsync(() => settings.IsSingleUse, "Settings to notice the session");

        // There is no vault to sync with, so the button that would talk to one refuses to run.
        Assert.False(settings.SyncNowCommand.CanExecute(null),
            "Sync now must not be offered in a session with no vault behind it");
        Assert.False(status.IsWaitingForUnlock, "nothing is waiting to be unlocked in single use");
    });
}
