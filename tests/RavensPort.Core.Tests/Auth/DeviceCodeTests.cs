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
/// The device authorization grant, against a provider that makes it wait.
///
/// Waiting is the whole flow, and it is expressed entirely through error codes: a pending
/// authorization is reported as an <em>error</em>, so a poller that read errors as failures would
/// give up on the first attempt every time — which is also the attempt that can never succeed,
/// since the user has not seen the code yet. The stub below answers
/// <c>authorization_pending</c> and <c>slow_down</c> before approving, so the loop has to
/// interpret both correctly to get anywhere.
/// </summary>
public class DeviceCodeTests : IAsyncLifetime
{
    private const string ClientId = "device-app";
    private const string UserCode = "WDJB-MJHT";
    private const string DeviceCode = "dev-code-abc";

    private readonly string _logPath = Path.Combine(Path.GetTempPath(), $"ravensport-device-logs-{Guid.NewGuid()}");

    private WebApplication _api = null!;
    private ActivityLog _activityLog = null!;
    private DeviceCodeService _service = null!;
    private string _baseUrl = "";

    /// <summary>How many times the token endpoint has been polled, so pacing can be asserted.</summary>
    private int _polls;

    /// <summary>Error codes the token endpoint returns, in order, before it approves.</summary>
    private readonly Queue<string> _pollScript = new();

    /// <summary>What the last device authorization request carried.</summary>
    private IFormCollection _lastDeviceForm = new FormCollection([]);
    private string? _lastDeviceAuthorization;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        _api = builder.Build();

        _api.Run(async context =>
        {
            var form = context.Request.HasFormContentType
                ? await context.Request.ReadFormAsync()
                : new FormCollection([]);

            context.Response.ContentType = "application/json";

            switch (context.Request.Path.Value)
            {
                case "/device":
                    _lastDeviceForm = form;
                    _lastDeviceAuthorization = context.Request.Headers.Authorization;

                    if (form["client_id"] != ClientId && string.IsNullOrEmpty(_lastDeviceAuthorization))
                    {
                        context.Response.StatusCode = 400;
                        await context.Response.WriteAsync("""{"error":"invalid_client"}""");
                        return;
                    }

                    // Interval of 1 keeps the test quick while still exercising the pacing;
                    // verification_uri_complete is offered because most real providers do.
                    await context.Response.WriteAsync($$"""
                        {
                          "device_code": "{{DeviceCode}}",
                          "user_code": "{{UserCode}}",
                          "verification_uri": "{{_baseUrl}}/activate",
                          "verification_uri_complete": "{{_baseUrl}}/activate?user_code={{UserCode}}",
                          "expires_in": 600,
                          "interval": 1
                        }
                        """);
                    return;

                // A provider answering with something the shell would resolve to a local file rather
                // than a page. Real providers do not do this; a mistyped or tampered endpoint can.
                case "/device-file-uri":
                    await context.Response.WriteAsync($$"""
                        {
                          "device_code": "{{DeviceCode}}",
                          "user_code": "{{UserCode}}",
                          "verification_uri": "file:///C:/Windows/System32/calc.exe",
                          "expires_in": 600,
                          "interval": 1
                        }
                        """);
                    return;

                case "/device/no-flow":
                    context.Response.StatusCode = 400;
                    await context.Response.WriteAsync(
                        """{"error":"unauthorized_client","error_description":"device flow is not enabled for this app"}""");
                    return;

                case "/token":
                    _polls++;

                    if (form["device_code"] != DeviceCode)
                    {
                        context.Response.StatusCode = 400;
                        await context.Response.WriteAsync("""{"error":"invalid_grant"}""");
                        return;
                    }

                    if (_pollScript.Count > 0)
                    {
                        // RFC 8628 §3.5 has these arrive as ordinary 400s carrying an error code.
                        context.Response.StatusCode = 400;
                        await context.Response.WriteAsync($$"""{"error":"{{_pollScript.Dequeue()}}"}""");
                        return;
                    }

                    await context.Response.WriteAsync(
                        """{"access_token":"APPROVED","refresh_token":"REFRESH","token_type":"Bearer","expires_in":3600}""");
                    return;

                case "/token/refresh":
                    await context.Response.WriteAsync(form["refresh_token"] == "REFRESH"
                        ? """{"access_token":"REFRESHED","token_type":"Bearer","expires_in":3600}"""
                        : """{"error":"invalid_grant"}""");
                    return;

                default:
                    context.Response.StatusCode = 404;
                    await context.Response.WriteAsync("{}");
                    return;
            }
        });

