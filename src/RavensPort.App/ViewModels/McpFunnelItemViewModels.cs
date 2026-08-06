using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RavensPort.App.Services;
using RavensPort.Core.Mcp;
using RavensPort.Core.Models;
using RavensPort.Core.Proxy;

namespace RavensPort.App.ViewModels;

/// <summary>One row in the sources grid.</summary>
public sealed partial class McpSourceItemViewModel : ObservableObject
{
    private readonly Action<McpSourceItemViewModel, string> _onChanged;

    public McpSourceItemViewModel(
        McpSourceRecord source,
        RouteMapping? route,
        McpSourceCatalog? catalog,
        Action<McpSourceItemViewModel, string> onChanged)
    {
        Source = source;
        Route = route;
        Catalog = catalog;
        _onChanged = onChanged;
        _enabled = source.Enabled;
    }

    public McpSourceRecord Source { get; }
    public RouteMapping? Route { get; }
    public McpSourceCatalog? Catalog { get; }

    public string Name => Source.Name;
    public string Alias => Source.Alias;

    public string Target => Source.Kind == McpSourceKind.ProxyRoute
        ? Route?.PathPrefix ?? "⚠ route missing"
        : Source.Url;

    public string KindLabel => Source.Kind == McpSourceKind.ProxyRoute ? "Route (credentialed)" : "URL (no auth)";

    public bool IsBroken => Source.Kind == McpSourceKind.ProxyRoute && Route is null;

    /// <summary>Result of the last Refresh, or a nudge to run one.</summary>
    public string Status => Catalog?.Describe() ?? "not checked yet — press Refresh";

    /// <summary>
    /// The untrimmed failure, for the cell's tooltip. Null on success, which is what leaves a
    /// working source with no tooltip at all rather than one repeating its own cell.
    /// </summary>
    public string? StatusDetail => Catalog?.Detail;

    public bool HasError => Catalog?.Error is not null;

    [ObservableProperty] private bool _enabled;

    partial void OnEnabledChanged(bool value)
    {
        Source.Enabled = value;
        _onChanged(this, $"Source '{Name}' {(value ? "enabled" : "disabled")}.");
    }
}

/// <summary>One row in the funnels grid.</summary>
public sealed partial class McpFunnelItemViewModel : ObservableObject
{
    private readonly Action<McpFunnelItemViewModel, string> _onChanged;

    public McpFunnelItemViewModel(
        McpFunnelRecord funnel,
        int listenPort,
        int sourceCount,
        Action<McpFunnelItemViewModel, string> onChanged,
        Action<string> onStatus,
        bool isMtls,
        IClipboardService clipboard)
    {
        Funnel = funnel;
        SourceCount = sourceCount;
        var scheme = isMtls ? "https" : "http";
        LocalUrl = $"{scheme}://127.0.0.1:{listenPort}{McpFunnelEndpoints.BasePath}/{funnel.Slug}";
        _onChanged = onChanged;
        _enabled = funnel.Enabled;

        // Only this key opens this endpoint. Nothing a route or another funnel is configured with
        // gets in here, so handing an agent this key grants it this funnel's tools and no more.
        Key = new ProxyKeyViewModel(
            funnel.Key,
            $"funnel '{funnel.Name}'",
            message => _onChanged(this, message),
            onStatus,
            clipboard);

        Key.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(ProxyKeyViewModel.Display)) OnPropertyChanged(nameof(LocalUrlWithKey));
        };
    }

    public McpFunnelRecord Funnel { get; }
    public string Name => Funnel.Name;
    public string Slug => Funnel.Slug;
    public string LocalUrl { get; }
    public int SourceCount { get; }
    public string SourceSummary => SourceCount == 1 ? "1 source" : $"{SourceCount} sources";

    /// <summary>This funnel's own proxy key.</summary>
    public ProxyKeyViewModel Key { get; }

    /// <summary>
    /// The endpoint with the key already in the query string, ready to paste into an MCP client
    /// that cannot set headers — which is most of the ones driven by a JSON config file.
    /// Follows the same masking as the key itself so it is not readable over a shoulder.
    /// </summary>
    public string LocalUrlWithKey => $"{LocalUrl}?{LocalAccessGuard.ApiKeyQueryName}={Key.Display}";

    [ObservableProperty] private bool _enabled;

    partial void OnEnabledChanged(bool value)
    {
        Funnel.Enabled = value;
        _onChanged(this, $"Funnel '{Name}' {(value ? "enabled" : "disabled")}.");
    }
}

