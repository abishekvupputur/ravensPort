using System.Diagnostics.CodeAnalysis;
using Avalonia.Controls;
using Avalonia.Threading;

namespace RavensPort.Helpers;

/// <summary>
/// Where to read about RavensPort and where to tell someone it is broken — shown on the Settings
/// and Setup pages and in the tray menu. One list so the three cannot drift apart.
/// </summary>
[SuppressMessage("Minor Code Smell", "S1075:URIs should not be hardcoded",
    Justification = "These are the project's public addresses, the same ones the website footer "
                    + "and README link to. There is nothing to configure them from.")]
public static class ProjectLinks
{
    public static IReadOnlyList<(string Label, string Uri)> All { get; } =
    [
        ("Website", "https://abishekvupputur.github.io/ravensPort/"),
        ("Docs", "https://abishekvupputur.github.io/ravensPort/docs/"),
        ("Request support", "https://github.com/abishekvupputur/ravensPort/issues"),
        ("Report vulnerabilities privately", "https://github.com/abishekvupputur/ravensPort/security/advisories/new"),
    ];

    /// <summary>
    /// Opens <paramref name="uri"/> in the default browser through <paramref name="window"/>'s
    /// launcher. Posted to the dispatcher because the WinForms tray menu calls this from its own
    /// message loop, and Avalonia's launcher has to be reached from the UI thread.
    /// </summary>
    public static void Open(TopLevel? window, string uri) => Dispatcher.UIThread.Post(async void () =>
    {
        try
        {
            if (window is not null) await window.Launcher.LaunchUriAsync(new Uri(uri));
        }
        catch
        {
            // Top-level async void, so nothing above catches this. A link that fails to open is
            // not worth taking the app down over.
        }
    });
}
