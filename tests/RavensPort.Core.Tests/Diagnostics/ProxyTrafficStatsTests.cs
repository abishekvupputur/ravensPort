using RavensPort.Core.Diagnostics;
using RavensPort.Core.Proxy;

namespace RavensPort.Core.Tests.Diagnostics;

/// <summary>
/// The counters behind the Dashboard. They are written from the request path, on every request, by
/// whatever threads Kestrel happens to be using — so the things worth pinning are that the totals
/// are exact under concurrency, that a hostile User-Agent cannot grow them without bound, and that
/// what is shown adds up: a client's calls must sum to the endpoint's.
/// </summary>
public class ProxyTrafficStatsTests
{
    private static readonly Guid EndpointId = Guid.NewGuid();

    private static void Call(ProxyTrafficStats stats, string? agent = "curl/8", int status = 200) =>
        stats.Record(ProxyTargetKind.Route, EndpointId, "route '/app'", agent, status, denied: false);

    private static EndpointTraffic Only(ProxyTrafficStats stats) => Assert.Single(stats.Snapshot());

    [Fact]
    public void CountsCallsAgainstTheEndpointThatServedThem()
    {
        var stats = new ProxyTrafficStats();

        Call(stats);
        Call(stats);

        var endpoint = Only(stats);

        Assert.Equal(ProxyTargetKind.Route, endpoint.Kind);
        Assert.Equal(EndpointId, endpoint.Id);
        Assert.Equal("route '/app'", endpoint.Description);
        Assert.Equal(2, endpoint.Calls);
        Assert.Equal(0, endpoint.Failed);
        Assert.Equal(0, endpoint.Denied);
    }

    [Theory]
    [InlineData(200, 0)]
    [InlineData(204, 0)]
    [InlineData(400, 1)]
    [InlineData(403, 1)]
    [InlineData(500, 1)]
    public void AnythingFromFourHundredUpCountsAsFailed(int status, long expectedFailed)
    {
        var stats = new ProxyTrafficStats();

        Call(stats, status: status);

        var endpoint = Only(stats);

        Assert.Equal(1, endpoint.Calls);
        Assert.Equal(expectedFailed, endpoint.Failed);
        Assert.Equal(expectedFailed, Assert.Single(endpoint.Clients).Failed);
    }

    [Fact]
    public void ARefusalIsNotACall()
    {
        var stats = new ProxyTrafficStats();

        stats.Record(ProxyTargetKind.Funnel, EndpointId, "funnel 'x'", "curl/8", 403, denied: true);

        var endpoint = Only(stats);

        // It never reached an upstream, so counting it as traffic would overstate what the
        // endpoint actually served — but it still has to show up, since a wall of refusals is
        // exactly what someone reads this tab to find.
        Assert.Equal(0, endpoint.Calls);
        Assert.Equal(1, endpoint.Denied);
        Assert.Empty(endpoint.Clients);
    }

    [Fact]
    public void RequestsToNoKnownEndpointAreCountedOnTheirOwn()
    {
        var stats = new ProxyTrafficStats();

        stats.RecordUnroutableDenial();
        stats.RecordUnroutableDenial();

        Assert.Equal(2, stats.UnroutableDenied);
        Assert.Empty(stats.Snapshot());
    }

    [Fact]
    public void EachKindAndIdIsItsOwnEndpoint()
    {
        var stats = new ProxyTrafficStats();
        var shared = Guid.NewGuid();

        // Same id under two kinds must not merge — ids are only unique within their own kind.
        stats.Record(ProxyTargetKind.Route, shared, "route", "curl/8", 200, denied: false);
        stats.Record(ProxyTargetKind.Bridge, shared, "bridge", "curl/8", 200, denied: false);

        Assert.Equal(2, stats.Snapshot().Count);
    }

    // ---- the caller ------------------------------------------------------------------------------

    [Fact]
    public void CallersAreSeparatedByAgentAndBlankIsNamedRatherThanDropped()
    {
        var stats = new ProxyTrafficStats();

        Call(stats, "claude-code/2.1");
        Call(stats, "claude-code/2.1");
        Call(stats, "curl/8");
        Call(stats, null);
        Call(stats, "   ");

        var clients = Only(stats).Clients;

        Assert.Equal(2, clients.Single(c => c.UserAgent == "claude-code/2.1").Calls);
        Assert.Equal(1, clients.Single(c => c.UserAgent == "curl/8").Calls);
        Assert.Equal(2, clients.Single(c => c.UserAgent == ProxyTrafficStats.UnknownClient).Calls);
    }

    [Fact]
    public void AHostileAgentIsStrippedAndTruncatedBeforeItIsKept()
    {
        var stats = new ProxyTrafficStats();

        // Control characters would corrupt the list it is rendered into; length is unbounded on
        // the wire. Both are the caller's to choose, so neither is trusted.
        Call(stats, "evil\r\nInjected: header");
        Call(stats, new string('x', 5000));

        var agents = Only(stats).Clients.Select(client => client.UserAgent).ToList();

        Assert.All(agents, agent => Assert.DoesNotContain('\n', agent));
        Assert.All(agents, agent => Assert.DoesNotContain('\r', agent));
        Assert.All(agents, agent => Assert.True(agent.Length <= 180, $"kept {agent.Length} characters"));
        Assert.Contains(agents, agent => agent.StartsWith("evilInjected", StringComparison.Ordinal));
    }

    [Fact]
    public void ManyDistinctAgentsArePooledRatherThanGrowingWithoutBound()
    {
        var stats = new ProxyTrafficStats();

        // An agent that varies per request — a scanner, or something appending a nonce — must not
        // be able to make this dictionary grow for as long as it keeps calling.
        for (var i = 0; i < 500; i++) Call(stats, $"agent-{i}");

        var endpoint = Only(stats);

        Assert.True(endpoint.Clients.Count <= 25, $"kept {endpoint.Clients.Count} distinct agents");

        // Whatever is pooled still has to add up, or the table contradicts its own total.
        Assert.Equal(endpoint.Calls, endpoint.Clients.Sum(client => client.Calls));
        Assert.Contains(endpoint.Clients, client => client.UserAgent == ProxyTrafficStats.OverflowClient);
    }

    // ---- under load ------------------------------------------------------------------------------

    [Fact]
    public void TotalsAreExactWhenManyThreadsRecordAtOnce()
    {
        var stats = new ProxyTrafficStats();

        Parallel.For(0, 4000, i =>
            stats.Record(
                ProxyTargetKind.Route, EndpointId, "route '/app'",
                i % 2 == 0 ? "a" : "b",
                i % 10 == 0 ? 500 : 200,
                denied: false));

        var endpoint = Only(stats);

        Assert.Equal(4000, endpoint.Calls);
        Assert.Equal(400, endpoint.Failed);
        Assert.Equal(4000, endpoint.Clients.Sum(client => client.Calls));
        Assert.Equal(400, endpoint.Clients.Sum(client => client.Failed));
    }

    [Fact]
    public void LastSeenNeverGoesBackwards()
    {
        var stats = new ProxyTrafficStats();

        Call(stats);
        var first = Only(stats).LastSeenUtc;

        Thread.Sleep(5);
        Call(stats);

        Assert.True(Only(stats).LastSeenUtc >= first);
        Assert.True(Only(stats).FirstSeenUtc <= first);
    }

    [Fact]
    public void ClearForgetsEverything()
    {
        var stats = new ProxyTrafficStats();

        Call(stats);
        stats.RecordUnroutableDenial();

        stats.Clear();

        Assert.Empty(stats.Snapshot());
        Assert.Equal(0, stats.UnroutableDenied);
    }
}
