using System.Text.Json;
using RavensPort.Core.Mcp;
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
    public void SkipsAParameterTheFormatCannotRepresentAndKeepsTheToolItBelongsTo()
    {
        // Tailscale's own published spec has exactly this: a query parameter whose "name" is
        // documentation text ("<field>=<value> filters") rather than an identifier. The manifest
        // validator rightly refuses '=' in a query parameter name — but the cost of that should be
        // the one parameter, not the endpoint. Dropping the tool lost a working call over a
        // documentation typo in somebody else's spec.
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
        Assert.Equal(["getBroken", "getOk"], manifest!.Tools.Select(t => t.Name));

        // The tool is there and callable; it simply has no argument for the parameter that could
        // not be named.
        var broken = manifest.Tools.Single(t => t.Name == "getBroken");
        Assert.Empty(broken.Request!.Query);

        Assert.Contains(result.Warnings, w => w.Contains("getBroken") && w.Contains("was skipped"));
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

    [Fact]
    public void SkipsOptionsAndTraceMethodsWithAWarning()
    {
        var spec = Document("""
            "/things": {
              "get": { "operationId": "getThings", "responses": { "200": { "description": "ok" } } },
              "options": { "operationId": "preflight", "responses": { "200": { "description": "ok" } } }
            }
            """);

        var result = Convert(spec);
        var (error, manifest) = ReadBack(result);

        Assert.Null(error);
        Assert.Single(manifest!.Tools);
        Assert.Contains(result.Warnings, w => w.Contains("OPTIONS or TRACE", StringComparison.Ordinal));
    }

    [Fact]
    public void FailsWhenEveryOperationFailsItsOwnValidation()
    {
        // A path this format refuses outright, rather than an unusable parameter — those are now
        // left out one at a time and the tool survives them, which is the point of the two tests
        // above. This is a failure nothing can be dropped to fix.
        var spec = Document("""
            "/things/../etc": {
              "get": {
                "operationId": "getThings",
                "responses": { "200": { "description": "ok" } }
              }
            }
            """);

        var result = Convert(spec);

        Assert.NotNull(result.Error);
        Assert.Contains("Nothing from this document fits", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void WarnsWhenTheRequestBodyHasNoJsonContentAtAll()
    {
        var spec = Document("""
            "/things": {
              "post": {
                "operationId": "upload",
                "requestBody": {
                  "content": { "multipart/form-data": { "schema": { "type": "string", "format": "binary" } } }
                },
                "responses": { "200": { "description": "ok" } }
              }
            }
            """);

        var result = Convert(spec);
        var (error, manifest) = ReadBack(result);

        Assert.Null(error);
        var tool = Assert.Single(manifest!.Tools);
        Assert.Equal(McpApiBridgeBodyMode.None, tool.Request!.BodyMode);
        Assert.Contains(result.Warnings, w => w.Contains("multipart/form-data", StringComparison.Ordinal));
    }

    [Fact]
    public void ForwardsABodyTheSpecDoesNotDescribeRatherThanSendingNoneAtAll()
    {
        // A free-form object, or a map keyed by something arbitrary — Tailscale's split-DNS body is
        // exactly this. There are no fields to declare, but Arguments mode forwards whatever the
        // caller passes, so the call still works. Sending no body produced a tool that reached the
        // endpoint empty and failed at the far end.
        var spec = Document("""
            "/things": {
              "post": {
                "operationId": "createThing",
                "requestBody": {
                  "content": { "application/json": { "schema": { "type": "object" } } }
                },
                "responses": { "200": { "description": "ok" } }
              }
            }
            """);

        var result = Convert(spec);
        var (error, manifest) = ReadBack(result);

        Assert.Null(error);
        var tool = Assert.Single(manifest!.Tools);

        Assert.Equal(McpApiBridgeBodyMode.Arguments, tool.Request!.BodyMode);

        // And the description says so, because nothing else would tell an agent it may send fields.
        Assert.Contains("fields the spec does not list", tool.Description, StringComparison.Ordinal);

        // The claim that matters: calling it actually produces a body. Asserting the mode alone
        // would still pass if the arguments went nowhere.
        var arguments = new Dictionary<string, JsonElement>
        {
            ["anything"] = JsonDocument.Parse("\"a value\"").RootElement,
        };

        var (built, buildError) = McpApiBridgeRequestBuilder.Build(tool, arguments);

        Assert.Null(buildError);
        Assert.Equal("""{"anything":"a value"}""", built!.Body);
    }

    [Fact]
    public void FlattensAnAllOfBodyIntoOneSetOfProperties()
    {
        var spec = Document("""
            "/things": {
              "post": {
                "operationId": "createThing",
                "requestBody": {
                  "content": {
                    "application/json": {
                      "schema": {
                        "allOf": [
                          {
                            "type": "object",
                            "properties": { "name": { "type": "string" } },
                            "required": ["name"]
                          },
                          {
                            "type": "object",
                            "properties": { "size": { "type": "integer" } }
                          }
                        ]
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

        var schema = tool.InputSchema;
        var properties = schema.GetProperty("properties");

        Assert.Equal("string", properties.GetProperty("name").GetProperty("type").GetString());
        Assert.Equal("integer", properties.GetProperty("size").GetProperty("type").GetString());

        // Every allOf branch applies at once, so its required fields stay required.
        Assert.Equal(["name"], schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public void TakesAOneOfBodyAsAUnionOfOptionalProperties()
    {
        var spec = Document("""
            "/things": {
              "post": {
                "operationId": "createThing",
                "requestBody": {
                  "content": {
                    "application/json": {
                      "schema": {
                        "oneOf": [
                          {
                            "type": "object",
                            "properties": { "byId": { "type": "string" } },
                            "required": ["byId"]
                          },
                          {
                            "type": "object",
                            "properties": { "byName": { "type": "string" } },
                            "required": ["byName"]
                          }
                        ]
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

        var properties = tool.InputSchema.GetProperty("properties");
        Assert.True(properties.TryGetProperty("byId", out _));
        Assert.True(properties.TryGetProperty("byName", out _));

        // Which branch applies is the caller's choice, so neither branch's field may be demanded —
        // requiring both would refuse every valid call.
        Assert.False(tool.InputSchema.TryGetProperty("required", out _));
    }

    [Fact]
    public void ImportsAGetThatDeclaresARequestBodyWithoutThatBody()
    {
        // GitHub's repos_get-content is a GET with a requestBody. A manifest may not send one, and
        // attaching it anyway had the validator refuse the whole tool.
        var spec = Document("""
            "/things": {
              "get": {
                "operationId": "getThings",
                "requestBody": {
                  "content": {
                    "application/json": {
                      "schema": { "type": "object", "properties": { "q": { "type": "string" } } }
                    }
                  }
                },
                "responses": { "200": { "description": "ok" } }
              }
            }
            """);

        var result = Convert(spec);
        var (error, manifest) = ReadBack(result);

        Assert.Null(error);
        var tool = Assert.Single(manifest!.Tools);

        Assert.Equal(McpApiBridgeBodyMode.None, tool.Request!.BodyMode);
        Assert.Contains(result.Warnings, w => w.Contains("request body on a GET", StringComparison.Ordinal));
    }

    [Fact]
    public void RenamesABodyPropertyThatCollidesWithAPathParameter()
    {
        var spec = Document("""
            "/things/{id}": {
              "put": {
                "operationId": "updateThing",
                "parameters": [
                  { "name": "id", "in": "path", "required": true, "schema": { "type": "string" } }
                ],
                "requestBody": {
                  "content": {
                    "application/json": {
                      "schema": {
                        "type": "object",
                        "properties": { "id": { "type": "string" }, "name": { "type": "string" } }
                      }
                    }
                  }
                },
                "responses": { "200": { "description": "ok" } }
              }
            }
            """);

        var result = Convert(spec);
        var (error, manifest) = ReadBack(result);

        Assert.Null(error);
        var tool = Assert.Single(manifest!.Tools);
        Assert.Equal(McpApiBridgeBodyMode.Arguments, tool.Request!.BodyMode);
        Assert.Contains(result.Warnings, w => w.Contains("collided", StringComparison.Ordinal));
    }

    [Fact]
    public void AllocatesALongOperationIdWithinTheToolNameCap()
    {
        var longId = new string('x', McpApiBridgeValidation.MaxToolNameLength + 20);

        var spec = Document($$"""
            "/things": {
              "get": { "operationId": "{{longId}}", "responses": { "200": { "description": "ok" } } }
            }
            """);

        var result = Convert(spec);
        var (error, manifest) = ReadBack(result);

        Assert.Null(error);
        var tool = Assert.Single(manifest!.Tools);
        Assert.True(tool.Name.Length <= McpApiBridgeValidation.MaxToolNameLength);
    }

    /// <summary>
    /// One operation exercising every schema and parameter feature at once: a path parameter whose
    /// name needs sanitizing, a non-reserved header parameter with a description, an enum, an array
    /// with typed items, a "format", an object-typed property, and a type this converter cannot map
    /// to any single JSON Schema type (a 3.1-style union of two non-null types) — plus a summary and
    /// a description that differ, so both halves of a tool's text get combined.
    /// </summary>
    [Fact]
    public void MapsEveryParameterAndSchemaFeatureInOneOperation()
    {
        // OpenAPI 3.1.0, not the shared 3.0.0 Document() helper: a "type" array — the union this
        // converter cannot map to one JSON Schema type — is only valid syntax from 3.1 onward.
        var spec = """
            {
              "openapi": "3.1.0",
              "info": { "title": "Test API", "version": "1" },
              "paths": {
                "/things/{item-id}": {
                  "get": {
                    "operationId": "getThing",
                    "summary": "Gets a thing",
                    "description": "Returns the full record.",
                    "parameters": [
                      { "name": "item-id", "in": "path", "required": true, "schema": { "type": "string" } },
                      {
                        "name": "X-Trace",
                        "in": "header",
                        "description": "Correlation id",
                        "schema": { "type": "string", "format": "uuid", "enum": ["a", "b"] }
                      },
                      {
                        "name": "tags",
                        "in": "query",
                        "schema": { "type": "array", "items": { "type": "integer" } }
                      },
                      {
                        "name": "detail",
                        "in": "query",
                        "schema": { "type": "object" }
                      },
                      {
                        "name": "weird",
                        "in": "query",
                        "schema": { "type": ["string", "integer"] }
                      }
                    ],
                    "responses": { "200": { "description": "ok" } }
                  }
                }
              }
            }
            """;

        var result = Convert(spec);
        var (error, manifest) = ReadBack(result);

        Assert.Null(error);
        var tool = Assert.Single(manifest!.Tools);

        Assert.Equal("Gets a thing Returns the full record.", tool.Description);
        Assert.Equal("/things/{item_id}", tool.Request!.Path);
        Assert.Equal("{X_Trace}", tool.Request.Headers["X-Trace"]);

        var properties = tool.InputSchema.GetProperty("properties");
        Assert.Equal("string", properties.GetProperty("item_id").GetProperty("type").GetString());
        Assert.Equal("string", properties.GetProperty("X_Trace").GetProperty("type").GetString());
        Assert.Equal("Correlation id", properties.GetProperty("X_Trace").GetProperty("description").GetString());
        Assert.Equal("uuid", properties.GetProperty("X_Trace").GetProperty("format").GetString());
        Assert.Equal(["a", "b"], properties.GetProperty("X_Trace").GetProperty("enum").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("array", properties.GetProperty("tags").GetProperty("type").GetString());
        Assert.Equal("integer", properties.GetProperty("tags").GetProperty("items").GetProperty("type").GetString());
        Assert.Equal("object", properties.GetProperty("detail").GetProperty("type").GetString());

        // The unmappable union falls back to string, with a warning naming it.
        Assert.Equal("string", properties.GetProperty("weird").GetProperty("type").GetString());
        Assert.Contains(result.Warnings, w => w.Contains("weird", StringComparison.Ordinal)
                                               && w.Contains("could not map", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("")]
    [InlineData("\0\0\0")]
    [InlineData("\t\t\tnot: [valid: yaml: at: all: {{{")]
    public void NeverThrowsRegardlessOfHowUnreadableTheInputIs(string garbage)
    {
        var result = Convert(garbage);

        Assert.NotNull(result.Error);
        Assert.Null(result.ManifestJson);
    }

    [Fact]
    public void DiscoverListsEveryOperationWithItsTagsAndSkipsOptionsAndTrace()
    {
        var spec = Document("""
            "/things": {
              "get": {
                "operationId": "listThings", "summary": "List things", "tags": ["things"],
                "responses": { "200": { "description": "ok" } }
              },
              "options": { "operationId": "preflight", "responses": { "200": { "description": "ok" } } }
            },
            "/things/{id}": {
              "delete": { "operationId": "deleteThing", "responses": { "200": { "description": "ok" } } }
            }
            """);

        var (error, operations, _) = OpenApiImporter.Discover(spec);

        Assert.Null(error);
        Assert.Equal(2, operations.Count);

        var listed = Assert.Single(operations, o => o.OperationId == "listThings");
        Assert.Equal("/things", listed.Path);
        Assert.Equal("GET", listed.Method);
        Assert.Equal("List things", listed.Summary);
        Assert.Equal(["things"], listed.Tags);

        var deleted = Assert.Single(operations, o => o.OperationId == "deleteThing");
        Assert.Empty(deleted.Tags);
    }

    [Fact]
    public void DiscoverReportsTheSameErrorAsConvertForAnUnreadableDocument()
    {
        var (error, operations, _) = OpenApiImporter.Discover("this is not json or yaml or anything of the sort: {{{");

        Assert.NotNull(error);
        Assert.Empty(operations);
    }

    [Fact]
    public void ConvertWithIncludeOnlyBuildsOnlyTheNamedOperationsWithNoWarning()
    {
        var spec = Document("""
            "/things": {
              "get": { "operationId": "listThings", "responses": { "200": { "description": "ok" } } },
              "post": { "operationId": "createThing", "responses": { "200": { "description": "ok" } } }
            },
            "/other": {
              "get": { "operationId": "getOther", "responses": { "200": { "description": "ok" } } }
            }
            """);

        var includeOnly = new HashSet<(string Path, string Method)> { ("/things", "GET") };
        var result = OpenApiImporter.Convert(spec, SourceName, includeOnly);
        var (error, manifest) = ReadBack(result);

        Assert.Null(error);
        var tool = Assert.Single(manifest!.Tools);
        Assert.Equal("listThings", tool.Name);
        Assert.Empty(result.Warnings);
    }
}
