using System.Text.Json.Serialization;

namespace RavensPort.Core.Models;

/// <summary>What answers step 5's yes/no questions.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DecisionModelKind
{
    /// <summary>
    /// The Jev wire protocol (POST /v1/systemone, "noul" questions): TypeSafe's hosted Jev, or a
    /// compatible server such as Laya's own laya-serve running on this PC.
    /// </summary>
    JevApi,

    /// <summary>A Laya checkpoint exported to ONNX, run inside RavensPort.</summary>
    LayaOnnx,

    /// <summary>
    /// Any zero-shot (NLI) or cross-encoder model exported to ONNX with a tokenizer.json, run inside
    /// RavensPort: the call is the premise, the statement the hypothesis.
    /// </summary>
    ZeroShotOnnx,
}

/// <summary>A model step 5 (Decision) can ask whether a statement holds for a call.</summary>
public sealed class DecisionModelRecord
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public required string Name { get; set; }

    public DecisionModelKind Kind { get; set; }

    /// <summary>For an API: its base, up to and including /v1.</summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>For an API: the model name it expects, e.g. jev-latest.</summary>
    public string Model { get; set; } = "";

    /// <summary>For an API: a credential whose secret is sent as the bearer token. Optional for local servers.</summary>
    public Guid? CredentialId { get; set; }

    /// <summary>For ONNX: the model folder, or its .onnx file.</summary>
    public string ModelPath { get; set; } = "";

    public OnnxDevice Device { get; set; } = OnnxDevice.Cpu;

    public int GpuIndex { get; set; }

    [JsonIgnore]
    public bool IsOnnx => Kind != DecisionModelKind.JevApi;

    /// <summary>True when the call's text leaves this PC: an API whose host is not loopback.</summary>
    [JsonIgnore]
    public bool IsRemote =>
        !IsOnnx && !(Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) && uri.IsLoopback);

    public const string JevBaseUrl = "https://api.typesafe.ai/v1";
    public const string LayaServeBaseUrl = "http://127.0.0.1:8000/v1";

    public static string Describe(DecisionModelKind kind) => kind switch
    {
        DecisionModelKind.JevApi => "Jev API (TypeSafe Jev, or laya-serve)",
        DecisionModelKind.LayaOnnx => "Laya, ONNX in RavensPort (local)",
        DecisionModelKind.ZeroShotOnnx => "Zero-shot / cross-encoder, ONNX in RavensPort (local)",
        _ => kind.ToString(),
    };
}
