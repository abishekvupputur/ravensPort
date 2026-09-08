using RavensPort.Core.Diagnostics;
using RavensPort.Core.Models;
using RavensPort.Core.Storage;
using RavensPort.Core.Vault;

namespace RavensPort.Core.Tests.Vault;

/// <summary>
/// The vault and the configuration drifting apart, and the tools for putting them back together.
///
/// Drift is not exotic: an item deleted or restored in the password manager's own UI, a save that
/// died between writing an item and writing the note, a second machine on the same vault. Ordinary
/// saves sweep what the current store refers to, so anything the store has forgotten stays in the
/// vault unreferenced and invisible — which is what this check is for.
/// </summary>
public class VaultIntegrityTests : IDisposable
{
    private readonly string _logPath = Path.Combine(Path.GetTempPath(), $"ravensport-integrity-{Guid.NewGuid()}");

    [Fact]
    public async Task AnItemNothingRefersToIsReportedAndCanBeDeleted()
    {
        var (vault, cache, integrity) = await LoadedAsync();

        // What a failed delete leaves: an owned item for a record the configuration has never
        // heard of. A save would not touch it — reconciliation only looks at records that exist.
        vault.AddForeignItem(VaultItemNaming.ForCredential(Guid.NewGuid(), "ghost"), "leftover");

        var report = await integrity.CheckAsync();

        var orphan = Assert.Single(report.Orphans);
        Assert.Contains("ghost", orphan.Title);
        Assert.False(report.IsHealthy);

        Assert.Equal(1, await integrity.DeleteItemsAsync([orphan]));
        Assert.DoesNotContain(vault.Items, i => i.ItemId == orphan.ItemId);

        Assert.True((await integrity.CheckAsync()).IsHealthy);
    }

    [Fact]
    public async Task ASecondItemClaimingTheSameRecordIsReported()
    {
        // Two items for one record is worse than untidy: the proxy accepts whichever the note
        // points at, and the other reads like a working key that opens nothing.
        var (vault, cache, integrity) = await LoadedAsync();

        var credential = cache.Current.Credentials[0];
        vault.AddForeignItem(VaultItemNaming.ForCredential(credential.Id, credential.Name), "duplicate");

        var report = await integrity.CheckAsync();

        Assert.Contains(report.Orphans, o => o.Reason.Contains("second item"));
    }

    [Fact]
    public async Task ARecordWhoseItemIsGoneIsReportedAndCanBeDropped()
    {
        var (vault, cache, integrity) = await LoadedAsync();

        var credentialItem = vault.Items.Single(i =>
            VaultItemNaming.TryParse(i.Title, out var role, out _) && role == VaultItemRole.Credential);
        vault.RemoveItem(credentialItem.ItemId);

        var report = await integrity.CheckAsync();

        var missing = Assert.Single(report.Missing);
        Assert.Equal(VaultItemRole.Credential, missing.Role);
        Assert.Contains("only in memory", missing.Consequence);

        Assert.Equal(1, await integrity.DropRecordsAsync([missing]));

        Assert.Empty(cache.Current.Credentials);

        // The route that used it survives, minus the reference — a route pointing at a credential
        // that is gone would look configured and forward nothing.
        Assert.NotEmpty(cache.Current.Routes);
        Assert.Empty(cache.Current.Routes.SelectMany(r => r.Credentials));
    }

    [Fact]
    public async Task WritingPutsAMissingItemBackWithoutLosingTheSecret()
    {
        // The non-destructive answer to "missing": the secret is still in memory, so writing it
        // back costs nothing. Dropping the record is the other option and it is a real loss.
        var (vault, _, integrity) = await LoadedAsync();

        var credentialItem = vault.Items.Single(i =>
            VaultItemNaming.TryParse(i.Title, out var role, out _) && role == VaultItemRole.Credential);
        vault.RemoveItem(credentialItem.ItemId);

        Assert.Single((await integrity.CheckAsync()).Missing);

        Assert.True(await integrity.WriteMissingToVaultAsync(TimeSpan.FromSeconds(30)));

        var report = await integrity.CheckAsync();
        Assert.True(report.IsHealthy);

        var reloaded = await vault.LoadAsync();
        Assert.Equal("secret", reloaded.Credentials[0].ClientSecret);
    }

    [Fact]
    public async Task RewritingAllItemsAlsoRestoresWhatIsMissing()
    {
        var (vault, _, integrity) = await LoadedAsync();

        var credentialItem = vault.Items.Single(i =>
            VaultItemNaming.TryParse(i.Title, out var role, out _) && role == VaultItemRole.Credential);
        vault.RemoveItem(credentialItem.ItemId);

        Assert.True(await integrity.RewriteAllItemsAsync(TimeSpan.FromSeconds(30)));

        Assert.True((await integrity.CheckAsync()).IsHealthy);
        Assert.Equal("secret", (await vault.LoadAsync()).Credentials[0].ClientSecret);
    }

