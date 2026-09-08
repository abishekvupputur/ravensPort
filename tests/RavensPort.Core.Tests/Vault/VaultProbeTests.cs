using RavensPort.Core.Vault;

namespace RavensPort.Core.Tests.Vault;

/// <summary>
/// Finding the CLI is the first thing that happens at startup and the first thing a user hits when
/// it goes wrong — "not installed" when it plainly is installed is a support conversation nobody
/// wants. These pin the search order.
///
/// PATH is passed in rather than set on the process: corrupting the real one would break every
/// other test in the suite, and the runtime underneath them, in ways that would look like
/// anything but this file.
/// </summary>
public class VaultProbeTests : IDisposable
{
    private const string Variable = "RAVENSPORT_TEST_PROBE_PATH";

    private readonly string _pathDir = NewDirectory();
    private readonly string _wellKnownDir = NewDirectory();

    [Fact]
    public void TheEnvironmentOverrideWins()
    {
        var overridden = Stub(_wellKnownDir, "op.exe");
        var onPath = Stub(_pathDir, "op.exe");
        Environment.SetEnvironmentVariable(Variable, overridden);

        Assert.Equal(overridden, VaultProbe.Find(Variable, "op.exe", [onPath], _pathDir));
    }

    [Fact]
    public void AnOverridePointingAtNothingFindsNothing()
    {
        // Deliberately not a fallback. Silently ignoring the override would leave the user staring
        // at "not installed" while the setting they set sat there doing nothing.
        var onPath = Stub(_pathDir, "op.exe");
        Environment.SetEnvironmentVariable(Variable, Path.Combine(_wellKnownDir, "does-not-exist.exe"));

        Assert.Null(VaultProbe.Find(Variable, "op.exe", [onPath], _pathDir));
    }

    [Fact]
    public void PathIsSearchedBeforeTheWellKnownLocations()
    {
        var onPath = Stub(_pathDir, "op.exe");
        var wellKnown = Stub(_wellKnownDir, "op.exe");

        Assert.Equal(onPath, VaultProbe.Find(Variable, "op.exe", [wellKnown], _pathDir));
    }

    [Fact]
    public void TheWellKnownLocationsAreTheFallback()
    {
        var wellKnown = Stub(_wellKnownDir, "pass-cli.exe");

        Assert.Equal(wellKnown, VaultProbe.Find(Variable, "pass-cli.exe", [wellKnown], _pathDir));
    }

    [Fact]
    public void NothingAnywhereFindsNothing()
    {
        Assert.Null(VaultProbe.Find(Variable, "op.exe", [Path.Combine(_wellKnownDir, "op.exe")], _pathDir));
    }

    [Fact]
    public void ASymlinkOnPathResolvesToTheBinaryBehindIt()
    {
        // WinGet installs both CLIs as symlinks in its Links directory, and that is the copy that
        // lands on PATH. A symlink is a zero-byte reparse point carrying no signature of its own,
        // so handing one on meant the signature check inspected an empty file and reported
        // 1Password's validly signed CLI as "not signed at all" — and RavensPort refused to run it.
        //
        // Skipped rather than failed where the OS will not create the link: that needs Developer
        // Mode or elevation, and a machine without either should not fail the suite over it.
        var target = Stub(_wellKnownDir, "op-real.exe");
        var link = Path.Combine(_pathDir, "op.exe");

        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        Assert.Equal(target, VaultProbe.Find(Variable, "op.exe", [], _pathDir));
    }

    [Fact]
    public void AnOrdinaryBinaryIsReturnedUnchanged()
    {
        // The other half of resolving links: a normal file must come back exactly as found, not
        // rewritten into some canonical form the trust cache and the launcher would then disagree
        // about.
        var onPath = Stub(_pathDir, "op.exe");

        Assert.Equal(onPath, VaultProbe.Find(Variable, "op.exe", [], _pathDir));
    }

    [Fact]
    public void AMalformedPathEntryDoesNotStopTheSearch()
    {
        // A long-lived Windows PATH accumulates junk — quoted entries, empty segments, and entries
        // with characters that make Path.Combine throw. One of those must not make the app report
        // every password manager as missing.
        var onPath = Stub(_pathDir, "op.exe");
        var messyPath = string.Join(Path.PathSeparator, ["C:\\has\"quotes\"\\bad", "", "   ", _pathDir]);

        Assert.Equal(onPath, VaultProbe.Find(Variable, "op.exe", [], messyPath));
    }

    [Theory]
    [InlineData("2.30.0", 2, 30)]
    [InlineData("2.30.0\n", 2, 30)]
    [InlineData("op 2.31.1 (build 2031001)", 2, 31)]
    [InlineData("pass-cli 1.4", 1, 4)]
    public void AVersionIsPulledOutOfWhateverTheCliPrints(string output, int major, int minor)
    {
        var version = VaultProbe.ParseVersion(output);

        Assert.NotNull(version);
        Assert.Equal(major, version.Major);
        Assert.Equal(minor, version.Minor);
    }

    [Theory]
    [InlineData("")]
    [InlineData("command not found")]
    public void OutputWithNoVersionInItParsesAsNothing(string output)
    {
        Assert.Null(VaultProbe.ParseVersion(output));
    }

    private static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ravensport-probe-{Guid.NewGuid()}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static string Stub(string directory, string exeName)
    {
        var path = Path.Combine(directory, exeName);
        File.WriteAllText(path, "");
        return path;
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(Variable, null);

        foreach (var directory in new[] { _pathDir, _wellKnownDir })
        {
            try { Directory.Delete(directory, recursive: true); } catch { /* best effort */ }
        }

        // Nothing here has a finalizer, but the pattern is what CA1816 asks for and what a
        // derived test fixture would need if one ever did.
        GC.SuppressFinalize(this);
    }
}
