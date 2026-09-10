using RavensPort.Core.Models;

namespace RavensPort.Core.Vault;

/// <summary>
/// An <see cref="IConfigVault"/> backed by a dictionary of items instead of a CLI. Ships in Core
/// rather than the test project because it is what every test needing a working store uses, and
/// because it is the honest stand-in for "a vault, minus the subprocess".
///
/// It runs the **real** <see cref="VaultMapper"/> over those items — the same split into a
/// redacted topology note plus one item per secret, the same index, the same reassembly on load,
/// the same delete reconciliation. A double that just held a <see cref="ConfigStore"/> would let
/// round-trip tests pass on a mapping that silently drops half the fields, which is exactly the
/// bug worth catching.
/// </summary>
public sealed class InMemoryVault : IConfigVault
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly VaultAvailability _availability;
    private readonly SaveBehavior _saveBehavior;

    /// <summary>
    /// What this vault calls itself. <see cref="VaultBackendKind.None"/> for the placeholder the
    /// gate holds before a backend is chosen, and <see cref="VaultBackendKind.SingleUse"/> when it
    /// is being used as one — see <see cref="ForSingleUse"/>.
    /// </summary>
    private readonly VaultBackendKind _kind;

    /// <summary>Item id to contents, standing in for the vault's own storage.</summary>
    private readonly Dictionary<string, VaultItemContents> _items = [];

    private long _revision;
    private int _nextItemId = 1;
    private string? _loadWarning;

    private enum SaveBehavior
    {
        Succeed,
        FailBeforeWriting,
        FailHalfway,
        Locked,
    }

    public InMemoryVault()
        : this(VaultAvailability.Ready, SaveBehavior.Succeed)
    {
    }

    private InMemoryVault(
        VaultAvailability availability,
        SaveBehavior saveBehavior,
        VaultBackendKind kind = VaultBackendKind.None)
    {
        _availability = availability;
        _saveBehavior = saveBehavior;
        _kind = kind;
    }

    /// <summary>
    /// The store for a single-use session: identical behaviour, but it identifies itself as a
    /// chosen backend so the sync queue drains into it and the tabs treat it as a live vault.
    /// Nothing it holds ever leaves this object, and the gate drops the whole instance on
    /// disconnect, which is what makes "purge from memory" true rather than approximately true.
    /// </summary>
    public static InMemoryVault ForSingleUse() =>
        new(VaultAvailability.Ready, SaveBehavior.Succeed, VaultBackendKind.SingleUse);

    public VaultBackendKind Kind => _kind;

    public string VaultName { get; private set; } = VaultConstants.VaultName;

    public string? LastLoadWarning { get; private set; }

    public IReadOnlyList<string> LastLoadRemovals { get; private set; } = [];

    /// <summary>Every item currently stored, for tests that assert on the vault's shape.</summary>
    public IReadOnlyCollection<VaultItemContents> Items => _items.Values;

    /// <summary>A working, initially empty vault.</summary>
    public static InMemoryVault Empty() => new();

    /// <summary>
    /// Fails every save before writing anything. Stands in for a vault that refuses the very first
    /// call — the case where ConfigStoreCache must roll its in-memory state back, because nothing
    /// durable changed.
    /// </summary>
    public static InMemoryVault ThatFailsBeforeWriting() =>
        new(VaultAvailability.Ready, SaveBehavior.FailBeforeWriting);

    /// <summary>
    /// Writes the secret items and then fails before the note. Stands in for a multi-item save
    /// that died part way: some of it is durable, so ConfigStoreCache must <em>not</em> roll back
    /// and must report being out of sync instead.
    /// </summary>
    public static InMemoryVault ThatFailsHalfway() =>
        new(VaultAvailability.Ready, SaveBehavior.FailHalfway);

    /// <summary>Probes as signed out and refuses writes with <see cref="VaultLockedException"/>.</summary>
    public static InMemoryVault ThatIsLocked() =>
        new(VaultAvailability.NotSignedIn, SaveBehavior.Locked);

    /// <summary>Seeds the vault as if the store had already been saved to it.</summary>
    public InMemoryVault Seeded(ConfigStore store)
    {
        WriteItems(store);
        return this;
    }

    /// <summary>
    /// Makes every load report an incomplete result — what a real backend does when a record's
    /// secret item has gone missing.
    /// </summary>
    public InMemoryVault WithLoadWarning(string warning)
    {
        _loadWarning = warning;
        return this;
    }

    /// <summary>Removes an item, standing in for one deleted in the password manager's own UI.</summary>
    public bool RemoveItem(string itemId) => _items.Remove(itemId);

    /// <summary>Adds an item this app does not own, which no save may ever delete.</summary>
    public void AddForeignItem(string title, string value)
    {
        var itemId = $"foreign-{_nextItemId++}";
        _items[itemId] = new VaultItemContents(itemId, title,
            new Dictionary<string, string> { [VaultFields.Password] = value });
    }

    /// <summary>Rewrites the config note, for testing recovery from a stale or broken index.</summary>
    public void EditConfigNote(Func<string, string> edit)
    {
        if (FindConfigNote() is not { } note) return;

        _items[note.ItemId] = note with
        {
            Fields = new Dictionary<string, string>
            {
                [VaultFields.NoteContent] = edit(note.Field(VaultFields.NoteContent) ?? ""),
            },
        };
    }

    public Task<VaultStatus> ProbeAsync(CancellationToken ct = default) =>
        Task.FromResult(new VaultStatus(Kind, _availability, VaultId: "in-memory"));

    /// <summary>Depth changes nothing here: there is no CLI to launch and nobody to prompt.</summary>
    public Task<VaultStatus> ProbeAsync(VaultProbeDepth depth, CancellationToken ct = default) =>
        ProbeAsync(ct);

    /// <summary>Records the name. There is only ever one vault here, and it always exists.</summary>
    public Task CreateVaultAsync(string vaultName, CancellationToken ct = default)
    {
        VaultName = vaultName;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Records the name. Identical to <see cref="CreateVaultAsync"/> on purpose: there is only ever
    /// one vault here, it always exists, and it is always usable — so "make me one" and "open the
    /// one I have" are the same act, and the distinction only matters to a real backend.
    /// </summary>
    public Task UseExistingVaultAsync(string vaultName, CancellationToken ct = default) =>
        CreateVaultAsync(vaultName, ct);

    /// <summary>
    /// Nothing to forget: this vault <em>is</em> its storage, so dropping the items would destroy
    /// the store rather than release a backend the app is disconnecting from.
    /// </summary>
    public void Forget()
    {
    }

    public async Task<ConfigStore> LoadAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            LastLoadWarning = _loadWarning;
            LastLoadRemovals = [];

            if (FindConfigNote() is not { } note) return new ConfigStore();

            var document = VaultDocument.TryParse(note.Field(VaultFields.NoteContent) ?? "");
            if (document is null) return new ConfigStore();

            _revision = document.Revision;

            var report = new VaultLoadReport();
            var store = VaultMapper.ComposeStore(document, ResolveSecrets(document.Index), report);

            LastLoadRemovals = report.Removals;

            if (report.HasAnything)
            {
                LastLoadWarning = string.Join(" ", new[] { _loadWarning, report.Message }
                    .Where(w => !string.IsNullOrEmpty(w)));
            }

            return store;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveAsync(ConfigStore store, CancellationToken ct = default)
    {
        if (_saveBehavior == SaveBehavior.Locked) throw new VaultLockedException(Kind);

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_saveBehavior == SaveBehavior.FailBeforeWriting)
            {
                throw new VaultSaveException("Simulated failure before any item was written.",
                    partiallyApplied: false);
            }

            WriteItems(store, stopBeforeTheNote: _saveBehavior == SaveBehavior.FailHalfway);

            if (_saveBehavior == SaveBehavior.FailHalfway)
            {
                throw new VaultSaveException("Simulated failure after some items were written.",
                    partiallyApplied: true);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Every write here already replaces the item outright, so this is just a save.</summary>
    public Task RewriteAllAsync(ConfigStore store, CancellationToken ct = default) => SaveAsync(store, ct);

    public Task<IReadOnlyList<VaultItemEntry>> ListLiveItemsAsync(CancellationToken ct = default)
    {
        IReadOnlyList<VaultItemEntry> live =
            [.. _items.Values.Select(item => VaultItemEntry.Classify(item.ItemId, item.Title))];

        return Task.FromResult(live);
    }

    public Task DeleteItemAsync(string itemId, CancellationToken ct = default)
    {
        if (!_items.Remove(itemId))
        {
            throw new VaultSaveException($"There is no item '{itemId}' to delete.", partiallyApplied: false);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// The same order a real provider uses: secret items first, note last, then reconcile
    /// deletions — so the note can never point at an item that does not exist.
    /// </summary>
    private void WriteItems(ConfigStore store, bool stopBeforeTheNote = false)
    {
        // The previous index finds the item a record already has; the new one is what the note
        // gets, holding only what this save wrote — the same split the real providers use, so a
        // record that is gone does not leave a dangling entry behind.
        var previousIndex = ReadIndex();
        var index = new VaultIndex();
        var secretItems = VaultMapper.BuildSecretItems(store, previousIndex);

        foreach (var item in secretItems)
        {
            var itemId = item.Spec.ItemId ?? $"item-{_nextItemId++}";

            _items[itemId] = new VaultItemContents(
                itemId,
                item.Spec.Title,
                item.Spec.Fields.ToDictionary(f => f.Name, f => f.Value));

            index.For(item.Role)[item.RecordId] = itemId;
            index.Fingerprints[item.RecordId] = item.Fingerprint;
        }

        if (stopBeforeTheNote) return;

        var note = VaultMapper.BuildConfigNote(store, index, ++_revision);
        var noteId = FindConfigNote()?.ItemId ?? $"item-{_nextItemId++}";

        _items[noteId] = new VaultItemContents(
            noteId,
            note.Title,
            note.Fields.ToDictionary(f => f.Name, f => f.Value));

        ReconcileDeletions(secretItems);
    }

    /// <summary>
    /// Deletes items this app owns whose record is gone, and never touches anything else — the
    /// vault belongs to the user, and their own entries must survive every save.
    /// </summary>
    private void ReconcileDeletions(List<VaultSecretItem> live)
    {
        var keep = live.Select(i => (i.Role, i.RecordId)).ToHashSet();

        foreach (var item in _items.Values.ToList())
        {
            if (!VaultItemNaming.TryParse(item.Title, out var role, out var id)) continue;
            if (role == VaultItemRole.Config) continue;

            if (!keep.Contains((role, id))) _items.Remove(item.ItemId);
        }
    }

    private VaultIndex ReadIndex() =>
        FindConfigNote() is { } note
        && VaultDocument.TryParse(note.Field(VaultFields.NoteContent) ?? "") is { } document
            ? document.Index
            : new VaultIndex();

    private Dictionary<(VaultItemRole, Guid), VaultItemContents> ResolveSecrets(VaultIndex index)
    {
        var resolved = new Dictionary<(VaultItemRole, Guid), VaultItemContents>();

        foreach (var role in VaultItemRoles.SecretBearing)
        {
            foreach (var (recordId, itemId) in index.For(role))
            {
                if (_items.TryGetValue(itemId, out var item)) resolved[(role, recordId)] = item;
            }
        }

        // Anything the index missed, recovered from the title — the same fallback a real provider
        // uses when the note is older than the items it points at.
        foreach (var item in _items.Values)
        {
            if (!VaultItemNaming.TryParse(item.Title, out var role, out var id)) continue;
            if (role == VaultItemRole.Config) continue;

            resolved.TryAdd((role, id), item);
        }

        return resolved;
    }

    private VaultItemContents? FindConfigNote() =>
        _items.Values.FirstOrDefault(i => i.Title == VaultItemNaming.ConfigTitle);
}
