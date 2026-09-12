using Avalonia.Controls;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using System.Text.Json;
using RavensPort.Core;
using RavensPort.Core.Auth;
using RavensPort.Core.Diagnostics;
using RavensPort.Core.Mcp;
using RavensPort.Core.Proxy;
using RavensPort.Core.Storage;
using RavensPort.Core.Vault;
using RavensPort.Platform;
using RavensPort.UI.Services;
using RavensPort.UI.ViewModels;
using RavensPort.Views;

namespace RavensPort.UI.Tests;

/// <summary>
/// One app's worth of RavensPort, started the way the "Start in single use" button starts it, with
/// its views available to drive.
///
/// This is the counterpart to SystemTestHost, and the differences are the point of the suite. That
/// one opens a real 1Password vault and needs a service-account token; this one runs on the
/// InMemoryVault the single-use button selects, so it needs nothing, touches nothing outside the
/// process and can run on a fork's pull request. What is otherwise identical is the part worth
/// testing: the same <c>AddRavensPort</c> registrations, the same guard and funnel middleware in
/// the same order, the same Kestrel.
///
/// The view models come out of the same container the app resolves them from, so a view bound here
/// is bound to the object the product would give it.
/// </summary>
internal sealed class SingleUseHarness : IAsyncDisposable
{
    private readonly WebApplication _proxy;
    private readonly WebApplication _upstream;
    private readonly List<McpClient> _mcpClients = [];

    public IServiceProvider Services => _proxy.Services;

    /// <summary>Where Kestrel actually bound, port 0 having been resolved by then.</summary>
    public string BaseUrl { get; }

    /// <summary>
    /// Where routes created by these tests point. It echoes what it was sent — headers and body —
    /// as JSON, which is the whole mechanism behind "the credential arrived": the only way to know
    /// a header was injected is to ask something on the far side what it received.
    /// </summary>
    public string UpstreamUrl { get; }

    private readonly WebApplication _auth;
    private readonly string _authUrl;

    private SingleUseHarness(
        WebApplication proxy, string baseUrl, WebApplication upstream, string upstreamUrl,
        WebApplication auth, string authUrl)
    {
        _proxy = proxy;
        BaseUrl = baseUrl;
        _upstream = upstream;
        UpstreamUrl = upstreamUrl;
        _auth = auth;
        _authUrl = authUrl;
    }

    /// <summary>What the echo upstream saw on the last request it answered.</summary>
    public sealed record Echoed(Dictionary<string, string> Headers, string Body);

    /// <summary>
    /// A token endpoint that issues one, so the client-credentials flow can be driven for real.
    ///
    /// This is the one OAuth2 grant a headless test can complete: the browser flow needs a person at
    /// a consent screen and the device flow needs one at a second device, while this is the app
    /// signing in as itself. The approval suite reaches a mock authorization server over the network
    /// for the same reason; here it is in-process, so the suite needs no secrets and no internet.
    /// </summary>
    public string TokenEndpoint => $"{_authUrl}/token";

    /// <summary>
    /// Where the device flow asks for a code. RFC 8628’s first leg, answered in-process.
    ///
    /// The poll that follows lands on the token endpoint above, which issues immediately — so the
    /// flow completes without the wait a real provider imposes while somebody types the code on
    /// another device. What is under test is the app’s half of the exchange, not its patience.
    /// </summary>
    public string DeviceAuthorizationEndpoint => $"{_authUrl}/device";

    /// <summary>How many tokens it has issued, so a refresh can be told from a cache hit.</summary>
    public int TokensIssued => _tokensIssued;

    private static int _tokensIssued;

    public static async Task<SingleUseHarness> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();

        // Port 0, unlike the app, which reads 5559 out of the vault. A developer running this suite
        // very likely has RavensPort itself running, and a fixed port would mean the suite either
        // fails or — worse — talks to their real proxy.
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        builder.Services.AddRavensPort();

        // The same six adapters App.StartHost registers. They are what the view models reach the
        // desktop through, and the Avalonia implementations work headlessly because the headless
        // platform supplies a real dispatcher and a real clipboard.
        builder.Services.AddSingleton<IUiDispatcher, AvaloniaUiDispatcher>();
        builder.Services.AddSingleton<IUiTimerFactory, AvaloniaUiTimerFactory>();
        builder.Services.AddSingleton<IClipboardService, AvaloniaClipboardService>();
        builder.Services.AddSingleton<IPlatformLauncher, RecordingLauncher>();
        builder.Services.AddSingleton<IHelloConsentPrompt, AvaloniaHelloConsentPrompt>();
        builder.Services.AddSingleton<IFileSavePicker, RecordingSavePicker>();
        builder.Services.AddSingleton<IFileOpenPicker, RecordingOpenPicker>();

