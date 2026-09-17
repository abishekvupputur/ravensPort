using RavensPort.App.ViewModels;
using RavensPort.Core.Models;

namespace RavensPort.Core.Tests.App;

/// <summary>
/// The OpenAPI operation picker's view model. It is a plain CommunityToolkit.Mvvm object — the tree
/// it builds is its own, not a WPF CollectionView — so it runs here like the other view models: no
/// Window, no Dispatcher, no STA thread.
///
/// What is worth pinning is the workflow a thousand-operation spec forces: group the list so it is
/// readable, search to narrow it, then tick what you found — either wholesale or a group at a time.
/// Every one of those steps is only correct if "select all", at any level, means "all of what the
/// search is showing" rather than "all of the document".
/// </summary>
public class OpenApiOperationPickerViewModelTests
{
    private static readonly OpenApiOperationSummary[] Operations =
    [
        new("/repos/{owner}/{repo}", "GET", "repos_get", "Get a repository", ["repos"]),
        new("/repos/{owner}/{repo}", "DELETE", "repos_delete", "Delete a repository", ["repos"]),
        new("/repos/{owner}/{repo}/issues", "GET", "issues_list", "List issues", ["issues"]),
        new("/repos/{owner}/{repo}/issues", "POST", "issues_create", "Create an issue", ["issues"]),
        new("/user", "GET", "users_get-authenticated", "Get the authenticated user", ["users"]),
        new("/octocat", "GET", "meta_octocat", "Get Octocat", []),
    ];

    private static OpenApiOperationPickerViewModel ViewModel() => new("{}", "spec.json", Operations);

    private static OpenApiOperationRowViewModel Row(OpenApiOperationPickerViewModel viewModel, string operationId) =>
        viewModel.Rows.Single(row => row.OperationId == operationId);

    private static IReadOnlyList<OpenApiOperationGroupViewModel> Groups(OpenApiOperationPickerViewModel viewModel) =>
        [.. viewModel.Tree.OfType<OpenApiOperationGroupViewModel>()];

    // ---- search ---------------------------------------------------------------------------------

    [Fact]
    public void APlainWordMatchesAnywhereInAnyField()
    {
        var viewModel = ViewModel();

        viewModel.SearchText = "issues";
        Assert.Equal(2, viewModel.VisibleRows.Count());

        // Matched on method, then on summary, then on tag.
        viewModel.SearchText = "delete";
        Assert.Equal("repos_delete", Assert.Single(viewModel.VisibleRows).OperationId);

        viewModel.SearchText = "octocat";
        Assert.Equal("meta_octocat", Assert.Single(viewModel.VisibleRows).OperationId);
    }

    [Theory]
    [InlineData("/repos/*", 4)]
    [InlineData("/user", 1)]
    [InlineData("*issues*", 2)]
    [InlineData("issues_*", 2)]
    [InlineData("repos_???", 1)]
    [InlineData("GET", 4)]
    [InlineData("/nothing/*", 0)]
    public void WildcardsMatchTheWholeField(string search, int expected)
    {
        var viewModel = ViewModel();

        viewModel.SearchText = search;

        Assert.Equal(expected, viewModel.VisibleRows.Count());
    }

    [Fact]
    public void AWildcardPatternIsAnchoredWhereAPlainWordIsNot()
    {
        var viewModel = ViewModel();

        // "repos" appears inside several paths and operation IDs...
        viewModel.SearchText = "repos";
        Assert.Equal(4, viewModel.VisibleRows.Count());

        // ...but as a glob it has to be the whole field, and no field is exactly "repos".
        viewModel.SearchText = "repos*";
        Assert.Equal(2, viewModel.VisibleRows.Count());
    }

    [Fact]
    public void ARegexMetacharacterIsMatchedLiterallyRatherThanCompiled()
    {
        var viewModel = ViewModel();

        // Would be a catastrophic pattern if this were passed to Regex unescaped.
        viewModel.SearchText = "(a+)+$";
        Assert.Empty(viewModel.VisibleRows);

        viewModel.SearchText = "{owner}";
        Assert.Equal(4, viewModel.VisibleRows.Count());
    }

    // ---- grouping -------------------------------------------------------------------------------

    [Fact]
    public void GroupsByTagByDefault()
    {
        var viewModel = ViewModel();

        Assert.Equal(OpenApiGroupingMode.Tag, viewModel.Grouping.Mode);
        Assert.Equal(["(untagged)", "issues", "repos", "users"], Groups(viewModel).Select(group => group.Label));
        Assert.Equal(2, Groups(viewModel).Single(group => group.Label == "repos").Leaves.Count);
    }

