using RavensPort.Core.Diagnostics;
using RavensPort.Core.Vault;

namespace RavensPort.Core.Tests.Vault;

/// <summary>
/// The headless Proton Pass login. What matters is where the token goes — the child's environment
/// and nowhere else — which session it opens, and that a server running longer than a PAT session
/// lasts logs in again rather than going quiet.
/// </summary>
public class ProtonPassPatSessionTests : IDisposable
{
    private const string Token = "pst_SENTINEL-PAT::KEY";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ravensport-pat-{Guid.NewGuid()}");
    private readonly string _passStub;
    private readonly ManualClock _clock = new();

    public ProtonPassPatSessionTests()
    {
        Directory.CreateDirectory(_root);
        _passStub = Path.Combine(_root, "pass-cli");
        File.WriteAllText(_passStub, "");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ops_this-is-a-1password-token")]
    public void AnythingThatIsNotAPersonalAccessTokenIsRefused(string token)
    {
        var (pat, _, _) = NewSession(new FakeCliRunner());

        Assert.Throws<VaultCliException>(() => pat.SetToken(token));
        Assert.False(pat.HasToken);
    }

    [Fact]
    public async Task TheTokenGoesInTheEnvironmentAndNeverInTheArguments()
    {
        var runner = new FakeCliRunner().Respond(["login"]);
        var (pat, session, _) = NewSession(runner);
        pat.SetToken($"  {Token}\n");

        await pat.LoginAsync();

        var login = Assert.Single(runner.CallsMatching("login"));
        Assert.Contains("PROTON_PASS_PERSONAL_ACCESS_TOKEN", login.Env);
        Assert.Contains("PROTON_PASS_SESSION_DIR", login.Env);
        Assert.Contains("PROTON_PASS_ENCRYPTION_KEY", login.Env);
        Assert.DoesNotContain(runner.AllArguments, a => a.Contains("SENTINEL", StringComparison.Ordinal));

        // Its own session, not the desktop app's.
        Assert.NotEqual(ProtonPassSession.DefaultDirectory, session.SessionDirectory);
        Assert.True(Directory.Exists(session.SessionDirectory));
    }

    [Fact]
    public async Task ASessionIsReusedUntilItIsNearlyTwoHoursOld()
    {
        var runner = new FakeCliRunner().Respond(["login"]);
        var (pat, _, _) = NewSession(runner);
        pat.SetToken(Token);

        await pat.EnsureLoggedInAsync();
        _clock.Advance(TimeSpan.FromMinutes(90));
        await pat.EnsureLoggedInAsync();

        Assert.Single(runner.CallsMatching("login"));

        _clock.Advance(TimeSpan.FromMinutes(15));
        await pat.EnsureLoggedInAsync();

        Assert.Equal(2, runner.CallsMatching("login").Count());
    }

    [Fact]
    public async Task ARefusedLoginSaysWhyAndIsTriedAgainNextTime()
    {
        var runner = new FakeCliRunner().Respond(["login"], exitCode: 1, stderr: "token revoked");
        var (pat, _, _) = NewSession(runner);
        pat.SetToken(Token);

        var ex = await Assert.ThrowsAsync<VaultCliException>(() => pat.EnsureLoggedInAsync());
        Assert.Contains("token revoked", ex.Message);

        await Assert.ThrowsAsync<VaultCliException>(() => pat.EnsureLoggedInAsync());
        Assert.Equal(2, runner.CallsMatching("login").Count());
    }

    [Fact]
    public async Task EndingLogsOutAndRemovesTheSession()
    {
        var runner = new FakeCliRunner().Respond(["login"]).Respond(["logout"]);
        var (pat, session, _) = NewSession(runner);
        pat.SetToken(Token);
        await pat.LoginAsync();

        await pat.EndAsync();

        Assert.Single(runner.CallsMatching("logout"));
        Assert.DoesNotContain("PROTON_PASS_PERSONAL_ACCESS_TOKEN", runner.CallsMatching("logout").Single().Env);
        Assert.False(Directory.Exists(session.SessionDirectory));
        Assert.False(pat.HasToken);
    }

    [Fact]
    public async Task EndingSurvivesAFailedLogout()
    {
        var runner = new FakeCliRunner().Respond(["login"]); // logout unscripted, so it throws
        var (pat, session, _) = NewSession(runner);
        pat.SetToken(Token);
        await pat.LoginAsync();

        await pat.EndAsync();

        Assert.False(Directory.Exists(session.SessionDirectory));
    }

    private (ProtonPassPatSession Pat, ProtonPassSession Session, ActivityLog Log) NewSession(FakeCliRunner runner)
    {
        var log = new ActivityLog(Path.Combine(_root, "logs"));
        var session = new ProtonPassSession(log, Path.Combine(_root, "pass-pat"));

        return (new ProtonPassPatSession(session, runner, log, _passStub, _clock), session, log);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 10, 9, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
