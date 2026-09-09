using Avalonia.Headless;

namespace RavensPort.UI.Tests;

/// <summary>
/// One Avalonia application, started once for the whole assembly, that test bodies run inside.
///
/// Avalonia is not a library you call from any thread: there is one dispatcher, one
/// Application.Current, and controls assert on both. <see cref="HeadlessUnitTestSession"/> owns
/// that thread and marshals work onto it, which is what Avalonia.Headless.XUnit's [AvaloniaFact]
/// does under its attribute — this suite calls it directly rather than taking on xunit.v3 for the
/// sugar. See the note in the csproj.
///
/// Started once because starting it is not free and because a second session in one process would
/// contend for the same statics.
/// </summary>
public static class UiSession
{
    private static readonly Lazy<HeadlessUnitTestSession> Session =
        new(() => HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApp)));

    /// <summary>Runs a test body on the UI thread, and brings its failure back out unwrapped.</summary>
    public static Task RunAsync(Func<Task> body) => Session.Value.Dispatch(body, CancellationToken.None);
}
