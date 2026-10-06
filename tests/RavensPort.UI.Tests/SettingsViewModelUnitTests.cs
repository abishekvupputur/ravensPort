using RavensPort.Core.Models;
using RavensPort.Core.Storage;
using RavensPort.Core.Vault;
using RavensPort.UI.ViewModels;

namespace RavensPort.UI.Tests;

/// <summary>
/// The Settings tab's decisions, asked without a window.
///
/// Every case runs in a single-use session, because that is the one backend with a real vault
/// behind it that needs no password manager: the InMemoryVault the gate hands out is the same
/// object the integrity check lists, the sync queue writes to and a disconnect drops. What the
/// tests assert is what the tab tells the user, since that line is the whole of the feedback for
/// most of these commands.
/// </summary>
public class SettingsViewModelUnitTests
{
    /// <summary>
    /// The integrity check sorts a vault into the three kinds of trouble it can be in, and each kind
    /// can then be dealt with one row at a time.
    ///
    /// Seeded by hand rather than by breaking a save: an item named like one of the app's own with
    /// no record behind it is an orphan, an item nobody here wrote is someone else's, and a route
    /// whose key item has gone from the vault is a record that would be lost on exit.
    /// </summary>
    [Fact]
    public async Task TheIntegrityCheckFindsEachKindOfTroubleAndEachCanBeResolved()
    {
        using var fixture = new ViewModelFixture();
        await fixture.StartSingleUseAsync();

        var vault = (InMemoryVault)fixture.Gate.Selected;
        var settings = fixture.Get<SettingsViewModel>();

        await fixture.Store.MutateAsync(store =>
            store.Routes.Add(new RouteMapping { PathPrefix = "/api", Key = ProxyKey.Generate() }));

        // The route reaches the vault through the sync queue, not at the mutation.
        Assert.True(await fixture.Get<VaultSyncQueue>().FlushAsync(TimeSpan.FromSeconds(10)));

        vault.AddForeignItem(VaultItemNaming.ForCredential(Guid.NewGuid(), "ghost"), "secret");
        vault.AddForeignItem("someone else's login", "hunter2");

        var routeItem = (await vault.ListLiveItemsAsync())
            .Single(i => i.Role == VaultItemRole.RouteKey);
        vault.RemoveItem(routeItem.ItemId);

        await settings.CheckIntegrityCommand.ExecuteAsync(null);

        Assert.Equal("Vault needs attention.", settings.StatusMessage);
        Assert.True(settings.HasIntegrityResult);
        Assert.False(settings.IsCheckingIntegrity);

        var orphan = Assert.Single(settings.Orphans);
        Assert.True(settings.HasOrphans);
        var missing = Assert.Single(settings.MissingItems);
        Assert.True(settings.HasMissingItems);
        var other = Assert.Single(settings.OtherItems);
        Assert.True(settings.HasOtherItems);

        await settings.DeleteOrphanCommand.ExecuteAsync(orphan);
        Assert.Equal($"Deleted '{orphan.Title}'.", settings.StatusMessage);
        Assert.False(settings.HasOrphans);

        await settings.DeleteOtherItemCommand.ExecuteAsync(other);
        Assert.Equal($"Deleted '{other.Title}'.", settings.StatusMessage);
        Assert.False(settings.HasOtherItems);

        var dropped = false;
        settings.RecordsDropped += () => dropped = true;

        await settings.DropMissingRecordCommand.ExecuteAsync(missing);
        Assert.Equal($"Removed '{missing.Title}' from the configuration.", settings.StatusMessage);
        Assert.False(settings.HasMissingItems);
        Assert.True(dropped, "the tabs were not told a record had gone");
        Assert.Empty(fixture.Store.Current.Routes);

        await settings.CheckIntegrityCommand.ExecuteAsync(null);
        Assert.Equal("Vault is healthy.", settings.StatusMessage);
    }

