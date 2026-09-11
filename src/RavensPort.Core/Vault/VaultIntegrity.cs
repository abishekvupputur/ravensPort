using RavensPort.Core.Diagnostics;
using RavensPort.Core.Models;
using RavensPort.Core.Storage;

namespace RavensPort.Core.Vault;

/// <summary>
/// One live item in the vault, whether or not this app owns it.
///
/// Everything is returned, not just items named "RavensPort …". Filtering by title is right for
/// saving — it is what keeps the user's own entries out of reach of delete reconciliation — but it
/// is wrong for looking: an item of ours whose title was edited stops matching, and from then on no
/// save can see it, reconcile it, or ever delete it. Only a reader that sees everything can report
/// that it is there.
/// </summary>
/// <param name="Role">Parsed from the title. <see cref="VaultItemRole.Config"/> for the note.</param>
/// <param name="RecordId">Empty when the title carries no record id.</param>
/// <param name="IsOwned">Whether the title parses as one of this app's items.</param>
/// <param name="UpdatedUtc">
/// When the item last changed, where the backend reports it, and null where it does not. Useful for
/// the same reason a file's mtime is: it says when a vault was last written without reading
/// anything out of it.
/// </param>
public sealed record VaultItemEntry(
    string ItemId, string Title, VaultItemRole Role, Guid RecordId, bool IsOwned,
    DateTimeOffset? UpdatedUtc = null)
{
    /// <summary>Reads what a title says about an item. One place, so both backends agree.</summary>
    public static VaultItemEntry Classify(string itemId, string title, DateTimeOffset? updatedUtc = null)
    {
        var owned = VaultItemNaming.TryParse(title, out var role, out var recordId);

        return new VaultItemEntry(
            itemId, title, owned ? role : default, owned ? recordId : Guid.Empty, owned, updatedUtc);
    }
}

/// <summary>An item in the vault that the configuration does not account for.</summary>
public sealed record VaultOrphanItem(string ItemId, string Title, string Reason);

/// <summary>A record in the configuration whose item is not in the vault.</summary>
public sealed record VaultMissingItem(VaultItemRole Role, Guid RecordId, string Title, string Consequence);

/// <summary>
/// What the vault holds versus what the configuration says it should.
/// </summary>
/// <param name="Others">
/// Live items in the vault that are not this app's. Reported so the picture is complete — an
/// RavensPort item someone renamed lands here, and it is the only place it is visible — but never
/// touched by anything automatic.
/// </param>
public sealed record VaultIntegrityReport(
    IReadOnlyList<VaultOrphanItem> Orphans,
    IReadOnlyList<VaultMissingItem> Missing,
    IReadOnlyList<VaultItemEntry> Others,
    int OwnedItems,
    bool ConfigItemPresent)
{
    /// <summary>Only ever about this app's own items. Someone else's are not a fault.</summary>
    public bool IsHealthy => Orphans.Count == 0 && Missing.Count == 0 && ConfigItemPresent;

    public string Summary => string.Join(" ",
        new[]
        {
            IsHealthy ? $"Healthy — {OwnedItems} RavensPort item(s), all accounted for." : null,
            ConfigItemPresent || IsHealthy ? null : "There is no RavensPort Config item in this vault.",
            Orphans.Count > 0 ? $"{Orphans.Count} item(s) the configuration does not refer to." : null,
            Missing.Count > 0 ? $"{Missing.Count} record(s) whose item is missing." : null,
            Others.Count > 0 ? $"{Others.Count} other item(s) in this vault, which RavensPort leaves alone." : null,
        }.Where(part => part is not null));
}

