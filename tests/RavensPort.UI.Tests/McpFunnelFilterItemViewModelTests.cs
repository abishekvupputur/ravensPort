using RavensPort.Core.Models;
using RavensPort.UI.ViewModels;

namespace RavensPort.UI.Tests;

public class McpFunnelFilterItemViewModelTests
{
    private static McpFunnelRecord Funnel() => new() { Name = "f", Slug = "f" };

    [Fact]
    public void AnUntouchedFunnel_StoresNoFilters()
    {
        var funnel = Funnel();

        var row = new McpFunnelFilterItemViewModel(funnel, FilterCategory.Secrets, _ => { });

        Assert.Equal(FilterLevel.Off, row.Level);
        Assert.Empty(funnel.Filters);
    }

    [Fact]
    public void ChangingTheLevel_WritesTheRecord_AndReportsIt()
    {
        var funnel = Funnel();
        string? reported = null;
        var row = new McpFunnelFilterItemViewModel(funnel, FilterCategory.Entities, m => reported = m);

        row.Level = FilterLevel.Strict;
        row.Action = FilterAction.Watch;

        var stored = Assert.Single(funnel.Filters);
        Assert.Equal(FilterLevel.Strict, stored.Level);
        Assert.Equal(FilterAction.Watch, stored.Action);
        Assert.NotNull(reported);
    }

    [Fact]
    public void AStoredFilter_IsShownAsSaved()
    {
        var funnel = Funnel();
        funnel.Filters.Add(new FunnelFilter { Category = FilterCategory.PersonalData, Level = FilterLevel.Balanced, Action = FilterAction.Watch });

        var row = new McpFunnelFilterItemViewModel(funnel, FilterCategory.PersonalData, _ => { });

        Assert.Equal(FilterLevel.Balanced, row.Level);
        Assert.Equal(FilterAction.Watch, row.Action);
    }
}
