using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using RavensPort.Core.Models;

namespace RavensPort.Core.Mcp;

/// <summary>
/// Turns one tool call into one HTTP request. Pure: no I/O, no config, no clock — everything it
/// needs is the tool and the arguments, which is what makes the whole templating matrix testable
/// without a host.
///
/// Argument rules, in one sentence: absent means omit, except in the path, where absent means
/// refuse. Optionality is therefore declared once, in the schema's <c>required</c> list, and the
/// template never invents a null or an empty segment. A path with a hole in it is not a path.
/// </summary>
public static class McpApiBridgeRequestBuilder
{
    /// <summary>What to send, relative to the route's base address.</summary>
    public sealed record BuiltRequest(
        HttpMethod Method,
        string RelativePath,
        string Query,
        IReadOnlyList<KeyValuePair<string, string>> Headers,
        string? Body,
        string ContentType)
    {
        public string PathAndQuery => RelativePath + Query;
    }

    /// <summary>
    /// Either <see cref="BuiltRequest"/> or an <c>Error</c>, never both.
    ///
    /// An argument problem is a tool <em>result</em> with IsError rather than an exception: MCP
    /// reserves protocol errors for "there is no such tool", and a model that gets one back
    /// learns nothing it can act on. A sentence naming the missing argument is something it can.
    /// </summary>
    public static (BuiltRequest? Request, string? Error) Build(
        McpApiBridgeTool tool,
        IDictionary<string, JsonElement>? arguments)
    {
        // The SDK hands arguments over as a mutable dictionary; everything below only reads, and
        // says so.
        IReadOnlyDictionary<string, JsonElement> supplied = arguments is null
            ? new Dictionary<string, JsonElement>()
            : arguments.AsReadOnly();

        var (request, selectorError) = SelectRequest(tool, supplied);
        if (selectorError is not null) return (null, selectorError);

        // The selector named a variant; it is a routing decision, not a parameter, so it does not
        // travel on to the upstream.
        var consumed = new HashSet<string>(StringComparer.Ordinal);
        if (tool.HasVariants && tool.VariantBy is { } selector) consumed.Add(selector);

        var (path, pathError) = ExpandPath(request!.Path, supplied, consumed);
        if (pathError is not null) return (null, pathError);

        var (query, queryError) = ExpandQuery(request.Query, supplied, consumed);
        if (queryError is not null) return (null, queryError);

        var (headers, headerError) = ExpandHeaders(request.Headers, supplied, consumed);
        if (headerError is not null) return (null, headerError);

        var (body, bodyError) = BuildBody(request, supplied, consumed);
        if (bodyError is not null) return (null, bodyError);

        var method = HttpMethod.Parse(request.Method.Trim().ToUpperInvariant());

        return (new BuiltRequest(method, path, query, headers, body, request.ContentType), null);
    }

    // ---- variant selection --------------------------------------------------------------------