    /// <summary>Deleting a row that has already gone says why, rather than pretending it worked.</summary>
    [Fact]
    public async Task DeletingAnItemTheVaultNoLongerHasReportsTheFailure()
    {
        using var fixture = new ViewModelFixture();
        await fixture.StartSingleUseAsync();

        var settings = fixture.Get<SettingsViewModel>();

        await settings.DeleteOrphanCommand.ExecuteAsync(new VaultOrphanItem("gone-1", "gone", "test"));
        Assert.Contains("gone-1", settings.StatusMessage);

        await settings.DeleteOtherItemCommand.ExecuteAsync(VaultItemEntry.Classify("gone-2", "other"));
        Assert.Contains("gone-2", settings.StatusMessage);

        // Nothing selected is nothing to do, and must not throw from a click on an empty row.
        var before = settings.StatusMessage;
        await settings.DeleteOrphanCommand.ExecuteAsync(null);
        await settings.DeleteOtherItemCommand.ExecuteAsync(null);
        await settings.DropMissingRecordCommand.ExecuteAsync(null);
        Assert.Equal(before, settings.StatusMessage);
    }

    /// <summary>
    /// "Delete all" removes every orphan the check found, and with none found it does nothing.
    /// </summary>
    [Fact]
    public async Task DeletingAllOrphansEmptiesTheList()
    {
        using var fixture = new ViewModelFixture();
        await fixture.StartSingleUseAsync();

        var vault = (InMemoryVault)fixture.Gate.Selected;
        var settings = fixture.Get<SettingsViewModel>();

        await settings.DeleteAllOrphansCommand.ExecuteAsync(null);
        Assert.Equal("Ready.", settings.StatusMessage);

        vault.AddForeignItem(VaultItemNaming.ForCredential(Guid.NewGuid(), "one"), "x");
        vault.AddForeignItem(VaultItemNaming.ForCredential(Guid.NewGuid(), "two"), "y");

        await settings.CheckIntegrityCommand.ExecuteAsync(null);
        Assert.Equal(2, settings.Orphans.Count);

        await settings.DeleteAllOrphansCommand.ExecuteAsync(null);

        Assert.Equal("Deleted 2 item(s).", settings.StatusMessage);
        Assert.False(settings.HasOrphans);
    }

    /// <summary>
    /// The two repair buttons write through the sync queue, and a successful repair re-runs the
    /// check so the list on screen is not left describing the vault as it was before.
    /// </summary>
    [Fact]
    public async Task RewritingAndWritingMissingItemsRepairTheVault()
    {
        using var fixture = new ViewModelFixture();
        await fixture.StartSingleUseAsync();

        var vault = (InMemoryVault)fixture.Gate.Selected;
        var settings = fixture.Get<SettingsViewModel>();

        await fixture.Store.MutateAsync(store =>
            store.Routes.Add(new RouteMapping { PathPrefix = "/api", Key = ProxyKey.Generate() }));

        await settings.RewriteAllItemsCommand.ExecuteAsync(null);
        Assert.Equal("Wrote every item and the configuration to the vault.", settings.StatusMessage);

        vault.RemoveItem((await vault.ListLiveItemsAsync()).Single(i => i.Role == VaultItemRole.RouteKey).ItemId);

        await settings.CheckIntegrityCommand.ExecuteAsync(null);
        Assert.True(settings.HasMissingItems);

        await settings.WriteMissingItemsCommand.ExecuteAsync(null);

        Assert.Equal("Vault is healthy.", settings.StatusMessage);
        Assert.False(settings.HasMissingItems);

        // With a result on screen, a full rewrite refreshes it too.
        await settings.RewriteAllItemsCommand.ExecuteAsync(null);
        Assert.Equal("Vault is healthy.", settings.StatusMessage);
    }

