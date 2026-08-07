using RavensPort.Core.Net;
using System.Collections.Concurrent;
using System.Security.Cryptography.X509Certificates;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using RavensPort.Core.Diagnostics;
using RavensPort.Core.Models;
using RavensPort.Core.Proxy;
using RavensPort.Core.Storage;

namespace RavensPort.Core.Mcp;

/// <summary>
/// Holds the MCP client sessions a funnel talks to its sources through.
///
/// Keyed by (funnel, source) rather than by source alone, and that is the whole point. An MCP
/// session is stateful — the upstream may hang pagination cursors, resource subscriptions, or
/// its own notion of "current context" off it. Sharing one session between two funnels would let
/// one agent's activity perturb another's, and would collapse both endpoints together the moment
/// that single session expired. Per-edge keying makes every local endpoint behave like a
/// standalone MCP client of that upstream: independent state, independent failure.
///
/// Requests are *not* serialized per session. McpClient multiplexes concurrent requests over one
/// connection by JSON-RPC id, so several calls on the same edge stay in flight together and
/// responses find their own caller. The pool's only job is handing out the right session.
/// </summary>
public sealed class McpSourceConnectionPool : IAsyncDisposable
{
    /// <summary>
    /// Funnel id used by the GUI's "Refresh" discovery. Discovery must not borrow a live funnel's
    /// session: a manual refresh in the UI would otherwise be able to disturb — or, on failure,
    /// tear down — a session an agent is in the middle of using.
    /// </summary>
    public static readonly Guid DiscoveryFunnelId = Guid.Empty;

    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(10);

    private readonly ConfigStoreCache _configStoreCache;
    private readonly ActivityLog _activityLog;
    private readonly KestrelMtlsState _kestrelMtls;

    private readonly ConcurrentDictionary<ConnectionKey, ConnectionEntry> _connections = new();
    private volatile bool _disposed;

    public McpSourceConnectionPool(ConfigStoreCache configStoreCache, ActivityLog activityLog, KestrelMtlsState kestrelMtls)
    {
        _configStoreCache = configStoreCache;
        _activityLog = activityLog;
        _kestrelMtls = kestrelMtls;
    }

    private readonly record struct ConnectionKey(Guid FunnelId, Guid SourceId);

