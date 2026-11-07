using System.Net;
using System.Text;
using System.Text.Json;
using RavensPort.Core.Filters;
using RavensPort.Core.Models;
using RavensPort.Core.Storage;
using RavensPort.Core.Vault;

namespace RavensPort.Core.Tests.Filters;

/// <summary>
/// Risky actions, judged by a stand-in model that embeds by shared words: deterministic, needs no
/// download, and close enough to how a real model ranks short texts for the rules to be tested.
/// The real ONNX path has its own test below, run when a model folder is given.
/// </summary>
public class FilterServiceTests
{
    private sealed class WordModel : IEmbeddingClient
    {
        public int Calls;

        public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, EmbeddingRole role, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(texts.Select(Vector).ToArray());
        }

        private static float[] Vector(string text)
        {
            var v = new float[256];
            foreach (var word in text.ToLowerInvariant().Split([' ', '\n', '"', '{', '}', ':', ',', '_', '/', '.', '-'], StringSplitOptions.RemoveEmptyEntries))
            {
                // A fixed hash: string.GetHashCode is randomised per process, which made scores —
                // and so these tests — differ from run to run.
                var hash = 17;
                foreach (var c in word) hash = unchecked(hash * 31 + c);
                v[(hash & 0x7fffffff) % 256] += 1;
            }
            return HttpEmbeddingClient.Normalize(v);
        }
    }

    private sealed class BrokenModel : IEmbeddingClient
    {
        public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, EmbeddingRole role, CancellationToken ct) =>
            throw new InvalidOperationException("connection refused");
    }

    private static (FilterService Service, FunnelFilter Filter) Setup(IEmbeddingClient model, Action<FunnelFilter>? configure = null)
    {
        var (service, filter, _, _) = SetupFull(model, configure);
        return (service, filter);
    }

    private static (FilterService Service, FunnelFilter Filter, ConfigStoreCache Cache, EmbeddingClientFactory Factory) SetupFull(
        IEmbeddingClient model, Action<FunnelFilter>? configure = null)
    {
        var cache = new ConfigStoreCache(new InMemoryVault());
        var provider = new EmbeddingProviderRecord { Name = "test", Kind = EmbeddingProviderKind.Ollama, Model = "m" };
        cache.Current.EmbeddingProviders.Add(provider);

        var filter = new FunnelFilter
        {
            Category = FilterCategory.RiskyActions,
            Level = FilterLevel.Balanced,
            EmbeddingProviderId = provider.Id,
            DenyInstructions = ["delete drop wipe all database tables"],
        };
        configure?.Invoke(filter);

        var factory = new EmbeddingClientFactory(null!) { Override = _ => model };
        return (new FilterService(cache, factory), filter, cache, factory);
    }

    private static List<KeyValuePair<string, JsonElement>> Args(object o) =>
        [.. JsonSerializer.SerializeToElement(o).EnumerateObject().Select(p => KeyValuePair.Create(p.Name, p.Value.Clone()))];

    [Fact]
    public async Task ACallCloseToTheDenyList_IsBlocked()
    {
        var (service, filter) = Setup(new WordModel());

        var hits = await service.EvaluateAsync([filter], "sql", Args(new { query = "drop all database tables" }), default);

        var hit = Assert.Single(hits);
        Assert.Equal(FilterCategory.RiskyActions, hit.Category);
        Assert.Contains("close to", hit.Detector);
    }

    [Fact]
    public async Task AnEverydayCall_GoesThrough()
    {
        var (service, filter) = Setup(new WordModel());

        Assert.Empty(await service.EvaluateAsync([filter], "read_file", Args(new { path = "README.md" }), default));
    }

    [Fact]
    public async Task AnAllowExampleCloserThanTheDenyList_LetsTheCallThrough()
    {
        var (service, filter) = Setup(new WordModel(), f => f.AllowExamples = ["drop the temp database tables in staging"]);

        var judgement = await service.JudgeAsync(filter, "sql", Args(new { query = "drop the temp database tables in staging" }), default);

        Assert.False(judgement.Denied);
        Assert.NotNull(judgement.AllowedBy);
    }

    [Fact]
    public async Task AModelThatIsDown_BlocksRatherThanWavingCallsThrough()
    {
        var (service, filter) = Setup(new BrokenModel());

        var hit = Assert.Single(await service.EvaluateAsync([filter], "read_file", Args(new { path = "x" }), default));

        Assert.Contains("unavailable", hit.Detector);
        Assert.Equal(FilterAction.Block, hit.Action);
    }

    [Fact]
    public async Task AnEmptyDenyList_ChecksNothing()
    {
        var (service, filter) = Setup(new BrokenModel(), f => f.DenyInstructions = []);

        Assert.Empty(await service.EvaluateAsync([filter], "anything", null, default));
    }

    [Fact]
    public async Task TheDenyListIsEmbeddedOnce_NotOnEveryCall()
    {
        var model = new WordModel();
        var (service, filter) = Setup(model);

        await service.EvaluateAsync([filter], "a", Args(new { x = "one" }), default);
        var afterFirst = model.Calls;
        await service.EvaluateAsync([filter], "a", Args(new { x = "two" }), default);

        // Only the new call's own text on the second round.
        Assert.Equal(afterFirst + 1, model.Calls);
    }

    [Fact]
    public async Task TheHitNamesTheArgumentThatCameClosest()
    {
        var (service, filter) = Setup(new WordModel());

        var hit = Assert.Single(await service.EvaluateAsync([filter], "sql",
            Args(new { title = "nightly maintenance job", query = "drop all database tables" }), default));

        Assert.Equal("query", hit.FieldPath);
    }

    [Fact]
    public void EachLongEnoughStringValueIsJudgedOnItsOwn_KeysAndNumbersAreNot()
    {
        var texts = FilterService.TextsToJudge("send",
            FilterEngine.Read(Args(new { to = "team", text = "I live in Munich", nested = new { note = "moved here last year" }, count = 12345678 })));

        Assert.Equal([("text", "I live in Munich"), ("nested.note", "moved here last year")], texts);
    }

    [Fact]
    public void ACallWithNothingToRead_IsJudgedWhole_SoItIsNeverSkipped()
    {
        var (path, text) = Assert.Single(FilterService.TextsToJudge("git_status", FilterEngine.Read(Args(new { flag = "x" }))));

        Assert.Equal("call", path);
        Assert.StartsWith("git_status ", text);
    }

    [Fact]
    public async Task AHandSetThreshold_OverridesTheLevel()
    {
        var (service, filter) = Setup(new WordModel(), f => f.Threshold = 0.99);

        Assert.Empty(await service.EvaluateAsync([filter], "sql", Args(new { query = "drop all database tables" }), default));
    }

    // ---- the order of the steps --------------------------------------------------------------

    /// <summary>Records exactly what reached the embedding model.</summary>
    private sealed class RecordingModel : IEmbeddingClient
    {
        private readonly WordModel _inner = new();
        public List<string> Seen { get; } = [];

        public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, EmbeddingRole role, CancellationToken ct)
        {
            if (role == EmbeddingRole.Query && !texts.SequenceEqual(FilterService.EverydayText)) Seen.AddRange(texts);
            return _inner.EmbedAsync(texts, role, ct);
        }
    }

    private static (FilterService Service, List<FunnelFilter> Filters, RecordingModel Model) Pipeline(
        FilterAction personal = FilterAction.Watch, FilterAction entities = FilterAction.Watch,
        Func<string, List<Entity>>? entityModel = null)
    {
        var model = new RecordingModel();
        var (service, meaning, cache, factory) = SetupFull(model);

        var entityRecord = new EntityModelRecord { Name = "fake", ModelPath = "x" };
        cache.Current.EntityModels.Add(entityRecord);
        factory.EntityOverride = _ => (text, _, _, _) => Task.FromResult(entityModel?.Invoke(text) ?? []);

        List<FunnelFilter> filters =
        [
            new() { Category = FilterCategory.Secrets, Level = FilterLevel.Balanced },
            new() { Category = FilterCategory.PersonalData, Level = FilterLevel.Balanced, Action = personal },
            new() { Category = FilterCategory.Entities, Level = FilterLevel.Balanced, Action = entities, EntityModelId = entityRecord.Id, EntityLabels = ["person"] },
            meaning,
        ];
        return (service, filters, model);
    }

    [Fact]
    public async Task ASecret_StopsTheCallAtStep1_AndNothingReachesTheEmbeddingModel()
    {
        var (service, filters, model) = Pipeline();

        var outcome = await service.RunAsync(filters, "shell", Args(new { command = "export AWS_KEY=AKIA" + "Z7QW3XKPLM2N4R6T && drop all database tables" }), default);

        Assert.Equal(FilterCategory.Secrets, outcome.StoppedAt);
        // Several gitleaks rules may match the same key (here also generic-api-key); all are step 1, all deny.
        Assert.NotEmpty(outcome.Hits);
        Assert.All(outcome.Hits, h => Assert.Equal((FilterCategory.Secrets, FilterAction.Block), (h.Category, h.Action)));
        Assert.Empty(model.Seen);
    }

    [Fact]
    public async Task PersonalDataThatIsOnlyWatched_IsStillReplacedBeforeStep4ReadsTheCall()
    {
        var (service, filters, model) = Pipeline(personal: FilterAction.Watch);

        var outcome = await service.RunAsync(filters, "mail", Args(new { body = "mail jane.doe@example.com and drop all database tables" }), default);

        // Step 2 only watched, so the call reached step 4 — which blocks it.
        Assert.Equal(FilterCategory.RiskyActions, outcome.StoppedAt);
        Assert.Contains(outcome.Hits, h => h.Category == FilterCategory.PersonalData && h.Action == FilterAction.Watch);
        Assert.Contains(outcome.Hits, h => h.Category == FilterCategory.RiskyActions);
        Assert.Contains(model.Seen, t => t.Contains("[Email address]"));
        Assert.DoesNotContain(model.Seen, t => t.Contains("jane.doe"));
    }

    [Fact]
    public async Task BlockedPersonalData_StopsAtStep2()
    {
        var (service, filters, model) = Pipeline(personal: FilterAction.Block);

        var outcome = await service.RunAsync(filters, "mail", Args(new { body = "mail jane.doe@example.com" }), default);

        Assert.Equal(FilterCategory.PersonalData, outcome.StoppedAt);
        Assert.Empty(model.Seen);
    }

    [Fact]
    public async Task Entities_ReadTheTextWithStep2sMatchesReplaced_AndTheirOwnMatchesAreReplacedForStep4()
    {
        string? entityModelSaw = null;
        var (service, filters, model) = Pipeline(entityModel: text =>
        {
            entityModelSaw = text;
            var at = text.IndexOf("John Doe", StringComparison.Ordinal);
            return at < 0 ? [] : [new Entity(at, "John Doe".Length, "person", 0.99)];
        });

        var outcome = await service.RunAsync(filters, "note", Args(new { text = "John Doe, jane.doe@example.com, wants the tables dropped" }), default);

        Assert.Equal("John Doe, [Email address], wants the tables dropped", entityModelSaw);
        Assert.Contains(outcome.Hits, h => h.Category == FilterCategory.Entities && h.Detector.StartsWith("person", StringComparison.Ordinal));
        Assert.Contains(model.Seen, t => t.Contains("[person]") && t.Contains("[Email address]"));
        Assert.DoesNotContain(model.Seen, t => t.Contains("John"));
    }

    [Fact]
    public async Task AnEntityModelThatFails_ReportsTheCallRatherThanWavingItThrough()
    {
        var (service, filters, _) = Pipeline(entities: FilterAction.Block, entityModel: _ => throw new InvalidOperationException("model missing"));

        var outcome = await service.RunAsync(filters, "note", Args(new { text = "anything at all here" }), default);

        Assert.Equal(FilterCategory.Entities, outcome.StoppedAt);
        Assert.Contains("unavailable", outcome.Hits.Single().Detector);
    }

    /// <summary>
    /// A real GLiNER model when RAVENSPORT_GLINER_TEST_MODEL names its .onnx (or folder), e.g.
    /// onnx-community/gliner_multi_pii-v1 model.onnx with spm.model, gliner_config.json and
    /// added_tokens.json beside it. Skipped otherwise, so CI does not download 1 GB.
    /// </summary>
    [Fact]
    public async Task GlinerModel_FindsANameACityAndAPhoneNumber_AndNothingInCode()
    {
        var path = Environment.GetEnvironmentVariable("RAVENSPORT_GLINER_TEST_MODEL");
        if (string.IsNullOrEmpty(path)) return;

        using var gliner = new GlinerModel(new EntityModelRecord { Name = "test", ModelPath = path }, log: null);
        string[] labels = ["person", "city", "phone number", "medical condition"];
        const string text = "My name is John Doe, I live in Berlin and my number is +49 170 0000000.";

        var found = await gliner.FindAsync(text, labels, 0.5, default);
        var code = await gliner.FindAsync("please fix the failing unit test in the parser and run npm run build", labels, 0.5, default);

        Assert.Contains(found, e => e.Label == "person" && text.Substring(e.Start, e.Length) == "John Doe");
        Assert.Contains(found, e => e.Label == "city" && text.Substring(e.Start, e.Length) == "Berlin");
        Assert.Contains(found, e => e.Label == "phone number");
        Assert.Empty(code);
    }

    // ---- the HTTP client --------------------------------------------------------------------

    private sealed class Recorder(string response) : HttpMessageHandler
    {
        public HttpRequestMessage? Request;
        public string? Body;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            Body = await request.Content!.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        }
    }

    [Fact]
    public async Task Voyage_GetsInputTypeAndTheKey_AndVectorsComeBackInOrderAndUnitLength()
    {
        var recorder = new Recorder("""{"data":[{"index":1,"embedding":[0,2]},{"index":0,"embedding":[3,4]}]}""");
        var provider = new EmbeddingProviderRecord
        {
            Name = "v", Kind = EmbeddingProviderKind.VoyageAI, BaseUrl = "https://api.voyageai.com/v1/",
            Model = "voyage-4-lite", CredentialId = Guid.NewGuid(),
        };
        var client = new HttpEmbeddingClient(new HttpClient(recorder), provider, _ => ValueTask.FromResult<string?>("k"));

        var vectors = await client.EmbedAsync(["a", "b"], EmbeddingRole.Query, default);

        Assert.Equal("https://api.voyageai.com/v1/embeddings", recorder.Request!.RequestUri!.ToString());
        Assert.Equal("Bearer k", recorder.Request.Headers.Authorization!.ToString());
        Assert.Contains("\"input_type\":\"query\"", recorder.Body);
        Assert.Equal([0.6f, 0.8f], vectors[0]);
        Assert.Equal([0f, 1f], vectors[1]);
    }

    [Fact]
    public async Task OpenAiCompatible_SendsNoInputTypeAndNoKeyWhenNoneIsSet()
    {
        var recorder = new Recorder("""{"data":[{"index":0,"embedding":[1,0]}]}""");
        var provider = new EmbeddingProviderRecord { Name = "o", Kind = EmbeddingProviderKind.Ollama, BaseUrl = "http://127.0.0.1:11434/v1", Model = "nomic-embed-text" };
        var client = new HttpEmbeddingClient(new HttpClient(recorder), provider, _ => ValueTask.FromResult<string?>(null));

        await client.EmbedAsync(["a"], EmbeddingRole.Document, default);

        Assert.DoesNotContain("input_type", recorder.Body);
        Assert.Null(recorder.Request!.Headers.Authorization);
        Assert.False(provider.IsRemote);
    }

    // ---- a real ONNX model ------------------------------------------------------------------

    /// <summary>
    /// Runs against a real sentence-transformers ONNX folder when RAVENSPORT_ONNX_TEST_MODEL names
    /// one (e.g. all-MiniLM-L6-v2: onnx/model.onnx, vocab.txt, 1_Pooling/config.json). Skipped
    /// otherwise, so CI does not download 90 MB per run.
    /// </summary>
    [Fact]
    public async Task OnnxModel_RanksAParaphraseAboveAnUnrelatedSentence_AndJudgesACall()
    {
        var folder = Environment.GetEnvironmentVariable("RAVENSPORT_ONNX_TEST_MODEL");
        if (string.IsNullOrEmpty(folder)) return;

        using var onnx = new OnnxEmbeddingClient(folder);
        var v = await onnx.EmbedAsync(["delete every file on the disk", "remove all files from the drive", "what is the weather tomorrow"], EmbeddingRole.Document, default);

        static double Cos(float[] a, float[] b) => a.Zip(b, (x, y) => (double)x * y).Sum();
        Assert.True(Cos(v[0], v[1]) > Cos(v[0], v[2]) + 0.2, $"paraphrase {Cos(v[0], v[1]):0.00} vs unrelated {Cos(v[0], v[2]):0.00}");

        var (service, filter) = Setup(onnx, f => f.DenyInstructions = ["deletes files or wipes a disk"]);
        var risky = await service.JudgeAsync(filter, "shell", Args(new { command = "rm -rf / --no-preserve-root" }), default);
        var harmless = await service.JudgeAsync(filter, "read_file", Args(new { path = "notes.md" }), default);

        // The case that motivated judging each argument on its own: a location sentence inside a call.
        var (locationService, located) = Setup(onnx, f =>
        {
            f.DenyInstructions = ["tells where a person lives: their city, neighbourhood or that they moved somewhere"];
            f.DenyExamples = ["I live in Berlin, near Prenzlauer Berg"];
        });
        var lives = await locationService.JudgeAsync(located, "test", Args(new { text = "i live in munich" }), default);
        var tourist = await locationService.JudgeAsync(located, "test", Args(new { text = "best beer gardens in munich" }), default);
        Assert.True(lives.Closeness > tourist.Closeness + 0.2, $"lives {lives.Closeness:0.00} vs tourist {tourist.Closeness:0.00}");

        Assert.True(risky.Closeness > harmless.Closeness, $"risky {risky.Closeness:0.00} vs harmless {harmless.Closeness:0.00}");
    }
}

