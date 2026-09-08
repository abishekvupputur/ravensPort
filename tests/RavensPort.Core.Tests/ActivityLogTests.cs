using RavensPort.Core.Diagnostics;

namespace RavensPort.Core.Tests;

/// <summary>
/// The activity log is line-oriented and records attacker-controlled text (request paths and
/// query keys arrive percent-*decoded*, so "%0A" is a real newline by the time it gets here).
/// Without escaping, anyone able to send the proxy a request could write whole fabricated
/// entries - and the DENIED line is written on the rejection path, so it does not even take a
/// valid API key.
/// </summary>
public class ActivityLogTests : IDisposable
{
    private const char Escape = '\u001b';

    private readonly string _logDirectory = Path.Combine(Path.GetTempPath(), $"ravensport-test-logs-{Guid.NewGuid()}");

    [Fact]
    public void Log_NewlinesInMessage_CannotForgeAdditionalEntries()
    {
        var log = new ActivityLog(_logDirectory);

        log.Log("DENIED GET /foo\n2026-01-01 00:00:00 PROXY GET /injected -> https://evil.test");

        var recent = log.GetRecent(50);
        Assert.Single(recent);
        Assert.DoesNotContain('\n', recent[0]);
        Assert.DoesNotContain('\r', recent[0]);

        // Escaped, not stripped: evidence of the attempt has to survive.
        Assert.Contains("\\n", recent[0]);
        Assert.Contains("PROXY GET /injected", recent[0]);

        // And the same holds on disk, which is what anyone reviewing an incident actually reads.
        Assert.Single(File.ReadAllLines(log.CurrentLogPath));
    }

    [Fact]
    public void Log_CarriageReturnsAndEscapeSequences_AreNeutralized()
    {
        var log = new ActivityLog(_logDirectory);

        // \r alone still rewrites a line in most viewers, and ESC drives ANSI sequences in the
        // terminal these files get opened in.
        log.Log($"CONNECT 'creds\r{Escape}[2Jcleared'");

        var line = Assert.Single(log.GetRecent(50));
        Assert.DoesNotContain('\r', line);
        Assert.DoesNotContain(Escape, line);
        Assert.Contains("\\r", line);
        Assert.Contains("\\x1b", line);
    }

    [Fact]
    public void Log_OrdinaryMessage_IsLeftAlone()
    {
        var log = new ActivityLog(_logDirectory);

        const string message = "PROXY GET /app/echo/resource?token=<redacted> -> https://api.test/";
        log.Log(message);

        Assert.EndsWith(message, Assert.Single(log.GetRecent(50)));
    }

    [Fact]
    public void LogError_SanitizesTheMessageButKeepsTheStackTraceReadable()
    {
        var log = new ActivityLog(_logDirectory);

        log.LogError("REFRESH 'a\nb' threw", new InvalidOperationException("boom"));

        Assert.DoesNotContain('\n', Assert.Single(log.GetRecent(50)));

        // errors.log is an explicitly multi-line format - collapsing the exception's own
        // rendering onto one line would make stack traces useless.
        var errorText = File.ReadAllText(log.ErrorLogPath);
        Assert.Contains("REFRESH 'a\\nb' threw", errorText);
        Assert.Contains("InvalidOperationException", errorText);
    }

    public void Dispose()
    {
        try { Directory.Delete(_logDirectory, recursive: true); } catch { /* best effort */ }

        // Nothing here has a finalizer, but the pattern is what CA1816 asks for and what a
        // derived test fixture would need if one ever did.
        GC.SuppressFinalize(this);
    }
}