        // The device flow opens the verification page itself rather than going through
        // IPlatformLauncher, so recording that launcher is not enough — without this the suite opens
        // a real browser tab per run on whatever machine it is on. DoNotOpen exists in the product
        // for exactly this, and is the only thing the app ever substitutes here.
        builder.Services.AddSingleton(sp => new DeviceCodeService(
            sp.GetRequiredService<ActivityLog>(), DeviceCodeService.DoNotOpen));

        builder.Services.AddSingleton<MainWindowViewModel>();
        builder.Services.AddSingleton<VaultStatusViewModel>();
        builder.Services.AddSingleton<SetupViewModel>();
        builder.Services.AddSingleton<CredentialsViewModel>();
        builder.Services.AddSingleton<RoutesViewModel>();
        builder.Services.AddSingleton<McpFunnelViewModel>();
        builder.Services.AddSingleton<ApiBridgeViewModel>();
        builder.Services.AddSingleton<SettingsViewModel>();
        builder.Services.AddSingleton<AppTabs>();

        // The shell itself, as App registers it. Worth having in the container rather than newed up
        // in a test: MainWindow takes all five tab view models plus the shell’s own, so resolving it
        // is what proves those six fit together the way the product wires them.
        builder.Services.AddSingleton<MainWindow>();

        var upstream = await StartEchoUpstreamAsync();
        var auth = await StartTokenEndpointAsync();

        var proxy = builder.Build();

        // Middleware in the order App.StartProxyAsync installs it: the guard first, so a caller
        // without the key is refused before anything else looks at the request; then the funnel,
        // which owns /mcp; then the routes.
        proxy.UseLocalAccessGuard();
        proxy.UseMcpFunnelGate();
        proxy.MapMcpFunnel();
        proxy.MapReverseProxy();

        await proxy.StartAsync();

        var baseUrl = AddressOf(proxy);

        return new SingleUseHarness(
            proxy, baseUrl, upstream, AddressOf(upstream), auth, AddressOf(auth));
    }

    /// <summary>
    /// Answers /token with a fresh access token, the way a client-credentials endpoint does.
    ///
    /// Each answer is distinct, which is what lets a test tell a refresh from a value the app had
    /// already cached — the two are indistinguishable from the row otherwise.
    /// </summary>
    private static async Task<WebApplication> StartTokenEndpointAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        var app = builder.Build();

        app.MapPost("/device", () => Results.Json(new
        {
            device_code = "device-code-1",
            user_code = "WDJB-MJHT",
            verification_uri = "https://example.test/activate",
            expires_in = 600,
            interval = 1,
        }));

        app.MapPost("/token", () =>
        {
            var issued = Interlocked.Increment(ref _tokensIssued);

            return Results.Json(new
            {
                access_token = $"issued-token-{issued}",
                token_type = "Bearer",
                expires_in = 3600,
            });
        });

        await app.StartAsync();
        return app;
    }

    private static string AddressOf(WebApplication app) => app.Services
        .GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();

    private static async Task<WebApplication> StartEchoUpstreamAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        var app = builder.Build();

        app.Run(async context =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync();

            var headers = context.Request.Headers
                .ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);

            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(JsonSerializer.Serialize(new { headers, body }));
        });

        await app.StartAsync();
        return app;
    }

    /// <summary>
    /// Presses "Start in single use" on a real <see cref="SetupView"/> and waits for the gate to
    /// open, exactly as a person would — no call to VaultGateService.UseSingleUse from here.
    /// </summary>
    public async Task StartSingleUseThroughTheUiAsync()
    {
        var setup = Services.GetRequiredService<SetupViewModel>();

        // What App.StartHost subscribes StartProxyAsync to. The listener is already up here, so
        // what remains of that method is the part single use needs: read the store into the cache
        // so every tab is built from it — and then tell the store which port that listener is on.
        //
        // The app has this the other way round: it reads ListenPort out of the vault and binds
        // exactly that, so the two agree by construction. This harness binds port 0 instead, because
        // a developer running the suite very likely has RavensPort itself on 5559 and a fixed port
        // would mean the tests either fail or, worse, talk to their real proxy. Writing the bound
        // port back is what keeps the setting honest — and it is not cosmetic: a funnel whose source
        // is one of this proxy’s own routes dials 127.0.0.1 at the port the *store* names, so
        // leaving it at 5559 sends the funnel somewhere else entirely and its sources come back
        // empty with nothing in the log to say why.
        setup.ReadyToStart += async () =>
        {
            var cache = Services.GetRequiredService<ConfigStoreCache>();

            await cache.InitializeAsync();
            await cache.MutateAsync(store => store.Settings.ListenPort = new Uri(BaseUrl).Port);
        };

        var window = UiDriver.Show<SetupView>(setup);

        await UiDriver.ClickAsync(window, AutomationIds.StartSingleUse);
        await UiDriver.UntilAsync(() => Services.GetRequiredService<VaultGateService>().IsSingleUse);
    }

    /// <summary>
    /// An HTTP caller the guard will admit, carrying the key the app generated for this route.
    ///
    /// Read back rather than supplied, unlike the approval suite, which creates its routes in code
    /// and can name the key. A route added through the Routes tab gets its key from the app, so a
    /// test that wants in has to present the one the user would copy out of the UI.
    /// </summary>
    public HttpClient CreateClientFor(string routeKey)
    {
        var client = new HttpClient { BaseAddress = new Uri(BaseUrl) };
        client.DefaultRequestHeaders.Add(LocalAccessGuard.ApiKeyHeaderName, routeKey);
        return client;
    }

    /// <summary>An HTTP caller the guard will refuse, for the test that says so.</summary>
    public HttpClient CreateClientWithNoKey() => new() { BaseAddress = new Uri(BaseUrl) };

    /// <summary>
    /// An MCP client speaking to one of this app’s funnels, over the same loopback listener a real
    /// client uses. The protocol version is pinned when a test wants to prove the funnel answers an
    /// older revision as well as the current one.
    /// </summary>
    public async Task<McpClient> ConnectMcpAsync(string slug, string funnelKey, string? protocolVersion = null)
    {
        var options = new HttpClientTransportOptions
        {
            Endpoint = new Uri($"{BaseUrl}{McpFunnelEndpoints.BasePath}/{slug}"),
            TransportMode = HttpTransportMode.StreamableHttp,
            ConnectionTimeout = TimeSpan.FromSeconds(30),
            AdditionalHeaders = new Dictionary<string, string> { [LocalAccessGuard.ApiKeyHeaderName] = funnelKey },
        };

        var transport = new HttpClientTransport(options, CreateClientFor(funnelKey), null, ownsHttpClient: true);

        var client = await McpClient.CreateAsync(
            transport,
            protocolVersion is null ? null : new McpClientOptions { ProtocolVersion = protocolVersion });

        _mcpClients.Add(client);
        return client;
    }

    /// <summary>Reads back what the echo upstream reported.</summary>
    public static Echoed ReadEcho(string json)
    {
        using var document = JsonDocument.Parse(json);

        var headers = document.RootElement.GetProperty("headers")
            .EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.GetString() ?? "", StringComparer.OrdinalIgnoreCase);

        return new Echoed(headers, document.RootElement.GetProperty("body").GetString() ?? "");
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _mcpClients) await client.DisposeAsync();

        await _proxy.StopAsync();
        await _proxy.DisposeAsync();

        await _upstream.StopAsync();
        await _upstream.DisposeAsync();

        await _auth.StopAsync();
        await _auth.DisposeAsync();
    }
}

