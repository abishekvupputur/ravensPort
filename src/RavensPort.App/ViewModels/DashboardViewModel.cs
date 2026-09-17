using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RavensPort.Core.Diagnostics;
using RavensPort.Core.Mcp;
using RavensPort.Core.Models;
using RavensPort.Core.Proxy;
using RavensPort.Core.Storage;

namespace RavensPort.App.ViewModels;

/// <summary>One endpoint's row: what it is, whether it is switched on, and what has reached it.</summary>
public sealed class EndpointStatusViewModel
{
    public required string Kind { get; init; }
    public required string Name { get; init; }
    public required string Address { get; init; }
    public required bool Enabled { get; init; }
    public required long Calls { get; init; }
    public required long Failed { get; init; }
    public required long Denied { get; init; }
    public required int ClientCount { get; init; }
    public required DateTimeOffset? LastUsedUtc { get; init; }

    /// <summary>What the key situation is, since an expired key turns every call into a 403.</summary>
    public required string KeyState { get; init; }

    public string LastUsed => DashboardViewModel.Ago(LastUsedUtc);

    public string CallsDisplay => Calls == 0 ? "—" : Calls.ToString("N0");

    public string ProblemsDisplay => Failed == 0 && Denied == 0
        ? "—"
        : string.Join(", ", new[]
        {
            Failed > 0 ? $"{Failed:N0} failed" : null,
            Denied > 0 ? $"{Denied:N0} denied" : null,
        }.Where(part => part is not null));

    public string ClientsDisplay => ClientCount == 0 ? "—" : ClientCount.ToString("N0");

    public bool HasProblems => Failed > 0 || Denied > 0;

    /// <summary>
    /// Dimmed in the table: switched off, or nothing has reached it this run. Anything with
    /// failures or refusals against it stays at full strength however quiet it is — a bridge
    /// nobody could get into is the row most worth noticing, not the one to fade out.
    /// </summary>
    public bool IsQuiet => (!Enabled || LastUsedUtc is null) && !HasProblems;
}

/// <summary>A caller, as far as its User-Agent distinguishes it, across every endpoint it touched.</summary>
public sealed class ClientStatusViewModel
{
    public required string UserAgent { get; init; }
    public required long Calls { get; init; }
    public required long Failed { get; init; }
    public required IReadOnlyList<string> Endpoints { get; init; }
    public required DateTimeOffset LastSeenUtc { get; init; }

    public string LastSeen => DashboardViewModel.Ago(LastSeenUtc);
    public string CallsDisplay => Calls.ToString("N0");
    public string EndpointsDisplay => string.Join(", ", Endpoints);
    public string FailedDisplay => Failed == 0 ? "—" : $"{Failed:N0} failed";
    public bool HasProblems => Failed > 0;
}

/// <summary>A session the funnel machinery is holding open to one of its sources right now.</summary>
public sealed class UpstreamSessionViewModel
{
    public required string Funnel { get; init; }
    public required string Source { get; init; }
    public required string State { get; init; }
    public required DateTimeOffset LastUsedUtc { get; init; }

    public string LastUsed => DashboardViewModel.Ago(LastUsedUtc);
    public bool IsFaulted => State == "faulted";
}

/// <summary>A credential whose token is gone or nearly gone, which is about to break every route using it.</summary>
public sealed class CredentialHealthViewModel
{
    public required string Name { get; init; }
    public required string State { get; init; }
    public required bool IsProblem { get; init; }
}

/// <summary>
/// The Dashboard tab: what this proxy is serving, what is actually being called, and by what.
///
/// Two different kinds of truth are on this tab and it is worth keeping them apart. The inventory —
/// how many routes, funnels and bridges exist and whether they are switched on — comes from the
/// configuration and is always accurate. The traffic — calls, failures, callers, last used — is
/// counted in memory from the moment the proxy started, so it says nothing about what happened
/// before the last restart. The header says which moment it is counting from rather than letting a
/// quiet table be mistaken for a quiet week.
///
/// There is deliberately no "clients connected" number. The MCP server runs stateless by design
/// (see ProxyStartupExtensions), so there is no session to count and a live figure would be
/// invented. What can honestly be shown is who called recently, which is the question that number
/// was standing in for.
/// </summary>
public sealed partial class DashboardViewModel : ObservableObject
{
    private readonly ConfigStoreCache _configStoreCache;
    private readonly ProxyTrafficStats _traffic;
    private readonly McpSourceConnectionPool _connectionPool;
    private readonly DispatcherTimer _timer;

    public ObservableCollection<EndpointStatusViewModel> Endpoints { get; } = [];
    public ObservableCollection<ClientStatusViewModel> Clients { get; } = [];
    public ObservableCollection<UpstreamSessionViewModel> UpstreamSessions { get; } = [];
    public ObservableCollection<CredentialHealthViewModel> Credentials { get; } = [];

