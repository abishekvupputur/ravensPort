using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using ModelContextProtocol.Protocol;

namespace RavensPort.Core.Mcp;

/// <summary>What one source currently offers, as last discovered.</summary>
public sealed record McpSourceCatalog(
    IReadOnlyList<string> Tools,
    IReadOnlyList<string> Resources,
    IReadOnlyList<string> Prompts,
    DateTimeOffset RetrievedUtc,
    string? Error)
{
    public static McpSourceCatalog Failed(string error) =>
        new([], [], [], DateTimeOffset.UtcNow, error);

    public bool IsEmpty => Tools.Count == 0 && Resources.Count == 0 && Prompts.Count == 0;

    /// <summary>One-line summary for the sources grid.</summary>
    public string Describe()
    {
        if (Error is not null) return $"⚠ {Summarize(Error)}";

        // Says "connected" explicitly, because the alternative reading of an empty result —
        // that the source could not be reached — is the one a user will assume otherwise.
        if (IsEmpty) return "connected — server offers nothing";

        var parts = new List<string>();
        if (Tools.Count > 0) parts.Add($"{Tools.Count} tools");
        if (Resources.Count > 0) parts.Add($"{Resources.Count} resources");
        if (Prompts.Count > 0) parts.Add($"{Prompts.Count} prompts");

        return string.Join(" · ", parts);
    }

    /// <summary>
    /// The full failure text, on one line, for a tooltip. Null when nothing failed.
    ///
    /// Nothing is dropped here — <see cref="Describe"/> is what has to fit in a cell, and the
    /// detail it trims still has to be readable somewhere, or diagnosing a source means going to
    /// the log file for text the app already had.
    /// </summary>
    public string? Detail => Error is null ? null : Collapse(Error);

    /// <summary>
    /// Reduces a failure to something a grid cell can hold.
    ///
    /// Transport exceptions from the MCP SDK carry the upstream's response body in their message,
    /// and an "MCP endpoint" is frequently also a web page — a Google Apps Script deployment
    /// answers a GET with its whole HTML document. Rendered verbatim in a wrapping cell, that one
    /// row grows to the height of a web page and pushes every other source off the screen.
    ///
    /// The markup is not the message, so it comes out; what is left is the sentence the server
    /// actually said, capped.
    /// </summary>
    internal static string Summarize(string error)
    {
        var text = LooksLikeMarkup(error) ? StripMarkup(error) : error;
        text = Collapse(text);

        // Markup that carried no text at all — a page that is pure script or styling. Saying so
        // beats an empty cell, which reads as "no error".
        if (text.Length == 0) return "the server answered with a web page, not an MCP response";

        return text.Length <= MaxCellLength ? text : $"{text[..MaxCellLength].TrimEnd()}…";
    }

    private const int MaxCellLength = 200;

    /// <summary>
    /// Every one of these runs over a string an upstream chose: the body it answered with, verbatim
    /// inside the SDK's exception message. A page with many unclosed &lt;script&gt; openings makes
    /// the lazy match rescan to the end of the text once per opening, so the ceiling is what keeps
    /// a hostile — or merely broken — response from stalling the refresh that is rendering it.
    /// Timing out is not a failure worth reporting: the caller already has the untouched text and
    /// falls back to it.
    /// </summary>
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>Every run of whitespace — newlines included — becomes one space.</summary>
    private static string Collapse(string text)
    {
        try
        {
            return Regex.Replace(text, @"\s+", " ", RegexOptions.None, MatchTimeout).Trim();
        }
        catch (RegexMatchTimeoutException)
        {
            return text.Trim();
        }
    }

    private static bool LooksLikeMarkup(string text) =>
        text.Contains("<!doctype", StringComparison.OrdinalIgnoreCase)
        || text.Contains("<html", StringComparison.OrdinalIgnoreCase)
        || text.Contains("<body", StringComparison.OrdinalIgnoreCase)
        || text.Contains("<script", StringComparison.OrdinalIgnoreCase);

    private static string StripMarkup(string text)
    {
        try
        {
            // Script and style first, with their contents: their text is not prose, and dropping
            // only the tags would leave JavaScript in the cell — which is what a bare tag-strip
            // does to an Apps Script page, whose body is almost entirely script.
            var stripped = Regex.Replace(
                text, "<(script|style)[^>]*>.*?</\\1>", " ",
                RegexOptions.IgnoreCase | RegexOptions.Singleline, MatchTimeout);

            stripped = Regex.Replace(stripped, "<[^>]*>", " ", RegexOptions.None, MatchTimeout);

            return System.Net.WebUtility.HtmlDecode(stripped);
        }
        catch (RegexMatchTimeoutException)
        {
            // Untouched markup, which Summarize then collapses and caps like anything else. A cell
            // holding 200 characters of HTML is poor, but it is bounded and it is what happened.
            return text;
        }
    }
}

