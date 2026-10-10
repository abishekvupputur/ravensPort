using RavensPort.Core.Diagnostics;
using RavensPort.Core.Models;
using RavensPort.Core.Storage;
using RavensPort.Core.Vault;

namespace RavensPort.Core.Tests.Vault;

/// <summary>
/// A backend connected read-only — the headless Proton Pass session. The promise is the whole
/// feature: configuration comes out of the vault, every change lives in memory, and nothing at all
/// goes back. Each test pins one way that promise could quietly be broken.
/// </summary>
public class ReadOnlyVaultTests : IDisposable
{
    private readonly string _stubDir = Path.Combine(Path.GetTempPath(), $"ravensport-ro-{Guid.NewGuid()}");
    private readonly string _logPath = Path.Combine(Path.GetTempPath(), $"ravensport-ro-logs-{Guid.NewGuid()}");
    private readonly string _opStub;

    public ReadOnlyVaultTests()
    {
        Directory.CreateDirectory(_stubDir);
        _opStub = Path.Combine(_stubDir, "op.exe");
        File.WriteAllText(_opStub, "");
    }

    [Fact]
    public async Task AReadOnlyConnectSelectsAVaultThatReadsAndRefusesEveryWrite()
    {
        var (gate, _) = NewGate(new FakeOnePassword());

        var status = await gate.ConnectAsync(VaultBackendKind.OnePassword, readOnly: true);

        Assert.True(status.IsReady);
        Assert.True(gate.IsReadOnly);
        Assert.IsType<ReadOnlyConfigVault>(gate.Selected);

        await gate.Selected.LoadAsync();

        await Assert.ThrowsAsync<VaultReadOnlyException>(() => gate.Selected.SaveAsync(new ConfigStore()));
        await Assert.ThrowsAsync<VaultReadOnlyException>(() => gate.Selected.RewriteAllAsync(new ConfigStore()));
        await Assert.ThrowsAsync<VaultReadOnlyException>(() => gate.Selected.DeleteItemAsync("item"));
        await Assert.ThrowsAsync<VaultReadOnlyException>(() => gate.Selected.CreateVaultAsync("x"));
        await Assert.ThrowsAsync<VaultReadOnlyException>(() => gate.Selected.UseExistingVaultAsync("x"));
    }

    [Fact]
    public async Task AReadOnlyConnectToAMissingVaultStaysUnreadyAndWritesNothing()
    {
        // Adoption writes the Config stamp, so a read-only session must never reach for it — an
        // unrecognised vault is reported, not repaired.
        var (gate, runner) = NewGate(new FakeOnePassword { VaultExists = false });

        var status = await gate.ConnectAsync(VaultBackendKind.OnePassword, readOnly: true);

        Assert.False(status.IsReady);
        Assert.DoesNotContain(runner.Invocations, i => IsWrite(i.Args));
    }

    [Fact]
    public async Task TheSyncQueueNeverWritesAndKeepsTheChangesPending()
    {
        var (gate, runner) = NewGate(new FakeOnePassword());
        await gate.ConnectAsync(VaultBackendKind.OnePassword, readOnly: true);

        var vault = new GatedConfigVault(gate);
        var cache = new ConfigStoreCache(vault);
        await cache.InitializeAsync();

        var queue = new VaultSyncQueue(cache, vault, gate, Log());
        var callsBefore = runner.Invocations.Count;

        await cache.MutateAsync(store => store.Upstreams.Add(new UpstreamRecord { Name = "u", BaseUrl = "https://example.com" }));

        Assert.False(await queue.TrySyncAsync());
        Assert.False(await queue.FlushAsync(TimeSpan.FromSeconds(5)));
        Assert.False(await queue.RewriteAllAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal(callsBefore, runner.Invocations.Count);
        Assert.True(cache.HasPendingChanges);
        Assert.Equal(VaultSyncState.ReadOnly, queue.State);
        Assert.Contains(cache.Current.Upstreams, u => u.Name == "u");
    }

    [Fact]
    public async Task ChoosingABackendNormallyEndsReadOnly()
    {
        var (gate, _) = NewGate(new FakeOnePassword());
        await gate.ConnectAsync(VaultBackendKind.OnePassword, readOnly: true);

        gate.SelectBackend(VaultBackendKind.OnePassword);

        Assert.False(gate.IsReadOnly);
        Assert.IsNotType<ReadOnlyConfigVault>(gate.Selected);
    }

    private static bool IsWrite(IReadOnlyList<string> args) =>
        args.Count >= 2 && args[0] is "item" or "vault" && args[1] is "create" or "edit" or "delete";

    private (VaultGateService Gate, FakeCliRunner Runner) NewGate(FakeOnePassword onePassword)
    {
        var runner = onePassword.AsRunner();
        var gate = new VaultGateService(
            new OnePasswordVaultProvider(runner, Log(), _opStub),
            new ProtonPassVaultProvider(new FakeCliRunner(), Log(), Path.Combine(_stubDir, "gone.exe")),
            Log());

        return (gate, runner);
    }

    private ActivityLog Log() => new(_logPath);

    public void Dispose()
    {
        try { Directory.Delete(_stubDir, recursive: true); } catch { /* best effort */ }
        try { Directory.Delete(_logPath, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }
}
