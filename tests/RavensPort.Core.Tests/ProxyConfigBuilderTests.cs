using RavensPort.Core.Models;
using RavensPort.Core.Proxy;

namespace RavensPort.Core.Tests;

public class ProxyConfigBuilderTests
{
    private static RouteCredential Bearer(Guid credentialId) =>
        RouteCredential.For(credentialId, CredentialPlacement.Header);

    [Fact]
    public void Build_EnabledRouteWithKnownUpstream_ProducesRouteAndCluster()
    {
        var upstream = new UpstreamRecord { Name = "httpbin", BaseUrl = "https://httpbin.org" };
        var credentialId = Guid.NewGuid();
        var route = new RouteMapping
        {
            PathPrefix = "/app/httpbin",
            UpstreamId = upstream.Id,
            Credentials = [Bearer(credentialId)],
            StripPrefix = true,
            Enabled = true,
        };

        var (routes, clusters) = ProxyConfigBuilder.Build([route], [upstream]);

        var routeConfig = Assert.Single(routes);
        Assert.Equal(route.Id.ToString(), routeConfig.RouteId);
        Assert.Equal(route.Id.ToString(), routeConfig.ClusterId);
        Assert.Equal("/app/httpbin/{**catch-all}", routeConfig.Match.Path);
        Assert.NotNull(routeConfig.Transforms);

        var written = Assert.Single(ProxyConfigBuilder.ReadCredentials(routeConfig.Metadata!));
        Assert.Equal(credentialId, written.CredentialId);

        var clusterConfig = Assert.Single(clusters);
        Assert.Equal(route.Id.ToString(), clusterConfig.ClusterId);
        Assert.Equal("https://httpbin.org", clusterConfig.Destinations!["d1"].Address);
    }

    [Fact]
    public void Build_DisabledRoute_IsExcluded()
    {
        var upstream = new UpstreamRecord { Name = "httpbin", BaseUrl = "https://httpbin.org" };
        var route = new RouteMapping
        {
            PathPrefix = "/app/httpbin",
            UpstreamId = upstream.Id,
            Credentials = [Bearer(Guid.NewGuid())],
            Enabled = false,
        };

        var (routes, clusters) = ProxyConfigBuilder.Build([route], [upstream]);

        Assert.Empty(routes);
        Assert.Empty(clusters);
    }

    [Fact]
    public void Build_RouteWithMissingUpstream_IsExcluded()
    {
        var route = new RouteMapping
        {
            PathPrefix = "/app/missing",
            UpstreamId = Guid.NewGuid(),
            Credentials = [Bearer(Guid.NewGuid())],
        };

        var (routes, clusters) = ProxyConfigBuilder.Build([route], []);

        Assert.Empty(routes);
        Assert.Empty(clusters);
    }

    [Fact]
    public void Build_RouteWithUnparseablePrefix_IsExcludedWithoutTakingOtherRoutesDown()
    {
        // A prefix containing '{' produces a template RoutePatternFactory cannot parse. Handed
        // to YARP it makes the whole config update fail, so the good route's pending edit would
        // be discarded too. Dropping just the bad one keeps the rest applying.
        var upstream = new UpstreamRecord { Name = "echo", BaseUrl = "https://api.test" };

        var good = new RouteMapping { PathPrefix = "/good", UpstreamId = upstream.Id, Credentials = [Bearer(Guid.NewGuid())] };
        var bad = new RouteMapping { PathPrefix = "/bad{x}", UpstreamId = upstream.Id, Credentials = [Bearer(Guid.NewGuid())] };

        var (routes, clusters) = ProxyConfigBuilder.Build([good, bad], [upstream]);

        Assert.Single(routes);
        Assert.Single(clusters);
        Assert.Equal("/good/{**catch-all}", routes[0].Match.Path);
    }

    [Fact]
    public void Build_WritesEveryCredentialAndItsPlacementIntoMetadata()
    {
        var upstream = new UpstreamRecord { Name = "api", BaseUrl = "https://api.test" };
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var route = new RouteMapping
        {
            PathPrefix = "/app/api",
            UpstreamId = upstream.Id,
            Credentials =
            [
                new RouteCredential { CredentialId = first, Placement = CredentialPlacement.Body, ParameterName = "access_token", ValuePrefix = "" },
                new RouteCredential { CredentialId = second, Placement = CredentialPlacement.Header, ParameterName = "X-Api-Key", ValuePrefix = "" },
            ],
        };

        var (routes, _) = ProxyConfigBuilder.Build([route], [upstream]);

        var read = ProxyConfigBuilder.ReadCredentials(Assert.Single(routes).Metadata!);
        Assert.Equal(2, read.Count);

        Assert.Equal(first, read[0].CredentialId);
        Assert.Equal(CredentialPlacement.Body, read[0].Placement);
        Assert.Equal("access_token", read[0].ParameterName);
        Assert.Equal("", read[0].ValuePrefix);

        Assert.Equal(second, read[1].CredentialId);
        Assert.Equal(CredentialPlacement.Header, read[1].Placement);
        Assert.Equal("X-Api-Key", read[1].ParameterName);
    }

