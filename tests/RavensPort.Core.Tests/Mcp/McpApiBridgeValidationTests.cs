using System.Text.Json;
using RavensPort.Core.Mcp;
using RavensPort.Core.Models;
using RavensPort.Core.Proxy;

namespace RavensPort.Core.Tests.Mcp;

/// <summary>
/// A manifest is user-authored input that decides which HTTP call this app makes with the user's
/// own credential attached. These pin the rules that keep it inside its route, out of the headers
/// authorization rests on, and small enough that the vault note stays writable.
/// </summary>
public class McpApiBridgeValidationTests
{
    private static McpApiBridgeManifest Manifest(params McpApiBridgeTool[] tools) =>
        new() { Tools = [.. tools] };

    private static McpApiBridgeTool Tool(string name = "get_thing", McpApiBridgeRequest? request = null) => new()
    {
        Name = name,
        InputSchema = Schema("""{"type":"object","properties":{"id":{"type":"string"}}}"""),
        Request = request ?? new McpApiBridgeRequest { Method = "GET", Path = "/things/{id}" },
    };

    private static JsonElement Schema(string json) => JsonDocument.Parse(json).RootElement.Clone();

    // ---- the record ------------------------------------------------------------------------

    [Theory]
    [InlineData("github")]
    [InlineData("task-tracker")]
    [InlineData("a1")]
    public void AcceptsReasonableSlugs(string slug) =>
        Assert.Null(McpApiBridgeValidation.ValidateSlug(slug, []));

    [Theory]
    [InlineData("")]
    [InlineData("GitHub")]
    [InlineData("with space")]
    [InlineData("with/slash")]
    [InlineData("with_underscore")]
    public void RejectsSlugsThatWouldNotSurviveAUrlPath(string slug) =>
        Assert.NotNull(McpApiBridgeValidation.ValidateSlug(slug, []));

    [Fact]
    public void RejectsADuplicateSlugButNotTheBridgesOwn()
    {
        var existing = new[] { new McpApiBridgeRecord { Name = "GitHub", Slug = "github" } };

        Assert.NotNull(McpApiBridgeValidation.ValidateSlug("github", existing));
        Assert.NotNull(McpApiBridgeValidation.ValidateSlug("GITHUB", existing));
        Assert.Null(McpApiBridgeValidation.ValidateSlug("github", existing, existing[0].Id));
    }

    // ---- paths: the escape surface ----------------------------------------------------------

    [Theory]
    [InlineData("/things")]
    [InlineData("/things/{id}")]
    [InlineData("/a/b/c/{x}/d")]
    public void AcceptsPathsThatStayInsideTheRoute(string path) =>
        Assert.Null(McpApiBridgeValidation.ValidatePathTemplate(path));

    [Theory]
    [InlineData("things")]                     // not rooted, so not relative to the prefix
    [InlineData("/../secrets")]                // climbs out of the route prefix
    [InlineData("/a/../../b")]
    [InlineData("//evil.example.com/x")]       // protocol-relative: another host entirely
    [InlineData("https://evil.example.com/x")]
    [InlineData("/things%2f..%2fsecrets")]     // decoded after the '..' check would have run
    [InlineData("/things?q=1")]                // the query map owns this
    [InlineData("/things#frag")]
    [InlineData("/things\\other")]
    [InlineData("/things /x")]
    public void RefusesPathsThatCouldLeaveTheRoute(string path) =>
        Assert.NotNull(McpApiBridgeValidation.ValidatePathTemplate(path));

    [Theory]
    [InlineData("/things/{id")]
    [InlineData("/things/id}")]
    [InlineData("/things/{}")]
    [InlineData("/things/{a{b}}")]
    [InlineData("/things/{a-b}")]
    public void RefusesMalformedPlaceholders(string path) =>
        Assert.NotNull(McpApiBridgeValidation.ValidatePathTemplate(path));

    // ---- headers -----------------------------------------------------------------------------

