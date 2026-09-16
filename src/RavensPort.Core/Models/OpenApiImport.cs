using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Reader;

namespace RavensPort.Core.Models;

/// <summary>
/// What came out of converting an OpenAPI document: either an error naming why nothing could be
/// produced, or a manifest draft plus whatever the converter had to drop or guess at along the way.
/// </summary>
public sealed class OpenApiImportResult
{
    /// <summary>Set when nothing could be produced. <see cref="ManifestJson"/> is null in that case.</summary>
    public string? Error { get; private init; }

    /// <summary>
    /// Manifest text, ready to drop into the same editor "Import file…" and "Load sample" fill —
    /// nothing here bypasses <see cref="McpApiBridgeValidation"/>. A leading block of "//" comments
    /// names what was skipped or guessed at; the format tolerates comments on read (see
    /// <c>McpApiBridgeValidation.ImportOptions</c>), so the warnings travel with the text itself
    /// rather than needing a second place to show them.
    /// </summary>
    public string? ManifestJson { get; private init; }

    public IReadOnlyList<string> Warnings { get; private init; } = [];

    public static OpenApiImportResult Failed(string error) => new() { Error = error };

    public static OpenApiImportResult Succeeded(string manifestJson, List<string> warnings) =>
        new() { ManifestJson = manifestJson, Warnings = warnings };
}

/// <summary>
/// Turns an OpenAPI 3.0/3.1 or Swagger 2.0 document — JSON or YAML — into a manifest draft: one
/// tool per operation, parameters mapped into <c>inputSchema</c>, using the same
/// <c>{placeholder}</c> syntax OpenAPI paths already use.
///
/// This is a draft, not a second way to bypass validation. Nothing it produces is trusted blindly:
/// the generated text is handed back as plain manifest JSON and goes through exactly the same
/// <see cref="McpApiBridgeValidation.TryReadManifest"/> path a hand-written or pasted manifest does.
/// Where the source document says something this format cannot represent — a credential-carrying
/// header, a non-JSON body, more operations than a manifest may declare — the converter leaves it
/// out and says so in a warning, rather than guessing at a shape that might validate by accident and
/// mean the wrong thing.
///
/// See <c>templates/api-mcp/AUTHORING.md</c> for the rules this mirrors, and note the one rule an
/// import can never satisfy on its own: paths are relative to the *route's* prefix, and this
/// converter has no route to check them against. That is called out in the warning header too.
/// </summary>
public static class OpenApiImporter
{
    private static readonly HashSet<string> PermittedMethods =
        new(StringComparer.Ordinal) { "GET", "HEAD", "POST", "PUT", "PATCH", "DELETE" };

    private static readonly JsonSerializerOptions ManifestWriteOptions = new() { WriteIndented = true };

