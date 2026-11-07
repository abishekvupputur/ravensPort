using System.Text.Json.Serialization;

namespace RavensPort.Core.Models;

/// <summary>Where an embedding model runs.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EmbeddingProviderKind
{
    /// <summary>Ollama's OpenAI-compatible endpoint. Local.</summary>
    Ollama,

    /// <summary>llama.cpp's server (started with --embeddings), LM Studio, or any OpenAI-compatible server.</summary>
    OpenAiCompatible,

    OpenAI,

    VoyageAI,

    /// <summary>An ONNX sentence-embedding model run inside RavensPort. Nothing leaves the PC.</summary>
    Onnx,
}

/// <summary>
/// One embedding model the meaning filter can use. Funnels pick one by id; the secret, when the
/// API needs one, is an ordinary credential from the Credentials tab and stays in the vault.
/// </summary>
public sealed class EmbeddingProviderRecord
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public required string Name { get; set; }

    public EmbeddingProviderKind Kind { get; set; }

    /// <summary>The API's base, up to and including /v1. Unused for <see cref="EmbeddingProviderKind.Onnx"/>.</summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>The model name the API expects. Unused for ONNX, where the folder is the model.</summary>
    public string Model { get; set; } = "";

    /// <summary>A credential whose secret is sent as the bearer token. Optional for local servers.</summary>
    public Guid? CredentialId { get; set; }

    /// <summary>For ONNX: the .onnx file, or a folder holding it; vocab.txt sits beside the model. See OnnxModelFiles.</summary>
    public string ModelFolder { get; set; } = "";

    /// <summary>For ONNX: where it runs.</summary>
    public OnnxDevice Device { get; set; } = OnnxDevice.Cpu;

    /// <summary>For ONNX on a GPU: the DXGI adapter index Windows lists it as.</summary>
    public int GpuIndex { get; set; }

    /// <summary>
    /// True when what is checked leaves this PC: any API whose host is not loopback. The UI
    /// warns on these, because the text of every checked call is sent to it.
    /// </summary>
    [JsonIgnore]
    public bool IsRemote =>
        Kind != EmbeddingProviderKind.Onnx &&
        !(Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) && uri.IsLoopback);

    public static string DefaultBaseUrl(EmbeddingProviderKind kind) => kind switch
    {
        EmbeddingProviderKind.Ollama => "http://127.0.0.1:11434/v1",
        EmbeddingProviderKind.OpenAiCompatible => "http://127.0.0.1:8080/v1",
        EmbeddingProviderKind.OpenAI => "https://api.openai.com/v1",
        EmbeddingProviderKind.VoyageAI => "https://api.voyageai.com/v1",
        _ => "",
    };

    /// <summary>Models to suggest for a kind. The user can type any other.</summary>
    public static IReadOnlyList<string> SuggestedModels(EmbeddingProviderKind kind) => kind switch
    {
        EmbeddingProviderKind.Ollama => ["nomic-embed-text", "mxbai-embed-large", "bge-m3", "snowflake-arctic-embed2"],
        EmbeddingProviderKind.OpenAI => ["text-embedding-3-small", "text-embedding-3-large"],
        EmbeddingProviderKind.VoyageAI => ["voyage-4-lite", "voyage-4", "voyage-4-large"],
        _ => [],
    };

    public static string Describe(EmbeddingProviderKind kind) => kind switch
    {
        EmbeddingProviderKind.Ollama => "Ollama (local)",
        EmbeddingProviderKind.OpenAiCompatible => "llama.cpp / OpenAI-compatible server",
        EmbeddingProviderKind.OpenAI => "OpenAI",
        EmbeddingProviderKind.VoyageAI => "Voyage AI",
        EmbeddingProviderKind.Onnx => "ONNX model in RavensPort (local)",
        _ => kind.ToString(),
    };
}
