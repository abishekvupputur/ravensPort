using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RavensPort.Core.Filters;
using RavensPort.Core.Models;
using RavensPort.Core.Storage;

namespace RavensPort.UI.ViewModels;

/// <summary>
/// The Filters tab: the models the filters can use, then per funnel the four steps every call
/// passes through in order — Secrets, Personal data, Entities, Meaning — grouped into named
/// profiles, which funnels and the upstreams inside them are pointed at; plus a box to try text
/// against a profile before trusting it.
/// </summary>
public sealed partial class FiltersViewModel : ObservableObject
{
    private readonly ConfigStoreCache _configStoreCache;
    private readonly FilterService _filterService;
    private readonly EmbeddingClientFactory _clients;

    private readonly RavensPort.UI.Services.IPlatformLauncher _launcher;

    public FiltersViewModel(
        ConfigStoreCache configStoreCache, FilterService filterService, EmbeddingClientFactory clients, RavensPort.UI.Services.IPlatformLauncher launcher)
    {
        _configStoreCache = configStoreCache;
        _filterService = filterService;
        _clients = clients;
        _launcher = launcher;
        _rulesStatus = DescribeRules(SecretRuleSet.Current);

        Gpus = OnnxSessions.DetectGpus();
        NewRuns = new DevicePickerViewModel(OnnxDevice.Cpu, 0, Gpus);
        NewEntityRuns = new DevicePickerViewModel(OnnxDevice.Cpu, 0, Gpus);

        _newProviderBaseUrl = EmbeddingProviderRecord.DefaultBaseUrl(_newProviderKind);
        _newProviderModel = EmbeddingProviderRecord.SuggestedModels(_newProviderKind).FirstOrDefault() ?? "";
        Reload();
    }

    public ObservableCollection<EmbeddingProviderItemViewModel> Providers { get; } = [];
    public ObservableCollection<EntityModelItemViewModel> EntityModels { get; } = [];
    public ObservableCollection<ProfileItemViewModel> Profiles { get; } = [];
    public ObservableCollection<FunnelAssignmentViewModel> Assignments { get; } = [];

    public bool HasProfiles => Profiles.Count > 0;
    public bool HasFunnels => Assignments.Count > 0;

    // ---- profiles --------------------------------------------------------------------------

    [ObservableProperty] private string _newProfileName = "";
    [ObservableProperty] private string _newProfileError = "";

    [RelayCommand]
    private void AddProfile() => CreateProfile(NewProfileName, []);

    internal void DuplicateProfile(ProfileItemViewModel item) =>
        CreateProfile(UniqueProfileName($"{item.Name} copy"),
            [.. JsonSerializer.Deserialize<List<FunnelFilter>>(JsonSerializer.Serialize(item.Record.Filters))!]);

    private void CreateProfile(string name, List<FunnelFilter> filters)
    {
        name = name.Trim();
        NewProfileError = name.Length == 0 ? "Give the profile a name, e.g. \"Strict\" or \"Trusted internal\"."
            : _configStoreCache.Current.FilterProfiles.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) ? "A profile with that name already exists."
            : "";
        if (NewProfileError.Length > 0) return;

