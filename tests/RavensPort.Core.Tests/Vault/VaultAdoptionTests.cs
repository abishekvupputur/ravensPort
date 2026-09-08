using RavensPort.Core.Diagnostics;
using RavensPort.Core.Models;
using RavensPort.Core.Vault;

namespace RavensPort.Core.Tests.Vault;

/// <summary>
/// Using a vault the user already has, instead of letting RavensPort create RavensPort.
///
/// Two rules carry the weight here. Only an empty vault or one this app has written to may be
/// taken over — everything else has the user's own entries in it, and delete reconciliation runs
/// over vaults this app owns. And an adopted vault must be found again on the next launch without
/// anything being stored on the PC, which is what the config item in it is for.
/// </summary>
public class VaultAdoptionTests : IDisposable
{
    private readonly string _stubDir = Path.Combine(Path.GetTempPath(), $"ravensport-adopt-{Guid.NewGuid()}");
    private readonly string _logPath = Path.Combine(Path.GetTempPath(), $"ravensport-adopt-logs-{Guid.NewGuid()}");

    private readonly string _opStub;
    private readonly string _passStub;

    public VaultAdoptionTests()
    {
        Directory.CreateDirectory(_stubDir);
        _opStub = StubBinary("op.exe");
        _passStub = StubBinary("pass-cli.exe");
    }

    [Fact]
    public async Task ProtonPass_AnEmptyVaultIsAcceptedAndStampedSoItIsFoundAgain()
    {
        var protonPass = new FakeProtonPass { VaultExists = false };
        var shareId = protonPass.AddVault("Agents");
        var provider = NewProtonPass(protonPass);

        await provider.UseExistingVaultAsync("Agents");

        Assert.Equal("Agents", provider.VaultName);
        Assert.True((await provider.ProbeAsync()).IsReady);

        // The config item is the only thing that identifies this vault as RavensPort's, and the
        // name the user typed is deliberately not written down anywhere on this PC.
        Assert.Contains(protonPass.ItemsInVault(shareId),
            item => item["title"]?.GetValue<string>() == VaultItemNaming.ConfigTitle);
    }

    [Fact]
    public async Task ProtonPass_AVaultHoldingOnlyTrashedItemsCountsAsEmpty()
    {
        // Deleting an item in Proton Pass moves it to the trash, and `item list` keeps returning
        // it. Counting those made a vault the user had cleared out for RavensPort look full, and
        // the offer to use it was refused with no way to tell why.
        var protonPass = new FakeProtonPass { VaultExists = false };
        var shareId = protonPass.AddVault("Agents");
        protonPass.AddItem(shareId, "an old login", state: "Trashed");

        var provider = NewProtonPass(protonPass);
        await provider.UseExistingVaultAsync("Agents");

        Assert.Equal("Agents", provider.VaultName);
    }

    [Fact]
    public async Task ProtonPass_ATrashedItemIsInvisibleToEveryReader()
    {
        // Not just adoption: a trashed credential item that still read as present made a deleted
        // credential look alive, and would have had a save compare against an item the user cannot
        // see in their own password manager.
        var protonPass = new FakeProtonPass();
        var provider = NewProtonPass(protonPass);

        await provider.SaveAsync(StoreWithSomethingInIt());

        var credentialItemId = protonPass.ItemIdOf(title => title.Contains("credential —", StringComparison.Ordinal));
        Assert.NotNull(credentialItemId);

        protonPass.Trash(credentialItemId);

        var reloaded = await provider.LoadAsync();

        // Gone from the vault as far as this app is concerned, so the ghost-credential rule fires.
        Assert.Empty(reloaded.Credentials);
        Assert.NotEmpty(provider.LastLoadRemovals);
    }

    [Fact]
    public async Task OnePassword_AnArchivedItemIsInvisibleToEveryReader()
    {
        var onePassword = new FakeOnePassword { VaultExists = false };
        var vaultId = onePassword.AddVault("Agents");
        onePassword.AddItem(vaultId, "an archived login", state: "ARCHIVED");

        var provider = NewOnePassword(onePassword);
        await provider.UseExistingVaultAsync("Agents");

        Assert.Equal("Agents", provider.VaultName);
    }

    [Fact]
    public async Task ProtonPass_AVaultWithTheUsersOwnItemsIsRefused()
    {
        var protonPass = new FakeProtonPass { VaultExists = false };
        var shareId = protonPass.AddVault("Personal2");
        protonPass.AddItem(shareId, "Bank");

        var provider = NewProtonPass(protonPass);

        var refusal = await Assert.ThrowsAsync<VaultAdoptionException>(
            () => provider.UseExistingVaultAsync("Personal2"));

        Assert.Contains("Personal2", refusal.Message);

        // Names what it counted. "It has items in it" about a vault the user believes they emptied
        // is impossible to argue with; naming one of them ends the argument.
        Assert.Contains("Bank", refusal.Message);

        // Nothing was written to it, and the provider did not quietly adopt it anyway.
        Assert.Single(protonPass.ItemsInVault(shareId));
        Assert.Equal(VaultAvailability.VaultMissing, (await provider.ProbeAsync()).Availability);
    }

