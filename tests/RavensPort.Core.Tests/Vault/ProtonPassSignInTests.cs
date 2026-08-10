using System.Security.AccessControl;
using System.Security.Principal;
using RavensPort.Core.Diagnostics;
using RavensPort.Core.Vault;

namespace RavensPort.Core.Tests.Vault;

/// <summary>
/// The in-app Proton Pass sign-in: the sandboxed session and the URL the user is shown.
///
/// The invariant these exist to hold is that the session key never becomes visible. It travels in
/// the child's environment and nowhere else — never an argument, which any process in the session
/// can read, and never a log line.
/// </summary>
public class ProtonPassSignInTests : IDisposable
{
    private const string Key = "SENTINEL-SESSION-KEY";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ravensport-signin-{Guid.NewGuid()}");

    private string SessionDir => Path.Combine(_root, "session");
    private string LogDir => Path.Combine(_root, "logs");

    private ActivityLog Log() => new(LogDir);

    private ProtonPassSession NewSession(bool unlocked = true)
    {
        var session = new ProtonPassSession(Log(), SessionDir);
        if (unlocked) session.Unlock(Key);

        return session;
    }

    // ---- The sandbox ------------------------------------------------------------------------

    [Fact]
    public void BuildEnvironment_IsEmpty_UntilUnlocked()
    {
        Assert.Empty(NewSession(unlocked: false).BuildEnvironment());
    }

    [Fact]
    public void BuildEnvironment_PointsPassCliAtRavensPortsOwnSession()
    {
        var env = NewSession().BuildEnvironment();

        Assert.Equal(SessionDir, env["PROTON_PASS_SESSION_DIR"]);

        // 'env' and not 'fs': the fs provider writes the key to local.key in plaintext, beside the
        // data it encrypts.
        Assert.Equal("env", env["PROTON_PASS_KEY_PROVIDER"]);
        Assert.Equal(Key, env["PROTON_PASS_ENCRYPTION_KEY"]);
    }

    [Fact]
    public void BuildEnvironment_TightensASessionDirectoryItDidNotCreate()
    {
        // The owner-only ACL is applied when the app creates the directory, which means it was
        // never applied to one that was already there — planted ahead of first run, left by a build
        // that predates the ACL, or widened by hand since. The encrypted session lives here, so
        // what matters is what the ACL says now.
        var everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null);

        Directory.CreateDirectory(SessionDir);

        var planted = new DirectoryInfo(SessionDir);
        var opened = planted.GetAccessControl();
        opened.AddAccessRule(new FileSystemAccessRule(everyone, FileSystemRights.FullControl, AccessControlType.Allow));
        planted.SetAccessControl(opened);

        NewSession().BuildEnvironment();

        var after = new DirectoryInfo(SessionDir).GetAccessControl();