/// <summary>
/// Compares the vault against the configuration and repairs the difference, on demand.
///
/// The two can drift for reasons no save can prevent: an item deleted or restored in the password
/// manager's own UI, a save that died between writing an item and writing the note, a second
/// machine writing the same vault. Ordinary saves already sweep what they can see, but they only
/// look at what the current store refers to — so anything the store has forgotten about stays in
/// the vault forever, unreferenced and invisible.
///
/// Nothing here happens on its own. Deleting a vault item and dropping a record are both losses
/// the user has to choose, so this reports first and acts only when told.
/// </summary>
public sealed class VaultIntegrityService(
    IConfigVault vault,
    ConfigStoreCache configStoreCache,
    VaultSyncQueue syncQueue,
    ActivityLog activityLog)
{
    public async Task<VaultIntegrityReport> CheckAsync(CancellationToken ct = default)
    {
        var all = await vault.ListLiveItemsAsync(ct);
        var store = configStoreCache.Current;

        var items = all.Where(i => i.IsOwned).ToList();
        var others = all.Where(i => !i.IsOwned).ToList();

        // The same list a save would write, built against an empty index so it describes what
        // *should* exist rather than what the note happens to point at.
        var expected = VaultMapper.BuildSecretItems(store, new VaultIndex());
        var expectedByRecord = expected.ToDictionary(item => (item.Role, item.RecordId));

        var orphans = new List<VaultOrphanItem>();

        // Titled as one of ours but not in a shape anything can match — a record id edited away,
        // or a title truncated. Worth calling out on its own: reconciliation matches on that shape,
        // so no save will ever touch this item again, and it would otherwise sit there forever.
        foreach (var malformed in others.Where(i => VaultItemNaming.IsOwned(i.Title)))
        {
            orphans.Add(new VaultOrphanItem(malformed.ItemId, malformed.Title,
                "named as one of RavensPort's items but not in a shape it can match, so no save will touch it"));
        }

        others = others.Where(i => !VaultItemNaming.IsOwned(i.Title)).ToList();

        foreach (var group in items.Where(i => i.Role != VaultItemRole.Config)
                     .GroupBy(i => (i.Role, i.RecordId)))
        {
            if (!expectedByRecord.ContainsKey(group.Key))
            {
                foreach (var item in group)
                {
                    orphans.Add(new VaultOrphanItem(item.ItemId, item.Title,
                        "nothing in the configuration refers to it"));
                }

                continue;
            }

            // A record with two items is what a failed delete leaves behind, and it is worse than
            // untidy: the proxy accepts whichever the note points at, and the other one reads like
            // a working key that opens nothing.
            foreach (var duplicate in group.Skip(1))
            {
                orphans.Add(new VaultOrphanItem(duplicate.ItemId, duplicate.Title,
                    "a second item claiming the same record"));
            }
        }

        var configItems = items.Where(i => i.Role == VaultItemRole.Config).ToList();

        foreach (var duplicate in configItems.Skip(1))
        {
            orphans.Add(new VaultOrphanItem(duplicate.ItemId, duplicate.Title,
                "a second configuration item — only one can be the real one"));
        }

        var present = items.Select(i => (i.Role, i.RecordId)).ToHashSet();

        var missing = expected
            .Where(item => !present.Contains((item.Role, item.RecordId)))
            .Select(item => new VaultMissingItem(item.Role, item.RecordId, item.Spec.Title, Consequence(item.Role)))
            .ToList();

        var report = new VaultIntegrityReport(orphans, missing, others, items.Count, configItems.Count > 0);
        activityLog.Log($"VAULT integrity check — {report.Summary}");

        return report;
    }

    /// <summary>
    /// What losing this item costs, so "missing" is a decision the user can make rather than a
    /// word they have to interpret.
    /// </summary>
    private static string Consequence(VaultItemRole role) => role switch
    {
        VaultItemRole.Credential =>
            "its secret exists only in memory — it is lost when RavensPort exits unless it is written back",
        VaultItemRole.RouteKey =>
            "the route's key exists only in memory — clients keep working until RavensPort exits",
        VaultItemRole.FunnelKey =>
            "the funnel's key exists only in memory — clients keep working until RavensPort exits",
        VaultItemRole.ApiBridgeKey =>
            "the API bridge's key exists only in memory — clients keep working until RavensPort exits",
        VaultItemRole.ApiBridgeManifest =>
            "the API bridge's tool definitions exist only in memory — they are lost when RavensPort exits unless they are written back",
        _ => "it is not in the vault",
    };

    /// <summary>Deletes items the user picked out of the report. Returns how many went.</summary>
    public async Task<int> DeleteItemsAsync(IEnumerable<VaultOrphanItem> orphans, CancellationToken ct = default)
    {
        var deleted = 0;

        foreach (var orphan in orphans)
        {
            await vault.DeleteItemAsync(orphan.ItemId, ct);
            activityLog.Log($"VAULT integrity — deleted '{orphan.Title}' ({orphan.Reason})");
            deleted++;
        }

        return deleted;
    }

    /// <summary>
    /// Deletes an item that is not this app's, one at a time and only when asked by name.
    ///
    /// Offered because the check is the only place these are visible, and a RavensPort item
    /// someone renamed can only be cleaned up from here. Deliberately not part of any bulk action:
    /// the rest of the vault is the user's, and nothing in this app may sweep it.
    /// </summary>
    public async Task DeleteOtherItemAsync(VaultItemEntry item, CancellationToken ct = default)
    {
        await vault.DeleteItemAsync(item.ItemId, ct);
        activityLog.Log($"VAULT integrity — deleted '{item.Title}', which was not one of RavensPort's items");
    }

    /// <summary>
    /// Drops the records whose items are gone, for a user who would rather lose the record than
    /// keep it. The alternative — <see cref="WriteMissingToVaultAsync"/> — writes the in-memory
    /// secret back instead, and is the better answer while the app is still running.
    /// </summary>
    public async Task<int> DropRecordsAsync(
        IReadOnlyCollection<VaultMissingItem> records, CancellationToken ct = default)
    {
        if (records.Count == 0) return 0;

        var dropped = 0;

        await configStoreCache.MutateAsync(store =>
        {
            foreach (var record in records)
            {
                if (!Drop(store, record)) continue;

                activityLog.Log($"VAULT integrity — removed '{record.Title}' from the configuration");
                dropped++;
            }
        }, ct);

        return dropped;
    }

    /// <summary>
    /// Removes one record from the store. False means there was nothing to remove — an unknown
    /// role, or a record the user had already deleted — and nothing is logged or counted for it.
    /// </summary>
    private static bool Drop(ConfigStore store, VaultMissingItem record) => record.Role switch
    {
        VaultItemRole.Credential => DropCredential(store, record.RecordId),
        VaultItemRole.RouteKey => store.Routes.RemoveAll(r => r.Id == record.RecordId) > 0,
        VaultItemRole.FunnelKey => store.McpFunnels.RemoveAll(f => f.Id == record.RecordId) > 0,
        VaultItemRole.ApiBridgeKey => DropApiBridge(store, record.RecordId),
        VaultItemRole.ApiBridgeManifest => DropApiBridge(store, record.RecordId),
        _ => false,
    };

    /// <summary>
    /// Removes a bridge, and any funnel source that pointed at it. A source left behind would
    /// name a bridge that no longer exists and fail on every connect — the same silent
    /// half-configured state that dropping a credential's routes avoids one layer up.
    /// </summary>
    private static bool DropApiBridge(ConfigStore store, Guid bridgeId)
    {
        if (store.McpApiBridges.RemoveAll(b => b.Id == bridgeId) == 0) return false;

        var stranded = store.McpSources
            .Where(s => s.Kind == McpSourceKind.ApiBridge && s.BridgeId == bridgeId)
            .Select(s => s.Id)
            .ToList();

        foreach (var sourceId in stranded)
        {
            store.McpSources.RemoveAll(s => s.Id == sourceId);

            foreach (var funnel in store.McpFunnels)
            {
                funnel.Sources.RemoveAll(link => link.SourceId == sourceId);
            }
        }

        return true;
    }

    private static bool DropCredential(ConfigStore store, Guid credentialId)
    {
        if (store.Credentials.RemoveAll(c => c.Id == credentialId) == 0) return false;

        // A route left pointing at it would look configured and forward nothing.
        foreach (var route in store.Routes)
        {
            route.Credentials.RemoveAll(c => c.CredentialId == credentialId);
        }

        return true;
    }

    /// <summary>
    /// Writes every item and the note again from what is in memory. Goes through the sync queue so
    /// it cannot race the background writer — one writer, whoever asked for it.
    /// </summary>
    public Task<bool> RewriteAllItemsAsync(TimeSpan timeout) => syncQueue.RewriteAllAsync(timeout);

    /// <summary>
    /// Puts back whatever the vault is missing, from memory. An ordinary save rather than a
    /// rewrite: it creates what is not there and leaves everything else untouched, so restoring
    /// one deleted item does not churn every other entry in the user's vault.
    ///
    /// The non-destructive answer to a missing item, and the reason dropping the record is only
    /// ever offered beside it: while the app is running, the secret is still in memory and putting
    /// it back costs nothing.
    /// </summary>
    public Task<bool> WriteMissingToVaultAsync(TimeSpan timeout) => syncQueue.WriteMissingAsync(timeout);
}
