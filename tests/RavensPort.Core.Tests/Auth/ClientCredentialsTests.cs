using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RavensPort.Core.Auth;
using RavensPort.Core.Diagnostics;
using RavensPort.Core.Models;
using RavensPort.Core.Storage;
using RavensPort.Core.Vault;

namespace RavensPort.Core.Tests.Auth;

/// <summary>
/// The client_credentials grant, against a token endpoint that actually inspects the request.
///
/// A mock would assert what this code already believes. The whole risk in this grant lives in the
/// details of the request — whether the client pair went in a Basic header or the body, whether an
/// audience parameter survived, whether the response was even asked for as JSON — and every one of
/// those is only observable at the other end. The stub below answers only for the exact shape it
/// was promised and reports what it saw, so a wrong request fails here rather than as an
/// unexplained 'invalid_client' against a real provider.
/// </summary>
public class ClientCredentialsTests : IAsyncLifetime
{
    private const string ClientId = "app-1";
    private const string ClientSecret = "s3cret";

    private readonly string _logPath = Path.Combine(Path.GetTempPath(), $"ravensport-ccreds-logs-{Guid.NewGuid()}");

    private WebApplication _api = null!;
    private ActivityLog _activityLog = null!;
    private ClientCredentialsService _service = null!;
    private string _baseUrl = "";

    /// <summary>What the last token request carried, for the assertions that are about the request.</summary>
    private string? _lastAuthorization;
    private string? _lastAccept;
    private IFormCollection _lastForm = new FormCollection([]);

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        _api = builder.Build();

        _api.Run(async context =>
        {
            _lastAuthorization = context.Request.Headers.Authorization;
            _lastAccept = context.Request.Headers.Accept;
            _lastForm = context.Request.HasFormContentType ? await context.Request.ReadFormAsync() : new FormCollection([]);

            var path = context.Request.Path.Value;

            // Accepts the pair either way and says which it got, so one endpoint can serve both
            // placement tests without the test having to know two URLs.
            var authorized = path switch
            {
                "/token" or "/token/no-expiry" or "/token/401" => PairIsCorrect(),
                _ => false,
            };

            if (!authorized)
            {
                // RFC 6749 §5.2 permits either status for a rejected client and real providers use
                // both, so the stub refuses with 400 by default and with 401 on its own path.
                context.Response.StatusCode = path == "/token/401" ? 401 : 400;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(
                    """{"error":"invalid_client","error_description":"client authentication failed"}""");
                return;
            }

            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/json";

            // A provider that advertises no lifetime at all. RFC 6749 makes expires_in optional
            // and several real ones omit it.
            await context.Response.WriteAsync(path == "/token/no-expiry"
                ? """{"access_token":"MINTED","token_type":"Bearer"}"""
                : """{"access_token":"MINTED","token_type":"Bearer","expires_in":3600}""");
        });

        await _api.StartAsync();

        _baseUrl = _api.Services
            .GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!
            .Addresses.First().TrimEnd('/');

