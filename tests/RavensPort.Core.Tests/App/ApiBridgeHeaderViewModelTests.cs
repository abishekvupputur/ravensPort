using RavensPort.UI.ViewModels;
using RavensPort.Core.Models;

namespace RavensPort.Core.Tests.App;

/// <summary>
/// The custom-headers editor's view models — <see cref="McpApiBridgeHeaderItemViewModel"/> and the
/// header-editing half of <see cref="ApiBridgeItemViewModel"/>. Both are plain CommunityToolkit.Mvvm
/// objects with no WPF runtime dependency, so they run here exactly like anything in Core: no
/// Window, no Dispatcher, no STA thread required.
///
/// The thing worth pinning is the same "may I become this?" contract routes already have — a
/// rejected edit must leave the record untouched and roll the box back to what is actually stored,
/// not just refuse silently.
/// </summary>
public class ApiBridgeHeaderViewModelTests
{
    private static McpApiBridgeRecord Bridge(params McpApiBridgeHeader[] headers) => new()
    {
        Name = "gh",
        Slug = "gh",
        Headers = [.. headers],
    };

    private static ApiBridgeItemViewModel ItemViewModel(
        McpApiBridgeRecord bridge,
        List<(ApiBridgeItemViewModel, string)>? changes = null,
        List<string>? invalids = null) =>
        new(
            bridge,
            route: null,
            listenPort: 5559,
            onChanged: (item, message) => changes?.Add((item, message)),
            onStatus: message => invalids?.Add(message),
            isMtls: false,
            clipboard: new NoDesktop());

    [Fact]
    public void StartsWithARowPerHeaderAlreadyOnTheBridge()
    {
        var bridge = Bridge(new McpApiBridgeHeader { Name = "User-Agent", Value = "RavensPort" });
        var viewModel = ItemViewModel(bridge);

        var row = Assert.Single(viewModel.Headers);
        Assert.Equal("User-Agent", row.Name);
        Assert.Equal("RavensPort", row.Value);
        Assert.True(viewModel.HasHeaders);
    }

    [Fact]
    public void AddHeaderAppendsAnEmptyRowToBothTheRecordAndTheCollection()
    {
        var bridge = Bridge();
        var changes = new List<(ApiBridgeItemViewModel, string)>();
        var viewModel = ItemViewModel(bridge, changes);

        viewModel.AddHeaderCommand.Execute(null);

        Assert.Single(bridge.Headers);
        Assert.Single(viewModel.Headers);
        Assert.Same(bridge.Headers[0], viewModel.Headers[0].Model);
        Assert.NotEmpty(changes);
    }

    [Fact]
    public void RemoveHeaderDropsTheRowFromBothTheRecordAndTheCollection()
    {
        var header = new McpApiBridgeHeader { Name = "User-Agent", Value = "x" };
        var bridge = Bridge(header);
        var changes = new List<(ApiBridgeItemViewModel, string)>();
        var viewModel = ItemViewModel(bridge, changes);
        var row = viewModel.Headers[0];

        viewModel.RemoveHeaderCommand.Execute(row);

        Assert.Empty(bridge.Headers);
        Assert.Empty(viewModel.Headers);
        Assert.False(viewModel.HasHeaders);
        Assert.Contains(changes, c => c.Item2.Contains("User-Agent", StringComparison.Ordinal));
    }

    [Fact]
    public void RenamingAHeaderToCollideWithAnotherIsRejectedAndRollsBack()
    {
        var bridge = Bridge(
            new McpApiBridgeHeader { Name = "User-Agent", Value = "a" },
            new McpApiBridgeHeader { Name = "X-Api-Version", Value = "b" });

        var invalids = new List<string>();
        var viewModel = ItemViewModel(bridge, invalids: invalids);
        var second = viewModel.Headers[1];

        second.Name = "User-Agent";

        // Refused: the underlying model is untouched, so the record still has two distinct names.
        Assert.Equal("X-Api-Version", second.Model.Name);
        Assert.Equal("X-Api-Version", bridge.Headers[1].Name);
        Assert.NotEmpty(invalids);
    }

    [Fact]
    public void RenamingAHeaderToAReservedNameIsRejected()
    {
        var bridge = Bridge(new McpApiBridgeHeader { Name = "User-Agent", Value = "a" });
        var invalids = new List<string>();
        var viewModel = ItemViewModel(bridge, invalids: invalids);

        viewModel.Headers[0].Name = "Authorization";

        Assert.Equal("User-Agent", bridge.Headers[0].Name);
        Assert.Contains(invalids, message => message.Contains("Authorization", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EditingAHeadersValueWritesThroughOnceValid()
    {
        var header = new McpApiBridgeHeader { Name = "User-Agent", Value = "old" };
        var bridge = Bridge(header);
        var viewModel = ItemViewModel(bridge);

        viewModel.Headers[0].Value = "new";

        Assert.Equal("new", header.Value);
        Assert.Equal("new", viewModel.Headers[0].Value);
    }
}