    private static (McpApiBridgeRequest? Request, string? Error) SelectRequest(
        McpApiBridgeTool tool,
        IReadOnlyDictionary<string, JsonElement> arguments)
    {
        if (!tool.HasVariants)
        {
            return tool.Request is { } single
                ? (single, null)
                : (null, $"Tool '{tool.Name}' declares no request. Its manifest is incomplete.");
        }

        var selector = tool.VariantBy ?? "";
        var options = string.Join(", ", tool.Variants.Keys.OrderBy(k => k, StringComparer.Ordinal));

        if (!arguments.TryGetValue(selector, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return (null, $"'{selector}' is required and decides which request this tool makes. "
                          + $"Valid values: {options}.");
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            return (null, $"'{selector}' must be one of: {options}.");
        }

        var chosen = value.GetString() ?? "";

        // Ordinal, matching the enum in the schema: the model was given exact strings to choose
        // between, and quietly accepting a different casing would make the manifest's own
        // uniqueness rule ambiguous.
        return tool.Variants.TryGetValue(chosen, out var request)
            ? (request, null)
            : (null, $"'{chosen}' is not a value this tool accepts for '{selector}'. Valid values: {options}.");
    }

    // ---- path -----------------------------------------------------------------------------------

    private static (string Path, string? Error) ExpandPath(
        string template,
        IReadOnlyDictionary<string, JsonElement> arguments,
        HashSet<string> consumed)
    {
        var builder = new StringBuilder();

        foreach (var segment in McpApiBridgePlaceholders.Parse(template))
        {
            if (!segment.IsPlaceholder)
            {
                builder.Append(segment.Text);
                continue;
            }

            if (!arguments.TryGetValue(segment.Text, out var value) || value.ValueKind == JsonValueKind.Null)
            {
                return ("", $"'{segment.Text}' is required — it is part of the path this tool calls.");
            }

            if (!TryReadScalar(value, out var scalar))
            {
                return ("", $"'{segment.Text}' must be a single value, not an object or a list.");
            }

            // Escaped per placeholder rather than over the whole path, so the template's own
            // slashes stay structural while an argument's do not. This is what keeps a value of
            // "../secrets" inside the route it was called through.
            builder.Append(Uri.EscapeDataString(scalar));
            consumed.Add(segment.Text);
        }

        var expanded = builder.ToString();

        return expanded.StartsWith('/') ? (expanded, null) : ("/" + expanded, null);
    }

    // ---- query ----------------------------------------------------------------------------------

    private static (string Query, string? Error) ExpandQuery(
        Dictionary<string, string> template,
        IReadOnlyDictionary<string, JsonElement> arguments,
        HashSet<string> consumed)
    {
        var query = new QueryString();

        foreach (var (name, valueTemplate) in template)
        {
            var (value, omit, error) = ExpandValue(valueTemplate, arguments, consumed, name);

            if (error is not null) return ("", error);

            // Omitted rather than sent empty. "?q=" is a filter matching nothing on some APIs and
            // everything on others, and neither is what "the model did not supply one" means.
            if (omit) continue;

            query = query.Add(name, value);
        }

        return (query.ToString() ?? "", null);
    }

    // ---- headers --------------------------------------------------------------------------------

    private static (List<KeyValuePair<string, string>> Headers, string? Error) ExpandHeaders(
        Dictionary<string, string> template,
        IReadOnlyDictionary<string, JsonElement> arguments,
        HashSet<string> consumed)
    {
        var headers = new List<KeyValuePair<string, string>>();

        foreach (var (name, valueTemplate) in template)
        {
            var (value, omit, error) = ExpandValue(valueTemplate, arguments, consumed, name);

            if (error is not null) return ([], error);
            if (omit) continue;

            // Checked again here, not only at import: this value can come from a call argument,
            // and a CR or LF in it would end the header line and let the rest be read as further
            // headers by the upstream.
            if (value.Any(char.IsControl))
            {
                return ([], $"'{name}' cannot carry a value containing control characters.");
            }

            headers.Add(new KeyValuePair<string, string>(name, value));
        }

        return (headers, null);
    }

    // ---- body -----------------------------------------------------------------------------------

    private static (string? Body, string? Error) BuildBody(
        McpApiBridgeRequest request,
        IReadOnlyDictionary<string, JsonElement> arguments,
        HashSet<string> consumed)
    {
        switch (request.BodyMode)
        {
            case McpApiBridgeBodyMode.None:
                return (null, null);

            case McpApiBridgeBodyMode.Arguments:
            {
                // Only what the path, query, headers and selector did not already carry. Sending
                // everything would put {"id": 5} in the body of PUT /items/5, which the author has
                // already said belongs in the path.
                var remaining = arguments.Where(pair => !consumed.Contains(pair.Key));

                using var stream = new MemoryStream();
                using (var writer = new Utf8JsonWriter(stream))
                {
                    writer.WriteStartObject();

                    foreach (var (name, value) in remaining)
                    {
                        writer.WritePropertyName(name);
                        value.WriteTo(writer);
                    }

                    writer.WriteEndObject();
                }

                return (Encoding.UTF8.GetString(stream.ToArray()), null);
            }

            case McpApiBridgeBodyMode.Template:
            {
                if (request.Body is not { } template)
                {
                    return (null, "This tool declares a template body but carries none.");
                }

                using var stream = new MemoryStream();
                using (var writer = new Utf8JsonWriter(stream))
                {
                    if (WriteTemplate(template, arguments, consumed, writer) is { } error) return (null, error);
                }

                return (Encoding.UTF8.GetString(stream.ToArray()), null);
            }

            default:
                return (null, null);
        }
    }

    /// <summary>
    /// Copies a body template, substituting placeholders.
    ///
    /// A string that is <em>exactly</em> one placeholder is replaced by the argument's whole JSON
    /// value, so a list or an object can be sent as itself; a string that merely contains one is
    /// interpolated as text. That distinction is what lets a template say <c>"tags": "{tags}"</c>
    /// and have an array arrive, without a second syntax for it.
    ///
    /// A property whose value resolves to a missing argument is dropped, not written as null — the
    /// same "absent means omit" rule as everywhere else.
    /// </summary>
    private static string? WriteTemplate(
        JsonElement element,
        IReadOnlyDictionary<string, JsonElement> arguments,
        HashSet<string> consumed,
        Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();

                foreach (var property in element.EnumerateObject())
                {
                    if (IsOmitted(property.Value, arguments)) continue;

                    writer.WritePropertyName(property.Name);

                    if (WriteTemplate(property.Value, arguments, consumed, writer) is { } error) return error;
                }

                writer.WriteEndObject();
                return null;

            case JsonValueKind.Array:
                writer.WriteStartArray();

                foreach (var item in element.EnumerateArray())
                {
                    // An omitted element would shift every index after it, so inside an array a
                    // missing argument is written as null rather than dropped.
                    if (IsOmitted(item, arguments))
                    {
                        writer.WriteNullValue();
                        continue;
                    }

                    if (WriteTemplate(item, arguments, consumed, writer) is { } error) return error;
                }

                writer.WriteEndArray();
                return null;

            case JsonValueKind.String:
                return WriteString(element.GetString() ?? "", arguments, consumed, writer);

            default:
                element.WriteTo(writer);
                return null;
        }
    }