    [Fact]
    public async Task ItemsThatAreNotRavensPortsAreReportedRatherThanIgnored()
    {
        // Every reader filters to items titled as this app's, which is right for saving and wrong
        // for looking: a check that cannot see the rest of the vault cannot account for it, and
        // the user has no way to tell an empty-looking result from an unseen one.
        var (vault, _, integrity) = await LoadedAsync();

        vault.AddForeignItem("Bank login", "hunter2");
        vault.AddForeignItem("Wifi", "password");

        var report = await integrity.CheckAsync();

        Assert.Equal(2, report.Others.Count);
        Assert.Contains(report.Others, other => other.Title == "Bank login");
        Assert.Contains("2 other item(s)", report.Summary);

        // Someone else's items are not a fault, and no bulk action may touch them.
        Assert.True(report.IsHealthy);
        Assert.Empty(report.Orphans);
    }

    [Fact]
    public async Task AnItemTitledAsOursButUnparseableIsReportedAsAnOrphan()
    {
        // The worst case of all: reconciliation matches on the record id in the title, so an item
        // whose title was edited is one no save will ever touch again. Before this it was in
        // neither list — invisible to the app and to the check.
        var (vault, _, integrity) = await LoadedAsync();

        vault.AddForeignItem("RavensPort credential — renamed by hand", "secret");

        var report = await integrity.CheckAsync();

        var orphan = Assert.Single(report.Orphans);
        Assert.Contains("no save will touch it", orphan.Reason);

        // Not double-counted as somebody else's item.
        Assert.Empty(report.Others);
        Assert.False(report.IsHealthy);
    }

    [Fact]
    public async Task DeletingSomebodyElsesItemIsOnePerCallAndNeverInBulk()
    {
        var (vault, _, integrity) = await LoadedAsync();

        vault.AddForeignItem("Bank login", "hunter2");

        var report = await integrity.CheckAsync();
        var other = Assert.Single(report.Others);

        // Bulk delete only ever takes orphans, so a vault full of the user's own entries cannot be
        // swept by one wrong click.
        Assert.Equal(0, await integrity.DeleteItemsAsync(report.Orphans));
        Assert.Contains(vault.Items, i => i.Title == "Bank login");

        await integrity.DeleteOtherItemAsync(other);
        Assert.DoesNotContain(vault.Items, i => i.Title == "Bank login");
    }

    [Fact]
    public async Task AVaultThatMatchesTheConfigurationIsHealthy()
    {
        var (_, _, integrity) = await LoadedAsync();

        var report = await integrity.CheckAsync();

        Assert.True(report.IsHealthy);
        Assert.True(report.ConfigItemPresent);
        Assert.Contains("Healthy", report.Summary);
    }

    /// <summary>A saved store, loaded into a cache, with an integrity service over both.</summary>
    private async Task<(InMemoryVault Vault, ConfigStoreCache Cache, VaultIntegrityService Integrity)> LoadedAsync()
    {
        var vault = InMemoryVault.Empty();

        var credential = new CredentialRecord { Name = "cred", ClientId = "id", ClientSecret = "secret" };
        var store = new ConfigStore();
        store.Credentials.Add(credential);
        store.Upstreams.Add(new UpstreamRecord { Name = "up", BaseUrl = "https://example.test" });
        store.Routes.Add(new RouteMapping
        {
            PathPrefix = "/api",
            UpstreamId = store.Upstreams[0].Id,
            Credentials = [RouteCredential.For(credential.Id, CredentialPlacement.Header)],
        });

        await vault.SaveAsync(store);

        var cache = new ConfigStoreCache(vault);
        await cache.InitializeAsync();

        // Loading issues a key to any route that has none, so the vault is one item behind until
        // that is written back. Without this the fixture itself would look broken.
        await vault.SaveAsync(cache.Current);

        var gate = new VaultGateService(
            new OnePasswordVaultProvider(new FakeCliRunner(), Log(), "missing.exe"),
            new ProtonPassVaultProvider(new FakeCliRunner(), Log(), "missing.exe"),
            Log());

        // The queue writes through whatever the gate selected, so it has to be pointed at this
        // vault — the same wiring the app has once a backend is resolved.
        gate.SelectBackend(VaultBackendKind.ProtonPass);

        var queue = new VaultSyncQueue(cache, vault, gate, Log());

        return (vault, cache, new VaultIntegrityService(vault, cache, queue, Log()));
    }

    private ActivityLog Log() => new(_logPath);

    public void Dispose()
    {
        try { Directory.Delete(_logPath, recursive: true); } catch { /* best effort */ }

        // Nothing here has a finalizer, but the pattern is what CA1816 asks for and what a
        // derived test fixture would need if one ever did.
        GC.SuppressFinalize(this);
    }
}
