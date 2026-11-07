using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using RavensPort.Core.Diagnostics;
using RavensPort.Core.Models;

namespace RavensPort.Core.Filters;

/// <summary>
/// Step 5's question to a model: how likely each statement holds for this text, 0 to 1.
/// One probability per statement, in order.
/// </summary>
public delegate Task<double[]> DecideAsync(string state, IReadOnlyList<string> statements, CancellationToken ct);

/// <summary>
/// The Jev wire protocol: POST {base}/systemone with the state and one "noul" (yes/no) question per
/// statement; each answer's "noul" is P(true). TypeSafe's hosted Jev and Laya's laya-serve both speak it.
/// </summary>
internal sealed class JevDecisionClient(HttpClient http, DecisionModelRecord record, Func<CancellationToken, ValueTask<string?>> getKey)
{
    public async Task<double[]> DecideAsync(string state, IReadOnlyList<string> statements, CancellationToken ct)
    {
        var questions = new JsonObject();
        for (var i = 0; i < statements.Count; i++)
        {
            questions[$"q{i}"] = new JsonObject { ["type"] = "noul", ["instructions"] = statements[i] };
        }

        var body = new JsonObject { ["state"] = state, ["questions"] = questions };
        if (record.Model.Length > 0) body["model"] = record.Model;

        using var request = new HttpRequestMessage(HttpMethod.Post, record.BaseUrl.TrimEnd('/') + "/systemone")
        {
            Content = JsonContent.Create(body),
        };

        if (record.CredentialId is not null)
        {
            var key = await getKey(ct).ConfigureAwait(false)
                      ?? throw new InvalidOperationException("its credential has no key — reconnect it on the Credentials tab");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }

        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            // Status only: an error body can echo the state, which is the call being checked.
            throw new InvalidOperationException($"the decision API answered HTTP {(int)response.StatusCode}");
        }

        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct)
            .ConfigureAwait(false);
        var answers = json.RootElement.GetProperty("answers");

        return [.. statements.Select((_, i) => answers.GetProperty($"q{i}").GetProperty("noul").GetDouble())];
    }
}

/// <summary>
/// A Hugging Face tokenizer.json, run by the same Rust library Python uses (Tokenizers.DotNet), so any
/// model's tokenizer works as it was trained. Encodes without special tokens; callers add them.
/// </summary>
internal sealed class HfTokens : IDisposable
{
    private readonly Tokenizers.DotNet.Tokenizer _inner;
    private readonly Lock _lock = new();

    public HfTokens(string tokenizerJson)
    {
        Json = JsonNode.Parse(File.ReadAllText(tokenizerJson))!.AsObject();
        IdOf = Json["added_tokens"]?.AsArray().ToDictionary(t => (string)t!["content"]!, t => (int)t!["id"]!) ?? [];

        // A copy without post-processing, truncation or padding: the plain token ids of a text.
        var stripped = Json.DeepClone().AsObject();
        stripped["post_processor"] = null;
        stripped["truncation"] = null;
        stripped["padding"] = null;
        var text = stripped.ToJsonString();

        // Derived data, named by its content: the temp folder is the right home.
        var cache = Path.Combine(Path.GetTempPath(), "RavensPort", "tokenizers");
        Directory.CreateDirectory(cache);
        var path = Path.Combine(cache, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16] + ".json");
        if (!File.Exists(path)) File.WriteAllText(path, text);

        _inner = new Tokenizers.DotNet.Tokenizer(vocabPath: path);
    }

    public JsonObject Json { get; }

    /// <summary>Special and added tokens by their text.</summary>
    public Dictionary<string, int> IdOf { get; }

    public int[] Encode(string text)
    {
        lock (_lock)
        {
            return Array.ConvertAll(_inner.Encode(text), t => (int)t);
        }
    }

    public void Dispose() => _inner.Dispose();
}

/// <summary>Builders and maths for the ONNX decision models, kept free of sessions so they can be tested.</summary>
internal static class DecisionInputs
{
    /// <summary>Laya's question type id for noul, fed to its type embedding.</summary>
    public const int NoulType = 2;