    [Fact]
    public void GroupsByPathAcrossTwoLevelsSkippingPlaceholderSegments()
    {
        var viewModel = ViewModel();

        viewModel.Grouping = viewModel.GroupingOptions.Single(option => option.Mode == OpenApiGroupingMode.Path);

        Assert.Equal(["/octocat", "/repos", "/user"], Groups(viewModel).Select(group => group.Label));

        var repos = Groups(viewModel).Single(group => group.Label == "/repos");

        // /repos/{owner}/{repo} and /repos/{owner}/{repo}/issues — the {owner}/{repo} placeholders
        // are skipped, so the second level is "issues" versus the bare repository itself.
        var second = repos.Children.OfType<OpenApiOperationGroupViewModel>().Select(group => group.Label);
        Assert.Equal(["(top level)", "issues"], second);
        Assert.Equal(4, repos.Leaves.Count);
    }

    [Fact]
    public void GroupingByNothingGivesAFlatListOfRows()
    {
        var viewModel = ViewModel();

        viewModel.Grouping = viewModel.GroupingOptions.Single(option => option.Mode == OpenApiGroupingMode.None);

        Assert.Empty(Groups(viewModel));
        Assert.Equal(Operations.Length, viewModel.Tree.OfType<OpenApiOperationRowViewModel>().Count());
    }

    [Fact]
    public void TheTreeOnlyHoldsWhatTheSearchIsShowing()
    {
        var viewModel = ViewModel();

        viewModel.SearchText = "issues";

        var group = Assert.Single(Groups(viewModel));
        Assert.Equal("issues", group.Label);
        Assert.Equal(2, group.Leaves.Count);
    }

    // ---- selection ------------------------------------------------------------------------------

    [Fact]
    public void EverythingStartsSelected()
    {
        var viewModel = ViewModel();

        Assert.All(viewModel.Rows, row => Assert.True(row.IsSelected));
        Assert.Equal("6 of 6 selected", viewModel.SelectionSummary);
        Assert.All(Groups(viewModel), group => Assert.True(group.IsSelected));
    }

    [Fact]
    public void AGroupTickBoxSelectsAndClearsEverythingUnderIt()
    {
        var viewModel = ViewModel();
        viewModel.SelectNoneCommand.Execute(null);

        var repos = Groups(viewModel).Single(group => group.Label == "repos");
        repos.IsSelected = true;

        Assert.Equal("2 of 6 selected", viewModel.SelectionSummary);
        Assert.True(Row(viewModel, "repos_get").IsSelected);
        Assert.True(Row(viewModel, "repos_delete").IsSelected);
        Assert.False(Row(viewModel, "issues_list").IsSelected);

        repos.IsSelected = false;

        Assert.Equal("0 of 6 selected", viewModel.SelectionSummary);
        Assert.All(viewModel.Rows, row => Assert.False(row.IsSelected));
    }

    [Fact]
    public void AGroupReadsBackAsHalfTickedWhenOnlySomeOfItIsSelected()
    {
        var viewModel = ViewModel();
        var repos = Groups(viewModel).Single(group => group.Label == "repos");

        Assert.True(repos.IsSelected);

        Row(viewModel, "repos_delete").IsSelected = false;
        Assert.Null(repos.IsSelected);

        Row(viewModel, "repos_get").IsSelected = false;
        Assert.False(repos.IsSelected);
    }

    [Fact]
    public void AnOuterGroupFollowsTheLevelsBeneathIt()
    {
        var viewModel = ViewModel();
        viewModel.Grouping = viewModel.GroupingOptions.Single(option => option.Mode == OpenApiGroupingMode.Path);
        viewModel.SelectNoneCommand.Execute(null);

        var repos = Groups(viewModel).Single(group => group.Label == "/repos");
        var issues = repos.Children.OfType<OpenApiOperationGroupViewModel>().Single(group => group.Label == "issues");

        issues.IsSelected = true;

        // The inner group is full, so the outer one is partly filled — not ticked, not clear.
        Assert.True(issues.IsSelected);
        Assert.Null(repos.IsSelected);
        Assert.Equal("2 of 6 selected", viewModel.SelectionSummary);

        repos.IsSelected = true;
        Assert.True(issues.IsSelected);
        Assert.Equal("4 of 6 selected", viewModel.SelectionSummary);

        repos.IsSelected = false;
        Assert.False(issues.IsSelected);
        Assert.Equal("0 of 6 selected", viewModel.SelectionSummary);
    }

