using RavensPort.Core.Diagnostics;
using RavensPort.Core.Models;
using RavensPort.Core.Vault;

namespace RavensPort.Core.Tests.Vault;

/// <summary>
/// What the setup page is allowed to put in front of the user.
///
/// Two rules, and they answer the same worry from opposite ends. The list is confined to vaults
/// named after RavensPort, because an app that recites the rest of someone's password manager
/// back at them has no business holding their tokens. And it is confined to vaults that would
/// actually be accepted — empty, or already RavensPort's — because offering one that
/// <see cref="VaultAdoption.Judge"/> then refuses is a trap the user walked into on the app's
/// invitation.
/// </summary>
public class VaultPickerTests : IDisposable
{
    private readonly string _stubDir = Path.Combine(Path.GetTempPath(), $"ravensport-picker-{Guid.NewGuid()}");
    private readonly string _logPath = Path.Combine(Path.GetTempPath(), $"ravensport-picker-logs-{Guid.NewGuid()}");

    private readonly string _opStub;
    private readonly string _passStub;

    public VaultPickerTests()
    {
        Directory.CreateDirectory(_stubDir);
        _opStub = StubBinary("op.exe");
        _passStub = StubBinary("pass-cli.exe");
    }

    [Fact]
    public async Task OnePassword_OnlyVaultsNamedAfterRavensPortAreOffered()
    {
        var onePassword = new FakeOnePassword();
        onePassword.AddVault($"{VaultConstants.VaultName} Work");
        onePassword.AddVault("Personal");

        var status = await NewOnePassword(onePassword).ProbeAsync();

        Assert.Contains(VaultConstants.VaultName, status.AdoptableVaults!);
        Assert.Contains($"{VaultConstants.VaultName} Work", status.AdoptableVaults!);

        // The user's own vault is in the account and stays out of the picker, empty or not.
        Assert.DoesNotContain("Personal", status.AdoptableVaults!);
        Assert.Contains("Personal", status.Vaults!);
    }

    [Fact]
    public async Task OnePassword_AVaultWithTheUsersOwnEntriesInItIsNotOffered()
    {
        // Named right, but full of someone's logins. Picking it would be refused — this app keeps to
        // a vault of its own — so it is never offered in the first place.
        var onePassword = new FakeOnePassword();
        var theirs = onePassword.AddVault($"{VaultConstants.VaultName} Archive");
        onePassword.AddItem(theirs, "Bank login");

        var status = await NewOnePassword(onePassword).ProbeAsync();

        Assert.DoesNotContain($"{VaultConstants.VaultName} Archive", status.AdoptableVaults!);
    }

    [Fact]
    public async Task ProtonPass_EmptyVaultsAndRavensPortsOwnAreOffered()
    {
        var protonPass = new FakeProtonPass { VaultExists = false };
        protonPass.AddVault($"{VaultConstants.VaultName} Work");
        protonPass.AddVault($"{VaultConstants.VaultName} Home");

        // One of them has been used before: it holds a configuration, which is the other half of
        // what makes a vault safe to open.
        var configured = NewProtonPass(protonPass);
        await configured.UseExistingVaultAsync($"{VaultConstants.VaultName} Home");
        await configured.SaveAsync(StoreWithSomethingInIt());

        var status = await NewProtonPass(protonPass).ProbeAsync();

        Assert.Contains($"{VaultConstants.VaultName} Work", status.AdoptableVaults!);
        Assert.Contains($"{VaultConstants.VaultName} Home", status.AdoptableVaults!);
    }

    [Fact]
    public async Task ProtonPass_AVaultAdoptedUnderAnotherNameIsStillFoundByItsConfiguration()
    {
        // The picker's name rule must not strand anyone who adopted a differently named vault
        // before it existed: that vault is found the way it always was — by the configuration in
        // it — and reported as such, so the page can still offer it.
        var protonPass = new FakeProtonPass { VaultExists = false };
        protonPass.AddVault("Agents");

        var provider = NewProtonPass(protonPass);
        await provider.UseExistingVaultAsync("Agents");
        await provider.SaveAsync(StoreWithSomethingInIt());

        var status = await NewProtonPass(protonPass).ProbeAsync();

        Assert.True(status.IsReady);
        Assert.Equal("Agents", status.VaultName);
        Assert.DoesNotContain("Agents", status.AdoptableVaults!);
    }

    private ProtonPassVaultProvider NewProtonPass(FakeProtonPass protonPass) =>
        new(protonPass.AsRunner(), Log(), _passStub);

    private OnePasswordVaultProvider NewOnePassword(FakeOnePassword onePassword) =>
        new(onePassword.AsRunner(), Log(), _opStub);

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