    private sealed class ConnectionEntry
    {
        public required Task<McpClient> Client { get; init; }
        public DateTimeOffset LastUsedUtc { get; set; } = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Runs one operation against a source's session for a funnel.
    ///
    /// <paramref name="isIdempotent"/> decides what happens when the session turns out to be
    /// dead — an expired Mcp-Session-Id (the upstream answers 404), a restarted server, or an
    /// idle SSE stream that YARP's 30-minute activity timeout cut. For a list or a read the
    /// session is rebuilt and the operation runs once more, which is invisible and correct. For
    /// tools/call it is not: the upstream may have executed the call before the transport
    /// failed, and silently repeating a side effect is worse than surfacing the error. There the
    /// session is dropped so the *next* call reconnects, and this one fails.
    /// </summary>
    public async ValueTask<TResult> ExecuteAsync<TResult>(
        Guid funnelId,
        McpSourceRecord source,
        Func<McpClient, CancellationToken, ValueTask<TResult>> operation,
        bool isIdempotent,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var key = new ConnectionKey(funnelId, source.Id);

        try
        {
            var client = await GetOrCreateAsync(key, source, cancellationToken).ConfigureAwait(false);
            return await operation(client, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller went away; the session is fine and must survive.
            throw;
        }
        catch (McpException)
        {
            // The server replied — with a refusal, but it replied. "Method not found:
            // resources/list" from a tools-only server is the common case, and treating it as a
            // dead connection tore down a working session and re-handshook on every discovery
            // pass. A protocol error says nothing about the transport, so the session stands.
            throw;
        }
        catch (Exception ex)
        {
            await InvalidateAsync(key).ConfigureAwait(false);

            if (!isIdempotent)
            {
                _activityLog.Log($"MCP source '{source.Name}' failed and its session was dropped — {ex.Message}");
                throw;
            }

            _activityLog.Log($"MCP source '{source.Name}' session was stale, reconnecting — {ex.Message}");

            var client = await GetOrCreateAsync(key, source, cancellationToken).ConfigureAwait(false);
            return await operation(client, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<McpClient> GetOrCreateAsync(ConnectionKey key, McpSourceRecord source, CancellationToken cancellationToken)
    {
        EvictIdle();

        // The task — not the completed client — is what gets cached, so concurrent first callers
        // await one handshake instead of racing to open several sessions to the same upstream.
        var entry = _connections.GetOrAdd(key, _ => new ConnectionEntry
        {
            Client = ConnectAsync(source, CancellationToken.None),
        });

        entry.LastUsedUtc = DateTimeOffset.UtcNow;

        try
        {
            return await entry.Client.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // A failed handshake must not be cached, or the source stays broken until restart
            // even after whatever caused it is fixed.
            await InvalidateAsync(key).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<McpClient> ConnectAsync(McpSourceRecord source, CancellationToken cancellationToken)
    {
        var options = BuildTransportOptions(source);

        // Only the hop back into this app's own listener speaks mTLS. A remote source is whatever
        // it is — usually ordinary public TLS — and handing it this proxy's private certificate,
        // or pinning its server certificate to one it has never heard of, would break it.
        var transport = source.Kind == McpSourceKind.ProxyRoute && _kestrelMtls.IsEnabled
            ? new HttpClientTransport(options, new HttpClient(CreateMtlsHandler()), null, ownsHttpClient: true)
            // Also given an explicit client, where this used to take the transport's default. The
            // default connects in DNS order, which on a host with a broken IPv6 route means every
            // source times out rather than falling back — see HappyEyeballs.
            : new HttpClientTransport(options, new HttpClient(HappyEyeballs.CreateHandler()), null, ownsHttpClient: true);

        var mcpClient = await McpClient.CreateAsync(transport, cancellationToken: cancellationToken).ConfigureAwait(false);
        _activityLog.Log($"MCP source '{source.Name}' connected ({mcpClient.ServerInfo?.Name ?? "unnamed server"})");

        return mcpClient;
    }

    /// <summary>
    /// The handler for the hop back into this app's own mTLS listener.
    ///
    /// Both ends hold the same self-signed certificate, so neither can validate the other by
    /// chain: the server has no issuer the client trusts, and vice versa. Pinning replaces that —
    /// on loopback, the only certificate accepted is the one the user generated, and chain and
    /// name errors are expected and deliberately not consulted.
    ///
    /// The word "loopback" is doing real work there, because this handler does not only ever see
    /// this app. The hop is one HTTP request and the upstream behind the route is free to answer
    /// with a redirect somewhere else entirely — Google Apps Script always answers 302 to
    /// script.googleusercontent.com — and HttpClient follows it on this same handler. Applying the
    /// pin to that leg rejected a perfectly good public certificate, which is what turned every
    /// Apps Script MCP source into "The SSL connection could not be established"; offering the
    /// user's private client certificate on it was the quieter half of the same mistake. Off
    /// loopback this is therefore an ordinary HTTPS client and nothing more.
    /// </summary>
    internal SocketsHttpHandler CreateMtlsHandler()
    {
        // Not null: IsEnabled is exactly "a certificate is loaded", and this is only reached
        // behind that check.
        var certificate = _kestrelMtls.Certificate!;
        var expectedThumbprint = certificate.Thumbprint;

        return HappyEyeballs.CreateHandler(handler =>
            handler.SslOptions = new System.Net.Security.SslClientAuthenticationOptions
            {
                ClientCertificates = [certificate],

                // targetHost is the name being dialled on this connection, so it still says
                // "somewhere else" after a redirect, when the endpoint this handler was built for
                // no longer does.
                LocalCertificateSelectionCallback = (_, targetHost, _, _, _) =>
                    IsLoopback(targetHost) ? certificate : null!,

                RemoteCertificateValidationCallback = (_, presented, _, errors) =>
                {
                    // GetCertHashString on the base type rather than X509Certificate2.Thumbprint:
                    // both are the SHA-1 hash in the same hex form, but SslStream is only
                    // contracted to hand back an X509Certificate, and a type check that failed
                    // would read as "wrong certificate" — the one conclusion that is definitely
                    // not what happened.
                    var actual = presented?.GetCertHashString();
                    if (string.Equals(actual, expectedThumbprint, StringComparison.OrdinalIgnoreCase))
                    {
                        // The pin does not exempt it from its own dates. Pinning replaces chain
                        // validation, which is what would ordinarily have caught this; leaving it
                        // out would mean the listener refuses an expired certificate while the
                        // funnel accepts one, and the two are the same certificate.
                        if (MtlsCertificateFactory.IsWithinValidity(presented!, DateTimeOffset.UtcNow)) return true;

                        _activityLog.Log(
                            $"The MCP funnel refused the stored mTLS certificate {Redact(actual)}: it is "
                            + "outside its validity window. Generate a new one on the Settings tab, install "
                            + "it on every client, and restart RavensPort.");

                        return false;
                    }

                    // Anything that is not the pinned certificate has to earn trust the ordinary
                    // way. On loopback nothing can: a public CA does not issue for 127.0.0.1, so
                    // this stays a pin there and only relaxes where it has to.
                    if (errors == System.Net.Security.SslPolicyErrors.None) return true;

                    _activityLog.Log(
                        "The MCP funnel refused a TLS certificate: expected the stored mTLS "
                        + $"certificate {Redact(expectedThumbprint)}, was offered "
                        + $"{Redact(actual) ?? "no certificate"} ({errors}).");

                    return false;
                },
            });

        // Enough to tell two certificates apart in a log without writing a full identifier of the
        // user's own credential into it.
        static string? Redact(string? thumbprint) =>
            thumbprint is null ? null : $"…{thumbprint[^8..]}";
    }

    /// <summary>
    /// Whether a host being dialled is this machine. Only these get the client certificate and the
    /// pinned server certificate; a redirect anywhere else is ordinary public HTTPS.
    /// </summary>
    internal static bool IsLoopback(string? host)
    {
        if (string.IsNullOrEmpty(host)) return false;
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)) return true;

        // Strips the brackets an IPv6 literal carries in a URI authority; IPAddress.TryParse does
        // not accept them.
        var trimmed = host.StartsWith('[') && host.EndsWith(']') ? host[1..^1] : host;

        return System.Net.IPAddress.TryParse(trimmed, out var address) && System.Net.IPAddress.IsLoopback(address);
    }

    /// <summary>
    /// Builds the transport for a source. A route-backed source is dialled back through this
    /// app's own listener so the request takes the ordinary proxied path — LocalAccessGuard, then
    /// YARP, then the credential transform — and picks up the OAuth token with zero duplication
    /// of that logic here.
    /// </summary>
    public HttpClientTransportOptions BuildTransportOptions(McpSourceRecord source)
    {
        var store = _configStoreCache.Current;
        var headers = new Dictionary<string, string>();
        Uri endpoint;

        if (source.Kind == McpSourceKind.ProxyRoute)
        {
            var route = store.Routes.FirstOrDefault(r => r.Id == source.RouteId)
                        ?? throw new InvalidOperationException(
                            $"MCP source '{source.Name}' points at a route that no longer exists.");

            var prefix = route.PathPrefix.TrimEnd('/');

            // Scheme comes from the listener's own state, never from settings. The setting is
            // what the user asked for; this is what Kestrel actually bound, and only the second
            // one is dialable — they differ for the whole of a session where mTLS was switched
            // on or off and the restart it asks for has not happened yet.
            endpoint = new Uri($"{_kestrelMtls.Scheme}://127.0.0.1:{store.Settings.ListenPort}{prefix}");

            // The route's own key, not the funnel's: this hop is an ordinary call to that route,
            // and the guard on the way back in accepts nothing else. Read live rather than
            // captured at construction, so regenerating the key on the Routes tab doesn't leave
            // every funnel authenticating with a stale one.
            //
            // A route whose key has expired fails here exactly as any other client would, with
            // 403 from the guard. That is the point of an expiry the user set.
            headers[LocalAccessGuard.ApiKeyHeaderName] = route.Key.Value;

            // Marks the request as originating from the funnel itself. The funnel gate refuses
            // anything already carrying it, which is what stops a source that resolves back to
            // /mcp from recursing.
            headers[LocalAccessGuard.FunnelHopHeaderName] = "1";
        }
        else
        {
            endpoint = new Uri(source.Url);
        }

        return new HttpClientTransportOptions
        {
            Endpoint = endpoint,
            Name = source.Name,
            TransportMode = source.Transport switch
            {
                McpTransportPreference.StreamableHttp => HttpTransportMode.StreamableHttp,
                McpTransportPreference.Sse => HttpTransportMode.Sse,
                _ => HttpTransportMode.AutoDetect,
            },
            // Generous on purpose. A cold-starting serverless MCP server — a Google Apps Script
            // deployment is the case that forced this — can take the better part of a minute to
            // answer its first initialize, and a timeout there is indistinguishable from "this
            // server has no tools". Nothing is held open while waiting, so the only cost of a
            // long ceiling is how long a genuinely dead source takes to report itself.
            ConnectionTimeout = TimeSpan.FromMinutes(2),
            AdditionalHeaders = headers,
        };
    }

    /// <summary>
    /// Asks a source what it currently offers, for the GUI's selection lists.
    ///
    /// Runs on the reserved discovery key rather than any funnel's, so a manual Refresh cannot
    /// borrow — or, if it fails, tear down — a session an agent is in the middle of using.
    /// Errors are returned rather than thrown: an unreachable source should colour one row in the
    /// grid, not raise a dialog.
    /// </summary>
    public async Task<McpSourceCatalog> DiscoverAsync(McpSourceRecord source, CancellationToken cancellationToken = default)
    {
        try
        {
            // Connect first, as its own step. Previously the handshake happened inside the
            // per-capability listing below, whose catch swallowed everything — so a source that
            // could not be reached at all came back as an empty catalog with no error, and the
            // UI reported "connected — nothing offered". A dead upstream and a server with no
            // tools looked identical, which is the worst possible answer to "why are there no
            // tools?".
            await ExecuteAsync(
                DiscoveryFunnelId,
                source,
                static (client, _) => ValueTask.FromResult(client.ServerInfo?.Name ?? ""),
                isIdempotent: true,
                cancellationToken).ConfigureAwait(false);

            var tools = await ListAsync(source, (client, ct) => client.ListToolsAsync(cancellationToken: ct), t => t.Name, cancellationToken);
            var resources = await ListAsync(source, (client, ct) => client.ListResourcesAsync(cancellationToken: ct), r => r.Uri, cancellationToken);
            var prompts = await ListAsync(source, (client, ct) => client.ListPromptsAsync(cancellationToken: ct), p => p.Name, cancellationToken);

            return new McpSourceCatalog(tools, resources, prompts, DateTimeOffset.UtcNow, Error: null);
        }
        catch (Exception ex)
        {
            _activityLog.Log($"MCP source '{source.Name}' could not be reached — {ex.Message}");
            return McpSourceCatalog.Failed(Describe(ex));
        }
    }

    /// <summary>
    /// One primitive kind for <see cref="DiscoverAsync"/>.
    ///
    /// Only a protocol-level refusal is tolerated: a server that implements no prompts answers
    /// "method not found", and most servers offer tools and nothing else, so treating that as a
    /// fault would mark almost every healthy source as broken. A transport failure is a
    /// different thing entirely and is left to propagate — the session was established a moment
    /// ago, so it means something genuinely went wrong.
    /// </summary>
    private async Task<List<string>> ListAsync<TItem>(
        McpSourceRecord source,
        Func<McpClient, CancellationToken, ValueTask<IList<TItem>>> list,
        Func<TItem, string> name,
        CancellationToken cancellationToken)
    {
        try
        {
            var items = await ExecuteAsync(DiscoveryFunnelId, source, list, isIdempotent: true, cancellationToken)
                .ConfigureAwait(false);

            return [.. items.Select(name)];
        }
        catch (McpException)
        {
            return [];
        }
    }

    /// <summary>
    /// A message worth putting in a grid cell. Transport failures nest the useful part one or
    /// two levels down — the outer text is usually "An error occurred while sending the request".
    /// </summary>
    private static string Describe(Exception ex)
    {
        var innermost = ex;
        while (innermost.InnerException is { } inner)
        {
            innermost = inner;
        }

        return ReferenceEquals(innermost, ex) ? ex.Message : $"{ex.Message} ({innermost.Message})";
    }

    /// <summary>Drops every session belonging to one funnel — used when that funnel is edited or deleted.</summary>
    public Task InvalidateFunnelAsync(Guid funnelId) =>
        InvalidateWhereAsync(key => key.FunnelId == funnelId);

    /// <summary>Drops every session to one source, across all funnels — used when the source is edited or deleted.</summary>
    public Task InvalidateSourceAsync(Guid sourceId) =>
        InvalidateWhereAsync(key => key.SourceId == sourceId);

    public Task InvalidateAllAsync() => InvalidateWhereAsync(_ => true);

    /// <summary>
    /// Removes every matching edge and closes it, without waiting on any handshake that has not
    /// landed yet.
    ///
    /// Both halves of that matter, and both were learned from Disconnect. It runs this before it
    /// can finish, and it used to walk the edges one after another, awaiting each session's
    /// *connect* before starting on the next — so an unreachable source made the whole app sit on
    /// its confirmation screen for as long as that upstream took to give up, multiplied by the
    /// number of edges. Waiting for a connection that is being thrown away is never useful: what
    /// the caller needs is for the entry to be gone from the dictionary, and that has already
    /// happened by the time <see cref="DetachAsync"/> decides whether to block.
    ///
    /// The close itself still happens — a session left open holds an Mcp-Session-Id the upstream
    /// is counting, so dropping it silently would leak one per disconnect. It just happens on its
    /// own once the handshake resolves, rather than in front of the user.
    /// </summary>
    private Task InvalidateWhereAsync(Func<ConnectionKey, bool> predicate)
    {
        var closing = _connections.Keys
            .Where(predicate)
            .Select(key => _connections.TryRemove(key, out var entry) ? DetachAsync(entry) : Task.CompletedTask);

        // Concurrently: these are independent upstreams, and a dispose is a network round trip of
        // its own — the session-termination DELETE — so serialising them only adds up.
        return Task.WhenAll(closing);
    }

    /// <summary>
    /// Closes a session, awaiting the close only when there is nothing left to wait for.
    ///
    /// A handshake still in flight cannot be disposed until it produces a client, so that case is
    /// left to finish on its own. Everything else — connected, or faulted, both of which complete
    /// immediately — is awaited, so the common path still reports when it is done.
    /// </summary>
    private static Task DetachAsync(ConnectionEntry entry)
    {
        if (!entry.Client.IsCompleted)
        {
            _ = InvalidateAsync(entry);
            return Task.CompletedTask;
        }

        return InvalidateAsync(entry);
    }

    private static async Task InvalidateAsync(ConnectionEntry entry)
    {
        try
        {
            var client = await entry.Client.ConfigureAwait(false);
            await client.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // Already broken, or never connected. Either way there is nothing left to close.
        }
    }

    private async Task InvalidateAsync(ConnectionKey key)
    {
        if (_connections.TryRemove(key, out var entry))
        {
            await InvalidateAsync(entry).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Opportunistic, on the access path rather than on a timer — the dictionary holds one entry
    /// per funnel-source edge, so it is tiny and a scan costs nothing worth a background service.
    /// </summary>
    private void EvictIdle()
    {
        var cutoff = DateTimeOffset.UtcNow - IdleTimeout;

        foreach (var (key, entry) in _connections)
        {
            if (entry.LastUsedUtc >= cutoff) continue;
            if (!_connections.TryRemove(key, out var removed)) continue;

            _ = InvalidateAsync(removed);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await InvalidateAllAsync().ConfigureAwait(false);
    }
}
