using RavensPort.Core.Models;
using RavensPort.UI.ViewModels;

namespace RavensPort.UI.Tests;

/// <summary>
/// The Credentials tab's form, asked without a window.
///
/// The view tests already prove the form adds each kind. What they do not reach is the other half
/// of every kind: opening an existing credential, changing it, and saving it back â€” where blank
/// secret boxes mean "keep the stored one" rather than "clear it", and where a careless edit could
/// leave a record describing a provider it no longer talks to. Each test here is one kind's
/// round trip, asserted against the stored record rather than against the form.
/// </summary>
public class CredentialsViewModelUnitTests
{
    /// <summary>
    /// Deliberately unrealistic, for the reason ServiceAccountCredentialTests gives: the parser only
    /// asks whether the fields are there, and nothing here signs anything.
    /// </summary>
    private const string KeyFile = """
        {
          "type": "service_account",
          "project_id": "example-project",
          "private_key_id": "abc123",
          "private_key": "placeholder-not-a-real-key",
          "client_email": "robot@example-project.iam.gserviceaccount.com",
          "client_id": "10987654321",
          "token_uri": "https://oauth2.googleapis.com/token"
        }
        """;

    /// <summary>A port nothing listens on, so a token request fails at once rather than hanging.</summary>
    private const string Unreachable = "http://127.0.0.1:1/token";

    private static async Task<(ViewModelFixture Fixture, CredentialsViewModel Credentials)> StartAsync()
    {
        var fixture = new ViewModelFixture();
        await fixture.StartSingleUseAsync();

        var credentials = fixture.Get<CredentialsViewModel>();
        credentials.Reload();

        return (fixture, credentials);
    }

    private static CredentialRecord Stored(ViewModelFixture fixture, string name) =>
        fixture.Store.Current.Credentials.Single(c => c.Name == name);

    private static CredentialItemViewModel Row(CredentialsViewModel credentials, string name) =>
        credentials.Credentials.Single(c => c.Name == name);

    /// <summary>
    /// An OAuth credential opened for editing keeps its secret when the box is left blank, and a
    /// rename lands on the same record rather than adding a second one.
    /// </summary>
    [Fact]
    public async Task AnOAuthCredentialIsEditedInPlaceAndKeepsItsSecret()
    {
        var (fixture, credentials) = await StartAsync();
        using var _ = fixture;

        credentials.SelectedKind = CredentialKind.OAuth2;
        credentials.SelectedPreset = OAuthProviderPreset.Google;
        credentials.NewName = "google";
        credentials.NewClientId = "client-1";
        credentials.NewClientSecret = "secret-1";
        credentials.NewScopes = "openid email";

        await credentials.SaveCredentialCommand.ExecuteAsync(null);
        Assert.Equal("secret-1", Stored(fixture, "google").ClientSecret);

        credentials.EditCredentialCommand.Execute(Row(credentials, "google"));
        Assert.True(credentials.IsEditing);
        Assert.Equal("Leave Client secret blank to keep the current one.", credentials.StatusMessage);
        Assert.Equal(OAuthProviderPreset.Google, credentials.SelectedPreset);

        credentials.NewName = "google (work)";
        credentials.NewClientId = "client-2";
        await credentials.SaveCredentialCommand.ExecuteAsync(null);

        Assert.Equal("Saved changes to 'google (work)'.", credentials.StatusMessage);
        Assert.False(credentials.IsEditing);

        var record = Assert.Single(fixture.Store.Current.Credentials);
        Assert.Equal("client-2", record.ClientId);
        Assert.Equal("secret-1", record.ClientSecret);
        Assert.True(record.IsGoogleProvider);
    }

