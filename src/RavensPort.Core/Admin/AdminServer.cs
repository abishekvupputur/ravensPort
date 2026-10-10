using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RavensPort.Core.Diagnostics;

namespace RavensPort.Core.Admin;

/// <summary>
/// The admin API, on its own Kestrel bound to the admin socket.
///
/// A second, tiny web host rather than a second listener on the proxy's. The proxy's listen
/// address is decided late — the port is in the vault — and Kestrel ignores the late-set URLs
/// entirely once any endpoint is configured explicitly, so adding the socket there would have
/// silently taken the proxy off its port. Separate hosts also mean no request to the proxy port
/// can ever reach /admin, with no middleware to get the order of wrong.
///
/// The endpoints act on the main host's services, passed in, so there is one store, one vault and
/// one sync queue whichever door a change came through.
/// </summary>
public sealed class AdminServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    private AdminServer(WebApplication app, string socketPath)
    {
        _app = app;
        SocketPath = socketPath;
    }

    public string SocketPath { get; }

    /// <param name="services">The main host's services.</param>
    /// <param name="hostDescription">Who is answering — the desktop app, or the headless server.</param>
    /// <param name="changesApplied">
    /// Raised after any edit, from a thread-pool thread, so the desktop app can rebuild its tabs.
    /// </param>
    public static async Task<AdminServer> StartAsync(
        IServiceProvider services,
        string hostDescription,
        Action? changesApplied = null,
        string? socketPath = null)
    {
        var path = socketPath ?? AdminChannel.SocketPath();
        AdminChannel.PrepareSocket(path);

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.ListenUnixSocket(path));
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            foreach (var converter in AdminChannel.JsonOptions.Converters) options.SerializerOptions.Converters.Add(converter);
        });

        var app = builder.Build();
        AdminEndpoints.Map(app, new AdminEndpoints.Context(services, hostDescription, changesApplied ?? (() => { })));

        await app.StartAsync().ConfigureAwait(false);

        // The directory is already owner-only; this is the second lock on the same door, for a
        // directory someone loosened by hand.
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        services.GetService<ActivityLog>()?.Log($"ADMIN listening on {path}");

        return new AdminServer(app, path);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await _app.StopAsync(timeout.Token).ConfigureAwait(false);
            await _app.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            try { File.Delete(SocketPath); } catch { /* the next start removes it anyway */ }
        }
    }
}
