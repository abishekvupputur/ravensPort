using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace RavensPort.Core.Filters;

/// <summary>A secret found in a text: where, and which rule found it.</summary>
public readonly record struct SecretMatch(int Start, int Length, string RuleId);

/// <summary>One rule as the UI lists it.</summary>
/// <param name="Source">Where it came from: "gitleaks", "RavensPort" or "your rules".</param>
public sealed record SecretRuleInfo(string Id, string Description, string Source);

/// <summary>
/// Step 1 of the call filters: secret rules in gitleaks' format, matched the way gitleaks matches
/// them — keywords as a cheap pre-check, the rule's regex, its secret group, the Shannon-entropy
/// floor (plus gitleaks' "generic rules need a digit"), and the per-rule and global allowlists of
/// regexes and stopwords. Path and commit allowlists do not apply to a tool call.
///
/// Rules come from several sources, read in order: gitleaks' own rule set, RavensPort's (passwords,
/// usernames and tokens, which gitleaks does not look for — see Rules/ravensport.toml), and the
/// user's rules file. A rule with an id seen before replaces the earlier one, which is how a user
/// edits a built-in rule.
///
/// One extension to the format: `keyName`. A tool call's arguments are JSON, so a password arrives
/// as the field "password" and the value "hunter2" — two texts, neither of which a text regex can
/// tie together. A keyName rule matches the value of a field whose name matches it.
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
        public string Description { get; set; } = "";
        public string Source { get; set; } = "";
        public Regex? Regex { get; set; }
        public Regex? KeyName { get; set; }
        public double Entropy { get; set; }
        public int SecretGroup { get; set; }
        public List<string> Keywords { get; } = [];
        public List<Allowlist> Allowlists { get; } = [];
    }

    private readonly List<Rule> _rules = [];
    private readonly List<Allowlist> _global = [];

    /// <summary>Rules that could not be used, with why — reported by the UI and pinned by a test.</summary>
    public IReadOnlyList<string> SkippedRules { get; }

    public int RuleCount => _rules.Count;

    /// <summary>Every rule in use, for the UI's list.</summary>
    public IReadOnlyList<SecretRuleInfo> Rules => [.. _rules.Select(r => new SecretRuleInfo(r.Id, r.Description, r.Source))];

    public int CountFrom(string source) => _rules.Count(r => r.Source == source);

    /// <summary>The gitleaks release the embedded rules come from.</summary>
    public const string GitleaksVersion = "v8.30.1";

    /// <summary>One rule set from text — mostly for tests.</summary>
    public SecretScanner(string toml, string source = "rules") : this([(source, toml)]) { }

    public SecretScanner(IEnumerable<(string Source, string Toml)> sources)
    {
        var skipped = new List<string>();
        var byId = new Dictionary<string, Rule>(StringComparer.Ordinal);
        var order = new List<string>();

        foreach (var (source, toml) in sources)
        {
            try
            {
                Load(source, toml, byId, order, skipped);
            }
            catch (Exception ex)
            {
                // A user's file with a typo must not take the built-in rules down with it.
                skipped.Add($"{source}: could not be read — {ex.Message}");
            }
        }

        // A rule with neither a regex nor a keyName is a path-only rule (it flags file names),
        // which a tool call does not have.
        _rules.AddRange(order.Select(id => byId[id]).Where(r => r.Regex is not null || r.KeyName is not null));
        SkippedRules = skipped;
    }

    private void Load(string source, string toml, Dictionary<string, Rule> byId, List<string> order, List<string> skipped)
    {
        Rule? rule = null;
        Allowlist? allowlist = null;

        foreach (var (section, key, value) in TomlLite.Read(toml))
        {
            if (key is null)
            {
                switch (section)
                {
                    case "[allowlist]" or "[[allowlists]]":
                        allowlist = new Allowlist();
                        _global.Add(allowlist);
                        rule = null;
                        break;
                    case "[[rules]]":
                        rule = new Rule { Source = source };
                        allowlist = null;
                        break;
                    case "[[rules.allowlists]]":
                        allowlist = new Allowlist();
                        rule?.Allowlists.Add(allowlist);
                        break;
                }
                continue;
            }

            if (section == "[[rules]]" && rule is not null)
            {
                switch (key)
                {
                    case "id":
                        rule.Id = (string)value!;
                        if (!byId.ContainsKey(rule.Id)) order.Add(rule.Id);
                        byId[rule.Id] = rule;
                        break;
                    case "description": rule.Description = (string)value!; break;
                    case "regex":
                        try { rule.Regex = Compile((string)value!); }
                        catch (Exception ex) { skipped.Add($"{source} {rule.Id}: {ex.Message}"); }
                        break;
                    case "keyName":
                        try { rule.KeyName = Compile((string)value!); }
                        catch (Exception ex) { skipped.Add($"{source} {rule.Id}: {ex.Message}"); }
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
                            catch (Exception ex) { skipped.Add($"{source} allowlist regex: {ex.Message}"); }
                        }
                        break;
                    case "regexTarget": allowlist.RegexTarget = (string)value!; break;
                    case "stopwords": allowlist.Stopwords.AddRange(((List<object>)value!).Cast<string>().Select(w => w.ToLowerInvariant())); break;
                    case "condition": allowlist.RequireAll = string.Equals((string)value!, "AND", StringComparison.OrdinalIgnoreCase); break;
                    case "paths" or "commits": allowlist.HasOtherCriteria = true; break;
                }
            }
        }
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

    /// <summary>Every secret in <paramref name="text"/>, skipping rules in <paramref name="disabled"/>.</summary>
    public IEnumerable<SecretMatch> Find(string text, IReadOnlyCollection<string>? disabled = null)
    {
        if (text.Length < 3) yield break;

        var lower = text.ToLowerInvariant();

        foreach (var rule in _rules)
        {
            if (rule.Regex is null || rule.KeyName is not null) continue;
            if (disabled?.Contains(rule.Id) == true) continue;
            if (rule.Keywords.Count > 0 && !rule.Keywords.Any(k => lower.Contains(k, StringComparison.Ordinal))) continue;

            foreach (Match match in rule.Regex.Matches(text))
            {
                var secretGroup = SecretGroupOf(rule, match);
                var secret = secretGroup.Value;

                if (rule.Entropy > 0)
                {
                    if (ShannonEntropy(secret) <= rule.Entropy) continue;
                    if (rule.Id.StartsWith("generic", StringComparison.Ordinal) && !secret.Any(char.IsAsciiDigit)) continue;
                }

                var line = LineAround(text, match.Index, match.Length);
                if (_global.Any(a => IsAllowed(a, secret, match.Value, line)) || rule.Allowlists.Any(a => IsAllowed(a, secret, match.Value, line))) continue;

                yield return new SecretMatch(secretGroup.Index, secretGroup.Length, rule.Id);
            }
        }
    }

    /// <summary>
    /// The value of a field named <paramref name="key"/>, judged by the keyName rules: the whole
    /// value is a secret when the name matches (and the value matches the rule's regex, if it has
    /// one), unless an allowlist says the value is only a placeholder.
    /// </summary>
    public IEnumerable<SecretMatch> FindByKey(string key, string value, IReadOnlyCollection<string>? disabled = null)
    {
        if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(value)) yield break;

        foreach (var rule in _rules)
        {
            if (rule.KeyName is null || disabled?.Contains(rule.Id) == true) continue;
            if (!rule.KeyName.IsMatch(key)) continue;
            if (rule.Regex is not null && !rule.Regex.IsMatch(value)) continue;
            if (_global.Any(a => IsAllowed(a, value, value, value)) || rule.Allowlists.Any(a => IsAllowed(a, value, value, value))) continue;

            yield return new SecretMatch(0, value.Length, rule.Id);
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
/// The secret rules in use: one file, the user's — %LocalAppData%\RavensPort\filters\secret-rules.toml.
/// It is the whole configuration: whatever rules it holds are the rules, and the user adds, edits
/// and deletes them freely. Until it exists, and after a reset, it is the original: gitleaks' rule
/// set, RavensPort's rules and the strict rules, in one file. It lives on this PC, not in the vault
/// — gitleaks' rules alone are larger than a vault note can be.
/// </summary>
public static class SecretRuleSet
{
    public const string UserSource = "your rules file";

    /// <summary>Where a rule came from, judged by its id: for the UI's counts and tooltips.</summary>
    public const string GitleaksOrigin = "gitleaks", RavensPortOrigin = "RavensPort", StrictOrigin = "strict", YoursOrigin = "yours";

    internal static string? DirectoryOverride { get; set; }

    public static string UserFilePath => Path.Combine(
        DirectoryOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RavensPort", "filters"),
        "secret-rules.toml");

    public static string BackupFilePath => UserFilePath + ".bak";

    /// <summary>True while there is no file of the user's yet, so the original rules apply.</summary>
    public static bool UsingOriginal => !File.Exists(UserFilePath);

    private static SecretScanner? _current;
    private static readonly Lock Gate = new();

    public static SecretScanner Current
    {
        get
        {
            lock (Gate)
            {
                return _current ??= Build();
            }
        }
    }

    /// <summary>Reads the file again — after the user saved it.</summary>
    public static SecretScanner Reload()
    {
        var scanner = Build();
        lock (Gate)
        {
            _current = scanner;
        }
        return scanner;
    }

    private static SecretScanner Build()
    {
        string rules;
        try
        {
            rules = File.Exists(UserFilePath) ? File.ReadAllText(UserFilePath) : OriginalRules;
        }
        catch (IOException)
        {
            // Locked by an editor mid-save, say. Better the original than no rules; Reload tries again.
            rules = OriginalRules;
        }

        return new SecretScanner(rules, UserSource);
    }

    /// <summary>The user's rules file, written with the original rules the first time it is asked for.</summary>
    public static string EnsureUserFile()
    {
        if (!File.Exists(UserFilePath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(UserFilePath)!);
            File.WriteAllText(UserFilePath, OriginalRules);
        }
        return UserFilePath;
    }

    /// <summary>Puts the original rules back. The file being replaced is kept as secret-rules.toml.bak.</summary>
    public static SecretScanner ResetUserFile()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(UserFilePath)!);
        if (File.Exists(UserFilePath)) File.Copy(UserFilePath, BackupFilePath, overwrite: true);
        File.WriteAllText(UserFilePath, OriginalRules);
        return Reload();
    }

    /// <summary>
    /// The original rules file: gitleaks' rule set, RavensPort's rules and the strict rules, in one
    /// valid TOML document — one title, and global allowlists as [[allowlists]] entries, which
    /// gitleaks itself reads.
    /// </summary>
    public static string OriginalRules => _original.Value;

    private static readonly Lazy<string> _original = new(() =>
        Header +
        $"\n# ===== gitleaks {SecretScanner.GitleaksVersion} rules (MIT, Copyright (c) 2019 Zachary Rice) =====\n\n" +
        AsPart(Embedded("RavensPort.Core.Filters.gitleaks.toml")) +
        "\n# ===== RavensPort rules: passwords, usernames, tokens and fields named like them =====\n\n" +
        AsPart(Embedded("RavensPort.Core.Filters.ravensport.toml")) +
        "\n# ===== Strict rules: broad generic patterns =====\n\n" +
        AsPart(Embedded("RavensPort.Core.Filters.strict.toml")));

    private const string Header = """
        # RavensPort's secret rules (step 1, Secrets), in gitleaks' format:
        # https://github.com/gitleaks/gitleaks#configuration
        #
        # This file is the whole configuration: the rules here are the rules step 1 uses, all on. Add,
        # edit or delete any of them. To turn one off for a single profile instead, use that profile's
        # Secrets step. "Reset rules" on the Filters tab brings back this original file (yours is kept
        # as secret-rules.toml.bak). After saving, press "Reload rules".
        #
        # Rules run with .NET's linear-time regex engine (like gitleaks' Go RE2): no lookarounds and no
        # backreferences. Besides gitleaks' fields, a rule can have `keyName`: it then matches the value
        # of a JSON field whose name matches the keyName regex.

        title = "RavensPort secret rules"

        """;

    /// <summary>One embedded file as part of the combined one: no title of its own, global allowlists as entries.</summary>
    private static string AsPart(string toml) => string.Join('\n', toml.Replace("\r\n", "\n").Split('\n')
        .Where(line => !line.StartsWith("title ", StringComparison.Ordinal) && !line.StartsWith("minVersion ", StringComparison.Ordinal))
        .Select(line => line.Trim() == "[allowlist]" ? "[[allowlists]]" : line)) + "\n";

    private static string Embedded(string name)
    {
        using var stream = typeof(SecretRuleSet).Assembly.GetManifestResourceStream(name)
                           ?? throw new InvalidOperationException($"The rule set {name} is missing from this build.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static readonly Lazy<HashSet<string>> GitleaksIds = new(() =>
        [.. System.Text.RegularExpressions.Regex.Matches(Embedded("RavensPort.Core.Filters.gitleaks.toml"), "(?m)^id = \"([^\"]+)\"",
            System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(5)).Select(m => m.Groups[1].Value)]);

    /// <summary>Where a rule came from: gitleaks, RavensPort, strict, or the user's own.</summary>
    public static string OriginOf(string id) =>
        id.StartsWith("ravensport-", StringComparison.Ordinal) ? RavensPortOrigin
        : id.StartsWith("strict-", StringComparison.Ordinal) ? StrictOrigin
        : GitleaksIds.Value.Contains(id) ? GitleaksOrigin
        : YoursOrigin;
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
            if (eq < 0) throw new FormatException($"expected 'key = value' near \"{Snippet(toml, i)}\"");
            var key = toml[i..eq].Trim().Trim('"');
            i = eq + 1;
            var value = ReadValue(toml, ref i);
            yield return (section, key, value);
        }
    }

    private static string Snippet(string s, int i) => s.Substring(i, Math.Min(40, s.Length - i)).Split('\n')[0];

    private static object ReadValue(string s, ref int i)
    {
        while (i < s.Length && s[i] is ' ' or '\t') i++;
        if (i >= s.Length) throw new FormatException("a key has no value");

        if (s.AsSpan(i).StartsWith("'''"))
        {
            var end = s.IndexOf("'''", i + 3, StringComparison.Ordinal);
            if (end < 0) throw new FormatException("a ''' string is not closed");
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
            if (end < 0) throw new FormatException("a ' string is not closed");
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
                if (i >= s.Length) throw new FormatException("an array is not closed");
                if (s[i] == ']') { i++; return items; }
                if (s[i] == ',') { i++; continue; }
                items.Add(ReadValue(s, ref i));
            }
        }

        var start = i;
        while (i < s.Length && s[i] is not ('\n' or '\r' or ',' or ']' or '#')) i++;
        var bare = s[start..i].Trim();
        if (bare is "true" or "false") return bare == "true";
        return double.TryParse(bare, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? number
            : throw new FormatException($"cannot read the value \"{bare}\"");
    }

    private static string ReadBasic(string s, ref int i)
    {
        var sb = new StringBuilder();
        i++;
        while (i < s.Length && s[i] != '"')
        {
            if (s[i] == '\\' && i + 1 < s.Length)
            {
                i++;
                switch (s[i])
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case 'u' when i + 4 < s.Length:
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
        if (i >= s.Length) throw new FormatException("a \" string is not closed");
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