        _configStoreCache.Current.FilterProfiles.Add(new FilterProfileRecord { Name = name, Filters = filters });
        Save($"Profile '{name}' added. Point a funnel or an upstream at it under Where profiles apply.");
        NewProfileName = "";
        Reload();
    }

    internal void RemoveProfile(ProfileItemViewModel item)
    {
        if (item.UsedBy.Count > 0)
        {
            // Not silently: a funnel pointing at a missing profile blocks every call, by design.
            StatusMessage = $"'{item.Name}' is used by {string.Join(", ", item.UsedBy)}. Point those elsewhere first.";
            return;
        }

        _configStoreCache.Current.FilterProfiles.Remove(item.Record);
        Save($"Profile '{item.Name}' removed.");
        Reload();
    }

    internal string? RenameProfile(ProfileItemViewModel item, string name)
    {
        name = name.Trim();
        if (name.Length == 0) return "A profile needs a name.";
        if (_configStoreCache.Current.FilterProfiles.Any(p => p != item.Record && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
            return "Another profile already has that name.";

        item.Record.Name = name;
        Save($"Profile renamed to '{name}'.");
        foreach (var assignment in Assignments) assignment.RefreshLabels(ProfileChoices());
        return null;
    }

    private string UniqueProfileName(string name)
    {
        var candidate = name;
        for (var n = 2; _configStoreCache.Current.FilterProfiles.Any(p => string.Equals(p.Name, candidate, StringComparison.OrdinalIgnoreCase)); n++)
            candidate = $"{name} {n}";
        return candidate;
    }

    /// <summary>Who uses a profile, in words: "claude-code" for a funnel, "claude-code › kb" for one upstream in it.</summary>
    internal List<string> UsersOf(Guid profileId)
    {
        var store = _configStoreCache.Current;
        var users = new List<string>();
        foreach (var funnel in store.McpFunnels)
        {
            if (funnel.FilterProfileId == profileId) users.Add(funnel.Name);
            foreach (var link in funnel.Sources.Where(l => l.FilterProfileId == profileId))
                users.Add($"{funnel.Name} › {store.McpSources.FirstOrDefault(src => src.Id == link.SourceId)?.Name ?? "an upstream"}");
        }
        return users;
    }

    private List<ProfileChoice> ProfileChoices() =>
        [.. _configStoreCache.Current.FilterProfiles.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Select(p => new ProfileChoice(p.Id, p.Name))];

    /// <summary>
    /// Funnels set up before profiles existed carry their filters themselves. The first time the
    /// tab loads one, those filters become a profile named after the funnel, and the funnel points
    /// at it — the same checks, now reusable and attachable to upstreams.
    /// </summary>
    private bool ConvertFunnelFilters()
    {
        var store = _configStoreCache.Current;
        var converted = new List<string>();

        foreach (var funnel in store.McpFunnels.Where(f => f.FilterProfileId is null && f.Filters.Count > 0))
        {
            var profile = new FilterProfileRecord { Name = UniqueProfileName(funnel.Name), Filters = funnel.Filters };
            store.FilterProfiles.Add(profile);
            funnel.FilterProfileId = profile.Id;
            funnel.Filters = [];
            converted.Add(funnel.Name);
        }

        if (converted.Count == 0) return false;
        Save($"Filters of {string.Join(", ", converted)} are now profiles of the same name.");
        return true;
    }
    public bool HasModels => Providers.Count + EntityModels.Count > 0;

    /// <summary>Secrets step: what the rules in use cover, for the row's description.</summary>
    public static string SecretsDescription()
    {
        var rules = SecretRuleSet.Current;
        var yours = rules.CountFrom(SecretRuleSet.UserSource);
        return $"All {rules.RuleCount} rules are on: {rules.CountFrom(SecretRuleSet.GitleaksSource)} from gitleaks {SecretScanner.GitleaksVersion} " +
               $"(cloud and API keys, tokens, private keys, connection strings), {rules.CountFrom(SecretRuleSet.RavensPortSource)} from RavensPort " +
               "(passwords, usernames, refresh and bearer tokens, credentials in URLs and fields named like them)" +
               (yours > 0 ? $", and {yours} of yours" : "") +
               ". No agent or tool needs any of these, so a match is always denied. Find a rule below to turn it off for this profile.";
    }

    // ---- secret rules (step 1) -------------------------------------------------------------

    public string UserRulesPath => SecretRuleSet.UserFilePath;

    [ObservableProperty] private string _rulesStatus;

    private static string DescribeRules(SecretScanner rules)
    {
        var line = $"{rules.RuleCount} rules in use: {rules.CountFrom(SecretRuleSet.GitleaksSource)} gitleaks, " +
                   $"{rules.CountFrom(SecretRuleSet.RavensPortSource)} RavensPort, {rules.CountFrom(SecretRuleSet.UserSource)} yours.";
        return rules.SkippedRules.Count == 0
            ? line
            : line + " Not used: " + string.Join("; ", rules.SkippedRules.Take(5)) + (rules.SkippedRules.Count > 5 ? " …" : "");
    }

    [RelayCommand]
    private async Task OpenRulesFileAsync()
    {
        try
        {
            await _launcher.OpenPathAsync(SecretRuleSet.EnsureUserFile());
            StatusMessage = "Rules file opened. Save it, then press Reload rules.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not open the rules file: {ex.Message}";
        }
    }

    [RelayCommand]
    private void ReloadRules()
    {
        var rules = SecretRuleSet.Reload();
        RulesStatus = DescribeRules(rules);
        StatusMessage = rules.SkippedRules.Count == 0
            ? $"Rules reloaded: {rules.RuleCount} in use. Applies to the agent's next call."
            : $"Rules reloaded with problems — see Secret rules. {rules.RuleCount} in use.";
        Reload();
    }

    [ObservableProperty] private string _statusMessage = "";

    // ---- devices ---------------------------------------------------------------------------

    /// <summary>The GPUs Windows lists (DirectML), discrete or integrated; the software renderer is left out.</summary>
    public IReadOnlyList<GpuAdapter> Gpus { get; }
    public bool HasGpus => Gpus.Count > 0;

    /// <summary>Where a model being added will run; the same picker its card and step show later.</summary>
    public DevicePickerViewModel NewRuns { get; }
    public DevicePickerViewModel NewEntityRuns { get; }

    // ---- embedding models (step 4) ---------------------------------------------------------

    public IReadOnlyList<ProviderKindChoice> ProviderKinds { get; } =
        [.. Enum.GetValues<EmbeddingProviderKind>().Select(k => new ProviderKindChoice(k, EmbeddingProviderRecord.Describe(k)))];

    public ObservableCollection<CredentialChoice> CredentialChoices { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NewIsOnnx), nameof(NewIsApi), nameof(NewModelHint), nameof(NewIsRemote))]
    private EmbeddingProviderKind _newProviderKind = EmbeddingProviderKind.Ollama;

    [ObservableProperty] private string _newProviderName = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NewIsRemote))]
    private string _newProviderBaseUrl;

    [ObservableProperty] private string _newProviderModel = "";
    [ObservableProperty] private CredentialChoice? _newProviderCredential;
    [ObservableProperty] private string _newProviderFolder = "";
    [ObservableProperty] private string _newProviderError = "";

    public ProviderKindChoice? NewProviderKindChoice
    {
        get => ProviderKinds.FirstOrDefault(k => k.Kind == NewProviderKind);
        set
        {
            if (value is null) return;
            NewProviderKind = value.Kind;
            OnPropertyChanged();
        }
    }

    partial void OnNewProviderKindChanged(EmbeddingProviderKind value)
    {
        NewProviderBaseUrl = EmbeddingProviderRecord.DefaultBaseUrl(value);
        NewProviderModel = EmbeddingProviderRecord.SuggestedModels(value).FirstOrDefault() ?? "";
    }

    public bool NewIsOnnx => NewProviderKind == EmbeddingProviderKind.Onnx;
    public bool NewIsApi => !NewIsOnnx;

    public bool NewIsRemote => new EmbeddingProviderRecord { Name = "", Kind = NewProviderKind, BaseUrl = NewProviderBaseUrl }.IsRemote;

    public string NewModelHint => EmbeddingProviderRecord.SuggestedModels(NewProviderKind) is { Count: > 0 } models
        ? "For example: " + string.Join(", ", models)
        : NewProviderKind == EmbeddingProviderKind.OpenAiCompatible
            ? "The model name the server reports. llama.cpp must be started with --embeddings."
            : "";

    [RelayCommand]
    private void AddProvider()
    {
        NewProviderError = ValidateNewProvider() ?? "";
        if (NewProviderError.Length > 0) return;

        var record = new EmbeddingProviderRecord
        {
            Name = NewProviderName.Trim(),
            Kind = NewProviderKind,
            BaseUrl = NewIsOnnx ? "" : NewProviderBaseUrl.Trim(),
            Model = NewIsOnnx ? "" : NewProviderModel.Trim(),
            ModelFolder = NewIsOnnx ? NewProviderFolder.Trim().Trim('"') : "",
            CredentialId = NewIsOnnx ? null : NewProviderCredential?.Id,
            Device = NewIsOnnx ? NewRuns.Device : OnnxDevice.Cpu,
            GpuIndex = NewIsOnnx ? NewRuns.GpuIndex : 0,
        };

        _configStoreCache.Current.EmbeddingProviders.Add(record);
        Save($"Embedding model '{record.Name}' added.");

        NewProviderName = "";
        NewProviderFolder = "";
        Reload();
    }

    private string? ValidateNewProvider()
    {
        if (string.IsNullOrWhiteSpace(NewProviderName)) return "Give it a name, e.g. \"Ollama nomic\".";
        if (NameTaken(NewProviderName)) return "A model with that name already exists.";

        if (NewIsOnnx) return OnnxModelFiles.Find(NewProviderFolder, out var error) is null ? error : null;

        if (!Uri.TryCreate(NewProviderBaseUrl.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return "Enter the API's base URL, e.g. http://127.0.0.1:11434/v1.";
        if (uri.Scheme == "http" && !uri.IsLoopback)
            return "Use https for an API that is not on this PC — every checked call is sent to it.";
        if (string.IsNullOrWhiteSpace(NewProviderModel)) return "Enter the model name.";
        if (NewProviderKind is EmbeddingProviderKind.OpenAI or EmbeddingProviderKind.VoyageAI && NewProviderCredential?.Id is null)
            return $"{EmbeddingProviderRecord.Describe(NewProviderKind)} needs an API key. Add it on the Credentials tab as an \"API key\" credential, then pick it here.";

        return null;
    }

    private bool NameTaken(string name) =>
        _configStoreCache.Current.EmbeddingProviders.Any(p => string.Equals(p.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)) ||
        _configStoreCache.Current.EntityModels.Any(p => string.Equals(p.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    internal void RemoveProvider(EmbeddingProviderItemViewModel item)
    {
        var users = FunnelsUsing(f => f.EmbeddingProviderId == item.Record.Id);
        if (users.Count > 0)
        {
            // Not silently: a step without its model blocks every call, by design.
            StatusMessage = $"'{item.Name}' is used by {string.Join(", ", users)}. Pick another model there first.";
            return;
        }

        _configStoreCache.Current.EmbeddingProviders.Remove(item.Record);
        Save($"Embedding model '{item.Name}' removed.");
        Reload();
    }

    internal async Task<string> TestProviderAsync(EmbeddingProviderRecord provider)
    {
        try
        {
            var watch = Stopwatch.StartNew();
            var vectors = await Task.Run(() => _clients.Get(provider).EmbedAsync(["delete every file", "remove all files", "weather tomorrow"], EmbeddingRole.Document, default));
            watch.Stop();

            static double Cos(float[] a, float[] b) => a.Zip(b, (x, y) => (double)x * y).Sum();
            var device = _clients.DeviceOf(provider.Id) is { } d ? $" on {d}" : "";

            return $"Works{device} — {vectors[0].Length} dimensions, {watch.ElapsedMilliseconds} ms for 3 texts (first run includes loading). " +
                   $"Similar pair {Cos(vectors[0], vectors[1]):0.00}, unrelated pair {Cos(vectors[0], vectors[2]):0.00}.";
        }
        catch (Exception ex)
        {
            return $"Failed: {ex.Message}";
        }
    }

    // ---- entity models (step 3) ------------------------------------------------------------

    [ObservableProperty] private string _newEntityName = "";
    [ObservableProperty] private string _newEntityPath = "";

    [ObservableProperty] private string _newEntityError = "";

    [RelayCommand]
    private void AddEntityModel()
    {
        NewEntityError = string.IsNullOrWhiteSpace(NewEntityName) ? "Give it a name, e.g. \"GLiNER PII\"."
            : NameTaken(NewEntityName) ? "A model with that name already exists."
            : GlinerFiles.Find(NewEntityPath, out var error) is null ? error ?? ""
            : "";
        if (NewEntityError.Length > 0) return;

        var record = new EntityModelRecord
        {
            Name = NewEntityName.Trim(),
            ModelPath = NewEntityPath.Trim().Trim('"'),
            Device = NewEntityRuns.Device,
            GpuIndex = NewEntityRuns.GpuIndex,
        };

        _configStoreCache.Current.EntityModels.Add(record);
        Save($"Entity model '{record.Name}' added.");

        NewEntityName = "";
        NewEntityPath = "";
        Reload();
    }

    internal void RemoveEntityModel(EntityModelItemViewModel item)
    {
        var users = FunnelsUsing(f => f.EntityModelId == item.Record.Id);
        if (users.Count > 0)
        {
            StatusMessage = $"'{item.Name}' is used by {string.Join(", ", users)}. Pick another model there first.";
            return;
        }

        _configStoreCache.Current.EntityModels.Remove(item.Record);
        Save($"Entity model '{item.Name}' removed.");
        Reload();
    }

    internal async Task<string> TestEntityModelAsync(EntityModelRecord record)
    {
        try
        {
            var watch = Stopwatch.StartNew();
            var found = await Task.Run(() => _clients.GetEntityModel(record)(
                "My name is John Doe and I live in Berlin, call me on +49 170 0000000.",
                ["person", "city", "phone number"], 0.5, default));
            watch.Stop();

            var device = _clients.DeviceOf(record.Id) is { } d ? $" on {d}" : "";
            return found.Count == 0
                ? $"Loaded{device} in {watch.ElapsedMilliseconds} ms, but found nothing in a sentence with a name, a city and a phone number — " +
                  "if this is an int8 or other quantized export, try the fp32 or fp16 file."
                : $"Works{device} — {watch.ElapsedMilliseconds} ms (first run includes loading). Found: " +
                  string.Join(", ", found.Select(e => $"{e.Label} {e.Score:0.00}"));
        }
        catch (Exception ex)
        {
            return $"Failed: {ex.Message}";
        }
    }

    private List<string> FunnelsUsing(Func<FunnelFilter, bool> uses) =>
        [.. _configStoreCache.Current.FilterProfiles.Where(p => p.Filters.Any(uses)).Select(p => $"profile '{p.Name}'")
            .Concat(_configStoreCache.Current.McpFunnels.Where(f => f.Filters.Any(uses)).Select(f => f.Name))];

    // ---- try it ----------------------------------------------------------------------------

    [ObservableProperty] private ProfileItemViewModel? _testProfile;
    [ObservableProperty] private string _testInput = "";
    [ObservableProperty] private string _testResult = "";

    [RelayCommand]
    private async Task RunTestAsync()
    {
        if (TestProfile is null)
        {
            TestResult = "Pick a profile to test against.";
            return;
        }

        if (string.IsNullOrWhiteSpace(TestInput))
        {
            TestResult = "Paste some text first — for example a command, an email, or a config snippet.";
            return;
        }

        var filters = TestProfile.Record.Filters;
        if (filters.All(f => f.Level == FilterLevel.Off))
        {
            TestResult = $"Nothing checked: every step in '{TestProfile.Name}' is Off.";
            return;
        }

        TestResult = "Checking…";

        // The same run the funnel does, on the text as a single argument.
        var args = new[] { KeyValuePair.Create("text", JsonSerializer.SerializeToElement(TestInput)) };
        var outcome = await Task.Run(() => _filterService.RunAsync(filters, "test", args, default));

        var lines = outcome.Hits.Select(h =>
            $"{FilterNames.StepOf(h.Category)} {FilterNames.Of(h.Category)}: {(h.Action == FilterAction.Block ? "BLOCK" : "log (Watch)")} — {h.Detector}").ToList();

        if (outcome.StoppedAt is { } stopped)
        {
            lines.Add($"Stopped at step {FilterNames.StepOf(stopped)}: later steps did not run, and nothing was sent to an embedding model.");
        }
        else if (outcome.Judgement is { } j)
        {
            lines.Add(j.AllowedBy is not null
                ? $"4 Meaning: allowed — closer to the allow example \"{j.AllowedBy}\""
                : $"4 Meaning: closeness {j.Closeness:0.00} to \"{j.Closest}\" (blocks at {j.Threshold:0.00})");
        }

        if (outcome.StoppedAt is null && filters.Any(f => f.Category == FilterCategory.RiskyActions && f.Level != FilterLevel.Off))
        {
            lines.Add("Step 4 read: " + string.Join(" | ", outcome.TextsForMeaning.Select(t => t.Text)));
        }

        TestResult = lines.Count == 0
            ? $"Nothing caught. A call carrying this text would pass '{TestProfile.Name}'."
            : string.Join("\n", lines);
    }

    // ---- loading and saving ----------------------------------------------------------------

    public void Reload()
    {
        var store = _configStoreCache.Current;

        Providers.Clear();
        foreach (var provider in store.EmbeddingProviders) Providers.Add(new EmbeddingProviderItemViewModel(provider, this));

        EntityModels.Clear();
        foreach (var model in store.EntityModels) EntityModels.Add(new EntityModelItemViewModel(model, this));

        CredentialChoices.Clear();
        CredentialChoices.Add(new CredentialChoice(null, "(none)"));
        foreach (var credential in store.Credentials)
        {
            CredentialChoices.Add(new CredentialChoice(credential.Id, $"{credential.Name} ({CredentialKindInfo.ShortLabel(credential.Kind)})"));
        }
        NewProviderCredential ??= CredentialChoices[0];

        ConvertFunnelFilters();

        var selectedId = TestProfile?.Record.Id;
        Profiles.Clear();
        foreach (var profile in store.FilterProfiles.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            Profiles.Add(new ProfileItemViewModel(profile, this, Save, Providers, EntityModels));
        }

        var choices = ProfileChoices();
        Assignments.Clear();
        foreach (var funnel in store.McpFunnels)
        {
            Assignments.Add(new FunnelAssignmentViewModel(funnel, store.McpSources, choices, OnAssignmentChanged));
        }

        TestProfile = Profiles.FirstOrDefault(p => p.Record.Id == selectedId) ?? Profiles.FirstOrDefault();
        OnPropertyChanged(nameof(HasProfiles));
        OnPropertyChanged(nameof(HasFunnels));
        OnPropertyChanged(nameof(HasModels));
    }

    internal void SaveModelChange(string message) => Save(message);

    private void OnAssignmentChanged(string message)
    {
        Save(message);
        foreach (var profile in Profiles) profile.RefreshUsedBy();
    }

    private async void Save(string message)
    {
        try
        {
            // Filters are read per call, so saving is all a change needs.
            await _configStoreCache.SaveAsync();
            StatusMessage = $"{message} Applies to the agent's next call.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not save change: {ex.Message}";
        }
    }
}

public sealed record ProviderKindChoice(EmbeddingProviderKind Kind, string Label)
{
    public override string ToString() => Label;
}

public sealed record CredentialChoice(Guid? Id, string Label)
{
    public override string ToString() => Label;
}

/// <summary>What a step's first dropdown offers: Secrets is Off/Deny, Personal data Off/On, the model steps four levels.</summary>
public sealed record LevelChoice(FilterLevel Level, string Label)
{
    public override string ToString() => Label;
}

public sealed record DeviceChoice(OnnxDevice Device, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// Where one ONNX model runs: CPU, or one of the GPUs Windows lists. Shown on the model's card and
/// in the step that uses it; both edit the same model record, so a change applies to every funnel
/// using that model, which reloads on its next call.
/// </summary>
public sealed partial class DevicePickerViewModel : ObservableObject
{
    private readonly Action<OnnxDevice, int> _apply;

    public DevicePickerViewModel(OnnxDevice device, int gpuIndex, IReadOnlyList<GpuAdapter> gpus, Action<OnnxDevice, int>? apply = null)
    {
        Gpus = gpus;
        DeviceChoices = gpus.Count > 0 || device == OnnxDevice.Gpu
            ? [new(OnnxDevice.Cpu, "CPU"), new(OnnxDevice.Gpu, "GPU")]
            : [new(OnnxDevice.Cpu, "CPU")];
        _device = device;
        _selectedGpu = gpus.FirstOrDefault(g => g.Index == gpuIndex) ?? gpus.OrderByDescending(g => g.DedicatedMemoryBytes).FirstOrDefault();
        _apply = apply ?? ((_, _) => { });
    }

    public IReadOnlyList<GpuAdapter> Gpus { get; }
    public IReadOnlyList<DeviceChoice> DeviceChoices { get; }

    public DeviceChoice? SelectedDeviceChoice
    {
        get => DeviceChoices.FirstOrDefault(c => c.Device == Device);
        set
        {
            if (value is not null) Device = value.Device;
        }
    }

    /// <summary>The GPU index to store: the selected GPU when on a GPU, else 0.</summary>
    public int GpuIndex => Device == OnnxDevice.Gpu ? SelectedGpu?.Index ?? 0 : 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsGpu), nameof(SelectedDeviceChoice))]
    private OnnxDevice _device;

    [ObservableProperty] private GpuAdapter? _selectedGpu;

    public bool ShowsGpu => Device == OnnxDevice.Gpu && Gpus.Count > 0;

    partial void OnDeviceChanged(OnnxDevice value) => _apply(value, SelectedGpu?.Index ?? 0);

    partial void OnSelectedGpuChanged(GpuAdapter? value)
    {
        if (Device == OnnxDevice.Gpu && value is not null) _apply(Device, value.Index);
    }
}

/// <summary>One configured embedding model.</summary>
public sealed partial class EmbeddingProviderItemViewModel : ObservableObject
{
    private readonly FiltersViewModel owner;

    public EmbeddingProviderItemViewModel(EmbeddingProviderRecord record, FiltersViewModel owner)
    {
        Record = record;
        this.owner = owner;
        Runs = new DevicePickerViewModel(record.Device, record.GpuIndex, owner.Gpus, (device, gpu) =>
        {
            Record.Device = device;
            Record.GpuIndex = gpu;
            OnPropertyChanged(nameof(Summary));
            owner.SaveModelChange($"Embedding model '{Name}' now runs on {DeviceLabel(device, gpu, owner.Gpus)}.");
        });
    }

    public EmbeddingProviderRecord Record { get; }
    public string Name => Record.Name;

    /// <summary>Only an in-process ONNX model has a device to choose; an API runs wherever it runs.</summary>
    public bool IsOnnx => Record.Kind == EmbeddingProviderKind.Onnx;
    public DevicePickerViewModel Runs { get; }

    public string Summary => Record.Kind == EmbeddingProviderKind.Onnx
        ? $"{EmbeddingProviderRecord.Describe(Record.Kind)} · {Record.ModelFolder} · {DeviceLabel(Record.Device, Record.GpuIndex, owner.Gpus)}"
        : $"{EmbeddingProviderRecord.Describe(Record.Kind)} · {Record.Model} · {Record.BaseUrl}";

    public bool IsRemote => Record.IsRemote;

    public string RemoteWarning => Uri.TryCreate(Record.BaseUrl, UriKind.Absolute, out var uri)
        ? $"Remote: the text step 4 reads is sent to {uri.Host} — after steps 1–3 have replaced what they found."
        : "Remote: the text step 4 reads is sent to this API.";

    [ObservableProperty] private string _testResult = "";

    [RelayCommand]
    private async Task TestAsync()
    {
        TestResult = "Testing…";
        TestResult = await owner.TestProviderAsync(Record);
    }

    [RelayCommand]
    private void Remove() => owner.RemoveProvider(this);

    internal static string DeviceLabel(OnnxDevice device, int gpuIndex, IReadOnlyList<GpuAdapter> gpus) =>
        device == OnnxDevice.Cpu ? "CPU" : gpus.FirstOrDefault(g => g.Index == gpuIndex)?.Label ?? $"GPU {gpuIndex} (not found — will use the CPU)";

    public override string ToString() => Name;
}

/// <summary>One configured entity model.</summary>
public sealed partial class EntityModelItemViewModel : ObservableObject
{
    private readonly FiltersViewModel owner;

    public EntityModelItemViewModel(EntityModelRecord record, FiltersViewModel owner)
    {
        Record = record;
        this.owner = owner;
        Runs = new DevicePickerViewModel(record.Device, record.GpuIndex, owner.Gpus, (device, gpu) =>
        {
            Record.Device = device;
            Record.GpuIndex = gpu;
            OnPropertyChanged(nameof(Summary));
            owner.SaveModelChange($"Entity model '{Name}' now runs on {EmbeddingProviderItemViewModel.DeviceLabel(device, gpu, owner.Gpus)}.");
        });
    }

    public EntityModelRecord Record { get; }
    public string Name => Record.Name;
    public DevicePickerViewModel Runs { get; }
    public string Summary => $"GLiNER (ONNX, local) · {Record.ModelPath} · {EmbeddingProviderItemViewModel.DeviceLabel(Record.Device, Record.GpuIndex, owner.Gpus)}";

    [ObservableProperty] private string _testResult = "";

    [RelayCommand]
    private async Task TestAsync()
    {
        TestResult = "Testing…";
        TestResult = await owner.TestEntityModelAsync(Record);
    }

    [RelayCommand]
    private void Remove() => owner.RemoveEntityModel(this);

    public override string ToString() => Name;
}

/// <summary>A choice of profile in a dropdown; a null id means "none" or "same as the funnel".</summary>
public sealed record ProfileChoice(Guid? Id, string Label)
{
    public override string ToString() => Label;
}

/// <summary>One profile's card: its name, who uses it, and its four steps in the order they run.</summary>
public sealed partial class ProfileItemViewModel : ObservableObject
{
    private readonly FiltersViewModel _owner;

    public ProfileItemViewModel(
        FilterProfileRecord record,
        FiltersViewModel owner,
        Action<string> onChanged,
        ObservableCollection<EmbeddingProviderItemViewModel>? providers = null,
        ObservableCollection<EntityModelItemViewModel>? entityModels = null)
    {
        Record = record;
        _owner = owner;
        _nameText = record.Name;
        Rows = [.. Enum.GetValues<FilterCategory>().Select(c =>
            new McpFunnelFilterItemViewModel(record.Filters, () => Record.Name, c, onChanged, providers, entityModels))];
        RefreshUsedBy();
    }

    public FilterProfileRecord Record { get; }
    public string Name => Record.Name;
    public IReadOnlyList<McpFunnelFilterItemViewModel> Rows { get; }

    [ObservableProperty] private string _nameText;
    [ObservableProperty] private string _nameError = "";
    [ObservableProperty] private bool _isExpanded;

    public List<string> UsedBy { get; private set; } = [];
    public string UsedBySummary => UsedBy.Count == 0 ? "Not used yet" : "Used by " + string.Join(", ", UsedBy);

    /// <summary>A one-line summary of what is on, for the collapsed card.</summary>
    public string StepsSummary => string.Join(" · ", Rows.Select(r => $"{r.Step} {r.Name}: {(r.IsOn ? r.SelectedLevelChoice?.Label : "Off")}"));

    internal void RefreshUsedBy()
    {
        UsedBy = _owner.UsersOf(Record.Id);
        OnPropertyChanged(nameof(UsedBySummary));
    }

    partial void OnNameTextChanged(string value)
    {
        if (value == Record.Name) return;
        NameError = _owner.RenameProfile(this, value) ?? "";
        if (NameError.Length == 0) OnPropertyChanged(nameof(Name));
    }

    [RelayCommand]
    private void Duplicate() => _owner.DuplicateProfile(this);

    [RelayCommand]
    private void Remove() => _owner.RemoveProfile(this);

    public override string ToString() => Name;
}

/// <summary>Where profiles apply for one funnel: its own profile, and an override per upstream in it.</summary>
public sealed partial class FunnelAssignmentViewModel : ObservableObject
{
    private readonly McpFunnelRecord _funnel;
    private readonly Action<string> _onChanged;
    private bool _loading = true;

    public FunnelAssignmentViewModel(
        McpFunnelRecord funnel, IReadOnlyList<McpSourceRecord> sources, IReadOnlyList<ProfileChoice> profiles, Action<string> onChanged)
    {
        _funnel = funnel;
        _onChanged = onChanged;

        Choices = [new ProfileChoice(null, "No filters"), .. profiles];
        SelectedProfile = Choices.FirstOrDefault(c => c.Id == funnel.FilterProfileId) ?? Choices[0];

        Upstreams = [.. funnel.Sources.Select(link =>
            new UpstreamAssignmentViewModel(funnel, link, sources.FirstOrDefault(s => s.Id == link.SourceId), profiles, onChanged))];
        _loading = false;
    }

    public string Name => _funnel.Name;
    public string Endpoint => $"/mcp/{_funnel.Slug}";
    public IReadOnlyList<ProfileChoice> Choices { get; private set; }
    public IReadOnlyList<UpstreamAssignmentViewModel> Upstreams { get; }
    public bool HasUpstreams => Upstreams.Count > 0;

    [ObservableProperty] private ProfileChoice? _selectedProfile;

    partial void OnSelectedProfileChanged(ProfileChoice? value)
    {
        if (_loading || value is null) return;
        _funnel.FilterProfileId = value.Id;
        _onChanged($"Funnel '{Name}' now uses {(value.Id is null ? "no filters" : $"profile '{value.Label}'")}.");
    }

    /// <summary>After a rename, so the dropdowns show the new name.</summary>
    internal void RefreshLabels(IReadOnlyList<ProfileChoice> profiles)
    {
        _loading = true;
        Choices = [new ProfileChoice(null, "No filters"), .. profiles];
        OnPropertyChanged(nameof(Choices));
        SelectedProfile = Choices.FirstOrDefault(c => c.Id == _funnel.FilterProfileId) ?? Choices[0];
        foreach (var upstream in Upstreams) upstream.RefreshLabels(profiles);
        _loading = false;
    }
}

/// <summary>One upstream inside a funnel: the funnel's profile, or one of its own.</summary>
public sealed partial class UpstreamAssignmentViewModel : ObservableObject
{
    private readonly McpFunnelRecord _funnel;
    private readonly McpFunnelSource _link;
    private readonly Action<string> _onChanged;
    private bool _loading = true;

    public UpstreamAssignmentViewModel(
        McpFunnelRecord funnel, McpFunnelSource link, McpSourceRecord? source, IReadOnlyList<ProfileChoice> profiles, Action<string> onChanged)
    {
        _funnel = funnel;
        _link = link;
        _onChanged = onChanged;
        Name = source?.Name ?? "(removed upstream)";
        Alias = source?.Alias ?? "";
        RefreshLabels(profiles);
        _loading = false;
    }

    public string Name { get; }
    public string Alias { get; }
    public IReadOnlyList<ProfileChoice> Choices { get; private set; } = [];

    [ObservableProperty] private ProfileChoice? _selectedProfile;

    partial void OnSelectedProfileChanged(ProfileChoice? value)
    {
        if (_loading || value is null) return;
        _link.FilterProfileId = value.Id;
        _onChanged(value.Id is null
            ? $"Calls from '{_funnel.Name}' to '{Name}' now follow the funnel's profile."
            : $"Calls from '{_funnel.Name}' to '{Name}' now use profile '{value.Label}'.");
    }

    internal void RefreshLabels(IReadOnlyList<ProfileChoice> profiles)
    {
        var was = _loading;
        _loading = true;
        Choices = [new ProfileChoice(null, "Same as the funnel"), .. profiles];
        OnPropertyChanged(nameof(Choices));
        SelectedProfile = Choices.FirstOrDefault(c => c.Id == _link.FilterProfileId) ?? Choices[0];
        _loading = was;
    }
}

/// <summary>
/// One Personal data pattern as a chip. Clicking switches it off for this funnel (struck through,
/// not hidden, so it can be switched back on). Editing happens in the pattern list below the chips.
/// </summary>
public sealed partial class DetectorChipViewModel(string name, bool isCustom, bool isOn, Action<DetectorChipViewModel> onToggle, string? description = null)
    : ObservableObject
{
    public string Name { get; } = name;
    public bool IsCustom { get; } = isCustom;

    [ObservableProperty] private bool _isOn = isOn;

    public string Tip => (description is { Length: > 0 } d ? d + "\n" : "") +
        (IsOn ? "On in this profile. Click to turn it off." : "Off in this profile. Click to turn it back on.");

    partial void OnIsOnChanged(bool value) => OnPropertyChanged(nameof(Tip));

    [RelayCommand]
    private void Toggle()
    {
        IsOn = !IsOn;
        onToggle(this);
    }
}

/// <summary>One row of the pattern editor: a built-in (editable, resettable) or a custom pattern (editable, removable).</summary>
public sealed partial class PatternEditorRowViewModel : ObservableObject
{
    private readonly Func<PatternEditorRowViewModel, string, string?> _save;
    private readonly Action<PatternEditorRowViewModel> _reset;

    public PatternEditorRowViewModel(
        string name, string regex, bool isBuiltIn, bool isEdited, string hint,
        Func<PatternEditorRowViewModel, string, string?> save, Action<PatternEditorRowViewModel> reset)
    {
        Name = name;
        _regex = regex;
        IsBuiltIn = isBuiltIn;
        _isEdited = isEdited;
        Hint = hint;
        _save = save;
        _reset = reset;
    }

    public string Name { get; }
    public bool IsBuiltIn { get; }
    public string Hint { get; }

    [ObservableProperty] private string _regex;
    [ObservableProperty] private string _error = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanReset))]
    private bool _isEdited;

    /// <summary>Built-ins reset to their shipped regex; custom patterns are removed.</summary>
    public string ResetLabel => IsBuiltIn ? "Reset" : "Remove";
    public bool CanReset => !IsBuiltIn || IsEdited;

    partial void OnRegexChanged(string value) => Error = _save(this, value) ?? "";

    [RelayCommand]
    private void Reset() => _reset(this);
}

