using System.Text.Json;
using RavensPort.Core.Mcp;
using RavensPort.Core.Models;

namespace RavensPort.Core.Tests.Mcp;

/// <summary>
/// The templating matrix, without a host.
///
/// This is where a manifest's text becomes a real HTTP request, so it is where "absent means omit,
/// except in the path" either holds or does not — and where an argument gets its one chance to
/// escape the route it was called through.
/// </summary>
public class McpApiBridgeRequestBuilderTests
{
    private static Dictionary<string, JsonElement> Args(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static McpApiBridgeTool Tool(McpApiBridgeRequest request) =>
        new() { Name = "call_it", Request = request };

    private static McpApiBridgeRequestBuilder.BuiltRequest Build(McpApiBridgeTool tool, string arguments)
    {
        var (request, error) = McpApiBridgeRequestBuilder.Build(tool, Args(arguments));

        Assert.Null(error);
        Assert.NotNull(request);

        return request;
    }

    private static string Failure(McpApiBridgeTool tool, string arguments)
    {
        var (request, error) = McpApiBridgeRequestBuilder.Build(tool, Args(arguments));

        Assert.Null(request);
        Assert.NotNull(error);

        return error;
    }

    // ---- path ------------------------------------------------------------------------------

    [Fact]
    public void FillsAPathPlaceholderAndEscapesIt()
    {
        var tool = Tool(new McpApiBridgeRequest { Method = "GET", Path = "/things/{id}" });

        Assert.Equal("/things/abc", Build(tool, """{"id":"abc"}""").RelativePath);
        Assert.Equal("/things/a%20b", Build(tool, """{"id":"a b"}""").RelativePath);
    }

    /// <summary>
    /// The reason path arguments are escaped one placeholder at a time rather than over the whole
    /// path: the template's slashes are structure, an argument's are data.
    /// </summary>
    [Fact]
    public void AnArgumentCannotClimbOutOfTheRoute()
    {
        var tool = Tool(new McpApiBridgeRequest { Method = "GET", Path = "/things/{id}" });

        var path = Build(tool, """{"id":"../../secrets"}""").RelativePath;

        Assert.Equal("/things/..%2F..%2Fsecrets", path);
        Assert.DoesNotContain("/../", path, StringComparison.Ordinal);
    }

    [Fact]
    public void RefusesToCallAPathWithAHoleInIt()
    {
        var tool = Tool(new McpApiBridgeRequest { Method = "GET", Path = "/things/{id}" });

        var error = Failure(tool, """{"other":"x"}""");

        Assert.Contains("id", error, StringComparison.Ordinal);
        Assert.Contains("required", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RefusesAnObjectWhereASingleValueBelongs()
    {
        var tool = Tool(new McpApiBridgeRequest { Method = "GET", Path = "/things/{id}" });

        Assert.Contains("single value", Failure(tool, """{"id":{"nested":1}}"""), StringComparison.Ordinal);
        Assert.Contains("single value", Failure(tool, """{"id":[1,2]}"""), StringComparison.Ordinal);
    }

    [Fact]
    public void AcceptsNumbersAndBooleansAsPathValues()
    {
        var tool = Tool(new McpApiBridgeRequest { Method = "GET", Path = "/things/{id}" });

        Assert.Equal("/things/42", Build(tool, """{"id":42}""").RelativePath);
        Assert.Equal("/things/true", Build(tool, """{"id":true}""").RelativePath);
    }

    // ---- query -----------------------------------------------------------------------------

    [Fact]
    public void OmitsAQueryParameterWhoseArgumentWasNotSupplied()
    {
        var tool = Tool(new McpApiBridgeRequest
        {
            Method = "GET",
            Path = "/things",
            Query = { ["q"] = "{query}", ["limit"] = "{limit}", ["format"] = "json" },
        });

        var built = Build(tool, """{"query":"cats"}""");

        Assert.Contains("q=cats", built.Query, StringComparison.Ordinal);
        Assert.Contains("format=json", built.Query, StringComparison.Ordinal);
        Assert.DoesNotContain("limit", built.Query, StringComparison.Ordinal);
    }

    [Fact]
    public void EscapesQueryValues()
    {
        var tool = Tool(new McpApiBridgeRequest { Method = "GET", Path = "/things", Query = { ["q"] = "{query}" } });

        Assert.Contains("a%20b", Build(tool, """{"query":"a b"}""").Query, StringComparison.Ordinal);
    }

    // ---- headers ---------------------------------------------------------------------------

    [Fact]
    public void OmitsAHeaderWhoseArgumentWasNotSupplied()
    {
        var tool = Tool(new McpApiBridgeRequest
        {
            Method = "GET",
            Path = "/things",
            Headers = { ["Accept"] = "application/json", ["X-Trace"] = "{trace}" },
        });

        var built = Build(tool, """{}""");

        Assert.Single(built.Headers);
        Assert.Equal("Accept", built.Headers[0].Key);
    }

    [Fact]
    public void RefusesAHeaderValueAnArgumentSmuggledANewlineInto()
    {
        var tool = Tool(new McpApiBridgeRequest
        {
            Method = "GET",
            Path = "/things",
            Headers = { ["X-Trace"] = "{trace}" },
        });

        Assert.NotNull(Failure(tool, """{"trace":"ok\r\nX-Evil: 1"}"""));
    }

    // ---- bodies ----------------------------------------------------------------------------

    [Fact]
    public void SendsAWholeJsonValueWhenTheTemplateIsExactlyOnePlaceholder()
    {
        var tool = Tool(new McpApiBridgeRequest
        {
            Method = "POST",
            Path = "/things",
            BodyMode = McpApiBridgeBodyMode.Template,
            Body = Json("""{"title":"{title}","tags":"{tags}","count":"{count}"}"""),
        });

        var body = Build(tool, """{"title":"hi","tags":["a","b"],"count":3}""").Body;

        Assert.NotNull(body);
        Assert.Equal("""{"title":"hi","tags":["a","b"],"count":3}""", body);
    }

    [Fact]
    public void InterpolatesAPlaceholderThatIsOnlyPartOfAString()
    {
        var tool = Tool(new McpApiBridgeRequest
        {
            Method = "POST",
            Path = "/things",
            BodyMode = McpApiBridgeBodyMode.Template,
            Body = Json("""{"greeting":"hello {name}"}"""),
        });

        Assert.Equal("""{"greeting":"hello world"}""", Build(tool, """{"name":"world"}""").Body);
    }

    [Fact]
    public void DropsATemplatePropertyWhoseArgumentWasNotSupplied()
    {
        var tool = Tool(new McpApiBridgeRequest
        {
            Method = "POST",
            Path = "/things",
            BodyMode = McpApiBridgeBodyMode.Template,
            Body = Json("""{"title":"{title}","notes":"{notes}"}"""),
        });

        Assert.Equal("""{"title":"hi"}""", Build(tool, """{"title":"hi"}""").Body);
    }

    [Fact]
    public void SendsOnlyTheArgumentsNothingElseAlreadyCarried()
    {
        var tool = Tool(new McpApiBridgeRequest
        {
            Method = "PUT",
            Path = "/things/{id}",
            Query = { ["mode"] = "{mode}" },
            BodyMode = McpApiBridgeBodyMode.Arguments,
        });

        var built = Build(tool, """{"id":"5","mode":"fast","title":"hi","done":true}""");

        Assert.Equal("/things/5", built.RelativePath);
        Assert.Equal("""{"title":"hi","done":true}""", built.Body);
    }

    // ---- variants --------------------------------------------------------------------------

    private static McpApiBridgeTool VariantTool() => new()
    {
        Name = "list_by_state",
        VariantBy = "state",
        Variants =
        {
            ["open"] = new McpApiBridgeRequest { Method = "GET", Path = "/tasks", Query = { ["status"] = "open" } },
            ["archived"] = new McpApiBridgeRequest { Method = "GET", Path = "/archive/tasks" },
        },
    };

    [Fact]
    public void TheModelsChoiceDecidesWhichRequestIsMade()
    {
        Assert.Equal("/tasks", Build(VariantTool(), """{"state":"open"}""").RelativePath);
        Assert.Equal("/archive/tasks", Build(VariantTool(), """{"state":"archived"}""").RelativePath);
    }

    [Fact]
    public void TheSelectorIsNeverForwardedToTheUpstream()
    {
        var tool = VariantTool();
        tool.Variants["open"].Method = "POST";
        tool.Variants["open"].BodyMode = McpApiBridgeBodyMode.Arguments;

        var body = Build(tool, """{"state":"open","title":"hi"}""").Body;

        Assert.Equal("""{"title":"hi"}""", body);
    }

    [Fact]
    public void AMissingOrUnknownSelectorSaysWhatTheChoicesAre()
    {
        var missing = Failure(VariantTool(), """{}""");
        Assert.Contains("archived, open", missing, StringComparison.Ordinal);

        var unknown = Failure(VariantTool(), """{"state":"deleted"}""");
        Assert.Contains("archived, open", unknown, StringComparison.Ordinal);
        Assert.Contains("deleted", unknown, StringComparison.Ordinal);

        var wrongType = Failure(VariantTool(), """{"state":123}""");
        Assert.Contains("archived, open", wrongType, StringComparison.Ordinal);
    }

    [Fact]
    public void EachVariantKeepsItsOwnQueryAndPath()
    {
        var open = Build(VariantTool(), """{"state":"open"}""");
        var archived = Build(VariantTool(), """{"state":"archived"}""");

        Assert.Contains("status=open", open.Query, StringComparison.Ordinal);
        Assert.Equal("", archived.Query);
    }

    // ---- method ----------------------------------------------------------------------------

    [Fact]
    public void CarriesTheDeclaredMethod()
    {
        var tool = Tool(new McpApiBridgeRequest { Method = "delete", Path = "/things/{id}" });

        Assert.Equal(HttpMethod.Delete, Build(tool, """{"id":"1"}""").Method);
    }
}
