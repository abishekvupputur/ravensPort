using ModelContextProtocol.Protocol;
using RavensPort.Core.Mcp;
using RavensPort.Core.Models;
using RavensPort.Core.Proxy;

namespace RavensPort.Core.Tests.Mcp;

/// <summary>
/// The whole path, end to end: an agent's tools/call on /api-mcp/{slug} becomes one HTTP request
/// that leaves through the route, carrying the route's credential and none of this proxy's own
/// signalling.
///
/// This is the test that would catch the feature quietly turning into something else — a call that
/// skips the guard, a token that never gets attached, an argument that escapes the route.
/// </summary>
public class McpApiBridgeEndToEndTests : IAsyncLifetime
{
    private const string Token = "BRIDGE-ACCESS-TOKEN";

    private const string Manifest = """
        {
          "version": 1,
          "instructions": "Use list_tasks before get_task.",
          "tools": [
            {
              "name": "list_tasks",
              "description": "Lists tasks.",
              "readOnly": true,
              "inputSchema": {
                "type": "object",
                "properties": { "query": {"type":"string"}, "limit": {"type":"integer"} },
                "required": ["query"]
              },
              "request": {
                "method": "GET",
                "path": "/tasks",
                "query": { "q": "{query}", "limit": "{limit}" },
                "headers": { "Accept": "application/json" }
              }
            },
            {
              "name": "get_task",
              "inputSchema": {
                "type": "object",
                "properties": { "id": {"type":"string"} },
                "required": ["id"]
              },
              "request": { "method": "GET", "path": "/tasks/{id}" }
            },
            {
              "name": "create_task",
              "inputSchema": {
                "type": "object",
                "properties": { "title": {"type":"string"}, "tags": {"type":"array"} },
                "required": ["title"]
              },
              "request": {
                "method": "POST",
                "path": "/tasks",
                "bodyMode": "template",
                "body": { "title": "{title}", "tags": "{tags}" }
              }
            },
            {
              "name": "list_by_state",
              "inputSchema": {
                "type": "object",
                "properties": { "state": {"type":"string","enum":["open","archived"]} },
                "required": ["state"]
              },
              "variantBy": "state",
              "variants": {
                "open": { "description": "Live tasks", "method": "GET", "path": "/tasks", "query": { "status": "open" } },
                "archived": { "description": "Archived ones", "method": "GET", "path": "/archive/tasks" }
              }
            }
          ],
          "prompts": [
            {
              "name": "daily_review",
              "description": "Review the day",
              "arguments": [ { "name": "focus", "required": false } ],
              "messages": [ { "role": "user", "content": "Review today, weighted towards {focus}." } ]
            }
          ],
          "skills": [
            { "name": "triage", "description": "How to triage", "content": "# Triage\n\nStart with list_tasks." }
          ]
        }
        """;

    /// <summary>A list-valued argument, so the body template has a whole JSON value to carry.</summary>
    private static readonly string[] Tags = ["a", "b"];

    private FakeRestApi _upstream = null!;
    private FunnelTestHost _host = null!;
    private McpApiBridgeRecord _bridge = null!;

    public async Task InitializeAsync()
    {
        _upstream = await FakeRestApi.StartAsync();
        _host = await FunnelTestHost.StartAsync();

        var credential = new CredentialRecord
        {
            Name = "api-credential",
            ClientId = "id",
            ClientSecret = "secret",
            Token = new TokenSet(Token, "refresh", DateTimeOffset.UtcNow.AddHours(1), "Bearer", DateTimeOffset.UtcNow),
        };

        var upstream = new UpstreamRecord { Name = "tasks", BaseUrl = _upstream.Url };

        var route = new RouteMapping
        {
            PathPrefix = "/tasks-api",
            UpstreamId = upstream.Id,
            Credentials = [RouteCredential.For(credential.Id, CredentialPlacement.Header)],
            StripPrefix = true,
        };

        await _host.MutateAsync(store =>
        {
            store.Credentials.Add(credential);
            store.Upstreams.Add(upstream);
            store.Routes.Add(route);
        });
        _host.RebuildProxyConfig();

        _bridge = await _host.AddApiBridgeAsync("tracker", route.Id, Manifest);
    }

    public async Task DisposeAsync()
    {
        await _host.DisposeAsync();
        await _upstream.DisposeAsync();
    }

    [Fact]
    public async Task TheManifestsToolsAreWhatTheAgentSees()
    {
        var client = await _host.ConnectBridgeAsync("tracker");

        var tools = await client.ListToolsAsync();

        Assert.Equal(
            ["create_task", "get_task", "list_by_state", "list_tasks"],
            tools.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal));