/// <summary>
/// One step on one funnel. The record's row is created on the first change, so a funnel nobody
/// touched stores nothing.
/// </summary>
public sealed partial class McpFunnelFilterItemViewModel : ObservableObject
{
    private readonly List<FunnelFilter> _filters;
    private readonly Func<string> _ownerName;
    private readonly Action<string> _onChanged;

    /// <summary>A step of a funnel's own filters (as stored before profiles existed).</summary>
    public McpFunnelFilterItemViewModel(
        McpFunnelRecord funnel,
        FilterCategory category,
        Action<string> onChanged,
        ObservableCollection<EmbeddingProviderItemViewModel>? providers = null,
        ObservableCollection<EntityModelItemViewModel>? entityModels = null)
        : this(funnel.Filters, () => funnel.Name, category, onChanged, providers, entityModels)
    {
    }

    /// <summary>A step of any filter list — in practice a profile's.</summary>
    public McpFunnelFilterItemViewModel(
        List<FunnelFilter> filters,
        Func<string> ownerName,
        FilterCategory category,
        Action<string> onChanged,
        ObservableCollection<EmbeddingProviderItemViewModel>? providers = null,
        ObservableCollection<EntityModelItemViewModel>? entityModels = null)
    {
        _filters = filters;
        _ownerName = ownerName;
        _onChanged = onChanged;
        Category = category;
        Providers = providers ?? [];
        EntityModels = entityModels ?? [];

        LevelChoices = category switch
        {
            FilterCategory.Secrets => [new(FilterLevel.Off, "Off"), new(FilterLevel.Balanced, "Deny")],
            FilterCategory.PersonalData => [new(FilterLevel.Off, "Off"), new(FilterLevel.Balanced, "On")],
            _ => [new(FilterLevel.Off, "Off"), new(FilterLevel.Relaxed, "Relaxed"), new(FilterLevel.Balanced, "Balanced"), new(FilterLevel.Strict, "Strict")],
        };

        var existing = Filter;
        // A level the step no longer offers (e.g. Strict saved for Personal data) shows as "on".
        _level = existing?.Level ?? FilterLevel.Off;
        if (LevelChoices.All(c => c.Level != _level)) _level = FilterLevel.Balanced;
        _action = category == FilterCategory.Secrets ? FilterAction.Block : existing?.Action ?? FilterAction.Block;

        _selectedProvider = Providers.FirstOrDefault(p => p.Record.Id == existing?.EmbeddingProviderId);
        _selectedEntityModel = EntityModels.FirstOrDefault(m => m.Record.Id == existing?.EntityModelId);
        _denyInstructionsText = Lines(existing?.DenyInstructions);
        _denyExamplesText = Lines(existing?.DenyExamples);
        _allowExamplesText = Lines(existing?.AllowExamples);
        _entityLabelsText = existing?.EntityLabels is { Count: > 0 } labels ? string.Join(", ", labels) : "";
        _thresholdText = existing?.Threshold?.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) ?? "";

