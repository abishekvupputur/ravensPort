using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using RavensPort.Core.Storage;
using RavensPort.Core.Tests.Mcp;
using System.Net.Http.Json;
using System.Text.Json;

namespace RavensPort.UI.Tests;

/// <summary>
/// The approval suite's stages, re-asked of a single-use session built through the views.
///
/// The mapping, stage for stage:
///
///   1  the vault starts empty                     -> SingleUseUiTests
///   2  credentials, routes and funnels are seeded -> every test here, through the tabs
///   3  every credential placement reaches upstream-> <see cref="EveryCredentialPlacementReachesTheUpstream"/>
///   4  both funnels answer on both revisions      -> <see cref="BothFunnelsAnswerOnBothProtocolRevisions"/>
///   5  the issued OAuth token reaches the server  -> <see cref="TheRouteCredentialReachesTheMcpServer"/>
///   6  mTLS is enabled and exported               -> not here, and cannot be
///   7  the configuration survives a restart       -> not here, and cannot be
///   8  placements still arrive over mTLS          -> not here
///   9  funnels still answer over mTLS             -> not here
///   10 the restored token still reaches the server-> not here
///   11 the listener refuses the wrong caller      -> SingleUseUiTests, in its keyless form
///
/// Six and eight through ten are mTLS, which this suite is deliberately without. Seven is the same
/// exclusion wearing a different hat: enabling mTLS rebinds Kestrel and the app restarts itself to
/// do it, and a restart is exactly what ends a single-use session — there is no vault behind it to
/// come back to. Those five stay the approval suite's job, against a real vault that survives.
/// </summary>
public class ApprovalParityUiTests
{
    /// <summary>
    /// Stage 3. Every shape a route can attach a credential in, asserted at the upstream — which is
    /// the only place that can tell an injected header from a configured intention.
    /// </summary>
    [Fact]
    public Task EveryCredentialPlacementReachesTheUpstream() => UiSession.RunAsync(async () =>
    {
        await using var harness = await SingleUseHarness.StartAsync();
        await harness.StartSingleUseThroughTheUiAsync();

        await ApprovalSeeding.SeedCredentialsAsync(harness);
        await ApprovalSeeding.SeedRouteMatrixAsync(harness);

        // Nothing attached: the hop still forwards, and the caller's own Authorization is stripped
        // rather than passed through — a route with no credential must not become a way to smuggle
        // one to the upstream.
        var none = await PostAsync(harness, "/app/none");
        Assert.Null(none.Header("Authorization"));

        var one = await PostAsync(harness, "/app/one");
        Assert.Equal($"Bearer {ApprovalSeeding.OAuthToken}", one.Header("Authorization"));

        var two = await PostAsync(harness, "/app/two-headers");
        Assert.Equal($"Bearer {ApprovalSeeding.OAuthToken}", two.Header("Authorization"));
        Assert.Equal(ApprovalSeeding.ProjectKey, two.Header("X-Project-Key"));

        var several = await PostAsync(harness, "/app/several-headers");
        Assert.Equal($"Bearer {ApprovalSeeding.OAuthToken}", several.Header("Authorization"));
        Assert.Equal(ApprovalSeeding.ProjectKey, several.Header("X-Api-Key"));
        Assert.Equal($"token {ApprovalSeeding.ProjectKey}", several.Header("PRIVATE-TOKEN"));

        var headerBody = await PostAsync(harness, "/app/header-body");
        Assert.Equal($"Bearer {ApprovalSeeding.OAuthToken}", headerBody.Header("Authorization"));
        Assert.Equal(ApprovalSeeding.ProjectKey, FieldOf(headerBody.Body, "auth_token"));

        // Both fields in one rewrite: two separate rewrites would each start from the original body
        // and the second would drop the first.
        var twoBody = await PostAsync(harness, "/app/two-body");
        Assert.Equal(ApprovalSeeding.OAuthToken, FieldOf(twoBody.Body, "access_token"));
        Assert.Equal(ApprovalSeeding.ProjectKey, FieldOf(twoBody.Body, "project_token"));

        var both = await PostAsync(harness, "/app/oauth-plus-key");
        Assert.Equal($"Bearer {ApprovalSeeding.OAuthToken}", both.Header("Authorization"));
        Assert.Equal(ApprovalSeeding.ProjectKey, both.Header("X-Api-Key"));
    });

