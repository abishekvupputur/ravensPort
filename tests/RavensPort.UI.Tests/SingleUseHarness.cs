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

    private SingleUseHarness(WebApplication proxy, string baseUrl, WebApplication upstream, string upstreamUrl)
    {
        _proxy = proxy;
        BaseUrl = baseUrl;
        _upstream = upstream;
        UpstreamUrl = upstreamUrl;
    }

    /// <summary>What the echo upstream saw on the last request it answered.</summary>
    public sealed record Echoed(Dictionary<string, string> Headers, string Body);

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
        builder.Services.AddSingleton<IFileSavePicker, AvaloniaFileSavePicker>();

        builder.Services.AddSingleton<MainWindowViewModel>();
        builder.Services.AddSingleton<VaultStatusViewModel>();
        builder.Services.AddSingleton<SetupViewModel>();
        builder.Services.AddSingleton<CredentialsViewModel>();
        builder.Services.AddSingleton<RoutesViewModel>();
        builder.Services.AddSingleton<McpFunnelViewModel>();
        builder.Services.AddSingleton<SettingsViewModel>();

        var upstream = await StartEchoUpstreamAsync();

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

        return new SingleUseHarness(proxy, baseUrl, upstream, AddressOf(upstream));
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
        // so every tab is built from it.
        setup.ReadyToStart += () => Services.GetRequiredService<ConfigStoreCache>().InitializeAsync();

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