/// <summary>
/// Answers the save dialog with a temp path instead of showing one.
///
/// The real picker is a modal window waiting for a person, so a test that reached it would hang
/// until the run timed out. What is under test is what gets written and where the app was told to
/// write it — both of which this keeps.
/// </summary>
/// <summary>
/// <see cref="IFileOpenPicker"/> that hands back whatever a test put in it, so an import can be
/// driven without a dialog. Cancels by default: a suite that opened a real picker would hang.
/// </summary>
internal sealed class RecordingOpenPicker : IFileOpenPicker
{
    /// <summary>What the next pick returns. Null means the user cancelled.</summary>
    public PickedFile? Next { get; set; }

    /// <summary>Every title it was asked with, in order.</summary>
    public List<string> Asked { get; } = [];

    public Task<PickedFile?> PickFileAsync(string title, string extension, string filterName)
    {
        Asked.Add(title);
        return Task.FromResult(Next);
    }
}

internal sealed class RecordingSavePicker : IFileSavePicker
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"ravensport-ui-{Guid.NewGuid():n}");

    /// <summary>Every path handed back, in order.</summary>
    public List<string> Picked { get; } = [];

    /// <summary>Set to make the next pick look like a cancelled dialog.</summary>
    public bool Cancel { get; set; }

    public Task<string?> PickSavePathAsync(
        string title, string suggestedFileName, string extension, string filterName)
    {
        if (Cancel) return Task.FromResult<string?>(null);

        Directory.CreateDirectory(_directory);

        var path = Path.Combine(_directory, suggestedFileName);
        Picked.Add(path);

        return Task.FromResult<string?>(path);
    }
}

/// <summary>
/// Records what the app would have opened in a browser instead of opening it. A test runner that
/// launched the default browser per OAuth test would be a bad neighbour on a developer machine and
/// a hang on a runner with no browser at all.
/// </summary>
internal sealed class RecordingLauncher : IPlatformLauncher
{
    public List<string> Opened { get; } = [];

    public Task OpenUriAsync(string uri)
    {
        Opened.Add(uri);
        return Task.CompletedTask;
    }

    public Task OpenPathAsync(string path)
    {
        Opened.Add(path);
        return Task.CompletedTask;
    }
}
