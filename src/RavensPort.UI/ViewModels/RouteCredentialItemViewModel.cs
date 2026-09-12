using CommunityToolkit.Mvvm.ComponentModel;
using RavensPort.Core.Models;

namespace RavensPort.UI.ViewModels;

/// <summary>
/// One credential attached to one route, as an editable row: which credential, and where its
/// token goes on the forwarded request.
///
/// A route owns a list of these, so the same editor serves "the one Bearer header" and "a
/// header plus two query parameters plus a body field" without special cases. Edits write
/// straight through to the underlying <see cref="RouteCredential"/> once validated; a rejected
/// value never reaches the record, because an unusable entry makes ProxyConfigBuilder drop the
/// whole route and take a working configuration off the air.
/// </summary>
public sealed class RouteCredentialItemViewModel : ObservableObject
{
    private const string Missing = "(missing)";

    /// <summary>
    /// Asks the owning route whether a proposed version of this entry is acceptable. The route
    /// answers, not this row, because the interesting failure is a *collision* — two entries
    /// writing the same header — which no single row can see on its own.
    /// </summary>
    private readonly Func<RouteCredentialItemViewModel, RouteCredential, string?> _validate;

    private readonly Action<RouteCredentialItemViewModel, string>? _onChanged;
    private readonly Action<string>? _onInvalid;

    private CredentialRecord? _credential;

    public RouteCredentialItemViewModel(
        RouteCredential model,
        IReadOnlyList<CredentialRecord> availableCredentials,
        Func<RouteCredentialItemViewModel, RouteCredential, string?> validate,
        Action<RouteCredentialItemViewModel, string>? onChanged = null,
        Action<string>? onInvalid = null)
    {
        Model = model;
        AvailableCredentials = availableCredentials;
        _validate = validate;
        _onChanged = onChanged;
        _onInvalid = onInvalid;

        _credential = availableCredentials.FirstOrDefault(c => c.Id == model.CredentialId);
    }

    public RouteCredential Model { get; }

    public IReadOnlyList<CredentialRecord> AvailableCredentials { get; }

    public IReadOnlyList<CredentialPlacement> Placements { get; } = RoutesViewModel.AllPlacements;

    /// <summary>
    /// Which credential's token this entry sends. Null when the record points at a credential
    /// that has since been deleted — the route still forwards, just without this one attached.
    /// </summary>
    public CredentialRecord? Credential
    {
        get => _credential;
        set
        {
            if (value is null || value.Id == Model.CredentialId) return;

            var candidate = Model.Clone();
            candidate.CredentialId = value.Id;
            if (Reject(candidate, nameof(Credential))) return;

            Model.CredentialId = value.Id;
            _credential = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CredentialName));
            OnPropertyChanged(nameof(IsMissingCredential));
            NotifyChanged();
        }
    }

    public string CredentialName => _credential?.Name ?? Missing;

    public bool IsMissingCredential => _credential is null;

    /// <summary>
    /// Header / query / body. Switching carries the name and prefix over to the new placement's
    /// defaults *only* when they were still at the old placement's defaults, so picking "Query"
    /// on an untouched entry gives "?access_token=" rather than "?Authorization=Bearer ".
    /// </summary>
    public CredentialPlacement Placement
    {
        get => Model.Placement;
        set
        {
            if (Model.Placement == value) return;

            var previous = CredentialInjection.DefaultFor(Model.Placement);
            var replacement = CredentialInjection.DefaultFor(value);

            var candidate = Model.Clone();
            candidate.Placement = value;
            if (candidate.ParameterName == previous.Name) candidate.ParameterName = replacement.Name;
            if (candidate.ValuePrefix == previous.ValuePrefix) candidate.ValuePrefix = replacement.ValuePrefix;

            if (Reject(candidate, nameof(Placement))) return;

            Model.Placement = candidate.Placement;
            Model.ParameterName = candidate.ParameterName;
            Model.ValuePrefix = candidate.ValuePrefix;

            OnPropertyChanged();
            OnPropertyChanged(nameof(ParameterName));
            OnPropertyChanged(nameof(ValuePrefix));
            OnPropertyChanged(nameof(ParameterNameLabel));
            NotifyChanged();
        }
    }

    public string ParameterName
    {
        get => Model.ParameterName;
        set
        {
            var incoming = (value ?? "").Trim();
            if (incoming == Model.ParameterName) return;

            var candidate = Model.Clone();
            candidate.ParameterName = incoming;
            if (Reject(candidate, nameof(ParameterName))) return;

            Model.ParameterName = incoming;
            OnPropertyChanged();
            NotifyChanged();
        }
    }

    public string ValuePrefix
    {
        get => Model.ValuePrefix;
        set
        {
            var incoming = value ?? "";
            if (incoming == Model.ValuePrefix) return;

            var candidate = Model.Clone();
            candidate.ValuePrefix = incoming;
            if (Reject(candidate, nameof(ValuePrefix))) return;

            Model.ValuePrefix = incoming;
            OnPropertyChanged();
            NotifyChanged();
        }
    }

    /// <summary>Label for the name box — it means something different per placement.</summary>
    public string ParameterNameLabel => Placement switch
    {
        CredentialPlacement.Query => "Query parameter name",
        CredentialPlacement.Body => "Body field name",
        _ => "Header name",
    };

    /// <summary>How this credential is attached, e.g. "header Authorization: Bearer &lt;token&gt;".</summary>
    public string InjectionSummary => Model.ToCredentialInjection().Describe();

    /// <summary>Credential name plus placement, for the grid column and the activity messages.</summary>
    public string Describe() => $"{CredentialName} as {InjectionSummary}";

    /// <summary>
    /// Reports the proposed edit to the owning route. Returns true when it was refused, in which
    /// case the record is untouched and the property change notification puts the stored value
    /// back in the box.
    /// </summary>
    private bool Reject(RouteCredential candidate, string propertyName)
    {
        if (_validate(this, candidate) is not { } error) return false;

        OnPropertyChanged(propertyName);
        _onInvalid?.Invoke(error);
        return true;
    }

    private void NotifyChanged()
    {
        OnPropertyChanged(nameof(InjectionSummary));
        _onChanged?.Invoke(this, $"Credential now sent as {Describe()}.");
    }
}
