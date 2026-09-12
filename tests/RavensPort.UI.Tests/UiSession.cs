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

    /// <summary>
    /// Runs a test body on the UI thread, and — the part that matters — fails the test when the body
    /// does.
    ///
    /// The obvious spelling, `Session.Dispatch(body, token)`, silently does not. There are three
    /// overloads — Dispatch(Action), Dispatch&lt;T&gt;(Func&lt;T&gt;) and
    /// Dispatch&lt;T&gt;(Func&lt;Task&lt;T&gt;&gt;) — and a Func&lt;Task&gt; binds to the middle one
    /// with T = Task, handing back a Task&lt;Task&gt;. Awaiting that awaits the scheduling of the
    /// body, not the body: every assertion inside ran, threw, and was dropped on the floor. The
    /// suite passed with a deliberately wrong automation id and with a control name that does not
    /// exist, which is how this was found.
    ///
    /// Returning a value is what picks the Func&lt;Task&lt;T&gt;&gt; overload, which awaits the body
    /// while the session keeps pumping the dispatcher. Owning the completion here instead — a
    /// TaskCompletionSource set from inside the lambda — deadlocks: nothing pumps the loop once
    /// Dispatch has returned, so the body's own InvokeAsync never completes.
    /// </summary>
    public static Task RunAsync(Func<Task> body) =>
        Session.Value.Dispatch(async () =>
        {
            await body();

            // The return value is never read. It is here to change which overload this binds to,
            // which is the whole point — see above.
            return true;
        }, CancellationToken.None);
}
