using CommunityToolkit.Mvvm.ComponentModel;
using RavensPort.Core.Models;

namespace RavensPort.App.ViewModels;

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

            // For an app login a failure is about the stored secret or the settings, never about
            // a grant needing to be re-authorized in a browser.
            { NeedsReconnect: true } c => (c.IsSelfIssuing ? "Token request failed" : "Needs reconnect", CredentialStatusKind.Broken, false),

            // Nor is a missing token a problem for one: it has everything it needs and simply has
            // not been asked yet. Saying "Not connected" made a working credential look broken.
            // One arm per answer rather than a ternary inside a ternary (S3358): the three cases
            // are unrelated, and nesting them read as though "not configured" were a kind of
            // "ready".
            { Token: null, IsSelfIssuing: true, HasSecret: true } => ("Ready · token on first use", CredentialStatusKind.Healthy, true),
            { Token: null, IsSelfIssuing: true } => ("Not configured", CredentialStatusKind.Broken, false),
            { Token: null } => ("Not connected", CredentialStatusKind.Idle, false),

            { Token: { } t } when t.IsExpiringWithin(TimeSpan.Zero) => ("Expired", CredentialStatusKind.Broken, false),

            // A token with no advertised expiry — a GitHub OAuth App token, say — is not an
            // expired one, and must not be shown next to a time it does not have. The arm above
            // already matched every null, so no null-forgiving operator is needed here (S8969).
            { Token: { ExpiresAtUtc: null } } => ("Connected · no expiry", CredentialStatusKind.Healthy, true),
            { Token: { } t } => ($"Connected · expires {t.ExpiresAtUtc.Value.ToLocalTime():t}", CredentialStatusKind.Healthy, true),
        };

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