        _activityLog = new ActivityLog(_logPath);
        _service = new ClientCredentialsService(_activityLog);
    }

    public async Task DisposeAsync()
    {
        _service.Dispose();
        await _api.StopAsync();

        try { Directory.Delete(_logPath, recursive: true); } catch { /* best effort */ }
    }

    private bool PairIsCorrect()
    {
        if (_lastForm["client_id"] == ClientId && _lastForm["client_secret"] == ClientSecret)
        {
            return true;
        }

        if (_lastAuthorization is not { } header || !header.StartsWith("Basic ", StringComparison.Ordinal))
        {
            return false;
        }

        // Basic credentials are form-url-encoded before being base64'd (RFC 6749 §2.3.1).
        var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(header["Basic ".Length..]));
        var parts = decoded.Split(':', 2);

        return parts.Length == 2
               && Uri.UnescapeDataString(parts[0]) == ClientId
               && Uri.UnescapeDataString(parts[1]) == ClientSecret;
    }

    private CredentialRecord NewCredential(string path = "/token", string secret = ClientSecret) => new()
    {
        Name = "app-login",
        Kind = CredentialKind.ClientCredentials,
        ClientId = ClientId,
        ClientSecret = secret,
        TokenEndpoint = _baseUrl + path,
        Scopes = ["read", "write"],
    };

    // ---- The request the provider actually sees -------------------------------------------------

    [Fact]
    public async Task ByDefaultTheClientPairGoesInABasicHeader()
    {
        var credential = NewCredential();

        var outcome = await _service.AcquireAsync(credential);

        Assert.True(outcome.Success, outcome.ErrorDescription);
        Assert.StartsWith("Basic ", _lastAuthorization);
        Assert.True(string.IsNullOrEmpty(_lastForm["client_secret"]),
            "the secret must not be sent twice — a provider that reads both can disagree with itself");
    }

    [Fact]
    public async Task TheClientPairCanBeMovedIntoTheBody()
    {
        // The setting exists because providers disagree about which they accept, and the one that
        // wants the other answers 'invalid_client' without saying which half was wrong.
        var credential = NewCredential();
        credential.SendClientCredentialsInBody = true;

        var outcome = await _service.AcquireAsync(credential);

        Assert.True(outcome.Success, outcome.ErrorDescription);
        Assert.Equal(ClientId, _lastForm["client_id"]);
        Assert.Equal(ClientSecret, _lastForm["client_secret"]);
        Assert.True(string.IsNullOrEmpty(_lastAuthorization), "the pair must go one place, not both");
    }

    [Fact]
    public async Task TheTokenRequestAsksForJson()
    {
        // GitHub's token endpoint answers form-encoded without this, which arrives as an
        // unparseable token response and sends you looking at the client secret instead.
        await _service.AcquireAsync(NewCredential());

        Assert.Contains("application/json", _lastAccept);
    }

    [Fact]
    public async Task ScopesAreSentSpaceSeparated()
    {
        await _service.AcquireAsync(NewCredential());

        Assert.Equal("read write", _lastForm["scope"]);
    }

    [Fact]
    public async Task AnEmptyScopeListIsOmittedRatherThanSentBlank()
    {
        // A provider that reads scope as "narrow the token to exactly this" treats an empty
        // string as no permissions, where an absent one means everything the client may have.
        var credential = NewCredential();
        credential.Scopes = [];

        await _service.AcquireAsync(credential);

        Assert.False(_lastForm.ContainsKey("scope"));
    }

    [Fact]
    public async Task ExtraParametersReachTheTokenRequest()
    {
        // There is no authorization request to hang an audience on, so this is the only way to
        // say what the token is for — and Auth0 issues a useless token without it.
        var credential = NewCredential();
        credential.ExtraAuthParams = "audience=https%3A%2F%2Fapi.example.com%2F&tenant=acme";

        await _service.AcquireAsync(credential);

        // Percent-decoded exactly once: the value is re-encoded on the wire, and skipping the
        // decode meant the provider saw a literal "%2F" where a "/" was meant.
        Assert.Equal("https://api.example.com/", _lastForm["audience"]);
        Assert.Equal("acme", _lastForm["tenant"]);
    }

    // ---- What comes back ------------------------------------------------------------------------

    [Fact]
    public async Task AMintedTokenIsStoredWithItsExpiryAndNoRefreshToken()
    {
        var credential = NewCredential();

        Assert.True((await _service.AcquireAsync(credential)).Success);

        Assert.NotNull(credential.Token);
        Assert.Equal("MINTED", credential.Token!.AccessToken);
        Assert.Equal("Bearer", credential.Token.TokenType);
        Assert.False(credential.NeedsReconnect);

        // RFC 6749 §4.4.3 says a client credentials response should not include one, and it would
        // be redundant if it did — the client secret is what renews the token.
        Assert.Null(credential.Token.RefreshToken);

        Assert.NotNull(credential.Token.ExpiresAtUtc);
        Assert.InRange(
            credential.Token.ExpiresAtUtc!.Value,
            DateTimeOffset.UtcNow.AddMinutes(58),
            DateTimeOffset.UtcNow.AddMinutes(61));
    }

    [Fact]
    public async Task AResponseWithoutExpiresInIsRecordedAsHavingNoExpiry()
    {
        // Not as "expired the instant it arrived", which is what computing "now + 0 seconds"
        // produced: a perfectly good token then showed as expired and was re-minted on every
        // single request.
        var credential = NewCredential("/token/no-expiry");

        Assert.True((await _service.AcquireAsync(credential)).Success);

        Assert.Null(credential.Token!.ExpiresAtUtc);
        Assert.False(credential.Token.IsExpiringWithin(TimeSpan.FromDays(3650)));
    }

    [Fact]
    public async Task ARefusalIsReportedWithTheProvidersOwnWordsAndWhereTheSecretWent()
    {
        var credential = NewCredential(secret: "WRONG");

        var outcome = await _service.AcquireAsync(credential);

        Assert.False(outcome.Success);
        Assert.Equal("invalid_client", outcome.Error);
        Assert.Contains("client authentication failed", outcome.ErrorDescription);

        // The one thing the app can usefully add to 'invalid_client': which of the two placements
        // it chose, since choosing the wrong one produces exactly this answer.
        Assert.Contains("Basic", outcome.ErrorDescription);

        Assert.True(credential.NeedsReconnect);
        Assert.Null(credential.Token);
    }

    [Fact]
    public async Task ARefusalSentAs401StillReportsTheProvidersOAuthError()
    {
        // The OAuth library treats only 400 as a protocol error and classifies a 401 as a
        // transport failure, so a response that named 'invalid_client' precisely arrived as the
        // bare word "Unauthorized" — the one detail worth having, discarded.
        var credential = NewCredential("/token/401", secret: "WRONG");

        var outcome = await _service.AcquireAsync(credential);

        Assert.False(outcome.Success);
        Assert.Equal("invalid_client", outcome.Error);
        Assert.Contains("client authentication failed", outcome.ErrorDescription);
    }

    [Fact]
    public async Task MissingConfigurationIsCaughtBeforeAnyRequestIsSent()
    {
        var credential = NewCredential();
        credential.TokenEndpoint = null;

        var outcome = await _service.AcquireAsync(credential);

        Assert.False(outcome.Success);
        Assert.Equal("invalid_configuration", outcome.Error);
        Assert.Contains("Token endpoint", outcome.ErrorDescription);
    }

    // ---- How the rest of the app uses it --------------------------------------------------------

    private OAuth2Service NewOAuth2Service() => new(
        new GoogleOAuthService(_activityLog),
        new GoogleServiceAccountService(_activityLog),
        _service,
        new DeviceCodeService(_activityLog),
        _activityLog);

    [Fact]
    public async Task AFreshCredentialMintsATokenOnItsFirstProxiedRequest()
    {
        // An app login has no Connect step and nobody to press it. If the first request through a
        // route did not mint, a correctly configured credential would forward nothing until
        // someone happened to open the window.
        var cache = new ConfigStoreCache(new InMemoryVault());
        await cache.InitializeAsync();

        var credential = NewCredential();
        await cache.MutateAsync(store => store.Credentials.Add(credential));

        var provider = new AccessTokenProvider(cache, NewOAuth2Service(), _activityLog);

        Assert.Null(credential.Token);
        Assert.Equal("MINTED", await provider.GetAccessTokenAsync(credential.Id));
        Assert.NotNull(credential.Token);
    }

    [Fact]
    public async Task ARefusedMintIsNotRetriedOnEverySingleRequest()
    {
        // Nothing about a rejected client secret changes between two requests a millisecond
        // apart. Retrying per request turns one configuration mistake into a burst of failed
        // token requests at whatever rate the caller is sending — enough to get rate-limited.
        var cache = new ConfigStoreCache(new InMemoryVault());
        await cache.InitializeAsync();

        var credential = NewCredential(secret: "WRONG");
        await cache.MutateAsync(store => store.Credentials.Add(credential));

        var provider = new AccessTokenProvider(cache, NewOAuth2Service(), _activityLog);

        Assert.Null(await provider.GetAccessTokenAsync(credential.Id));
        Assert.True(credential.NeedsReconnect);

        var afterFirstAttempt = _lastForm;

        // The second request must not reach the token endpoint at all. Clearing the record of the
        // last request makes that observable: anything arriving would replace it.
        _lastForm = new FormCollection([]);
        Assert.Null(await provider.GetAccessTokenAsync(credential.Id));

        Assert.NotEmpty(afterFirstAttempt);
        Assert.Empty(_lastForm);
    }

    [Fact]
    public async Task TheBackgroundLoopIsWhatRetriesAFailedAppLogin()
    {
        // Since the per-request path stops after a refusal, the retry has to live somewhere with
        // a backoff on it — otherwise a credential that failed once while the network was down
        // would stay dead until someone opened the window and pressed a button.
        var cache = new ConfigStoreCache(new InMemoryVault());
        await cache.InitializeAsync();

        var credential = NewCredential();
        credential.NeedsReconnect = true;
        await cache.MutateAsync(store => store.Credentials.Add(credential));

        var refresher = new TokenRefreshService(
            cache, NewOAuth2Service(), _activityLog, NullLogger<TokenRefreshService>.Instance);

        await refresher.RefreshDueCredentialsAsync(CancellationToken.None);

        Assert.Equal("MINTED", credential.Token!.AccessToken);
        Assert.False(credential.NeedsReconnect);
    }

    [Fact]
    public async Task TheBackgroundLoopLeavesAnUnconfiguredCredentialAlone()
    {
        // An app login with no secret cannot mint anything, and attempting it every minute would
        // fill the log with a failure the user has not finished causing yet.
        var cache = new ConfigStoreCache(new InMemoryVault());
        await cache.InitializeAsync();

        var credential = NewCredential();
        credential.ClientSecret = "";
        await cache.MutateAsync(store => store.Credentials.Add(credential));

        var refresher = new TokenRefreshService(
            cache, NewOAuth2Service(), _activityLog, NullLogger<TokenRefreshService>.Instance);

        await refresher.RefreshDueCredentialsAsync(CancellationToken.None);

        Assert.Null(credential.Token);
        Assert.Empty(_lastForm);
    }

    [Fact]
    public async Task TheBackgroundLoopRenewsAnAppLoginThatHasNoRefreshToken()
    {
        // The loop used to require a refresh token to consider a credential renewable, which an
        // app login never has — every one of them would have expired mid-use and stayed expired.
        var cache = new ConfigStoreCache(new InMemoryVault());
        await cache.InitializeAsync();

        var credential = NewCredential();
        credential.Token = new TokenSet("STALE", null, DateTimeOffset.UtcNow.AddMinutes(1), "Bearer", DateTimeOffset.UtcNow);
        await cache.MutateAsync(store => store.Credentials.Add(credential));

        var refresher = new TokenRefreshService(
            cache, NewOAuth2Service(), _activityLog, NullLogger<TokenRefreshService>.Instance);

        await refresher.RefreshDueCredentialsAsync(CancellationToken.None);

        Assert.Equal("MINTED", credential.Token!.AccessToken);
    }
}
