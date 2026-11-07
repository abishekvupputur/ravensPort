using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Text.RegularExpressions;
using RavensPort.Core.Models;

namespace RavensPort.Core.Filters;

/// <summary>A check a pattern's matches must also pass, so a regex alone does not decide.</summary>
public enum PatternCheck
{
    None,
    Luhn,
    Iban,
    IPv4,
    IPv6,
}

/// <summary>A built-in Personal data pattern. Users can edit the regex; the check stays.</summary>
public sealed record PatternDefinition(string Name, string Regex, PatternCheck Check, bool OnByDefault, string Hint);

/// <summary>A pattern as one funnel runs it: built-in (maybe edited) or the user's own.</summary>
public sealed record ActivePattern(string Name, string Regex, PatternCheck Check, bool IsBuiltIn);

/// <summary>
/// Step 2 of the call filters: personal data by pattern. The built-ins are ordinary regexes the
/// user can edit, switch off, or add to; where a format has a checksum or a grammar (cards, IBANs,
/// IP addresses) a match must also pass it, because a regex alone flags every long number and
/// every version string.
/// </summary>
public static class PersonalPatterns
{
    public static IReadOnlyList<PatternDefinition> BuiltIn { get; } =
    [
        new("Email address", @"\b[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}\b", PatternCheck.None, true,
            "name@example.com"),
        // ponytail: Luhn passes ~1 in 10 random 13-19 digit runs (order ids); add an issuer-prefix
        // check (4, 51-55, 2221-2720, 34/37, 6011/65) if this proves too noisy.
        new("Payment card number", @"\b(?:\d[ \-]?){12,18}\d\b", PatternCheck.Luhn, true,
            "Also passes the Luhn checksum"),
        new("IBAN", @"\b[A-Z]{2}\d{2}(?: ?[A-Z0-9]){11,30}\b", PatternCheck.Iban, true,
            "Also passes the ISO 13616 mod-97 check"),
        new("Phone number (international)", @"(?<![\w+])\+[1-9]\d{0,2}(?:[\s./-]?\(?\d{1,5}\)?){2,5}(?!\w)", PatternCheck.None, true,
            "+49 170 0000000, +1 (555) 555 0100"),
        new("German mobile number", @"(?<![\w+])0?1[5-7]\d[\s/-]?\d{3,4}[\s/-]?\d{3,5}\b", PatternCheck.None, true,
            "0170 0000000"),
        new("Tax ID (Steuer-ID)", @"(?i)\b(?:steuer-?id|steuerliche identifikationsnummer|idnr)\b\D{0,10}\d{2}\s?\d{3}\s?\d{3}\s?\d{3}\b", PatternCheck.None, true,
            "Steuer-ID 12 345 678 901"),
        new("Social security no. (DE)", @"\b\d{2}\s?\d{6}\s?[A-Z]\s?\d{3}\b", PatternCheck.None, true,
            "12 010100 X 000"),
        new("ID document file", @"(?i)(personalausweis|reisepass|passport|id[_-]?card|f[üu]hrerschein|driving[_-]?licen[cs]e)", PatternCheck.None, true,
            "Personalausweis.jpg, passport_scan.pdf"),
        new("Street address (DE)", @"\b[A-ZÄÖÜ][a-zäöüß]+(?:str\.|straße|strasse|weg|platz|allee|gasse)\s?\d{1,4}[a-z]?\b", PatternCheck.None, false,
            "Musterstraße 1 — also matches street names in ordinary text"),
        new("Postcode + city (DE)", @"\b\d{5}\s+[A-ZÄÖÜ][a-zäöüß]{2,}", PatternCheck.None, false,
            "12345 Musterstadt — also matches some counts and ids"),
        new("IPv4 address", @"\b\d{1,3}(?:\.\d{1,3}){3}\b", PatternCheck.IPv4, false,
            "Personal data under GDPR, but in every config and log too"),
        new("IPv6 address", @"(?<![0-9A-Za-z:])[0-9A-Fa-f]{0,4}(?::[0-9A-Fa-f]{0,4}){2,7}(?![0-9A-Za-z:])", PatternCheck.IPv6, false,
            "2001:db8::1"),
    ];

