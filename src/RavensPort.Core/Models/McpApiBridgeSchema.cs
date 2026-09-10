using System.Text.Json;

namespace RavensPort.Core.Models;

/// <summary>
/// Keeps a tool's <see cref="McpApiBridgeTool.InputSchema"/> in a state the rest of the app can
/// survive.
///
/// A JsonElement is the right shape to hold a schema in — it is what the MCP SDK's Tool takes, so
/// tools/list converts nothing, and it round-trips through the vault note as real readable JSON
/// rather than an escaped string blob. It has one sharp edge, and this class exists for it:
/// <c>default(JsonElement)</c> has ValueKind Undefined, and writing an Undefined element throws.
/// A tool with no "inputSchema" key deserializes to exactly that, and the throw does not land at
/// import where it would be understood — it lands on the next vault save, and on every save after
/// it, while the app carries on looking healthy.
///
/// So nothing is allowed to hold an Undefined schema. Normalize is cheap and is called at import,
/// after a vault load, and again when listing tools. Three calls beat one missed one.
/// </summary>
public static class McpApiBridgeSchema
{
    /// <summary>
    /// The minimal schema the SDK accepts, and what anything unusable becomes. Cloned off its
    /// document so the static is safe to hand out for the life of the process.
    /// </summary>
    public static readonly JsonElement Empty =
        JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone();

    /// <summary>
    /// The schema as given, or <see cref="Empty"/> when it is Undefined or not an object.
    ///
    /// Not-an-object is folded in here as well as being refused at import, because the SDK's Tool
    /// setter throws unless the schema is an object — and a manifest can be edited straight into
    /// the vault note by hand, where import validation never runs. One bad schema would otherwise
    /// throw out of tools/list and blank the whole bridge.
    /// </summary>
    public static JsonElement Normalize(JsonElement schema) =>
        schema.ValueKind == JsonValueKind.Object ? schema : Empty;

    /// <summary>Normalizes every tool in a manifest, in place.</summary>
    public static void Normalize(McpApiBridgeManifest manifest)
    {
        foreach (var tool in manifest.Tools)
        {
            tool.InputSchema = Normalize(tool.InputSchema);
        }
    }

    /// <summary>
    /// Whether a schema is one the SDK will accept: an object whose "type" is "object". The MCP
    /// contract for a tool's input is narrower than JSON Schema in general, and the SDK enforces
    /// it by throwing from a property setter.
    /// </summary>
    public static bool IsUsableToolSchema(JsonElement schema) =>
        schema.ValueKind == JsonValueKind.Object
        && schema.TryGetProperty("type", out var type)
        && type.ValueKind == JsonValueKind.String
        && string.Equals(type.GetString(), "object", StringComparison.Ordinal);

    /// <summary>
    /// The enum values declared for one property of a tool schema, or null when the property is
    /// absent or carries no enum. Used to check that a variant selector advertises exactly the
    /// variants that exist.
    /// </summary>
    public static List<string>? ReadEnumValues(JsonElement schema, string propertyName)
    {
        if (schema.ValueKind != JsonValueKind.Object) return null;
        if (!schema.TryGetProperty("properties", out var properties)) return null;
        if (properties.ValueKind != JsonValueKind.Object) return null;
        if (!properties.TryGetProperty(propertyName, out var property)) return null;
        if (property.ValueKind != JsonValueKind.Object) return null;
        if (!property.TryGetProperty("enum", out var values)) return null;
        if (values.ValueKind != JsonValueKind.Array) return null;

        var result = new List<string>();

        foreach (var value in values.EnumerateArray())
        {
            // A non-string enum entry cannot name a variant key, which is a JSON object key. It
            // is reported as a mismatch rather than skipped, so the author sees why.
            if (value.ValueKind != JsonValueKind.String) return null;

            result.Add(value.GetString() ?? "");
        }

        return result;
    }

    /// <summary>Whether a tool schema declares the named property at all.</summary>
    public static bool DeclaresProperty(JsonElement schema, string propertyName) =>
        schema.ValueKind == JsonValueKind.Object
        && schema.TryGetProperty("properties", out var properties)
        && properties.ValueKind == JsonValueKind.Object
        && properties.TryGetProperty(propertyName, out _);
}
