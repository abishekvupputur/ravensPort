using RavensPort.Core.Diagnostics;
using RavensPort.Core.Vault;

namespace RavensPort.Core.Tests.Vault;

/// <summary>
/// CliRunner against real processes. The parts worth pinning are the ones that only misbehave
/// against a real one: piping a secret in on stdin without deadlocking, and reporting a non-zero
/// exit as an answer rather than an exception.
///
/// What arguments each provider passes — and specifically that none of them is ever a secret — is
/// asserted in the provider tests, which can see the calls without needing a process at all.
/// </summary>
public class CliRunnerTests : IDisposable
{
    private readonly string _logPath = Path.Combine(Path.GetTempPath(), $"ravensport-cli-logs-{Guid.NewGuid()}");

    /// <summary>
    /// A real process to run, per platform. CliRunner itself is portable — pipes, exit codes and
    /// timeouts work the same everywhere — so these tests should run everywhere too, and the only
    /// thing that has to change is which shell is asked and how.
    /// </summary>
    private static readonly bool OnWindows = OperatingSystem.IsWindows();

    private readonly string _shell = OnWindows
        ? Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe"
        : "/bin/sh";

    /// <summary>Echoes stdin back on stdout, which is how a secret reaches stdout for real.</summary>
    private static string[] EchoStdin => OnWindows ? ["/c", "more"] : ["-c", "cat"];

    private static string[] Exit(int code) =>
        OnWindows ? ["/c", "exit", code.ToString()] : ["-c", $"exit {code}"];

    /// <summary>Runs long enough to be killed by a timeout measured in milliseconds.</summary>
    private static string[] Hang => OnWindows
        ? ["/c", "ping", "-n", "30", "127.0.0.1"]
        : ["-c", "sleep 30"];

    private CliRunner NewRunner() => new(new ActivityLog(_logPath));

    [Fact]
    public async Task StdinIsPipedThroughWithoutDeadlocking()
    {
        // The failure this guards against: writing stdin before draining stdout. A child that
        // fills its output buffer blocks, and if this side is still writing, neither moves — the
        // process just hangs until the timeout, looking like a slow password manager.
        var payload = string.Join('\n', Enumerable.Range(0, 500).Select(i => $"line-{i}-{new string('x', 200)}"));

        var result = await NewRunner().RunAsync(_shell, EchoStdin, stdin: payload);

        Assert.True(result.Succeeded, result.StdErr);
        Assert.Contains("line-499", result.StdOut);
    }

    [Fact]
    public async Task ACommandWithNoStdinStillCompletes()
    {
        // Stdin is closed unconditionally. Skipping that on the no-stdin path would hang every
        // CLI that reads a template from stdin and waits for EOF.
        var result = await NewRunner().RunAsync(_shell, Exit(0));

        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task ANonZeroExitIsAnAnswerRatherThanAnException()
    {
        // "Not signed in" and "no such vault" both arrive this way. Throwing would turn a state
        // the setup page knows how to explain into an unhandled error.
        var result = await NewRunner().RunAsync(_shell, Exit(7));

        Assert.Equal(7, result.ExitCode);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task AMissingBinaryIsReportedAsAVaultCliException()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"definitely-not-here-{Guid.NewGuid()}");

        await Assert.ThrowsAsync<VaultCliException>(() => NewRunner().RunAsync(missing, ["--version"]));
    }

    [Fact]
    public async Task AHangingCommandIsKilledAtTheTimeout()
    {
        // A password manager waiting on a prompt nobody answers must not hold the app forever.
        var runner = NewRunner();

        var exception = await Assert.ThrowsAsync<VaultCliException>(() =>
            runner.RunAsync(_shell, Hang,
                timeout: TimeSpan.FromMilliseconds(500)));

        Assert.Contains("did not respond", exception.Message);
    }

    [Fact]
    public async Task StdoutIsNeverWrittenToTheActivityLog()
    {
        // Captured stdout is item contents. Logging it would put every secret the app reads into
        // a plaintext file next to the app — the exact thing storing them in a vault avoids.
        const string Secret = "SENTINEL-SECRET-IN-STDOUT";

        // Fed in on stdin and echoed back, which is how a real secret reaches stdout — as item
        // contents, never as an argument. (An argument *is* logged, which is exactly why the
        // providers are forbidden from putting a secret in one.)
        var activityLog = new ActivityLog(_logPath);
        var result = await new CliRunner(activityLog).RunAsync(_shell, EchoStdin, stdin: Secret);

        Assert.Contains(Secret, result.StdOut);
        Assert.DoesNotContain(activityLog.GetRecent(100), line => line.Contains(Secret));
    }

    [Fact]
    public async Task TheBinaryThatActuallyRanIsRecorded_OncePerPath()
    {
        // VaultProbe takes the first match on PATH, and PATH routinely includes directories an
        // unprivileged process can write to. Nothing here stops a swapped binary — this pins that
        // the swap leaves a record. Describe() writes only the file name, which cannot tell the
        // real op.exe from an impostor sitting earlier on PATH.
        var activityLog = new ActivityLog(_logPath);
        var runner = new CliRunner(activityLog);

        // Deliberately non-canonical: what is recorded has to be the file that ran, not whatever
        // string the caller happened to be holding.
        var indirect = Path.Combine(Path.GetDirectoryName(_shell)!, ".", Path.GetFileName(_shell));

        await runner.RunAsync(indirect, Exit(0));
        await runner.RunAsync(_shell, Exit(0));

        var launches = activityLog.GetRecent(100).Where(line => line.Contains("launching CLI from")).ToList();

        Assert.Single(launches);
        Assert.Contains(_shell, launches[0], StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        try { Directory.Delete(_logPath, recursive: true); } catch { /* best effort */ }
    }
}
