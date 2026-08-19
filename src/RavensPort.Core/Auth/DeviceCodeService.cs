using System.Diagnostics;
using IdentityModel.Client;
using RavensPort.Core.Diagnostics;
using RavensPort.Core.Models;

namespace RavensPort.Core.Auth;

/// <summary>
/// What the user has to be shown to finish a device code sign-in, reported as soon as the
/// provider issues it rather than when the flow finishes — the whole flow is the user acting on
/// this, so it is useless after the fact.
/// </summary>
/// <param name="UserCode">The short code to type. Shown verbatim, hyphens and all.</param>
/// <param name="VerificationUri">Where to type it.</param>
/// <param name="VerificationUriComplete">
/// The same page with the code already in it, when the provider offers one. Preferred for opening
/// a browser, since it removes the transcription step entirely.
/// </param>
/// <param name="ExpiresAtUtc">When the code stops being accepted.</param>
public sealed record DeviceCodePrompt(
    string UserCode,
    string VerificationUri,
    string? VerificationUriComplete,
    DateTimeOffset ExpiresAtUtc);

/// <summary>
/// The OAuth2 device authorization grant, RFC 8628.
///
/// The provider issues a short code, the user types it wherever they like — a phone, another
/// machine, the browser here — and this app polls the token endpoint until they have. It is a
/// real user login and yields a refresh token like any other; what it does not need is a redirect
/// coming back to this machine, which is the whole point. A provider that will not register a
/// loopback callback URL, or a machine with no browser to open, has no other way through.
///
/// The waiting is the unusual part. A pending authorization is reported as an <em>error</em>
/// (<c>authorization_pending</c>), so the poll loop below reads error codes as control flow, and
/// treats only the ones RFC 8628 §3.5 names as terminal.
/// </summary>
public sealed class DeviceCodeService : IDisposable
{
    /// <summary>Poll interval when the provider names none. RFC 8628 §3.2 sets this default.</summary>
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(5);

    /// <summary>Added to the interval on <c>slow_down</c>, as RFC 8628 §3.5 requires.</summary>
    private static readonly TimeSpan SlowDownIncrement = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long to wait when the provider does not say how long its code lives. Long enough for
    /// someone to walk to another device, short enough that an abandoned flow does not poll
    /// somebody's token endpoint until the app is closed.
    /// </summary>
    private static readonly TimeSpan DefaultCodeLifetime = TimeSpan.FromMinutes(15);

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Never opens anything. Pass this from tests and from any headless host: the device flow is
    /// exercised end to end against a stub provider, and the default would shell out to a real
    /// browser once per authorization -- on a CI agent that is a window per test, on a developer's
    /// machine it is a tab per test, and in neither case is it what the test is asserting.
    /// </summary>
    public static readonly Action<Uri> DoNotOpen = _ => { };

    private readonly ActivityLog _activityLog;
    private readonly HttpClient _httpClient;

    /// <summary>
    /// How the verification page gets opened, injected so the shell-out is substitutable. Left at
    /// its default everywhere in the app; see <see cref="DoNotOpen"/> for why anything else exists.
    /// </summary>
    private readonly Action<Uri> _openBrowser;

    public DeviceCodeService(ActivityLog activityLog, Action<Uri>? openBrowser = null)
    {
        _activityLog = activityLog;
        _openBrowser = openBrowser ?? ShellOpen;

        // Same reason as everywhere else a token endpoint is called directly: a provider that
        // defaults to form-encoded output otherwise returns something the parser rejects.
        _httpClient = new HttpClient(new JsonAcceptHandler(new HttpClientHandler()))
        {
            Timeout = RequestTimeout,
        };
    }

    public async Task<AuthorizationOutcome> AuthorizeAsync(
        CredentialRecord credential,
        IProgress<DeviceCodePrompt>? prompt = null,
        CancellationToken ct = default)
    {
        var configError = CredentialValidation.ValidateDeviceCode(
            credential.ClientId, credential.DeviceAuthorizationEndpoint, credential.TokenEndpoint);

        if (configError is not null)
        {
            return new AuthorizationOutcome(false, "invalid_configuration", configError);
        }

        DeviceAuthorizationResponse authorization;
        try
        {
            authorization = await RequestCodeAsync(credential, ct);
        }
        catch (Exception ex)
        {
            _activityLog.LogError($"Device code request failed for '{credential.Name}'", ex);
            return new AuthorizationOutcome(false, "device_code_error", ex.Message);
        }

        if (authorization.IsError)
        {
            var (error, description) = TokenErrorReader.Read(authorization);
            _activityLog.Log($"DEVICE '{credential.Name}' refused at {credential.DeviceAuthorizationEndpoint}: "
                             + $"{error} {description}".Trim());
            return new AuthorizationOutcome(false, error, description);
        }

        if (authorization.DeviceCode is not { Length: > 0 } deviceCode ||
            authorization.UserCode is not { Length: > 0 } userCode ||
            authorization.VerificationUri is not { Length: > 0 } verificationUri)
        {
            return new AuthorizationOutcome(false, "incomplete_response",
                "The provider answered without a device code, user code, or verification URL.");
        }

        var lifetime = authorization.ExpiresIn is > 0
            ? TimeSpan.FromSeconds(authorization.ExpiresIn.Value)
            : DefaultCodeLifetime;

        var deadline = DateTimeOffset.UtcNow + lifetime;

        prompt?.Report(new DeviceCodePrompt(
            userCode, verificationUri, authorization.VerificationUriComplete, deadline));

        // Logged without the device code, which is the bearer-equivalent half of this exchange.
        // The user code is meaningless without it and is on screen anyway.
        _activityLog.Log($"DEVICE '{credential.Name}' code {userCode} issued — enter it at {verificationUri} "
                         + $"by {deadline.ToLocalTime():t}");

        OpenVerificationPage(authorization.VerificationUriComplete ?? verificationUri, credential.Name);

        return await PollAsync(credential, deviceCode, authorization.Interval, deadline, ct);
    }