    /// <summary>The two options of a noul question as Laya renders them: index 1 is "true".</summary>
    public static readonly string[] NoulOptions = ["false: no, the statement does not hold", "true: yes, the statement holds"];

    /// <summary>
    /// Laya's sequence for one noul question (laya/common.py build_sequence):
    /// [CLS] noul question: statement [SEP] [MASK] false… [MASK] true… [SEP] state [SEP].
    /// The model scores each option at its [MASK], so every offset matters.
    /// </summary>
    public static (int[] Ids, int[] Markers) LayaSequence(
        Func<string, int[]> encode, string maskToken, int cls, int sep, int mask,
        string statement, string state, int maxLen, int headMaxLen)
    {
        string Scrub(string s) => s.Replace(maskToken, " ", StringComparison.Ordinal);

        var head = encode($"noul question: {Scrub(statement)}").ToList();
        var options = NoulOptions.Select(o => new[] { mask }.Concat(encode(" " + o).Take(48)).ToList()).ToList();

        var budget = headMaxLen - options.Sum(o => o.Count);
        if (budget < 16)
        {
            var per = Math.Max(4, (headMaxLen - 16) / options.Count);
            options = [.. options.Select(o => o.Take(per).ToList())];
            budget = headMaxLen - options.Sum(o => o.Count);
        }

        var ids = new List<int> { cls };
        ids.AddRange(head.Take(Math.Max(8, budget)));
        ids.Add(sep);

        var markers = new List<int>();
        foreach (var option in options)
        {
            markers.Add(ids.Count);
            ids.AddRange(option);
        }
        ids.Add(sep);

        var room = Math.Max(0, maxLen - ids.Count - 1);
        ids.AddRange(encode(Scrub(state)).Take(room));
        ids.Add(sep);

        if (ids.Count > maxLen) ids = ids[..maxLen];
        markers.RemoveAll(m => m >= maxLen);
        if (markers.Count != 2) throw new InvalidOperationException("the statement is too long for the model's question budget");

        return ([.. ids], [.. markers]);
    }

    /// <summary>
    /// A text pair as the tokenizer's own post-processor joins it ([CLS] a [SEP] b [SEP] for BERT and
    /// DeBERTa, &lt;s&gt; a &lt;/s&gt;&lt;/s&gt; b &lt;/s&gt; for RoBERTa), with token type ids.
    /// The first text is shortened to fit <paramref name="maxLen"/>.
    /// </summary>
    public static (int[] Ids, int[] Types) Pair(JsonObject tokenizerJson, int[] a, int[] b, int maxLen)
    {
        var processor = tokenizerJson["post_processor"] as JsonObject
                        ?? throw new InvalidOperationException("tokenizer.json has no post_processor, so how to join two texts is unknown");

        if ((string?)processor["type"] == "Sequence")
        {
            processor = processor["processors"]!.AsArray().OfType<JsonObject>()
                            .FirstOrDefault(p => (string?)p["type"] is "TemplateProcessing" or "RobertaProcessing" or "BertProcessing")
                        ?? throw new InvalidOperationException("tokenizer.json's post_processor does not say how to join two texts");
        }

        // Pieces: a special token's ids, or a sequence (A or B), each with a type id.
        var pieces = new List<(int[]? Special, char? Seq, int Type)>();
        switch ((string?)processor["type"])
        {
            case "TemplateProcessing":
                foreach (var item in processor["pair"]!.AsArray().OfType<JsonObject>())
                {
                    if (item["SpecialToken"] is JsonObject special)
                    {
                        var ids = processor["special_tokens"]![(string)special["id"]!]!["ids"]!.AsArray().Select(i => (int)i!).ToArray();
                        pieces.Add((ids, null, (int?)special["type_id"] ?? 0));
                    }
                    else if (item["Sequence"] is JsonObject seq)
                    {
                        pieces.Add((null, ((string)seq["id"]!)[0], (int?)seq["type_id"] ?? 0));
                    }
                }
                break;

            case "RobertaProcessing" or "BertProcessing":
                var cls = (int)processor["cls"]![1]!;
                var sep = (int)processor["sep"]![1]!;
                var roberta = (string?)processor["type"] == "RobertaProcessing";
                pieces.AddRange([([cls], null, 0), (null, 'A', 0), ([sep], null, 0)]);
                if (roberta) pieces.Add(([sep], null, 0));
                pieces.AddRange([(null, 'B', roberta ? 0 : 1), ([sep], null, roberta ? 0 : 1)]);
                break;

            default:
                throw new InvalidOperationException($"tokenizer.json's post_processor {(string?)processor["type"]} is not supported");
        }

        var fixedLength = pieces.Sum(p => p.Special?.Length ?? 0) + b.Length;
        var roomForA = maxLen - fixedLength;
        if (roomForA < 1) throw new InvalidOperationException("the statement is too long for the model");
        if (a.Length > roomForA) a = a[..roomForA];

        var outIds = new List<int>();
        var outTypes = new List<int>();
        foreach (var (special, seq, type) in pieces)
        {
            var part = special ?? (seq == 'A' ? a : b);
            outIds.AddRange(part);
            outTypes.AddRange(Enumerable.Repeat(type, part.Length));
        }

        return ([.. outIds], [.. outTypes]);
    }

