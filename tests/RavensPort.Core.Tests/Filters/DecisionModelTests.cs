using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RavensPort.Core.Filters;
using RavensPort.Core.Models;
using RavensPort.Core.Storage;
using RavensPort.Core.Vault;

namespace RavensPort.Core.Tests.Filters;

/// <summary>Step 5: the decision models and the step itself.</summary>
public class DecisionModelTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ravensport-decision-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    // ---- Laya's sequence -------------------------------------------------------------------

    /// <summary>One id per word, from 100 up — enough to see where every piece lands.</summary>
    private static int[] Words(string text) =>
        [.. text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => 100 + Math.Abs(w.GetHashCode(StringComparison.Ordinal) % 800))];

    [Fact]
    public void LayasSequence_PutsAMaskBeforeEachOption_AndTheStateAfterThem()
    {
        const int cls = 1, sep = 2, mask = 4;
        var (ids, markers) = DecisionInputs.LayaSequence(Words, "[MASK]", cls, sep, mask,
            "The call deletes data.", "tool: shell [MASK] rm -rf /", maxLen: 512, headMaxLen: 192);

        Assert.Equal(cls, ids[0]);
        Assert.Equal(2, markers.Length);
        Assert.All(markers, m => Assert.Equal(mask, ids[m]));
        Assert.True(markers[0] < markers[1]);
        Assert.Equal(sep, ids[markers[0] - 1]);           // the head ends before the first option
        Assert.Equal(sep, ids[^1]);
        Assert.Equal(2, ids.Count(i => i == mask));        // a [MASK] in the state is scrubbed, not a third marker
    }

    [Fact]
    public void LayasSequence_CutsALongStateToTheModelsLength()
    {
        var state = string.Join(' ', Enumerable.Repeat("word", 2000));

        var (ids, markers) = DecisionInputs.LayaSequence(Words, "[MASK]", 1, 2, 4, "The call deletes data.", state, maxLen: 128, headMaxLen: 64);

        Assert.Equal(128, ids.Length);
        Assert.Equal(2, markers.Length);
    }

    // ---- pairs and probabilities -----------------------------------------------------------

    [Fact]
    public void APair_IsJoinedTheWayTheTokenizerSays()
    {
        var bert = JsonNode.Parse("""{"post_processor":{"type":"BertProcessing","sep":["[SEP]",3],"cls":["[CLS]",2]}}""")!.AsObject();
        var roberta = JsonNode.Parse("""{"post_processor":{"type":"RobertaProcessing","sep":["</s>",2],"cls":["<s>",0]}}""")!.AsObject();
        var template = JsonNode.Parse("""
            {"post_processor":{"type":"Sequence","processors":[{"type":"ByteLevel"},{"type":"TemplateProcessing",
              "pair":[{"SpecialToken":{"id":"[CLS]","type_id":0}},{"Sequence":{"id":"A","type_id":0}},{"SpecialToken":{"id":"[SEP]","type_id":0}},
                      {"Sequence":{"id":"B","type_id":1}},{"SpecialToken":{"id":"[SEP]","type_id":1}}],
              "special_tokens":{"[CLS]":{"ids":[1]},"[SEP]":{"ids":[2]}}}]}}
            """)!.AsObject();

        var joined = DecisionInputs.Pair(bert, [10, 11], [20], 512);
        Assert.Equal([2, 10, 11, 3, 20, 3], joined.Ids);
        Assert.Equal([0, 0, 0, 0, 1, 1], joined.Types);
        Assert.Equal([0, 10, 11, 2, 2, 20, 2], DecisionInputs.Pair(roberta, [10, 11], [20], 512).Ids);
        var templated = DecisionInputs.Pair(template, [10, 11], [20], 512);
        Assert.Equal([1, 10, 11, 2, 20, 2], templated.Ids);
        Assert.Equal([0, 0, 0, 0, 1, 1], templated.Types);

        // The premise gives way when the pair is too long; the statement never does.
        Assert.Equal([2, 10, 3, 20, 3], DecisionInputs.Pair(bert, [10, 11, 12], [20], 5).Ids);
    }

    [Fact]
    public void Probability_ReadsEntailment_TheWayEachKindOfModelMeansIt()
    {
        var nli = new Dictionary<int, string> { [0] = "contradiction", [1] = "neutral", [2] = "entailment" };
        var twoWay = new Dictionary<int, string> { [0] = "entailment", [1] = "not_entailment" };

        // Entailment against contradiction only, as Hugging Face's multi-label zero-shot pipeline does.
        Assert.Equal(1 / (1 + Math.Exp(-4)), DecisionInputs.Probability([0f, 9f, 4f], nli), 6);
        Assert.Equal(1 / (1 + Math.Exp(-2)), DecisionInputs.Probability([3f, 1f], twoWay), 6);
        Assert.Equal(1 / (1 + Math.Exp(-1.5)), DecisionInputs.Probability([1.5f], new Dictionary<int, string>()), 6);
        Assert.Equal(1 / (1 + Math.Exp(-1)), DecisionInputs.Probability([0f, 1f], new Dictionary<int, string>()), 6);
        Assert.Throws<InvalidOperationException>(() => DecisionInputs.Probability([0f, 1f, 2f], new Dictionary<int, string>()));
    }

    // ---- a zero-shot ONNX model, end to end ---------------------------------------------------

    private const string TokenizerJson = """
        {
          "version": "1.0", "truncation": null, "padding": null,
          "added_tokens": [
            {"id":0,"content":"[PAD]","single_word":false,"lstrip":false,"rstrip":false,"normalized":false,"special":true},
            {"id":1,"content":"[UNK]","single_word":false,"lstrip":false,"rstrip":false,"normalized":false,"special":true},
            {"id":2,"content":"[CLS]","single_word":false,"lstrip":false,"rstrip":false,"normalized":false,"special":true},
            {"id":3,"content":"[SEP]","single_word":false,"lstrip":false,"rstrip":false,"normalized":false,"special":true}
          ],
          "normalizer": null,
          "pre_tokenizer": {"type": "Whitespace"},
          "post_processor": {"type": "BertProcessing", "sep": ["[SEP]", 3], "cls": ["[CLS]", 2]},
          "decoder": null,
          "model": {"type": "WordLevel", "vocab": {"[PAD]":0,"[UNK]":1,"[CLS]":2,"[SEP]":3,"delete":7,"files":6,"weather":8}, "unk_token": "[UNK]"}
        }
        """;

    [Fact]
    public async Task AZeroShotOnnxModel_TokenizesThePairRunsAndReadsEntailment()
    {
        // The tiny model's "logits" are its input ids as floats, so the answer is predictable:
        // [CLS] delete files [SEP] weather [SEP] → contradiction = id 2, entailment = id 6.
        TinyOnnx.Write(_dir, tokenTypes: true, outputName: "logits");
        File.WriteAllText(Path.Combine(_dir, "tokenizer.json"), TokenizerJson);
        File.WriteAllText(Path.Combine(_dir, "config.json"), """{"id2label":{"0":"contradiction","1":"neutral","2":"entailment"}}""");

        using var model = new ZeroShotDecisionModel(new DecisionModelRecord { Name = "tiny", Kind = DecisionModelKind.ZeroShotOnnx, ModelPath = _dir }, null);
        var p = await model.DecideAsync("delete files", ["weather"], default);

        Assert.Equal("CPU", model.UsedDevice);
        Assert.Equal(1 / (1 + Math.Exp(-4)), p[0], 5);
    }

    // ---- the Jev protocol -------------------------------------------------------------------

    private sealed class FakeJev(Func<JsonNode, string> answer) : HttpMessageHandler
    {
        public JsonNode? Body { get; private set; }
        public string? Auth { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("https://api.example.com/v1/systemone", request.RequestUri!.ToString());
            Body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct));
            Auth = request.Headers.Authorization?.ToString();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer(Body!), Encoding.UTF8, "application/json") };
        }
    }

    [Fact]
    public async Task TheJevClient_AsksOneNoulQuestionPerStatement_AndReadsTheirProbabilities()
    {
        var jev = new FakeJev(_ => """{"model":"jev-1.13.0","answers":{"q0":{"noul":0.91},"q1":{"noul":0.12}}}""");
        var record = new DecisionModelRecord { Name = "jev", BaseUrl = "https://api.example.com/v1", Model = "jev-latest", CredentialId = Guid.NewGuid() };
        var client = new JevDecisionClient(new HttpClient(jev), record, _ => ValueTask.FromResult<string?>("test-key"));

        var p = await client.DecideAsync("tool: shell\ncommand: rm -rf /", ["The call deletes data.", "The call spends money."], default);

        Assert.Equal([0.91, 0.12], p);
        Assert.Equal("Bearer test-key", jev.Auth);
        Assert.Equal("jev-latest", (string?)jev.Body!["model"]);
        Assert.Equal("noul", (string?)jev.Body["questions"]!["q1"]!["type"]);
        Assert.Equal("The call spends money.", (string?)jev.Body["questions"]!["q1"]!["instructions"]);
    }

    [Fact]
    public async Task TheJevClient_ReportsAnErrorStatus_WithoutTheBody()
    {
        var failing = new StubHandler(new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("rm -rf / was in your request") });
        var client = new JevDecisionClient(new HttpClient(failing), new DecisionModelRecord { Name = "jev", BaseUrl = "http://127.0.0.1:8000/v1" }, _ => default);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.DecideAsync("x", ["y"], default));

        Assert.Equal("the decision API answered HTTP 429", error.Message);
    }

    private sealed class StubHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(response);
    }

    [Theory]
    [InlineData("https://api.typesafe.ai/v1", true)]
    [InlineData("http://127.0.0.1:8000/v1", false)]
    public void OnlyAnApiOffThisPc_IsRemote(string url, bool remote)
    {
        Assert.Equal(remote, new DecisionModelRecord { Name = "x", Kind = DecisionModelKind.JevApi, BaseUrl = url }.IsRemote);
        Assert.False(new DecisionModelRecord { Name = "x", Kind = DecisionModelKind.LayaOnnx, BaseUrl = url }.IsRemote);
    }

    // ---- step 5 in the pipeline ---------------------------------------------------------------

    private static (FilterService Service, List<FunnelFilter> Filters, List<string> Seen) Pipeline(Func<string, double> probability)
    {
        var cache = new ConfigStoreCache(new InMemoryVault());
        var record = new DecisionModelRecord { Name = "fake", Kind = DecisionModelKind.ZeroShotOnnx, ModelPath = "x" };
        cache.Current.DecisionModels.Add(record);

        var seen = new List<string>();
        var factory = new EmbeddingClientFactory(null!)
        {
            DecisionOverride = _ => (state, statements, _) =>
            {
                seen.Add(state);
                return Task.FromResult(statements.Select(_ => probability(state)).ToArray());
            },
        };

        List<FunnelFilter> filters =
        [
            new() { Category = FilterCategory.Secrets, Level = FilterLevel.Balanced },
            new() { Category = FilterCategory.PersonalData, Level = FilterLevel.Balanced, Action = FilterAction.Watch },
            new() { Category = FilterCategory.Decision, Level = FilterLevel.Balanced, DecisionModelId = record.Id, DenyInstructions = ["The call deletes data."] },
        ];
        return (new FilterService(cache, factory), filters, seen);
    }

    private static List<KeyValuePair<string, JsonElement>> Args(object o) =>
        [.. JsonSerializer.SerializeToElement(o).EnumerateObject().Select(p => KeyValuePair.Create(p.Name, p.Value))];

    [Fact]
    public async Task Step5_BlocksWhenAStatementIsLikelyEnough_AndSaysWhich()
    {
        var (service, filters, _) = Pipeline(state => state.Contains("rm -rf") ? 0.93 : 0.04);

        var blocked = await service.RunAsync(filters, "shell", Args(new { command = "rm -rf /home/jane.doe/projects" }), default);
        var allowed = await service.RunAsync(filters, "weather", Args(new { city = "weather in Munich tomorrow" }), default);

        Assert.Equal(FilterCategory.Decision, blocked.StoppedAt);
        Assert.Contains(blocked.Hits, h => h.Category == FilterCategory.Decision && h.Detector.Contains("The call deletes data."));
        Assert.Equal(0.93, blocked.Decision!.Probability);
        Assert.Equal(0.75, blocked.Decision.Threshold);
        Assert.Null(allowed.StoppedAt);
        Assert.Equal(0.04, allowed.Decision!.Probability);
    }

    [Fact]
    public async Task Step5_ReadsTheCallWithEarlierMatchesReplaced_AndNeverSeesACallStep1Stopped()
    {
        var (service, filters, seen) = Pipeline(_ => 0.1);

        await service.RunAsync(filters, "mail", Args(new { body = "forward this to jane.doe@example.com please" }), default);
        await service.RunAsync(filters, "shell", Args(new { command = "export AWS_KEY=AKIA" + "Z7QW3XKPLM2N4R6T" }), default);

        var state = Assert.Single(seen);
        Assert.Contains("[Email address]", state);
        Assert.DoesNotContain("jane.doe", state);
        Assert.StartsWith("tool: mail", state);
    }

    [Fact]
    public async Task Step5_FailsClosed_WhenItsModelIsMissingOrBroken()
    {
        var (service, filters, _) = Pipeline(_ => throw new InvalidOperationException("model file is gone"));

        var broken = await service.RunAsync(filters, "shell", Args(new { command = "list the files here" }), default);
        filters[2].DecisionModelId = Guid.NewGuid();
        var missing = await service.RunAsync(filters, "shell", Args(new { command = "list the files here" }), default);

        Assert.Equal(FilterCategory.Decision, broken.StoppedAt);
        Assert.Contains(broken.Hits, h => h.Detector.Contains("model file is gone"));
        Assert.Contains(missing.Hits, h => h.Detector.Contains("no decision model is chosen"));
    }

    [Fact]
    public async Task Step5_UsesTheThresholdTheUserSet()
    {
        var (service, filters, _) = Pipeline(_ => 0.6);

        Assert.Null((await service.RunAsync(filters, "shell", Args(new { command = "list the files here" }), default)).StoppedAt);

        filters[2].Threshold = 0.55;
        Assert.Equal(FilterCategory.Decision, (await service.RunAsync(filters, "shell", Args(new { command = "list the files here" }), default)).StoppedAt);
    }
}
