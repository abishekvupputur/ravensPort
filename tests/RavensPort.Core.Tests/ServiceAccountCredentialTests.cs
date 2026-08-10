using RavensPort.Core.Models;

namespace RavensPort.Core.Tests;

/// <summary>
/// Reading a Google service account key file, and the rules the editor applies before one is
/// saved.
///
/// The parsing matters more than it looks. Everything a user might paste here is plausible JSON —
/// an OAuth client secret file, an authorized-user file, a key file with the private key trimmed
/// out — and accepting one of those stores a credential that fails much later, inside a token
/// request, with a message about a missing key rather than about the wrong file.
/// </summary>
public class ServiceAccountCredentialTests
{
    /// <summary>
    /// Shaped exactly like a real downloaded key, private key included. The value is not a usable
    /// RSA key and does not need to be: nothing here signs anything, and a real one in a source
    /// file is a secret in a repository.
    /// </summary>
    private const string KeyFile = """
        {
          "type": "service_account",
          "project_id": "example-project",
          "private_key_id": "abc123",
          "private_key": "-----BEGIN PRIVATE KEY-----\nnot-a-real-key\n-----END PRIVATE KEY-----\n",
          "client_email": "robot@example-project.iam.gserviceaccount.com",
          "client_id": "10987654321",
          "token_uri": "https://oauth2.googleapis.com/token"
        }
        """;

    private static readonly List<string> Scopes = ["https://www.googleapis.com/auth/drive.readonly"];

    // ---- Parsing ---------------------------------------------------------------------------------

    [Fact]
    public void ARealKeyFileParsesIntoTheFourFieldsTheAppNeeds()
    {
        var key = GoogleServiceAccountKey.TryParse(KeyFile, out var error);

        Assert.Null(error);
        Assert.NotNull(key);
        Assert.Equal("robot@example-project.iam.gserviceaccount.com", key!.ClientEmail);
        Assert.Equal("abc123", key.PrivateKeyId);
        Assert.Equal("https://oauth2.googleapis.com/token", key.TokenUri);
        Assert.Contains("BEGIN PRIVATE KEY", key.PrivateKey);
    }

    [Fact]
    public void AKeyFileWithoutATokenUriFallsBackToGooglesOwn()
    {
        // Every file the console produces has one; a hand-trimmed file should still work rather
        // than failing with a null URL somewhere inside the client library.
        var key = GoogleServiceAccountKey.TryParse(
            """
            {
              "type": "service_account",
              "private_key": "-----BEGIN PRIVATE KEY-----\nx\n-----END PRIVATE KEY-----\n",
              "client_email": "robot@example.iam.gserviceaccount.com"
            }
            """, out var error);

        Assert.Null(error);
        Assert.Equal(GoogleServiceAccountKey.DefaultTokenUri, key!.TokenUri);
    }

    [Fact]
    public void AnOAuthClientSecretFileIsNamedAsSuchRatherThanCalledMalformed()
    {
        // By far the likeliest wrong file: it is what the OAuth2 credential type asks for, so it
        // is already open in the same downloads folder.
        var key = GoogleServiceAccountKey.TryParse(
            """{"installed":{"client_id":"x.apps.googleusercontent.com","client_secret":"y"}}""", out var error);

        Assert.Null(key);
        Assert.Contains("OAuth client secret", error);
        Assert.Contains("OAuth2 credential type", error);
    }

    [Fact]
    public void AKeyFileOfTheWrongTypeSaysWhichTypeItIs()
    {
        var key = GoogleServiceAccountKey.TryParse(
            """{"type":"authorized_user","refresh_token":"x"}""", out var error);

        Assert.Null(key);
        Assert.Contains("authorized_user", error);
    }

    [Fact]
    public void SomethingThatIsNotJsonIsRejectedWithoutThrowing()
    {
        Assert.Null(GoogleServiceAccountKey.TryParse("not json at all", out var error));
        Assert.Contains("not valid JSON", error);
    }

    [Fact]
    public void AnEmptyKeyIsRejected()
    {
        Assert.Null(GoogleServiceAccountKey.TryParse("   ", out var error));
        Assert.Contains("required", error);
    }

    [Fact]
    public void AKeyFileMissingItsPrivateKeyIsRejected()
    {
        var key = GoogleServiceAccountKey.TryParse(
            """{"type":"service_account","client_email":"robot@example.iam.gserviceaccount.com"}""", out var error);

        Assert.Null(key);
        Assert.Contains("private_key", error);
    }

    // ---- The editor's rules ----------------------------------------------------------------------

    [Fact]
    public void AGoodKeyWithScopesPasses() =>
        Assert.Null(CredentialValidation.ValidateServiceAccount(KeyFile, Scopes, subject: null));

