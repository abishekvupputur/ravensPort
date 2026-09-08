using RavensPort.Core.Diagnostics;
using RavensPort.Core.Vault;

namespace RavensPort.Core.Tests.Vault;

/// <summary>
/// Keeping a 1Password service-account token between runs.
///
/// This is the one place the "never stored" rule bends, and only because the user asked for it. What
/// makes it acceptable is that the stored form is ciphertext whose key exists nowhere: it is derived
/// from a Windows Hello signature each time, so the saved bytes open only to a gesture on this PC.
/// The tests below are that claim, checked — the token must not be recoverable from the store alone,
/// and it must be removable, because service accounts rotate and a revoked token that cannot be
/// cleared would fail every startup with nothing to do about it.
///
/// The TPM is substituted (see <see cref="FakeHelloSigner"/>); the encryption, blob layout and store
/// are the real implementations.
/// </summary>
public class OnePasswordTokenStorageTests : IDisposable
{
    private const string Token = "ops_SENTINEL-SERVICE-ACCOUNT-TOKEN-VALUE";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ravensport-optoken-{Guid.NewGuid()}");
    private readonly FakeHelloSigner _hello = new();
    private readonly InMemorySecretStore _store = new();
    private readonly HelloKeyProtector _protector;

    public OnePasswordTokenStorageTests()
    {
        _protector = new HelloKeyProtector(new ActivityLog(Path.Combine(_root, "logs")), _hello, _store);
    }

    [Fact]
    public async Task ASavedTokenComesBackExactly()
    {
        await _protector.ProtectOnePasswordTokenAsync(Token);

        Assert.Equal(Token, await _protector.UnprotectOnePasswordTokenAsync());
    }

    [Fact]
    public async Task NothingSavedMeansNothingToReturn()
    {
        // Null rather than a throw: "you have not saved one" is an ordinary state the setup page
        // asks about on every rebuild, not a failure.
        Assert.False(_protector.HasProtectedOnePasswordToken());
        Assert.Null(await _protector.UnprotectOnePasswordTokenAsync());
    }

    [Fact]
    public async Task TheStoredBytesDoNotContainTheToken()
    {
        // The whole point. Credential Manager hands its bytes to any process running as this user,
        // silently and with no prompt — so if the token were recoverable from them, the Hello
        // gesture would be decoration.
        await _protector.ProtectOnePasswordTokenAsync(Token);

        var name = Assert.Single(_store.Names);
        var stored = _store.Peek(name)!;

        BlobAssert.DoesNotContainSequence(System.Text.Encoding.UTF8.GetBytes(Token), stored);
    }

    [Fact]
    public async Task WithoutTheHelloCredentialTheSavedTokenIsUnopenable()
    {
        // A reset Hello enrolment, or another machine. The blob survives; what opens it does not.
        await _protector.ProtectOnePasswordTokenAsync(Token);

        _hello.ResetEnrolment();

        await Assert.ThrowsAsync<VaultCliException>(() => _protector.UnprotectOnePasswordTokenAsync());
    }

    [Fact]
    public async Task ForgettingRemovesItForGood()
    {
        // Service accounts rotate. A saved token that has been revoked fails every startup, so
        // clearing it has to be possible from inside the app.
        await _protector.ProtectOnePasswordTokenAsync(Token);
        Assert.True(_protector.HasProtectedOnePasswordToken());

        await _protector.ForgetOnePasswordTokenAsync();

        Assert.False(_protector.HasProtectedOnePasswordToken());
        Assert.Null(await _protector.UnprotectOnePasswordTokenAsync());
    }

    [Fact]
    public async Task ForgettingWhenNothingIsSavedIsHarmless()
    {
        // The button is offered from a card whose state may be a moment stale, and a user pressing
        // it twice should not see an error.
        await _protector.ForgetOnePasswordTokenAsync();

        Assert.False(_protector.HasProtectedOnePasswordToken());
    }

    [Fact]
    public async Task TheTokenAndTheProtonSessionKeyAreSeparateCredentials()
    {
        // Different secrets, different managers, different lifetimes. One name for both would make
        // "forget the token" quietly sign the user out of Proton Pass — and rotating a service
        // account would take a working Proton session with it.
        var sessionDir = Path.Combine(_root, "session");

        await _protector.ProtectAsync(sessionDir, "proton-session-key");
        await _protector.ProtectOnePasswordTokenAsync(Token);

        await _protector.ForgetOnePasswordTokenAsync();

        Assert.False(_protector.HasProtectedOnePasswordToken());
        Assert.Equal("proton-session-key", await _protector.UnprotectAsync(sessionDir));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }

        // Nothing here has a finalizer, but the pattern is what CA1816 asks for and what a
        // derived test fixture would need if one ever did.
        GC.SuppressFinalize(this);
    }
}
