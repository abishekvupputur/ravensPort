using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RavensPort.UI.Services;
using RavensPort.Core.Mcp;
using RavensPort.Core.Models;
using RavensPort.Core.Proxy;
using RavensPort.Core.Storage;

namespace RavensPort.UI.ViewModels;

public sealed partial class RoutesViewModel : ObservableObject
{
    private readonly ConfigStoreCache _configStoreCache;
    private readonly ProxyConfigChangeNotifier _proxyConfigChangeNotifier;
    private readonly KestrelMtlsState _mtlsState;

    /// <summary>Handed to every row's key editor, which is the only thing here that copies.</summary>
    private readonly IClipboardService _clipboard;

    public ObservableCollection<UpstreamRecord> Upstreams { get; } = [];
    public ObservableCollection<RouteItemViewModel> Routes { get; } = [];
    public ObservableCollection<CredentialRecord> Credentials { get; } = [];

    [ObservableProperty] private string _newUpstreamName = "";
    [ObservableProperty] private string _newUpstreamBaseUrl = "";

    [ObservableProperty] private string _newRoutePathPrefix = "";
    [ObservableProperty] private UpstreamRecord? _newRouteUpstream;
    [ObservableProperty] private CredentialRecord? _newRouteCredential;
    [ObservableProperty] private bool _newRouteStripPrefix = true;

    /// <summary>
    /// How long the key issued to a new route stays valid. Defaults to never, matching what a
    /// machine-local endpoint pointed at by a config file needs; anything shorter is a deliberate
    /// choice the user makes here or changes later on the row.
    /// </summary>
    [ObservableProperty] private ProxyKeyLifetime _newRouteKeyLifetime = ProxyKeyLifetime.Never;

    public IReadOnlyList<ProxyKeyLifetime> KeyLifetimes => ProxyKeyLifetime.All;

    // Bearer-in-a-header is the default here and in RouteCredential, so someone who never opens
    // these fields gets exactly the behaviour the app had before they existed.
    [ObservableProperty] private CredentialPlacement _newRouteCredentialPlacement = CredentialPlacement.Header;
    [ObservableProperty] private string _newRouteCredentialParameterName = CredentialInjection.BearerHeader.Name;
    [ObservableProperty] private string _newRouteCredentialValuePrefix = CredentialInjection.BearerHeader.ValuePrefix;

    [ObservableProperty] private string _statusMessage = "Ready.";

    /// <summary>Drop-down source for both the add-route form and each row's editor.</summary>
    public static IReadOnlyList<CredentialPlacement> AllPlacements { get; } = Enum.GetValues<CredentialPlacement>();

    public IReadOnlyList<CredentialPlacement> Placements => AllPlacements;

    /// <summary>The name box means something different per placement.</summary>
    public string NewRouteParameterNameLabel => NewRouteCredentialPlacement switch
    {
        CredentialPlacement.Query => "Query parameter name",
        CredentialPlacement.Body => "Body field name (JSON object or form body)",
        _ => "Header name",
    };

    /// <summary>Live preview of what the upstream will receive, e.g. "header Authorization: Bearer &lt;token&gt;".</summary>
    public string NewRouteInjectionSummary => NewRouteCredential is null
        ? "nothing — the request is forwarded unauthenticated"
        : new CredentialInjection(
            NewRouteCredentialPlacement, NewRouteCredentialParameterName, NewRouteCredentialValuePrefix).Describe();

    /// <summary>
    /// Whether the add-route form will attach a credential. Drives the visibility of the
    /// placement fields, which mean nothing when there is no token to place.
    /// </summary>
    public bool NewRouteHasCredential => NewRouteCredential is not null;

