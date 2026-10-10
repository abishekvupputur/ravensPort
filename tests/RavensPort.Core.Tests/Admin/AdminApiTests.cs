using System.Net;
using System.Net.Http.Json;
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

namespace RavensPort.Core.Tests.Admin;

/// <summary>
/// The admin API over its real socket, against an in-memory vault. What these pin is that an edit
/// through the socket lands in the one store the proxy serves from, and is refused for the same
/// reasons the tabs refuse it.
/// </summary>
public sealed class AdminApiTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"rp-admin-{Guid.NewGuid().ToString("N")[..8]}");
    private WebApplication _host = null!;
    private AdminServer _admin = null!;
    private HttpClient _client = null!;
    private int _changes;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddRavensPort();
        builder.Services.Replace(ServiceDescriptor.Singleton<IConfigVault>(_ => new InMemoryVault()));
        builder.Services.Replace(ServiceDescriptor.Singleton(_ => new ActivityLog(Path.Combine(_root, "logs"))));
        _host = builder.Build();

        var socket = Path.Combine(_root, "admin.sock");
        _admin = await AdminServer.StartAsync(_host.Services, "test host", () => Interlocked.Increment(ref _changes), socket);
        _client = AdminChannel.CreateClient(socket);
    }

    private ConfigStoreCache Cache => _host.Services.GetRequiredService<ConfigStoreCache>();

    [Fact]
    public async Task StatusAnswersBeforeAnythingIsLoadedButEditsWait()
    {
        var status = await _client.GetFromJsonAsync<AdminStatus>("status", AdminChannel.JsonOptions);
        Assert.Equal("test host", status!.Host);

        var list = await _client.GetAsync("routes");
        Assert.Equal(HttpStatusCode.Conflict, list.StatusCode);
    }

    [Fact]
    public async Task ARouteAddedOverTheSocketIsInTheStoreWithItsOwnKey()
    {
        await Cache.InitializeAsync();

        await Post("upstreams", new AddUpstreamRequest("api", "https://api.example.com/"));
        var added = await Post("routes", new AddRouteRequest("weather", "api"));

        Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        var route = Assert.Single(Cache.Current.Routes);
        Assert.Equal("/weather", route.PathPrefix);
        Assert.Equal("https://api.example.com", Assert.Single(Cache.Current.Upstreams).BaseUrl);
        Assert.True(route.Key.IsConfigured);
        Assert.True(Cache.HasPendingChanges);
        Assert.True(_changes >= 2);

        var key = await _client.GetFromJsonAsync<KeyInfo>($"routes/{route.Id}/key", AdminChannel.JsonOptions);
        Assert.Equal(route.Key.Value, key!.Value);

        await Post($"routes/{route.Id}/key/rotate", new { });
        Assert.NotEqual(key.Value, route.Key.Value);

        await Post($"routes/{route.Id}/disable", new { });
        Assert.False(route.Enabled);

        var deleted = await _client.DeleteAsync($"routes/{route.Id}");
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        Assert.Empty(Cache.Current.Routes);
    }

    [Fact]
    public async Task EditsAreRefusedForTheSameReasonsAsInTheWindow()
    {
        await Cache.InitializeAsync();

        var plainHttp = await Post("upstreams", new AddUpstreamRequest("api", "http://api.example.com"));
        Assert.Equal(HttpStatusCode.BadRequest, plainHttp.StatusCode);
        Assert.Empty(Cache.Current.Upstreams);

        await Post("upstreams", new AddUpstreamRequest("api", "https://api.example.com"));
        await Post("routes", new AddRouteRequest("/weather", "api"));

        var duplicate = await Post("routes", new AddRouteRequest("/Weather/", "api"));
        Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
        var reply = await duplicate.Content.ReadFromJsonAsync<AdminMessage>(AdminChannel.JsonOptions);
        Assert.Contains("already exists", reply!.Message);

        var missing = await Post("routes", new AddRouteRequest("/other", "nope"));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task ACredentialCannotArriveWithAToken()
    {
        await Cache.InitializeAsync();

        var record = new CredentialRecord
        {
            Name = "github",
            Kind = CredentialKind.ApiKey,
            ApiKey = "secret-value",
            Token = new TokenSet("smuggled", null, null, "Bearer", DateTimeOffset.UtcNow),
        };

        var added = await _client.PostAsJsonAsync("credentials", record, AdminChannel.JsonOptions);

        Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        var stored = Assert.Single(Cache.Current.Credentials);
        Assert.Null(stored.Token);

        var again = await _client.PostAsJsonAsync("credentials", new CredentialRecord
        {
            Name = "GitHub", Kind = CredentialKind.ApiKey, ApiKey = "x",
        }, AdminChannel.JsonOptions);
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
    }

    [Fact]
    public async Task AFunnelPoolsASourceWithAToolSelection()
    {
        await Cache.InitializeAsync();

        await Post("sources", new AddSourceRequest("Remote tools", McpSourceKind.RemoteUrl, Url: "https://mcp.example.com/mcp"));
        await Post("funnels", new AddFunnelRequest("Coding agent"));

        var membership = await Post("funnels/coding-agent/sources",
            new FunnelSourceRequest("remotetools", McpSelectionMode.Include, ["search"]));

        Assert.Equal(HttpStatusCode.OK, membership.StatusCode);
        var funnel = Assert.Single(Cache.Current.McpFunnels);
        var link = Assert.Single(funnel.Sources);
        Assert.Equal(McpSelectionMode.Include, link.ToolMode);
        Assert.Equal(["search"], link.Tools);

        await _client.DeleteAsync("sources/remotetools");
        Assert.Empty(funnel.Sources);
    }

    private Task<HttpResponseMessage> Post<T>(string path, T body) =>
        _client.PostAsJsonAsync(path, body, AdminChannel.JsonOptions);

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _admin.DisposeAsync();
        await _host.DisposeAsync();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}
