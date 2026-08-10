using RavensPort.Core.Models;
using RavensPort.Core.Vault;

namespace RavensPort.Core.Tests.Vault;

/// <summary>
/// The store survives a trip through the vault intact.
///
/// This is the descendant of the old SecureStore round-trip test, and it matters more than that
/// one did: the file format was one JSON document, while this splits every secret into a separate
/// item and reassembles it. A field the mapper forgets is a field that silently stops being
/// stored, and nobody notices until a restart.
/// </summary>
public class VaultRoundTripTests
{
    [Fact]
    public async Task AMaximalStoreSurvivesASaveAndLoad()
    {
        var vault = InMemoryVault.Empty();
        var original = MaximalStore();

        await vault.SaveAsync(original);
        var reloaded = await vault.LoadAsync();

        AssertSameStore(original, reloaded);
    }

    [Fact]
    public async Task RepeatedSavesDoNotAccumulateItems()
    {
        // The index has to be read back and reused. If a save could not find the item it wrote
        // last time it would create a second one, and the vault would grow without bound while
        // the app kept working — invisible until the user opened their password manager.
        var vault = InMemoryVault.Empty();
        var store = MaximalStore();

        await vault.SaveAsync(store);
        var afterFirst = vault.Items.Count;

        await vault.SaveAsync(store);
        await vault.SaveAsync(store);

        Assert.Equal(afterFirst, vault.Items.Count);
    }

    [Fact]
    public async Task ACredentialWithNoSecretYetGetsNoItem()
    {
        // An OAuth credential entered but never connected has nothing worth storing separately.
        // Writing an item holding only a record id would fill the user's vault with entries that
        // mean nothing to them.
        var store = new ConfigStore();
        store.Credentials.Add(new CredentialRecord { Name = "not yet connected", ClientId = "", ClientSecret = "" });

        var vault = InMemoryVault.Empty();
        await vault.SaveAsync(store);

        Assert.All(vault.Items, item => Assert.Equal(VaultItemNaming.ConfigTitle, item.Title));

        var reloaded = await vault.LoadAsync();
        var credential = Assert.Single(reloaded.Credentials);
        Assert.Equal("not yet connected", credential.Name);
        Assert.Null(credential.Token);
    }

    [Fact]
    public async Task AnAccessTokenWithoutARefreshTokenRoundTrips()
    {
        // A grant that cannot be refreshed is a normal state, and RefreshToken being null is what
        // TokenRefreshService filters on — turning it into an empty string would put the
        // credential in a refresh loop that can never succeed.
        var store = new ConfigStore();
        store.Credentials.Add(new CredentialRecord
        {
            Name = "no-refresh",
            ClientId = "id",
            ClientSecret = "secret",
            Token = new TokenSet("access-only", null, DateTimeOffset.UtcNow.AddHours(1), "Bearer", DateTimeOffset.UtcNow),
        });

        var vault = InMemoryVault.Empty();
        await vault.SaveAsync(store);

        var token = Assert.Single((await vault.LoadAsync()).Credentials).Token;
        Assert.NotNull(token);
        Assert.Equal("access-only", token.AccessToken);
        Assert.Null(token.RefreshToken);
    }

    [Fact]
    public async Task AServiceAccountKeyFileRoundTripsWithItsSubject()
    {
        // The key file is the whole identity of this credential and the vault is its only copy;
        // losing it on a reload would leave a credential that looks configured and can mint
        // nothing. The subject travels the other way — through the note, not the item.
        const string KeyFile =
            """{"type":"service_account","client_email":"robot@x.iam.gserviceaccount.com","private_key":"PEM"}""";

        var store = new ConfigStore();
        store.Credentials.Add(new CredentialRecord
        {
            Name = "workspace",
            Kind = CredentialKind.GoogleServiceAccount,
            ServiceAccountJson = KeyFile,
            ServiceAccountSubject = "person@example.com",
            Scopes = ["https://www.googleapis.com/auth/gmail.readonly"],
        });

        var vault = InMemoryVault.Empty();
        await vault.SaveAsync(store);

        var credential = Assert.Single((await vault.LoadAsync()).Credentials);
        Assert.Equal(CredentialKind.GoogleServiceAccount, credential.Kind);
        Assert.Equal(KeyFile, credential.ServiceAccountJson);
        Assert.Equal("person@example.com", credential.ServiceAccountSubject);
    }