    /// <summary>
    /// Stage 4. A funnel pooling two servers, and one pooling a single server, on the current
    /// protocol revision and on the older one a client may pin.
    /// </summary>
    [Fact]
    public Task BothFunnelsAnswerOnBothProtocolRevisions() => UiSession.RunAsync(async () =>
    {
        await using var harness = await SingleUseHarness.StartAsync();
        await harness.StartSingleUseThroughTheUiAsync();

        await using var alpha = await FakeMcpServer.StartAsync();
        await using var beta = await FakeMcpServer.StartAsync();

        await ApprovalSeeding.EnableFunnelAsync(harness);
        await ApprovalSeeding.SeedSourcesAsync(harness,
            ("alpha", "alpha", alpha.Url), ("beta", "beta", beta.Url));

        // Two funnels over the same two sources, which is the point of the pair: one pools both,
        // the other pools alpha alone and must not show beta's tools.
        await ApprovalSeeding.SeedFunnelAsync(harness, "both", "alpha", "beta");
        await ApprovalSeeding.SeedFunnelAsync(harness, "solo", "alpha");

        var store = harness.Services.GetRequiredService<ConfigStoreCache>();
        var bothKey = store.Current.McpFunnels.Single(f => f.Slug == "both").Key.Value;
        var soloKey = store.Current.McpFunnels.Single(f => f.Slug == "solo").Key.Value;

        foreach (var version in new string?[] { null, "2025-11-25" })
        {
            var pooled = await harness.ConnectMcpAsync("both", bothKey, version);

            // Asserted before anything else: a pin that quietly failed would leave the client on the
            // current revision and every check below would still pass.
            if (version is not null) Assert.Equal(version, pooled.NegotiatedProtocolVersion);

            var tools = (await pooled.ListToolsAsync()).Select(t => t.Name).ToList();
            Assert.Contains("alpha__echo", tools);
            Assert.Contains("beta__echo", tools);

            var call = await pooled.CallToolAsync(
                "alpha__echo", new Dictionary<string, object?> { ["value"] = "hello" }!);
            Assert.Equal("hello", call.Content.OfType<TextContentBlock>().First().Text);

            var solo = await harness.ConnectMcpAsync("solo", soloKey, version);
            var soloTools = (await solo.ListToolsAsync()).Select(t => t.Name).ToList();
            Assert.Contains("alpha__echo", soloTools);
            Assert.DoesNotContain(soloTools, n => n.StartsWith("beta__", StringComparison.Ordinal));
        }
    });

    /// <summary>
    /// Stage 5, in the form single use can ask it.
    ///
    /// The approval suite runs a real client-credentials grant against a mock authorization server
    /// and then watches that token arrive at an MCP server. There is no authorization server on a
    /// runner with no secrets, so what is proved here is the half that is this product's: the
    /// funnel's source is one of the proxy's own routes, so reaching it dials the loopback listener
    /// and puts the route's credential transform in the path. The fake records what it was actually
    /// sent, so the assertion is the header that arrived rather than the configuration meant to
    /// produce it.
    /// </summary>
    [Fact]
    public Task TheRouteCredentialReachesTheMcpServer() => UiSession.RunAsync(async () =>
    {
        await using var harness = await SingleUseHarness.StartAsync();
        await harness.StartSingleUseThroughTheUiAsync();

        await using var secured = await FakeMcpServer.StartAsync();

        await ApprovalSeeding.SeedCredentialsAsync(harness);
        await ApprovalSeeding.EnableFunnelAsync(harness);
        await ApprovalSeeding.SeedSecuredRouteFunnelAsync(harness, secured.Url);

        var store = harness.Services.GetRequiredService<ConfigStoreCache>();
        var key = store.Current.McpFunnels.Single(f => f.Slug == "oauth").Key.Value;

        secured.ReceivedAuthorization.Clear();

        var client = await harness.ConnectMcpAsync("oauth", key);

        var tools = (await client.ListToolsAsync()).Select(t => t.Name).ToList();
        Assert.True(tools.Contains("secured__echo"),
            $"funnel offered [{string.Join(", ", tools)}]; the fake saw {secured.ReceivedAuthorization.Count} "
            + $"request(s); activity log: "
            + string.Join(" // ", harness.Services.GetRequiredService<RavensPort.Core.Diagnostics.ActivityLog>()
                .GetRecent(8)));

        var call = await client.CallToolAsync(
            "secured__echo", new Dictionary<string, object?> { ["value"] = "through the route" }!);
        Assert.Equal("through the route", call.Content.OfType<TextContentBlock>().First().Text);

        Assert.NotEmpty(secured.ReceivedAuthorization);
        Assert.All(secured.ReceivedAuthorization,
            seen => Assert.Equal($"Bearer {ApprovalSeeding.OAuthToken}", seen));
    });

    /// <summary>
    /// POSTs a small JSON object, because body placements need something to rewrite.
    ///
    /// The caller sends an Authorization of its own every time. It must never survive: the transform
    /// replaces it where a credential is configured and strips it where none is.
    /// </summary>
    private static async Task<Seen> PostAsync(SingleUseHarness harness, string prefix)
    {
        var store = harness.Services.GetRequiredService<ConfigStoreCache>();
        var route = store.Current.Routes.Single(r => r.PathPrefix == prefix);

        using var client = harness.CreateClientFor(route.Key.Value);
        client.DefaultRequestHeaders.Add("Authorization", "Bearer CALLER-SUPPLIED-VALUE");

        var response = await client.PostAsJsonAsync(prefix + "/anything", new { hello = "world" });
        var payload = await response.Content.ReadAsStringAsync();

        Assert.True(response.IsSuccessStatusCode, $"{prefix} returned {(int)response.StatusCode}: {payload}");

        var echo = SingleUseHarness.ReadEcho(payload);
        return new Seen(echo.Headers, echo.Body);
    }

    private static string? FieldOf(string json, string field)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty(field, out var v) ? v.GetString() : null;
    }

    private sealed record Seen(Dictionary<string, string> Headers, string Body)
    {
        public string? Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;
    }
}
