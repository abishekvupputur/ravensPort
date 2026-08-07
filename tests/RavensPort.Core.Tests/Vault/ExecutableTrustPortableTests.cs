using RavensPort.Core.Vault;

namespace RavensPort.Core.Tests.Vault;

/// <summary>
/// The signature gate where there are no signatures to read.
///
/// Windows asks who signed a binary. Linux cannot, so it asks the question that actually matters
/// for this threat: could an unprivileged process have put the file there? A binary in a system
/// location, under directories no ordinary user can write to, is one only an administrator could
/// have placed — and an attacker with root does not need to plant a fake <c>op</c>.
///
/// These used to be a branch that allowed everything, which was harmless only because it could not
/// be reached while the app was Windows-only.
///
/// Compiled on both targets and skipped at run time on Windows, deliberately. The invariant is
/// about the platform the process is running on, not the framework it was built for — and the
/// portable target still runs on Windows during development, where the Authenticode path is live
/// and these expectations would be wrong.
/// </summary>
public class ExecutableTrustPortableTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ravensport-trust-portable-{Guid.NewGuid()}");

    public ExecutableTrustPortableTests() => Directory.CreateDirectory(_root);

    private string Planted(string name)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, "not a real executable");

        return path;
    }

    [Fact]
    public void ABinaryInAUserWritableDirectoryIsRefused()
    {
        if (OperatingSystem.IsWindows()) return;

        // The whole attack in one line: a file called op, somewhere the user can write, found first
        // on PATH. It must not be handed the vault session key.
        var decision = AuthenticodeTrustPolicy.Default.Decide(Planted("op"));

        Assert.False(decision.Allowed);
    }

    [Fact]
    public void TheRefusalSaysWhyAndOffersTheWayThrough()
    {
        if (OperatingSystem.IsWindows()) return;

        var decision = AuthenticodeTrustPolicy.Default.Decide(Planted("op"));

        // Naming the override matters: without it this is a dead end for someone who genuinely has
        // a portable or self-built copy and knows exactly which binary they mean.
        Assert.Contains(VaultProbe.OnePasswordPathVariable, decision.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ABinaryInstalledByAPackageManagerIsAccepted()
    {
        if (OperatingSystem.IsWindows()) return;

        // /usr/bin/env is on every Linux that can run this test, and it is exactly the shape being
        // trusted: a system location, under root-owned directories no ordinary user can write to.
        // Renamed through the environment override would prove nothing, so this asserts on the
        // provenance check itself rather than going through the name-keyed policy.
        Assert.True(
            UnixExecutableProvenance.IsAdministratorInstalled("/usr/bin/env", out var why),
            $"expected /usr/bin/env to qualify, but: {why}");
    }

    [Fact]
    public void SomethingOutsideASystemLocationDoesNotQualifyHoweverTightItsPermissions()
    {
        if (OperatingSystem.IsWindows()) return;

        // Permissions alone are not enough. A file under the user's own home can be mode 500 and
        // still be one the user put there — and replaced yesterday.
        var path = Planted("op");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        Assert.False(UnixExecutableProvenance.IsAdministratorInstalled(path, out _));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A temp directory that outlives the run is not a test failure.
        }

        GC.SuppressFinalize(this);
    }
}
