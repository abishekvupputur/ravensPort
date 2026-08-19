using System.IO;

namespace RavensPort.Core.Vault;

/// <summary>
/// Removes the pre-2.0 store file. Nothing in this version can read it, so the only question is
/// how thoroughly it goes: the file is a blob of the user's credentials, and a plain delete leaves
/// those bytes on the volume until something else happens to reuse the clusters. Every byte is
/// overwritten with zeros and flushed to the device before the entry is unlinked.
///
/// Run unconditionally at startup — the file is never allowed to survive a launch. This replaces
/// the earlier behaviour, where the file was left alone and the setup page offered to delete it.
/// </summary>
public static class LegacyStorePurge
{
    /// <summary>The pre-vault store, written by versions before 2.0 and unreadable since.</summary>
    public static string StorePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "RavensPort",
        "store.dat");

    /// <summary>
    /// Zeros and deletes <see cref="StorePath"/> if it is there.
    /// </summary>
    /// <returns>
    /// True when the file is gone — including when it was never there. False means it is still on
    /// disk, which on Windows is almost always another handle holding it open; the caller decides
    /// whether that is worth saying out loud.
    /// </returns>
    public static bool Purge() => Purge(StorePath);

    internal static bool Purge(string path)
    {
        try
        {
            if (!File.Exists(path)) return true;

            Zero(path);
            File.Delete(path);
            return true;
        }
        catch (Exception)
        {
            // Left for the caller to report. Failing the launch over it would be worse than the
            // file surviving: the app has no need of it either way, and the next start tries again.
            return !File.Exists(path);
        }
    }

    /// <summary>
    /// Overwrites the file in place. Opened without <see cref="FileShare"/> so a second process
    /// cannot be reading it mid-wipe, and flushed with <c>flushToDisk</c> so the zeros reach the
    /// device rather than sitting in the cache behind a delete that removes the entry first.
    /// </summary>
    private static void Zero(string path)
    {
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Write, FileShare.None, bufferSize: 4096,
            FileOptions.WriteThrough);

        var remaining = stream.Length;
        if (remaining == 0) return;

        var buffer = new byte[(int)Math.Min(remaining, 64 * 1024)];

        while (remaining > 0)
        {
            var chunk = (int)Math.Min(remaining, buffer.Length);
            stream.Write(buffer, 0, chunk);
            remaining -= chunk;
        }

        stream.Flush(flushToDisk: true);
    }
}
