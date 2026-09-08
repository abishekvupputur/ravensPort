using Moq;
using RavensPort.Core.Diagnostics;
using RavensPort.Core.Vault;

namespace RavensPort.Core.Tests.Vault;

/// <summary>
/// Signing in to 1Password with a service-account token instead of the desktop app.
///
/// The mode exists because desktop app integration needs the 1Password GUI running and unlocked,
/// and reaches it through a library whose lifetime this app cannot control — see
/// <see cref="VaultAuthorization.IsUnreachable"/>. A token needs none of that: the SDK routes it to
/// its own embedded core and talks to 1Password over the network, which is the only way to run this
/// app on a machine nobody is sitting at.
///
/// The credential is a bearer token for every vault it was granted and does not expire on its own,
/// so the rule it is built around is that it never leaves memory. Most of what follows is that rule
/// stated as assertions.
/// </summary>
[Collection(NativeCliRunnerCollection.Name)]
public class OnePasswordServiceAccountTests : IDisposable
{
    private const string Token = "ops_SENTINEL-SERVICE-ACCOUNT-TOKEN";

    private readonly string _logPath =
        Path.Combine(Path.GetTempPath(), $"ravensport-sa-{Guid.NewGuid()}");

    // ---- The session ----------------------------------------------------------------------------

    [Fact]
    public void AnEmptyTokenIsRefusedBeforeItReachesTheVault()
    {
        var session = new OnePasswordSession();

        Assert.Throws<VaultCliException>(() => session.Unlock(""));
        Assert.Throws<VaultCliException>(() => session.Unlock("   "));
        Assert.Throws<VaultCliException>(() => session.Unlock(null));
        Assert.False(session.HasToken);
    }

    [Fact]
    public void SomethingThatIsNotATokenIsRefusedWithAnAnswerableMessage()
    {
        // The mistake this catches is pasting the account name, or half a copy. Saying "that is not
        // a token, they start with ops_" is something a user can act on; letting it through produces
        // a network round trip and a refusal from 1Password that explains nothing.
        var session = new OnePasswordSession();

        var error = Assert.Throws<VaultCliException>(() => session.Unlock("Our Family"));

        Assert.Contains("ops_", error.Message, StringComparison.Ordinal);
        Assert.False(session.HasToken);
    }

    [Fact]
    public void APastedTokenIsTrimmed()
    {
        // Copy buttons and password managers add whitespace. A trailing newline is not a reason to
        // tell someone their credential is wrong.
        var session = new OnePasswordSession();

        session.Unlock($"  {Token}\r\n");

        Assert.Equal(Token, session.BuildEnvironment()["OP_SERVICE_ACCOUNT_TOKEN"]);
    }

    [Fact]
    public void TheTokenLeavesTheSessionOnlyThroughTheEnvironment()
    {
        // The invariant the whole mode rests on. BuildEnvironment puts the token in a child
        // process's environment block; nothing else may hand it out, because a Windows command line
        // is readable by any process in the session and a log file outlives the run.
        var session = new OnePasswordSession();
        session.Unlock(Token);

        var handedOut = typeof(OnePasswordSession)
            .GetProperties()
            .Where(p => p.PropertyType == typeof(string))
            .ToList();

        Assert.Empty(handedOut);
        Assert.Equal(Token, session.BuildEnvironment()["OP_SERVICE_ACCOUNT_TOKEN"]);
    }

    [Fact]
    public void ClearingForgetsTheTokenAndOffersNothingToTheEnvironment()
    {
        var session = new OnePasswordSession();
        session.Unlock(Token);

        session.Clear();

        Assert.False(session.HasToken);
        Assert.Empty(session.BuildEnvironment());
    }

    [Fact]
    public void TheSessionHasNoWayToPersistAnything()
    {
        // Structural rather than behavioural, on purpose. ProtonPassSession legitimately writes a
        // session directory; this one must never grow the equivalent, and the cheapest guard
        // against someone adding it later is a test that says so.
        var members = typeof(OnePasswordSession)
            .GetMembers()
            .Select(m => m.Name)
            .ToList();

        Assert.DoesNotContain(members, name =>
            name.Contains("Save", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Path", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Directory", StringComparison.OrdinalIgnoreCase)
            || name.Contains("File", StringComparison.OrdinalIgnoreCase));
    }

