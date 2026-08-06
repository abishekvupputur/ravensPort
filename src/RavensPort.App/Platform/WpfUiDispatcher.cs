using System.Windows.Threading;
using RavensPort.UI.Services;

namespace RavensPort.App.Platform;

/// <summary>
/// <see cref="IUiDispatcher"/> over the WPF dispatcher.
///
/// The dispatcher is handed in rather than read from <see cref="Dispatcher.CurrentDispatcher"/>
/// here, because that property does not fail on the wrong thread — it silently creates a dispatcher
/// for whatever thread asked, one that nothing will ever pump, and every Post to it disappears. So
/// it is read once at registration, where the caller is provably the UI thread, instead of at
/// construction, where it depends on when the container happened to resolve this.
/// </summary>
internal sealed class WpfUiDispatcher(Dispatcher dispatcher) : IUiDispatcher
{
    public void Post(Action action) => dispatcher.BeginInvoke(action);
}
