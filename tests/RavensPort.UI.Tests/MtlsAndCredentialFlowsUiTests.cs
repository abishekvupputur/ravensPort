using Microsoft.Extensions.DependencyInjection;
using RavensPort.Core.Models;
using RavensPort.Core.Storage;
using RavensPort.UI.Services;
using RavensPort.UI.ViewModels;
using RavensPort.Views;

namespace RavensPort.UI.Tests;

/// <summary>
/// The two flows that produce something outside the vault: a client certificate, and a credential
/// tested against a live endpoint.
///
/// Both are Windows-only or network-bound in the product, and both are reachable here because the
/// harness already has what they need — a real listener for the certificate to be issued against,
/// and the echo upstream for a test call to land on.
/// </summary>
public class MtlsAndCredentialFlowsUiTests
{
    /// <summary>
    /// Generating a client certificate and exporting it.
    ///
    /// Generating is behind a confirmation because it replaces the one in use: every client already
    /// configured with the old certificate stops being admitted the moment a new one is issued. The
    /// export is what a user then hands to those clients, so what matters is that a real file
    /// arrives at the path the picker returned.
    /// </summary>
    [Fact]
    public Task GeneratingAndExportingAClientCertificateWritesARealFile() => UiSession.RunAsync(async () =>
    {
        await using var harness = await SingleUseHarness.StartAsync();
        await harness.StartSingleUseThroughTheUiAsync();

        var vm = harness.Services.GetRequiredService<SettingsViewModel>();
        var view = UiDriver.Show<SettingsView>(vm);
        var store = harness.Services.GetRequiredService<ConfigStoreCache>();
        var picker = (RecordingSavePicker)harness.Services.GetRequiredService<IFileSavePicker>();

        // Confirm first, then choose a password — that order is the product’s, not a preference.
        // The first press opens the confirmation and deliberately clears the box, so a password
        // typed beforehand is discarded; the tab then asks for one, and refuses to mint without it
        // because a PFX with no password is one Windows and curl both decline to load.
        await UiDriver.ClickAsync(view, AutomationIds.GenerateMtls);
        await UiDriver.UntilAsync(
            () => vm.IsConfirmingGenerateCertificate,
            () => $"the confirmation to appear — the tab says: {vm.StatusMessage}");

        await UiDriver.TypeAsync(view, AutomationIds.MtlsPassword, "the-pfx-password"); // gitleaks:allow
        await UiDriver.ClickAsync(view, AutomationIds.ConfirmGenerateMtls);

        await UiDriver.UntilAsync(
            () => !string.IsNullOrEmpty(store.Current.Settings.MtlsClientCertificatePfx),
            () => $"a certificate to be issued — the tab says: {vm.StatusMessage}");

        await UiDriver.ClickAsync(view, AutomationIds.ExportMtls);

        await UiDriver.UntilAsync(
            () => picker.Picked.Count == 1,
            () => $"the export to choose a path — the tab says: {vm.StatusMessage}");

        var written = picker.Picked.Single();

        await UiDriver.UntilAsync(
            () => File.Exists(written) && new FileInfo(written).Length > 0,
            () => $"'{written}' to be written — the tab says: {vm.StatusMessage}");

        // A PFX, not an empty file with the right name: the first two bytes of a PKCS#12 are the
        // DER sequence header, which is the cheapest thing that distinguishes one from nothing.
        var head = File.ReadAllBytes(written)[..2];
        Assert.Equal(0x30, head[0]);
    });

    /// <summary>
    /// Cancelling the export dialog, which must leave no file and no complaint.
    /// </summary>
    [Fact]
    public Task CancellingTheExportWritesNothing() => UiSession.RunAsync(async () =>
    {
        await using var harness = await SingleUseHarness.StartAsync();
        await harness.StartSingleUseThroughTheUiAsync();

        var vm = harness.Services.GetRequiredService<SettingsViewModel>();
        var view = UiDriver.Show<SettingsView>(vm);
        var store = harness.Services.GetRequiredService<ConfigStoreCache>();
        var picker = (RecordingSavePicker)harness.Services.GetRequiredService<IFileSavePicker>();

        await UiDriver.ClickAsync(view, AutomationIds.GenerateMtls);
        await UiDriver.UntilAsync(() => vm.IsConfirmingGenerateCertificate, "the confirmation");
        await UiDriver.TypeAsync(view, AutomationIds.MtlsPassword, "the-pfx-password"); // gitleaks:allow
        await UiDriver.ClickAsync(view, AutomationIds.ConfirmGenerateMtls);
        await UiDriver.UntilAsync(
            () => !string.IsNullOrEmpty(store.Current.Settings.MtlsClientCertificatePfx),
            "a certificate to be issued");

        picker.Cancel = true;

        await UiDriver.ClickAsync(view, AutomationIds.ExportMtls);
        await UiDriver.PumpAsync();

        Assert.Empty(picker.Picked);
    });