    [Fact]
    public void SelectAllAndSelectNoneActOnTheSearchResultsNotTheWholeList()
    {
        var viewModel = ViewModel();

        viewModel.SelectNoneCommand.Execute(null);
        viewModel.SearchText = "issues";
        viewModel.SelectAllCommand.Execute(null);

        Assert.Equal("2 of 6 selected", viewModel.SelectionSummary);

        viewModel.SearchText = "";
        Assert.Equal("2 of 6 selected", viewModel.SelectionSummary);
        Assert.True(Row(viewModel, "issues_list").IsSelected);
        Assert.False(Row(viewModel, "repos_get").IsSelected);
    }

    [Fact]
    public void AGroupTickBoxOnlyReachesTheRowsTheSearchLeftInIt()
    {
        var viewModel = ViewModel();
        viewModel.SelectNoneCommand.Execute(null);

        // "repos" the tag covers two operations; this search leaves one of them showing.
        viewModel.SearchText = "repos_get";

        Groups(viewModel).Single(group => group.Label == "repos").IsSelected = true;

        viewModel.SearchText = "";

        Assert.True(Row(viewModel, "repos_get").IsSelected);
        Assert.False(Row(viewModel, "repos_delete").IsSelected);
    }

    [Fact]
    public void SelectionSurvivesRegroupingAndResearching()
    {
        var viewModel = ViewModel();
        viewModel.SelectNoneCommand.Execute(null);
        Row(viewModel, "issues_create").IsSelected = true;

        viewModel.Grouping = viewModel.GroupingOptions.Single(option => option.Mode == OpenApiGroupingMode.Method);
        viewModel.SearchText = "POST";

        var post = Groups(viewModel).Single(group => group.Label == "POST");

        Assert.True(post.IsSelected);
        Assert.Equal("1 of 6 selected", viewModel.SelectionSummary);
    }

    [Fact]
    public void ImportIsRefusedWhileNothingIsSelected()
    {
        var viewModel = ViewModel();

        Assert.True(viewModel.ImportCommand.CanExecute(null));

        viewModel.SelectNoneCommand.Execute(null);

        // Without this the button stays live, Convert() is handed an empty set, and the user is
        // told the *document* has nothing importable in it — which blames the spec for a choice
        // they made in this window.
        Assert.False(viewModel.ImportCommand.CanExecute(null));

        viewModel.Rows[0].IsSelected = true;
        Assert.True(viewModel.ImportCommand.CanExecute(null));
    }

    [Fact]
    public void BothCapsAreShownAgainstTheirLimitAsTheSelectionChanges()
    {
        var viewModel = ViewModel();

        Assert.Equal($"6 / {McpApiBridgeValidation.MaxTools} tools", viewModel.ToolCountSummary);
        Assert.False(viewModel.IsOverToolCap);
        Assert.False(viewModel.IsOverSizeCap);

        viewModel.SelectNoneCommand.Execute(null);

        Assert.Equal($"0 / {McpApiBridgeValidation.MaxTools} tools", viewModel.ToolCountSummary);
        Assert.StartsWith("0 / ", viewModel.SizeSummary);
    }

    [Fact]
    public void GoingOverTheToolCapIsFlaggedTheMomentItHappens()
    {
        var operations = Enumerable.Range(0, McpApiBridgeValidation.MaxTools + 1)
            .Select(i => new OpenApiOperationSummary($"/thing{i}", "GET", $"op{i}", null, []))
            .ToList();

        var viewModel = new OpenApiOperationPickerViewModel("{}", "spec.json", operations);

        Assert.True(viewModel.IsOverToolCap);
        Assert.Equal($"{operations.Count} / {McpApiBridgeValidation.MaxTools} tools", viewModel.ToolCountSummary);

        viewModel.Rows[0].IsSelected = false;

        Assert.False(viewModel.IsOverToolCap);
        Assert.Equal($"{McpApiBridgeValidation.MaxTools} / {McpApiBridgeValidation.MaxTools} tools", viewModel.ToolCountSummary);
    }

