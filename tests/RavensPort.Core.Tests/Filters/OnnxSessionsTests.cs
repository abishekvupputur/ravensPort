using RavensPort.Core.Diagnostics;
using RavensPort.Core.Filters;
using RavensPort.Core.Models;

namespace RavensPort.Core.Tests.Filters;

/// <summary>Where an ONNX model runs, and what happens when the chosen GPU cannot run it.</summary>
public class OnnxSessionsTests
{
    [Fact]
    public void DetectingGpus_NeverThrows_AndNeverListsTheSoftwareRenderer()
    {
        var gpus = OnnxSessions.DetectGpus();

        Assert.DoesNotContain(gpus, g => g.Name.Contains("Basic Render", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(gpus.Count, gpus.Select(g => g.Index).Distinct().Count());
    }

    /// <summary>
    /// The user's rule: a GPU that cannot start the model falls back to the CPU, and the activity
    /// log says so. Needs a real model file (RAVENSPORT_ONNX_TEST_MODEL, any .onnx); skipped otherwise.
    /// </summary>
    [Fact]
    public void AGpuThatIsNotThere_FallsBackToTheCpu_AndTheActivityLogSaysSo()
    {
        var path = Environment.GetEnvironmentVariable("RAVENSPORT_ONNX_TEST_MODEL");
        if (string.IsNullOrEmpty(path)) return;
        var model = OnnxModelFiles.Find(path, out var error)?.Model ?? throw new InvalidOperationException(error);

        var log = new ActivityLog(Path.Combine(Path.GetTempPath(), $"ravensport-onnx-logs-{Guid.NewGuid()}"));

        using var session = OnnxSessions.Open(model, OnnxDevice.Gpu, gpuIndex: 99, "test model", log, out var used);

        Assert.StartsWith("CPU (fell back", used);
        Assert.Contains(log.GetRecent(10), line => line.Contains("FILTERS model 'test model' could not start on GPU 99") && line.Contains("running on the CPU instead"));
        Assert.Contains("fell back", File.ReadAllText(log.ErrorLogPath));
    }
}
