using System.Text;
using System.Text.Json;
using RavensPort.Core.Mcp;
using RavensPort.Core.Proxy;
using RavensPort.Core.Vault;

namespace RavensPort.Core.Models;

/// <summary>
/// One place deciding whether a bridge and its manifest are usable, shared by the UI and by the
/// endpoint for the same reason <see cref="RouteValidation"/> and <see cref="McpFunnelValidation"/>
/// are: the two must not disagree, or the UI accepts a manifest the endpoint then refuses to serve.
///
/// Every method returns null when acceptable, or a message fit for the UI footer. Messages name
/// the offending tool or field, because a manifest is a document the user wrote and "invalid
/// manifest" sends them looking through all of it.
/// </summary>
public static class McpApiBridgeValidation
{
    public const int MaxTools = 64;
    public const int MaxVariantsPerTool = 16;
    public const int MaxPrompts = 32;
    public const int MaxSkills = 8;
    public const int MaxDescriptionLength = 4096;
    public const int MaxInstructionsLength = 16 * 1024;

    /// <summary>
    /// Per-manifest ceiling, measured on the indented form that is actually stored.
    ///
    /// A manifest gets a vault item of its own, so this is a bound on one item rather than on the
    /// whole configuration — but neither password manager documents a size limit this app could
    /// rely on, and a paste accident should not be the thing that finds it. Generous enough for a
    /// documented API with skills attached.
    /// </summary>
    public const int MaxManifestBytes = 128 * 1024;

    /// <summary>
    /// A tool name has to survive being prefixed with a source alias when a funnel pools this
    /// bridge — <see cref="McpNameMapper.Encode"/> truncates past 128 characters, and a truncated
    /// name is dropped from the funnel's listing entirely. Derived rather than written as 94, so
    /// that raising the alias cap cannot silently start deleting tools from funnels.
    /// </summary>
    public const int MaxToolNameLength =
        McpNameMapper.MaxNameLength - McpFunnelValidation.MaxAliasLength - 2;

    private static readonly string[] PermittedMethods =
        ["GET", "HEAD", "POST", "PUT", "PATCH", "DELETE"];

    /// <summary>
    /// Headers a manifest may not write, on top of the ones the forward itself owns (see
    /// <see cref="RouteValidation.IsReservedHeaderName"/>).
    ///
    /// Authorization is the sharp one. It is the slot the credential transform writes, and a
    /// manifest able to set it could replace the user's token with one of its own choosing —
    /// which is the whole of what a bridge must never be able to do. Cookie is here because the
    /// transform strips the caller's cookies deliberately and a manifest-set one would put them
    /// back; Content-Type because the request's own field owns it; and the proxy's internal
    /// headers because they decide authentication and loop protection.
    /// </summary>
    private static readonly string[] BridgeReservedHeaders =
    [
        "authorization",
        "cookie",
        "content-type",
        LocalAccessGuard.ApiKeyHeaderName,
        LocalAccessGuard.FunnelHopHeaderName,
        LocalAccessGuard.BridgeHopHeaderName,
    ];

    /// <summary>
    /// Forgiving on the way in: case-insensitive property names, trailing commas, and comments
    /// are all accepted, because a manifest is hand-written JSON and none of those three change
    /// its meaning. The stored form is normalized regardless.
    /// </summary>
    private static readonly JsonSerializerOptions ImportOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    // ---- the record ------------------------------------------------------------------------