    [Fact]
    public void GoingOverTheSizeCapIsFlaggedEvenWhileUnderTheToolCap()
    {
        // Ten tools is well inside the 64-tool cap, but each carries a description at the maximum
        // length — which is exactly the case the byte cap exists for, and the one a count alone
        // would say nothing about.
        var fat = new string('x', McpApiBridgeValidation.MaxDescriptionLength);

        var operations = Enumerable.Range(0, 40)
            .Select(i => new OpenApiOperationSummary(
                $"/thing{i}", "GET", $"op{i}", fat, [], EstimatedBytes: 4 * 1024))
            .ToList();

        var viewModel = new OpenApiOperationPickerViewModel("{}", "spec.json", operations, baseManifestBytes: 200);

        Assert.False(viewModel.IsOverToolCap);
        Assert.True(viewModel.IsOverSizeCap);
        Assert.Equal($"160 / {McpApiBridgeValidation.MaxManifestBytes / 1024} KB", viewModel.SizeSummary);

        viewModel.SelectNoneCommand.Execute(null);
        Assert.False(viewModel.IsOverSizeCap);
    }

    [Fact]
    public void TheEstimatedSizeTracksWhatTheImportActuallyWeighs()
    {
        var paths = Enumerable.Range(0, 20).Select(i => $$"""
            "/things/{{i}}": {
              "get": {
                "operationId": "getThing{{i}}",
                "summary": "Fetch thing number {{i}} from the store, with all of its fields.",
                "parameters": [
                  { "name": "id", "in": "path", "required": true, "schema": { "type": "string" } },
                  { "name": "verbose", "in": "query", "schema": { "type": "boolean" } }
                ],
                "responses": { "200": { "description": "ok" } }
              }
            }
            """);

        var spec = $$"""
            {
              "openapi": "3.0.0",
              "info": { "title": "Test API", "version": "1", "description": "A test document." },
              "paths": { {{string.Join(",\n", paths)}} }
            }
            """;

        var (error, operations, baseBytes) = OpenApiImporter.Discover(spec);
        Assert.Null(error);

        var viewModel = new OpenApiOperationPickerViewModel(spec, "spec.json", operations, baseBytes);
        viewModel.SelectNoneCommand.Execute(null);

        foreach (var row in viewModel.Rows.Take(7)) row.IsSelected = true;

        viewModel.ImportCommand.Execute(null);

        var manifestJson = viewModel.Result!.ManifestJson!;
        var readBack = McpApiBridgeValidation.TryReadManifest(manifestJson, out var manifest);
        Assert.Null(readBack);

        var actual = McpApiBridgeValidation.MeasureBytes(manifest!);
        var estimated = viewModel.EstimatedManifestBytes;

        // Only the commas between array entries are unaccounted for, so this is close rather than
        // merely the right order of magnitude. A loose bound here would let the estimate rot.
        Assert.InRange(estimated, actual - 64, actual + 64);
    }

    // ---- the result ------------------------------------------------------------------------------

    [Fact]
    public void ImportBuildsOnlyTheSelectedOperationsAndAsksToClose()
    {
        const string spec = """
            {
              "openapi": "3.0.0",
              "info": { "title": "Test API", "version": "1" },
              "paths": {
                "/things": { "get": { "operationId": "listThings", "responses": { "200": { "description": "ok" } } } },
                "/widgets": { "get": { "operationId": "listWidgets", "responses": { "200": { "description": "ok" } } } }
              }
            }
            """;

        var (error, operations, _) = OpenApiImporter.Discover(spec);
        Assert.Null(error);

        var viewModel = new OpenApiOperationPickerViewModel(spec, "spec.json", operations);
        bool? closedWith = null;
        viewModel.CloseRequested += confirmed => closedWith = confirmed;

        viewModel.SelectNoneCommand.Execute(null);
        viewModel.Rows.Single(row => row.Path == "/widgets").IsSelected = true;

        viewModel.ImportCommand.Execute(null);

        Assert.True(closedWith);
        Assert.NotNull(viewModel.Result);
        Assert.Null(viewModel.Result!.Error);

        var readBack = McpApiBridgeValidation.TryReadManifest(viewModel.Result.ManifestJson, out var manifest);

        Assert.Null(readBack);
        Assert.Equal("listWidgets", Assert.Single(manifest!.Tools).Name);
    }

    [Fact]
    public void CancellingAsksToCloseWithoutBuildingAnything()
    {
        var viewModel = ViewModel();
        bool? closedWith = null;
        viewModel.CloseRequested += confirmed => closedWith = confirmed;

        viewModel.CancelCommand.Execute(null);

        Assert.False(closedWith);
        Assert.Null(viewModel.Result);
    }
}
