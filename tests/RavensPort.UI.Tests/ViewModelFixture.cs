using Microsoft.Extensions.DependencyInjection;
using RavensPort.Core;
using RavensPort.Core.Proxy;
using RavensPort.Core.Storage;
using RavensPort.Core.Vault;
using RavensPort.UI.Services;
using RavensPort.UI.ViewModels;

namespace RavensPort.UI.Tests;

/// <summary>
/// A view model and everything behind it, with no Avalonia anywhere.
///
/// The other tests in this project drive real views, which is the right shape for asking whether a
/// button is wired to the thing it claims. It is the wrong shape for asking what a view model does
/// with an answer it did not like — that needs no window, and paying for one costs a headless
/// session, a dispatcher and a layout pass per case.
///
/// So this is the same container the app builds, minus the desktop: <c>AddRavensPort</c> exactly as
/// the product registers it, and the six seams the view models reach the desktop through replaced
/// by the inline fakes below. Nothing is stubbed underneath that — the vault is the real
/// InMemoryVault a single-use session gets, and a provider asked for a password manager that is not
/// installed fails the way it fails on a machine without one, which is the state most of these
/// tests are about.
/// </summary>
internal sealed class ViewModelFixture : IDisposable
{
    private readonly ServiceProvider _services;

    public ViewModelFixture()
    {
        var services = new ServiceCollection();

        services.AddRavensPort();

        services.AddSingleton<IUiDispatcher, InlineDispatcher>();
        services.AddSingleton<IUiTimerFactory, NoTimers>();
        services.AddSingleton<IClipboardService, RecordingClipboard>();
        services.AddSingleton<IPlatformLauncher, RecordingLauncher>();
        services.AddSingleton<IHelloConsentPrompt, ConsentingPrompt>();
        services.AddSingleton<IFileSavePicker, RecordingSavePicker>();
        services.AddSingleton<IFileOpenPicker, RecordingOpenPicker>();

        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<VaultStatusViewModel>();
        services.AddSingleton<SetupViewModel>();
        services.AddSingleton<CredentialsViewModel>();
        services.AddSingleton<RoutesViewModel>();
        services.AddSingleton<McpFunnelViewModel>();
        services.AddSingleton<ApiBridgeViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<AppTabs>();

        _services = services.BuildServiceProvider();
    }

    public T Get<T>() where T : notnull => _services.GetRequiredService<T>();

    public ConfigStoreCache Store => Get<ConfigStoreCache>();

    public VaultGateService Gate => Get<VaultGateService>();

    /// <summary>
    /// Opens the single-use session these tests run in, and loads the store behind it.
    ///
    /// The gate call rather than the button, because there is no button here — what this fixture is
    /// for is everything that happens after a session exists.
    /// </summary>
    public async Task StartSingleUseAsync()
    {
        Gate.UseSingleUse();
        await Store.InitializeAsync();
    }

    /// <summary>
    /// Waits for something a command started on another thread, without a dispatcher to pump.
    ///
    /// Short by design: everything here is in-process, so a wait that needs seconds means the thing
    /// being waited for is not going to happen and the message should say so.
    /// </summary>
    public static async Task UntilAsync(Func<bool> condition, Func<string> because)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);

        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(20);
        }

        throw new TimeoutException($"Waited 10s for {because()} and it never came true.");
    }

    public void Dispose() => _services.Dispose();

    /// <summary>Runs UI work where it was posted. There is no other thread to marshal to.</summary>
    private sealed class InlineDispatcher : IUiDispatcher
    {
        public void Post(Action action) => action();
    }

    /// <summary>
    /// Starts nothing. The repeating timers refresh what is on screen, and there is no screen —
    /// leaving them running would have every test racing a tick it never asked for.
    /// </summary>
    private sealed class NoTimers : IUiTimerFactory
    {
        public IDisposable StartRepeating(TimeSpan interval, Action onTick) => new Nothing();

        private sealed class Nothing : IDisposable
        {
            public void Dispose() { }
        }
    }

    private sealed class RecordingClipboard : IClipboardService
    {
        public List<string> Copied { get; } = [];

        public Task SetTextAsync(string text)
        {
            Copied.Add(text);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Says yes, and runs the work the prompt was guarding.
    ///
    /// The prompt is a Windows Hello gesture in the product, so the thing worth substituting is the
    /// person: consenting keeps the code under test on the path that follows a yes, which is the
    /// one with something to assert.
    /// </summary>
    private sealed class ConsentingPrompt : IHelloConsentPrompt
    {
        public async Task<bool> RequestUnlockAsync(Func<Task> unlockAsync) => await RunAsync(unlockAsync);

        public async Task<bool> RequestSetupAsync(Func<Task> prepareAsync) => await RunAsync(prepareAsync);

        public async Task<bool> RequestTokenSaveAsync(Func<Task> protectAsync) => await RunAsync(protectAsync);

        public async Task<bool> RequestTokenUnlockAsync(Func<Task> unlockAsync) => await RunAsync(unlockAsync);

        /// <summary>
        /// A refusal from underneath is not a refusal by the person, and the two must not be
        /// confused: the prompt reports whether consent was given, and the work throwing afterwards
        /// is the caller's to handle.
        /// </summary>
        private static async Task<bool> RunAsync(Func<Task> work)
        {
            try
            {
                await work();
            }
            catch
            {
                // Swallowed here for the same reason the real prompt does not surface it: what this
                // returns is "the user agreed", and they did.
            }

            return true;
        }
    }
}