    /// <summary>
    /// A service account keeps its key file when the box is left blank on edit, and a key file
    /// that does not parse is refused with the parser's reason.
    /// </summary>
    [Fact]
    public async Task AServiceAccountIsEditedWithoutReplacingItsKey()
    {
        var (fixture, credentials) = await StartAsync();
        using var _ = fixture;

        credentials.SelectedKind = CredentialKind.GoogleServiceAccount;

        await credentials.SaveCredentialCommand.ExecuteAsync(null);
        Assert.Equal("Name is required.", credentials.StatusMessage);

        credentials.NewName = "robot";
        credentials.NewServiceAccountJson = "{ not json";
        credentials.NewScopes = "https://www.googleapis.com/auth/drive.readonly";
        await credentials.SaveCredentialCommand.ExecuteAsync(null);
        Assert.Empty(fixture.Store.Current.Credentials);

        credentials.NewServiceAccountJson = KeyFile;
        credentials.NewDefaultPlacement = CredentialPlacement.Query;
        credentials.NewDefaultParameterName = "";
        await credentials.SaveCredentialCommand.ExecuteAsync(null);
        Assert.Empty(fixture.Store.Current.Credentials);

        credentials.NewDefaultPlacement = CredentialPlacement.Header;
        credentials.NewDefaultParameterName = "Authorization";
        await credentials.SaveCredentialCommand.ExecuteAsync(null);
        Assert.Single(fixture.Store.Current.Credentials);

        credentials.EditCredentialCommand.Execute(Row(credentials, "robot"));
        Assert.Equal("Leave the key file blank to keep the current one.", credentials.StatusMessage);
        Assert.Equal("", credentials.NewServiceAccountJson);

        credentials.NewName = "robot (impersonating)";
        credentials.NewServiceAccountSubject = "someone@example.com";
        await credentials.SaveCredentialCommand.ExecuteAsync(null);

        Assert.Equal("Saved changes to 'robot (impersonating)'.", credentials.StatusMessage);

        var record = Stored(fixture, "robot (impersonating)");
        Assert.Equal(KeyFile.Trim(), record.ServiceAccountJson);
        Assert.Equal("someone@example.com", record.ServiceAccountSubject);
    }

    /// <summary>
    /// Client credentials: the name is required, an edit keeps the secret, and an edit clears the
    /// authorization-code fields a record carried over from being another kind.
    /// </summary>
    [Fact]
    public async Task ClientCredentialsAreEditedInPlaceAndKeepTheirSecret()
    {
        var (fixture, credentials) = await StartAsync();
        using var _ = fixture;

        credentials.SelectedKind = CredentialKind.ClientCredentials;

        await credentials.SaveCredentialCommand.ExecuteAsync(null);
        Assert.Equal("Name is required.", credentials.StatusMessage);

        credentials.NewName = "machine";
        credentials.NewClientId = "app-1";
        await credentials.SaveCredentialCommand.ExecuteAsync(null);
        Assert.Empty(fixture.Store.Current.Credentials);

        credentials.NewClientSecret = "s3cret";
        credentials.NewTokenEndpoint = "https://login.example.com/token";
        credentials.NewExtraParams = "audience=api";
        await credentials.SaveCredentialCommand.ExecuteAsync(null);
        Assert.Single(fixture.Store.Current.Credentials);

        credentials.EditCredentialCommand.Execute(Row(credentials, "machine"));
        Assert.Equal("https://login.example.com/token", credentials.NewTokenEndpoint);
        Assert.Equal("audience=api", credentials.NewExtraParams);

        credentials.NewName = "machine (body auth)";
        credentials.NewSendClientCredentialsInBody = true;
        await credentials.SaveCredentialCommand.ExecuteAsync(null);

        Assert.Equal("Saved changes to 'machine (body auth)'.", credentials.StatusMessage);

        var record = Stored(fixture, "machine (body auth)");
        Assert.Equal("s3cret", record.ClientSecret);
        Assert.True(record.SendClientCredentialsInBody);
        Assert.Null(record.AuthorizationEndpoint);
        Assert.False(record.IsGoogleProvider);
    }