/// <summary>
/// Last-known catalog per source, populated by the GUI's Refresh. Purely a UI convenience — the
/// funnel handlers always ask the upstream directly, so a stale entry here can never cause a
/// stale tool list to be served to an agent.
/// </summary>
public sealed class McpCatalogCache
{
    private readonly ConcurrentDictionary<Guid, McpSourceCatalog> _catalogs = new();

    public McpSourceCatalog? Get(Guid sourceId) =>
        _catalogs.TryGetValue(sourceId, out var catalog) ? catalog : null;

    public void Set(Guid sourceId, McpSourceCatalog catalog) => _catalogs[sourceId] = catalog;

    public void Remove(Guid sourceId) => _catalogs.TryRemove(sourceId, out _);
}

/// <summary>
/// Pagination drain plus the field-by-field clones the funnel needs when it renames a primitive.
///
/// The protocol objects are mutable, but the ones coming back from a source belong to that
/// source's client and may be handed out again — mutating a Tool's Name in place would rename it
/// for every funnel sharing the catalog. Every rename therefore produces a copy.
/// </summary>
internal static class McpProtocolExtensions
{
    /// <summary>
    /// Upper bound on pages pulled from one source. A funnel answers tools/list as a single
    /// unpaginated page (there is no sane way to interleave several upstreams' opaque cursors
    /// into one), so a source that never stops paging would otherwise hang the request forever.
    /// </summary>
    public const int MaxPages = 50;

    /// <summary>
    /// The freshness a funnel promises on its four list results: none.
    ///
    /// 2026-07-28 requires ttlMs and cacheScope on every result that implements CacheableResult,
    /// and for the list endpoints the only honest answer here is zero. A funnel resolves its
    /// sources and its tool selection from the config store on every request precisely so that an
    /// edit in the GUI -- unticking a tool, disabling a source -- lands on the agent's very next
    /// call. It holds no session and raises no listChanged notification, so nothing exists that
    /// could invalidate a list a client had already cached. A non-zero ttl would let an agent go
    /// on calling a tool the user had just unticked for as long as that ttl ran, which is the one
    /// property this class is built to prevent.
    /// </summary>
    public static readonly TimeSpan ListFreshness = TimeSpan.Zero;

    /// <summary>
    /// Stamps the 2026-07-28 cacheability fields onto a result.
    ///
    /// cacheScope is always Private, on every result and without exception. A funnel's answer is
    /// specific to that funnel's selection, and for ProxyRoute sources it was fetched with the
    /// user's own credential attached on the way out by the route's transform. A shared
    /// intermediary must never be free to hand one caller's view of a funnel to another.
    ///
    /// <paramref name="timeToLive"/> is left unset by the list endpoints, which take
    /// <see cref="ListFreshness"/>. Only resources/read passes one: the bytes of a resource are
    /// the upstream's business and its own ttl is the best available answer, whereas which
    /// resources are visible through this funnel is not.
    /// </summary>
    public static T AsFunnelCacheable<T>(this T result, TimeSpan? timeToLive = null)
        where T : ICacheableResult
    {
        result.TimeToLive = timeToLive ?? ListFreshness;
        result.CacheScope = CacheScope.Private;
        return result;
    }

    public static Tool WithName(this Tool tool, string name) => new()
    {
        Name = name,
        Title = tool.Title,
        Description = tool.Description,
        InputSchema = tool.InputSchema,
        OutputSchema = tool.OutputSchema,
        Annotations = tool.Annotations,
        Icons = tool.Icons,
        Meta = tool.Meta,
    };

    public static Prompt WithName(this Prompt prompt, string name) => new()
    {
        Name = name,
        Title = prompt.Title,
        Description = prompt.Description,
        Arguments = prompt.Arguments,
        Icons = prompt.Icons,
        Meta = prompt.Meta,
    };

    public static Resource WithNameAndUri(this Resource resource, string name, string uri) => new()
    {
        Name = name,
        Uri = uri,
        Title = resource.Title,
        Description = resource.Description,
        MimeType = resource.MimeType,
        Size = resource.Size,
        Annotations = resource.Annotations,
        Icons = resource.Icons,
        Meta = resource.Meta,
    };

    public static ResourceTemplate WithNameAndTemplate(this ResourceTemplate template, string name, string uriTemplate) => new()
    {
        Name = name,
        UriTemplate = uriTemplate,
        Title = template.Title,
        Description = template.Description,
        MimeType = template.MimeType,
        Annotations = template.Annotations,
        Icons = template.Icons,
        Meta = template.Meta,
    };
}
