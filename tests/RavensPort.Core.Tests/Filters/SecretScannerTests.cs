using RavensPort.Core.Filters;

namespace RavensPort.Core.Tests.Filters;

/// <summary>
/// Step 1: the embedded gitleaks rule set. Fixtures are assembled at run time so no scanner
/// mistakes this file for a leak, and none is a real credential.
/// </summary>
public class SecretScannerTests
{
    private static readonly SecretScanner Scanner = SecretRuleSet.Current;

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

    // ---- what gitleaks alone did not catch: RavensPort's rules ------------------------------

    [Theory]
    [InlineData("password", "hunter2", "ravensport-password-field")]
    [InlineData("db_password", "s3cret!", "ravensport-password-field")]
    [InlineData("client_secret", "abc", "ravensport-secret-field")]
    [InlineData("refresh_token", "1//0gExampleExampleExampleExampleExample", "ravensport-token-field")]
    [InlineData("accessToken", "xyz123", "ravensport-token-field")]
    [InlineData("username", "admin", "ravensport-username-field")]
    [InlineData("login", "jane.doe", "ravensport-username-field")]
    public void AFieldNamedLikeASecret_IsASecret_HoweverSimpleItsValue(string key, string value, string rule)
    {
        Assert.Contains(Scanner.FindByKey(key, value), m => m.RuleId == rule);
    }

    [Theory]
    [InlineData("password", "")]
    [InlineData("password", "${DB_PASSWORD}")]
    [InlineData("password", "********")]
    [InlineData("password", "<password>")]
    [InlineData("token", "null")]
    [InlineData("note", "hunter2")]
    [InlineData("passenger", "Jane Doe")]
    public void PlaceholdersAndOrdinaryFields_AreNot(string key, string value)
    {
        Assert.Empty(Scanner.FindByKey(key, value));
    }

    [Theory]
    [InlineData("connect with password=hunter2 please", "ravensport-password-assignment")]
    [InlineData("mysql -u root --password hunter2", "ravensport-password-flag")]
    [InlineData("postgres://jane:hunter2@db.example.com/app", "ravensport-url-credentials")]
    [InlineData("Authorization: Bearer abcdefgh12345678", "ravensport-bearer-header")]
    [InlineData("Authorization: Basic am9objpodW50ZXIy", "ravensport-basic-auth-header")]
    [InlineData("refresh_token=abc123def456ghi", "ravensport-token-assignment")]
    [InlineData("user: admin", "ravensport-username-assignment")]
    [InlineData("token 1//0gExampleExampleExampleExampleExample", "ravensport-google-refresh-token")]
    public void CredentialsWrittenIntoText_AreFound(string text, string rule)
    {
        Assert.Contains(Scanner.Find(text), m => m.RuleId == rule);
    }

    [Theory]
    [InlineData("please send a password reset link to the user")]
    [InlineData("the login page shows the username field")]
    public void ProseAboutCredentials_IsNot(string text)
    {
        Assert.Empty(Scanner.Find(text));
    }

    [Fact]
    public void ARuleCanBeTurnedOff()
    {
        Assert.DoesNotContain(Scanner.Find("password=hunter2", ["ravensport-password-assignment"]), m => m.RuleId == "ravensport-password-assignment");
        Assert.Empty(Scanner.FindByKey("password", "hunter2", ["ravensport-password-field"]));
    }

    [Fact]
    public void EveryRuleIsOnByDefault_FromAllSources()
    {
        Assert.True(Scanner.CountFrom(SecretRuleSet.GitleaksSource) > 200);
        Assert.True(Scanner.CountFrom(SecretRuleSet.RavensPortSource) >= 12);
        Assert.All(Scanner.Rules, r => Assert.False(string.IsNullOrEmpty(r.Id)));
    }

    [Fact]
    public void AUserRuleWithABuiltInId_ReplacesIt_AndNewOnesAreAdded()
    {
        var scanner = new SecretScanner(
        [
            ("built-in", """
                [[rules]]
                id = "thing"
                description = "built-in"
                regex = '''THING-\d+'''
                """),
            ("yours", """
                [[rules]]
                id = "thing"
                description = "edited"
                regex = '''OTHER-\d+'''

                [[rules]]
                id = "mine"
                keyName = '''(?i)^customer[_-]?number$'''
                """),
        ]);

        Assert.Equal(2, scanner.RuleCount);
        Assert.Equal("edited", scanner.Rules.Single(r => r.Id == "thing").Description);
        Assert.Empty(scanner.Find("THING-1"));
        Assert.Single(scanner.Find("OTHER-1"));
        Assert.Single(scanner.FindByKey("customerNumber", "12345"));
    }

