using RavensPort.Core.Models;

namespace RavensPort.Core.Tests.Models;

/// <summary>
/// The OpenAPI importer never gets to skip <see cref="McpApiBridgeValidation"/> — it produces text
/// for the same editor a hand-written or pasted manifest fills, so these pin both halves: what the
/// converter does with an operation, and that the result it hands back always survives the
/// validator that actually decides what gets served.
/// </summary>
public class OpenApiImportTests
{
    private const string SourceName = "spec.json";

    private static OpenApiImportResult Convert(string spec) => OpenApiImporter.Convert(spec, SourceName);

    private static string Document(string paths, string components = "") => $$"""
        {
          "openapi": "3.0.0",
          "info": { "title": "Test API", "version": "1" },
          "paths": { {{paths}} }
          {{components}}
        }
        """;

    private static (string? Error, McpApiBridgeManifest? Manifest) ReadBack(OpenApiImportResult result)
    {
        Assert.Null(result.Error);
        Assert.NotNull(result.ManifestJson);

        var error = McpApiBridgeValidation.TryReadManifest(result.ManifestJson, out var manifest);
        return (error, manifest);
    }

    [Fact]
    public void MapsAPlainGetWithPathAndQueryParameters()
    {
        var spec = Document("""
            "/things/{id}": {
              "get": {
                "operationId": "getThing",
                "summary": "Get a thing",
                "parameters": [
                  { "name": "id", "in": "path", "required": true, "schema": { "type": "string" } },
                  { "name": "verbose", "in": "query", "schema": { "type": "boolean" } }
                ],
                "responses": { "200": { "description": "ok" } }
              }
            }
            """);

        var (error, manifest) = ReadBack(Convert(spec));

        Assert.Null(error);
        var tool = Assert.Single(manifest!.Tools);

        Assert.Equal("getThing", tool.Name);
        Assert.Equal("Get a thing", tool.Description);
        Assert.True(tool.ReadOnly);
        Assert.Equal("GET", tool.Request!.Method);
        Assert.Equal("/things/{id}", tool.Request.Path);
        Assert.Equal("{verbose}", tool.Request.Query["verbose"]);
    }

    [Fact]
    public void MapsAJsonRequestBodyToArgumentsMode()
    {
        var spec = Document("""
            "/things": {
              "post": {
                "operationId": "createThing",
                "requestBody": {
                  "content": {
                    "application/json": {
                      "schema": {
                        "type": "object",
                        "properties": { "name": { "type": "string" } },
                        "required": ["name"]
                      }
                    }
                  }
                },
                "responses": { "200": { "description": "ok" } }
              }
            }
            """);

        var (error, manifest) = ReadBack(Convert(spec));

        Assert.Null(error);
        var tool = Assert.Single(manifest!.Tools);

        Assert.Equal(McpApiBridgeBodyMode.Arguments, tool.Request!.BodyMode);
        Assert.False(tool.ReadOnly);
    }

    [Fact]
    public void SynthesizesANameWhenOperationIdIsMissing()
    {
        var spec = Document("""
            "/things/{id}": {
              "delete": {
                "parameters": [
                  { "name": "id", "in": "path", "required": true, "schema": { "type": "string" } }
                ],
                "responses": { "204": { "description": "ok" } }
              }
            }
            """);

        var (error, manifest) = ReadBack(Convert(spec));

        Assert.Null(error);
        var tool = Assert.Single(manifest!.Tools);
        Assert.Equal("delete_things_id", tool.Name);
    }

    [Fact]
    public void DeduplicatesCollidingOperationIds()
    {
        var spec = Document("""
            "/a": {
              "get": { "operationId": "doIt", "responses": { "200": { "description": "ok" } } }
            },
            "/b": {
              "get": { "operationId": "doIt", "responses": { "200": { "description": "ok" } } }
            }
            """);

        var result = Convert(spec);
        var (error, manifest) = ReadBack(result);

        Assert.Null(error);
        Assert.Equal(["doIt", "doIt_2"], manifest!.Tools.Select(t => t.Name));
    }

