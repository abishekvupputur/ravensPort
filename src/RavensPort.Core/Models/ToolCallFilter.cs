using System.Text.Json.Serialization;

namespace RavensPort.Core.Models;

/// <summary>
/// The four steps a funnel's calls pass through, in this order. A Deny in one step stops the call
/// there; what a step matched is replaced with a placeholder before the next one sees it, so the
/// meaning step — which may send text to an API — never receives a secret or personal data that
/// an earlier step found.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FilterCategory
{
    // Stored by name; the declaration order is the order the steps run in.

    /// <summary>Step 1: credentials of every kind, by the gitleaks rule set. Off or Deny, nothing in between.</summary>
    Secrets,

    /// <summary>Step 2: personal data by pattern — editable built-ins plus the user's own.</summary>
    PersonalData,

    /// <summary>Step 3: personal data by an entity model (GLiNER) looking for labels the user chooses.</summary>
    Entities,

    /// <summary>Step 4: calls that mean something the user denied, judged by an embedding model.</summary>
    RiskyActions,

    /// <summary>
    /// Step 5: statements the user wrote ("the call deletes data"), each answered by a decision model
    /// with the probability it holds for the call; a probability at or over the threshold blocks.
    /// </summary>
    Decision,
}

/// <summary>
/// How a step runs. Secrets and Personal data use only Off and on (any other value); Entities and
/// Meaning read Relaxed/Balanced/Strict as a score threshold preset.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FilterLevel
{
    Off,
    Relaxed,
    Balanced,
    Strict,
}

/// <summary>What happens when a step matches.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FilterAction
{
    /// <summary>Logged only. The call goes through, so a step can be tried before it is trusted.</summary>
    Watch,

    /// <summary>The call is refused with a message naming the step, and never reaches the source.</summary>
    Block,
}

/// <summary>A user-defined pattern in the Personal data step.</summary>
public sealed class CustomPattern
{
    public required string Name { get; set; }
    public required string Regex { get; set; }
}

/// <summary>
/// One step's setting on one funnel. Only what differs from the defaults is stored — the vault
/// note that carries this has a size ceiling (see VaultRedaction.NoteOptions).
/// </summary>
public sealed class FunnelFilter
{
    public FilterCategory Category { get; set; }
    public FilterLevel Level { get; set; } = FilterLevel.Off;
    public FilterAction Action { get; set; } = FilterAction.Block;

    // ---- personal data (patterns) ----------------------------------------------------------

    /// <summary>Built-in patterns switched off, by name.</summary>
    public List<string> DisabledDetectors { get; set; } = [];

    /// <summary>Built-in patterns that are off by default and were switched on, by name.</summary>
    public List<string> EnabledDetectors { get; set; } = [];

    /// <summary>Built-in patterns whose regex the user edited: name → regex. Their checksum still applies.</summary>
    public Dictionary<string, string> RegexOverrides { get; set; } = [];

    /// <summary>The user's own patterns.</summary>
    public List<CustomPattern> Patterns { get; set; } = [];

    // ---- entities (GLiNER) -----------------------------------------------------------------

    /// <summary>Which entity model looks for <see cref="EntityLabels"/>.</summary>
    public Guid? EntityModelId { get; set; }

    /// <summary>What to look for, in plain words: "person", "address", "medical condition".</summary>
    public List<string> EntityLabels { get; set; } = [];

    // ---- meaning (embeddings) --------------------------------------------------------------

    /// <summary>Which embedding model judges this funnel's calls.</summary>
    public Guid? EmbeddingProviderId { get; set; }

    /// <summary>What to deny, in the user's words, one per entry: "deletes files or databases".</summary>
    public List<string> DenyInstructions { get; set; } = [];

    /// <summary>Example texts that must be denied.</summary>
    public List<string> DenyExamples { get; set; } = [];

    /// <summary>Texts that look similar but are fine; a text closer to one of these is let through.</summary>
    public List<string> AllowExamples { get; set; } = [];

    // ---- decision (step 5) — its statements are kept in DenyInstructions ----------------------

    /// <summary>Which decision model answers this step's statements.</summary>
    public Guid? DecisionModelId { get; set; }

    /// <summary>A score the user set by hand (Entities, Meaning and Decision). Null uses the level's preset.</summary>
    public double? Threshold { get; set; }
}

/// <summary>Where an ONNX model runs.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OnnxDevice
{
    Cpu,

    /// <summary>A DirectX 12 GPU through DirectML (Windows). Falls back to the CPU if it cannot start.</summary>
    Gpu,
}

/// <summary>An entity model (GLiNER, exported to ONNX) the Entities step can use. Runs in this process.</summary>
public sealed class EntityModelRecord
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public required string Name { get; set; }

    /// <summary>The .onnx file, or a folder holding it; spm.model and gliner_config.json sit beside it.</summary>
    public string ModelPath { get; set; } = "";

    public OnnxDevice Device { get; set; } = OnnxDevice.Cpu;

    /// <summary>The DXGI adapter index when <see cref="Device"/> is GPU — what Windows lists the GPU as.</summary>
    public int GpuIndex { get; set; }
}
