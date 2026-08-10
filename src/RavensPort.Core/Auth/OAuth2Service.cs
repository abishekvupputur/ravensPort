using System.Collections.Concurrent;
using IdentityModel.Client;
using IdentityModel.Jwk;
using IdentityModel.OidcClient;
using RavensPort.Core.Diagnostics;
using RavensPort.Core.Models;

namespace RavensPort.Core.Auth;

public sealed record AuthorizationOutcome(bool Success, string? Error, string? ErrorDescription);

/// <summary>
/// Single entry point ViewModels/TokenRefreshService call regardless of provider or kind.
///
/// Four paths meet here. The two app logins — client credentials and Google service accounts —
/// need no browser and no user, so both "authorize" and "refresh" mean the same thing for them:
/// mint a fresh token from the stored secret. Of the interactive ones, Google credentials
/// delegate to GoogleOAuthService (Google's own official client library) and every other provider
/// (GitHub, Nextcloud, Custom) goes through the generic OidcClient path here, branching only on
/// whether the credential has an Authority (OIDC discovery) or manual
/// AuthorizationEndpoint/TokenEndpoint. A device code credential is interactive too but never
/// touches a redirect URI, so it has its own path both ways.
/// </summary>
public sealed class OAuth2Service(
    GoogleOAuthService googleOAuthService,
    GoogleServiceAccountService googleServiceAccountService,
    ClientCredentialsService clientCredentialsService,
    DeviceCodeService deviceCodeService,
    ActivityLog activityLog)
{
    // Guards against the background refresh loop and a manual "Refresh Now" UI action
    // racing a refresh for the same credential.
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _refreshLocks = new();

    private static bool IsGoogle(CredentialRecord credential) => credential.IsGoogleProvider;

    /// <summary>
    /// Obtains a token for a credential that needs neither browser nor user. Null for the kinds
    /// that do, so callers can tell "not handled here" from "handled and failed".
    /// </summary>
    private Task<AuthorizationOutcome>? AcquireSelfIssuedAsync(CredentialRecord credential, CancellationToken ct) =>
        credential.Kind switch
        {
            CredentialKind.GoogleServiceAccount => googleServiceAccountService.AcquireAsync(credential, ct),
            CredentialKind.ClientCredentials => clientCredentialsService.AcquireAsync(credential, ct),
            _ => null,
        };

    /// <param name="devicePrompt">
    /// Where to report the code a device flow needs the user to enter. Ignored by every other
    /// kind. Optional so that callers with nowhere to show it — the refresh loop, a test — do not
    /// have to pretend to have a UI.
    /// </param>
    public async Task<AuthorizationOutcome> StartAuthorizationAsync(
        CredentialRecord credential,
        IProgress<DeviceCodePrompt>? devicePrompt = null,
        CancellationToken ct = default)
    {
        // "Connect" on an app login opens nothing; it fetches a token now so a mistyped secret or
        // an ungranted delegation is reported while the user is still looking at the form,
        // instead of as a 401 on the first proxied request.
        if (AcquireSelfIssuedAsync(credential, ct) is { } acquire)
        {
            return await acquire;
        }

        // Checked before the Google branch: a Google device credential is still a device
        // credential, and its provider being Google says nothing about which flow it uses.
        if (credential.Kind == CredentialKind.DeviceCode)
        {
            return await deviceCodeService.AuthorizeAsync(credential, devicePrompt, ct);
        }

        if (IsGoogle(credential))
        {
            return await googleOAuthService.StartAuthorizationAsync(credential, ct);
        }

        // The browser owns its HttpListener per-invocation and always releases it, so there
        // is nothing to dispose here.
        var browser = new LoopbackBrowser();
        var options = BuildOptions(credential, browser);

        var request = new LoginRequest();
        if (!string.IsNullOrWhiteSpace(credential.ExtraAuthParams))
        {
            request.FrontChannelExtraParameters ??= new Parameters();
            foreach (var pair in ExtraParameters.Parse(credential.ExtraAuthParams))
            {
                request.FrontChannelExtraParameters.Add(pair.Key, pair.Value);
            }
        }

        var client = new OidcClient(options);
        var result = await client.LoginAsync(request, ct);

        if (result.IsError)
        {
            return new AuthorizationOutcome(false, result.Error, result.ErrorDescription);
        }

        credential.Token = new TokenSet(
            result.AccessToken,
            result.RefreshToken,
            // Read from the raw expires_in rather than from AccessTokenExpiration, which is
            // computed as "now + expires_in" and so reports a token with no advertised lifetime —
            // a GitHub OAuth App token, for one — as expiring the instant it was issued.
            ExpiryFrom(result.TokenResponse?.ExpiresIn ?? 0),
            "Bearer",
            DateTimeOffset.UtcNow);
        credential.NeedsReconnect = false;

        return new AuthorizationOutcome(true, null, null);
    }

    /// <summary>
    /// Turns an <c>expires_in</c> into an absolute expiry, or null when the provider sent none.
    /// Zero is the library's stand-in for an absent value, and it is not a real lifetime.
    /// </summary>
    private static DateTimeOffset? ExpiryFrom(int expiresInSeconds) =>
        expiresInSeconds > 0 ? DateTimeOffset.UtcNow.AddSeconds(expiresInSeconds) : null;

    public async Task<TokenSet?> RefreshAsync(CredentialRecord credential, CancellationToken ct = default)
    {
        var refreshLock = _refreshLocks.GetOrAdd(credential.Id, _ => new SemaphoreSlim(1, 1));
        await refreshLock.WaitAsync(ct);
        try
        {
            // Checked before the refresh-token guard: an app login has no refresh token by
            // design, and requiring one would leave both new kinds permanently unrefreshable.
            if (AcquireSelfIssuedAsync(credential, ct) is { } acquire)
            {
                var outcome = await acquire;
                return outcome.Success ? credential.Token : null;
            }

            if (credential.Token?.RefreshToken is null)
            {
                return null;
            }

            // An ordinary refresh_token exchange, but on its own path: OidcClient wants an
            // Authority or a full ProviderInformation, and a device credential legitimately has
            // only a token endpoint.
            if (credential.Kind == CredentialKind.DeviceCode)
            {
                return await deviceCodeService.RefreshAsync(credential, ct);
            }

            if (IsGoogle(credential))
            {
                return await googleOAuthService.RefreshAsync(credential, ct);
            }

            var refreshToken = credential.Token.RefreshToken;
            var options = BuildOptions(credential, browser: null);
            var client = new OidcClient(options);
            var result = await client.RefreshTokenAsync(refreshToken, null, null, ct);

            if (result.IsError)
            {
                // Same fix as GoogleOAuthService: the provider's actual error/description was
                // being discarded here, leaving only "reconnect may be required" in the UI
                // with no way to find out why (expired refresh token, revoked grant, endpoint
                // unreachable, etc).
                activityLog.Log($"REFRESH '{credential.Name}' provider error: {result.Error} {result.ErrorDescription}".Trim());
                credential.NeedsReconnect = true;
                return null;
            }

            // Most providers omit refresh_token on subsequent refreshes — keep the old one.
            var newRefreshToken = string.IsNullOrEmpty(result.RefreshToken) ? refreshToken : result.RefreshToken;

            var newToken = new TokenSet(
                result.AccessToken,
                newRefreshToken,
                ExpiryFrom(result.ExpiresIn),
                "Bearer",
                DateTimeOffset.UtcNow);

            credential.Token = newToken;
            credential.NeedsReconnect = false;
            return newToken;
        }
        finally
        {
            refreshLock.Release();
        }
    }

    private static OidcClientOptions BuildOptions(CredentialRecord credential, LoopbackBrowser? browser)
    {
        var options = new OidcClientOptions
        {
            ClientId = credential.ClientId,
            ClientSecret = credential.ClientSecret,
            Scope = string.Join(' ', credential.Scopes),
            Policy = new Policy(),
            // Defaults to true, which makes OidcClient call the userinfo endpoint after the
            // token exchange and throw "No userinfo endpoint specified" for plain-OAuth2
            // providers like Nextcloud that have none. We only ever want the access token —
            // the profile claims are never read — so skip that call entirely.
            LoadProfile = false,

            // Asks the token endpoint for JSON. GitHub answers form-encoded without it, which
            // arrives here as an unparseable token response — see JsonAcceptHandler.
            BackchannelHandler = new JsonAcceptHandler(new HttpClientHandler()),
        };

        if (browser is not null)
        {
            options.Browser = browser;
            options.RedirectUri = browser.RedirectUri;
        }

        if (!string.IsNullOrWhiteSpace(credential.Authority))
        {
            options.Authority = credential.Authority;
        }
        else
        {
            options.ProviderInformation = new ProviderInformation
            {
                IssuerName = credential.AuthorizationEndpoint ?? credential.TokenEndpoint!,
                KeySet = new JsonWebKeySet(),
                AuthorizeEndpoint = credential.AuthorizationEndpoint,
                TokenEndpoint = credential.TokenEndpoint,
            };
        }

        // CredentialRecord.UsesPkce is deliberately not consulted here. IdentityModel.OidcClient 6
        // always sends a code_challenge and exposes no switch to turn that off, so this path is
        // unconditionally PKCE-protected. Only the Google flow honours the flag, and the UI now
        // shows the checkbox only for Google rather than implying a setting that does nothing.
        if (!credential.RequiresIdToken)
        {
            // Plain-OAuth2 providers (e.g. Nextcloud) don't return an id_token at all —
            // skip identity-token validation entirely rather than requiring one.
            options.IdentityTokenValidator = new NoValidationIdentityTokenValidator();
        }

        return options;
    }
}
