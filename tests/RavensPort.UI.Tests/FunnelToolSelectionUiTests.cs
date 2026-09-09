using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using RavensPort.Core.Storage;
using RavensPort.Core.Tests.Mcp;
using RavensPort.UI.ViewModels;
using RavensPort.Views;

namespace RavensPort.UI.Tests;

/// <summary>
/// What a funnel is for: pointing one agent at several MCP servers and deciding, per source,
/// exactly which of their tools it may see.
///
/// Every funnel here is built on the tab and then dialled as an MCP client, because the two halves
/// answer different questions. That the tab saved a selection is worth little on its own — the
/// question is whether the funnel then serves exactly those tools and refuses the rest, and only a
/// real tools/list and a real tools/call can say so.
///
/// The fake server offers five tools (echo, whoami, slow, alpha, beta), which is what makes a
/// partial selection meaningful rather than a choice between none and all.
/// </summary>
public class FunnelToolSelectionUiTests
{
    /// <summary>The three sources the pooling test builds, named once (CA1861).</summary>
    private static readonly string[] ThreeSources = ["one", "two", "three"];

    /// <summary>
    /// Three sources, everything pooled: every tool of every source is reachable, and each arrives
    /// under its own source's prefix so two servers offering "echo" do not collide.
    /// </summary>
    [Fact]
    public Task AFunnelOverThreeSourcesReachesEveryToolOfEach() => UiSession.RunAsync(async () =>
    {
        await using var harness = await SingleUseHarness.StartAsync();
        await harness.StartSingleUseThroughTheUiAsync();

        await using var one = await FakeMcpServer.StartAsync();
        await using var two = await FakeMcpServer.StartAsync();
        await using var three = await FakeMcpServer.StartAsync();

        await ApprovalSeeding.EnableFunnelAsync(harness);
        await ApprovalSeeding.SeedSourcesAsync(harness,
            ("one", "one", one.Url), ("two", "two", two.Url), ("three", "three", three.Url));

        await ApprovalSeeding.SeedFunnelAsync(harness, "everything", "one", "two", "three");

        var tools = await ListToolsAsync(harness, "everything");

        foreach (var source in ThreeSources)
        {
            foreach (var tool in one.Tools)
            {
                Assert.True(tools.Contains($"{source}__{tool}"),
                    $"expected {source}__{tool} in the pool; it offered [{string.Join(", ", tools)}]");
            }
        }

        Assert.Equal(3 * one.Tools.Count, tools.Count);

        // Reachable, not merely listed. A name in tools/list that cannot be called is the failure
        // this pooling exists to avoid — the prefix has to survive the round trip and be stripped
        // again before the upstream sees it.
        var client = await harness.ConnectMcpAsync("everything", KeyOf(harness, "everything"));

        foreach (var source in ThreeSources)
        {
            var call = await client.CallToolAsync(
                $"{source}__echo", new Dictionary<string, object?> { ["value"] = $"hello {source}" }!);

            Assert.Equal($"hello {source}", call.Content.OfType<TextContentBlock>().First().Text);
        }
    });

