using System.Text.Json;
using System.Text.Json.Serialization;

namespace RavensPort.Core.Models;

/// <summary>
/// Writes and reads enum values in the camelCase form the manifest documents. The rest of the
/// store serializes enums in their declared casing, but a manifest is written by hand against a
/// published shape, and "bodyMode": "Template" in the note while the sample says "template" is
/// the kind of difference someone loses an afternoon to. Reading stays case-insensitive either
/// way, so both forms are accepted.
/// </summary>
public sealed class CamelCaseEnumConverter : JsonStringEnumConverter
{
    public CamelCaseEnumConverter() : base(JsonNamingPolicy.CamelCase) { }
}

/// <summary>Where a tool's arguments go once the request is built.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter))]
public enum McpApiBridgeBodyMode
{
    /// <summary>No request body.</summary>
    None,

    /// <summary>
    /// Whatever arguments the path, query, headers and the variant selector did not consume, as
    /// one JSON object. Consuming rather than forwarding everything is deliberate: sending
    /// {"id": 5} in the body of PUT /items/5 is noise the upstream may well reject, and the
    /// author has already said where that argument goes.
    /// </summary>
    Arguments,

    /// <summary>The <see cref="McpApiBridgeRequest.Body"/> template, with placeholders substituted.</summary>
    Template,
}

/// <summary>
/// One uploaded API description: the tools an agent sees, plus the guidance it needs to use them.
///
/// Everything here is authored by the user and none of it is secret, which is why it lives in the
/// vault's topology note rather than in an item of its own. Property names are pinned with
/// <see cref="JsonPropertyNameAttribute"/> so the shape in the note is the same shape the user
/// uploaded — the note is meant to be readable, and hand-editable, in the password manager.
/// </summary>
public sealed class McpApiBridgeManifest
{
    public const int CurrentVersion = 1;

    /// <summary>
    /// Refused rather than ignored when higher than <see cref="CurrentVersion"/>. A manifest this
    /// build cannot honour must not be served as though it could: the difference between versions
    /// is which HTTP call gets made, and guessing at that is worse than declining to serve.
    /// </summary>
    [JsonPropertyName("version")]
    public int Version { get; set; } = CurrentVersion;

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>
    /// Becomes the server's instructions in the initialize result — how to use this API well:
    /// which tool to reach for first, how ids and pagination behave, what the limits are. The
    /// cheapest of the three guidance forms and the one every MCP client already shows.
    /// </summary>
    [JsonPropertyName("instructions")]
    public string? Instructions { get; set; }

    [JsonPropertyName("tools")]
    public List<McpApiBridgeTool> Tools { get; set; } = [];

    [JsonPropertyName("prompts")]
    public List<McpApiBridgePrompt> Prompts { get; set; } = [];

    [JsonPropertyName("skills")]
    public List<McpApiBridgeSkill> Skills { get; set; } = [];
}

/// <summary>
/// One tool. Either a single <see cref="Request"/>, or a set of <see cref="Variants"/> the model
/// picks between — never both.
/// </summary>
public sealed class McpApiBridgeTool
{
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>
    /// Raw JSON Schema, handed to the agent untouched. Nothing validates arguments against it:
    /// there is no validator here and adding a dependency for one was ruled out. It is
    /// advertisement to the model, and the only enforcement on the call path is that a
    /// placeholder the template needs is present and scalar. See <see cref="McpApiBridgeSchema"/>
    /// for why this is a JsonElement and what has to be done about its default value.
    /// </summary>
    [JsonPropertyName("inputSchema")]
    public JsonElement InputSchema { get; set; }

    /// <summary>Becomes the tool's read-only hint. Advisory: nothing here enforces it.</summary>
    [JsonPropertyName("readOnly")]
    public bool ReadOnly { get; set; }

    /// <summary>Set when this tool has one request. Null when <see cref="Variants"/> is used.</summary>
    [JsonPropertyName("request")]
    public McpApiBridgeRequest? Request { get; set; }

