using RavensPort.Core.Models;

namespace RavensPort.Core.Tests.Filters;

/// <summary>Which filters a funnel call runs: the upstream's profile, else the funnel's, else none.</summary>
public class FilterProfilesTests
{
    private static readonly FilterProfileRecord Strict = new() { Name = "Strict", Filters = [new FunnelFilter { Category = FilterCategory.PersonalData, Level = FilterLevel.Balanced }] };
    private static readonly FilterProfileRecord Trusted = new() { Name = "Trusted", Filters = [new FunnelFilter { Category = FilterCategory.Secrets, Level = FilterLevel.Balanced }] };

    private static ConfigStore Store() => new() { FilterProfiles = [Strict, Trusted] };

    [Fact]
    public void TheUpstreamsProfile_WinsOverTheFunnels()
    {
        var funnel = new McpFunnelRecord { Name = "f", Slug = "f", FilterProfileId = Strict.Id };
        var link = new McpFunnelSource { FilterProfileId = Trusted.Id };

        var choice = FilterProfiles.Choose(Store(), funnel, link);

        Assert.Same(Trusted.Filters, choice.Filters);
        Assert.Equal("upstream profile 'Trusted'", choice.Source);
    }

    [Fact]
    public void AnUpstreamWithoutOne_GetsTheFunnelsProfile()
    {
        var funnel = new McpFunnelRecord { Name = "f", Slug = "f", FilterProfileId = Strict.Id };

        Assert.Same(Strict.Filters, FilterProfiles.Choose(Store(), funnel, new McpFunnelSource()).Filters);
    }

    [Fact]
    public void AFunnelWithNoProfile_FallsBackToItsOwnFilters_ThenToNone()
    {
        var legacy = new McpFunnelRecord { Name = "f", Slug = "f", Filters = [new FunnelFilter { Category = FilterCategory.Secrets, Level = FilterLevel.Balanced }] };
        var bare = new McpFunnelRecord { Name = "g", Slug = "g" };

        Assert.Same(legacy.Filters, FilterProfiles.Choose(Store(), legacy, new McpFunnelSource()).Filters);
        Assert.Empty(FilterProfiles.Choose(Store(), bare, new McpFunnelSource()).Filters);
    }

    [Fact]
    public void AProfileThatNoLongerExists_IsReported_NotSkippedToALaxerOne()
    {
        var funnel = new McpFunnelRecord { Name = "f", Slug = "f", FilterProfileId = Strict.Id };
        var link = new McpFunnelSource { FilterProfileId = Guid.NewGuid() };

        Assert.Throws<FilterProfileMissingException>(() => FilterProfiles.Choose(Store(), funnel, link));
    }
}