    /// <summary>
    /// The same three sources, but two of them cut down to named tools.
    ///
    /// This is the selection the tab exists for: "Include" mode plus ticks, per source, per group.
    /// A tool left unticked must not be listed and must not be callable — a funnel that still
    /// served it would be leaking reach the user thought they had withheld.
    /// </summary>
    [Fact]
    public Task AFunnelServesOnlyTheToolsTickedForEachSource() => UiSession.RunAsync(async () =>
    {
        await using var harness = await SingleUseHarness.StartAsync();
        await harness.StartSingleUseThroughTheUiAsync();

        await using var one = await FakeMcpServer.StartAsync();
        await using var two = await FakeMcpServer.StartAsync();
        await using var three = await FakeMcpServer.StartAsync();

        await ApprovalSeeding.EnableFunnelAsync(harness);
        await ApprovalSeeding.SeedSourcesAsync(harness,
            ("one", "one", one.Url), ("two", "two", two.Url), ("three", "three", three.Url));

        await ApprovalSeeding.SeedFunnelAsync(harness, "picked", "one", "two", "three");

        // Two sources cut down, the third left alone — so the test also says that "All" still means
        // all when its neighbours are restricted.
        await ApprovalSeeding.SelectToolsAsync(harness, "picked", "one", "echo", "whoami");
        await ApprovalSeeding.SelectToolsAsync(harness, "picked", "two", "alpha");

        var tools = await ListToolsAsync(harness, "picked");

        Assert.Contains("one__echo", tools);
        Assert.Contains("one__whoami", tools);
        Assert.Contains("two__alpha", tools);

        // Everything not ticked, gone.
        Assert.DoesNotContain("one__slow", tools);
        Assert.DoesNotContain("one__alpha", tools);
        Assert.DoesNotContain("one__beta", tools);
        Assert.DoesNotContain("two__echo", tools);
        Assert.DoesNotContain("two__whoami", tools);

        // The untouched source is untouched.
        foreach (var tool in three.Tools)
        {
            Assert.Contains($"three__{tool}", tools);
        }

        Assert.Equal(2 + 1 + three.Tools.Count, tools.Count);

        // A tool that survived the selection still works end to end.
        var client = await harness.ConnectMcpAsync("picked", KeyOf(harness, "picked"));

        var call = await client.CallToolAsync(
            "one__echo", new Dictionary<string, object?> { ["value"] = "still here" }!);
        Assert.Equal("still here", call.Content.OfType<TextContentBlock>().First().Text);

        // And one that did not is refused rather than quietly forwarded. Whether that refusal
        // arrives as a protocol error or as a result marked IsError is the library's business; what
        // matters is that the call does not simply run.
        var refused = false;

        try
        {
            var blocked = await client.CallToolAsync(
                "one__slow", new Dictionary<string, object?> { ["value"] = "should not run" }!);

            refused = blocked.IsError is true;
        }
        catch (Exception)
        {
            refused = true;
        }

        Assert.True(refused, "a tool left unticked was still callable through the funnel");
    });

    /// <summary>
    /// Two funnels over one set of sources, each serving a different slice.
    ///
    /// The point of a funnel being a unit rather than a global setting: two agents pointed at the
    /// same servers can be given different reach, and neither selection may leak into the other.
    /// </summary>
    [Fact]
    public Task TwoFunnelsOverTheSameSourcesKeepTheirOwnSelections() => UiSession.RunAsync(async () =>
    {
        await using var harness = await SingleUseHarness.StartAsync();
        await harness.StartSingleUseThroughTheUiAsync();

        await using var one = await FakeMcpServer.StartAsync();
        await using var two = await FakeMcpServer.StartAsync();

        await ApprovalSeeding.EnableFunnelAsync(harness);
        await ApprovalSeeding.SeedSourcesAsync(harness, ("one", "one", one.Url), ("two", "two", two.Url));

        await ApprovalSeeding.SeedFunnelAsync(harness, "reader", "one", "two");
        await ApprovalSeeding.SeedFunnelAsync(harness, "writer", "one");

        await ApprovalSeeding.SelectToolsAsync(harness, "reader", "one", "echo");
        await ApprovalSeeding.SelectToolsAsync(harness, "writer", "one", "alpha", "beta");

        var reader = await ListToolsAsync(harness, "reader");
        var writer = await ListToolsAsync(harness, "writer");

        Assert.Contains("one__echo", reader);
        Assert.DoesNotContain("one__alpha", reader);
        foreach (var tool in two.Tools) Assert.Contains($"two__{tool}", reader);

        Assert.Contains("one__alpha", writer);
        Assert.Contains("one__beta", writer);
        Assert.DoesNotContain("one__echo", writer);
        Assert.DoesNotContain(writer, n => n.StartsWith("two__", StringComparison.Ordinal));
    });

    private static string KeyOf(SingleUseHarness harness, string slug) =>
        harness.Services.GetRequiredService<ConfigStoreCache>()
            .Current.McpFunnels.Single(f => f.Slug == slug).Key.Value;

    private static async Task<List<string>> ListToolsAsync(SingleUseHarness harness, string slug)
    {
        var client = await harness.ConnectMcpAsync(slug, KeyOf(harness, slug));
        return [.. (await client.ListToolsAsync()).Select(t => t.Name)];
    }
}
