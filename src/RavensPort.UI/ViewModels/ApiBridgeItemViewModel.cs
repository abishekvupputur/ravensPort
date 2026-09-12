using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RavensPort.Core.Mcp;
using RavensPort.Core.Models;
using RavensPort.Core.Proxy;
using RavensPort.UI.Services;

namespace RavensPort.UI.ViewModels;

/// <summary>One row in the API bridges grid.</summary>
public sealed partial class ApiBridgeItemViewModel : ObservableObject
{
    private readonly Action<ApiBridgeItemViewModel, string> _onChanged;
    private readonly Action<string> _onStatus;

    public ApiBridgeItemViewModel(
        McpApiBridgeRecord bridge,
        RouteMapping? route,
        int listenPort,
        Action<ApiBridgeItemViewModel, string> onChanged,
        Action<string> onStatus,
        bool isMtls,
        IClipboardService clipboard)
    {
        Bridge = bridge;
        Route = route;
        _onChanged = onChanged;
        _onStatus = onStatus;
        _enabled = bridge.Enabled;

        var scheme = isMtls ? "https" : "http";
        LocalUrl = $"{scheme}://127.0.0.1:{listenPort}{McpApiBridgeEndpoints.BasePath}/{bridge.Slug}";

        // Only this key opens this endpoint — not the key of the route the bridge calls, so an
        // agent handed this one reaches the manifest's operations and nothing else that route
        // serves.
        Key = new ProxyKeyViewModel(
            bridge.Key,
            $"API bridge '{bridge.Name}'",
            message => _onChanged(this, message),
            onStatus,
            clipboard);

        Key.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(ProxyKeyViewModel.Display)) OnPropertyChanged(nameof(ClientConfigSnippet));
        };

        foreach (var header in Bridge.Headers)
        {
            Headers.Add(Wrap(header));
        }

        Headers.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasHeaders));
        };
    }

    /// <summary>
    /// Static headers sent on every call this bridge makes, ahead of whatever a tool's own
    /// manifest sets for the same name. The common case is a header a whole upstream API demands
    /// unconditionally — GitHub's REST API refuses every request with no User-Agent — so this is
    /// set once here rather than repeated in every tool.
    /// </summary>
    public ObservableCollection<McpApiBridgeHeaderItemViewModel> Headers { get; } = [];

    public bool HasHeaders => Headers.Count > 0;

    [RelayCommand]
    private void AddHeader()
    {
        var header = new McpApiBridgeHeader();
        Bridge.Headers.Add(header);
        Headers.Add(Wrap(header));

        _onChanged(this, $"API bridge '{Name}' now has {Headers.Count} custom header(s) — name the new one.");
    }

    [RelayCommand]
    private void RemoveHeader(McpApiBridgeHeaderItemViewModel? item)
    {
        if (item is null) return;

        Bridge.Headers.Remove(item.Model);
        Headers.Remove(item);

        _onChanged(this, string.IsNullOrWhiteSpace(item.Model.Name)
            ? $"API bridge '{Name}': removed an unnamed custom header."
            : $"API bridge '{Name}': no longer sends '{item.Model.Name}' on every call.");
    }

    private McpApiBridgeHeaderItemViewModel Wrap(McpApiBridgeHeader header) =>
        new(header, ValidateHeaderEntry, (_, message) => _onChanged(this, message), _onStatus);

    /// <summary>
    /// Answers a row's "may I become this?" question on behalf of the whole bridge — two entries
    /// writing the same header name would silently overwrite each other, which no single row can
    /// see on its own.
    /// </summary>
    private string? ValidateHeaderEntry(McpApiBridgeHeaderItemViewModel item, McpApiBridgeHeader candidate)
    {
        var proposed = Headers
            .Select(existing => ReferenceEquals(existing, item) ? candidate : existing.Model)
            .ToList();

        return McpApiBridgeValidation.ValidateBridgeHeaders(proposed);
    }

    public McpApiBridgeRecord Bridge { get; }
    public RouteMapping? Route { get; }

    public string Name => Bridge.Name;
    public string Slug => Bridge.Slug;
    public string LocalUrl { get; }

    /// <summary>The route whose credential every tool call spends.</summary>
    public string RouteLabel => Route?.PathPrefix ?? "⚠ route missing";

    public bool IsBroken => Route is null;

    /// <summary>What the manifest offers, for the row: the thing a user is actually checking.</summary>
    public string ManifestSummary
    {
        get
        {
            var parts = new List<string> { Describe(Bridge.Manifest.Tools.Count, "tool") };

            var variants = Bridge.Manifest.Tools.Count(t => t.HasVariants);
            if (variants > 0) parts.Add($"{variants} with variants");

            if (Bridge.Manifest.Prompts.Count > 0) parts.Add(Describe(Bridge.Manifest.Prompts.Count, "prompt"));
            if (Bridge.Manifest.Skills.Count > 0) parts.Add(Describe(Bridge.Manifest.Skills.Count, "skill"));

            return string.Join(" · ", parts);
        }
    }

    /// <summary>Where the manifest came from, for a user with several bridges to keep straight.</summary>
    public string Origin => string.IsNullOrWhiteSpace(Bridge.ManifestOrigin)
        ? $"imported {Bridge.ImportedUtc.ToLocalTime():yyyy-MM-dd HH:mm}"
        : $"{Bridge.ManifestOrigin} · {Bridge.ImportedUtc.ToLocalTime():yyyy-MM-dd HH:mm}";

    /// <summary>This bridge's own proxy key.</summary>
    public ProxyKeyViewModel Key { get; }

    /// <summary>
    /// A ready-to-paste MCP client entry: the endpoint, plus the key as the header it has to
    /// travel in. Masked exactly as the key itself is, so the panel is not readable over a
    /// shoulder until Show is pressed.
    /// </summary>
    public string ClientConfigSnippet =>
        $$"""
          "{{Slug}}": {
            "url": "{{LocalUrl}}",
            "headers": { "{{LocalAccessGuard.ApiKeyHeaderName}}": "{{Key.Display}}" }
          }
          """;

    [ObservableProperty] private bool _enabled;

    partial void OnEnabledChanged(bool value)
    {
        Bridge.Enabled = value;
        _onChanged(this, $"API bridge '{Name}' {(value ? "enabled" : "disabled")}.");
    }

    private static string Describe(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";
}
