using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RavensPort.Core.Admin;

/// <summary>
/// Where the admin socket lives, and how to talk to it.
///
/// **Why a socket and not the proxy port.** Management has to reach whichever RavensPort process
/// owns the vault — two writers on one vault corrupt its index — but the proxy port is reachable by
/// every process on the machine, and LocalAccessGuard's whole job there is to refuse callers
/// without an endpoint's key. A Unix domain socket in a directory only this user can enter needs
/// no key at all: the operating system has already decided who can connect.
///
/// One mechanism on both platforms. Windows has supported AF_UNIX since 10 1803, and the socket
/// sits under the user's own LocalAppData, which no other non-administrator account can open.
/// </summary>
public static class AdminChannel
{
    /// <summary>Overrides the socket path, for tests and for running two isolated instances.</summary>
    public const string SocketPathVariable = "RAVENSPORT_ADMIN_SOCKET";

    public static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    public static string SocketPath()
    {
        if (Environment.GetEnvironmentVariable(SocketPathVariable) is { Length: > 0 } overridden) return overridden;

        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RavensPort", "admin.sock");
        }

        // The runtime directory is the right home: tmpfs, private to the user, and emptied at
        // logout, so a socket left by a crash cannot outlive the session. A system service without
        // one falls back to the state directory.
        var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        var root = !string.IsNullOrEmpty(runtime)
            ? runtime
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state");

        return Path.Combine(root, "ravensport", "admin.sock");
    }

    /// <summary>
    /// An HTTP client whose every connection goes to the socket. The host name in the base address
    /// is never resolved; it is there because HTTP needs one.
    /// </summary>
    public static HttpClient CreateClient(string? socketPath = null)
    {
        var path = socketPath ?? SocketPath();

        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, ct) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), ct).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };

        // Sign-in waits on a person, so no client-side timeout; the server bounds its own flows.
        return new HttpClient(handler) { BaseAddress = new Uri("http://ravensport/admin/"), Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>
    /// Makes the socket's directory, owner-only, and removes a socket a crashed process left
    /// behind. Safe because only one RavensPort runs at a time — the single-instance mutex is
    /// taken before this is reached — so a socket already here cannot belong to a live server.
    /// </summary>
    internal static void PrepareSocket(string path)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        if (File.Exists(path)) File.Delete(path);
    }
}
