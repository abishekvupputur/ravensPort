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
using RavensPort.Core.Tests.Storage;
using RavensPort.Core.Vault;

namespace RavensPort.Core.Tests.Admin;

/// <summary>
/// The rest of the admin surface: settings and certificates, bridges, sign-in streaming, reload
/// and the cascades. Each test drives one area end to end over the real socket.
///
/// In the manifest-store collection because bridges write their manifest to disk, and the store's
/// redirect away from the real profile is a static.
/// </summary>
[Collection(ManifestStoreCollection.Name)]
public sealed class AdminEndpointCoverageTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"rp-admin2-{Guid.NewGuid().ToString("N")[..8]}");
    private WebApplication _host = null!;
    private AdminServer _admin = null!;
    private HttpClient _client = null!;

    // Closed on purpose: nothing listens on port 1, so anything dialling it fails at once.
    private const string Unreachable = "https://127.0.0.1:1";

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        ManifestLocalStore.RootOverride = Path.Combine(_root, "manifests");

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddRavensPort();
        builder.Services.Replace(ServiceDescriptor.Singleton<IConfigVault>(_ => new InMemoryVault()));
        builder.Services.Replace(ServiceDescriptor.Singleton(_ => new ActivityLog(Path.Combine(_root, "logs"))));
        _host = builder.Build();

        await Cache.InitializeAsync();

        var socket = Path.Combine(_root, "admin.sock");
        _admin = await AdminServer.StartAsync(_host.Services, "coverage host", socketPath: socket);
        _client = AdminChannel.CreateClient(socket);
    }

    private ConfigStoreCache Cache => _host.Services.GetRequiredService<ConfigStoreCache>();

    [Fact]
    public async Task SettingsAreReadValidatedAndChanged()
    {
        var settings = await Get<SettingsReply>("settings");
        Assert.False(settings.HasCertificate);

        Assert.Equal(HttpStatusCode.BadRequest, (await Patch(new SettingsPatch(ListenPort: 70000))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Patch(new SettingsPatch(MtlsEnabled: true))).StatusCode);

        var changed = await Patch(new SettingsPatch(ListenPort: 6001, McpFunnelEnabled: true, McpApiBridgeEnabled: true));
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.Equal(6001, Cache.Current.Settings.ListenPort);
        Assert.True(Cache.Current.Settings.McpFunnelEnabled);
        Assert.True(Cache.Current.Settings.McpApiBridgeEnabled);

        var nothing = await (await Patch(new SettingsPatch())).Content.ReadFromJsonAsync<AdminMessage>(AdminChannel.JsonOptions);
        Assert.Equal("Nothing to change.", nothing!.Message);
    }

#if !STORE_BUILD
    [Fact]
    public async Task ACertificateCanBeGeneratedAndThenMtlsSwitchedOn()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await Post("settings/mtls/generate", new GenerateCertificateRequest(""))).StatusCode);

        var generated = await Post("settings/mtls/generate", new GenerateCertificateRequest("pfx-password"));
        var reply = await generated.Content.ReadFromJsonAsync<GenerateCertificateReply>(AdminChannel.JsonOptions);

        Assert.False(string.IsNullOrEmpty(reply!.PfxBase64));
        Assert.Equal(reply.PfxBase64, Cache.Current.Settings.MtlsClientCertificatePfx);

        Assert.Equal(HttpStatusCode.OK, (await Patch(new SettingsPatch(MtlsEnabled: true))).StatusCode);
        Assert.True(Cache.Current.Settings.MtlsEnabled);
        Assert.True((await Get<SettingsReply>("settings")).HasCertificate);
    }
