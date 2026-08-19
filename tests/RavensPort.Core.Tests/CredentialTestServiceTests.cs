using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RavensPort.Core.Auth;
using RavensPort.Core.Diagnostics;
using RavensPort.Core.Models;
using RavensPort.Core.Storage;
using RavensPort.Core.Vault;

namespace RavensPort.Core.Tests;

/// <summary>
/// The credential test button, against a real endpoint that actually checks what it was sent.
///
/// A mock would prove nothing here: the entire value of this feature is that it distinguishes a
/// key the upstream accepts from one it does not, and that distinction only exists on the other
/// end of a real request. The fake API below answers 200 only for the exact secret in the exact
/// place, 401 otherwise, and offers a redirect endpoint — the case a naive implementation
/// reports as success.
/// </summary>
public class CredentialTestServiceTests : IAsyncLifetime
{
    private const string Key = "GOOD-API-KEY";

    private readonly string _logPath = Path.Combine(Path.GetTempPath(), $"ravensport-credtest-logs-{Guid.NewGuid()}");

    private WebApplication _api = null!;
    private ConfigStoreCache _cache = null!;
    private CredentialTestService _service = null!;
    private string _baseUrl = "";

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        _api = builder.Build();

        _api.Run(async context =>
        {
            switch (context.Request.Path.Value)
            {
                // Accepts the key in a bespoke header, the shape most key-based APIs document.
                case "/header":
                    context.Response.StatusCode = context.Request.Headers["X-Api-Key"] == Key ? 200 : 401;
                    break;

                // Accepts a Bearer token, the OAuth shape.
                case "/bearer":
                    context.Response.StatusCode = context.Request.Headers.Authorization == $"Bearer {Key}" ? 200 : 401;
                    break;

                case "/query":
                    context.Response.StatusCode = context.Request.Query["api_key"] == Key ? 200 : 401;
                    break;

                // Bounces every request to a sign-in page — the failure mode that looks like
                // success to anything that follows redirects.
                case "/redirect":
                    context.Response.StatusCode = 302;
                    context.Response.Headers.Location = "/signin";
                    break;

                case "/signin":
                    context.Response.StatusCode = 200;
                    break;

                default:
                    context.Response.StatusCode = 404;
                    break;
            }

            await context.Response.WriteAsync("");
        });

        await _api.StartAsync();

        _baseUrl = _api.Services
            .GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!
            .Addresses.First().TrimEnd('/');

        var activityLog = new ActivityLog(_logPath);
        _cache = new ConfigStoreCache(new InMemoryVault());
        await _cache.InitializeAsync();

