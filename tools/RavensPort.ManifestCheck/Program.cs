using RavensPort.Core.Models;

namespace RavensPort.ManifestCheck;

/// <summary>
/// Checks API to MCP manifests from the command line.
///
/// It exists because a manifest is increasingly written by an agent rather than by a person, and
/// an agent needs a way to find out whether what it produced is servable before handing it over.
/// The app's own editor already answers that, but only to somebody sitting in front of it.
///
/// It calls <see cref="McpApiBridgeValidation"/> rather than restating the rules, which is the
/// whole point: a second definition of what a valid manifest looks like would start agreeing with
/// the first and end disagreeing, and the disagreement would show up as a file this tool passed
/// and the app then refused.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
        {
            Usage();
            return args.Length == 0 ? 2 : 0;
        }

        var files = Collect(args, out var badPaths);

        foreach (var path in badPaths)
        {
            Console.Error.WriteLine($"{path}: no such file or directory");
        }

        if (files.Count == 0)
        {
            Console.Error.WriteLine("Nothing to check.");
            return 2;
        }

        var failed = 0;

        foreach (var file in files)
        {
            if (!Check(file)) failed++;
        }

        Console.WriteLine();
        Console.WriteLine(failed == 0
            ? $"{files.Count} manifest(s) checked, all valid."
            : $"{files.Count} manifest(s) checked, {failed} invalid.");

        return failed == 0 && badPaths.Count == 0 ? 0 : 1;
    }

    private static bool Check(string path)
    {
        Console.WriteLine();
        Console.WriteLine(path);

        string json;

        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ERROR  could not read it: {ex.Message}");
            return false;
        }

        if (McpApiBridgeValidation.TryReadManifest(json, out var manifest) is { } error)
        {
            Console.WriteLine($"  INVALID  {error}");
            return false;
        }

        Describe(manifest!);
        return true;
    }

    /// <summary>
    /// Prints every call the manifest would make, variants expanded one line each.
    ///
    /// This is the part worth reading, and it is deliberately the same shape as the preview in the
    /// app. A wrong path is the one class of error nothing can catch automatically — the validator
    /// does not know which route the manifest will be attached to — so the only defence is showing
    /// the author what they actually wrote.
    /// </summary>
    private static void Describe(McpApiBridgeManifest manifest)
    {
        Console.WriteLine($"  VALID  {manifest.Tools.Count} tool(s), {manifest.Prompts.Count} prompt(s), "
                          + $"{manifest.Skills.Count} skill(s), {McpApiBridgeValidation.MeasureBytes(manifest) / 1024} KB"
                          + (manifest.Instructions is null ? "" : ", with instructions"));

        foreach (var tool in manifest.Tools)
        {
            if (!tool.HasVariants)
            {
                Console.WriteLine($"    {tool.Name} -> {tool.Request!.Method.ToUpperInvariant()} {Describe(tool.Request)}");
                continue;
            }

            Console.WriteLine($"    {tool.Name} -> picks on '{tool.VariantBy}':");

            foreach (var (key, request) in tool.Variants)
            {
                Console.WriteLine($"        {key} -> {request.Method.ToUpperInvariant()} {Describe(request)}");
            }
        }

        foreach (var prompt in manifest.Prompts) Console.WriteLine($"    prompt: {prompt.Name}");
        foreach (var skill in manifest.Skills) Console.WriteLine($"    skill:  {skill.Name}");
    }

    /// <summary>
    /// The path with its query template, so a parameter written into the wrong slot is visible.
    /// Rendered as the template rather than as an expanded call: there are no arguments here.
    /// </summary>
    private static string Describe(McpApiBridgeRequest request)
    {
        if (request.Query.Count == 0) return request.Path;

        var query = string.Join("&", request.Query.Select(pair => $"{pair.Key}={pair.Value}"));

        return $"{request.Path}?{query}";
    }

    /// <summary>
    /// Expands the arguments into files to check. A directory contributes its .json files, so
    /// pointing this at a whole folder of templates works without a shell glob — which matters on
    /// Windows, where the shell does not expand one.
    /// </summary>
    private static List<string> Collect(IEnumerable<string> args, out List<string> bad)
    {
        var files = new List<string>();
        bad = [];

        foreach (var argument in args)
        {
            if (argument.StartsWith('-')) continue;

            if (Directory.Exists(argument))
            {
                files.AddRange(Directory.EnumerateFiles(argument, "*.json", SearchOption.TopDirectoryOnly)
                    .OrderBy(path => path, StringComparer.Ordinal));

                continue;
            }

            if (File.Exists(argument))
            {
                files.Add(argument);
                continue;
            }

            bad.Add(argument);
        }

        return files;
    }

    private static void Usage()
    {
        Console.WriteLine("""
            Checks RavensPort API to MCP manifests.

              dotnet run --project tools/RavensPort.ManifestCheck -- <file or folder> [more...]

            Examples:

              dotnet run --project tools/RavensPort.ManifestCheck -- my-api.json
              dotnet run --project tools/RavensPort.ManifestCheck -- templates/api-mcp

            Prints every call each manifest would make, so a path written against the wrong base
            is visible. Exit code is 0 when everything is valid, 1 when something is not.

            The rules are documented in templates/api-mcp/AUTHORING.md. What this checks is exactly
            what RavensPort checks on import -- it calls the same validator.
            """);
    }
}