    [Fact]
    public void ScopesAreRequired()
    {
        // Google issues a token for a scopeless assertion without complaint, and every API then
        // rejects it — which reads as a permissions problem rather than a configuration one.
        var error = CredentialValidation.ValidateServiceAccount(KeyFile, [], subject: null);

        Assert.NotNull(error);
        Assert.Contains("scope", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheImpersonatedUserMayNotCarryStrayWhitespace()
    {
        // It becomes a claim in a signed JWT, so a line break picked up while copying is signed
        // along with everything else.
        Assert.NotNull(CredentialValidation.ValidateServiceAccount(KeyFile, Scopes, "user@example.com\n"));
        Assert.Null(CredentialValidation.ValidateServiceAccount(KeyFile, Scopes, "user@example.com"));
    }

    [Fact]
    public void ARecordIsValidatedThroughTheSameRules()
    {
        var record = new CredentialRecord
        {
            Name = "workspace",
            Kind = CredentialKind.GoogleServiceAccount,
            ServiceAccountJson = KeyFile,
            Scopes = Scopes,
        };

        Assert.Null(CredentialValidation.Validate(record));

        record.Scopes = [];
        Assert.NotNull(CredentialValidation.Validate(record));
    }

    // ---- Client credentials ----------------------------------------------------------------------

    [Fact]
    public void AClientCredentialsGrantNeedsAnIdASecretAndATokenEndpoint()
    {
        Assert.Contains("Client ID", CredentialValidation.ValidateClientCredentials(
            "", true, "https://example.com/token"));

        Assert.Contains("Client secret", CredentialValidation.ValidateClientCredentials(
            "app", false, "https://example.com/token"));

        Assert.Contains("Token endpoint", CredentialValidation.ValidateClientCredentials(
            "app", true, ""));

        Assert.Null(CredentialValidation.ValidateClientCredentials("app", true, "https://example.com/token"));
    }

    [Fact]
    public void TheTokenEndpointIsHeldToTheSameTransportRuleAsEveryOtherEndpoint()
    {
        // The client secret is sent there. Plain http off localhost would put it on the wire.
        Assert.NotNull(CredentialValidation.ValidateClientCredentials("app", true, "http://example.com/token"));
        Assert.Null(CredentialValidation.ValidateClientCredentials("app", true, "http://localhost:9000/token"));
    }

    // ---- What each kind counts as configured -----------------------------------------------------

    [Fact]
    public void AnAppLoginIsConfiguredByItsStoredSecretRatherThanByHavingATokenYet()
    {
        // The token is a derivative these kinds can re-mint at will, so "no token yet" is not the
        // same as "not set up" — reporting it as such made a working credential look broken.
        var serviceAccount = new CredentialRecord
        {
            Name = "sa",
            Kind = CredentialKind.GoogleServiceAccount,
            ServiceAccountJson = KeyFile,
        };

        var clientCredentials = new CredentialRecord
        {
            Name = "cc",
            Kind = CredentialKind.ClientCredentials,
            ClientId = "app",
            ClientSecret = "s3cret",
        };

        Assert.True(serviceAccount.HasSecret);
        Assert.True(clientCredentials.HasSecret);
        Assert.True(serviceAccount.IsSelfIssuing);
        Assert.True(clientCredentials.IsSelfIssuing);
        Assert.False(serviceAccount.IsInteractiveOAuth);

        Assert.False(new CredentialRecord { Name = "sa", Kind = CredentialKind.GoogleServiceAccount }.HasSecret);
        Assert.False(new CredentialRecord { Name = "cc", Kind = CredentialKind.ClientCredentials }.HasSecret);

        // The browser grant is unchanged: for it, the token really is the thing it holds.
        Assert.False(new CredentialRecord { Name = "o", ClientSecret = "x" }.IsSelfIssuing);
    }

    // ---- Tokens with no advertised expiry --------------------------------------------------------

    [Fact]
    public void ATokenWithNoAdvertisedExpiryNeverCountsAsExpiring()
    {
        // A GitHub OAuth App token is the real case: no expires_in, no refresh token, and it does
        // not age out. Recorded as an expiry of "now" it showed as permanently expired.
        var token = new TokenSet("gho_x", null, null, "Bearer", DateTimeOffset.UtcNow);

        Assert.False(token.IsExpiringWithin(TimeSpan.Zero));
        Assert.False(token.IsExpiringWithin(TimeSpan.FromDays(3650)));
        Assert.Equal("no expiry advertised", token.DescribeExpiry());
    }

    [Fact]
    public void ATokenWithAnExpiryStillReportsIt()
    {
        var token = new TokenSet("x", null, DateTimeOffset.UtcNow.AddMinutes(5), "Bearer", DateTimeOffset.UtcNow);

        Assert.False(token.IsExpiringWithin(TimeSpan.FromMinutes(1)));
        Assert.True(token.IsExpiringWithin(TimeSpan.FromMinutes(10)));
    }
}
