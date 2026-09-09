using Microsoft.Extensions.DependencyInjection;
using RavensPort.Core;
using RavensPort.Core.Vault;
using RavensPort.UI.ViewModels;
using RavensPort.Views;

namespace RavensPort.UI.Tests;

/// <summary>
/// The setup page before anything is connected — the first screen anyone sees.
///
/// Nothing here needs a password manager, and that is the point: the page's job when there is none
/// installed is to say so per manager and offer the ways forward. A card is built for every backend
/// the build supports whatever the probe found, so "not installed" is a state the page renders
/// rather than an absence it hides.
///
/// The probe is deliberately shallow. It looks at what is on disk and stops, because asking a
/// manager whether it is signed in is what raises a biometric prompt — an app that did that on
/// startup would greet everyone with a queue of them.
/// </summary>
public class SetupPageUiTests
{
    /// <summary>
    /// Pressing "Check again" and getting a card per supported manager, each saying what it found.
    /// </summary>
    [Fact]
    public Task CheckingBuildsACardForEveryManagerTheBuildSupports() => UiSession.RunAsync(async () =>
    {
        await using var harness = await SingleUseHarness.StartAsync();

        var vm = harness.Services.GetRequiredService<SetupViewModel>();
        var view = UiDriver.Show<SetupView>(vm);

        await UiDriver.ClickAsync(view, AutomationIds.CheckAgain);

        await UiDriver.UntilAsync(
            () => !vm.IsBusy && vm.Managers.Count > 0,
            () => $"the check to finish and build cards — the page says: {vm.StatusMessage}");

        // 1Password is in every build; Proton Pass is dropped from the Store one, which is exactly
        // what BuildProfile decides and what this assertion follows rather than restates.
        Assert.Contains(vm.Managers, m => m.Kind == VaultBackendKind.OnePassword);
        Assert.Equal(BuildProfile.ProtonPassEnabled ? 2 : 1, vm.Managers.Count);

        // Every card says something about what it found, whatever that was.
        Assert.All(vm.Managers, card =>
        {
            Assert.False(string.IsNullOrWhiteSpace(card.Name), "a card with no manager name");
            Assert.False(string.IsNullOrWhiteSpace(card.StateLabel), $"'{card.Name}' says nothing about its state");
        });

        // Nothing was connected, so the page is still the page.
        Assert.False(harness.Services.GetRequiredService<VaultGateService>().IsSingleUse);
    });

    /// <summary>
    /// A manager that is not installed offers no way to connect to it.
    ///
    /// The runner has neither CLI, which makes this the honest case rather than a contrived one: the
    /// card has to explain the absence instead of presenting a button that could only fail.
    /// </summary>
    [Fact]
    public Task AManagerThatIsNotInstalledSaysSoRatherThanOfferingToConnect() => UiSession.RunAsync(async () =>
    {
        await using var harness = await SingleUseHarness.StartAsync();

        var vm = harness.Services.GetRequiredService<SetupViewModel>();
        var view = UiDriver.Show<SetupView>(vm);

        await UiDriver.ClickAsync(view, AutomationIds.CheckAgain);
        await UiDriver.UntilAsync(
            () => !vm.IsBusy && vm.Managers.Count > 0, "the check to finish");

        foreach (var card in vm.Managers.Where(c => c.Availability == VaultAvailability.NotInstalled))
        {
            // What it offers instead is how to get it: install instructions, not a sign-in.
            Assert.True(card.ShowInstall, $"'{card.Name}' is not installed but does not say how to install it");
            Assert.False(card.ShowSignIn, $"'{card.Name}' is not installed but offers to sign in");
            Assert.False(card.IsReady, $"'{card.Name}' is not installed but reports itself ready");
            Assert.False(string.IsNullOrWhiteSpace(card.StateLabel));
        }
    });

    /// <summary>
    /// Checking twice, which is what the button is for.
    ///
    /// Someone presses it after installing a CLI, so it has to rebuild rather than accumulate — a
    /// second press that doubled the cards would be the obvious way to get this wrong.
    /// </summary>
    [Fact]
    public Task CheckingTwiceRebuildsTheCardsRatherThanAddingMore() => UiSession.RunAsync(async () =>
    {
        await using var harness = await SingleUseHarness.StartAsync();

        var vm = harness.Services.GetRequiredService<SetupViewModel>();
        var view = UiDriver.Show<SetupView>(vm);

        await UiDriver.ClickAsync(view, AutomationIds.CheckAgain);
        await UiDriver.UntilAsync(() => !vm.IsBusy && vm.Managers.Count > 0, "the first check");

        var first = vm.Managers.Count;

        await UiDriver.ClickAsync(view, AutomationIds.CheckAgain);
        await UiDriver.UntilAsync(() => !vm.IsBusy, "the second check");

        Assert.Equal(first, vm.Managers.Count);
    });

    /// <summary>
    /// The port conflict the page exists to rescue, and the only place it can be fixed.
    ///
    /// The listen port lives in the vault, so a port already in use leaves the app with nothing
    /// serving and no way in — which is why the setup page takes a new one while the proxy is down.
    /// </summary>
    [Fact]
    public Task APortConflictIsReportedAndTheNewPortAccepted() => UiSession.RunAsync(async () =>
    {
        await using var harness = await SingleUseHarness.StartAsync();

        var vm = harness.Services.GetRequiredService<SetupViewModel>();
        UiDriver.Show<SetupView>(vm);

        await UiDriver.RunOnUiAsync(() => vm.ReportPortConflict(5559, "Port 5559 is already in use."));

        Assert.True(vm.HasPortConflict);
        Assert.Equal("5559", vm.ListenPort);
        Assert.Contains("5559", vm.StatusMessage);

        // And the reconnect failure the host reports the same way, which shares the status line.
        await UiDriver.RunOnUiAsync(() => vm.ReportReconnectFailure("the vault could not be read"));

        Assert.Contains("could not be read", vm.StatusMessage);
    });
}
