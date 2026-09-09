using Microsoft.Extensions.DependencyInjection;
using RavensPort.Core.Models;
using RavensPort.Core.Storage;
using RavensPort.UI.ViewModels;
using RavensPort.Views;

namespace RavensPort.UI.Tests;

/// <summary>
/// The Credentials tab, driven as a person drives it.
///
/// Five kinds of credential share one editor, and which fields it shows — and which it insists on —
/// changes with the kind. That is the interesting part and the part nothing else covers: the
/// approval suite constructs CredentialRecords directly, so the editor's own rules have never been
/// exercised by a test at all.
///
/// A credential is saved to the vault, so each test asserts against the store rather than the form:
/// what a field said matters only if it reached the record.
/// </summary>
public class CredentialsTabUiTests
{
    private const string ServiceAccountJson = """
        {"type":"service_account","project_id":"p","private_key_id":"k",
         "private_key":"-----BEGIN PRIVATE KEY-----\nMIIB\n-----END PRIVATE KEY-----\n",
         "client_email":"svc@p.iam.gserviceaccount.com","client_id":"1","token_uri":"https://oauth2.googleapis.com/token"}
        """;

    /// <summary>An API key: no flow, no expiry, and a default placement of its own.</summary>
    [Fact]
    public Task AnApiKeyCredentialIsSavedWithItsDefaultPlacement() => UiSession.RunAsync(async () =>
    {
        await using var harness = await SingleUseHarness.StartAsync();
        await harness.StartSingleUseThroughTheUiAsync();

        var (view, store) = Open(harness);

        await UiDriver.SelectAsync(view, AutomationIds.CredentialKind, "API key");
        await UiDriver.TypeAsync(view, AutomationIds.CredentialName, "gitlab");
        await UiDriver.TypeAsync(view, AutomationIds.CredentialApiKey, "glpat-xxxx"); // gitleaks:allow
        await UiDriver.SelectAsync(view, AutomationIds.CredentialPlacement, "Header");
        await UiDriver.TypeAsync(view, AutomationIds.CredentialParameter, "PRIVATE-TOKEN");
        await UiDriver.TypeAsync(view, AutomationIds.CredentialValuePrefix, "");
        await UiDriver.ClickAsync(view, AutomationIds.SaveCredential);

        await UiDriver.UntilAsync(() => store.Current.Credentials.Count == 1, "the key to be saved");

        var saved = store.Current.Credentials.Single();
        Assert.Equal(CredentialKind.ApiKey, saved.Kind);
        Assert.Equal("gitlab", saved.Name);
        Assert.Equal(CredentialPlacement.Header, saved.DefaultPlacement);
        Assert.Equal("PRIVATE-TOKEN", saved.DefaultParameterName);
    });

    /// <summary>
    /// The app signing in as itself. No browser, so a token endpoint and a secret are the whole of
    /// what it needs — and the editor says so by refusing without them.
    /// </summary>
    [Fact]
    public Task AClientCredentialsCredentialIsSavedWithItsTokenEndpoint() => UiSession.RunAsync(async () =>
    {
        await using var harness = await SingleUseHarness.StartAsync();
        await harness.StartSingleUseThroughTheUiAsync();

        var (view, store) = Open(harness);

        await UiDriver.SelectAsync(view, AutomationIds.CredentialKind, "OAuth2 client credentials (app login)");
        await UiDriver.TypeAsync(view, AutomationIds.CredentialName, "service");
        await UiDriver.TypeAsync(view, AutomationIds.CredentialClientId, "client-id");
        await UiDriver.TypeAsync(view, AutomationIds.CredentialClientSecret, "client-secret"); // gitleaks:allow
        await UiDriver.TypeAsync(view, AutomationIds.CredentialTokenEndpoint, "https://example.test/token");
        await UiDriver.TypeAsync(view, AutomationIds.CredentialScopes, "read write");
        await UiDriver.ClickAsync(view, AutomationIds.SaveCredential);

        await UiDriver.UntilAsync(() => store.Current.Credentials.Count == 1,
            () => $"the app login to be saved — the tab says: {Vm(harness).StatusMessage}");

        var saved = store.Current.Credentials.Single();
        Assert.Equal(CredentialKind.ClientCredentials, saved.Kind);
        Assert.Equal("https://example.test/token", saved.TokenEndpoint);
        Assert.Contains("read", saved.Scopes);
    });

