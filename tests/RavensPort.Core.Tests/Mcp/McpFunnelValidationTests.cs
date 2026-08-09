using RavensPort.Core.Models;

namespace RavensPort.Core.Tests.Mcp;

/// <summary>
/// Validation is shared by the GUI and the funnel server, so these pin the rules both rely on:
/// the endpoint path stays unambiguous, and an alias can always be routed back to one source.
/// </summary>
public class McpFunnelValidationTests
{
    private static McpFunnelRecord Funnel(string slug) => new() { Name = slug, Slug = slug };

    private static McpSourceRecord Source(string alias) =>
        new() { Name = alias, Alias = alias, Kind = McpSourceKind.RemoteUrl, Url = "https://example.com/mcp" };

    [Theory]
    [InlineData("agent")]
    [InlineData("coding-agent")]
    [InlineData("a1")]
    public void AcceptsReasonableSlugs(string slug) =>
        Assert.Null(McpFunnelValidation.ValidateSlug(slug, []));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Agent")]
    [InlineData("with space")]
    [InlineData("with/slash")]
    [InlineData("with_underscore")]
    [InlineData("with.dot")]
    public void RejectsSlugsThatWouldNotSurviveAUrlPath(string slug) =>
        Assert.NotNull(McpFunnelValidation.ValidateSlug(slug, []));

    [Fact]
    public void RejectsADuplicateSlug()
    {
        var existing = new[] { Funnel("agent") };

        Assert.NotNull(McpFunnelValidation.ValidateSlug("agent", existing));
        Assert.NotNull(McpFunnelValidation.ValidateSlug("AGENT", existing));

        // ...but not against itself, so an unrelated edit to the same funnel still saves.
        Assert.Null(McpFunnelValidation.ValidateSlug("agent", existing, existing[0].Id));
    }

    [Theory]
    [InlineData("gh")]
    [InlineData("my-source")]
    [InlineData("src_1")]
    public void AcceptsReasonableAliases(string alias) =>
        Assert.Null(McpFunnelValidation.ValidateAlias(alias, []));

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("has.dot")]
    [InlineData("has/slash")]
    public void RejectsAliasesThatWouldNotSurviveAToolName(string alias) =>
        Assert.NotNull(McpFunnelValidation.ValidateAlias(alias, []));

    [Fact]
    public void RejectsAnAliasContainingTheSeparator()
    {
        // "a__b__tool" would split after "a", routing to a source that never offered the tool.
        Assert.NotNull(McpFunnelValidation.ValidateAlias("a__b", []));
    }

    [Fact]
    public void RejectsADuplicateAlias()
    {
        var existing = new[] { Source("gh") };

        Assert.NotNull(McpFunnelValidation.ValidateAlias("gh", existing));
        Assert.NotNull(McpFunnelValidation.ValidateAlias("GH", existing));
        Assert.Null(McpFunnelValidation.ValidateAlias("gh", existing, existing[0].Id));
    }

    [Fact]
    public void ARouteSourceNeedsARouteThatExists()
    {
        var route = new RouteMapping { PathPrefix = "/x" };

        Assert.Null(McpFunnelValidation.ValidateTarget(McpSourceKind.ProxyRoute, route.Id, null, [route]));
        Assert.NotNull(McpFunnelValidation.ValidateTarget(McpSourceKind.ProxyRoute, Guid.NewGuid(), null, [route]));
    }

    [Fact]
    public void AUrlSourceIsHeldToTheSameTransportRulesAsEverythingElse()
    {
        Assert.Null(McpFunnelValidation.ValidateTarget(McpSourceKind.RemoteUrl, Guid.Empty, "https://example.com/mcp", []));
        Assert.Null(McpFunnelValidation.ValidateTarget(McpSourceKind.RemoteUrl, Guid.Empty, "http://localhost:3000/mcp", []));

        Assert.NotNull(McpFunnelValidation.ValidateTarget(McpSourceKind.RemoteUrl, Guid.Empty, "", []));
        Assert.NotNull(McpFunnelValidation.ValidateTarget(McpSourceKind.RemoteUrl, Guid.Empty, "not-a-url", []));

        // Plain http off-box would put the session on the wire in cleartext.
        Assert.NotNull(McpFunnelValidation.ValidateTarget(McpSourceKind.RemoteUrl, Guid.Empty, "http://example.com/mcp", [])); // DevSkim: ignore DS137138
    }

    [Theory]
    [InlineData("/mcp")]
    [InlineData("/mcp/")]
    [InlineData("/MCP")]
    [InlineData("/mcp/anything")]
    public void RoutesMayNotClaimTheFunnelsOwnPath(string prefix) =>
        Assert.NotNull(RouteValidation.ValidatePathPrefix(prefix));

    [Theory]
    [InlineData("/mcpsrv")]
    [InlineData("/mcp-server")]
    [InlineData("/my/mcp")]
    public void OnlyTheWholeSegmentIsReserved(string prefix) =>
        Assert.Null(RouteValidation.ValidatePathPrefix(prefix));
}