    [Fact]
    public async Task ATokenWithNoExpiryComesBackWithoutOne()
    {
        // "No expiry advertised" and "expires now" are different states with opposite meanings,
        // and the vault stores an absent field for the first. Substituting a timestamp on read
        // would turn a GitHub token that never ages out into one that expired on every launch.
        var store = new ConfigStore();
        store.Credentials.Add(new CredentialRecord
        {
            Name = "github",
            ClientId = "id",
            ClientSecret = "secret",
            Token = new TokenSet("gho_x", null, null, "Bearer", DateTimeOffset.UtcNow),
        });

        var vault = InMemoryVault.Empty();
        await vault.SaveAsync(store);

        var token = Assert.Single((await vault.LoadAsync()).Credentials).Token;
        Assert.NotNull(token);
        Assert.Null(token!.ExpiresAtUtc);
        Assert.False(token.IsExpiringWithin(TimeSpan.FromDays(365)));
    }

    [Fact]
    public async Task DeletingARecordDeletesItsItem()
    {
        var store = MaximalStore();
        var vault = InMemoryVault.Empty();
        await vault.SaveAsync(store);

        var doomed = store.Credentials[0];
        store.Credentials.Remove(doomed);
        await vault.SaveAsync(store);

        Assert.DoesNotContain(vault.Items, item =>
            VaultItemNaming.TryParse(item.Title, out var role, out var id)
            && role == VaultItemRole.Credential
            && id == doomed.Id);
    }

    [Fact]
    public async Task RenamingARecordRetitlesItsItemRatherThanCreatingASecond()
    {
        var store = MaximalStore();
        var vault = InMemoryVault.Empty();
        await vault.SaveAsync(store);

        var credential = store.Credentials[0];
        credential.Name = "renamed entirely";
        await vault.SaveAsync(store);

        var matching = vault.Items.Where(item =>
            VaultItemNaming.TryParse(item.Title, out var role, out var id)
            && role == VaultItemRole.Credential
            && id == credential.Id).ToList();

        var item = Assert.Single(matching);
        Assert.Contains("renamed entirely", item.Title);
    }

