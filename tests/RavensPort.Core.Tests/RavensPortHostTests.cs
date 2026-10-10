using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using RavensPort.Core.Auth;
using RavensPort.Core.Diagnostics;
using RavensPort.Core.Mcp;
using RavensPort.Core.Proxy;
using RavensPort.Core.Storage;
using RavensPort.Core.Vault;

namespace RavensPort.Core.Tests;

/// <summary>
/// The startup both hosts share — the desktop app and `ravensport serve`. What matters is that it
/// binds loopback on the stored port, puts the guard in front of everything, and settles mTLS from
/// the store the way the log says it did.
/// </summary>
public sealed class RavensPortHostTests : IAsyncDisposable
{
    private readonly string _logs = Path.Combine(Path.GetTempPath(), $"rp-host-{Guid.NewGuid():N}");
    private WebApplication? _app;

    [Fact]
    public async Task TheSharedPipelineListensOnLoopbackAndGuardsEveryRequest()
    {
        var app = await BuildAsync();
        var port = FreePort();
        await Cache(app).MutateAsync(s => s.Settings.ListenPort = port);

        RavensPortHost.ApplyMtlsDecision(app.Services);
        Assert.Equal(port, app.UseStoredListenUrl());
        Assert.Equal($"http://127.0.0.1:{port}", Assert.Single(app.Urls));

        app.MapRavensPortPipeline();
        await app.StartAsync();

        using var client = new HttpClient();
        var response = await client.GetAsync($"http://127.0.0.1:{port}/anything");

        // No route and no key: the guard answers before anything could be forwarded.
        Assert.False(response.IsSuccessStatusCode);
        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task MtlsSwitchedOnWithNoCertificateFallsBackToHttpAndSaysSo()
    {
        var app = await BuildAsync();
        await Cache(app).MutateAsync(s => s.Settings.MtlsEnabled = true);

        RavensPortHost.ApplyMtlsDecision(app.Services);

        var state = app.Services.GetRequiredService<KestrelMtlsState>();
        Assert.False(state.IsEnabled);
        Assert.Equal("http", state.Scheme);
        Assert.Contains(app.Services.GetRequiredService<ActivityLog>().GetRecent(20),
            line => line.Contains("no client certificate this build can open", StringComparison.Ordinal));
    }

#if !STORE_BUILD
    [Fact]
    public async Task MtlsWithAStoredCertificateServesHttps()
    {
        var app = await BuildAsync();
        var pfx = MtlsCertificateFactory.GenerateClientCertificatePfx("host-test");
        await Cache(app).MutateAsync(s =>
        {
            s.Settings.MtlsEnabled = true;
            s.Settings.MtlsClientCertificatePfx = pfx;
            s.Settings.MtlsClientCertificatePassword = "host-test";
        });

        RavensPortHost.ApplyMtlsDecision(app.Services);
        app.UseStoredListenUrl();

        Assert.True(app.Services.GetRequiredService<KestrelMtlsState>().IsEnabled);
        Assert.StartsWith("https://127.0.0.1:", Assert.Single(app.Urls));
        Assert.Contains(app.Services.GetRequiredService<ActivityLog>().GetRecent(20),
            line => line.Contains("mTLS enabled", StringComparison.Ordinal));
    }
#endif

    [Fact]
    public void BrowserLauncherSendsUrlsWhereverTheCurrentFlowSays()
    {
        var opened = new List<Uri>();

        using (BrowserLauncher.Redirect(opened.Add))
        {
            BrowserLauncher.Open(new Uri("https://example.com/authorize"));

            // Nested, as a second flow inside the first would be; the outer one comes back after.
            var inner = new List<Uri>();
            using (BrowserLauncher.Redirect(inner.Add))
            {
                BrowserLauncher.Open(new Uri("https://example.com/inner"));
            }

            BrowserLauncher.Open(new Uri("https://example.com/again"));
            Assert.Single(inner);
        }

        Assert.Equal(["https://example.com/authorize", "https://example.com/again"], opened.Select(u => u.AbsoluteUri));
    }

    private async Task<WebApplication> BuildAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.ConfigureRavensPortKestrel();
        builder.Services.AddRavensPort();
        builder.Services.Replace(ServiceDescriptor.Singleton<IConfigVault>(_ => new InMemoryVault()));
        builder.Services.Replace(ServiceDescriptor.Singleton(_ => new ActivityLog(_logs)));

        _app = builder.Build();
        await Cache(_app).InitializeAsync();
        return _app;
    }

    private static ConfigStoreCache Cache(WebApplication app) => app.Services.GetRequiredService<ConfigStoreCache>();

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null) await _app.DisposeAsync();
        try { Directory.Delete(_logs, recursive: true); } catch { /* best effort */ }
    }
}
