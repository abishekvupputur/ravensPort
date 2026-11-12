using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;
using RavensPort.Core.Diagnostics;
using RavensPort.Core.Models;

namespace RavensPort.Core.Filters;

/// <summary>An entity the model found in a text.</summary>
public readonly record struct Entity(int Start, int Length, string Label, double Score);

/// <summary>Where a GLiNER model's files are, from a path the user gave: the .onnx itself, or a folder.</summary>
public sealed record GlinerFiles(string Model, string Folder)
{
    public static GlinerFiles? Find(string? path, out string? error)
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
            // Hugging Face repos keep the model in onnx/ and the tokenizer one level up.
            if (!File.Exists(Path.Combine(folder, "spm.model")) && Path.GetFileName(folder) == "onnx")
                folder = Path.GetDirectoryName(folder)!;
        }
        else if (Directory.Exists(path))
        {
            folder = path;
            model = new[] { "model.onnx", Path.Combine("onnx", "model.onnx") }
                .Select(p => Path.Combine(path, p))
                .FirstOrDefault(File.Exists);

            if (model is null)
            {
                error = "No model.onnx in that folder or its onnx/ folder. Point at the .onnx file to use another one.";
                return null;
            }
        }
        else
        {
            error = "Nothing exists at that path.";
            return null;
        }

        var missing = new[] { "spm.model", "gliner_config.json" }.Where(f => !File.Exists(Path.Combine(folder, f))).ToList();
        if (missing.Count > 0)
        {
            error = $"No {string.Join(" or ", missing)} in {folder}. Download them (and added_tokens.json) from the model's " +
                    "Hugging Face page next to the .onnx — an .onnx file holds only the network.";
            return null;
        }

        return new GlinerFiles(model, folder);
    }
}

/// <summary>
/// A GLiNER model (span mode, as exported to ONNX by onnx-community) run in this process: finds
/// spans of text matching labels given in plain words, such as "person" or "medical condition".
///
/// Mirrors GLiNER's own processing: words split by <c>\w+(?:[-_]\w+)*|\S</c>; the prompt
/// <c>[CLS] &lt;&lt;ENT&gt;&gt; label … &lt;&lt;SEP&gt;&gt;</c> before the words; words_mask marking each word's
/// first sub-token; every span up to max_width words scored per label; then the highest-scoring
/// spans kept without overlap. Verified against onnx-community/gliner_multi_pii-v1, whose int8
/// export finds almost nothing — use the fp32 or fp16 file.
/// </summary>
internal sealed class GlinerModel : IDisposable
{
    /// <summary>Words per window. GLiNER is trained on up to 384 tokens; a long text is read in windows.</summary>
    private const int WindowWords = 200;

    private static readonly Regex WordSplitter = new(@"\w+(?:[-_]\w+)*|\S", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    /// <summary>What GLiNER's ONNX graph takes, built from one window of words.</summary>
    internal sealed record Inputs(long[] Ids, long[] WordsMask, int Words, long[] SpanIdx, bool[] SpanMask, int MaxWidth);

    private readonly InferenceSession? _session;
    private readonly Func<string, IReadOnlyList<int>> _tokenize;
    private readonly Func<Inputs, int, float[]> _run;
    private readonly int _maxWidth;
    private readonly long _cls, _sep, _ent, _entSep;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public string UsedDevice { get; }

    /// <summary>
    /// A model without files, for tests: a tokenizer and a stand-in for the ONNX run that returns
    /// the flattened logits [words × maxWidth × labels]. Everything around the run — the prompt,
    /// the word mask, the spans, windowing, scoring and decoding — is the real code.
    /// </summary>
    internal GlinerModel(Func<string, IReadOnlyList<int>> tokenize, Func<Inputs, int, float[]> run, int maxWidth = 12,
        long cls = 1, long sep = 2, long ent = 250103, long entSep = 250104)
    {
        _tokenize = tokenize;
        _run = run;
        _maxWidth = maxWidth;
        (_cls, _sep, _ent, _entSep) = (cls, sep, ent, entSep);
        UsedDevice = "test";
    }

    public GlinerModel(EntityModelRecord record, ActivityLog? log)
    {
        var files = GlinerFiles.Find(record.ModelPath, out var error) ?? throw new InvalidOperationException(error);

        SentencePieceTokenizer tokenizer;
        using (var spm = File.OpenRead(Path.Combine(files.Folder, "spm.model")))
        {
            tokenizer = SentencePieceTokenizer.Create(spm, addBeginningOfSentence: false, addEndOfSentence: false);
        }
        _tokenize = text => tokenizer.EncodeToIds(text);

        using var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(files.Folder, "gliner_config.json")));
        _maxWidth = config.RootElement.TryGetProperty("max_width", out var w) ? w.GetInt32() : 12;

        var added = ReadAddedTokens(files.Folder);
        _ent = added.GetValueOrDefault("<<ENT>>", config.RootElement.TryGetProperty("class_token_index", out var c) ? c.GetInt64() : 0);
        _entSep = added.GetValueOrDefault("<<SEP>>", _ent + 1);
        _cls = tokenizer.Vocabulary.TryGetValue("[CLS]", out var cls) ? cls : 1;
        _sep = tokenizer.Vocabulary.TryGetValue("[SEP]", out var sep) ? sep : 2;