    /// <summary>
    /// Every shape the model supports, so a mapper that handles only the common case fails here.
    /// </summary>
    private static ConfigStore MaximalStore()
    {
        var oauth = new CredentialRecord
        {
            Name = "Google Drive",
            Kind = CredentialKind.OAuth2,
            ClientId = "client-id-123",
            ClientSecret = "client-secret-456",
            Scopes = ["drive.readonly", "drive.metadata"],
            IsGoogleProvider = true,
            Authority = "https://accounts.google.com",
            AuthorizationEndpoint = "https://accounts.google.com/o/oauth2/v2/auth",
            TokenEndpoint = "https://oauth2.googleapis.com/token",
            RequiresIdToken = true,
            UsesPkce = true,
            ExtraAuthParams = "access_type=offline&prompt=consent",
            TestEndpoint = "https://www.googleapis.com/drive/v3/about?fields=user",
            Token = new TokenSet(
                "ACCESS-TOKEN-VALUE",
                "REFRESH-TOKEN-VALUE",
                DateTimeOffset.UtcNow.AddMinutes(42),
                "Bearer",
                DateTimeOffset.UtcNow.AddMinutes(-18)),
        };

        var apiKey = new CredentialRecord
        {
            Name = "Weather API",
            Kind = CredentialKind.ApiKey,
            ApiKey = "STATIC-API-KEY-VALUE",
            DefaultPlacement = CredentialPlacement.Query,
            DefaultParameterName = "appid",
            DefaultValuePrefix = "",
        };

        var upstream = new UpstreamRecord { Name = "google", BaseUrl = "https://www.googleapis.com" };

        var route = new RouteMapping
        {
            PathPrefix = "/gdrive",
            UpstreamId = upstream.Id,
            StripPrefix = false,
            Enabled = true,
            Key = ProxyKey.Generate(TimeSpan.FromDays(30)),
            Credentials =
            [
                RouteCredential.For(oauth.Id, CredentialPlacement.Header),
                RouteCredential.For(apiKey.Id, CredentialPlacement.Query),
                new RouteCredential
                {
                    CredentialId = oauth.Id,
                    Placement = CredentialPlacement.Body,
                    ParameterName = "auth_token",
                    ValuePrefix = "token ",
                },
            ],
        };

        var source = new McpSourceRecord
        {
            Name = "github",
            Alias = "gh",
            Kind = McpSourceKind.RemoteUrl,
            Url = "https://example.com/mcp",
            Transport = McpTransportPreference.Sse,
            Enabled = true,
        };

        var funnel = new McpFunnelRecord
        {
            Name = "coding agent",
            Slug = "coding-agent",
            Enabled = true,
            Key = ProxyKey.Generate(),
            Sources =
            [
                new McpFunnelSource
                {
                    SourceId = source.Id,
                    ToolMode = McpSelectionMode.Include,
                    Tools = ["create_issue", "list_issues"],
                    ResourceMode = McpSelectionMode.Exclude,
                    Resources = ["secret-resource"],
                    PromptMode = McpSelectionMode.All,
                },
            ],
        };

        var store = new ConfigStore
        {
            Settings = { ListenPort = 5610, McpFunnelEnabled = true },
        };

        store.Credentials.AddRange([oauth, apiKey]);
        store.Upstreams.Add(upstream);
        store.Routes.Add(route);
        store.McpSources.Add(source);
        store.McpFunnels.Add(funnel);

        return store;
    }