    public static OpenApiImportResult Convert(string specText, string sourceName)
    {
        Microsoft.OpenApi.OpenApiDocument? document;
        OpenApiDiagnostic? diagnostic;

        try
        {
            var settings = new OpenApiReaderSettings();
            settings.AddYamlReader();

            var result = OpenApiModelFactory.Parse(specText, format: null, settings);
            document = result.Document;
            diagnostic = result.Diagnostic;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return OpenApiImportResult.Failed($"This is not a readable OpenAPI document: {ex.Message}");
        }

        if (document is null || document.Paths.Count == 0)
        {
            var detail = diagnostic is { Errors.Count: > 0 }
                ? string.Join(" ", diagnostic.Errors.Select(e => e.Message))
                : "no paths were found in it.";

            return OpenApiImportResult.Failed($"This is not a usable OpenAPI document: {detail}");
        }

        var manifest = new McpApiBridgeManifest
        {
            Title = document.Info?.Title,
            Description = Truncate(document.Info?.Description),
        };

        var warnings = new List<string>();
        var usedToolNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var skippedByMethod = 0;

        foreach (var (path, item) in document.Paths.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var sharedParameters = item.Parameters ?? [];

            foreach (var (method, operation) in (item.Operations ?? new Dictionary<HttpMethod, Microsoft.OpenApi.OpenApiOperation>())
                         .OrderBy(o => o.Key.Method, StringComparer.Ordinal))
            {
                var methodName = method.Method.ToUpperInvariant();

                if (!PermittedMethods.Contains(methodName))
                {
                    skippedByMethod++;
                    continue;
                }

                var tool = BuildTool(path, methodName, operation, sharedParameters, usedToolNames, warnings);

                // Belt and braces: nothing here should be able to produce a tool the validator
                // itself refuses, but a spec can contain things this converter never anticipated —
                // Tailscale's own document has a query parameter literally named
                // "<field>=<value> filters", documentation text left in the name slot by mistake.
                // Checked against the one source of truth for what a tool may look like rather than
                // trusting this converter's own idea of what it produced, so a manifest that comes
                // out of Convert() is always one the app would accept.
                if (McpApiBridgeValidation.ValidateTool(tool) is { } toolError)
                {
                    warnings.Add($"'{tool.Name}' was dropped — {toolError}");
                    continue;
                }

                manifest.Tools.Add(tool);
            }
        }

        if (skippedByMethod > 0)
        {
            warnings.Add($"{skippedByMethod} operation(s) used OPTIONS or TRACE, which a manifest "
                         + "cannot call, and were skipped.");
        }

        if (manifest.Tools.Count > McpApiBridgeValidation.MaxTools)
        {
            var dropped = manifest.Tools.Count - McpApiBridgeValidation.MaxTools;
            manifest.Tools.RemoveRange(McpApiBridgeValidation.MaxTools, dropped);

            warnings.Add($"{dropped} operation(s) were dropped — a manifest may declare at most "
                         + $"{McpApiBridgeValidation.MaxTools} tools. Trim this document and add the "
                         + "rest by hand if you need them.");
        }

        // The tool-count cap and the byte cap are independent: 64 tools with long, well-written
        // descriptions (exactly what a good API's own spec tends to have) can still be too big for
        // one vault item. Trimmed from the end rather than failing outright — a partial, oversized
        // import is still less work than starting from nothing.
        var droppedForSize = 0;

        while (manifest.Tools.Count > 0
               && McpApiBridgeValidation.MeasureBytes(manifest) > McpApiBridgeValidation.MaxManifestBytes)
        {
            manifest.Tools.RemoveAt(manifest.Tools.Count - 1);
            droppedForSize++;
        }

        if (droppedForSize > 0)
        {
            warnings.Add($"{droppedForSize} more tool(s) were dropped to fit the manifest under its "
                         + $"{McpApiBridgeValidation.MaxManifestBytes / 1024} KB cap — this document's "
                         + "descriptions run long. Trim descriptions, or add the dropped tools by hand.");
        }

        if (manifest.Tools.Count == 0)
        {
            return OpenApiImportResult.Failed(
                "Nothing from this document fits in a manifest: every operation either uses a method "
                + "a manifest cannot call, fails validation on its own, or is too large to fit even alone.");
        }

        var json = JsonSerializer.Serialize(manifest, ManifestWriteOptions);
        var withHeader = PrependWarningHeader(json, sourceName, manifest.Tools.Count, warnings);

        return OpenApiImportResult.Succeeded(withHeader, warnings);
    }

    // ---- one operation ------------------------------------------------------------------------

