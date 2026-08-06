using Avalonia.Threading;
using RavensPort.UI.Services;

namespace RavensPort.Platform;

/// <summary>
/// <see cref="IUiDispatcher"/> over Avalonia's dispatcher.
///
/// Nothing is captured here, unlike the WPF implementation this replaces.
/// <see cref="Dispatcher.UIThread"/> is a single application-wide dispatcher rather than a
/// per-thread one, so there is no wrong instance to get hold of and no ordering to be careful
/// about — which removes the sharpest edge in the WPF version.
/// </summary>
internal sealed class AvaloniaUiDispatcher : IUiDispatcher
{
    public void Post(Action action) => Dispatcher.UIThread.Post(action);
}
