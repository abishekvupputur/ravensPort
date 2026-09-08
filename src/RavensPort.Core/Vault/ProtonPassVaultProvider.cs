using System.Text.Json;
using System.Text.Json.Nodes;
using RavensPort.Core.Diagnostics;
using RavensPort.Core.Models;

namespace RavensPort.Core.Vault;

/// <summary>
/// The store, backed by the Proton Pass CLI (<c>pass-cli</c>).
///
/// Shaped by one constraint the 1Password provider does not have: <c>pass-cli item update</c>
/// takes its values as <c>--field name=value</c> arguments and offers no other way in. A Windows
/// process command line is readable by any process in the session, so using it would publish every
/// client secret and token this app touches.
///
/// <c>item create</c> does accept a template on stdin (<c>--from-template -</c>), so this provider
/// never updates. A changed record is written as a new item, the note is rewritten to point at it,
/// and only then is the old item deleted. That ordering keeps the invariant — the note never
/// references an item that is gone. The cost is that Proton Pass history for these entries is a
/// chain of items rather than revisions of one, which is a fair price for keeping secrets off the
/// command line.
///
/// The wire shapes below were taken from a real pass-cli 2.2.3 (<c>--get-template</c> for writes,
/// observed output for reads), not inferred. Two of them are asymmetric and easy to get wrong:
/// a template writes <c>field_name</c>/<c>field_type</c>/<c>value</c> while a read returns
/// <c>name</c> plus a <c>{"Text"|"Hidden": value}</c> wrapper, and <c>--show-secrets</c> moves the
/// title from the top level down into <c>content.title</c>.
/// </summary>
/// <param name="exePathOverride">
/// Skips the search for the binary. For tests, which must not depend on whether the real CLI
/// happens to be installed — and must not reach for the process-wide environment variable, since
/// test classes run in parallel and would clobber each other's.
/// </param>
/// <param name="session">
/// RavensPort's own pass-cli session. Optional so the existing tests, which drive the provider
/// against a fake runner, keep working unchanged: without one this behaves exactly as it did
/// before, using whatever session the machine's pass-cli defaults to.
/// </param>
public sealed class ProtonPassVaultProvider(
    ICliRunner cliRunner,
    ActivityLog activityLog,
    string? exePathOverride = null,
    ProtonPassSession? session = null) : IConfigVault
{
    /// <summary>Section every custom field goes in, so the Proton Pass UI groups them together.</summary>
    private const string SectionName = "RavensPort";

    /// <summary>
    /// The two pieces of <c>pass-cli</c>'s command line that recur across the verbs below. Named
    /// for the same reason as their 1Password counterparts: a typo in one of six call sites fails
    /// only that path, while the rest keep working.
    /// </summary>
    private const string VaultNoun = "vault";

    /// <inheritdoc cref="VaultNoun"/>
    private const string OutputFlag = "--output";

    private string? _exePath;
    private string? _shareId;
    private string _vaultName = VaultConstants.VaultName;
    private long _loadedRevision;

    /// <summary>
    /// Whether this provider has a trustworthy picture of what is in this vault — set by a
    /// complete read, and equally by a save, which establishes the same thing by writing it.
    ///
    /// Gates the delete sweep, and nothing else. The sweep decides which items are no longer wanted
    /// by subtraction: anything in the vault the store does not account for. That inference is only
    /// sound once this instance knows the vault, so without it nothing is deleted — an empty store
    /// might mean "the user removed everything" or might mean "nothing was ever read", and the two
    /// are indistinguishable from here.
    ///
    /// A save counts because it is not a guess: it has just written the note and every item the
    /// store calls for. Requiring a *load* specifically would break the first run, where a fresh
    /// vault has no configuration item to read and every later save would then refuse to tidy up
    /// after itself.
    ///
    /// Reset by <see cref="Forget"/>, so a disconnect or a switch to another vault starts from "I
    /// know nothing" rather than carrying one vault's baseline into another.
    /// </summary>
    private bool _hasCompletedLoad;

    /// <summary>
    /// Set by <see cref="Forget"/>, cleared when the user names a vault again. Without it a probe
    /// after disconnecting would rediscover the vault the user has just left and reattach to it.
    /// </summary>
    private bool _discoveryDisabled;

    /// <summary>
    /// Set by <see cref="Forget"/>, cleared the moment the user chooses a vault again. While set,
    /// every write refuses rather than re-resolving a vault to write into.
    ///
    /// Reads stay allowed: probing is how the setup page finds out what is available, and a read
    /// cannot destroy anything.
    /// </summary>
    private bool _writesDisabled;

    /// <summary>
    /// Refuses a write issued while this provider has no vault the user has chosen. Called by every
    /// mutating entry point, ahead of the vault lookup that would otherwise adopt one.
    /// </summary>
    private void RequireWritesAllowed()
    {
        if (!_writesDisabled) return;

        throw new VaultSaveException(
            "RavensPort is not connected to a Proton Pass vault, so nothing was written. "
            + "Choose a vault on the setup page first.",
            partiallyApplied: false);
    }

    public VaultBackendKind Kind => VaultBackendKind.ProtonPass;

    public string VaultName => _vaultName;

    public string? LastLoadWarning { get; private set; }

    public IReadOnlyList<string> LastLoadRemovals { get; private set; } = [];

    /// <summary>
    /// Optional personal access token, for an unattended machine. Passed in the child's
    /// environment, never as an argument.
    /// </summary>
    public string? PersonalAccessToken { get; set; }

    public Task<VaultStatus> ProbeAsync(CancellationToken ct = default) =>
        ProbeAsync(VaultProbeDepth.Full, ct);

    public async Task<VaultStatus> ProbeAsync(VaultProbeDepth depth, CancellationToken ct = default)
    {
        _exePath = exePathOverride ?? VaultProbe.FindProtonPass();
        if (_exePath is null || !File.Exists(_exePath)) return VaultStatus.NotInstalled(Kind);

        if (LockedWithoutAsking() is { } locked) return locked;

        string? version;
        CliResult vaultList;

        try
        {
            var versionResult = await RunAsync(["--version"], ct: ct);
            version = versionResult.Succeeded ? VaultProbe.ParseVersion(versionResult.StdOut)?.ToString() : null;

            // A discovery probe stops here: `vault list` is a network round trip against the user's
            // account, and the vault and item listings after it are several more. The check above
            // has already reported the states that can be known for free, so what is left is
            // genuinely "RavensPort has not asked yet".
            if (depth == VaultProbeDepth.Discovery)
            {
                return VaultStatus.NotConnected(Kind, _exePath, version);
            }

            vaultList = await RunAsync([VaultNoun, "list", OutputFlag, "json"], ct: ct);
        }
        catch (VaultCliException ex)
        {
            return VaultStatus.Faulted(Kind, ex.Message, _exePath);
        }

        if (!vaultList.Succeeded)
        {
            // A pass-cli session persists until logout, so this is usually "signed out" rather
            // than "locked". Either way the answer for the user is the same, and the CLI's own
            // wording says which more accurately than an exit code could.
            return new VaultStatus(Kind, VaultAvailability.NotSignedIn,
                _exePath, version, Detail: vaultList.FirstErrorLine());
        }

        var vaults = ParseVaults(vaultList.StdOut);
        _shareId = vaults.FirstOrDefault(v => v.Name == _vaultName)?.ShareId;

        var configured = await AdoptConfiguredVaultAsync(vaults, ct);

        return new VaultStatus(
            Kind,
            Resolve(),
            _exePath,
            version,
            _shareId,
            VaultName: _vaultName,
            Vaults: [.. vaults.Select(v => v.Name)],
            ConfiguredVaults: [.. configured.Select(v => v.Name)],
            AdoptableVaults: await FindAdoptableVaultsAsync(vaults, configured, ct));

        // More than one configured vault is separate profiles, and opening one would mean
        // overwriting the other's note on the next save. That is a question for the user.
        VaultAvailability Resolve()
        {
            if (_shareId is not null) return VaultAvailability.Ready;

            return configured.Count > 1
                ? VaultAvailability.VaultChoiceNeeded
                : VaultAvailability.VaultMissing;
        }
    }

    /// <summary>
    /// The one state that can be answered without launching anything, or null to carry on.
    ///
    /// The env key provider refuses an empty PROTON_PASS_ENCRYPTION_KEY, so running the CLI here
    /// would spend a process launch to be told off in wording that says nothing about the actual
    /// problem — which is simply that nobody has unlocked this session since the app started.
    /// </summary>
    private VaultStatus? LockedWithoutAsking()
    {
        if (session is null || session.HasKey || PersonalAccessToken is { Length: > 0 }) return null;

        return new VaultStatus(
            Kind,
            VaultAvailability.NotSignedIn,
            _exePath,
            // States the situation only. What to do about it is the setup page's job, and saying
            // it in both places made the card repeat itself twice over.
            Detail: session.HasSessionOnDisk
                ? "Locked — RavensPort has a session here but not the key that opens it."
                : "RavensPort is not signed in to Proton Pass yet.");
    }

    /// <summary>
    /// Finds the vault that already holds a RavensPort configuration and adopts it when there is
    /// exactly one, returning every candidate it found so the caller can offer a choice.
    ///
    /// Skipped once the user has disconnected. Rediscovery is what makes a vault stick across
    /// restarts, and straight after a disconnect it would silently reattach the very vault they
    /// just stepped away from — leaving them no way to pick a different one.
    /// </summary>
    private async Task<List<ProtonVault>> AdoptConfiguredVaultAsync(
        List<ProtonVault> vaults, CancellationToken ct)
    {
        if (_shareId is not null || _discoveryDisabled) return [];

        var configured = await FindConfiguredVaultsAsync(vaults, ct);
        if (configured.Count != 1) return configured;

        // A vault the user pointed RavensPort at is not remembered on this PC — nothing about
        // this app is — so it is found the same way the backend itself is: whichever vault
        // actually holds the configuration is the one that was being used. Only reached when
        // RavensPort is absent, so the ordinary path is one `vault list`.
        _vaultName = configured[0].Name;
        _shareId = configured[0].ShareId;

        activityLog.Log($"VAULT Proton Pass — using the existing '{_vaultName}' vault, "
                        + "which holds the RavensPort configuration");

        return configured;
    }

    public async Task CreateVaultAsync(string vaultName, CancellationToken ct = default)
    {
        var name = vaultName.Trim();
        if (name.Length == 0) throw VaultAdoption.NameRequired();

        await RequireExeAsync();

        var listed = await RunAsync([VaultNoun, "list", OutputFlag, "json"], ct: ct);
        if (!listed.Succeeded) throw new VaultLockedException(Kind, listed.FirstErrorLine());

        // Refused rather than silently reused: "create" and "take over what is already there" are
        // different intentions, and the second one has rules — see VaultAdoption.
        if (ParseVaults(listed.StdOut).Any(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new VaultAdoptionException(
                $"There is already a vault called '{name}'. Choose it under \"use a vault you already have\" "
                + "instead, or pick a different name.");
        }

        var result = await RunAsync(
            [VaultNoun, "create", "--name", name],
            timeout: CliRunner.WriteTimeout, ct: ct);

        if (!result.Succeeded)
        {
            throw new VaultSaveException(
                $"Could not create the '{name}' vault: {result.FirstErrorLine()}",
                partiallyApplied: false);
        }

        // `vault create` does not report the share id, so re-list rather than parse its output.
        var after = await RunAsync([VaultNoun, "list", OutputFlag, "json"], ct: ct);

        _shareId = after.Succeeded
            ? ParseVaults(after.StdOut)
                .FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase))?.ShareId
            : null;

        if (_shareId is null)
        {
            throw new VaultSaveException(
                $"Proton Pass reported creating '{name}' but it is not in the vault list.",
                partiallyApplied: false);
        }

        _vaultName = name;
        _loadedRevision = 0;
        _discoveryDisabled = false;

        // The user has named a vault, which is the only thing that re-opens writing.
        _writesDisabled = false;

        // Stamped straight away: the config item is what identifies this vault as RavensPort's on
        // the next launch, and the name the user just chose is not written down anywhere on this PC.
        await SaveAsync(new ConfigStore(), ct);

        activityLog.Log($"VAULT Proton Pass — created the '{_vaultName}' vault");
    }

    /// <summary>
    /// Takes over a vault the user already has. See <see cref="VaultAdoption"/> for why only an
    /// empty vault or one RavensPort has written to is accepted.
    /// </summary>
    public async Task UseExistingVaultAsync(string vaultName, CancellationToken ct = default)
    {
        var name = vaultName.Trim();
        if (name.Length == 0) throw VaultAdoption.NameRequired();

        await RequireExeAsync();

        var listed = await RunAsync([VaultNoun, "list", OutputFlag, "json"], ct: ct);
        if (!listed.Succeeded) throw new VaultLockedException(Kind, listed.FirstErrorLine());

        var vaults = ParseVaults(listed.StdOut);

        // Case-insensitive, because the user is typing a name they read in the Proton Pass UI and
        // being told "no such vault" over capitalisation would be a poor way to spend their time.
        var match = vaults.FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase))
                    ?? throw VaultAdoption.NoSuchVault(name, vaults.Select(v => v.Name));

        var (items, _) = await ListAllAsync(match.ShareId, match.Name, withSecrets: true, ct);

        var note = items.FirstOrDefault(i => i.Title == VaultItemNaming.ConfigTitle);
        var outcome = VaultAdoption.Judge(
            match.Name,
            [.. items.Select(item => item.Title)],
            note is null ? null : note.Contents.Field(VaultFields.NoteContent) ?? "");

        _vaultName = match.Name;
        _shareId = match.ShareId;
        _loadedRevision = 0;
        _discoveryDisabled = false;

        // The user has named a vault, which is the only thing that re-opens writing.
        _writesDisabled = false;
        LastLoadWarning = null;

        if (outcome == VaultAdoptionOutcome.Empty)
        {
            // Stamped now rather than on the first real edit: the config item is the only thing
            // that identifies this vault as RavensPort's next launch, and the name the user just
            // typed is deliberately not written down anywhere on this PC.
            await SaveAsync(new ConfigStore(), ct);
        }

        activityLog.Log($"VAULT Proton Pass — using the existing '{_vaultName}' vault");
    }

    /// <summary>
    /// Lets a probe resolve a vault again, for a manager the user is deliberately connecting to.
    /// See <see cref="OnePasswordVaultProvider.AllowDiscovery"/> — writing stays shut until
    /// <see cref="AllowWrites"/>, so a probe that fails cannot leave this writable with no vault.
    /// </summary>
    public void AllowDiscovery() => _discoveryDisabled = false;

    /// <summary>Re-opens writing, once a probe has actually resolved a vault.</summary>
    public void AllowWrites() => _writesDisabled = false;

    public void Forget()
    {
        _shareId = null;
        _vaultName = VaultConstants.VaultName;
        _loadedRevision = 0;
        LastLoadRemovals = [];
        LastLoadWarning = null;

        // The baseline the delete sweep relies on belonged to the vault being left. Carrying it
        // into the next one would let a save decide that vault's items are unwanted.
        _hasCompletedLoad = false;

        // No writing until the user names a vault again.
        //
        // This is the mechanism that lost a user's items. Disconnect clears the vault id, but a save
        // already queued — or one racing the disconnect — reaches RequireVaultAsync, finds no vault,
        // probes, silently adopts whatever it discovers, and writes a configuration belonging to
        // somewhere else into it. Refusing outright means a stale save dies with an error instead of
        // finding a new home for itself.
        _writesDisabled = true;

        // Until the user names a vault again. Otherwise the next probe finds the vault holding the
        // configuration — the one they just disconnected from — and quietly picks it up again.
        _discoveryDisabled = true;
    }

    public async Task<ConfigStore> LoadAsync(CancellationToken ct = default)
    {
        LastLoadWarning = null;
        LastLoadRemovals = [];

        // Cleared on the way in, set only on the way out. A load that throws half-way must not
        // leave the delete sweep believing it has a complete picture of the vault.
        _hasCompletedLoad = false;

        await RequireVaultAsync(ct);

        // One call for the whole vault, secrets included. `item list --show-secrets` returns full
        // contents, so there is no reason to fetch items one at a time — which on a store with a
        // dozen credentials would be a dozen extra subprocess launches on the startup path.
        var items = await ListAsync(withSecrets: true, ct);

        var note = items.FirstOrDefault(i => i.Title == VaultItemNaming.ConfigTitle);
        if (note is null)
        {
            _loadedRevision = 0;
            return new ConfigStore();
        }

        var document = VaultDocument.TryParse(note.Contents.Field(VaultFields.NoteContent) ?? "");

        if (document is null)
        {
            // The note is free text the user can open and edit in Proton Pass, so a broken one is
            // a mistake rather than corruption. Coming up empty is recoverable; refusing to start
            // is not, and the old configuration is still in the vault to be repaired.
            LastLoadWarning = $"The '{VaultItemNaming.ConfigTitle}' item could not be read as configuration, "
                              + "so RavensPort started with nothing. The item has not been changed.";
            _loadedRevision = 0;
            return new ConfigStore();
        }

        if (document.IsFromANewerLayout)
        {
            LastLoadWarning = "The vault was written by a newer version of RavensPort "
                              + $"(layout {document.VaultLayoutVersion}). Some settings may not be understood.";
        }

        _loadedRevision = document.Revision;

        var report = new VaultLoadReport();
        var store = VaultMapper.ComposeStore(document, ResolveSecrets(document.Index, items), report);

        LastLoadRemovals = report.Removals;

        if (report.HasAnything)
        {
            LastLoadWarning = string.Join(" ", new[] { LastLoadWarning, report.Message }
                .Where(w => !string.IsNullOrEmpty(w)));
        }

        // Only here. The earlier returns above — no config item, or one that could not be parsed —
        // are answers rather than reads: they produce an empty store, and letting the delete sweep
        // act on that would wipe every item in the vault on the next save. Reaching this line means
        // the note was read and every secret it indexes either resolved or was positively reported
        // gone, which is the only state where "not in the store" reliably means "not wanted".
        _hasCompletedLoad = true;

        return store;
    }

    public Task SaveAsync(ConfigStore store, CancellationToken ct = default) =>
        SaveAsync(store, rewriteEverything: false, ct);

    /// <summary>
    /// Every item written again, whether or not it changed. On this backend a rewrite means
    /// deleting and recreating each entry, so it is never done automatically — only when the user
    /// asks for the vault to be made to match memory.
    /// </summary>
    public Task RewriteAllAsync(ConfigStore store, CancellationToken ct = default) =>
        SaveAsync(store, rewriteEverything: true, ct);

    /// <summary>Every live item in the vault, ours and the user's. No secrets are fetched.</summary>
    public async Task<IReadOnlyList<VaultItemEntry>> ListLiveItemsAsync(CancellationToken ct = default)
    {
        await RequireVaultAsync(ct);

        var (items, _) = await ListAllAsync(_shareId!, _vaultName, withSecrets: false, ct);

        return [.. items.Select(item => VaultItemEntry.Classify(item.ItemId, item.Title))];
    }

    public async Task DeleteItemAsync(string itemId, CancellationToken ct = default)
    {
        RequireWritesAllowed();
        await RequireVaultAsync(ct);

        var result = await RunAsync(
            ["item", "delete", Share, ItemArg(itemId)], timeout: CliRunner.WriteTimeout, ct: ct);

        if (!result.Succeeded)
        {
            throw new VaultSaveException(
                $"Proton Pass would not delete the item: {result.FirstErrorLine()}", partiallyApplied: false);
        }
    }

    private async Task SaveAsync(ConfigStore store, bool rewriteEverything, CancellationToken ct)
    {
        RequireWritesAllowed();
        await RequireVaultAsync(ct);

        var existing = await ListAsync(withSecrets: true, ct);
        var previousNote = existing.FirstOrDefault(i => i.Title == VaultItemNaming.ConfigTitle);

        var previousIndex = ReadIndex(previousNote);
        var index = new VaultIndex();

        var written = 0;
        var secretItems = VaultMapper.BuildSecretItems(store, previousIndex);

        foreach (var item in secretItems)
        {
            ct.ThrowIfCancellationRequested();

            var current = FindCurrent(existing, item, previousIndex);

            // Unchanged records are left alone. On this backend a rewrite means deleting and
            // recreating the user's vault entry, so saving a store whose secrets have not moved
            // must not churn it — a port change would otherwise replace every credential item.
            // A deliberate rewrite skips the shortcut: making the vault match memory is the point.
            if (!rewriteEverything && current is not null && IsUnchanged(current, item))
            {
                index.For(item.Role)[item.RecordId] = current.ItemId;
                index.Fingerprints[item.RecordId] = item.Fingerprint;
                continue;
            }

            try
            {
                index.For(item.Role)[item.RecordId] = await CreateItemAsync(item.Spec, ct);
                index.Fingerprints[item.RecordId] = item.Fingerprint;
                written++;
            }
            catch (Exception ex) when (ex is VaultCliException or VaultSaveException)
            {
                throw new VaultSaveException(
                    $"Could not save '{item.Spec.Title}' to Proton Pass: {ex.Message}",
                    partiallyApplied: written > 0,
                    ex);
            }
        }

        // After every secret item and before any deletion, so at no point does the note reference
        // something that is gone. A crash before it leaves unreferenced new items; a crash after
        // leaves superseded old ones. The next save sweeps both.
        try
        {
            var noteSpec = VaultMapper.BuildConfigNote(store, index, _loadedRevision + 1);
            await CreateItemAsync(noteSpec, ct);
            _loadedRevision++;

            if (previousNote is not null) await DeleteItemAsync(previousNote.ItemId, previousNote.Title, ct);
        }
        catch (Exception ex) when (ex is VaultCliException or VaultSaveException)
        {
            throw new VaultSaveException(
                $"Could not save the configuration item to Proton Pass: {ex.Message}",
                partiallyApplied: written > 0,
                ex);
        }

await ReconcileAsync(existing, index, previousIndex, ct);

        // After the sweep, deliberately. Setting it first would let a save authorise its own
        // deletions, which is no guard at all: the very first save by a provider that has never
        // read this vault would sweep it on the strength of a baseline that save had just invented.
        // A save earns the baseline for the *next* one — on a first run, where there was no note to
        // read, that is what eventually allows tidying up at all.
        _hasCompletedLoad = true;
    }

    // ---- CLI calls ------------------------------------------------------------------------------

    /// <summary>
    /// Ids as <c>--flag=value</c> rather than two arguments.
    ///
    /// Proton Pass ids are base64url, so roughly one in sixty starts with a hyphen — and
    /// <c>--item-id -0_TRk…</c> is parsed by the CLI as an unknown flag, not as a value:
    /// "error: unexpected argument '-0' found". The attached form has no such ambiguity.
    ///
    /// The symptom was ugly and permanent. A delete that can never succeed leaves an orphaned
    /// item behind on every save, so a route ended up with two key items and no way to tell which
    /// one the proxy was actually accepting.
    /// </summary>
    private string Share => ShareArg(_shareId!);

    private static string ShareArg(string shareId) => $"--share-id={shareId}";

    private static string ItemArg(string itemId) => $"--item-id={itemId}";


    /// <summary>
    /// Creates an item from a template on stdin — the only write path that keeps the value out of
    /// an argument. Returns the new item id, which the CLI prints bare rather than as JSON.
    /// </summary>
    private async Task<string> CreateItemAsync(VaultItemSpec spec, CancellationToken ct)
    {
        var type = TypeName(spec.Category);

        var result = await RunAsync(
            ["item", "create", type, Share, "--from-template", "-"],
            stdin: BuildTemplate(spec).ToJsonString(),
            timeout: CliRunner.WriteTimeout,
            ct: ct);

        if (!result.Succeeded)
        {
            throw new VaultSaveException(
                $"{result.FirstErrorLine()} (if this mentions the template format, compare it with "
                + $"`pass-cli item create {type} --get-template`)",
                partiallyApplied: false);
        }

        var itemId = result.StdOut.Trim();

        return itemId.Length > 0
            ? itemId
            : throw new VaultSaveException(
                "Proton Pass created the item but did not report its id.", partiallyApplied: false);
    }

    /// <summary>Items in the active vault that this app owns.</summary>
    private async Task<List<ProtonItem>> ListAsync(bool withSecrets, CancellationToken ct)
    {
        var (items, concealed) = await ListAllAsync(_shareId!, _vaultName, withSecrets, ct);

        if (concealed > 0)
        {
            // Storing the placeholder would put the literal string into the app's config as if it
            // were the secret, and every request using it would fail against the upstream with
            // nothing to explain why.
            LastLoadWarning = "Proton Pass returned masked values rather than the stored secrets, "
                              + "so some credentials loaded without them.";
        }

        // Only items this app owns. The rest of the vault is the user's and is never read, never
        // written, and never a candidate for deletion.
        return items.Where(i => VaultItemNaming.IsOwned(i.Title)).ToList();
    }

    /// <summary>
    /// Everything in a vault, owned or not. The unfiltered count is what decides whether a vault
    /// the user named is empty enough to take over, so this deliberately does not filter.
    /// </summary>
    private async Task<(List<ProtonItem> Items, int Concealed)> ListAllAsync(
        string shareId, string vaultLabel, bool withSecrets, CancellationToken ct)
    {
        string[] args = withSecrets
            ? ["item", "list", ShareArg(shareId), OutputFlag, "json", "--show-secrets"]
            : ["item", "list", ShareArg(shareId), OutputFlag, "json"];

        var result = await RunAsync(args, ct: ct);

        if (!result.Succeeded)
        {
            throw new VaultCliException(
                $"Could not list the '{vaultLabel}' vault: {result.FirstErrorLine()}");
        }

        var items = new List<ProtonItem>();
        var concealed = 0;

        foreach (var node in JsonNode.Parse(result.StdOut)?["items"] as JsonArray ?? [])
        {
            if (ParseItem(node, ref concealed) is { } item) items.Add(item);
        }

        return (items, concealed);
    }

    /// <summary>
    /// One item from <c>item list --output json</c>, with or without <c>--show-secrets</c>. The
    /// two shapes differ: without secrets the title is top level and there is no content; with
    /// them the title moves into <c>content.title</c> and the payload hangs off a type-tagged
    /// wrapper.
    /// </summary>
    private static ProtonItem? ParseItem(JsonNode? node, ref int concealed)
    {
        var itemId = ReadString(node, "id");
        if (itemId is null) return null;

        // Deleting an item in Proton Pass moves it to the trash; `item list` keeps returning it
        // with state=Trashed. Reading those made a vault the user had emptied look full, made a
        // deleted credential look present, and would have had a save compare against an item the
        // user cannot see. Absent state is treated as live, so an output shape without the field
        // does not silently hide the whole vault.
        if (ReadString(node, "state") is { } state
            && !state.Equals("Active", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var fields = new Dictionary<string, string>(StringComparer.Ordinal);

        if (node?["content"] is not JsonObject content)
        {
            return ReadString(node, "title") is { } listedTitle
                ? new ProtonItem(itemId, listedTitle, new VaultItemContents(itemId, listedTitle, fields))
                : null;
        }

        var title = ReadString(content, "title") ?? "";

        // The note slot carries the config document for a note item, and a human-readable caption
        // for everything else. Both land here; only the config note is ever read back.
        if (ReadString(content, "note") is { Length: > 0 } note) fields[VaultFields.NoteContent] = note;

        ReadPayload(content["content"], fields, ref concealed);

        return new ProtonItem(itemId, title, new VaultItemContents(itemId, title, fields));
    }

    /// <summary>
    /// The type-tagged payload, of which this app writes and reads exactly two shapes: a login for
    /// a single secret, and a custom item for anything with more than one field. Anything else is
    /// someone else's item and contributes no fields.
    /// </summary>
    private static void ReadPayload(JsonNode? payload, Dictionary<string, string> fields, ref int concealed)
    {
        if (payload?["Login"] is JsonObject login)
        {
            Record(fields, VaultFields.Username, ReadString(login, "username"), ref concealed);
            Record(fields, VaultFields.Password, ReadString(login, "password"), ref concealed);
            Record(fields, VaultFields.Website, (login["urls"] as JsonArray)?.FirstOrDefault()?.GetValue<string>(),
                ref concealed);
            return;
        }

        if (payload?["Custom"] is JsonObject custom) ReadCustomFields(custom, fields, ref concealed);
    }

    private static void ReadCustomFields(JsonObject custom, Dictionary<string, string> fields, ref int concealed)
    {
        foreach (var section in custom["sections"] as JsonArray ?? [])
        {
            foreach (var field in section?["section_fields"] as JsonArray ?? [])
            {
                var name = ReadString(field, "name");
                if (name is null) continue;

                // Reads wrap the value in its type — {"Text": "..."} or {"Hidden": "..."} —
                // while writes use a flat field_type/value pair. Neither side is wrong; they just
                // are not the same shape.
                var wrapper = field?["content"];
                Record(fields, name, ReadString(wrapper, "Text") ?? ReadString(wrapper, "Hidden"), ref concealed);
            }
        }
    }

    private static void Record(Dictionary<string, string> fields, string name, string? value, ref int concealed)
    {
        if (string.IsNullOrEmpty(value)) return;

        if (value.Contains("concealed by Proton Pass", StringComparison.OrdinalIgnoreCase))
        {
            concealed++;
            return;
        }

        fields[name] = value;
    }

    private async Task DeleteItemAsync(string itemId, string title, CancellationToken ct)
    {
        var result = await RunAsync(
            ["item", "delete", Share, ItemArg(itemId)],
            timeout: CliRunner.WriteTimeout, ct: ct);

        if (!result.Succeeded)
        {
            // Deliberately not fatal. The store is already saved; a leftover item is untidy rather
            // than wrong, and the next save tries again. Throwing would report a successful save
            // as a failure and trigger a pointless rollback.
            activityLog.Log($"VAULT could not delete '{title}': {result.FirstErrorLine()}");
        }
    }

    /// <summary>
    /// Deletes every owned item the note no longer points at: records that are gone, and the
    /// superseded predecessors of records that were rewritten.
    /// </summary>
    private async Task ReconcileAsync(
        List<ProtonItem> before, VaultIndex index, VaultIndex previousIndex, CancellationToken ct)
    {
        // Same rule as the 1Password sweep, for the same reason: this is the only irreversible
        // thing done to a user's vault, and "not in the store" only means "not wanted" if the store
        // came from a complete read of *this* vault. Without one — after a disconnect, a failed
        // load, or a switch to another vault — an empty store would take the whole vault with it.
        if (!_hasCompletedLoad)
        {
            activityLog.Log(
                "VAULT skipped deleting unused Proton Pass items — this session has not completed a full "
                + "read of the vault, so it cannot tell an item you removed from one it never saw");
            return;
        }

        var previousRecordIds = previousIndex.Credentials.Keys
            .Concat(previousIndex.RouteKeys.Keys)
            .Concat(previousIndex.FunnelKeys.Keys)
            .ToHashSet();

        var newRecordIds = index.Credentials.Keys
            .Concat(index.RouteKeys.Keys)
            .Concat(index.FunnelKeys.Keys)
            .ToHashSet();

        if (previousRecordIds.Count > 0 && newRecordIds.Count > 0 && !newRecordIds.Overlaps(previousRecordIds))
        {
            activityLog.Log(
                "VAULT skipped deleting unused Proton Pass items — the incoming configuration carries records "
                + "that share no identity with this vault's index, so it cannot authorize deletions here");
            return;
        }

        var keep = index.Credentials.Values
            .Concat(index.RouteKeys.Values)
            .Concat(index.FunnelKeys.Values)
            .ToHashSet(StringComparer.Ordinal);

        // Only items the note being replaced actually pointed at, and this is the property that
        // makes the accident impossible rather than unlikely.
        //
        // The sweep used to consider every owned item in the vault, which is sound only while the
        // store and the vault describe the same thing. They came apart: a save carrying one vault's
        // configuration reached another vault, every item its note had never heard of looked
        // unwanted, and nine of a user's items were deleted. A note from elsewhere indexes ids that
        // do not exist here, so restricting deletion to those ids means nothing matches and nothing
        // goes. A record the user really removed was in this note a moment ago, so it still does.
        var deletable = previousIndex.Credentials.Values
            .Concat(previousIndex.RouteKeys.Values)
            .Concat(previousIndex.FunnelKeys.Values)
            .ToHashSet(StringComparer.Ordinal);

        var doomed = before
            .Where(item => !keep.Contains(item.ItemId))
            .Where(item => VaultItemNaming.TryParse(item.Title, out var role, out var id)
                           && role != VaultItemRole.Config
                           && (deletable.Contains(item.ItemId) || DuplicatesALiveRecord(role, id, index)))
            .ToList();

        if (!WithinDeletionBudget(doomed.Count, keep.Count)) return;

        foreach (var item in doomed)
        {
            await DeleteItemAsync(item.ItemId, item.Title, ct);
        }
    }

    /// <summary>
    /// A second item claiming a record this save has just written authoritatively.
    ///
    /// The only thing outside the previous index that may be deleted, and it is safe for a reason
    /// that does not generalise: the record is in the store, the item holding its current value has
    /// just been written, and this is a different item claiming the same record. Nothing is lost by
    /// removing it, while leaving it is actively harmful — the proxy honours only the indexed one,
    /// and the other looks equally real in the password manager.
    ///
    /// An item belonging to some other vault's configuration can never match: its record id is not
    /// in this store, so there is nothing for it to duplicate.
    /// </summary>
    private static bool DuplicatesALiveRecord(VaultItemRole role, Guid recordId, VaultIndex index) =>
        index.Find(role, recordId) is { Length: > 0 };

    /// <summary>
    /// The last line of defence: a routine save is never allowed to be a mass deletion.
    ///
    /// Everything above is meant to make an unwanted sweep impossible, and something like it was
    /// meant to be impossible before. This does not reason about *why* the numbers look wrong — it
    /// refuses to let a background write remove a large number of a user's items at once, and says
    /// so loudly. Removing several credentials at a stroke is a deliberate act, and the Settings
    /// tab's integrity tools exist to do it with the user watching.
    /// </summary>
    private bool WithinDeletionBudget(int doomed, int kept)
    {
        const int alwaysAllowed = 2;

        if (doomed <= alwaysAllowed || doomed <= kept) return true;

        activityLog.Log(
            $"VAULT REFUSED to delete {doomed} Proton Pass item(s) during a save that kept only {kept} — "
            + "a routine save does not remove that much at once. Nothing was deleted. Use the vault "
            + "integrity check on the Settings tab if these items really should go.");

        return false;
    }

    // ---- Templates and lookups --------------------------------------------------------------------

    /// <summary>
    /// The item as a create template, in the shape <c>--get-template</c> reports for its type.
    ///
    /// The type choice is forced by what each template can carry. A login has no custom fields at
    /// all, so a credential — which needs a client id, a secret, an API key, two tokens and their
    /// timestamps — has to be a custom item. A proxy key is a single secret, so it stays a login,
    /// where Proton Pass gives it the usual conceal-and-copy treatment.
    /// </summary>
    private static JsonObject BuildTemplate(VaultItemSpec spec)
    {
        var template = new JsonObject { ["title"] = spec.Title };

        switch (spec.Category)
        {
            case VaultItemCategory.SecureNote:
                template["note"] = spec.Field(VaultFields.NoteContent) ?? "";
                return template;

            case VaultItemCategory.Password:
                // The login template has no note field, so the caption is dropped here rather
                // than smuggled somewhere it would be read back as data.
                template["username"] = "";
                template["password"] = spec.Field(VaultFields.Password) ?? "";
                return template;

            default:
                var fields = new JsonArray();

                foreach (var field in spec.Fields)
                {
                    fields.Add(new JsonObject
                    {
                        ["field_name"] = field.Name,
                        ["field_type"] = field.Concealed ? "hidden" : "text",
                        ["value"] = field.Value,
                    });
                }

                template["note"] = spec.Caption ?? "";
                template["sections"] = new JsonArray
                {
                    new JsonObject { ["section_name"] = SectionName, ["fields"] = fields },
                };

                return template;
        }
    }

    private static string TypeName(VaultItemCategory category) => category switch
    {
        VaultItemCategory.SecureNote => "note",
        VaultItemCategory.Password => "login",
        _ => "custom",
    };

    /// <summary>
    /// Whether the stored item already carries what would be written.
    ///
    /// Compared against <see cref="WritableFields"/> rather than the whole spec, because not every
    /// template can hold every field: a login has nowhere to put a record id, so a proxy key's is
    /// dropped on the way in and absent on the way out. Comparing it would make every proxy key
    /// look changed on every save and churn the user's vault forever.
    ///
    /// The caption is excluded for the same reason it is written but never read: it is decoration,
    /// and a proxy key's includes its remaining lifetime.
    /// </summary>
    private static bool IsUnchanged(ProtonItem current, VaultSecretItem item)
    {
        if (current.Title != item.Spec.Title) return false;

        var expected = WritableFields(item.Spec)
            .Where(f => f.Value.Length > 0)
            .ToList();

        foreach (var field in expected)
        {
            if (current.Contents.Field(field.Name) != field.Value) return false;
        }

        // A field in the vault that is not being written means something was removed — a
        // disconnected credential's token — which is a change that has to be applied.
        var stored = current.Contents.Fields
            .Where(f => f.Key != VaultFields.NoteContent && f.Value.Length > 0)
            .Select(f => f.Key);

        return !stored.Except(expected.Select(f => f.Name)).Any();
    }

    /// <summary>
    /// The fields <see cref="BuildTemplate"/> will actually store for this item's type. Kept
    /// beside it, because the two drifting apart is what makes an item rewrite itself on every
    /// save with nothing visibly wrong.
    /// </summary>
    private static IEnumerable<VaultItemField> WritableFields(VaultItemSpec spec) => spec.Category switch
    {
        VaultItemCategory.SecureNote => spec.Fields.Where(f => f.Name == VaultFields.NoteContent),
        VaultItemCategory.Password => spec.Fields.Where(f => f.Name == VaultFields.Password),
        _ => spec.Fields.Where(f => f.Name != VaultFields.NoteContent),
    };

    private static ProtonItem? FindCurrent(List<ProtonItem> existing, VaultSecretItem item, VaultIndex index)
    {
        if (index.Find(item.Role, item.RecordId) is { } indexed
            && existing.FirstOrDefault(i => i.ItemId == indexed) is { } byIndex)
        {
            return byIndex;
        }

        // The index is only a cache. An item recreated by hand has an id the note has never seen,
        // and without this fallback the save would add a second item claiming the same record.
        return existing.FirstOrDefault(i =>
            VaultItemNaming.TryParse(i.Title, out var role, out var id)
            && role == item.Role && id == item.RecordId);
    }

    /// <summary>
    /// The index from the note that is about to be replaced.
    ///
    /// Deliberately does not compare revisions. This app assumes a single instance, and the sync
    /// queue re-reads and rewrites whenever it can — so a guard here would turn a lock that lifted
    /// at an awkward moment into a save that refuses and retries forever, which is a worse outcome
    /// than the concurrent write it would be guarding against. The revision is still stamped into
    /// the note, so a second writer is at least visible after the fact.
    /// </summary>
    private static VaultIndex ReadIndex(ProtonItem? note) =>
        note is not null && VaultDocument.TryParse(note.Contents.Field(VaultFields.NoteContent) ?? "") is { } document
            ? document.Index
            : new VaultIndex();

    private static Dictionary<(VaultItemRole, Guid), VaultItemContents> ResolveSecrets(
        VaultIndex index, List<ProtonItem> items)
    {
        var byId = items.ToDictionary(i => i.ItemId, i => i.Contents, StringComparer.Ordinal);
        var resolved = new Dictionary<(VaultItemRole, Guid), VaultItemContents>();

        foreach (var role in new[] { VaultItemRole.Credential, VaultItemRole.RouteKey, VaultItemRole.FunnelKey })
        {
            foreach (var (recordId, itemId) in index.For(role))
            {
                if (byId.TryGetValue(itemId, out var contents)) resolved[(role, recordId)] = contents;
            }
        }

        // Anything the index missed, recovered from the guid in the title.
        foreach (var item in items)
        {
            if (!VaultItemNaming.TryParse(item.Title, out var role, out var id)) continue;
            if (role == VaultItemRole.Config) continue;

            resolved.TryAdd((role, id), item.Contents);
        }

        return resolved;
    }

    private static List<ProtonVault> ParseVaults(string vaultListJson)
    {
        var vaults = new List<ProtonVault>();

        foreach (var node in JsonNode.Parse(vaultListJson)?["vaults"] as JsonArray ?? [])
        {
            if (ReadString(node, "name") is { } name && ReadString(node, "share_id") is { } shareId)
            {
                vaults.Add(new ProtonVault(name, shareId));
            }
        }

        return vaults;
    }

    /// <summary>
    /// Every vault holding a RavensPort configuration, when the expected one is not there. Reads
    /// item titles only — no <c>--show-secrets</c> — because all that is being asked is which vault
    /// this app was last pointed at, and the rest of the user's vaults are none of its business.
    ///
    /// All of them rather than the first: two configured vaults is a user keeping separate profiles,
    /// and picking one at random would open one and overwrite the other on the next save.
    /// </summary>
    private async Task<List<ProtonVault>> FindConfiguredVaultsAsync(
        List<ProtonVault> vaults, CancellationToken ct)
    {
        var matchingVaults = vaults.Where(v => VaultProfile.Matches(v.Name)).ToList();
        var matchingChecks = matchingVaults.Select(async vault =>
        {
            try
            {
                var (items, _) = await ListAllAsync(vault.ShareId, vault.Name, withSecrets: false, ct);
                return items.Any(i => i.Title == VaultItemNaming.ConfigTitle) ? vault : null;
            }
            catch (Exception ex) when (ex is VaultCliException or JsonException)
            {
                activityLog.Log($"VAULT could not look inside the '{vault.Name}' vault: {ex.Message}");
                return null;
            }
        });

        var matchingResults = (await Task.WhenAll(matchingChecks)).Where(v => v is not null).Select(v => v!).ToList();
        if (matchingResults.Count > 0) return matchingResults;

        var remainingVaults = vaults.Except(matchingVaults).ToList();
        var remainingChecks = remainingVaults.Select(async vault =>
        {
            try
            {
                var (items, _) = await ListAllAsync(vault.ShareId, vault.Name, withSecrets: false, ct);
                return items.Any(i => i.Title == VaultItemNaming.ConfigTitle) ? vault : null;
            }
            catch (Exception ex) when (ex is VaultCliException or JsonException)
            {
                activityLog.Log($"VAULT could not look inside the '{vault.Name}' vault: {ex.Message}");
                return null;
            }
        });

        return (await Task.WhenAll(remainingChecks)).Where(v => v is not null).Select(v => v!).ToList();
    }

    /// <summary>
    /// The vaults the setup page may offer: named after RavensPort, and either empty or
    /// already RavensPort's. Only the name-matching ones are looked inside — every other vault in
    /// the account is none of this app's business, and listing them all is both the slow answer
    /// and the one that makes a user wonder what else it is reading.
    /// </summary>
    /// <param name="configured">
    /// Vaults the discovery pass has already been through, so their items are not listed twice.
    /// Holding a configuration is precisely what makes a vault adoptable.
    /// </param>
    private async Task<List<string>> FindAdoptableVaultsAsync(
        List<ProtonVault> vaults, List<ProtonVault> configured, CancellationToken ct)
    {
        var adoptable = new List<string>();

        foreach (var vault in vaults.Where(v => VaultProfile.Matches(v.Name)))
        {
            if (configured.Any(c => c.ShareId == vault.ShareId))
            {
                adoptable.Add(vault.Name);
                continue;
            }

            try
            {
                var (items, _) = await ListAllAsync(vault.ShareId, vault.Name, withSecrets: false, ct);
                if (VaultAdoption.LooksAdoptable([.. items.Select(i => i.Title)])) adoptable.Add(vault.Name);
            }
            catch (Exception ex) when (ex is VaultCliException or JsonException)
            {
                // A vault this session cannot list cannot be offered either — it would be refused
                // for the same reason the moment it was picked. The rest still get an answer.
                activityLog.Log($"VAULT could not look inside the '{vault.Name}' vault: {ex.Message}");
            }
        }

        return adoptable;
    }

    private static string? ReadString(JsonNode? node, string property)
    {
        try
        {
            return node?[property]?.GetValue<string>();
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            // The property exists but is not a string. Treating it as absent is right: this shape
            // comes from another program's output, not from a contract this app controls.
            return null;
        }
    }

    /// <summary>
    /// Locates the binary for a call that does not go through a probe first — creating or taking
    /// over a vault, both of which can be the first thing this provider is asked to do.
    /// </summary>
    private Task RequireExeAsync()
    {
        _exePath ??= exePathOverride ?? VaultProbe.FindProtonPass();

        return _exePath is null || !File.Exists(_exePath)
            ? throw new VaultCliException("The Proton Pass CLI is not installed.")
            : Task.CompletedTask;
    }

    private async Task RequireVaultAsync(CancellationToken ct)
    {
        if (_shareId is not null) return;

        var status = await ProbeAsync(ct);

        _shareId = status.VaultId ?? throw (status.Availability switch
        {
            VaultAvailability.NotSignedIn => new VaultLockedException(Kind, status.Detail),
            VaultAvailability.NotInstalled => new VaultCliException("The Proton Pass CLI is not installed."),
            VaultAvailability.VaultMissing =>
                new VaultCliException($"The '{_vaultName}' vault does not exist yet."),
            VaultAvailability.VaultChoiceNeeded =>
                new VaultCliException("More than one Proton Pass vault holds a RavensPort configuration. "
                                      + "Choose which one to use on the setup page."),
            _ => (Exception)new VaultCliException(status.Detail ?? "Proton Pass is unavailable."),
        });
    }

    private Task<CliResult> RunAsync(
        IReadOnlyList<string> args, string? stdin = null, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        return cliRunner.RunAsync(
            _exePath ?? throw new VaultCliException("The Proton Pass CLI has not been located yet."),
            args, stdin, BuildEnvironment(), timeout, ct);
    }

    /// <summary>
    /// The environment every pass-cli child gets. The single place both credentials are decided,
    /// so no call site can accidentally run against the wrong session.
    ///
    /// A personal access token wins outright rather than being merged: it authenticates on its own
    /// and needs no session, so handing it a session directory as well would just be two answers to
    /// one question. This is the unattended path from
    /// <see cref="VaultLockGuidance.UnattendedTokenSteps"/>, where there is no user to unlock
    /// anything.
    /// </summary>
    private IReadOnlyDictionary<string, string>? BuildEnvironment()
    {
        if (PersonalAccessToken is { Length: > 0 } token)
        {
            return new Dictionary<string, string> { ["PROTON_PASS_PERSONAL_ACCESS_TOKEN"] = token };
        }

        var env = session?.BuildEnvironment();
        return env is { Count: > 0 } ? env : null;
    }

    /// <summary>An item as this provider needs it: its id, its title, and its fields flattened.</summary>
    private sealed record ProtonItem(string ItemId, string Title, VaultItemContents Contents);

    /// <summary>One vault from <c>vault list</c>. The share id is what every other call takes.</summary>
    private sealed record ProtonVault(string Name, string ShareId);
}