    // ---- Choosing a transport -------------------------------------------------------------------

    [Fact]
    public async Task WithATokenAndTheCliInstalledTheWorkGoesToARealProcess()
    {
        // Both routes work headless, so the reason to prefer op.exe is isolation: the token lives in
        // a child process that exits, rather than being handed to a library mapped into this one for
        // the rest of the run.
        var native = new FakeCliRunner();
        var process = new FakeCliRunner();
        var session = new OnePasswordSession();
        session.Unlock(Token);

        var provider = NewProvider(native, process, session, cliExe: @"C:\op\op.exe");
        await provider.ProbeAsync();

        Assert.NotEmpty(process.Invocations);
        Assert.Empty(native.Invocations);
        Assert.All(process.Invocations, i => Assert.Equal(@"C:\op\op.exe", i.ExePath));
    }

    [Fact]
    public async Task WithATokenAndNoCliTheWorkStaysInProcess()
    {
        // The fallback matters as much as the preference: a machine chosen for this mode is quite
        // likely to have no 1Password software installed at all, and requiring a CLI download would
        // defeat a mode whose whole selling point is needing nothing local.
        var native = new FakeCliRunner();
        var process = new FakeCliRunner();
        var session = new OnePasswordSession();
        session.Unlock(Token);

        var provider = NewProvider(native, process, session, cliExe: null);
        await provider.ProbeAsync();

        Assert.NotEmpty(native.Invocations);
        Assert.Empty(process.Invocations);
    }

    [Fact]
    public async Task ACliThatCannotBeTrustedFallsBackToTheSdkInsteadOfFailing()
    {
        // The CLI is a preference, not a requirement. A user's install refused it outright — WinGet
        // ships op.exe as a symlink, following it failed inside RavensPort's process while working
        // elsewhere, and the trust policy declined a file it could not verify. Failing the whole
        // connection over that was wrong twice: the token needs no CLI at all, and the machines
        // where verification is awkward are exactly the ones that should quietly use the SDK.
        var native = new FakeCliRunner();
        var process = new FakeCliRunner();
        var session = new OnePasswordSession();
        session.Unlock(Token);

        var provider = new OnePasswordVaultProvider(
            Scripted(native), new ActivityLog(_logPath), "native", session, Scripted(process),
            () => @"C:\op\op.exe",
            new RefusingTrustPolicy());

        await provider.ProbeAsync();

        Assert.NotEmpty(native.Invocations);
        Assert.Empty(process.Invocations);
    }

    [Fact]
    public async Task RefusingTheCliIsExplainedRatherThanSilent()
    {
        // A quieter transport is still a changed one. If the CLI was skipped, the log has to say so
        // and why, or a user comparing two machines has no way to tell why one launches op.exe.
        var log = new ActivityLog(_logPath);
        var session = new OnePasswordSession();
        session.Unlock(Token);

        var provider = new OnePasswordVaultProvider(
            Scripted(new FakeCliRunner()), log, "native", session, Scripted(new FakeCliRunner()),
            () => @"C:\op\op.exe",
            new RefusingTrustPolicy());

        await provider.ProbeAsync();

        var lines = log.GetRecent(200);
        Assert.Contains(lines, l => l.Contains("not using the CLI"));
        Assert.Contains(lines, l => l.Contains("needs no CLI"));
    }

    [Fact]
    public async Task WithNoTokenTheCliIsNeverLaunchedEvenIfInstalled()
    {
        // Desktop app integration can only go through the in-process SDK, and a stray op.exe on the
        // machine must not quietly change how an existing install authenticates.
        var native = new FakeCliRunner();
        var process = new FakeCliRunner();

        var provider = NewProvider(native, process, new OnePasswordSession(), cliExe: @"C:\op\op.exe");
        await provider.ProbeAsync();

        Assert.NotEmpty(native.Invocations);
        Assert.Empty(process.Invocations);
    }

    // ---- Choosing a vault -----------------------------------------------------------------------

