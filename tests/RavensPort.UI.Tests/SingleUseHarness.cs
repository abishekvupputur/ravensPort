using Avalonia.Controls;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
    /// <summary>
    /// The proxy key every caller has to present. A fixture, not a credential: it is generated per
    /// harness so two runs never share one, and it never leaves this process.
    /// </summary>
    public string ApiKey { get; } = $"ui-suite-{Guid.NewGuid():n}";

    private readonly WebApplication _proxy;

    public IServiceProvider Services => _proxy.Services;

    /// <summary>Where Kestrel actually bound, port 0 having been resolved by then.</summary>
    public string BaseUrl { get; }

    private SingleUseHarness(WebApplication proxy, string baseUrl)
    {
        _proxy = proxy;
        BaseUrl = baseUrl;
    }

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

        var proxy = builder.Build();

        // Middleware in the order App.StartProxyAsync installs it: the guard first, so a caller
        // without the key is refused before anything else looks at the request; then the funnel,
        // which owns /mcp; then the routes.
        proxy.UseLocalAccessGuard();
        proxy.UseMcpFunnelGate();
        proxy.MapMcpFunnel();
        proxy.MapReverseProxy();

        await proxy.StartAsync();

        var baseUrl = proxy.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First();

        return new SingleUseHarness(proxy, baseUrl);
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

    /// <summary>An HTTP caller the guard will admit.</summary>
    public HttpClient CreateClient()
    {
        var client = new HttpClient { BaseAddress = new Uri(BaseUrl) };
        client.DefaultRequestHeaders.Add(LocalAccessGuard.ApiKeyHeaderName, ApiKey);
        return client;
    }

    /// <summary>An HTTP caller the guard will refuse, for the test that says so.</summary>
    public HttpClient CreateClientWithNoKey() => new() { BaseAddress = new Uri(BaseUrl) };

    public async ValueTask DisposeAsync()
    {
        await _proxy.StopAsync();
        await _proxy.DisposeAsync();
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
