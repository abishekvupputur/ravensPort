using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RavensPort.Core.Filters;
using RavensPort.Core.Models;

namespace RavensPort.UI.ViewModels;

/// <summary>Step 5's models: the Jev API (or laya-serve), Laya in ONNX, or any zero-shot / cross-encoder ONNX model.</summary>
public sealed partial class FiltersViewModel
{
    public ObservableCollection<DecisionModelItemViewModel> DecisionModels { get; } = [];

    public IReadOnlyList<DecisionKindChoice> DecisionKinds { get; } =
        [.. Enum.GetValues<DecisionModelKind>().Select(k => new DecisionKindChoice(k, DecisionModelRecord.Describe(k)))];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NewDecisionIsApi), nameof(NewDecisionIsOnnx), nameof(NewDecisionIsRemote), nameof(NewDecisionHint), nameof(NewDecisionKindChoice))]
    private DecisionModelKind _newDecisionKind = DecisionModelKind.ZeroShotOnnx;

    [ObservableProperty] private string _newDecisionName = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NewDecisionIsRemote))]
    private string _newDecisionBaseUrl = DecisionModelRecord.JevBaseUrl;

    [ObservableProperty] private string _newDecisionModel = "jev-latest";
    [ObservableProperty] private CredentialChoice? _newDecisionCredential;
    [ObservableProperty] private string _newDecisionPath = "";
    [ObservableProperty] private string _newDecisionError = "";

    public DevicePickerViewModel NewDecisionRuns => _newDecisionRuns ??= new DevicePickerViewModel(OnnxDevice.Cpu, 0, Gpus);
    private DevicePickerViewModel? _newDecisionRuns;

    public DecisionKindChoice? NewDecisionKindChoice
    {
        get => DecisionKinds.FirstOrDefault(k => k.Kind == NewDecisionKind);
        set
        {
            if (value is not null) NewDecisionKind = value.Kind;
        }
    }

    public bool NewDecisionIsApi => NewDecisionKind == DecisionModelKind.JevApi;
    public bool NewDecisionIsOnnx => !NewDecisionIsApi;
    public bool NewDecisionIsRemote => new DecisionModelRecord { Name = "", Kind = NewDecisionKind, BaseUrl = NewDecisionBaseUrl }.IsRemote;

    public string NewDecisionHint => NewDecisionKind switch
    {
        DecisionModelKind.JevApi =>
            $"TypeSafe's Jev: {DecisionModelRecord.JevBaseUrl}, model jev-latest, with its API key as a credential. " +
            $"Laya on this PC: run `pip install \"laya[serve]\"` and `laya-serve`, then use {DecisionModelRecord.LayaServeBaseUrl} and no key. " +
            "Any server that answers POST /v1/systemone with noul questions works.",
        DecisionModelKind.LayaOnnx =>
            "A Laya checkpoint exported to ONNX with Laya's own exporter (laya-dotnet/tools/export_onnx.py): the folder holding model.onnx " +
            "(or encoder.onnx + head.onnx), rl_agent_config.json and tokenizer.json. convaiinnovations/laya (English) or its multilingual checkpoint. " +
            "Laya ships overconfident — start Strict-to-Balanced and check Try it.",
        _ =>
            "Any Hugging Face zero-shot (NLI) or cross-encoder model exported to ONNX: the folder with model.onnx (or onnx/model.onnx), " +
            "tokenizer.json and config.json. For example MoritzLaurer/deberta-v3-base-zeroshot-v2.0, MoritzLaurer/mDeBERTa-v3-base-xnli-multilingual-nli-2mil7, " +
            "facebook/bart-large-mnli, or a bge-reranker. The call is the premise, your statement the hypothesis — phrase it as " +
            "\"This text is about deleting files or data.\"; that is how these models were trained, and they score far better for it.",
    };

    partial void OnNewDecisionKindChanged(DecisionModelKind value)
    {
        if (value == DecisionModelKind.JevApi && string.IsNullOrWhiteSpace(NewDecisionBaseUrl)) NewDecisionBaseUrl = DecisionModelRecord.JevBaseUrl;
    }

    [RelayCommand]
    private void AddDecisionModel()
    {
        NewDecisionError = ValidateNewDecisionModel() ?? "";
        if (NewDecisionError.Length > 0) return;

        var api = NewDecisionIsApi;
        var record = new DecisionModelRecord
        {
            Name = NewDecisionName.Trim(),
            Kind = NewDecisionKind,
            BaseUrl = api ? NewDecisionBaseUrl.Trim() : "",
            Model = api ? NewDecisionModel.Trim() : "",
            CredentialId = api ? NewDecisionCredential?.Id : null,
            ModelPath = api ? "" : NewDecisionPath.Trim().Trim('"'),
            Device = api ? OnnxDevice.Cpu : NewDecisionRuns.Device,
            GpuIndex = api ? 0 : NewDecisionRuns.GpuIndex,
        };

        _configStoreCache.Current.DecisionModels.Add(record);
        Save($"Decision model '{record.Name}' added.");

        NewDecisionName = "";
        NewDecisionPath = "";
        Reload();
    }

    private string? ValidateNewDecisionModel()
    {
        if (string.IsNullOrWhiteSpace(NewDecisionName)) return "Give it a name, e.g. \"DeBERTa zero-shot\" or \"Jev\".";
        if (NameTaken(NewDecisionName)) return "A model with that name already exists.";

        if (NewDecisionIsOnnx)
        {
            var path = NewDecisionPath.Trim().Trim('"');
            return path.Length == 0 ? "Enter the model's folder (or its .onnx file)."
                : File.Exists(path) || Directory.Exists(path) ? null
                : "Nothing exists at that path.";
        }

        if (!Uri.TryCreate(NewDecisionBaseUrl.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return "Enter the API's base URL, e.g. https://api.typesafe.ai/v1.";
        if (NewDecisionIsRemote && NewDecisionCredential?.Id is null)
            return "A remote API needs a key. Add it on the Credentials tab as an \"API key\" credential, then pick it here.";
        return null;
    }

    internal void RemoveDecisionModel(DecisionModelItemViewModel item)
    {
        var users = FunnelsUsing(f => f.DecisionModelId == item.Record.Id);
        if (users.Count > 0)
        {
            StatusMessage = $"'{item.Name}' is used by {string.Join(", ", users)}. Pick another model there first.";
            return;
        }

        _configStoreCache.Current.DecisionModels.Remove(item.Record);
        Save($"Decision model '{item.Name}' removed.");
        Reload();
    }

    internal async Task<string> TestDecisionModelAsync(DecisionModelRecord record)
    {
        try
        {
            var watch = Stopwatch.StartNew();
            string[] states = ["tool: shell\ntext: rm -rf / --no-preserve-root", "tool: weather\ntext: weather in Munich tomorrow"];
            const string statement = "The call deletes files or data.";

            var decide = _clients.GetDecisionModel(record);
            var risky = await Task.Run(() => decide(states[0], [statement], default));
            var harmless = await Task.Run(() => decide(states[1], [statement], default));
            watch.Stop();

            var device = _clients.DeviceOf(record.Id) is { } d ? $" on {d}" : "";
            var verdict = risky[0] > harmless[0] ? "" : " — it did not tell them apart; check Try it before trusting this model.";
            return $"Works{device} — {watch.ElapsedMilliseconds} ms for 2 calls (first run includes loading). " +
                   $"\"{statement}\": rm -rf / {risky[0]:0.00}, a weather lookup {harmless[0]:0.00}{verdict}";
        }
        catch (Exception ex)
        {
            return $"Failed: {ex.Message}";
        }
    }

    private void ReloadDecisionModels()
    {
        DecisionModels.Clear();
        foreach (var model in _configStoreCache.Current.DecisionModels) DecisionModels.Add(new DecisionModelItemViewModel(model, this));
        NewDecisionCredential ??= CredentialChoices.FirstOrDefault();
    }
}

public sealed record DecisionKindChoice(DecisionModelKind Kind, string Label)
{
    public override string ToString() => Label;
}

/// <summary>One configured decision model.</summary>
public sealed partial class DecisionModelItemViewModel : ObservableObject
{
    private readonly FiltersViewModel owner;

    public DecisionModelItemViewModel(DecisionModelRecord record, FiltersViewModel owner)
    {
        Record = record;
        this.owner = owner;
        Runs = new DevicePickerViewModel(record.Device, record.GpuIndex, owner.Gpus, (device, gpu) =>
        {
            Record.Device = device;
            Record.GpuIndex = gpu;
            OnPropertyChanged(nameof(Summary));
            owner.SaveModelChange($"Decision model '{Name}' now runs on {EmbeddingProviderItemViewModel.DeviceLabel(device, gpu, owner.Gpus)}.");
        });
    }

    public DecisionModelRecord Record { get; }
    public string Name => Record.Name;
    public bool IsOnnx => Record.IsOnnx;
    public bool IsRemote => Record.IsRemote;
    public DevicePickerViewModel Runs { get; }

    public string Summary => Record.IsOnnx
        ? $"{DecisionModelRecord.Describe(Record.Kind)} · {Record.ModelPath} · {EmbeddingProviderItemViewModel.DeviceLabel(Record.Device, Record.GpuIndex, owner.Gpus)}"
        : $"{DecisionModelRecord.Describe(Record.Kind)} · {Record.Model} · {Record.BaseUrl}";

    public string RemoteWarning => Uri.TryCreate(Record.BaseUrl, UriKind.Absolute, out var uri)
        ? $"Remote: the text step 5 reads is sent to {uri.Host} — after steps 1–3 have replaced what they found."
        : "Remote: the text step 5 reads is sent to this API.";

    [ObservableProperty] private string _testResult = "";

    [RelayCommand]
    private async Task TestAsync()
    {
        TestResult = "Testing…";
        TestResult = await owner.TestDecisionModelAsync(Record);
    }

    [RelayCommand]
    private void Remove() => owner.RemoveDecisionModel(this);

    public override string ToString() => Name;
}

/// <summary>Step 5 on one profile: the model, the statements (kept in DenyInstructions) and the threshold.</summary>
public sealed partial class McpFunnelFilterItemViewModel
{
    public ObservableCollection<DecisionModelItemViewModel> DecisionModels { get; private set; } = [];

    public bool IsDecision => Category == FilterCategory.Decision;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedDecisionIsOnnx), nameof(SelectedDecisionIsApi))]
    private DecisionModelItemViewModel? _selectedDecisionModel;

    public bool SelectedDecisionIsOnnx => SelectedDecisionModel?.IsOnnx == true;
    public bool SelectedDecisionIsApi => SelectedDecisionModel is { IsOnnx: false };

    internal void UseDecisionModels(ObservableCollection<DecisionModelItemViewModel>? models)
    {
        DecisionModels = models ?? [];
        _selectedDecisionModel = DecisionModels.FirstOrDefault(m => m.Record.Id == Filter?.DecisionModelId);
    }

    partial void OnSelectedDecisionModelChanged(DecisionModelItemViewModel? value)
    {
        EnsureFilter().DecisionModelId = value?.Record.Id;
        _onChanged($"'{_ownerName()}': Decision now uses {value?.Name ?? "no model"}.");
    }
}
