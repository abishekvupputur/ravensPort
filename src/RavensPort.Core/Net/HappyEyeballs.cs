using System.Net;
using System.Net.Sockets;

namespace RavensPort.Core.Net;

/// <summary>
/// Connects over whichever of IPv6 and IPv4 answers first, rather than whichever DNS happened to
/// list first.
///
/// **The failure this exists for.** A router that advertises a default IPv6 route it cannot
/// actually carry is common — a misconfigured RA, an ISP that hands out a prefix without transit, a
/// container host with IPv6 enabled and unrouted. The host then has a global IPv6 address, DNS
/// returns AAAA records, and every connection to them hangs until something gives up.
///
/// <see cref="SocketsHttpHandler"/> tries addresses strictly in the order DNS returned them, which
/// puts AAAA first, and waits the full connect timeout on each before moving on. With
/// <see cref="HttpClient"/>'s default hundred-second timeout, the request dies before IPv4 is ever
/// attempted. Observed exactly that way: curl reached the same host in 0.4 seconds while every call
/// from this app timed out, because curl does what this class does and the runtime does not.
///
/// **What it does.** RFC 8305 in the small: start the first family, give it a short head start, and
/// if it has not connected by then start the other in parallel. First socket to connect wins; the
/// loser is disposed. A working network is unaffected, because the winner is decided in the time it
/// takes to complete a handshake and the head start is never spent.
/// </summary>
public static class HappyEyeballs
{
    /// <summary>
    /// How long the first family gets alone before the second is tried alongside it. RFC 8305 calls
    /// this the Connection Attempt Delay and recommends 250ms; the same value is used here. Long
    /// enough that a healthy network never opens a second socket, short enough that a black hole
    /// costs a quarter of a second rather than a timeout.
    /// </summary>
    private static readonly TimeSpan AttemptDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// A handler that connects this way. Everything in this app that makes an outbound HTTPS call
    /// should be built from one of these — see the callers, which is every HTTP path in the app.
    /// </summary>
    /// <param name="configure">Applied before the callback is attached, for mTLS and the like.</param>
    public static SocketsHttpHandler CreateHandler(Action<SocketsHttpHandler>? configure = null)
    {
        var handler = new SocketsHttpHandler();
        configure?.Invoke(handler);

        handler.ConnectCallback = ConnectAsync;
        return handler;
    }

    private static async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var endPoint = context.DnsEndPoint;

        // An address literal has nothing to race. Also the path taken for a proxy configured by IP.
        if (IPAddress.TryParse(endPoint.Host, out var literal))
        {
            return await ConnectToAsync(new IPEndPoint(literal, endPoint.Port), ct).ConfigureAwait(false);
        }

        var addresses = await Dns.GetHostAddressesAsync(endPoint.Host, ct).ConfigureAwait(false);
        if (addresses.Length == 0)
        {
            throw new SocketException((int)SocketError.HostNotFound);
        }

        var v6 = addresses.Where(a => a.AddressFamily == AddressFamily.InterNetworkV6).ToArray();
        var v4 = addresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork).ToArray();

        // Only one family available: nothing to race, and no head start to waste.
        if (v6.Length == 0 || v4.Length == 0)
        {
            return await ConnectToFirstWorkingAsync(addresses, endPoint.Port, ct).ConfigureAwait(false);
        }

        // The order DNS gave, honoured: whichever family came first is the one that gets the head
        // start. This is a tie-breaker, not a preference — a host whose IPv6 works still uses it.
        var (first, second) = addresses[0].AddressFamily == AddressFamily.InterNetworkV6
            ? (v6, v4)
            : (v4, v6);

        using var loserCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var firstAttempt = ConnectToFirstWorkingAsync(first, endPoint.Port, loserCancellation.Token);

        // If the head start elapses without a connection, the second family joins in. If the first
        // family fails outright before then, the second starts immediately.
        var headStart = Task.Delay(AttemptDelay, loserCancellation.Token);
        var settled = await Task.WhenAny(firstAttempt, headStart).ConfigureAwait(false);

        if (settled == firstAttempt && firstAttempt.IsCompletedSuccessfully)
        {
            await loserCancellation.CancelAsync().ConfigureAwait(false);
            return await firstAttempt.ConfigureAwait(false);
        }

        var secondAttempt = ConnectToFirstWorkingAsync(second, endPoint.Port, loserCancellation.Token);

        var winner = await Task.WhenAny(firstAttempt, secondAttempt).ConfigureAwait(false);

        if (winner.IsCompletedSuccessfully)
        {
            await loserCancellation.CancelAsync().ConfigureAwait(false);

            // The losing attempt may still complete and hand back a connected socket nobody wants.
            Discard(winner == firstAttempt ? secondAttempt : firstAttempt);

            return await winner.ConfigureAwait(false);
        }

        // The first to finish failed; the other is still the only hope.
        var remaining = winner == firstAttempt ? secondAttempt : firstAttempt;

        try
        {
            var stream = await remaining.ConfigureAwait(false);
            await loserCancellation.CancelAsync().ConfigureAwait(false);
            return stream;
        }
        catch
        {
            // Both families failed. Surface the first failure, which is the one about the family
            // DNS preferred and so the one that describes what the user's network actually did.
            await winner.ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Walks one family's addresses in order, returning the first that connects. A host with
    /// several A records where the first is down is an ordinary thing, and is not what the race
    /// above is for.
    /// </summary>
    private static async Task<Stream> ConnectToFirstWorkingAsync(
        IReadOnlyList<IPAddress> addresses, int port, CancellationToken ct)
    {
        Exception? last = null;

        foreach (var address in addresses)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                return await ConnectToAsync(new IPEndPoint(address, port), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException && !ct.IsCancellationRequested)
            {
                last = ex;
            }
        }

        throw last ?? new SocketException((int)SocketError.HostUnreachable);
    }

    private static async Task<Stream> ConnectToAsync(IPEndPoint endPoint, CancellationToken ct)
    {
        // NoDelay because this carries request/response traffic, where Nagle only adds latency.
        var socket = new Socket(endPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };

        try
        {
            await socket.ConnectAsync(endPoint, ct).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Disposes whatever the losing attempt produces, whenever it produces it. Without this a race
    /// that IPv4 wins leaves the IPv6 socket open until the finalizer runs.
    /// </summary>
    private static void Discard(Task<Stream> loser) => _ = loser.ContinueWith(
        static t =>
        {
            if (t.IsCompletedSuccessfully) t.Result.Dispose();
            else _ = t.Exception; // Observed, so it cannot reach the unobserved-exception handler.
        },
        CancellationToken.None,
        TaskContinuationOptions.ExecuteSynchronously,
        TaskScheduler.Default);
}