        var oAuth2Service = new OAuth2Service(
            new GoogleOAuthService(activityLog),
            new GoogleServiceAccountService(activityLog),
            new ClientCredentialsService(activityLog),
            new DeviceCodeService(activityLog),
            activityLog);
        _service = new CredentialTestService(new AccessTokenProvider(_cache, oAuth2Service, activityLog), activityLog);
    }

    public async Task DisposeAsync()
    {
        _service.Dispose();
        await _api.StopAsync();

        try { Directory.Delete(_logPath, recursive: true); } catch { /* best effort */ }
    }

    private async Task<CredentialRecord> AddApiKeyAsync(
        string path, string apiKey = Key, CredentialPlacement placement = CredentialPlacement.Header,
        string name = "X-Api-Key", string prefix = "")
    {
        var credential = new CredentialRecord
        {
            Name = "api-key-credential",
            Kind = CredentialKind.ApiKey,
            ApiKey = apiKey,
            DefaultPlacement = placement,
            DefaultParameterName = name,
            DefaultValuePrefix = prefix,
            TestEndpoint = path.Length == 0 ? null : _baseUrl + path,
        };

        await _cache.MutateAsync(store => store.Credentials.Add(credential));
        return credential;
    }

    [Fact]
    public async Task AGoodKeyInAHeaderPasses()
    {
        var result = await _service.TestAsync(await AddApiKeyAsync("/header"));

        Assert.True(result.Success, result.Message);
        Assert.Equal(200, result.StatusCode);
        Assert.Contains("works", result.Message);
    }

    [Fact]
    public async Task AWrongKeyFailsWithTheUpstreamsStatus()
    {
        // The whole point of the feature: a typo is caught here rather than surfacing hours
        // later as a 401 that reads like an upstream problem.
        var result = await _service.TestAsync(await AddApiKeyAsync("/header", apiKey: "WRONG"));

        Assert.False(result.Success);
        Assert.Equal(401, result.StatusCode);
        Assert.Contains("rejected", result.Message);
    }

    [Fact]
    public async Task AGoodKeyInTheWrongPlaceFails()
    {
        // The key is right, the placement is not — which is exactly as broken, and previously
        // indistinguishable from a bad key.
        var result = await _service.TestAsync(
            await AddApiKeyAsync("/header", name: "X-Wrong-Header"));

        Assert.False(result.Success);
        Assert.Equal(401, result.StatusCode);
    }

    [Fact]
    public async Task AKeyInAQueryParameterIsRefusedWithoutSendingAnything()
    {
        // Test sends a real request to a real endpoint, so it leaks whatever a proxied request
        // would. If this were the one path that still put the secret in a URL, the withdrawal
        // would be cosmetic - and the user would have been shown a green tick for a
        // configuration the proxy refuses to serve.
        var result = await _service.TestAsync(
            await AddApiKeyAsync("/query", placement: CredentialPlacement.Query, name: "api_key"));

        Assert.False(result.Success);
        Assert.Null(result.StatusCode);
        Assert.Contains("not permitted", result.Message);
    }

    [Fact]
    public async Task AValuePrefixIsApplied()
    {
        var result = await _service.TestAsync(
            await AddApiKeyAsync("/bearer", name: "Authorization", prefix: "Bearer "));

        Assert.True(result.Success, result.Message);
    }

    [Fact]
    public async Task ARedirectIsNotFollowedAndCountsAsAFailure()
    {
        // /redirect bounces to /signin, which answers 200. Following it would report a sign-in
        // page as proof the credential works — precisely the failure this is meant to catch.
        var result = await _service.TestAsync(await AddApiKeyAsync("/redirect"));

        Assert.False(result.Success);
        Assert.Equal(302, result.StatusCode);
        Assert.Contains("sign-in", result.Message);
    }

    [Fact]
    public async Task A404PointsAtTheEndpointRatherThanTheCredential()
    {
        var result = await _service.TestAsync(await AddApiKeyAsync("/nothing-here"));

        Assert.False(result.Success);
        Assert.Equal(404, result.StatusCode);
        Assert.Contains("test endpoint URL itself", result.Message);
    }

    [Fact]
    public async Task ACredentialWithNoTestEndpointSaysSo()
    {
        var result = await _service.TestAsync(await AddApiKeyAsync(""));

        Assert.False(result.Success);
        Assert.Null(result.StatusCode);
        Assert.Contains("No test endpoint", result.Message);
    }

    [Fact]
    public async Task ACredentialWithNoStoredKeySaysSo()
    {
        var credential = await AddApiKeyAsync("/header");
        credential.ApiKey = "";

        var result = await _service.TestAsync(credential);

        Assert.False(result.Success);
        Assert.Contains("no API key stored", result.Message);
    }

    [Fact]
    public async Task ABodyPlacementIsRefusedRatherThanQuietlyTestingSomethingElse()
    {
        // The test request is a GET, which has no body. Inventing one would test a request shape
        // the proxy never sends.
        var result = await _service.TestAsync(
            await AddApiKeyAsync("/header", placement: CredentialPlacement.Body, name: "api_key"));

        Assert.False(result.Success);
        Assert.Contains("Body placement cannot be tested", result.Message);
    }

    [Fact]
    public async Task APlainHttpEndpointOffLocalhostIsRefusedBeforeTheSecretIsSent()
    {
        var credential = await AddApiKeyAsync("/header");
        credential.TestEndpoint = "http://api.example.com/me"; // DevSkim: ignore DS137138

        var result = await _service.TestAsync(credential);

        Assert.False(result.Success);
        Assert.Contains("cleartext", result.Message);
    }

    [Fact]
    public async Task AnUnreachableEndpointIsNotReportedAsABadCredential()
    {
        // Reporting this as a rejected key would send someone off regenerating a key that is
        // fine. Port 1 is reserved and nothing listens there.
        var credential = await AddApiKeyAsync("/header");
        credential.TestEndpoint = "http://127.0.0.1:1/me";

        var result = await _service.TestAsync(credential);

        Assert.False(result.Success);
        Assert.Null(result.StatusCode);
        Assert.Contains("says nothing about the credential", result.Message);
    }

    [Fact]
    public async Task AnOAuthCredentialIsTestedWithItsStoredToken()
    {
        var credential = new CredentialRecord
        {
            Name = "oauth-credential",
            TestEndpoint = _baseUrl + "/bearer",
            Token = new TokenSet(Key, null, DateTimeOffset.UtcNow.AddHours(1), "Bearer", DateTimeOffset.UtcNow),
        };
        await _cache.MutateAsync(store => store.Credentials.Add(credential));

        var result = await _service.TestAsync(credential);

        Assert.True(result.Success, result.Message);
    }

    [Fact]
    public async Task AnUnconnectedOAuthCredentialSaysToAuthorizeItFirst()
    {
        var credential = new CredentialRecord { Name = "oauth-credential", TestEndpoint = _baseUrl + "/bearer" };
        await _cache.MutateAsync(store => store.Credentials.Add(credential));

        var result = await _service.TestAsync(credential);

        Assert.False(result.Success);
        Assert.Contains("not connected", result.Message);
    }

    [Fact]
    public async Task TheKeyIsNeverPutInTheResultMessage()
    {
        // The message goes straight into the UI footer and the activity log, both of which are
        // read (and screenshotted) far more casually than the encrypted store.
        foreach (var result in new[]
                 {
                     await _service.TestAsync(await AddApiKeyAsync("/header")),
                     await _service.TestAsync(await AddApiKeyAsync("/header", apiKey: "WRONG")),
                     await _service.TestAsync(await AddApiKeyAsync(
                         "/query", placement: CredentialPlacement.Query, name: "api_key")),
                 })
        {
            Assert.DoesNotContain(Key, result.Message);
            Assert.DoesNotContain("WRONG", result.Message);
        }
    }
}
