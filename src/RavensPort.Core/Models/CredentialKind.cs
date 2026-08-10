using System.Text.Json.Serialization;

namespace RavensPort.Core.Models;

/// <summary>
/// What kind of secret a credential holds, and therefore how the proxy obtains a usable value
/// for it on each request.
///
/// The default is <see cref="OAuth2"/> so a store written before this existed deserializes into
/// exactly what every credential in it already was. Values are persisted by name, not by
/// ordinal, so new members can be appended without disturbing an existing vault.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CredentialKind
{
    /// <summary>An OAuth2 grant: a token obtained by a browser flow and refreshed in the background.</summary>
    OAuth2,

    /// <summary>
    /// A static API key typed in by the user. No authorization flow, no expiry, nothing to
    /// refresh — plenty of APIs never offered OAuth at all, and routing to them previously
    /// meant either leaving the route unauthenticated or inventing a fake OAuth credential.
    /// </summary>
    ApiKey,

    /// <summary>
    /// The OAuth2 client_credentials grant (RFC 6749 §4.4): the app itself is the principal, so
    /// there is no user, no browser, and no refresh token. A token is minted straight from the
    /// client id and secret and re-minted when it ages out.
    /// </summary>
    ClientCredentials,

    /// <summary>
    /// A Google service account key file. The private key in it signs a JWT that Google's token
    /// endpoint exchanges for an access token (RFC 7523) — again no user and no browser, and
    /// again nothing to refresh: the key mints a new token whenever one is needed.
    /// </summary>
    GoogleServiceAccount,

    /// <summary>
    /// The device authorization grant (RFC 8628): the provider issues a short code, the user
    /// enters it on any device they like, and this app polls until they have. Still a user login
    /// and still yields a refresh token — the difference is that nothing has to come back to a
    /// redirect URI on this machine, which is what makes it work for a provider that refuses to
    /// register a loopback callback.
    /// </summary>
    DeviceCode,
}

/// <summary>
/// How each kind is named and explained wherever the user picks one. Lives here rather than in
/// the view so the editor, the credential list, and the activity log all say the same words —
/// and so the raw enum name ("GoogleServiceAccount") never reaches the screen.
/// </summary>
public sealed record CredentialKindInfo(CredentialKind Kind, string Label, string Blurb)
{
    public static readonly IReadOnlyList<CredentialKindInfo> All =
    [
        new(CredentialKind.OAuth2, "OAuth2 (user login)",
            "A grant you authorize in your browser, refreshed automatically before it expires."),
        new(CredentialKind.DeviceCode, "OAuth2 device code",
            "The provider shows a short code; you enter it on any device. No redirect URI to register."),
        new(CredentialKind.ApiKey, "API key",
            "A static key you paste in. No authorization flow, no expiry, nothing to refresh."),
        new(CredentialKind.ClientCredentials, "OAuth2 client credentials (app login)",
            "The app signs in as itself with its client id and secret. No browser, no user consent."),
        new(CredentialKind.GoogleServiceAccount, "Google service account",
            "A Google service account key file signs for its own access tokens. No browser, no user consent."),
    ];

    public static CredentialKindInfo For(CredentialKind kind) =>
        All.First(info => info.Kind == kind);

    /// <summary>Short form for lists and log lines.</summary>
    public static string ShortLabel(CredentialKind kind) => kind switch
    {
        CredentialKind.ApiKey => "API key",
        CredentialKind.ClientCredentials => "Client creds",
        CredentialKind.GoogleServiceAccount => "Service acct",
        CredentialKind.DeviceCode => "Device code",
        _ => "OAuth2",
    };
}
