using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;
using RavensPort.Core.Auth;
using RavensPort.Core.Diagnostics;
using RavensPort.Core.Models;

namespace RavensPort.Core.Filters;

/// <summary>Whether a text is the thing being judged or one of the references it is judged against.</summary>
public enum EmbeddingRole
{
    Query,
    Document,
}

/// <summary>Turns texts into unit-length vectors. One instance per provider configuration.</summary>
public interface IEmbeddingClient
{
    Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, EmbeddingRole role, CancellationToken ct);
}

/// <summary>
/// OpenAI's /embeddings request shape, which Ollama, llama.cpp's server, LM Studio and Voyage AI
/// all accept. Voyage additionally takes input_type, which tunes the vector for its role.
/// </summary>
internal sealed class HttpEmbeddingClient(
    HttpClient http, EmbeddingProviderRecord provider, Func<CancellationToken, ValueTask<string?>> getKey) : IEmbeddingClient
{
    public async Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, EmbeddingRole role, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = provider.Model,
            ["input"] = new JsonArray([.. texts.Select(t => (JsonNode)t)]),
        };

        if (provider.Kind == EmbeddingProviderKind.VoyageAI)
        {
            body["input_type"] = role == EmbeddingRole.Query ? "query" : "document";
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, provider.BaseUrl.TrimEnd('/') + "/embeddings")
        {
            Content = JsonContent.Create(body),
        };

        if (provider.CredentialId is not null)
        {
            var key = await getKey(ct).ConfigureAwait(false)
                      ?? throw new InvalidOperationException("its credential has no key — reconnect it on the Credentials tab");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }

        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            // Status only: an API's error body can echo the input, which is the call being checked.
            throw new InvalidOperationException($"the embedding API answered HTTP {(int)response.StatusCode}");
        }

        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct)
            .ConfigureAwait(false);

        var vectors = json.RootElement.GetProperty("data").EnumerateArray()
            .OrderBy(d => d.TryGetProperty("index", out var i) ? i.GetInt32() : 0)
            .Select(d => Normalize([.. d.GetProperty("embedding").EnumerateArray().Select(v => v.GetSingle())]))
            .ToArray();

        if (vectors.Length != texts.Count) throw new InvalidOperationException("the embedding API returned the wrong number of vectors");

        return vectors;
    }

    internal static float[] Normalize(float[] v)
    {
        var norm = MathF.Sqrt(v.Sum(x => x * x));
        if (norm > 0) for (var i = 0; i < v.Length; i++) v[i] /= norm;
        return v;
    }
}

/// <summary>Where an ONNX model's files are, from a path the user gave: the .onnx itself, or a folder.</summary>
public sealed record OnnxModelFiles(string Model, string Vocab, string Folder)
{
    /// <summary>
    /// Resolves <paramref name="path"/>, or says what is missing in words a user can act on.
    /// A folder is searched for model.onnx (also under onnx/, where Hugging Face repos keep it);
    /// a file is taken as the model, with its tokenizer expected beside it.
    /// </summary>
    public static OnnxModelFiles? Find(string? path, out string? error)
    {
        error = null;
        path = path?.Trim().Trim('"');

        if (string.IsNullOrEmpty(path))
        {
            error = "Enter the path of the .onnx file, or of the folder holding it.";
            return null;
        }

        string? model;
        string folder;

        if (File.Exists(path))
        {
            if (!path.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase))
            {
                error = "That file is not a .onnx model.";
                return null;
            }
            model = path;
            folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        }
        else if (Directory.Exists(path))
        {
            folder = path;
            model = new[] { "model.onnx", Path.Combine("onnx", "model.onnx") }
                .Select(p => Path.Combine(path, p))
                .FirstOrDefault(File.Exists)
                ?? Directory.EnumerateFiles(path, "*.onnx").FirstOrDefault();

            if (model is null)
            {
                error = "No .onnx file in that folder (or its onnx/ folder).";
                return null;
            }
        }
        else
        {
            error = "Nothing exists at that path.";
            return null;
        }

        // The tokenizer is not inside an ONNX file: it ships beside the model on Hugging Face.
        var vocab = Path.Combine(folder, "vocab.txt");
        if (!File.Exists(vocab))
        {
            error = $"No vocab.txt next to the model in {folder}. An .onnx file holds only the network: download vocab.txt " +
                    "(and tokenizer_config.json) from the model's Hugging Face page into that folder. Only BERT-style " +
                    "tokenizers (vocab.txt) are supported: MiniLM, bge, e5, gte, stella-en-400M.";
            return null;
        }

        return new OnnxModelFiles(model, vocab, folder);
    }
}