    /// <summary>
    /// Refreshes a grant obtained this way. An ordinary refresh_token exchange — the device flow
    /// is only how the grant was first approved, and the provider does not care afterwards.
    ///
    /// Not routed through OidcClient like the browser flow's refresh: that path wants an Authority
    /// to discover or a full ProviderInformation, and a device credential legitimately has only a
    /// token endpoint. It also has no say over how a public client identifies itself, which is the
    /// one thing that has to be right here.
    /// </summary>
    public async Task<TokenSet?> RefreshAsync(CredentialRecord credential, CancellationToken ct = default)
    {
        if (credential.Token?.RefreshToken is not { } refreshToken) return null;

        if (string.IsNullOrWhiteSpace(credential.TokenEndpoint))
        {
            _activityLog.Log($"REFRESH '{credential.Name}' has no token endpoint stored — reconnect required");
            credential.NeedsReconnect = true;
            return null;
        }

        var request = new RefreshTokenRequest
        {
            Address = credential.TokenEndpoint.Trim(),
            RefreshToken = refreshToken,
        };

        ApplyClientIdentity(request, credential);

        try
        {
            var response = await _httpClient.RequestRefreshTokenAsync(request, ct);

            if (response.IsError)
            {
                var (error, description) = TokenErrorReader.Read(response);
                _activityLog.Log($"REFRESH '{credential.Name}' provider error: {error} {description}".Trim());
                credential.NeedsReconnect = true;
                return null;
            }

            credential.Token = new TokenSet(
                response.AccessToken!,
                // Most providers omit refresh_token on a refresh — keep the one we have. Some
                // rotate it, and dropping the new one there would break the next refresh instead.
                string.IsNullOrEmpty(response.RefreshToken) ? refreshToken : response.RefreshToken,
                response.ExpiresIn > 0 ? DateTimeOffset.UtcNow.AddSeconds(response.ExpiresIn) : null,
                string.IsNullOrEmpty(response.TokenType) ? "Bearer" : response.TokenType,
                DateTimeOffset.UtcNow);
            credential.NeedsReconnect = false;

            return credential.Token;
        }
        catch (Exception ex)
        {
            _activityLog.LogError($"Device code refresh failed for '{credential.Name}'", ex);
            credential.NeedsReconnect = true;
            return null;
        }
    }

    private async Task<DeviceAuthorizationResponse> RequestCodeAsync(CredentialRecord credential, CancellationToken ct)
    {
        var request = new DeviceAuthorizationRequest
        {
            Address = credential.DeviceAuthorizationEndpoint!.Trim(),
            Scope = credential.Scopes.Count == 0 ? null : string.Join(' ', credential.Scopes),
        };

        ApplyClientIdentity(request, credential);

        foreach (var pair in ExtraParameters.Parse(credential.ExtraAuthParams))
        {
            request.Parameters.Add(pair.Key, pair.Value);
        }

        return await _httpClient.RequestDeviceAuthorizationAsync(request, ct);
    }

