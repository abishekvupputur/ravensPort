using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace RavensPort.Core.Filters;

/// <summary>A secret found in a text: where, and which gitleaks rule found it.</summary>
public readonly record struct SecretMatch(int Start, int Length, string RuleId);

/// <summary>
/// Step 1 of the call filters: gitleaks' rule set (MIT), embedded unchanged, matched the way
/// gitleaks matches it — keywords as a cheap pre-check, the rule's regex, its secret group, the
/// Shannon-entropy floor (plus gitleaks' "generic rules need a digit"), and the per-rule and global
/// allowlists of regexes and stopwords. Path and commit allowlists do not apply to a tool call.
///
/// Regexes run with <see cref="RegexOptions.NonBacktracking"/>: gitleaks writes them for Go's RE2,
/// which is linear-time, and so is this — no pattern can be made to stall a call.
/// </summary>
public sealed class SecretScanner
{
    private sealed class Allowlist
    {
        public List<Regex> Regexes { get; } = [];
        public string RegexTarget { get; set; } = "secret";
        public List<string> Stopwords { get; } = [];
        public bool RequireAll { get; set; }
        public bool HasOtherCriteria { get; set; }
    }

    private sealed class Rule
    {
        public string Id { get; set; } = "";
        public Regex? Regex { get; set; }
        public double Entropy { get; set; }
        public int SecretGroup { get; set; }
        public List<string> Keywords { get; } = [];
        public List<Allowlist> Allowlists { get; } = [];
    }

    private readonly List<Rule> _rules = [];
    private readonly Allowlist _global = new();

    /// <summary>Rules that could not be used, with why — reported by the UI and pinned by a test.</summary>
    public IReadOnlyList<string> SkippedRules { get; }

    public int RuleCount => _rules.Count;

    /// <summary>The gitleaks release the embedded rules come from.</summary>
    public const string GitleaksVersion = "v8.30.1";

    private static readonly Lazy<SecretScanner> Embedded = new(() =>
    {
        using var stream = typeof(SecretScanner).Assembly.GetManifestResourceStream("RavensPort.Core.Filters.gitleaks.toml")
                           ?? throw new InvalidOperationException("The gitleaks rule set is missing from this build.");
        using var reader = new StreamReader(stream);
        return new SecretScanner(reader.ReadToEnd());
    });

    /// <summary>The rule set that ships with RavensPort.</summary>
    public static SecretScanner Default => Embedded.Value;

    public SecretScanner(string toml)
    {
        var skipped = new List<string>();
        Rule? rule = null;
        Allowlist? allowlist = null;

        foreach (var (section, key, value) in TomlLite.Read(toml))
        {
            switch (section)
            {
                case "[allowlist]":
                    allowlist = _global;
                    break;
                case "[[rules]]":
                    if (key is null)
                    {
                        rule = new Rule();
                        _rules.Add(rule);
                        allowlist = null;
                        continue;
                    }
                    break;
                case "[[rules.allowlists]]":
                    if (key is null)
                    {
                        allowlist = new Allowlist();
                        rule?.Allowlists.Add(allowlist);
                        continue;
                    }
                    break;
            }

            if (key is null) continue;

            if (section == "[[rules]]" && rule is not null)
            {
                switch (key)
                {
                    case "id": rule.Id = (string)value!; break;
                    case "regex":
                        try { rule.Regex = Compile((string)value!); }
                        catch (Exception ex) { skipped.Add($"{rule.Id}: {ex.Message}"); }
                        break;
                    case "entropy": rule.Entropy = Convert.ToDouble(value, CultureInfo.InvariantCulture); break;
                    case "secretGroup": rule.SecretGroup = Convert.ToInt32(value, CultureInfo.InvariantCulture); break;
                    case "keywords": rule.Keywords.AddRange(((List<object>)value!).Cast<string>().Select(k => k.ToLowerInvariant())); break;
                }
            }
            else if (allowlist is not null)
            {
                switch (key)
                {
                    case "regexes":
                        foreach (var r in ((List<object>)value!).Cast<string>())
                        {
                            try { allowlist.Regexes.Add(Compile(r)); }
                            catch (Exception ex) { skipped.Add($"allowlist regex: {ex.Message}"); }
                        }
                        break;
                    case "regexTarget": allowlist.RegexTarget = (string)value!; break;
                    case "stopwords": allowlist.Stopwords.AddRange(((List<object>)value!).Cast<string>().Select(w => w.ToLowerInvariant())); break;
                    case "condition": allowlist.RequireAll = string.Equals((string)value!, "AND", StringComparison.OrdinalIgnoreCase); break;
                    case "paths" or "commits": allowlist.HasOtherCriteria = true; break;
                }
            }
        }

        // A rule with no regex is a path-only rule (it flags file names), which a tool call does not have.
        _rules.RemoveAll(r => r.Regex is null);
        SkippedRules = skipped;
    }