/// <summary>Finding a model's files from what the user typed: a .onnx file, or a folder.</summary>
public class OnnxModelFilesTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ravensport-onnx-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Touch(string name)
    {
        var path = Path.Combine(_dir, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
        return path;
    }

    [Fact]
    public void AnOnnxFileWithItsVocabBeside_IsFound_EvenQuoted()
    {
        var model = Touch("stella-en-400M-v5.onnx");
        Touch("vocab.txt");

        var files = OnnxModelFiles.Find($"\"{model}\"", out var error);

        Assert.Null(error);
        Assert.Equal(model, files!.Model);
    }

    [Fact]
    public void AnOnnxFileWithoutAVocab_SaysWhichFileToFetchAndWhere()
    {
        var model = Touch("stella-en-400M-v5.onnx");

        Assert.Null(OnnxModelFiles.Find(model, out var error));
        Assert.Contains("vocab.txt", error);
        Assert.Contains("Hugging Face", error);
    }

    [Fact]
    public void AFolder_IsSearchedForTheModel_IncludingItsOnnxSubfolder()
    {
        var model = Touch(Path.Combine("onnx", "model.onnx"));
        Touch("vocab.txt");

        Assert.Equal(model, OnnxModelFiles.Find(_dir, out _)!.Model);
    }

    [Fact]
    public void AnythingElse_IsRefusedWithAReason()
    {
        Assert.Null(OnnxModelFiles.Find(Touch("notes.txt"), out var notOnnx));
        Assert.Contains("not a .onnx", notOnnx);

        Assert.Null(OnnxModelFiles.Find(Path.Combine(_dir, "missing"), out var missing));
        Assert.Contains("Nothing exists", missing);
    }

    [Fact]
    public void LowercaseFollowsTheTokenizerConfig_AndDefaultsToUncased()
    {
        Assert.True(OnnxEmbeddingClient.ReadsLowerCase(_dir));

        File.WriteAllText(Path.Combine(_dir, "tokenizer_config.json"), """{"do_lower_case": false}""");
        Assert.False(OnnxEmbeddingClient.ReadsLowerCase(_dir));
    }
}
