namespace RavensPort.App.Services;

/// <summary>
/// Starts a repeating timer whose callback runs on the UI thread.
///
/// Deliberately not a general-purpose timer type with Start/Stop/Interval. Both callers want the
/// same thing — begin ticking at construction, keep ticking for the life of the view model, touch
/// bound properties on every tick — so the interface offers exactly that and nothing to get wrong.
/// A plain <see cref="System.Threading.Timer"/> would not do: its callback arrives on a thread-pool
/// thread, which is the one place these ticks must not run.
/// </summary>
public interface IUiTimerFactory
{
    /// <summary>
    /// Begins ticking immediately. Disposing the returned handle stops it; nothing in the app
    /// currently does, because both timers live exactly as long as the process.
    /// </summary>
    IDisposable StartRepeating(TimeSpan interval, Action onTick);
}