    /// <summary>What one funnel's Personal data step runs, after its switches, edits and additions.</summary>
    public static List<ActivePattern> Active(FunnelFilter filter)
    {
        var active = new List<ActivePattern>();

        foreach (var d in BuiltIn)
        {
            var on = d.OnByDefault
                ? !filter.DisabledDetectors.Contains(d.Name, StringComparer.Ordinal)
                : filter.EnabledDetectors.Contains(d.Name, StringComparer.Ordinal);
            if (!on) continue;

            var regex = filter.RegexOverrides.TryGetValue(d.Name, out var edited) ? edited : d.Regex;
            active.Add(new ActivePattern(d.Name, regex, d.Check, IsBuiltIn: true));
        }

        foreach (var p in filter.Patterns.Where(p => !filter.DisabledDetectors.Contains(p.Name, StringComparer.Ordinal)))
        {
            active.Add(new ActivePattern(p.Name, p.Regex, PatternCheck.None, IsBuiltIn: false));
        }

        return active;
    }

    // A pattern that backtracks badly on a hostile string must not stall a call. A timeout counts
    // as a match (fail closed), so this bounds the cost, not the protection.
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(250);

    private static readonly ConcurrentDictionary<string, Regex> Compiled = new(StringComparer.Ordinal);

    internal static Regex Compile(string pattern) =>
        Compiled.GetOrAdd(pattern, p => new Regex(p, RegexOptions.Compiled | RegexOptions.CultureInvariant, Timeout));

    /// <summary>
    /// Every match of <paramref name="pattern"/> in <paramref name="text"/> that passes its check.
    /// A pattern that throws or times out reports the whole text: failing closed.
    /// </summary>
    public static IEnumerable<(int Start, int Length)> Find(ActivePattern pattern, string text)
    {
        List<(int, int)> found;
        try
        {
            found = [.. Compile(pattern.Regex).Matches(text)
                .Where(m => Passes(pattern.Check, m.Value))
                .Select(m => (m.Index, m.Length))];
        }
        catch (Exception)
        {
            found = [(0, text.Length)];
        }
        return found;
    }

    public static bool Passes(PatternCheck check, string candidate) => check switch
    {
        PatternCheck.Luhn => IsLuhnValid(candidate),
        PatternCheck.Iban => IsIbanValid(candidate),
        PatternCheck.IPv4 => IsAddress(candidate, AddressFamily.InterNetwork),
        PatternCheck.IPv6 => candidate.Any(char.IsAsciiHexDigit) && IsAddress(candidate, AddressFamily.InterNetworkV6),
        _ => true,
    };

    /// <summary>Why a pattern cannot be saved, or null when it can.</summary>
    public static string? Validate(string? name, string? regex)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Give the pattern a name — it is what the log and the agent are told.";
        if (string.IsNullOrWhiteSpace(regex)) return "Enter a regular expression.";
        if (regex.Length > 500) return "Keep the pattern under 500 characters.";

        Regex compiled;
        try
        {
            compiled = new Regex(regex, RegexOptions.CultureInvariant, Timeout);
        }
        catch (ArgumentException ex)
        {
            return $"Not a valid regular expression: {ex.Message}";
        }

        // A pattern that matches nothing at all matches every call — almost certainly a mistake.
        if (compiled.IsMatch("")) return "This pattern matches empty text, so it would match every call.";

        return null;
    }

    internal static bool IsLuhnValid(string candidate)
    {
        var digits = candidate.Where(char.IsAsciiDigit).Select(c => c - '0').ToArray();
        if (digits.Length is < 13 or > 19) return false;

        var sum = 0;
        for (var i = 0; i < digits.Length; i++)
        {
            var d = digits[digits.Length - 1 - i];
            if (i % 2 == 1)
            {
                d *= 2;
                if (d > 9) d -= 9;
            }
            sum += d;
        }

        return sum % 10 == 0;
    }

    internal static bool IsIbanValid(string candidate)
    {
        var iban = candidate.Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();
        if (iban.Length is < 15 or > 34 || !iban.All(char.IsAsciiLetterOrDigit)) return false;

        // ISO 13616: move the country code and check digits to the end, letters become 10..35,
        // and the whole number mod 97 must be 1.
        var rearranged = iban[4..] + iban[..4];
        var numeric = string.Concat(rearranged.Select(c => char.IsAsciiDigit(c) ? c.ToString() : (c - 'A' + 10).ToString()));

        return BigInteger.Parse(numeric) % 97 == 1;
    }

    private static bool IsAddress(string candidate, AddressFamily family) =>
        IPAddress.TryParse(candidate, out var address) && address.AddressFamily == family;
}