/// <summary>One tickable tool, resource, or prompt inside a selection list.</summary>
public sealed partial class McpSelectableItemViewModel : ObservableObject
{
    private readonly Action _onChanged;
    private bool _suppress;

    public McpSelectableItemViewModel(string name, bool isSelected, Action onChanged)
    {
        Name = name;
        _isSelected = isSelected;
        _onChanged = onChanged;
    }

    public string Name { get; }

    [ObservableProperty] private bool _isSelected;

    partial void OnIsSelectedChanged(bool value)
    {
        if (_suppress) return;
        _onChanged();
    }

    /// <summary>
    /// Sets the tick without treating it as a user edit. Switching a group between Include and
    /// Exclude re-derives every box from the new mode, and letting those writes call back would
    /// save once per item and, worse, rewrite the stored list from a half-updated view.
    /// </summary>
    public void SetSilently(bool value)
    {
        _suppress = true;
        IsSelected = value;
        _suppress = false;
    }
}

/// <summary>
/// The tools, resources, or prompts a funnel takes from one source, with the mode that decides
/// how the ticks are read. Modes are not cosmetic: Include is a closed list, so a tool the
/// upstream adds later stays hidden until someone picks it, while Exclude lets it through
/// immediately. Which one is right depends on whether the user is granting or revoking.
/// </summary>
public sealed partial class McpSelectionGroupViewModel : ObservableObject
{
    private readonly Action _onChanged;
    private bool _loading;

    public McpSelectionGroupViewModel(string title, string emptyHint, Action onChanged)
    {
        Title = title;
        EmptyHint = emptyHint;
        _onChanged = onChanged;
    }

    public string Title { get; }
    public string EmptyHint { get; }

    public IReadOnlyList<McpSelectionMode> AllModes { get; } = Enum.GetValues<McpSelectionMode>();

    public ObservableCollection<McpSelectableItemViewModel> Items { get; } = [];

    public bool HasItems => Items.Count > 0;
    public bool HasNoItems => Items.Count == 0;

    /// <summary>Ticks only matter when the mode reads them; under "All" the list is inert.</summary>
    public bool IsSelectable => Mode != McpSelectionMode.All;

    /// <summary>
    /// What this group amounts to, in a few characters, so a collapsed group still says what it
    /// is doing. A funnel over three sources shows nine of these at once — without a summary,
    /// collapsing them would hide the very thing the user came to check.
    /// </summary>
    public string Summary => Mode switch
    {
        McpSelectionMode.Include => $"{Items.Count(i => i.IsSelected)} of {Items.Count} allowed",
        McpSelectionMode.Exclude => $"{Items.Count(i => i.IsSelected)} blocked",
        _ => "all",
    };

    /// <summary>
    /// Exactly one of these three is true at a time. Previously two could be — an empty list
    /// under "All" showed both "nothing discovered" and "everything this source offers", which
    /// read as a contradiction.
    /// </summary>
    public bool ShowItems => IsExpanded && IsSelectable && HasItems;

    public bool ShowEmptyHint => IsExpanded && IsSelectable && HasNoItems;

    public bool ShowAllHint => IsExpanded && !IsSelectable;

    public string ExpandGlyph => IsExpanded ? "▾" : "▸";

    [ObservableProperty] private bool _isExpanded;

    [ObservableProperty] private McpSelectionMode _mode;

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;

    partial void OnIsExpandedChanged(bool value) => NotifyVisibility();

    partial void OnModeChanged(McpSelectionMode value)
    {
        NotifyVisibility();
        if (_loading) return;

        _onChanged();
    }

    private void NotifyVisibility()
    {
        OnPropertyChanged(nameof(IsSelectable));
        OnPropertyChanged(nameof(ShowItems));
        OnPropertyChanged(nameof(ShowEmptyHint));
        OnPropertyChanged(nameof(ShowAllHint));
        OnPropertyChanged(nameof(ExpandGlyph));
        OnPropertyChanged(nameof(Summary));
    }

    public void Load(McpSelectionMode mode, IEnumerable<string> known, IReadOnlyCollection<string> selected)
    {
        _loading = true;

        Mode = mode;

        Items.Clear();

        // Union of what the source last advertised and what is already selected. A tool that has
        // since disappeared upstream still has to be visible, or unticking it would be
        // impossible and it would sit in the stored list forever.
        foreach (var name in known.Concat(selected).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            // Ticking an item both persists and moves the summary, which is what a collapsed
            // group shows — so the summary has to be refreshed here rather than only on reload.
            Items.Add(new McpSelectableItemViewModel(name, selected.Contains(name), () =>
            {
                OnPropertyChanged(nameof(Summary));
                _onChanged();
            }));
        }

        _loading = false;

        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(HasNoItems));
        NotifyVisibility();
    }

    public List<string> SelectedNames() => [.. Items.Where(i => i.IsSelected).Select(i => i.Name)];
}

