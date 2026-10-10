using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RavensPort.Core.Admin;
using RavensPort.Core.Auth;
using RavensPort.Core.Diagnostics;
using RavensPort.Core.Mcp;
using RavensPort.Core.Proxy;
using RavensPort.Core.Storage;
using RavensPort.Core.Vault;

namespace RavensPort.Headless;

internal enum Backend
{
    OnePassword,
    ProtonPass,
}

internal sealed record ServeOptions(Backend Backend, string? Vault, string? CreateVault, bool ReadOnly);

/// <summary>
/// <c>ravensport serve</c>: the proxy, the funnels and the bridges, with no window.
///
/// The same pipeline the desktop app runs — <see cref="RavensPortHost"/> is shared — with the
/// vault unlocked by a token read from stdin instead of the setup page:
///
/// - 1Password: a service-account token. Read-write, exactly as in the app.
/// - Proton Pass: a personal access token. Always read-only: the configuration is read from the
///   vault, edits live in memory, and nothing is written back.
///
/// Runs until Ctrl+C or SIGTERM, then flushes pending changes to the vault the way the app does on
/// exit.
/// </summary>
internal static class ServeCommand
{
    /// <summary>
    /// Held for the life of the process, never released by hand: a named mutex belongs to the
    /// thread that took it, and the async code below finishes on whatever thread it likes. The
    /// operating system drops it when the process ends, which is the only moment it should go.
    /// </summary>
    private static Mutex? _singleInstance;