        await _api.StartAsync();

        _baseUrl = _api.Services
            .GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!
            .Addresses.First().TrimEnd('/');

        _activityLog = new ActivityLog(_logPath);
        // DoNotOpen, or every authorization in this class launches a browser: these tests run the
        // real flow against the stub above, and the service opens the verification page as a
        // convenience. On CI that is a window per test on the agent.
        _service = new DeviceCodeService(_activityLog, DeviceCodeService.DoNotOpen);
    }

    public async Task DisposeAsync()
    {
        _service.Dispose();
        await _api.StopAsync();

        try { Directory.Delete(_logPath, recursive: true); } catch { /* best effort */ }
    }

    private CredentialRecord NewCredential(string devicePath = "/device", string tokenPath = "/token") => new()
    {
        Name = "device-login",
        Kind = CredentialKind.DeviceCode,
        ClientId = ClientId,
        DeviceAuthorizationEndpoint = _baseUrl + devicePath,
        TokenEndpoint = _baseUrl + tokenPath,
        Scopes = ["read:user"],
    };

    // ---- The happy path -------------------------------------------------------------------------

    [Fact]
    public async Task ThePendingAnswerIsWaitedOutRatherThanTreatedAsFailure()
    {
        // The first poll can never succeed — the user has not been shown the code yet — so a
        // poller that gave up on the first error would fail every single sign-in.
        _pollScript.Enqueue("authorization_pending");
        _pollScript.Enqueue("authorization_pending");

        var credential = NewCredential();
        var outcome = await _service.AuthorizeAsync(credential, prompt: null);

        Assert.True(outcome.Success, outcome.ErrorDescription);
        Assert.Equal("APPROVED", credential.Token!.AccessToken);
        Assert.Equal("REFRESH", credential.Token.RefreshToken);
        Assert.Equal(3, _polls);
        Assert.False(credential.NeedsReconnect);
    }

    [Fact]
    public async Task TheCodeIsReportedBeforeTheFlowFinishes()
    {
        // The flow only finishes because the user acted on the code, so reporting it at the end
        // would be reporting it too late to be of any use.
        _pollScript.Enqueue("authorization_pending");

        DeviceCodePrompt? reported = null;
        var wasReportedBeforeCompletion = false;

        var prompt = new Progress<DeviceCodePrompt>(p =>
        {
            reported = p;
            wasReportedBeforeCompletion = true;
        });

        var credential = NewCredential();
        var outcome = await _service.AuthorizeAsync(credential, prompt);

        Assert.True(outcome.Success, outcome.ErrorDescription);
        Assert.True(wasReportedBeforeCompletion);
        Assert.NotNull(reported);
        Assert.Equal(UserCode, reported!.UserCode);
        Assert.Equal(_baseUrl + "/activate", reported.VerificationUri);
        Assert.Contains("user_code=" + UserCode, reported.VerificationUriComplete);
        Assert.InRange(reported.ExpiresAtUtc, DateTimeOffset.UtcNow.AddMinutes(8), DateTimeOffset.UtcNow.AddMinutes(11));
    }

    [Fact]
    public async Task SlowDownLengthensTheIntervalInsteadOfFailing()
    {
        // Ignoring slow_down is how a provider stops answering altogether, and it is reported
        // exactly like a fatal error, so the difference has to be read from the code itself.
        _pollScript.Enqueue("slow_down");

        var started = DateTimeOffset.UtcNow;
        var credential = NewCredential();
        var outcome = await _service.AuthorizeAsync(credential, prompt: null);

        Assert.True(outcome.Success, outcome.ErrorDescription);

        // One second for the scripted interval, then six for the mandated five-second increment.
        Assert.True(DateTimeOffset.UtcNow - started >= TimeSpan.FromSeconds(6),
            "the interval must grow by five seconds, or the provider is entitled to stop answering");
    }

    [Fact]
    public async Task TheUserCodeIsLoggedButTheDeviceCodeIsNot()
    {
        // The device code is the bearer-equivalent half of this exchange: anything holding it can
        // collect the token. The user code is on screen anyway and useless on its own.
        var credential = NewCredential();
        await _service.AuthorizeAsync(credential, prompt: null);

        var log = string.Join('\n', _activityLog.GetRecent(200));

        Assert.Contains(UserCode, log);
        Assert.DoesNotContain(DeviceCode, log);
    }

    [Fact]
    public async Task TheVerificationPageIsOpenedThroughTheInjectedOpenerAndNothingElse()
    {
        // The seam that keeps CI quiet. Without it AuthorizeAsync shells out to the real browser,
        // and every test in this class that runs the flow -- most of them -- opens a window on the
        // agent. Asserting the opener is *called* rather than merely absent is what stops the fix
        // from being a silently dead convenience: production still opens the page.
        var opened = new List<Uri>();

        using var service = new DeviceCodeService(_activityLog, opened.Add);

        var credential = NewCredential();
        var outcome = await service.AuthorizeAsync(credential, prompt: null);

        Assert.True(outcome.Success, outcome.ErrorDescription);

        // The complete URI, not the bare one: it carries the user code, which is the whole point of
        // opening a page rather than making someone type it.
        var launched = Assert.Single(opened);
        Assert.Equal(_baseUrl + "/activate", launched.GetLeftPart(UriPartial.Path));
        Assert.Contains("user_code=" + UserCode, launched.Query);
    }

    [Fact]
    public async Task ANonHttpVerificationUrlIsNotHandedToTheOpener()
    {
        // The endpoint comes from user-editable configuration and the default opener is a shell
        // execute, which resolves whatever it is given -- a protocol handler, a UNC path, an
        // executable. The scheme check has to happen before the opener sees it, so a recording
        // opener is the only way to tell "refused" from "opened something dangerous".
        var opened = new List<Uri>();

        using var service = new DeviceCodeService(_activityLog, opened.Add);

        var credential = NewCredential(devicePath: "/device-file-uri");
        await service.AuthorizeAsync(credential, prompt: null);

        Assert.Empty(opened);
        Assert.Contains(
            _activityLog.GetRecent(200),
            line => line.Contains("not opening it", StringComparison.OrdinalIgnoreCase));
    }

    // ---- Refusals -------------------------------------------------------------------------------

    [Fact]
    public async Task ADeclinedRequestStopsRatherThanPollingOn()
    {
        _pollScript.Enqueue("access_denied");

        var credential = NewCredential();
        var outcome = await _service.AuthorizeAsync(credential, prompt: null);

        Assert.False(outcome.Success);
        Assert.Equal("access_denied", outcome.Error);
        Assert.Equal(1, _polls);
        Assert.Null(credential.Token);
    }

    [Fact]
    public async Task AnExpiredCodeSaysToStartAgain()
    {
        _pollScript.Enqueue("expired_token");

        var outcome = await _service.AuthorizeAsync(NewCredential(), prompt: null);

        Assert.False(outcome.Success);
        Assert.Equal("expired_token", outcome.Error);
        Assert.Contains("Start again", outcome.ErrorDescription);
    }

    [Fact]
    public async Task AProviderRefusingToIssueACodeReportsItsOwnReason()
    {
        // GitHub answers exactly this when Device Flow has not been ticked on in the OAuth App,
        // which is off by default and therefore the likeliest first-run failure.
        var outcome = await _service.AuthorizeAsync(NewCredential(devicePath: "/device/no-flow"), prompt: null);

        Assert.False(outcome.Success);
        Assert.Equal("unauthorized_client", outcome.Error);
        Assert.Contains("device flow is not enabled", outcome.ErrorDescription);
        Assert.Equal(0, _polls);
    }

    [Fact]
    public async Task MissingConfigurationIsCaughtBeforeAnyRequestIsSent()
    {
        var credential = NewCredential();
        credential.DeviceAuthorizationEndpoint = null;

        var outcome = await _service.AuthorizeAsync(credential, prompt: null);

        Assert.False(outcome.Success);
        Assert.Equal("invalid_configuration", outcome.Error);
        Assert.Contains("Device authorization endpoint", outcome.ErrorDescription);
    }

    [Fact]
    public async Task CancellingStopsTheWait()
    {
        // The poll loop runs for as long as the code lives — ten minutes here — so the caller
        // must be able to end it without waiting that out.
        _pollScript.Enqueue("authorization_pending");
        _pollScript.Enqueue("authorization_pending");
        _pollScript.Enqueue("authorization_pending");

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var outcome = await _service.AuthorizeAsync(NewCredential(), prompt: null, cts.Token);

        Assert.False(outcome.Success);
        Assert.Equal("cancelled", outcome.Error);
    }

    // ---- A public client identifies itself in the body ------------------------------------------

    [Fact]
    public async Task APublicClientSendsItsIdInTheBodyAndNoBasicHeader()
    {
        // RFC 8628 exists for clients that cannot hold a secret. A Basic header built from an
        // empty one is a different, wrong assertion, and providers reject it as invalid_client
        // rather than ignoring it.
        var credential = NewCredential();
        credential.ClientSecret = "";

        Assert.True((await _service.AuthorizeAsync(credential, prompt: null)).Success);

        Assert.Equal(ClientId, _lastDeviceForm["client_id"]);
        Assert.True(string.IsNullOrEmpty(_lastDeviceAuthorization));
    }

    [Fact]
    public async Task AConfidentialClientStillSendsItsSecret()
    {
        var credential = NewCredential();
        credential.ClientSecret = "s3cret";

        Assert.True((await _service.AuthorizeAsync(credential, prompt: null)).Success);

        Assert.StartsWith("Basic ", _lastDeviceAuthorization);
    }

    [Fact]
    public async Task ScopesAndExtraParametersReachTheDeviceRequest()
    {
        var credential = NewCredential();
        credential.ExtraAuthParams = "audience=https%3A%2F%2Fapi.example.com%2F";

        Assert.True((await _service.AuthorizeAsync(credential, prompt: null)).Success);

        Assert.Equal("read:user", _lastDeviceForm["scope"]);
        Assert.Equal("https://api.example.com/", _lastDeviceForm["audience"]);
    }

    // ---- Afterwards it is an ordinary grant -----------------------------------------------------

    [Fact]
    public async Task ItRefreshesLikeAnyOtherGrant()
    {
        // The device flow is only how the grant was first approved; the provider does not care
        // afterwards, and this must renew in the background like everything else.
        var credential = NewCredential(tokenPath: "/token/refresh");
        credential.Token = new TokenSet("OLD", "REFRESH", DateTimeOffset.UtcNow.AddMinutes(1), "Bearer", DateTimeOffset.UtcNow);

        var refreshed = await _service.RefreshAsync(credential);

        Assert.NotNull(refreshed);
        Assert.Equal("REFRESHED", refreshed!.AccessToken);

        // The provider sent no new refresh token, which is the common case — keeping the old one
        // is what lets the next refresh work at all.
        Assert.Equal("REFRESH", refreshed.RefreshToken);
        Assert.False(credential.NeedsReconnect);
    }

    [Fact]
    public async Task ARefusedRefreshAsksForAReconnect()
    {
        var credential = NewCredential(tokenPath: "/token/refresh");
        credential.Token = new TokenSet("OLD", "STALE", DateTimeOffset.UtcNow.AddMinutes(1), "Bearer", DateTimeOffset.UtcNow);

        Assert.Null(await _service.RefreshAsync(credential));
        Assert.True(credential.NeedsReconnect);
    }

    [Fact]
    public void ADeviceCredentialCountsAsAUserLoginRatherThanAnAppLogin()
    {
        // It needs a person and holds a refresh token, so it must not be swept into the
        // re-mint-from-a-stored-secret path that has neither.
        var credential = NewCredential();

        Assert.True(credential.IsInteractiveOAuth);
        Assert.False(credential.IsSelfIssuing);
    }
}
