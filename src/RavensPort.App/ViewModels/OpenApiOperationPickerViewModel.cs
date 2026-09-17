using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RavensPort.Core.Models;

namespace RavensPort.App.ViewModels;

/// <summary>How the picker buckets the operations it is showing.</summary>
public enum OpenApiGroupingMode
{
    /// <summary>The spec's own <c>tags</c>, which is what produced GitHub's <c>repos_</c>/<c>issues_</c> tool names.</summary>
    Tag,

    /// <summary>First URI segment, then the first literal segment under it — two levels.</summary>
    Path,

    Method,

    /// <summary>One flat list, as this window first shipped.</summary>
    None,
}

public sealed record OpenApiGroupingOption(OpenApiGroupingMode Mode, string Label);

/// <summary>
/// Anything the picker's tree can hold — a group or a single operation. They share a base so the
/// tree is typed, and so the container style can bind <see cref="IsExpanded"/> without every leaf
/// reporting a missing property.
/// </summary>
public abstract partial class OpenApiPickerNodeViewModel : ObservableObject
{
    [ObservableProperty] private bool _isExpanded;
}

/// <summary>One row of the picker — a single operation plus whether it is currently ticked.</summary>
public sealed partial class OpenApiOperationRowViewModel(OpenApiOperationSummary summary) : OpenApiPickerNodeViewModel
{
    public string Path { get; } = summary.Path;
    public string Method { get; } = summary.Method;
    public string? OperationId { get; } = summary.OperationId;
    public string? Summary { get; } = summary.Summary;
    public IReadOnlyList<string> Tags { get; } = summary.Tags;

    /// <summary>What ticking this row adds to the manifest's stored size.</summary>
    public int EstimatedBytes { get; } = summary.EstimatedBytes;

    public string TagsDisplay { get; } = summary.Tags.Count > 0 ? string.Join(", ", summary.Tags) : "(untagged)";

    /// <summary>The group this row sits under in the tree as currently built, if any.</summary>
    internal OpenApiOperationGroupViewModel? Parent { get; set; }

    [ObservableProperty] private bool _isSelected = true;
}

/// <summary>
/// A bucket of operations in the picker's tree — either of rows, or of further groups. Its tick box
/// is the "select everything under here" control, and reads back as ticked, cleared, or the dash in
/// between, so a group that is half chosen says so rather than looking untouched.
/// </summary>
public sealed class OpenApiOperationGroupViewModel : OpenApiPickerNodeViewModel
{
    private readonly OpenApiOperationPickerViewModel _owner;
    private int _selectedLeafCount;

    public string Label { get; }

    /// <summary>Child groups, or rows at the bottom level.</summary>
    public IReadOnlyList<OpenApiPickerNodeViewModel> Children { get; }

    /// <summary>Every row under this group, however deep — the set its tick box acts on.</summary>
    public IReadOnlyList<OpenApiOperationRowViewModel> Leaves { get; }

    internal OpenApiOperationGroupViewModel? Parent { get; set; }

    public OpenApiOperationGroupViewModel(
        OpenApiOperationPickerViewModel owner, string label, IReadOnlyList<OpenApiPickerNodeViewModel> children)
    {
        _owner = owner;
        Label = label;
        Children = children;

        Leaves = children
            .SelectMany(child => child is OpenApiOperationGroupViewModel group
                ? group.Leaves
                : (IEnumerable<OpenApiOperationRowViewModel>)[(OpenApiOperationRowViewModel)child])
            .ToList();

        foreach (var child in children)
        {
            if (child is OpenApiOperationGroupViewModel group) group.Parent = this;
            else ((OpenApiOperationRowViewModel)child).Parent = this;
        }

        _selectedLeafCount = Leaves.Count(leaf => leaf.IsSelected);
    }

    public string CountLabel => Leaves.Count == 1 ? "1 operation" : $"{Leaves.Count} operations";

