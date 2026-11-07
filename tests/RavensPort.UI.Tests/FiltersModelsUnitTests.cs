using RavensPort.Core.Filters;
using RavensPort.Core.Models;
using RavensPort.UI.ViewModels;

namespace RavensPort.UI.Tests;

/// <summary>The Filters tab's model cards, rules file buttons, profile names and Try it, with stand-in models.</summary>
public class FiltersModelsUnitTests
{
    private sealed class WordVectors : IEmbeddingClient
    {
        public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, EmbeddingRole role, CancellationToken ct) =>
            Task.FromResult(texts.Select(t => t.Contains("files", StringComparison.Ordinal) ? new[] { 1f, 0f } : new[] { 0f, 1f }).ToArray());
    }

    private static async Task<(ViewModelFixture Fixture, FiltersViewModel Vm, EmbeddingClientFactory Models)> TabAsync()
    {
        var fixture = new ViewModelFixture();
        await fixture.StartSingleUseAsync();
        await fixture.Store.MutateAsync(store => store.FilterProfiles.Add(new FilterProfileRecord { Name = "agent" }));

        var models = fixture.Get<EmbeddingClientFactory>();
        models.Override = _ => new WordVectors();
        models.EntityOverride = _ => (text, _, _, _) => Task.FromResult(text.Contains("John", StringComparison.Ordinal)
            ? new List<Entity> { new(11, 8, "person", 0.93) }
            : []);
        models.DecisionOverride = _ => (state, statements, _) =>
            Task.FromResult(statements.Select(_ => state.Contains("rm -rf", StringComparison.Ordinal) ? 0.97 : 0.02).ToArray());

        var vm = fixture.Get<FiltersViewModel>();
        vm.Reload();
        return (fixture, vm, models);
    }

    // ---- profiles ----------------------------------------------------------------------------

    [Fact]
    public async Task AProfile_CannotBeRenamedToNothingOrToAnothersName_AndCopiesGetUniqueNames()
    {
        var (fixture, vm, _) = await TabAsync();
        using var _ = fixture;
        var profile = vm.Profiles.Single();

        profile.NameText = " ";
        Assert.Equal("A profile needs a name.", profile.NameError);

        profile.DuplicateCommand.Execute(null);
        profile.DuplicateCommand.Execute(null);
        Assert.Equal(["agent", "agent copy", "agent copy 2"], fixture.Store.Current.FilterProfiles.Select(p => p.Name).OrderBy(n => n));

        profile = vm.Profiles.Single(p => p.Name == "agent");
        profile.NameText = "Agent Copy";
        Assert.Equal("Another profile already has that name.", profile.NameError);

        profile.NameText = "Strict";
        Assert.Equal("", profile.NameError);
        Assert.Contains(fixture.Store.Current.FilterProfiles, p => p.Name == "Strict");
    }

    // ---- secret rules file -------------------------------------------------------------------

    [Fact]
    public async Task TheRulesFile_OpensResetsAndReloads_KeepingTheOldFileOnReset()
    {
        var (fixture, vm, _) = await TabAsync();
        using var _ = fixture;
        var path = SecretRuleSet.UserFilePath;
        if (File.Exists(path)) File.Delete(path);

        try
        {
            vm.ResetRulesCommand.Execute(null);
            Assert.Contains("written with the original rules", vm.StatusMessage);
            Assert.Contains("from your rules file", vm.RulesStatus);

            await vm.OpenRulesFileCommand.ExecuteAsync(null);
            Assert.Contains(path, ((RecordingLauncher)fixture.Get<RavensPort.UI.Services.IPlatformLauncher>()).Opened);

            File.AppendAllText(path, "\n[[rules]]\nid = \"my-ticket\"\nregex = '''TKT-\\d{4}'''\n\n[[rules]]\nid = \"broken\"\nregex = '''(unclosed'''\n");
            vm.ReloadRulesCommand.Execute(null);
            Assert.Contains("with problems", vm.StatusMessage);
            Assert.Contains("1 yours", vm.RulesStatus);
            Assert.Contains("Not used", vm.RulesStatus);

            vm.ResetRulesCommand.Execute(null);
            Assert.Contains("secret-rules.toml.bak", vm.StatusMessage);
            vm.ReloadRulesCommand.Execute(null);
            Assert.StartsWith("Rules reloaded:", vm.StatusMessage);
            Assert.Contains("rules in the rules file are on", FiltersViewModel.SecretsDescription());
        }
        finally
        {
            File.Delete(path);
            File.Delete(SecretRuleSet.BackupFilePath);
            SecretRuleSet.Reload();
        }
    }

    // ---- embedding models (step 4) ------------------------------------------------------------

    [Fact]
    public async Task AnEmbeddingApi_IsCheckedBeforeItIsAdded_ThenTestsAndIsRemoved()
    {
        var (fixture, vm, _) = await TabAsync();
        using var _ = fixture;

        vm.NewProviderKindChoice = vm.ProviderKinds.Single(k => k.Kind == EmbeddingProviderKind.OpenAI);
        vm.AddProviderCommand.Execute(null);
        Assert.Contains("Give it a name", vm.NewProviderError);

        vm.NewProviderName = "remote";
        vm.NewProviderBaseUrl = "not a url";
        vm.AddProviderCommand.Execute(null);
        Assert.Contains("base URL", vm.NewProviderError);

        vm.NewProviderBaseUrl = "http://embeddings.example.com/v1";
        vm.AddProviderCommand.Execute(null);
        Assert.Contains("Use https", vm.NewProviderError);

        vm.NewProviderBaseUrl = "https://embeddings.example.com/v1";
        vm.NewProviderModel = "";
        vm.AddProviderCommand.Execute(null);
        Assert.Contains("model name", vm.NewProviderError);

        vm.NewProviderModel = "text-embedding-3-small";
        vm.AddProviderCommand.Execute(null);
        Assert.Contains("needs an API key", vm.NewProviderError);

        vm.NewProviderKindChoice = vm.ProviderKinds.Single(k => k.Kind == EmbeddingProviderKind.Ollama);
        Assert.False(vm.NewIsRemote);
        Assert.NotEmpty(vm.NewModelHint);
        vm.NewProviderName = "ollama";
        vm.AddProviderCommand.Execute(null);
        Assert.Equal("", vm.NewProviderError);

        vm.NewProviderName = "OLLAMA";
        vm.AddProviderCommand.Execute(null);
        Assert.Contains("already exists", vm.NewProviderError);

        var model = Assert.Single(vm.Providers);
        Assert.False(model.IsOnnx);
        Assert.False(model.IsRemote);
        Assert.Contains("Ollama", model.Summary);
        Assert.Contains("127.0.0.1", model.RemoteWarning);
        Assert.Equal("ollama", model.ToString());

        await model.TestCommand.ExecuteAsync(null);
        Assert.StartsWith("Works", model.TestResult);
        Assert.Contains("2 dimensions", model.TestResult);

        var meaning = vm.Profiles.Single().Rows.Single(r => r.IsRiskyActions);
        meaning.SelectedProvider = meaning.Providers.Single();
        vm.Providers.Single().RemoveCommand.Execute(null);
        Assert.Contains("is used by profile 'agent'", vm.StatusMessage);

        meaning.SelectedProvider = null;
        vm.Providers.Single().RemoveCommand.Execute(null);
        Assert.Empty(vm.Providers);
    }

    [Fact]
    public async Task AnEmbeddingModelThatFails_SaysWhyOnTest()
    {
        var (fixture, vm, models) = await TabAsync();
        using var _ = fixture;
        models.Override = _ => throw new InvalidOperationException("the server is not running");

        vm.NewProviderName = "ollama";
        vm.AddProviderCommand.Execute(null);
        var model = vm.Providers.Single();
        await model.TestCommand.ExecuteAsync(null);

        Assert.Equal("Failed: the server is not running", model.TestResult);
    }

    // ---- entity models (step 3) ---------------------------------------------------------------

    [Fact]
    public async Task AnEntityModel_Tests_RunsOnTheChosenDevice_AndCannotBeRemovedWhileUsed()
    {
        var (fixture, vm, models) = await TabAsync();
        using var _ = fixture;
        var record = new EntityModelRecord { Name = "gliner", ModelPath = @"C:\Models\gliner" };
        fixture.Store.Current.EntityModels.Add(record);
        vm.Reload();

        var model = vm.EntityModels.Single();
        Assert.Contains("GLiNER", model.Summary);
        Assert.Equal("gliner", model.ToString());

        await model.TestCommand.ExecuteAsync(null);
        Assert.Contains("person 0", model.TestResult);

        models.EntityOverride = _ => (_, _, _, _) => Task.FromResult(new List<Entity>());
        await model.TestCommand.ExecuteAsync(null);
        Assert.Contains("found nothing", model.TestResult);

        models.EntityOverride = _ => throw new FileNotFoundException("spm.model is missing");
        await model.TestCommand.ExecuteAsync(null);
        Assert.Equal("Failed: spm.model is missing", model.TestResult);

        model.Runs.SelectedDeviceChoice = model.Runs.DeviceChoices.First();
        Assert.Equal(OnnxDevice.Cpu, record.Device);

        var entities = vm.Profiles.Single().Rows.Single(r => r.IsEntities);
        entities.SelectedEntityModel = entities.EntityModels.Single();
        model.RemoveCommand.Execute(null);
        Assert.Contains("is used by", vm.StatusMessage);

        entities.SelectedEntityModel = null;
        vm.EntityModels.Single().RemoveCommand.Execute(null);
        Assert.Empty(vm.EntityModels);
    }

    // ---- decision models (step 5) -------------------------------------------------------------

    [Fact]
    public async Task ADecisionModel_IsCheckedBeforeItIsAdded_ThenTestsAndIsRemoved()
    {
        var (fixture, vm, models) = await TabAsync();
        using var _ = fixture;

        vm.NewDecisionKindChoice = vm.DecisionKinds.Single(k => k.Kind == DecisionModelKind.ZeroShotOnnx);
        Assert.True(vm.NewDecisionIsOnnx);
        Assert.Contains("zero-shot", vm.NewDecisionHint);
        vm.AddDecisionModelCommand.Execute(null);
        Assert.Contains("Give it a name", vm.NewDecisionError);

        vm.NewDecisionName = "deberta";
        vm.AddDecisionModelCommand.Execute(null);
        Assert.Contains("model's folder", vm.NewDecisionError);

        vm.NewDecisionPath = Path.Combine(Path.GetTempPath(), $"no-model-{Guid.NewGuid():n}");
        vm.AddDecisionModelCommand.Execute(null);
        Assert.Equal("Nothing exists at that path.", vm.NewDecisionError);

        vm.NewDecisionPath = Path.GetTempPath();
        vm.AddDecisionModelCommand.Execute(null);
        Assert.Equal("", vm.NewDecisionError);

        vm.NewDecisionKindChoice = vm.DecisionKinds.Single(k => k.Kind == DecisionModelKind.LayaOnnx);
        Assert.Contains("Laya", vm.NewDecisionHint);
        vm.NewDecisionKindChoice = vm.DecisionKinds.Single(k => k.Kind == DecisionModelKind.JevApi);
        Assert.Contains("laya-serve", vm.NewDecisionHint);
        vm.NewDecisionName = "DEBERTA";
        vm.AddDecisionModelCommand.Execute(null);
        Assert.Contains("already exists", vm.NewDecisionError);

        vm.NewDecisionName = "jev";
        vm.NewDecisionBaseUrl = "ftp://example.com";
        vm.AddDecisionModelCommand.Execute(null);
        Assert.Contains("base URL", vm.NewDecisionError);

        var model = vm.DecisionModels.Single();
        Assert.True(model.IsOnnx);
        Assert.False(model.IsRemote);
        Assert.Contains("Zero-shot", model.Summary);
        Assert.Equal("deberta", model.ToString());

        await model.TestCommand.ExecuteAsync(null);
        Assert.Contains("rm -rf / 0", model.TestResult);
        Assert.DoesNotContain("did not tell them apart", model.TestResult);

        models.DecisionOverride = _ => (_, s, _) => Task.FromResult(s.Select(_ => 0.5).ToArray());
        await model.TestCommand.ExecuteAsync(null);
        Assert.Contains("did not tell them apart", model.TestResult);

        models.DecisionOverride = _ => throw new InvalidOperationException("no tokenizer.json beside the model");
        await model.TestCommand.ExecuteAsync(null);
        Assert.Equal("Failed: no tokenizer.json beside the model", model.TestResult);

        model.Runs.SelectedDeviceChoice = model.Runs.DeviceChoices.First();

        var decision = vm.Profiles.Single().Rows.Single(r => r.IsDecision);
        decision.SelectedDecisionModel = decision.DecisionModels.Single();
        Assert.True(decision.SelectedDecisionIsOnnx);
        Assert.False(decision.SelectedDecisionIsApi);
        model.RemoveCommand.Execute(null);
        Assert.Contains("is used by", vm.StatusMessage);

        decision.SelectedDecisionModel = null;
        vm.DecisionModels.Single().RemoveCommand.Execute(null);
        Assert.Empty(vm.DecisionModels);
    }

    [Fact]
    public async Task ARemoteDecisionApi_WarnsWhereTheTextGoes()
    {
        var (fixture, vm, _) = await TabAsync();
        using var _ = fixture;
        fixture.Store.Current.DecisionModels.Add(new DecisionModelRecord
        {
            Name = "jev", Kind = DecisionModelKind.JevApi, BaseUrl = DecisionModelRecord.JevBaseUrl, Model = "jev-latest", CredentialId = Guid.NewGuid(),
        });
        vm.Reload();

        var model = vm.DecisionModels.Single();
        Assert.True(model.IsRemote);
        Assert.Contains("api.typesafe.ai", model.RemoteWarning);
        Assert.Contains("jev-latest", model.Summary);
    }

    // ---- try it ------------------------------------------------------------------------------

    [Fact]
    public async Task TryIt_ExplainsWhatItNeeds_AndReportsSteps4And5()
    {
        var (fixture, vm, _) = await TabAsync();
        using var _ = fixture;

        vm.TestProfile = null;
        await vm.RunTestCommand.ExecuteAsync(null);
        Assert.Equal("Pick a profile to test against.", vm.TestResult);

        vm.TestProfile = vm.Profiles.Single();
        vm.TestInput = " ";
        await vm.RunTestCommand.ExecuteAsync(null);
        Assert.StartsWith("Paste some text first", vm.TestResult);

        vm.TestInput = "rm -rf /home/jane.doe/projects";
        await vm.RunTestCommand.ExecuteAsync(null);
        Assert.StartsWith("Nothing checked", vm.TestResult);

        var embedding = new EmbeddingProviderRecord { Name = "words", Kind = EmbeddingProviderKind.Ollama, BaseUrl = "http://127.0.0.1:11434/v1", Model = "m" };
        var decider = new DecisionModelRecord { Name = "decider", Kind = DecisionModelKind.ZeroShotOnnx, ModelPath = "x" };
        fixture.Store.Current.EmbeddingProviders.Add(embedding);
        fixture.Store.Current.DecisionModels.Add(decider);
        var filters = fixture.Store.Current.FilterProfiles.Single().Filters;
        filters.Add(new FunnelFilter { Category = FilterCategory.RiskyActions, Level = FilterLevel.Relaxed, Action = FilterAction.Watch, EmbeddingProviderId = embedding.Id, DenyInstructions = ["delete files"] });
        filters.Add(new FunnelFilter { Category = FilterCategory.Decision, Level = FilterLevel.Balanced, DecisionModelId = decider.Id, DenyInstructions = ["This text is about deleting files."] });
        vm.Reload();
        vm.TestProfile = vm.Profiles.Single();

        await vm.RunTestCommand.ExecuteAsync(null);
        Assert.True(vm.TestBlocked);
        Assert.Contains("5 Decision", vm.TestResult);
        Assert.Contains("4 Meaning: closeness", vm.TestResult);
        Assert.Contains("Steps 4–5 read:", vm.TestResult);

        vm.TestInput = "weather in Munich tomorrow";
        await vm.RunTestCommand.ExecuteAsync(null);
        Assert.True(vm.TestAllowed);
        Assert.Contains("5 Decision: \"This text is about deleting files.\" holds with probability", vm.TestResult);
    }

    [Fact]
    public async Task Step5_DescribesItself_AndSavesItsStatements()
    {
        var (fixture, vm, _) = await TabAsync();
        using var _ = fixture;
        var decision = vm.Profiles.Single().Rows.Single(r => r.IsDecision);

        Assert.StartsWith("Off.", decision.LevelExplained);
        decision.SelectedLevelChoice = decision.LevelChoices.Single(c => c.Level == FilterLevel.Strict);
        decision.ThresholdText = "0.6";
        Assert.Contains("0", decision.LevelExplained);
        decision.ThresholdText = "two";
        Assert.NotEmpty(decision.ThresholdError);

        Assert.Contains("5 Decision: Strict", vm.Profiles.Single().StepsSummary);
        Assert.Equal(0.6, fixture.Store.Current.FilterProfiles.Single().Filters.Single(f => f.Category == FilterCategory.Decision).Threshold);
    }
}
