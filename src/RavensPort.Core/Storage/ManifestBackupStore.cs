using System.Text;

namespace RavensPort.Core.Storage;

/// <summary>
/// Plain-text copies of manifests and imported OpenAPI specs, written to
/// <c>%LocalAppData%\RavensPort\manifests\</c> purely for the user's own reference — nothing here
/// is read back by the app.
///
/// Safe to write unencrypted: a manifest is not secret (see <see cref="Models.McpApiBridgeManifest"/>'s
/// own doc comment — it is kept out of the vault's config note for size, not for confidentiality),
/// and an OpenAPI spec is a public API description by definition. Neither ever carries a credential;
/// the whole point of a manifest is that the route attaches one later, one hop past anything written
/// here.
///
/// Best-effort throughout, the same way <see cref="Vault.LocalSettings"/> treats its own local
/// file: a failure to write a convenience copy must never be why the real save — to the vault, or of
/// the manifest itself — did not happen.
/// </summary>
public static class ManifestBackupStore
{
    /// <summary>Set only from tests, so a round trip exercises real file I/O without touching the machine's actual %LocalAppData%.</summary>
    internal static string? RootOverride { get; set; }

    private static string Directory => RootOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RavensPort",
        "manifests");

    /// <summary>Saves a bridge's manifest text exactly as it stood in the editor when saved.</summary>
    public static string? SaveManifest(string bridgeName, string manifestJson) =>
        Save(bridgeName, "manifest", ".json", manifestJson);

    /// <summary>Saves an OpenAPI document exactly as it was read from disk, before conversion.</summary>
    public static string? SaveImportedSpec(string fileName, string specText)
    {
        var extension = Path.GetExtension(fileName);

        return Save(
            Path.GetFileNameWithoutExtension(fileName),
            "openapi-import",
            string.IsNullOrEmpty(extension) ? ".txt" : extension,
            specText);
    }

    private static string? Save(string label, string kind, string extension, string content)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);

            var fileName = $"{Sanitize(label)}-{kind}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}{extension}";
            var path = Path.Combine(Directory, fileName);

            File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(value.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();

        return cleaned.Length == 0 ? "unnamed" : cleaned;
    }
}
