using System.Text;
using RavensPort.Core.Vault;

namespace RavensPort.Core.Tests;

/// <summary>
/// The pre-2.0 store held every credential the user had, and this version cannot read it - so it
/// is wiped at startup rather than left for the user to deal with. A plain delete would unlink the
/// entry and leave the secrets in the clusters, so the bytes are overwritten first; these tests
/// pin that the overwrite really happens before the file goes, and that a locked file fails
/// honestly instead of reporting success.
/// </summary>
public class LegacyStorePurgeTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"ravensport-test-legacy-{Guid.NewGuid()}");

    public LegacyStorePurgeTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch
        {
            // A test that left a handle open should fail on its own assertion, not on cleanup.
        }

        GC.SuppressFinalize(this);
    }

    private string WriteStore(string contents)
    {
        var path = Path.Combine(_directory, "store.dat");
        File.WriteAllText(path, contents, Encoding.UTF8);
        return path;
    }

    [Fact]
    public void Purge_ExistingStore_DeletesIt()
    {
        var path = WriteStore("secret-token-value");

        Assert.True(LegacyStorePurge.Purge(path));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Purge_MissingStore_ReportsSuccess()
    {
        // The ordinary case on every start after the first, and on every clean install.
        Assert.True(LegacyStorePurge.Purge(Path.Combine(_directory, "store.dat")));
    }

    [Fact]
    public void Purge_EmptyStore_DeletesIt()
    {
        // Zero-length means nothing to overwrite; the wipe loop must not stall or throw on it.
        var path = WriteStore(string.Empty);

        Assert.True(LegacyStorePurge.Purge(path));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Purge_OverwritesEveryByteBeforeDeleting()
    {
        // The point of the whole exercise: the delete must land on a file that is already zeros.
        // Read through a second hard link, which shares the file's data with the path being
        // purged and survives the unlink - so what it shows is what the delete left behind.
        var path = WriteStore(new string('S', 200_000));
        var length = new FileInfo(path).Length;

        var witness = Path.Combine(_directory, "witness.dat");
        if (!CreateHardLinkW(witness, path, IntPtr.Zero))
        {
            // Hard links need the same volume and an NTFS-like filesystem. Where the temp
            // directory cannot provide one there is nothing to observe, so skip rather than
            // assert something weaker and call it the same test.
            return;
        }

        Assert.True(LegacyStorePurge.Purge(path));
        Assert.False(File.Exists(path));

        var remaining = File.ReadAllBytes(witness);
        Assert.Equal(length, remaining.Length);
        Assert.All(remaining, b => Assert.Equal(0, b));
    }

    [Fact]
    public void Purge_StoreHeldOpenByAnotherHandle_ReportsFailure()
    {
        // Reporting success here would be the dangerous failure: the setup page hides its card on
        // a true result, so the file would stay on disk with nothing left saying so.
        var path = WriteStore("secret-token-value");

        using var holder = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        Assert.False(LegacyStorePurge.Purge(path));
        Assert.True(File.Exists(path));
    }

    [System.Runtime.InteropServices.DllImport(
        "kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);
}
