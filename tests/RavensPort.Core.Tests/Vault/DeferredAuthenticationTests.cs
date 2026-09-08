using Moq;
using RavensPort.Core.Diagnostics;
using RavensPort.Core.Models;
using RavensPort.Core.Vault;

namespace RavensPort.Core.Tests.Vault;

/// <summary>
/// Who gets to raise an authentication prompt, and when.
///
/// The rule these tests hold down is that launching RavensPort must not ask anybody for anything.
/// Startup discovers which password managers are installed; the unlock prompt, the Hello gesture
/// and the 1Password desktop approval all belong to a button the user pressed. The check is not
/// cosmetic — every one of those prompts is a CLI call, so "did it prompt" is answerable by asking
/// the fake runner what was run.
/// </summary>
[Collection(NativeCliRunnerCollection.Name)]
public class DeferredAuthenticationTests : IDisposable
{
    private readonly string _stubDir = Path.Combine(Path.GetTempPath(), $"ravensport-defer-{Guid.NewGuid()}");
    private readonly string _logPath = Path.Combine(Path.GetTempPath(), $"ravensport-defer-logs-{Guid.NewGuid()}");

    private readonly string _opStub;
    private readonly string _passStub;

    public DeferredAuthenticationTests()
    {
        Directory.CreateDirectory(_stubDir);
        _opStub = StubBinary("op.exe");
        _passStub = StubBinary("pass-cli.exe");
    }

    [Fact]
    public async Task DiscoveryProbeOfOnePasswordRunsNothingButVersion()
    {
        // FakeCliRunner throws on anything it has no script for, so scripting only --version is
        // the assertion: a `vault list` or an `item list` here would fail the test loudly, and each
        // of those is a desktop-app approval on a real machine.
        var runner = new FakeCliRunner().Respond(["--version"], "2.30.0");
        var provider = new OnePasswordVaultProvider(runner, Log(), _opStub);

        var status = await provider.ProbeAsync(VaultProbeDepth.Discovery);

        Assert.Equal(VaultAvailability.NotConnected, status.Availability);
        Assert.Equal("2.30.0", status.Version);
        Assert.Single(runner.Invocations);
    }

