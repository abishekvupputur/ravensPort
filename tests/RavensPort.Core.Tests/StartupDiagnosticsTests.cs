using RavensPort.Core.Diagnostics;
using RavensPort.Core.Models;
using RavensPort.Core.Proxy;
using RavensPort.Core.Storage;
using RavensPort.Core.Vault;
using Yarp.ReverseProxy.Configuration;

namespace RavensPort.Core.Tests;

/// <summary>
/// Two things can go wrong at startup with nothing said about either: the vault hands back a
/// store that is missing secrets, and a plain-http endpoint keeps putting tokens and client
/// secrets on the wire in cleartext. Both are silent failures without these.
/// </summary>
public class StartupDiagnosticsTests : IDisposable
{
    private readonly string _logPath = Path.Combine(Path.GetTempPath(), $"ravensport-test-logs-{Guid.NewGuid()}");

    [Fact]
    public async Task Startup_WhenTheVaultReportsAnIncompleteLoad_SaysSoInTheActivityLog()
    {
        // A credential whose secret item is gone loads looking perfectly healthy and then fails
        // against the upstream hours later. The load warning is the only chance to say so.
        const string Warning = "1 credential loaded without its client secret";

        var activityLog = await RunStartupAsync(InMemoryVault.Empty().WithLoadWarning(Warning));

        Assert.Contains(activityLog.GetRecent(100), line => line.Contains(Warning));
    }

    [Fact]
    public async Task Startup_WithACleanLoad_SaysNothingAboutIt()
    {
        var activityLog = await RunStartupAsync(InMemoryVault.Empty());

        Assert.DoesNotContain(activityLog.GetRecent(100), line => line.Contains("loaded without"));
    }

    [Fact]
    public async Task Startup_WithAPlainHttpUpstream_WarnsThatTheTokenWouldTravelInCleartext()
    {
        // Validation runs when a record is added, but the vault can also be edited directly in
        // the password manager, which bypasses it entirely.
        var store = new ConfigStore();
        store.Upstreams.Add(new UpstreamRecord { Name = "insecure", BaseUrl = "http://api.example.com" }); // DevSkim: ignore DS137138

        var activityLog = await RunStartupAsync(InMemoryVault.Empty().Seeded(store));

        Assert.Contains(activityLog.GetRecent(100), line =>
            line.Contains("STARTUP WARNING") && line.Contains("insecure") && line.Contains("cleartext"));
    }

    [Fact]
    public async Task Startup_WithAPlainHttpTokenEndpoint_WarnsAboutTheCredential()
    {
        var store = new ConfigStore();
        store.Credentials.Add(new CredentialRecord
        {
            Name = "insecure-credential",
            ClientId = "id",
            ClientSecret = "secret",
            TokenEndpoint = "http://idp.example.com/token", // DevSkim: ignore DS137138
        });

        var activityLog = await RunStartupAsync(InMemoryVault.Empty().Seeded(store));

        Assert.Contains(activityLog.GetRecent(100), line =>
            line.Contains("STARTUP WARNING") && line.Contains("insecure-credential"));
    }

    [Fact]
    public async Task Startup_WithHttpsEndpointsAndLoopbackUpstreams_WarnsAboutNothing()
    {
        var store = new ConfigStore();
        store.Upstreams.Add(new UpstreamRecord { Name = "secure", BaseUrl = "https://api.example.com" });

        // Plain http is fine for a local development upstream - it never leaves the machine.
        store.Upstreams.Add(new UpstreamRecord { Name = "local-dev", BaseUrl = "http://127.0.0.1:8080" });

        var activityLog = await RunStartupAsync(InMemoryVault.Empty().Seeded(store));

        Assert.DoesNotContain(activityLog.GetRecent(100), line => line.Contains("STARTUP WARNING"));
    }

    private async Task<ActivityLog> RunStartupAsync(IConfigVault vault)
    {
        var activityLog = new ActivityLog(_logPath);
        var cache = new ConfigStoreCache(vault);
        var notifier = new ProxyConfigChangeNotifier(cache, new InMemoryConfigProvider([], []), activityLog);

        await new ConfigStoreInitializerHostedService(cache, vault, notifier, activityLog)
            .StartAsync(CancellationToken.None);

        return activityLog;
    }

    public void Dispose()
    {
        try { Directory.Delete(_logPath, recursive: true); } catch { /* best effort */ }
    }
}