    private static string? WriteString(
        string template,
        IReadOnlyDictionary<string, JsonElement> arguments,
        HashSet<string> consumed,
        Utf8JsonWriter writer)
    {
        if (McpApiBridgePlaceholders.IsWholeValue(template, out var whole))
        {
            if (!arguments.TryGetValue(whole, out var value))
            {
                writer.WriteNullValue();
                return null;
            }

            consumed.Add(whole);
            value.WriteTo(writer);
            return null;
        }

        var builder = new StringBuilder();

        foreach (var segment in McpApiBridgePlaceholders.Parse(template))
        {
            if (!segment.IsPlaceholder)
            {
                builder.Append(segment.Text);
                continue;
            }

            if (!arguments.TryGetValue(segment.Text, out var value)) continue;

            if (!TryReadScalar(value, out var scalar))
            {
                return $"'{segment.Text}' must be a single value to be used inside text, not an "
                       + "object or a list.";
            }

            builder.Append(scalar);
            consumed.Add(segment.Text);
        }

        writer.WriteStringValue(builder.ToString());
        return null;
    }

    /// <summary>
    /// Whether a template value stands for exactly one argument that was not supplied — the one
    /// case where a property disappears rather than being written.
    /// </summary>
    private static bool IsOmitted(JsonElement template, IReadOnlyDictionary<string, JsonElement> arguments) =>
        template.ValueKind == JsonValueKind.String
        && McpApiBridgePlaceholders.IsWholeValue(template.GetString(), out var name)
        && (!arguments.TryGetValue(name, out var value) || value.ValueKind == JsonValueKind.Null);

    // ---- shared ----------------------------------------------------------------------------------

    /// <summary>
    /// Expands a query or header value. <c>Omit</c> means every placeholder in it resolved to a
    /// missing argument, so the whole parameter is left out.
    /// </summary>
    private static (string Value, bool Omit, string? Error) ExpandValue(
        string template,
        IReadOnlyDictionary<string, JsonElement> arguments,
        HashSet<string> consumed,
        string slotName)
    {
        var builder = new StringBuilder();
        var sawPlaceholder = false;
        var filledOne = false;

        foreach (var segment in McpApiBridgePlaceholders.Parse(template))
        {
            if (!segment.IsPlaceholder)
            {
                builder.Append(segment.Text);
                continue;
            }

            sawPlaceholder = true;

            if (!arguments.TryGetValue(segment.Text, out var value) || value.ValueKind == JsonValueKind.Null)
            {
                continue;
            }

            if (!TryReadScalar(value, out var scalar))
            {
                return ("", false, $"'{segment.Text}' must be a single value, not an object or a list, "
                                   + $"to be sent as '{slotName}'.");
            }

            builder.Append(scalar);
            consumed.Add(segment.Text);
            filledOne = true;
        }

        return (builder.ToString(), sawPlaceholder && !filledOne, null);
    }

    /// <summary>
    /// A JSON value as the text an HTTP request can carry. Objects and arrays have no single
    /// obvious rendering in a path segment or a header, so they are refused rather than serialized
    /// into one.
    /// </summary>
    private static bool TryReadScalar(JsonElement value, out string scalar)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                scalar = value.GetString() ?? "";
                return true;

            case JsonValueKind.Number:
                scalar = value.GetRawText();
                return true;

            case JsonValueKind.True:
                scalar = "true";
                return true;

            case JsonValueKind.False:
                scalar = "false";
                return true;

            default:
                scalar = "";
                return false;
        }
    }
}
