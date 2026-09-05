using ModelContextProtocol.Protocol;
using RavensPort.Core.Models;

namespace RavensPort.Core.Tests.Mcp;

/// <summary>
/// The funnel with an old peer on one side or the other.
///
/// Adopting 2026-07-28 must not cost anything on either edge, and the two edges fail differently.
/// A source that will never be updated has to keep working, and must not start receiving fields it
/// has never heard of. An agent on an SDK from before the revision has to keep working too, and
/// has to survive the fields the funnel now always sends.
///
/// Neither is covered by the rest of the suite, which lets both ends negotiate the newest revision
/// they share and so only ever exercises the new one.
/// </summary>
public class McpFunnelBackwardCompatibilityTests : IAsyncLifetime
{
    /// <summary>The revision before the one this funnel now targets.</summary>
    private const string PreviousRevision = "2025-11-25";

    /// <summary>The revision this funnel targets.</summary>
    private const string CurrentRevision = "2026-07-28";

    private FakeMcpServer _upstream = null!;
    private FunnelTestHost _host = null!;

    public async Task InitializeAsync()
    {
        _upstream = await FakeMcpServer.StartAsync();
        _host = await FunnelTestHost.StartAsync();

        var source = await _host.AddRemoteSourceAsync("up", _upstream.Url);
        await _host.AddFunnelAsync("agent", new McpFunnelSource { SourceId = source.Id });
    }

    public async Task DisposeAsync()
    {
        await _host.DisposeAsync();
        await _upstream.DisposeAsync();
    }

    // ---- an agent from before the revision --------------------------------------------------

    [Fact]
    public async Task AnAgentPinnedToThePreviousRevisionStillGetsAWorkingFunnel()
    {
        var client = await _host.ConnectAsync("agent", protocolVersion: PreviousRevision);

        // Without this the rest of the test proves nothing: a pin that quietly failed would leave
        // the client on the current revision and every assertion below would still pass.
        Assert.Equal(PreviousRevision, client.NegotiatedProtocolVersion);

        var tools = (await client.ListToolsAsync()).Select(t => t.Name).ToList();
        Assert.Contains("up__echo", tools);

        Assert.Equal("hello", await FunnelTestHost.CallTextAsync(client, "up__echo", "hello"));

        var resources = await client.ListResourcesAsync();
        var read = await client.ReadResourceAsync(resources[0].Uri);
        Assert.NotEmpty(read.Contents);

        var prompts = await client.ListPromptsAsync();
        Assert.NotEmpty(prompts);
    }

    [Fact]
    public async Task TheNewCacheFieldsDoNotDisturbAnOlderAgent()
    {
        // The one thing this change puts on the wire that was not there before. ttlMs and
        // cacheScope go out on every list, to every client, because the funnel has no way to vary
        // its answer by the caller's revision and no reason to want one. An older client is
        // supposed to ignore fields it does not know; this is the assertion that it does, rather
        // than a claim that it should.
        var client = await _host.ConnectAsync("agent", protocolVersion: PreviousRevision);
        Assert.Equal(PreviousRevision, client.NegotiatedProtocolVersion);

        var tools = await client.ListToolsAsync(new ListToolsRequestParams());

        Assert.NotEmpty(tools.Tools);
        Assert.Equal(TimeSpan.Zero, tools.TimeToLive);
        Assert.Equal(CacheScope.Private, tools.CacheScope);
    }

    // ---- a source from before the revision --------------------------------------------------

    [Fact]
    public async Task ASourceStuckOnThePreviousRevisionIsStillUsable()
    {
        // This fake has never implemented server/discover -- it answers -32601, as a server
        // written against the older revision does -- so pinning the version it reports is the
        // whole of what makes it an old server. Everything the funnel offers has to survive that.
        _upstream.PinnedProtocolVersion = PreviousRevision;

        var client = await _host.ConnectAsync("agent");

        var tools = (await client.ListToolsAsync()).Select(t => t.Name).ToList();
        Assert.Contains("up__echo", tools);

        Assert.Equal("hello", await FunnelTestHost.CallTextAsync(client, "up__echo", "hello"));

        var resources = await client.ListResourcesAsync();
        Assert.NotEmpty(resources);
    }

