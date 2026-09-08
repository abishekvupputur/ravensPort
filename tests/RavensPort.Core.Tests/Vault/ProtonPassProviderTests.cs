using RavensPort.Core.Diagnostics;
using RavensPort.Core.Models;
using RavensPort.Core.Vault;

namespace RavensPort.Core.Tests.Vault;

/// <summary>
/// The Proton Pass provider, against a fake <c>pass-cli</c>.
///
/// The behaviour worth pinning hardest is what its constraint forced: <c>item update</c> takes
/// values as arguments, so this provider never updates. A changed record is written as a new item,
/// the note is rewritten to point at it, and only then is the old one deleted.
/// </summary>
public class ProtonPassProviderTests : IDisposable
{
    private const string ClientSecret = "SENTINEL-CLIENT-SECRET";
    private const string ApiKey = "SENTINEL-API-KEY";
    private const string AccessToken = "SENTINEL-ACCESS-TOKEN";
    private const string RefreshToken = "SENTINEL-REFRESH-TOKEN";
    private const string RouteKeyValue = "SENTINEL-ROUTE-KEY";

    private static readonly string[] AllSecrets =
        [ClientSecret, ApiKey, AccessToken, RefreshToken, RouteKeyValue];

    private readonly string _stubDir = Path.Combine(Path.GetTempPath(), $"ravensport-pass-{Guid.NewGuid()}");
    private readonly string _logPath = Path.Combine(Path.GetTempPath(), $"ravensport-pass-logs-{Guid.NewGuid()}");

    private readonly string _stub;

    public ProtonPassProviderTests()
    {
        // Handed to the provider directly rather than set in the environment: xunit runs test
        // classes in parallel, and a process-wide variable would be clobbered by whichever other
        // provider test happened to be running at the same moment.
        Directory.CreateDirectory(_stubDir);
        _stub = Path.Combine(_stubDir, "pass-cli.exe");
        File.WriteAllText(_stub, "");
    }

    [Fact]
    public async Task NotInstalled_WhenTheBinaryIsMissing()
    {
        var provider = new ProtonPassVaultProvider(
            new FakeCliRunner(), new ActivityLog(_logPath), Path.Combine(_stubDir, "not-here.exe"));

        Assert.Equal(VaultAvailability.NotInstalled, (await provider.ProbeAsync()).Availability);
    }

    [Fact]
    public async Task NotSignedIn_WhenListingVaultsFails()
    {
        var runner = new FakeCliRunner()
            .Respond(["--version"], "1.4.0")
            .Respond(["vault", "list"], exitCode: 1, stderr: "not logged in; run `pass-cli login`");

        var status = await NewProvider(runner).ProbeAsync();

        Assert.Equal(VaultAvailability.NotSignedIn, status.Availability);
        Assert.Contains("pass-cli login", status.Detail);
    }

    [Fact]
    public async Task VaultMissing_WhenSignedInButRavensPortDoesNotExist()
    {
        var fake = new FakeProtonPass { VaultExists = false };

        Assert.Equal(VaultAvailability.VaultMissing, (await NewProvider(fake.AsRunner()).ProbeAsync()).Availability);
    }

    [Fact]
    public async Task Ready_WhenTheVaultExists()
    {
        Assert.True((await NewProvider(new FakeProtonPass().AsRunner()).ProbeAsync()).IsReady);
    }

    [Fact]
    public async Task CreateVault_UsesTheNameTheUserChoseAndResolvesItsShareId()
    {
        // The name is the user's: RavensPort is only the default this app looks for first, and
        // a separate vault per profile is the whole point of being able to name it.
        var fake = new FakeProtonPass { VaultExists = false };
        var runner = fake.AsRunner();
        var provider = NewProvider(runner);

        await provider.ProbeAsync();
        await provider.CreateVaultAsync("Agents");

        Assert.Contains(runner.CallsMatching("vault", "create"), call => call.Args.Contains("Agents"));
        Assert.Equal("Agents", provider.VaultName);
        Assert.Equal(VaultAvailability.Ready, (await provider.ProbeAsync()).Availability);
    }

