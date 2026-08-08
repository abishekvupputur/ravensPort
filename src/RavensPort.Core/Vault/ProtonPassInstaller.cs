using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using RavensPort.Core.Diagnostics;

namespace RavensPort.Core.Vault;

/// <summary>
/// Fetches the Proton Pass CLI so a user does not have to install it themselves.
///
/// **Downloaded, never bundled.** pass-cli is GPL-3.0 and RavensPort is MIT. Shipping the binary
/// inside the installer would make every RavensPort release a GPL redistribution, carrying a
/// written-offer-for-source obligation with it. Fetching the upstream release on the user's say-so
/// keeps the two at arm's length: the user obtains it from Proton, unmodified, and RavensPort only
/// runs it as a child process. See THIRD-PARTY-NOTICES.md.
///
/// **Pinned by hash, not by "latest".** A version range would mean the bytes RavensPort downloads
/// could change without a RavensPort release, which is precisely the property an attacker wants
/// from a supply chain. The pin below is verified before anything is written to disk, so a
/// mismatch fails with nothing extracted rather than leaving a half-trusted binary behind.
/// </summary>
public sealed class ProtonPassInstaller
{
    public const string PinnedVersion = "2.2.4";

    /// <summary>
    /// Whether the platform's asset is a zip holding the exe and a DLL beside it, or the bare
    /// binary. Proton ships a zip for Windows and an unwrapped executable for Linux and macOS.
    /// </summary>
    private static bool AssetIsZipped => OperatingSystem.IsWindows();

    /// <summary>
    /// SHA-256 of the pinned release asset for this platform, as published by Proton alongside it
    /// and independently recomputed from the downloaded bytes before being written here. Bumping
    /// <see cref="PinnedVersion"/> without bumping these is a deliberate hard failure.
    /// </summary>
    public static string PinnedSha256 => OperatingSystem.IsWindows()
        // pass-cli-windows-x86_64.zip
        ? "8077bbfed54842305dbdef2744bddaa368fd36b349ce9e2c406a598c82e38d77"
        // pass-cli-linux-x86_64
        : "9d50cb8604e3c7aee0bdd29fcecf4696ed3259134a6c17e4b8adadfde17d7bb6";

    /// <summary>
    /// x86_64 only. An arm64 Linux gets no in-app install — Proton publishes an aarch64 build, but
    /// pinning a hash for an architecture nothing here can verify against would be pinning a number
    /// someone read off a web page. Those users install it themselves.
    /// </summary>
    public static bool CanInstallInApp =>
        RuntimeInformation.OSArchitecture == Architecture.X64
        && (OperatingSystem.IsWindows() || OperatingSystem.IsLinux());

    public static string DownloadUrl => OperatingSystem.IsWindows()
        ? $"https://github.com/protonpass/pass-cli/releases/download/{PinnedVersion}/pass-cli-windows-x86_64.zip"
        : $"https://github.com/protonpass/pass-cli/releases/download/{PinnedVersion}/pass-cli-linux-x86_64";

    /// <summary>The file name the CLI is installed and probed under, per platform.</summary>
    public static string ExeName => OperatingSystem.IsWindows() ? "pass-cli.exe" : "pass-cli";

    /// <summary>Where the upstream source can be obtained, for the GPL notice and the UI.</summary>
    public const string SourceUrl = "https://github.com/protonpass/pass-cli";