    private async Task<AuthorizationOutcome> PollAsync(
        CredentialRecord credential,
        string deviceCode,
        int? providerInterval,
        DateTimeOffset deadline,
        CancellationToken ct)
    {
        var interval = providerInterval is > 0 ? TimeSpan.FromSeconds(providerInterval.Value) : DefaultInterval;

        while (true)
        {
            // Waits before the first poll on purpose: the user cannot possibly have approved
            // anything yet, and an immediate request only earns a slow_down.
            try
            {
                await Task.Delay(interval, ct);
            }
            catch (OperationCanceledException)
            {
                return new AuthorizationOutcome(false, "cancelled", "Waiting for the code was cancelled.");
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                return new AuthorizationOutcome(false, "expired_token",
                    "The code expired before it was approved. Start again to get a new one.");
            }

            TokenResponse response;
            try
            {
                var request = new DeviceTokenRequest
                {
                    Address = credential.TokenEndpoint!.Trim(),
                    DeviceCode = deviceCode,
                };

                ApplyClientIdentity(request, credential);
                response = await _httpClient.RequestDeviceTokenAsync(request, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One failed poll is not a failed sign-in — a dropped connection mid-wait is
                // ordinary, and the user is still standing at the other device. Keep waiting;
                // the deadline above is what ends this.
                _activityLog.LogError($"Device code poll for '{credential.Name}' threw — still waiting", ex);
                continue;
            }
            catch (OperationCanceledException)
            {
                return new AuthorizationOutcome(false, "cancelled", "Waiting for the code was cancelled.");
            }

            if (!response.IsError)
            {
                if (string.IsNullOrEmpty(response.AccessToken))
                {
                    return new AuthorizationOutcome(false, "no_token",
                        "The provider approved the code but returned no access token.");
                }

                credential.Token = new TokenSet(
                    response.AccessToken,
                    string.IsNullOrEmpty(response.RefreshToken) ? null : response.RefreshToken,
                    response.ExpiresIn > 0 ? DateTimeOffset.UtcNow.AddSeconds(response.ExpiresIn) : null,
                    string.IsNullOrEmpty(response.TokenType) ? "Bearer" : response.TokenType,
                    DateTimeOffset.UtcNow);
                credential.NeedsReconnect = false;

                _activityLog.Log($"DEVICE '{credential.Name}' approved — {credential.Token.DescribeExpiry()}"
                                 + (credential.Token.RefreshToken is null
                                     ? " (no refresh token issued — it will need reconnecting when it expires)"
                                     : ""));

                return new AuthorizationOutcome(true, null, null);
            }

            var (error, description) = TokenErrorReader.Read(response);

            switch (error)
            {
                // Nobody has typed the code yet. The expected answer for most of this loop.
                case "authorization_pending":
                    continue;

                // Polling too fast. The increment is mandatory and cumulative — ignoring it is
                // how a provider ends up refusing outright.
                case "slow_down":
                    interval += SlowDownIncrement;
                    continue;

                case "access_denied":
                    return new AuthorizationOutcome(false, error,
                        "The request was declined at the provider.");

                case "expired_token":
                    return new AuthorizationOutcome(false, error,
                        "The code expired before it was approved. Start again to get a new one.");

                default:
                    _activityLog.Log($"DEVICE '{credential.Name}' poll failed: {error} {description}".Trim());
                    return new AuthorizationOutcome(false, error, description);
            }
        }
    }

    /// <summary>
    /// Says who is asking, in whichever way this client can.
    ///
    /// A public client — which most device flow clients are, since RFC 8628 exists for clients
    /// that cannot hold a secret — sends only <c>client_id</c>, and it must go in the body: an
    /// HTTP Basic header built from an empty secret is a different, wrong assertion, and providers
    /// reject it as <c>invalid_client</c> rather than ignoring it.
    /// </summary>
    private static void ApplyClientIdentity(ProtocolRequest request, CredentialRecord credential)
    {
        request.ClientId = credential.ClientId.Trim();

        if (string.IsNullOrEmpty(credential.ClientSecret))
        {
            request.ClientCredentialStyle = ClientCredentialStyle.PostBody;
            return;
        }

        request.ClientSecret = credential.ClientSecret;
        request.ClientCredentialStyle = credential.SendClientCredentialsInBody
            ? ClientCredentialStyle.PostBody
            : ClientCredentialStyle.AuthorizationHeader;
    }

    /// <summary>
    /// Opens the verification page as a convenience. Best-effort on purpose: the code and the URL
    /// have already been reported, and this flow's whole premise is that the user may be about to
    /// use a different device anyway — so a browser that will not open is not a failed sign-in.
    /// </summary>
    private void OpenVerificationPage(string url, string credentialName)
    {
        // The URL comes from a provider named by user-editable configuration, and UseShellExecute
        // hands whatever it is to the shell — a protocol handler, a UNC path, an executable. Same
        // guard the loopback browser applies for the same reason.
        if (!UrlValidation.IsSafeToOpenInBrowser(url, out var verificationUri))
        {
            _activityLog.Log($"DEVICE '{credentialName}' verification URL is not an http/https address — not opening it");
            return;
        }

        try
        {
            // The parsed form rather than the string, so what is launched is what was checked.
            _openBrowser(verificationUri);
        }
        catch (Exception ex)
        {
            _activityLog.LogError($"Could not open the verification page for '{credentialName}'", ex);
        }
    }

    /// <summary>
    /// The real thing: hand the URL to the shell and let Windows pick the browser. Only ever
    /// reached through <see cref="_openBrowser"/>, and only when the caller supplied nothing --
    /// which is every caller in the app and none in the tests.
    /// </summary>
    private static void ShellOpen(Uri uri) =>
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });

    public void Dispose() => _httpClient.Dispose();
}