    /// <summary>
    /// Re-initialising asks first, does nothing until the host has said how, and reports a reload
    /// that failed rather than claiming it worked.
    /// </summary>
    [Fact]
    public async Task ReinitialisingConfirmsFirstAndReportsWhatHappened()
    {
        using var fixture = new ViewModelFixture();
        await fixture.StartSingleUseAsync();

        var settings = fixture.Get<SettingsViewModel>();

        await settings.ReinitialiseCommand.ExecuteAsync(null);
        Assert.True(settings.IsConfirmingReinitialise);

        settings.CancelReinitialiseCommand.Execute(null);
        Assert.False(settings.IsConfirmingReinitialise);
        Assert.Equal("Left as it is.", settings.StatusMessage);

        await settings.ReinitialiseCommand.ExecuteAsync(null);
        await settings.ReinitialiseCommand.ExecuteAsync(null);
        Assert.Equal("Nothing to re-initialise from yet.", settings.StatusMessage);

        var reloads = 0;
        settings.ReinitialiseRequested = () =>
        {
            reloads++;
            return Task.CompletedTask;
        };

        await settings.ReinitialiseCommand.ExecuteAsync(null);
        await settings.ReinitialiseCommand.ExecuteAsync(null);
        Assert.Equal(1, reloads);
        Assert.Equal("Reloaded from the vault.", settings.StatusMessage);

        settings.ReinitialiseRequested = () => throw new InvalidOperationException("the vault is locked");

        await settings.ReinitialiseCommand.ExecuteAsync(null);
        await settings.ReinitialiseCommand.ExecuteAsync(null);
        Assert.Equal("Could not reload: the vault is locked", settings.StatusMessage);
    }

    /// <summary>
    /// "Sync now" with nothing pending re-reads the vault instead, and a failed re-read is reported
    /// rather than swallowed.
    /// </summary>
    [Fact]
    public async Task SyncingWithNothingPendingRereadsTheVault()
    {
        using var fixture = new ViewModelFixture();
        await fixture.StartSingleUseAsync();

        var settings = fixture.Get<SettingsViewModel>();

        await settings.SyncNowCommand.ExecuteAsync(null);
        Assert.Equal("Checked — the vault already has everything.", settings.StatusMessage);

        var rereads = 0;
        settings.ReloadFromVaultRequested = () =>
        {
            rereads++;
            return Task.CompletedTask;
        };

        await settings.SyncNowCommand.ExecuteAsync(null);
        Assert.Equal(1, rereads);
        Assert.Equal("Checked — the vault already has everything.", settings.StatusMessage);

        settings.ReloadFromVaultRequested = () => throw new InvalidOperationException("locked");

        await settings.SyncNowCommand.ExecuteAsync(null);
        Assert.Equal("Could not read the vault: locked", settings.StatusMessage);
    }

    /// <summary>
    /// Disconnecting from a single-use session asks first and then purges it: the tabs are rebuilt,
    /// the host is told, and the tab stops describing a session that no longer exists.
    /// </summary>
    [Fact]
    public async Task DisconnectingASingleUseSessionConfirmsAndThenPurgesIt()
    {
        using var fixture = new ViewModelFixture();
        await fixture.StartSingleUseAsync();

        var settings = fixture.Get<SettingsViewModel>();
        settings.Reload();

        Assert.True(settings.IsSingleUse);
        Assert.Equal("Single use — no password manager.", settings.PasswordManagerSummary);

        await settings.DisconnectCommand.ExecuteAsync(null);
        Assert.True(settings.IsConfirmingDisconnect);
        Assert.Equal("", settings.DisconnectWarning);

        settings.CancelDisconnectCommand.Execute(null);
        Assert.False(settings.IsConfirmingDisconnect);
        Assert.Equal("Left connected.", settings.StatusMessage);

        var rebuilt = false;
        var disconnected = false;
        settings.UseTabRebuilder(() => rebuilt = true);
        settings.Disconnected += () => disconnected = true;

        await settings.DisconnectCommand.ExecuteAsync(null);
        await settings.DisconnectCommand.ExecuteAsync(null);

        Assert.Equal("Single-use configuration purged.", settings.StatusMessage);
        Assert.True(rebuilt);
        Assert.True(disconnected);
        Assert.False(fixture.Gate.IsSingleUse);
        Assert.False(settings.IsConnected);
        Assert.Equal("Not connected to a password manager.", settings.PasswordManagerSummary);
    }