    /// <summary>
    /// Go's RE2 dialect into .NET's: named groups are spelled (?&lt;n&gt;) here, and POSIX classes
    /// such as [[:alnum:]] do not exist. Everything else gitleaks uses means the same in both.
    /// </summary>
    internal static Regex Compile(string re2)
    {
        var pattern = re2
            .Replace("(?P<", "(?<", StringComparison.Ordinal)
            .Replace("[:alnum:]", "a-zA-Z0-9", StringComparison.Ordinal)
            .Replace("[:alpha:]", "a-zA-Z", StringComparison.Ordinal)
            .Replace("[:digit:]", "0-9", StringComparison.Ordinal)
            .Replace("[:xdigit:]", "0-9A-Fa-f", StringComparison.Ordinal)
            .Replace("[:upper:]", "A-Z", StringComparison.Ordinal)
            .Replace("[:lower:]", "a-z", StringComparison.Ordinal)
            .Replace("[:space:]", @"\s", StringComparison.Ordinal);

        return new Regex(pattern, RegexOptions.NonBacktracking | RegexOptions.CultureInvariant);
    }

    /// <summary>Every secret in <paramref name="text"/>.</summary>
    public IEnumerable<SecretMatch> Find(string text)
    {
        if (text.Length < 8) yield break;

        var lower = text.ToLowerInvariant();

        foreach (var rule in _rules)
        {
            if (rule.Keywords.Count > 0 && !rule.Keywords.Any(k => lower.Contains(k, StringComparison.Ordinal))) continue;

            foreach (Match match in rule.Regex!.Matches(text))
            {
                var secretGroup = SecretGroupOf(rule, match);
                var secret = secretGroup.Value;

                if (rule.Entropy > 0)
                {
                    if (ShannonEntropy(secret) <= rule.Entropy) continue;
                    if (rule.Id.StartsWith("generic", StringComparison.Ordinal) && !secret.Any(char.IsAsciiDigit)) continue;
                }

                var line = LineAround(text, match.Index, match.Length);
                if (IsAllowed(_global, secret, match.Value, line) || rule.Allowlists.Any(a => IsAllowed(a, secret, match.Value, line))) continue;

                yield return new SecretMatch(secretGroup.Index, secretGroup.Length, rule.Id);
            }
        }
    }

    /// <summary>gitleaks: the configured secretGroup, else the first non-empty capture group, else the whole match.</summary>
    private static Capture SecretGroupOf(Rule rule, Match match)
    {
        if (rule.SecretGroup > 0 && rule.SecretGroup < match.Groups.Count && match.Groups[rule.SecretGroup].Success)
            return match.Groups[rule.SecretGroup];

        for (var g = 1; g < match.Groups.Count; g++)
            if (match.Groups[g].Success && match.Groups[g].Length > 0) return match.Groups[g];

        return match;
    }

    private static bool IsAllowed(Allowlist list, string secret, string match, string line)
    {
        if (list.Regexes.Count == 0 && list.Stopwords.Count == 0) return false;

        var target = list.RegexTarget switch { "match" => match, "line" => line, _ => secret };
        var lowerSecret = secret.ToLowerInvariant();

        bool? byRegex = list.Regexes.Count == 0 ? null : list.Regexes.Any(r => r.IsMatch(target));
        bool? byStopword = list.Stopwords.Count == 0 ? null : list.Stopwords.Any(w => lowerSecret.Contains(w, StringComparison.Ordinal));

        if (list.RequireAll)
        {
            // AND over every criterion the list has. Paths and commits can never match a tool call.
            if (list.HasOtherCriteria) return false;
            return byRegex != false && byStopword != false;
        }

        return byRegex == true || byStopword == true;
    }

