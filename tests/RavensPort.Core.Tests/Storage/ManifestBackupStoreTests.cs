using RavensPort.Core.Storage;

namespace RavensPort.Core.Tests.Storage;

/// <summary>
/// The timestamped, best-effort convenience copies — never read back by the app, so these only pin
/// that a save actually lands a readable file on disk and that two saves for the same name do not
/// collide.
/// </summary>
[Collection(ManifestStoreCollection.Name)]
public sealed class ManifestBackupStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ravensport-manifest-backup-tests-" + Guid.NewGuid());

    public ManifestBackupStoreTests() => ManifestBackupStore.RootOverride = _root;

    public void Dispose()
    {
        ManifestBackupStore.RootOverride = null;

        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void SaveManifestWritesAReadableFileNamedAfterTheBridge()
    {
        var path = ManifestBackupStore.SaveManifest("my-bridge", """{"version":1,"tools":[]}""");

        Assert.NotNull(path);
        Assert.True(File.Exists(path));
        Assert.Contains("my-bridge", Path.GetFileName(path), StringComparison.Ordinal);
        Assert.Equal("""{"version":1,"tools":[]}""", File.ReadAllText(path));
    }

    [Fact]
    public void SaveImportedSpecKeepsTheOriginalExtension()
    {
        var path = ManifestBackupStore.SaveImportedSpec("petstore.yaml", "openapi: 3.0.0");

        Assert.NotNull(path);
        Assert.EndsWith(".yaml", path, StringComparison.Ordinal);
        Assert.Equal("openapi: 3.0.0", File.ReadAllText(path));
    }

    [Fact]
    public void ANameWithInvalidFileNameCharactersIsSanitizedRatherThanFailing()
    {
        var path = ManifestBackupStore.SaveManifest("weird/name:here", "{}");

        Assert.NotNull(path);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void TwoSavesForTheSameBridgeProduceTwoDistinctFiles()
    {
        var first = ManifestBackupStore.SaveManifest("dup", "{}");
        Thread.Sleep(1100); // the filename's resolution is whole seconds
        var second = ManifestBackupStore.SaveManifest("dup", "{}");

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first, second);
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(second));
    }
}
