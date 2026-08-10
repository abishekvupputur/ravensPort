using System.Text.Json;
using IdentityModel.Client;
using RavensPort.Core.Diagnostics;
using RavensPort.Core.Models;

namespace RavensPort.Core.Auth;

/// <summary>
/// Mints access tokens with the OAuth2 client_credentials grant (RFC 6749 §4.4) — an app login.
///
/// The client id and secret are the whole of the identity: there is no user, no browser, and no
/// refresh token, because a client that can prove itself once can prove itself again. An expired
/// token is therefore re-minted rather than refreshed, and a credential of this kind is usable by
/// a route the moment it is saved, without anyone pressing Connect.
/// </summary>
public sealed class ClientCredentialsService : IDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private readonly ActivityLog _activityLog;
    private readonly HttpClient _httpClient;

    public ClientCredentialsService(ActivityLog activityLog)
    {
        _activityLog = activityLog;

        // JsonAcceptHandler for the same reason the interactive back channel has one: a token
        // endpoint that defaults to form-encoded output otherwise returns something the parser
        // reports as a malformed response.
        _httpClient = new HttpClient(new JsonAcceptHandler(new HttpClientHandler()))
        {
            Timeout = RequestTimeout,
        };
    }

    public async Task<AuthorizationOutcome> AcquireAsync(CredentialRecord credential, CancellationToken ct = default)
    {
        var configError = CredentialValidation.ValidateClientCredentials(
            credential.ClientId, !string.IsNullOrEmpty(credential.ClientSecret), credential.TokenEndpoint);

        if (configError is not null)
        {
            credential.NeedsReconnect = true;
            return new AuthorizationOutcome(false, "invalid_configuration", configError);
        }

        var request = new ClientCredentialsTokenRequest
        {
            Address = credential.TokenEndpoint!.Trim(),
            ClientId = credential.ClientId.Trim(),
            ClientSecret = credential.ClientSecret,

            // Omitted rather than sent empty: a provider that treats scope as "narrow the token to
            // exactly this" reads an empty string as "no permissions", where absent means "all the
            // client is entitled to".
            Scope = credential.Scopes.Count == 0 ? null : string.Join(' ', credential.Scopes),

            ClientCredentialStyle = credential.SendClientCredentialsInBody
                ? ClientCredentialStyle.PostBody
                : ClientCredentialStyle.AuthorizationHeader,
        };

        // There is no authorization request to hang these on, so they go on the token request —
        // which is where the 'audience' (Auth0) or 'resource' (Entra ID) that decides what the
        // token is even for has to be.
        foreach (var pair in ExtraParameters.Parse(credential.ExtraAuthParams))
        {
            request.Parameters.Add(pair.Key, pair.Value);
        }

        try
        {
            var response = await _httpClient.RequestClientCredentialsTokenAsync(request, ct);

            if (response.IsError)
            {
                credential.NeedsReconnect = true;

                var (error, description) = DescribeFailure(response);

                // 'invalid_client' is by far the most common answer here and says nothing about
                // which half is wrong. The one thing this app can usefully add is the choice it
                // made about where the credentials went, since that is the usual cause.
                var placement = credential.SendClientCredentialsInBody
                    ? "credentials were sent in the request body"
                    : "credentials were sent as an HTTP Basic header";

                _activityLog.Log($"TOKEN '{credential.Name}' refused by {request.Address}: "
                                 + $"{error} {description} ({placement})".Trim());

                var detail = string.IsNullOrWhiteSpace(description) ? "" : description + " ";

                return new AuthorizationOutcome(false, error,
                    $"{detail}The {placement}; if the provider expects the other, change the "
                    + "'Send client credentials in request body' setting.");
            }

            if (string.IsNullOrEmpty(response.AccessToken))
            {
                credential.NeedsReconnect = true;
                return new AuthorizationOutcome(false, "no_token",
                    $"{request.Address} answered successfully but returned no access_token.");
            }

            credential.Token = new TokenSet(
                response.AccessToken,
                // A client credentials response is not supposed to include one (RFC 6749 §4.4.3),
                // and it would be useless if it did — the client secret already renews the token.
                RefreshToken: null,
                // ExpiresIn is 0 when the provider sent no lifetime. Recorded as "no expiry"
                // rather than as "expired the moment it arrived".
                response.ExpiresIn > 0 ? DateTimeOffset.UtcNow.AddSeconds(response.ExpiresIn) : null,
                string.IsNullOrEmpty(response.TokenType) ? "Bearer" : response.TokenType,
                DateTimeOffset.UtcNow);
            credential.NeedsReconnect = false;

            _activityLog.Log($"TOKEN '{credential.Name}' minted by client credentials at {request.Address} "
                             + $"— {credential.Token.DescribeExpiry()}");

            return new AuthorizationOutcome(true, null, null);
        }
        catch (Exception ex)
        {
            credential.NeedsReconnect = true;
            _activityLog.LogError($"Client credentials token request failed for '{credential.Name}'", ex);
            return new AuthorizationOutcome(false, "client_credentials_error", ex.Message);
        }
    }

    /// <summary>
    /// What went wrong, preferring the provider's own OAuth error over the HTTP one.
    ///
    /// RFC 6749 §5.2 lets a server answer either 400 or 401 for a rejected client, and real ones
    /// use both. The library only treats 400 as a protocol error; a 401 is classified as a
    /// transport failure and <c>Error</c> becomes the bare reason phrase — so a response saying
    /// exactly which of "invalid_client" or "invalid_scope" it was reached the user as
    /// "Unauthorized". The body is parsed either way, so it is read directly here.
    /// </summary>
    private static (string? Error, string? Description) DescribeFailure(TokenResponse response)
    {
        if (response.Json is { ValueKind: JsonValueKind.Object } json &&
            ReadString(json, "error") is { } error)
        {
            return (error, ReadString(json, "error_description"));
        }

        return (response.Error, response.ErrorDescription);
    }

    private static string? ReadString(JsonElement json, string name) =>
        json.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
        && value.GetString() is { Length: > 0 } text
            ? text
            : null;

    public void Dispose() => _httpClient.Dispose();
}