    [Fact]
    public async Task AServiceAccountIsOfferedEveryVaultItCanReach()
    {
        // The name filter exists to protect a human's other vaults: through the desktop app this
        // app sees everything the user sees, Private included, and a picker listing all of it would
        // be inviting someone to point a credential store at their personal vault.
        //
        // A service account has already been scoped by hand. It can see exactly what it was granted
        // and cannot see Private at all, so the visible set *is* the deliberate choice the naming
        // rule stands in for. Applying it again filtered an already-filtered list against a rule the
        // grant never had to follow — a user who granted one vault called "Automation" was offered
        // nothing to pick, and could not connect at all.
        var fake = new FakeOnePassword { VaultExists = false };
        fake.AddVault("Automation");
        fake.AddVault("Shared Ops");

        var session = new OnePasswordSession();
        session.Unlock(Token);

        var status = await NewProviderOver(fake, session).ProbeAsync();

        Assert.Contains("Automation", status.AdoptableVaults!);
        Assert.Contains("Shared Ops", status.AdoptableVaults!);
    }

    [Fact]
    public async Task SignedInAsAPersonTheOfferIsStillLimitedToRavensPortVaults()
    {
        // The other half, and the one that must not regress: through the desktop app the app can
        // see a human's whole account, so the naming rule is what keeps the offer to vaults made
        // for this purpose rather than their personal one.
        var fake = new FakeOnePassword { VaultExists = false };
        fake.AddVault("Automation");
        fake.AddVault("Personal");

        var status = await NewProviderOver(fake, session: null).ProbeAsync();

        Assert.DoesNotContain("Automation", status.AdoptableVaults!);
        Assert.DoesNotContain("Personal", status.AdoptableVaults!);
    }

    [Fact]
    public async Task AVaultHoldingSomebodyElsesItemsIsNeverOffered()
    {
        // The check that actually protects data, and the one that does not relax for a service
        // account. Being granted a vault is not the same as that vault being free to take over —
        // adopting one full of unrelated items would be refused anyway, so offering it would be
        // offering something that cannot be accepted.
        var fake = new FakeOnePassword { VaultExists = false };
        var occupied = fake.AddVault("Automation");
        fake.AddItem(occupied, "Someone's bank login");

        var session = new OnePasswordSession();
        session.Unlock(Token);

        var status = await NewProviderOver(fake, session).ProbeAsync();

        Assert.DoesNotContain("Automation", status.AdoptableVaults!);
    }

    // ---- The credential does not leak -----------------------------------------------------------

    [Fact]
    public async Task TheTokenIsPassedInTheEnvironmentAndNeverInArguments()
    {
        // A Windows process command line is readable by any process in the same session — no API
        // call, no permission, just an enumeration. An argument would make this strictly worse than
        // not having the feature.
        var native = new FakeCliRunner();
        var process = new FakeCliRunner();
        var session = new OnePasswordSession();
        session.Unlock(Token);

        await NewProvider(native, process, session, cliExe: @"C:\op\op.exe").ProbeAsync();

        Assert.All(process.Invocations, i => Assert.Contains("OP_SERVICE_ACCOUNT_TOKEN", i.Env));
        Assert.DoesNotContain(process.AllArguments, arg => arg.Contains("SENTINEL"));
    }

    [Fact]
    public async Task TheTokenNeverReachesTheActivityLog()
    {
        // The log is written to disk, kept across runs, and is the first thing a user attaches to a
        // bug report.
        var log = new ActivityLog(_logPath);
        var session = new OnePasswordSession();
        session.Unlock(Token);

        var provider = new OnePasswordVaultProvider(
            Scripted(new FakeCliRunner()), log, "native", session,
            Scripted(new FakeCliRunner()), () => @"C:\op\op.exe", new AllowingTrustPolicy());

        await provider.ProbeAsync();

        Assert.DoesNotContain(log.GetRecent(500), line => line.Contains("SENTINEL"));
    }

    // ---- The in-process path --------------------------------------------------------------------