    public static async Task<int> RunAsync(ServeOptions options, TextWriter output, TextWriter error, CancellationToken ct)
    {
        var readOnly = options.ReadOnly || options.Backend == Backend.ProtonPass;

        if (options.CreateVault is not null && readOnly)
        {
            error.WriteLine("error: --create-vault writes to the vault, so it cannot be combined with a read-only session.");
            return ExitCodes.Usage;
        }

        if (options.CreateVault is not null && options.Backend == Backend.ProtonPass)
        {
            error.WriteLine("error: --create-vault is for 1Password; Proton Pass sessions are read-only.");
            return ExitCodes.Usage;
        }

        _singleInstance = new Mutex(initiallyOwned: true, RavensPortHost.SingleInstanceMutexName, out var isNewInstance);
        if (!isNewInstance)
        {
            error.WriteLine("error: RavensPort is already running on this machine — the desktop app or another `serve`.");
            error.WriteLine("Manage it with the other ravensport commands instead.");
            return ExitCodes.StartFailed;
        }

        var secret = SecretInput.Read(options.Backend == Backend.OnePassword
            ? "1Password service account token: "
            : "Proton Pass personal access token: ");

        if (string.IsNullOrWhiteSpace(secret))
        {
            error.WriteLine(options.Backend == Backend.OnePassword
                ? "error: no token on stdin. Pipe a 1Password service account token in, or run in a terminal to be asked for one."
                : "error: no token on stdin. Pipe a Proton Pass personal access token in (`pass-cli pat create`), or run in a terminal to be asked for one.");
            return ExitCodes.Usage;
        }

        var app = BuildHost(options.Backend);
        AdminServer? admin = null;
        var activityLog = app.Services.GetRequiredService<ActivityLog>();
        var kind = options.Backend == Backend.OnePassword ? VaultBackendKind.OnePassword : VaultBackendKind.ProtonPass;

        try
        {
            if (await ConnectAsync(app.Services, options, kind, readOnly, secret, error, ct) is { } failure) return failure;

            var cache = app.Services.GetRequiredService<ConfigStoreCache>();
            await cache.InitializeAsync(ct);

            RavensPortHost.ApplyMtlsDecision(app.Services);
            var port = app.UseStoredListenUrl();
            app.MapRavensPortPipeline();

            try
            {
                await app.StartAsync(ct);
            }
            catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException)
            {
                activityLog.LogError("Could not start the proxy", ex);
                error.WriteLine($"error: could not listen on 127.0.0.1:{port} — {ex.Message}");
                error.WriteLine("Change the port with the desktop app's Settings tab, or stop whatever else is using it.");
                return ExitCodes.StartFailed;
            }

            admin = await AdminServer.StartAsync(app.Services, readOnly
                ? $"ravensport serve ({VaultLockGuidance.DisplayName(kind)}, read-only)"
                : $"ravensport serve ({VaultLockGuidance.DisplayName(kind)})");

            var scheme = app.Services.GetRequiredService<KestrelMtlsState>().Scheme;
            output.WriteLine($"RavensPort is serving on {scheme}://127.0.0.1:{port}");
            output.WriteLine($"  vault:    {VaultLockGuidance.DisplayName(kind)} — {app.Services.GetRequiredService<VaultGateService>().Selected.VaultName}");
            output.WriteLine($"  store:    {Describe(cache)}");
            output.WriteLine($"  manage:   ravensport status   (admin socket {admin.SocketPath})");
            output.WriteLine($"  log:      {activityLog.CurrentLogPath}");
            if (readOnly)
            {
                output.WriteLine("  READ-ONLY: changes are kept in memory and discarded when this process exits.");
            }

            output.WriteLine("Press Ctrl+C to stop.");

            activityLog.Log(readOnly
                ? $"STARTUP headless, read-only from {VaultLockGuidance.DisplayName(kind)} on port {port}"
                : $"STARTUP headless from {VaultLockGuidance.DisplayName(kind)} on port {port}");

            if (cache.Current.McpSources.Any(s => s.Enabled)) _ = DiscoverSourcesAsync(app.Services);

            await app.WaitForShutdownAsync(ct);

            if (readOnly && cache.HasPendingChanges)
            {
                activityLog.Log("SHUTDOWN read-only session — changes held in memory were discarded");
                output.WriteLine("Read-only session: changes made while running were discarded.");
            }
            else if (cache.HasPendingChanges)
            {
                error.WriteLine("warning: some changes could not be saved to the vault before exit.");
            }

            return ExitCodes.Ok;
        }
        finally
        {
            if (admin is not null) await admin.DisposeAsync();
            if (app.Services.GetService<ProtonPassPatSession>() is { } pat) await pat.EndAsync();
            await app.DisposeAsync();
        }
    }

    private static WebApplication BuildHost(Backend backend)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });

        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(o =>
        {
            o.SingleLine = true;
            o.TimestampFormat = "HH:mm:ss ";
        });
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        builder.ConfigureRavensPortKestrel();
        builder.Services.AddRavensPort();

        // A device code is printed for the person at the terminal; there is no browser here to
        // open, and on a desktop machine opening one in the server's session would be a surprise.
        builder.Services.Replace(ServiceDescriptor.Singleton(sp =>
            new DeviceCodeService(sp.GetRequiredService<ActivityLog>(), DeviceCodeService.DoNotOpen)));

        if (backend == Backend.ProtonPass)
        {
            // A session of its own, per process and thrown away on exit — never the desktop app's.
            builder.Services.Replace(ServiceDescriptor.Singleton(sp =>
                new ProtonPassSession(sp.GetRequiredService<ActivityLog>(), ProtonPassPatSession.DefaultDirectory())));
            builder.Services.AddSingleton(sp => new ProtonPassPatSession(
                sp.GetRequiredService<ProtonPassSession>(),
                sp.GetRequiredService<ICliRunner>(),
                sp.GetRequiredService<ActivityLog>()));
        }

        // Registered after AddRavensPort, so it is stopped before the sync queue: hosted services
        // stop in reverse order, and the flush needs the queue still alive.
        builder.Services.AddHostedService<FlushOnShutdown>();
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(35));

        return builder.Build();
    }

    /// <summary>Unlocks and connects, or prints why not and returns the exit code.</summary>
    private static async Task<int?> ConnectAsync(
        IServiceProvider services, ServeOptions options, VaultBackendKind kind, bool readOnly, string secret,
        TextWriter error, CancellationToken ct)
    {
        var gate = services.GetRequiredService<VaultGateService>();

        try
        {
            if (kind == VaultBackendKind.OnePassword)
            {
                services.GetRequiredService<OnePasswordSession>().Unlock(secret);
            }
            else
            {
                var pat = services.GetRequiredService<ProtonPassPatSession>();
                pat.SetToken(secret);
                await pat.LoginAsync(ct);
            }

            var status = await gate.ConnectAsync(kind, readOnly, ct);

            // A vault that is reachable but not yet RavensPort's. Naming or creating one is a
            // write, so only a read-write session may do it, and only when asked to.
            if (!status.IsReady && !readOnly && status.For(kind)?.Availability is VaultAvailability.VaultMissing)
            {
                if (options.CreateVault is { } create)
                {
                    status = await gate.CreateVaultAsync(kind, create, ct);
                }
                else if (options.Vault is { } name)
                {
                    status = await gate.UseExistingVaultAsync(kind, name, ct);
                }
            }

            if (status.IsReady) return null;

            var detail = status.For(kind);
            error.WriteLine($"error: {VaultLockGuidance.DisplayName(kind)} is not ready ({detail?.Availability.ToString() ?? "unknown"}).");
            if (detail?.Detail is { Length: > 0 } why) error.WriteLine($"  {why}");

            if (detail?.Availability is VaultAvailability.VaultMissing)
            {
                error.WriteLine(readOnly
                    ? "  No vault with a RavensPort configuration was found. Read-only sessions never adopt one — set it up from the desktop app or a read-write `serve` first."
                    : "  Name the vault to use with --vault <name>, or create one with --create-vault <name>.");
            }

            return ExitCodes.VaultUnavailable;
        }
        catch (Exception ex) when (ex is VaultCliException or VaultLockedException or VaultAdoptionException or NotSupportedException)
        {
            error.WriteLine($"error: {ex.Message}");
            return ExitCodes.VaultUnavailable;
        }
    }

    private static string Describe(ConfigStoreCache cache)
    {
        var s = cache.Current;
        return $"{s.Credentials.Count} credential(s), {s.Routes.Count} route(s), {s.McpFunnels.Count} funnel(s), {s.McpApiBridges.Count} bridge(s)";
    }

    /// <summary>
    /// Finds out what each MCP source offers without being asked, as the desktop app does at
    /// start. After Start(): a source reached over a route needs the proxy listening.
    /// </summary>
    private static async Task DiscoverSourcesAsync(IServiceProvider services)
    {
        try
        {
            var pool = services.GetRequiredService<McpSourceConnectionPool>();
            var catalogs = services.GetRequiredService<McpCatalogCache>();
            var sources = services.GetRequiredService<ConfigStoreCache>().Current.McpSources.Where(s => s.Enabled).ToList();

            await Task.WhenAll(sources.Select(async source => catalogs.Set(source.Id, await pool.DiscoverAsync(source))));
        }
        catch (Exception ex)
        {
            services.GetService<ActivityLog>()?.LogError("Startup MCP source discovery failed", ex);
        }
    }

    /// <summary>
    /// Writes pending changes before the host stops, as the desktop app does on exit — otherwise
    /// an edit made a moment before Ctrl+C would die with the process. A no-op when read-only.
    /// </summary>
    private sealed class FlushOnShutdown(VaultSyncQueue queue, ActivityLog activityLog) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            activityLog.Log("SHUTDOWN teardown started");

            try
            {
                await queue.FlushAsync(TimeSpan.FromSeconds(20));
            }
            catch (Exception ex)
            {
                activityLog.LogError("Could not flush the vault on shutdown", ex);
            }
        }
    }
}
