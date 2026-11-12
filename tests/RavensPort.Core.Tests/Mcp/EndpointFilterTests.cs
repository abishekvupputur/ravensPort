using System.Net;
using System.Net.Http.Json;
using ModelContextProtocol.Protocol;
using RavensPort.Core.Models;
using RavensPort.Core.Proxy;

namespace RavensPort.Core.Tests.Mcp;

/// <summary>
/// Filter profiles on plain routes and on API bridges, through the real pipeline: a blocked
/// request never reaches the upstream, and says why.
/// </summary>
public class EndpointFilterTests : IAsyncLifetime
{
    private const string Manifest = """
        {
          "version": 1,
          "tools": [
            {
              "name": "create_task",
              "inputSchema": { "type": "object", "properties": { "title": {"type":"string"} }, "required": ["title"] },
              "request": { "method": "POST", "path": "/tasks", "bodyMode": "template", "body": { "title": "{title}" } }
            }
          ]
        }
        """;

    private FakeRestApi _upstream = null!;
    private FunnelTestHost _host = null!;
    private RouteMapping _route = null!;
    private McpApiBridgeRecord _bridge = null!;
    private FilterProfileRecord _strict = null!;

    public async Task InitializeAsync()
    {
        _upstream = await FakeRestApi.StartAsync();
        _host = await FunnelTestHost.StartAsync();

        var upstream = new UpstreamRecord { Name = "tasks", BaseUrl = _upstream.Url };
        _route = new RouteMapping { PathPrefix = "/tasks-api", UpstreamId = upstream.Id, StripPrefix = true };
        _strict = new FilterProfileRecord
        {
            Name = "Strict",
            Filters =
            [
                new FunnelFilter { Category = FilterCategory.Secrets, Level = FilterLevel.Balanced },
                new FunnelFilter { Category = FilterCategory.PersonalData, Level = FilterLevel.Balanced },
            ],
        };

        await _host.MutateAsync(store =>
        {
            store.Upstreams.Add(upstream);
            store.Routes.Add(_route);
            store.FilterProfiles.Add(_strict);
        });
        _host.RebuildProxyConfig();

        _bridge = await _host.AddApiBridgeAsync("tracker", _route.Id, Manifest);
    }

    public async Task DisposeAsync()
    {
        await _host.DisposeAsync();
        await _upstream.DisposeAsync();
    }

    private Task UseProfileOnRoute(Guid? id) => _host.MutateAsync(store => store.Routes.Single(r => r.Id == _route.Id).FilterProfileId = id);
    private Task UseProfileOnBridge(Guid? id) => _host.MutateAsync(store => store.McpApiBridges.Single(b => b.Id == _bridge.Id).FilterProfileId = id);

    private async Task<HttpResponseMessage> PostToRouteAsync(object body)
    {
        using var http = _host.CreateClientHandler() is { } handler ? new HttpClient(handler) : new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_host.BaseUrl}/tasks-api/tasks") { Content = JsonContent.Create(body) };
        request.Headers.Add(LocalAccessGuard.ApiKeyHeaderName, FunnelTestHost.ApiKey);
        return await http.SendAsync(request);
    }

    [Fact]
    public async Task ARouteWithAProfile_RefusesARequestCarryingAPassword_AndForwardsACleanOne()
    {
        await UseProfileOnRoute(_strict.Id);

        var refused = await PostToRouteAsync(new { username = "jane.doe", password = "hunter2" });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        // The refusal names the first blocking hit; the activity log lists them all.
        Assert.Contains("ravensport-username-field", await refused.Content.ReadAsStringAsync());
        Assert.Contains("ravensport-password-field", File.ReadAllText(_host.ActivityLog.CurrentLogPath));
        Assert.DoesNotContain("hunter2", await refused.Content.ReadAsStringAsync());
        Assert.Empty(_upstream.Received);

        var forwarded = await PostToRouteAsync(new { title = "water the plants" });
        Assert.True(forwarded.IsSuccessStatusCode, $"{(int)forwarded.StatusCode}");
        Assert.Single(_upstream.Received);
    }

    [Fact]
    public async Task ARouteWithoutAProfile_IsNotFiltered()
    {
        var forwarded = await PostToRouteAsync(new { password = "hunter2" });

        Assert.True(forwarded.IsSuccessStatusCode);
        Assert.Single(_upstream.Received);
    }

    [Fact]
    public async Task ARouteWhoseProfileIsGone_RefusesRatherThanForwarding()
    {
        await UseProfileOnRoute(Guid.NewGuid());

        var refused = await PostToRouteAsync(new { title = "anything" });

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("no longer exists", await refused.Content.ReadAsStringAsync());
        Assert.Empty(_upstream.Received);
    }

    [Fact]
    public async Task ABridgeWithAProfile_RefusesTheToolCall_AndSaysWhy()
    {
        await UseProfileOnBridge(_strict.Id);
        var client = await _host.ConnectBridgeAsync("tracker");

        var result = await client.CallToolAsync("create_task", new Dictionary<string, object?> { ["title"] = "mail jane.doe@example.com" });

        Assert.True(result.IsError);
        Assert.Contains("Email address", result.Content.OfType<TextContentBlock>().Single().Text);
        Assert.Empty(_upstream.Received);
        Assert.Contains("blocked by bridge profile 'Strict'", File.ReadAllText(_host.ActivityLog.CurrentLogPath));
    }

    [Fact]
    public async Task ABridgeWithoutAProfile_SendsTheCall()
    {
        var client = await _host.ConnectBridgeAsync("tracker");

        var result = await client.CallToolAsync("create_task", new Dictionary<string, object?> { ["title"] = "mail jane.doe@example.com" });

        Assert.NotEqual(true, result.IsError);
        Assert.Single(_upstream.Received);
    }
}