#endif

    [Fact]
    public async Task ABridgeIsImportedListedRekeyedAndDeletedWithItsSources()
    {
        await Post("upstreams", new AddUpstreamRequest("api", "https://api.example.com"));
        await Post("routes", new AddRouteRequest("/tracker", "api"));
        var routeId = Assert.Single(Cache.Current.Routes).Id;

        Assert.Equal(HttpStatusCode.BadRequest,
            (await Post("bridges", new ImportBridgeRequest("Broken", routeId.ToString(), "{ not json"))).StatusCode);

        var imported = await Post("bridges", new ImportBridgeRequest("Task tracker", routeId.ToString(), McpApiBridgeSample.Read(), KeyLifetimeDays: 30));
        Assert.Equal(HttpStatusCode.OK, imported.StatusCode);

        var bridge = Assert.Single(await Get<List<BridgeSummary>>("bridges"));
        Assert.Equal("task-tracker", bridge.Slug);
        Assert.Equal("/tracker", bridge.Route);
        Assert.True(bridge.Tools > 0);

        var before = await Get<KeyInfo>("bridges/task-tracker/key");
        await Post("bridges/task-tracker/key/rotate", new { });
        Assert.NotEqual(before.Value, (await Get<KeyInfo>("bridges/task-tracker/key")).Value);

        await Post("bridges/task-tracker/disable", new { });
        Assert.False(Cache.Current.McpApiBridges.Single().Enabled);

        await Post("sources", new AddSourceRequest("Tracker tools", McpSourceKind.ApiBridge, Bridge: "task-tracker"));
        await Post("funnels", new AddFunnelRequest("Agent"));
        await Post("funnels/agent/sources", new FunnelSourceRequest("trackertools"));
        Assert.Single(Cache.Current.McpFunnels.Single().Sources);

        var deleted = await _client.DeleteAsync("bridges/task-tracker");
        var message = await deleted.Content.ReadFromJsonAsync<AdminMessage>(AdminChannel.JsonOptions);

        Assert.Contains("1 funnel source", message!.Message);
        Assert.Empty(Cache.Current.McpApiBridges);
        Assert.Empty(Cache.Current.McpSources);
        Assert.Empty(Cache.Current.McpFunnels.Single().Sources);
    }

    [Fact]
    public async Task ASignInIsStreamedAndEndsWithItsVerdict()
    {
        var missing = await _client.PostAsync("credentials/nobody/signin", null);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        await _client.PostAsJsonAsync("credentials", new CredentialRecord
        {
            Name = "app",
            Kind = CredentialKind.ClientCredentials,
            ClientId = "client",
            ClientSecret = "secret",
            TokenEndpoint = $"{Unreachable}/token",
        }, AdminChannel.JsonOptions);

        var stream = await (await _client.PostAsync("credentials/app/signin", null)).Content.ReadAsStringAsync();
        var lines = stream.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.StartsWith("Requesting a token for 'app'", lines[0]);
        Assert.StartsWith("FAILED:", lines[^1]);
    }

    [Fact]
    public async Task CredentialsAreListedTestedSignedOutAndRemoved()
    {
        var badOauth = await _client.PostAsJsonAsync("credentials", new CredentialRecord
        {
            Name = "web",
            Kind = CredentialKind.OAuth2,
            ClientId = "client",
            TokenEndpoint = "http://plain.example.com/token",
        }, AdminChannel.JsonOptions);
        Assert.Equal(HttpStatusCode.BadRequest, badOauth.StatusCode);

        await _client.PostAsJsonAsync("credentials", new CredentialRecord
        {
            Name = "key", Kind = CredentialKind.ApiKey, ApiKey = "value", TestEndpoint = $"{Unreachable}/ping",
        }, AdminChannel.JsonOptions);

        var listed = Assert.Single(await Get<List<CredentialSummary>>("credentials"));
        Assert.Equal("static key", listed.TokenExpiry);

        var test = await (await _client.PostAsync("credentials/key/test", null)).Content
            .ReadFromJsonAsync<CredentialTestReply>(AdminChannel.JsonOptions);
        Assert.False(test!.Success);

        Assert.Equal(HttpStatusCode.OK, (await _client.PostAsync("credentials/key/signout", null)).StatusCode);

        await Post("upstreams", new AddUpstreamRequest("api", "https://api.example.com"));
        await Post("routes", new AddRouteRequest("/api", "api", Credential: "key"));

        var removed = await (await _client.DeleteAsync("credentials/key")).Content.ReadFromJsonAsync<AdminMessage>(AdminChannel.JsonOptions);
        Assert.Contains("1 route(s) still name it", removed!.Message);

        var upstreamGone = await (await _client.DeleteAsync("upstreams/api")).Content.ReadFromJsonAsync<AdminMessage>(AdminChannel.JsonOptions);
        Assert.Contains("1 route(s) now have no upstream", upstreamGone!.Message);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.DeleteAsync("credentials/key")).StatusCode);
    }

    [Fact]
    public async Task ReloadRefusesToDiscardChangesUnlessForced()
    {
        await Post("upstreams", new AddUpstreamRequest("api", "https://api.example.com"));
        Assert.True(Cache.HasPendingChanges);

        Assert.Equal(HttpStatusCode.Conflict, (await Post("reload", new ReloadRequest())).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await Post("reload", new ReloadRequest(Force: true))).StatusCode);
        Assert.Empty(Cache.Current.Upstreams);
    }

    [Fact]
    public async Task SourcesAndFunnelsCanBeRefreshedToggledAndUnpooled()
    {
        var none = await (await Post("sources/refresh", new { })).Content.ReadFromJsonAsync<AdminMessage>(AdminChannel.JsonOptions);
        Assert.Equal("No enabled sources to refresh.", none!.Message);

        await Post("sources", new AddSourceRequest("Remote", McpSourceKind.RemoteUrl, Url: $"{Unreachable}/mcp"));

        var refreshed = await (await Post("sources/refresh?source=remote", new { })).Content.ReadFromJsonAsync<AdminMessage>(AdminChannel.JsonOptions);
        Assert.Contains("remote: unreachable", refreshed!.Message);
        Assert.Contains("unreachable", Assert.Single(await Get<List<SourceSummary>>("sources")).Catalog);

        await Post("sources/remote/disable", new { });
        Assert.False(Cache.Current.McpSources.Single().Enabled);
        await Post("sources/remote/enable", new { });
        Assert.True(Cache.Current.McpSources.Single().Enabled);

        await Post("funnels", new AddFunnelRequest("Agent", "agent"));
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.DeleteAsync("funnels/agent/sources/remote")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await Post("funnels/agent/sources", new FunnelSourceRequest("remote", McpSelectionMode.Include))).StatusCode);

        await Post("funnels/agent/sources", new FunnelSourceRequest("remote", McpSelectionMode.Exclude, ["drop"]));
        Assert.Contains("all except drop", Assert.Single(Assert.Single(await Get<List<FunnelSummary>>("funnels")).Sources));

        Assert.Equal(HttpStatusCode.OK, (await _client.DeleteAsync("funnels/agent/sources/remote")).StatusCode);
        Assert.Empty(Cache.Current.McpFunnels.Single().Sources);

        Assert.False(string.IsNullOrEmpty((await Get<KeyInfo>("funnels/agent/key")).Value));
        await Post("funnels/agent/disable", new { });
        Assert.False(Cache.Current.McpFunnels.Single().Enabled);
        Assert.Equal(HttpStatusCode.NotFound, (await Post("funnels/nobody/enable", new { })).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await _client.DeleteAsync("funnels/agent")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _client.DeleteAsync("sources/remote")).StatusCode);
    }

    private async Task<T> Get<T>(string path) =>
        (await _client.GetFromJsonAsync<T>(path, AdminChannel.JsonOptions))!;

    private Task<HttpResponseMessage> Post<T>(string path, T body) =>
        _client.PostAsJsonAsync(path, body, AdminChannel.JsonOptions);

    private Task<HttpResponseMessage> Patch(SettingsPatch patch) =>
        _client.PatchAsJsonAsync("settings", patch, AdminChannel.JsonOptions);

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _admin.DisposeAsync();
        await _host.DisposeAsync();
        ManifestLocalStore.RootOverride = null;
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}