    [Fact]
    public void SkipsCookieAndCredentialHeaderParametersWithAWarning()
    {
        var spec = Document("""
            "/things": {
              "get": {
                "operationId": "listThings",
                "parameters": [
                  { "name": "session", "in": "cookie", "schema": { "type": "string" } },
                  { "name": "Authorization", "in": "header", "schema": { "type": "string" } }
                ],
                "responses": { "200": { "description": "ok" } }
              }
            }
            """);

        var result = Convert(spec);
        var (error, manifest) = ReadBack(result);

        Assert.Null(error);
        var tool = Assert.Single(manifest!.Tools);

        Assert.Empty(tool.Request!.Headers);
        Assert.Contains(result.Warnings, w => w.Contains("session") && w.Contains("cookie"));
        Assert.Contains(result.Warnings, w => w.Contains("Authorization") && w.Contains("credential"));
    }

    [Fact]
    public void TruncatesAnOversizedToolListAndWarns()
    {
        var pathEntries = Enumerable.Range(0, McpApiBridgeValidation.MaxTools + 5)
            .Select(i => $$"""
                "/things/{{i}}": {
                  "get": { "operationId": "get{{i}}", "responses": { "200": { "description": "ok" } } }
                }
                """);

        var spec = Document(string.Join(",\n", pathEntries));

        var result = Convert(spec);
        var (error, manifest) = ReadBack(result);

        Assert.Null(error);
        Assert.Equal(McpApiBridgeValidation.MaxTools, manifest!.Tools.Count);
        Assert.Contains(result.Warnings, w => w.Contains("dropped"));
    }

    [Fact]
    public void DropsAToolWhoseParameterNameTheFormatCannotRepresentRatherThanFailingTheWholeImport()
    {
        // Tailscale's own published spec has exactly this: a query parameter whose "name" is
        // documentation text ("<field>=<value> filters") rather than an identifier. The manifest
        // validator rightly refuses '=' in a query parameter name; the importer must drop that one
        // tool and keep going, not hand back a manifest that fails whole.
        var spec = Document("""
            "/ok": {
              "get": { "operationId": "getOk", "responses": { "200": { "description": "ok" } } }
            },
            "/broken": {
              "get": {
                "operationId": "getBroken",
                "parameters": [
                  { "name": "<field>=<value> filters", "in": "query", "schema": { "type": "string" } }
                ],
                "responses": { "200": { "description": "ok" } }
              }
            }
            """);

        var result = Convert(spec);
        var (error, manifest) = ReadBack(result);

        Assert.Null(error);
        Assert.Single(manifest!.Tools);
        Assert.Equal("getOk", manifest.Tools[0].Name);
        Assert.Contains(result.Warnings, w => w.Contains("getBroken") && w.Contains("dropped"));
    }

    [Fact]
    public void DropsTrailingToolsToStayUnderTheManifestByteCapEvenWithinTheToolCountCap()
    {
        // A separate cap from MaxTools: enough tools with long descriptions can still be too many
        // bytes for one vault item well before hitting the 64-tool ceiling.
        var longDescription = new string('a', 4000);

        var pathEntries = Enumerable.Range(0, 40)
            .Select(i => $$"""
                "/things/{{i}}": {
                  "get": {
                    "operationId": "get{{i}}",
                    "description": "{{longDescription}}",
                    "responses": { "200": { "description": "ok" } }
                  }
                }
                """);

        var spec = Document(string.Join(",\n", pathEntries));

        var result = Convert(spec);
        var (error, manifest) = ReadBack(result);

        Assert.Null(error);
        Assert.True(manifest!.Tools.Count < 40);
        Assert.Contains(result.Warnings, w => w.Contains("KB cap"));
    }

    [Fact]
    public void ReportsAnErrorForSomethingThatIsNotOpenApi()
    {
        var result = Convert("this is not json or yaml or anything of the sort: {{{");

        Assert.NotNull(result.Error);
        Assert.Null(result.ManifestJson);
    }

    [Fact]
    public void TheWarningCommentHeaderDoesNotBreakParsing()
    {
        var spec = Document("""
            "/things": {
              "get": {
                "operationId": "listThings",
                "parameters": [
                  { "name": "session", "in": "cookie", "schema": { "type": "string" } }
                ],
                "responses": { "200": { "description": "ok" } }
              }
            }
            """);

        var result = Convert(spec);

        Assert.StartsWith("//", result.ManifestJson);
        Assert.NotEmpty(result.Warnings);

        var (error, manifest) = ReadBack(result);
        Assert.Null(error);
        Assert.NotEmpty(manifest!.Tools);
    }
}
