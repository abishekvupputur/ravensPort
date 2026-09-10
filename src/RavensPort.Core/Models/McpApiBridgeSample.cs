using System.Reflection;

namespace RavensPort.Core.Models;

/// <summary>
/// The worked example manifest, shipped with the app.
///
/// A user's first manifest is the hard one: the shape is small but every part of it has a rule
/// behind it, and a blank editor teaches none of them. The sample exercises each feature once —
/// a plain tool, a required path placeholder, a variant tool, a JSON body template, instructions,
/// a prompt, and a skill — so editing it into the API at hand is a shorter path than writing one
/// from the documentation.
///
/// It is an embedded resource rather than a string constant so that a test can run the very bytes
/// the user is handed through <see cref="McpApiBridgeValidation.TryReadManifest"/>. A sample that
/// no longer validates is worse than no sample.
/// </summary>
public static class McpApiBridgeSample
{
    private const string ResourceName = "RavensPort.Core.Assets.sample-api-mcp-manifest.json";

    /// <summary>What the file is called when the user saves it to disk.</summary>
    public const string FileName = "sample-api-mcp-manifest.json";

    public static string Read()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
                           ?? throw new InvalidOperationException(
                               $"The sample manifest '{ResourceName}' is missing from this build.");

        using var reader = new StreamReader(stream);

        return reader.ReadToEnd();
    }
}