    [Fact]
    public async Task AnOldSourceIsNeverSentTheNewRoundTripFields()
    {
        // The regression this guards against is subtle: the funnel relays inputResponses and
        // requestState, and a relay that wrote them as explicit nulls would be handing a server
        // that predates them two parameters it has never seen. They have to be absent, not empty.
        _upstream.PinnedProtocolVersion = PreviousRevision;

        var client = await _host.ConnectAsync("agent", protocolVersion: PreviousRevision);

        await FunnelTestHost.CallTextAsync(client, "up__echo", "hello");

        Assert.Collection(_upstream.ReceivedRequestState, Assert.Null);
        Assert.Collection(_upstream.ReceivedInputResponses, Assert.Null);
    }

    // ---- a funnel whose sources disagree ----------------------------------------------------

    [Fact]
    public async Task SourcesOnDifferentRevisionsCoexistInOneFunnel()
    {
        // The realistic case, and the one the connection pool's shape already decides: it keys a
        // client per (funnel, source), so each source negotiates with its own upstream and knows
        // nothing about what the others agreed to. A funnel is therefore free to be a mix, and no
        // source can drag another down to its revision.
        await using var oldUpstream = await FakeMcpServer.StartAsync();
        oldUpstream.PinnedProtocolVersion = PreviousRevision;

        var oldSource = await _host.AddRemoteSourceAsync("old", oldUpstream.Url);
        await _host.AddFunnelAsync("mixed",
            new McpFunnelSource { SourceId = oldSource.Id },
            new McpFunnelSource { SourceId = SourceIdFor("up") });

        var client = await _host.ConnectAsync("mixed");

        var tools = (await client.ListToolsAsync()).Select(t => t.Name).ToList();

        Assert.Contains("old__echo", tools);
        Assert.Contains("up__echo", tools);

        // Both are callable, and each call lands on its own upstream.
        Assert.Equal("from old", await FunnelTestHost.CallTextAsync(client, "old__echo", "from old"));
        Assert.Equal("from new", await FunnelTestHost.CallTextAsync(client, "up__echo", "from new"));

        Assert.Equal(1, oldUpstream.CallsByTool.GetValueOrDefault("echo"));
        Assert.Equal(1, _upstream.CallsByTool.GetValueOrDefault("echo"));
    }

    [Fact]
    public async Task WhatTheAgentNegotiatesDoesNotDependOnTheSources()
    {
        // The question a mixed funnel raises: which revision does the agent end up on? Neither of
        // the upstreams' -- the downstream handshake is between the agent and this funnel's own
        // server, and the sources are not party to it. An old source does not pull the agent back,
        // which is what makes a mixed funnel safe to assemble.
        await using var oldUpstream = await FakeMcpServer.StartAsync();
        oldUpstream.PinnedProtocolVersion = PreviousRevision;

        var oldSource = await _host.AddRemoteSourceAsync("old", oldUpstream.Url);
        await _host.AddFunnelAsync("mixed",
            new McpFunnelSource { SourceId = oldSource.Id },
            new McpFunnelSource { SourceId = SourceIdFor("up") });

        var client = await _host.ConnectAsync("mixed");

        Assert.Equal(CurrentRevision, client.NegotiatedProtocolVersion);

        // And the merged list is stamped by the funnel, not inherited from whichever source
        // happened to answer first.
        var listed = await client.ListToolsAsync(new ListToolsRequestParams());
        Assert.Equal(TimeSpan.Zero, listed.TimeToLive);
        Assert.Equal(CacheScope.Private, listed.CacheScope);
    }

    private Guid SourceIdFor(string alias) =>
        _host.Cache.Current.McpSources.First(s => s.Alias == alias).Id;

    // ---- both ends old ----------------------------------------------------------------------

    [Fact]
    public async Task AnOldAgentThroughToAnOldSourceStillWorksEndToEnd()
    {
        _upstream.PinnedProtocolVersion = PreviousRevision;

        var client = await _host.ConnectAsync("agent", protocolVersion: PreviousRevision);

        Assert.Equal("hello", await FunnelTestHost.CallTextAsync(client, "up__echo", "hello"));
        Assert.Equal(1, _upstream.CallsByTool.GetValueOrDefault("echo"));
    }
}
