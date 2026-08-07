using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using RavensPort.Core.Diagnostics;
using RavensPort.Core.Models;
using RavensPort.Core.Net;

namespace RavensPort.Core.Auth;

/// <summary>
/// Google-specific OAuth flow using Google's own official client library
/// (GoogleWebAuthorizationBroker) instead of the generic IdentityModel.OidcClient path —
/// gets Google's own loopback handling, PKCE toggle, and refresh support "for free" and
/// keeps up with any Google-side auth quirks automatically. Every other provider
/// (Nextcloud, Custom) still goes through the generic OAuth2Service/OidcClient path.
/// </summary>
public sealed class GoogleOAuthService(ActivityLog activityLog)
{
    public const string GoogleAuthority = "https://accounts.google.com";

    // Fixed port so the redirect URI is stable and displayable/registerable in Google Cloud
    // Console, instead of a fresh random port every attempt.
    private const int RedirectPort = 51004;
    public static readonly string RedirectUri = new FixedPortGoogleCodeReceiver(RedirectPort).RedirectUri;

    public async Task<AuthorizationOutcome> StartAuthorizationAsync(CredentialRecord credential, CancellationToken ct = default)
    {
        var initializer = new GoogleAuthorizationCodeFlow.Initializer
        {
            ClientSecrets = new ClientSecrets { ClientId = credential.ClientId, ClientSecret = credential.ClientSecret },
            DataStore = new NoOpDataStore(),
            // Connects over whichever IP family answers, and gives up in 30s rather than 100.
            // See RavensPortGoogleHttpClientFactory.
            HttpClientFactory = RavensPortGoogleHttpClientFactory.Instance,
            // Google only issues a refresh_token on a user's first consent for this
            // client+scope combination — every later authorization is access-token-only
            // unless the consent screen is forced to show again. Without this, a credential
            // that's been (re)connected more than once ends up with no refresh_token at all,
            // and then silently can't auto-refresh: RefreshAsync just returns null with
            // nothing to log, since there's no exception, just nothing to refresh with.
            Prompt = "consent",
        };

        try
        {
            var userCredential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
                initializer,
                credential.Scopes,
                "user",
                credential.UsesPkce,
                ct,
                new NoOpDataStore(),
                new FixedPortGoogleCodeReceiver(RedirectPort));

            ApplyToken(credential, userCredential.Token);
            return new AuthorizationOutcome(true, null, null);
        }
        catch (Exception ex)
        {
            return new AuthorizationOutcome(false, "google_auth_error", ex.Message);
        }
    }

    public async Task<TokenSet?> RefreshAsync(CredentialRecord credential, CancellationToken ct = default)
    {
        if (credential.Token?.RefreshToken is not { } refreshToken)
        {
            activityLog.Log($"REFRESH '{credential.Name}' has no refresh token stored — reconnect required. "
                           + "(This happens if it was connected before the fix that forces Google's consent screen on every authorization.)");
            return null;
        }

        var initializer = new GoogleAuthorizationCodeFlow.Initializer
        {
            ClientSecrets = new ClientSecrets { ClientId = credential.ClientId, ClientSecret = credential.ClientSecret },
            DataStore = new NoOpDataStore(),
            // Connects over whichever IP family answers, and gives up in 30s rather than 100.
            // See RavensPortGoogleHttpClientFactory.
            HttpClientFactory = RavensPortGoogleHttpClientFactory.Instance,
        };

        using var flow = new GoogleAuthorizationCodeFlow(initializer);

        try
        {
            var tokenResponse = await flow.RefreshTokenAsync("user", refreshToken, ct);
            ApplyToken(credential, tokenResponse);
            return credential.Token;
        }
        catch (Exception ex)
        {
            // This used to be a bare catch that discarded the real reason — every failed
            // refresh (both the manual button and the background auto-refresh loop, which
            // goes through this exact method) showed only "reconnect may be required" with
            // no way to find out why. Now the actual exception — invalid_grant, revoked
            // access, network failure, whatever it is — lands in the error log.
            activityLog.LogError($"Google token refresh failed for '{credential.Name}'", ex);
            credential.NeedsReconnect = true;
            return null;
        }
    }

    private static void ApplyToken(CredentialRecord credential, TokenResponse token)
    {
        var expiresAtUtc = token.ExpiresInSeconds.HasValue
            ? DateTimeOffset.UtcNow.AddSeconds(token.ExpiresInSeconds.Value)
            : DateTimeOffset.UtcNow.AddHours(1);

        // Google often omits refresh_token on subsequent refreshes — keep the old one.
        var refreshToken = string.IsNullOrEmpty(token.RefreshToken) ? credential.Token?.RefreshToken : token.RefreshToken;

        credential.Token = new TokenSet(token.AccessToken, refreshToken, expiresAtUtc, "Bearer", DateTimeOffset.UtcNow);
        credential.NeedsReconnect = false;
    }
}