    private static McpApiBridgeTool BuildTool(
        string path,
        string methodName,
        Microsoft.OpenApi.OpenApiOperation operation,
        IList<IOpenApiParameter> sharedParameters,
        HashSet<string> usedToolNames,
        List<string> warnings)
    {
        var name = AllocateToolName(operation.OperationId, methodName, path, usedToolNames);

        var properties = new JsonObject();
        var required = new List<string>();
        var usedArgNames = new HashSet<string>(StringComparer.Ordinal);

        var query = new Dictionary<string, string>();
        var headers = new Dictionary<string, string>();
        var rewrittenPath = path;

        foreach (var parameter in MergeParameters(sharedParameters, operation.Parameters))
        {
            AddParameter(parameter, name, ref rewrittenPath, query, headers, properties, required, usedArgNames, warnings);
        }

        var bodyMode = McpApiBridgeBodyMode.None;

        if (operation.RequestBody is { } requestBody)
        {
            bodyMode = AddRequestBody(requestBody, name, properties, required, usedArgNames, warnings);
        }

        var inputSchema = BuildInputSchema(properties, required);

        return new McpApiBridgeTool
        {
            Name = name,
            Description = CombineDescription(operation.Summary, operation.Description),
            ReadOnly = methodName is "GET" or "HEAD",
            InputSchema = inputSchema,
            Request = new McpApiBridgeRequest
            {
                Method = methodName,
                Path = rewrittenPath,
                Query = query,
                Headers = headers,
                BodyMode = bodyMode,
            },
        };
    }

    /// <summary>Operation-level parameters override a path-level one of the same name and location.</summary>
    private static IEnumerable<IOpenApiParameter> MergeParameters(
        IList<IOpenApiParameter> shared, IList<IOpenApiParameter>? operationLevel)
    {
        var merged = new Dictionary<(string Name, ParameterLocation? In), IOpenApiParameter>();

        foreach (var parameter in shared)
        {
            if (parameter.Name is { } name) merged[(name, parameter.In)] = parameter;
        }

        foreach (var parameter in operationLevel ?? [])
        {
            if (parameter.Name is { } name) merged[(name, parameter.In)] = parameter;
        }

        return merged.Values;
    }

    private static void AddParameter(
        IOpenApiParameter parameter,
        string toolName,
        ref string path,
        Dictionary<string, string> query,
        Dictionary<string, string> headers,
        JsonObject properties,
        List<string> required,
        HashSet<string> usedArgNames,
        List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(parameter.Name)) return;

        if (parameter.In == ParameterLocation.Cookie)
        {
            warnings.Add($"'{toolName}': cookie parameter '{parameter.Name}' was skipped — a bridge does not forward cookies.");
            return;
        }

        if (parameter.In == ParameterLocation.Header
            && (RouteValidation.IsReservedHeaderName(parameter.Name) || IsBridgeReservedHeader(parameter.Name)))
        {
            warnings.Add($"'{toolName}': header parameter '{parameter.Name}' was skipped — the route attaches the credential.");
            return;
        }

        var argName = Deduplicate(SanitizeArgName(parameter.Name), usedArgNames);

        AddSchemaProperty(properties, argName, parameter.Schema, parameter.Description, warnings, toolName);

        if (parameter.Required || parameter.In == ParameterLocation.Path) required.Add(argName);

