using System.Text.Json;
using RavensPort.Core.Models;
using RavensPort.Core.Storage;
using RavensPort.Core.Vault;

namespace RavensPort.Core.Tests.Vault;

/// <summary>
/// A bridge survives a trip through the vault.
///
/// The manifest and the key take different paths — the manifest rides in the topology note, the
/// key gets an item of its own — and both halves have a quiet failure mode. A manifest that does
/// not round-trip byte for byte changes which HTTP call gets made; a key that is written but never
/// read back leaves the endpoint answering 403 after the next restart with nothing logged.
/// </summary>
public class McpApiBridgeVaultRoundTripTests
{
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
    public async Task TheManifestComesBackByteForByte()
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
    }

    [Fact]
    public async Task TheKeyLivesInItsOwnItemAndTheNoteNeverHoldsIt()
    {
        var store = new ConfigStore();
        var bridge = Bridge();
        store.McpApiBridges.Add(bridge);

        var vault = InMemoryVault.Empty();
        await vault.SaveAsync(store);

        var note = vault.Items.Single(i => i.Title == VaultItemNaming.ConfigTitle);
        Assert.DoesNotContain(bridge.Key.Value, note.Field(VaultFields.NoteContent), StringComparison.Ordinal);

        // The manifest is not in the note either. It is the largest thing a user writes, and the
        // note is rewritten in full on every save — including every token refresh.
        Assert.DoesNotContain("list_by_state", note.Field(VaultFields.NoteContent), StringComparison.Ordinal);

        // ...but the bridge itself still is, so the topology stays readable in one place.
        Assert.Contains(bridge.Slug, note.Field(VaultFields.NoteContent), StringComparison.Ordinal);

        var keyItem = vault.Items.Single(i => i.Title.Contains("api bridge key —", StringComparison.Ordinal));
        Assert.Equal(bridge.Key.Value, keyItem.Field(VaultFields.Password));
        Assert.Contains(bridge.Slug, keyItem.Title, StringComparison.Ordinal);

        var reloaded = await vault.LoadAsync();
        Assert.Equal(bridge.Key.Value, reloaded.McpApiBridges[0].Key.Value);
        Assert.Equal(bridge.Key.ExpiresUtc, reloaded.McpApiBridges[0].Key.ExpiresUtc);
    }

    /// <summary>
    /// The manifest gets an item of its own, named after the endpoint it belongs to, so someone
    /// looking through their password manager can find and read what an agent is being offered
    /// without opening the topology note at all.
    /// </summary>
    [Fact]
    public async Task TheManifestLivesInItsOwnItemPerBridge()
    {
        var store = new ConfigStore();
        var first = Bridge("tracker");
        var second = Bridge("billing");

        store.McpApiBridges.Add(first);
        store.McpApiBridges.Add(second);

        var vault = InMemoryVault.Empty();
        await vault.SaveAsync(store);

        var manifests = vault.Items
            .Where(i => i.Title.Contains("api bridge manifest —", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(2, manifests.Count);
        Assert.Contains(manifests, i => i.Title.Contains("/api-mcp/tracker", StringComparison.Ordinal));
        Assert.Contains(manifests, i => i.Title.Contains("/api-mcp/billing", StringComparison.Ordinal));

        // One item per bridge, per role: the manifest and the key never share one.
        Assert.All(manifests, item => Assert.Contains("list_by_state", item.Field(VaultFields.NoteContent)!, StringComparison.Ordinal));
        Assert.All(manifests, item => Assert.Null(item.Field(VaultFields.Password)));

        var reloaded = await vault.LoadAsync();

        Assert.Equal(2, reloaded.McpApiBridges.Count);
        Assert.All(reloaded.McpApiBridges, b => Assert.NotEmpty(b.Manifest.Tools));
    }

    /// <summary>
    /// The wording of the two bridge roles overlaps — "api bridge key" and "api bridge manifest" —
    /// so the title scan that recovers a lost index has to tell them apart.
    /// </summary>
    [Fact]
    public async Task TheTwoBridgeItemsAreNotConfusedForEachOther()
    {
        var store = new ConfigStore();
        var bridge = Bridge();
        store.McpApiBridges.Add(bridge);

        var vault = InMemoryVault.Empty();
        await vault.SaveAsync(store);

        vault.EditConfigNote(json =>
        {
            var document = VaultDocument.TryParse(json)!;
            document.Index = new VaultIndex();
            return document.Serialize();
        });

        var reloaded = await vault.LoadAsync();
        var loaded = Assert.Single(reloaded.McpApiBridges);

        Assert.Equal(bridge.Key.Value, loaded.Key.Value);
        Assert.Equal(Canonical(bridge.Manifest), Canonical(loaded.Manifest));
    }

    /// <summary>
    /// A manifest item deleted in the password manager takes its bridge with it, the same rule a
    /// credential follows: the vault is the only copy, and a bridge with no tools left to serve
    /// would raise the same ghost on every launch.
    /// </summary>
    [Fact]
    public async Task ABridgeWhoseManifestItemIsGoneIsRemovedAndReported()
    {
        var store = new ConfigStore();
        var bridge = Bridge();
        store.McpApiBridges.Add(bridge);

        var vault = InMemoryVault.Empty();
        await vault.SaveAsync(store);

        var manifestItem = vault.Items.Single(i => i.Title.Contains("api bridge manifest —", StringComparison.Ordinal));
        await vault.DeleteItemAsync(manifestItem.ItemId);

        var reloaded = await vault.LoadAsync();

        Assert.Empty(reloaded.McpApiBridges);
        Assert.Contains(vault.LastLoadRemovals, r => r.Contains(bridge.Name, StringComparison.Ordinal));
    }

    /// <summary>
    /// The item is free text a user can edit by hand, so broken JSON in it is a real case. It
    /// costs that bridge its tools, not the whole load.
    /// </summary>
    [Fact]
    public async Task AManifestItemEditedIntoNonsenseLoadsEmptyRatherThanThrowing()
    {
        var store = new ConfigStore();
        store.McpApiBridges.Add(Bridge());
        store.Routes.Add(new RouteMapping { PathPrefix = "/api" });

        var vault = InMemoryVault.Empty();
        await vault.SaveAsync(store);

        var manifestItem = vault.Items.Single(i => i.Title.Contains("api bridge manifest —", StringComparison.Ordinal));
        vault.ReplaceItemField(manifestItem.ItemId, VaultFields.NoteContent, "{ this is not json");

        var reloaded = await vault.LoadAsync();

        Assert.Single(reloaded.McpApiBridges);
        Assert.Empty(reloaded.McpApiBridges[0].Manifest.Tools);
        Assert.Single(reloaded.Routes);
    }

    /// <summary>
    /// The index is a cache, and the fallback is a title scan. A role missing from either is the
    /// failure this feature is most likely to ship with, because it only shows up on the second
    /// run.
    /// </summary>
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
