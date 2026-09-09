using Microsoft.Extensions.DependencyInjection;
using RavensPort.Core.Storage;
using RavensPort.Core.Vault;

namespace RavensPort.UI.Tests;

/// <summary>
/// The approval suite's opening question, asked of the single-use button: does pressing it leave a
/// running proxy with an empty configuration behind it?
/// </summary>
public class SingleUseUiTests
{
    [Fact]
    public Task PressingStartInSingleUseOpensTheGateOnAnEmptyStore() => UiSession.RunAsync(async () =>
    {
        await using var harness = await SingleUseHarness.StartAsync();

        var gate = harness.Services.GetRequiredService<VaultGateService>();
        Assert.False(gate.IsSingleUse, "the gate should be shut before anything is pressed");

        await harness.StartSingleUseThroughTheUiAsync();

        Assert.True(gate.IsSingleUse);

        // Stage 1's assertion, against the store the button just selected: a session that begins
        // with anything in it would make every later stage's "it is there now" meaningless.
        var store = harness.Services.GetRequiredService<ConfigStoreCache>().Current;
        Assert.Empty(store.Credentials);
        Assert.Empty(store.Routes);
        Assert.Empty(store.McpSources);
    });
}
