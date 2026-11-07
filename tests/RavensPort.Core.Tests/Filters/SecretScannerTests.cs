using RavensPort.Core.Filters;

namespace RavensPort.Core.Tests.Filters;

/// <summary>
/// Step 1: the embedded gitleaks rule set. Fixtures are assembled at run time so no scanner
/// mistakes this file for a leak, and none is a real credential.
/// </summary>
public class SecretScannerTests
{
    private static readonly SecretScanner Scanner = SecretScanner.Default;

    [Fact]
    public void EveryGitleaksRuleCompiles_AndNoneIsSkipped()
    {
        Assert.Empty(Scanner.SkippedRules);
        Assert.True(Scanner.RuleCount > 200, $"only {Scanner.RuleCount} rules loaded");
    }

    [Theory]
    [InlineData("aws_access_key_id = AKIA" + "Z7QW3XKPLM2N4R6T")]
    [InlineData("token: ghp_" + "Xk2Lm9Pq4Rs7Tv1Wy3Za5Bc8De6Fg0Hi2Jk")]
    [InlineData("-----BEGIN " + "OPENSSH PRIVATE KEY-----\nb3BlbnNzaC1rZXktdjEAAAAABG5vbmUAAAAEbm9uZQAAAAAAAAABAAAAMwAAAAtzc2gtZW\n-----END OPENSSH PRIVATE KEY-----")]
    [InlineData("export API_SECRET=\"q8Zr2Lx9Vb4Nm7Kp3Wt6Yh1Jd5\"")]
    [InlineData("Authorization: Bearer eyJhbGciOiJIUzI1NiJ9" + ".eyJzdWIiOiIxMjM0NTY3ODkwIiwibmFtZSI6IkpvaG4ifQ.Xk2Lm9Pq4Rs7Tv1Wy3Za5Bc8De6Fg0Hi2Jk")]
    public void CommonCredentials_AreFound(string text)
    {
        Assert.NotEmpty(Scanner.Find(text));
    }

    [Theory]
    [InlineData("please fix the failing unit test in the parser")]
    [InlineData("password = example")]
    [InlineData("api_version = v2")]
    [InlineData("the access key is stored in the vault, ask an admin")]
    [InlineData("SELECT * FROM users WHERE id = 42")]
    public void OrdinaryText_IsNot(string text)
    {
        Assert.Empty(Scanner.Find(text));
    }

    [Fact]
    public void TheSpanCoversTheSecret_NotTheWholeLine()
    {
        var key = "AKIA" + "Z7QW3XKPLM2N4R6T";
        var text = $"aws_access_key_id = {key}";

        var match = Assert.Single(Scanner.Find(text));

        Assert.Equal(key, text.Substring(match.Start, match.Length));
    }

    [Fact]
    public void Re2SyntaxIsTranslated()
    {
        Assert.Matches(SecretScanner.Compile("(?P<x>[[:alnum:]]{3})"), "ab1");
    }
}