    /// <summary>
    /// P(the statement holds) from a classifier's logits: entailment against contradiction for an NLI
    /// model (as Hugging Face's multi-label zero-shot pipeline does), entailment against the rest for a
    /// two-way one, a sigmoid for a cross-encoder's single score, and label 1 for an unnamed two-way head.
    /// </summary>
    public static double Probability(ReadOnlySpan<float> logits, IReadOnlyDictionary<int, string> labels)
    {
        if (logits.Length == 1) return 1 / (1 + Math.Exp(-logits[0]));

        // "not_entailment" names the other side, so a label with "not" in it never counts.
        int Find(string word)
        {
            foreach (var (index, label) in labels)
            {
                if (label.Contains(word, StringComparison.OrdinalIgnoreCase) && !label.Contains("not", StringComparison.OrdinalIgnoreCase)) return index;
            }
            return -1;
        }

        var entail = Find("entail");
        var contra = Find("contradict");

        if (entail >= 0 && contra >= 0) return Softmax([logits[contra], logits[entail]])[1];
        if (entail >= 0) return Softmax(logits)[entail];
        if (logits.Length == 2) return Softmax(logits)[1];

        throw new InvalidOperationException("the model's labels name no 'entailment', so which output means yes is unknown");
    }

    public static double[] Softmax(ReadOnlySpan<float> logits, double temperature = 1)
    {
        var p = new double[logits.Length];
        var max = double.NegativeInfinity;
        for (var i = 0; i < p.Length; i++) max = Math.Max(max, logits[i] / temperature);

        var sum = 0.0;
        for (var i = 0; i < p.Length; i++) sum += p[i] = Math.Exp(logits[i] / temperature - max);
        for (var i = 0; i < p.Length; i++) p[i] /= sum;
        return p;
    }

    /// <summary>The tensor for an integer input, in whichever of int64/int32 the graph declares.</summary>
    public static NamedOnnxValue Ints(InferenceSession session, string name, long[] data, int[] shape) =>
        session.InputMetadata[name].ElementType == typeof(int)
            ? NamedOnnxValue.CreateFromTensor(name, new DenseTensor<int>(Array.ConvertAll(data, x => (int)x), shape))
            : NamedOnnxValue.CreateFromTensor(name, new DenseTensor<long>(data, shape));
}

/// <summary>
/// A Laya checkpoint exported to ONNX (laya-dotnet/tools/export_onnx.py or laya-ts): model.onnx, or
/// encoder.onnx + head.onnx, with rl_agent_config.json and tokenizer.json. Only noul questions are
/// asked; their answer is the tempered softmax at the "true" marker, as Laya computes it.
/// </summary>
internal sealed class LayaDecisionModel : IDisposable
{
    private readonly InferenceSession? _fused, _encoder, _head;
    private readonly HfTokens _tokens;
    private readonly int _maxLen, _headMaxLen, _cls, _sep, _mask, _pad;
    private readonly string _maskToken;
    private readonly double _temperature;

