using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using RavensPort.Core.Admin;
using RavensPort.Core.Diagnostics;
using RavensPort.Core.Models;
using RavensPort.Core.Proxy;
using RavensPort.Core.Storage;
using RavensPort.Core.Vault;

namespace RavensPort.Cli.Tests;

/// <summary>
/// The commands as they ship — the same command tree Main builds — run against a real admin socket
/// over an in-memory vault. What matters most is where secrets come from: stdin, never argv.
/// </summary>
public sealed class CliTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"rp-cli-{Guid.NewGuid().ToString("N")[..8]}");
    private WebApplication _host = null!;
    private AdminServer _admin = null!;

    private string Socket => Path.Combine(_root, "admin.sock");

    private ConfigStoreCache Cache => _host.Services.GetRequiredService<ConfigStoreCache>();

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddRavensPort();
        builder.Services.Replace(ServiceDescriptor.Singleton<IConfigVault>(_ => new InMemoryVault()));
        builder.Services.Replace(ServiceDescriptor.Singleton(_ => new ActivityLog(Path.Combine(_root, "logs"))));
        _host = builder.Build();

        await _host.Services.GetRequiredService<ConfigStoreCache>().InitializeAsync();
        _admin = await AdminServer.StartAsync(_host.Services, "test host", socketPath: Socket);
    }

    [Fact]
    public async Task ASecretOnTheCommandLineIsRefusedBeforeAnythingIsSent()
    {
        var (code, _, error) = await RunAsync("credentials", "add", "gh", "--kind", "api-key", "--api-key", "leaked");

        Assert.NotEqual(0, code);
        Assert.Contains("not accepted as arguments", error);
        Assert.Empty(Cache.Current.Credentials);
    }

    [Fact]
    public async Task AnApiKeyIsReadFromStdinAndStoredWithTheKindsDefaults()
    {
        var prompts = new List<string>();
        SecretInput.Reader = prompt =>
        {
            prompts.Add(prompt);
            return "from-stdin";
        };

        var (code, output, _) = await RunAsync("credentials", "add", "github", "--kind", "apikey");

        Assert.Equal(0, code);
        Assert.Contains("added", output);
        Assert.Single(prompts);

        var stored = Assert.Single(Cache.Current.Credentials);
        Assert.Equal(CredentialKind.ApiKey, stored.Kind);
        Assert.Equal("from-stdin", stored.ApiKey);
        Assert.Equal("X-Api-Key", stored.DefaultParameterName);
    }

    [Fact]
    public async Task ARouteIsAddressedByItsPrefix()
    {
        Assert.Equal(0, (await RunAsync("upstreams", "add", "api", "https://api.example.com")).Code);
        Assert.Equal(0, (await RunAsync("routes", "add", "/weather", "--upstream", "api", "--key-days", "7")).Code);

        var route = Assert.Single(Cache.Current.Routes);
        Assert.NotNull(route.Key.ExpiresUtc);

        var (code, output, _) = await RunAsync("routes", "key", "show", "/weather");
        Assert.Equal(0, code);
        Assert.Equal(route.Key.Value, output.Trim());

        Assert.Equal(0, (await RunAsync("routes", "disable", "weather")).Code);
        Assert.False(route.Enabled);
    }

    [Fact]
    public async Task ARefusalExitsNonZeroWithTheServersReason()
    {
        var (code, _, error) = await RunAsync("upstreams", "add", "api", "http://plain.example.com");

        Assert.Equal(ExitCodes.Refused, code);
        Assert.StartsWith("error:", error.Trim());
    }

    [Fact]
    public async Task NobodyListeningSaysHowToStartOne()
    {
        var (code, _, error) = await RunAsync(["status", "--socket", Path.Combine(_root, "nobody.sock")], useDefaultSocket: false);

        Assert.Equal(ExitCodes.NotRunning, code);
        Assert.Contains("ravensport-cli serve", error);
    }

    [Fact]
    public async Task StatusDescribesTheHost()
    {
        var (code, output, _) = await RunAsync("status");

        Assert.Equal(0, code);
        Assert.Contains("test host", output);
    }

    private Task<(int Code, string Output, string Error)> RunAsync(params string[] args) => RunAsync(args, useDefaultSocket: true);

    private async Task<(int Code, string Output, string Error)> RunAsync(string[] args, bool useDefaultSocket)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        string[] full = useDefaultSocket ? [.. args, "--socket", Socket] : args;

        var code = await Cli.Build(output, error).Parse(full).InvokeAsync(new System.CommandLine.InvocationConfiguration
        {
            Output = output,
            Error = error,
        });

        return (code, output.ToString(), error.ToString());
    }

    public async Task DisposeAsync()
    {
        await _admin.DisposeAsync();
        await _host.DisposeAsync();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}
