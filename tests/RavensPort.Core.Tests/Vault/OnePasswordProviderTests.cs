using RavensPort.Core.Diagnostics;
using RavensPort.Core.Models;
using RavensPort.Core.Vault;

namespace RavensPort.Core.Tests.Vault;

/// <summary>
/// The 1Password provider, against a fake <c>op</c>. Covers the probe ladder the setup page walks
/// the user up, a real save-and-load round trip, and the rule that matters most: no secret ever
/// reaches a command-line argument.
/// </summary>
public class OnePasswordProviderTests : IDisposable
{
    private const string ClientSecret = "SENTINEL-CLIENT-SECRET";
    private const string ApiKey = "SENTINEL-API-KEY";
    private const string AccessToken = "SENTINEL-ACCESS-TOKEN";
    private const string RefreshToken = "SENTINEL-REFRESH-TOKEN";
    private const string RouteKeyValue = "SENTINEL-ROUTE-KEY";
    private const string FunnelKeyValue = "SENTINEL-FUNNEL-KEY";

    private static readonly string[] AllSecrets =
        [ClientSecret, ApiKey, AccessToken, RefreshToken, RouteKeyValue, FunnelKeyValue];

    private readonly string _stubDir = Path.Combine(Path.GetTempPath(), $"ravensport-op-{Guid.NewGuid()}");
    private readonly string _logPath = Path.Combine(Path.GetTempPath(), $"ravensport-op-logs-{Guid.NewGuid()}");

    private readonly string _stub;

    public OnePasswordProviderTests()
    {
        // The stub path is handed to the provider directly rather than set in the environment:
        // xunit runs test classes in parallel, and a process-wide variable would be clobbered by
        // whichever other provider test happened to be running at the same moment.
        Directory.CreateDirectory(_stubDir);
        _stub = Path.Combine(_stubDir, "op.exe");
        File.WriteAllText(_stub, "");
    }

    // ---- Probe ----------------------------------------------------------------------------------

    [Fact]
    public async Task NotInstalled_WhenTheBinaryIsMissing()
    {
        var provider = new OnePasswordVaultProvider(
            new FakeCliRunner(), new ActivityLog(_logPath), Path.Combine(_stubDir, "not-here.exe"));

        Assert.Equal(VaultAvailability.NotInstalled, (await provider.ProbeAsync()).Availability);
    }

    [Fact]
    public async Task NotSignedIn_WhenListingVaultsFails()
    {
        var runner = new FakeCliRunner()
            .Respond(["--version"], "2.31.0")
            .Respond(["vault", "list"], exitCode: 1, stderr: "[ERROR] you are not currently signed in");

        var status = await NewProvider(runner).ProbeAsync();

        Assert.Equal(VaultAvailability.NotSignedIn, status.Availability);

        // The CLI's own wording reaches the setup page: it distinguishes locked from signed out
        // from integration-disabled far better than anything guessed from an exit code.
        Assert.Contains("not currently signed in", status.Detail);
    }

    [Fact]
    public async Task VaultMissing_WhenSignedInButRavensPortDoesNotExist()
    {
        var fake = new FakeOnePassword { VaultExists = false };

        var status = await NewProvider(fake.AsRunner()).ProbeAsync();

        Assert.Equal(VaultAvailability.VaultMissing, status.Availability);
        Assert.True(status.CanCreateVault);
    }

    [Fact]
    public async Task Ready_WhenTheVaultExists()
    {
        var status = await NewProvider(new FakeOnePassword().AsRunner()).ProbeAsync();

        Assert.Equal(VaultAvailability.Ready, status.Availability);
        Assert.True(status.IsReady);
    }

    [Fact]
    public async Task Faulted_WhenTheCliIsTooOld()
    {
        // 1.x has neither the item/vault nouns nor JSON output. Saying so is far better than the
        // parse errors it would otherwise produce three calls later.
        var fake = new FakeOnePassword { Version = "0.3.0" };

        var status = await NewProvider(fake.AsRunner()).ProbeAsync();

        Assert.Equal(VaultAvailability.Faulted, status.Availability);
        Assert.Contains("too old", status.Detail);
    }

