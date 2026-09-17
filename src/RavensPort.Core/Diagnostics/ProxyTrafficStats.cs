using System.Collections.Concurrent;
using RavensPort.Core.Proxy;

namespace RavensPort.Core.Diagnostics;

/// <summary>What one caller of one endpoint has done, as far as this proxy can tell them apart.</summary>
public sealed record ClientTraffic(string UserAgent, long Calls, long Failed, DateTimeOffset LastSeenUtc);

/// <summary>One endpoint's traffic since the proxy started.</summary>
public sealed record EndpointTraffic(
    ProxyTargetKind Kind,
    Guid Id,
    string Description,
    long Calls,
    long Denied,
    long Failed,
    DateTimeOffset FirstSeenUtc,
    DateTimeOffset LastSeenUtc,
    IReadOnlyList<ClientTraffic> Clients);

/// <summary>
/// Counts what actually goes through the proxy, per endpoint and per caller, so the Dashboard can
/// answer "what is being used, by what, and what has gone quiet".
///
/// In memory only, and deliberately so. Nothing here is written to disk: the activity log is
/// already the durable record, and these counters are a picture of the current run rather than a
/// history worth keeping. They reset when the proxy restarts, which the Dashboard says plainly by
/// showing what they are counted from.
///
/// A caller is identified only by its User-Agent. On a loopback-only proxy the remote address is
/// always 127.0.0.1 and the port is ephemeral, so the agent string is the one thing that
/// distinguishes Claude Code from a browser from curl. It is also attacker-controlled text, so it
/// is truncated and stripped of control characters before it is kept, and the number of distinct
/// ones per endpoint is capped — otherwise anything that varies its agent per request would grow
/// this without limit.
/// </summary>
public sealed class ProxyTrafficStats
{
    /// <summary>Past this many distinct agents on one endpoint, the rest are pooled together.</summary>
    private const int MaxClientsPerEndpoint = 24;

    private const int MaxUserAgentLength = 180;

    internal const string UnknownClient = "(no user agent)";
    internal const string OverflowClient = "(other clients)";

    private readonly ConcurrentDictionary<(ProxyTargetKind Kind, Guid Id), EndpointCounters> _endpoints = new();

    /// <summary>When counting began — the proxy's start, since nothing here survives a restart.</summary>
    public DateTimeOffset StartedUtc { get; } = DateTimeOffset.UtcNow;

    /// <summary>Requests refused before they reached an endpoint that could be named.</summary>
    public long UnroutableDenied => Interlocked.Read(ref _unroutableDenied);

    private long _unroutableDenied;

    /// <summary>
    /// One call against one endpoint. <paramref name="denied"/> means the guard refused it, in
    /// which case no upstream was reached and it is not counted as a call.
    /// </summary>
    public void Record(
        ProxyTargetKind kind,
        Guid id,
        string description,
        string? userAgent,
        int statusCode,
        bool denied)
    {
        var now = DateTimeOffset.UtcNow;
        var counters = _endpoints.GetOrAdd((kind, id), _ => new EndpointCounters(description, now));

        counters.Description = description;
        counters.Touch(now);

        if (denied)
        {
            Interlocked.Increment(ref counters.Denied);
            return;
        }

        Interlocked.Increment(ref counters.Calls);

        var failed = statusCode >= 400;
        if (failed) Interlocked.Increment(ref counters.Failed);

        counters.RecordClient(Normalize(userAgent), failed, now);
    }

    /// <summary>A request refused before it could be attributed to any endpoint.</summary>
    public void RecordUnroutableDenial() => Interlocked.Increment(ref _unroutableDenied);

    public IReadOnlyList<EndpointTraffic> Snapshot() =>
        [.. _endpoints
            .Select(pair => pair.Value.ToRecord(pair.Key.Kind, pair.Key.Id))
            .OrderByDescending(endpoint => endpoint.LastSeenUtc)];

    /// <summary>Forgets everything counted so far, as the Dashboard's Reset does.</summary>
    public void Clear()
    {
        _endpoints.Clear();
        Interlocked.Exchange(ref _unroutableDenied, 0);
    }

    /// <summary>
    /// Untrusted text on its way to a WPF list: control characters out, length capped. A blank
    /// agent is named rather than dropped — "no user agent" is itself a useful thing to see.
    /// </summary>
    private static string Normalize(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent)) return UnknownClient;

        var cleaned = new string([.. userAgent.Where(c => !char.IsControl(c))]).Trim();

        if (cleaned.Length == 0) return UnknownClient;

        return cleaned.Length > MaxUserAgentLength ? cleaned[..MaxUserAgentLength] : cleaned;
    }

    private sealed class EndpointCounters(string description, DateTimeOffset firstSeen)
    {
        private readonly ConcurrentDictionary<string, ClientCounters> _clients = new(StringComparer.Ordinal);
        private readonly DateTimeOffset _firstSeen = firstSeen;
        private long _lastSeenTicks = firstSeen.UtcTicks;

        public long Calls;
        public long Denied;
        public long Failed;

        public string Description { get; set; } = description;

        public void Touch(DateTimeOffset now)
        {
            // Monotonic under concurrency: a slower thread must not drag the last-seen backwards.
            var ticks = now.UtcTicks;
            long seen;

            while ((seen = Interlocked.Read(ref _lastSeenTicks)) < ticks
                   && Interlocked.CompareExchange(ref _lastSeenTicks, ticks, seen) != seen)
            {
                // Another thread moved it first; re-read and decide again.
            }
        }

        public void RecordClient(string userAgent, bool failed, DateTimeOffset now)
        {
            // Past the cap everything lands in one bucket rather than being dropped, so the counts
            // still add up to the endpoint's total.
            var key = _clients.Count >= MaxClientsPerEndpoint && !_clients.ContainsKey(userAgent)
                ? OverflowClient
                : userAgent;

            _clients.GetOrAdd(key, _ => new ClientCounters()).Record(failed, now);
        }

        public EndpointTraffic ToRecord(ProxyTargetKind kind, Guid id) => new(
            kind,
            id,
            Description,
            Interlocked.Read(ref Calls),
            Interlocked.Read(ref Denied),
            Interlocked.Read(ref Failed),
            _firstSeen,
            new DateTimeOffset(Interlocked.Read(ref _lastSeenTicks), TimeSpan.Zero),
            [.. _clients
                .Select(pair => pair.Value.ToRecord(pair.Key))
                .OrderByDescending(client => client.LastSeenUtc)]);
    }

    private sealed class ClientCounters
    {
        private long _calls;
        private long _failed;
        private long _lastSeenTicks;

        public void Record(bool failed, DateTimeOffset now)
        {
            Interlocked.Increment(ref _calls);
            if (failed) Interlocked.Increment(ref _failed);
            Interlocked.Exchange(ref _lastSeenTicks, now.UtcTicks);
        }

        public ClientTraffic ToRecord(string userAgent) => new(
            userAgent,
            Interlocked.Read(ref _calls),
            Interlocked.Read(ref _failed),
            new DateTimeOffset(Interlocked.Read(ref _lastSeenTicks), TimeSpan.Zero));
    }
}
