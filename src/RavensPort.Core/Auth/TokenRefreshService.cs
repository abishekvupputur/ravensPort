using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RavensPort.Core.Diagnostics;
using RavensPort.Core.Models;
using RavensPort.Core.Storage;

namespace RavensPort.Core.Auth;

/// <summary>
/// Scans all stored credentials on startup and every minute after, refreshing any token expiring
/// within 10 minutes. Runs for the lifetime of the host (tied to App.OnStartup/OnExit).
/// </summary>
public sealed class TokenRefreshService(
    ConfigStoreCache configStoreCache,
    OAuth2Service oAuth2Service,
    ActivityLog activityLog,
    ILogger<TokenRefreshService> logger) : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ExpiryWindow = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan InitialBackoff = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromHours(1);

    /// <summary>
    /// Consecutive failures per credential, used to space out retries. A grant that has been
    /// revoked matches the "expiring soon" filter forever, so without this the loop hit the
    /// provider every 60 seconds indefinitely — enough to get rate-limited or flagged, and
    /// enough to bury the log in identical errors.
    ///
    /// Concurrent because ResetBackoff is called from the WPF dispatcher thread while the loop
    /// reads and writes it on a thread-pool thread.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, (int Failures, DateTimeOffset NextAttemptUtc)> _backoff = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TickInterval);

        // Scan first, wait second. WaitForNextTickAsync sleeps a full interval before its first
        // tick, and this service does not start until a vault has been unlocked — so the tokens
        // that expired while the app was closed, which is most of the ones that ever expire, spent
        // that first minute on screen in red. The user is looking at the window right then, and a
        // failure the app is about to repair by itself reads as one it cannot.
        do
        {
            try
            {
                await RefreshDueCredentialsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // The whole tick is wrapped, not just the per-credential work. SaveAsync used
                // to sit outside the inner try, so a locked or full disk threw straight out of
                // ExecuteAsync — and BackgroundService's default StopHost behavior then tore
                // down Kestrel. The tray icon stayed put while every proxied request died, with
                // nothing on screen to say why.
                logger.LogError(ex, "Token refresh tick failed");
                activityLog.LogError("REFRESH tick failed — will retry next minute", ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>One pass of the loop. Internal so its vault gate can be tested without a 60s wait.</summary>
    internal async Task RefreshDueCredentialsAsync(CancellationToken ct)
    {
        // Refreshing proceeds whether or not the vault is reachable. Only the newest token is ever
        // needed, so a refresh that cannot be written yet still leaves the proxy working — the new
        // token is in memory and the sync queue persists it when the manager comes back. The cost
        // is bounded and understood: if the app exits with that write still pending, the grant is
        // gone and the credential has to be reconnected. Refusing to refresh would instead break
        // every OAuth route the moment its token aged out, which is the more common outcome.
        var now = DateTimeOffset.UtcNow;

        // Snapshot the list so edits to Credentials during iteration (e.g. a delete from
        // the UI) can't throw a collection-modified error.
        var dueCredentials = configStoreCache.Current.Credentials
            .Where(c => IsRenewable(c) && IsDue(c) && IsDueForAttempt(c.Id, now))
            .ToList();

        if (dueCredentials.Count == 0) return;

        var anyRefreshed = false;
        foreach (var credential in dueCredentials)
        {
            try
            {
                activityLog.Log(credential.Token is null
                    ? $"REFRESH '{credential.Name}' has no token yet — obtaining one"
                    : $"REFRESH '{credential.Name}' expiring soon — refreshing token");
                var refreshed = await oAuth2Service.RefreshAsync(credential, ct);

                if (refreshed is not null)
                {
                    anyRefreshed = true;
                    _backoff.TryRemove(credential.Id, out _);
                    activityLog.Log($"REFRESH '{credential.Name}' OK — {refreshed.DescribeExpiry()}");
                }
                else
                {
                    var retryAt = RecordFailure(credential.Id, now);

                    // An app login has nothing to reconnect: no browser flow exists for it, so
                    // the fix is in its stored secret or its configuration, and saying
                    // "reconnect" would send someone looking for a button that is not there.
                    var remedy = credential.IsSelfIssuing
                        ? "check its stored secret and settings"
                        : "reconnect required";

                    activityLog.Log($"REFRESH '{credential.Name}' FAILED — {remedy} "
                                    + $"(next automatic attempt {retryAt.ToLocalTime():g})");
                }
            }
            catch (Exception ex)
            {
                var retryAt = RecordFailure(credential.Id, now);
                logger.LogWarning(ex, "Failed to refresh token for credential {CredentialName}", credential.Name);
                activityLog.LogError($"REFRESH '{credential.Name}' threw (next attempt {retryAt.ToLocalTime():g})", ex);
                credential.NeedsReconnect = true;
            }
        }

        if (anyRefreshed)
        {
            await configStoreCache.SaveAsync(ct);
        }

        PruneBackoffForDeletedCredentials();
    }

    /// <summary>
    /// Whether this loop can renew the credential without a person present.
    ///
    /// For an interactive grant that means holding a refresh token. An app login holds none by
    /// design — it re-mints from its own client secret or service account key — so testing for a
    /// refresh token would have quietly excluded both new kinds from automatic renewal and left
    /// every one of them to expire in the middle of whatever was using it.
    /// </summary>
    private static bool IsRenewable(CredentialRecord credential) =>
        credential.IsSelfIssuing
            ? credential.HasSecret
            : credential.Token?.RefreshToken is not null;

    /// <summary>
    /// Whether there is anything to obtain right now.
    ///
    /// An app login with no token at all counts, which an interactive grant does not: it holds
    /// everything it needs, so "no token yet" is work this loop can finish, not a user action it
    /// is waiting on. That also gives a failing one a retry path — the backoff above spaces the
    /// attempts out, where the per-request path would otherwise be the only one trying and would
    /// try on every single request.
    /// </summary>
    private static bool IsDue(CredentialRecord credential) => credential.Token is { } token
        ? token.IsExpiringWithin(ExpiryWindow)
        : credential.IsSelfIssuing;

    private bool IsDueForAttempt(Guid credentialId, DateTimeOffset now) =>
        !_backoff.TryGetValue(credentialId, out var state) || now >= state.NextAttemptUtc;

    /// <summary>Doubles the wait after each consecutive failure, capped at an hour.</summary>
    private DateTimeOffset RecordFailure(Guid credentialId, DateTimeOffset now)
    {
        var failures = _backoff.TryGetValue(credentialId, out var state) ? state.Failures + 1 : 1;

        var delayTicks = Math.Min(
            InitialBackoff.Ticks * (long)Math.Pow(2, Math.Min(failures - 1, 10)),
            MaxBackoff.Ticks);

        var nextAttempt = now + TimeSpan.FromTicks(delayTicks);
        _backoff[credentialId] = (failures, nextAttempt);
        return nextAttempt;
    }

    /// <summary>Keeps the dictionary from growing across a long uptime of add/delete cycles.</summary>
    private void PruneBackoffForDeletedCredentials()
    {
        if (_backoff.Count == 0) return;

        var live = configStoreCache.Current.Credentials.Select(c => c.Id).ToHashSet();
        foreach (var id in _backoff.Keys.Where(id => !live.Contains(id)).ToList())
        {
            _backoff.TryRemove(id, out _);
        }
    }

    /// <summary>
    /// Lets the UI's "Connect"/"Refresh now" clear a credential's backoff immediately, so a
    /// user who just fixed the underlying problem is not made to wait out the timer.
    /// </summary>
    public void ResetBackoff(CredentialRecord credential) => _backoff.TryRemove(credential.Id, out _);
}
