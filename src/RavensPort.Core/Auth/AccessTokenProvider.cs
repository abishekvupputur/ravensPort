using RavensPort.Core.Diagnostics;
using RavensPort.Core.Models;
using RavensPort.Core.Storage;

namespace RavensPort.Core.Auth;

/// <summary>
/// Single place that answers "give me a usable access token for this credential, right now".
///
/// The periodic <see cref="TokenRefreshService"/> alone was not enough: it ticks once a minute,
/// so a machine waking from sleep, or a token whose real lifetime is shorter than advertised,
/// left the proxy forwarding a stale token and the caller seeing a bare 401 with no recovery.
/// Refreshing on demand at the moment of use closes that window.
/// </summary>
public sealed class AccessTokenProvider(
    ConfigStoreCache configStoreCache,
    OAuth2Service oAuth2Service,
    ActivityLog activityLog)
{
    /// <summary>
    /// Refresh margin for the on-demand path. Deliberately small — the periodic loop handles
    /// the comfortable 10-minute-ahead case, and this only catches what slipped through.
    /// </summary>
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromSeconds(30);

    public async ValueTask<string?> GetAccessTokenAsync(Guid credentialId, CancellationToken ct = default)
    {
        var credential = configStoreCache.GetCredential(credentialId);
        if (credential is null) return null;

        // A static API key has no expiry, no refresh token, and no provider to ask — everything
        // below this line is about keeping an OAuth token alive and would do nothing but waste
        // work. Returned null when blank so the caller reports "not connected" rather than
        // attaching an empty header the upstream would reject with a confusing 401.
        if (credential.Kind == CredentialKind.ApiKey)
        {
            return string.IsNullOrEmpty(credential.ApiKey) ? null : credential.ApiKey;
        }

        if (credential.Token is not { } token)
        {
            // An app login has no "connect" step: the stored secret is enough to mint a token, so
            // the first proxied request through a freshly saved credential mints one here rather
            // than failing as unauthorized and waiting for someone to press a button.
            //
            // Not once a mint has already been refused, though. Nothing about a rejected client
            // secret changes between two requests a millisecond apart, and retrying per request
            // would turn one configuration mistake into a burst of failed token requests at
            // whatever rate the caller happens to be sending. The background loop keeps retrying
            // on a backoff, and that is the right place for it.
            return credential.IsSelfIssuing && !credential.NeedsReconnect
                ? await MintOnDemandAsync(credential, "not yet fetched", ct)
                : null;
        }

        if (!token.IsExpiringWithin(RefreshMargin))
        {
            return token.AccessToken;
        }

        // Nothing to refresh with, or a previous attempt already established the grant is
        // dead. Hand back what we have rather than hammering the provider on every request —
        // a 401 from upstream is the honest outcome at that point.
        //
        // An app login is exempt from the refresh-token half: it has none by design and renews
        // from its own stored secret instead. It is not exempt from NeedsReconnect, which for
        // these kinds means the last mint was refused and repeating it every request would only
        // add rate limiting to a configuration problem.
        if ((token.RefreshToken is null && !credential.IsSelfIssuing) || credential.NeedsReconnect)
        {
            return token.AccessToken;
        }

        return await RefreshOnDemandAsync(credential, ct);
    }

    /// <summary>
    /// First fetch for an app login. Separate from <see cref="RefreshOnDemandAsync"/> only in what
    /// it can fall back to: there is no previous token to hand over, so a failure here really is a
    /// failure and the caller must be told there is no credential to attach.
    /// </summary>
    private async ValueTask<string?> MintOnDemandAsync(CredentialRecord credential, string reason, CancellationToken ct)
    {
        try
        {
            activityLog.Log($"TOKEN '{credential.Name}' {reason} — obtaining one before forwarding");
            var minted = await oAuth2Service.RefreshAsync(credential, ct);
            if (minted is null) return null;

            // Persisted for the same reason a refresh is: the token outlives this request, and a
            // restart that lost it would mint again on the next one for no benefit.
            await configStoreCache.SaveAsync(ct);
            return minted.AccessToken;
        }
        catch (Exception ex)
        {
            activityLog.LogError($"On-demand token request for '{credential.Name}' threw", ex);
            return null;
        }
    }

    private async ValueTask<string?> RefreshOnDemandAsync(CredentialRecord credential, CancellationToken ct)
    {
        try
        {
            // OAuth2Service serializes refreshes per credential, so a burst of concurrent
            // proxied requests produces exactly one token exchange; the rest wait and then
            // observe the already-refreshed token below.
            activityLog.Log($"REFRESH '{credential.Name}' expired mid-use — refreshing before forwarding");
            var refreshed = await oAuth2Service.RefreshAsync(credential, ct);

            if (refreshed is null)
            {
                activityLog.Log($"REFRESH '{credential.Name}' FAILED on demand — reconnect required");
                return credential.Token?.AccessToken;
            }

            await configStoreCache.SaveAsync(ct);
            return refreshed.AccessToken;
        }
        catch (Exception ex)
        {
            // A proxied request must never be taken down by a refresh failure; forward the
            // stale token and let the upstream give its own verdict.
            activityLog.LogError($"On-demand refresh of '{credential.Name}' threw", ex);
            return credential.Token?.AccessToken;
        }
    }
}
