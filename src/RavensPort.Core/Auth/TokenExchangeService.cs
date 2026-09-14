using System.Text;
using System.Text.Json;
using RavensPort.Core.Diagnostics;
using RavensPort.Core.Models;

namespace RavensPort.Core.Auth;

/// <summary>
/// Mints access tokens by trading a secret for one at a user-named endpoint — the same shape as
/// the OAuth2 client_credentials grant (<see cref="ClientCredentialsService"/>), generalised for
/// an API that predates OAuth or simply invented its own login call instead of adopting it.
///
/// Two shapes of secret can go out: a static key on a header of its own
/// (<see cref="TokenExchangeMode.ApiKey"/>), or a JSON body the user authored by hand and this
/// service posts verbatim (<see cref="TokenExchangeMode.CustomBody"/>) — a username/password pair
/// is the common case, but the body is opaque here. Nothing comes back with OAuth's fixed shape
/// either, so where the token — and, optionally, its lifetime — sits in the response is itself
/// configured, as a dot path read by <see cref="JsonPathReader"/>.
///
/// Self-issuing like client_credentials: there is no refresh token, so an expiring token is
/// re-minted by repeating the same exchange rather than presented for renewal.
/// </summary>
public sealed class TokenExchangeService : IDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// A refusal's body is logged for the "where did it fail" question, but not without a limit —
    /// a captive portal or a misrouted proxy answers with a full HTML page, and none of it is more
    /// informative than the first couple thousand characters.
    /// </summary>
    private const int MaxErrorBodyLength = 2000;

    private readonly ActivityLog _activityLog;
    private readonly HttpClient _httpClient;

    public TokenExchangeService(ActivityLog activityLog)
    {
        _activityLog = activityLog;
        _httpClient = new HttpClient { Timeout = RequestTimeout };
    }

    public async Task<AuthorizationOutcome> AcquireAsync(CredentialRecord credential, CancellationToken ct = default)
    {
        var configError = CredentialValidation.ValidateTokenExchange(
            credential.ExchangeEndpoint, credential.ExchangeMode,
            credential.ExchangeMode == TokenExchangeMode.ApiKey
                ? !string.IsNullOrEmpty(credential.ExchangeApiKey)
                : !string.IsNullOrWhiteSpace(credential.ExchangeRequestBody),
            credential.ExchangeApiKeyHeaderName, credential.ExchangeRequestBody, credential.ExchangeTokenPath);

        if (configError is not null)
        {
            credential.NeedsReconnect = true;
            return new AuthorizationOutcome(false, "invalid_configuration", configError);
        }

        var endpoint = credential.ExchangeEndpoint!.Trim();

        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint);

        if (credential.ExchangeMode == TokenExchangeMode.ApiKey)
        {
            message.Headers.TryAddWithoutValidation(
                credential.ExchangeApiKeyHeaderName.Trim(),
                credential.ExchangeApiKeyValuePrefix + credential.ExchangeApiKey);
        }
        else
        {
            message.Content = new StringContent(credential.ExchangeRequestBody!, Encoding.UTF8, "application/json");
        }

        try
        {
            using var response = await _httpClient.SendAsync(message, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                credential.NeedsReconnect = true;

                var detail = body.Length > MaxErrorBodyLength ? body[..MaxErrorBodyLength] + "…" : body;
                _activityLog.Log($"TOKEN '{credential.Name}' refused by {endpoint}: HTTP {(int)response.StatusCode}");

                return new AuthorizationOutcome(false, $"http_{(int)response.StatusCode}",
                    string.IsNullOrWhiteSpace(detail)
                        ? $"{endpoint} answered {(int)response.StatusCode} with no body."
                        : detail);
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(body);
            }
            catch (JsonException)
            {
                credential.NeedsReconnect = true;
                return new AuthorizationOutcome(false, "invalid_response",
                    $"{endpoint} answered successfully but the response was not valid JSON.");
            }

            using (document)
            {
                if (!JsonPathReader.TryGetString(document.RootElement, credential.ExchangeTokenPath, out var token))
                {
                    credential.NeedsReconnect = true;
                    return new AuthorizationOutcome(false, "no_token",
                        $"{endpoint} answered successfully but had nothing at '{credential.ExchangeTokenPath}'.");
                }

                DateTimeOffset? expiresAtUtc = null;
                if (JsonPathReader.TryGetSeconds(document.RootElement, credential.ExchangeExpiresInPath, out var seconds)
                    && seconds > 0)
                {
                    expiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(seconds);
                }

                credential.Token = new TokenSet(token!, RefreshToken: null, expiresAtUtc, "Bearer", DateTimeOffset.UtcNow);
                credential.NeedsReconnect = false;

                _activityLog.Log($"TOKEN '{credential.Name}' minted by token exchange at {endpoint} "
                                 + $"— {credential.Token.DescribeExpiry()}");

                return new AuthorizationOutcome(true, null, null);
            }
        }
        catch (Exception ex)
        {
            credential.NeedsReconnect = true;
            _activityLog.LogError($"Token exchange request failed for '{credential.Name}'", ex);
            return new AuthorizationOutcome(false, "token_exchange_error", ex.Message);
        }
    }

    public void Dispose() => _httpClient.Dispose();
}