    /// <summary>
    /// Null means "some of them". The setter only ever receives true or false, because the box is
    /// not three-state from the user's side — clicking a half-ticked group selects all of it, and
    /// clicking again clears all of it.
    /// </summary>
    public bool? IsSelected
    {
        get
        {
            if (_selectedLeafCount == 0) return false;

            return _selectedLeafCount == Leaves.Count ? true : null;
        }

        set => _owner.SetSelection(Leaves, value ?? false);
    }

    internal void OnLeafSelectionChanged(bool isSelected)
    {
        _selectedLeafCount += isSelected ? 1 : -1;

        // During a bulk set every leaf reports in turn; the owner raises one refresh at the end
        // instead of a notification per leaf per level.
        if (!_owner.IsBulkUpdating) OnPropertyChanged(nameof(IsSelected));

        Parent?.OnLeafSelectionChanged(isSelected);
    }

    internal void RefreshSelectionState()
    {
        OnPropertyChanged(nameof(IsSelected));

        foreach (var child in Children.OfType<OpenApiOperationGroupViewModel>()) child.RefreshSelectionState();
    }

    internal void Expand(bool isExpanded)
    {
        IsExpanded = isExpanded;

        foreach (var child in Children.OfType<OpenApiOperationGroupViewModel>()) child.Expand(isExpanded);
    }
}

/// <summary>
/// Backs <see cref="Views.OpenApiOperationPickerWindow"/>: lets the user see every operation an
/// OpenAPI document declares and choose which ones become tools, before
/// <see cref="OpenApiImporter.Convert(string,string,IReadOnlySet{ValueTuple{string,string}}?)"/>
/// builds anything.
///
/// Everything here is shaped by the size of the documents this exists for — GitHub's is 1239
/// operations. A flat list of those is unreadable, so they are grouped and collapsed; finding the
/// handful you want means searching, so the search takes wildcards; and acting on what you found
/// means every "select all" — the global one and each group's — works on the search results rather
/// than the whole document.
/// </summary>
public sealed partial class OpenApiOperationPickerViewModel : ObservableObject
{
    /// <summary>
    /// Groups start collapsed, because a thousand expanded rows is the thing this window is for
    /// avoiding — but once a search has narrowed the list this far, opening it is what the user
    /// wanted anyway.
    /// </summary>
    private const int AutoExpandThreshold = 60;

    private readonly string _specText;
    private readonly string _sourceName;

    private readonly int _baseManifestBytes;

    private Regex? _wildcard;
    private string _plainSearch = "";
    private int _selectedCount;
    private int _selectedBytes;
    private int _bulkDepth;

    /// <summary>Every operation in the document. Selection lives here, so it survives a search.</summary>
    public IReadOnlyList<OpenApiOperationRowViewModel> Rows { get; }

    /// <summary>What the tree shows: groups, or rows when grouping is off.</summary>
    public ObservableCollection<OpenApiPickerNodeViewModel> Tree { get; } = [];

    public IReadOnlyList<OpenApiGroupingOption> GroupingOptions { get; } =
    [
        new(OpenApiGroupingMode.Tag, "Tag"),
        new(OpenApiGroupingMode.Path, "Path"),
        new(OpenApiGroupingMode.Method, "Method"),
        new(OpenApiGroupingMode.None, "Nothing (flat list)"),
    ];

    [ObservableProperty] private OpenApiGroupingOption _grouping;

    [ObservableProperty] private string _searchText = "";

    [ObservableProperty] private string _selectionSummary = "";

    [ObservableProperty] private string _matchSummary = "";

    /// <summary>"62 / 64 tools" — the cap the manifest applies to how many tools it may declare.</summary>
    [ObservableProperty] private string _toolCountSummary = "";

    /// <summary>"118 / 128 KB" — an estimate, since the exact bytes depend on the final serialization.</summary>
    [ObservableProperty] private string _sizeSummary = "";

    [ObservableProperty] private bool _isOverToolCap;

    [ObservableProperty] private bool _isOverSizeCap;

    /// <summary>Set once <see cref="Import"/> runs; the window closes with this as the result.</summary>
    public OpenApiImportResult? Result { get; private set; }

    /// <summary>Raised to tell the window to close — true if <see cref="Result"/> should be used, false on cancel.</summary>
    public event Action<bool>? CloseRequested;

