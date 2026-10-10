using RavensPort.Core.Models;

namespace RavensPort.Core.Vault;

/// <summary>
/// Thrown by <see cref="ReadOnlyConfigVault"/> for anything that would change the vault.
/// </summary>
public sealed class VaultReadOnlyException(string message) : InvalidOperationException(message);

/// <summary>
/// A vault that can be read and never written: everything that reads is forwarded, and everything
/// that would write, delete, create or adopt throws.
///
/// For the headless Proton Pass session, which is read-only by design — configuration comes out of
/// the vault, edits live in memory, and nothing goes back. <see cref="VaultSyncQueue"/> already
/// declines to write while the gate says read-only, so in normal running nothing reaches the throw.
/// This is the guarantee rather than the mechanism: a write path added later, or one that forgot to
/// ask, fails loudly instead of quietly changing a vault the user was told would not be touched.
/// </summary>
public sealed class ReadOnlyConfigVault(IConfigVault inner) : IConfigVault
{
    public IConfigVault Inner => inner;

    public VaultBackendKind Kind => inner.Kind;

    public string VaultName => inner.VaultName;

    public string? LastLoadWarning => inner.LastLoadWarning;

    public IReadOnlyList<string> LastLoadRemovals => inner.LastLoadRemovals;

    public Task<VaultStatus> ProbeAsync(CancellationToken ct = default) => inner.ProbeAsync(ct);

    public Task<VaultStatus> ProbeAsync(VaultProbeDepth depth, CancellationToken ct = default) =>
        inner.ProbeAsync(depth, ct);

    public Task CreateVaultAsync(string vaultName, CancellationToken ct = default) => throw Refuse("create a vault");

    public Task UseExistingVaultAsync(string vaultName, CancellationToken ct = default) =>
        throw Refuse("adopt a vault");

    public void Forget() => inner.Forget();

    public Task<ConfigStore> LoadAsync(CancellationToken ct = default) => inner.LoadAsync(ct);

    public Task SaveAsync(ConfigStore store, CancellationToken ct = default) => throw Refuse("save");

    public Task RewriteAllAsync(ConfigStore store, CancellationToken ct = default) => throw Refuse("rewrite the vault");

    public Task<IReadOnlyList<VaultItemEntry>> ListLiveItemsAsync(CancellationToken ct = default) =>
        inner.ListLiveItemsAsync(ct);

    public Task DeleteItemAsync(string itemId, CancellationToken ct = default) => throw Refuse("delete an item");

    private VaultReadOnlyException Refuse(string what) => new(
        $"This {VaultLockGuidance.DisplayName(inner.Kind)} session is read-only, so RavensPort will not "
        + $"{what}. Changes are kept in memory and discarded when it exits.");
}