    [Fact]
    public void Build_DropsARouteHoldingAQueryPlacement()
    {
        // Only an inherited store can contain one. Building the route anyway would serve it with
        // that credential silently missing, so the whole route goes - the same fail-closed rule
        // an unparseable prefix gets, and the notifier names it in the activity log.
        var upstream = new UpstreamRecord { Name = "api", BaseUrl = "https://api.test" };
        var route = new RouteMapping
        {
            PathPrefix = "/app/api",
            UpstreamId = upstream.Id,
            Credentials =
            [
                new RouteCredential { CredentialId = Guid.NewGuid(), Placement = CredentialPlacement.Header, ParameterName = "X-Api-Key", ValuePrefix = "" },
                new RouteCredential { CredentialId = Guid.NewGuid(), Placement = CredentialPlacement.Query, ParameterName = "access_token", ValuePrefix = "" },
            ],
        };

        var (routes, clusters) = ProxyConfigBuilder.Build([route], [upstream]);

        Assert.Empty(routes);
        Assert.Empty(clusters);
    }

    [Fact]
    public void Build_RouteWithNoCredentials_IsStillServedAndCarriesAnEmptyList()
    {
        // A route that attaches nothing is a supported configuration — a plain forwarding hop —
        // not a half-finished one. The metadata key is still written, because the transform it
        // switches on also strips the caller's own Authorization and cookies.
        var upstream = new UpstreamRecord { Name = "public", BaseUrl = "https://api.test" };
        var route = new RouteMapping { PathPrefix = "/app/public", UpstreamId = upstream.Id };

        var (routes, clusters) = ProxyConfigBuilder.Build([route], [upstream]);

        var routeConfig = Assert.Single(routes);
        Assert.Single(clusters);
        Assert.True(routeConfig.Metadata!.ContainsKey(ProxyConfigBuilder.CredentialsMetadataKey));
        Assert.Empty(ProxyConfigBuilder.ReadCredentials(routeConfig.Metadata));
    }

    [Fact]
    public void ReadCredentials_WithoutTheMetadataKey_YieldsNothing()
    {
        // The only safe reading of metadata this build cannot interpret is "attach nothing".
        Assert.Empty(ProxyConfigBuilder.ReadCredentials(new Dictionary<string, string>()));
        Assert.Empty(ProxyConfigBuilder.ReadCredentials(null));
        Assert.Empty(ProxyConfigBuilder.ReadCredentials(
            new Dictionary<string, string> { [ProxyConfigBuilder.CredentialsMetadataKey] = "not json" }));
    }

    [Fact]
    public void Build_RouteWithUnusableCredentialSettings_IsExcluded()
    {
        // A newline in the prefix would split the header line at the upstream, and a route that
        // cannot attach its credential is not worth serving unauthenticated.
        var upstream = new UpstreamRecord { Name = "api", BaseUrl = "https://api.test" };
        var route = new RouteMapping
        {
            PathPrefix = "/app/api",
            UpstreamId = upstream.Id,
            Credentials =
            [
                new RouteCredential
                {
                    CredentialId = Guid.NewGuid(),
                    ParameterName = "Authorization",
                    ValuePrefix = "Bearer \r\nX-Admin: 1",
                },
            ],
        };

        var (routes, clusters) = ProxyConfigBuilder.Build([route], [upstream]);

        Assert.Empty(routes);
        Assert.Empty(clusters);
    }

    [Fact]
    public void Build_RouteWithTwoCredentialsInTheSameSlot_IsExcluded()
    {
        // The second would silently overwrite the first, so the upstream would see one token
        // while the UI showed two. There is no reading of that config that does what it says.
        var upstream = new UpstreamRecord { Name = "api", BaseUrl = "https://api.test" };
        var route = new RouteMapping
        {
            PathPrefix = "/app/api",
            UpstreamId = upstream.Id,
            Credentials = [Bearer(Guid.NewGuid()), Bearer(Guid.NewGuid())],
        };

        var (routes, clusters) = ProxyConfigBuilder.Build([route], [upstream]);

        Assert.Empty(routes);
        Assert.Empty(clusters);
    }

    [Fact]
    public void Build_RouteWithDotSegmentPrefix_IsExcluded()
    {
        var upstream = new UpstreamRecord { Name = "echo", BaseUrl = "https://api.test" };
        var route = new RouteMapping
        {
            PathPrefix = "/api/../admin",
            UpstreamId = upstream.Id,
            Credentials = [Bearer(Guid.NewGuid())],
        };

        var (routes, _) = ProxyConfigBuilder.Build([route], [upstream]);

        Assert.Empty(routes);
    }
}
