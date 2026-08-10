using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Responses;
using RavensPort.Core.Diagnostics;
using RavensPort.Core.Models;

namespace RavensPort.Core.Auth;

/// <summary>
/// Mints access tokens from a Google service account key file.
///
/// There is no user and no browser here. The private key in the key file signs a JWT asserting
/// "this service account, for these scopes"; Google's token endpoint exchanges that assertion for
/// an access token (RFC 7523). Since the key can produce another token at any moment, there is no
/// refresh token and nothing to reconnect — an expired token is simply re-minted.
/// </summary>
public sealed class GoogleServiceAccountService(ActivityLog activityLog)
{
    public async Task<AuthorizationOutcome> AcquireAsync(CredentialRecord credential, CancellationToken ct = default)
    {
        var key = GoogleServiceAccountKey.TryParse(credential.ServiceAccountJson, out var parseError);
        if (key is null)
        {
            credential.NeedsReconnect = true;
            return new AuthorizationOutcome(false, "invalid_key_file", parseError);
        }

        if (credential.Scopes.Count == 0)
        {
            // Google would issue a token for a scopeless assertion and every API would then
            // reject it, which looks like a permissions problem rather than a config one.
            credential.NeedsReconnect = true;
            return new AuthorizationOutcome(false, "missing_scopes",
                "No scopes are set. A service account token without scopes is accepted by Google and refused by every API.");
        }

        var subject = string.IsNullOrWhiteSpace(credential.ServiceAccountSubject)
            ? null
            : credential.ServiceAccountSubject.Trim();

        try
        {
            var initializer = new ServiceAccountCredential.Initializer(key.ClientEmail, key.TokenUri)
            {
                Scopes = credential.Scopes,
                KeyId = key.PrivateKeyId,

                // Domain-wide delegation. Null leaves the token belonging to the service account
                // itself, which is what Google Cloud APIs want; a Workspace API mostly wants the
                // account to act as a person, and that person is named here.
                User = subject,

                // Left off deliberately. With it on, Google's library sees a scoped service
                // account and signs a JWT it hands straight to the caller without ever calling
                // the token endpoint — that token is accepted by Google's own APIs but carries no
                // expiry this app can read, and domain-wide delegation does not work through it.
                // Forcing the exchange keeps one code path with a real expires_in on it.
                UseJwtAccessWithScopes = false,
            }.FromPrivateKey(key.PrivateKey);

            var serviceAccount = new ServiceAccountCredential(initializer);

            var accessToken = await serviceAccount.GetAccessTokenForRequestAsync(cancellationToken: ct);
            if (string.IsNullOrEmpty(accessToken))
            {
                credential.NeedsReconnect = true;
                return new AuthorizationOutcome(false, "no_token",
                    "Google accepted the signed assertion but returned no access token.");
            }

            credential.Token = new TokenSet(
                accessToken,
                // No refresh token exists for this grant, and none is wanted: the key file is the
                // renewable thing, so the app re-signs rather than presenting anything.
                RefreshToken: null,
                ExpiryOf(serviceAccount.Token),
                "Bearer",
                DateTimeOffset.UtcNow);
            credential.NeedsReconnect = false;

            activityLog.Log($"TOKEN '{credential.Name}' minted for service account {key.ClientEmail}"
                            + (subject is null ? "" : $" acting as {subject}")
                            + $" — {credential.Token.DescribeExpiry()}");

            return new AuthorizationOutcome(true, null, null);
        }
        catch (TokenResponseException ex)
        {
            // Google's own refusal, which names the problem far better than the exception text:
            // 'unauthorized_client' for delegation that was never granted in the Admin console,
            // 'invalid_grant' for a key that has been deleted, and so on.
            credential.NeedsReconnect = true;
            activityLog.Log($"TOKEN '{credential.Name}' refused by Google: {ex.Error?.Error} {ex.Error?.ErrorDescription}".Trim());
            return new AuthorizationOutcome(false, ex.Error?.Error ?? "token_error", ex.Error?.ErrorDescription ?? ex.Message);
        }
        catch (Exception ex)
        {
            // A malformed private key throws out of FromPrivateKey rather than returning, and the
            // network can fail like anything else. Neither may take down an always-on tray app.
            credential.NeedsReconnect = true;
            activityLog.LogError($"Service account token request failed for '{credential.Name}'", ex);
            return new AuthorizationOutcome(false, "service_account_error", ex.Message);
        }
    }

    /// <summary>
    /// When the minted token runs out, as Google reported it. Null when the response carried no
    /// lifetime at all, which the app reads as "does not expire" rather than "expired now".
    /// </summary>
    private static DateTimeOffset? ExpiryOf(TokenResponse? token)
    {
        if (token?.ExpiresInSeconds is not { } seconds) return null;

        // IssuedUtc is stamped by the library's clock when the response arrives. A default value
        // would put the expiry in the year 1 and make a fresh token look long dead.
        var issued = token.IssuedUtc == default
            ? DateTimeOffset.UtcNow
            : new DateTimeOffset(DateTime.SpecifyKind(token.IssuedUtc, DateTimeKind.Utc));

        return issued.AddSeconds(seconds);
    }
}