/// <summary>
/// One source as seen from inside a funnel: whether it is pooled at all, and what of it is
/// exposed. Edits write straight through to the stored funnel and are persisted by the owner.
/// </summary>
public sealed partial class McpFunnelSourceItemViewModel : ObservableObject
{
    private readonly McpFunnelRecord _funnel;
    private readonly Action<string> _onChanged;
    private bool _loading = true;

    public McpFunnelSourceItemViewModel(
        McpFunnelRecord funnel,
        McpSourceRecord source,
        McpFunnelSource? link,
        McpSourceCatalog? catalog,
        Action<string> onChanged)
    {
        _funnel = funnel;
        Source = source;
        _onChanged = onChanged;
        _isIncluded = link is not null;

        Tools = new McpSelectionGroupViewModel("Tools", "No tools discovered — press Refresh on the source.", Persist);
        Resources = new McpSelectionGroupViewModel("Resources", "No resources discovered.", Persist);
        Prompts = new McpSelectionGroupViewModel("Prompts", "No prompts discovered.", Persist);

        var effective = link ?? new McpFunnelSource { SourceId = source.Id };

        Tools.Load(effective.ToolMode, catalog?.Tools ?? [], effective.Tools);
        Resources.Load(effective.ResourceMode, catalog?.Resources ?? [], effective.Resources);
        Prompts.Load(effective.PromptMode, catalog?.Prompts ?? [], effective.Prompts);

        // A funnel over three sources otherwise opens as nine expanded tick lists at once, which
        // is unreadable and buries the funnel list above it. The one-line summary is what makes
        // collapsing safe: the row still says what it is doing without being opened.
        foreach (var group in new[] { Tools, Resources, Prompts })
        {
            group.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName is nameof(McpSelectionGroupViewModel.Summary))
                {
                    OnPropertyChanged(nameof(Summary));
                }
            };
        }

        _loading = false;
    }

    public McpSourceRecord Source { get; }

    public string Name => Source.Name;
    public string Alias => Source.Alias;
    public string ToolPrefix => $"{Source.Alias}{McpNameMapper.Separator}";

    public McpSelectionGroupViewModel Tools { get; }
    public McpSelectionGroupViewModel Resources { get; }
    public McpSelectionGroupViewModel Prompts { get; }

    public string Summary => IsIncluded
        ? $"tools: {Tools.Summary}  ·  resources: {Resources.Summary}  ·  prompts: {Prompts.Summary}"
        : "not pooled by this funnel";

    public string ExpandGlyph => IsExpanded ? "▾" : "▸";

    /// <summary>Detail is only meaningful for a source this funnel actually pools.</summary>
    public bool ShowDetail => IsIncluded && IsExpanded;

    [ObservableProperty] private bool _isExpanded;

    [ObservableProperty] private bool _isIncluded;

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;

    partial void OnIsExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(ExpandGlyph));
        OnPropertyChanged(nameof(ShowDetail));
    }

    partial void OnIsIncludedChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowDetail));
        OnPropertyChanged(nameof(Summary));

        if (_loading) return;

        // Opening it on inclusion saves the extra click: someone who has just pooled a source is
        // about to choose what it exposes.
        if (value) IsExpanded = true;

        Persist();
        _onChanged($"Source '{Name}' {(value ? "added to" : "removed from")} funnel '{_funnel.Name}'.");
    }

    /// <summary>
    /// Writes this row's state back onto the funnel record. Rebuilt wholesale rather than patched
    /// field by field, so the stored link can never end up describing a state the UI never showed.
    /// </summary>
    private void Persist()
    {
        if (_loading) return;

        _funnel.Sources.RemoveAll(s => s.SourceId == Source.Id);

        if (IsIncluded)
        {
            _funnel.Sources.Add(new McpFunnelSource
            {
                SourceId = Source.Id,
                ToolMode = Tools.Mode,
                Tools = Tools.SelectedNames(),
                ResourceMode = Resources.Mode,
                Resources = Resources.SelectedNames(),
                PromptMode = Prompts.Mode,
                Prompts = Prompts.SelectedNames(),
            });
        }

        _onChanged($"Funnel '{_funnel.Name}' updated.");
    }
}
