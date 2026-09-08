using RavensPort.Core.Diagnostics;
using RavensPort.Core.Models;
using RavensPort.Core.Vault;

namespace RavensPort.Core.Tests.Vault;

/// <summary>
/// Choosing a backend. The interesting case is two managers both holding the vault: the app
/// stores nothing locally, so there is no remembered preference to fall back on, and guessing
/// would mean silently reading one while silently overwriting the other.
/// </summary>
public class VaultGateServiceTests : IDisposable
{
    private readonly string _stubDir = Path.Combine(Path.GetTempPath(), $"ravensport-gate-{Guid.NewGuid()}");
    private readonly string _logPath = Path.Combine(Path.GetTempPath(), $"ravensport-gate-logs-{Guid.NewGuid()}");

    private readonly string _opStub;
    private readonly string _passStub;

    public VaultGateServiceTests()
    {
        // Stub paths handed to the providers directly rather than set in the environment: xunit
        // runs test classes in parallel, and a process-wide variable would be clobbered by
        // whichever other vault test happened to be running at the same moment.
        Directory.CreateDirectory(_stubDir);
        _opStub = StubBinary("op.exe");
        _passStub = StubBinary("pass-cli.exe");
    }

    [Fact]
    public async Task NeitherInstalled_IsNotReadyAndDoesNotAsk()
    {
        var missing = Path.Combine(_stubDir, "gone.exe");
        var gate = new VaultGateService(
            new OnePasswordVaultProvider(new FakeCliRunner(), Log(), missing),
            new ProtonPassVaultProvider(new FakeCliRunner(), Log(), missing),
            Log());

        var status = await gate.EvaluateAsync();

        Assert.False(status.IsReady);
        Assert.False(status.NeedsAChoice);
        Assert.All(status.Statuses, s => Assert.Equal(VaultAvailability.NotInstalled, s.Availability));
    }

    [Fact]
    public async Task ExactlyOneReady_IsSelectedWithoutAsking()
    {
        var gate = NewGate(new FakeOnePassword().AsRunner(), SignedOutProtonPass());

        var status = await gate.EvaluateAsync();

        Assert.True(status.IsReady);
        Assert.Equal(VaultBackendKind.OnePassword, status.Selected);
        Assert.False(status.NeedsAChoice);
        Assert.Equal(VaultBackendKind.OnePassword, gate.Selected.Kind);
    }

    [Fact]
    public async Task BothReadyButOnlyOneHoldsAConfiguration_UsesThatOneWithoutAsking()
    {
        // The normal two-manager case, and the reason no stored preference is needed: whichever
        // vault actually has the configuration in it is the one that was being used.
        var onePassword = new FakeOnePassword();
        var protonPass = new FakeProtonPass();

        await new OnePasswordVaultProvider(onePassword.AsRunner(), Log(), _opStub)
            .SaveAsync(StoreWithSomethingInIt());

        var status = await NewGate(onePassword.AsRunner(), protonPass.AsRunner()).EvaluateAsync();

        Assert.True(status.IsReady);
        Assert.Equal(VaultBackendKind.OnePassword, status.Selected);
        Assert.False(status.NeedsAChoice);
    }

    [Fact]
    public async Task BothReadyAndNeitherHoldsAConfiguration_AsksTheUser()
    {
        var status = await NewGate(new FakeOnePassword().AsRunner(), new FakeProtonPass().AsRunner())
            .EvaluateAsync();

        Assert.True(status.NeedsAChoice);
        Assert.False(status.IsReady);
        Assert.Equal(VaultBackendKind.None, status.Selected);
    }

