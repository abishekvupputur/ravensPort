namespace RavensPort.App.Services;

/// <summary>
/// Marshals work onto the UI thread.
///
/// A view model reaches for this because the things it subscribes to — the vault sync pump, the
/// config store's pending-changes notification — raise their events on thread-pool threads, and
/// every UI framework in existence throws when a bound property changes off its own thread. The
/// abstraction exists so the view models can say "back to the UI thread" without naming which
/// framework's dispatcher that is: WPF's <c>Dispatcher</c> and Avalonia's <c>Dispatcher.UIThread</c>
/// are the same idea behind two incompatible types.
/// </summary>
public interface IUiDispatcher
{
    /// <summary>
    /// Queues the action on the UI thread and returns immediately. Fire-and-forget on purpose:
    /// every caller is an event handler on a background thread that has nothing to wait for, and
    /// blocking one of those on the UI thread is how the deadlocks in this app have always started.
    /// </summary>
    void Post(Action action);
}
