using System.Text;
using Microsoft.ML.OnnxRuntime.Tensors;
using RavensPort.Core.Diagnostics;
using RavensPort.Core.Filters;
using RavensPort.Core.Models;

namespace RavensPort.Core.Tests.Filters;

/// <summary>
/// A real ONNX model small enough to write by hand: one Cast node that turns input_ids into floats
/// and calls the result "embedding". It lets CI exercise the ONNX Runtime path — loading, the
/// tokenizer, running, the device choice and the CPU fallback — without downloading a model.
/// </summary>
internal static class TinyOnnx
{
    private static void Varint(List<byte> to, ulong value)
    {
        while (value >= 0x80)
        {
            to.Add((byte)(value | 0x80));
            value >>= 7;
        }
        to.Add((byte)value);
    }

    private static byte[] Key(int field, int wire)
    {
        var b = new List<byte>();
        Varint(b, (ulong)((field << 3) | wire));
        return [.. b];
    }

    private static byte[] Int(int field, long value)
    {
        var b = new List<byte>(Key(field, 0));
        Varint(b, (ulong)value);
        return [.. b];
    }

    private static byte[] Bytes(int field, byte[] payload)
    {
        var b = new List<byte>(Key(field, 2));
        Varint(b, (ulong)payload.Length);
        b.AddRange(payload);
        return [.. b];
    }

    private static byte[] Str(int field, string value) => Bytes(field, Encoding.UTF8.GetBytes(value));

    private static byte[] Cat(params byte[][] parts) => [.. parts.SelectMany(p => p)];

    /// <summary>ValueInfoProto: a tensor input or output with symbolic dimensions.</summary>
    private static byte[] Tensor(string name, int elemType, params string[] dims) =>
        Cat(Str(1, name), Bytes(2, Bytes(1, Cat(Int(1, elemType), Bytes(2, Cat([.. dims.Select(d => Bytes(1, Str(2, d)))]))))));

    private const int Float = 1, Int64 = 7;
    public const int Bool = 9, Long = Int64, Single = Float;

    /// <summary>
    /// A graph with the given inputs whose one output is <paramref name="from"/> cast to float: inputs it
    /// does not use are still declared, so code that feeds them by name is exercised.
    /// </summary>
    public static void WriteCast(string path, (string Name, int Type, string[] Dims)[] inputs, string from, string output, string[] outputDims)
    {
        var cast = Cat(Str(1, from), Str(2, output), Str(4, "Cast"), Bytes(5, Cat(Str(1, "to"), Int(3, Float), Int(20, 2))));
        var graph = Cat([Bytes(1, cast), Str(2, "tiny"), .. inputs.Select(i => Bytes(11, Tensor(i.Name, i.Type, i.Dims))), Bytes(12, Tensor(output, Float, outputDims))]);
        File.WriteAllBytes(path, Cat(Int(1, 8), Bytes(8, Int(2, 13)), Bytes(7, graph)));
    }

    /// <summary>input_ids, attention_mask (and optionally token_type_ids) → embedding = float(input_ids).</summary>
    public static string Write(string folder, bool tokenTypes = false, string outputName = "embedding")
    {
        var cast = Cat(Str(1, "input_ids"), Str(2, outputName), Str(4, "Cast"), Bytes(5, Cat(Str(1, "to"), Int(3, Float), Int(20, 2))));
        var inputs = new List<byte[]> { Bytes(11, Tensor("input_ids", Int64, "b", "n")), Bytes(11, Tensor("attention_mask", Int64, "b", "n")) };
        if (tokenTypes) inputs.Add(Bytes(11, Tensor("token_type_ids", Int64, "b", "n")));
        var graph = Cat([Bytes(1, cast), Str(2, "tiny"), .. inputs, Bytes(12, Tensor(outputName, Float, "b", "n"))]);
        var model = Cat(Int(1, 8), Bytes(8, Int(2, 13)), Bytes(7, graph));

        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "model.onnx");
        File.WriteAllBytes(path, model);
        File.WriteAllText(Path.Combine(folder, "vocab.txt"), "[PAD]\n[UNK]\n[CLS]\n[SEP]\n[MASK]\nhello\nworld\n");
        return path;
    }
}