    [Fact]
    public async Task ProtonPass_AVaultThatAlreadyHoldsTheConfigurationIsAcceptedAndRead()
    {
        // What reconnecting to an adopted vault from a second install looks like.
        var protonPass = new FakeProtonPass { VaultExists = false };
        protonPass.AddVault("Agents");

        var first = NewProtonPass(protonPass);
        await first.UseExistingVaultAsync("Agents");
        await first.SaveAsync(StoreWithSomethingInIt());

        var second = NewProtonPass(protonPass);
        await second.UseExistingVaultAsync("Agents");

        var store = await second.LoadAsync();
        Assert.Equal("cred", Assert.Single(store.Credentials).Name);
    }

    [Fact]
    public async Task ProtonPass_AnAdoptedVaultIsFoundAgainByAFreshProviderWithNothingStoredLocally()
    {
        var protonPass = new FakeProtonPass { VaultExists = false };
        protonPass.AddVault("Agents");

        var first = NewProtonPass(protonPass);
        await first.UseExistingVaultAsync("Agents");
        await first.SaveAsync(StoreWithSomethingInIt());

        // A restart: a provider that has never heard of "Agents" and no RavensPort to fall
        // back on. The configuration in the vault is the only evidence, and it is enough.
        var afterRestart = NewProtonPass(protonPass);
        var status = await afterRestart.ProbeAsync();

        Assert.True(status.IsReady);
        Assert.Equal("Agents", status.VaultName);
        Assert.Single((await afterRestart.LoadAsync()).Credentials);
    }

    [Fact]
    public async Task ProtonPass_AMisspelledVaultIsRefusedWithTheNamesThatDoExist()
    {
        var protonPass = new FakeProtonPass { VaultExists = false };
        protonPass.AddVault("Agents");

        var refusal = await Assert.ThrowsAsync<VaultAdoptionException>(
            () => NewProtonPass(protonPass).UseExistingVaultAsync("Agnets"));

        Assert.Contains("Agents", refusal.Message);
    }

    [Fact]
    public async Task ProtonPass_TheNameIsMatchedWithoutRegardToCase()
    {
        // The user is typing a name they read in the Proton Pass UI, not one this app gave them.
        var protonPass = new FakeProtonPass { VaultExists = false };
        protonPass.AddVault("Agents");

        var provider = NewProtonPass(protonPass);
        await provider.UseExistingVaultAsync("agents");

        Assert.Equal("Agents", provider.VaultName);
    }

    [Fact]
    public async Task OnePassword_AnEmptyVaultIsAcceptedAndStampedSoItIsFoundAgain()
    {
        var onePassword = new FakeOnePassword { VaultExists = false };
        var vaultId = onePassword.AddVault("Agents");
        var provider = NewOnePassword(onePassword);

        await provider.UseExistingVaultAsync("Agents");

        Assert.Equal("Agents", provider.VaultName);
        Assert.Contains(onePassword.ItemsInVault(vaultId),
            item => item["title"]?.GetValue<string>() == VaultItemNaming.ConfigTitle);
    }

    [Fact]
    public async Task OnePassword_AVaultWithTheUsersOwnItemsIsRefused()
    {
        var onePassword = new FakeOnePassword { VaultExists = false };
        var vaultId = onePassword.AddVault("Personal2");
        onePassword.AddItem(vaultId, "Bank");

        await Assert.ThrowsAsync<VaultAdoptionException>(
            () => NewOnePassword(onePassword).UseExistingVaultAsync("Personal2"));

        Assert.Single(onePassword.ItemsInVault(vaultId));
    }

    [Fact]
    public async Task OnePassword_AnAdoptedVaultIsFoundAgainByAFreshProvider()
    {
        var onePassword = new FakeOnePassword { VaultExists = false };
        onePassword.AddVault("Agents");

        var first = NewOnePassword(onePassword);
        await first.UseExistingVaultAsync("Agents");
        await first.SaveAsync(StoreWithSomethingInIt());

        var status = await NewOnePassword(onePassword).ProbeAsync();

        Assert.True(status.IsReady);
        Assert.Equal("Agents", status.VaultName);
    }

    [Fact]
    public async Task TheDefaultVaultStillWinsOverAnythingElseHoldingAConfiguration()
    {
        // Scanning happens only when RavensPort is absent. A user who has both should not have
        // the app quietly moved into the other one by a leftover config item.
        var protonPass = new FakeProtonPass();
        var otherShare = protonPass.AddVault("Agents");
        protonPass.AddItem(otherShare, VaultItemNaming.ConfigTitle);

        var status = await NewProtonPass(protonPass).ProbeAsync();

        Assert.Equal(VaultConstants.VaultName, status.VaultName);
        Assert.Equal(protonPass.ShareId, status.VaultId);
    }