        Assert.True(after.AreAccessRulesProtected, "inheritance should be broken");
        Assert.DoesNotContain(
            after.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>(),
            rule => everyone.Equals(rule.IdentityReference));
    }

    [Fact]
    public void Unlock_RejectsAnEmptyKey()
    {
        // The env provider errors on an empty value, in wording that says nothing about the actual
        // problem. Refusing here means the user is told what to do instead.
        Assert.Throws<VaultCliException>(() => new ProtonPassSession(Log(), SessionDir).Unlock("   "));
    }

    [Fact]
    public void GenerateKey_IsNotGuessable()
    {
        var keys = Enumerable.Range(0, 50).Select(_ => ProtonPassSession.GenerateKey()).ToList();

        Assert.Equal(50, keys.Distinct().Count());
        Assert.All(keys, key => Assert.Equal(32, Convert.FromBase64String(key).Length));
    }

    // Hello now has its own files: HelloSealedKeyTests for the envelope, HelloCredentialBindingTests
    // for the link between the gesture and the Credential Manager, HelloKeyStorageTests for the
    // real Windows pieces.

    [Fact]
    public void Wipe_RemovesTheSessionEvenWhenPassCliCannot()
    {
        // A cancelled login leaves pass-cli.db and session.json behind, and pass-cli's own logout
        // refuses that state ("Session is some but is not logged in") — so the next sign-in would
        // be blocked by files nothing would clean up.
        var session = NewSession();
        Directory.CreateDirectory(Path.Combine(SessionDir, ".session"));
        File.WriteAllText(Path.Combine(SessionDir, ".session", "session.json"), "{}");

        Assert.True(session.HasSessionOnDisk);

        session.Wipe();

        Assert.False(session.HasSessionOnDisk);
        Assert.False(Directory.Exists(SessionDir));
    }

    // ---- What the provider runs -------------------------------------------------------------

    [Fact]
    public async Task Provider_RunsEveryCallAgainstRavensPortsOwnSession()
    {
        var fake = new FakeProtonPass();
        var runner = fake.AsRunner();

        await NewProvider(runner, NewSession()).ProbeAsync();

        Assert.NotEmpty(runner.Invocations);
        Assert.All(runner.Invocations, call =>
        {
            Assert.Contains("PROTON_PASS_SESSION_DIR", call.Env);
            Assert.Contains("PROTON_PASS_KEY_PROVIDER", call.Env);
            Assert.Contains("PROTON_PASS_ENCRYPTION_KEY", call.Env);
        });
    }

    [Fact]
    public async Task Provider_NeverPutsTheSessionKeyInAnArgument()
    {
        var fake = new FakeProtonPass();
        var runner = fake.AsRunner();

        await NewProvider(runner, NewSession()).ProbeAsync();

        // A Windows command line is readable by any process in the session. This is the whole
        // reason the key goes in the environment.
        Assert.DoesNotContain(Key, runner.AllArguments);
    }

    [Fact]
    public async Task Provider_PrefersAPersonalAccessTokenOverTheSession()
    {
        var fake = new FakeProtonPass();
        var runner = fake.AsRunner();

        var provider = NewProvider(runner, NewSession());
        provider.PersonalAccessToken = "SENTINEL-PAT";

        await provider.ProbeAsync();

        // A token authenticates on its own. Handing it a session directory as well would be two
        // answers to one question.
        Assert.All(runner.Invocations, call =>
        {
            Assert.Contains("PROTON_PASS_PERSONAL_ACCESS_TOKEN", call.Env);
            Assert.DoesNotContain("PROTON_PASS_SESSION_DIR", call.Env);
        });
    }

    [Fact]
    public async Task Provider_SaysNotSignedIn_WithoutLaunchingAnything_WhenLocked()
    {
        var runner = new FakeCliRunner();

        var status = await NewProvider(runner, NewSession(unlocked: false)).ProbeAsync();

        Assert.Equal(VaultAvailability.NotSignedIn, status.Availability);

        // Not one process launched: the answer was already known, and an unscripted call against
        // FakeCliRunner throws rather than passing quietly.
        Assert.Empty(runner.Invocations);
    }

    // ---- The URL the user is shown ----------------------------------------------------------

    [Theory]
    [InlineData(
        "https://account.proton.me/desktop/login?app=pass#payload=0%3AC18ME3HH%3AhVwT%3Acli-pass",
        "https://account.proton.me/desktop/login?app=pass#payload=0%3AC18ME3HH%3AhVwT%3Acli-pass")]
    [InlineData("Visit https://account.proton.me/login now.", "https://account.proton.me/login")]
    [InlineData("Please open the following URL in your browser to complete authentication:", null)]
    [InlineData("Waiting for authentication to complete...", null)]
    [InlineData("", null)]
    [InlineData("See https://protonpass.github.io/pass-cli/ for docs", null)]
    [InlineData("https://evil.example.com/account.proton.me", null)]
    public void ExtractUrl_TakesOnlyAProtonSignInLink(string line, string? expected)
    {
        Assert.Equal(expected, ProtonPassAuthenticator.ExtractUrl(line));
    }

    [Fact]
    public void ExtractUrl_FindsTheUrlWhereverInTheOutputItAppears()
    {
        // Matched by shape, not by the prose around it — that wording is a future pass-cli release's
        // to change, and the real output puts the URL on its own line two lines further down.
        var output = """

            Please open the following URL in your browser to complete authentication:

            https://account.proton.me/desktop/login?app=pass#payload=abc

            Waiting for authentication to complete...
            """;

        var found = output.Split('\n')
            .Select(ProtonPassAuthenticator.ExtractUrl)
            .FirstOrDefault(url => url is not null);

        Assert.Equal("https://account.proton.me/desktop/login?app=pass#payload=abc", found);
    }

    // ---- Losing the key ----------------------------------------------------------------------

    [Fact]
    public async Task DiscardLocalSession_ClearsTheSessionWithoutRunningTheCli()
    {
        var session = NewSession();
        Directory.CreateDirectory(Path.Combine(SessionDir, ".session"));
        File.WriteAllText(Path.Combine(SessionDir, ".session", "session.json"), "{}");

        var runner = new FakeCliRunner();
        await NewAuthenticator(runner, session).DiscardLocalSessionAsync();

        Assert.False(session.HasSessionOnDisk);
        Assert.False(session.HasKey);

        // Nothing was launched: without the key there is no session to log out of, and an
        // unscripted call against FakeCliRunner throws rather than passing quietly.
        Assert.Empty(runner.Invocations);
    }

    [Fact]
    public async Task SignOut_RunsNoCliCommand_WhenThereIsNoKey()
    {
        // The property that matters. Without a key, BuildEnvironment is empty — so a `logout` would
        // run with no PROTON_PASS_SESSION_DIR at all and pass-cli would fall back to the user's own
        // default session and sign *that* out. Signing out of RavensPort must never sign the user
        // out of their terminal.
        var session = NewSession(unlocked: false);
        var runner = new FakeCliRunner();

        await NewAuthenticator(runner, session).SignOutAsync();

        Assert.Empty(runner.Invocations);
    }

    // ---- Nothing is installed for the user ---------------------------------------------------

    /// <summary>
    /// RavensPort used to fetch and unpack pass-cli itself, and the setup page offered to open
    /// Proton's download page. Both are gone: the app installs no software and links to none, so
    /// the only thing left to get right is telling the user what to run. The winget command has to
    /// survive here, and a URL must not creep back in.
    /// </summary>
    [Fact]
    public void AMissingCli_IsReportedWithTheCommandThatInstallsIt()
    {
        Assert.Contains("winget install Proton.PassCLI", ProtonPassAuthenticator.CliMissing);
        Assert.DoesNotContain(
            "http", ProtonPassAuthenticator.CliMissing, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Helpers -----------------------------------------------------------------------------

    /// <summary>
    /// An authenticator wired to fakes. Both providers are pointed at paths that do not exist, so
    /// nothing here can reach a real password manager on the machine running the tests.
    /// </summary>
    private ProtonPassAuthenticator NewAuthenticator(ICliRunner runner, ProtonPassSession session)
    {
        var log = Log();
        var missing = Path.Combine(_root, "not-installed.exe");

        var gate = new VaultGateService(
            new OnePasswordVaultProvider(runner, log, missing),
            new ProtonPassVaultProvider(runner, log, missing, session),
            log);

        return new ProtonPassAuthenticator(runner, session, new HelloKeyProtector(log), gate, log);
    }

    private ProtonPassVaultProvider NewProvider(ICliRunner runner, ProtonPassSession session)
    {
        var stub = Path.Combine(_root, "pass-cli.exe");
        Directory.CreateDirectory(_root);
        if (!File.Exists(stub)) File.WriteAllText(stub, "");

        return new ProtonPassVaultProvider(runner, Log(), stub, session);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // A temp directory. Not worth failing a test run over.
        }

        GC.SuppressFinalize(this);
    }
}