public class OnnxModelTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ravensport-tiny-onnx-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private ActivityLog Log() => new(Path.Combine(_dir, "logs"));

    [Fact]
    public async Task AnOnnxEmbeddingModel_LoadsTokenizesRunsAndReturnsUnitVectors()
    {
        TinyOnnx.Write(_dir);
        using var client = new OnnxEmbeddingClient(_dir);

        var vectors = await client.EmbedAsync(["hello world", "hello"], EmbeddingRole.Query, default);

        Assert.Equal("CPU", client.UsedDevice);
        Assert.Equal(4, vectors[0].Length); // [CLS] hello world [SEP]
        Assert.Equal(3, vectors[1].Length);
        Assert.All(vectors, v => Assert.Equal(1.0, Math.Sqrt(v.Sum(x => (double)x * x)), 3));
    }

    [Fact]
    public async Task AModelThatTakesTokenTypes_IsGivenThem()
    {
        TinyOnnx.Write(_dir, tokenTypes: true);
        using var client = new OnnxEmbeddingClient(_dir);

        Assert.Single(await client.EmbedAsync(["hello"], EmbeddingRole.Document, default));
    }

    [Fact]
    public void AGpuThatIsNotThere_FallsBackToTheCpu_AndTheActivityLogSaysSo()
    {
        var model = TinyOnnx.Write(_dir);
        var log = Log();

        using var session = OnnxSessions.Open(model, OnnxDevice.Gpu, gpuIndex: 99, "test model", log, out var used);

        Assert.StartsWith("CPU (fell back", used);
        Assert.Contains(log.GetRecent(10), line => line.Contains("FILTERS model 'test model' could not start on GPU 99") && line.Contains("running on the CPU instead"));
        Assert.Contains("fell back", File.ReadAllText(log.ErrorLogPath));
    }

    [Fact]
    public void TheCpu_IsUsedWhenChosen()
    {
        using var session = OnnxSessions.Open(TinyOnnx.Write(_dir), OnnxDevice.Cpu, 0, "m", log: null, out var used);

        Assert.Equal("CPU", used);
    }

    [Fact]
    public async Task TheFactory_KeepsAModelLoaded_UntilItsSettingsChange()
    {
        TinyOnnx.Write(_dir);
        var provider = new EmbeddingProviderRecord { Name = "tiny", Kind = EmbeddingProviderKind.Onnx, ModelFolder = _dir };
        using var factory = new EmbeddingClientFactory(null!, Log());

        var first = factory.Get(provider);
        Assert.Same(first, factory.Get(provider));
        Assert.Equal("CPU", factory.DeviceOf(provider.Id));
        Assert.Single(await first.EmbedAsync(["hello"], EmbeddingRole.Query, default));

        provider.Device = OnnxDevice.Gpu;
        provider.GpuIndex = 99;
        var second = factory.Get(provider);

        Assert.NotSame(first, second);
        Assert.StartsWith("CPU (fell back", factory.DeviceOf(provider.Id));
        Assert.Null(factory.DeviceOf(Guid.NewGuid()));
    }

    [Fact]
    public void AnApiProvider_GetsAnHttpClient_FromTheFactory()
    {
        using var factory = new EmbeddingClientFactory(null!);
        var provider = new EmbeddingProviderRecord { Name = "o", Kind = EmbeddingProviderKind.Ollama, BaseUrl = "http://127.0.0.1:11434/v1", Model = "m" };

        Assert.IsType<HttpEmbeddingClient>(factory.Get(provider));
        Assert.Null(factory.DeviceOf(provider.Id));
    }

    [Fact]
    public void Pooling_UsesThePooledOutputAsIs_OrTheMeanOrTheFirstToken()
    {
        var pooled = new DenseTensor<float>(new float[] { 3, 4 }, [1, 2]);
        var perToken = new DenseTensor<float>(new float[] { 1, 0, 0, 1 }, [1, 2, 2]);

        Assert.Equal([0.6f, 0.8f], OnnxEmbeddingClient.Pool(pooled, 2, clsPooling: false));
        Assert.Equal(1.0, OnnxEmbeddingClient.Pool(perToken, 2, clsPooling: false).Sum(x => (double)x * x), 3);
        Assert.Equal([1f, 0f], OnnxEmbeddingClient.Pool(perToken, 2, clsPooling: true));
    }

    [Fact]
    public void ClsPooling_IsReadFromTheModelsPoolingConfig()
    {
        Assert.False(OnnxEmbeddingClient.ReadsClsPooling(_dir));

        Directory.CreateDirectory(Path.Combine(_dir, "1_Pooling"));
        File.WriteAllText(Path.Combine(_dir, "1_Pooling", "config.json"), """{"pooling_mode_cls_token": true}""");

        Assert.True(OnnxEmbeddingClient.ReadsClsPooling(_dir));
    }
}

/// <summary>Everything GLiNER does around its ONNX run, with a stand-in for the run.</summary>
public class GlinerModelTests
{
    /// <summary>One token per word: its length, so ids are predictable.</summary>
    private static IReadOnlyList<int> Tokenize(string word) => [100 + word.Length];