        RebuildPatterns();
        RebuildSecretRules();
    }

    public FilterCategory Category { get; }
    public int Step => FilterNames.StepOf(Category);
    public string Name => FilterNames.Of(Category);
    public string Title => $"{Step} · {Name}";

    public bool IsSecrets => Category == FilterCategory.Secrets;
    public bool IsPersonalData => Category == FilterCategory.PersonalData;
    public bool IsEntities => Category == FilterCategory.Entities;
    public bool IsRiskyActions => Category == FilterCategory.RiskyActions;
    public bool HasAction => !IsSecrets;

    public IReadOnlyList<LevelChoice> LevelChoices { get; }
    public static IReadOnlyList<FilterAction> AllActions { get; } = Enum.GetValues<FilterAction>();

    public LevelChoice? SelectedLevelChoice
    {
        get => LevelChoices.FirstOrDefault(c => c.Level == Level);
        set
        {
            if (value is null || value.Level == Level) return;
            Level = value.Level;
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LevelExplained), nameof(ActionExplained), nameof(IsOn), nameof(SelectedLevelChoice))]
    private FilterLevel _level;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActionExplained))]
    private FilterAction _action;

    public bool IsOn => Level != FilterLevel.Off;

    /// <summary>What this step does, in words.</summary>
    public string LevelExplained => Category switch
    {
        FilterCategory.Secrets => FiltersViewModel.SecretsDescription(),
        FilterCategory.PersonalData => IsOn
            ? "Checks for each pattern below, on this PC. Click one to switch it off; Edit patterns changes a regex or adds your own."
            : "Off. Finds personal data by pattern — emails, card numbers (Luhn), IBANs (mod-97), phone numbers, German tax and social security numbers, and your own regexes.",
        FilterCategory.Entities => IsOn
            ? $"Reports text the model labels as one of yours with a score of {Threshold:0.00} or more. Runs on this PC."
            : "Off. Finds personal data an entity model recognises — names, places, medical conditions — even where no pattern fits. Needs a GLiNER model (Models, above).",
        _ => IsOn
            ? $"Blocks calls whose closeness to your deny list is {Threshold:0.00} or more (0 = as close as everyday text, 1 = identical). Reads the call after steps 1–3 replaced what they found."
            : "Off. Judges each call by meaning with an embedding model: describe what to deny, give examples, and calls that come close are stopped.",
    };

    private double Threshold => Filter?.Threshold ?? FilterService.PresetThreshold(Level, Category);

    /// <summary>What happens on a match, in words.</summary>
    public string ActionExplained => !IsOn || IsSecrets ? "" : Action switch
    {
        FilterAction.Block => "Block: the call is not sent, and the agent is told which step stopped it. Later steps do not run.",
        _ => "Watch: the call goes through and the match is logged (Settings tab). What matched is still replaced before later steps read the call.",
    };

    partial void OnLevelChanged(FilterLevel value)
    {
        Store($"step {Step} ({Name}) {(value == FilterLevel.Off ? "off" : LevelChoices.First(c => c.Level == value).Label)}.");
        RebuildPatterns();
        RebuildSecretRules();
    }

    // ---- secrets: every rule on; find one to turn it off -----------------------------------

    /// <summary>Rules matching the search, to turn off (or back on) for this profile.</summary>
    public ObservableCollection<DetectorChipViewModel> SecretRuleMatches { get; } = [];

    /// <summary>Rules this profile turned off.</summary>
    public ObservableCollection<DetectorChipViewModel> SecretRulesOff { get; } = [];

    public bool HasSecretRulesOff => SecretRulesOff.Count > 0;

    [ObservableProperty] private string _secretRuleSearch = "";
    [ObservableProperty] private string _secretRuleSearchHint = "";

    partial void OnSecretRuleSearchChanged(string value) => RebuildSecretRules();

    private void RebuildSecretRules()
    {
        SecretRuleMatches.Clear();
        SecretRulesOff.Clear();
        if (!IsSecrets) return;

        var off = Filter?.DisabledDetectors.ToHashSet(StringComparer.Ordinal) ?? [];
        var rules = SecretRuleSet.Current.Rules;

        foreach (var rule in rules.Where(r => off.Contains(r.Id)))
            SecretRulesOff.Add(new DetectorChipViewModel(rule.Id, isCustom: false, isOn: false, ToggleSecretRule, $"{rule.Source}: {rule.Description}"));

        var query = SecretRuleSearch.Trim();
        if (query.Length >= 2)
        {
            var found = rules.Where(r => !off.Contains(r.Id) &&
                (r.Id.Contains(query, StringComparison.OrdinalIgnoreCase) || r.Description.Contains(query, StringComparison.OrdinalIgnoreCase))).ToList();
            foreach (var rule in found.Take(15))
                SecretRuleMatches.Add(new DetectorChipViewModel(rule.Id, isCustom: rule.Source == SecretRuleSet.UserSource, isOn: true, ToggleSecretRule, $"{rule.Source}: {rule.Description}"));
            SecretRuleSearchHint = found.Count == 0 ? "No rule matches." : found.Count > 15 ? $"{found.Count} rules match — the first 15 are shown. Click one to turn it off." : "Click a rule to turn it off.";
        }
        else
        {
            SecretRuleSearchHint = "";
        }

        OnPropertyChanged(nameof(HasSecretRulesOff));
    }

    private void ToggleSecretRule(DetectorChipViewModel chip)
    {
        var filter = EnsureFilter();
        filter.DisabledDetectors.Remove(chip.Name);
        if (!chip.IsOn) filter.DisabledDetectors.Add(chip.Name);

        _onChanged($"'{_ownerName()}': secret rule {chip.Name} {(chip.IsOn ? "turned back on" : "turned off")}.");
        RebuildSecretRules();
    }

    partial void OnActionChanged(FilterAction value) => Store($"step {Step} ({Name}) set to {value} on a match.");

    private FunnelFilter? Filter => _filters.FirstOrDefault(f => f.Category == Category);

    private FunnelFilter EnsureFilter()
    {
        var filter = Filter;
        if (filter is null)
        {
            filter = new FunnelFilter { Category = Category, Level = Level, Action = Action };
            _filters.Add(filter);
        }
        return filter;
    }

    private void Store(string what)
    {
        var filter = EnsureFilter();
        filter.Level = Level;
        filter.Action = IsSecrets ? FilterAction.Block : Action;

        _onChanged($"'{_ownerName()}': {what}");
    }

    // ---- personal data: chips and the pattern editor ---------------------------------------

    public ObservableCollection<DetectorChipViewModel> Chips { get; } = [];
    public ObservableCollection<PatternEditorRowViewModel> PatternRows { get; } = [];

    [ObservableProperty] private bool _isEditingPatterns;
    [ObservableProperty] private string _newPatternName = "";
    [ObservableProperty] private string _newPatternRegex = "";
    [ObservableProperty] private string _patternError = "";

    [RelayCommand]
    private void ToggleEditPatterns() => IsEditingPatterns = !IsEditingPatterns;

    private void RebuildPatterns()
    {
        Chips.Clear();
        PatternRows.Clear();
        if (!IsPersonalData) return;

        var filter = Filter ?? new FunnelFilter { Category = Category };
        var active = PersonalPatterns.Active(filter).Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

        foreach (var d in PersonalPatterns.BuiltIn)
        {
            Chips.Add(new DetectorChipViewModel(d.Name, isCustom: false, active.Contains(d.Name), ToggleChip));

            var edited = filter.RegexOverrides.TryGetValue(d.Name, out var regex);
            var hint = d.Check == PatternCheck.None ? d.Hint : $"{d.Hint} · plus a {d.Check} check";
            PatternRows.Add(new PatternEditorRowViewModel(d.Name, edited ? regex! : d.Regex, isBuiltIn: true, edited, hint, SaveRegex, ResetRow));
        }

        foreach (var p in filter.Patterns)
        {
            Chips.Add(new DetectorChipViewModel(p.Name, isCustom: true, active.Contains(p.Name), ToggleChip));
            PatternRows.Add(new PatternEditorRowViewModel(p.Name, p.Regex, isBuiltIn: false, isEdited: false, "Your pattern", SaveRegex, ResetRow));
        }
    }

    private void ToggleChip(DetectorChipViewModel chip)
    {
        var filter = EnsureFilter();
        var builtIn = PersonalPatterns.BuiltIn.FirstOrDefault(d => d.Name == chip.Name);

        filter.DisabledDetectors.Remove(chip.Name);
        filter.EnabledDetectors.Remove(chip.Name);

        // Only what differs from the default is stored.
        if (builtIn is { OnByDefault: false } && chip.IsOn) filter.EnabledDetectors.Add(chip.Name);
        if ((builtIn is null || builtIn.OnByDefault) && !chip.IsOn) filter.DisabledDetectors.Add(chip.Name);

        _onChanged($"'{_ownerName()}': {chip.Name} {(chip.IsOn ? "checked" : "no longer checked")}.");
    }

    private string? SaveRegex(PatternEditorRowViewModel row, string regex)
    {
        var error = PersonalPatterns.Validate(row.Name, regex);
        if (error is not null) return error;

        var filter = EnsureFilter();
        if (row.IsBuiltIn)
        {
            var shipped = PersonalPatterns.BuiltIn.First(d => d.Name == row.Name).Regex;
            filter.RegexOverrides.Remove(row.Name);
            if (regex != shipped) filter.RegexOverrides[row.Name] = regex;
            row.IsEdited = regex != shipped;
        }
        else
        {
            filter.Patterns.First(p => p.Name == row.Name).Regex = regex;
        }

        _onChanged($"'{_ownerName()}': pattern '{row.Name}' saved.");
        return null;
    }

    private void ResetRow(PatternEditorRowViewModel row)
    {
        var filter = EnsureFilter();
        if (row.IsBuiltIn)
        {
            filter.RegexOverrides.Remove(row.Name);
            _onChanged($"'{_ownerName()}': pattern '{row.Name}' reset to the built-in.");
        }
        else
        {
            filter.Patterns.RemoveAll(p => p.Name == row.Name);
            filter.DisabledDetectors.Remove(row.Name);
            _onChanged($"'{_ownerName()}': pattern '{row.Name}' removed.");
        }
        RebuildPatterns();
    }

    [RelayCommand]
    private void AddPattern()
    {
        var name = NewPatternName.Trim();
        PatternError = PersonalPatterns.Validate(name, NewPatternRegex) ?? "";
        if (PatternError.Length == 0 && (PersonalPatterns.BuiltIn.Any(d => d.Name == name) || Filter?.Patterns.Any(p => p.Name == name) == true))
        {
            PatternError = "A pattern with that name already exists here.";
        }
        if (PatternError.Length > 0) return;

        EnsureFilter().Patterns.Add(new CustomPattern { Name = name, Regex = NewPatternRegex });
        _onChanged($"'{_ownerName()}': pattern '{name}' added.");

        NewPatternName = "";
        NewPatternRegex = "";
        RebuildPatterns();
    }

    // ---- entities --------------------------------------------------------------------------

    public ObservableCollection<EntityModelItemViewModel> EntityModels { get; }

    /// <summary>Labels that suit the PII-tuned GLiNER models; any plain words work.</summary>
    public static string SuggestedLabels => "person, address, phone number, email, date of birth, medical condition, passport number, bank account number, credit card number";

    [ObservableProperty] private EntityModelItemViewModel? _selectedEntityModel;
    [ObservableProperty] private string _entityLabelsText;

    partial void OnSelectedEntityModelChanged(EntityModelItemViewModel? value)
    {
        EnsureFilter().EntityModelId = value?.Record.Id;
        _onChanged($"'{_ownerName()}': Entities now uses {value?.Name ?? "no model"}.");
    }

    partial void OnEntityLabelsTextChanged(string value)
    {
        EnsureFilter().EntityLabels = [.. value.Split([',', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(25)];
        _onChanged($"'{_ownerName()}': Entities labels saved.");
    }

    [RelayCommand]
    private void UseSuggestedLabels() => EntityLabelsText = SuggestedLabels;

    // ---- meaning ---------------------------------------------------------------------------

    public ObservableCollection<EmbeddingProviderItemViewModel> Providers { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedProviderIsOnnx), nameof(SelectedProviderIsApi))]
    private EmbeddingProviderItemViewModel? _selectedProvider;

    // Plain bools for the view: binding IsVisible through a null SelectedProvider shows the element,
    // and a FallbackValue there silently stops Avalonia compiling the app's XAML at all.
    public bool SelectedProviderIsOnnx => SelectedProvider?.IsOnnx == true;
    public bool SelectedProviderIsApi => SelectedProvider is { IsOnnx: false };

    [ObservableProperty] private string _denyInstructionsText;
    [ObservableProperty] private string _denyExamplesText;
    [ObservableProperty] private string _allowExamplesText;
    [ObservableProperty] private string _thresholdText;
    [ObservableProperty] private string _thresholdError = "";

    partial void OnSelectedProviderChanged(EmbeddingProviderItemViewModel? value)
    {
        EnsureFilter().EmbeddingProviderId = value?.Record.Id;
        _onChanged($"'{_ownerName()}': Meaning now uses {value?.Name ?? "no model"}.");
    }

    partial void OnDenyInstructionsTextChanged(string value) => StoreList(f => f.DenyInstructions = ToList(value), "deny list");
    partial void OnDenyExamplesTextChanged(string value) => StoreList(f => f.DenyExamples = ToList(value), "deny examples");
    partial void OnAllowExamplesTextChanged(string value) => StoreList(f => f.AllowExamples = ToList(value), "allow examples");

    partial void OnThresholdTextChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            ThresholdError = "";
            EnsureFilter().Threshold = null;
        }
        else if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var t) && t is > 0 and <= 1)
        {
            ThresholdError = "";
            EnsureFilter().Threshold = t;
        }
        else
        {
            ThresholdError = "A number between 0 and 1, like 0.40 — or leave it empty to use the level.";
            return;
        }

        OnPropertyChanged(nameof(LevelExplained));
        _onChanged($"'{_ownerName()}': step {Step} threshold {(string.IsNullOrWhiteSpace(value) ? "follows the level" : $"set to {value}")}.");
    }

    private void StoreList(Action<FunnelFilter> set, string what)
    {
        set(EnsureFilter());
        _onChanged($"'{_ownerName()}': Meaning {what} saved.");
    }

    private static string Lines(List<string>? items) => items is null ? "" : string.Join(Environment.NewLine, items);

    private static List<string> ToList(string text) =>
        [.. text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Take(50).Select(l => l.Length > 500 ? l[..500] : l)];
}
