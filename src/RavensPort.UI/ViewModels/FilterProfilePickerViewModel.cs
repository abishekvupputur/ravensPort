using CommunityToolkit.Mvvm.ComponentModel;
using RavensPort.Core.Models;

namespace RavensPort.UI.ViewModels;

/// <summary>
/// The filter profile dropdown that routes, API bridges, funnels and the upstreams inside a funnel
/// each carry in their editor — the same place their credential or key is chosen. "None" (or, for
/// an upstream, "Same as the funnel"), then every profile from the Filters tab.
///
/// A profile id that no longer resolves is shown as missing rather than as "None": requests through
/// such an endpoint are blocked until another profile is picked, and the dropdown must say so.
/// </summary>
public sealed partial class FilterProfilePickerViewModel : ObservableObject
{
    private readonly Action<Guid?, string> _apply;
    private readonly bool _loading;

    public FilterProfilePickerViewModel(Guid? current, IEnumerable<FilterProfileRecord> profiles, string noneLabel, Action<Guid?, string> apply)
    {
        _loading = true;
        _apply = apply;

        var choices = new List<ProfileChoice> { new(null, noneLabel) };
        choices.AddRange(profiles.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Select(p => new ProfileChoice(p.Id, p.Name)));
        if (current is { } id && choices.All(c => c.Id != id)) choices.Add(new ProfileChoice(id, "⚠ missing profile — calls are blocked"));
        Choices = choices;

        HasProfiles = choices.Count > 1;
        Selected = choices.First(c => c.Id == current);
        _loading = false;
    }

    public IReadOnlyList<ProfileChoice> Choices { get; }

    /// <summary>False until a profile exists on the Filters tab; the editor then says where to make one.</summary>
    public bool HasProfiles { get; }

    [ObservableProperty] private ProfileChoice? _selected;

    partial void OnSelectedChanged(ProfileChoice? value)
    {
        if (_loading || value is null) return;
        _apply(value.Id, value.Id is null ? value.Label.ToLowerInvariant() : $"profile '{value.Label}'");
    }
}

// Each editor that can carry a profile: set by its tab when the rows are built.

public sealed partial class RouteItemViewModel
{
    public FilterProfilePickerViewModel? FilterProfile { get; set; }
}

public sealed partial class ApiBridgeItemViewModel
{
    public FilterProfilePickerViewModel? FilterProfile { get; set; }
}

public sealed partial class McpFunnelItemViewModel
{
    public FilterProfilePickerViewModel? FilterProfile { get; set; }
}

public sealed partial class McpFunnelSourceItemViewModel
{
    public FilterProfilePickerViewModel? FilterProfile { get; set; }
}
