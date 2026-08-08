using System.Reflection;
using System.Runtime.Versioning;
using System.Text;
using RavensPort.Core.Diagnostics;
using RavensPort.Core.Vault;

namespace RavensPort.Core.Tests.Vault;

/// <summary>
/// The Linux half of session key storage, which had no tests at all: the Windows target carries
/// <see cref="HelloKeyStorageTests"/> and <see cref="HelloCredentialBindingTests"/> over
/// <c>HelloKeyProtector</c>, and the portable target shipped its replacement unexercised. The
/// Linux CI job was green over 708 tests, none of which touched the code that decides where a
/// Proton Pass session key lives on Linux.
///
/// Everything here runs against a fake <see cref="ISecretStore"/>, so it runs on any machine —
/// there is no session bus on a CI runner and no libsecret at all on Windows. That covers the
/// arrangement: the name a key is filed under, what is handed over, what comes back, and what the
/// user is told. The one thing a fake cannot answer — whether the shipping app is wired to the
/// real keyring rather than to something like this — is the last test, and it is the one that
/// only runs on Linux.
///
/// Carries the protector's own <see cref="UnsupportedOSPlatformAttribute"/> so that CA1416 sees a
/// call site with the same platform contract as what it calls. It is a compile-time claim only:
/// these tests do run on Windows, because with the store faked there is nothing here that Windows
/// cannot do, and a Linux-only test is one a Windows developer never sees fail.
/// </summary>
[UnsupportedOSPlatform("windows")]
public class KeyringKeyStorageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ravensport-keyring-{Guid.NewGuid()}");
    private readonly FakeSecretStore _store = new();

    private string SessionDir => Path.Combine(_root, "session");

    private ActivityLog Log() => new(Path.Combine(_root, "logs"));

    private KeyringSessionKeyProtector Protector(ActivityLog? log = null) => new(log ?? Log(), _store);

    // ---- What is kept, and under what name ----------------------------------------------------

    [Fact]
    public async Task AKeyGoesInAndComesBackOut()
    {
        await Protector().ProtectAsync(SessionDir, "the-session-key");

        Assert.Equal("the-session-key", await Protector().UnprotectAsync(SessionDir));
    }

    [Fact]
    public async Task NothingStoredIsNullRatherThanAFailure()
    {
        // A first run, which callers distinguish from a key that is there and will not open. The
        // difference decides whether the user is asked to sign in or told something is wrong.
        Assert.Null(await Protector().UnprotectAsync(SessionDir));
    }

    [Fact]
    public async Task TheKeyIsStoredAsItsOwnUtf8Bytes()
    {
        // Not an implementation detail: a key written by one build has to be readable by the next.
        await Protector().ProtectAsync(SessionDir, "ünïcode-key");

        Assert.Equal(
            Encoding.UTF8.GetBytes("ünïcode-key"),
            _store.Read(KeyringSessionKeyProtector.NameFor(SessionDir)));
    }

    [Fact]
    public async Task TwoSessionDirectoriesDoNotShareAKey()
    {
        // Two RavensPort profiles pointing at different Proton Pass sessions. Sharing a name here
        // means the second sign-in silently overwrites the first, and the first profile then
        // decrypts nothing.
        var other = Path.Combine(_root, "other-session");

        await Protector().ProtectAsync(SessionDir, "first");
        await Protector().ProtectAsync(other, "second");

        Assert.Equal("first", await Protector().UnprotectAsync(SessionDir));
        Assert.Equal("second", await Protector().UnprotectAsync(other));
    }

    [Fact]
    public async Task HasProtectedKeyAnswersWithoutReadingTheKey()
    {
        var protector = Protector();

        Assert.False(protector.HasProtectedKey(SessionDir));

        await protector.ProtectAsync(SessionDir, "the-session-key");

        Assert.True(protector.HasProtectedKey(SessionDir));

        // The setup page binds this from a property getter, so it must not be the call that pulls
        // the secret out — a store that prompts, or logs a read, would do so on every repaint.
        Assert.Equal(0, _store.Reads);
    }

    [Fact]
    public async Task ForgettingRemovesIt()
    {
        var protector = Protector();
        await protector.ProtectAsync(SessionDir, "the-session-key");

        await protector.ForgetAsync(SessionDir);

        Assert.False(protector.HasProtectedKey(SessionDir));
        Assert.Null(await protector.UnprotectAsync(SessionDir));
    }

    [Fact]
    public async Task ForgettingWhatWasNeverThereIsNotAnError()
    {
        // Offered on the setup page as a way out of a broken session, which means it is reachable
        // when there is nothing to remove.
        await Protector().ForgetAsync(SessionDir);
    }

    // ---- Failure, and what the user is told ----------------------------------------------------

    [Fact]
    public async Task AKeyringThatRefusesTheWriteThrowsRatherThanReportingSuccess()
    {
        // The interface says so, and the reason is in ProtectAsync: a caller told this succeeded
        // lets someone finish a sign-in believing the session survives a restart.
        _store.FailWrites = true;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Protector().ProtectAsync(SessionDir, "the-session-key"));
    }

    [Fact]
    public async Task TheUserIsToldItIsTheKeyringAndNotAGesture()
    {
        var log = Log();

        await Protector(log).ProtectAsync(SessionDir, "the-session-key");

        var entry = Assert.Single(log.GetRecent(10), line => line.Contains("session key"));

        // The whole point of this class is that it makes a weaker promise than Hello does. Saying
        // "Hello" here — or borrowing the Windows copy — would tell someone a gesture protects a
        // key on a platform where no gesture is involved.
        Assert.Contains("keyring", entry, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Hello", entry, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TheKeyItselfNeverReachesTheLog()
    {
        var log = Log();
        var protector = Protector(log);

        await protector.ProtectAsync(SessionDir, "the-session-key");
        await protector.UnprotectAsync(SessionDir);

        Assert.DoesNotContain(log.GetRecent(50), line => line.Contains("the-session-key"));
    }

    // ---- The real thing ------------------------------------------------------------------------

    [Fact]
    public void TheAppComposesTheRealKeyring()
    {
        // Skipped on Windows, and not merely because libsecret is missing: constructing
        // SecretServiceStore there loads libsecret-1.so.0 to build its schema and throws. The
        // portable target still runs on Windows during development, which is exactly when a suite
        // full of fakes could otherwise hide a shipping build wired to a fake.
        if (OperatingSystem.IsWindows()) return;

        var protector = new KeyringSessionKeyProtector(Log());

        var store = typeof(KeyringSessionKeyProtector)
            .GetField("_store", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(protector);

        Assert.IsType<SecretServiceStore>(store);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);

        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>
    /// A keyring that is always unlocked and never prompts, which is close enough to the real one:
    /// what this file is about is the arrangement around the store, not libsecret's behaviour.
    /// Counts reads so that a getter which quietly pulls the secret out cannot pass.
    /// </summary>
    private sealed class FakeSecretStore : ISecretStore
    {
        private readonly Dictionary<string, byte[]> _entries = [];

        public int Reads { get; private set; }

        public bool FailWrites { get; set; }

        public bool Exists(string target) => _entries.ContainsKey(target);

        public byte[]? Read(string target)
        {
            Reads++;

            return _entries.TryGetValue(target, out var blob) ? blob : null;
        }

        public void Write(string target, byte[] blob)
        {
            if (FailWrites) throw new InvalidOperationException("the keyring refused the write");

            _entries[target] = blob;
        }

        public void Delete(string target) => _entries.Remove(target);
    }
}
