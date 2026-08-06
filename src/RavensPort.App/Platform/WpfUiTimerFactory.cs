using System.Windows.Threading;
using RavensPort.App.Services;

namespace RavensPort.App.Platform;

/// <summary>
/// <see cref="IUiTimerFactory"/> over <see cref="DispatcherTimer"/>, whose Tick already arrives on
/// the UI thread — which is the entire reason the view models used it directly before this.
///
/// The dispatcher is passed to the timer explicitly for the same reason it is passed to
/// <see cref="WpfUiDispatcher"/>: the parameterless <c>DispatcherTimer()</c> binds to the calling
/// thread's dispatcher, so a view model that ever got constructed off the UI thread would get a
/// timer that never ticks rather than an error saying so.
/// </summary>
internal sealed class WpfUiTimerFactory(Dispatcher dispatcher) : IUiTimerFactory
{
    public IDisposable StartRepeating(TimeSpan interval, Action onTick)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher) { Interval = interval };
        timer.Tick += (_, _) => onTick();
        timer.Start();

        return new Subscription(timer);
    }

    /// <summary>
    /// DispatcherTimer is not IDisposable — it is kept alive by the dispatcher's timer list while
    /// running and collected once stopped — so stopping it is the whole of the cleanup.
    /// </summary>
    private sealed class Subscription(DispatcherTimer timer) : IDisposable
    {
        public void Dispose() => timer.Stop();
    }
}