    /// <summary>
    /// Proton Pass sign-out asks first and can be backed out of. The confirmed half needs a real
    /// Proton Pass session, which a single-use session is not, so it is left to the system suite.
    /// </summary>
    [Fact]
    public async Task SigningOutOfProtonPassAsksFirstAndCanBeCancelled()
    {
        using var fixture = new ViewModelFixture();
        await fixture.StartSingleUseAsync();

        var settings = fixture.Get<SettingsViewModel>();

        Assert.False(settings.CanSignOutOfProtonPass);

        await settings.SignOutOfProtonPassCommand.ExecuteAsync(null);
        Assert.True(settings.IsConfirmingSignOut);
        Assert.StartsWith("Confirm to sign out", settings.StatusMessage);

        settings.CancelSignOutCommand.Execute(null);
        Assert.False(settings.IsConfirmingSignOut);
        Assert.Equal("Left signed in.", settings.StatusMessage);
    }

    /// <summary>
    /// mTLS cannot be switched on before a certificate exists, a certificate needs a password, and
    /// once both are there the switch is saved and the tab says a restart is needed.
    /// </summary>
    [Fact]
    public async Task MtlsNeedsACertificateWithAPasswordBeforeItCanBeEnabled()
    {
        using var fixture = new ViewModelFixture();
        await fixture.StartSingleUseAsync();

        var settings = fixture.Get<SettingsViewModel>();

        settings.MtlsEnabled = true;
        await ViewModelFixture.UntilAsync(
            () => settings.StatusMessage.StartsWith("Generate a client certificate first", StringComparison.Ordinal),
            () => $"mTLS to be refused without a certificate (status: '{settings.StatusMessage}')");
        Assert.False(settings.MtlsEnabled);

        await settings.ExportMtlsCertificateCommand.ExecuteAsync(null);
        Assert.Equal("No certificate has been generated yet.", settings.StatusMessage);

        await settings.GenerateMtlsCertificateCommand.ExecuteAsync(null);
        Assert.True(settings.IsConfirmingGenerateCertificate);

        settings.CancelGenerateCertificateCommand.Execute(null);
        Assert.False(settings.IsConfirmingGenerateCertificate);
        Assert.Equal("Left current certificate intact.", settings.StatusMessage);

        await settings.GenerateMtlsCertificateCommand.ExecuteAsync(null);
        await settings.GenerateMtlsCertificateCommand.ExecuteAsync(null);
        Assert.StartsWith("Enter a password", settings.StatusMessage);

        settings.NewCertificatePassword = "correct horse";
        await settings.GenerateMtlsCertificateCommand.ExecuteAsync(null);

        Assert.StartsWith("New client certificate generated", settings.StatusMessage);
        Assert.True(settings.HasCertificate);
        Assert.False(settings.IsCertificateExpiryUrgent);
        Assert.Equal("", settings.NewCertificatePassword);

        settings.MtlsEnabled = true;
        await ViewModelFixture.UntilAsync(
            () => fixture.Store.Current.Settings.MtlsEnabled,
            () => $"mTLS to be saved (status: '{settings.StatusMessage}')");
        Assert.True(settings.IsMtlsRestartRequired);

        settings.MtlsEnabled = false;
        await ViewModelFixture.UntilAsync(
            () => !fixture.Store.Current.Settings.MtlsEnabled,
            () => $"mTLS to be switched off (status: '{settings.StatusMessage}')");
        Assert.StartsWith("mTLS disabled", settings.StatusMessage);
    }

    /// <summary>
    /// A certificate whose expiry has passed, or that cannot be opened at all, is reported as
    /// urgent — the proxy refuses every caller presenting it, and the failure is a dropped TLS
    /// handshake that says nothing on the client side.
    /// </summary>
    [Fact]
    public async Task AnUnreadableCertificateIsReportedAsUrgent()
    {
        using var fixture = new ViewModelFixture();
        await fixture.StartSingleUseAsync();

        await fixture.Store.MutateAsync(store =>
        {
            store.Settings.MtlsClientCertificatePfx = Convert.ToBase64String([1, 2, 3, 4]);
            store.Settings.MtlsClientCertificatePassword = "pw";
        });

        var settings = fixture.Get<SettingsViewModel>();
        settings.Reload();

        Assert.True(settings.IsCertificateExpiryUrgent);
        Assert.StartsWith("The stored certificate could not be read", settings.CertificateExpirySummary);
    }