    /// <summary>The device flow: a code on one screen, entered on another. No redirect to register.</summary>
    [Fact]
    public Task ADeviceCodeCredentialIsSavedWithBothEndpoints() => UiSession.RunAsync(async () =>
    {
        await using var harness = await SingleUseHarness.StartAsync();
        await harness.StartSingleUseThroughTheUiAsync();

        var (view, store) = Open(harness);

        await UiDriver.SelectAsync(view, AutomationIds.CredentialKind, "OAuth2 device code");
        await UiDriver.TypeAsync(view, AutomationIds.CredentialName, "tv");
        await UiDriver.TypeAsync(view, AutomationIds.CredentialClientId, "device-client");
        await UiDriver.TypeAsync(view, AutomationIds.CredentialDeviceEndpoint, "https://example.test/device");
        await UiDriver.TypeAsync(view, AutomationIds.CredentialTokenEndpoint, "https://example.test/token");
        await UiDriver.ClickAsync(view, AutomationIds.SaveCredential);

        await UiDriver.UntilAsync(() => store.Current.Credentials.Count == 1,
            () => $"the device-code credential to be saved — the tab says: {Vm(harness).StatusMessage}");

        var saved = store.Current.Credentials.Single();
        Assert.Equal(CredentialKind.DeviceCode, saved.Kind);
        Assert.Equal("https://example.test/device", saved.DeviceAuthorizationEndpoint);
    });

    /// <summary>A Google service account signs for its own tokens; the key file is the credential.</summary>
    [Fact]
    public Task AServiceAccountCredentialIsSavedFromItsKeyFile() => UiSession.RunAsync(async () =>
    {
        await using var harness = await SingleUseHarness.StartAsync();
        await harness.StartSingleUseThroughTheUiAsync();

        var (view, store) = Open(harness);

        await UiDriver.SelectAsync(view, AutomationIds.CredentialKind, "Google service account");
        await UiDriver.TypeAsync(view, AutomationIds.CredentialName, "sheets");
        await UiDriver.TypeAsync(view, AutomationIds.CredentialServiceAccountJson, ServiceAccountJson);
        await UiDriver.TypeAsync(view, AutomationIds.CredentialScopes,
            "https://www.googleapis.com/auth/spreadsheets.readonly");
        await UiDriver.ClickAsync(view, AutomationIds.SaveCredential);

        await UiDriver.UntilAsync(() => store.Current.Credentials.Count == 1,
            () => $"the service account to be saved — the tab says: {Vm(harness).StatusMessage}");

        Assert.Equal(CredentialKind.GoogleServiceAccount, store.Current.Credentials.Single().Kind);
    });

    /// <summary>
    /// The refusals, which are the editor's real subject: every kind is asked for a name, and each
    /// asks for the one thing it cannot work without. Nothing is written on any of these paths.
    /// </summary>
    [Fact]
    public Task TheEditorRefusesEachKindWithoutWhatItNeeds() => UiSession.RunAsync(async () =>
    {
        await using var harness = await SingleUseHarness.StartAsync();
        await harness.StartSingleUseThroughTheUiAsync();

        var (view, store) = Open(harness);
        var vm = Vm(harness);

        // No name, on the kind that needs least.
        await UiDriver.SelectAsync(view, AutomationIds.CredentialKind, "API key");
        await UiDriver.TypeAsync(view, AutomationIds.CredentialApiKey, "something");
        await UiDriver.ClickAsync(view, AutomationIds.SaveCredential);
        Assert.Empty(store.Current.Credentials);
        Assert.Contains("name", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);

        // A name, but no key.
        await UiDriver.TypeAsync(view, AutomationIds.CredentialName, "no key");
        await UiDriver.TypeAsync(view, AutomationIds.CredentialApiKey, "");
        await UiDriver.ClickAsync(view, AutomationIds.SaveCredential);
        Assert.Empty(store.Current.Credentials);

        // An OAuth2 login with no client id.
        await UiDriver.SelectAsync(view, AutomationIds.CredentialKind, "OAuth2 (user login)");
        await UiDriver.TypeAsync(view, AutomationIds.CredentialName, "no client");
        await UiDriver.TypeAsync(view, AutomationIds.CredentialClientId, "");
        await UiDriver.ClickAsync(view, AutomationIds.SaveCredential);
        Assert.Empty(store.Current.Credentials);
        Assert.Contains("Client ID", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);

        // An endpoint that would put the secret on the wire in clear.
        await UiDriver.TypeAsync(view, AutomationIds.CredentialClientId, "id");
        await UiDriver.TypeAsync(view, AutomationIds.CredentialTokenEndpoint, "http://example.test/token");
        await UiDriver.ClickAsync(view, AutomationIds.SaveCredential);
        Assert.Empty(store.Current.Credentials);
    });

