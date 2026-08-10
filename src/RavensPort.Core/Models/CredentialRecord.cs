using System.Text.Json.Serialization;

namespace RavensPort.Core.Models;

public sealed class CredentialRecord
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Name { get; set; }

    /// <summary>
    /// Which sort of secret this is. Everything below splits along this line: the browser-flow
    /// fields are meaningless for an API key, <see cref="ApiKey"/> is meaningless for a grant,
    /// and <see cref="ServiceAccountJson"/> belongs to exactly one kind.
    /// </summary>
    public CredentialKind Kind { get; set; } = CredentialKind.OAuth2;

    // Not `required` any more: an API-key credential has no OAuth client at all, and forcing
    // empty strings through a required initializer only obscured which fields actually matter.
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public List<string> Scopes { get; set; } = [];

    // Provider config, resolved from an OAuthProviderPreset at creation time and then
    // owned by the credential — presets are just prefill templates, not a persisted table.
    // IsGoogleProvider is set once from which preset was picked and never re-derived —
    // deciding "is this Google" from the editable Authority text field is fragile (a stray
    // edit, trailing slash, or blank would silently misroute the flow).
    public bool IsGoogleProvider { get; set; }
    public string? Authority { get; set; }
    public string? AuthorizationEndpoint { get; set; }
    public string? TokenEndpoint { get; set; }

    /// <summary>
    /// Where a <see cref="CredentialKind.DeviceCode"/> grant asks for its user code (RFC 8628
    /// §3.1). Separate from <see cref="AuthorizationEndpoint"/>, which is the page a browser is
    /// sent to — this one is called by the app and answers JSON, and providers publish them at
    /// different addresses.
    /// </summary>
    public string? DeviceAuthorizationEndpoint { get; set; }
    public bool RequiresIdToken { get; set; }
    public bool UsesPkce { get; set; } = true;

    /// <summary>
    /// Extra "a=1&amp;b=2" parameters for the provider. For an interactive grant these ride on the
    /// front-channel authorization request; for <see cref="CredentialKind.ClientCredentials"/>
    /// there is no front channel, so they are added to the token request instead — which is where
    /// the <c>audience</c> or <c>resource</c> that Auth0 and Entra ID insist on has to go.
    /// </summary>
    public string? ExtraAuthParams { get; set; }

    /// <summary>
    /// Whether the client credentials go in the POST body rather than an HTTP Basic header.
    ///
    /// RFC 6749 has clients support Basic and servers optionally accept the body, and real
    /// providers land on both sides of that: sending the wrong one gets a bare
    /// <c>invalid_client</c> with nothing to say which half of the pair was at fault. It is one
    /// checkbox rather than a debugging session.
    /// </summary>
    public bool SendClientCredentialsInBody { get; set; }

    public TokenSet? Token { get; set; }

    /// <summary>
    /// The secret itself, for <see cref="CredentialKind.ApiKey"/>. Encrypted at rest with
    /// everything else in the store, and never redisplayed once saved — the editor treats a
    /// blank box as "keep the current key", exactly as it does for a client secret.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// The whole downloaded service account key file, for
    /// <see cref="CredentialKind.GoogleServiceAccount"/>.
    ///
    /// Stored verbatim rather than split into email/key/token-uri fields: it is what Google hands
    /// out, it is what the user has on disk, and re-deriving the file from parsed pieces would be
    /// a second representation to keep honest. It contains a private key, so it is a secret in
    /// every sense — held back from the topology note, written to its own vault item, and never
    /// redisplayed after saving.
    /// </summary>
    public string? ServiceAccountJson { get; set; }

    /// <summary>
    /// Optional user to impersonate via domain-wide delegation — the "subject" of the signed JWT.
    ///
    /// Without it the token belongs to the service account itself, which is right for most Google
    /// Cloud APIs; Workspace APIs (Gmail, Calendar, Drive) mostly act on a person's data and need
    /// the account to borrow that person's identity. Not a secret: it is an email address.
    /// </summary>
    public string? ServiceAccountSubject { get; set; }

    // ---- Default placement ------------------------------------------------------------------
    //
    // Where this credential's secret normally goes. Two uses: it is what the "Test" button below
    // sends, and it prefills a route's credential entry so an "X-Api-Key" credential does not
    // have to be re-described on every route that uses it. The route still owns the placement it
    // actually forwards with — this is a default, not a constraint.

    public CredentialPlacement DefaultPlacement { get; set; } = CredentialPlacement.Header;
    public string DefaultParameterName { get; set; } = CredentialInjection.BearerHeader.Name;
    public string DefaultValuePrefix { get; set; } = CredentialInjection.BearerHeader.ValuePrefix;

    /// <summary>
    /// Optional URL that answers 200 to an authenticated GET, used to check the credential
    /// actually works.
    ///
    /// For an API key there is otherwise no way to tell a good key from a typo: unlike an OAuth
    /// flow, nothing validates it at the moment it is entered, so the first evidence of a wrong
    /// key is a 401 from a real request hours later.
    /// </summary>
    public string? TestEndpoint { get; set; }

    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>True once a refresh attempt has failed and the user needs to reconnect. Not persisted.</summary>
    [JsonIgnore]
    public bool NeedsReconnect { get; set; }

    /// <summary>Where this credential's secret goes by default.</summary>
    public CredentialInjection ToDefaultInjection() =>
        new(DefaultPlacement, DefaultParameterName, DefaultValuePrefix);

    /// <summary>
    /// True when the credential currently holds something usable — a stored API key, a service
    /// account key, a client secret to mint with, or an OAuth token. Says nothing about whether
    /// the upstream will accept it; that is what <see cref="TestEndpoint"/> is for.
    /// </summary>
    [JsonIgnore]
    public bool HasSecret => Kind switch
    {
        CredentialKind.ApiKey => !string.IsNullOrEmpty(ApiKey),
        // For the two app logins the stored secret, not the token, is the thing that lasts: the
        // token is a derivative the app can re-mint at any moment, so a credential with a key and
        // no token yet is configured, not empty.
        CredentialKind.GoogleServiceAccount => !string.IsNullOrWhiteSpace(ServiceAccountJson),
        CredentialKind.ClientCredentials => !string.IsNullOrEmpty(ClientSecret),
        _ => Token is not null,
    };

    /// <summary>
    /// True for the kinds that can obtain a token entirely on their own — no browser, no user,
    /// no refresh token. The distinction matters in three places: such a credential needs no
    /// "Connect" click before a route can use it, it is refreshed by re-minting rather than by
    /// presenting a refresh token, and a null token on one means "not fetched yet" rather than
    /// "not authorized".
    /// </summary>
    [JsonIgnore]
    public bool IsSelfIssuing =>
        Kind is CredentialKind.ClientCredentials or CredentialKind.GoogleServiceAccount;

    /// <summary>
    /// True for the kinds that need a person: a real grant belonging to a human, obtained by them
    /// approving it and renewed afterwards with a refresh token. The two differ only in how the
    /// approval gets back here — a redirect to this machine, or a code typed on any device.
    /// </summary>
    [JsonIgnore]
    public bool IsInteractiveOAuth => Kind is CredentialKind.OAuth2 or CredentialKind.DeviceCode;

    /// <summary>The placement defaults a kind starts with, offered by the editor.</summary>
    public static CredentialInjection DefaultInjectionFor(CredentialKind kind) => kind == CredentialKind.ApiKey
        // Bearer is an OAuth convention; an API key almost always wants a bare value in a
        // bespoke header, which is what nearly every key-based API documents. Both app-login
        // kinds produce ordinary OAuth bearer tokens, so they keep the OAuth default.
        ? new CredentialInjection(CredentialPlacement.Header, "X-Api-Key", "")
        : CredentialInjection.BearerHeader;
}