    public string UsedDevice { get; }

    public LayaDecisionModel(DecisionModelRecord record, ActivityLog? log)
    {
        var folder = Folder(record.ModelPath);

        var tokenizerJson = new[] { Path.Combine(folder, "tokenizer", "tokenizer.json"), Path.Combine(folder, "tokenizer.json") }.FirstOrDefault(File.Exists)
                            ?? throw new InvalidOperationException("no tokenizer.json (or tokenizer/tokenizer.json) beside the model");
        var configPath = Path.Combine(folder, "rl_agent_config.json");
        if (!File.Exists(configPath)) throw new InvalidOperationException("no rl_agent_config.json beside the model — is this a Laya export?");

        var config = JsonNode.Parse(File.ReadAllText(configPath))!.AsObject();
        _maxLen = (int?)config["max_len"] ?? 512;
        _headMaxLen = (int?)config["head_max_len"] ?? 192;
        var byOptions = config["temperature_by_options"] as JsonObject;
        var temperatures = config["temperature"] as JsonArray;
        var t = (double?)byOptions?["noul:2"] ?? (temperatures is { Count: > 2 } ? (double?)temperatures[2] : null) ?? 1.0;
        _temperature = double.IsFinite(t) ? Math.Clamp(t, 0.5, 5.0) : 1.0;

        _tokens = new HfTokens(tokenizerJson);
        var tokenizerConfig = Path.Combine(Path.GetDirectoryName(tokenizerJson)!, "tokenizer_config.json");
        var names = File.Exists(tokenizerConfig) ? JsonNode.Parse(File.ReadAllText(tokenizerConfig))!.AsObject() : [];

        int Special(string key, params string[] conventional)
        {
            var name = names[key] switch
            {
                JsonValue v => (string?)v,
                JsonObject o => (string?)o["content"],
                _ => null,
            };
            foreach (var candidate in name is null ? conventional : [name, .. conventional])
            {
                if (_tokens.IdOf.TryGetValue(candidate, out var id)) return id;
            }
            throw new InvalidOperationException($"the tokenizer has no {key}");
        }

        _cls = Special("cls_token", "[CLS]", "<bos>", "<s>");
        _sep = Special("sep_token", "[SEP]", "<eos>", "</s>");
        _mask = Special("mask_token", "[MASK]", "<mask>");
        _pad = Special("pad_token", "[PAD]", "<pad>");
        _maskToken = _tokens.IdOf.First(kv => kv.Value == _mask).Key;

        string used;
        if (File.Exists(Path.Combine(folder, "model.onnx")))
        {
            _fused = OnnxSessions.Open(Path.Combine(folder, "model.onnx"), record.Device, record.GpuIndex, record.Name, log, out used);
        }
        else if (File.Exists(Path.Combine(folder, "encoder.onnx")) && File.Exists(Path.Combine(folder, "head.onnx")))
        {
            _encoder = OnnxSessions.Open(Path.Combine(folder, "encoder.onnx"), record.Device, record.GpuIndex, record.Name, log, out used);
            _head = OnnxSessions.Open(Path.Combine(folder, "head.onnx"), record.Device, record.GpuIndex, record.Name, log, out _);
        }
        else
        {
            throw new InvalidOperationException("no model.onnx (or encoder.onnx + head.onnx) in the folder");
        }
        UsedDevice = used;
    }

    internal static string Folder(string path)
    {
        path = path.Trim().Trim('"');
        if (File.Exists(path)) return Path.GetDirectoryName(Path.GetFullPath(path))!;
        if (Directory.Exists(path)) return path;
        throw new InvalidOperationException("nothing exists at the model path");
    }

