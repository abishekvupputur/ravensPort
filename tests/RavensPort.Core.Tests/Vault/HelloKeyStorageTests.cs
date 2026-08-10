using System.Reflection;
using RavensPort.Core.Diagnostics;
using RavensPort.Core.Vault;

namespace RavensPort.Core.Tests.Vault;

/// <summary>
/// The real Windows pieces: the Credential Manager P/Invoke, and the wiring that decides which
/// implementations the shipping app actually gets.
///
/// <see cref="HelloCredentialBindingTests"/> substitutes the TPM to test the scheme; this file
/// exists so that substitution cannot hide a broken real one. Two things are checked here that a
/// fake cannot check: that <c>CredWrite</c>/<c>CredRead</c>/<c>CredDelete</c> round-trip against
/// the actual credential vault, and that <c>new HelloKeyProtector(log)</c> composes the real signer
/// with the real store — because a test suite full of fakes is exactly the situation in which
/// shipping a fake would go unnoticed.
/// </summary>
public class HelloKeyStorageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ravensport-hello-{Guid.NewGuid()}");
    private readonly WindowsCredentialStore _store = new();

    private string SessionDir => Path.Combine(_root, "session");
    private string CredentialName => HelloKeyProtector.NameFor(SessionDir);

    private ActivityLog Log() => new(Path.Combine(_root, "logs"));

    // ---- The Credential Manager itself --------------------------------------------------------

    [Fact]
    public void CredentialStore_RoundTripsABlob()
    {
        var target = $"RavensPort.Test.{Guid.NewGuid():N}";
        var blob = new byte[] { 1, 2, 3, 250, 251, 0, 255 };

        try
        {
            Assert.False(_store.Exists(target));

            _store.Write(target, blob);

            Assert.True(_store.Exists(target));
            Assert.Equal(blob, _store.Read(target));
        }
        finally
        {
            _store.Delete(target);
        }

        // Gone means gone: sign-out depends on this, and a credential that survived it would keep
        // offering a gesture for a session the user just ended.
        Assert.False(_store.Exists(target));
        Assert.Null(_store.Read(target));
    }

    [Fact]
    public void CredentialStore_RoundTripsARealSealedBlob()
    {
        // The size and shape of the thing actually stored, rather than a handful of bytes: a blob
        // with embedded nulls and a length near the generic-credential limit is where a marshalling
        // bug would show up.
        var target = $"RavensPort.Test.{Guid.NewGuid():N}";
        var key = HelloSealedKey.DeriveKey([1, 2, 3, 4]);
        var blob = HelloSealedKey.Seal(key, HelloSealedKey.NewChallenge(), ProtonPassSession.GenerateKey());

        try
        {
            _store.Write(target, blob);

            var read = _store.Read(target);

            Assert.Equal(blob, read);

            // And it is still openable after the round trip, which is the property that matters.
            Assert.NotNull(read);
            Assert.False(string.IsNullOrEmpty(HelloSealedKey.Open(key, read)));
        }
        finally
        {
            _store.Delete(target);
        }
    }

    [Fact]
    public void CredentialStore_Write_ReplacesRatherThanDuplicating()
    {
        var target = $"RavensPort.Test.{Guid.NewGuid():N}";

        try
        {
            _store.Write(target, [1, 1, 1]);
            _store.Write(target, [2, 2]);

            Assert.Equal([2, 2], _store.Read(target));
        }
        finally
        {
            _store.Delete(target);
        }
    }

    [Fact]
    public void CredentialStore_Read_IsNull_RatherThanThrowing_WhenNothingIsStored()
    {
        // A first run is not an error. It has to be distinguishable from one, because the two need
        // opposite things said about them.
        Assert.Null(_store.Read($"RavensPort.Test.{Guid.NewGuid():N}"));
        Assert.False(_store.Exists($"RavensPort.Test.{Guid.NewGuid():N}"));
    }

    [Fact]
    public void CredentialStore_Delete_IsSilent_WhenNothingIsStored()
    {
        // Sign-out calls this unconditionally and must not fail because there was nothing to remove.
        _store.Delete($"RavensPort.Test.{Guid.NewGuid():N}");
    }

    // ---- Naming -------------------------------------------------------------------------------

    [Fact]
    public void NameFor_KeepsTheBareNameForTheRealSessionDirectory()
    {
        // Installs that predate the move out of hello.bin have to find their own credential, so the
        // default directory's name is not allowed to drift.
        Assert.Equal(
            "RavensPort.ProtonPassSessionKey",
            HelloKeyProtector.NameFor(ProtonPassSession.DefaultDirectory));
    }

    [Fact]
    public void NameFor_SeparatesOtherDirectories()
    {
        var mine = HelloKeyProtector.NameFor(SessionDir);
        var theirs = HelloKeyProtector.NameFor(Path.Combine(_root, "other"));

        Assert.NotEqual(mine, theirs);
        Assert.NotEqual(HelloKeyProtector.NameFor(ProtonPassSession.DefaultDirectory), mine);

        // Stable across calls, or the key would be stored under one name and looked for under
        // another.
        Assert.Equal(mine, HelloKeyProtector.NameFor(SessionDir));
    }

    [Fact]
    public void NameFor_IgnoresTrailingSeparatorsAndCase()
    {
        // The same directory spelled two ways has to reach the same credential, or an override
        // written with a trailing slash would look like a different session.
        Assert.Equal(
            HelloKeyProtector.NameFor(SessionDir),
            HelloKeyProtector.NameFor(SessionDir + Path.DirectorySeparatorChar));

        Assert.Equal(
            HelloKeyProtector.NameFor(SessionDir),
            HelloKeyProtector.NameFor(SessionDir.ToUpperInvariant()));
    }

    // ---- What the shipping app is actually built with -----------------------------------------

    [Fact]
    public void TheProductionProtector_UsesTheRealTpmAndTheRealCredentialManager()
    {
        // The regression this file exists for. Every other Hello test substitutes the signer, so
        // nothing else in the suite would notice if the public constructor started handing out a
        // fake — and a fake signer means a stored key with no gesture in front of it.
        var protector = new HelloKeyProtector(Log());

        Assert.IsType<KeyCredentialHelloSigner>(FieldValue(protector, "_signer"));
        Assert.IsType<WindowsCredentialStore>(FieldValue(protector, "_store"));
    }

    [Fact]
    public void TheTestConstructorIsNotVisibleToTheDiContainer()
    {
        // ServiceProvider only considers public constructors, so the seam cannot be selected by
        // accident at startup. Asserted rather than assumed, because "internal" is one keyword away
        // from "public".
        var publicConstructors = typeof(HelloKeyProtector)
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance);

        var only = Assert.Single(publicConstructors);
        var parameter = Assert.Single(only.GetParameters());

        Assert.Equal(typeof(ActivityLog), parameter.ParameterType);
    }

    [Fact]
    public async Task IsAvailableAsync_AnswersWithoutThrowing_OnAnyMachine()
    {
        // Runs on developer laptops with Hello and on CI runners without it. Either answer is
        // correct; throwing is not, because this is on the startup path.
        var available = await new HelloKeyProtector(Log()).IsAvailableAsync();

        Assert.True(available || !available);
    }

    [Fact]
    public void HasProtectedKey_IsFalse_ForASessionThatHasNeverBeenProtected()
    {
        Assert.False(new HelloKeyProtector(Log()).HasProtectedKey(SessionDir));
    }

    [Fact]
    public async Task UnprotectAsync_ReturnsNull_RatherThanPrompting_WhenNothingIsStored()
    {
        // Distinct from "stored but would not open", which throws: the setup page offers a sign-in
        // for the first and a discard for the second. This also means CI never raises a prompt.
        Assert.Null(await new HelloKeyProtector(Log()).UnprotectAsync(SessionDir));
    }

    [Fact]
    public async Task ForgetAsync_RemovesTheRealCredentialAndTheLegacyFile()
    {
        Directory.CreateDirectory(SessionDir);
        File.WriteAllBytes(HelloKeyProtector.LegacyBlobPath(SessionDir), [1, 2, 3]);
        _store.Write(CredentialName, [4, 5, 6]);

        await new HelloKeyProtector(Log()).ForgetAsync(SessionDir);

        Assert.False(_store.Exists(CredentialName));
        Assert.False(File.Exists(HelloKeyProtector.LegacyBlobPath(SessionDir)));
    }

    [Fact]
    public void HasProtectedKey_MovesALegacyBlobIntoTheRealCredentialManager()
    {
        // The migration, end to end against the actual credential vault rather than a dictionary.
        var legacy = HelloKeyProtector.LegacyBlobPath(SessionDir);
        var blob = new byte[] { 1, 32, 0, 0, 0, 9, 9, 9 };

        Directory.CreateDirectory(SessionDir);
        File.WriteAllBytes(legacy, blob);

        Assert.True(new HelloKeyProtector(Log()).HasProtectedKey(SessionDir));

        Assert.Equal(blob, _store.Read(CredentialName));

        // The old copy goes. Leaving it would mean one secret in two places, only one of which
        // sign-out knows about.
        Assert.False(File.Exists(legacy));
    }

    // ---- The rule the rest of it exists to enforce --------------------------------------------

    [Fact]
    public async Task SignInAsync_RefusesWhenNoKeyHasBeenPrepared()
    {
        var session = new ProtonPassSession(Log(), SessionDir);

        var authenticator = new ProtonPassAuthenticator(
            new ThrowingCliRunner(),
            session,
            new HelloKeyProtector(Log()),
            new VaultGateService(
                new OnePasswordVaultProvider(new ThrowingCliRunner(), Log(), Path.Combine(_root, "none.exe")),
                new ProtonPassVaultProvider(new ThrowingCliRunner(), Log(), Path.Combine(_root, "none.exe"), session),
                Log()),
            Log());

        // Refused before pass-cli is even located: the key has to exist and be stored before there
        // is a session on disk for it to open, never the other way round.
        await Assert.ThrowsAsync<VaultCliException>(() => authenticator.SignInAsync(_ => { }));

        Assert.False(session.HasSessionOnDisk);
    }

    private static object? FieldValue(object instance, string name) =>
        instance.GetType()
            .GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)
            ?.GetValue(instance);

    /// <summary>Fails loudly if anything reaches it — nothing in these tests should run a CLI.</summary>
    private sealed class ThrowingCliRunner : ICliRunner
    {
        public Task<CliResult> RunAsync(
            string exePath, IReadOnlyList<string> args, string? stdin = null,
            IReadOnlyDictionary<string, string>? env = null, TimeSpan? timeout = null,
            CancellationToken ct = default)
            => throw new InvalidOperationException("No CLI should run here.");

        public Task<CliResult> RunStreamingAsync(
            string exePath, IReadOnlyList<string> args, Action<string> onOutputLine,
            IReadOnlyDictionary<string, string>? env = null, TimeSpan? timeout = null,
            CancellationToken ct = default)
            => throw new InvalidOperationException("No CLI should run here.");
    }

    public void Dispose()
    {
        try
        {
            _store.Delete(CredentialName);
            _store.Delete(HelloKeyProtector.NameFor(Path.Combine(_root, "other")));
        }
        catch
        {
            // Nothing left to clean up.
        }

        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);

        GC.SuppressFinalize(this);
    }
}