    /// <summary>
    /// Local rather than roaming, unlike the activity log. This is a 40 MB platform-specific
    /// native binary; a roaming profile would copy it between machines that may not even be the
    /// same architecture.
    /// </summary>
    public static string DefaultInstallRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RavensPort",
        "cli",
        "pass-cli");

    /// <summary>
    /// Where the default install lands, for <see cref="VaultProbe"/> — which searches for a binary
    /// rather than being handed one, so it has no instance to ask.
    /// </summary>
    public static string DefaultExePath { get; } =
        Path.Combine(DefaultInstallRoot, PinnedVersion, ExeName);

    public string InstallRoot { get; }

    /// <summary>Versioned, so a pin bump installs alongside rather than over a running binary.</summary>
    public string VersionDirectory { get; }

    public string ExePath { get; }

    /// <summary>
    /// True when this exact pinned version is already unpacked. Cheap enough for the setup page
    /// to ask on every probe.
    /// </summary>
    public bool IsInstalled => File.Exists(ExePath);

    private readonly ActivityLog _activityLog;
    private readonly Func<CancellationToken, Task<byte[]>> _download;

    /// <param name="download">
    /// The fetch step, injectable so tests can exercise the verify-and-extract path — including a
    /// hash mismatch — without reaching the network.
    /// </param>
    /// <param name="installRootOverride">
    /// Somewhere other than the profile. For tests, which must neither write into nor depend on
    /// the real install location — a developer machine that happens to have run the installer
    /// would otherwise make the mismatch test pass by skipping it.
    /// </param>
    public ProtonPassInstaller(
        ActivityLog activityLog,
        Func<CancellationToken, Task<byte[]>>? download = null,
        string? installRootOverride = null)
    {
        _activityLog = activityLog;
        _download = download ?? DownloadFromGitHubAsync;

        InstallRoot = installRootOverride ?? DefaultInstallRoot;
        VersionDirectory = Path.Combine(InstallRoot, PinnedVersion);
        ExePath = Path.Combine(VersionDirectory, ExeName);
    }

    /// <summary>
    /// Downloads, verifies and unpacks the pinned release. Returns the path to the executable.
    /// A no-op returning the existing path when the pinned version is already installed.
    /// </summary>
    public async Task<string> InstallAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (IsInstalled)
        {
            progress?.Report($"Proton Pass CLI {PinnedVersion} is already installed.");
            return ExePath;
        }

        progress?.Report($"Downloading the Proton Pass CLI {PinnedVersion} (about 14 MB)…");

        byte[] archive;
        try
        {
            archive = await _download(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _activityLog.LogError($"VAULT could not download pass-cli {PinnedVersion}", ex);
            throw new VaultCliException(
                "The Proton Pass CLI could not be downloaded. Check your internet connection, or "
                + $"install it yourself from {SourceUrl} and choose Check again.", ex);
        }

        progress?.Report("Verifying the download…");
        Verify(archive);

        progress?.Report("Unpacking…");
        Extract(archive);

        _activityLog.Log($"VAULT installed pass-cli {PinnedVersion} to {VersionDirectory}");
        return ExePath;
    }

    /// <summary>
    /// Checked before a single byte is written. A mismatch means the bytes are not the release
    /// this build was pinned to, and the only safe thing to do with them is nothing.
    /// </summary>
    private void Verify(byte[] archive)
    {
        var actual = Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant();

        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(actual), Convert.FromHexString(PinnedSha256)))
        {
            _activityLog.Log($"VAULT REFUSED pass-cli download — expected {PinnedSha256}, got {actual}");

            throw new VaultCliException(
                "The downloaded Proton Pass CLI did not match the checksum RavensPort expects, so it "
                + "was discarded. Nothing was installed. This can mean the download was corrupted or "
                + "intercepted — try again, and if it keeps happening install the CLI yourself from "
                + $"{SourceUrl}.");
        }
    }

    /// <summary>
    /// Unpacks into a temporary sibling and then moves it into place, so an interrupted extract
    /// cannot leave a directory that looks installed but is missing files. The archive holds
    /// <c>pass-cli.exe</c> and <c>libcrypto-3-x64.dll</c>; the exe will not start without the DLL
    /// beside it, so both are extracted flat into the same directory.
    /// </summary>
    private void Extract(byte[] archive)
    {
        var staging = VersionDirectory + ".incoming-" + Guid.NewGuid().ToString("N")[..8];

        try
        {
            Directory.CreateDirectory(staging);

            if (AssetIsZipped)
            {
                using var stream = new MemoryStream(archive, writable: false);
                using var zip = new ZipArchive(stream, ZipArchiveMode.Read);

                foreach (var entry in zip.Entries)
                {
                    // Flattened deliberately: the archive is two files at the root today, and a
                    // flat extract means a crafted entry name like "..\..\evil.exe" cannot walk
                    // out of the staging directory.
                    var name = Path.GetFileName(entry.FullName);
                    if (name.Length == 0) continue;

                    entry.ExtractToFile(Path.Combine(staging, name), overwrite: true);
                }
            }
            else
            {
                // Not an archive at all on Linux and macOS: Proton publishes the executable itself,
                // statically linked, with nothing to unpack and no sibling library to place. The
                // bytes have already passed the hash check, so this is the whole of the install.
                var target = Path.Combine(staging, ExeName);
                File.WriteAllBytes(target, archive);

                // Downloaded files are not executable, and the CLI is about to be launched.
                // Owner-only: this is the program the vault session key is handed to.
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(target,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }
            }

            if (!File.Exists(Path.Combine(staging, ExeName)))
            {
                throw new VaultCliException(
                    $"The Proton Pass CLI download did not contain {ExeName}. Nothing was installed.");
            }

            Directory.CreateDirectory(InstallRoot);

            // Another instance may have won the race and installed it already; its copy passed the
            // same hash check, so it is the same bytes and there is nothing to argue about.
            if (Directory.Exists(VersionDirectory))
            {
                Directory.Delete(staging, recursive: true);
                return;
            }

            Directory.Move(staging, VersionDirectory);
        }
        catch (Exception ex) when (ex is not VaultCliException)
        {
            _activityLog.LogError($"VAULT could not unpack pass-cli {PinnedVersion}", ex);

            throw new VaultCliException(
                $"The Proton Pass CLI could not be unpacked into {InstallRoot}: {ex.Message}", ex);
        }
        finally
        {
            TryDelete(staging);
        }
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch
        {
            // Staging left behind in temp-ish form under LOCALAPPDATA. Harmless, and failing the
            // install over a cleanup problem would be worse than the litter.
        }
    }

    private static async Task<byte[]> DownloadFromGitHubAsync(CancellationToken ct)
    {
        // Constructed per call rather than held: this runs at most once per machine per pinned
        // version, so a pooled client would sit idle for the life of the process for nothing.
        // Same connector as everything else: a 46 MB download that silently stalls on an
        // unroutable address is the worst place to discover a broken IPv6 route.
        using var client = new HttpClient(Net.HappyEyeballs.CreateHandler()) { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("RavensPort");

        return await client.GetByteArrayAsync(DownloadUrl, ct).ConfigureAwait(false);
    }
}