    public Task<double[]> DecideAsync(string state, IReadOnlyList<string> statements, CancellationToken ct) => Task.Run(() =>
    {
        var rows = statements.Select(s => DecisionInputs.LayaSequence(_tokens.Encode, _maskToken, _cls, _sep, _mask, s, state, _maxLen, _headMaxLen)).ToList();
        var n = rows.Count;
        var length = rows.Max(r => r.Ids.Length);

        var ids = new long[n * length];
        Array.Fill(ids, _pad);
        var attention = new long[n * length];
        var markerPos = new long[n * 2];
        var markerMask = new bool[n * 2];
        for (var i = 0; i < n; i++)
        {
            for (var j = 0; j < rows[i].Ids.Length; j++)
            {
                ids[i * length + j] = rows[i].Ids[j];
                attention[i * length + j] = 1;
            }
            for (var j = 0; j < 2; j++)
            {
                markerPos[i * 2 + j] = rows[i].Markers[j];
                markerMask[i * 2 + j] = true;
            }
        }

        ct.ThrowIfCancellationRequested();
        var logits = Run(ids, attention, markerPos, markerMask, n, length);
        var width = logits.Length / n;

        return statements.Select((_, i) => DecisionInputs.Softmax(logits.AsSpan(i * width, 2), _temperature)[1]).ToArray();
    }, ct);

    private float[] Run(long[] ids, long[] attention, long[] markerPos, bool[] markerMask, int n, int length)
    {
        var head = _fused ?? _head!;
        var inputs = new List<NamedOnnxValue>();

        if (_fused is null)
        {
            var encoderInputs = new List<NamedOnnxValue> { DecisionInputs.Ints(_encoder!, "input_ids", ids, [n, length]) };
            if (_encoder!.InputMetadata.ContainsKey("attention_mask")) encoderInputs.Add(DecisionInputs.Ints(_encoder, "attention_mask", attention, [n, length]));
            using var encoded = _encoder.Run(encoderInputs);
            var hidden = encoded.First().AsTensor<float>().ToDenseTensor();
            inputs.Add(NamedOnnxValue.CreateFromTensor("hidden_states", hidden));
        }
        else
        {
            inputs.Add(DecisionInputs.Ints(head, "input_ids", ids, [n, length]));
        }

        if (head.InputMetadata.ContainsKey("attention_mask")) inputs.Add(DecisionInputs.Ints(head, "attention_mask", attention, [n, length]));
        if (head.InputMetadata.ContainsKey("marker_pos")) inputs.Add(DecisionInputs.Ints(head, "marker_pos", markerPos, [n, 2]));
        if (head.InputMetadata.ContainsKey("marker_mask"))
        {
            inputs.Add(head.InputMetadata["marker_mask"].ElementType == typeof(float)
                ? NamedOnnxValue.CreateFromTensor("marker_mask", new DenseTensor<float>(Array.ConvertAll(markerMask, b => b ? 1f : 0f), [n, 2]))
                : NamedOnnxValue.CreateFromTensor("marker_mask", new DenseTensor<bool>(markerMask, [n, 2])));
        }
        if (head.InputMetadata.TryGetValue("qtype", out var qtype))
        {
            var types = Enumerable.Repeat((long)DecisionInputs.NoulType, n).ToArray();
            inputs.Add(DecisionInputs.Ints(head, "qtype", types, qtype.Dimensions.Length == 2 ? [n, 1] : [n]));
        }

        using var results = head.Run(inputs);
        return results.First(r => r.Name == "logits").AsTensor<float>().ToArray();
    }

    public void Dispose()
    {
        _fused?.Dispose();
        _encoder?.Dispose();
        _head?.Dispose();
        _tokens.Dispose();
    }
}

/// <summary>
/// Any Hugging Face sequence-pair classifier exported to ONNX — a zero-shot NLI model (DeBERTa,
/// RoBERTa, BART MNLI…) or a cross-encoder reranker: the call is the premise, each statement the
/// hypothesis, and P(entailment) — or the sigmoid of a reranker's score — is P(the statement holds).
/// Needs model.onnx (or onnx/model.onnx), tokenizer.json and, for an NLI model, config.json's id2label.
/// </summary>
internal sealed class ZeroShotDecisionModel : IDisposable
{
    private readonly InferenceSession _session;
    private readonly HfTokens _tokens;
    private readonly Dictionary<int, string> _labels;
    private readonly int _maxLen, _pad;

