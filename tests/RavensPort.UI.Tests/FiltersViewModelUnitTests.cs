using RavensPort.Core.Models;
using RavensPort.UI.ViewModels;

namespace RavensPort.UI.Tests;

public class FiltersViewModelUnitTests
{
    private static readonly string AwsKey = "AKIA" + "Z7QW3XKPLM2N4R6T";

    /// <summary>
    /// A single-use session holding one funnel pointed at one profile ("agent", with
    /// <paramref name="filters"/>), and the tab reloaded to show them.
    /// </summary>
    private static async Task<(ViewModelFixture Fixture, FiltersViewModel Vm)> WithFunnelAsync(params FunnelFilter[] filters)
    {
        var fixture = new ViewModelFixture();
        await fixture.StartSingleUseAsync();
        await fixture.Store.MutateAsync(store =>
        {
            var profile = new FilterProfileRecord { Name = "agent", Filters = [.. filters] };
            store.FilterProfiles.Add(profile);
            store.McpFunnels.Add(new McpFunnelRecord { Name = "agent", Slug = "agent", FilterProfileId = profile.Id });
        });

        var vm = fixture.Get<FiltersViewModel>();
        vm.Reload();
        return (fixture, vm);
    }

    [Fact]
    public async Task EveryProfileGetsACard_WithItsFourStepsInOrder()
    {
        var (fixture, vm) = await WithFunnelAsync();
        using var _ = fixture;

        var card = Assert.Single(vm.Profiles);
        Assert.Equal(["1 · Secrets", "2 · Personal data", "3 · Entities", "4 · Meaning"], card.Rows.Select(r => r.Title));
        Assert.Same(card, vm.TestProfile);
        Assert.Equal("Used by agent", card.UsedBySummary);
    }

    [Fact]
    public async Task AFunnelsOwnFilters_BecomeAProfileOfTheSameName_TheFirstTimeTheTabLoads()
    {
        var fixture = new ViewModelFixture();
        using var _ = fixture;
        await fixture.StartSingleUseAsync();
        await fixture.Store.MutateAsync(store => store.McpFunnels.Add(new McpFunnelRecord
        {
            Name = "claude-code", Slug = "claude-code",
            Filters = [new FunnelFilter { Category = FilterCategory.Secrets, Level = FilterLevel.Balanced }],
        }));

        var vm = fixture.Get<FiltersViewModel>();
        vm.Reload();

        var profile = Assert.Single(fixture.Store.Current.FilterProfiles);
        var funnel = fixture.Store.Current.McpFunnels.Single();
        Assert.Equal("claude-code", profile.Name);
        Assert.Equal(FilterCategory.Secrets, profile.Filters.Single().Category);
        Assert.Equal(profile.Id, funnel.FilterProfileId);
        Assert.Empty(funnel.Filters);
    }

    [Fact]
    public async Task ProfilesAreAddedDuplicatedRenamed_AndCannotBeRemovedWhileInUse()
    {
        var (fixture, vm) = await WithFunnelAsync(new FunnelFilter { Category = FilterCategory.Secrets, Level = FilterLevel.Balanced });
        using var _ = fixture;

        vm.NewProfileName = "agent";
        vm.AddProfileCommand.Execute(null);
        Assert.Contains("already exists", vm.NewProfileError);

        vm.Profiles.Single().DuplicateCommand.Execute(null);
        var copy = vm.Profiles.Single(p => p.Name == "agent copy");
        Assert.Equal(FilterLevel.Balanced, copy.Record.Filters.Single().Level);
        Assert.NotSame(vm.Profiles.Single(p => p.Name == "agent").Record.Filters, copy.Record.Filters);

        copy.NameText = "Strict";
        Assert.Equal("Strict", fixture.Store.Current.FilterProfiles.Single(p => p.Id == copy.Record.Id).Name);

        vm.Profiles.Single(p => p.Name == "agent").RemoveCommand.Execute(null);
        Assert.Contains("used by", vm.StatusMessage);
        Assert.Equal(2, vm.Profiles.Count);

        vm.Profiles.Single(p => p.Name == "Strict").RemoveCommand.Execute(null);
        Assert.Single(vm.Profiles);
    }

    [Fact]
    public async Task AnUpstreamInAFunnel_CanBePointedAtItsOwnProfile_OrBackToTheFunnels()
    {
        var (fixture, vm) = await WithFunnelAsync();
        using var _ = fixture;
        var source = new McpSourceRecord { Name = "kb", Alias = "kb", Url = "https://kb.example.com/mcp" };
        await fixture.Store.MutateAsync(store =>
        {
            store.McpSources.Add(source);
            store.McpFunnels.Single().Sources.Add(new McpFunnelSource { SourceId = source.Id });
        });
        vm.NewProfileName = "Trusted";
        vm.AddProfileCommand.Execute(null);

        var upstream = vm.Assignments.Single().Upstreams.Single();
        Assert.Equal("Same as the funnel", upstream.SelectedProfile!.Label);

        upstream.SelectedProfile = upstream.Choices.Single(c => c.Label == "Trusted");
        var link = fixture.Store.Current.McpFunnels.Single().Sources.Single();
        Assert.Equal(vm.Profiles.Single(p => p.Name == "Trusted").Record.Id, link.FilterProfileId);
        Assert.Equal("Used by agent › kb", vm.Profiles.Single(p => p.Name == "Trusted").UsedBySummary);

        upstream.SelectedProfile = upstream.Choices.Single(c => c.Id is null);
        Assert.Null(link.FilterProfileId);
    }

