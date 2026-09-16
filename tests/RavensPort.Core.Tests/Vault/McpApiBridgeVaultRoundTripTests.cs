using System.Text.Json;
using RavensPort.Core.Models;
using RavensPort.Core.Storage;
using RavensPort.Core.Vault;

namespace RavensPort.Core.Tests.Vault;

/// <summary>
/// A bridge survives a trip through the vault.
///
/// The manifest and the key take different paths — the manifest lives entirely on local disk now
/// (see <see cref="ManifestLocalStore"/>; a real vault backend was measured to reject a note past a
/// few tens of KB, well under what a manifest is allowed to be), while the key still gets an item of
/// its own in the vault. Both halves have a quiet failure mode: a manifest that does not round-trip
/// byte for byte changes which HTTP call gets made; a key that is written but never read back leaves
/// the endpoint answering 403 after the next restart with nothing logged.
/// </summary>
[Collection(RavensPort.Core.Tests.Storage.ManifestStoreCollection.Name)]
public class McpApiBridgeVaultRoundTripTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ravensport-bridge-vault-tests-" + Guid.NewGuid());

    public McpApiBridgeVaultRoundTripTests() => ManifestLocalStore.RootOverride = _root;

    public void Dispose()
    {
        ManifestLocalStore.RootOverride = null;

        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static McpApiBridgeRecord Bridge(string slug = "tracker") => new()
    {
        Name = "Task tracker",
        Slug = slug,
        RouteId = Guid.NewGuid(),
        Key = ProxyKey.Generate(TimeSpan.FromDays(30)),
        ManifestOrigin = "sample-api-mcp-manifest.json",
        Manifest = ReadSample(),
    };

    private static McpApiBridgeManifest ReadSample()
    {
        var error = McpApiBridgeValidation.TryReadManifest(McpApiBridgeSample.Read(), out var manifest);

        Assert.Null(error);

        return manifest!;
    }

    private static string Canonical(McpApiBridgeManifest manifest) =>
        JsonSerializer.Serialize(manifest, VaultRedaction.FullOptions);

    [Fact]
    public async Task TheManifestComesBackByteForByteFromLocalStorage()
    {
        var store = new ConfigStore();
        var bridge = Bridge();
        store.McpApiBridges.Add(bridge);

        var vault = InMemoryVault.Empty();
        await vault.SaveAsync(store);
        var reloaded = await vault.LoadAsync();

        var loaded = Assert.Single(reloaded.McpApiBridges);

        Assert.Equal(bridge.Slug, loaded.Slug);
        Assert.Equal(bridge.RouteId, loaded.RouteId);
        Assert.Equal(bridge.ManifestOrigin, loaded.ManifestOrigin);
        Assert.Equal(Canonical(bridge.Manifest), Canonical(loaded.Manifest));

        // Including the parts most likely to be dropped by a careless model change.
        var variantTool = Assert.Single(loaded.Manifest.Tools, t => t.HasVariants);
        Assert.Equal(3, variantTool.Variants.Count);
        Assert.NotEmpty(loaded.Manifest.Skills[0].Content);
        Assert.NotNull(loaded.Manifest.Instructions);

        // And it is genuinely on disk, not just in the reloaded ConfigStore.
        var onDisk = ManifestLocalStore.TryLoad(bridge.Id);
        Assert.NotNull(onDisk);
        Assert.Equal(Canonical(bridge.Manifest), Canonical(onDisk));
    }

    [Fact]
    public async Task TheKeyLivesInItsOwnItemAndTheNoteNeverHoldsTheManifest()
    {
        var store = new ConfigStore();
        var bridge = Bridge();
        store.McpApiBridges.Add(bridge);

        var vault = InMemoryVault.Empty();
        await vault.SaveAsync(store);

        var note = vault.Items.Single(i => i.Title == VaultItemNaming.ConfigTitle);
        Assert.DoesNotContain(bridge.Key.Value, note.Field(VaultFields.NoteContent), StringComparison.Ordinal);

        // The manifest is never in the vault at all now — not in the note, not in an item of its own.
        Assert.DoesNotContain("list_by_state", note.Field(VaultFields.NoteContent), StringComparison.Ordinal);
        Assert.DoesNotContain(vault.Items, i => i.Title.Contains("api bridge manifest —", StringComparison.Ordinal));

        // ...but the bridge itself still is, so the topology stays readable in one place.
        Assert.Contains(bridge.Slug, note.Field(VaultFields.NoteContent), StringComparison.Ordinal);

        var keyItem = vault.Items.Single(i => i.Title.Contains("api bridge key —", StringComparison.Ordinal));
        Assert.Equal(bridge.Key.Value, keyItem.Field(VaultFields.Password));
        Assert.Contains(bridge.Slug, keyItem.Title, StringComparison.Ordinal);

        var reloaded = await vault.LoadAsync();
        Assert.Equal(bridge.Key.Value, reloaded.McpApiBridges[0].Key.Value);
        Assert.Equal(bridge.Key.ExpiresUtc, reloaded.McpApiBridges[0].Key.ExpiresUtc);
    }

    [Fact]
    public async Task SavingSeveralBridgesWritesNoManifestItemsForAnyOfThem()
    {
        var store = new ConfigStore();
        var first = Bridge("tracker");
        var second = Bridge("billing");

        store.McpApiBridges.Add(first);
        store.McpApiBridges.Add(second);

        var vault = InMemoryVault.Empty();
        await vault.SaveAsync(store);

        Assert.DoesNotContain(vault.Items, i => i.Title.Contains("api bridge manifest —", StringComparison.Ordinal));

        var reloaded = await vault.LoadAsync();

        Assert.Equal(2, reloaded.McpApiBridges.Count);
        Assert.All(reloaded.McpApiBridges, b => Assert.NotEmpty(b.Manifest.Tools));
        Assert.NotNull(ManifestLocalStore.TryLoad(first.Id));
        Assert.NotNull(ManifestLocalStore.TryLoad(second.Id));
    }

    /// <summary>
    /// An install from before manifests moved out of the vault still has the old item sitting
    /// there. The first load after upgrading has to recover it once, write it to local storage, and
    /// leave the stale vault item to be swept on the bridge's next ordinary save — never delete the
    /// bridge itself over it, which is what used to happen when the manifest was the vault's only copy.
    /// </summary>
    [Fact]
    public async Task ALegacyManifestStillOnlyInTheVaultIsMigratedToLocalStorageOnLoad()
    {
        var store = new ConfigStore();
        var bridge = new McpApiBridgeRecord
        {
            Name = "legacy", Slug = "legacy", Key = ProxyKey.Generate(TimeSpan.FromDays(30)),
        };
        store.McpApiBridges.Add(bridge);

        var vault = InMemoryVault.Empty();
        await vault.SaveAsync(store); // an ordinary save today never creates a manifest item

        // Simulate the pre-upgrade item by hand — SaveAsync no longer produces one to build from.
        vault.AddForeignItem(VaultItemNaming.ForApiBridgeManifest(bridge.Id, bridge.Slug), "placeholder");
        var seeded = vault.Items.Single(i => i.Title.Contains("api bridge manifest —", StringComparison.Ordinal));
        vault.ReplaceItemField(seeded.ItemId, VaultFields.NoteContent, Canonical(ReadSample()));

        // The save above already wrote an empty manifest locally, since it ran with nothing in
        // memory yet — that could not happen in a real upgrade, where the *first* thing the new
        // code does for an existing bridge is load (and migrate), never save. Reset to the "never
        // written" state a genuine upgrade would actually start from.
        ManifestLocalStore.Delete(bridge.Id);
        Assert.Null(ManifestLocalStore.TryLoad(bridge.Id));

        var reloaded = await vault.LoadAsync();

        var loaded = Assert.Single(reloaded.McpApiBridges);
        Assert.NotEmpty(loaded.Manifest.Tools);
        Assert.Contains("moved", vault.LastLoadWarning ?? "", StringComparison.OrdinalIgnoreCase);

        var onDisk = ManifestLocalStore.TryLoad(bridge.Id);
        Assert.NotNull(onDisk);
        Assert.NotEmpty(onDisk.Tools);

        // The bridge's next ordinary save sweeps the now-unreferenced legacy item away...
        await vault.SaveAsync(reloaded);
        Assert.DoesNotContain(vault.Items, i => i.Title.Contains("api bridge manifest —", StringComparison.Ordinal));

        // ...and the manifest still comes back on a later load, now purely from disk.
        var reloadedAgain = await vault.LoadAsync();
        Assert.NotEmpty(Assert.Single(reloadedAgain.McpApiBridges).Manifest.Tools);
    }

    /// <summary>
    /// Corrupt content in a legacy vault item — free text a user could always edit by hand — costs
    /// that bridge its tools during migration, not the whole load, and never destroys the bridge.
    /// </summary>
    [Fact]
    public async Task ALegacyManifestItemEditedIntoNonsenseMigratesAsEmptyRatherThanThrowing()
    {
        var store = new ConfigStore();
        var bridge = new McpApiBridgeRecord
        {
            Name = "legacy", Slug = "legacy", Key = ProxyKey.Generate(TimeSpan.FromDays(30)),
        };
        store.McpApiBridges.Add(bridge);
        store.Routes.Add(new RouteMapping { PathPrefix = "/api" });

        var vault = InMemoryVault.Empty();
        await vault.SaveAsync(store);

        vault.AddForeignItem(VaultItemNaming.ForApiBridgeManifest(bridge.Id, bridge.Slug), "placeholder");
        var seeded = vault.Items.Single(i => i.Title.Contains("api bridge manifest —", StringComparison.Ordinal));
        vault.ReplaceItemField(seeded.ItemId, VaultFields.NoteContent, "{ this is not json");

        var reloaded = await vault.LoadAsync();

        Assert.Single(reloaded.McpApiBridges);
        Assert.Empty(reloaded.McpApiBridges[0].Manifest.Tools);
        Assert.Single(reloaded.Routes);
    }

    /// <summary>
    /// A bridge whose manifest cannot be found anywhere — no local file, nothing left in the vault
    /// either — simply serves no tools. It must never be destroyed for that: unlike a credential's
    /// secret, the vault stopped being the only copy of a manifest, so the old "the item is gone,
    /// drop the whole record" rule no longer applies here.
    /// </summary>
    [Fact]
    public async Task ABridgeWithNoManifestAnywhereJustServesNothingRatherThanBeingDropped()
    {
        var store = new ConfigStore();
        store.McpApiBridges.Add(new McpApiBridgeRecord
        {
            Name = "empty", Slug = "empty", Key = ProxyKey.Generate(TimeSpan.FromDays(30)),
        });

        var vault = InMemoryVault.Empty();
        await vault.SaveAsync(store);

        var reloaded = await vault.LoadAsync();

        var loaded = Assert.Single(reloaded.McpApiBridges);
        Assert.Empty(loaded.Manifest.Tools);
        Assert.Empty(vault.LastLoadRemovals);
    }

    [Fact]
    public async Task TheKeyIsFoundAgainEvenWhenTheIndexHasLostIt()
    {
        var store = new ConfigStore();
        var bridge = Bridge();
        store.McpApiBridges.Add(bridge);

        var vault = InMemoryVault.Empty();
        await vault.SaveAsync(store);

        var indexed = VaultDocument.TryParse(
            vault.Items.Single(i => i.Title == VaultItemNaming.ConfigTitle).Field(VaultFields.NoteContent) ?? "");

        Assert.NotNull(indexed);
        Assert.True(indexed.Index.ApiBridgeKeys.ContainsKey(bridge.Id));

        vault.EditConfigNote(json =>
        {
            var document = VaultDocument.TryParse(json)!;
            document.Index.ApiBridgeKeys.Clear();
            return document.Serialize();
        });

        var reloaded = await vault.LoadAsync();
        Assert.Equal(bridge.Key.Value, reloaded.McpApiBridges[0].Key.Value);
    }

    [Fact]
    public async Task ABridgeThatArrivedWithNoKeyIsIssuedOne()
    {
        var store = new ConfigStore();
        store.McpApiBridges.Add(new McpApiBridgeRecord { Name = "hand made", Slug = "hand-made" });

        var vault = InMemoryVault.Empty();
        await vault.SaveAsync(store);

        var cache = new ConfigStoreCache(vault);
        await cache.InitializeAsync();

        Assert.True(cache.Current.McpApiBridges[0].Key.IsConfigured);
        Assert.Null(cache.Current.McpApiBridges[0].Key.ExpiresUtc);
    }

    /// <summary>
    /// The snapshot the cache restores through is a positional record, so a list left out of it
    /// compiles and then silently empties itself on the next reload while the vault still has the
    /// data.
    /// </summary>
    [Fact]
    public async Task AReloadDoesNotDropBridgesFromMemory()
    {
        var store = new ConfigStore();
        store.McpApiBridges.Add(Bridge());
        store.Settings.McpApiBridgeEnabled = true;

        var vault = InMemoryVault.Empty();
        await vault.SaveAsync(store);

        var cache = new ConfigStoreCache(vault);
        await cache.InitializeAsync();
        Assert.Single(cache.Current.McpApiBridges);

        await cache.ReloadAsync();

        Assert.Single(cache.Current.McpApiBridges);
        Assert.True(cache.Current.Settings.McpApiBridgeEnabled);
        Assert.NotEmpty(cache.Current.McpApiBridges[0].Manifest.Tools);
    }
}