    [ObservableProperty] private string _inventorySummary = "";
    [ObservableProperty] private string _trafficSummary = "";
    [ObservableProperty] private string _countingSince = "";
    [ObservableProperty] private string _deniedSummary = "";
    [ObservableProperty] private bool _hasDenials;
    [ObservableProperty] private bool _hasTraffic;
    [ObservableProperty] private bool _hasClients;
    [ObservableProperty] private bool _hasUpstreamSessions;
    [ObservableProperty] private bool _hasCredentialProblems;

    public DashboardViewModel(
        ConfigStoreCache configStoreCache,
        ProxyTrafficStats traffic,
        McpSourceConnectionPool connectionPool)
    {
        _configStoreCache = configStoreCache;
        _traffic = traffic;
        _connectionPool = connectionPool;

        // Same cadence as the Credentials tab's status refresh. Everything read here is either an
        // in-memory counter or a dictionary snapshot, so a tick costs nothing worth saving.
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _timer.Tick += (_, _) => Reload();
        _timer.Start();

        Reload();
    }

    /// <summary>Re-reads configuration and counters. Called on the timer and on tab switch.</summary>
    public void Reload()
    {
        var store = _configStoreCache.Current;
        var traffic = _traffic.Snapshot().ToDictionary(entry => (entry.Kind, entry.Id));

        ReloadEndpoints(store, traffic);
        ReloadClients(traffic);
        ReloadUpstreamSessions(store);
        ReloadCredentials(store);
        ReloadSummaries(store, traffic);
    }

