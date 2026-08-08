using CommunityToolkit.Mvvm.ComponentModel;
using RavensPort.Core.Models;

namespace RavensPort.UI.ViewModels;

/// <summary>
/// How a credential's status reads at a glance, as a fact rather than as a colour.
///
/// The view model used to hand the view a ready-made brush, which meant it had to reach into the
/// application's resources to find one — a view model asking a UI framework for a colour. This says
/// what is true and leaves the palette where the palette lives.
/// </summary>
public enum CredentialStatusKind
{
    /// <summary>Nothing is wrong, and nothing has been done yet — never connected.</summary>
    Idle,

    /// <summary>Usable right now: a live token, or a stored API key.</summary>
    Healthy,

    /// <summary>Needs the user: expired, revoked, or never stored.</summary>
    Broken,
}

/// <summary>Thin bindable wrapper around a CredentialRecord — call Refresh() to re-pull display text after the record changes.</summary>
public sealed partial class CredentialItemViewModel(CredentialRecord record) : ObservableObject
{
    public CredentialRecord Record => record;

    public Guid Id => record.Id;
    public string Name => record.Name;
    public string ScopesDisplay => record.Kind == CredentialKind.ApiKey
        ? record.ToDefaultInjection().Describe()
        : string.Join(", ", record.Scopes);

    public string KindDisplay => record.Kind == CredentialKind.ApiKey ? "API key" : "OAuth2";

    /// <summary>
    /// Connect / Disconnect / Refresh are browser-flow and token operations; an API key has
    /// nothing to authorize and nothing to refresh, so offering them advertises actions that
    /// would do nothing.
    /// </summary>
    public bool IsOAuth => record.Kind == CredentialKind.OAuth2;

    /// <summary>Whether the Test button is worth offering — it needs an endpoint to call.</summary>
    public bool CanTest => !string.IsNullOrWhiteSpace(record.TestEndpoint);

    [ObservableProperty] private string _statusDisplay = "Not connected";
    [ObservableProperty] private CredentialStatusKind _statusKind = CredentialStatusKind.Idle;
    [ObservableProperty] private bool _isConnected;

    public CredentialItemViewModel Refresh()
    {
        (StatusDisplay, StatusKind, IsConnected) = record switch
        {
            // An API key does not expire and is never "connected" in the OAuth sense; it is
            // either stored or it is not. Reporting it through the token states would have shown
            // every API key as permanently "Not connected".
            { Kind: CredentialKind.ApiKey } c => string.IsNullOrEmpty(c.ApiKey)
                ? ("No API key stored", CredentialStatusKind.Broken, false)
                : ("API key stored", CredentialStatusKind.Healthy, true),
            { NeedsReconnect: true } => ("Needs reconnect", CredentialStatusKind.Broken, false),
            { Token: null } => ("Not connected", CredentialStatusKind.Idle, false),
            { Token: { } t } when t.IsExpiringWithin(TimeSpan.Zero) => ("Expired", CredentialStatusKind.Broken, false),
            { Token: { } t } => ($"Connected · expires {t.ExpiresAtUtc.ToLocalTime():t}", CredentialStatusKind.Healthy, true),
        };

        // Name/ScopesDisplay read straight from the record rather than caching, so after an
        // edit we still need to raise change notifications for them explicitly.
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(ScopesDisplay));
        OnPropertyChanged(nameof(KindDisplay));
        OnPropertyChanged(nameof(IsOAuth));
        OnPropertyChanged(nameof(CanTest));
        return this;
    }
}
