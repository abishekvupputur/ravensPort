using RavensPort.Core.Models;

namespace RavensPort.Core.Tests.Mcp;

/// <summary>
/// Every manifest shipped in templates/api-mcp has to validate.
///
/// Those files are starting points — one is what the app's own Load sample button inserts, and the
/// rest are what somebody, increasingly some agent, copies when writing a manifest for their own
/// API. A template that no longer passes the validator teaches the wrong shape to everything that
/// reads it, and it does so silently: nothing else in the build opens these files.
/// </summary>
public class ApiBridgeTemplateTests
{
    public static TheoryData<string> Templates
    {
        get
        {
            var data = new TheoryData<string>();

            foreach (var path in Directory.EnumerateFiles(TemplateFolder(), "*.json").OrderBy(p => p, StringComparer.Ordinal))
            {
                data.Add(Path.GetFileName(path));
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Templates))]
    public void EveryShippedTemplateValidates(string fileName)
    {
        var json = File.ReadAllText(Path.Combine(TemplateFolder(), fileName));

        var error = McpApiBridgeValidation.TryReadManifest(json, out var manifest);

        Assert.Null(error);
        Assert.NotNull(manifest);
        Assert.NotEmpty(manifest.Tools);
    }

    /// <summary>
    /// Guards the folder itself. A theory over an empty file list passes without running, so
    /// "someone moved the templates" would otherwise look exactly like "the templates are fine".
    /// </summary>
    [Fact]
    public void TheTemplateFolderStillHoldsItsWorkedExamples()
    {
        var names = Directory.EnumerateFiles(TemplateFolder(), "*.json")
            .Select(Path.GetFileName)
            .ToList();

        Assert.Contains("sample-task-tracker.json", names);
        Assert.Contains("google-drive-readonly.json", names);
        Assert.Contains("tailscale-readonly.json", names);

        Assert.True(File.Exists(Path.Combine(TemplateFolder(), "AUTHORING.md")),
            "The authoring instructions are what an agent reads before writing a manifest.");
    }

    public static TheoryData<string> ReadOnlyTemplates
    {
        get
        {
            var data = new TheoryData<string>();

            foreach (var path in Directory.EnumerateFiles(TemplateFolder(), "*-readonly.json").OrderBy(p => p, StringComparer.Ordinal))
            {
                data.Add(Path.GetFileName(path));
            }

            return data;
        }
    }

    /// <summary>
    /// A template whose name says read-only is the one that would do real damage if it grew a
    /// write. A correctly scoped credential would fail on anything else anyway — but by then the
    /// user has already handed an agent a tool that says it can delete their files.
    ///
    /// Matched on the filename so a new read-only template is covered the day it lands.
    /// </summary>
    [Theory]
    [MemberData(nameof(ReadOnlyTemplates))]
    public void AReadOnlyTemplateOnlyEverReads(string fileName)
    {
        var json = File.ReadAllText(Path.Combine(TemplateFolder(), fileName));

        Assert.Null(McpApiBridgeValidation.TryReadManifest(json, out var manifest));

        foreach (var tool in manifest!.Tools)
        {
            Assert.True(tool.ReadOnly, $"'{tool.Name}' is not marked read-only.");

            foreach (var request in Requests(tool))
            {
                Assert.True(
                    request.Method.Equals("GET", StringComparison.OrdinalIgnoreCase),
                    $"'{tool.Name}' uses {request.Method}, which is not a read.");

                Assert.Equal(McpApiBridgeBodyMode.None, request.BodyMode);
            }
        }
    }

    private static McpApiBridgeRequest[] Requests(McpApiBridgeTool tool) =>
        tool.HasVariants ? [.. tool.Variants.Values] : [tool.Request!];

    /// <summary>
    /// Found by walking up from the test assembly rather than by a relative path from the working
    /// directory, which differs between `dotnet test`, the IDE, and CI.
    /// </summary>
    private static string TemplateFolder()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "templates", "api-mcp");
            if (Directory.Exists(candidate)) return candidate;

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "templates/api-mcp was not found above " + AppContext.BaseDirectory);
    }
}