/// <summary>
/// A sentence-embedding model exported to ONNX, run in this process. Supports BERT-style
/// tokenizers (vocab.txt): MiniLM, bge, e5, gte, stella-en-400M. Takes either output shape: a
/// pooled [batch, dim] embedding (used as is), or per-token [batch, tokens, dim] states pooled
/// mean or CLS as the model's 1_Pooling/config.json says.
/// </summary>
// ponytail: BERT-style tokenizers only. Models with a BPE/SentencePiece tokenizer (Qwen-based,
// e.g. stella-en-1.5B) need Microsoft.ML.Tokenizers' BPE loaders wired in here.
internal sealed class OnnxEmbeddingClient : IEmbeddingClient, IDisposable
{
    private const int MaxTokens = 512;

    /// <summary>Output names that are already one vector per text, preferred over per-token states.</summary>
    private static readonly string[] PooledOutputs = ["sentence_embedding", "embedding", "embeddings", "pooler_output"];

    private readonly InferenceSession _session;
    private readonly BertTokenizer _tokenizer;
    private readonly bool _clsPooling;
    private readonly bool _wantsTokenTypes;
    private readonly string _output;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public string UsedDevice { get; }

    public OnnxEmbeddingClient(string path, OnnxDevice device = OnnxDevice.Cpu, int gpuIndex = 0, string name = "ONNX model", ActivityLog? log = null)
    {
        var files = OnnxModelFiles.Find(path, out var error) ?? throw new InvalidOperationException(error);

        _tokenizer = BertTokenizer.Create(files.Vocab, new BertOptions { LowerCaseBeforeTokenization = ReadsLowerCase(files.Folder) });
        _session = OnnxSessions.Open(files.Model, device, gpuIndex, name, log, out var used);
        UsedDevice = used;
        _wantsTokenTypes = _session.InputMetadata.ContainsKey("token_type_ids");
        _clsPooling = ReadsClsPooling(files.Folder);
        _output = PooledOutputs.FirstOrDefault(_session.OutputMetadata.ContainsKey) ?? _session.OutputMetadata.Keys.First();
    }

    /// <summary>tokenizer_config.json's do_lower_case; uncased (true) when there is none, as most BERT models are.</summary>
    internal static bool ReadsLowerCase(string folder)
    {
        var path = Path.Combine(folder, "tokenizer_config.json");
        if (!File.Exists(path)) return true;

        using var json = JsonDocument.Parse(File.ReadAllText(path));
        return !json.RootElement.TryGetProperty("do_lower_case", out var lower) || lower.ValueKind != JsonValueKind.False;
    }

    internal static bool ReadsClsPooling(string folder)
    {
        var path = Path.Combine(folder, "1_Pooling", "config.json");
        if (!File.Exists(path)) return false;

        using var json = JsonDocument.Parse(File.ReadAllText(path));
        return json.RootElement.TryGetProperty("pooling_mode_cls_token", out var cls) && cls.ValueKind == JsonValueKind.True;
    }

    public async Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, EmbeddingRole role, CancellationToken ct)
    {
        // One inference at a time: the session is thread-safe, but parallel runs only fight over
        // the same cores, and this keeps memory flat.
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return [.. texts.Select(Embed)];
        }
        finally
        {
            _gate.Release();
        }
    }

    private float[] Embed(string text)
    {
        var ids = _tokenizer.EncodeToIds(text, MaxTokens, out _, out _).Select(i => (long)i).ToArray();
        var length = ids.Length;

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(ids, [1, length])),
            NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(Enumerable.Repeat(1L, length).ToArray(), [1, length])),
        };

        if (_wantsTokenTypes)
        {
            inputs.Add(NamedOnnxValue.CreateFromTensor("token_type_ids", new DenseTensor<long>(new long[length], [1, length])));
        }

        using var results = _session.Run(inputs, [_output]);
        return Pool(results.First().AsTensor<float>(), length, _clsPooling);
    }

    /// <summary>
    /// The model's output as one unit vector: a pooled [1, dim] output as it is, per-token
    /// [1, tokens, dim] states by their mean or by the first ([CLS]) token.
    /// </summary>
    internal static float[] Pool(Tensor<float> hidden, int length, bool clsPooling)
    {
        if (hidden.Dimensions.Length == 2) return HttpEmbeddingClient.Normalize([.. hidden]);

        var dim = hidden.Dimensions[2];
        var pooled = new float[dim];

        if (clsPooling)
        {
            for (var d = 0; d < dim; d++) pooled[d] = hidden[0, 0, d];
        }
        else
        {
            for (var t = 0; t < length; t++)
            for (var d = 0; d < dim; d++)
                pooled[d] += hidden[0, t, d] / length;
        }

        return HttpEmbeddingClient.Normalize(pooled);
    }

    public void Dispose()
    {
        _session.Dispose();
        _gate.Dispose();
    }
}

