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

        // Checked over the whole string rather than only outside placeholders. A control
        // character inside a name would fail the charset rule below anyway, and hoisting it out
        // means the scan underneath only has to think about braces.
        if (template.Any(char.IsControl))
        {
            return $"{what} may not contain control characters.";
        }

        var index = 0;

        while (index < template.Length)
        {
            var open = template.IndexOf('{', index);
            var stray = template.IndexOf('}', index);

            // Nothing left but literal text.
            if (open < 0 && stray < 0) return null;

            // A closing brace before the next opening one, or with no opening one at all.
            if (stray >= 0 && (open < 0 || stray < open))
            {
                return $"{what} has a '}}' with no matching '{{'.";
            }

            var close = template.IndexOf('}', open + 1);
            if (close < 0) return $"{what} has a '{{' with no matching '}}'.";

            if (ValidateName(template[(open + 1)..close], what) is { } error) return error;

            index = close + 1;
        }

        return null;
    }

    private static string? ValidateName(string name, string what)
    {
        if (name.Length == 0)
        {
            return $"{what} has an empty placeholder '{{}}'. Name the argument it stands for.";
        }

        // Named separately from the charset rule below, which would also reject it: "you cannot
        // nest these" is a different mistake from "that character is not allowed in a name", and
        // the author needs to know which one they made.
        if (name.Contains('{', StringComparison.Ordinal))
        {
            return $"{what} has a '{{' inside a placeholder. Placeholders may not nest.";
        }

        return IsValidName(name)
            ? null
            : $"{what} has the placeholder '{{{name}}}'. Placeholder names may only "
              + "contain letters, digits, and underscores.";
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
