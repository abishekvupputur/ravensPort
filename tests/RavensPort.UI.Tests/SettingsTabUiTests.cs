using Microsoft.Extensions.DependencyInjection;
using RavensPort.Core.Diagnostics;
using RavensPort.Core.Storage;
using RavensPort.UI.Services;
using RavensPort.UI.ViewModels;
using RavensPort.Views;

namespace RavensPort.UI.Tests;

/// <summary>
/// The Settings tab: the listen port, the log buttons, and the two confirmations.
///
/// Everything here writes to the vault or to disk, so each test asks what actually changed rather
/// than what the form said. The log buttons are the exception — they hand a path to the desktop,
/// which the harness records instead of opening, because a test run that opened four editor windows
/// on a developer's machine would be a bad neighbour and a hang on a runner with no editor at all.
/// </summary>
public class SettingsTabUiTests
{
    /// <summary>
    /// Changing the port, which is the one setting that cannot be applied where it is typed: the
    /// listener is already bound, so the vault takes the number and the next start uses it.
    /// </summary>
    [Fact]
    public Task SavingANewPortWritesItToTheVault() => UiSession.RunAsync(async () =>
    {
        await using var harness = await SingleUseHarness.StartAsync();
        await harness.StartSingleUseThroughTheUiAsync();

        var (view, store, vm) = Open(harness);

        await UiDriver.TypeAsync(view, AutomationIds.ListenPort, "5610");
        await UiDriver.ClickAsync(view, AutomationIds.SavePort);

        await UiDriver.UntilAsync(
            () => store.Current.Settings.ListenPort == 5610,
            () => $"the port to be saved — the tab says: {vm.StatusMessage}");
    });

    /// <summary>
    /// A port the machine cannot give out is refused where it is typed, not at the next start.
    ///
    /// Both ends of the range and a word: the number is read from a text box, so "not a number" is
    /// as ordinary an input as 70000.
    /// </summary>
    [Fact]
    public Task AnImpossiblePortIsRefusedAndTheOldOneKept() => UiSession.RunAsync(async () =>
    {
        await using var harness = await SingleUseHarness.StartAsync();
        await harness.StartSingleUseThroughTheUiAsync();

        var (view, store, vm) = Open(harness);
        var original = store.Current.Settings.ListenPort;

        foreach (var bad in new[] { "0", "70000", "not-a-port", "" })
        {
            await UiDriver.TypeAsync(view, AutomationIds.ListenPort, bad);
            await UiDriver.ClickAsync(view, AutomationIds.SavePort);
            await UiDriver.PumpAsync();

            Assert.Equal(original, store.Current.Settings.ListenPort);
            Assert.False(string.IsNullOrWhiteSpace(vm.StatusMessage),
                $"'{bad}' was refused without saying why");
        }
    });

    /// <summary>
    /// The log buttons. Three open something, one deletes.
    ///
    /// What is asserted is that each hands the desktop a path that exists — a button that opened
    /// nothing, or opened a file that had not been written yet, would look identical from the tab.
    /// </summary>
    [Fact]
    public Task TheLogButtonsOpenRealPaths() => UiSession.RunAsync(async () =>
    {
        await using var harness = await SingleUseHarness.StartAsync();
        await harness.StartSingleUseThroughTheUiAsync();

        var (view, _, _) = Open(harness);
        var launcher = (RecordingLauncher)harness.Services.GetRequiredService<IPlatformLauncher>();
        var log = harness.Services.GetRequiredService<ActivityLog>();

        // Something to open: the log file is created on first write, not at startup.
        log.Log("SETTINGS test wrote this line so the activity log exists");

        launcher.Opened.Clear();

        await UiDriver.ClickAsync(view, AutomationIds.OpenActivityLog);
        await UiDriver.ClickAsync(view, AutomationIds.OpenLogFolder);

        await UiDriver.UntilAsync(
            () => launcher.Opened.Count >= 2,
            () => $"both buttons to hand over a path (so far: {string.Join(", ", launcher.Opened)})");

        Assert.All(launcher.Opened, p =>
            Assert.True(File.Exists(p) || Directory.Exists(p), $"'{p}' does not exist"));

        // Pruning is destructive and has no confirmation, because what it deletes is the log's own
        // history and the current file survives.
        await UiDriver.ClickAsync(view, AutomationIds.PruneLogs);
        await UiDriver.PumpAsync();

        Assert.True(Directory.Exists(log.LogDirectory), "pruning must not remove the log directory");
    });

    /// <summary>
    /// Backing out of a disconnect, which is the half of that flow nothing else covers.
    ///
    /// The destructive half is covered by SingleUseUiTests; this is the one that must leave the
    /// session exactly as it was, because a confirmation that discards anything on "Cancel" is
    /// worse than no confirmation.
    /// </summary>
    [Fact]
    public Task CancellingADisconnectLeavesTheSessionAlone() => UiSession.RunAsync(async () =>
    {
        await using var harness = await SingleUseHarness.StartAsync();
        await harness.StartSingleUseThroughTheUiAsync();

        await ApprovalSeeding.SeedCredentialsAsync(harness);

        var (view, store, vm) = Open(harness);

        await UiDriver.UntilAsync(() => vm.IsSingleUse, "Settings to notice the session");

        await UiDriver.ClickAsync(view, AutomationIds.Disconnect);
        await UiDriver.UntilAsync(() => vm.IsConfirmingDisconnect, "the confirmation to appear");

        await UiDriver.RunOnUiAsync(() => vm.CancelDisconnectCommand.Execute(null));
        await UiDriver.UntilAsync(() => !vm.IsConfirmingDisconnect, "the confirmation to close");

        Assert.Equal(2, store.Current.Credentials.Count);
        Assert.True(harness.Services.GetRequiredService<RavensPort.Core.Vault.VaultGateService>().IsSingleUse);
    });

    private static (Avalonia.Controls.Window View, ConfigStoreCache Store, SettingsViewModel Vm) Open(
        SingleUseHarness harness)
    {
        var vm = harness.Services.GetRequiredService<SettingsViewModel>();

        return (UiDriver.Show<SettingsView>(vm),
                harness.Services.GetRequiredService<ConfigStoreCache>(),
                vm);
    }
}
