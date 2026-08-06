using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RavensPort.App.Services;
using RavensPort.Core.Models;

namespace RavensPort.App.ViewModels;

/// <summary>
/// A route row with its upstream resolved to a name and its credentials expanded into editable
/// entries, so the grid can show the whole hop (local endpoint -> upstream, and which tokens get
/// attached where) instead of raw ids. Rebuilt by RoutesViewModel.Reload() rather than mutated
/// in place.
///
/// A route may attach nothing, one credential, or several — including the same credential twice
/// in different places — so the credential side of the row is a collection rather than a fixed
/// set of fields.
/// </summary>
public sealed partial class RouteItemViewModel : ObservableObject
{
    private const string Missing = "(missing)";

    /// <summary>Raised when a field is edited, so the owner can persist, rebuild, and report.</summary>
    private readonly Action<RouteItemViewModel, string>? _onChanged;

    /// <summary>Raised when an edit was rejected, so the owner can show why.</summary>
    private readonly Action<string>? _onInvalid;

    private readonly IReadOnlyList<CredentialRecord> _availableCredentials;

    public RouteItemViewModel(
        RouteMapping route,
        UpstreamRecord? upstream,
        IReadOnlyList<CredentialRecord> availableCredentials,
        int listenPort,
        bool isMtls,
        IClipboardService clipboard,
        Action<RouteItemViewModel, string>? onChanged = null,
        Action<string>? onInvalid = null)
    {
        Route = route;
        _availableCredentials = availableCredentials;
        _onChanged = onChanged;
        _onInvalid = onInvalid;

        // Each route authenticates its own callers, so the key lives on the row rather than in
        // Settings. Regenerating it here revokes exactly this route and nothing else.
        Key = new ProxyKeyViewModel(
            route.Key,
            $"route '{route.PathPrefix}'",
            message => _onChanged?.Invoke(this, message),
            message => _onInvalid?.Invoke(message),
            clipboard);

        UpstreamName = upstream?.Name ?? Missing;
        UpstreamBaseUrl = upstream?.BaseUrl ?? Missing;
        var scheme = isMtls ? "https" : "http";
        LocalUrl = $"{scheme}://127.0.0.1:{listenPort}{route.PathPrefix}";

        // A route whose upstream is gone is silently dropped from the proxy config, so flag it
        // rather than let it look active. A missing credential still routes, unauthenticated.
        IsBroken = upstream is null;

        foreach (var credential in Route.Credentials)
        {
            Credentials.Add(Wrap(credential));
        }

        Credentials.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasCredentials));
            OnPropertyChanged(nameof(HasNoCredentials));
            OnPropertyChanged(nameof(CredentialSummary));
            OnPropertyChanged(nameof(Summary));
        };
    }

    public RouteMapping Route { get; }

    public string PathPrefix => Route.PathPrefix;

    /// <summary>This route's own proxy key — what its callers must present, and for how long.</summary>
    public ProxyKeyViewModel Key { get; }

    /// <summary>Editable credential entries, in the order they are written onto the request.</summary>
    public ObservableCollection<RouteCredentialItemViewModel> Credentials { get; } = [];

    public bool HasCredentials => Credentials.Count > 0;
    public bool HasNoCredentials => Credentials.Count == 0;

    /// <summary>
    /// Settable so the grid's checkbox can turn a route off without deleting it. Writes
    /// straight through to the underlying record, then notifies the owner to save + rebuild.
    /// </summary>
    public bool Enabled
    {
        get => Route.Enabled;
        set
        {
            if (Route.Enabled == value) return;
            Route.Enabled = value;
            OnPropertyChanged();
            _onChanged?.Invoke(this, value
                ? $"Route '{PathPrefix}' enabled."
                : $"Route '{PathPrefix}' disabled — requests to it will no longer be proxied.");
        }
    }

    public bool StripPrefix
    {
        get => Route.StripPrefix;
        set
        {
            if (Route.StripPrefix == value) return;
            Route.StripPrefix = value;
            OnPropertyChanged();
            _onChanged?.Invoke(this, $"Route '{PathPrefix}' updated — prefix is now "
                                     + (value ? "removed" : "kept") + " when forwarding.");
        }
    }

    /// <summary>
    /// Adds another credential to this route, defaulted to the first free slot so clicking the
    /// button twice does not produce two entries that collide and take the route off the air.
    /// </summary>
    [RelayCommand]
    private void AddCredential()
    {
        if (_availableCredentials.Count == 0)
        {
            _onInvalid?.Invoke("No credentials available — connect one on the Credentials tab first.");
            return;
        }

        if (NextFreeEntry() is not { } entry)
        {
            _onInvalid?.Invoke(
                "Every default slot is already taken on this route. Change one of the existing "
                + "entries' placement or name first, then add another.");
            return;
        }

        Route.Credentials.Add(entry);
        var item = Wrap(entry);
        Credentials.Add(item);

        _onChanged?.Invoke(this, $"Route '{PathPrefix}' now also sends {item.Describe()}.");
    }

    [RelayCommand]
    private void RemoveCredential(RouteCredentialItemViewModel? item)
    {
        if (item is null) return;

        Route.Credentials.Remove(item.Model);
        Credentials.Remove(item);

        _onChanged?.Invoke(this, Credentials.Count == 0
            ? $"Route '{PathPrefix}' no longer attaches any credential — requests are forwarded unauthenticated."
            : $"Route '{PathPrefix}' no longer sends {item.Describe()}.");
    }

    /// <summary>
    /// A credential entry that does not collide with any already on the route.
    ///
    /// Each credential's own default placement is tried first — an "X-Api-Key" credential should
    /// arrive already described as one rather than as a Bearer header nobody wanted — then the
    /// generic per-placement defaults, then the next credential.
    /// </summary>
    private RouteCredential? NextFreeEntry()
    {
        var taken = Route.Credentials.Select(c => c.Slot).ToHashSet(StringComparer.Ordinal);

        foreach (var credential in _availableCredentials)
        {
            var preferred = credential.ToDefaultInjection();
            var candidate = new RouteCredential
            {
                CredentialId = credential.Id,
                Placement = preferred.Placement,
                ParameterName = preferred.Name,
                ValuePrefix = preferred.ValuePrefix,
            };

            if (taken.Add(candidate.Slot)) return candidate;

            foreach (var placement in RoutesViewModel.AllPlacements)
            {
                var fallback = RouteCredential.For(credential.Id, placement);
                if (taken.Add(fallback.Slot)) return fallback;
            }
        }

        return null;
    }

    private RouteCredentialItemViewModel Wrap(RouteCredential credential) =>
        new(credential, _availableCredentials, ValidateEntry, OnEntryChanged, _onInvalid);

    /// <summary>
    /// Answers a row's "may I become this?" question, on behalf of the whole route.
    ///
    /// The row's own settings are checked first, then the proposed entry is put in the row's
    /// place among its siblings and the whole set is validated. That second half is the point:
    /// two entries writing the same header would silently overwrite each other, so the route,
    /// not the row, has to be the one to say no.
    /// </summary>
    private string? ValidateEntry(RouteCredentialItemViewModel item, RouteCredential candidate)
    {
        var proposed = Credentials
            .Select(existing => ReferenceEquals(existing, item) ? candidate : existing.Model)
            .ToList();

        return RouteValidation.ValidateCredentials(proposed);
    }

    private void OnEntryChanged(RouteCredentialItemViewModel item, string message)
    {
        OnPropertyChanged(nameof(CredentialSummary));
        OnPropertyChanged(nameof(Summary));
        _onChanged?.Invoke(this, $"Route '{PathPrefix}': {message}");
    }

    public string LocalUrl { get; }
    public string UpstreamName { get; }
    public string UpstreamBaseUrl { get; }

    public bool IsBroken { get; }

    /// <summary>Every credential this route attaches, one per line, for the grid column.</summary>
    public string CredentialSummary => Credentials.Count == 0
        ? "no credential — forwarded unauthenticated"
        : string.Join("\n", Credentials.Select(c => c.Describe()));

    /// <summary>Short human summary of what this route does, e.g. for tooltips.</summary>
    public string Summary =>
        $"{LocalUrl}  →  {UpstreamBaseUrl}"
        + (StripPrefix ? $"   (prefix '{PathPrefix}' removed before forwarding)" : "   (prefix kept)")
        + $"\n{CredentialSummary}";
}
