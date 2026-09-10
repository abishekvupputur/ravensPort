using System.Text.Json;
using System.Text.Json.Serialization;
using RavensPort.Core.Models;

namespace RavensPort.Core.Vault;

/// <summary>
/// The contents of the "RavensPort Config" note: the whole store minus its secrets, plus what is
/// needed to find the secrets again and to notice another machine writing at the same time.
/// </summary>
public sealed class VaultDocument
{
    public const int CurrentLayoutVersion = 1;

    public int VaultLayoutVersion { get; set; } = CurrentLayoutVersion;

    /// <summary>
    /// Bumped on every save. Both managers sync, so two installs pointed at one vault is a real
    /// configuration — and without this they would overwrite each other's routes and keys in
    /// silence, a failure mode the single local file never had. A save re-reads this first and
    /// refuses if it moved since the load it is based on.
    /// </summary>
    public long Revision { get; set; }

    /// <summary>Which machine wrote it, so a conflict can name the other side.</summary>
    public string WrittenBy { get; set; } = "";

    public DateTimeOffset WrittenUtc { get; set; }

    public ConfigStore Store { get; set; } = new();

    /// <summary>Record id to vault item id, so a load is a direct fetch rather than a title scan.</summary>
    public VaultIndex Index { get; set; } = new();

    [JsonIgnore]
    public bool IsFromANewerLayout => VaultLayoutVersion > CurrentLayoutVersion;

    public string Serialize() => JsonSerializer.Serialize(this, VaultRedaction.NoteOptions);

    public static VaultDocument? TryParse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<VaultDocument>(json, VaultRedaction.NoteOptions);
        }
        catch (JsonException)
        {
            // The note is free text that a user can open and edit in their password manager, so
            // "someone broke the JSON" is a real case rather than a corruption. The caller falls
            // back to rebuilding from item titles.
            return null;
        }
    }
}

/// <summary>
/// Which vault item holds each record's secret.
///
/// A cache, not the source of truth: every entry is recoverable by scanning item titles for the
/// record's guid, which is what happens when this is missing or stale. Keeping it means a load is
/// N direct fetches instead of a list plus N fetches.
/// </summary>
public sealed class VaultIndex
{
    public Dictionary<Guid, string> Credentials { get; set; } = [];
    public Dictionary<Guid, string> RouteKeys { get; set; } = [];
    public Dictionary<Guid, string> FunnelKeys { get; set; } = [];
    public Dictionary<Guid, string> ApiBridgeKeys { get; set; } = [];
    public Dictionary<Guid, string> Fingerprints { get; set; } = [];

    public Dictionary<Guid, string> For(VaultItemRole role) => role switch
    {
        VaultItemRole.Credential => Credentials,
        VaultItemRole.RouteKey => RouteKeys,
        VaultItemRole.FunnelKey => FunnelKeys,
        VaultItemRole.ApiBridgeKey => ApiBridgeKeys,
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "The config note is not indexed by record."),
    };

    public string? Find(VaultItemRole role, Guid id) =>
        For(role).TryGetValue(id, out var itemId) ? itemId : null;
}
