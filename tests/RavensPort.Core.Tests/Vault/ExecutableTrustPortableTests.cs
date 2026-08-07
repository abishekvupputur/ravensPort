using RavensPort.Core.Vault;

namespace RavensPort.Core.Tests.Vault;

/// <summary>
/// What the signature gate does where there are no signatures to read.
///
/// This covers a branch that used to be unreachable. While the app was Windows-only,
/// <c>ExecutableTrust</c> answered "Authenticode is Windows-only" and <em>allowed</em> — a
/// statement that was true and had no consequence, because nothing ever ran off Windows. A
/// portable build makes it reachable, and as an allow it would have meant: run whatever file named
/// <c>op</c> turns up first on the PATH, and hand it the vault session key. So it refuses now, and
/// this is the test that says so.
///
/// Compiled on both targets and skipped at run time on Windows, deliberately. The invariant is
/// about the platform the process is actually running on, not the framework it was built for — and
/// the portable target still runs on Windows during development, where the Authenticode path is
/// live and these expectations would be wrong.
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
    public void WithNoWayToVerifyAPublisherTheBinaryIsRefused()
    {
        if (OperatingSystem.IsWindows()) return;

        var decision = AuthenticodeTrustPolicy.Default.Decide(Planted("op"));

        Assert.False(decision.Allowed);
    }

    [Fact]
    public void TheRefusalSaysWhyAndOffersTheWayThrough()
    {
        if (OperatingSystem.IsWindows()) return;

        var decision = AuthenticodeTrustPolicy.Default.Decide(Planted("op"));

        // Naming the override matters: without it this is a dead end for someone on Linux who
        // genuinely has 1Password installed and knows exactly which binary they mean.
        Assert.Contains(VaultProbe.OnePasswordPathVariable, decision.Summary, StringComparison.OrdinalIgnoreCase);
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
