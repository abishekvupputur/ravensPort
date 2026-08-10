using RavensPort.Core.Models;
using RavensPort.Core.Vault;

namespace RavensPort.Core.Tests.Vault;

/// <summary>
/// The topology note holds no secrets.
///
/// The whole point of the hybrid layout is that secrets sit in their own items where the password
/// manager conceals them. A secret that also leaked into the note would be visible in plain text
/// to anyone who opened it, and would be a second copy nothing keeps in step. This is the direct
/// descendant of the old "the encrypted file must not contain the plaintext secret" test.
/// </summary>
public class VaultRedactionTests
{
    private const string ClientSecret = "SENTINEL-CLIENT-SECRET";
    private const string ApiKey = "SENTINEL-API-KEY";
    private const string AccessToken = "SENTINEL-ACCESS-TOKEN";
    private const string RefreshToken = "SENTINEL-REFRESH-TOKEN";
    private const string RouteKey = "SENTINEL-ROUTE-KEY";
    private const string FunnelKey = "SENTINEL-FUNNEL-KEY";

    /// <summary>Stands in for the private key inside a Google service account key file.</summary>
    private const string ServiceAccountKey =
        """{"type":"service_account","client_email":"robot@x.iam.gserviceaccount.com","private_key":"SENTINEL-PRIVATE-KEY"}""";

    [Fact]
    public void TheConfigNoteContainsNoSecret()
    {
        var note = VaultMapper.BuildConfigNote(StoreStuffedWithSentinels(), new VaultIndex(), revision: 1);
        var text = note.Field(VaultFields.NoteContent)!;

        foreach (var secret in new[] { ClientSecret, ApiKey, AccessToken, RefreshToken, RouteKey, FunnelKey, ServiceAccountKey })
        {
            Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheConfigNoteStillContainsTheNonSecretConfiguration()
    {
        // Redaction that took the topology with it would be safe and useless. The note is what
        // reconstructs routes, scopes, and endpoints on load.
        var note = VaultMapper.BuildConfigNote(StoreStuffedWithSentinels(), new VaultIndex(), revision: 1);
        var text = note.Field(VaultFields.NoteContent)!;

        Assert.Contains("/gdrive", text, StringComparison.Ordinal);
        Assert.Contains("https://accounts.google.com", text, StringComparison.Ordinal);
        Assert.Contains("drive.readonly", text, StringComparison.Ordinal);
        Assert.Contains("coding-agent", text, StringComparison.Ordinal);

        // The impersonated user is configuration, not a secret. Redacting it would lose which
        // person a Workspace credential acts as, and there would be nowhere else to find it.
        Assert.Contains("person@example.com", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AProxyKeysExpiryStaysInTheNoteEvenThoughItsValueDoesNot()
    {
        // Expiry is policy, not secret. Keeping it in the note means a key item that is lost and
        // reissued comes back with the same lifetime rather than silently becoming permanent.
        var store = StoreStuffedWithSentinels();
        store.Routes[0].Key.SetLifetime(TimeSpan.FromDays(30));

        var text = VaultMapper.BuildConfigNote(store, new VaultIndex(), revision: 1)
            .Field(VaultFields.NoteContent)!;

        Assert.DoesNotContain(RouteKey, text, StringComparison.Ordinal);
        Assert.Contains("ExpiresUtc", text, StringComparison.Ordinal);
    }

    [Fact]
    public void EverySecretIsCarriedByAConcealedFieldOnItsOwnItem()
    {
        // The other half of the contract: redacted from the note *and* present in an item, marked
        // concealed. A secret written as a visible field would defeat the manager's own masking.
        var items = VaultMapper.BuildSecretItems(StoreStuffedWithSentinels(), new VaultIndex());

        var everyValue = items.SelectMany(i => i.Spec.Fields).ToList();

        foreach (var secret in new[] { ClientSecret, ApiKey, AccessToken, RefreshToken, RouteKey, FunnelKey, ServiceAccountKey })
        {
            var field = Assert.Single(everyValue, f => f.Value == secret);
            Assert.True(field.Concealed, $"'{field.Name}' carries a secret but is not concealed");
        }
    }

    private static ConfigStore StoreStuffedWithSentinels()
    {
        var oauth = new CredentialRecord
        {
            Name = "Google Drive",
            ClientId = "client-id",
            ClientSecret = ClientSecret,
            Scopes = ["drive.readonly"],
            Authority = "https://accounts.google.com",
            Token = new TokenSet(AccessToken, RefreshToken, DateTimeOffset.UtcNow.AddHours(1), "Bearer", DateTimeOffset.UtcNow),
        };

        var apiKeyCredential = new CredentialRecord
        {
            Name = "Weather",
            Kind = CredentialKind.ApiKey,
            ApiKey = ApiKey,
        };

        var serviceAccount = new CredentialRecord
        {
            Name = "Workspace",
            Kind = CredentialKind.GoogleServiceAccount,
            ServiceAccountJson = ServiceAccountKey,
            // Not a secret, and deliberately kept in the note: it is an email address, and the
            // note is the one home for configuration.
            ServiceAccountSubject = "person@example.com",
            Scopes = ["https://www.googleapis.com/auth/gmail.readonly"],
        };

        var upstream = new UpstreamRecord { Name = "google", BaseUrl = "https://www.googleapis.com" };

        var store = new ConfigStore();
        store.Credentials.AddRange([oauth, apiKeyCredential, serviceAccount]);
        store.Upstreams.Add(upstream);
        store.Routes.Add(new RouteMapping
        {
            PathPrefix = "/gdrive",
            UpstreamId = upstream.Id,
            Key = new ProxyKey { Value = RouteKey },
            Credentials = [RouteCredential.For(oauth.Id, CredentialPlacement.Header)],
        });
        store.McpFunnels.Add(new McpFunnelRecord
        {
            Name = "coding agent",
            Slug = "coding-agent",
            Key = new ProxyKey { Value = FunnelKey },
        });

        return store;
    }
}
