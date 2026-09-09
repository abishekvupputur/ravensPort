using RavensPort.Core.Models;
using RavensPort.Core.Vault;
using RavensPort.UI.ViewModels;

namespace RavensPort.UI.Tests;

/// <summary>
/// The setup page's decisions, asked without a window.
///
/// Most of this page is what happens when a password manager will not cooperate, and what each test
/// asserts is that the page reports the refusal and stays usable — the alternative is a first-run
/// screen that has quietly given up.
///
/// Nothing here presses Connect, and that is deliberate. Whether that command fails instantly or
/// talks to a real manager depends on what happens to be installed: on CI there is no `op` and it
/// returns at once, while on a developer's machine it reaches the desktop app and can raise an
/// approval prompt at somebody who was only running the tests. A test that behaves differently per
/// machine — and interrupts one of them — is not worth the lines it covers. The refusals below are
/// all decided by the page itself, before any manager is asked.
/// </summary>
public class SetupViewModelUnitTests
{
    /// <summary>
    /// Creating a vault whose name is already taken is refused before the manager is asked.
    ///
    /// Two vaults called RavensPort are indistinguishable in the picker and the app would choose
    /// between them by list order, so this is the one thing the page must not produce — and it
    /// answers instantly rather than spending a round trip to find out.
    /// </summary>
    [Fact]
    public async Task CreatingAVaultWithANameThatExistsIsRefusedWithAdvice()
    {
        using var fixture = new ViewModelFixture();

        var setup = fixture.Get<SetupViewModel>();
        await setup.CheckCommand.ExecuteAsync(null);

        // A card built from a status that already lists the vault, which is what the page is given
        // once a manager has been asked what it holds.
        var card = new ManagerCardViewModel(new VaultStatus(
            VaultBackendKind.OnePassword,
            VaultAvailability.NotSignedIn,
            AdoptableVaults: [VaultProfile.NameFor("")]));

        await setup.CreateVaultCommand.ExecuteAsync(card);

        Assert.Contains("already exists", setup.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.False(setup.IsBusy);
    }

    /// <summary>
    /// Opening an existing vault with nothing chosen says which box is empty.
    /// </summary>
    [Fact]
    public async Task UsingAnExistingVaultWithNoNameChosenSaysSo()
    {
        using var fixture = new ViewModelFixture();

        var setup = fixture.Get<SetupViewModel>();
        await setup.CheckCommand.ExecuteAsync(null);

        var card = setup.Managers.First(m => m.Kind == VaultBackendKind.OnePassword);
        card.SelectedVaultName = "";

        await setup.UseExistingVaultCommand.ExecuteAsync(card);

        Assert.Contains("choose a vault", setup.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The port conflict, which is the one failure this page exists to rescue.
    ///
    /// The listen port lives in the vault, so a port already in use leaves the app serving nothing
    /// with no way in — the setup page takes a new one while the proxy is down, and that is the only
    /// moment it can be changed.
    /// </summary>
    [Fact]
    public async Task APortConflictIsShownAndANewPortIsValidatedBeforeItIsSaved()
    {
        using var fixture = new ViewModelFixture();
        await fixture.StartSingleUseAsync();

        var setup = fixture.Get<SetupViewModel>();

        setup.ReportPortConflict(5559, "Port 5559 is already in use.");

        Assert.True(setup.HasPortConflict);
        Assert.Equal("5559", setup.ListenPort);

        // A port the machine cannot give out is refused where it is typed.
        foreach (var bad in new[] { "0", "70000", "not-a-port" })
        {
            setup.ListenPort = bad;
            await setup.RetryPortCommand.ExecuteAsync(null);

            Assert.NotEqual(0, fixture.Store.Current.Settings.ListenPort);
            Assert.False(string.IsNullOrWhiteSpace(setup.StatusMessage));
        }

        setup.ListenPort = "5610";
        await setup.RetryPortCommand.ExecuteAsync(null);

        Assert.False(setup.HasPortConflict, "the conflict is still being reported after it was fixed");

        // Straight to the vault, not through the cache. The proxy is not running at this point —
        // that is the whole reason this button exists — so there is nothing holding a loaded store to
        // update, and reading the cache here would report the port this session started on rather
        // than the one just saved.
        await fixture.Store.ReloadAsync();
        Assert.Equal(5610, fixture.Store.Current.Settings.ListenPort);
    }

    /// <summary>
    /// The host's two ways of telling the page something went wrong after startup.
    /// </summary>
    [Fact]
    public void TheHostCanReportAReconnectFailureAndADisconnection()
    {
        using var fixture = new ViewModelFixture();

        var setup = fixture.Get<SetupViewModel>();

        setup.ReportReconnectFailure("the vault could not be read");
        Assert.Contains("could not be read", setup.StatusMessage);

        setup.IsDisconnected = true;
        Assert.True(setup.IsDisconnected, "the page has to know it was disconnected deliberately");
    }

    /// <summary>
    /// Single use, from the gate's side: the session opens and the page stops offering to start one.
    /// </summary>
    [Fact]
    public async Task StartingInSingleUseOpensTheGate()
    {
        using var fixture = new ViewModelFixture();

        var setup = fixture.Get<SetupViewModel>();

        await setup.StartSingleUseCommand.ExecuteAsync(null);

        Assert.True(fixture.Gate.IsSingleUse);
        Assert.False(setup.IsBusy);
    }
}
