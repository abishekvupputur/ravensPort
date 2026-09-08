using System.Text;

namespace RavensPort.Core.Diagnostics;

/// <summary>
/// Append-only activity log with automatic 2-day rotation, plus an in-memory ring buffer so
/// the UI can show recent lines without re-reading the file. Thread-safe: proxied requests
/// are logged from thread-pool threads while the UI reads on the dispatcher thread.
/// </summary>
public sealed class ActivityLog
{
    private const int RotationDays = 2;
    private const int RetainedPeriods = 5;   // ~10 days of history
    private const int BufferSize = 300;

    /// <summary>How often the retention sweep may run, rather than on every single write.</summary>
    private static readonly TimeSpan PruneInterval = TimeSpan.FromMinutes(30);

    private readonly object _gate = new();
    private readonly Queue<string> _recent = new();
    private readonly string _logDirectory;
    private DateTime _nextPruneUtc = DateTime.UtcNow;

    public ActivityLog(string? logDirectory = null)
    {
        _logDirectory = logDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RavensPort",
            "logs");

        try
        {
            Directory.CreateDirectory(_logDirectory);
        }
        catch
        {
            // This runs during DI construction, so throwing here failed the whole host build
            // and took the app down before it could show anything. The in-memory ring buffer
            // still works without a writable directory; every file write below is already
            // best-effort, so degrade to memory-only logging instead.
        }
    }

    public string LogDirectory => _logDirectory;

    /// <summary>Error log lives alongside the activity log so one folder holds everything.</summary>
    public string ErrorLogPath => Path.Combine(_logDirectory, "errors.log");

    /// <summary>
    /// Current activity file. Named by 2-day bucket, so it rolls over on its own with no
    /// rename/lock dance — the name simply changes when the bucket does.
    /// </summary>
    public string CurrentLogPath => Path.Combine(_logDirectory, $"activity-{CurrentPeriodStart():yyyyMMdd}.log");

    private static DateTime CurrentPeriodStart()
    {
        var today = DateTime.UtcNow.Date;
        var daysSinceEpoch = (int)(today - DateTime.UnixEpoch.Date).TotalDays;
        return today.AddDays(-(daysSinceEpoch % RotationDays));
    }

    public void Log(string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {Sanitize(message)}";

        lock (_gate)
        {
            _recent.Enqueue(line);
            while (_recent.Count > BufferSize) _recent.Dequeue();

            try
            {
                File.AppendAllText(CurrentLogPath, line + Environment.NewLine, Encoding.UTF8);
                PruneExpired();
            }
            catch
            {
                // Logging must never break the thing being logged.
            }
        }
    }

    public void LogError(string message, Exception ex)
    {
        Log($"ERROR {message}: {ex.Message}");
        lock (_gate)
        {
            try
            {
                // Only the caller-supplied message is sanitized. The exception's own rendering
                // is deliberately left intact — errors.log is an explicitly multi-line format
                // and a stack trace is worthless collapsed onto one line.
                File.AppendAllText(ErrorLogPath,
                    $"{DateTime.Now:O} {Sanitize(message)}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}",
                    Encoding.UTF8);
            }
            catch
            {
                // ignored
            }
        }
    }

    /// <summary>
    /// Collapses control characters so one logged event can only ever produce one line.
    ///
    /// This log is line-oriented, and much of what it records is attacker-controlled: request
    /// paths and query keys arrive here percent-*decoded* (Kestrel turns "%0A" into a real
    /// newline), and credential names are free text. Without this, anyone who can send the
    /// proxy a request could write whole fabricated entries — including convincing "PROXY ...
    /// [token: ...]" lines. It does not even take a valid API key: the DENIED line is written
    /// on the rejection path, so a web page's blocked request forges log content just as well.
    ///
    /// Escapes rather than strips, so evidence of the attempt survives instead of being
    /// quietly erased. ESC and friends go too — these files get opened in a terminal.
    /// </summary>
    private static string Sanitize(string message)
    {
        if (!message.Any(char.IsControl)) return message;

        var builder = new StringBuilder(message.Length);
        foreach (var character in message)
        {
            builder.Append(character switch
            {
                '\r' => "\\r",
                '\n' => "\\n",
                '\t' => "\\t",
                _ when char.IsControl(character) => $"\\x{(int)character:x2}",
                _ => character.ToString(),
            });
        }

        return builder.ToString();
    }

    /// <summary>Most recent lines, oldest first.</summary>
    public IReadOnlyList<string> GetRecent(int count)
    {
        lock (_gate)
        {
            return _recent.Reverse().Take(count).Reverse().ToList();
        }
    }

    /// <summary>Deletes every rotated activity log except the current one, plus the error log.</summary>
    public int PruneAll()
    {
        lock (_gate)
        {
            var deleted = 0;
            var current = CurrentLogPath;

            foreach (var file in Directory.EnumerateFiles(_logDirectory, "activity-*.log"))
            {
                if (string.Equals(file, current, StringComparison.OrdinalIgnoreCase)) continue;
                if (TryDelete(file)) deleted++;
            }

            if (File.Exists(ErrorLogPath) && TryDelete(ErrorLogPath)) deleted++;
            return deleted;
        }
    }

    /// <summary>
    /// Drops rotated files older than the retention window. Rate-limited to twice an hour:
    /// this used to enumerate the log directory on every single write, and with two lines
    /// logged per proxied request — all under the same global lock — a busy MCP session was
    /// paying a directory scan per request for a sweep that can only find something once
    /// every two days.
    /// </summary>
    private void PruneExpired()
    {
        var now = DateTime.UtcNow;
        if (now < _nextPruneUtc) return;
        _nextPruneUtc = now + PruneInterval;

        var cutoff = CurrentPeriodStart().AddDays(-RotationDays * RetainedPeriods);

        foreach (var file in Directory.EnumerateFiles(_logDirectory, "activity-*.log"))
        {
            var stamp = Path.GetFileNameWithoutExtension(file).Replace("activity-", "");
            // Invariant, because the stamp is one this class wrote: CurrentPeriodStart formats it
            // with the same fixed pattern, so a user in a non-Gregorian calendar must still be able
            // to prune the files their own app left behind.
            if (DateTime.TryParseExact(stamp, "yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var periodStart)
                && periodStart < cutoff)
            {
                TryDelete(file);
            }
        }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
