using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RavensPort.App.Services;
using RavensPort.Core.Auth;
using RavensPort.Core.Diagnostics;
using RavensPort.Core.Models;
using RavensPort.Core.Storage;

namespace RavensPort.App.ViewModels;

public sealed partial class CredentialsViewModel : ObservableObject
{
    private readonly ConfigStoreCache _configStoreCache;
    private readonly OAuth2Service _oAuth2Service;
    private readonly TokenRefreshService _tokenRefreshService;
    private readonly CredentialTestService _credentialTestService;
    private readonly ActivityLog _activityLog;

    /// <summary>
    /// Held only so the timer is not collected out from under the view model. Never stopped — the
    /// tab lives as long as the process does.
    /// </summary>
    private readonly IDisposable _statusTimer;

    private CredentialItemViewModel? _editingItem;

    public ObservableCollection<CredentialItemViewModel> Credentials { get; } = [];
    public IReadOnlyList<OAuthProviderPreset> Presets { get; } = OAuthProviderPreset.All;

    /// <summary>OAuth2 or API key — the first choice the form asks for, since it decides the rest.</summary>
    public IReadOnlyList<CredentialKind> Kinds { get; } = Enum.GetValues<CredentialKind>();

    public IReadOnlyList<CredentialPlacement> Placements { get; } = Enum.GetValues<CredentialPlacement>();

    [ObservableProperty] private CredentialKind _selectedKind = CredentialKind.OAuth2;
    [ObservableProperty] private OAuthProviderPreset _selectedPreset = OAuthProviderPreset.Google;
    [ObservableProperty] private string _newName = "";
    [ObservableProperty] private string _newClientId = "";
    [ObservableProperty] private string _newClientSecret = "";
    [ObservableProperty] private string _newApiKey = "";
    [ObservableProperty] private string _newScopes = "";
    [ObservableProperty] private string _newAuthority = "";
    [ObservableProperty] private string _newAuthorizationEndpoint = "";
    [ObservableProperty] private string _newTokenEndpoint = "";
    [ObservableProperty] private bool _newUsesPkce = true;
    [ObservableProperty] private string _redirectUriInfo = "";
    [ObservableProperty] private string _redirectUri = "";
    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private string _formHeaderText = "Add credential";
    [ObservableProperty] private string _saveButtonLabel = "Add credential";
    [ObservableProperty] private string _statusMessage = "Ready.";

    // Where this credential's secret goes by default: what the Test button sends, and what
    // prefills a route's credential entry. The route still owns what it actually forwards with.
    [ObservableProperty] private CredentialPlacement _newDefaultPlacement = CredentialPlacement.Header;
    [ObservableProperty] private string _newDefaultParameterName = CredentialInjection.BearerHeader.Name;
    [ObservableProperty] private string _newDefaultValuePrefix = CredentialInjection.BearerHeader.ValuePrefix;
    [ObservableProperty] private string _newTestEndpoint = "";

    public bool HasCredentials => Credentials.Count > 0;
    public bool HasNoCredentials => Credentials.Count == 0;

    /// <summary>Drives which half of the form is shown; WPF has no negating visibility converter built in.</summary>
    public bool IsApiKeyKind => SelectedKind == CredentialKind.ApiKey;
    public bool IsOAuthKind => SelectedKind == CredentialKind.OAuth2;

    /// <summary>The name box means something different per placement.</summary>
    public string DefaultParameterNameLabel => NewDefaultPlacement switch
    {
        CredentialPlacement.Query => "Query parameter name",
        CredentialPlacement.Body => "Body field name",
        _ => "Header name",
    };

    /// <summary>Live preview, e.g. "header X-Api-Key: &lt;token&gt;".</summary>
    public string DefaultInjectionSummary =>
        new CredentialInjection(NewDefaultPlacement, NewDefaultParameterName, NewDefaultValuePrefix).Describe();

    /// <summary>
    /// A GET carries no body, so a body-placement credential cannot be tested. Said in the form
    /// rather than only on failure, so the test endpoint field does not look broken.
    /// </summary>
    public bool IsUntestablePlacement => NewDefaultPlacement == CredentialPlacement.Body;

    partial void OnSelectedKindChanged(CredentialKind value)
    {
        // Bearer-in-a-header is an OAuth convention; a key-based API almost always wants a bare
        // value in a bespoke header. Only moved when the fields are still at the other kind's
        // defaults, so a value the user typed is left alone.
        var previous = CredentialRecord.DefaultInjectionFor(value == CredentialKind.ApiKey ? CredentialKind.OAuth2 : CredentialKind.ApiKey);
        var replacement = CredentialRecord.DefaultInjectionFor(value);

        if (NewDefaultParameterName == previous.Name) NewDefaultParameterName = replacement.Name;
        if (NewDefaultValuePrefix == previous.ValuePrefix) NewDefaultValuePrefix = replacement.ValuePrefix;

        OnPropertyChanged(nameof(IsApiKeyKind));
        OnPropertyChanged(nameof(IsOAuthKind));
    }

