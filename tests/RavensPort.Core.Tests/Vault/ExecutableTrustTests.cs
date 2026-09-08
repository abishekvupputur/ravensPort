using RavensPort.Core.Diagnostics;
using RavensPort.Core.Vault;

namespace RavensPort.Core.Tests.Vault;

/// <summary>
/// The signature gate in front of the password-manager CLIs.
///
/// The attack being tested is the cheap one: PATH on a normal machine contains directories an
/// unprivileged process can write to, so dropping a file called op.exe into one of them is enough
/// to be found first — and whatever is found gets handed the vault session key in its environment.
///
/// A real signed binary is needed to test the accepting half, and the only one guaranteed to exist
/// wherever these tests run is the host that is running them. dotnet.exe carries an embedded
/// Authenticode signature, which makes it both a valid signature to accept and — once copied to
/// the name op.exe — exactly the case that must still be refused: properly signed, by the wrong
/// publisher.
/// </summary>
public class ExecutableTrustTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ravensport-trust-{Guid.NewGuid()}");

    private static string SignedBinary => Environment.ProcessPath
        ?? Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "dotnet.exe");

    public ExecutableTrustTests() => Directory.CreateDirectory(_root);

    private string Unsigned(string name)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, "not a real executable");

        return path;
    }

    private string SignedAs(string name)
    {
        var path = Path.Combine(_root, name);
        File.Copy(SignedBinary, path, overwrite: true);

        return path;
    }

    // ---- The verification itself -------------------------------------------------------------

    [Fact]
    public void AValidlySignedBinaryIsReadAsSignedAndNamesItsPublisher()
    {
        var signature = ExecutableSignature.Read(SignedBinary);

        Assert.True(signature.IsTrusted, signature.Detail);
        Assert.False(string.IsNullOrWhiteSpace(signature.Publisher));
    }

    [Fact]
    public void AFileWithNoSignatureIsNotTrusted()
    {
        var signature = ExecutableSignature.Read(Unsigned("whatever.exe"));

        Assert.False(signature.IsTrusted);
        Assert.Contains("not signed", signature.Detail);
        Assert.Null(signature.Publisher);
    }

    [Fact]
    public void ASymlinkedBinaryIsVerifiedThroughToItsTarget()
    {
        // WinGet installs op.exe as a symlink in its Links directory, which is what most 1Password
        // CLI installs actually put on PATH. WinVerifyTrust follows the link and the certificate
        // reader does not, so reading the two off the same path reported "validly signed" with no
        // publisher — and the policy refused the genuine binary. Users would have been locked out
        // of their own vault by a hardening measure.
        var link = Path.Combine(_root, "linked-op.exe");

        try
        {
            File.CreateSymbolicLink(link, SignedBinary);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Creating one needs Developer Mode or elevation. Nothing to assert without it.
            return;
        }

        var signature = ExecutableSignature.Read(link);

        Assert.True(signature.IsTrusted, signature.Detail);
        Assert.False(string.IsNullOrWhiteSpace(signature.Publisher));
    }

    [Fact]
    public void AMissingFileIsNotTrustedRatherThanAnException()
    {
        // The probe checks File.Exists, but a binary can be uninstalled between the check and the
        // launch. Throwing out of a trust check would turn that race into a crash.
        var signature = ExecutableSignature.Read(Path.Combine(_root, "gone.exe"));

        Assert.False(signature.IsTrusted);
    }

    // ---- The policy --------------------------------------------------------------------------

    [Fact]
    public void AnUnsignedOpExeIsRefused()
    {
        var decision = AuthenticodeTrustPolicy.Default.Decide(Unsigned("op.exe"));

        Assert.False(decision.Allowed);
        Assert.Contains("not signed", decision.Summary);
    }

    [Fact]
    public void ASignedBinaryUnderTheWrongNameIsRefused()
    {
        // The interesting half. Requiring "a valid signature" alone would accept this: it is
        // properly signed, just not by the people who make op.exe. Anyone can buy a code-signing
        // certificate, so the publisher is the part that has to match.
        var decision = AuthenticodeTrustPolicy.Default.Decide(SignedAs("op.exe"));

        Assert.False(decision.Allowed);
        Assert.Contains("is signed by", decision.Summary);
    }

    [Fact]
    public void ABinaryThatIsNotAPasswordManagerCliIsLeftAlone()
    {
        // The probe only ever looks for op.exe and pass-cli.exe, so a rule written for two specific
        // vendors' binaries has no business being applied to anything else.
        var decision = AuthenticodeTrustPolicy.Default.Decide(Unsigned("helper.exe"));

        Assert.True(decision.Allowed);
    }

    [Fact]
    public void AnEnvironmentOverrideIsHonouredUnsigned()
    {
        // pass-cli is GPL and building it yourself is a supported thing to do; the result is
        // unsigned. Someone who can set your environment variables can already run code as you.
        var built = Unsigned("pass-cli.exe");
        Environment.SetEnvironmentVariable(VaultProbe.ProtonPassPathVariable, built);

        var decision = AuthenticodeTrustPolicy.Default.Decide(built);

        Assert.True(decision.Allowed);
        Assert.Contains("environment override", decision.Summary);
    }

    [Fact]
    public void AnOverridePointingSomewhereElseDoesNotExcuseADifferentBinary()
    {
        // The override says "run this file", not "stop checking". Excusing every pass-cli.exe
        // because one was named would hand the planted-on-PATH case a free pass.
        var named = Unsigned("chosen-pass-cli.exe");
        var planted = Unsigned("pass-cli.exe");
        Environment.SetEnvironmentVariable(VaultProbe.ProtonPassPathVariable, named);

        Assert.False(AuthenticodeTrustPolicy.Default.Decide(planted).Allowed);
    }

    // ---- The gate in front of the launch -----------------------------------------------------

    [Fact]
    public async Task CliRunnerWillNotLaunchAnUntrustedBinary()
    {
        var runner = new CliRunner(new ActivityLog(Path.Combine(_root, "logs")));

        var exception = await Assert.ThrowsAsync<VaultCliException>(
            () => runner.RunAsync(Unsigned("op.exe"), ["--version"]));

        Assert.Contains("will not run it", exception.Message);
    }

    [Fact]
    public async Task CliRunnerRecordsTheRefusalEveryTime()
    {
        // The refusal is what the setup page shows. One that logged only the first time would
        // leave every later attempt looking like an unexplained failure.
        var activityLog = new ActivityLog(Path.Combine(_root, "logs"));
        var runner = new CliRunner(activityLog);
        var planted = Unsigned("op.exe");

        for (var attempt = 0; attempt < 2; attempt++)
        {
            await Assert.ThrowsAsync<VaultCliException>(() => runner.RunAsync(planted, ["--version"]));
        }

        Assert.Equal(2, activityLog.GetRecent(100).Count(line => line.Contains("refused to launch")));
    }

    [Fact]
    public async Task ReplacingTheBinaryReopensTheQuestion()
    {
        // Trust decisions are cached, so an update has to invalidate the entry. op.exe in
        // particular is signed with a certificate valid for days, meaning every release carries a
        // new one — a verdict remembered across an upgrade would be a verdict about a file that is
        // no longer there.
        var runner = new CliRunner(new ActivityLog(Path.Combine(_root, "logs")));
        var path = Unsigned("op.exe");

        var first = await Assert.ThrowsAsync<VaultCliException>(() => runner.RunAsync(path, ["--version"]));
        Assert.Contains("not signed", first.Message);

        File.Copy(SignedBinary, path, overwrite: true);

        // Still refused — wrong publisher — but for a different reason, which is only possible if
        // the file was looked at again rather than answered from the cache.
        var second = await Assert.ThrowsAsync<VaultCliException>(() => runner.RunAsync(path, ["--version"]));
        Assert.Contains("is signed by", second.Message);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(VaultProbe.ProtonPassPathVariable, null);
        Environment.SetEnvironmentVariable(VaultProbe.OnePasswordPathVariable, null);

        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }

        // Nothing here has a finalizer, but the pattern is what CA1816 asks for and what a
        // derived test fixture would need if one ever did.
        GC.SuppressFinalize(this);
    }
}