        var list = tools.Single(t => t.Name == "list_tasks");
        Assert.True(list.ProtocolTool.Annotations?.ReadOnlyHint);
        Assert.Contains("query", list.JsonSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public async Task TheServerTellsTheAgentHowToUseTheApi()
    {
        var client = await _host.ConnectBridgeAsync("tracker");

        Assert.Equal("Use list_tasks before get_task.", client.ServerInstructions);
    }

    [Fact]
    public async Task AVariantToolExplainsEachChoiceItOffers()
    {
        var client = await _host.ConnectBridgeAsync("tracker");

        var tool = (await client.ListToolsAsync()).Single(t => t.Name == "list_by_state");

        // The enum tells the model the strings; the descriptions tell it what they mean.
        Assert.Contains("open: Live tasks", tool.Description, StringComparison.Ordinal);
        Assert.Contains("archived: Archived ones", tool.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACallArrivesAtTheUpstreamWithTheRoutesCredentialAttached()
    {
        var client = await _host.ConnectBridgeAsync("tracker");

        var result = await client.CallToolAsync("list_tasks", new Dictionary<string, object?>
        {
            ["query"] = "cats",
            ["limit"] = 10,
        });

        Assert.True(result.IsError != true);
        Assert.Contains("""{"ok":true}""", Text(result), StringComparison.Ordinal);

        var request = _upstream.Single();

        Assert.Equal("GET", request.Method);
        Assert.Equal("/tasks", request.Path);
        Assert.Contains("q=cats", request.Query, StringComparison.Ordinal);
        Assert.Contains("limit=10", request.Query, StringComparison.Ordinal);
        Assert.Equal("application/json", request.Header("Accept"));

        // The point of the whole feature.
        Assert.Equal($"Bearer {Token}", request.Header("Authorization"));

        // And none of this proxy's own signalling reaches the upstream's access log.
        Assert.Null(request.Header(LocalAccessGuard.ApiKeyHeaderName));
        Assert.Null(request.Header(LocalAccessGuard.FunnelHopHeaderName));
        Assert.Null(request.Header(LocalAccessGuard.BridgeHopHeaderName));
    }

    [Fact]
    public async Task TheModelsChoiceOfVariantDecidesWhichEndpointIsCalled()
    {
        var client = await _host.ConnectBridgeAsync("tracker");

        await client.CallToolAsync("list_by_state", new Dictionary<string, object?> { ["state"] = "archived" });
        Assert.Equal("/archive/tasks", _upstream.Single().Path);

        _upstream.Clear();

        await client.CallToolAsync("list_by_state", new Dictionary<string, object?> { ["state"] = "open" });
        var request = _upstream.Single();

        Assert.Equal("/tasks", request.Path);
        Assert.Contains("status=open", request.Query, StringComparison.Ordinal);

        // The selector named a variant; it is not a parameter of the API.
        Assert.DoesNotContain("state=", request.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABodyTemplateSendsWholeJsonValues()
    {
        var client = await _host.ConnectBridgeAsync("tracker");

        await client.CallToolAsync("create_task", new Dictionary<string, object?>
        {
            ["title"] = "write it down",
            ["tags"] = Tags,
        });

        var request = _upstream.Single();

        Assert.Equal("POST", request.Method);
        Assert.Equal("""{"title":"write it down","tags":["a","b"]}""", request.Body);
    }

    [Fact]
    public async Task AMissingRequiredArgumentIsAnErrorAndTheUpstreamHearsNothing()
    {
        var client = await _host.ConnectBridgeAsync("tracker");

        var result = await client.CallToolAsync("get_task", new Dictionary<string, object?>());

        Assert.True(result.IsError);
        Assert.Contains("id", Text(result), StringComparison.Ordinal);
        Assert.Empty(_upstream.Received);
    }

    [Fact]
    public async Task AnArgumentCannotEscapeTheRoute()
    {
        var client = await _host.ConnectBridgeAsync("tracker");

        await client.CallToolAsync("get_task", new Dictionary<string, object?> { ["id"] = "../../admin" });

        var request = _upstream.Single();

        // The upstream sees one path segment, whatever the model put in it.
        Assert.StartsWith("/tasks/", request.Path, StringComparison.Ordinal);
        Assert.DoesNotContain("/admin", request.Path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUpstreamFailureComesBackAsAToolErrorCarryingTheStatus()
    {
        _upstream.StatusCode = 422;
        _upstream.ResponseBody = """{"error":"nope"}""";

        var client = await _host.ConnectBridgeAsync("tracker");

        var result = await client.CallToolAsync("list_tasks", new Dictionary<string, object?> { ["query"] = "x" });

        Assert.True(result.IsError);
        Assert.Contains("422", Text(result), StringComparison.Ordinal);
        Assert.Contains("nope", Text(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARedirectIsRefusedRatherThanFollowedWithoutTheCredential()
    {
        _upstream.RedirectTo = "https://elsewhere.example.com/x";

        var client = await _host.ConnectBridgeAsync("tracker");

        var result = await client.CallToolAsync("list_tasks", new Dictionary<string, object?> { ["query"] = "x" });

        Assert.True(result.IsError);
        Assert.Contains("elsewhere.example.com", Text(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnOversizedResponseIsTruncatedRatherThanCarriedWhole()
    {
        _upstream.ResponseBody = new string('x', McpApiBridgeHandlerFactory.MaxResponseBytes + 4096);

        var client = await _host.ConnectBridgeAsync("tracker");

        var result = await client.CallToolAsync("list_tasks", new Dictionary<string, object?> { ["query"] = "x" });

        Assert.True(result.IsError != true);
        Assert.Contains("truncated", Text(result), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The bridge's own log line names the tool and the status and nothing else. Arguments and
    /// response bodies routinely carry the user's own data, and so does an expanded URL — the
    /// expansion <em>is</em> the arguments.
    ///
    /// The forwarding line the credential transform writes for the hop is a separate thing, and it
    /// does carry the path, exactly as it does for a route called directly. That is the existing
    /// contract for proxied traffic; suppressing it for bridges would make their calls the one
    /// kind of request that leaves no trace in the log.
    /// </summary>
    [Fact]
    public async Task TheBridgesOwnLogLineCarriesNeitherArgumentsNorTheResponse()
    {
        var client = await _host.ConnectBridgeAsync("tracker");

        await client.CallToolAsync("get_task", new Dictionary<string, object?> { ["id"] = "secret-record-id" });

        var lines = _host.ActivityLog.GetRecent(50);
        var call = Assert.Single(lines.Where(line => line.Contains("MCP bridge", StringComparison.Ordinal)));

        Assert.Contains("get_task", call, StringComparison.Ordinal);
        Assert.Contains("200", call, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-record-id", call, StringComparison.Ordinal);

        // The response body reaches no log line at all.
        Assert.DoesNotContain("""{"ok":true}""", string.Join("\n", lines), StringComparison.Ordinal);
    }

    // ---- prompts and skills ------------------------------------------------------------------

    [Fact]
    public async Task PromptsAreAnsweredFromTheManifestWithNoCallToTheUpstream()
    {
        var client = await _host.ConnectBridgeAsync("tracker");

        Assert.Equal("daily_review", (await client.ListPromptsAsync()).Single().Name);

        var result = await client.GetPromptAsync("daily_review", new Dictionary<string, object?> { ["focus"] = "billing" });
        var text = result.Messages.Select(m => m.Content).OfType<TextContentBlock>().Single().Text;

        Assert.Equal("Review today, weighted towards billing.", text);

        // An optional argument nobody supplied leaves nothing behind.
        var bare = await client.GetPromptAsync("daily_review", new Dictionary<string, object?>());
        var bareText = bare.Messages.Select(m => m.Content).OfType<TextContentBlock>().Single().Text;

        Assert.Equal("Review today, weighted towards .", bareText);
        Assert.Empty(_upstream.Received);
    }

    [Fact]
    public async Task ASkillIsServedAsAResourceAndNeverAsAnHttpCall()
    {
        var client = await _host.ConnectBridgeAsync("tracker");

        var resource = Assert.Single(await client.ListResourcesAsync());
        Assert.Equal($"skill://tracker/triage", resource.Uri);
        Assert.Equal("text/markdown", resource.ProtocolResource.MimeType);

        var read = await client.ReadResourceAsync(resource.Uri);
        var contents = Assert.IsType<TextResourceContents>(Assert.Single(read.Contents));

        Assert.Contains("Start with list_tasks", contents.Text, StringComparison.Ordinal);
        Assert.Empty(_upstream.Received);
    }

    // ---- configuration edges -------------------------------------------------------------------

    [Fact]
    public async Task AManifestEditLandsOnTheAgentsVeryNextCall()
    {
        var client = await _host.ConnectBridgeAsync("tracker");

        Assert.Equal(4, (await client.ListToolsAsync()).Count);

        await _host.MutateAsync(store =>
            store.McpApiBridges.Single(b => b.Id == _bridge.Id).Manifest.Tools.RemoveAll(t => t.Name == "get_task"));

        Assert.DoesNotContain("get_task", (await client.ListToolsAsync()).Select(t => t.Name));
    }

    [Fact]
    public async Task ABridgeWhoseRouteIsGoneSaysSoRatherThanVanishing()
    {
        await _host.MutateAsync(store => store.Routes.Clear());
        _host.RebuildProxyConfig();

        var client = await _host.ConnectBridgeAsync("tracker");

        // The endpoint is still there, and still lists its tools: the configuration is what is
        // incomplete, and only the user can fix that.
        Assert.NotEmpty(await client.ListToolsAsync());

        var result = await client.CallToolAsync("list_tasks", new Dictionary<string, object?> { ["query"] = "x" });

        Assert.True(result.IsError);
        Assert.Contains("route", Text(result), StringComparison.OrdinalIgnoreCase);
    }

    private static string Text(CallToolResult result) =>
        string.Join("\n", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
}
