using System.Text;
using System.Text.Json;
using RavensPort.Core.Models;

namespace RavensPort.Core.Storage;

/// <summary>
/// The canonical, current copy of every API bridge's manifest — one file per bridge, keyed by id,
/// at <c>%LocalAppData%\RavensPort\manifests\bridges\{id}.json</c>.
///
/// Manifests used to live in the vault, split into an item of their own because they held the
/// largest, most rarely-changing content in the whole configuration. That stopped being viable once
/// a real vault backend turned out to reject a note past a few tens of KB — see the comment on
/// <see cref="Models.McpApiBridgeValidation.MaxManifestBytes"/> for the size cap this app enforces,
/// which is well above what at least one backend actually accepts. A manifest holds no secret (the
/// route attaches a credential one hop later), so the vault bought nothing here but a size ceiling
/// nobody could see coming.
///
/// This is the one local-file store in this codebase that is <em>read back</em> — unlike
/// <see cref="ManifestBackupStore"/>'s timestamped, best-effort convenience copies, or
/// <see cref="Vault.LocalSettings"/>'s UI state, losing a write here loses the bridge's actual
/// tool definitions. <see cref="Save"/> therefore reports a failure instead of swallowing it, the
/// same way a vault save failure already does.
/// </summary>
public static class ManifestLocalStore
{
    /// <summary>
    /// Set only from tests (via <c>InternalsVisibleTo</c>) so a round-trip test exercises real file
    /// I/O without touching the machine's actual <c>%LocalAppData%</c>.
    /// </summary>
    internal static string? RootOverride { get; set; }

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private static string Directory => RootOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RavensPort",
        "manifests",
        "bridges");

    private static string PathFor(Guid bridgeId) => Path.Combine(Directory, $"{bridgeId:D}.json");

    /// <summary>Writes a bridge's manifest. Returns null on success, or a message fit for the UI.</summary>
    public static string? Save(Guid bridgeId, McpApiBridgeManifest manifest)
    {
        try
        {
            // A tool with no "inputSchema" deserializes its JsonElement as Undefined, which throws
            // on write rather than on read — see McpApiBridgeSchema's own doc comment. Every caller
            // that reaches this method today already normalized on the way in, but this is now the
            // one place a manifest is written to its canonical home, so it defends itself too rather
            // than trusting that to stay true.
            McpApiBridgeSchema.Normalize(manifest);

            System.IO.Directory.CreateDirectory(Directory);

            var json = JsonSerializer.Serialize(manifest, Options);
            File.WriteAllText(PathFor(bridgeId), json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return $"Could not save the manifest to disk: {ex.Message}";
        }
    }

    /// <summary>The stored manifest, or null when there is none — no file, or one this build cannot read.</summary>
    public static McpApiBridgeManifest? TryLoad(Guid bridgeId)
    {
        var path = PathFor(bridgeId);
        if (!File.Exists(path)) return null;

        try
        {
            var json = File.ReadAllText(path);
            var manifest = JsonSerializer.Deserialize<McpApiBridgeManifest>(json, Options);

            if (manifest is not null) McpApiBridgeSchema.Normalize(manifest);

            return manifest;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Removes a bridge's manifest file. Best-effort — called when the bridge itself is deleted.</summary>
    public static void Delete(Guid bridgeId)
    {
        try
        {
            File.Delete(PathFor(bridgeId));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing more useful to do with a delete failure for a file about to be orphaned anyway.
        }
    }
}
