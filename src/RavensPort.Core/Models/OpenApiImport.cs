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
/// One operation a spec declares, as listed by <see cref="OpenApiImporter.Discover"/> before
/// conversion. <paramref name="EstimatedBytes"/> is what this operation adds to the manifest's
/// stored size — measured as the difference it makes to a real serialization, so it accounts for
/// the indentation the tool carries inside the array, which serializing it on its own would miss.
/// </summary>
public sealed record OpenApiOperationSummary(
    string Path,
    string Method,
    string? OperationId,
    string? Summary,
    IReadOnlyList<string> Tags,
    int EstimatedBytes = 0);

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

    public static OpenApiImportResult Convert(string specText, string sourceName) =>
        Convert(specText, sourceName, includeOnly: null);

    /// <summary>
    /// <paramref name="includeOnly"/> null means every operation the spec has (today's behavior);
    /// otherwise only (path, method) pairs present in the set are built into tools — everything else
    /// is left out silently, since that is a deliberate exclusion the caller made, not a limitation
    /// this converter is reporting on.
    /// </summary>
    public static OpenApiImportResult Convert(
        string specText, string sourceName, IReadOnlySet<(string Path, string Method)>? includeOnly)
    {
        var (error, document) = ParseDocument(specText);

        if (error is not null) return OpenApiImportResult.Failed(error);

        var manifest = new McpApiBridgeManifest
        {
            Title = document!.Info?.Title,
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

                if (includeOnly is not null && !includeOnly.Contains((path, methodName)))
                {
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

    /// <summary>
    /// Parse only — one entry per operation <see cref="Convert(string,string,IReadOnlySet{ValueTuple{string,string}}?)"/>
    /// could build (same <see cref="PermittedMethods"/> filter, so nothing shown here is a dead end),
    /// for a picker to list before any tool gets built.
    ///
    /// Each operation is costed as it goes, so a picker can show a running size against the
    /// manifest's byte cap without re-parsing the document — which for a spec the size of GitHub's
    /// is the expensive part by a wide margin. <c>BaseManifestBytes</c> is what a manifest of this
    /// document with no tools in it already weighs.
    /// </summary>
    public static (string? Error, IReadOnlyList<OpenApiOperationSummary> Operations, int BaseManifestBytes) Discover(
        string specText)
    {
        var (error, document) = ParseDocument(specText);

        if (error is not null) return (error, [], 0);

        // Reused for every measurement: one tool in, measure, take it out again.
        var scratch = new McpApiBridgeManifest
        {
            Title = document!.Info?.Title,
            Description = Truncate(document.Info?.Description),
        };

        var baseBytes = McpApiBridgeValidation.MeasureBytes(scratch);

        var operations = new List<OpenApiOperationSummary>();
        var discardedWarnings = new List<string>();

        foreach (var (path, item) in document.Paths.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var sharedParameters = item.Parameters ?? [];

            foreach (var (method, operation) in (item.Operations ?? new Dictionary<HttpMethod, Microsoft.OpenApi.OpenApiOperation>())
                         .OrderBy(o => o.Key.Method, StringComparer.Ordinal))
            {
                var methodName = method.Method.ToUpperInvariant();

                if (!PermittedMethods.Contains(methodName)) continue;

                var tags = operation.Tags?.Select(tag => tag.Name).Where(name => name is not null).Cast<string>().ToList()
                           ?? [];

                // A name set of its own, so this measurement does not depend on which other
                // operations happen to have been costed before it.
                discardedWarnings.Clear();
                var tool = BuildTool(
                    path, methodName, operation, sharedParameters,
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase), discardedWarnings);

                scratch.Tools.Clear();
                scratch.Tools.Add(tool);

                var bytes = McpApiBridgeValidation.MeasureBytes(scratch) - baseBytes;

                operations.Add(new OpenApiOperationSummary(
                    path, methodName, operation.OperationId, operation.Summary, tags, bytes));
            }
        }

        return (null, operations, baseBytes);
    }

    private static (string? Error, Microsoft.OpenApi.OpenApiDocument? Document) ParseDocument(string specText)
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
            return ($"This is not a readable OpenAPI document: {ex.Message}", null);
        }

        if (document is null || document.Paths.Count == 0)
        {
            var detail = diagnostic is { Errors.Count: > 0 }
                ? string.Join(" ", diagnostic.Errors.Select(e => e.Message))
                : "no paths were found in it.";

            return ($"This is not a usable OpenAPI document: {detail}", null);
        }

        return (null, document);
    }

    // ---- one operation ------------------------------------------------------------------------

    /// <summary>
    /// Everything one operation's conversion accumulates into, bundled so the methods that fill it
    /// take one parameter instead of six — <c>path</c> stays separate since it is reassigned, not
    /// appended to.
    /// </summary>
    private sealed class ToolBuildState(string toolName, List<string> warnings)
    {
        public string ToolName { get; } = toolName;
        public JsonObject Properties { get; } = [];
        public List<string> Required { get; } = [];
        public HashSet<string> UsedArgNames { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> Query { get; } = [];
        public Dictionary<string, string> Headers { get; } = [];
        public List<string> Warnings { get; } = warnings;

        /// <summary>
        /// Appended to the tool's description when the body could not be described field by field.
        /// The call still works — <see cref="McpApiBridgeBodyMode.Arguments"/> forwards whatever
        /// arguments the path, query and headers did not consume — but an agent only knows it may
        /// send them if something says so.
        /// </summary>
        public string? BodyNote { get; set; }
    }

    private static McpApiBridgeTool BuildTool(
        string path,
        string methodName,
        Microsoft.OpenApi.OpenApiOperation operation,
        IList<IOpenApiParameter> sharedParameters,
        HashSet<string> usedToolNames,
        List<string> warnings)
    {
        var name = AllocateToolName(operation.OperationId, methodName, path, usedToolNames);
        var state = new ToolBuildState(name, warnings);
        var rewrittenPath = path;

        foreach (var parameter in MergeParameters(sharedParameters, operation.Parameters))
        {
            AddParameter(parameter, ref rewrittenPath, state);
        }

        var bodyMode = operation.RequestBody is { } requestBody
            ? AddRequestBody(requestBody, methodName, state)
            : McpApiBridgeBodyMode.None;

        var inputSchema = BuildInputSchema(state.Properties, state.Required);

        return new McpApiBridgeTool
        {
            Name = name,
            Description = CombineDescription(operation.Summary, operation.Description, state.BodyNote),
            ReadOnly = methodName is "GET" or "HEAD",
            InputSchema = inputSchema,
            Request = new McpApiBridgeRequest
            {
                Method = methodName,
                Path = rewrittenPath,
                Query = state.Query,
                Headers = state.Headers,
                BodyMode = bodyMode,
            },
        };
    }

    /// <summary>Operation-level parameters override a path-level one of the same name and location.</summary>
    private static Dictionary<(string Name, ParameterLocation? In), IOpenApiParameter>.ValueCollection MergeParameters(
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

    private static void AddParameter(IOpenApiParameter parameter, ref string path, ToolBuildState state)
    {
        if (string.IsNullOrWhiteSpace(parameter.Name)) return;

        if (parameter.In == ParameterLocation.Cookie)
        {
            state.Warnings.Add($"'{state.ToolName}': cookie parameter '{parameter.Name}' was skipped — a bridge does not forward cookies.");
            return;
        }

        if (parameter.In == ParameterLocation.Header
            && (RouteValidation.IsReservedHeaderName(parameter.Name) || IsBridgeReservedHeader(parameter.Name)))
        {
            state.Warnings.Add($"'{state.ToolName}': header parameter '{parameter.Name}' was skipped — the route attaches the credential.");
            return;
        }

        // A parameter the format cannot name is left out on its own rather than allowed to fail the
        // whole tool later. One unusable parameter costs that parameter; it should not cost the
        // endpoint — published specs do carry them, and the endpoint is usually fine without.
        if (parameter.In == ParameterLocation.Query
            && !McpApiBridgeValidation.IsUsableQueryParameterName(parameter.Name))
        {
            state.Warnings.Add($"'{state.ToolName}': query parameter '{parameter.Name}' was skipped — a name containing "
                               + "& = # ? or control characters reads as structure rather than as part of the name. "
                               + "The rest of the tool was imported.");
            return;
        }

        if (parameter.In == ParameterLocation.Header && !RouteValidation.IsValidHeaderName(parameter.Name.Trim()))
        {
            state.Warnings.Add($"'{state.ToolName}': header parameter '{parameter.Name}' was skipped — it is not a valid "
                               + "HTTP header name. The rest of the tool was imported.");
            return;
        }

        var argName = Deduplicate(SanitizeArgName(parameter.Name), state.UsedArgNames);

        AddSchemaProperty(state.Properties, argName, parameter.Schema, parameter.Description, state.Warnings, state.ToolName);

        if (parameter.Required || parameter.In == ParameterLocation.Path) state.Required.Add(argName);

        switch (parameter.In)
        {
            case ParameterLocation.Path:
                if (argName != parameter.Name)
                {
                    path = path.Replace($"{{{parameter.Name}}}", $"{{{argName}}}", StringComparison.Ordinal);
                }

                break;

            case ParameterLocation.Query:
                state.Query[parameter.Name] = $"{{{argName}}}";
                break;

            case ParameterLocation.Header:
                state.Headers[parameter.Name] = $"{{{argName}}}";
                break;
        }
    }

    private static McpApiBridgeBodyMode AddRequestBody(
        IOpenApiRequestBody requestBody, string methodName, ToolBuildState state)
    {
        // A manifest may not send a body with GET or HEAD, and specs do declare one anyway —
        // GitHub's repos_get-content is a GET with a request body. Leaving the body off keeps the
        // endpoint; attaching it would have the validator refuse the tool whole.
        if (methodName is "GET" or "HEAD")
        {
            state.Warnings.Add($"'{state.ToolName}': the spec declares a request body on a {methodName}, which a "
                               + "manifest cannot send. The tool was imported without it.");

            return McpApiBridgeBodyMode.None;
        }

        var json = (requestBody.Content ?? new Dictionary<string, IOpenApiMediaType>()).FirstOrDefault(
            pair => pair.Key.Equals("application/json", StringComparison.OrdinalIgnoreCase));

        if (json.Value is null)
        {
            if (requestBody.Content is { Count: > 0 })
            {
                state.Warnings.Add($"'{state.ToolName}': request body content ({string.Join(", ", requestBody.Content.Keys)}) "
                             + "is not application/json and was not imported. Add it by hand if the tool needs it.");
            }

            return McpApiBridgeBodyMode.None;
        }

        var schema = json.Value.Schema;

        var properties = new Dictionary<string, IOpenApiSchema?>(StringComparer.Ordinal);
        var required = new HashSet<string>(StringComparer.Ordinal);

        if (schema is not null) CollectBodyProperties(schema, properties, required, depth: 0);

        if (properties.Count == 0)
        {
            // A body the spec does not describe field by field — a free-form object, a map keyed by
            // something arbitrary (Tailscale's split DNS), or a composition this could not flatten.
            // Arguments mode still sends it: whatever the agent passes that the path, query and
            // headers did not consume becomes the body. Dropping the body instead produced a tool
            // that called the endpoint with nothing in it, which fails at the far end rather than
            // here, and is the less honest of the two.
            state.BodyNote = "This call takes a JSON request body whose fields the spec does not list. "
                             + "Pass them as arguments and they are forwarded as the body.";

            return McpApiBridgeBodyMode.Arguments;
        }

        foreach (var (propertyName, propertySchema) in properties)
        {
            var argName = Deduplicate(SanitizeArgName(propertyName), state.UsedArgNames, preferOriginal: propertyName);

            if (argName != propertyName)
            {
                state.Warnings.Add($"'{state.ToolName}': body property '{propertyName}' collided with a path/query/header "
                             + $"argument and was renamed to '{argName}'.");
            }

            AddSchemaProperty(state.Properties, argName, propertySchema, description: null, state.Warnings, state.ToolName);

            if (required.Contains(propertyName)) state.Required.Add(argName);
        }

        return McpApiBridgeBodyMode.Arguments;
    }

    /// <summary>
    /// Flattens a request-body schema into one set of properties.
    ///
    /// <c>allOf</c> is a genuine merge — every branch applies at once, so its required fields stay
    /// required. <c>oneOf</c> and <c>anyOf</c> are a union taken as all-optional: exactly one branch
    /// (or some combination) applies, and which one is the caller's choice, so calling any branch's
    /// field required here would refuse valid calls. That is a description of the body rather than a
    /// faithful translation of the rule, and it is the closest this format can express — the
    /// alternative, which this used to do, was to send no body at all.
    /// </summary>
    private static void CollectBodyProperties(
        IOpenApiSchema schema,
        Dictionary<string, IOpenApiSchema?> properties,
        HashSet<string> required,
        int depth,
        bool optional = false)
    {
        // Guards against a self-referencing schema, which a $ref cycle makes reachable.
        if (depth > 8) return;

        if (schema.Properties is { Count: > 0 } own)
        {
            foreach (var (name, propertySchema) in own) properties.TryAdd(name, propertySchema);

            if (!optional && schema.Required is { Count: > 0 } names)
            {
                foreach (var name in names) required.Add(name);
            }
        }

        foreach (var branch in schema.AllOf ?? []) CollectBodyProperties(branch, properties, required, depth + 1, optional);

        foreach (var branch in (schema.OneOf ?? []).Concat(schema.AnyOf ?? []))
        {
            CollectBodyProperties(branch, properties, required, depth + 1, optional: true);
        }
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

    private static string? CombineDescription(string? summary, string? description, string? note = null)
    {
        string? text;

        if (string.IsNullOrWhiteSpace(summary))
        {
            text = description;
        }
        else if (string.IsNullOrWhiteSpace(description) || description == summary)
        {
            text = summary;
        }
        else
        {
            text = $"{summary} {description}";
        }

        if (string.IsNullOrWhiteSpace(note)) return Truncate(text);
        if (string.IsNullOrWhiteSpace(text)) return Truncate(note);

        // The note survives the cap rather than being truncated off the end of a long description:
        // it is the only thing telling an agent that this call takes a body at all, so the prose
        // gives way to it instead.
        var room = McpApiBridgeValidation.MaxDescriptionLength - note.Length - 1;

        if (room <= 0) return Truncate(note);

        return $"{(text.Length > room ? text[..room] : text)} {note}";
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