    [Fact]
    public async Task Secrets_IsOffOrDeny_WithNoActionToChoose()
    {
        var (fixture, vm) = await WithFunnelAsync();
        using var _ = fixture;
        var secrets = vm.Profiles[0].Rows.Single(r => r.IsSecrets);

        Assert.Equal(["Off", "Deny"], secrets.LevelChoices.Select(c => c.Label));
        Assert.False(secrets.HasAction);

        secrets.SelectedLevelChoice = secrets.LevelChoices.Single(c => c.Label == "Deny");

        var stored = vm.Profiles[0].Record.Filters.Single();
        Assert.NotEqual(FilterLevel.Off, stored.Level);
        Assert.Equal(FilterAction.Block, stored.Action);
    }

    [Fact]
    public async Task PersonalData_ChipsSwitchPatterns_StoringOnlyWhatDiffersFromTheDefault()
    {
        var (fixture, vm) = await WithFunnelAsync();
        using var _ = fixture;
        var row = vm.Profiles[0].Rows.Single(r => r.IsPersonalData);
        row.SelectedLevelChoice = row.LevelChoices.Single(c => c.Label == "On");

        row.Chips.Single(c => c.Name == "Email address").ToggleCommand.Execute(null); // on by default → off
        row.Chips.Single(c => c.Name == "IPv4 address").ToggleCommand.Execute(null);  // off by default → on

        var stored = vm.Profiles[0].Record.Filters.Single();
        Assert.Equal(["Email address"], stored.DisabledDetectors);
        Assert.Equal(["IPv4 address"], stored.EnabledDetectors);

        row.Chips.Single(c => c.Name == "Email address").ToggleCommand.Execute(null);
        Assert.Empty(stored.DisabledDetectors);
    }

    [Fact]
    public async Task ABuiltInRegexCanBeEdited_RefusedWhenBroken_AndReset()
    {
        var (fixture, vm) = await WithFunnelAsync();
        using var _ = fixture;
        var row = vm.Profiles[0].Rows.Single(r => r.IsPersonalData);
        row.SelectedLevelChoice = row.LevelChoices.Single(c => c.Label == "On");
        var iban = row.PatternRows.Single(p => p.Name == "IBAN");

        iban.Regex = "(broken";
        Assert.Contains("Not a valid", iban.Error);
        Assert.Empty(vm.Profiles[0].Record.Filters.Single().RegexOverrides);

        iban.Regex = @"\bDE\d{20}\b";
        Assert.Equal("", iban.Error);
        Assert.True(iban.CanReset);
        Assert.Equal(@"\bDE\d{20}\b", vm.Profiles[0].Record.Filters.Single().RegexOverrides["IBAN"]);

        iban.ResetCommand.Execute(null);
        Assert.Empty(vm.Profiles[0].Record.Filters.Single().RegexOverrides);
    }

    [Fact]
    public async Task ACustomPatternIsCheckedBeforeItIsAdded_ThenShowsAsAChipAndARow()
    {
        var (fixture, vm) = await WithFunnelAsync();
        using var _ = fixture;
        var row = vm.Profiles[0].Rows.Single(r => r.IsPersonalData);
        row.SelectedLevelChoice = row.LevelChoices.Single(c => c.Label == "On");

        row.NewPatternName = "Customer id";
        row.NewPatternRegex = "(broken";
        row.AddPatternCommand.Execute(null);
        Assert.Contains("Not a valid", row.PatternError);

        row.NewPatternRegex = @"\bKD-\d{6}\b";
        row.AddPatternCommand.Execute(null);

        Assert.Equal("", row.PatternError);
        Assert.True(row.Chips.Single(c => c.Name == "Customer id").IsCustom);

        row.PatternRows.Single(p => p.Name == "Customer id").ResetCommand.Execute(null);
        Assert.Empty(vm.Profiles[0].Record.Filters.Single().Patterns);
    }

