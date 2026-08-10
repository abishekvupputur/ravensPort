using System.Text.Json;

namespace RavensPort.Core.Models;

/// <summary>
/// The four fields the app needs out of a Google service account key file, plus one place that
/// decides whether a pasted blob is one at all.
///
/// Parsing is shared by the editor and by the code that mints tokens, on purpose. The editor's
/// job is to say "this is the wrong file" while the user still has the right one to hand — a
/// download that is an OAuth *client* secret, or an authorized-user file, or the console's
/// on-screen JSON rather than the downloaded key, all look like plausible JSON and would
/// otherwise be accepted and then fail hours later inside a token request with a message about a
/// missing private key.
/// </summary>
/// <param name="ClientEmail">The service account's own address; the JWT's issuer.</param>
/// <param name="PrivateKey">PKCS#8 PEM. The signing key, and the reason the file is a secret.</param>
/// <param name="PrivateKeyId">Which key of the account signed, so Google can rotate. Optional.</param>
/// <param name="TokenUri">Where the signed JWT is exchanged, as named by the file itself.</param>
public sealed record GoogleServiceAccountKey(
    string ClientEmail,
    string PrivateKey,
    string? PrivateKeyId,
    string TokenUri)
{
    /// <summary>
    /// Google's token endpoint, used only when a key file omits <c>token_uri</c>. Every file the
    /// console produces has one; this keeps a hand-trimmed file working rather than failing with
    /// a null URL deep inside the client library.
    /// </summary>
    public const string DefaultTokenUri = "https://oauth2.googleapis.com/token";

    /// <summary>The <c>type</c> a service account key file declares. Any other value is a different file.</summary>
    private const string ServiceAccountType = "service_account";

    /// <summary>
    /// Parses a key file. Returns null and an explanation on failure; never throws for bad input.
    /// </summary>
    public static GoogleServiceAccountKey? TryParse(string? json, out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(json))
        {
            error = "Service account key is required. Paste the whole JSON key file downloaded from Google Cloud Console.";
            return null;
        }

        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(json);
            root = document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            error = $"That is not valid JSON ({ex.Message}). Paste the whole key file, including the outer braces.";
            return null;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            error = "The key file must be a JSON object. Paste the whole file downloaded from Google Cloud Console.";
            return null;
        }

        var type = ReadString(root, "type");
        if (type is not null && type != ServiceAccountType)
        {
            error = $"That key file is of type '{type}', not '{ServiceAccountType}'. "
                    + "Create a key on a service account (IAM & Admin → Service Accounts → Keys → Add key → JSON).";
            return null;
        }

        var clientEmail = ReadString(root, "client_email");
        var privateKey = ReadString(root, "private_key");

        if (clientEmail is null || privateKey is null)
        {
            // The likeliest wrong file by far is the OAuth *client* secret JSON, which is what
            // the Google credential form above already asks for and so is the one already open
            // in the user's downloads folder.
            error = root.TryGetProperty("installed", out _) || root.TryGetProperty("web", out _)
                ? "That is an OAuth client secret file, not a service account key. "
                  + "For a client secret file, use the OAuth2 credential type instead."
                : "The key file is missing 'client_email' or 'private_key'. Paste the whole downloaded file, unedited.";
            return null;
        }

        return new GoogleServiceAccountKey(
            clientEmail,
            privateKey,
            ReadString(root, "private_key_id"),
            ReadString(root, "token_uri") ?? DefaultTokenUri);
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
        && value.GetString() is { Length: > 0 } text
            ? text
            : null;
}