    /// <summary>
    /// Editing an existing credential, and cancelling out of it.
    ///
    /// Edit is the path that must not lose a secret: the boxes come back blank because a stored key
    /// is never redisplayed, so a save that took them literally would wipe it.
    /// </summary>
    [Fact]
    public Task EditingKeepsTheStoredKeyAndCancellingChangesNothing() => UiSession.RunAsync(async () =>
    {
        await using var harness = await SingleUseHarness.StartAsync();
        await harness.StartSingleUseThroughTheUiAsync();

        var (view, store) = Open(harness);

        await UiDriver.SelectAsync(view, AutomationIds.CredentialKind, "API key");
        await UiDriver.TypeAsync(view, AutomationIds.CredentialName, "before");
        await UiDriver.TypeAsync(view, AutomationIds.CredentialApiKey, "the-secret"); // gitleaks:allow
        await UiDriver.ClickAsync(view, AutomationIds.SaveCredential);
        await UiDriver.UntilAsync(() => store.Current.Credentials.Count == 1, "the credential to be saved");

        var id = store.Current.Credentials.Single().Id;

        // Rename, leaving the key box blank on purpose.
        await UiDriver.ClickForItemAsync<CredentialItemViewModel>(
            view, AutomationIds.EditCredentialRow, c => c.Record.Id == id);
        await UiDriver.TypeAsync(view, AutomationIds.CredentialName, "after");
        await UiDriver.ClickAsync(view, AutomationIds.SaveCredential);

        await UiDriver.UntilAsync(
            () => store.Current.Credentials.Single().Name == "after", "the rename to be saved");

        Assert.Equal("the-secret", store.Current.Credentials.Single().ApiKey);
        Assert.Single(store.Current.Credentials);

        // Start another edit and back out of it.
        await UiDriver.ClickForItemAsync<CredentialItemViewModel>(
            view, AutomationIds.EditCredentialRow, c => c.Record.Id == id);
        await UiDriver.TypeAsync(view, AutomationIds.CredentialName, "discarded");
        await UiDriver.ClickAsync(view, AutomationIds.CancelCredentialEdit);

        await UiDriver.PumpAsync();
        Assert.Equal("after", store.Current.Credentials.Single().Name);
    });

    /// <summary>Deleting one, which is the only way a credential leaves the vault.</summary>
    [Fact]
    public Task DeletingACredentialRemovesItFromTheVault() => UiSession.RunAsync(async () =>
    {
        await using var harness = await SingleUseHarness.StartAsync();
        await harness.StartSingleUseThroughTheUiAsync();

        var (view, store) = Open(harness);

        foreach (var name in new[] { "first", "second" })
        {
            await UiDriver.SelectAsync(view, AutomationIds.CredentialKind, "API key");
            await UiDriver.TypeAsync(view, AutomationIds.CredentialName, name);
            await UiDriver.TypeAsync(view, AutomationIds.CredentialApiKey, $"key-{name}");
            await UiDriver.ClickAsync(view, AutomationIds.SaveCredential);
            await UiDriver.UntilAsync(
                () => store.Current.Credentials.Any(c => c.Name == name), $"'{name}' to be saved");
        }

        var doomed = store.Current.Credentials.Single(c => c.Name == "first").Id;

        await UiDriver.ClickForItemAsync<CredentialItemViewModel>(
            view, AutomationIds.DeleteCredentialRow, c => c.Record.Id == doomed);

        await UiDriver.UntilAsync(
            () => store.Current.Credentials.Count == 1, "the credential to be deleted");

        Assert.Equal("second", store.Current.Credentials.Single().Name);
    });

    private static CredentialsViewModel Vm(SingleUseHarness harness) =>
        harness.Services.GetRequiredService<CredentialsViewModel>();

    private static (Avalonia.Controls.Window View, ConfigStoreCache Store) Open(SingleUseHarness harness) =>
        (UiDriver.Show<CredentialsView>(Vm(harness)),
         harness.Services.GetRequiredService<ConfigStoreCache>());
}