    /// <summary>A stand-in run that scores one span (words a..b, label c) high and everything else low.</summary>
    private static Func<GlinerModel.Inputs, int, float[]> Scoring(params (int Start, int End, int Label, float Logit)[] spans) =>
        (inputs, labels) =>
        {
            var logits = Enumerable.Repeat(-10f, inputs.Words * inputs.MaxWidth * labels).ToArray();
            foreach (var (start, end, label, logit) in spans)
                if (start < inputs.Words && end - start < inputs.MaxWidth)
                    logits[(start * inputs.MaxWidth + (end - start)) * labels + label] = logit;
            return logits;
        };

    [Fact]
    public async Task ItBuildsGlinersPrompt_AndMapsTheBestSpanBackToTheText()
    {
        GlinerModel.Inputs? seen = null;
        var score = Scoring((3, 4, 0, 5f));
        using var model = new GlinerModel(Tokenize, (inputs, labels) => { seen = inputs; return score(inputs, labels); }, maxWidth: 3);
        const string text = "My name is John Doe.";

        var found = await model.FindAsync(text, ["person"], 0.5, default);

        var entity = Assert.Single(found);
        Assert.Equal("John Doe", text.Substring(entity.Start, entity.Length));
        Assert.Equal("person", entity.Label);

        // [CLS] <<ENT>> person <<SEP>> My name is John Doe . [SEP]
        Assert.Equal([1, 250103, 106, 250104, 102, 104, 102, 104, 103, 101, 2], seen!.Ids);
        Assert.Equal([0, 0, 0, 0, 1, 2, 3, 4, 5, 6, 0], seen.WordsMask);
        Assert.Equal(6, seen.Words);
        Assert.Equal(6 * 3 * 2, seen.SpanIdx.Length);
        Assert.False(seen.SpanMask[5 * 3 + 1]); // a span running past the last word
    }

    [Fact]
    public async Task OverlappingSpans_KeepTheHigherScore_AndLowScoresAreDropped()
    {
        using var model = new GlinerModel(Tokenize, Scoring((0, 1, 0, 2f), (1, 2, 1, 6f), (3, 3, 0, -1f)), maxWidth: 3);

        var found = await model.FindAsync("Jane Doe Musterstadt here", ["person", "city"], 0.5, default);

        Assert.Equal(["city"], found.Select(e => e.Label));
    }

    [Fact]
    public async Task ALongText_IsReadInWindows()
    {
        var runs = 0;
        var score = Scoring();
        using var model = new GlinerModel(Tokenize, (inputs, labels) => { runs++; return score(inputs, labels); });

        await model.FindAsync(string.Join(' ', Enumerable.Repeat("word", 450)), ["person"], 0.5, default);

        Assert.Equal(3, runs);
    }

    [Fact]
    public async Task NoLabelsOrNoText_RunsNothing()
    {
        var runs = 0;
        using var model = new GlinerModel(Tokenize, (_, _) => { runs++; return []; });

        Assert.Empty(await model.FindAsync("text", [], 0.5, default));
        Assert.Empty(await model.FindAsync("   ", ["person"], 0.5, default));
        Assert.Equal(0, runs);
        Assert.Equal("test", model.UsedDevice);
    }
}

public class EmbeddingProviderRecordTests
{
    [Theory]
    [InlineData(EmbeddingProviderKind.Ollama, "http://127.0.0.1:11434/v1", false)]
    [InlineData(EmbeddingProviderKind.OpenAiCompatible, "http://127.0.0.1:8080/v1", false)]
    [InlineData(EmbeddingProviderKind.OpenAI, "https://api.openai.com/v1", true)]
    [InlineData(EmbeddingProviderKind.VoyageAI, "https://api.voyageai.com/v1", true)]
    public void EachKind_HasADefaultUrl_AndIsRemoteOnlyOffThisPc(EmbeddingProviderKind kind, string url, bool remote)
    {
        Assert.Equal(url, EmbeddingProviderRecord.DefaultBaseUrl(kind));
        Assert.Equal(remote, new EmbeddingProviderRecord { Name = "x", Kind = kind, BaseUrl = url }.IsRemote);
        Assert.False(string.IsNullOrEmpty(EmbeddingProviderRecord.Describe(kind)));
    }

    [Fact]
    public void AnOnnxModel_IsNeverRemote_AndHasNoUrlOrSuggestions()
    {
        Assert.False(new EmbeddingProviderRecord { Name = "x", Kind = EmbeddingProviderKind.Onnx }.IsRemote);
        Assert.Equal("", EmbeddingProviderRecord.DefaultBaseUrl(EmbeddingProviderKind.Onnx));
        Assert.Empty(EmbeddingProviderRecord.SuggestedModels(EmbeddingProviderKind.Onnx));
        Assert.NotEmpty(EmbeddingProviderRecord.SuggestedModels(EmbeddingProviderKind.VoyageAI));
    }
}
