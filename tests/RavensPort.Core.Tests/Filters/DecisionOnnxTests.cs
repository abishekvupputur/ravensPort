using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using RavensPort.Core.Filters;
using RavensPort.Core.Models;

namespace RavensPort.Core.Tests.Filters;

/// <summary>Laya and zero-shot models on tiny ONNX graphs, the decision factory, and how files are found.</summary>
public class DecisionOnnxTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ravensport-laya-").FullName;

    public void Dispose()
    {
        Directory.Delete(_dir, recursive: true);
        GC.SuppressFinalize(this);
    }

    private const string Tokenizer = """
        {
          "version": "1.0", "truncation": null, "padding": null,
          "added_tokens": [
            {"id":0,"content":"[PAD]","single_word":false,"lstrip":false,"rstrip":false,"normalized":false,"special":true},
            {"id":1,"content":"[UNK]","single_word":false,"lstrip":false,"rstrip":false,"normalized":false,"special":true},
            {"id":2,"content":"[CLS]","single_word":false,"lstrip":false,"rstrip":false,"normalized":false,"special":true},
            {"id":3,"content":"[SEP]","single_word":false,"lstrip":false,"rstrip":false,"normalized":false,"special":true},
            {"id":4,"content":"[MASK]","single_word":false,"lstrip":false,"rstrip":false,"normalized":false,"special":true}
          ],
          "normalizer": null,
          "pre_tokenizer": {"type": "Whitespace"},
          "post_processor": {"type": "BertProcessing", "sep": ["[SEP]", 3], "cls": ["[CLS]", 2]},
          "decoder": null,
          "model": {"type": "WordLevel", "vocab": {"[PAD]":0,"[UNK]":1,"[CLS]":2,"[SEP]":3,"[MASK]":4,"delete":5,"files":6}, "unk_token": "[UNK]"}
        }
        """;

    private static readonly (string, int, string[])[] HeadInputs =
    [
        ("marker_pos", TinyOnnx.Long, ["b", "m"]),
        ("marker_mask", TinyOnnx.Bool, ["b", "m"]),
    ];

    private void WriteLayaFiles(string tokenizerFolder)
    {
        Directory.CreateDirectory(tokenizerFolder);
        File.WriteAllText(Path.Combine(tokenizerFolder, "tokenizer.json"), Tokenizer);
        File.WriteAllText(Path.Combine(tokenizerFolder, "tokenizer_config.json"),
            """{"cls_token":"[CLS]","sep_token":{"content":"[SEP]"},"mask_token":"[MASK]","pad_token":"[PAD]"}""");
        File.WriteAllText(Path.Combine(_dir, "rl_agent_config.json"),
            """{"max_len":128,"head_max_len":64,"temperature":[1.0,1.0,2.0],"temperature_by_options":{"noul:2":10.0}}""");
    }

    /// <summary>The model's logits are the two marker positions, so P(true) is predictable from the sequence.</summary>
    private static double Expected(string tokenizerJson, string statement, string state, double temperature)
    {
        using var tokens = new HfTokens(tokenizerJson);
        var (_, markers) = DecisionInputs.LayaSequence(tokens.Encode, "[MASK]", 2, 3, 4, statement, state, 128, 64);
        return DecisionInputs.Softmax([markers[0], markers[1]], temperature)[1];
    }

    [Fact]
    public async Task AFusedLayaExport_RunsTheNoulQuestion_WithTheCheckpointsTemperature()
    {
        WriteLayaFiles(Path.Combine(_dir, "tokenizer"));
        TinyOnnx.WriteCast(Path.Combine(_dir, "model.onnx"),
            [("input_ids", TinyOnnx.Long, ["b", "n"]), ("attention_mask", TinyOnnx.Long, ["b", "n"]), .. HeadInputs, ("qtype", TinyOnnx.Long, ["b"])],
            "marker_pos", "logits", ["b", "m"]);

        using var model = new LayaDecisionModel(new DecisionModelRecord { Name = "laya", Kind = DecisionModelKind.LayaOnnx, ModelPath = _dir }, null);
        var p = await model.DecideAsync("delete files", ["The call deletes data.", "delete"], default);

        Assert.Equal("CPU", model.UsedDevice);
        Assert.Equal(Expected(Path.Combine(_dir, "tokenizer", "tokenizer.json"), "The call deletes data.", "delete files", 5.0), p[0], 6); // 10 is capped at 5, as Laya does
        Assert.Equal(2, p.Length);
    }

    [Fact]
    public async Task ASplitLayaExport_RunsTheEncoderThenTheHead()
    {
        WriteLayaFiles(_dir);
        TinyOnnx.WriteCast(Path.Combine(_dir, "encoder.onnx"),
            [("input_ids", TinyOnnx.Long, ["b", "n"]), ("attention_mask", TinyOnnx.Long, ["b", "n"])], "input_ids", "hidden", ["b", "n"]);
        TinyOnnx.WriteCast(Path.Combine(_dir, "head.onnx"),
            [("hidden_states", TinyOnnx.Single, ["b", "n"]), .. HeadInputs, ("qtype", TinyOnnx.Long, ["b", "one"])],
            "marker_pos", "logits", ["b", "m"]);

        using var model = new LayaDecisionModel(new DecisionModelRecord { Name = "laya", Kind = DecisionModelKind.LayaOnnx, ModelPath = Path.Combine(_dir, "head.onnx") }, null);
        var p = await model.DecideAsync("delete files", ["The call deletes data."], default);

        Assert.Equal(Expected(Path.Combine(_dir, "tokenizer.json"), "The call deletes data.", "delete files", 5.0), p[0], 6); // 10 is capped at 5, as Laya does
    }

    [Fact]
    public void ALayaFolder_SaysWhatIsMissing()
    {
        string Error(string path) => Assert.Throws<InvalidOperationException>(() =>
            new LayaDecisionModel(new DecisionModelRecord { Name = "laya", ModelPath = path }, null)).Message;

        Assert.Contains("nothing exists", Error(Path.Combine(_dir, "nope")));
        Assert.Contains("no tokenizer.json", Error(_dir));

        File.WriteAllText(Path.Combine(_dir, "tokenizer.json"), Tokenizer);
        Assert.Contains("rl_agent_config.json", Error(_dir));

        File.WriteAllText(Path.Combine(_dir, "rl_agent_config.json"), "{}");
        Assert.Contains("no model.onnx", Error(_dir));
    }

    [Fact]
    public void AZeroShotFolder_SaysWhatIsMissing()
    {
        string Error(string path) => Assert.ThrowsAny<Exception>(() =>
            new ZeroShotDecisionModel(new DecisionModelRecord { Name = "zs", ModelPath = path }, null)).Message;

        Directory.CreateDirectory(Path.Combine(_dir, "onnx"));
        Assert.Contains("no .onnx file", Error(_dir));

        TinyOnnx.Write(Path.Combine(_dir, "onnx"), outputName: "logits");
        Assert.Contains("no tokenizer.json", Error(_dir));
    }

    [Fact]
    public async Task TheFactory_LoadsAZeroShotModelOnce_AndBuildsAJevClientWithoutCallingIt()
    {
        TinyOnnx.Write(_dir, outputName: "logits");
        File.WriteAllText(Path.Combine(_dir, "tokenizer.json"), Tokenizer);
        File.WriteAllText(Path.Combine(_dir, "config.json"), """{"id2label":{"0":"entailment","1":"not_entailment"},"max_position_embeddings":64}""");

        using var factory = new EmbeddingClientFactory(null!);
        var record = new DecisionModelRecord { Name = "zs", Kind = DecisionModelKind.ZeroShotOnnx, ModelPath = Path.Combine(_dir, "model.onnx") };

        var p = await factory.GetDecisionModel(record)("delete files", ["files"], default);
        Assert.InRange(p[0], 0, 1);
        Assert.Equal("CPU", factory.DeviceOf(record.Id));

        Assert.NotNull(factory.GetDecisionModel(new DecisionModelRecord { Name = "jev", Kind = DecisionModelKind.JevApi, BaseUrl = "http://127.0.0.1:8000/v1" }));
    }

    [Fact]
    public void APair_RefusesWhatItCannotJoin()
    {
        Assert.Throws<InvalidOperationException>(() => DecisionInputs.Pair([], [1], [2], 16));
        Assert.Throws<InvalidOperationException>(() => DecisionInputs.Pair(JsonNode.Parse("""{"post_processor":{"type":"ByteLevel"}}""")!.AsObject(), [1], [2], 16));
        Assert.Throws<InvalidOperationException>(() => DecisionInputs.Pair(JsonNode.Parse("""{"post_processor":{"type":"Sequence","processors":[{"type":"ByteLevel"}]}}""")!.AsObject(), [1], [2], 16));
        Assert.Throws<InvalidOperationException>(() => DecisionInputs.Pair(JsonNode.Parse("""{"post_processor":{"type":"BertProcessing","sep":["[SEP]",3],"cls":["[CLS]",2]}}""")!.AsObject(), [1], [2, 2, 2, 2], 6));
    }

    [Fact]
    public void LayasSequence_RefusesAStatementTooLongForTheQuestionBudget_AndShortensLongOptions()
    {
        static int[] Each(string s) => [.. s.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(_ => 9)];

        Assert.Throws<InvalidOperationException>(() =>
            DecisionInputs.LayaSequence(Each, "[MASK]", 2, 3, 4, string.Join(' ', Enumerable.Repeat("word", 50)), "state", maxLen: 12, headMaxLen: 10));

        var (ids, markers) = DecisionInputs.LayaSequence(Each, "[MASK]", 2, 3, 4, "short", "state", maxLen: 64, headMaxLen: 20);
        Assert.Equal(2, markers.Length);
        Assert.All(markers, m => Assert.Equal(4, ids[m]));
    }

    // ---- a route's request, read as texts ------------------------------------------------------

    private static HttpRequest Request(string? body, string? type, string query = "")
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString(query);
        if (body is not null)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(body);
            context.Request.Body = new MemoryStream(bytes);
            context.Request.ContentLength = bytes.Length;
        }
        context.Request.ContentType = type;
        return context.Request;
    }

    private static async Task<Dictionary<string, string>> Read(HttpRequest request) =>
        (await RouteFilters.ReadAsync(request, default)).ToDictionary(a => a.Key, a => a.Value.ValueKind == JsonValueKind.String ? a.Value.GetString()! : a.Value.GetRawText());

    [Fact]
    public async Task ARoutesRequest_IsReadFromItsQueryAndWhateverBodyItHas()
    {
        Assert.Equal("jane", (await Read(Request(null, null, "?user=jane")))["query.user"]);
        Assert.Equal("hunter2", (await Read(Request("""{"password":"hunter2"}""", "application/json")))["password"]);
        Assert.Equal("""["a","b"]""", (await Read(Request("""["a","b"]""", "application/json")))["body"]);
        Assert.Equal("{not json", (await Read(Request("{not json", "application/json")))["body"]);
        var form = await Read(Request("user=jane+doe&password=hunter%212&flag", "application/x-www-form-urlencoded"));
        Assert.Equal(("jane doe", "hunter!2", ""), (form["user"], form["password"], form["flag"]));
        Assert.Equal("plain words", (await Read(Request("plain words", "text/plain")))["body"]);
    }

    [Fact]
    public async Task ARequestTooLargeToCheck_IsRefused()
    {
        var request = Request("x", "text/plain");
        request.ContentLength = 2L * 1024 * 1024;

        await Assert.ThrowsAsync<InvalidDataException>(() => RouteFilters.ReadAsync(request, default));
    }

    // ---- an embedding API ----------------------------------------------------------------------

    private sealed class Answer(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public string? Sent { get; private set; }
        public string? Auth { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Sent = await request.Content!.ReadAsStringAsync(ct);
            Auth = request.Headers.Authorization?.ToString();
            return new HttpResponseMessage(status) { Content = new StringContent(body) };
        }
    }

    [Fact]
    public async Task AnEmbeddingApi_IsAskedInOpenAisShape_WithVoyagesInputType_AndItsKey()
    {
        var api = new Answer(HttpStatusCode.OK, """{"data":[{"index":1,"embedding":[0,2]},{"index":0,"embedding":[3,4]}]}""");
        var record = new EmbeddingProviderRecord { Name = "voyage", Kind = EmbeddingProviderKind.VoyageAI, BaseUrl = "https://api.example.com/v1", Model = "voyage-4-lite", CredentialId = Guid.NewGuid() };
        var client = new HttpEmbeddingClient(new HttpClient(api), record, _ => ValueTask.FromResult<string?>("test-key"));

        var vectors = await client.EmbedAsync(["a", "b"], EmbeddingRole.Document, default);

        Assert.Equal([0.6f, 0.8f], vectors[0]);
        Assert.Equal([0f, 1f], vectors[1]);
        Assert.Contains("\"input_type\":\"document\"", api.Sent);
        Assert.Equal("Bearer test-key", api.Auth);
    }

    [Fact]
    public async Task AnEmbeddingApi_ThatFailsMiscountsOrHasNoKey_IsAnError()
    {
        var record = new EmbeddingProviderRecord { Name = "x", Kind = EmbeddingProviderKind.OpenAI, BaseUrl = "https://api.example.com/v1", Model = "m", CredentialId = Guid.NewGuid() };

        var failing = new HttpEmbeddingClient(new HttpClient(new Answer(HttpStatusCode.Unauthorized, "secret text echoed")), record, _ => ValueTask.FromResult<string?>("k"));
        Assert.Equal("the embedding API answered HTTP 401", (await Assert.ThrowsAsync<InvalidOperationException>(() => failing.EmbedAsync(["a"], EmbeddingRole.Query, default))).Message);

        var short1 = new HttpEmbeddingClient(new HttpClient(new Answer(HttpStatusCode.OK, """{"data":[]}""")), record, _ => ValueTask.FromResult<string?>("k"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => short1.EmbedAsync(["a"], EmbeddingRole.Query, default));

        var keyless = new HttpEmbeddingClient(new HttpClient(new Answer(HttpStatusCode.OK, "{}")), record, _ => ValueTask.FromResult<string?>(null));
        Assert.Contains("no key", (await Assert.ThrowsAsync<InvalidOperationException>(() => keyless.EmbedAsync(["a"], EmbeddingRole.Query, default))).Message);
    }

    // ---- model files ---------------------------------------------------------------------------

    [Fact]
    public void GlinersFiles_AreFoundFromAFileOrAFolder_OrTheMissingOnesNamed()
    {
        Assert.Null(GlinerFiles.Find(" ", out var empty));
        Assert.NotNull(empty);
        Assert.Null(GlinerFiles.Find(Path.Combine(_dir, "nope"), out var nothing));
        Assert.Equal("Nothing exists at that path.", nothing);

        var text = Path.Combine(_dir, "notes.txt");
        File.WriteAllText(text, "");
        Assert.Null(GlinerFiles.Find(text, out var notOnnx));
        Assert.Equal("That file is not a .onnx model.", notOnnx);

        Assert.Null(GlinerFiles.Find(_dir, out var noModel));
        Assert.Contains("No model.onnx", noModel);

        var onnx = Directory.CreateDirectory(Path.Combine(_dir, "onnx")).FullName;
        File.WriteAllText(Path.Combine(onnx, "model.onnx"), "");
        Assert.Null(GlinerFiles.Find(Path.Combine(onnx, "model.onnx"), out var noTokenizer));
        Assert.Contains("spm.model", noTokenizer);

        File.WriteAllText(Path.Combine(_dir, "spm.model"), "");
        File.WriteAllText(Path.Combine(_dir, "gliner_config.json"), "{}");
        Assert.Equal(_dir, GlinerFiles.Find(Path.Combine(onnx, "model.onnx"), out _)!.Folder);
        Assert.Equal(Path.Combine(_dir, "onnx", "model.onnx"), GlinerFiles.Find(_dir, out _)!.Model);
    }
}
