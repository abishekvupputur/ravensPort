using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RavensPort.Core.Auth;
using RavensPort.Core.Diagnostics;
using RavensPort.Core.Models;

namespace RavensPort.Core.Tests.Auth;

/// <summary>
/// The token exchange credential, against an endpoint that actually inspects the request — same
/// reasoning as <see cref="ClientCredentialsTests"/>: what matters here is only observable at the
/// other end, since nothing about the request or the response has a fixed OAuth shape to check
/// against in-process.
/// </summary>
public class TokenExchangeTests : IAsyncLifetime
{
    private const string Key = "sk-secret-key";

    private readonly string _logPath = Path.Combine(Path.GetTempPath(), $"ravensport-texch-logs-{Guid.NewGuid()}");

    private WebApplication _api = null!;
    private ActivityLog _activityLog = null!;
    private TokenExchangeService _service = null!;
    private string _baseUrl = "";

    private string? _lastAuthorization;
    private string? _lastBody;
    private int _requestCount;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        _api = builder.Build();

        _api.Run(async context =>
        {
            _requestCount++;
            _lastAuthorization = context.Request.Headers.Authorization;
            using var reader = new StreamReader(context.Request.Body);
            _lastBody = await reader.ReadToEndAsync();

            var path = context.Request.Path.Value;
            context.Response.ContentType = "application/json";

            switch (path)
            {
                case "/exchange":
                    context.Response.StatusCode = 200;
                    await context.Response.WriteAsync(
                        """{"data":{"token":"MINTED","lifetime":{"seconds":3600}}}""");
                    return;

                case "/exchange/no-expiry":
                    context.Response.StatusCode = 200;
                    await context.Response.WriteAsync("""{"access_token":"MINTED"}""");
                    return;

                case "/exchange/no-token":
                    context.Response.StatusCode = 200;
                    await context.Response.WriteAsync("""{"ok":true}""");
                    return;

                case "/exchange/not-json":
                    context.Response.StatusCode = 200;
                    await context.Response.WriteAsync("not json at all");
                    return;

                case "/exchange/refused":
                    context.Response.StatusCode = 401;
                    await context.Response.WriteAsync("""{"error":"invalid key"}""");
                    return;

                default:
                    context.Response.StatusCode = 404;
                    return;
            }
        });

        await _api.StartAsync();

        _baseUrl = _api.Services
            .GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!
            .Addresses.First();