    [Fact]
    public async Task VoyageAndOpenAi_NeedAKey_AndARemoteApiIsFlagged()
    {
        var (fixture, vm) = await WithFunnelAsync();
        using var _ = fixture;

        vm.NewProviderKindChoice = vm.ProviderKinds.Single(k => k.Kind == EmbeddingProviderKind.VoyageAI);
        vm.NewProviderName = "voyage";
        vm.AddProviderCommand.Execute(null);

        Assert.Contains("API key", vm.NewProviderError);
        Assert.True(vm.NewIsRemote);
        Assert.Equal("voyage-4-lite", vm.NewProviderModel);

        vm.NewProviderKindChoice = vm.ProviderKinds.Single(k => k.Kind == EmbeddingProviderKind.Ollama);
        Assert.False(vm.NewIsRemote);
        vm.AddProviderCommand.Execute(null);

        Assert.Equal("", vm.NewProviderError);
        Assert.Single(vm.Providers);
    }

    [Fact]
    public async Task AnEntityModelNeedsItsTokenizerFiles()
    {
        var (fixture, vm) = await WithFunnelAsync();
        using var _ = fixture;
        var dir = Directory.CreateTempSubdirectory("ravensport-gliner-").FullName;
        try
        {
            var model = Path.Combine(dir, "model.onnx");
            File.WriteAllText(model, "");

            vm.NewEntityName = "gliner";
            vm.NewEntityPath = model;
            vm.AddEntityModelCommand.Execute(null);
            Assert.Contains("spm.model", vm.NewEntityError);

            File.WriteAllText(Path.Combine(dir, "spm.model"), "");
            File.WriteAllText(Path.Combine(dir, "gliner_config.json"), "{}");
            vm.AddEntityModelCommand.Execute(null);

            Assert.Equal("", vm.NewEntityError);
            Assert.Single(vm.EntityModels);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Step4_ChoosesCpuOrGpuForItsOnnxModel_AndTheModelRecordChanges()
    {
        var provider = new EmbeddingProviderRecord { Name = "stella", Kind = EmbeddingProviderKind.Onnx, ModelFolder = "C:/models/stella.onnx" };
        var (fixture, vm) = await WithFunnelAsync();
        using var _ = fixture;
        await fixture.Store.MutateAsync(store => store.EmbeddingProviders.Add(provider));
        vm.Reload();

        var meaning = vm.Profiles[0].Rows.Single(r => r.IsRiskyActions);
        meaning.Level = FilterLevel.Balanced;
        meaning.SelectedProvider = vm.Providers.Single();
        var runs = meaning.SelectedProvider!.Runs;

        Assert.True(meaning.SelectedProvider.IsOnnx);
        Assert.Equal(OnnxDevice.Cpu, runs.Device);

        // A machine with no GPU only offers the CPU; one with a GPU can switch to it.
        if (runs.DeviceChoices.Any(c => c.Device == OnnxDevice.Gpu))
        {
            runs.SelectedDeviceChoice = runs.DeviceChoices.Single(c => c.Label == "GPU");
            Assert.Equal(OnnxDevice.Gpu, provider.Device);
            Assert.Equal(runs.SelectedGpu!.Index, provider.GpuIndex);
            Assert.Contains("now runs on", vm.StatusMessage);
        }
        else
        {
            Assert.Equal(["CPU"], runs.DeviceChoices.Select(c => c.Label));
        }
    }

    [Fact]
    public async Task AnApiModel_HasNoDeviceToChoose()
    {
        var (fixture, vm) = await WithFunnelAsync();
        using var _ = fixture;
        vm.NewProviderName = "ollama";
        vm.AddProviderCommand.Execute(null);

        Assert.False(vm.Providers.Single().IsOnnx);
    }

    [Fact]
    public async Task AModelInUse_CannotBeRemoved()
    {
        var (fixture, vm) = await WithFunnelAsync();
        using var _ = fixture;
        vm.NewProviderName = "local";
        vm.AddProviderCommand.Execute(null);

        var meaning = vm.Profiles[0].Rows.Single(r => r.IsRiskyActions);
        meaning.Level = FilterLevel.Balanced;
        meaning.SelectedProvider = vm.Providers.Single();

        vm.Providers.Single().RemoveCommand.Execute(null);

        Assert.Single(vm.Providers);
        Assert.Contains("used by", vm.StatusMessage);
    }

    [Fact]
    public async Task TryIt_ReportsTheStepThatStoppedTheCall_WithoutTheValue()
    {
        var (fixture, vm) = await WithFunnelAsync(new FunnelFilter { Category = FilterCategory.Secrets, Level = FilterLevel.Balanced });
        using var _ = fixture;

        vm.TestInput = $"aws configure set aws_access_key_id {AwsKey}";
        await vm.RunTestCommand.ExecuteAsync(null);

        Assert.Contains("1 Secrets: BLOCK", vm.TestResult);
        Assert.Contains("Stopped at step 1", vm.TestResult);
        Assert.DoesNotContain(AwsKey, vm.TestResult);
    }

    [Fact]
    public async Task TryIt_SaysWhenEverythingIsOff()
    {
        var (fixture, vm) = await WithFunnelAsync();
        using var _ = fixture;

        vm.TestInput = AwsKey;
        await vm.RunTestCommand.ExecuteAsync(null);

        Assert.Contains("is Off", vm.TestResult);
    }
}