    [Fact]
    public async Task DiscoveringOnePasswordNeverOpensTheDesktopConnection()
    {
        // The end-to-end version of the rule, through the runner the app actually ships: 1Password
        // has no CLI here, it goes through onepassword.dll, and Initialize() on that is what makes
        // the desktop app demand an unlock. A discovery probe must not reach it.
        //
        // This is the regression that mattered. The probe was already discovery-only, but the
        // native runner initialised before dispatching on the arguments, so the --version call it
        // made to identify the binary connected anyway -- and the user was asked to unlock
        // 1Password by a page that had not yet offered them anything to press.
        var client = new Mock<IOnePasswordNativeClient>();
        var provider = new OnePasswordVaultProvider(
            new NativeCliRunner(client: client.Object), Log(), exePathOverride: "native");

        var status = await provider.ProbeAsync(VaultProbeDepth.Discovery);

        Assert.Equal(VaultAvailability.NotConnected, status.Availability);
        client.Verify(c => c.Initialize(It.IsAny<string>()), Times.Never);
        client.Verify(c => c.ListVaults(), Times.Never);
        client.Verify(c => c.ListItems(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task DiscoveryProbeOfProtonPassRunsNothingButVersion()
    {
        var runner = new FakeCliRunner().Respond(["--version"], "1.4.0");
        var provider = new ProtonPassVaultProvider(runner, Log(), _passStub);

        var status = await provider.ProbeAsync(VaultProbeDepth.Discovery);

        Assert.Equal(VaultAvailability.NotConnected, status.Availability);
        Assert.Single(runner.Invocations);
    }

    [Fact]
    public async Task DiscoveryStillReportsAManagerThatIsNotInstalled()
    {
        // The one thing discovery must keep answering: it is what the setup page's install
        // instructions hang off, and it costs nothing to find out.
        var missing = Path.Combine(_stubDir, "gone.exe");
        var provider = new OnePasswordVaultProvider(new FakeCliRunner(), Log(), missing);

        var status = await provider.ProbeAsync(VaultProbeDepth.Discovery);

        Assert.Equal(VaultAvailability.NotInstalled, status.Availability);
    }

    [Fact]
    public async Task StartupDiscoveryNeverOpensAVaultEvenWhenBothManagersWouldBeReady()
    {
        // The old behaviour: both fakes are fully signed in, so a full probe resolves a backend and
        // reads a vault. A discovery pass must not, because on a real machine that resolution is
        // paid for in prompts the user never asked for.
        var onePassword = new FakeOnePassword();
        var protonPass = new FakeProtonPass();
        var gate = NewGate(onePassword.AsRunner(), protonPass.AsRunner());

        var status = await gate.EvaluateAsync(VaultProbeDepth.Discovery);

        Assert.False(status.IsReady);
        Assert.False(status.NeedsAChoice);
        Assert.Equal(VaultBackendKind.None, status.Selected);
        Assert.All(status.Statuses, s => Assert.Equal(VaultAvailability.NotConnected, s.Availability));
    }

    [Fact]
    public async Task ConnectingToOneManagerLeavesTheOtherAlone()
    {
        // The point of connecting per manager rather than re-evaluating: a user who has 1Password
        // installed and is connecting Proton Pass must not be made to approve 1Password commands
        // on the way past.
        var onePasswordRunner = new FakeOnePassword().AsRunner();
        var protonPassRunner = new FakeProtonPass().AsRunner();
        var gate = NewGate(onePasswordRunner, protonPassRunner);

        await gate.EvaluateAsync(VaultProbeDepth.Discovery);
        var onePasswordCallsAfterDiscovery = onePasswordRunner.Invocations.Count;

        var status = await gate.ConnectAsync(VaultBackendKind.ProtonPass);

        Assert.True(status.IsReady);
        Assert.Equal(VaultBackendKind.ProtonPass, status.Selected);
        Assert.Equal(onePasswordCallsAfterDiscovery, onePasswordRunner.Invocations.Count);

        // And the untouched card still says what discovery found, rather than pretending to know.
        Assert.Equal(VaultAvailability.NotConnected, status.For(VaultBackendKind.OnePassword)!.Availability);
    }

    [Fact]
    public async Task ConnectingToAManagerThatIsNotSignedInLeavesTheGateClosedAndSaysSo()
    {
        var protonPass = new FakeCliRunner()
            .Respond(["--version"], "1.4.0")
            .Respond(["vault", "list"], exitCode: 1, stderr: "not logged in");

        var gate = NewGate(new FakeOnePassword().AsRunner(), protonPass);

        await gate.EvaluateAsync(VaultProbeDepth.Discovery);
        var status = await gate.ConnectAsync(VaultBackendKind.ProtonPass);

        Assert.False(status.IsReady);
        Assert.Equal(VaultAvailability.NotSignedIn, status.For(VaultBackendKind.ProtonPass)!.Availability);
    }

    [Fact]
    public async Task ReconnectingAfterADisconnectCanWriteAgain()
    {
        // The failure this pins is quiet, which is what made it bad: Disconnect shuts writing on the
        // provider, and only naming a vault used to re-open it. Connect became a third way to
        // become the selected backend and cleared nothing -- so the probe still resolved the vault
        // by name, reads worked, the Settings tab said "1Password - vault 'RavensPort'", and every
        // save died with "not connected to a 1Password vault. Choose a vault on the setup page
        // first" about the page the user had just used successfully.
        var onePassword = new FakeOnePassword();
        var gate = NewGate(onePassword.AsRunner(), SignedOutProtonPass());

        Assert.True((await gate.EvaluateAsync()).IsReady);
        await gate.Selected.SaveAsync(StoreWithSomethingInIt());

        gate.Disconnect();

        var reconnected = await gate.ConnectAsync(VaultBackendKind.OnePassword);
        Assert.True(reconnected.IsReady);
        Assert.Equal(VaultBackendKind.OnePassword, gate.Selected.Kind);

        // The assertion that matters. Reading proved nothing here -- it worked throughout the bug.
        await gate.Selected.SaveAsync(StoreWithSomethingInIt());
    }

    [Fact]
    public async Task AConnectThatFailsLeavesWritingShut()
    {
        // The other half of the rule. Re-opening writes on the way *into* the probe would leave a
        // provider writable with no vault resolved, which is the state Forget exists to prevent:
        // a queued save then resolves a vault of its own choosing and writes into it.
        var lockedOnePassword = new FakeCliRunner()
            .Respond(["--version"], "2.30.0")
            .Respond(["vault", "list"], exitCode: 1, stderr: "not signed in");

        var gate = NewGate(new FakeOnePassword().AsRunner(), SignedOutProtonPass());
        Assert.True((await gate.EvaluateAsync()).IsReady);
        gate.Disconnect();

        // A provider that has been disconnected and whose reconnect probe fails.
        var provider = new OnePasswordVaultProvider(lockedOnePassword, Log(), _opStub);
        provider.Forget();
        provider.AllowDiscovery();

        await Assert.ThrowsAsync<VaultSaveException>(() => provider.SaveAsync(StoreWithSomethingInIt()));
    }

    // ---- Single use --------------------------------------------------------------------------

    [Fact]
    public async Task SingleUseOpensTheGateWithoutTouchingAPasswordManager()
    {
        // Both runners are unscripted, so any CLI call at all throws. That is the assertion: single
        // use has to be reachable on a machine whose password managers are locked, missing, or
        // simply not wanted.
        var gate = NewGate(new FakeCliRunner(), new FakeCliRunner());

        var status = gate.UseSingleUse();

        Assert.True(status.IsReady);
        Assert.Equal(VaultBackendKind.SingleUse, status.Selected);
        Assert.True(gate.IsSingleUse);

        // And it is a working store, not a stub that fails on first use.
        await gate.Selected.SaveAsync(StoreWithSomethingInIt());
        Assert.Single((await gate.Selected.LoadAsync()).Credentials);
    }

    [Fact]
    public async Task DisconnectingFromSingleUseThrowsTheConfigurationAway()
    {
        // There is no vault behind a single-use session, so this is the only copy. "Purged" has to
        // mean the store is gone, not merely that the UI stopped showing it.
        var gate = NewGate(new FakeCliRunner(), new FakeCliRunner());

        gate.UseSingleUse();
        await gate.Selected.SaveAsync(StoreWithSomethingInIt());

        gate.Disconnect();

        Assert.False(gate.IsSingleUse);
        Assert.True(gate.IsDisconnected);
        Assert.Empty((await gate.Selected.LoadAsync()).Credentials);

        // A second single-use session starts empty rather than inheriting the first one's items.
        gate.UseSingleUse();
        Assert.Empty((await gate.Selected.LoadAsync()).Credentials);
    }

    [Fact]
    public async Task ASingleUseSessionSurvivesAManagerBecomingAvailable()
    {
        // A probe that resolved 1Password underneath a single-use session would start writing the
        // user's in-memory configuration into a vault they deliberately did not choose.
        var gate = NewGate(new FakeOnePassword().AsRunner(), new FakeProtonPass().AsRunner());

        gate.UseSingleUse();
        await gate.Selected.SaveAsync(StoreWithSomethingInIt());

        var status = await gate.EvaluateAsync(VaultProbeDepth.Discovery);

        Assert.Equal(VaultBackendKind.SingleUse, status.Selected);
        Assert.Single((await gate.Selected.LoadAsync()).Credentials);
    }

    [Fact]
    public void ConnectingAfterSingleUseDropsTheSingleUseStore()
    {
        var gate = NewGate(new FakeCliRunner(), new FakeCliRunner());

        gate.UseSingleUse();
        gate.SelectBackend(VaultBackendKind.OnePassword);

        Assert.False(gate.IsSingleUse);
        Assert.Equal(VaultBackendKind.OnePassword, gate.Selected.Kind);
    }

    private static FakeCliRunner SignedOutProtonPass() =>
        new FakeCliRunner()
            .Respond(["--version"], "1.4.0")
            .Respond(["vault", "list"], exitCode: 1, stderr: "not logged in");

    private VaultGateService NewGate(FakeCliRunner onePassword, FakeCliRunner protonPass) =>
        new(new OnePasswordVaultProvider(onePassword, Log(), _opStub),
            new ProtonPassVaultProvider(protonPass, Log(), _passStub),
            Log());

    private ActivityLog Log() => new(_logPath);

    private static ConfigStore StoreWithSomethingInIt()
    {
        var store = new ConfigStore();
        store.Credentials.Add(new CredentialRecord { Name = "cred", ClientId = "id", ClientSecret = "secret" });
        return store;
    }

    private string StubBinary(string exeName)
    {
        var path = Path.Combine(_stubDir, exeName);
        File.WriteAllText(path, "");
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(_stubDir, recursive: true); } catch { /* best effort */ }
        try { Directory.Delete(_logPath, recursive: true); } catch { /* best effort */ }

        // Nothing here has a finalizer, but the pattern is what CA1816 asks for and what a
        // derived test fixture would need if one ever did.
        GC.SuppressFinalize(this);
    }
}