    partial void OnNewDefaultPlacementChanged(CredentialPlacement oldValue, CredentialPlacement newValue)
    {
        var previous = CredentialInjection.DefaultFor(oldValue);
        var replacement = CredentialInjection.DefaultFor(newValue);

        if (NewDefaultParameterName == previous.Name) NewDefaultParameterName = replacement.Name;
        if (NewDefaultValuePrefix == previous.ValuePrefix) NewDefaultValuePrefix = replacement.ValuePrefix;

        OnPropertyChanged(nameof(DefaultParameterNameLabel));
        OnPropertyChanged(nameof(DefaultInjectionSummary));
        OnPropertyChanged(nameof(IsUntestablePlacement));
    }

    partial void OnNewDefaultParameterNameChanged(string value) =>
        OnPropertyChanged(nameof(DefaultInjectionSummary));

    partial void OnNewDefaultValuePrefixChanged(string value) =>
        OnPropertyChanged(nameof(DefaultInjectionSummary));

    /// <summary>
    /// Drives the PKCE checkbox's visibility. Only the Google flow actually honours the flag —
    /// IdentityModel.OidcClient always uses PKCE and offers no way to disable it — so showing
    /// the control for other providers advertised a setting that did nothing.
    /// </summary>
    public bool IsPkceOptionApplicable => ReferenceEquals(SelectedPreset, OAuthProviderPreset.Google);

    /// <summary>Inverse of <see cref="IsPkceOptionApplicable"/>; WPF has no negating visibility converter built in.</summary>
    public bool IsPkceAlwaysOn => !IsPkceOptionApplicable;