        if (_ent == 0) throw new InvalidOperationException("the model's <<ENT>> token id is unknown: added_tokens.json is missing");

        _session = OnnxSessions.Open(files.Model, record.Device, record.GpuIndex, record.Name, log, out var used);
        UsedDevice = used;
        _run = RunSession;
    }

    /// <summary>The ONNX run itself: the six inputs GLiNER's export takes, its "logits" back flattened.</summary>
    private float[] RunSession(Inputs inputs, int labels)
    {
        var length = inputs.Ids.Length;
        var spans = inputs.Words * inputs.MaxWidth;
        var named = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(inputs.Ids, [1, length])),
            NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(Enumerable.Repeat(1L, length).ToArray(), [1, length])),
            NamedOnnxValue.CreateFromTensor("words_mask", new DenseTensor<long>(inputs.WordsMask, [1, length])),
            NamedOnnxValue.CreateFromTensor("text_lengths", new DenseTensor<long>(new long[] { inputs.Words }, [1, 1])),
            NamedOnnxValue.CreateFromTensor("span_idx", new DenseTensor<long>(inputs.SpanIdx, [1, spans, 2])),
            NamedOnnxValue.CreateFromTensor("span_mask", new DenseTensor<bool>(inputs.SpanMask, [1, spans])),
        };

        using var results = _session!.Run(named, ["logits"]);
        return results.First().AsTensor<float>().ToArray();
    }

    private static Dictionary<string, long> ReadAddedTokens(string folder)
    {
        var path = Path.Combine(folder, "added_tokens.json");
        if (!File.Exists(path)) return [];

        using var json = JsonDocument.Parse(File.ReadAllText(path));
        return json.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt64());
    }

    public async Task<List<Entity>> FindAsync(string text, IReadOnlyList<string> labels, double threshold, CancellationToken ct)
    {
        if (labels.Count == 0 || string.IsNullOrWhiteSpace(text)) return [];

        var words = WordSplitter.Matches(text).Select(m => (m.Index, End: m.Index + m.Length, m.Value)).ToList();
        var found = new List<Entity>();

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            for (var start = 0; start < words.Count; start += WindowWords)
            {
                ct.ThrowIfCancellationRequested();
                found.AddRange(Predict(text, words.GetRange(start, Math.Min(WindowWords, words.Count - start)), labels, threshold));
            }
        }
        finally
        {
            _gate.Release();
        }

        return found;
    }

    private List<Entity> Predict(string text, List<(int Index, int End, string Value)> words, IReadOnlyList<string> labels, double threshold)
    {
        var ids = new List<long> { _cls };
        var wordsMask = new List<long> { 0 };

        foreach (var label in labels)
        {
            ids.Add(_ent);
            wordsMask.Add(0);
            foreach (var t in _tokenize(label)) { ids.Add(t); wordsMask.Add(0); }
        }
        ids.Add(_entSep);
        wordsMask.Add(0);

        for (var w = 0; w < words.Count; w++)
        {
            var pieces = _tokenize(words[w].Value);
            for (var p = 0; p < pieces.Count; p++)
            {
                ids.Add(pieces[p]);
                wordsMask.Add(p == 0 ? w + 1 : 0);
            }
        }
        ids.Add(_sep);
        wordsMask.Add(0);

        var n = words.Count;
        var spanIdx = new long[n * _maxWidth * 2];
        var spanMask = new bool[n * _maxWidth];
        for (var i = 0; i < n; i++)
        for (var j = 0; j < _maxWidth; j++)
        {
            var k = i * _maxWidth + j;
            spanIdx[k * 2] = i;
            spanIdx[k * 2 + 1] = i + j;
            spanMask[k] = i + j < n;
        }

        // Logits come back as [1, words, maxWidth, labels], flattened.
        var logits = _run(new Inputs([.. ids], [.. wordsMask], n, spanIdx, spanMask, _maxWidth), labels.Count);

        var candidates = new List<(int Start, int End, int Label, double Score)>();
        for (var i = 0; i < n; i++)
        for (var j = 0; j < _maxWidth && i + j < n; j++)
        for (var c = 0; c < labels.Count; c++)
        {
            var score = 1 / (1 + Math.Exp(-logits[(i * _maxWidth + j) * labels.Count + c]));
            if (score >= threshold) candidates.Add((i, i + j, c, score));
        }

        // Highest score first, no overlaps: GLiNER's flat decoding.
        var kept = new List<(int Start, int End, int Label, double Score)>();
        foreach (var candidate in candidates.OrderByDescending(x => x.Score))
        {
            if (kept.All(k => candidate.End < k.Start || candidate.Start > k.End)) kept.Add(candidate);
        }

        return [.. kept.Select(k =>
        {
            var from = words[k.Start].Index;
            return new Entity(from, words[k.End].End - from, labels[k.Label], k.Score);
        })];
    }

    public void Dispose()
    {
        _session?.Dispose();
        _gate.Dispose();
    }
}
