using RavensPort.Core.Models;
using RavensPort.Core.Vault;
using RavensPort.UI.ViewModels;

namespace RavensPort.UI.Tests;

/// <summary>
/// The two small view models that are pure functions of what they were handed.
///
/// Neither touches a vault, a manager or a window, so both are worth testing directly rather than
/// through a screen: every state they can be in is one line to construct, and several of those
/// states are ones a running app reaches rarely and a UI test would have to contrive.
/// </summary>
public class CardAndKeyUnitTests
{
    /// <summary>
    /// A manager card says what the probe found, in the words the setup page shows.
    ///
    /// Every availability maps to a sentence, and the hedged ones are hedged deliberately: a
    /// discovery probe ran the binary and asked it nothing else, so "locked" or "signed out" would
    /// be a guess. Pinned here because a wrong label sends someone to fix the wrong thing.
    /// </summary>
    [Theory]
    [InlineData(VaultAvailability.NotInstalled, "Not installed")]
    [InlineData(VaultAvailability.NotConnected, "not connected")]
    [InlineData(VaultAvailability.NotSignedIn, "Locked or signed out")]
    [InlineData(VaultAvailability.VaultChoiceNeeded, "Choose a vault")]
    public void ACardDescribesWhatTheProbeFound(VaultAvailability availability, string expected)
    {
        var card = new ManagerCardViewModel(
            new VaultStatus(VaultBackendKind.OnePassword, availability));

        Assert.Contains(expected, card.StateLabel, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(availability, card.Availability);
        Assert.False(string.IsNullOrWhiteSpace(card.Name));
    }

    /// <summary>
    /// A ready card names the vault it is ready with, because "Ready" alone does not say which.
    /// </summary>
    [Fact]
    public void AReadyCardNamesItsVault()
    {
        var card = new ManagerCardViewModel(new VaultStatus(
            VaultBackendKind.OnePassword, VaultAvailability.Ready, VaultName: "RavensPort-work"));

        Assert.True(card.IsReady);
        Assert.Contains("RavensPort-work", card.StateLabel);
        Assert.False(card.ShowInstall, "a ready manager does not need installing");
    }

    /// <summary>
    /// Proton Pass gets its own wording for the same state, because for it the answer is not hedged.
    /// </summary>
    [Fact]
    public void ProtonPassSaysNotSignedInRatherThanHedging()
    {
        var card = new ManagerCardViewModel(
            new VaultStatus(VaultBackendKind.ProtonPass, VaultAvailability.NotSignedIn));

        Assert.Equal("Not signed in", card.StateLabel);
    }

    /// <summary>
    /// A card offers the vaults it could adopt, and a card with none offers nothing to choose.
    /// </summary>
    [Fact]
    public void ACardOffersOnlyTheVaultsTheManagerReported()
    {
        var withVaults = new ManagerCardViewModel(new VaultStatus(
            VaultBackendKind.OnePassword,
            VaultAvailability.VaultChoiceNeeded,
            AdoptableVaults: ["one", "two"]));

        Assert.Equal(2, withVaults.Vaults.Count);

        var without = new ManagerCardViewModel(
            new VaultStatus(VaultBackendKind.OnePassword, VaultAvailability.NotInstalled));

        Assert.Empty(without.Vaults);
    }

    /// <summary>
    /// A proxy key is hidden until asked for, and the mask is a fixed width.
    ///
    /// Fixed on purpose: a mask that matched the key's length would leak it, which is the sort of
    /// thing that is obvious once said and invisible in a screenshot.
    /// </summary>
    [Fact]
    public void AKeyIsMaskedUntilItIsShown()
    {
        var vm = Key(new ProxyKey { Value = "a-very-long-proxy-key-value" });

        Assert.DoesNotContain("a-very-long", vm.Display);
        Assert.Equal("Show", vm.ToggleLabel);

        vm.IsVisible = true;

        Assert.Equal("a-very-long-proxy-key-value", vm.Display);
        Assert.Equal("Hide", vm.ToggleLabel);
    }

    /// <summary>
    /// A key past its expiry says so, and one without an expiry never does.
    /// </summary>
    [Fact]
    public void AnExpiredKeyIsReportedAsExpired()
    {
        var expired = Key(new ProxyKey
        {
            Value = "old",
            ExpiresUtc = DateTimeOffset.UtcNow.AddDays(-1),
        });

        Assert.True(expired.IsExpired);
        Assert.False(string.IsNullOrWhiteSpace(expired.ExpirySummary));

        var forever = Key(new ProxyKey { Value = "eternal" });

        Assert.False(forever.IsExpired, "a key with no expiry cannot be past it");
    }

    private static ProxyKeyViewModel Key(ProxyKey key) =>
        new(key, owner: "route /app/test", onChanged: _ => { }, onStatus: _ => { }, clipboard: new NoClipboard());

    /// <summary>Nothing here copies anything; the key's own display is the subject.</summary>
    private sealed class NoClipboard : RavensPort.UI.Services.IClipboardService
    {
        public Task SetTextAsync(string text) => Task.CompletedTask;
    }
}
