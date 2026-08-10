using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using RavensPort.Core.Models;
using Application = System.Windows.Application;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;

namespace RavensPort.App.ViewModels;

/// <summary>Thin bindable wrapper around a CredentialRecord — call Refresh() to re-pull display text after the record changes.</summary>
public sealed partial class CredentialItemViewModel(CredentialRecord record) : ObservableObject
{
    public CredentialRecord Record => record;

    public Guid Id => record.Id;
    public string Name => record.Name;
    public string ScopesDisplay => record.Kind == CredentialKind.ApiKey
        ? record.ToDefaultInjection().Describe()
        : string.Join(", ", record.Scopes);

    public string KindDisplay => CredentialKindInfo.ShortLabel(record.Kind);

    /// <summary>
    /// Whether a token operation is on offer at all. An API key has nothing to authorize and
    /// nothing to refresh, so offering the buttons advertises actions that would do nothing.
    /// </summary>
    public bool HasToken => record.Kind != CredentialKind.ApiKey;

    /// <summary>
    /// Whether the button that obtains a token should say "Connect" — i.e. whether pressing it
    /// opens a browser. An app login gets a token without one, so it says "Get token" instead:
    /// calling that "Connect" implies a consent screen that is never going to appear.
    /// </summary>
    public bool IsInteractive => record.IsInteractiveOAuth;

    public string AcquireButtonLabel => record.IsInteractiveOAuth ? "Connect" : "Get token";

    /// <summary>
    /// Disconnect clears the stored token. For an app login that is momentary — the next request
    /// mints another from the key it still holds — so it is offered only where it means something.
    /// </summary>
    public bool CanDisconnect => record.IsInteractiveOAuth && IsConnected;

    /// <summary>Whether the Test button is worth offering — it needs an endpoint to call.</summary>
    public bool CanTest => !string.IsNullOrWhiteSpace(record.TestEndpoint);

    [ObservableProperty] private string _statusDisplay = "Not connected";
    [ObservableProperty] private Brush _statusBrush = Brushes.Gray;
    [ObservableProperty] private bool _isConnected;

    public CredentialItemViewModel Refresh()
    {
        (StatusDisplay, var brushKey, IsConnected) = record switch
        {
            // An API key does not expire and is never "connected" in the OAuth sense; it is
            // either stored or it is not. Reporting it through the token states would have shown
            // every API key as permanently "Not connected".
            { Kind: CredentialKind.ApiKey } c => string.IsNullOrEmpty(c.ApiKey)
                ? ("No API key stored", "ErrorBrush", false)
                : ("API key stored", "SuccessBrush", true),

            // For an app login a failure is about the stored secret or the settings, never about
            // a grant needing to be re-authorized in a browser.
            { NeedsReconnect: true } c => (c.IsSelfIssuing ? "Token request failed" : "Needs reconnect", "ErrorBrush", false),

            // Nor is a missing token a problem for one: it has everything it needs and simply has
            // not been asked yet. Saying "Not connected" made a working credential look broken.
            { Token: null } c => c.IsSelfIssuing
                ? (c.HasSecret ? ("Ready · token on first use", "SuccessBrush", true) : ("Not configured", "ErrorBrush", false))
                : ("Not connected", "MutedTextBrush", false),

            { Token: { } t } when t.IsExpiringWithin(TimeSpan.Zero) => ("Expired", "ErrorBrush", false),

            // A token with no advertised expiry — a GitHub OAuth App token, say — is not an
            // expired one, and must not be shown next to a time it does not have.
            { Token: { ExpiresAtUtc: null } } => ("Connected · no expiry", "SuccessBrush", true),
            { Token: { } t } => ($"Connected · expires {t.ExpiresAtUtc!.Value.ToLocalTime():t}", "SuccessBrush", true),
        };

        StatusBrush = Application.Current?.TryFindResource(brushKey) as Brush ?? Brushes.Gray;

        // Name/ScopesDisplay read straight from the record rather than caching, so after an
        // edit we still need to raise change notifications for them explicitly.
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(ScopesDisplay));
        OnPropertyChanged(nameof(KindDisplay));
        OnPropertyChanged(nameof(HasToken));
        OnPropertyChanged(nameof(IsInteractive));
        OnPropertyChanged(nameof(AcquireButtonLabel));
        OnPropertyChanged(nameof(CanDisconnect));
        OnPropertyChanged(nameof(CanTest));
        return this;
    }
}
