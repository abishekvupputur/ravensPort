namespace RavensPort.Core.Vault;

/// <summary>
/// The kinds of item this app writes. Backend-neutral: each provider maps these onto whatever its
/// CLI calls them, which is not the same vocabulary on both.
/// </summary>
public enum VaultItemCategory
{
    /// <summary>Free text. Holds the topology document.</summary>
    SecureNote,

    /// <summary>Username + password + URL. Holds a credential.</summary>
    Login,

    /// <summary>A bare secret. Holds a route or funnel proxy key.</summary>
    Password,
}

/// <summary>
/// Semantic field names shared by the mapper and both providers.
///
/// <see cref="Username"/>, <see cref="Password"/>, <see cref="Website"/> and
/// <see cref="NoteContent"/> are the manager's own built-in slots — putting the client id and
/// secret there is what makes the item look like a real login in the manager's UI rather than an
/// opaque blob. Everything else becomes a custom field.
/// </summary>
public static class VaultFields
{
    public const string Username = "username";
    public const string Password = "password";
    public const string Website = "website";
    public const string NoteContent = "notesPlain";

    public const string ApiKey = "api_key";
    public const string AccessToken = "access_token";
    public const string RefreshToken = "refresh_token";
    public const string RecordId = "record_id";
    public const string Kind = "kind";
    public const string TokenType = "token_type";
    public const string ExpiresAtUtc = "expires_at_utc";
    public const string ObtainedUtc = "obtained_utc";

    /// <summary>
    /// The whole Google service account key file. It contains a private key, so it belongs to the
    /// item rather than the note. The impersonated subject deliberately does not live here: it is
    /// an email address, not a secret, and the note is its one home.
    /// </summary>
    public const string ServiceAccountJson = "service_account_json";

    /// <summary>Fields that must never be readable at a glance in the manager's UI.</summary>
    public static bool IsConcealed(string name) =>
        name is Password or ApiKey or AccessToken or RefreshToken or ServiceAccountJson;

    /// <summary>Fields that map to a built-in slot rather than a custom field.</summary>
    public static bool IsBuiltIn(string name) => name is Username or Password or Website or NoteContent;
}

/// <summary>One field of an item, as the mapper describes it and a provider writes it.</summary>
public sealed record VaultItemField(string Name, string Value)
{
    public bool Concealed => VaultFields.IsConcealed(Name);
}

/// <summary>
/// An item the mapper wants to exist. <see cref="ItemId"/> is null for one that does not yet —
/// the provider creates it and reports the id back so the index can record it.
/// </summary>
public sealed record VaultItemSpec(
    string Title,
    VaultItemCategory Category,
    IReadOnlyList<VaultItemField> Fields)
{
    /// <summary>Backend id, once known. Null means "create this".</summary>
    public string? ItemId { get; init; }

    /// <summary>
    /// Human-readable line written into the item's notes for someone browsing the vault. Written
    /// every save and never read back — it is a caption, not data.
    /// </summary>
    public string? Caption { get; init; }

    public string? Field(string name) => Fields.FirstOrDefault(f => f.Name == name)?.Value;
}

/// <summary>What a list returns: enough to match an item to a record without fetching secrets.</summary>
public sealed record VaultItemSummary(string ItemId, string Title);

/// <summary>What a get returns.</summary>
public sealed record VaultItemContents(string ItemId, string Title, IReadOnlyDictionary<string, string> Fields)
{
    public string? Field(string name) => Fields.TryGetValue(name, out var value) ? value : null;
}