    partial void OnNewRouteCredentialChanged(CredentialRecord? value)
    {
        // Prefill from the credential's own default placement, so picking an API-key credential
        // offers "X-Api-Key: <key>" rather than the Bearer header no key-based API wants. Only
        // when the fields are still at a placement default — a value the user typed is left
        // alone rather than overwritten by changing the credential.
        if (value is not null && IsAtAPlacementDefault())
        {
            var preferred = value.ToDefaultInjection();
            NewRouteCredentialPlacement = preferred.Placement;
            NewRouteCredentialParameterName = preferred.Name;
            NewRouteCredentialValuePrefix = preferred.ValuePrefix;
        }

        OnPropertyChanged(nameof(NewRouteHasCredential));
        OnPropertyChanged(nameof(NewRouteInjectionSummary));
    }

    private bool IsAtAPlacementDefault()
    {
        var current = CredentialInjection.DefaultFor(NewRouteCredentialPlacement);

        return NewRouteCredentialParameterName == current.Name
               && NewRouteCredentialValuePrefix == current.ValuePrefix;
    }

    /// <summary>
    /// Clears the credential picker. A route with no credential is a supported configuration —
    /// a plain forwarding hop to an upstream that needs no token — and a ComboBox offers no way
    /// to go back to "nothing selected" on its own.
    /// </summary>
    [RelayCommand]
    private void ClearNewRouteCredential() => NewRouteCredential = null;

    /// <summary>
    /// Moves the name and prefix to the new placement's defaults when they were still at the
    /// old placement's defaults, so switching to "Query" offers "?access_token=" instead of
    /// carrying "Authorization"/"Bearer " across, while a value the user typed is left alone.
    /// </summary>
    partial void OnNewRouteCredentialPlacementChanged(CredentialPlacement oldValue, CredentialPlacement newValue)
    {
        var previous = CredentialInjection.DefaultFor(oldValue);
        var replacement = CredentialInjection.DefaultFor(newValue);

        if (NewRouteCredentialParameterName == previous.Name) NewRouteCredentialParameterName = replacement.Name;
        if (NewRouteCredentialValuePrefix == previous.ValuePrefix) NewRouteCredentialValuePrefix = replacement.ValuePrefix;

        OnPropertyChanged(nameof(NewRouteParameterNameLabel));
        OnPropertyChanged(nameof(NewRouteInjectionSummary));
    }

    partial void OnNewRouteCredentialParameterNameChanged(string value) =>
        OnPropertyChanged(nameof(NewRouteInjectionSummary));

    partial void OnNewRouteCredentialValuePrefixChanged(string value) =>
        OnPropertyChanged(nameof(NewRouteInjectionSummary));

    public bool HasUpstreams => Upstreams.Count > 0;
    public bool HasNoUpstreams => Upstreams.Count == 0;
    public bool HasRoutes => Routes.Count > 0;
    public bool HasNoRoutes => Routes.Count == 0;

