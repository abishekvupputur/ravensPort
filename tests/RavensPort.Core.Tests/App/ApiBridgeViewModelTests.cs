using RavensPort.UI.ViewModels;
using RavensPort.Core.Diagnostics;
using RavensPort.Core.Mcp;
using RavensPort.Core.Models;
using RavensPort.Core.Storage;
using RavensPort.Core.Vault;
using RavensPort.UI.Services;

namespace RavensPort.Core.Tests.App;

/// <summary>
/// The bridge editor's save path — where a manifest actually reaches
/// <see cref="ManifestLocalStore"/>, now that it is the manifest's only home. Constructed for real
/// rather than mocked: every dependency is a plain CLR object with no UI-framework requirement, so the
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

    /// <summary>
    /// Where saves leave their backup copy. Redirected for the same reason the manifest root is:
    /// every save writes one, and without this the suite wrote them into the developer's own
    /// %LocalAppData%.
    /// </summary>
    private readonly string _backupRoot = Path.Combine(Path.GetTempPath(), "ravensport-bridge-vm-backups-" + Guid.NewGuid());

    public ApiBridgeViewModelTests()
    {
        ManifestLocalStore.RootOverride = _manifestRoot;
        ManifestBackupStore.RootOverride = _backupRoot;

        _activityLog = new ActivityLog(_logDir);
        _cache = new ConfigStoreCache(InMemoryVault.Empty());
        _loopback = new LoopbackHttpClient(_mtls, _cache, _activityLog);
        _pool = new McpSourceConnectionPool(_cache, _activityLog, _mtls, _loopback);
    }

    public async ValueTask DisposeAsync()
    {
        ManifestLocalStore.RootOverride = null;
        ManifestBackupStore.RootOverride = null;

        await _pool.DisposeAsync();
        _loopback.Dispose();
        _mtls.Dispose();

        if (Directory.Exists(_manifestRoot)) Directory.Delete(_manifestRoot, recursive: true);
        if (Directory.Exists(_backupRoot)) Directory.Delete(_backupRoot, recursive: true);
        if (Directory.Exists(_logDir)) Directory.Delete(_logDir, recursive: true);
    }

    private async Task<ApiBridgeViewModel> NewViewModelAsync(ScriptedDesktop? desktop = null)
    {
        await _cache.InitializeAsync();
        await _cache.MutateAsync(store => store.Routes.Add(_route));

        IClipboardService clipboard = desktop ?? (IClipboardService)new NoDesktop();
        IFileOpenPicker openPicker = desktop ?? (IFileOpenPicker)new NoDesktop();
        IFileSavePicker savePicker = desktop ?? (IFileSavePicker)new NoDesktop();
        IOpenApiOperationPicker operationPicker = desktop ?? (IOpenApiOperationPicker)new NoDesktop();

        return new ApiBridgeViewModel(
            _cache, _pool, _activityLog, _mtls,
            clipboard, openPicker, savePicker, operationPicker);
    }

    /// <summary>Adds the sample bridge through the form, the way a user would.</summary>
    private static async Task AddTrackerAsync(ApiBridgeViewModel viewModel, string name = "Tracker")
    {
        viewModel.NewBridgeName = name;
        viewModel.NewBridgeRoute = viewModel.Routes[0];
        viewModel.ManifestJson = SampleManifest();
        await viewModel.SaveBridgeCommand.ExecuteAsync(null);
    }

    private static string OpenApiDocument(string paths) => $$"""
        {
          "openapi": "3.0.0",
          "info": { "title": "Test API", "version": "1" },
          "paths": { {{paths}} }
        }
        """;

    private const string OneOperation = """
        "/things": {
          "get": {
            "operationId": "listThings",
            "summary": "List things",
            "responses": { "200": { "description": "ok" } }
          }
        }
        """;

    /// <summary>
    /// The desktop as a script: what the next file pick returns, where a save goes, and what the
    /// operation picker answers. NoDesktop is the "user cancelled everything" case; this is the
    /// one where they did not.
    /// </summary>
    private sealed class ScriptedDesktop : IClipboardService, IFileOpenPicker, IFileSavePicker, IOpenApiOperationPicker
    {
        public PickedFile? NextFile { get; set; }

        public Exception? PickFailure { get; set; }

        public string? NextSavePath { get; set; }

        public Func<OpenApiOperationPickerViewModel, OpenApiImportResult?> Operations { get; set; } = _ => null;

        public Task SetTextAsync(string text) => Task.CompletedTask;

        public Task<PickedFile?> PickFileAsync(string title, IReadOnlyList<string> extensions, string filterName) =>
            PickFailure is { } failure ? Task.FromException<PickedFile?>(failure) : Task.FromResult(NextFile);

        public Task<string?> PickSavePathAsync(
            string title, string suggestedFileName, string extension, string filterName) =>
            Task.FromResult(NextSavePath);

        public Task<OpenApiImportResult?> PickAsync(OpenApiOperationPickerViewModel viewModel) =>
            Task.FromResult(Operations(viewModel));
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

    /// <summary>
    /// A manifest that does not parse is refused at save, and the live validator says so as it is
    /// typed — the editor is where someone finds out, not the first tools/call.
    /// </summary>
    [Fact]
    public async Task AnInvalidManifestIsRefusedAndTheEditorSaysWhy()
    {
        var viewModel = await NewViewModelAsync();
        viewModel.NewBridgeName = "Tracker";
        viewModel.NewBridgeRoute = viewModel.Routes[0];

        viewModel.ManifestJson = "{ \"version\": 1, \"tools\": [";
        Assert.False(viewModel.ManifestIsValid);
        Assert.False(string.IsNullOrWhiteSpace(viewModel.ManifestStatus));

        await viewModel.SaveBridgeCommand.ExecuteAsync(null);

        Assert.Empty(_cache.Current.McpApiBridges);
        Assert.Equal(viewModel.ManifestStatus, viewModel.StatusMessage);

        viewModel.ManifestJson = SampleManifest();
        Assert.True(viewModel.ManifestIsValid);
        Assert.StartsWith("Valid", viewModel.ManifestStatus, StringComparison.Ordinal);
    }

    /// <summary>
    /// A bridge needs a route to call through and a slug no other bridge has; either missing is
    /// refused before anything is written.
    /// </summary>
    [Fact]
    public async Task ABridgeWithNoRouteOrATakenSlugIsRefused()
    {
        var viewModel = await NewViewModelAsync();

        viewModel.NewBridgeName = "Tracker";
        viewModel.NewBridgeRoute = null;
        viewModel.ManifestJson = SampleManifest();
        await viewModel.SaveBridgeCommand.ExecuteAsync(null);
        Assert.Empty(_cache.Current.McpApiBridges);

        await AddTrackerAsync(viewModel);
        Assert.Single(_cache.Current.McpApiBridges);

        viewModel.NewBridgeName = "Another";
        viewModel.NewBridgeSlug = "tracker";
        viewModel.NewBridgeRoute = viewModel.Routes[0];
        viewModel.ManifestJson = SampleManifest();
        await viewModel.SaveBridgeCommand.ExecuteAsync(null);

        Assert.Single(_cache.Current.McpApiBridges);
    }

    /// <summary>
    /// The slug derived from a name collapses whatever punctuation it had into single hyphens, so
    /// the endpoint a client is given is one somebody could type.
    /// </summary>
    [Fact]
    public async Task ASlugDerivedFromANameHasNoDoubledHyphens()
    {
        var viewModel = await NewViewModelAsync();

        await AddTrackerAsync(viewModel, "My  --  Tracker!!");

        var bridge = Assert.Single(_cache.Current.McpApiBridges);
        Assert.DoesNotContain("--", bridge.Slug, StringComparison.Ordinal);
        Assert.StartsWith("my-", bridge.Slug, StringComparison.Ordinal);
    }

    /// <summary>
    /// Deleting a bridge takes with it every funnel source that exposed it, and every funnel's link
    /// to those sources — a funnel left pointing at a bridge that is gone would fail on every call.
    /// </summary>
    [Fact]
    public async Task DeletingABridgeRemovesTheFunnelSourcesThatExposedIt()
    {
        var viewModel = await NewViewModelAsync();
        await AddTrackerAsync(viewModel);

        var bridge = Assert.Single(_cache.Current.McpApiBridges);
        var source = new McpSourceRecord
        {
            Name = "tracker",
            Alias = "tracker",
            Kind = McpSourceKind.ApiBridge,
            BridgeId = bridge.Id,
        };

        await _cache.MutateAsync(store =>
        {
            store.McpSources.Add(source);
            store.McpFunnels.Add(new McpFunnelRecord
            {
                Name = "funnel",
                Slug = "funnel",
                Key = ProxyKey.Generate(),
                Sources = [new McpFunnelSource { SourceId = source.Id }],
            });
        });

        viewModel.Reload();
        viewModel.EditManifestCommand.Execute(viewModel.Bridges[0]);

        await viewModel.DeleteBridgeCommand.ExecuteAsync(viewModel.Bridges[0]);

        Assert.Empty(_cache.Current.McpApiBridges);
        Assert.Empty(_cache.Current.McpSources);
        Assert.Empty(Assert.Single(_cache.Current.McpFunnels).Sources);
        Assert.Contains("along with 1 funnel source(s)", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Null(viewModel.EditingBridge);
        Assert.Null(ManifestLocalStore.TryLoad(bridge.Id));

        // Nothing selected is nothing to delete.
        await viewModel.DeleteBridgeCommand.ExecuteAsync(null);
    }

    /// <summary>A bridge nothing exposes says where its clients will now be turned away.</summary>
    [Fact]
    public async Task DeletingABridgeNoFunnelUsesSaysClientsWillBeRefused()
    {
        var viewModel = await NewViewModelAsync();
        await AddTrackerAsync(viewModel);

        await viewModel.DeleteBridgeCommand.ExecuteAsync(viewModel.Bridges[0]);

        Assert.Empty(_cache.Current.McpApiBridges);
        Assert.Contains("deleted — clients pointed at", viewModel.StatusMessage, StringComparison.Ordinal);
    }

    /// <summary>
    /// Leaving the editor says whether a bridge was being edited, so "nothing changed" is not
    /// mistaken for "the edit was discarded".
    /// </summary>
    [Fact]
    public async Task CancellingTheEditorSaysWhatWasLeft()
    {
        var viewModel = await NewViewModelAsync();
        await AddTrackerAsync(viewModel);

        viewModel.CancelEditCommand.Execute(null);
        Assert.Equal("Editor cleared.", viewModel.StatusMessage);

        viewModel.EditManifestCommand.Execute(viewModel.Bridges[0]);
        Assert.NotNull(viewModel.EditingBridge);

        viewModel.CancelEditCommand.Execute(null);
        Assert.StartsWith("Stopped editing 'Tracker'", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Equal("", viewModel.ManifestJson);

        viewModel.EditManifestCommand.Execute(null);
        Assert.Null(viewModel.EditingBridge);

        viewModel.RefreshCommand.Execute(null);
        Assert.Equal("Refreshed — 1 bridge(s).", viewModel.StatusMessage);
    }

    /// <summary>
    /// A manifest file goes straight into the editor, and a file that cannot be read is reported
    /// rather than thrown.
    /// </summary>
    [Fact]
    public async Task ImportingAManifestFileLoadsItIntoTheEditor()
    {
        var desktop = new ScriptedDesktop { NextFile = new PickedFile("tracker.json", SampleManifest()) };
        var viewModel = await NewViewModelAsync(desktop);

        await viewModel.ImportManifestCommand.ExecuteAsync(null);

        Assert.Equal("Loaded tracker.json into the editor.", viewModel.StatusMessage);
        Assert.Equal("tracker.json", viewModel.NewBridgeManifestOrigin);
        Assert.True(viewModel.ManifestIsValid);

        desktop.PickFailure = new IOException("the file is locked");
        await viewModel.ImportManifestCommand.ExecuteAsync(null);
        Assert.Equal("Could not read that file: the file is locked", viewModel.StatusMessage);

        await viewModel.ImportOpenApiCommand.ExecuteAsync(null);
        Assert.Equal("Could not read that file: the file is locked", viewModel.StatusMessage);
    }

    /// <summary>
    /// An OpenAPI spec becomes a draft manifest in the editor, through the operation picker; a spec
    /// with nothing to import, and a picker that is cancelled, both leave the editor alone.
    /// </summary>
    [Fact]
    public async Task ImportingAnOpenApiSpecDraftsAManifest()
    {
        var spec = OpenApiDocument(OneOperation);
        var desktop = new ScriptedDesktop
        {
            NextFile = new PickedFile("spec.json", spec),
            Operations = _ => OpenApiImporter.Convert(spec, "spec.json"),
        };
        var viewModel = await NewViewModelAsync(desktop);

        await viewModel.ImportOpenApiCommand.ExecuteAsync(null);

        Assert.StartsWith("Imported spec.json", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Equal("openapi:spec.json", viewModel.NewBridgeManifestOrigin);
        Assert.True(viewModel.ManifestIsValid);

        // Cancelled at the picker: the draft already in the editor stays.
        var draft = viewModel.ManifestJson;
        desktop.Operations = _ => null;
        await viewModel.ImportOpenApiCommand.ExecuteAsync(null);
        Assert.Equal(draft, viewModel.ManifestJson);

        // Not a spec at all.
        desktop.NextFile = new PickedFile("notes.json", "{ \"hello\": 1 }");
        await viewModel.ImportOpenApiCommand.ExecuteAsync(null);
        Assert.StartsWith("Could not import notes.json", viewModel.StatusMessage, StringComparison.Ordinal);

        // A spec the conversion refuses.
        desktop.NextFile = new PickedFile("spec.json", spec);
        desktop.Operations = _ => OpenApiImporter.Convert("{ not json", "spec.json");
        await viewModel.ImportOpenApiCommand.ExecuteAsync(null);
        Assert.StartsWith("Could not import spec.json", viewModel.StatusMessage, StringComparison.Ordinal);

        // No file chosen.
        desktop.NextFile = null;
        await viewModel.ImportOpenApiCommand.ExecuteAsync(null);
        Assert.Equal(draft, viewModel.ManifestJson);
    }

    /// <summary>The sample loads into the editor, and saving it writes the file the user chose.</summary>
    [Fact]
    public async Task TheSampleManifestCanBeLoadedAndSaved()
    {
        var target = Path.Combine(_backupRoot, "sample.json");
        Directory.CreateDirectory(_backupRoot);

        var desktop = new ScriptedDesktop();
        var viewModel = await NewViewModelAsync(desktop);

        viewModel.LoadSampleCommand.Execute(null);
        Assert.True(viewModel.ManifestIsValid);

        await viewModel.SaveSampleCommand.ExecuteAsync(null);
        Assert.False(File.Exists(target));

        desktop.NextSavePath = target;
        await viewModel.SaveSampleCommand.ExecuteAsync(null);

        Assert.True(File.Exists(target));
        Assert.Equal($"Sample manifest saved to {target}.", viewModel.StatusMessage);

        desktop.NextSavePath = Path.Combine(target, "cannot-be-under-a-file.json");
        await viewModel.SaveSampleCommand.ExecuteAsync(null);
        Assert.StartsWith("Could not save the sample", viewModel.StatusMessage, StringComparison.Ordinal);
    }
}