    [Theory]
    [InlineData("Authorization")]
    [InlineData("authorization")]
    [InlineData("Cookie")]
    [InlineData("Content-Type")]
    [InlineData("Host")]
    [InlineData("Content-Length")]
    [InlineData("Transfer-Encoding")]
    public void RefusesHeadersThisPipelineOwns(string name)
    {
        var error = McpApiBridgeValidation.ValidateHeaders(new Dictionary<string, string> { [name] = "x" });

        Assert.NotNull(error);
        Assert.Contains(name, error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RefusesTheProxysOwnInternalHeaders()
    {
        foreach (var name in new[]
                 {
                     LocalAccessGuard.ApiKeyHeaderName,
                     LocalAccessGuard.FunnelHopHeaderName,
                     LocalAccessGuard.BridgeHopHeaderName,
                 })
        {
            Assert.NotNull(McpApiBridgeValidation.ValidateHeaders(new Dictionary<string, string> { [name] = "1" }));
        }
    }

    [Fact]
    public void AcceptsAnOrdinaryHeaderAndRefusesOneCarryingANewline()
    {
        Assert.Null(McpApiBridgeValidation.ValidateHeaders(
            new Dictionary<string, string> { ["Accept"] = "application/json" }));

        // Request splitting, aimed at the upstream.
        Assert.NotNull(McpApiBridgeValidation.ValidateHeaders(
            new Dictionary<string, string> { ["Accept"] = "application/json\r\nX-Evil: 1" }));

        Assert.NotNull(McpApiBridgeValidation.ValidateHeaders(
            new Dictionary<string, string> { ["Not A Header"] = "x" }));
    }

    // ---- bodies ------------------------------------------------------------------------------

    [Fact]
    public void RefusesABodyOnAMethodThatHasNoUseForOne()
    {
        var request = new McpApiBridgeRequest
        {
            Method = "GET",
            Path = "/things",
            BodyMode = McpApiBridgeBodyMode.Arguments,
        };

        Assert.NotNull(McpApiBridgeValidation.ValidateBody(request));
    }

    [Fact]
    public void RefusesATemplateBodyThatIsMissingOrTheWrongShape()
    {
        Assert.NotNull(McpApiBridgeValidation.ValidateBody(new McpApiBridgeRequest
        {
            Method = "POST",
            BodyMode = McpApiBridgeBodyMode.Template,
        }));

        Assert.NotNull(McpApiBridgeValidation.ValidateBody(new McpApiBridgeRequest
        {
            Method = "POST",
            BodyMode = McpApiBridgeBodyMode.Template,
            Body = Schema("\"just a string\""),
        }));

        Assert.Null(McpApiBridgeValidation.ValidateBody(new McpApiBridgeRequest
        {
            Method = "POST",
            BodyMode = McpApiBridgeBodyMode.Template,
            Body = Schema("""{"raw":"{value}"}"""),
        }));
    }

    [Fact]
    public void RefusesAMalformedPlaceholderNestedInsideABody()
    {
        var request = new McpApiBridgeRequest
        {
            Method = "POST",
            BodyMode = McpApiBridgeBodyMode.Template,
            Body = Schema("""{"outer":{"inner":["ok","{broken"]}}"""),
        };

        Assert.NotNull(McpApiBridgeValidation.ValidateBody(request));
    }

    // ---- tools -------------------------------------------------------------------------------

    [Fact]
    public void RefusesAToolNameThatWouldNotSurviveBeingPrefixedByAFunnel()
    {
        var tool = Tool(new string('a', McpApiBridgeValidation.MaxToolNameLength + 1));

        Assert.NotNull(McpApiBridgeValidation.ValidateTool(tool));
        Assert.Null(McpApiBridgeValidation.ValidateTool(Tool(new string('a', McpApiBridgeValidation.MaxToolNameLength))));
    }

    /// <summary>
    /// The cap is derived, not written down, and this is what makes that worth doing: if the two
    /// ever drift, a funnel silently drops the tool rather than reporting anything.
    /// </summary>
    [Fact]
    public void TheToolNameCapLeavesRoomForAnAliasAndTheSeparator()
    {
        Assert.Equal(
            McpNameMapper.MaxNameLength,
            McpApiBridgeValidation.MaxToolNameLength
            + McpFunnelValidation.MaxAliasLength
            + McpNameMapper.Separator.Length);

        var longestName = new string('a', McpApiBridgeValidation.MaxToolNameLength);
        var longestAlias = new string('b', McpFunnelValidation.MaxAliasLength);

        Assert.False(McpNameMapper.IsTruncated(longestAlias, longestName));
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("has.dot")]
    [InlineData("has/slash")]
    [InlineData("")]
    public void RefusesToolNamesOutsideTheMcpCharset(string name) =>
        Assert.NotNull(McpApiBridgeValidation.ValidateTool(Tool(name)));

    [Fact]
    public void RefusesTwoToolsWithTheSameNameEvenInDifferentCase()
    {
        var error = McpApiBridgeValidation.ValidateManifest(Manifest(Tool("get_thing"), Tool("GET_THING")));

        Assert.NotNull(error);
        Assert.Contains("unique", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RefusesASchemaTheSdkWouldThrowOn()
    {
        var tool = Tool();
        tool.InputSchema = Schema("""{"type":"array"}""");

        Assert.NotNull(McpApiBridgeValidation.ValidateTool(tool));
    }

    [Fact]
    public void AcceptsAToolThatDeclaresNoSchemaAtAll()
    {
        // No "inputSchema" key deserializes to Undefined, which normalizes to the empty object
        // schema. A tool that takes no arguments is a real thing.
        var manifest = JsonSerializer.Deserialize<McpApiBridgeManifest>(
            """{"version":1,"tools":[{"name":"ping","request":{"method":"GET","path":"/ping"}}]}""");

        Assert.NotNull(manifest);
        McpApiBridgeSchema.Normalize(manifest);

        Assert.Null(McpApiBridgeValidation.ValidateManifest(manifest));
        Assert.Equal(JsonValueKind.Object, manifest.Tools[0].InputSchema.ValueKind);
    }

    // ---- variants ----------------------------------------------------------------------------

    private static McpApiBridgeTool VariantTool(string schema, params string[] variantKeys)
    {
        var tool = new McpApiBridgeTool
        {
            Name = "list_by_state",
            InputSchema = Schema(schema),
            VariantBy = "state",
        };

        foreach (var key in variantKeys)
        {
            tool.Variants[key] = new McpApiBridgeRequest { Method = "GET", Path = $"/{key}" };
        }

        return tool;
    }

    private const string StateSchema =
        """{"type":"object","properties":{"state":{"type":"string","enum":["open","done"]}}}""";

    [Fact]
    public void AcceptsVariantsTheSchemaAdvertisesExactly() =>
        Assert.Null(McpApiBridgeValidation.ValidateTool(VariantTool(StateSchema, "open", "done")));

    [Fact]
    public void RefusesAVariantTheModelWouldNeverBeToldAbout()
    {
        // "archived" exists but the enum does not offer it, so nothing would ever call it.
        var error = McpApiBridgeValidation.ValidateTool(VariantTool(StateSchema, "open", "done", "archived"));

        Assert.NotNull(error);
        Assert.Contains("archived", error, StringComparison.Ordinal);
    }

    [Fact]
    public void RefusesAnAdvertisedValueWithNoVariantBehindIt()
    {
        var error = McpApiBridgeValidation.ValidateTool(VariantTool(StateSchema, "open"));

        Assert.NotNull(error);
    }

    [Fact]
    public void RefusesVariantsWithNoSelectorOrAnUndeclaredOne()
    {
        var noSelector = VariantTool(StateSchema, "open", "done");
        noSelector.VariantBy = null;
        Assert.NotNull(McpApiBridgeValidation.ValidateTool(noSelector));

        var undeclared = VariantTool(StateSchema, "open", "done");
        undeclared.VariantBy = "missing";
        Assert.NotNull(McpApiBridgeValidation.ValidateTool(undeclared));

        var noEnum = VariantTool("""{"type":"object","properties":{"state":{"type":"string"}}}""", "open", "done");
        Assert.NotNull(McpApiBridgeValidation.ValidateTool(noEnum));
    }

    [Fact]
    public void RefusesAToolThatIsBothOneRequestAndSeveral()
    {
        var tool = VariantTool(StateSchema, "open", "done");
        tool.Request = new McpApiBridgeRequest { Method = "GET", Path = "/things" };

        Assert.NotNull(McpApiBridgeValidation.ValidateTool(tool));
    }

    [Fact]
    public void RefusesASingleVariant()
    {
        var single = new McpApiBridgeTool
        {
            Name = "list_by_state",
            InputSchema = Schema("""{"type":"object","properties":{"state":{"type":"string","enum":["open"]}}}"""),
            VariantBy = "state",
        };

        single.Variants["open"] = new McpApiBridgeRequest { Method = "GET", Path = "/open" };

        Assert.NotNull(McpApiBridgeValidation.ValidateTool(single));
    }

    [Fact]
    public void ValidatesEveryVariantsRequestNotJustTheFirst()
    {
        var tool = VariantTool(StateSchema, "open", "done");
        tool.Variants["done"] = new McpApiBridgeRequest { Method = "GET", Path = "/../secrets" };

        var error = McpApiBridgeValidation.ValidateTool(tool);

        Assert.NotNull(error);
        Assert.Contains("done", error, StringComparison.Ordinal);
    }

    // ---- prompts and skills ------------------------------------------------------------------

    [Fact]
    public void RefusesAPromptUsingAnArgumentItNeverDeclared()
    {
        var prompt = new McpApiBridgePrompt
        {
            Name = "review",
            Messages = [new McpApiBridgePromptMessage { Role = "user", Content = "Review since {since}." }],
        };

        Assert.NotNull(McpApiBridgeValidation.ValidatePrompt(prompt));

        prompt.Arguments.Add(new McpApiBridgePromptArgument { Name = "since" });
        Assert.Null(McpApiBridgeValidation.ValidatePrompt(prompt));
    }

    [Fact]
    public void RefusesAPromptMessageWithAnUnknownRole()
    {
        var prompt = new McpApiBridgePrompt
        {
            Name = "review",
            Messages = [new McpApiBridgePromptMessage { Role = "system", Content = "Go." }],
        };

        Assert.NotNull(McpApiBridgeValidation.ValidatePrompt(prompt));
    }

    [Fact]
    public void RefusesASkillNameThatWouldNotSurviveAResourceUri()
    {
        Assert.NotNull(McpApiBridgeValidation.ValidateSkill(new McpApiBridgeSkill { Name = "has/slash", Content = "x" }));
        Assert.NotNull(McpApiBridgeValidation.ValidateSkill(new McpApiBridgeSkill { Name = "ok", Content = "" }));
        Assert.Null(McpApiBridgeValidation.ValidateSkill(new McpApiBridgeSkill { Name = "task-triage", Content = "# x" }));
    }

    // ---- manifest as a whole -------------------------------------------------------------------

    [Fact]
    public void RefusesAManifestFromALaterBuild()
    {
        var manifest = Manifest(Tool());
        manifest.Version = McpApiBridgeManifest.CurrentVersion + 1;

        Assert.NotNull(McpApiBridgeValidation.ValidateManifest(manifest));
    }

    [Fact]
    public void RefusesMoreToolsThanAnAgentCouldChooseBetween()
    {
        var manifest = Manifest([.. Enumerable.Range(0, McpApiBridgeValidation.MaxTools + 1)
            .Select(i => Tool($"tool_{i}"))]);

        Assert.NotNull(McpApiBridgeValidation.ValidateManifest(manifest));
    }

    [Fact]
    public void RefusesAManifestTooLargeForTheNoteToCarry()
    {
        var manifest = Manifest(Tool());

        manifest.Skills.Add(new McpApiBridgeSkill
        {
            Name = "huge",
            Content = new string('x', McpApiBridgeValidation.MaxManifestBytes + 1),
        });

        var error = McpApiBridgeValidation.ValidateManifest(manifest);

        Assert.NotNull(error);
        Assert.Contains("KB", error, StringComparison.Ordinal);
    }

    [Fact]
    public void RefusesAnImportThatWouldPushTheWholeNotePastItsBudget()
    {
        var store = new ConfigStore();

        // Several bridges, each individually fine, and together still inside the budget — one
        // more is what tips it over.
        for (var i = 0; i < 4; i++)
        {
            var manifest = Manifest(Tool());
            manifest.Skills.Add(new McpApiBridgeSkill
            {
                Name = $"skill-{i}",
                Content = new string('x', McpApiBridgeValidation.MaxManifestBytes - 2048),
            });

            store.McpApiBridges.Add(new McpApiBridgeRecord
            {
                Name = $"bridge {i}",
                Slug = $"bridge-{i}",
                Manifest = manifest,
            });
        }

        var incoming = Manifest(Tool());
        incoming.Skills.Add(new McpApiBridgeSkill
        {
            Name = "one-more",
            Content = new string('x', McpApiBridgeValidation.MaxManifestBytes - 2048),
        });

        Assert.NotNull(McpApiBridgeValidation.ValidateNoteBudget(store, Guid.NewGuid(), incoming));

        // Re-importing over a bridge's own manifest does not read as doubling it.
        Assert.Null(McpApiBridgeValidation.ValidateNoteBudget(
            store, store.McpApiBridges[0].Id, store.McpApiBridges[0].Manifest));
    }

    [Fact]
    public void ReadsAManifestWithCommentsAndTrailingCommas()
    {
        const string json = """
            {
              // hand-written, so both of these are tolerated
              "version": 1,
              "tools": [
                { "name": "ping", "request": { "method": "GET", "path": "/ping" } },
              ],
            }
            """;

        Assert.Null(McpApiBridgeValidation.TryReadManifest(json, out var manifest));
        Assert.NotNull(manifest);
        Assert.Single(manifest.Tools);
    }

    [Fact]
    public void ReportsBadJsonAsBadJsonRatherThanAsAnEmptyManifest()
    {
        var error = McpApiBridgeValidation.TryReadManifest("{ not json", out var manifest);

        Assert.NotNull(error);
        Assert.Null(manifest);
    }

    /// <summary>
    /// The sample ships in the app and is the first manifest most users will edit. If it stops
    /// validating, the feature's front door is broken.
    /// </summary>
    [Fact]
    public void TheShippedSampleManifestValidates()
    {
        var error = McpApiBridgeValidation.TryReadManifest(McpApiBridgeSample.Read(), out var manifest);

        Assert.Null(error);
        Assert.NotNull(manifest);

        // And it demonstrates every feature, which is the only reason to ship a sample at all.
        Assert.Contains(manifest.Tools, t => t.HasVariants);
        Assert.Contains(manifest.Tools, t => t.Request?.BodyMode == McpApiBridgeBodyMode.Template);
        Assert.NotNull(manifest.Instructions);
        Assert.NotEmpty(manifest.Prompts);
        Assert.NotEmpty(manifest.Skills);
    }
}