    public string UsedDevice { get; }

    public ZeroShotDecisionModel(DecisionModelRecord record, ActivityLog? log)
    {
        var path = record.ModelPath.Trim().Trim('"');
        var folder = LayaDecisionModel.Folder(path);
        var model = File.Exists(path) ? path
            : new[] { "model.onnx", Path.Combine("onnx", "model.onnx") }.Select(p => Path.Combine(folder, p)).FirstOrDefault(File.Exists)
              ?? Directory.EnumerateFiles(folder, "*.onnx").FirstOrDefault()
              ?? Directory.EnumerateFiles(Path.Combine(folder, "onnx"), "*.onnx").FirstOrDefault()
              ?? throw new InvalidOperationException("no .onnx file in the folder (or its onnx/ folder)");

        // Hugging Face repos keep the tokenizer and config in the repo root, above onnx/.
        var roots = new[] { Path.GetDirectoryName(Path.GetFullPath(model))!, folder, Path.GetDirectoryName(Path.GetFullPath(folder)) ?? folder };
        var tokenizerJson = roots.Select(r => Path.Combine(r, "tokenizer.json")).FirstOrDefault(File.Exists)
                            ?? throw new InvalidOperationException("no tokenizer.json beside the model");
        var configJson = roots.Select(r => Path.Combine(r, "config.json")).FirstOrDefault(File.Exists);

        _tokens = new HfTokens(tokenizerJson);
        _labels = [];
        _maxLen = 512;
        if (configJson is not null)
        {
            var config = JsonNode.Parse(File.ReadAllText(configJson))!.AsObject();
            if (config["id2label"] is JsonObject id2label)
            {
                foreach (var (key, value) in id2label) _labels[int.Parse(key, System.Globalization.CultureInfo.InvariantCulture)] = (string?)value ?? "";
            }
            if ((int?)config["max_position_embeddings"] is { } max and > 0) _maxLen = Math.Min(512, max);
        }

        _pad = (int?)_tokens.Json["padding"]?["pad_id"] ?? _tokens.IdOf.GetValueOrDefault("[PAD]", _tokens.IdOf.GetValueOrDefault("<pad>", 0));

        _session = OnnxSessions.Open(model, record.Device, record.GpuIndex, record.Name, log, out var used);
        UsedDevice = used;
    }

    public Task<double[]> DecideAsync(string state, IReadOnlyList<string> statements, CancellationToken ct) => Task.Run(() =>
    {
        var premise = _tokens.Encode(state);
        var rows = statements.Select(s => DecisionInputs.Pair(_tokens.Json, premise, _tokens.Encode(s), _maxLen)).ToList();
        var n = rows.Count;
        var length = rows.Max(r => r.Ids.Length);

        var ids = new long[n * length];
        Array.Fill(ids, _pad);
        var attention = new long[n * length];
        var types = new long[n * length];
        for (var i = 0; i < n; i++)
        {
            for (var j = 0; j < rows[i].Ids.Length; j++)
            {
                ids[i * length + j] = rows[i].Ids[j];
                attention[i * length + j] = 1;
                types[i * length + j] = rows[i].Types[j];
            }
        }

        ct.ThrowIfCancellationRequested();
        var inputs = new List<NamedOnnxValue> { DecisionInputs.Ints(_session, "input_ids", ids, [n, length]) };
        if (_session.InputMetadata.ContainsKey("attention_mask")) inputs.Add(DecisionInputs.Ints(_session, "attention_mask", attention, [n, length]));
        if (_session.InputMetadata.ContainsKey("token_type_ids")) inputs.Add(DecisionInputs.Ints(_session, "token_type_ids", types, [n, length]));

        using var results = _session.Run(inputs);
        var logits = results.First().AsTensor<float>().ToArray();
        var width = logits.Length / n;

        return statements.Select((_, i) => DecisionInputs.Probability(logits.AsSpan(i * width, width), _labels)).ToArray();
    }, ct);

    public void Dispose()
    {
        _session.Dispose();
        _tokens.Dispose();
    }
}
