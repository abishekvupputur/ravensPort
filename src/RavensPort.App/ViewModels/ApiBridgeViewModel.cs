using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RavensPort.Core.Diagnostics;
using RavensPort.Core.Mcp;
using RavensPort.Core.Models;
using RavensPort.Core.Storage;

namespace RavensPort.App.ViewModels;

/// <summary>
/// The API to MCP tab: turn one of this proxy's routes into an MCP endpoint an agent can drive,
/// from a manifest the user writes.
///
/// Like the funnel tab and unlike the routes tab, there is no YARP config to rebuild — the bridge
/// reads its manifest from the store on every request, so saving is all a change needs. What does
/// need telling is the connection pool, since a funnel pooling this bridge holds a session whose
/// headers were fixed when it connected.
/// </summary>
public sealed partial class ApiBridgeViewModel : ObservableObject
{
    private readonly ConfigStoreCache _configStoreCache;
    private readonly McpSourceConnectionPool _connectionPool;
    private readonly ActivityLog _activityLog;
    private readonly KestrelMtlsState _mtlsState;

    public ObservableCollection<ApiBridgeItemViewModel> Bridges { get; } = [];
    public ObservableCollection<RouteMapping> Routes { get; } = [];

    /// <summary>One line per tool the pasted manifest would serve, variants expanded.</summary>
    public ObservableCollection<string> ManifestPreview { get; } = [];

    [ObservableProperty] private bool _isEnabled;

    [ObservableProperty] private string _newBridgeName = "";
    [ObservableProperty] private string _newBridgeSlug = "";
    [ObservableProperty] private RouteMapping? _newBridgeRoute;

    /// <summary>
    /// How long the key issued to a new bridge stays valid. Never by default, matching what a
    /// machine-local endpoint named in a config file needs.
    /// </summary>
    [ObservableProperty] private ProxyKeyLifetime _newBridgeKeyLifetime = ProxyKeyLifetime.Never;

    /// <summary>The manifest editor's text. Everything is parsed from here, however it got here.</summary>
    [ObservableProperty] private string _manifestJson = "";

    /// <summary>Set while an existing bridge's manifest is being replaced rather than a new one added.</summary>
    [ObservableProperty] private ApiBridgeItemViewModel? _editingBridge;

    [ObservableProperty] private string _manifestStatus = "Paste a manifest, import one, or load the sample.";
    [ObservableProperty] private bool _manifestIsValid;

    [ObservableProperty] private string _statusMessage = "Ready.";

    [SuppressMessage("Major Code Smell", "S2325:Methods and properties that don't access instance data should be static",
        Justification = "Bound with {Binding} from XAML, which resolves instance members off the DataContext only. A static here compiles and then binds to nothing at runtime.")]
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Bound with {Binding} from XAML, which resolves instance members off the DataContext only. A static here compiles and then binds to nothing at runtime.")]
    public IReadOnlyList<ProxyKeyLifetime> KeyLifetimes => ProxyKeyLifetime.All;

    public bool HasBridges => Bridges.Count > 0;
    public bool HasNoBridges => Bridges.Count == 0;
    public bool HasRoutes => Routes.Count > 0;
    public bool HasNoRoutes => Routes.Count == 0;

    public bool IsEditingExisting => EditingBridge is not null;

    public string SaveButtonLabel => EditingBridge is null ? "Add bridge" : $"Replace '{EditingBridge.Name}' manifest";