/// <summary>
/// Hands out the models the filters use — embedding clients and entity models — one per
/// configuration, rebuilding one when its record is edited. ONNX sessions are expensive to open,
/// so they are kept for as long as their configuration is.
/// </summary>
// ponytail: ONNX sessions stay loaded until the record changes or the app exits; add an idle
// unload if memory on small machines becomes a complaint (stella-400M ~1.7 GB, GLiNER ~1.2 GB).
public sealed class EmbeddingClientFactory(AccessTokenProvider tokens, ActivityLog? log = null) : IDisposable
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private readonly Dictionary<Guid, (string Signature, IDisposable? Owned, object Model)> _models = [];
    private readonly Lock _lock = new();

    /// <summary>Tests swap the real clients out here, so no model or API is needed to run them.</summary>
    internal Func<EmbeddingProviderRecord, IEmbeddingClient>? Override { get; set; }

    public IEmbeddingClient Get(EmbeddingProviderRecord provider)
    {
        if (Override is { } replace) return replace(provider);

        var signature = $"{provider.Kind}|{provider.BaseUrl}|{provider.Model}|{provider.CredentialId}|{provider.ModelFolder}|{provider.Device}|{provider.GpuIndex}";

        return (IEmbeddingClient)GetOrCreate(provider.Id, signature, () => provider.Kind == EmbeddingProviderKind.Onnx
            ? new OnnxEmbeddingClient(provider.ModelFolder, provider.Device, provider.GpuIndex, provider.Name, log)
            : new HttpEmbeddingClient(Http, provider, ct => provider.CredentialId is { } id
                ? tokens.GetAccessTokenAsync(id, ct)
                : ValueTask.FromResult<string?>(null)));
    }

    /// <summary>Tests swap entity models out here.</summary>
    internal Func<EntityModelRecord, Func<string, IReadOnlyList<string>, double, CancellationToken, Task<List<Entity>>>>? EntityOverride { get; set; }

    /// <summary>The entity model for a record, as a function, so tests can replace it.</summary>
    public Func<string, IReadOnlyList<string>, double, CancellationToken, Task<List<Entity>>> GetEntityModel(EntityModelRecord record)
    {
        if (EntityOverride is { } replace) return replace(record);

        var model = (GlinerModel)GetOrCreate(record.Id, $"{record.ModelPath}|{record.Device}|{record.GpuIndex}", () => new GlinerModel(record, log));
        return model.FindAsync;
    }

    /// <summary>Tests swap decision models out here.</summary>
    internal Func<DecisionModelRecord, DecideAsync>? DecisionOverride { get; set; }

    /// <summary>The decision model for a record (step 5): an API client, or an ONNX model loaded once.</summary>
    public DecideAsync GetDecisionModel(DecisionModelRecord record)
    {
        if (DecisionOverride is { } replace) return replace(record);

        if (record.Kind == DecisionModelKind.JevApi)
        {
            return new JevDecisionClient(Http, record, ct => record.CredentialId is { } id
                ? tokens.GetAccessTokenAsync(id, ct)
                : ValueTask.FromResult<string?>(null)).DecideAsync;
        }

        var model = GetOrCreate(record.Id, $"{record.Kind}|{record.ModelPath}|{record.Device}|{record.GpuIndex}", () => record.Kind == DecisionModelKind.LayaOnnx
            ? new LayaDecisionModel(record, log)
            : new ZeroShotDecisionModel(record, log));
        return model is LayaDecisionModel laya ? laya.DecideAsync : ((ZeroShotDecisionModel)model).DecideAsync;
    }

    /// <summary>Where an ONNX model ended up running, once loaded: for the Test buttons.</summary>
    public string? DeviceOf(Guid id)
    {
        lock (_lock)
        {
            return _models.TryGetValue(id, out var m) ? m.Model switch
            {
                OnnxEmbeddingClient o => o.UsedDevice,
                GlinerModel g => g.UsedDevice,
                LayaDecisionModel l => l.UsedDevice,
                ZeroShotDecisionModel z => z.UsedDevice,
                _ => null,
            } : null;
        }
    }

    private object GetOrCreate(Guid id, string signature, Func<object> create)
    {
        lock (_lock)
        {
            if (_models.TryGetValue(id, out var cached) && cached.Signature == signature) return cached.Model;

            cached.Owned?.Dispose();

            var model = create();
            _models[id] = (signature, model as IDisposable, model);
            return model;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var (_, owned, _) in _models.Values) owned?.Dispose();
            _models.Clear();
        }
    }
}
