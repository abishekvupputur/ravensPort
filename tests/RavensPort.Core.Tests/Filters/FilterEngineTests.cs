using System.Text.Json;
using RavensPort.Core.Filters;
using RavensPort.Core.Models;

namespace RavensPort.Core.Tests.Filters;

/// <summary>Step 2 (Personal data patterns), and the reading and redaction every step shares.</summary>
public class FilterEngineTests
{
    private static List<ActivePattern> Defaults() => PersonalPatterns.Active(new FunnelFilter { Category = FilterCategory.PersonalData });

    private static List<string> Found(string text, FunnelFilter? filter = null) =>
        [.. FilterEngine.FindPersonal(filter is null ? Defaults() : PersonalPatterns.Active(filter), text).Select(s => s.Name).Distinct()];

    [Theory]
    [InlineData("4111 1111 1111 1111", true)]   // the standard Visa test number: Luhn-valid
    [InlineData("4111 1111 1111 1112", false)]  // one digit off
    [InlineData("order 1234567890123", false)]  // a long number that fails Luhn
    public void Cards_NeedAValidChecksum(string text, bool expected)
    {
        Assert.Equal(expected, Found(text).Contains("Payment card number"));
    }

    [Theory]
    [InlineData("DE89 3704 0044 0532 0130 00", true)]  // the ISO 13616 example IBAN
    [InlineData("DE89 3704 0044 0532 0130 01", false)]
    public void Ibans_NeedAValidChecksum(string text, bool expected)
    {
        Assert.Equal(expected, Found(text).Contains("IBAN"));
    }

    [Theory]
    [InlineData("FYI jane.doe@example.com", "Email address")]
    [InlineData("call +49 170 0000000", "Phone number (international)")]
    [InlineData("ruf 0170 0000000 an", "German mobile number")]
    [InlineData("Steuer-ID 12 345 678 901", "Tax ID (Steuer-ID)")]
    [InlineData("SVNR 12 010100 X 000", "Social security no. (DE)")]
    [InlineData("C:/Users/abish/Documents/Personalausweis.jpg", "ID document file")]
    public void DefaultPatterns_FindWhatTheyAreFor(string text, string pattern)
    {
        Assert.Contains(pattern, Found(text));
    }

    [Theory]
    [InlineData("please fix the failing unit test in the parser")]
    [InlineData("Address field validation fails for German postcodes")]
    [InlineData("version 10.0.26300.1, connect to 192.168.1.20")]  // IPs are off by default
    [InlineData("git commit -m 'fix order total'")]
    public void OrdinaryText_IsNotFoundByTheDefaults(string text)
    {
        Assert.Empty(Found(text));
    }

    [Fact]
    public void AnOffByDefaultPattern_RunsOnlyOnceSwitchedOn()
    {
        var filter = new FunnelFilter { Category = FilterCategory.PersonalData, EnabledDetectors = ["IPv4 address"] };

        Assert.Equal(["IPv4 address"], Found("connect to 192.168.1.20", filter));
    }

    [Fact]
    public void ADefaultPattern_CanBeSwitchedOff()
    {
        var filter = new FunnelFilter { Category = FilterCategory.PersonalData, DisabledDetectors = ["Email address"] };

        Assert.Empty(Found("jane.doe@example.com", filter));
    }

    [Fact]
    public void AnEditedBuiltIn_UsesTheNewRegex_AndKeepsItsChecksum()
    {
        // Narrowed to Visa only — but a Visa-shaped number still has to pass Luhn.
        var filter = new FunnelFilter
        {
            Category = FilterCategory.PersonalData,
            RegexOverrides = new() { ["Payment card number"] = @"\b4(?:\d[ \-]?){12,15}\d\b" },
        };

        Assert.Contains("Payment card number", Found("4111 1111 1111 1111", filter));
        Assert.DoesNotContain("Payment card number", Found("4111 1111 1111 1112", filter));
        Assert.DoesNotContain("Payment card number", Found("5555 5555 5555 4444", filter)); // valid Mastercard, now out of scope
    }

    [Fact]
    public void ACustomPattern_MatchesUnderItsOwnName()
    {
        var filter = new FunnelFilter { Category = FilterCategory.PersonalData, Patterns = [new CustomPattern { Name = "Customer id", Regex = @"\bKD-\d{6}\b" }] };

        Assert.Contains("Customer id", Found("see KD-123456", filter));
    }

    [Fact]
    public void APatternThatThrows_ReportsTheWholeText_FailingClosed()
    {
        var broken = new ActivePattern("broken", "(", PatternCheck.None, IsBuiltIn: false);

        Assert.Equal([(0, 8)], PersonalPatterns.Find(broken, "harmless"));
    }

    [Theory]
    [InlineData("", @"\d", "name")]
    [InlineData("n", "", "regular expression")]
    [InlineData("n", "(unclosed", "Not a valid")]
    [InlineData("n", "a*", "empty text")]
    public void BadPatterns_AreRefusedWithAReason(string name, string regex, string expected)
    {
        Assert.Contains(expected, PersonalPatterns.Validate(name, regex));
    }

    [Theory]
    [InlineData("connect to 2001:db8::1 now", "IPv6 address")]
    [InlineData("connect to 192.168.1.20 now", "IPv4 address")]
    public void IpAddresses_AreToldApartByFamily(string text, string expected)
    {
        var filter = new FunnelFilter { Category = FilterCategory.PersonalData, EnabledDetectors = ["IPv4 address", "IPv6 address"], DisabledDetectors = ["Phone number (international)", "German mobile number"] };

        Assert.Equal([expected], Found(text, filter));
    }

    [Theory]
    [InlineData("meeting at 12:30:45")]
    [InlineData("call std::vector::push_back")]
    [InlineData("mac aa:bb:cc:dd:ee:ff")]
    [InlineData("999.1.1.1")]
    public void ThingsThatOnlyLookLikeAddresses_DoNotMatch(string text)
    {
        var filter = new FunnelFilter { Category = FilterCategory.PersonalData, EnabledDetectors = ["IPv4 address", "IPv6 address"] };

        Assert.DoesNotContain(Found(text, filter), n => n.StartsWith("IP", StringComparison.Ordinal));
    }

    // ---- shared plumbing --------------------------------------------------------------------

    [Fact]
    public void Redact_ReplacesEachSpanWithItsName_AndSkipsOverlaps()
    {
        var text = "John Doe, jane@example.com";

        // Positions found, not counted by hand, so the example text can change without breaking them.
        var email = text.IndexOf("jane@", StringComparison.Ordinal);
        var redacted = FilterEngine.Redact(text, [new Span(email, text.Length - email, "Email address"), new Span(0, "John Doe".Length, "person"), new Span(5, 3, "overlap")]);

        Assert.Equal("[person], [Email address]", redacted);
    }

    [Fact]
    public void Read_ReturnsKeysValuesAndNumbers_AndMarksWhichAreValues()
    {
        var args = JsonSerializer.SerializeToElement(new { to = "team", nested = new { note = "hi there" }, list = new[] { "a" }, count = 12345 })
            .EnumerateObject().Select(p => KeyValuePair.Create(p.Name, p.Value.Clone())).ToList();

        var texts = FilterEngine.Read(args);

        Assert.Contains(new CallText("nested.note", "hi there", IsValue: true), texts);
        Assert.Contains(new CallText("nested.note", "note", IsValue: false), texts);
        Assert.Contains(new CallText("list[0]", "a", IsValue: true), texts);
        Assert.Contains(new CallText("count", "12345", IsValue: false), texts);
    }
}