    public ApiBridgeViewModel(
        ConfigStoreCache configStoreCache,
        McpSourceConnectionPool connectionPool,
        ActivityLog activityLog,
        KestrelMtlsState mtlsState)
    {
        _configStoreCache = configStoreCache;
        _connectionPool = connectionPool;
        _activityLog = activityLog;
        _mtlsState = mtlsState;

        Bridges.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasBridges));
            OnPropertyChanged(nameof(HasNoBridges));
        };
        Routes.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasRoutes));
            OnPropertyChanged(nameof(HasNoRoutes));
        };

        Reload();
    }

    /// <summary>
    /// Re-reads everything from the shared config cache. This view model is a singleton, and a
    /// route added on the Routes tab afterwards has to appear in the picker here.
    /// </summary>
    public void Reload()
    {
        var store = _configStoreCache.Current;
        var selectedRouteId = NewBridgeRoute?.Id;

        IsEnabled = store.Settings.McpApiBridgeEnabled;

        Routes.Clear();
        foreach (var route in store.Routes) Routes.Add(route);

        Bridges.Clear();

        // The bound scheme, not the configured one — see RoutesViewModel.Reload.
        var isMtls = _mtlsState.IsEnabled;

        foreach (var bridge in store.McpApiBridges)
        {
            Bridges.Add(new ApiBridgeItemViewModel(
                bridge,
                store.Routes.FirstOrDefault(r => r.Id == bridge.RouteId),
                store.Settings.ListenPort,
                OnBridgeEdited,
                message => StatusMessage = message,
                isMtls));
        }

        NewBridgeRoute = Routes.FirstOrDefault(r => r.Id == selectedRouteId);
    }

    partial void OnEditingBridgeChanged(ApiBridgeItemViewModel? value)
    {
        OnPropertyChanged(nameof(IsEditingExisting));
        OnPropertyChanged(nameof(SaveButtonLabel));
    }

    partial void OnManifestJsonChanged(string value) => ValidateManifest();

    partial void OnIsEnabledChanged(bool value)
    {
        // Reload() also assigns this property; skip the write when the store already agrees, so
        // refreshing the tab does not queue a write to the vault.
        if (_configStoreCache.Current.Settings.McpApiBridgeEnabled == value) return;

        _ = PersistAsync(
            store => store.Settings.McpApiBridgeEnabled = value,
            value
                ? "API bridges enabled — endpoints under /api-mcp are now served."
                : "API bridges disabled — endpoints under /api-mcp now return 404.");

        _activityLog.Log($"SETTINGS API bridges {(value ? "enabled" : "disabled")}");
    }

    [RelayCommand]
    private void Refresh()
    {
        Reload();
        StatusMessage = $"Refreshed — {Bridges.Count} bridge(s).";
    }

    // ---- the manifest editor ------------------------------------------------------------------

    /// <summary>
    /// Parses the editor and rebuilds the preview. Everything the user is about to serve is shown
    /// before it is served — a manifest is a document, and "invalid manifest" would send them
    /// looking through all of it.
    /// </summary>
    [RelayCommand]
    private void ValidateManifest()
    {
        ManifestPreview.Clear();

        if (string.IsNullOrWhiteSpace(ManifestJson))
        {
            ManifestIsValid = false;
            ManifestStatus = "Paste a manifest, import one, or load the sample.";
            return;
        }

        if (McpApiBridgeValidation.TryReadManifest(ManifestJson, out var manifest) is { } error)
        {
            ManifestIsValid = false;
            ManifestStatus = error;
            return;
        }

        ManifestIsValid = true;

        foreach (var tool in manifest!.Tools)
        {
            if (!tool.HasVariants)
            {
                ManifestPreview.Add($"{tool.Name} → {tool.Request!.Method.ToUpperInvariant()} {tool.Request.Path}");
                continue;
            }

            // Expanded one line per variant: the whole point of a variant tool is that it makes
            // several calls, and a single line naming the tool would hide which.
            ManifestPreview.Add($"{tool.Name} → picks on '{tool.VariantBy}':");

            foreach (var (key, request) in tool.Variants)
            {
                ManifestPreview.Add($"    {key} → {request.Method.ToUpperInvariant()} {request.Path}");
            }
        }

        foreach (var prompt in manifest.Prompts) ManifestPreview.Add($"prompt: {prompt.Name}");
        foreach (var skill in manifest.Skills) ManifestPreview.Add($"skill: {skill.Name}");

        var size = McpApiBridgeValidation.MeasureBytes(manifest);

        ManifestStatus = $"Valid — {manifest.Tools.Count} tool(s), {manifest.Prompts.Count} prompt(s), "
                         + $"{manifest.Skills.Count} skill(s), {size / 1024} KB"
                         + (manifest.Instructions is null ? "" : ", with instructions");
    }

    /// <summary>
    /// Fills the editor from a file rather than parsing the file straight into a record.
    ///
    /// That is the whole reason importing and pasting share one path: a manifest that fails
    /// validation can be fixed where it is, which it cannot be if the file went directly into the
    /// store.
    /// </summary>
    [RelayCommand]
    private void ImportManifest()
    {
        // Qualified: WinForms is enabled in this project and ships an OpenFileDialog of its own,
        // so the bare name does not compile. This is the WPF one.
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import an API to MCP manifest",
            DefaultExt = ".json",
            Filter = "Manifest (*.json)|*.json|All files (*.*)|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            ManifestJson = File.ReadAllText(dialog.FileName);
            NewBridgeManifestOrigin = Path.GetFileName(dialog.FileName);
            StatusMessage = $"Loaded {NewBridgeManifestOrigin} into the editor.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not read that file: {ex.Message}";
        }
    }

    /// <summary>Where the text in the editor came from, recorded on the bridge when it is saved.</summary>
    [ObservableProperty] private string _newBridgeManifestOrigin = "pasted";

    [RelayCommand]
    private void LoadSample()
    {
        ManifestJson = McpApiBridgeSample.Read();
        NewBridgeManifestOrigin = McpApiBridgeSample.FileName;
        StatusMessage = "Loaded the sample manifest — edit the paths and arguments for your own API.";
    }

    /// <summary>
    /// Writes the sample to disk, for editing outside this window. The same bytes the editor
    /// loads, so what is documented and what validates cannot come apart.
    /// </summary>
    [RelayCommand]
    private void SaveSample()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save the sample manifest",
            FileName = McpApiBridgeSample.FileName,
            DefaultExt = ".json",
            AddExtension = true,
            Filter = "Manifest (*.json)|*.json|All files (*.*)|*.*",
            OverwritePrompt = true,
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            File.WriteAllText(dialog.FileName, McpApiBridgeSample.Read());
            StatusMessage = $"Sample manifest saved to {dialog.FileName}.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not save the sample: {ex.Message}";
        }
    }

    [RelayCommand]
    private void EditManifest(ApiBridgeItemViewModel? item)
    {
        if (item is null) return;

        ManifestJson = System.Text.Json.JsonSerializer.Serialize(
            item.Bridge.Manifest,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

        EditingBridge = item;
        NewBridgeManifestOrigin = item.Bridge.ManifestOrigin;
        StatusMessage = $"Editing the manifest of '{item.Name}'. Saving replaces it.";
    }

    [RelayCommand]
    private void CancelEdit()
    {
        EditingBridge = null;
        ManifestJson = "";
        NewBridgeManifestOrigin = "pasted";
        StatusMessage = "Edit cancelled.";
    }

    // ---- bridges --------------------------------------------------------------------------------

    [RelayCommand]
    private async Task SaveBridgeAsync()
    {
        if (McpApiBridgeValidation.TryReadManifest(ManifestJson, out var manifest) is { } manifestError)
        {
            StatusMessage = manifestError;
            return;
        }

        if (EditingBridge is { } editing)
        {
            await ReplaceManifestAsync(editing, manifest!);
            return;
        }

        await AddBridgeAsync(manifest!);
    }

    private async Task AddBridgeAsync(McpApiBridgeManifest manifest)
    {
        var store = _configStoreCache.Current;

        if (McpApiBridgeValidation.ValidateName(NewBridgeName) is { } nameError)
        {
            StatusMessage = nameError;
            return;
        }

        var slug = string.IsNullOrWhiteSpace(NewBridgeSlug)
            ? Slugify(NewBridgeName)
            : NewBridgeSlug.Trim().ToLowerInvariant();

        if (McpApiBridgeValidation.ValidateSlug(slug, store.McpApiBridges) is { } slugError)
        {
            StatusMessage = slugError;
            return;
        }

        var routeId = NewBridgeRoute?.Id ?? Guid.Empty;

        if (McpApiBridgeValidation.ValidateRoute(routeId, store.Routes) is { } routeError)
        {
            StatusMessage = routeError;
            return;
        }

        if (McpApiBridgeValidation.ValidateNoteBudget(store, Guid.Empty, manifest) is { } budgetError)
        {
            StatusMessage = budgetError;
            return;
        }

        // Issued here, with the record, rather than left to the load-time backfill: a key
        // generated and saved in one write can never differ between memory and vault.
        var bridge = new McpApiBridgeRecord
        {
            Name = NewBridgeName.Trim(),
            Slug = slug,
            RouteId = routeId,
            Manifest = manifest,
            ManifestOrigin = NewBridgeManifestOrigin,
            Key = ProxyKey.Generate(NewBridgeKeyLifetime.Duration),
        };

        await PersistAsync(s => s.McpApiBridges.Add(bridge),
            $"API bridge '{bridge.Name}' added at {McpApiBridgeEndpoints.BasePath}/{slug} with its own proxy key "
            + $"({bridge.Key.DescribeExpiry(DateTimeOffset.UtcNow)}) — copy the key from its row."
            + (IsEnabled ? "" : " Switch API bridges on above before pointing an agent at it."));

        NewBridgeName = "";
        NewBridgeSlug = "";
        ManifestJson = "";
        NewBridgeManifestOrigin = "pasted";

        Reload();
    }

    private async Task ReplaceManifestAsync(ApiBridgeItemViewModel item, McpApiBridgeManifest manifest)
    {
        var store = _configStoreCache.Current;

        if (McpApiBridgeValidation.ValidateNoteBudget(store, item.Bridge.Id, manifest) is { } budgetError)
        {
            StatusMessage = budgetError;
            return;
        }

        await PersistAsync(s =>
        {
            if (s.McpApiBridges.FirstOrDefault(b => b.Id == item.Bridge.Id) is not { } stored) return;

            stored.Manifest = manifest;
            stored.ManifestOrigin = NewBridgeManifestOrigin;
            stored.ImportedUtc = DateTimeOffset.UtcNow;
        }, $"Manifest of '{item.Name}' replaced — {manifest.Tools.Count} tool(s). Agents see it on their next call.");

        EditingBridge = null;
        ManifestJson = "";
        NewBridgeManifestOrigin = "pasted";

        Reload();
    }

    [RelayCommand]
    private async Task DeleteBridgeAsync(ApiBridgeItemViewModel? item)
    {
        if (item is null) return;

        var affected = _configStoreCache.Current.McpSources
            .Count(s => s.Kind == McpSourceKind.ApiBridge && s.BridgeId == item.Bridge.Id);

        await PersistAsync(store =>
        {
            store.McpApiBridges.RemoveAll(b => b.Id == item.Bridge.Id);

            // A source left pointing at it would name a bridge that no longer exists and fail on
            // every connect, with nothing in the funnel tab able to explain why.
            var stranded = store.McpSources
                .Where(s => s.Kind == McpSourceKind.ApiBridge && s.BridgeId == item.Bridge.Id)
                .Select(s => s.Id)
                .ToList();

            foreach (var sourceId in stranded)
            {
                store.McpSources.RemoveAll(s => s.Id == sourceId);

                foreach (var funnel in store.McpFunnels)
                {
                    funnel.Sources.RemoveAll(link => link.SourceId == sourceId);
                }
            }
        }, affected == 0
            ? $"API bridge '{item.Name}' deleted — clients pointed at {McpApiBridgeEndpoints.BasePath}/{item.Slug} will now get 404."
            : $"API bridge '{item.Name}' deleted, along with {affected} funnel source(s) that exposed it.");

        await InvalidateSourcesForAsync(item.Bridge.Id);

        if (EditingBridge?.Bridge.Id == item.Bridge.Id) EditingBridge = null;

        Reload();
    }

    /// <summary>Turns a display name into something usable as a path segment.</summary>
    private static string Slugify(string name)
    {
        var slug = new string([.. name.Trim().ToLowerInvariant()
            .Select(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) ? c : '-')]);

        while (slug.Contains("--", StringComparison.Ordinal))
        {
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        }

        return slug.Trim('-');
    }

    // ---- persistence -----------------------------------------------------------------------------

    private void OnBridgeEdited(ApiBridgeItemViewModel item, string message) =>
        _ = PersistEditAsync(message, () => InvalidateSourcesForAsync(item.Bridge.Id));

    /// <summary>
    /// Drops any pooled funnel session that reaches this bridge.
    ///
    /// A source's transport headers are fixed when it connects, and one of them is this bridge's
    /// key — so regenerating the key would otherwise leave every funnel edge authenticating with
    /// the old value until the pool's idle eviction got to it, ten minutes later.
    /// </summary>
    private async Task InvalidateSourcesForAsync(Guid bridgeId)
    {
        var sources = _configStoreCache.Current.McpSources
            .Where(s => s.Kind == McpSourceKind.ApiBridge && s.BridgeId == bridgeId)
            .Select(s => s.Id)
            .ToList();

        foreach (var sourceId in sources)
        {
            await _connectionPool.InvalidateSourceAsync(sourceId);
        }
    }

    private async Task PersistEditAsync(string message, Func<Task> invalidate)
    {
        try
        {
            await _configStoreCache.SaveAsync();
            await invalidate();
            StatusMessage = message;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not save change: {ex.Message}";
        }
    }

    private async Task PersistAsync(Action<ConfigStore> mutate, string message)
    {
        try
        {
            await _configStoreCache.MutateAsync(mutate);
            StatusMessage = message;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not save: {ex.Message}";
        }
    }
}