    private static void AssertSameStore(ConfigStore expected, ConfigStore actual)
    {
        Assert.Equal(expected.Settings.ListenPort, actual.Settings.ListenPort);

        Assert.Equal(expected.Settings.McpFunnelEnabled, actual.Settings.McpFunnelEnabled);

        Assert.Equal(expected.Credentials.Count, actual.Credentials.Count);
        foreach (var (want, got) in expected.Credentials.Zip(actual.Credentials))
        {
            Assert.Equal(want.Id, got.Id);
            Assert.Equal(want.Name, got.Name);
            Assert.Equal(want.Kind, got.Kind);
            Assert.Equal(want.ClientId, got.ClientId);
            Assert.Equal(want.ClientSecret, got.ClientSecret);
            Assert.Equal(want.Scopes, got.Scopes);
            Assert.Equal(want.IsGoogleProvider, got.IsGoogleProvider);
            Assert.Equal(want.Authority, got.Authority);
            Assert.Equal(want.AuthorizationEndpoint, got.AuthorizationEndpoint);
            Assert.Equal(want.TokenEndpoint, got.TokenEndpoint);
            Assert.Equal(want.RequiresIdToken, got.RequiresIdToken);
            Assert.Equal(want.UsesPkce, got.UsesPkce);
            Assert.Equal(want.ExtraAuthParams, got.ExtraAuthParams);
            Assert.Equal(want.ApiKey, got.ApiKey);
            Assert.Equal(want.DefaultPlacement, got.DefaultPlacement);
            Assert.Equal(want.DefaultParameterName, got.DefaultParameterName);
            Assert.Equal(want.DefaultValuePrefix, got.DefaultValuePrefix);
            Assert.Equal(want.TestEndpoint, got.TestEndpoint);

            if (want.Token is null)
            {
                Assert.Null(got.Token);
                continue;
            }

            Assert.NotNull(got.Token);
            Assert.Equal(want.Token.AccessToken, got.Token.AccessToken);
            Assert.Equal(want.Token.RefreshToken, got.Token.RefreshToken);
            Assert.Equal(want.Token.TokenType, got.Token.TokenType);
            AssertCloseEnough(want.Token.ExpiresAtUtc, got.Token.ExpiresAtUtc);
            AssertCloseEnough(want.Token.ObtainedUtc, got.Token.ObtainedUtc);
        }

        Assert.Equal(expected.Upstreams.Count, actual.Upstreams.Count);
        foreach (var (want, got) in expected.Upstreams.Zip(actual.Upstreams))
        {
            Assert.Equal(want.Id, got.Id);
            Assert.Equal(want.Name, got.Name);
            Assert.Equal(want.BaseUrl, got.BaseUrl);
        }

        Assert.Equal(expected.Routes.Count, actual.Routes.Count);
        foreach (var (want, got) in expected.Routes.Zip(actual.Routes))
        {
            Assert.Equal(want.Id, got.Id);
            Assert.Equal(want.PathPrefix, got.PathPrefix);
            Assert.Equal(want.UpstreamId, got.UpstreamId);
            Assert.Equal(want.StripPrefix, got.StripPrefix);
            Assert.Equal(want.Enabled, got.Enabled);
            Assert.Equal(want.Key.Value, got.Key.Value);
            AssertCloseEnough(want.Key.CreatedUtc, got.Key.CreatedUtc);
            Assert.Equal(want.Key.ExpiresUtc is null, got.Key.ExpiresUtc is null);

            // Order matters: two credentials in the same placement are applied in list order, so
            // a reordering silently changes which one wins.
            Assert.Equal(
                want.Credentials.Select(c => (c.CredentialId, c.Placement, c.ParameterName, c.ValuePrefix)),
                got.Credentials.Select(c => (c.CredentialId, c.Placement, c.ParameterName, c.ValuePrefix)));
        }

        Assert.Equal(expected.McpSources.Count, actual.McpSources.Count);
        foreach (var (want, got) in expected.McpSources.Zip(actual.McpSources))
        {
            Assert.Equal(want.Id, got.Id);
            Assert.Equal(want.Name, got.Name);
            Assert.Equal(want.Alias, got.Alias);
            Assert.Equal(want.Kind, got.Kind);
            Assert.Equal(want.Url, got.Url);
            Assert.Equal(want.Transport, got.Transport);
            Assert.Equal(want.Enabled, got.Enabled);
        }

        Assert.Equal(expected.McpFunnels.Count, actual.McpFunnels.Count);
        foreach (var (want, got) in expected.McpFunnels.Zip(actual.McpFunnels))
        {
            Assert.Equal(want.Id, got.Id);
            Assert.Equal(want.Name, got.Name);
            Assert.Equal(want.Slug, got.Slug);
            Assert.Equal(want.Enabled, got.Enabled);
            Assert.Equal(want.Key.Value, got.Key.Value);

            Assert.Equal(want.Sources.Count, got.Sources.Count);
            foreach (var (wantSource, gotSource) in want.Sources.Zip(got.Sources))
            {
                Assert.Equal(wantSource.SourceId, gotSource.SourceId);
                Assert.Equal(wantSource.ToolMode, gotSource.ToolMode);
                Assert.Equal(wantSource.Tools, gotSource.Tools);
                Assert.Equal(wantSource.ResourceMode, gotSource.ResourceMode);
                Assert.Equal(wantSource.Resources, gotSource.Resources);
                Assert.Equal(wantSource.PromptMode, gotSource.PromptMode);
            }
        }
    }

    /// <summary>
    /// Timestamps go through a text field, so equality is to the second rather than the tick —
    /// what matters is that the instant survives, not that the sub-millisecond precision does.
    /// </summary>
    private static void AssertCloseEnough(DateTimeOffset expected, DateTimeOffset actual) =>
        Assert.True((expected - actual).Duration() < TimeSpan.FromSeconds(1),
            $"expected {expected:O} but got {actual:O}");

    /// <summary>
    /// Same check for an optional timestamp. "Absent" has to round-trip as absent: a token whose
    /// provider advertised no expiry must not come back holding one, or a credential that never
    /// expires would be treated as long expired on the next launch.
    /// </summary>
    private static void AssertCloseEnough(DateTimeOffset? expected, DateTimeOffset? actual)
    {
        Assert.Equal(expected is null, actual is null);
        if (expected is { } want && actual is { } got) AssertCloseEnough(want, got);
    }
}