        switch (parameter.In)
        {
            case ParameterLocation.Path:
                if (argName != parameter.Name)
                {
                    path = path.Replace($"{{{parameter.Name}}}", $"{{{argName}}}", StringComparison.Ordinal);
                }

                break;

            case ParameterLocation.Query:
                query[parameter.Name] = $"{{{argName}}}";
                break;

            case ParameterLocation.Header:
                headers[parameter.Name] = $"{{{argName}}}";
                break;
        }
    }

    private static McpApiBridgeBodyMode AddRequestBody(
        IOpenApiRequestBody requestBody,
        string toolName,
        JsonObject properties,
        List<string> required,
        HashSet<string> usedArgNames,
        List<string> warnings)
    {
        var json = (requestBody.Content ?? new Dictionary<string, IOpenApiMediaType>()).FirstOrDefault(
            pair => pair.Key.Equals("application/json", StringComparison.OrdinalIgnoreCase));

        if (json.Value is null)
        {
            if (requestBody.Content is { Count: > 0 })
            {
                warnings.Add($"'{toolName}': request body content ({string.Join(", ", requestBody.Content.Keys)}) "
                             + "is not application/json and was not imported. Add it by hand if the tool needs it.");
            }

            return McpApiBridgeBodyMode.None;
        }

        var schema = json.Value.Schema;
        var bodyProperties = schema?.Properties;

        if (schema is null || bodyProperties is not { Count: > 0 })
        {
            warnings.Add($"'{toolName}': the JSON request body has no fixed set of properties in the spec "
                         + "(or uses allOf/oneOf/anyOf, which this importer does not flatten) and was not imported.");

            return McpApiBridgeBodyMode.None;
        }

        var bodyRequired = schema.Required ?? new HashSet<string>();

        foreach (var (propertyName, propertySchema) in bodyProperties)
        {
            var argName = Deduplicate(SanitizeArgName(propertyName), usedArgNames, preferOriginal: propertyName);

            if (argName != propertyName)
            {
                warnings.Add($"'{toolName}': body property '{propertyName}' collided with a path/query/header "
                             + $"argument and was renamed to '{argName}'.");
            }

            AddSchemaProperty(properties, argName, propertySchema, description: null, warnings, toolName);

            if (bodyRequired.Contains(propertyName)) required.Add(argName);
        }

        return McpApiBridgeBodyMode.Arguments;
    }

    // ---- schema -------------------------------------------------------------------------------

    private static void AddSchemaProperty(
        JsonObject properties,
        string argName,
        IOpenApiSchema? schema,
        string? description,
        List<string> warnings,
        string toolName)
    {
        var type = MapType(schema?.Type, argName, toolName, warnings);

        var propertySchema = new JsonObject { ["type"] = type };

        var effectiveDescription = description ?? schema?.Description;

        if (!string.IsNullOrWhiteSpace(effectiveDescription))
        {
            propertySchema["description"] = Truncate(effectiveDescription);
        }

        if (!string.IsNullOrWhiteSpace(schema?.Format))
        {
            propertySchema["format"] = schema.Format;
        }

        if (type == "array")
        {
            propertySchema["items"] = new JsonObject { ["type"] = MapType(schema?.Items?.Type, argName, toolName, warnings, isItems: true) };
        }

        if (schema?.Enum is { Count: > 0 } enumValues)
        {
            var stringValues = enumValues
                .Select(node => node is JsonValue value && value.TryGetValue<string>(out var s) ? s : null)
                .Where(s => s is not null)
                .Cast<string>()
                .ToList();

            if (stringValues.Count == enumValues.Count)
            {
                propertySchema["enum"] = new JsonArray(stringValues.Select(v => (JsonNode)v).ToArray());
            }
        }

        properties[argName] = propertySchema;
    }

    /// <summary>
    /// JsonSchemaType is a flags enum with a Null bit for OpenAPI 3.1's nullable-via-union — masked
    /// off here since the manifest format has no separate "nullable" concept. Anything this
    /// converter cannot map to one plain JSON Schema type falls back to "string" with a warning
    /// rather than emitting a schema shape the MCP SDK might refuse outright.
    /// </summary>
    private static string MapType(JsonSchemaType? type, string argName, string toolName, List<string> warnings, bool isItems = false)
    {
        if (type is null) return "string";

        var masked = type.Value & ~JsonSchemaType.Null;

        var mapped = masked switch
        {
            JsonSchemaType.Boolean => "boolean",
            JsonSchemaType.Integer => "integer",
            JsonSchemaType.Number => "number",
            JsonSchemaType.String => "string",
            JsonSchemaType.Array => "array",
            JsonSchemaType.Object => "object",
            _ => null,
        };

        if (mapped is not null) return mapped;

        var what = isItems ? $"'{argName}' items" : $"'{argName}'";
        warnings.Add($"'{toolName}': {what} has a schema type this importer could not map and was treated as a string.");
        return "string";
    }

    private static JsonElement BuildInputSchema(JsonObject properties, List<string> required)
    {
        var schema = new JsonObject { ["type"] = "object" };

        if (properties.Count > 0)
        {
            schema["properties"] = properties;
        }

        if (required.Count > 0)
        {
            schema["required"] = new JsonArray(required.Distinct(StringComparer.Ordinal).Select(r => (JsonNode)r).ToArray());
        }

        return JsonDocument.Parse(schema.ToJsonString()).RootElement.Clone();
    }

    // ---- naming ---------------------------------------------------------------------------------

    private static string AllocateToolName(string? operationId, string method, string path, HashSet<string> used)
    {
        var raw = !string.IsNullOrWhiteSpace(operationId)
            ? operationId
            : SynthesizeName(method, path);

        var sanitized = SanitizeToolName(raw);

        // Room for a "_NN" de-duplication suffix without exceeding the cap the funnel alias
        // prefix leaves for a tool name.
        if (sanitized.Length > McpApiBridgeValidation.MaxToolNameLength - 4)
        {
            sanitized = sanitized[..(McpApiBridgeValidation.MaxToolNameLength - 4)];
        }

        return Deduplicate(sanitized, used);
    }

    private static string SynthesizeName(string method, string path)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(segment => segment.Trim('{', '}'));

        return $"{method.ToLowerInvariant()}_{string.Join('_', segments)}";
    }

    private static string SanitizeToolName(string raw)
    {
        var sanitized = new string(raw.Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '_').ToArray());
        return sanitized.Length == 0 ? "tool" : sanitized;
    }

    private static string SanitizeArgName(string raw)
    {
        var sanitized = new string(raw.Select(c => char.IsAsciiLetterOrDigit(c) || c == '_' ? c : '_').ToArray());
        return sanitized.Length == 0 ? "arg" : sanitized;
    }

    /// <summary>
    /// Ordinal, not case-insensitive: two OpenAPI names differing only by sanitization (a header
    /// "X-Trace" and a query "x_trace") should collide, but two schema properties differing only in
    /// case should not — that is a real (if unusual) pair of JSON properties, not a mistake.
    /// </summary>
    private static string Deduplicate(string desired, HashSet<string> used, string? preferOriginal = null)
    {
        if (preferOriginal is not null && !used.Contains(preferOriginal))
        {
            used.Add(preferOriginal);
            return preferOriginal;
        }

        if (used.Add(desired)) return desired;

        var i = 2;
        while (!used.Add($"{desired}_{i}")) i++;

        return $"{desired}_{i}";
    }

    private static bool IsBridgeReservedHeader(string name) => name.ToLowerInvariant() switch
    {
        "authorization" or "cookie" or "content-type" => true,
        _ => false,
    };

    // ---- text -------------------------------------------------------------------------------

    private static string? CombineDescription(string? summary, string? description)
    {
        if (string.IsNullOrWhiteSpace(summary)) return Truncate(description);
        if (string.IsNullOrWhiteSpace(description) || description == summary) return Truncate(summary);

        return Truncate($"{summary} {description}");
    }

    private static string? Truncate(string? text) =>
        text is { Length: > McpApiBridgeValidation.MaxDescriptionLength }
            ? text[..McpApiBridgeValidation.MaxDescriptionLength]
            : text;

    private static string PrependWarningHeader(string manifestJson, string sourceName, int toolCount, List<string> warnings)
    {
        var header = new StringBuilder();

        header.Append("// Imported from ").Append(sourceName).AppendLine(" via OpenAPI.");
        header.Append("// ").Append(toolCount).AppendLine(" tool(s) imported — review before saving.");

        if (warnings.Count > 0)
        {
            header.Append("// ").Append(warnings.Count).AppendLine(" warning(s):");

            foreach (var warning in warnings)
            {
                header.Append("//   - ").AppendLine(warning);
            }
        }

        header.AppendLine("// Paths are exactly as the spec wrote them — check each one against this");
        header.AppendLine("// bridge's route prefix before saving. See templates/api-mcp/AUTHORING.md.");
        header.Append(manifestJson);

        return header.ToString();
    }
}