    [Fact]
    public void ABrokenUserFile_IsReported_AndTheBuiltInRulesStillApply()
    {
        var scanner = new SecretScanner(
        [
            ("built-in", """
                [[rules]]
                id = "thing"
                regex = '''THING-\d+'''
                """),
            ("yours", "[[rules]]\nid = \"oops\"\nregex = '''unclosed"),
        ]);

        Assert.Single(scanner.Find("THING-1"));
        Assert.Contains(scanner.SkippedRules, s => s.StartsWith("yours", StringComparison.Ordinal));
    }

    [Fact]
    public void TheDefaultUserFile_IsFilledWithStrictRules_ThatAllCompile()
    {
        var scanner = new SecretScanner(SecretRuleSet.DefaultUserRules, "yours");

        Assert.Equal(14, scanner.RuleCount);
        Assert.Empty(scanner.SkippedRules);
        Assert.All(scanner.Rules, r => Assert.StartsWith("strict-", r.Id));
    }

    [Theory]
    [InlineData("X-API-Key: Zr8Lq2Vm5Tx9Nb4Kc7Pw", "strict-generic-api-key")]
    [InlineData("export OPENAI=sk-proj-Ab12Cd34Ef56Gh78Ij90Kl", "strict-prefixed-key")]
    [InlineData("eyJhbGciOiJub25lIn0.eyJzdWIiOiJqb2huLmRvZSJ9.", "strict-jwt")]
    [InlineData("refresh_token=1//0gExampleExampleExampleEx", "strict-token-assignment")]
    [InlineData("https://api.example.com/v1/me?access_token=ExampleExampleExample12", "strict-token-in-url")]
    [InlineData("Authorization: Token Example1234Example5678", "strict-bearer")]
    [InlineData("Cookie: lang=en; sessionid=Ab12Cd34Ef56Gh78Ij90", "strict-session-cookie")]
    [InlineData("-----BEGIN PGP PRIVATE KEY BLOCK-----", "strict-pem-private-key")]
    [InlineData("-----BEGIN CERTIFICATE-----", "strict-pem-certificate")]
    [InlineData("cat ~/.ssh/id_ed25519", "strict-ssh-private-key-file")]
    [InlineData("Server=db.example.com;Database=app;User Id=jane;Password=hunter2;", "strict-connection-string-password")]
    [InlineData("https://hooks.slack.com/services/T000/B000/XXXXXXXXXXXXXXXXXXXXXXXX", "strict-webhook-url")]
    public void TheDefaultUserRules_CatchWhatTheySayTheyDo(string text, string rule)
    {
        var scanner = new SecretScanner(SecretRuleSet.DefaultUserRules, "yours");

        Assert.Contains(scanner.Find(text), m => m.RuleId == rule);
    }

    [Theory]
    [InlineData("the api key is stored in the vault, ask an admin")]
    [InlineData("please refresh the token page")]
    [InlineData("cookie banner text update")]
    [InlineData("SELECT id, title FROM tasks WHERE done = false")]
    public void TheDefaultUserRules_LeaveOrdinaryTextAlone(string text)
    {
        Assert.Empty(new SecretScanner(SecretRuleSet.DefaultUserRules, "yours").Find(text));
    }

    [Fact]
    public void TheUserFile_IsCreatedOnFirstUse_AndPickedUpOnReload()
    {
        var dir = Directory.CreateTempSubdirectory("ravensport-rules-").FullName;
        SecretRuleSet.DirectoryOverride = dir;
        try
        {
            var path = SecretRuleSet.EnsureUserFile();
            File.AppendAllText(path, "\n[[rules]]\nid = \"my-ticket\"\nregex = '''\\bTKT-\\d{4}\\b'''\n");

            var reloaded = SecretRuleSet.Reload();

            Assert.Contains(reloaded.Rules, r => r.Id == "my-ticket" && r.Source == SecretRuleSet.UserSource);
            Assert.Single(reloaded.Find("see TKT-1234"));
        }
        finally
        {
            SecretRuleSet.DirectoryOverride = null;
            SecretRuleSet.Reload();
            Directory.Delete(dir, recursive: true);
        }
    }
}