    [Fact]
    public async Task CreateVault_StampsTheConfigItemSoTheVaultIsFoundAgain()
    {
        // Nothing on this PC remembers the name that was just typed, so the new vault has to carry
        // the evidence itself — otherwise the next launch would not know where its configuration is.
        var fake = new FakeProtonPass { VaultExists = false };
        var provider = NewProvider(fake.AsRunner());

        await provider.CreateVaultAsync("Agents");

        Assert.Contains(fake.ItemsInVault("share-agents"),
            item => item["title"]?.GetValue<string>() == VaultItemNaming.ConfigTitle);
    }

    [Fact]
    public async Task CreateVault_RefusesANameThatIsAlreadyTaken()
    {
        // "Create" and "take over what is already there" are different intentions, and the second
        // one has rules about what may be adopted.
        var fake = new FakeProtonPass();
        var provider = NewProvider(fake.AsRunner());

        var refusal = await Assert.ThrowsAsync<VaultAdoptionException>(
            () => provider.CreateVaultAsync(VaultConstants.VaultName));

        Assert.Contains("already a vault", refusal.Message);
    }

    [Fact]
    public async Task AStoreSurvivesASaveAndLoadThroughTheCli()
    {
        var provider = NewProvider(new FakeProtonPass().AsRunner());
        var store = StoreWithSecrets();

        await provider.SaveAsync(store);
        var reloaded = await provider.LoadAsync();

        Assert.Equal(ClientSecret, reloaded.Credentials[0].ClientSecret);
        Assert.Equal(AccessToken, reloaded.Credentials[0].Token!.AccessToken);
        Assert.Equal(RefreshToken, reloaded.Credentials[0].Token!.RefreshToken);
        Assert.Equal(ApiKey, reloaded.Credentials[1].ApiKey);
        Assert.Equal(RouteKeyValue, reloaded.Routes[0].Key.Value);
        Assert.Equal("/gdrive", reloaded.Routes[0].PathPrefix);
    }

