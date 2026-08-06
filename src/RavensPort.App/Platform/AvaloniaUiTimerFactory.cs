using Avalonia.Threading;
using RavensPort.UI.Services;

namespace RavensPort.Platform;

/// <summary>
/// <see cref="IUiTimerFactory"/> over Avalonia's dispatcher timer.
///
/// <see cref="DispatcherTimer.Run"/> is already the shape this interface asks for — start ticking,
/// keep going while the callback says so, hand back something disposable to stop it — so there is
/// no wrapper class here the way there is on the WPF side.
/// </summary>
internal sealed class AvaloniaUiTimerFactory : IUiTimerFactory
{
    public IDisposable StartRepeating(TimeSpan interval, Action onTick) =>
        DispatcherTimer.Run(() =>
        {
            onTick();

            // Keep repeating. Returning false here is how Run stops, and neither caller ever wants
            // to: both timers refresh a tab that lives as long as the process.
            return true;
        }, interval);
}