    internal bool IsBulkUpdating => _bulkDepth > 0;

    public OpenApiOperationPickerViewModel(
        string specText,
        string sourceName,
        IReadOnlyList<OpenApiOperationSummary> operations,
        int baseManifestBytes = 0)
    {
        _specText = specText;
        _sourceName = sourceName;
        _baseManifestBytes = baseManifestBytes;
        _grouping = GroupingOptions[0];

        var rows = new List<OpenApiOperationRowViewModel>(operations.Count);

        foreach (var operation in operations)
        {
            var row = new OpenApiOperationRowViewModel(operation);
            row.PropertyChanged += OnRowPropertyChanged;
            rows.Add(row);
        }

        Rows = rows;
        _selectedCount = rows.Count;
        _selectedBytes = rows.Sum(row => row.EstimatedBytes);

        RebuildTree();
        ReportSelection();
    }

    /// <summary>The rows the current search leaves showing — what every "select all" acts on.</summary>
    public IEnumerable<OpenApiOperationRowViewModel> VisibleRows => Rows.Where(MatchesSearch);

    /// <summary>
    /// What the current selection would weigh as a stored manifest. Close rather than exact: the
    /// per-operation costs miss the commas between entries in the tools array.
    /// </summary>
    public int EstimatedManifestBytes => _selectedCount == 0 ? 0 : _baseManifestBytes + _selectedBytes;

    // ---- search ---------------------------------------------------------------------------------

    partial void OnSearchTextChanged(string value)
    {
        var trimmed = value.Trim();

        // A pattern with no wildcard in it is a plain "contains", which is what typing a few
        // letters into a search box is expected to do. Once there is a * or a ?, it becomes a glob
        // over the whole field — so "user*" means starts-with, and "*user*" is the contains it
        // already was.
        if (trimmed.Length > 0 && (trimmed.Contains('*') || trimmed.Contains('?')))
        {
            var pattern = "^" + Regex.Escape(trimmed).Replace("\\*", ".*").Replace("\\?", ".") + "$";

            _wildcard = new Regex(
                pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));

            _plainSearch = "";
        }
        else
        {
            _wildcard = null;
            _plainSearch = trimmed;
        }