    [Fact]
    public async Task AServiceAccountConnectsWithoutTheIntegrationChannel()
    {
        // The guard that keeps RavensPort from mapping 1Password's library while the app is closed
        // must not apply here. A service account opens no pipe and loads none of their code, so
        // applying it would block the one mode that is immune to the fault it exists for — and block
        // it on exactly the machines this mode is for, where 1Password is not installed at all.
        var client = new Mock<IOnePasswordNativeClient>();
        var session = new OnePasswordSession();
        session.Unlock(Token);

        var runner = new NativeCliRunner(
            client: client.Object,
            integrationChannelPresent: () => false,
            session: session);

        client.Setup(c => c.ListVaults()).Returns(new System.Text.Json.Nodes.JsonArray());
        NativeCliRunner.ResetInitialization();

        var result = await runner.RunAsync("native", ["vault", "list"]);

        Assert.Equal(0, result.ExitCode);
        client.Verify(c => c.InitializeServiceAccount(Token), Times.Once);
        client.Verify(c => c.Initialize(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task AServiceAccountDoesNotNeedAnAccountName()
    {
        // The account name is read off the desktop app's sidebar, and this mode is for machines that
        // do not have the desktop app. Demanding one would make the mode impossible to use where it
        // is most needed — and the SDK refuses a name and a token together anyway.
        var previous = Environment.GetEnvironmentVariable("OP_ACCOUNT");
        Environment.SetEnvironmentVariable("OP_ACCOUNT", null);
        NativeCliRunner.ResetInitialization();

        try
        {
            var client = new Mock<IOnePasswordNativeClient>();
            client.Setup(c => c.ListVaults()).Returns(new System.Text.Json.Nodes.JsonArray());

            var session = new OnePasswordSession();
            session.Unlock(Token);

            var runner = new NativeCliRunner(
                client: client.Object, integrationChannelPresent: () => true, session: session);

            var result = await runner.RunAsync("native", ["vault", "list"]);

            Assert.Equal(0, result.ExitCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OP_ACCOUNT", previous);
            NativeCliRunner.ResetInitialization();
        }
    }

    // ---- Helpers ---------------------------------------------------------------------------------

    /// <param name="cliExe">
    /// Where op.exe appears to be. The trust policy is allowed by default here so these tests
    /// exercise the routing rather than Authenticode against a path that does not exist.
    /// </param>
    private OnePasswordVaultProvider NewProvider(
        FakeCliRunner native, FakeCliRunner process, OnePasswordSession session, string? cliExe) =>
        new(Scripted(native), new ActivityLog(_logPath), "native", session, Scripted(process),
            () => cliExe, new AllowingTrustPolicy());

    /// <summary>
    /// Enough of a 1Password to get through a probe. The routing tests care only about which runner
    /// was asked, so an account with no vaults is the shortest path that still exercises the whole
    /// call sequence.
    /// </summary>
    private static FakeCliRunner Scripted(FakeCliRunner runner) => runner
        .Respond(["--version"], "2.34.0")
        .Respond(["vault", "list"], "[]");

    /// <summary>
    /// A provider over a populated fake account, for the questions about which vaults get offered.
    /// A null session is the desktop-app path.
    /// </summary>
    private OnePasswordVaultProvider NewProviderOver(FakeOnePassword fake, OnePasswordSession? session) =>
        new(fake.AsRunner(), new ActivityLog(_logPath), "native", session);

    /// <summary>A trust policy that says no, standing in for a CLI that cannot be verified here.</summary>
    private sealed class RefusingTrustPolicy : IExecutableTrustPolicy
    {
        public TrustDecision Decide(string resolvedPath) =>
            new(false, "is a link that could not be followed just now");
    }

    /// <summary>
    /// A trust policy that says yes, so the routing tests are about routing. The real policy would
    /// refuse these paths for the honest reason that they are invented.
    /// </summary>
    private sealed class AllowingTrustPolicy : IExecutableTrustPolicy
    {
        public TrustDecision Decide(string resolvedPath) => new(true, "allowed by the test policy");
    }

    public void Dispose()
    {
        try { Directory.Delete(_logPath, recursive: true); } catch { /* best effort */ }

        // Nothing here has a finalizer, but the pattern is what CA1816 asks for and what a
        // derived test fixture would need if one ever did.
        GC.SuppressFinalize(this);
    }
}
