using System.Runtime.InteropServices;
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
        if (!TryCreateHardLink(witness, path))
        {
            // Hard links need the same volume and a filesystem that supports them. Where the temp
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

    /// <summary>
    /// A second name for the same file data, or false when this platform will not give one.
    ///
    /// Two calls because .NET has no portable one: kernel32 on Windows, link(2) on everything
    /// else. Worth the second p/invoke rather than skipping the test off Windows -- what it pins
    /// is that credentials are overwritten before the file is unlinked, which matters as much on
    /// the Linux build as on the Windows one.
    ///
    /// A refusal is not a failure. Hard links want the same volume and a filesystem that supports
    /// them, and the caller reads false as "nothing to observe here" and skips.
    /// </summary>
    private static bool TryCreateHardLink(string linkPath, string existingPath)
    {
        try
        {
            return OperatingSystem.IsWindows()
                ? CreateHardLinkW(linkPath, existingPath, IntPtr.Zero)
                : link(existingPath, linkPath) == 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // A platform with neither. Nothing to observe, same as a filesystem that refuses.
            return false;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

    [DllImport("libc", SetLastError = true)]
    private static extern int link(string oldpath, string newpath);
}