        RebuildTree();
    }

    private bool MatchesSearch(OpenApiOperationRowViewModel row)
    {
        if (_wildcard is null && _plainSearch.Length == 0) return true;

        return Matches(row.Method) || Matches(row.Path) || Matches(row.OperationId)
               || Matches(row.Summary) || Matches(row.TagsDisplay);
    }

    private bool Matches(string? text)
    {
        if (text is null) return false;

        if (_wildcard is null) return text.Contains(_plainSearch, StringComparison.OrdinalIgnoreCase);

        try
        {
            return _wildcard.IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    // ---- grouping -------------------------------------------------------------------------------

    partial void OnGroupingChanged(OpenApiGroupingOption value) => RebuildTree();

    private void RebuildTree()
    {
        var visible = VisibleRows.ToList();

        Tree.Clear();

        foreach (var node in Build(visible, KeySelectors(Grouping.Mode), depth: 0)) Tree.Add(node);

        var expand = visible.Count <= AutoExpandThreshold;

        foreach (var group in Tree.OfType<OpenApiOperationGroupViewModel>()) group.Expand(expand);

        MatchSummary = visible.Count == Rows.Count
            ? $"{Rows.Count} operations"
            : $"{visible.Count} of {Rows.Count} operations match";
    }

    private List<OpenApiPickerNodeViewModel> Build(
        IReadOnlyList<OpenApiOperationRowViewModel> rows,
        IReadOnlyList<Func<OpenApiOperationRowViewModel, string>> keys,
        int depth)
    {
        if (depth >= keys.Count) return [.. rows];

        return rows
            .GroupBy(keys[depth])
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(OpenApiPickerNodeViewModel (group) => new OpenApiOperationGroupViewModel(
                this, group.Key, Build([.. group], keys, depth + 1)))
            .ToList();
    }

    private static IReadOnlyList<Func<OpenApiOperationRowViewModel, string>> KeySelectors(OpenApiGroupingMode mode) =>
        mode switch
        {
            OpenApiGroupingMode.Tag => [row => row.Tags.Count > 0 ? row.Tags[0] : "(untagged)"],

            // Two levels, and the second deliberately skips "{owner}"-style placeholders: GitHub's
            // paths are all /repos/{owner}/{repo}/…, so grouping on the literal second segment puts
            // every one of them in one useless bucket.
            OpenApiGroupingMode.Path =>
            [
                row => "/" + Segments(row.Path).FirstOrDefault("(root)"),
                row => Segments(row.Path).Skip(1).FirstOrDefault(segment => !segment.StartsWith('{')) ?? "(top level)",
            ],

            OpenApiGroupingMode.Method => [row => row.Method],
            _ => [],
        };

    private static string[] Segments(string path) => path.Split('/', StringSplitOptions.RemoveEmptyEntries);

    // ---- selection ------------------------------------------------------------------------------

    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(OpenApiOperationRowViewModel.IsSelected)) return;

        var row = (OpenApiOperationRowViewModel)sender!;

        _selectedCount += row.IsSelected ? 1 : -1;
        _selectedBytes += row.IsSelected ? row.EstimatedBytes : -row.EstimatedBytes;
        row.Parent?.OnLeafSelectionChanged(row.IsSelected);

        ReportSelection();
    }

    /// <summary>
    /// The one path every bulk tick goes through — the global buttons and each group's box alike —
    /// so a 500-row group costs one summary update and one tree refresh rather than 500 of each.
    /// </summary>
    internal void SetSelection(IEnumerable<OpenApiOperationRowViewModel> rows, bool isSelected)
    {
        _bulkDepth++;

        try
        {
            foreach (var row in rows) row.IsSelected = isSelected;
        }
        finally
        {
            _bulkDepth--;
        }

        foreach (var group in Tree.OfType<OpenApiOperationGroupViewModel>()) group.RefreshSelectionState();

        ReportSelection();
    }

    /// <summary>
    /// Shows both caps against the current selection, as a running figure rather than a verdict
    /// delivered afterwards. The caps still trim what does not fit and still say so in the import's
    /// warning header — but being told after the fact that 170 of the 234 operations you chose were
    /// dropped is the same surprise this window exists to remove, just one step later.
    ///
    /// A manifest is capped on two independent things, and either one alone can do the trimming:
    /// 64 well-described tools can be over the byte cap while under the count.
    /// </summary>
    private void ReportSelection()
    {
        if (IsBulkUpdating) return;

        SelectionSummary = $"{_selectedCount} of {Rows.Count} selected";

        IsOverToolCap = _selectedCount > McpApiBridgeValidation.MaxTools;
        ToolCountSummary = $"{_selectedCount} / {McpApiBridgeValidation.MaxTools} tools";

        IsOverSizeCap = EstimatedManifestBytes > McpApiBridgeValidation.MaxManifestBytes;
        SizeSummary = $"{Kilobytes(EstimatedManifestBytes)} / {Kilobytes(McpApiBridgeValidation.MaxManifestBytes)} KB";

        ImportCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// One decimal below 10 KB, whole numbers above it — enough to see a small selection grow
    /// without a 128 KB cap reading as "127.8".
    /// </summary>
    private static string Kilobytes(int bytes)
    {
        var kilobytes = bytes / 1024d;

        return kilobytes < 10 ? kilobytes.ToString("0.#") : Math.Round(kilobytes).ToString("0");
    }

    [RelayCommand]
    private void SelectAll() => SetSelection(VisibleRows.ToList(), true);

    [RelayCommand]
    private void SelectNone() => SetSelection(VisibleRows.ToList(), false);

    private bool CanImport() => _selectedCount > 0;

    [RelayCommand(CanExecute = nameof(CanImport))]
    private void Import()
    {
        var includeOnly = Rows
            .Where(row => row.IsSelected)
            .Select(row => (row.Path, row.Method))
            .ToHashSet();

        Result = OpenApiImporter.Convert(_specText, _sourceName, includeOnly);
        CloseRequested?.Invoke(true);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(false);
}
