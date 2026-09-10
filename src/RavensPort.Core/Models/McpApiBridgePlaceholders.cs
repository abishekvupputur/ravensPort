namespace RavensPort.Core.Models;

/// <summary>
/// The one definition of what <c>{name}</c> means in a manifest.
///
/// Paths, query values, header values, body templates and prompt messages all use it, and they
/// have to agree: a name that validates in one slot and not in another would produce a manifest
/// the UI accepts and the endpoint then refuses. Parsing is shared for the same reason validation
/// is shared with the UI everywhere else in this app.
///
/// Braces are structural everywhere they appear. There is no escape sequence, and a literal brace
/// in a template is refused rather than quietly passed through — a URL path or a header value
/// containing one is rare enough that refusing it costs nothing next to the ambiguity of guessing.
/// </summary>
public static class McpApiBridgePlaceholders
{
    /// <summary>One piece of a template: literal text, or a placeholder to substitute.</summary>
    public readonly record struct Segment(string Text, bool IsPlaceholder);

    /// <summary>
    /// Checks a template's braces. Returns null when usable, or a message naming what is wrong,
    /// already phrased for the UI footer. <paramref name="what"/> names the slot, e.g. "Tool path".
    /// </summary>
    public static string? Validate(string? template, string what)
    {
        if (template is null) return null;

        var depth = 0;
        var nameStart = 0;

        for (var index = 0; index < template.Length; index++)
        {
            var current = template[index];

            if (current == '{')
            {
                if (depth > 0)
                {
                    return $"{what} has a '{{' inside a placeholder. Placeholders may not nest.";
                }

                depth = 1;
                nameStart = index + 1;
                continue;
            }

            if (current == '}')
            {
                if (depth == 0)
                {
                    return $"{what} has a '}}' with no matching '{{'.";
                }

                var name = template[nameStart..index];

                if (name.Length == 0)
                {
                    return $"{what} has an empty placeholder '{{}}'. Name the argument it stands for.";
                }

                if (!IsValidName(name))
                {
                    return $"{what} has the placeholder '{{{name}}}'. Placeholder names may only "
                           + "contain letters, digits, and underscores.";
                }

                depth = 0;
                continue;
            }

            if (depth == 0 && char.IsControl(current))
            {
                return $"{what} may not contain control characters.";
            }
        }

        return depth == 0 ? null : $"{what} has a '{{' with no matching '}}'.";
    }

    /// <summary>
    /// Splits a template into literal and placeholder segments. Assumes <see cref="Validate"/>
    /// has already passed; an unbalanced brace at this point is treated as literal text rather
    /// than throwing, because a manifest hand-edited into the vault note never saw validation and
    /// a malformed path is better sent as written than turned into a crash on the call path.
    /// </summary>
    public static List<Segment> Parse(string? template)
    {
        var segments = new List<Segment>();
        if (string.IsNullOrEmpty(template)) return segments;

        var index = 0;

        while (index < template.Length)
        {
            var open = template.IndexOf('{', index);

            if (open < 0)
            {
                segments.Add(new Segment(template[index..], false));
                break;
            }

            var close = template.IndexOf('}', open);

            if (close < 0)
            {
                segments.Add(new Segment(template[index..], false));
                break;
            }

            if (open > index)
            {
                segments.Add(new Segment(template[index..open], false));
            }

            segments.Add(new Segment(template[(open + 1)..close], true));
            index = close + 1;
        }

        return segments;
    }

    /// <summary>Every placeholder name a template refers to, in order, including repeats.</summary>
    public static IEnumerable<string> Names(string? template) =>
        Parse(template).Where(segment => segment.IsPlaceholder).Select(segment => segment.Text);

    /// <summary>Whether a template is exactly one placeholder and nothing else.</summary>
    public static bool IsWholeValue(string? template, out string name)
    {
        name = "";

        var segments = Parse(template);
        if (segments is not [{ IsPlaceholder: true } only]) return false;

        name = only.Text;
        return true;
    }

    private static bool IsValidName(string name) =>
        name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
}
