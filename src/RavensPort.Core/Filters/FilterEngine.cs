using System.Text;
using System.Text.Json;
using RavensPort.Core.Models;

namespace RavensPort.Core.Filters;

/// <summary>A step that matched, and where.</summary>
/// <param name="FieldPath">Where in the arguments, e.g. <c>body.recipients[2]</c>. Names only, never the value.</param>
/// <param name="Step">A label in place of the step's name, for a hit no step produced (a missing profile).</param>
public sealed record FilterHit(FilterCategory Category, FilterAction Action, string Detector, string FieldPath, string? Step = null)
{
    /// <summary>One line for the activity log and the agent. Carries no matched value, by design.</summary>
    public string Describe() => $"{Step ?? FilterNames.Of(Category)}: {Detector} in '{FieldPath}'";
}

/// <summary>Something one step found inside one text, to be replaced before later steps read it.</summary>
public readonly record struct Span(int Start, int Length, string Name);

/// <summary>One piece of text from a call's arguments.</summary>
/// <param name="IsValue">False for an object key or a number — checked by patterns, but not read for meaning.</param>
public sealed record CallText(string Path, string Text, bool IsValue);

/// <summary>Display names of the steps, shared by log lines, errors and the UI.</summary>
public static class FilterNames
{
    public static string Of(FilterCategory category) => category switch
    {
        FilterCategory.Secrets => "Secrets",
        FilterCategory.PersonalData => "RegEx",
        FilterCategory.Entities => "Entities",
        FilterCategory.RiskyActions => "Meaning",
        FilterCategory.Decision => "Decision",
        _ => category.ToString(),
    };

    public static int StepOf(FilterCategory category) => (int)category + 1;
}

/// <summary>
/// The pattern steps (1 Secrets, 2 RegEx) and the plumbing all four steps share: reading a
/// call's arguments into texts, and replacing what a step found before the next step reads them.
///
/// Every string value is checked, wherever it is nested, and object keys too — a secret pasted as a
/// key is still a secret. Numbers are checked as their raw text, since a card number sent as a JSON
/// number is the same card number.
/// </summary>
public static class FilterEngine
{
    /// <summary>
    /// Step 1 on one text: the text rules over it, and — when it is the value of a JSON field — the
    /// field-name rules over its key. <paramref name="disabled"/> holds the rule ids a profile turned off.
    /// </summary>
    public static IEnumerable<Span> FindSecrets(CallText text, IReadOnlyCollection<string>? disabled = null, SecretScanner? scanner = null)
    {
        var rules = scanner ?? SecretRuleSet.Current;
        var found = rules.Find(text.Text, disabled).Select(m => new Span(m.Start, m.Length, m.RuleId));

        return text.IsValue
            ? found.Concat(rules.FindByKey(KeyOf(text.Path), text.Text, disabled).Select(m => new Span(m.Start, m.Length, m.RuleId)))
            : found;
    }

    /// <summary>The field name a value sits under: "body.auth.password" → "password", "tokens[2]" → "tokens".</summary>
    internal static string KeyOf(string path)
    {
        var last = path[(path.LastIndexOf('.') + 1)..];
        var bracket = last.IndexOf('[');
        return bracket >= 0 ? last[..bracket] : last;
    }

    /// <summary>Step 2 on one text, with the funnel's patterns.</summary>
    public static IEnumerable<Span> FindPersonal(IReadOnlyList<ActivePattern> patterns, string text) =>
        patterns.SelectMany(p => PersonalPatterns.Find(p, text).Select(m => new Span(m.Start, m.Length, p.Name)));

    /// <summary>
    /// <paramref name="text"/> with every span replaced by its name in brackets: what later steps —
    /// and an embedding API — read instead of the value.
    /// </summary>
    public static string Redact(string text, IEnumerable<Span> spans)
    {
        var ordered = spans.Where(s => s.Length > 0).OrderBy(s => s.Start).ToList();
        if (ordered.Count == 0) return text;

        var sb = new StringBuilder();
        var at = 0;
        foreach (var span in ordered)
        {
            if (span.Start < at) continue; // overlaps one already replaced
            sb.Append(text, at, span.Start - at).Append('[').Append(span.Name).Append(']');
            at = span.Start + span.Length;
        }
        return sb.Append(text, at, text.Length - at).ToString();
    }

    /// <summary>Every checkable text in the arguments, with a dotted path naming where it was.</summary>
    public static List<CallText> Read(IEnumerable<KeyValuePair<string, JsonElement>>? arguments)
    {
        var texts = new List<CallText>();
        if (arguments is null) return texts;

        foreach (var (name, value) in arguments)
        {
            texts.Add(new CallText(name, name, IsValue: false));
            Walk(name, value, texts);
        }
        return texts;
    }

    private static void Walk(string path, JsonElement element, List<CallText> into)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                into.Add(new CallText(path, element.GetString() ?? "", IsValue: true));
                break;

            case JsonValueKind.Number:
                into.Add(new CallText(path, element.GetRawText(), IsValue: false));
                break;

            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var child = $"{path}.{property.Name}";
                    into.Add(new CallText(child, property.Name, IsValue: false));
                    Walk(child, property.Value, into);
                }
                break;

            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    Walk($"{path}[{index}]", item, into);
                    index++;
                }
                break;
        }
    }
}