    [Fact]
    public async Task AGateDisconnectDoesNotReconnectItselfOnTheNextProbe()
    {
        // Disconnecting has to survive the very next probe, or the single-ready-manager shortcut
        // would undo it before the user has taken their hand off the mouse.
        var protonPass = new FakeProtonPass();
        var gate = NewGate(SignedOutOnePassword(), protonPass.AsRunner());

        Assert.True((await gate.EvaluateAsync()).IsReady);

        gate.Disconnect();
        var afterDisconnect = await gate.EvaluateAsync();

        Assert.True(gate.IsDisconnected);
        Assert.False(afterDisconnect.IsReady);
        Assert.Equal(VaultBackendKind.None, afterDisconnect.Selected);

        // ...and the ready manager is offered as a choice, which is how the user connects back.
        Assert.True(afterDisconnect.NeedsAChoice);

        var reconnected = gate.SelectBackend(VaultBackendKind.ProtonPass);
        Assert.True(reconnected.IsReady);
        Assert.False(gate.IsDisconnected);
    }

    [Fact]
    public async Task AfterDisconnectingTheVaultIsNotRediscoveredBehindTheUsersBack()
    {
        // The whole point of disconnecting is to choose a different vault. Rediscovery — the thing
        // that makes an adopted vault stick across restarts — would reattach the one just left, and
        // the setup page would come back Ready with no way to pick another.
        var protonPass = new FakeProtonPass { VaultExists = false };
        protonPass.AddVault("Agents");

        var provider = NewProtonPass(protonPass);
        await provider.UseExistingVaultAsync("Agents");
        await provider.SaveAsync(StoreWithSomethingInIt());

        Assert.True((await provider.ProbeAsync()).IsReady);

        provider.Forget();

        var afterDisconnect = await provider.ProbeAsync();

        Assert.False(afterDisconnect.IsReady);
        Assert.Equal(VaultConstants.VaultName, afterDisconnect.VaultName);

        // ...and the vaults are offered so another can be chosen without typing it exactly.
        Assert.Contains("Agents", afterDisconnect.Vaults!);

        // Naming one again puts discovery back on, so the next restart still finds it.
        await provider.UseExistingVaultAsync("Agents");

        var afterRestart = await NewProtonPass(protonPass).ProbeAsync();
        Assert.Equal("Agents", afterRestart.VaultName);
    }

    [Fact]
    public async Task TwoVaultsHoldingAConfigurationAreOfferedRatherThanGuessedBetween()
    {
        // Two configured vaults is a user keeping separate profiles. Picking one would open it and
        // overwrite the other's note on the next save.
        var protonPass = new FakeProtonPass { VaultExists = false };
        protonPass.AddVault("Work");
        protonPass.AddVault("Home");

        var work = NewProtonPass(protonPass);
        await work.UseExistingVaultAsync("Work");
        await work.SaveAsync(StoreWithSomethingInIt());

        var home = NewProtonPass(protonPass);
        await home.UseExistingVaultAsync("Home");
        await home.SaveAsync(StoreWithSomethingInIt());

        var status = await NewProtonPass(protonPass).ProbeAsync();

        Assert.Equal(VaultAvailability.VaultChoiceNeeded, status.Availability);
        Assert.False(status.IsReady);
        Assert.Equal(2, status.ConfiguredVaults!.Count);
        Assert.Contains("Work", status.ConfiguredVaults);
        Assert.Contains("Home", status.ConfiguredVaults);
    }

    [Fact]
    public async Task DisconnectingMakesTheProvidersForgetTheVaultTheyResolved()
    {
        var protonPass = new FakeProtonPass { VaultExists = false };
        protonPass.AddVault("Agents");

        var provider = NewProtonPass(protonPass);
        var gate = new VaultGateService(
            new OnePasswordVaultProvider(SignedOutOnePassword(), Log(), _opStub), provider, Log());

        await gate.UseExistingVaultAsync(VaultBackendKind.ProtonPass, "Agents");
        Assert.Equal("Agents", provider.VaultName);

        gate.Disconnect();

        // Back to looking for RavensPort: reconnecting must not keep writing to the vault the
        // user just walked away from.
        Assert.Equal(VaultConstants.VaultName, provider.VaultName);
    }

    private ProtonPassVaultProvider NewProtonPass(FakeProtonPass protonPass) =>
        new(protonPass.AsRunner(), Log(), _passStub);

    private OnePasswordVaultProvider NewOnePassword(FakeOnePassword onePassword) =>
        new(onePassword.AsRunner(), Log(), _opStub);

    private VaultGateService NewGate(FakeCliRunner onePassword, FakeCliRunner protonPass) =>
        new(new OnePasswordVaultProvider(onePassword, Log(), _opStub),
            new ProtonPassVaultProvider(protonPass, Log(), _passStub),
            Log());

    private static FakeCliRunner SignedOutOnePassword() =>
        new FakeCliRunner()
            .Respond(["--version"], "2.31.0")
            .Respond(["vault", "list"], exitCode: 1, stderr: "not signed in");

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
