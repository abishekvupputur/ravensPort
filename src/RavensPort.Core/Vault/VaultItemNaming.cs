using System.Globalization;
using System.Text.RegularExpressions;

namespace RavensPort.Core.Vault;

/// <summary>What a vault item belongs to.</summary>
public enum VaultItemRole
{
    Config,
    Credential,
    RouteKey,
    FunnelKey,
    ApiBridgeKey,

    /// <summary>
    /// One API bridge's manifest. Its own item rather than a section of the topology note,
    /// because a manifest is a document the user wrote — often the largest thing in the whole
    /// configuration — and burying it in a note that is rewritten on every token refresh made it
    /// both hard to read in the password manager and expensive to store.
    /// </summary>
    ApiBridgeManifest,
}

/// <summary>
/// The roles that get an item of their own, in one place.
///
/// Every provider resolves these by walking this list, and each of them used to spell it out
/// itself. The failure mode of a missed copy is the quiet kind: the new role's items are written
/// to the vault correctly and never read back, so the endpoint works until the first restart and
/// then answers 403 — or serves an empty toolset — with nothing logged anywhere. One list means
/// adding a role cannot be done halfway.
///
/// Most of these hold a secret. <see cref="VaultItemRole.ApiBridgeManifest"/> does not; it is
/// here because it is stored the same way, per record and outside the note.
/// </summary>
public static class VaultItemRoles
{
    public static readonly VaultItemRole[] PerRecord =
    [
        VaultItemRole.Credential,
        VaultItemRole.RouteKey,
        VaultItemRole.FunnelKey,
        VaultItemRole.ApiBridgeKey,
        VaultItemRole.ApiBridgeManifest,
    ];
}

/// <summary>
/// Item titles, and how to read a record back out of one.
///
/// Every item this app owns starts with <see cref="Prefix"/>. That is what makes the vault safe to
/// share with the user's other things: reconciliation only ever considers prefixed items, so
/// nothing else in the RavensPort vault can be deleted by a save no matter what it is called.
///
/// The trailing "[guid]" is the real identity. A user renaming a credential must retitle the
/// existing item rather than orphan it and create a second one, and the index in the config note
/// can go stale (restored from an older version, or an item recreated by hand) — in which case
/// scanning titles for the guid is what reconnects everything.
/// </summary>
public static partial class VaultItemNaming
{
    public const string Prefix = "RavensPort ";

    /// <summary>Fixed title of the topology note. Found by title, since nothing else points at it.</summary>
    public const string ConfigTitle = "RavensPort Config";

    public static string ForCredential(Guid id, string name) =>
        $"{Prefix}credential — {Clean(name)} [{id:D}]";

    public static string ForRouteKey(Guid id, string pathPrefix) =>
        $"{Prefix}route key — {Clean(pathPrefix)} [{id:D}]";

    public static string ForFunnelKey(Guid id, string slug) =>
        $"{Prefix}funnel key — /mcp/{Clean(slug)} [{id:D}]";

    public static string ForApiBridgeKey(Guid id, string slug) =>
        $"{Prefix}api bridge key — /api-mcp/{Clean(slug)} [{id:D}]";

    public static string ForApiBridgeManifest(Guid id, string slug) =>
        $"{Prefix}api bridge manifest — /api-mcp/{Clean(slug)} [{id:D}]";

    public static bool IsOwned(string title) =>
        title.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>
    /// Reads the role and record id back out of a title. Returns false for the config note (which
    /// has no guid) and for anything this app does not own.
    /// </summary>
    public static bool TryParse(string title, out VaultItemRole role, out Guid id)
    {
        role = default;
        id = Guid.Empty;

        if (!IsOwned(title)) return false;

        if (title.Equals(ConfigTitle, StringComparison.Ordinal))
        {
            role = VaultItemRole.Config;
            return true;
        }

        var match = TrailingGuid().Match(title);
        if (!match.Success) return false;

        if (!Guid.TryParseExact(match.Groups[1].Value, "D", out id)) return false;

        role = RoleOf(title);

        return role != default;
    }

    /// <summary>
    /// Which role a title's wording claims. <see cref="VaultItemRole.Config"/> is not reachable
    /// here — the config note has no guid and is matched by name before this is asked — so an
    /// unrecognised title falls through to <c>default</c>, which the caller reads as "not ours".
    /// </summary>
    private static VaultItemRole RoleOf(string title)
    {
        if (title.Contains("credential —", StringComparison.Ordinal)) return VaultItemRole.Credential;
        if (title.Contains("route key —", StringComparison.Ordinal)) return VaultItemRole.RouteKey;
        if (title.Contains("funnel key —", StringComparison.Ordinal)) return VaultItemRole.FunnelKey;
        if (title.Contains("api bridge key —", StringComparison.Ordinal)) return VaultItemRole.ApiBridgeKey;
        if (title.Contains("api bridge manifest —", StringComparison.Ordinal)) return VaultItemRole.ApiBridgeManifest;

        return default;
    }

    /// <summary>
    /// Keeps a user-chosen name from wrecking the title. Newlines would break the one-line title
    /// the CLIs expect, and a trailing "[...]" in the name itself would fight the guid suffix the
    /// parser looks for. Length is capped so a pasted URL does not produce an unreadable list.
    /// </summary>
    private static string Clean(string value)
    {
        var collapsed = Whitespace().Replace(value, " ").Replace("[", "(").Replace("]", ")").Trim();

        if (collapsed.Length == 0) return "(unnamed)";

        return collapsed.Length <= 60
            ? collapsed
            : string.Concat(collapsed.AsSpan(0, 57), "…");
    }

    [GeneratedRegex(@"\[([0-9a-fA-F]{8}-(?:[0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12})\]\s*$")]
    private static partial Regex TrailingGuid();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    /// <summary>Round-trips a timestamp through a text field without losing the offset.</summary>
    public static string FormatTimestamp(DateTimeOffset value) =>
        value.ToString("O", CultureInfo.InvariantCulture);

    public static DateTimeOffset? ParseTimestamp(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;
}