        _activityLog = new ActivityLog(_logPath);
        _service = new TokenExchangeService(_activityLog);
    }

    public async Task DisposeAsync()
    {
        _service.Dispose();
        await _api.StopAsync();
        try { Directory.Delete(_logPath, recursive: true); } catch { /* best effort */ }
    }

    private CredentialRecord ApiKeyCredential(string path = "/exchange") => new()
    {
        Name = "test", Kind = CredentialKind.TokenExchange,
        ExchangeMode = TokenExchangeMode.ApiKey,
        ExchangeEndpoint = _baseUrl + path,
        ExchangeApiKey = Key,
        ExchangeApiKeyHeaderName = "Authorization",
        ExchangeApiKeyValuePrefix = "Bearer ",
        ExchangeTokenPath = "data.token",
        ExchangeExpiresInPath = "data.lifetime.seconds",
    };

    // ---- the request the endpoint actually sees --------------------------------------------------

    [Fact]
    public async Task TheApiKeyGoesOnTheConfiguredHeaderWithItsPrefix()
    {
        var outcome = await _service.AcquireAsync(ApiKeyCredential());

        Assert.True(outcome.Success);
        Assert.Equal($"Bearer {Key}", _lastAuthorization);
    }

    [Fact]
    public async Task ACustomBodyIsPostedVerbatim()
    {
        var credential = new CredentialRecord
        {
            Name = "test", Kind = CredentialKind.TokenExchange,
            ExchangeMode = TokenExchangeMode.CustomBody,
            ExchangeEndpoint = _baseUrl + "/exchange",
            ExchangeRequestBody = """{"username":"bob","password":"hunter2"}""",
            ExchangeTokenPath = "data.token",
        };

        await _service.AcquireAsync(credential);

        Assert.Equal("""{"username":"bob","password":"hunter2"}""", _lastBody);
        Assert.True(string.IsNullOrEmpty(_lastAuthorization));
    }

    // ---- what comes back ---------------------------------------------------------------------------

    [Fact]
    public async Task ATokenIsReadFromTheConfiguredNestedPath()
    {
        var outcome = await _service.AcquireAsync(ApiKeyCredential());

        Assert.True(outcome.Success);
    }

    [Fact]
    public async Task TheMintedTokenAndItsExpiryAreStoredWithNoRefreshToken()
    {
        var credential = ApiKeyCredential();
        await _service.AcquireAsync(credential);

        Assert.Equal("MINTED", credential.Token!.AccessToken);
        Assert.Null(credential.Token.RefreshToken);
        Assert.NotNull(credential.Token.ExpiresAtUtc);
        Assert.InRange(credential.Token.ExpiresAtUtc!.Value,
            DateTimeOffset.UtcNow.AddMinutes(59), DateTimeOffset.UtcNow.AddMinutes(61));
        Assert.False(credential.NeedsReconnect);
    }

    [Fact]
    public async Task AResponseWithNoExpiryPathHitIsRecordedAsNeverExpiring()
    {
        var credential = ApiKeyCredential("/exchange/no-expiry");
        credential.ExchangeTokenPath = "access_token";
        credential.ExchangeExpiresInPath = "expires_in";

        await _service.AcquireAsync(credential);

        Assert.Equal("MINTED", credential.Token!.AccessToken);
        Assert.Null(credential.Token.ExpiresAtUtc);
    }

    [Fact]
    public async Task ATokenMissingFromTheConfiguredPathIsAnError()
    {
        var credential = ApiKeyCredential("/exchange/no-token");
        var outcome = await _service.AcquireAsync(credential);

        Assert.False(outcome.Success);
        Assert.Equal("no_token", outcome.Error);
        Assert.Contains("data.token", outcome.ErrorDescription);
        Assert.Null(credential.Token);
        Assert.True(credential.NeedsReconnect);
    }

    [Fact]
    public async Task ANonJsonResponseIsReportedRatherThanThrown()
    {
        var credential = ApiKeyCredential("/exchange/not-json");
        var outcome = await _service.AcquireAsync(credential);

        Assert.False(outcome.Success);
        Assert.Equal("invalid_response", outcome.Error);
        Assert.True(credential.NeedsReconnect);
    }

    [Fact]
    public async Task ARefusalCarriesTheStatusAndTheBody()
    {
        var credential = ApiKeyCredential("/exchange/refused");
        var outcome = await _service.AcquireAsync(credential);

        Assert.False(outcome.Success);
        Assert.Equal("http_401", outcome.Error);
        Assert.Contains("invalid key", outcome.ErrorDescription);
        Assert.True(credential.NeedsReconnect);
    }

    [Fact]
    public async Task MissingConfigurationIsCaughtBeforeAnyRequestIsSent()
    {
        var credential = new CredentialRecord
        {
            Name = "test", Kind = CredentialKind.TokenExchange,
            ExchangeMode = TokenExchangeMode.ApiKey,
            ExchangeEndpoint = null,
            ExchangeApiKey = Key,
        };

        var outcome = await _service.AcquireAsync(credential);

        Assert.False(outcome.Success);
        Assert.Equal("invalid_configuration", outcome.Error);
        Assert.Equal(0, _requestCount);
    }

    // ---- wired into OAuth2Service, like every other self-issuing kind ------------------------------

    [Fact]
    public async Task OAuth2ServiceDispatchesToTokenExchangeForBothAuthorizeAndRefresh()
    {
        var oAuth2Service = new OAuth2Service(
            new GoogleOAuthService(_activityLog),
            new GoogleServiceAccountService(_activityLog),
            new ClientCredentialsService(_activityLog),
            new DeviceCodeService(_activityLog),
            _service,
            _activityLog);

        var credential = ApiKeyCredential();

        var authorized = await oAuth2Service.StartAuthorizationAsync(credential);
        Assert.True(authorized.Success);
        Assert.Equal("MINTED", credential.Token!.AccessToken);

        var refreshed = await oAuth2Service.RefreshAsync(credential);
        Assert.NotNull(refreshed);
        Assert.Equal(2, _requestCount);
    }
}
