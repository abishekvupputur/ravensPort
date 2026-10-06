using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using RavensPort.Core.Auth;
using RavensPort.Core.Diagnostics;
using RavensPort.Core.Models;
using RavensPort.Core.Tests.Mcp;

namespace RavensPort.Core.Tests.Auth;

/// <summary>
/// Minting a token for a Google service account, against a token endpoint on loopback.
///
/// The key file names its own token endpoint, so pointing <c>token_uri</c> at a local server is all
/// it takes to run the real signing path end to end: the Google library builds and signs the JWT
/// assertion with a key generated for the test, posts it, and parses whatever comes back. Nothing
/// here talks to Google, and no key in this file has ever been anywhere else.
/// </summary>
public sealed class GoogleServiceAccountServiceTests : IAsyncLifetime
{
    private static readonly List<string> DriveScope = ["https://www.googleapis.com/auth/drive.readonly"];

    private readonly string _logDir = Path.Combine(Path.GetTempPath(), "ravensport-sa-logs-" + Guid.NewGuid());
    private FakeRestApi _tokenEndpoint = null!;

    public async Task InitializeAsync() => _tokenEndpoint = await FakeRestApi.StartAsync();

    public async Task DisposeAsync()
    {
        await _tokenEndpoint.DisposeAsync();
        if (Directory.Exists(_logDir)) Directory.Delete(_logDir, recursive: true);
    }

    private GoogleServiceAccountService NewService() => new(new ActivityLog(_logDir));

    /// <summary>A key file with a freshly generated private key, naming the given token endpoint.</summary>
    private static string KeyFile(string tokenUri)
    {
        using var rsa = RSA.Create(2048);

        return JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["type"] = "service_account",
            ["project_id"] = "example-project",
            ["private_key_id"] = "test-key-1",
            ["private_key"] = rsa.ExportPkcs8PrivateKeyPem(),
            ["client_email"] = "robot@example-project.iam.gserviceaccount.com",
            ["client_id"] = "10987654321",
            ["token_uri"] = tokenUri,
        });
    }

    private CredentialRecord Credential(string? subject = null, List<string>? scopes = null) => new()
    {
        Name = "robot",
        Kind = CredentialKind.GoogleServiceAccount,
        ServiceAccountJson = KeyFile($"{_tokenEndpoint.Url}/token"),
        ServiceAccountSubject = subject,
        Scopes = scopes ?? DriveScope,
    };

    /// <summary>
    /// A token the endpoint issues is stored with its expiry, and the request that got it was a
    /// signed JWT bearer grant â€” not a client secret, which a service account does not have.
    /// </summary>
    [Fact]
    public async Task ATokenIsMintedFromASignedAssertionAndStoredWithItsExpiry()
    {
        _tokenEndpoint.ResponseBody = """{"access_token":"ya29.minted","expires_in":3600,"token_type":"Bearer"}""";

        var credential = Credential(subject: "  someone@example.com  ");
        credential.NeedsReconnect = true;

        var outcome = await NewService().AcquireAsync(credential);

        Assert.True(outcome.Success, $"{outcome.Error}: {outcome.ErrorDescription}");
        Assert.False(credential.NeedsReconnect);
        Assert.Equal("ya29.minted", credential.Token?.AccessToken);
        Assert.Null(credential.Token?.RefreshToken);

        var expiry = Assert.NotNull(credential.Token?.ExpiresAtUtc);
        Assert.InRange(expiry - DateTimeOffset.UtcNow, TimeSpan.FromMinutes(55), TimeSpan.FromMinutes(61));

        var request = _tokenEndpoint.Single();
        Assert.Equal("POST", request.Method);
        Assert.Contains("grant_type=urn%3Aietf%3Aparams%3Aoauth%3Agrant-type%3Ajwt-bearer", request.Body, StringComparison.Ordinal);
        Assert.Contains("assertion=", request.Body, StringComparison.Ordinal);
    }

    /// <summary>
    /// Google refusing the assertion is an outcome naming Google's own error, and the credential is
    /// marked as needing attention rather than left looking healthy.
    /// </summary>
    [Fact]
    public async Task ARefusalFromTheTokenEndpointIsReportedWithItsReason()
    {
        _tokenEndpoint.StatusCode = StatusCodes.Status400BadRequest;
        _tokenEndpoint.ResponseBody = """{"error":"invalid_grant","error_description":"Invalid JWT Signature."}""";

        var credential = Credential();

        var outcome = await NewService().AcquireAsync(credential);

        Assert.False(outcome.Success);
        Assert.Equal("invalid_grant", outcome.Error);
        Assert.Equal("Invalid JWT Signature.", outcome.ErrorDescription);
        Assert.True(credential.NeedsReconnect);
        Assert.Null(credential.Token);
    }

    /// <summary>An endpoint that cannot be reached is a failed outcome, not an exception.</summary>
    [Fact]
    public async Task AnUnreachableTokenEndpointIsAFailedOutcome()
    {
        var credential = Credential();
        credential.ServiceAccountJson = KeyFile("http://127.0.0.1:1/token");

        var outcome = await NewService().AcquireAsync(credential);

        Assert.False(outcome.Success);
        Assert.Equal("service_account_error", outcome.Error);
        Assert.True(credential.NeedsReconnect);
    }

    /// <summary>
    /// A key file that does not parse, and a credential with no scopes, are both refused before
    /// anything is signed or sent â€” the second because Google would issue a token every API then
    /// rejects, which is much harder to diagnose than a refusal here.
    /// </summary>
    [Fact]
    public async Task ABadKeyFileOrMissingScopesAreRefusedBeforeAnythingIsSent()
    {
        var service = NewService();

        var unparseable = Credential();
        unparseable.ServiceAccountJson = "{ not json";

        var badKey = await service.AcquireAsync(unparseable);
        Assert.Equal("invalid_key_file", badKey.Error);
        Assert.True(unparseable.NeedsReconnect);

        var unscoped = Credential(scopes: []);

        var noScopes = await service.AcquireAsync(unscoped);
        Assert.Equal("missing_scopes", noScopes.Error);
        Assert.True(unscoped.NeedsReconnect);

        Assert.Empty(_tokenEndpoint.Received);
    }
}
