using RavensPort.App.ViewModels;
using RavensPort.Core.Diagnostics;
using RavensPort.Core.Mcp;
using RavensPort.Core.Models;
using RavensPort.Core.Storage;
using RavensPort.Core.Vault;

namespace RavensPort.Core.Tests.App;

/// <summary>
/// The bridge editor's save path — where a manifest actually reaches
/// <see cref="ManifestLocalStore"/>, now that it is the manifest's only home. Constructed for real
/// rather than mocked: every dependency is a plain CLR object with no WPF requirement, so the
/// interesting failure mode (a local write that fails must abort the save, not silently lose the
/// manifest) is worth exercising through the actual view model rather than through
/// <c>ManifestLocalStore</c> alone.
/// </summary>
[Collection(RavensPort.Core.Tests.Storage.ManifestStoreCollection.Name)]
public sealed class ApiBridgeViewModelTests : IAsyncDisposable
{
    private const string SampleTool = "list_tasks";

    private readonly string _manifestRoot = Path.Combine(Path.GetTempPath(), "ravensport-bridge-vm-manifests-" + Guid.NewGuid());
    private readonly string _logDir = Path.Combine(Path.GetTempPath(), "ravensport-bridge-vm-logs-" + Guid.NewGuid());

    private readonly ConfigStoreCache _cache;
    private readonly McpSourceConnectionPool _pool;
    private readonly ActivityLog _activityLog;
    private readonly KestrelMtlsState _mtls = new();
    private readonly LoopbackHttpClient _loopback;
    private readonly RouteMapping _route = new() { PathPrefix = "/api" };

    public ApiBridgeViewModelTests()
    {
        ManifestLocalStore.RootOverride = _manifestRoot;

        _activityLog = new ActivityLog(_logDir);
        _cache = new ConfigStoreCache(InMemoryVault.Empty());
        _loopback = new LoopbackHttpClient(_mtls, _cache, _activityLog);
        _pool = new McpSourceConnectionPool(_cache, _activityLog, _mtls, _loopback);
    }

    public async ValueTask DisposeAsync()
    {
        ManifestLocalStore.RootOverride = null;

        await _pool.DisposeAsync();
        _loopback.Dispose();
        _mtls.Dispose();

        if (Directory.Exists(_manifestRoot)) Directory.Delete(_manifestRoot, recursive: true);
        if (Directory.Exists(_logDir)) Directory.Delete(_logDir, recursive: true);
    }

    private async Task<ApiBridgeViewModel> NewViewModelAsync()
    {
        await _cache.InitializeAsync();
        await _cache.MutateAsync(store => store.Routes.Add(_route));

        return new ApiBridgeViewModel(_cache, _pool, _activityLog, _mtls);
    }

    private static string SampleManifest(string toolName = SampleTool) => $$"""
        {
          "version": 1,
          "tools": [
            { "name": "{{toolName}}", "request": { "method": "GET", "path": "/things" } }
          ]
        }
        """;

    [Fact]
    public async Task SavingANewBridgeWritesItsManifestToLocalStorage()
    {
        var viewModel = await NewViewModelAsync();
        viewModel.NewBridgeName = "Tracker";
        viewModel.NewBridgeRoute = viewModel.Routes[0];
        viewModel.ManifestJson = SampleManifest();

        await viewModel.SaveBridgeCommand.ExecuteAsync(null);

        var bridge = Assert.Single(_cache.Current.McpApiBridges);
        Assert.Equal("Tracker", bridge.Name);

        var onDisk = ManifestLocalStore.TryLoad(bridge.Id);
        Assert.NotNull(onDisk);
        Assert.Equal([SampleTool], onDisk.Tools.Select(t => t.Name));
    }

    [Fact]
    public async Task ANameThatFailsValidationAddsNoBridgeAndWritesNoManifest()
    {
        var viewModel = await NewViewModelAsync();
        viewModel.NewBridgeName = ""; // required
        viewModel.NewBridgeRoute = viewModel.Routes[0];
        viewModel.ManifestJson = SampleManifest();

        await viewModel.SaveBridgeCommand.ExecuteAsync(null);

        Assert.Empty(_cache.Current.McpApiBridges);
        Assert.Contains("required", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WhenTheLocalManifestWriteFailsTheBridgeIsNeverAdded()
    {
        // A file standing where ManifestLocalStore needs a directory: Directory.CreateDirectory
        // fails reliably and portably, without relying on ACLs or a full disk.
        var blocker = Path.Combine(_manifestRoot, "blocked-by-a-file");
        Directory.CreateDirectory(_manifestRoot);
        File.WriteAllText(blocker, "");
        ManifestLocalStore.RootOverride = Path.Combine(blocker, "bridges");

        var viewModel = await NewViewModelAsync();
        viewModel.NewBridgeName = "Tracker";
        viewModel.NewBridgeRoute = viewModel.Routes[0];
        viewModel.ManifestJson = SampleManifest();

        await viewModel.SaveBridgeCommand.ExecuteAsync(null);

        Assert.Empty(_cache.Current.McpApiBridges);
        Assert.Contains("Could not save the manifest", viewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EditingAnExistingBridgesManifestOverwritesTheLocalFile()
    {
        var viewModel = await NewViewModelAsync();
        viewModel.NewBridgeName = "Tracker";
        viewModel.NewBridgeRoute = viewModel.Routes[0];
        viewModel.ManifestJson = SampleManifest("original_tool");
        await viewModel.SaveBridgeCommand.ExecuteAsync(null);

        var bridge = Assert.Single(_cache.Current.McpApiBridges);

        viewModel.EditManifestCommand.Execute(viewModel.Bridges[0]);
        viewModel.ManifestJson = SampleManifest("replacement_tool");
        await viewModel.SaveBridgeCommand.ExecuteAsync(null);

        var reloadedBridge = Assert.Single(_cache.Current.McpApiBridges);
        Assert.Equal(bridge.Id, reloadedBridge.Id);

        var onDisk = ManifestLocalStore.TryLoad(bridge.Id);
        Assert.Equal(["replacement_tool"], onDisk!.Tools.Select(t => t.Name));
    }
}
