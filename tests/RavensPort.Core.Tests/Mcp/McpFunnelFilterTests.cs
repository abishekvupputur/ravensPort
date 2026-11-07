using ModelContextProtocol.Protocol;
using RavensPort.Core.Models;

namespace RavensPort.Core.Tests.Mcp;

/// <summary>
/// Filters through the real pipeline: a blocked call must never reach the source, must tell the
/// agent why, and must leave the value out of the activity log.
/// </summary>
public class McpFunnelFilterTests : IAsyncLifetime
{
    private static readonly string AwsKey = "AKIA" + "Z7QW3XKPLM2N4R6T";

    private FakeMcpServer _upstream = null!;
    private FunnelTestHost _host = null!;
    private McpFunnelRecord _funnel = null!;

    public async Task InitializeAsync()
    {
        _upstream = await FakeMcpServer.StartAsync();
        _host = await FunnelTestHost.StartAsync();

        var source = await _host.AddRemoteSourceAsync("up", _upstream.Url);
        _funnel = await _host.AddFunnelAsync("filtered", new McpFunnelSource { SourceId = source.Id });
    }

    public async Task DisposeAsync()
    {
        await _host.DisposeAsync();
        await _upstream.DisposeAsync();
    }

    private Task SetFilterAsync(FilterAction personalData = FilterAction.Block) => _host.MutateAsync(store =>
        store.McpFunnels.Single(f => f.Id == _funnel.Id).Filters =
        [
            new FunnelFilter { Category = FilterCategory.Secrets, Level = FilterLevel.Balanced },
            new FunnelFilter { Category = FilterCategory.PersonalData, Level = FilterLevel.Balanced, Action = personalData },
        ]);

    [Fact]
    public async Task ABlockedCall_NeverReachesTheSource_AndSaysWhy()
    {
        await SetFilterAsync(FilterAction.Block);
        var client = await _host.ConnectAsync("filtered");

        var result = await FunnelTestHost.CallAsync(client, "up__echo", $"key {AwsKey}");
        var text = result.Content.OfType<TextContentBlock>().Single().Text;

        Assert.True(result.IsError);
        Assert.Contains("aws-access-token", text);
        Assert.DoesNotContain(AwsKey, text);
        Assert.False(_upstream.CallsByTool.ContainsKey("echo"));

        var log = File.ReadAllText(_host.ActivityLog.CurrentLogPath);
        Assert.Contains("blocked by the funnel's own filters", log);
        Assert.DoesNotContain(AwsKey, log);
    }

    [Fact]
    public async Task ACleanCall_GoesThrough()
    {
        await SetFilterAsync(FilterAction.Block);
        var client = await _host.ConnectAsync("filtered");

        Assert.Equal("hello", await FunnelTestHost.CallTextAsync(client, "up__echo", "hello"));
    }

    [Fact]
    public async Task Watch_LetsTheCallThrough_ButLogsIt()
    {
        await SetFilterAsync(personalData: FilterAction.Watch);
        var client = await _host.ConnectAsync("filtered");

        await FunnelTestHost.CallTextAsync(client, "up__echo", "write to jane.doe@example.com");

        Assert.Contains("watched by the funnel's own filters", File.ReadAllText(_host.ActivityLog.CurrentLogPath));
    }

    [Fact]
    public async Task AChangedFilter_AppliesToTheNextCall()
    {
        var client = await _host.ConnectAsync("filtered");
        await FunnelTestHost.CallTextAsync(client, "up__echo", $"key {AwsKey}");

        await SetFilterAsync(FilterAction.Block);

        Assert.True((await FunnelTestHost.CallAsync(client, "up__echo", $"key {AwsKey}")).IsError);
    }

    [Fact]
    public async Task AnUpstreamsOwnProfile_OverridesTheFunnels_ForCallsToThatUpstream()
    {
        var strict = new FilterProfileRecord { Name = "Strict", Filters = [new FunnelFilter { Category = FilterCategory.PersonalData, Level = FilterLevel.Balanced }] };
        var trusted = new FilterProfileRecord { Name = "Trusted", Filters = [new FunnelFilter { Category = FilterCategory.Secrets, Level = FilterLevel.Balanced }] };
        await _host.MutateAsync(store =>
        {
            store.FilterProfiles.AddRange([strict, trusted]);
            var funnel = store.McpFunnels.Single(f => f.Id == _funnel.Id);
            funnel.FilterProfileId = strict.Id;
            funnel.Sources.Single().FilterProfileId = trusted.Id;
        });
        var client = await _host.ConnectAsync("filtered");

        // Trusted for this upstream: personal data goes through, secrets still do not.
        Assert.Equal("write to jane.doe@example.com", await FunnelTestHost.CallTextAsync(client, "up__echo", "write to jane.doe@example.com"));
        Assert.True((await FunnelTestHost.CallAsync(client, "up__echo", $"key {AwsKey}")).IsError);

        // Without the override, the funnel's strict profile applies again.
        await _host.MutateAsync(store => store.McpFunnels.Single(f => f.Id == _funnel.Id).Sources.Single().FilterProfileId = null);
        Assert.True((await FunnelTestHost.CallAsync(client, "up__echo", "write to jane.doe@example.com")).IsError);
        Assert.Contains("blocked by funnel profile 'Strict'", File.ReadAllText(_host.ActivityLog.CurrentLogPath));
    }

    [Fact]
    public async Task AProfileDeletedUnderneath_BlocksTheCall()
    {
        await _host.MutateAsync(store => store.McpFunnels.Single(f => f.Id == _funnel.Id).FilterProfileId = Guid.NewGuid());
        var client = await _host.ConnectAsync("filtered");

        var result = await FunnelTestHost.CallAsync(client, "up__echo", "hello");

        Assert.True(result.IsError);
        Assert.Contains("no longer exists", result.Content.OfType<TextContentBlock>().Single().Text);
    }
}