    [Fact]
    public async Task NoSecretEverReachesACommandLineArgument()
    {
        // The constraint that shaped this whole provider. `item update` would have been far
        // simpler and would have published every secret to the process table.
        var runner = new FakeProtonPass().AsRunner();
        var provider = NewProvider(runner);

        await provider.SaveAsync(StoreWithSecrets());
        await provider.LoadAsync();

        Assert.NotEmpty(runner.Invocations);
        Assert.Empty(runner.CallsMatching("item", "update"));

        foreach (var secret in AllSecrets)
        {
            Assert.DoesNotContain(runner.AllArguments, arg => arg.Contains(secret, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task SecretsTravelOnStdin()
    {
        var runner = new FakeProtonPass().AsRunner();

        await NewProvider(runner).SaveAsync(StoreWithSecrets());

        var piped = string.Join('\n', runner.Invocations.Select(i => i.Stdin ?? ""));

        foreach (var secret in AllSecrets) Assert.Contains(secret, piped, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APersonalAccessTokenIsPassedInTheEnvironmentAndNeverInArguments()
    {
        var runner = new FakeProtonPass().AsRunner();
        var provider = NewProvider(runner);
        provider.PersonalAccessToken = "pst_SENTINEL-PAT::KEY";

        await provider.ProbeAsync();

        Assert.All(runner.Invocations, i => Assert.Contains("PROTON_PASS_PERSONAL_ACCESS_TOKEN", i.Env));
        Assert.DoesNotContain(runner.AllArguments, arg => arg.Contains("SENTINEL-PAT"));
    }

    [Fact]
    public async Task AnUnchangedRecordIsNotRewritten()
    {
        // On this backend a rewrite means deleting and recreating the user's vault entry, so
        // saving a store whose secrets have not moved must touch nothing.
        var runner = new FakeProtonPass().AsRunner();
        var provider = NewProvider(runner);
        var store = StoreWithSecrets();

        await provider.SaveAsync(store);
        var createsAfterFirst = runner.CallsMatching("item", "create").Count();

        store.Settings.ListenPort = 5999;
        await provider.SaveAsync(store);

        // Exactly one more create: the new config note. No secret item was touched.
        Assert.Equal(createsAfterFirst + 1, runner.CallsMatching("item", "create").Count());
    }

    [Fact]
    public async Task AChangedRecordIsRecreatedAndItsPredecessorDeleted()
    {
        var fake = new FakeProtonPass();
        var runner = fake.AsRunner();
        var provider = NewProvider(runner);
        var store = StoreWithSecrets();

        await provider.SaveAsync(store);

        store.Credentials[0].ClientSecret = "ROTATED-CLIENT-SECRET";
        await provider.SaveAsync(store);

        Assert.NotEmpty(runner.CallsMatching("item", "delete"));

        // Exactly one item claims that credential afterwards, and it holds the new secret.
        var reloaded = await provider.LoadAsync();
        Assert.Equal("ROTATED-CLIENT-SECRET", reloaded.Credentials[0].ClientSecret);

        var claiming = fake.Items.Count(item =>
            VaultItemNaming.TryParse(item["title"]?.GetValue<string>() ?? "", out var role, out var id)
            && role == VaultItemRole.Credential
            && id == store.Credentials[0].Id);

        Assert.Equal(1, claiming);
    }

    [Fact]
    public async Task IdsArePassedAsAttachedFlagsSoLeadingHyphensAreNotReadAsArguments()
    {
        // Proton Pass ids are base64url, so roughly one in sixty starts with a hyphen. Passed as
        // two arguments, the CLI reads "--item-id -0_TRk…" as an unknown flag and refuses:
        // "error: unexpected argument '-0' found". The delete then fails silently on every save,
        // leaving an orphaned item behind — which is how one route ended up with two key items.
        var fake = new FakeProtonPass();
        var runner = fake.AsRunner();
        var provider = NewProvider(runner);
        var store = StoreWithSecrets();

        await provider.SaveAsync(store);
        store.Credentials.RemoveAt(0);
        await provider.SaveAsync(store);

        Assert.All(runner.Invocations, invocation =>
            Assert.DoesNotContain(invocation.Args, arg => arg is "--share-id" or "--item-id"));

        Assert.Contains(runner.AllArguments, arg => arg.StartsWith("--share-id=", StringComparison.Ordinal));
        Assert.Contains(runner.AllArguments, arg => arg.StartsWith("--item-id=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ADuplicateItemClaimingTheSameRecordIsSweptOnTheNextSave()
    {
        // Whatever leaves one behind — a delete that failed, a hand-made copy — the next save has
        // to converge. Two items claiming one route key is worse than untidy: the proxy accepts
        // only the indexed one, and the other looks equally valid in the password manager.
        var fake = new FakeProtonPass();
        var provider = NewProvider(fake.AsRunner());
        var store = StoreWithSecrets();

        await provider.SaveAsync(store);

        var routeId = store.Routes[0].Id;
        fake.ForgeDuplicate(VaultItemNaming.ForRouteKey(routeId, store.Routes[0].PathPrefix), "STALE-KEY");

        Assert.Equal(2, CountRouteKeyItems(fake, routeId));

        store.Settings.ListenPort = 5999;
        await provider.SaveAsync(store);

        Assert.Equal(1, CountRouteKeyItems(fake, routeId));
        Assert.Equal(RouteKeyValue, (await provider.LoadAsync()).Routes[0].Key.Value);
    }

    private static int CountRouteKeyItems(FakeProtonPass fake, Guid routeId) =>
        fake.Items.Count(item =>
            VaultItemNaming.TryParse(item["title"]?.GetValue<string>() ?? "", out var role, out var id)
            && role == VaultItemRole.RouteKey
            && id == routeId);

    [Fact]
    public async Task DeletingACredentialDeletesItsItem()
    {
        var provider = NewProvider(new FakeProtonPass().AsRunner());
        var store = StoreWithSecrets();

        await provider.SaveAsync(store);
        store.Credentials.RemoveAt(0);
        await provider.SaveAsync(store);

        Assert.Single((await provider.LoadAsync()).Credentials);
    }

    [Fact]
    public async Task AMaskedSecretIsRefusedRatherThanStored()
    {
        // If pass-cli hands back its "<concealed by Proton Pass>" placeholder, using it would
        // write that literal string into the app's config as if it were the secret, and every
        // request would fail against the upstream with nothing to explain why.
        var fake = new FakeProtonPass();
        var provider = NewProvider(fake.AsRunner());

        await provider.SaveAsync(StoreWithSecrets());

        fake.MaskSecrets = true;
        var reloaded = await provider.LoadAsync();

        Assert.DoesNotContain("concealed by Proton Pass", reloaded.Credentials[0].ClientSecret);
        Assert.Equal("", reloaded.Credentials[0].ClientSecret);
        Assert.NotNull(provider.LastLoadWarning);
        Assert.Contains("masked", provider.LastLoadWarning);
    }


    [Fact]
    public async Task ATemplateRejectionNamesTheFlagThatWouldShowTheRightShape()
    {
        // The template shape is the one part of this provider inferred rather than documented, so
        // a rejection has to point at how to find the real one instead of failing opaquely.
        var fake = new FakeProtonPass { WriteFailure = "invalid template: unknown field" };

        var exception = await Assert.ThrowsAsync<VaultSaveException>(() =>
            NewProvider(fake.AsRunner()).SaveAsync(StoreWithSecrets()));

        Assert.Contains("--get-template", exception.Message);
    }

    [Fact]
    public async Task AnEmptyVaultLoadsAsAnEmptyStore()
    {
        var store = await NewProvider(new FakeProtonPass().AsRunner()).LoadAsync();

        Assert.Empty(store.Credentials);
        Assert.Empty(store.Routes);
    }

    private ProtonPassVaultProvider NewProvider(FakeCliRunner runner) =>
        new(runner, new ActivityLog(_logPath), _stub);

    private static ConfigStore StoreWithSecrets()
    {
        var oauth = new CredentialRecord
        {
            Name = "Google Drive",
            ClientId = "client-id",
            ClientSecret = ClientSecret,
            Scopes = ["drive.readonly"],
            Authority = "https://accounts.google.com",
            Token = new TokenSet(AccessToken, RefreshToken,
                DateTimeOffset.UtcNow.AddHours(1), "Bearer", DateTimeOffset.UtcNow),
        };

        var apiKeyCredential = new CredentialRecord
        {
            Name = "Weather",
            Kind = CredentialKind.ApiKey,
            ApiKey = ApiKey,
        };

        var upstream = new UpstreamRecord { Name = "google", BaseUrl = "https://www.googleapis.com" };

        var store = new ConfigStore { Settings = { ListenPort = 5610 } };
        store.Credentials.AddRange([oauth, apiKeyCredential]);
        store.Upstreams.Add(upstream);
        store.Routes.Add(new RouteMapping
        {
            PathPrefix = "/gdrive",
            UpstreamId = upstream.Id,
            Key = new ProxyKey { Value = RouteKeyValue },
            Credentials = [RouteCredential.For(oauth.Id, CredentialPlacement.Header)],
        });

        return store;
    }

    public void Dispose()
    {
        try { Directory.Delete(_stubDir, recursive: true); } catch { /* best effort */ }
        try { Directory.Delete(_logPath, recursive: true); } catch { /* best effort */ }

        // Nothing here has a finalizer, but the pattern is what CA1816 asks for and what a
        // derived test fixture would need if one ever did.
        GC.SuppressFinalize(this);
    }
}
