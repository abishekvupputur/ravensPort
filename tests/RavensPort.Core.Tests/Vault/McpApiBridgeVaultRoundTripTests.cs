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

        // ...but the manifest is in the note, which is the whole point of keeping it there.
        Assert.Contains("list_by_state", note.Field(VaultFields.NoteContent), StringComparison.Ordinal);

        var keyItem = vault.Items.Single(i => i.Title.Contains("api bridge key —", StringComparison.Ordinal));
        Assert.Equal(bridge.Key.Value, keyItem.Field(VaultFields.Password));
        Assert.Contains(bridge.Slug, keyItem.Title, StringComparison.Ordinal);

        var reloaded = await vault.LoadAsync();
        Assert.Equal(bridge.Key.Value, reloaded.McpApiBridges[0].Key.Value);
        Assert.Equal(bridge.Key.ExpiresUtc, reloaded.McpApiBridges[0].Key.ExpiresUtc);
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
