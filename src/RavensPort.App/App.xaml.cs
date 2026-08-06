using System.Windows;
using System.Windows.Threading;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;
using RavensPort.Core.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RavensPort.App.Platform;
using RavensPort.UI.Services;
using RavensPort.App.Tray;
using RavensPort.UI.ViewModels;
using RavensPort.App.Views;
using RavensPort.Core.Mcp;

using RavensPort.Core.Proxy;
using RavensPort.Core.Storage;
using RavensPort.Core.Vault;

namespace RavensPort.App;

/// <summary>
/// WPF Application drives the process lifetime; it owns a Generic Host (Kestrel + YARP)
/// started here and stopped on exit. Both the web pipeline and the WPF UI share one DI
/// container. The app is always tray-resident — no window is shown on launch.
/// </summary>
public partial class App : Application
{
    private const string SingleInstanceMutexName = "RavensPort_SingleInstance";

    /// <summary>
    /// How a second launch asks the running instance to come to the front. Named, because the two
    /// processes have nothing else in common — and the whole point is that the second one exits.
    /// </summary>
    private const string ShowWindowEventName = "RavensPort_ShowWindow";

    private static Mutex? _singleInstanceMutex;

    private WebApplication? _webApp;
    private TrayIconManager? _trayIconManager;
    private EventWaitHandle? _showWindowSignal;

    /// <summary>
    /// Kestrel can only be started once. The setup page can raise its ready event more than once —
    /// "Check again" after the gate has already opened, say — and a second Start() would throw.
    /// </summary>
    private bool _proxyStarted;

