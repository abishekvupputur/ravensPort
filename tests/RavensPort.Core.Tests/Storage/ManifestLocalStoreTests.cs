using RavensPort.Core.Models;
using RavensPort.Core.Storage;

namespace RavensPort.Core.Tests.Storage;

/// <summary>
/// The canonical per-bridge manifest file — the one local-file store in this codebase whose write
/// failures are reported rather than swallowed, because losing this write loses the bridge's actual
/// tool definitions now that manifests are no longer written to the vault at all.
///
/// Uses <see cref="ManifestLocalStore.RootOverride"/> throughout so nothing here touches the
/// machine's real %LocalAppData%.
/// </summary>
[Collection(ManifestStoreCollection.Name)]
public sealed class ManifestLocalStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ravensport-manifest-store-tests-" + Guid.NewGuid());

    public ManifestLocalStoreTests() => ManifestLocalStore.RootOverride = _root;

    public void Dispose()
    {
        ManifestLocalStore.RootOverride = null;

        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static McpApiBridgeManifest Manifest(string toolName) => new()
    {
        Tools =
        [
            new McpApiBridgeTool
            {
                Name = toolName,
                Request = new McpApiBridgeRequest { Method = "GET", Path = "/things" },
            },
        ],
    };

    [Fact]
    public void LoadingABridgeThatWasNeverSavedReturnsNull() =>
        Assert.Null(ManifestLocalStore.TryLoad(Guid.NewGuid()));

    [Fact]
    public void SavedManifestReadsBackWithTheSameTools()
    {
        var id = Guid.NewGuid();

        Assert.Null(ManifestLocalStore.Save(id, Manifest("list_things")));

        var loaded = ManifestLocalStore.TryLoad(id);

        Assert.NotNull(loaded);
        Assert.Equal(["list_things"], loaded.Tools.Select(t => t.Name));
    }

    [Fact]
    public void SavingAgainOverwritesRatherThanAppending()
    {
        var id = Guid.NewGuid();

        ManifestLocalStore.Save(id, Manifest("first"));
        ManifestLocalStore.Save(id, Manifest("second"));

        var loaded = ManifestLocalStore.TryLoad(id);

        Assert.Equal(["second"], loaded!.Tools.Select(t => t.Name));
    }

    [Fact]
    public void DeleteRemovesTheFileAndIsSafeWhenNothingWasEverSaved()
    {
        var id = Guid.NewGuid();
        ManifestLocalStore.Save(id, Manifest("x"));

        ManifestLocalStore.Delete(id);
        Assert.Null(ManifestLocalStore.TryLoad(id));

        // Deleting again, or deleting a bridge that was never saved, must not throw.
        ManifestLocalStore.Delete(id);
        ManifestLocalStore.Delete(Guid.NewGuid());
    }

    [Fact]
    public void EachBridgeGetsItsOwnFileById()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        ManifestLocalStore.Save(a, Manifest("a-tool"));
        ManifestLocalStore.Save(b, Manifest("b-tool"));

        Assert.Equal(["a-tool"], ManifestLocalStore.TryLoad(a)!.Tools.Select(t => t.Name));
        Assert.Equal(["b-tool"], ManifestLocalStore.TryLoad(b)!.Tools.Select(t => t.Name));
    }
}