    [Fact]
    public async Task BothReadyAndBothHoldConfigurations_AsksRatherThanGuessing()
    {
        // Guessing here would silently read one vault and silently overwrite the other, which is
        // exactly the data loss the whole revision-guard machinery exists to prevent.
        var onePassword = new FakeOnePassword();
        var protonPass = new FakeProtonPass();

        await new OnePasswordVaultProvider(onePassword.AsRunner(), Log(), _opStub).SaveAsync(StoreWithSomethingInIt());
        await new ProtonPassVaultProvider(protonPass.AsRunner(), Log(), _passStub).SaveAsync(StoreWithSomethingInIt());

        var status = await NewGate(onePassword.AsRunner(), protonPass.AsRunner()).EvaluateAsync();

        Assert.True(status.NeedsAChoice);
        Assert.False(status.IsReady);
    }

    [Fact]
    public async Task SelectBackend_ResolvesTheChoiceForThisRunOnly()
    {
        var onePassword = new FakeOnePassword();
        var protonPass = new FakeProtonPass();
        var gate = NewGate(onePassword.AsRunner(), protonPass.AsRunner());

        await gate.EvaluateAsync();
        var chosen = gate.SelectBackend(VaultBackendKind.ProtonPass);

        Assert.True(chosen.IsReady);
        Assert.Equal(VaultBackendKind.ProtonPass, gate.Selected.Kind);

        // Nothing was written down, so a fresh gate is back to asking. That is the deliberate
        // trade for storing nothing about this app outside the vault.
        var freshStatus = await NewGate(onePassword.AsRunner(), protonPass.AsRunner()).EvaluateAsync();
        Assert.True(freshStatus.NeedsAChoice);
    }

    [Fact]
    public async Task OpeningAnExplicitlyChosenVaultDoesNotProbeTheOtherManagerAgain()
    {
        var onePassword = new FakeOnePassword();
        var protonPass = new FakeProtonPass();
        var onePasswordRunner = onePassword.AsRunner();
        var protonPassRunner = protonPass.AsRunner();
        var gate = NewGate(onePasswordRunner, protonPassRunner);

        await gate.EvaluateAsync();
        var onePasswordCallsBeforeOpening = onePasswordRunner.Invocations.Count;

        var status = await gate.UseExistingVaultAsync(VaultBackendKind.ProtonPass, VaultConstants.VaultName);

        Assert.True(status.IsReady);
        Assert.Equal(VaultBackendKind.ProtonPass, status.Selected);
        Assert.Equal(onePasswordCallsBeforeOpening, onePasswordRunner.Invocations.Count);
    }

    [Fact]
    public async Task CreateVault_MakesAMissingVaultReady()
    {
        var gate = NewGate(new FakeOnePassword { VaultExists = false }.AsRunner(), SignedOutProtonPass());

        var before = await gate.EvaluateAsync();
        Assert.True(before.For(VaultBackendKind.OnePassword)!.CanCreateVault);

        var after = await gate.CreateVaultAsync(VaultBackendKind.OnePassword, VaultConstants.VaultName);

        Assert.True(after.IsReady);
        Assert.Equal(VaultBackendKind.OnePassword, after.Selected);
    }

    [Fact]
    public async Task AThrowingProbeIsReportedRatherThanTakingTheAppDown()
    {
        // The setup page is the only thing that can explain what went wrong, so nothing on the
        // way to it may be allowed to throw past it.
        var exploding = new FakeCliRunner().Respond(_ =>
            throw new InvalidOperationException("something unexpected"));

        var status = await NewGate(exploding, SignedOutProtonPass()).EvaluateAsync();

        var onePassword = status.For(VaultBackendKind.OnePassword)!;
        Assert.Equal(VaultAvailability.Faulted, onePassword.Availability);
        Assert.Contains("something unexpected", onePassword.Detail);
    }

    private VaultGateService NewGate(FakeCliRunner onePassword, FakeCliRunner protonPass) =>
        new(new OnePasswordVaultProvider(onePassword, Log(), _opStub),
            new ProtonPassVaultProvider(protonPass, Log(), _passStub),
            Log());

    private static FakeCliRunner SignedOutProtonPass() =>
        new FakeCliRunner()
            .Respond(["--version"], "1.4.0")
            .Respond(["vault", "list"], exitCode: 1, stderr: "not logged in");

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
