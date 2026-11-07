using RavensPort.Core.Filters;

namespace RavensPort.Core.Tests.Filters;

/// <summary>
/// Listing GPUs. Where a model runs, and the fallback to the CPU, are tested against a real (tiny)
/// model in <see cref="OnnxModelTests"/>.
/// </summary>
public class OnnxSessionsTests
{
    [Fact]
    public void DetectingGpus_NeverThrows_AndNeverListsTheSoftwareRenderer()
    {
        var gpus = OnnxSessions.DetectGpus();

        Assert.DoesNotContain(gpus, g => g.Name.Contains("Basic Render", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(gpus.Count, gpus.Select(g => g.Index).Distinct().Count());
    }
}