    private static string LineAround(string text, int index, int length)
    {
        var start = text.LastIndexOf('\n', Math.Max(0, index - 1)) + 1;
        var end = text.IndexOf('\n', index + length);
        return text[start..(end < 0 ? text.Length : end)];
    }

    internal static double ShannonEntropy(string s)
    {
        if (s.Length == 0) return 0;
        return s.GroupBy(c => c).Sum(g =>
        {
            var p = (double)g.Count() / s.Length;
            return -p * Math.Log2(p);
        });
    }
}

/// <summary>
/// The slice of TOML gitleaks.toml is written in: [table] and [[array-of-tables]] headers, and
/// key = value with literal '''…''' or '…' strings, basic "…" strings, numbers, booleans and
/// arrays of those spanning lines. Yields (header, null, null) at each header, then its keys.
/// </summary>
internal static class TomlLite
{
    public static IEnumerable<(string Section, string? Key, object? Value)> Read(string toml)
    {
        var section = "";
        var i = 0;

        while (i < toml.Length)
        {
            SkipSpaceAndComments(toml, ref i);
            if (i >= toml.Length) yield break;

            if (toml[i] == '[')
            {
                var end = toml.IndexOf('\n', i);
                section = (end < 0 ? toml[i..] : toml[i..end]).Trim();
                var hash = section.IndexOf(" #", StringComparison.Ordinal);
                if (hash >= 0) section = section[..hash].Trim();
                i = end < 0 ? toml.Length : end + 1;
                yield return (section, null, null);
                continue;
            }

            var eq = toml.IndexOf('=', i);
            var key = toml[i..eq].Trim().Trim('"');
            i = eq + 1;
            var value = ReadValue(toml, ref i);
            yield return (section, key, value);
        }
    }

    private static object ReadValue(string s, ref int i)
    {
        while (i < s.Length && s[i] is ' ' or '\t') i++;

        if (s.AsSpan(i).StartsWith("'''"))
        {
            var end = s.IndexOf("'''", i + 3, StringComparison.Ordinal);
            // A literal string may end in up to two extra quotes that belong to it.
            while (end + 3 < s.Length && s[end + 3] == '\'') end++;
            var value = s[(i + 3)..end];
            i = end + 3;
            if (value.StartsWith('\n')) value = value[1..];
            return value;
        }

        if (s[i] == '\'')
        {
            var end = s.IndexOf('\'', i + 1);
            var value = s[(i + 1)..end];
            i = end + 1;
            return value;
        }

        if (s[i] == '"') return ReadBasic(s, ref i);

        if (s[i] == '[')
        {
            i++;
            var items = new List<object>();
            while (true)
            {
                SkipSpaceAndComments(s, ref i);
                if (s[i] == ']') { i++; return items; }
                if (s[i] == ',') { i++; continue; }
                items.Add(ReadValue(s, ref i));
            }
        }

        var start = i;
        while (i < s.Length && s[i] is not ('\n' or '\r' or ',' or ']' or '#')) i++;
        var bare = s[start..i].Trim();
        if (bare is "true" or "false") return bare == "true";
        return double.Parse(bare, CultureInfo.InvariantCulture);
    }

    private static string ReadBasic(string s, ref int i)
    {
        var sb = new StringBuilder();
        i++;
        while (s[i] != '"')
        {
            if (s[i] == '\\')
            {
                i++;
                switch (s[i])
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case 'u':
                        sb.Append((char)Convert.ToInt32(s.Substring(i + 1, 4), 16));
                        i += 4;
                        break;
                    default: sb.Append(s[i]); break;
                }
            }
            else
            {
                sb.Append(s[i]);
            }
            i++;
        }
        i++;
        return sb.ToString();
    }

    private static void SkipSpaceAndComments(string s, ref int i)
    {
        while (i < s.Length)
        {
            if (char.IsWhiteSpace(s[i])) { i++; continue; }
            if (s[i] == '#')
            {
                var end = s.IndexOf('\n', i);
                i = end < 0 ? s.Length : end + 1;
                continue;
            }
            break;
        }
    }
}