    public RoutesViewModel(
        ConfigStoreCache configStoreCache,
        ProxyConfigChangeNotifier proxyConfigChangeNotifier,
        KestrelMtlsState mtlsState,
        IClipboardService clipboard)
    {
        _configStoreCache = configStoreCache;
        _proxyConfigChangeNotifier = proxyConfigChangeNotifier;
        _mtlsState = mtlsState;
        _clipboard = clipboard;

        // Empty-state visibility is derived from these collections, so re-evaluate on change.
        Upstreams.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasUpstreams));
            OnPropertyChanged(nameof(HasNoUpstreams));
        };
        Routes.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasRoutes));
            OnPropertyChanged(nameof(HasNoRoutes));
        };

        Reload();
    }

    /// <summary>
    /// Re-reads everything from the shared config cache. Needed because this view model is a
    /// singleton that used to snapshot the credential list once at construction — a credential
    /// added on the Credentials tab afterwards never appeared in the Routes dropdown.
    /// Called on construction, from the Refresh button, and whenever the Routes tab is shown.
    /// </summary>
    public void Reload()
    {
        // Selections are object references into the collections we're about to clear, so
        // remember them by id and restore afterwards to avoid resetting the user's pickers.
        var selectedUpstreamId = NewRouteUpstream?.Id;
        var selectedCredentialId = NewRouteCredential?.Id;

        var store = _configStoreCache.Current;

        Upstreams.Clear();
        foreach (var u in store.Upstreams) Upstreams.Add(u);

        Credentials.Clear();
        foreach (var c in store.Credentials) Credentials.Add(c);

        // Resolved against the current upstream/credential lists so the grid can show names
        // and the real local URL rather than bare ids.
        Routes.Clear();
        var credentials = store.Credentials.ToList();
        // What the listener actually bound, not what the setting asks for. Between switching mTLS
        // on and the restart it needs, the two disagree, and the URL in this grid is one the user
        // copies into a client — it has to be the one that answers today.
        var isMtls = _mtlsState.IsEnabled;
        foreach (var r in store.Routes)
        {
            Routes.Add(new RouteItemViewModel(
                r,
                store.Upstreams.FirstOrDefault(u => u.Id == r.UpstreamId),
                credentials,
                store.Settings.ListenPort,
                isMtls,
                _clipboard,
                OnRouteEdited,
                message => StatusMessage = message));
        }

        NewRouteUpstream = Upstreams.FirstOrDefault(u => u.Id == selectedUpstreamId);
        NewRouteCredential = Credentials.FirstOrDefault(c => c.Id == selectedCredentialId);
    }

    [RelayCommand]
    private void Refresh()
    {
        Reload();
        StatusMessage = $"Refreshed — {Credentials.Count} credential(s), {Upstreams.Count} upstream(s), {Routes.Count} route(s).";
    }

    [RelayCommand]
    private async Task AddUpstreamAsync()
    {
        if (string.IsNullOrWhiteSpace(NewUpstreamName) || string.IsNullOrWhiteSpace(NewUpstreamBaseUrl)) return;

        var baseUrl = NewUpstreamBaseUrl.Trim().TrimEnd('/');

        // The access token is attached to every request forwarded here, so a plain-http
        // upstream would put it on the wire in cleartext.
        if (UrlValidation.ValidateEndpoint(baseUrl, "Upstream base URL") is { } error)
        {
            StatusMessage = error;
            return;
        }

        var upstream = new UpstreamRecord { Name = NewUpstreamName.Trim(), BaseUrl = baseUrl };
        await SaveAndRebuildAsync(store => store.Upstreams.Add(upstream));
        Upstreams.Add(upstream);

        NewUpstreamName = "";
        NewUpstreamBaseUrl = "";
        StatusMessage = $"Upstream '{upstream.Name}' added.";
    }

    [RelayCommand]
    private async Task DeleteUpstreamAsync(UpstreamRecord? upstream)
    {
        if (upstream is null) return;

        var affected = _configStoreCache.Current.Routes.Count(r => r.UpstreamId == upstream.Id);

        await SaveAndRebuildAsync(store => store.Upstreams.Remove(upstream));
        // Reload so any route that pointed at this upstream immediately shows as broken
        // rather than silently continuing to display a name that no longer exists.
        Reload();

        StatusMessage = affected == 0
            ? $"Upstream '{upstream.Name}' deleted."
            : $"Upstream '{upstream.Name}' deleted — {affected} route(s) now have no upstream and will not be served.";
    }

    [RelayCommand]
    private async Task AddRouteAsync()
    {
        // The credential is deliberately not required. A route with none is a plain forwarding
        // hop for an upstream that needs no token, and further credentials can be added to any
        // route afterwards from its row.
        if (string.IsNullOrWhiteSpace(NewRoutePathPrefix) || NewRouteUpstream is null)
        {
            StatusMessage = "Path prefix and upstream are required.";
            return;
        }

        var prefix = NewRoutePathPrefix.Trim();
        if (!prefix.StartsWith('/')) prefix = "/" + prefix;

        // Shared with ProxyConfigBuilder so the UI cannot accept a prefix the config builder
        // would go on to skip. Covers '/' alone, '..' segments, and the route-template
        // metacharacters that would stop the route loading at all.
        if (RouteValidation.ValidatePathPrefix(prefix) is { } prefixError)
        {
            StatusMessage = prefixError;
            return;
        }

        // Two routes with the same prefix produce two ASP.NET endpoints with identical match
        // patterns. That loads without complaint but throws AmbiguousMatchException on every
        // request, so the whole prefix 500s. Reject it here rather than let it fail silently.
        var normalized = prefix.TrimEnd('/');
        if (_configStoreCache.Current.Routes.Any(r =>
                string.Equals(r.PathPrefix.TrimEnd('/'), normalized, StringComparison.OrdinalIgnoreCase)))
        {
            StatusMessage = $"A route for '{prefix}' already exists. Path prefixes must be unique — " +
                            "duplicates make every request to that prefix fail with an ambiguous-match error.";
            return;
        }

        List<RouteCredential> credentials = NewRouteCredential is null
            ? []
            : [
                new RouteCredential
                {
                    CredentialId = NewRouteCredential.Id,
                    Placement = NewRouteCredentialPlacement,
                    ParameterName = NewRouteCredentialParameterName.Trim(),
                    ValuePrefix = NewRouteCredentialValuePrefix,
                },
            ];

        // Shared with ProxyConfigBuilder, which drops a route whose credential settings cannot be
        // put on the wire — accepting one here would create a route that never serves anything.
        if (RouteValidation.ValidateCredentials(credentials) is { } injectionError)
        {
            StatusMessage = injectionError;
            return;
        }

        var route = new RouteMapping
        {
            PathPrefix = prefix,
            UpstreamId = NewRouteUpstream.Id,
            StripPrefix = NewRouteStripPrefix,
            Enabled = true,
            Credentials = credentials,

            // Issued here, with the record, rather than left for the load-time backfill: a key
            // generated and saved in the same write can never differ between memory and disk.
            Key = ProxyKey.Generate(NewRouteKeyLifetime.Duration),
        };

        await SaveAndRebuildAsync(store => store.Routes.Add(route));
        // Reload rather than Add: the row needs its upstream/credential names resolved.
        Reload();

        NewRoutePathPrefix = "";

        var keyNote = $" Its own proxy key was generated ({route.Key.DescribeExpiry(DateTimeOffset.UtcNow)}) — "
                      + "copy it from the route's row; no other route's key opens this one.";

        StatusMessage = (credentials.Count == 0
            ? $"Route '{prefix}' added — no credential attached, requests are forwarded unauthenticated. "
              + "Use 'Add credential' on the route to attach one."
            : $"Route '{prefix}' added — credential sent as {credentials[0].ToCredentialInjection().Describe()}.")
            + keyNote;
    }

    [RelayCommand]
    private async Task DeleteRouteAsync(RouteItemViewModel? item)
    {
        if (item is null) return;
        await SaveAndRebuildAsync(store => store.Routes.Remove(item.Route));
        Routes.Remove(item);
        StatusMessage = $"Route '{item.PathPrefix}' deleted.";
    }

    /// <summary>
    /// Called when a route row is edited in the grid — a checkbox toggled, or one of the
    /// credential-injection fields changed. Those property setters are synchronous, so
    /// persistence is kicked off here and any failure is reported in the footer rather than
    /// surfacing as an unobserved task exception.
    /// </summary>
    private void OnRouteEdited(RouteItemViewModel item, string message)
    {
        _ = PersistRouteEditAsync(item, message);
    }

    private async Task PersistRouteEditAsync(RouteItemViewModel item, string message)
    {
        try
        {
            await SaveAndRebuildAsync();
            StatusMessage = message;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not save change to '{item.PathPrefix}': {ex.Message}";
        }
    }

    /// <summary>
    /// Applies an edit and persists it under the store's write lock, then hot-reloads YARP.
    /// The mutation has to happen inside the lock — the token refresh loop serializes the same
    /// object on a background thread, and a list edit landing mid-serialization throws.
    /// </summary>
    private async Task SaveAndRebuildAsync(Action<ConfigStore>? mutate = null)
    {
        if (mutate is null)
        {
            await _configStoreCache.SaveAsync();
        }
        else
        {
            await _configStoreCache.MutateAsync(mutate);
        }

        _proxyConfigChangeNotifier.Rebuild();
    }
}