    /// <summary>
    /// The slug becomes a literal path segment in "/api-mcp/{slug}", so it is restricted exactly
    /// as a funnel's is. Deliberately not checked against funnel slugs: the two live in different
    /// path space, and refusing 'gmail' for a bridge because a funnel already used it would be a
    /// rule with no mechanism behind it.
    /// </summary>
    public static string? ValidateSlug(string? slug, IEnumerable<McpApiBridgeRecord> existing, Guid? editingId = null)
    {
        if (string.IsNullOrWhiteSpace(slug))
        {
            return "Endpoint slug is required.";
        }

        var trimmed = slug.Trim();

        if (trimmed.Length > 64)
        {
            return "Endpoint slug may be at most 64 characters.";
        }

        if (!trimmed.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-'))
        {
            return "Endpoint slug may only contain lowercase letters, digits, and hyphens.";
        }

        if (existing.Any(b => b.Id != editingId && string.Equals(b.Slug, trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            return $"Another API bridge already uses the slug '{trimmed}'.";
        }

        return null;
    }

    public static string? ValidateName(string? name) =>
        string.IsNullOrWhiteSpace(name) ? "Name is required." : null;

    public static string? ValidateRoute(Guid routeId, IEnumerable<RouteMapping> routes) =>
        routes.Any(r => r.Id == routeId)
            ? null
            : "Pick the route this API is reached through — its credential is what the tools will use.";

    // ---- the manifest ----------------------------------------------------------------------

    /// <summary>
    /// Parses and validates raw manifest text. The one entry point the UI uses, so that what the
    /// preview shows and what the endpoint serves cannot come apart.
    /// </summary>
    public static string? TryReadManifest(string? json, out McpApiBridgeManifest? manifest)
    {
        manifest = null;

        if (string.IsNullOrWhiteSpace(json))
        {
            return "Paste a manifest, or import one from a file.";
        }

        McpApiBridgeManifest? parsed;

        try
        {
            parsed = JsonSerializer.Deserialize<McpApiBridgeManifest>(json, ImportOptions);
        }
        catch (JsonException ex)
        {
            // The SDK's message already carries the line and position, which is the only part of
            // a parse failure worth putting in front of someone editing JSON by hand.
            return $"This is not valid JSON: {ex.Message}";
        }

        if (parsed is null)
        {
            return "This manifest is empty.";
        }

        McpApiBridgeSchema.Normalize(parsed);

        if (ValidateManifest(parsed) is { } error) return error;

        manifest = parsed;
        return null;
    }

    public static string? ValidateManifest(McpApiBridgeManifest manifest)
    {
        if (ValidateVersion(manifest) is { } versionError) return versionError;

        if (manifest.Instructions is { Length: > MaxInstructionsLength })
        {
            return $"Instructions may be at most {MaxInstructionsLength} characters.";
        }

        if (ValidateTools(manifest) is { } toolError) return toolError;
        if (ValidateSection(manifest.Prompts, "prompt", MaxPrompts, p => p.Name, ValidatePrompt) is { } promptError) return promptError;
        if (ValidateSection(manifest.Skills, "skill", MaxSkills, s => s.Name, ValidateSkill) is { } skillError) return skillError;

        var size = MeasureBytes(manifest);

        return size > MaxManifestBytes
            ? $"This manifest is {size / 1024} KB. The limit is {MaxManifestBytes / 1024} KB, which is "
              + "as much as a vault item can be relied on to carry."
            : null;
    }

    private static string? ValidateVersion(McpApiBridgeManifest manifest)
    {
        if (manifest.Version > McpApiBridgeManifest.CurrentVersion)
        {
            return $"This manifest declares version {manifest.Version}, and this build understands "
                   + $"version {McpApiBridgeManifest.CurrentVersion}. Serving it anyway would mean "
                   + "guessing which HTTP calls it meant.";
        }

        return manifest.Version < 1 ? "Manifest version must be 1 or higher." : null;
    }

    private static string? ValidateTools(McpApiBridgeManifest manifest)
    {
        if (manifest.Tools.Count == 0)
        {
            return "A manifest needs at least one tool.";
        }

        return ValidateSection(manifest.Tools, "tool", MaxTools, t => t.Name, ValidateTool);
    }

    /// <summary>
    /// The three list-shaped sections of a manifest are checked identically — a ceiling, unique
    /// names, then each entry — so they share one pass rather than three near-copies that would
    /// drift in their wording.
    /// </summary>
    private static string? ValidateSection<T>(
        List<T> entries,
        string noun,
        int maximum,
        Func<T, string> name,
        Func<T, string?> validate)
    {
        if (entries.Count > maximum)
        {
            return $"A manifest may declare at most {maximum} {noun}s. This one declares {entries.Count}.";
        }

        // Case-insensitively, even though MCP names are case-sensitive: two entries differing only
        // in case is a trap for a model, not a feature.
        if (FirstDuplicate(entries.Select(name)) is { } duplicate)
        {
            return $"Two {noun}s are both named '{duplicate}'. {char.ToUpperInvariant(noun[0])}{noun[1..]} names must be unique.";
        }

        foreach (var entry in entries)
        {
            if (validate(entry) is { } error)
            {
                return $"{char.ToUpperInvariant(noun[0])}{noun[1..]} '{name(entry)}': {error}";
            }
        }

        return null;
    }

    // ---- tools -----------------------------------------------------------------------------

    public static string? ValidateTool(McpApiBridgeTool tool)
    {
        if (ValidateMcpName(tool.Name, "Tool name") is { } nameError) return nameError;

        if (tool.Description is { Length: > MaxDescriptionLength })
        {
            return $"description may be at most {MaxDescriptionLength} characters.";
        }

        // Undefined is what a tool with no "inputSchema" key deserializes to, and it is normalized
        // to the empty object schema rather than refused — declaring no arguments is a real thing
        // for a tool that takes none.
        if (tool.InputSchema.ValueKind != JsonValueKind.Undefined
            && !McpApiBridgeSchema.IsUsableToolSchema(tool.InputSchema))
        {
            return "inputSchema must be a JSON Schema object with \"type\": \"object\". MCP requires "
                   + "that shape, and a client is entitled to refuse anything else.";
        }

        if (tool.Request is not null && tool.HasVariants)
        {
            return "declares both 'request' and 'variants'. Use one: a single request, or several "
                   + "the model picks between.";
        }

        return tool.HasVariants ? ValidateVariants(tool) : ValidateSingleRequest(tool);
    }

    private static string? ValidateSingleRequest(McpApiBridgeTool tool)
    {
        if (tool.Request is null)
        {
            return "has no 'request'. A tool needs either one request or a set of variants.";
        }

        if (!string.IsNullOrWhiteSpace(tool.VariantBy))
        {
            return "sets 'variantBy' but declares no variants.";
        }

        return ValidateRequest(tool.Request, "Tool path");
    }

    private static string? ValidateVariants(McpApiBridgeTool tool)
    {
        if (tool.Variants.Count < 2)
        {
            return "declares one variant. A tool with a single variant is a tool with a pointless "
                   + "argument — give it a plain 'request' instead.";
        }

        if (tool.Variants.Count > MaxVariantsPerTool)
        {
            return $"declares {tool.Variants.Count} variants. The limit is {MaxVariantsPerTool}.";
        }

        if (string.IsNullOrWhiteSpace(tool.VariantBy))
        {
            return "declares variants but no 'variantBy'. Name the argument the model sets to "
                   + "choose between them.";
        }

        var selector = tool.VariantBy.Trim();

        if (!selector.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
        {
            return "'variantBy' may only contain letters, digits, and underscores.";
        }

        if (!McpApiBridgeSchema.DeclaresProperty(tool.InputSchema, selector))
        {
            return $"'variantBy' names '{selector}', which inputSchema does not declare. The model "
                   + "can only choose a variant it has been told about.";
        }

        var advertised = McpApiBridgeSchema.ReadEnumValues(tool.InputSchema, selector);

        if (advertised is null)
        {
            return $"inputSchema declares '{selector}' without a string \"enum\". That enum is how "
                   + "the model learns which variants exist.";
        }

        if (MatchVariantsToEnum(tool, selector, advertised) is { } mismatch) return mismatch;

        foreach (var (key, request) in tool.Variants)
        {
            if (ValidateRequest(request, $"Variant '{key}' path") is { } error)
            {
                return $"variant '{key}': {error}";
            }
        }

        return null;
    }

    /// <summary>
    /// The enum and the variant keys have to name the same set, in both directions. An extra enum
    /// value is a choice that always fails; an extra variant is a call nothing can reach.
    /// </summary>
    private static string? MatchVariantsToEnum(McpApiBridgeTool tool, string selector, List<string> advertised)
    {
        if (tool.Variants.Keys.Any(string.IsNullOrWhiteSpace))
        {
            return "has a variant with an empty name.";
        }

        var unadvertised = tool.Variants.Keys.FirstOrDefault(key => !advertised.Contains(key, StringComparer.Ordinal));

        if (unadvertised is not null)
        {
            return $"has a variant '{unadvertised}' that the enum on '{selector}' does not list, so "
                   + "nothing would ever call it.";
        }

        var unimplemented = advertised.FirstOrDefault(value => !tool.Variants.ContainsKey(value));

        return unimplemented is null
            ? null
            : $"the enum on '{selector}' offers '{unimplemented}', but no variant implements it, "
              + "so choosing it would always fail.";
    }

    // ---- one request template ----------------------------------------------------------------

    public static string? ValidateRequest(McpApiBridgeRequest request, string pathLabel)
    {
        if (!PermittedMethods.Contains(request.Method?.Trim().ToUpperInvariant(), StringComparer.Ordinal))
        {
            return $"method '{request.Method}' is not one of {string.Join(", ", PermittedMethods)}.";
        }

        if (ValidatePathTemplate(request.Path, pathLabel) is { } pathError) return pathError;
        if (ValidateQuery(request.Query) is { } queryError) return queryError;
        if (ValidateHeaders(request.Headers) is { } headerError) return headerError;
        if (ValidateBody(request) is { } bodyError) return bodyError;

        if (request.Description is { Length: > MaxDescriptionLength })
        {
            return $"description may be at most {MaxDescriptionLength} characters.";
        }

        return null;
    }

    /// <summary>
    /// The escape surface, so it is a deny-list of everything structural.
    ///
    /// A bridge is confined to one route, and the path is the only part of a call the manifest
    /// author controls that could leave it. Absolute and protocol-relative URLs would land the
    /// request on another host; '..' would climb out of the route prefix; and a percent-escape
    /// would survive this check only to be decoded by the listener afterwards, which is how a
    /// literal "%2f" becomes a path separator that the '..' test never saw.
    /// </summary>
    public static string? ValidatePathTemplate(string? path, string what = "Tool path")
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return $"{what} is required.";
        }

        var value = path.Trim();

        if (!value.StartsWith('/'))
        {
            return $"{what} must start with '/' — it is relative to the route's path prefix.";
        }

        if (value.StartsWith("//", StringComparison.Ordinal) || value.Contains("://", StringComparison.Ordinal))
        {
            return $"{what} may not be an absolute or protocol-relative URL. Every tool of a bridge "
                   + "goes through its one route, so the path is relative to that route's prefix.";
        }

        if (value.Split('/').Any(segment => segment == ".."))
        {
            return $"{what} may not contain '..' segments.";
        }

        if (value.Contains('%'))
        {
            return $"{what} may not contain '%'. Write literal characters — argument values are "
                   + "URL-escaped for you.";
        }

        if (value.IndexOfAny(['?', '#', '\\']) >= 0)
        {
            return $"{what} may not contain '?', '#' or '\\' — use the query map for parameters.";
        }

        if (value.Any(char.IsWhiteSpace))
        {
            return $"{what} may not contain spaces.";
        }

        return McpApiBridgePlaceholders.Validate(value, what);
    }

    public static string? ValidateQuery(IReadOnlyDictionary<string, string> query)
    {
        if (query.Keys.Any(string.IsNullOrWhiteSpace))
        {
            return "has a query parameter with no name.";
        }

        foreach (var (name, value) in query)
        {

            // Escaped on the way out, so these would still arrive intact — but a key containing
            // '&' reads to its author as two parameters, and that is a bug nobody finds by looking.
            if (name.IndexOfAny(['&', '=', '#', '?']) >= 0)
            {
                return $"query parameter '{name}' contains one of & = # ?, which read as structure "
                       + "rather than as part of the name.";
            }

            if (name.Any(char.IsControl))
            {
                return $"query parameter '{name}' contains control characters.";
            }

            if (McpApiBridgePlaceholders.Validate(value, $"Query parameter '{name}'") is { } error)
            {
                return error;
            }
        }

        return null;
    }

    public static string? ValidateHeaders(IReadOnlyDictionary<string, string> headers)
    {
        if (headers.Keys.Any(string.IsNullOrWhiteSpace))
        {
            return "has a header with no name.";
        }

        foreach (var (name, value) in headers)
        {

            var trimmed = name.Trim();

            if (!RouteValidation.IsValidHeaderName(trimmed))
            {
                return $"header '{trimmed}' is not a valid HTTP header name.";
            }

            if (RouteValidation.IsReservedHeaderName(trimmed))
            {
                return $"header '{trimmed}' is set by the proxy itself and cannot be written by a manifest.";
            }

            if (BridgeReservedHeaders.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
            {
                return $"header '{trimmed}' is owned by this proxy's own authentication and cannot "
                       + "be written by a manifest. The route's credential is attached for you.";
            }

            // A CR or LF here would end the header line and let the rest be read as further
            // headers — request splitting, aimed at the upstream. Checked again at expansion,
            // because the value can come from a call argument.
            if (value.Any(char.IsControl))
            {
                return $"header '{trimmed}' has a value containing control characters.";
            }

            if (McpApiBridgePlaceholders.Validate(value, $"Header '{trimmed}'") is { } error)
            {
                return error;
            }
        }

        return null;
    }

    public static string? ValidateBody(McpApiBridgeRequest request)
    {
        var method = request.Method?.Trim().ToUpperInvariant() ?? "";
        var sendsBody = request.BodyMode != McpApiBridgeBodyMode.None;

        if (sendsBody && (method == "GET" || method == "HEAD"))
        {
            return $"sends a body with {method}, which has no defined meaning and which many "
                   + "upstreams reject outright.";
        }

        if (request.BodyMode == McpApiBridgeBodyMode.Template)
        {
            if (request.Body is not { } body)
            {
                return "has bodyMode 'template' but no 'body'.";
            }

            if (body.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
            {
                return "has a 'body' that is not a JSON object or array.";
            }

            if (ValidateBodyPlaceholders(body) is { } error) return error;
        }
        else if (request.Body is not null)
        {
            return $"has a 'body' but bodyMode is '{request.BodyMode}'. Set bodyMode to 'template' "
                   + "to send it.";
        }

        if (sendsBody && string.IsNullOrWhiteSpace(request.ContentType))
        {
            return "has an empty contentType.";
        }

        if (request.ContentType.Any(char.IsControl))
        {
            return "has a contentType containing control characters.";
        }

        return null;
    }

    private static string? ValidateBodyPlaceholders(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return McpApiBridgePlaceholders.Validate(element.GetString(), "Body value");

            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (ValidateBodyPlaceholders(property.Value) is { } error) return error;
                }

                return null;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (ValidateBodyPlaceholders(item) is { } error) return error;
                }

                return null;

            default:
                return null;
        }
    }

    // ---- prompts and skills ------------------------------------------------------------------

    public static string? ValidatePrompt(McpApiBridgePrompt prompt)
    {
        if (ValidateMcpName(prompt.Name, "Prompt name") is { } nameError) return nameError;
        if (ValidatePromptArguments(prompt) is { } argumentError) return argumentError;

        if (prompt.Messages.Count == 0)
        {
            return "has no messages.";
        }

        var declared = prompt.Arguments.Select(a => a.Name).ToHashSet(StringComparer.Ordinal);

        foreach (var message in prompt.Messages)
        {
            if (ValidatePromptMessage(message, declared) is { } error) return error;
        }

        return null;
    }

    private static string? ValidatePromptArguments(McpApiBridgePrompt prompt)
    {
        if (FirstDuplicate(prompt.Arguments.Select(a => a.Name)) is { } duplicate)
        {
            return $"declares the argument '{duplicate}' twice.";
        }

        if (prompt.Arguments.Any(a => string.IsNullOrWhiteSpace(a.Name)))
        {
            return "has an argument with no name.";
        }

        var malformed = prompt.Arguments
            .FirstOrDefault(a => !a.Name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'));

        return malformed is null
            ? null
            : $"argument '{malformed.Name}' may only contain letters, digits, and underscores.";
    }

    private static string? ValidatePromptMessage(McpApiBridgePromptMessage message, HashSet<string> declared)
    {
        if (message.Role is not ("user" or "assistant"))
        {
            return $"has a message with role '{message.Role}'. Use 'user' or 'assistant'.";
        }

        if (string.IsNullOrWhiteSpace(message.Content))
        {
            return "has a message with no content.";
        }

        if (message.Content.Length > MaxDescriptionLength * 4)
        {
            return "has a message longer than a prompt should be. Put long guidance in a skill.";
        }

        if (McpApiBridgePlaceholders.Validate(message.Content, "Prompt message") is { } error)
        {
            return error;
        }

        var undeclared = McpApiBridgePlaceholders.Names(message.Content)
            .FirstOrDefault(name => !declared.Contains(name));

        return undeclared is null
            ? null
            : $"a message uses '{{{undeclared}}}', which is not one of its declared arguments.";
    }

    public static string? ValidateSkill(McpApiBridgeSkill skill)
    {
        if (string.IsNullOrWhiteSpace(skill.Name))
        {
            return "name is required.";
        }

        var trimmed = skill.Name.Trim();

        if (trimmed.Length > 64)
        {
            return "name may be at most 64 characters.";
        }

        // The name becomes a URI path segment, so it is restricted the way a slug is rather than
        // the way an MCP tool name is.
        if (!trimmed.All(c => char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_'))
        {
            return "name may only contain letters, digits, hyphens, and underscores — it becomes "
                   + "part of the resource URI.";
        }

        return string.IsNullOrWhiteSpace(skill.Content) ? "has no content." : null;
    }

    // ---- size --------------------------------------------------------------------------------

    /// <summary>The manifest's stored size, measured on the indented form actually written.</summary>
    public static int MeasureBytes(McpApiBridgeManifest manifest) =>
        Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(manifest, VaultRedaction.NoteOptions));

    // ---- shared ------------------------------------------------------------------------------

    /// <summary>
    /// The MCP name rules, applied to tools and prompts alike. Both are prefixed with a source
    /// alias when a funnel pools this bridge, so both carry the same length ceiling.
    /// </summary>
    private static string? ValidateMcpName(string? name, string what)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return $"{what} is required.";
        }

        var trimmed = name.Trim();

        if (trimmed.Length > MaxToolNameLength)
        {
            return $"{what} may be at most {MaxToolNameLength} characters, so that it still fits "
                   + "when a funnel prefixes it with a source alias.";
        }

        return trimmed.All(c => char.IsAsciiLetterOrDigit(c) || c == '_' || c == '-')
            ? null
            : $"{what} may only contain letters, digits, underscores, and hyphens.";
    }

    private static string? FirstDuplicate(IEnumerable<string> names)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        return names.FirstOrDefault(name => !seen.Add(name ?? ""));
    }
}