    /// <summary>
    /// The argument the model sets to choose a variant. It must be declared in
    /// <see cref="InputSchema"/> as a string whose enum lists exactly the variant keys — that
    /// enum is the whole mechanism by which the model learns its choices, so a variant the schema
    /// does not advertise would never be called, and an advertised value with no variant behind
    /// it would always fail.
    /// </summary>
    [JsonPropertyName("variantBy")]
    public string? VariantBy { get; set; }

    /// <summary>
    /// Several near-identical endpoints presented as one tool. Collapsing them is the point: an
    /// agent choosing between one tool's three values does better than the same agent choosing
    /// between three tools whose descriptions differ by a word.
    /// </summary>
    [JsonPropertyName("variants")]
    public Dictionary<string, McpApiBridgeRequest> Variants { get; set; } = [];

    /// <summary>Whether this tool selects between variants rather than holding one request.</summary>
    [JsonIgnore]
    public bool HasVariants => Variants.Count > 0;
}

/// <summary>One HTTP request template, relative to the bridge's route.</summary>
public sealed class McpApiBridgeRequest
{
    /// <summary>
    /// Shown to the model as part of the tool description when this is a variant. A bare enum of
    /// "inbox | sent | drafts" tells it the shape of the choice but nothing about the meaning.
    /// </summary>
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("method")]
    public string Method { get; set; } = "GET";

    /// <summary>Relative to the route's path prefix. May contain {placeholders}.</summary>
    [JsonPropertyName("path")]
    public string Path { get; set; } = "/";

    /// <summary>Parameter name to a literal or {placeholder} value.</summary>
    [JsonPropertyName("query")]
    public Dictionary<string, string> Query { get; set; } = [];

    /// <summary>
    /// Header name to a literal or {placeholder} value. The headers this pipeline owns — the
    /// forward's own, and every one authorization rests on — are refused by validation.
    /// </summary>
    [JsonPropertyName("headers")]
    public Dictionary<string, string> Headers { get; set; } = [];

    [JsonPropertyName("bodyMode")]
    public McpApiBridgeBodyMode BodyMode { get; set; } = McpApiBridgeBodyMode.None;

    /// <summary>The template when <see cref="BodyMode"/> is Template. Null otherwise.</summary>
    [JsonPropertyName("body")]
    public JsonElement? Body { get; set; }

    [JsonPropertyName("contentType")]
    public string ContentType { get; set; } = "application/json";
}

/// <summary>
/// One MCP prompt, answered from the manifest with no HTTP call. Served through the ordinary
/// prompts/list and prompts/get, so a funnel pooling this bridge filters and re-prefixes them
/// with the code it already has.
/// </summary>
public sealed class McpApiBridgePrompt
{
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("arguments")]
    public List<McpApiBridgePromptArgument> Arguments { get; set; } = [];

    [JsonPropertyName("messages")]
    public List<McpApiBridgePromptMessage> Messages { get; set; } = [];
}

public sealed class McpApiBridgePromptArgument
{
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("required")]
    public bool Required { get; set; }
}

public sealed class McpApiBridgePromptMessage
{
    /// <summary>"user" or "assistant". Anything else is refused at import.</summary>
    [JsonPropertyName("role")]
    public string Role { get; set; } = "user";

    /// <summary>Text, with {placeholders} drawn from the prompt's declared arguments.</summary>
    [JsonPropertyName("content")]
    public string Content { get; set; } = "";
}

/// <summary>
/// A skill document — how to use this API to get something done, rather than what one endpoint
/// takes. Served as a markdown resource so any client that reads resources can pull it in.
/// </summary>
public sealed class McpApiBridgeSkill
{
    /// <summary>Becomes a URI path segment, so it is restricted the way a slug is.</summary>
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>Markdown. Counts against the manifest byte cap — see McpApiBridgeValidation.</summary>
    [JsonPropertyName("content")]
    public string Content { get; set; } = "";
}