    private void ReloadEndpoints(ConfigStore store, Dictionary<(ProxyTargetKind, Guid), EndpointTraffic> traffic)
    {
        var rows = new List<EndpointStatusViewModel>();

        foreach (var route in store.Routes)
        {
            rows.Add(Row("Route", route.PathPrefix, route.PathPrefix, route.Enabled, route.Key,
                traffic.GetValueOrDefault((ProxyTargetKind.Route, route.Id))));
        }

        foreach (var funnel in store.McpFunnels)
        {
            rows.Add(Row("Funnel", funnel.Name, $"/mcp/{funnel.Slug}", funnel.Enabled, funnel.Key,
                traffic.GetValueOrDefault((ProxyTargetKind.Funnel, funnel.Id))));
        }

        foreach (var bridge in store.McpApiBridges)
        {
            rows.Add(Row("Bridge", bridge.Name, $"/api-mcp/{bridge.Slug}", bridge.Enabled, bridge.Key,
                traffic.GetValueOrDefault((ProxyTargetKind.Bridge, bridge.Id))));
        }

        // Busiest first, then everything untouched — which is the half of this table worth reading,
        // since a route nobody has called is either new, broken, or no longer needed.
        Replace(Endpoints, rows
            .OrderByDescending(row => row.LastUsedUtc ?? DateTimeOffset.MinValue)
            .ThenBy(row => row.Kind, StringComparer.Ordinal)
            .ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase));
    }

    private static EndpointStatusViewModel Row(
        string kind, string name, string address, bool enabled, ProxyKey key, EndpointTraffic? traffic) => new()
    {
        Kind = kind,
        Name = name,
        Address = address,
        Enabled = enabled,
        Calls = traffic?.Calls ?? 0,
        Failed = traffic?.Failed ?? 0,
        Denied = traffic?.Denied ?? 0,
        ClientCount = traffic?.Clients.Count ?? 0,
        LastUsedUtc = traffic is null ? null : traffic.LastSeenUtc,
        KeyState = DescribeKey(key),
    };

    private static string DescribeKey(ProxyKey key)
    {
        if (!key.IsConfigured) return "no key";
        if (key.IsExpired(DateTimeOffset.UtcNow)) return "key expired";

        return key.ExpiresUtc is { } expiry ? $"key expires {Ago(expiry)}" : "key ok";
    }

    private void ReloadClients(Dictionary<(ProxyTargetKind, Guid), EndpointTraffic> traffic)
    {
        // The same agent usually calls several endpoints, so it is folded together here and the
        // endpoints it touched are listed against it — that is the "who is using what" view.
        var byAgent = traffic.Values
            .SelectMany(endpoint => endpoint.Clients.Select(client => (endpoint, client)))
            .GroupBy(pair => pair.client.UserAgent, StringComparer.Ordinal)
            .Select(group => new ClientStatusViewModel
            {
                UserAgent = group.Key,
                Calls = group.Sum(pair => pair.client.Calls),
                Failed = group.Sum(pair => pair.client.Failed),
                Endpoints = [.. group.Select(pair => pair.endpoint.Description).Distinct(StringComparer.Ordinal).Order(StringComparer.OrdinalIgnoreCase)],
                LastSeenUtc = group.Max(pair => pair.client.LastSeenUtc),
            })
            .OrderByDescending(client => client.LastSeenUtc);

        Replace(Clients, byAgent);
        HasClients = Clients.Count > 0;
    }

    private void ReloadUpstreamSessions(ConfigStore store)
    {
        var sessions = _connectionPool.Snapshot()
            .Select(connection => new UpstreamSessionViewModel
            {
                Funnel = connection.FunnelId == McpSourceConnectionPool.DiscoveryFunnelId
                    ? "(discovery)"
                    : store.McpFunnels.FirstOrDefault(f => f.Id == connection.FunnelId)?.Name ?? "(removed funnel)",
                Source = store.McpSources.FirstOrDefault(s => s.Id == connection.SourceId)?.Name ?? "(removed source)",
                State = connection.IsFaulted ? "faulted" : connection.IsConnected ? "connected" : "connecting",
                LastUsedUtc = connection.LastUsedUtc,
            })
            .OrderByDescending(session => session.LastUsedUtc);

        Replace(UpstreamSessions, sessions);
        HasUpstreamSessions = UpstreamSessions.Count > 0;
    }

    private void ReloadCredentials(ConfigStore store)
    {
        var rows = store.Credentials.Select(credential =>
        {
            var token = credential.Token;

            var (state, problem) = token switch
            {
                null => ("no token yet", true),
                _ when token.ExpiresAtUtc is { } expiry && expiry <= DateTimeOffset.UtcNow => ("expired", true),
                _ when token.IsExpiringWithin(TimeSpan.FromMinutes(15)) => ($"expires {Ago(token.ExpiresAtUtc)}", true),
                _ => (token.DescribeExpiry(), false),
            };

            return new CredentialHealthViewModel { Name = credential.Name, State = state, IsProblem = problem };
        });

        // Only the ones worth acting on. A healthy credential list is the Credentials tab's job.
        Replace(Credentials, rows.Where(row => row.IsProblem).OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase));
        HasCredentialProblems = Credentials.Count > 0;
    }

    private void ReloadSummaries(ConfigStore store, Dictionary<(ProxyTargetKind, Guid), EndpointTraffic> traffic)
    {
        var enabled = store.Routes.Count(r => r.Enabled)
                      + store.McpFunnels.Count(f => f.Enabled)
                      + store.McpApiBridges.Count(b => b.Enabled);

        var total = store.Routes.Count + store.McpFunnels.Count + store.McpApiBridges.Count;

        InventorySummary = $"{store.Routes.Count} route(s), {store.McpFunnels.Count} funnel(s), "
                           + $"{store.McpApiBridges.Count} bridge(s) — {enabled} of {total} switched on";

        var calls = traffic.Values.Sum(endpoint => endpoint.Calls);
        var failed = traffic.Values.Sum(endpoint => endpoint.Failed);
        var used = traffic.Values.Count(endpoint => endpoint.Calls > 0);

        HasTraffic = calls > 0;

        TrafficSummary = calls == 0
            ? "No calls yet this run."
            : $"{calls:N0} call(s) to {used} endpoint(s), {Clients.Count} caller(s)"
              + (failed > 0 ? $", {failed:N0} failed" : "");

        CountingSince = $"Counted since {_traffic.StartedUtc.ToLocalTime():HH:mm} "
                        + $"({Ago(_traffic.StartedUtc)}) — these reset when RavensPort restarts.";

        var denied = traffic.Values.Sum(endpoint => endpoint.Denied);
        var unroutable = _traffic.UnroutableDenied;

        HasDenials = denied + unroutable > 0;
        DeniedSummary = $"{denied + unroutable:N0} request(s) refused"
                        + (unroutable > 0 ? $", {unroutable:N0} of them to a path that matches no endpoint" : "")
                        + " — a caller with the wrong key, or none.";
    }

    [RelayCommand]
    private void ResetCounters()
    {
        _traffic.Clear();
        Reload();
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> rows)
    {
        target.Clear();
        foreach (var row in rows) target.Add(row);
    }

    /// <summary>
    /// "just now" / "4m ago" / "2d ago", and "never" for nothing at all. Coarse on purpose: this
    /// tab is read to tell live from dormant, and a timestamp to the second makes that harder.
    /// </summary>
    public static string Ago(DateTimeOffset? moment)
    {
        if (moment is not { } value) return "never";

        var span = DateTimeOffset.UtcNow - value;

        // Negative for a key that expires in the future, which is read the same way round.
        var future = span < TimeSpan.Zero;
        var magnitude = future ? -span : span;

        var text = magnitude switch
        {
            { TotalSeconds: < 10 } => "just now",
            { TotalMinutes: < 1 } => $"{(int)magnitude.TotalSeconds}s",
            { TotalHours: < 1 } => $"{(int)magnitude.TotalMinutes}m",
            { TotalDays: < 1 } => $"{(int)magnitude.TotalHours}h",
            _ => $"{(int)magnitude.TotalDays}d",
        };

        if (text == "just now") return text;

        return future ? $"in {text}" : $"{text} ago";
    }
}