    /// <summary>
    /// Testing a credential against a real endpoint, which is the only button on the Credentials tab
    /// that leaves the machine.
    ///
    /// Pointed at the harness's echo upstream, so what comes back is a genuine 200 with the request
    /// it was sent — a test that reported success against nothing would look identical from the tab.
    /// </summary>
    [Fact]
    public Task TestingACredentialCallsItsTestEndpoint() => UiSession.RunAsync(async () =>
    {
        await using var harness = await SingleUseHarness.StartAsync();
        await harness.StartSingleUseThroughTheUiAsync();

        var vm = harness.Services.GetRequiredService<CredentialsViewModel>();
        var view = UiDriver.Show<CredentialsView>(vm);
        var store = harness.Services.GetRequiredService<ConfigStoreCache>();

        await UiDriver.SelectAsync(view, AutomationIds.CredentialKind, "API key");
        await UiDriver.TypeAsync(view, AutomationIds.CredentialName, "tested");
        await UiDriver.TypeAsync(view, AutomationIds.CredentialApiKey, "the-key"); // gitleaks:allow
        await UiDriver.SelectAsync(view, AutomationIds.CredentialPlacement, "Header");
        await UiDriver.TypeAsync(view, AutomationIds.CredentialParameter, "X-Api-Key");
        await UiDriver.TypeAsync(view, AutomationIds.CredentialTestEndpoint, harness.UpstreamUrl);
        await UiDriver.ClickAsync(view, AutomationIds.SaveCredential);

        await UiDriver.UntilAsync(
            () => store.Current.Credentials.Count == 1,
            () => $"the credential to be saved — the tab says: {vm.StatusMessage}");

        var id = store.Current.Credentials.Single().Id;

        await UiDriver.ClickForItemAsync<CredentialItemViewModel>(
            view, AutomationIds.TestCredentialRow, c => c.Record.Id == id);

        // The echo upstream answers 200 to anything, so a report of failure here would mean the
        // call never reached it.
        await UiDriver.UntilAsync(
            () => vm.StatusMessage.Contains("200", StringComparison.Ordinal)
                  || vm.StatusMessage.Contains("ok", StringComparison.OrdinalIgnoreCase)
                  || vm.StatusMessage.Contains("success", StringComparison.OrdinalIgnoreCase),
            () => $"the test call to report what it got — the tab says: {vm.StatusMessage}");
    });

    /// <summary>
    /// The provider presets, which fill the OAuth2 endpoints in so nobody has to look them up.
    ///
    /// Choosing one is the difference between a working GitHub credential and three URLs typed from
    /// memory, so what is asserted is that the endpoints actually arrive in the record.
    /// </summary>
    [Fact]
    public Task ChoosingAProviderPresetFillsInItsEndpoints() => UiSession.RunAsync(async () =>
    {
        await using var harness = await SingleUseHarness.StartAsync();
        await harness.StartSingleUseThroughTheUiAsync();

        var vm = harness.Services.GetRequiredService<CredentialsViewModel>();
        var view = UiDriver.Show<CredentialsView>(vm);
        var store = harness.Services.GetRequiredService<ConfigStoreCache>();

        await UiDriver.SelectAsync(view, AutomationIds.CredentialKind, "OAuth2 (user login)");

        // Whatever the app offers, driven by its own list rather than a name typed here.
        var preset = vm.Presets.First(p => !ReferenceEquals(p, OAuthProviderPreset.Custom));

        await UiDriver.RunOnUiAsync(() => vm.SelectedPreset = preset);

        await UiDriver.TypeAsync(view, AutomationIds.CredentialName, "preset");
        await UiDriver.TypeAsync(view, AutomationIds.CredentialClientId, "id");
        await UiDriver.TypeAsync(view, AutomationIds.CredentialClientSecret, "secret"); // gitleaks:allow
        await UiDriver.ClickAsync(view, AutomationIds.SaveCredential);

        await UiDriver.UntilAsync(
            () => store.Current.Credentials.Count == 1,
            () => $"the preset credential to be saved — the tab says: {vm.StatusMessage}");

        // Asserted against what the preset itself defines rather than against a fixed shape:
        // Google names an authority and leaves the endpoints to discovery, while a provider with no
        // discovery document names the two endpoints outright. Both are a filled-in preset.
        var saved = store.Current.Credentials.Single();

        var filledIn = !string.IsNullOrWhiteSpace(saved.Authority)
                       || !string.IsNullOrWhiteSpace(saved.AuthorizationEndpoint)
                       || !string.IsNullOrWhiteSpace(saved.TokenEndpoint);

        Assert.True(filledIn,
            $"preset '{preset.Name}' filled in nothing: authority='{saved.Authority}', "
            + $"authorize='{saved.AuthorizationEndpoint}', token='{saved.TokenEndpoint}'");

        if (preset.Authority is { Length: > 0 } authority) Assert.Equal(authority, saved.Authority);
    });
}