    [Fact]
    public async Task CreateVault_UsesTheNameTheUserChoseAndRefusesToRepeatIt()
    {
        var fake = new FakeOnePassword { VaultExists = false };
        var runner = fake.AsRunner();
        var provider = NewProvider(runner);

        await provider.ProbeAsync();
        await provider.CreateVaultAsync("Agents");

        Assert.Contains(runner.CallsMatching("vault", "create"), call => call.Args.Contains("Agents"));
        Assert.Equal("Agents", provider.VaultName);

        // A second attempt at the same name is a different intention — using what is already
        // there — and that has rules of its own.
        await Assert.ThrowsAsync<VaultAdoptionException>(() => provider.CreateVaultAsync("Agents"));
        Assert.Single(runner.CallsMatching("vault", "create"));
    }

    [Fact]
    public async Task ASaveRecreatesAnItemTheNoteStillPointsAtButTheVaultNoLongerHas()
    {
        // The note's index outlives the item it names — deleted in 1Password's own UI, or by the
        // integrity check. Editing that id fails the whole save with "isn't an item", which made
        // putting a missing item back impossible: the one operation whose job is to recreate it
        // was the one that could not.
        var fake = new FakeOnePassword();
        var provider = NewProvider(fake.AsRunner());
        var store = StoreWithSecrets();

        await provider.SaveAsync(store);

        var credential = store.Credentials[0];
        var itemId = (await provider.ListLiveItemsAsync())
            .Single(item => item.Role == VaultItemRole.Credential && item.RecordId == credential.Id).ItemId;

        // Deleted behind the provider's back, exactly as the password manager's own UI would.
        await provider.DeleteItemAsync(itemId);

        // The same store, so the same record ids: the note still points at the item that is gone.
        await provider.SaveAsync(store);

        var reloaded = await provider.LoadAsync();
        Assert.Equal(ClientSecret, reloaded.Credentials.Single(c => c.Id == credential.Id).ClientSecret);
    }

    // ---- Round trip -----------------------------------------------------------------------------

    [Fact]
    public async Task AStoreSurvivesASaveAndLoadThroughTheCli()
    {
        var provider = NewProvider(new FakeOnePassword().AsRunner());
        var store = StoreWithSecrets();

        await provider.SaveAsync(store);
        var reloaded = await provider.LoadAsync();

        Assert.Equal(ClientSecret, reloaded.Credentials[0].ClientSecret);
        Assert.Equal(AccessToken, reloaded.Credentials[0].Token!.AccessToken);
        Assert.Equal(RefreshToken, reloaded.Credentials[0].Token!.RefreshToken);
        Assert.Equal(ApiKey, reloaded.Credentials[1].ApiKey);
        Assert.Equal(RouteKeyValue, reloaded.Routes[0].Key.Value);
        Assert.Equal(FunnelKeyValue, reloaded.McpFunnels[0].Key.Value);

        // And the non-secret half, which lives only in the note.
        Assert.Equal("/gdrive", reloaded.Routes[0].PathPrefix);
        Assert.Equal(["drive.readonly"], reloaded.Credentials[0].Scopes);
        Assert.Equal(5610, reloaded.Settings.ListenPort);
    }