    /// <summary>
    /// Device code: the name is required, a missing endpoint is refused, and an edit keeps the
    /// optional secret and matches the record back to its provider preset.
    /// </summary>
    [Fact]
    public async Task ADeviceCodeCredentialIsEditedInPlace()
    {
        var (fixture, credentials) = await StartAsync();
        using var _ = fixture;

        credentials.SelectedKind = CredentialKind.DeviceCode;
        credentials.SelectedPreset = OAuthProviderPreset.Custom;

        await credentials.SaveCredentialCommand.ExecuteAsync(null);
        Assert.Equal("Name is required.", credentials.StatusMessage);

        credentials.NewName = "tv";
        credentials.NewClientId = "device-app";
        credentials.NewDeviceAuthorizationEndpoint = "";
        credentials.NewTokenEndpoint = "";
        await credentials.SaveCredentialCommand.ExecuteAsync(null);
        Assert.Empty(fixture.Store.Current.Credentials);

        credentials.NewDeviceAuthorizationEndpoint = "https://login.example.com/device";
        credentials.NewTokenEndpoint = "https://login.example.com/token";
        credentials.NewClientSecret = "optional";
        await credentials.SaveCredentialCommand.ExecuteAsync(null);
        Assert.Single(fixture.Store.Current.Credentials);

        credentials.EditCredentialCommand.Execute(Row(credentials, "tv"));
        Assert.Equal("https://login.example.com/device", credentials.NewDeviceAuthorizationEndpoint);
        Assert.Equal(OAuthProviderPreset.Custom, credentials.SelectedPreset);

        credentials.NewName = "tv (living room)";
        await credentials.SaveCredentialCommand.ExecuteAsync(null);

        Assert.Equal("Saved changes to 'tv (living room)'.", credentials.StatusMessage);
        Assert.Equal("optional", Stored(fixture, "tv (living room)").ClientSecret);
    }

    /// <summary>
    /// Token exchange, in API-key mode: refused until it has an endpoint and a key, added once it
    /// has both, and edited without the key being cleared by an empty box.
    /// </summary>
    [Fact]
    public async Task ATokenExchangeCredentialWithAnApiKeyIsAddedAndEdited()
    {
        var (fixture, credentials) = await StartAsync();
        using var _ = fixture;

        credentials.SelectedKind = CredentialKind.TokenExchange;

        await credentials.SaveCredentialCommand.ExecuteAsync(null);
        Assert.Equal("Name is required.", credentials.StatusMessage);

        credentials.NewName = "exchange";
        await credentials.SaveCredentialCommand.ExecuteAsync(null);
        Assert.StartsWith("Exchange endpoint is required", credentials.StatusMessage);

        credentials.NewExchangeEndpoint = "https://auth.example.com/exchange";
        await credentials.SaveCredentialCommand.ExecuteAsync(null);
        Assert.StartsWith("API key is required", credentials.StatusMessage);

        credentials.NewExchangeApiKey = "long-lived";
        credentials.NewExchangeExpiresInPath = "";
        await credentials.SaveCredentialCommand.ExecuteAsync(null);

        Assert.StartsWith("Added 'exchange'.", credentials.StatusMessage);
        Assert.Equal("", credentials.NewExchangeApiKey);

        var added = Stored(fixture, "exchange");
        Assert.Equal("long-lived", added.ExchangeApiKey);
        Assert.Null(added.ExchangeExpiresInPath);

        credentials.EditCredentialCommand.Execute(Row(credentials, "exchange"));
        Assert.Equal("Leave API key blank to keep the current one.", credentials.StatusMessage);

        credentials.NewName = "exchange (v2)";
        credentials.NewExchangeTokenPath = "data.token";
        await credentials.SaveCredentialCommand.ExecuteAsync(null);

        Assert.Equal("Saved changes to 'exchange (v2)'.", credentials.StatusMessage);

        var record = Stored(fixture, "exchange (v2)");
        Assert.Equal("long-lived", record.ExchangeApiKey);
        Assert.Equal("data.token", record.ExchangeTokenPath);
    }

    /// <summary>
    /// Token exchange, in request-body mode: the body is the secret, so it is what an edit keeps
    /// when left blank.
    /// </summary>
    [Fact]
    public async Task ATokenExchangeCredentialWithARequestBodyKeepsItOnEdit()
    {
        var (fixture, credentials) = await StartAsync();
        using var _ = fixture;

        credentials.SelectedKind = CredentialKind.TokenExchange;
        credentials.NewExchangeMode = TokenExchangeMode.CustomBody;
        credentials.NewName = "login";
        credentials.NewExchangeEndpoint = "https://auth.example.com/login";
        credentials.NewExchangeRequestBody = """{"user":"me","password":"pw"}""";

        await credentials.SaveCredentialCommand.ExecuteAsync(null);
        Assert.StartsWith("Added 'login'.", credentials.StatusMessage);

        credentials.EditCredentialCommand.Execute(Row(credentials, "login"));
        Assert.Equal("Leave the request body blank to keep the current one.", credentials.StatusMessage);
        Assert.Equal("", credentials.NewExchangeRequestBody);

        await credentials.SaveCredentialCommand.ExecuteAsync(null);

        Assert.Equal("""{"user":"me","password":"pw"}""", Stored(fixture, "login").ExchangeRequestBody);
    }

