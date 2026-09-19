using CommunityToolkit.Mvvm.ComponentModel;
using RavensPort.Core.Models;

namespace RavensPort.UI.ViewModels;

/// <summary>
/// One static header attached to every call a bridge makes, as an editable row.
///
/// Edits write straight through to the underlying <see cref="McpApiBridgeHeader"/> once validated
/// against every other header on the same bridge; a rejected value never reaches the record,
/// mirroring <see cref="RouteCredentialItemViewModel"/> — the interesting failure here is the same
/// shape, two entries writing the same header name, and no single row can see that on its own.
/// </summary>
public sealed class McpApiBridgeHeaderItemViewModel : ObservableObject
{
    private readonly Func<McpApiBridgeHeaderItemViewModel, McpApiBridgeHeader, string?> _validate;
    private readonly Action<McpApiBridgeHeaderItemViewModel, string>? _onChanged;
    private readonly Action<string>? _onInvalid;

    public McpApiBridgeHeaderItemViewModel(
        McpApiBridgeHeader model,
        Func<McpApiBridgeHeaderItemViewModel, McpApiBridgeHeader, string?> validate,
        Action<McpApiBridgeHeaderItemViewModel, string>? onChanged = null,
        Action<string>? onInvalid = null)
    {
        Model = model;
        _validate = validate;
        _onChanged = onChanged;
        _onInvalid = onInvalid;
    }

    public McpApiBridgeHeader Model { get; }

    public string Name
    {
        get => Model.Name;
        set
        {
            var incoming = (value ?? "").Trim();
            if (incoming == Model.Name) return;

            var candidate = Model.Clone();
            candidate.Name = incoming;
            if (Reject(candidate, nameof(Name))) return;

            Model.Name = incoming;
            OnPropertyChanged();
            NotifyChanged();
        }
    }

    public string Value
    {
        get => Model.Value;
        set
        {
            var incoming = value ?? "";
            if (incoming == Model.Value) return;

            var candidate = Model.Clone();
            candidate.Value = incoming;
            if (Reject(candidate, nameof(Value))) return;

            Model.Value = incoming;
            OnPropertyChanged();
            NotifyChanged();
        }
    }

    /// <summary>
    /// Reports the proposed edit to the owning bridge. Returns true when it was refused, in which
    /// case the record is untouched and the property change notification puts the stored value
    /// back in the box.
    /// </summary>
    private bool Reject(McpApiBridgeHeader candidate, string propertyName)
    {
        if (_validate(this, candidate) is not { } error) return false;

        OnPropertyChanged(propertyName);
        _onInvalid?.Invoke(error);
        return true;
    }

    private void NotifyChanged() =>
        _onChanged?.Invoke(this, string.IsNullOrWhiteSpace(Model.Name)
            ? "Custom header updated."
            : $"Custom header '{Model.Name}' updated.");
}