    public CredentialsViewModel(
        ConfigStoreCache configStoreCache,
        OAuth2Service oAuth2Service,
        TokenRefreshService tokenRefreshService,
        CredentialTestService credentialTestService,
        ActivityLog activityLog,
        IUiTimerFactory uiTimerFactory)
    {
        _configStoreCache = configStoreCache;
        _oAuth2Service = oAuth2Service;
        _tokenRefreshService = tokenRefreshService;
        _credentialTestService = credentialTestService;
        _activityLog = activityLog;
        ApplyPresetDefaults(_selectedPreset);

        Reload();

        // Empty-state visibility is derived from the collection, so it has to be re-evaluated
        // whenever rows are added or removed.
        Credentials.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasCredentials));
            OnPropertyChanged(nameof(HasNoCredentials));
        };

        _statusTimer = uiTimerFactory.StartRepeating(TimeSpan.FromSeconds(15), RefreshStatuses);
    }

    /// <summary>
    /// Rebuilds the rows from the store. Needed because "Reload from vault" replaces the contents
    /// of the store's lists, which leaves every row here bound to a record that is no longer in it.
    /// </summary>
    public void Reload()
    {
        Credentials.Clear();

        foreach (var record in _configStoreCache.Current.Credentials)
        {
            Credentials.Add(new CredentialItemViewModel(record).Refresh());
        }
    }

    partial void OnSelectedPresetChanged(OAuthProviderPreset value)
    {
        ApplyPresetDefaults(value);
        OnPropertyChanged(nameof(IsPkceOptionApplicable));
        OnPropertyChanged(nameof(IsPkceAlwaysOn));
    }

    private void ApplyPresetDefaults(OAuthProviderPreset preset)
    {
        NewAuthority = preset.Authority ?? "";
        NewAuthorizationEndpoint = preset.AuthorizationEndpointHint ?? "";
        NewTokenEndpoint = preset.TokenEndpointHint ?? "";
        NewScopes = string.Join(", ", preset.DefaultScopes);
        NewUsesPkce = preset.UsesPkce;

        if (preset.Name == "Google")
        {
            RedirectUri = GoogleOAuthService.RedirectUri;
            RedirectUriInfo = "Register this in Google Cloud Console if your client is 'Web application' type. Not required for 'Desktop app' type (Google accepts any loopback port automatically).";
        }
        else
        {
            RedirectUri = LoopbackBrowser.StaticRedirectUri;
            RedirectUriInfo = "Register this exact URI as the redirect/callback URL in your provider's OAuth client settings.";
        }
    }

    [RelayCommand]
    private async Task SaveCredentialAsync()
    {
        if (SelectedKind == CredentialKind.ApiKey)
        {
            await SaveApiKeyCredentialAsync();
            return;
        }

        if (string.IsNullOrWhiteSpace(NewName) || string.IsNullOrWhiteSpace(NewClientId))
        {
            StatusMessage = "Name and Client ID are required.";
            return;
        }

        var scopes = NewScopes.Split([',', ' ', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var authority = string.IsNullOrWhiteSpace(NewAuthority) ? null : NewAuthority.Trim();
        var authorizationEndpoint = string.IsNullOrWhiteSpace(NewAuthorizationEndpoint) ? null : NewAuthorizationEndpoint.Trim();
        var tokenEndpoint = string.IsNullOrWhiteSpace(NewTokenEndpoint) ? null : NewTokenEndpoint.Trim();
        var isGoogle = ReferenceEquals(SelectedPreset, OAuthProviderPreset.Google);

        // These endpoints receive the client secret and refresh token. A pasted or mistyped
        // "http://" here put both on the wire in cleartext and nothing objected.
        var validationError = UrlValidation.ValidateEndpoint(authority, "Authority")
                              ?? UrlValidation.ValidateEndpoint(authorizationEndpoint, "Authorization endpoint")
                              ?? UrlValidation.ValidateEndpoint(tokenEndpoint, "Token endpoint")
                              ?? ValidatePlacementAndTestEndpoint();
        if (validationError is not null)
        {
            StatusMessage = validationError;
            return;
        }

        if (_editingItem is { } editing)
        {
            var record = editing.Record;

            // Inside MutateAsync rather than mutate-then-SaveAsync. These nine assignments are
            // not atomic together, and the refresh loop serializes the same store on a
            // background thread — landing mid-edit persisted a record that was half old and
            // half new (a new authority against an old client id, say), which then fails to
            // authorize in a way that looks nothing like a save race.
            await _configStoreCache.MutateAsync(_ =>
            {
                record.Name = NewName.Trim();
                record.ClientId = NewClientId.Trim();
                if (!string.IsNullOrWhiteSpace(NewClientSecret))
                {
                    // Blank means "keep the existing secret" — we never redisplay stored secrets.
                    record.ClientSecret = NewClientSecret.Trim();
                }
                record.Scopes = scopes;
                record.Authority = authority;
                record.AuthorizationEndpoint = authorizationEndpoint;
                record.TokenEndpoint = tokenEndpoint;
                record.RequiresIdToken = SelectedPreset.RequiresIdToken;
                record.UsesPkce = NewUsesPkce;
                record.IsGoogleProvider = isGoogle;
                ApplyPlacementAndTestEndpoint(record);
            });

            editing.Refresh();
            StatusMessage = $"Saved changes to '{record.Name}'.";
            CancelEdit();
        }
        else
        {
            var record = new CredentialRecord
            {
                Name = NewName.Trim(),
                Kind = CredentialKind.OAuth2,
                ClientId = NewClientId.Trim(),
                ClientSecret = NewClientSecret.Trim(),
                Scopes = scopes,
                Authority = authority,
                AuthorizationEndpoint = authorizationEndpoint,
                TokenEndpoint = tokenEndpoint,
                RequiresIdToken = SelectedPreset.RequiresIdToken,
                UsesPkce = NewUsesPkce,
                IsGoogleProvider = isGoogle,
            };
            ApplyPlacementAndTestEndpoint(record);

            // MutateAsync rather than mutate-then-save: the refresh loop can be serializing the
            // store on another thread, and a List.Add landing mid-serialization throws
            // "Collection was modified" out of that loop.
            await _configStoreCache.MutateAsync(store => store.Credentials.Add(record));
            Credentials.Add(new CredentialItemViewModel(record).Refresh());

            NewName = "";
            NewClientId = "";
            NewClientSecret = "";
            StatusMessage = $"Added '{record.Name}'. Click Connect to authorize.";
        }
    }

    /// <summary>
    /// The API-key branch of Save. Separate from the OAuth one rather than threaded through it
    /// with conditionals: the two share only a name and a placement, and every field the OAuth
    /// path validates (client id, authority, endpoints, scopes, PKCE) is meaningless here.
    /// </summary>
    private async Task SaveApiKeyCredentialAsync()
    {
        if (string.IsNullOrWhiteSpace(NewName))
        {
            StatusMessage = "Name is required.";
            return;
        }

        // On an edit, a blank box means "keep the current key" — the stored key is never
        // redisplayed, exactly as for a client secret.
        var keepExistingKey = IsEditing && string.IsNullOrWhiteSpace(NewApiKey);

        if (!keepExistingKey && CredentialValidation.ValidateApiKey(NewApiKey) is { } keyError)
        {
            StatusMessage = keyError;
            return;
        }

        if (ValidatePlacementAndTestEndpoint() is { } placementError)
        {
            StatusMessage = placementError;
            return;
        }

        if (_editingItem is { } editing)
        {
            var record = editing.Record;

            await _configStoreCache.MutateAsync(_ =>
            {
                record.Name = NewName.Trim();
                record.Kind = CredentialKind.ApiKey;
                if (!keepExistingKey) record.ApiKey = NewApiKey.Trim();
                ApplyPlacementAndTestEndpoint(record);
            });

            editing.Refresh();
            StatusMessage = $"Saved changes to '{record.Name}'.";
            CancelEdit();
            return;
        }

        var created = new CredentialRecord
        {
            Name = NewName.Trim(),
            Kind = CredentialKind.ApiKey,
            ApiKey = NewApiKey.Trim(),
        };
        ApplyPlacementAndTestEndpoint(created);

        await _configStoreCache.MutateAsync(store => store.Credentials.Add(created));
        Credentials.Add(new CredentialItemViewModel(created).Refresh());

        NewName = "";
        NewApiKey = "";
        StatusMessage = string.IsNullOrWhiteSpace(created.TestEndpoint)
            ? $"Added '{created.Name}'. It is ready to attach to a route."
            : $"Added '{created.Name}'. Click Test to check the key against {created.TestEndpoint}.";
    }

    /// <summary>Checks the two fields both kinds share.</summary>
    private string? ValidatePlacementAndTestEndpoint() =>
        RouteValidation.ValidateCredentialInjection(NewDefaultPlacement, NewDefaultParameterName, NewDefaultValuePrefix)
        ?? CredentialValidation.ValidateTestEndpoint(NewTestEndpoint);

    private void ApplyPlacementAndTestEndpoint(CredentialRecord record)
    {
        record.DefaultPlacement = NewDefaultPlacement;
        record.DefaultParameterName = NewDefaultParameterName.Trim();
        record.DefaultValuePrefix = NewDefaultValuePrefix;
        record.TestEndpoint = string.IsNullOrWhiteSpace(NewTestEndpoint) ? null : NewTestEndpoint.Trim();
    }

    /// <summary>
    /// Sends one authenticated GET to the credential's test endpoint and reports what came back.
    ///
    /// Worth a button of its own because nothing else verifies a static API key: an OAuth grant
    /// proves itself during the browser flow, but a pasted key's first sign of being wrong is a
    /// 401 on a real request later, which reads as an upstream problem rather than a typo.
    /// </summary>
    [RelayCommand]
    private async Task TestCredentialAsync(CredentialItemViewModel? item)
    {
        if (item is null) return;

        StatusMessage = $"Testing '{item.Name}'…";

        try
        {
            var result = await _credentialTestService.TestAsync(item.Record);
            StatusMessage = result.Message;
        }
        catch (Exception ex)
        {
            // A test is a diagnostic; it must never be able to take down an always-on tray app.
            StatusMessage = $"Could not test '{item.Name}': {ex.Message}";
            _activityLog.LogError($"TEST '{item.Name}' threw", ex);
        }

        item.Refresh();
    }

    [RelayCommand]
    private void EditCredential(CredentialItemViewModel? item)
    {
        if (item is null) return;

        _editingItem = item;
        IsEditing = true;
        FormHeaderText = $"Edit '{item.Name}'";
        SaveButtonLabel = "Save changes";

        // Set before the rest: the kind decides which half of the form is shown, and its change
        // handler moves the placement defaults, which the assignments below then overwrite with
        // what this credential actually stored.
        SelectedKind = item.Record.Kind;

        // Best-effort preset match so provider-specific hints (redirect URI, help text) still
        // make sense while editing — SelectedPreset itself isn't persisted, only the resolved
        // fields below are, and those are set explicitly right after so they win either way.
        SelectedPreset = item.Record.IsGoogleProvider ? OAuthProviderPreset.Google : OAuthProviderPreset.Custom;

        NewName = item.Record.Name;
        NewClientId = item.Record.ClientId;
        NewClientSecret = "";
        NewApiKey = "";
        NewScopes = string.Join(", ", item.Record.Scopes);
        NewAuthority = item.Record.Authority ?? "";
        NewAuthorizationEndpoint = item.Record.AuthorizationEndpoint ?? "";
        NewTokenEndpoint = item.Record.TokenEndpoint ?? "";
        NewUsesPkce = item.Record.UsesPkce;
        NewDefaultPlacement = item.Record.DefaultPlacement;
        NewDefaultParameterName = item.Record.DefaultParameterName;
        NewDefaultValuePrefix = item.Record.DefaultValuePrefix;
        NewTestEndpoint = item.Record.TestEndpoint ?? "";

        StatusMessage = item.Record.Kind == CredentialKind.ApiKey
            ? "Leave API key blank to keep the current one."
            : "Leave Client secret blank to keep the current one.";
    }

    [RelayCommand]
    private void CancelEdit()
    {
        _editingItem = null;
        IsEditing = false;
        FormHeaderText = "Add credential";
        SaveButtonLabel = "Add credential";
        NewName = "";
        NewClientId = "";
        NewClientSecret = "";
        NewApiKey = "";
        NewTestEndpoint = "";

        var defaults = CredentialRecord.DefaultInjectionFor(SelectedKind);
        NewDefaultPlacement = defaults.Placement;
        NewDefaultParameterName = defaults.Name;
        NewDefaultValuePrefix = defaults.ValuePrefix;

        ApplyPresetDefaults(SelectedPreset);
    }

    [RelayCommand]
    private async Task DeleteCredentialAsync(CredentialItemViewModel? item)
    {
        if (item is null) return;
        if (ReferenceEquals(item, _editingItem)) CancelEdit();
        await _configStoreCache.MutateAsync(store => store.Credentials.Remove(item.Record));
        Credentials.Remove(item);
    }

    [RelayCommand]
    private async Task ConnectAsync(CredentialItemViewModel? item)
    {
        if (item is null) return;
        StatusMessage = $"Opening browser to authorize '{item.Name}'…";
        _activityLog.Log($"CONNECT '{item.Name}' starting OAuth flow");

        // A deliberate reconnect means the user believes the problem is fixed; don't make them
        // sit out the automatic-retry backoff that earlier failures accumulated.
        _tokenRefreshService.ResetBackoff(item.Record);

        try
        {
            // Not wrapped in MutateAsync, unlike the other write paths: the record is mutated
            // inside the service, and the browser consent flow it waits on can take minutes.
            // Holding the store's write lock for that would stall the refresh loop and every
            // other save. Safe because the only field written is Token, and a single reference
            // assignment cannot be observed half-applied by a concurrent serialization.
            var outcome = await _oAuth2Service.StartAuthorizationAsync(item.Record);
            if (outcome.Success)
            {
                await _configStoreCache.SaveAsync();
                StatusMessage = $"'{item.Name}' connected.";
                _activityLog.Log($"CONNECT '{item.Name}' OK — token stored");
            }
            else
            {
                StatusMessage = $"Failed to connect '{item.Name}': {outcome.Error} {outcome.ErrorDescription}".Trim();
                _activityLog.Log($"CONNECT '{item.Name}' FAILED — {outcome.Error} {outcome.ErrorDescription}".Trim());
            }
        }
        catch (Exception ex)
        {
            // Provider/library errors (bad endpoints, missing userinfo, port already bound…)
            // must surface in the UI, never take down an always-on tray app.
            StatusMessage = $"Failed to connect '{item.Name}': {ex.Message}";
            _activityLog.LogError($"CONNECT '{item.Name}' threw", ex);
        }
        item.Refresh();
    }

    [RelayCommand]
    private async Task DisconnectAsync(CredentialItemViewModel? item)
    {
        if (item is null) return;

        // Clears the locally stored token only — it does not revoke the grant at the
        // provider, so Connect re-authorizes without a fresh consent screen in most cases.
        await _configStoreCache.MutateAsync(_ =>
        {
            item.Record.Token = null;
            item.Record.NeedsReconnect = false;
        });
        item.Refresh();
        StatusMessage = $"'{item.Name}' disconnected — stored token cleared (not revoked at the provider).";
        _activityLog.Log($"DISCONNECT '{item.Name}' — stored token cleared");
    }

    [RelayCommand]
    private async Task RefreshNowAsync(CredentialItemViewModel? item)
    {
        if (item is null) return;
        _tokenRefreshService.ResetBackoff(item.Record);
        try
        {
            var token = await _oAuth2Service.RefreshAsync(item.Record);
            if (token is not null)
            {
                await _configStoreCache.SaveAsync();
                StatusMessage = $"'{item.Name}' refreshed.";
            }
            else
            {
                StatusMessage = $"Could not refresh '{item.Name}' — reconnect may be required.";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not refresh '{item.Name}': {ex.Message}";
        }
        item.Refresh();
    }

    private void RefreshStatuses()
    {
        foreach (var item in Credentials) item.Refresh();
    }
}