    /// <summary>
    /// An API key with a placement the proxy cannot carry out is refused, and the field labels
    /// follow the placement so the box asks for the right kind of name.
    /// </summary>
    [Fact]
    public async Task AnApiKeyWithAnUnusablePlacementIsRefused()
    {
        var (fixture, credentials) = await StartAsync();
        using var _ = fixture;

        credentials.SelectedKind = CredentialKind.ApiKey;
        credentials.NewName = "key";
        credentials.NewApiKey = "abc";

        credentials.NewDefaultPlacement = CredentialPlacement.Query;
        Assert.Equal("Query parameter name", credentials.DefaultParameterNameLabel);

        credentials.NewDefaultPlacement = CredentialPlacement.Body;
        Assert.Equal("Body field name", credentials.DefaultParameterNameLabel);

        credentials.NewDefaultParameterName = "";
        await credentials.SaveCredentialCommand.ExecuteAsync(null);

        Assert.Empty(fixture.Store.Current.Credentials);
        Assert.NotEqual("Ready.", credentials.StatusMessage);
    }

    /// <summary>
    /// Connecting and refreshing against a provider that cannot be reached both end in a sentence
    /// naming the credential, not an exception and not a silent no-op.
    /// </summary>
    [Fact]
    public async Task ConnectingAndRefreshingAnUnreachableProviderReportTheFailure()
    {
        var (fixture, credentials) = await StartAsync();
        using var _ = fixture;

        credentials.SelectedKind = CredentialKind.ClientCredentials;
        credentials.NewName = "offline";
        credentials.NewClientId = "app";
        credentials.NewClientSecret = "s";
        credentials.NewTokenEndpoint = Unreachable;
        await credentials.SaveCredentialCommand.ExecuteAsync(null);

        var row = Row(credentials, "offline");

        await credentials.ConnectCommand.ExecuteAsync(row);
        Assert.StartsWith("Failed to connect 'offline'", credentials.StatusMessage);

        await credentials.RefreshNowCommand.ExecuteAsync(row);
        Assert.StartsWith("Could not get a token for 'offline'", credentials.StatusMessage);

        // An empty click is nothing to do, for every row command.
        var before = credentials.StatusMessage;
        await credentials.ConnectCommand.ExecuteAsync(null);
        await credentials.RefreshNowCommand.ExecuteAsync(null);
        await credentials.TestCredentialCommand.ExecuteAsync(null);
        await credentials.DisconnectCommand.ExecuteAsync(null);
        await credentials.DeleteCredentialCommand.ExecuteAsync(null);
        credentials.EditCredentialCommand.Execute(null);
        Assert.Equal(before, credentials.StatusMessage);
    }

    /// <summary>
    /// Deleting the credential that is open in the form closes the form, so a save afterwards cannot
    /// write to a record that no longer exists.
    /// </summary>
    [Fact]
    public async Task DeletingTheCredentialBeingEditedClosesTheForm()
    {
        var (fixture, credentials) = await StartAsync();
        using var _ = fixture;

        credentials.SelectedKind = CredentialKind.ApiKey;
        credentials.NewName = "doomed";
        credentials.NewApiKey = "abc";
        await credentials.SaveCredentialCommand.ExecuteAsync(null);

        var row = Row(credentials, "doomed");
        credentials.EditCredentialCommand.Execute(row);
        Assert.True(credentials.IsEditing);

        await credentials.DeleteCredentialCommand.ExecuteAsync(row);

        Assert.False(credentials.IsEditing);
        Assert.Empty(fixture.Store.Current.Credentials);
    }
}