    /// <summary>A generated certificate can be exported, and a cancelled export writes nothing.</summary>
    [Fact]
    public async Task AGeneratedCertificateCanBeExported()
    {
        using var fixture = new ViewModelFixture();
        await fixture.StartSingleUseAsync();

        var settings = fixture.Get<SettingsViewModel>();
        var picker = (RecordingSavePicker)fixture.Get<RavensPort.UI.Services.IFileSavePicker>();

        await settings.GenerateMtlsCertificateCommand.ExecuteAsync(null);
        settings.NewCertificatePassword = "pw";
        await settings.GenerateMtlsCertificateCommand.ExecuteAsync(null);

        picker.Cancel = true;
        await settings.ExportMtlsCertificateCommand.ExecuteAsync(null);
        Assert.StartsWith("Export cancelled", settings.StatusMessage);

        picker.Cancel = false;
        await settings.ExportMtlsCertificateCommand.ExecuteAsync(null);

        var path = Assert.Single(picker.Picked);
        Assert.True(File.Exists(path));
        Assert.StartsWith($"Certificate saved to {path}", settings.StatusMessage);
    }

    /// <summary>The listen port is range-checked before it is saved.</summary>
    [Fact]
    public async Task TheListenPortIsRangeCheckedBeforeItIsSaved()
    {
        using var fixture = new ViewModelFixture();
        await fixture.StartSingleUseAsync();

        var settings = fixture.Get<SettingsViewModel>();
        var before = fixture.Store.Current.Settings.ListenPort;

        settings.ListenPort = 0;
        await settings.SavePortCommand.ExecuteAsync(null);
        Assert.Equal("Listen port must be between 1 and 65535.", settings.StatusMessage);
        Assert.Equal(before, fixture.Store.Current.Settings.ListenPort);

        settings.ListenPort = 5612;
        await settings.SavePortCommand.ExecuteAsync(null);
        Assert.Equal(5612, fixture.Store.Current.Settings.ListenPort);
    }

    /// <summary>
    /// The log buttons open what exists and say so when it does not, and pruning keeps the current
    /// log. All of it against the fixture's own log directory, never the developer's.
    /// </summary>
    [Fact]
    public async Task TheLogButtonsOpenWhatExistsAndPruneKeepsTheCurrentLog()
    {
        using var fixture = new ViewModelFixture();

        var settings = fixture.Get<SettingsViewModel>();
        var launcher = (RecordingLauncher)fixture.Get<RavensPort.UI.Services.IPlatformLauncher>();
        var log = fixture.Get<RavensPort.Core.Diagnostics.ActivityLog>();

        await settings.OpenErrorLogCommand.ExecuteAsync(null);
        Assert.Equal("No error log yet — nothing has failed.", settings.StatusMessage);

        await settings.OpenActivityLogCommand.ExecuteAsync(null);
        Assert.Equal("No activity log file yet.", settings.StatusMessage);

        settings.PruneLogsCommand.Execute(null);
        Assert.Equal("Nothing to prune — only the current log exists.", settings.StatusMessage);

        log.Log("TEST something happened");
        log.LogError("TEST something failed", new InvalidOperationException("boom"));

        await settings.OpenErrorLogCommand.ExecuteAsync(null);
        await settings.OpenActivityLogCommand.ExecuteAsync(null);
        await settings.OpenLogFolderCommand.ExecuteAsync(null);

        Assert.Equal([log.ErrorLogPath, log.CurrentLogPath, log.LogDirectory], launcher.Opened);

        settings.PruneLogsCommand.Execute(null);
        Assert.StartsWith("Pruned 1 log file(s)", settings.StatusMessage);
        Assert.True(File.Exists(log.CurrentLogPath));
    }

    /// <summary>The key summary counts what the configuration holds, and says what to do when empty.</summary>
    [Fact]
    public async Task TheKeySummaryCountsRoutesAndFunnels()
    {
        using var fixture = new ViewModelFixture();
        await fixture.StartSingleUseAsync();

        var settings = fixture.Get<SettingsViewModel>();
        Assert.StartsWith("No endpoints yet", settings.KeyLocationSummary);

        await fixture.Store.MutateAsync(store =>
            store.Routes.Add(new RouteMapping { PathPrefix = "/api", Key = ProxyKey.Generate() }));

        Assert.StartsWith("1 route(s) and 0 funnel(s)", settings.KeyLocationSummary);
    }
}
