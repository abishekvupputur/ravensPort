using System.Text.Json;
using System.Text.Json.Nodes;
using RavensPort.Core.Diagnostics;
using RavensPort.Core.Models;

namespace RavensPort.Core.Vault;

/// <summary>
/// The store, backed by the 1Password CLI (<c>op</c>).
///
/// Every write goes through a JSON item template on stdin rather than <c>field=value</c>
/// arguments. Both forms are documented and the argument form is far more convenient, but a
/// Windows process command line is readable by any process in the session — putting a client
/// secret there would make this strictly worse than the encrypted local file it replaces.
/// </summary>
/// <param name="exePathOverride">
/// Skips the search for the binary. For tests, which must not depend on whether the real CLI
/// happens to be installed — and must not reach for the process-wide environment variable, since
/// test classes run in parallel and would clobber each other's.
/// </param>
/// <param name="session">
/// The service-account token, when the user has chosen that way in. Optional so every existing
/// caller — and every test — keeps working against the desktop-app path unchanged.
/// </param>
/// <param name="processRunner">
/// A runner that launches the real <c>op.exe</c>, used only in service-account mode and only when
/// the CLI is actually installed. See <see cref="RunAsync"/> for why that is preferred over the
/// in-process SDK when both are available.
/// </param>
/// <param name="cliExeLocator">
/// Where to look for <c>op.exe</c>. A seam for tests, which must not behave differently depending
/// on whether the machine running them happens to have the 1Password CLI installed.
/// </param>
/// <param name="executableTrust">
/// Whether a located CLI may be launched. Asked before choosing the CLI route so a refusal quietly
/// selects the other transport instead of failing a vault operation — see <see cref="ResolveCliExe"/>.
/// </param>
public sealed class OnePasswordVaultProvider(
    ICliRunner cliRunner,
    ActivityLog activityLog,
    string? exePathOverride = null,
    OnePasswordSession? session = null,
    ICliRunner? processRunner = null,
    Func<string?>? cliExeLocator = null,
    IExecutableTrustPolicy? executableTrust = null) : IConfigVault
{
    private IExecutableTrustPolicy trustPolicy => executableTrust ?? AuthenticodeTrustPolicy.Default;

    /// <summary>Fields that can legitimately become absent, and so must be actively cleared.</summary>
    private static readonly string[] ClearableSecretFields =
    [
        VaultFields.ApiKey, VaultFields.AccessToken, VaultFields.RefreshToken,
        VaultFields.TokenType, VaultFields.ExpiresAtUtc, VaultFields.ObtainedUtc,
        VaultFields.ServiceAccountJson,
    ];

    private string? _exePath;
    private string? _vaultId;
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
            "RavensPort is not connected to a 1Password vault, so nothing was written. "
            + "Choose a vault on the setup page first.",
            partiallyApplied: false);
    }

    public VaultBackendKind Kind => VaultBackendKind.OnePassword;

    public string VaultName => _vaultName;

    public string? LastLoadWarning { get; private set; }

    public IReadOnlyList<string> LastLoadRemovals { get; private set; } = [];

    /// <summary>
    /// The service-account token, for a machine that should never show an unlock prompt. Passed in
    /// the child's environment, never as an argument.
    ///
    /// Reads through to <see cref="OnePasswordSession"/> so there is one copy of the credential in
    /// the process rather than two that can disagree. Settable for the tests that predate the
    /// session, which set it directly; a session, when there is one, wins.
    /// </summary>
    public string? ServiceAccountToken
    {
        get => session?.HasToken == true ? session.CurrentToken : _serviceAccountToken;
        set => _serviceAccountToken = value;
    }

    private string? _serviceAccountToken;

    /// <summary>
    /// Where <c>op.exe</c> is, looked up once. <see cref="RunAsync"/> asks on every call and the
    /// lookup walks PATH plus two registry values, which is not something to do per vault read.
    /// Null means "not looked yet"; the sentinel below means "looked, and it is not installed".
    /// </summary>
    private string? _cliExePath;
    private bool _cliExeResolved;

    private const string NativeExePath = "native";

    /// <summary>
    /// Whether this provider is signed in as a service account rather than as the person at the
    /// keyboard. Changes what may be offered, never what may be read or written — see
    /// <see cref="FindAdoptableVaultsAsync"/>.
    /// </summary>
    private bool UsesServiceAccount => ServiceAccountToken is { Length: > 0 };

    public Task<VaultStatus> ProbeAsync(CancellationToken ct = default) =>
        ProbeAsync(VaultProbeDepth.Full, ct);

    public async Task<VaultStatus> ProbeAsync(VaultProbeDepth depth, CancellationToken ct = default)
    {
        _exePath = exePathOverride ?? VaultProbe.FindOnePassword();
        if (_exePath is null || (!File.Exists(_exePath) && _exePath != "native")) return VaultStatus.NotInstalled(Kind);

        Version? version;
        try
        {
            var versionResult = await RunAsync(["--version"], ct: ct);
            if (!versionResult.Succeeded)
            {
                return VaultStatus.Faulted(Kind, versionResult.FirstErrorLine(), _exePath);
            }

            version = VaultProbe.ParseVersion(versionResult.StdOut);
        }
        catch (VaultCliException ex)
        {
            return VaultStatus.Faulted(Kind, ex.Message, _exePath);
        }

        if (version is not null && version < VaultProbe.MinimumOnePasswordVersion)
        {
            return VaultStatus.Faulted(Kind,
                $"1Password CLI {version} is too old — {VaultProbe.MinimumOnePasswordVersion} or newer is required.",
                _exePath);
        }

        // Everything past this point talks to the user's account, and `op` answers by asking the
        // desktop app — which is a biometric prompt, once per command, and there are several
        // commands below. A discovery probe stops here and says only what it can see from disk.
        if (depth == VaultProbeDepth.Discovery)
        {
            return VaultStatus.NotConnected(Kind, _exePath, version?.ToString());
        }

        CliResult vaultList;
        try
        {
            vaultList = await RunAsync(["vault", "list", "--format", "json"], ct: ct);
        }
        catch (VaultCliException ex)
        {
            return VaultStatus.Faulted(Kind, ex.Message, _exePath);
        }

        if (!vaultList.Succeeded)
        {
            // Everything that is not a working session lands here: locked, signed out, desktop-app
            // integration turned off, a service-account token that has expired. They are one state
            // as far as the app is concerned — "you need to authenticate" — and the CLI's own
            // wording is more accurate than anything guessed from an exit code.
            return new VaultStatus(Kind, VaultAvailability.NotSignedIn,
                _exePath, version?.ToString(), Detail: vaultList.FirstErrorLine());
        }

        var vaults = ParseVaults(vaultList.StdOut);
        _vaultId = vaults.FirstOrDefault(v => v.Name == _vaultName)?.VaultId;

        List<OnePasswordVault> configured = [];

        // Skipped once the user has disconnected. Rediscovery is what makes a vault stick across
        // restarts, and straight after a disconnect it would silently reattach the very vault they
        // just stepped away from — leaving them no way to pick a different one.
        if (_vaultId is null && !_discoveryDisabled)
        {
            configured = await FindConfiguredVaultsAsync(vaults, ct);

            if (configured.Count == 1)
            {
                // A vault the user pointed RavensPort at is not remembered on this PC — nothing
                // about this app is — so it is found the same way the backend itself is: whichever
                // vault actually holds the configuration is the one that was being used.
                _vaultName = configured[0].Name;
                _vaultId = configured[0].VaultId;

                activityLog.Log($"VAULT 1Password — using the existing '{_vaultName}' vault, "
                                + "which holds the RavensPort configuration");
            }
        }

        return new VaultStatus(
            Kind,
            Resolve(),
            _exePath,
            version?.ToString(),
            _vaultId,
            VaultName: _vaultName,
            Vaults: [.. vaults.Select(v => v.Name)],
            ConfiguredVaults: [.. configured.Select(v => v.Name)],

            // Computed even when a vault is already resolved, though it costs an `op item list` per
            // candidate. Skipping it there looked like free speed and is not: this list is what the
            // setup page offers for *switching* vaults, so an install already connected to
            // 'RavensPort' would lose the ability to move to 'RavensPort Work'. Parallel instead.
            AdoptableVaults: await FindAdoptableVaultsAsync(vaults, configured, ct));

        // More than one configured vault is separate profiles, and opening one would mean
        // overwriting the other's note on the next save. That is a question for the user.
        VaultAvailability Resolve() =>
            _vaultId is not null ? VaultAvailability.Ready
            : configured.Count > 1 ? VaultAvailability.VaultChoiceNeeded
            : VaultAvailability.VaultMissing;
    }

    public async Task CreateVaultAsync(string vaultName, CancellationToken ct = default)
    {
        var name = vaultName.Trim();
        if (name.Length == 0) throw VaultAdoption.NameRequired();

        RequireExe();

        var listed = await RunAsync(["vault", "list", "--format", "json"], ct: ct);
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
            ["vault", "create", name, "--description", VaultConstants.VaultDescription, "--format", "json"],
            timeout: CliRunner.WriteTimeout, ct: ct);

        if (!result.Succeeded)
        {
            throw new VaultSaveException(
                $"Could not create the '{name}' vault: {result.FirstErrorLine()}",
                partiallyApplied: false);
        }

        _vaultId = ReadString(JsonNode.Parse(result.StdOut), "id")
                   ?? throw new VaultSaveException(
                       "1Password created the vault but did not report its id.", partiallyApplied: false);

        _vaultName = name;
        _loadedRevision = 0;
        _discoveryDisabled = false;

        // The user has named a vault, which is the only thing that re-opens writing.
        _writesDisabled = false;

        // Stamped straight away: the config item is what identifies this vault as RavensPort's on
        // the next launch, and the name the user just chose is not written down anywhere on this PC.
        await SaveAsync(new ConfigStore(), ct);

        activityLog.Log($"VAULT 1Password — created the '{_vaultName}' vault");
    }

    /// <summary>
    /// Takes over a vault the user already has. See <see cref="VaultAdoption"/> for why only an
    /// empty vault or one RavensPort has written to is accepted.
    /// </summary>
    public async Task UseExistingVaultAsync(string vaultName, CancellationToken ct = default)
    {
        var name = vaultName.Trim();
        if (name.Length == 0) throw VaultAdoption.NameRequired();

        RequireExe();

        var listed = await RunAsync(["vault", "list", "--format", "json"], ct: ct);
        if (!listed.Succeeded) throw new VaultLockedException(Kind, listed.FirstErrorLine());

        var vaults = ParseVaults(listed.StdOut);

        // Case-insensitive, because the user is typing a name they read in the 1Password UI and
        // being told "no such vault" over capitalisation would be a poor way to spend their time.
        var match = vaults.FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase))
                    ?? throw VaultAdoption.NoSuchVault(name, vaults.Select(v => v.Name));

        var items = await ListItemsAsync(match.VaultId, match.Name, ct);
        var noteSummary = items.FirstOrDefault(i => i.Title == VaultItemNaming.ConfigTitle);

        string? note = null;
        if (noteSummary is not null)
        {
            note = (await GetItemAsync(noteSummary.ItemId, match.VaultId, ct))?.Field(VaultFields.NoteContent) ?? "";
        }

        var outcome = VaultAdoption.Judge(match.Name, [.. items.Select(item => item.Title)], note);

        _vaultName = match.Name;
        _vaultId = match.VaultId;
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

        activityLog.Log($"VAULT 1Password — using the existing '{_vaultName}' vault");
    }

    /// <summary>
    /// Lets a probe resolve a vault again, for a manager the user is deliberately connecting to.
    ///
    /// Half of undoing <see cref="Forget"/>, and deliberately only half. Discovery is what finds a
    /// vault adopted under a name other than RavensPort, so without this a reconnect to one could
    /// only ever report it missing. Writing stays shut until <see cref="AllowWrites"/>, because a
    /// probe can fail and a provider that is writable with no vault resolved is the exact state
    /// Forget exists to prevent.
    /// </summary>
    public void AllowDiscovery() => _discoveryDisabled = false;

    /// <summary>
    /// Re-opens writing. Called once a probe has actually resolved a vault, which is the same
    /// guarantee naming one by hand gives — see the comment in <see cref="Forget"/>.
    /// </summary>
    public void AllowWrites() => _writesDisabled = false;

    public void Forget()
    {
        _vaultId = null;
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

        var items = await ListOwnedSummariesAsync(ct);

        var noteSummary = items.FirstOrDefault(i => i.Title == VaultItemNaming.ConfigTitle);
        if (noteSummary is null)
        {
            // No note means nothing has ever been saved here. An empty store is the correct
            // answer, and the setup page has already confirmed the vault itself exists.
            _loadedRevision = 0;
            return new ConfigStore();
        }

        var noteItem = await GetItemAsync(noteSummary.ItemId, ct);
        var document = VaultDocument.TryParse(noteItem?.Field(VaultFields.NoteContent) ?? "");

        if (document is null)
        {
            // The note is free text the user can open and edit in 1Password, so a broken one is a
            // mistake rather than corruption. Coming up empty is recoverable; refusing to start is
            // not, and the old configuration is still sitting in the vault to be repaired.
            LastLoadWarning = $"The '{VaultItemNaming.ConfigTitle}' item could not be read as configuration, "
                              + "so RavensPort started with nothing. The item has not been changed.";
            _loadedRevision = 0;
            return new ConfigStore();
        }

        if (document.IsFromANewerLayout)
        {
            LastLoadWarning = $"The vault was written by a newer version of RavensPort "
                              + $"(layout {document.VaultLayoutVersion}). Some settings may not be understood.";
        }

        _loadedRevision = document.Revision;

        var secrets = await ResolveSecretsAsync(document.Index, items, ct);
        var report = new VaultLoadReport();
        var store = VaultMapper.ComposeStore(document, secrets, report);

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

    /// <summary>
    /// Already a full rewrite: every edit sends the whole template, including empty values for
    /// fields that should go away, so there is nothing for a forced version to do differently.
    /// </summary>
    public Task RewriteAllAsync(ConfigStore store, CancellationToken ct = default) => SaveAsync(store, ct);

    /// <summary>Every live item in the vault, ours and the user's. No item contents are fetched.</summary>
    public async Task<IReadOnlyList<VaultItemEntry>> ListLiveItemsAsync(CancellationToken ct = default)
    {
        await RequireVaultAsync(ct);

        var items = await ListItemsAsync(_vaultId!, _vaultName, ct);

        return [.. items.Select(item => VaultItemEntry.Classify(item.ItemId, item.Title))];
    }

    public async Task DeleteItemAsync(string itemId, CancellationToken ct = default)
    {
        RequireWritesAllowed();
        await RequireVaultAsync(ct);

        var result = await RunAsync(
            ["item", "delete", itemId, "--vault", _vaultId!], timeout: CliRunner.WriteTimeout, ct: ct);

        if (!result.Succeeded)
        {
            throw new VaultSaveException(
                $"1Password would not delete the item: {result.FirstErrorLine()}", partiallyApplied: false);
        }
    }

    public async Task SaveAsync(ConfigStore store, CancellationToken ct = default)
    {
        RequireWritesAllowed();
        await RequireVaultAsync(ct);

        var items = await ListOwnedSummariesAsync(ct);
        var noteSummary = items.FirstOrDefault(i => i.Title == VaultItemNaming.ConfigTitle);

        // Two indexes, not one. The previous is what finds the item a record already has; the new
        // one is what the note gets, and it holds only what this save actually wrote. Carrying the
        // old entries forward left the note pointing at items that had been deleted — a dangling
        // reference the design is supposed to make impossible, and a wasted fetch on every load.
        var previousIndex = await ReadIndexAsync(noteSummary, items, ct);
        var index = new VaultIndex();

        var written = 0;
        var secretItems = VaultMapper.BuildSecretItems(store, previousIndex);

        foreach (var item in secretItems)
        {
            ct.ThrowIfCancellationRequested();

            var existingId = ResolveExistingItem(items, item);
            var isUnchanged = existingId is not null
                              && previousIndex.Fingerprints.TryGetValue(item.RecordId, out var previousFingerprint)
                              && previousFingerprint == item.Fingerprint;

            if (isUnchanged)
            {
                index.For(item.Role)[item.RecordId] = existingId!;
                index.Fingerprints[item.RecordId] = item.Fingerprint;
                continue;
            }

            try
            {
                var itemId = existingId is null
                    ? await CreateItemAsync(item.Spec, ct)
                    : await EditItemAsync(existingId, item.Spec, ct);

                index.For(item.Role)[item.RecordId] = itemId;
                index.Fingerprints[item.RecordId] = item.Fingerprint;
                written++;
            }
            catch (Exception ex) when (ex is VaultCliException or VaultSaveException)
            {
                throw new VaultSaveException(
                    $"Could not save '{item.Spec.Title}' to 1Password: {ex.Message}",
                    partiallyApplied: written > 0,
                    ex);
            }
        }

        // The note goes last, carrying the index the writes above produced. A crash before this
        // point leaves orphan items that the next save sweeps; a crash after leaves a note one
        // revision behind. Neither leaves the note pointing at an item that does not exist.
        try
        {
            var note = VaultMapper.BuildConfigNote(store, index, _loadedRevision + 1);

            if (noteSummary is null)
            {
                await CreateItemAsync(note, ct);
            }
            else
            {
                await EditItemAsync(noteSummary.ItemId, note, ct);
            }

            _loadedRevision++;
        }
        catch (Exception ex) when (ex is VaultCliException or VaultSaveException)
        {
            throw new VaultSaveException(
                $"Could not save the configuration item to 1Password: {ex.Message}",
                partiallyApplied: written > 0,
                ex);
        }

await ReconcileDeletionsAsync(items, secretItems, previousIndex, ct);

        // After the sweep, deliberately. Setting it first would let a save authorise its own
        // deletions, which is no guard at all: the very first save by a provider that has never
        // read this vault would sweep it on the strength of a baseline that save had just invented.
        // A save earns the baseline for the *next* one — on a first run, where there was no note to
        // read, that is what eventually allows tidying up at all.
        _hasCompletedLoad = true;
    }

    // ---- CLI calls ------------------------------------------------------------------------------

    private async Task<string> CreateItemAsync(VaultItemSpec spec, CancellationToken ct)
    {
        var result = await RunAsync(
            ["item", "create", "--vault", _vaultId!, "--format", "json", "-"],
            stdin: BuildTemplate(spec, includeClears: false).ToJsonString(),
            timeout: CliRunner.WriteTimeout,
            ct: ct);

        if (!result.Succeeded)
        {
            throw new VaultSaveException(result.FirstErrorLine(), partiallyApplied: false);
        }

        return ReadString(JsonNode.Parse(result.StdOut), "id")
               ?? throw new VaultSaveException("1Password created the item but did not report its id.", false);
    }

    private async Task<string> EditItemAsync(string itemId, VaultItemSpec spec, CancellationToken ct)
    {
        // includeClears: an edit merges rather than replaces, so a field that has legitimately gone
        // away — the access token of a credential the user just disconnected — would otherwise sit
        // in the vault forever. Sending it as empty is what actually revokes it from the item.
        var result = await RunAsync(
            ["item", "edit", itemId, "--vault", _vaultId!, "--format", "json", "-"],
            stdin: BuildTemplate(spec, includeClears: true).ToJsonString(),
            timeout: CliRunner.WriteTimeout,
            ct: ct);

        if (!result.Succeeded)
        {
            throw new VaultSaveException(result.FirstErrorLine(), partiallyApplied: false);
        }

        return itemId;
    }

    /// <summary>
    /// Items in the active vault that this app owns. Everything else in the vault is the user's and
    /// is never read, never written, and — crucially — never a candidate for deletion.
    /// </summary>
    private async Task<List<VaultItemSummary>> ListOwnedSummariesAsync(CancellationToken ct)
    {
        var items = await ListItemsAsync(_vaultId!, _vaultName, ct);
        return items.Where(i => VaultItemNaming.IsOwned(i.Title)).ToList();
    }

    /// <summary>
    /// Everything in a vault, owned or not. The unfiltered count is what decides whether a vault
    /// the user named is empty enough to take over, so this deliberately does not filter.
    /// </summary>
    private async Task<List<VaultItemSummary>> ListItemsAsync(string vaultId, string vaultLabel, CancellationToken ct)
    {
        var result = await RunAsync(["item", "list", "--vault", vaultId, "--format", "json"], ct: ct);

        if (!result.Succeeded)
        {
            throw new VaultCliException($"Could not list the '{vaultLabel}' vault: {result.FirstErrorLine()}");
        }

        var items = new List<VaultItemSummary>();

        foreach (var node in JsonNode.Parse(result.StdOut) as JsonArray ?? [])
        {
            var id = ReadString(node, "id");
            var title = ReadString(node, "title");

            // An archived item is one the user has put away. Reading it would make a vault they
            // had cleared look full and a credential they had removed look present — the same
            // trap Proton Pass's trash sets. Only items with no state at all are live.
            var state = ReadString(node, "state");
            if (state is { Length: > 0 } && !string.Equals(state, "active", StringComparison.OrdinalIgnoreCase)) continue;

            if (id is not null && title is not null) items.Add(new VaultItemSummary(id, title));
        }

        return items;
    }

    private Task<VaultItemContents?> GetItemAsync(string itemId, CancellationToken ct) =>
        GetItemAsync(itemId, _vaultId!, ct);

    /// <summary>
    /// How many times a read is attempted before it is called inconclusive. `op` reaches the
    /// desktop app over IPC, and that handshake times out under load often enough to see in a
    /// single startup — see the retry note on <see cref="GetItemAsync"/>.
    /// </summary>
    private const int ReadAttempts = 3;

    /// <summary>
    /// Whether `op` is saying the item does not exist, as opposed to saying it could not look.
    ///
    /// **Matched narrowly, and everything unmatched is treated as unreachable.** That direction is
    /// deliberate: mistaking "gone" for "unreachable" costs a failed load the user can retry, while
    /// mistaking "unreachable" for "gone" deletes their credential. A new `op` release wording a
    /// message differently must therefore fall through to the safe side.
    /// </summary>
    private static bool SaysItemDoesNotExist(string stderr) =>
        stderr.Contains("isn't an item", StringComparison.OrdinalIgnoreCase)
        || stderr.Contains("no item matching", StringComparison.OrdinalIgnoreCase)
        || stderr.Contains("item not found", StringComparison.OrdinalIgnoreCase)
        || stderr.Contains("doesn't exist", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// One item's contents. Null means 1Password positively reported it is not there; anything else
    /// that goes wrong throws.
    ///
    /// **This used to return null for every failure**, on the reasoning that a listing and a fetch
    /// are separate calls and an item can be deleted between them. That is true, but it made a
    /// transient failure indistinguishable from a deletion — and the consequences are not
    /// symmetric. A null here drops the record from the store, which marks the store changed, which
    /// makes the next save rewrite the note without it and then
    /// <see cref="ReconcileDeletionsAsync"/> delete the item from the vault. So a desktop-app IPC
    /// timeout, which shows up in the logs of an ordinary startup, silently destroyed a credential.
    ///
    /// Retried before giving up, because the common failure is the desktop app being busy rather
    /// than absent, and a read is idempotent so there is nothing to be careful about in repeating
    /// it.
    /// </summary>
    private async Task<VaultItemContents?> GetItemAsync(string itemId, string vaultId, CancellationToken ct)
    {
        CliResult result = default;
        Exception? lastFailure = null;

        for (var attempt = 1; attempt <= ReadAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                result = await RunAsync(["item", "get", itemId, "--vault", vaultId, "--format", "json"], ct: ct);
                lastFailure = null;

                if (result.Succeeded) break;

                // The one failure that is an answer rather than a fault.
                if (SaysItemDoesNotExist(result.StdErr)) return null;
            }
            catch (VaultCliException ex)
            {
                // The runner's own timeout or a process that would not start. Inconclusive, so it
                // is retried rather than believed.
                lastFailure = ex;
            }

            if (attempt < ReadAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(400 * attempt), ct).ConfigureAwait(false);
            }
        }

        if (lastFailure is not null || !result.Succeeded)
        {
            var detail = lastFailure?.Message ?? result.FirstErrorLine();

            throw new VaultCliException(
                $"1Password could not be asked for one of the items holding your secrets ({detail}). "
                + "Nothing has been changed — RavensPort will not treat an item it could not read as "
                + "one you deleted.",
                lastFailure);
        }

        var node = JsonNode.Parse(result.StdOut);
        if (node is null) return null;

        var fields = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var field in node["fields"] as JsonArray ?? [])
        {
            var value = ReadString(field, "value");
            if (value is null) continue;

            // Keyed by id, which is what the template sets, with the label as a fallback for a
            // field 1Password rewrote or a user added by hand in its UI.
            if (ReadString(field, "id") is { Length: > 0 } id) fields[id] = value;
            var label = ReadString(field, "title") ?? ReadString(field, "label");
            if (label is { Length: > 0 }) fields.TryAdd(label, value);
        }

        // The Go SDK exposes notes as a top-level property rather than a field.
        if (ReadString(node, "notes") is { Length: > 0 } notes)
        {
            fields[VaultFields.NoteContent] = notes;
        }

        return new VaultItemContents(itemId, ReadString(node, "title") ?? "", fields);
    }

    private async Task ReconcileDeletionsAsync(
        List<VaultItemSummary> existing,
        List<VaultSecretItem> live,
        VaultIndex previousIndex,
        CancellationToken ct)
    {
        // The only irreversible thing this app does to a user's vault, so it refuses to act on a
        // baseline it did not establish itself.
        //
        // Deleting means answering "is this item still wanted", and the store in memory is only a
        // trustworthy answer if it came from a complete read of this vault. Without that read the
        // store may be empty because nothing was loaded rather than because the user removed
        // everything — after a disconnect, a failed load, or a save that raced initialisation —
        // and sweeping then deletes the entire configuration.
        if (!_hasCompletedLoad)
        {
            activityLog.Log(
                "VAULT skipped deleting unused 1Password items — this session has not completed a full "
                + "read of the vault, so it cannot tell an item you removed from one it never saw");
            return;
        }

        var previousRecordIds = previousIndex.Credentials.Keys
            .Concat(previousIndex.RouteKeys.Keys)
            .Concat(previousIndex.FunnelKeys.Keys)
            .ToHashSet();

        var liveRecordIds = live.Select(i => i.RecordId).ToHashSet();

        if (previousRecordIds.Count > 0 && liveRecordIds.Count > 0 && !liveRecordIds.Overlaps(previousRecordIds))
        {
            activityLog.Log(
                "VAULT skipped deleting unused 1Password items — the incoming configuration carries records "
                + "that share no identity with this vault's index, so it cannot authorize deletions here");
            return;
        }

        var keep = live.Select(i => (i.Role, i.RecordId)).ToHashSet();

        // Every item id the note being replaced actually pointed at. This is the whole safety
        // property: an item is only ever deleted if *this vault's own note* claimed it and the
        // store no longer does.
        //
        // The sweep used to work by subtraction over the entire vault — delete anything titled like
        // ours that the store does not account for. That is sound only while the store and the
        // vault describe the same thing, and they can come apart: a save carrying one vault's
        // configuration reached another vault, and every item the incoming note had never heard of
        // looked unwanted. Nine of a user's items were deleted that way.
        //
        // Restricting deletion to the previous index makes that impossible rather than unlikely.
        // A note from elsewhere indexes item ids that do not exist here, so nothing matches and
        // nothing is deleted. A record the user genuinely removed was in the note a moment ago, so
        // it still is. The cost is that an item created by hand, which no note ever referenced, is
        // left alone — which is the right way to be wrong.
        var deletable = previousIndex.Credentials.Values
            .Concat(previousIndex.RouteKeys.Values)
            .Concat(previousIndex.FunnelKeys.Values)
            .ToHashSet(StringComparer.Ordinal);

        var doomed = existing
            .Where(item => deletable.Contains(item.ItemId))
            .Where(item => VaultItemNaming.TryParse(item.Title, out var role, out var id)
                           && role != VaultItemRole.Config
                           && !keep.Contains((role, id)))
            .ToList();

        if (!WithinDeletionBudget(doomed.Count, keep.Count)) return;

        foreach (var item in doomed)
        {
            var result = await RunAsync(
                ["item", "delete", item.ItemId, "--vault", _vaultId!],
                timeout: CliRunner.WriteTimeout, ct: ct);

            if (!result.Succeeded)
            {
                // Deliberately not fatal. The store itself is already saved; a leftover item is
                // untidy, not wrong, and the next save tries again. Failing here would report a
                // successful save as a failure and trigger a pointless rollback.
                activityLog.Log($"VAULT could not delete '{item.Title}': {result.FirstErrorLine()}");
            }
        }
    }

    /// <summary>
    /// The last line of defence: a routine save is never allowed to be a mass deletion.
    ///
    /// Everything above is meant to make an unwanted sweep impossible, and something like it was
    /// meant to be impossible before. This does not reason about *why* the numbers look wrong — it
    /// simply refuses to let a background write remove a large number of a user's items at once,
    /// and says so loudly. Removing several credentials at a stroke is a deliberate act, and the
    /// Settings tab's integrity tools exist to do it with the user watching.
    /// </summary>
    private bool WithinDeletionBudget(int doomed, int kept)
    {
        const int alwaysAllowed = 2;

        if (doomed <= alwaysAllowed || doomed <= kept) return true;

        activityLog.Log(
            $"VAULT REFUSED to delete {doomed} 1Password item(s) during a save that kept only {kept} — "
            + "a routine save does not remove that much at once. Nothing was deleted. Use the vault "
            + "integrity check on the Settings tab if these items really should go.");

        return false;
    }

    // ---- Templates and parsing ------------------------------------------------------------------

    /// <summary>
    /// The item as 1Password's JSON template. Built with JsonNode rather than string
    /// concatenation so a value containing a quote or a backslash cannot break out of the
    /// document — the field values here are user-supplied secrets and names.
    /// </summary>
    private JsonNode BuildTemplate(VaultItemSpec spec, bool includeClears)
    {
        var fields = new JsonArray();
        var present = new HashSet<string>(StringComparer.Ordinal);

        foreach (var field in spec.Fields)
        {
            present.Add(field.Name);
            fields.Add(BuildField(field.Name, field.Value));
        }

        if (includeClears)
        {
            foreach (var name in ClearableSecretFields.Where(n => !present.Contains(n)))
            {
                fields.Add(BuildField(name, ""));
            }
        }

        var notes = "";
        if (spec.Caption is { Length: > 0 } caption && !present.Contains(VaultFields.NoteContent))
        {
            notes = caption;
        }

        var json = new JsonObject
        {
            ["title"] = spec.Title,
            ["category"] = CategoryName(spec.Category),
            ["vault"] = new JsonObject { ["id"] = _vaultId },
            ["fields"] = fields,
        };

        if (notes.Length > 0)
        {
            json["notes"] = notes;
        }
        else
        {
            // If the spec explicitly contains NoteContent field, use that
            var noteField = spec.Fields.FirstOrDefault(f => f.Name == VaultFields.NoteContent);
            if (noteField?.Name != null)
            {
                json["notes"] = noteField.Value;
                // Remove it from fields array since it's mapped to top-level
                var fieldNode = fields.FirstOrDefault(n => ReadString(n, "id") == VaultFields.NoteContent);
                if (fieldNode != null) fields.Remove(fieldNode);
            }
        }

        return json;
    }

    private static JsonObject BuildField(string name, string value)
    {
        var field = new JsonObject
        {
            ["id"] = name,
            ["title"] = name,
            ["fieldType"] = VaultFields.IsConcealed(name) ? "Concealed" : "Text",
            ["value"] = value,
        };

        return field;
    }

    /// <summary>
    /// The category as <c>op</c> writes it in a template — the enum code, not the display name.
    ///
    /// Checked against <c>op item template get</c> on 2.34.1, which emits "SECURE_NOTE", "LOGIN"
    /// and "PASSWORD". Sending the display names ("Secure Note") makes op read no category at all
    /// and refuse the item with <c>"" is not a recognized item category</c> — listing the display
    /// names in the error, which sends you looking in exactly the wrong direction.
    /// </summary>
    private static string CategoryName(VaultItemCategory category) => category switch
    {
        VaultItemCategory.SecureNote => "SecureNote",
        VaultItemCategory.Login => "Login",
        VaultItemCategory.Password => "Password",
        _ => "SecureNote",
    };

    private async Task<VaultIndex> ReadIndexAsync(
        VaultItemSummary? noteSummary, List<VaultItemSummary> items, CancellationToken ct)
    {
        if (noteSummary is null) return new VaultIndex();

        var note = await GetItemAsync(noteSummary.ItemId, ct);
        var document = VaultDocument.TryParse(note?.Field(VaultFields.NoteContent) ?? "");

        // Deliberately no revision comparison. This app assumes a single instance, and the sync
        // queue re-reads and rewrites whenever it can — so a guard here would turn a lock that
        // lifted at an awkward moment into a save that refuses and retries forever, a worse
        // outcome than the concurrent write it would be guarding against. The revision is still
        // stamped into the note, so a second writer is at least visible after the fact.
        //
        // An unreadable note yields an empty index rather than throwing: the guids in the item
        // titles are enough to reconnect everything, and refusing to save would strand the user
        // with a broken note they could only fix by hand.
        return document?.Index ?? new VaultIndex();
    }

    private async Task<Dictionary<(VaultItemRole, Guid), VaultItemContents>> ResolveSecretsAsync(
        VaultIndex index, List<VaultItemSummary> items, CancellationToken ct)
    {
        var wanted = new Dictionary<(VaultItemRole, Guid), string>();

        foreach (var role in new[] { VaultItemRole.Credential, VaultItemRole.RouteKey, VaultItemRole.FunnelKey })
        {
            foreach (var (recordId, itemId) in index.For(role)) wanted[(role, recordId)] = itemId;
        }

        // The index is only a cache. Anything it missed — a note restored from an older version,
        // an item recreated by hand — is recovered from the guid in the title.
        foreach (var item in items)
        {
            if (!VaultItemNaming.TryParse(item.Title, out var role, out var id)) continue;
            if (role == VaultItemRole.Config) continue;

            wanted.TryAdd((role, id), item.ItemId);
        }

        var resolved = new Dictionary<(VaultItemRole, Guid), VaultItemContents>();

        var tasks = wanted.Select(async pair =>
        {
            var ((role, id), itemId) = pair;
            var contents = await GetItemAsync(itemId, ct);
            return (role, id, contents);
        });

        var results = await Task.WhenAll(tasks);

        foreach (var (role, id, contents) in results)
        {
            if (contents is not null) resolved[(role, id)] = contents;
        }

        return resolved;
    }

    /// <summary>
    /// The item this record should be written over, or null to create a fresh one.
    ///
    /// The index is checked <em>against the live listing</em> rather than trusted. An item the note
    /// points at can be gone — deleted in 1Password's own UI, or by the integrity check — and
    /// editing an id that no longer exists fails the whole save with "isn't an item". That made
    /// putting a missing item back impossible: the one operation whose entire job is to recreate
    /// it was the one that could not.
    ///
    /// Falling back to the record id in the title covers the other direction: an item recreated by
    /// hand has an id the note has never seen, and creating a second one would leave two entries
    /// claiming the same record.
    /// </summary>
    private static string? ResolveExistingItem(List<VaultItemSummary> items, VaultSecretItem item)
    {
        if (item.Spec.ItemId is { } indexed && items.Any(summary => summary.ItemId == indexed))
        {
            return indexed;
        }

        return FindByRecord(items, item.Role, item.RecordId);
    }

    private static string? FindByRecord(List<VaultItemSummary> items, VaultItemRole role, Guid recordId) =>
        items.FirstOrDefault(i =>
            VaultItemNaming.TryParse(i.Title, out var itemRole, out var id)
            && itemRole == role && id == recordId)?.ItemId;

    private static List<OnePasswordVault> ParseVaults(string vaultListJson)
    {
        var vaults = new List<OnePasswordVault>();

        foreach (var node in JsonNode.Parse(vaultListJson) as JsonArray ?? [])
        {
            var name = ReadString(node, "title") ?? ReadString(node, "name");
            if (name is not null && ReadString(node, "id") is { } id)
            {
                vaults.Add(new OnePasswordVault(name, id));
            }
        }

        return vaults;
    }

    /// <summary>
    /// Every vault holding a RavensPort configuration, when the expected one is not there. Reads
    /// item titles only — no item contents — because all that is being asked is which vault this
    /// app was last pointed at, and the rest of the user's vaults are none of its business.
    ///
    /// All of them rather than the first: two configured vaults is a user keeping separate
    /// profiles, and picking one at random would open one and overwrite the other on the next save.
    /// </summary>
    private async Task<List<OnePasswordVault>> FindConfiguredVaultsAsync(
        List<OnePasswordVault> vaults, CancellationToken ct)
    {
        var matchingVaults = vaults.Where(v => VaultProfile.Matches(v.Name)).ToList();
        var matchingChecks = matchingVaults.Select(async vault =>
        {
            try
            {
                var items = await ListItemsAsync(vault.VaultId, vault.Name, ct);
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
                var items = await ListItemsAsync(vault.VaultId, vault.Name, ct);
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
    /// The vaults the setup page may offer: either empty or already RavensPort's, and — signing in
    /// as a person — named after RavensPort.
    ///
    /// <b>The name filter exists to protect a human's other vaults.</b> Signed in through the
    /// desktop app, this app can see everything the user can, Private included. A picker listing all
    /// of it would be inviting someone to point a credential store at their personal vault, and
    /// would read as an app rummaging through things that are none of its business. The naming rule
    /// is what keeps the offer to vaults made for this purpose.
    ///
    /// <b>A service account has already been scoped, by hand, by someone.</b> It can see exactly the
    /// vaults it was granted and cannot see a Private vault at all, so the visible set <em>is</em>
    /// the deliberate choice the name filter is a proxy for — applying it again filters a list that
    /// was already filtered, on a rule the grant never had to follow. A user who granted their
    /// service account one vault called "Automation" was then offered nothing to pick and could not
    /// choose it, which is the whole feature refusing to start over a naming convention.
    ///
    /// <see cref="VaultAdoption.LooksAdoptable"/> still applies either way: empty, or already
    /// holding a RavensPort configuration. That is the check that actually protects data, and it is
    /// the one that never relaxes.
    /// </summary>
    /// <param name="configured">
    /// Vaults the discovery pass has already been through, so their items are not listed twice.
    /// Holding a configuration is precisely what makes a vault adoptable.
    /// </param>
    private async Task<List<string>> FindAdoptableVaultsAsync(
        List<OnePasswordVault> vaults, List<OnePasswordVault> configured, CancellationToken ct)
    {
        var candidates = UsesServiceAccount
            ? vaults
            : vaults.Where(v => VaultProfile.Matches(v.Name)).ToList();

        // Concurrently, like FindConfiguredVaultsAsync above. These are independent vaults with
        // nothing to order between them, and each look is a subprocess launch measured in seconds —
        // done one after another this was the slowest thing on the setup path.
        var checks = candidates.Select(async vault =>
        {
            if (configured.Any(c => c.VaultId == vault.VaultId)) return vault.Name;

            try
            {
                var items = await ListItemsAsync(vault.VaultId, vault.Name, ct);
                return VaultAdoption.LooksAdoptable([.. items.Select(i => i.Title)]) ? vault.Name : null;
            }
            catch (Exception ex) when (ex is VaultCliException or JsonException)
            {
                // A vault this session cannot list cannot be offered either — it would be refused
                // for the same reason the moment it was picked. The rest still get an answer.
                activityLog.Log($"VAULT could not look inside the '{vault.Name}' vault: {ex.Message}");
                return null;
            }
        });

        return [.. (await Task.WhenAll(checks)).Where(name => name is not null).Select(name => name!)];
    }

    /// <summary>One vault from <c>vault list</c>.</summary>
    private sealed record OnePasswordVault(string Name, string VaultId);

    private static string? ReadString(JsonNode? node, string property)
    {
        try
        {
            return node?[property]?.GetValue<string>();
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            // The property exists but is not a string. Treating that as absent is right: these
            // shapes come from another program's output, not from a contract this app controls.
            return null;
        }
    }

    /// <summary>
    /// Locates the binary for a call that does not go through a probe first — creating or taking
    /// over a vault, both of which can be the first thing this provider is asked to do.
    /// </summary>
    private void RequireExe()
    {
        _exePath ??= exePathOverride ?? VaultProbe.FindOnePassword();

        if (_exePath is null || (!File.Exists(_exePath) && _exePath != "native"))
        {
            throw new VaultCliException("The 1Password CLI is not installed.");
        }
    }

    private async Task RequireVaultAsync(CancellationToken ct)
    {
        if (_vaultId is not null) return;

        var status = await ProbeAsync(ct);

        _vaultId = status.VaultId ?? throw (status.Availability switch
        {
            VaultAvailability.NotSignedIn => new VaultLockedException(Kind, status.Detail),
            VaultAvailability.NotInstalled => new VaultCliException("The 1Password CLI is not installed."),
            VaultAvailability.VaultMissing =>
                new VaultCliException($"The '{_vaultName}' vault does not exist yet."),
            VaultAvailability.VaultChoiceNeeded =>
                new VaultCliException("More than one 1Password vault holds a RavensPort configuration. "
                                      + "Choose which one to use on the setup page."),
            _ => (Exception)new VaultCliException(status.Detail ?? "1Password is unavailable."),
        });
    }

    /// <summary>
    /// Runs one command, against whichever of the two transports fits the way the user signed in.
    ///
    /// <b>Desktop app</b> goes to the in-process SDK, which is the only thing that can talk to the
    /// desktop app at all.
    ///
    /// <b>Service account</b> prefers the real <c>op.exe</c> when it is installed. Both routes work
    /// headless, so the reason is isolation: the token lives in a child process that exits, rather
    /// than being handed to a library mapped into this one for the rest of the run. It also keeps
    /// the credential out of the SDK entirely, and the environment-block plumbing it uses is the
    /// same one Proton Pass has been using all along.
    ///
    /// Falling back to the SDK matters as much as preferring the CLI: a machine chosen for the token
    /// mode is quite likely to have no 1Password software installed whatsoever, and requiring a CLI
    /// download would defeat the point of a mode whose selling point is needing nothing local.
    /// </summary>
    private Task<CliResult> RunAsync(
        IReadOnlyList<string> args, string? stdin = null, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var env = ServiceAccountToken is { Length: > 0 } token
            ? new Dictionary<string, string> { ["OP_SERVICE_ACCOUNT_TOKEN"] = token }
            : null;

        if (env is not null && processRunner is not null && ResolveCliExe() is { } opExe)
        {
            return processRunner.RunAsync(opExe, args, stdin, env, timeout, ct);
        }

        return cliRunner.RunAsync(
            _exePath ?? throw new VaultCliException("The 1Password CLI has not been located yet."),
            args, stdin, env, timeout, ct);
    }

    /// <summary>
    /// The real <c>op.exe</c>, or null when there is no usable one. Answered once per provider.
    ///
    /// "Usable" means more than present. The CLI is a preference, not a requirement — the token
    /// works perfectly well through the in-process SDK — so anything that would stop this launch
    /// has to send the work down the other route rather than fail the whole connection. A machine
    /// where the CLI cannot be verified is exactly a machine where the SDK should be used instead,
    /// and the user should never have to know either happened.
    ///
    /// That is not a theoretical case. A user's install refused the CLI outright: WinGet's copy is a
    /// symlink, following it failed inside RavensPort's process while succeeding elsewhere, and the
    /// trust policy — correctly, on the evidence available to it — declined to run a file it could
    /// not verify. The right answer there was never "the feature is broken"; it was "use the SDK".
    ///
    /// Deliberately ignores <c>exePathOverride</c>: that is either the "native" sentinel, which is
    /// not a file, or a test's stub path, and neither is a CLI this should launch for real.
    /// </summary>
    private string? ResolveCliExe()
    {
        if (_cliExeResolved) return _cliExePath;

        _cliExeResolved = true;

        var found = exePathOverride is null or NativeExePath
            ? (cliExeLocator ?? VaultProbe.FindOnePassword)()
            : null;

        if (found is null) return _cliExePath = null;

        // Asked here rather than left to the launcher, because a refusal at launch is a failed
        // vault operation while a refusal here is just a quieter transport.
        var decision = trustPolicy.Decide(found);

        if (!decision.Allowed)
        {
            activityLog.Log(
                $"VAULT 1Password service account — not using the CLI at {found} ({decision.Summary}). "
                + "Connecting to 1Password directly instead, which needs no CLI.");

            return _cliExePath = null;
        }

        activityLog.Log(
            $"VAULT 1Password service account — using the 1Password CLI at {found}, "
            + "so the token stays in a child process rather than in RavensPort");

        return _cliExePath = found;
    }
}
