using RavensPort.App.ViewModels;

namespace RavensPort.Core.Tests.App;

/// <summary>
/// The Dashboard's presentation rules. The view model itself needs a vault and a connection pool to
/// construct, but the decisions worth pinning are pure: which rows are faded out, and how an
/// instant becomes the "2m ago" the tab is read in.
/// </summary>
public class DashboardViewModelTests
{
    private static EndpointStatusViewModel Endpoint(
        bool enabled = true, long calls = 0, long failed = 0, long denied = 0, int? agoMinutes = null) => new()
    {
        Kind = "Route",
        Name = "/app",
        Address = "/app",
        Enabled = enabled,
        Calls = calls,
        Failed = failed,
        Denied = denied,
        ClientCount = 0,
        LastUsedUtc = agoMinutes is null ? null : DateTimeOffset.UtcNow.AddMinutes(-agoMinutes.Value),
        KeyState = "key ok",
    };

    [Fact]
    public void ASwitchedOffOrNeverCalledEndpointIsFadedOut()
    {
        Assert.True(Endpoint(enabled: false, calls: 12, agoMinutes: 3).IsQuiet);
        Assert.True(Endpoint(calls: 0).IsQuiet);
        Assert.False(Endpoint(calls: 5, agoMinutes: 1).IsQuiet);
    }

    [Fact]
    public void AnEndpointWithProblemsIsNeverFadedOutHoweverQuietItIs()
    {
        // The row this matters for: a bridge nobody has got into because every attempt was
        // refused. It is the quietest row on the tab and the one most worth noticing, so the
        // "nothing happening here" fade must not swallow it.
        Assert.False(Endpoint(calls: 0, denied: 9).IsQuiet);
        Assert.False(Endpoint(enabled: false, failed: 3).IsQuiet);
    }

    [Fact]
    public void ProblemsReadAsProseAndAnUntouchedEndpointShowsADash()
    {
        Assert.Equal("—", Endpoint().ProblemsDisplay);
        Assert.Equal("—", Endpoint().CallsDisplay);
        Assert.Equal("3 failed", Endpoint(failed: 3).ProblemsDisplay);
        Assert.Equal("2 denied", Endpoint(denied: 2).ProblemsDisplay);
        Assert.Equal("3 failed, 2 denied", Endpoint(failed: 3, denied: 2).ProblemsDisplay);
    }

    [Fact]
    public void NeverUsedSaysSoRatherThanShowingAnEpoch()
    {
        Assert.Equal("never", DashboardViewModel.Ago(null));
        Assert.Equal("never", Endpoint().LastUsed);
    }

    [Theory]
    [InlineData(0, "just now")]
    [InlineData(45, "45s ago")]
    [InlineData(60 * 4, "4m ago")]
    [InlineData(60 * 60 * 3, "3h ago")]
    [InlineData(60 * 60 * 24 * 2, "2d ago")]
    public void APastMomentIsCoarseEnoughToReadAtAGlance(int secondsAgo, string expected) =>
        Assert.Equal(expected, DashboardViewModel.Ago(DateTimeOffset.UtcNow.AddSeconds(-secondsAgo)));

    [Fact]
    public void AMomentStillToComeReadsForwards()
    {
        // Key expiry is shown through the same helper, and "6d ago" for a key that lapses next
        // week would be exactly wrong.
        Assert.Equal("in 6d", DashboardViewModel.Ago(DateTimeOffset.UtcNow.AddDays(6).AddMinutes(1)));
        Assert.Equal("in 30m", DashboardViewModel.Ago(DateTimeOffset.UtcNow.AddMinutes(30).AddSeconds(1)));
    }
}