    /// <summary>The port Kestrel actually bound, so a reconnect can say when a vault disagrees.</summary>
    private int _boundPort;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Only one instance may run. A second one would fight the first over the fixed
        // ports — the proxy port and, more subtly, the fixed OAuth loopback ports, where
        // the loser fails with "conflicts with an existing registration on the machine".
        var mutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var isNewInstance);
        if (!isNewInstance)
        {
            // Not the owner, so it must never be released here — only disposed. The field is
            // left null so OnExit can tell "we own it" from "we're the duplicate".
            mutex.Dispose();

            // Launching an app that is already running should show it, not explain itself. This is
            // what makes the Start menu shortcut a real way back into a window the user closed —
            // the previous message box told them to go hunting in the tray instead.
            if (!TryShowRunningInstance())
            {
                MessageBox.Show(
                    "RavensPort is already running — look for the padlock icon in the system tray.",
                    "RavensPort", MessageBoxButton.OK, MessageBoxImage.Information);
            }

            Shutdown();
            return;
        }

        _singleInstanceMutex = mutex;

        // Safety net: this is an always-on tray app, so an unhandled exception anywhere must
        // not terminate the process (a Nextcloud login error used to kill it outright). Errors
        // are surfaced to the user and swallowed so the proxy and tray icon stay alive.
        DispatcherUnhandledException += (_, args) =>
        {
            ReportError("Unexpected error", args.Exception);
            args.Handled = true;
        };
        // Logged, deliberately not shown. An unobserved exception is by definition one nothing was
        // waiting on, so it is not news: whatever started that work has already run its own error
        // path and told the user in the place that makes sense. The MCP client is the reliable
        // source of these — a source whose proxy key has expired fails its handshake, which the
        // pool catches, logs and shows on the row, and the library's own message-pump task is then
        // collected still holding the same 403. The dialog that produced arrived seconds later,
        // detached from anything the user had done, and said nothing the grid had not.
        //
        // The finalizer thread raises this, so a MessageBox here also blocks GC until it is
        // dismissed. Errors that genuinely need interrupting come through
        // DispatcherUnhandledException above.
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            if (args.Exception is not null && args.Exception.ToString().Contains("The transport was closed."))
            {
                args.SetObserved();
                return;
            }
            LogError("Unobserved background error", args.Exception!);
            args.SetObserved();
        };

        // Everything below can fail in ways that used to leave a live process with no tray
        // icon, no window, and no message: a listen port already in use throws out of
        // app.Start(), and DispatcherUnhandledException then marked it handled, so the app
        // "kept running" having never finished starting — while still holding the single-
        // instance mutex, so no later launch could get in either. Startup failure now means
        // an explanation and a real shutdown.
        try
        {
            StartHost();
        }
        catch (Exception ex)
        {
            ReportStartupFailure(ex);
            Shutdown();
        }
    }

    private void StartHost()
    {
        var builder = WebApplication.CreateBuilder();

        // Deliberately no UseUrls here. The listen port lives in the vault along with everything
        // else, and the vault cannot be read until the password manager is unlocked — which may
        // involve a biometric prompt and cannot be made to happen before the host is built.
        // WebApplication.Urls stays writable right up until Start(), so the port is set in
        // StartProxyAsync once the store has actually been loaded.
        builder.WebHost.ConfigureKestrel(options =>
        {
            // Long-lived MCP SSE/streamable-HTTP sessions shouldn't be dropped by Kestrel.
            options.Limits.KeepAliveTimeout = TimeSpan.FromHours(2);

            var kestrelMtls = options.ApplicationServices.GetRequiredService<RavensPort.Core.Mcp.KestrelMtlsState>();

            // Runs when the https endpoint is bound, which is inside Start() — after
            // StartProxyAsync has read the vault and settled the state below. Reading it out here
            // instead would settle nothing: at this point the vault has not been unlocked.
            options.ConfigureHttpsDefaults(https =>
            {
                if (kestrelMtls.Certificate is not { } certificate) return;

                https.ServerCertificate = certificate;
                https.ClientCertificateMode = Microsoft.AspNetCore.Server.Kestrel.Https.ClientCertificateMode.RequireCertificate;

                // The certificate is self-signed and shared by both ends, so there is no chain to
                // validate and the default handling — which rejects on any SslPolicyError — would
                // refuse every caller including this app's own funnel. The thumbprint is the check.
                //
                // Plus the validity window, put back by hand: turning off chain validation turns
                // off the platform's expiry check with it, and without this line a certificate
                // that expired months ago would still open the proxy. That is the whole of what
                // retires one — there is no CA here, so no CRL and no OCSP.
                https.ClientCertificateValidation = (clientCert, _, _) =>
                    string.Equals(clientCert.Thumbprint, certificate.Thumbprint, StringComparison.OrdinalIgnoreCase)
                    && MtlsCertificateFactory.IsWithinValidity(clientCert, DateTimeOffset.UtcNow);
            });
        });

        builder.Services.AddRavensPort();

        // The view models talk to the desktop only through these. Registered before them so the
        // dependency reads in the direction it actually runs: WPF here, an Avalonia set later,
        // and nothing above this line naming either.
        //
        // The two that marshal work take the dispatcher as an instance captured right here, on the
        // UI thread. Reading it inside a container-built instance instead would make correctness
        // depend on which thread happened to resolve it first — and get that wrong silently, since
        // Dispatcher.CurrentDispatcher answers on any thread by making a new one nobody pumps.
        var uiDispatcher = Dispatcher.CurrentDispatcher;
        builder.Services.AddSingleton<IUiDispatcher>(new WpfUiDispatcher(uiDispatcher));
        builder.Services.AddSingleton<IUiTimerFactory>(new WpfUiTimerFactory(uiDispatcher));
        builder.Services.AddSingleton<IClipboardService, WpfClipboardService>();
        builder.Services.AddSingleton<IPlatformLauncher, WindowsPlatformLauncher>();
        builder.Services.AddSingleton<IHelloConsentPrompt, WpfHelloConsentPrompt>();

        builder.Services.AddSingleton<MainWindowViewModel>();
        builder.Services.AddSingleton<VaultStatusViewModel>();
        builder.Services.AddSingleton<SetupViewModel>();
        builder.Services.AddSingleton<CredentialsViewModel>();
        builder.Services.AddSingleton<RoutesViewModel>();
        builder.Services.AddSingleton<McpFunnelViewModel>();
        builder.Services.AddSingleton<SettingsViewModel>();
        builder.Services.AddSingleton<MainWindow>();
        builder.Services.AddSingleton<TrayIconManager>();

        // Build the host on a thread-pool thread (via Task.Run) rather than inline on the WPF
        // Dispatcher thread. Anything in this call graph that awaits async I/O would, with the
        // Dispatcher's SynchronizationContext still ambient, try to post its continuation back
        // onto this very thread — which is blocked waiting for it. Task.Run runs the delegate
        // with no SynchronizationContext, so nothing in it can capture the Dispatcher.
        _webApp = Task.Run(builder.Build).GetAwaiter().GetResult();

        var mainWindow = _webApp.Services.GetRequiredService<MainWindow>();
        var settingsViewModel = _webApp.Services.GetRequiredService<SettingsViewModel>();
        var mainWindowViewModel = _webApp.Services.GetRequiredService<MainWindowViewModel>();

        _trayIconManager = _webApp.Services.GetRequiredService<TrayIconManager>();
        _trayIconManager.Initialize(
            mainWindow,
            confirmExit: ConfirmExitWithUnsavedChanges);
        _trayIconManager.SetState(TrayState.Starting);
        mainWindow.HiddenWhileGated += () => _trayIconManager.NotifyIdleWhileGated();

        // The setup page drives everything from here: it decides when there is a usable vault and
        // calls back to start the proxy. Wired before the first check so a gate that opens
        // immediately still gets a listener.
        var setupViewModel = _webApp.Services.GetRequiredService<SetupViewModel>();
        setupViewModel.ReadyToStart += StartProxyAsync;

        // "Sync now" with nothing pending checks the vault instead of doing nothing. The reload
        // itself lives on the vault-status view model, which depends on this one, so it arrives as
        // a hook rather than a reference.
        settingsViewModel.ReloadFromVaultRequested = () =>
            _webApp.Services.GetRequiredService<VaultStatusViewModel>().ReloadFromVaultAsync();

        // "Re-initialise from vault": empty everything held in memory and load it again, which is
        // the same work a reconnect does — the only difference is that the password manager was
        // never disconnected.
        settingsViewModel.ReinitialiseRequested = async () =>
        {
            var configStoreCache = _webApp.Services.GetRequiredService<ConfigStoreCache>();

            await configStoreCache.ResetAsync();
            await _webApp.Services.GetRequiredService<VaultStatusViewModel>().ReconnectAsync();

            _webApp.Services.GetRequiredService<ProxyConfigChangeNotifier>().Rebuild();
        };

        // Dropping a record can take a route or funnel with it, so the tabs showing them have to
        // be rebuilt — their rows hold references to records that are no longer in the store.
        settingsViewModel.RecordsDropped += () =>
        {
            _webApp.Services.GetRequiredService<CredentialsViewModel>().Reload();
            _webApp.Services.GetRequiredService<RoutesViewModel>().Reload();
            _webApp.Services.GetRequiredService<McpFunnelViewModel>().Reload();
        };

        // Every tab rebuilt from the emptied store, so a disconnect leaves no row belonging to the
        // vault just left. VaultStatusViewModel owns the four tab view models, so it is what knows
        // how to rebuild them.
        settingsViewModel.UseTabRebuilder(
            () => _webApp.Services.GetRequiredService<VaultStatusViewModel>().ReloadTabs());

        // Disconnecting from the Settings tab puts the whole window back to the setup page: with no
        // password manager there is no configuration, so the tabs would be four empty grids whose
        // every control fails — the same reason the app starts there.
        settingsViewModel.Disconnected += () =>
        {
            mainWindowViewModel.EnterSetupMode();
            _trayIconManager?.SetState(TrayState.SetupRequired);

            _ = setupViewModel.CheckAsync();
        };

        ListenForSecondLaunch(mainWindow);

        // Shown, not left hidden. The app used to start straight to the tray with no window at all,
        // which meant a launch produced no visible response — and once the tray menu's Exit had been
        // used there was no way back in short of finding the exe on disk. That is also what the
        // Microsoft Store rejected under 10.1.2.10. Autostart no longer exists, so every launch is
        // now something a person deliberately did, and answering it with a window is simply correct.
        ShowWindow(mainWindow);

        // Fire and forget on the Dispatcher rather than blocking it. The original deadlock hazard
        // was blocking this thread while a continuation tried to post back onto it; this never
        // blocks, and every piece of work below is still wrapped in Task.Run so nothing captures
        // the Dispatcher's SynchronizationContext.
        _ = CheckForManagersAsync(setupViewModel);
    }

    /// <summary>
    /// Watches for a second launch and brings this instance's window up when one happens.
    ///
    /// The waiting thread is a background one and is never joined: it is parked in WaitOne for the
    /// life of the process, and shutdown is not going to wait politely for a wait that only ends
    /// when someone launches the app again.
    /// </summary>
    private void ListenForSecondLaunch(MainWindow mainWindow)
    {
        try
        {
            _showWindowSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowWindowEventName);
        }
        catch (Exception ex)
        {
            // Losing this costs the second-launch-shows-the-window nicety and nothing else, so it
            // must not stop the app starting. The duplicate falls back to its message box.
            LogError("Could not listen for a second launch", ex);
            return;
        }

        var listener = new Thread(() =>
        {
            try
            {
                while (_showWindowSignal.WaitOne())
                {
                    mainWindow.Dispatcher.BeginInvoke(() => ShowWindow(mainWindow));
                }
            }
            catch (ObjectDisposedException)
            {
                // Shutdown closed the handle out from under the wait. Expected; nothing to do.
            }
        })
        {
            IsBackground = true,
            Name = "RavensPort second-launch listener",
        };

        listener.Start();
    }

    /// <summary>
    /// Asks an already-running instance to show itself. False when there is nothing listening —
    /// an instance older than this change, or one whose listener failed to start.
    /// </summary>
    private static bool TryShowRunningInstance()
    {
        try
        {
            if (!EventWaitHandle.TryOpenExisting(ShowWindowEventName, out var handle)) return false;

            using (handle) return handle.Set();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        {
            return false;
        }
    }

    private static void ShowWindow(Window window)
    {
        window.Show();
        window.WindowState = WindowState.Normal;
        window.Activate();
    }

    /// <summary>
    /// Finds out what is installed, and nothing more.
    ///
    /// This used to raise a Windows Hello prompt on the way past and then probe both managers in
    /// full, which meant launching RavensPort produced a queue of authentication prompts — a
    /// gesture, then a desktop-app approval per 1Password command — before the user had said which
    /// manager they wanted or whether they wanted one at all. Both now belong to the Connect button
    /// on the setup page, so a launch costs nothing and asks nobody for anything.
    /// </summary>
    private async Task CheckForManagersAsync(SetupViewModel setupViewModel)
    {
        try
        {
            await setupViewModel.CheckAsync();
        }
        catch (Exception ex)
        {
            // Never allowed to stop the app reaching its setup page — that page is the only thing
            // that can explain what went wrong, and offer the retry or the discard.
            _webApp?.Services.GetService<ActivityLog>()?.LogError("Startup password-manager check failed", ex);
        }
    }

    /// <summary>
    /// Loads the store, then binds and starts Kestrel. Separate from host <em>build</em> because
    /// the listen port lives in the vault, so it is not knowable until a password manager has been
    /// unlocked — which may involve a prompt the user takes a minute to notice.
    /// </summary>
    private async Task StartProxyAsync()
    {
        if (_proxyStarted)
        {
            await ReconnectAsync();
            return;
        }

        var configStoreCache = _webApp!.Services.GetRequiredService<ConfigStoreCache>();
        var mainWindowViewModel = _webApp.Services.GetRequiredService<MainWindowViewModel>();
        var setupViewModel = _webApp.Services.GetRequiredService<SetupViewModel>();

        var port = 0;

        try
        {
            // Task.Run for the same reason as the build above: this awaits vault I/O and then runs
            // hosted-service startup, and neither may capture the Dispatcher.
            await Task.Run(async () =>
            {
                await configStoreCache.InitializeAsync();

                port = configStoreCache.Current.Settings.ListenPort;

                // Settled before the URL is chosen and before Start() binds it, because the state
                // decides both: the scheme Kestrel listens on, and the scheme the MCP funnel dials
                // its own routes on. Anything that read the setting a second time could disagree.
                var kestrelMtls = _webApp.Services.GetRequiredService<RavensPort.Core.Mcp.KestrelMtlsState>();
                if (configStoreCache.Current.Settings.MtlsEnabled)
                {
                    // A store can say "mTLS on" and hold no certificate — earlier builds let the
                    // checkbox be ticked without generating one. Neither answer to that is free:
                    // binding plain HTTP anyway would tell the user their proxy is certificate-
                    // protected while anything on the machine can call it, and refusing to start
                    // strands them on the setup page with no way back to the checkbox. So the
                    // certificate is minted, and the Settings tab's export is where they get it.
                    if (string.IsNullOrWhiteSpace(configStoreCache.Current.Settings.MtlsClientCertificatePfx))
                    {
                        await configStoreCache.MutateAsync(store =>
                            store.Settings.MtlsClientCertificatePfx = MtlsCertificateFactory.GenerateClientCertificatePfx());

                        _webApp.Services.GetService<ActivityLog>()?.Log(
                            "mTLS was enabled with no certificate stored; a new one was generated. "
                            + "Export it from the Settings tab and install it on every client that calls this proxy.");
                    }

                    kestrelMtls.Enable(
                        configStoreCache.Current.Settings.MtlsClientCertificatePfx,
                        configStoreCache.Current.Settings.MtlsClientCertificatePassword);

                    // The other half of the pin, recorded at the moment it is decided. When the
                    // funnel later refuses this listener it logs what was presented; without this
                    // line there is nothing to compare that against, and "the remote certificate
                    // was rejected" is equally consistent with a stale certificate, a certificate
                    // regenerated since the last start, and Kestrel never having received one.
                    _webApp.Services.GetService<ActivityLog>()?.Log(
                        $"mTLS enabled — serving certificate …{kestrelMtls.Certificate!.Thumbprint[^8..]} "
                        + "and requiring the same one from every caller.");

                    // Said at startup as well as on the Settings tab, because this is the state in
                    // which every request fails at the handshake: no status code reaches the
                    // caller, so without a line here the only evidence is a connection that closes.
                    if (kestrelMtls.IsExpired)
                    {
                        _webApp.Services.GetService<ActivityLog>()?.Log(
                            $"STARTUP the mTLS certificate expired on {kestrelMtls.ExpiresUtc:yyyy-MM-dd} and is no "
                            + "longer accepted. Every caller will be refused at the TLS handshake until a new "
                            + "certificate is generated on the Settings tab, exported, installed on every client, "
                            + "and RavensPort restarted.");
                    }
                }

                _webApp.Urls.Clear();
                _webApp.Urls.Add($"{kestrelMtls.Scheme}://127.0.0.1:{port}");

                // Must sit ahead of MapReverseProxy: it rejects callers that cannot present the
                // endpoint's proxy key, and blocks DNS-rebinding and browser-originated requests.
                // Without it, any process on this machine can spend the user's OAuth grant.
                _webApp.UseLocalAccessGuard();

                // After the guard, so funnel callers must present a proxy key like anyone else, and
                // before MapReverseProxy so /mcp is unambiguously the funnel's — routes are
                // forbidden from claiming that prefix.
                _webApp.UseMcpFunnelGate();
                _webApp.MapMcpFunnel();

                _webApp.MapReverseProxy();
                _webApp.Start();
            });
        }
        catch (Exception ex)
        {
            // A port clash used to be a dead end: the app shut down telling the user to edit a
            // file that no longer exists. The port lives in the vault now, so it can be changed
            // from the setup page while the proxy is down — which is the only moment it matters.
            _webApp.Services.GetService<ActivityLog>()?.LogError("Could not start the proxy", ex);
            setupViewModel.ReportPortConflict(port, ex.Message);
            _trayIconManager?.SetState(TrayState.SetupRequired);
            return;
        }

        _proxyStarted = true;
        _boundPort = port;

        // The tabs were built before this — they have to be, the window exists while the vault is
        // still locked — so every one of them was rendered from an empty store. Without this the
        // Credentials tab opens saying there are none, and stays that way until something else
        // reloads it. The other tabs hid the same bug: switching to them reloads them on the way in,
        // and Credentials is the tab already on screen.
        _webApp.Services.GetRequiredService<VaultStatusViewModel>().ReloadTabs();

        mainWindowViewModel.EnterNormalMode();
        _trayIconManager?.SetState(TrayState.Running);

        // Discover what the MCP sources offer without being asked. Every source otherwise opens as
        // "not checked yet — press Refresh", so the first thing anyone does on this tab is press a
        // button and wait — for information the app could have had ready before they arrived.
        //
        // After Start(), not before: a source of kind ProxyRoute is reached over this very proxy,
        // so nothing is discoverable until Kestrel is listening. Fire and forget, because the
        // window is already usable and this is one network round trip per source.
        if (configStoreCache.Current.McpSources.Any(source => source.Enabled))
        {
            _ = DiscoverMcpSourcesAsync();
        }
    }

    /// <summary>
    /// The startup discovery pass, in its own method so the failure has somewhere to go. Discovery
    /// reports an unreachable source by returning a failed catalog rather than throwing, so this
    /// should stay quiet — but a bare "_ =" on the call would drop a real fault silently, and a
    /// startup step that disappears without trace is the kind of thing that gets diagnosed twice.
    /// </summary>
    private async Task DiscoverMcpSourcesAsync()
    {
        try
        {
            await _webApp!.Services.GetRequiredService<McpFunnelViewModel>()
                .RefreshAllSourcesCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            _webApp!.Services.GetService<ActivityLog>()?.LogError("Startup MCP source discovery failed", ex);
        }
    }

    /// <summary>
    /// Loads the store again after the user disconnected a password manager and connected one
    /// back. Kestrel is already bound and cannot be rebound in this process, so a listen port that
    /// differs in the newly connected vault takes effect at the next start — everything else, from
    /// routes to proxy keys, comes back immediately.
    /// </summary>
    private async Task ReconnectAsync()
    {
        var vaultStatusViewModel = _webApp!.Services.GetRequiredService<VaultStatusViewModel>();
        var mainWindowViewModel = _webApp.Services.GetRequiredService<MainWindowViewModel>();
        var setupViewModel = _webApp.Services.GetRequiredService<SetupViewModel>();
        var configStoreCache = _webApp.Services.GetRequiredService<ConfigStoreCache>();

        try
        {
            await vaultStatusViewModel.ReconnectAsync();
        }
        catch (Exception ex)
        {
            // Staying on the setup page is the right answer: the store did not load, so the tabs
            // would show a configuration that is not there.
            _webApp.Services.GetService<ActivityLog>()?.LogError("Could not reload the vault", ex);
            setupViewModel.ReportReconnectFailure(ex.Message);
            return;
        }

        _webApp.Services.GetRequiredService<ProxyConfigChangeNotifier>().Rebuild();

        if (configStoreCache.Current.Settings.ListenPort != _boundPort)
        {
            _webApp.Services.GetService<ActivityLog>()?.Log(
                $"STARTUP this vault asks for port {configStoreCache.Current.Settings.ListenPort}, but the proxy is "
                + $"already listening on {_boundPort} — restart RavensPort to move it");
        }



        mainWindowViewModel.EnterNormalMode();
        _trayIconManager?.SetState(TrayState.Running);
    }

    /// <summary>
    /// Asks before quitting with changes that are only in memory. Returns true when it is safe to
    /// proceed.
    ///
    /// This is the one place the deferred-sync design can actually cost the user something. Edits
    /// and token refreshes go ahead while the password manager is locked, and nothing is written
    /// to disk in the meantime, so exiting is the moment they stop existing — and a credential
    /// whose token rotated in that window needs reconnecting. Worth one dialog.
    ///
    /// Called from the tray's Exit rather than from OnExit, because OnExit runs after shutdown is
    /// already committed and there is no way back from it.
    /// </summary>
    private bool ConfirmExitWithUnsavedChanges()
    {
        var configStoreCache = _webApp?.Services.GetService<ConfigStoreCache>();
        if (configStoreCache is null) return true;

        // Single use has no pending changes to warn about — every save succeeds, into memory — and
        // that is exactly what makes the warning necessary. There is no vault behind it, so exiting
        // is not "losing the last few edits", it is losing all of it, and nothing else in the app
        // gets a chance to say so after this point.
        if (_webApp!.Services.GetService<VaultGateService>() is { IsSingleUse: true }
            && HasAnythingWorthKeeping(configStoreCache))
        {
            return MessageBox.Show(
                "RavensPort is running in single use, so this configuration is held in memory only."
                + $"{Environment.NewLine}{Environment.NewLine}"
                + "Exiting discards every credential, route, funnel and key you set up in this "
                + "session. None of it can be recovered."
                + $"{Environment.NewLine}{Environment.NewLine}"
                + "To keep it, choose Cancel and connect a password manager from the Settings tab.",
                "RavensPort — single use",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning,
                MessageBoxResult.Cancel) == MessageBoxResult.OK;
        }

        if (!configStoreCache.HasPendingChanges) return true;

        // One last attempt first. The manager is often unlocked by now — the user may have
        // unlocked it for something else entirely — and warning about losing changes that could
        // simply have been written would be a poor way to find that out.
        if (_webApp!.Services.GetService<VaultSyncQueue>() is { } syncQueue)
        {
            try
            {
                Task.Run(() => syncQueue.FlushAsync(TimeSpan.FromSeconds(15))).Wait(TimeSpan.FromSeconds(20));
            }
            catch
            {
                // The confirmation below is what actually protects the user.
            }

            if (!configStoreCache.HasPendingChanges) return true;
        }

        var manager = VaultLockGuidance.DisplayName(
            _webApp.Services.GetService<VaultGateService>()?.Status.Selected ?? VaultBackendKind.None);

        var answer = MessageBox.Show(
            $"Some changes have not been saved to {manager} yet."
            + $"{Environment.NewLine}{Environment.NewLine}"
            + "They are only in memory, so exiting now discards them — and any credential whose "
            + "token was refreshed while it was locked will need to be reconnected."
            + $"{Environment.NewLine}{Environment.NewLine}"
            + $"Unlock {manager} and choose Cancel to save them first.",
            "RavensPort — unsaved changes",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel);

        return answer == MessageBoxResult.OK;
    }

    /// <summary>
    /// Whether a single-use session has anything in it worth interrupting an exit over. Somebody
    /// who pressed "Start in single use" to look around and then quit should not be asked to
    /// confirm losing nothing.
    /// </summary>
    private static bool HasAnythingWorthKeeping(ConfigStoreCache configStoreCache)
    {
        var store = configStoreCache.Current;

        return store.Credentials.Count > 0
               || store.Routes.Count > 0
               || store.Upstreams.Count > 0
               || store.McpSources.Count > 0
               || store.McpFunnels.Count > 0;
    }

    private static void ReportStartupFailure(Exception ex)
    {
        // The host may be half-built, so don't count on resolving ActivityLog from it.
        try
        {
            new ActivityLog().LogError("Startup failed", ex);
        }
        catch
        {
            // ignored
        }

        MessageBox.Show(
            $"RavensPort could not start.{Environment.NewLine}{Environment.NewLine}{ex.Message}"
            + $"{Environment.NewLine}{Environment.NewLine}"
            + "If the listen port is already in use, close the other program using it — the port "
            + "is stored in your password manager and can be changed from the Settings tab.",
            "RavensPort", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private void ReportError(string title, Exception ex)
    {
        LogError(title, ex);

        MessageBox.Show($"{title}:{Environment.NewLine}{Environment.NewLine}{ex.Message}",
            "RavensPort", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    /// <summary>
    /// Records an error without interrupting anyone. For failures that have already been handled
    /// and reported wherever they belong — see the unobserved-task handler in
    /// <see cref="OnStartup"/> — where a dialog would be a second telling of the same thing.
    /// </summary>
    private void LogError(string title, Exception ex)
    {
        try
        {
            // Route through ActivityLog so it lands in the same folder the Settings tab
            // exposes, rather than a stray file in %TEMP%.
            _webApp?.Services.GetService<ActivityLog>()?.LogError(title, ex);
        }
        catch
        {
            // Logging must never itself take the app down.
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIconManager?.Dispose();
        _showWindowSignal?.Dispose();

        // Same reasoning as the Task.Run in OnStartup: StopAsync/DisposeAsync run on the
        // WPF Dispatcher thread here. If anything in Kestrel/hosted-service shutdown awaits
        // without ConfigureAwait(false) while the Dispatcher's SynchronizationContext is
        // still ambient, its continuation tries to post back onto this exact thread — which
        // is blocked waiting for it. That deadlock left RavensPort.exe running after "Exit"
        // until force-killed. Task.Run drops the Dispatcher context for this whole shutdown
        // sequence, so nothing in it can capture it.
        if (_webApp is not null)
        {
            try
            {
                // Bounded overall wait too: StopAsync(5s) makes a best effort to respect that
                // timeout internally, but nothing here should be able to hang OnExit forever —
                // Wait() with its own timeout is the actual backstop.
                Task.Run(async () =>
                {
                    // Belt and braces. The tray's Exit already flushed and asked, but this method
                    // also runs on paths that never went through it — a Windows shutdown, or the
                    // startup-failure path — and it ends in Environment.Exit, which would
                    // otherwise kill the process mid-write.
                    if (_webApp.Services.GetService<VaultSyncQueue>() is { } syncQueue)
                    {
                        await syncQueue.FlushAsync(TimeSpan.FromSeconds(20));
                    }

                    await _webApp.StopAsync(TimeSpan.FromSeconds(5));
                    await _webApp.DisposeAsync();
                }).Wait(TimeSpan.FromSeconds(35));
            }
            catch
            {
                // Whatever went wrong, exiting is still non-negotiable — fall through to
                // ReleaseMutex/Environment.Exit below rather than leaving the process stuck.
            }
        }

        // Non-null only in the instance that actually owns the mutex; the duplicate disposed
        // its handle at startup and left this null, so it never reaches ReleaseMutex.
        if (_singleInstanceMutex is not null)
        {
            try
            {
                _singleInstanceMutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Not the owner (shouldn't happen given the guard above) — nothing to release.
            }

            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
        }

        base.OnExit(e);

        // Belt-and-suspenders: WPF exiting is supposed to let Main() return and the process
        // die naturally, but that only happens if every thread in the process is a background
        // thread. A stray foreground thread anywhere in the dependency graph — YARP, a Google
        // auth library, anything — would otherwise leave RavensPort.exe running invisibly
        // after "Exit", exactly what was reported. This makes shutdown unconditional.
        Environment.Exit(0);
    }
}