    [Fact]
    public async Task NoSecretEverReachesACommandLineArgument()
    {
        // The single most important test here. A Windows process command line is readable by any
        // process in the session, so a secret in argv would make this strictly worse than the
        // encrypted local file it replaced — a downgrade dressed up as a password manager.
        var fake = new FakeOnePassword();
        var runner = fake.AsRunner();
        var provider = NewProvider(runner);

        await provider.SaveAsync(StoreWithSecrets());
        await provider.LoadAsync();

        // Something must actually have run, or this passes vacuously.
        Assert.NotEmpty(runner.Invocations);

        foreach (var secret in AllSecrets)
        {
            Assert.DoesNotContain(runner.AllArguments, arg => arg.Contains(secret, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task SecretsTravelOnStdin()
    {
        // The other half of the rule: absent from argv because they went somewhere else, not
        // because they were quietly dropped.
        var runner = new FakeOnePassword().AsRunner();

        await NewProvider(runner).SaveAsync(StoreWithSecrets());

        var piped = string.Join('\n', runner.Invocations.Select(i => i.Stdin ?? ""));

        foreach (var secret in AllSecrets)
        {
            Assert.Contains(secret, piped, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task AServiceAccountTokenIsPassedInTheEnvironmentAndNeverInArguments()
    {
        var runner = new FakeOnePassword().AsRunner();
        var provider = NewProvider(runner);
        provider.ServiceAccountToken = "ops_SENTINEL-SERVICE-ACCOUNT-TOKEN";

        await provider.ProbeAsync();

        Assert.All(runner.Invocations, i => Assert.Contains("OP_SERVICE_ACCOUNT_TOKEN", i.Env));
        Assert.DoesNotContain(runner.AllArguments, arg => arg.Contains("SENTINEL-SERVICE-ACCOUNT-TOKEN"));
    }

    // ---- Item lifecycle -------------------------------------------------------------------------

    [Fact]
    public async Task ASecondSaveEditsItemsRatherThanCreatingMore()
    {
        var fake = new FakeOnePassword();
        var runner = fake.AsRunner();
        var provider = NewProvider(runner);
        var store = StoreWithSecrets();

        await provider.SaveAsync(store);
        var afterFirst = fake.Items.Count;
        var createsAfterFirst = runner.CallsMatching("item", "create").Count();

        store.Credentials[0].Name = "renamed";
        await provider.SaveAsync(store);

        Assert.Equal(afterFirst, fake.Items.Count);
        Assert.Equal(createsAfterFirst, runner.CallsMatching("item", "create").Count());
        Assert.NotEmpty(runner.CallsMatching("item", "edit"));
    }

    [Fact]
    public async Task DeletingACredentialDeletesItsItem()
    {
        var fake = new FakeOnePassword();
        var runner = fake.AsRunner();
        var provider = NewProvider(runner);
        var store = StoreWithSecrets();

        await provider.SaveAsync(store);
        store.Credentials.RemoveAt(0);
        await provider.SaveAsync(store);

        Assert.Single(runner.CallsMatching("item", "delete"));

        var reloaded = await provider.LoadAsync();
        Assert.Single(reloaded.Credentials);
    }

    [Fact]
    public async Task DisconnectingACredentialClearsItsTokenFromTheVault()
    {
        // op merges an edit rather than replacing the item, so a token that simply stops being
        // sent would sit in the vault forever — a revoked credential still readable in plain
        // sight. The provider has to send it as empty to actually remove it.
        var fake = new FakeOnePassword();
        var provider = NewProvider(fake.AsRunner());
        var store = StoreWithSecrets();

        await provider.SaveAsync(store);
        store.Credentials[0].Token = null;
        await provider.SaveAsync(store);

        var everyValue = fake.Items
            .SelectMany(item => item["fields"]?.AsArray() ?? [])
            .Select(field => field?["value"]?.GetValue<string>())
            .ToList();

        Assert.DoesNotContain(AccessToken, everyValue);
        Assert.DoesNotContain(RefreshToken, everyValue);

        Assert.Null((await provider.LoadAsync()).Credentials[0].Token);
    }

    [Fact]
    public async Task AFailedWriteIsReportedAsAPartialSaveOnceSomethingHasBeenWritten()
    {
        // The distinction ConfigStoreCache keys its rollback on. Getting it wrong in this
        // direction makes the next successful save delete records that are already stored.
        var fake = new FakeOnePassword();
        var provider = NewProvider(fake.AsRunner());

        await provider.SaveAsync(StoreWithSecrets());

        fake.WriteFailure = "[ERROR] vault is locked";

        var store = StoreWithSecrets();
        store.Credentials[0].ClientSecret = "changed";

        var exception = await Assert.ThrowsAsync<VaultSaveException>(() => provider.SaveAsync(store));
        Assert.False(exception.PartiallyApplied);
    }


    [Fact]
    public async Task ItemsThisAppDoesNotOwnAreNeverReadOrDeleted()
    {
        var fake = new FakeOnePassword();
        var runner = fake.AsRunner();
        var provider = NewProvider(runner);

        await provider.SaveAsync(StoreWithSecrets());
        await provider.SaveAsync(new ConfigStore());

        // Every delete targeted an item this app created; the listing filter is what keeps a
        // user's own entries in RavensPort out of reach.
        var deleted = runner.CallsMatching("item", "delete").Select(i => i.Args[2]).ToList();
        Assert.All(deleted, id => Assert.StartsWith("item-", id));
    }

    [Fact]
    public async Task AnEmptyVaultLoadsAsAnEmptyStore()
    {
        var store = await NewProvider(new FakeOnePassword().AsRunner()).LoadAsync();

        Assert.Empty(store.Credentials);
        Assert.Empty(store.Routes);
    }

    private OnePasswordVaultProvider NewProvider(FakeCliRunner runner) =>
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
        store.McpFunnels.Add(new McpFunnelRecord
        {
            Name = "coding agent",
            Slug = "coding-agent",
            Key = new ProxyKey { Value = FunnelKeyValue },
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
