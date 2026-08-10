using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
    private readonly DispatcherTimer _statusTimer;

    private CredentialItemViewModel? _editingItem;

    public ObservableCollection<CredentialItemViewModel> Credentials { get; } = [];
    public IReadOnlyList<OAuthProviderPreset> Presets { get; } = OAuthProviderPreset.All;

    /// <summary>
    /// Which sort of credential — the first choice the form asks for, since it decides the rest.
    /// Bound as descriptions rather than raw enum values so the list reads as English instead of
    /// showing "GoogleServiceAccount".
    /// </summary>
    public IReadOnlyList<CredentialKindInfo> Kinds { get; } = CredentialKindInfo.All;

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
    [ObservableProperty] private string _newDeviceAuthorizationEndpoint = "";
    [ObservableProperty] private bool _newUsesPkce = true;
    [ObservableProperty] private string _newServiceAccountJson = "";
    [ObservableProperty] private string _newServiceAccountSubject = "";
    [ObservableProperty] private string _newExtraParams = "";
    [ObservableProperty] private bool _newSendClientCredentialsInBody;
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

    // Which parts of the form apply. Written out one flag per block rather than derived in XAML,
    // because WPF has no negating visibility converter and no boolean operators in a binding.

    public bool IsApiKeyKind => SelectedKind == CredentialKind.ApiKey;
    public bool IsOAuthKind => SelectedKind == CredentialKind.OAuth2;
    public bool IsClientCredentialsKind => SelectedKind == CredentialKind.ClientCredentials;
    public bool IsServiceAccountKind => SelectedKind == CredentialKind.GoogleServiceAccount;
    public bool IsDeviceCodeKind => SelectedKind == CredentialKind.DeviceCode;

    /// <summary>The kinds that identify themselves with a client id and secret.</summary>
    public bool UsesClientPair => IsOAuthKind || IsClientCredentialsKind || IsDeviceCodeKind;

    /// <summary>Everything except a static API key asks a provider for scopes.</summary>
    public bool UsesScopes => SelectedKind != CredentialKind.ApiKey;

    /// <summary>Every flow that exchanges anything has a token endpoint; only the browser flow has two.</summary>
    public bool UsesTokenEndpoint => UsesClientPair;

    /// <summary>Provider presets prefill endpoints, which only the provider-shaped kinds have.</summary>
    public bool UsesPreset => IsOAuthKind || IsDeviceCodeKind;

    /// <summary>
    /// A device flow client is usually public — RFC 8628 exists for clients that cannot hold a
    /// secret — so the box has to say the field is optional, or an empty one reads as unfinished.
    /// </summary>
    public string ClientSecretLabel => IsDeviceCodeKind ? "Client secret (optional)" : "Client secret";

    /// <summary>The preset's advice for the flow being configured; the two differ per provider.</summary>
    public string? PresetHelpText => SelectedPreset.HelpTextFor(SelectedKind);

    /// <summary>
    /// The two flows with no front channel to carry provider-specific parameters, which therefore
    /// put them on the request that starts the flow instead.
    /// </summary>
    public bool UsesExtraParams => IsClientCredentialsKind || IsDeviceCodeKind;

    /// <summary>Named for the request they actually ride on, which is not the same one.</summary>
    public string ExtraParamsLabel => IsDeviceCodeKind
        ? "Extra device request parameters (optional)"
        : "Extra token request parameters (optional)";

    /// <summary>One sentence explaining the selected kind, shown under the picker.</summary>
    public string KindBlurb => CredentialKindInfo.For(SelectedKind).Blurb;

    /// <summary>What the button that obtains a token should say — no browser opens for an app login.</summary>
    public string SaveHintText => SelectedKind switch
    {
        CredentialKind.OAuth2 => "After saving, click Connect to authorize in your browser.",
        CredentialKind.DeviceCode => "After saving, click Connect. The provider issues a short code, which "
                                     + "is copied to your clipboard and shown below — enter it on any device.",
        CredentialKind.ApiKey => "Ready to attach to a route as soon as it is saved.",
        _ => "No browser flow: the first request through a route mints a token by itself. "
             + "Click Get token to check the settings now instead of on the first real request.",
    };

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

    partial void OnSelectedKindChanged(CredentialKind oldValue, CredentialKind newValue)
    {
        // Bearer-in-a-header is an OAuth convention; a key-based API almost always wants a bare
        // value in a bespoke header. Only moved when the fields are still at the kind we are
        // leaving behind, so a value the user typed is left alone.
        var previous = CredentialRecord.DefaultInjectionFor(oldValue);
        var replacement = CredentialRecord.DefaultInjectionFor(newValue);

        if (NewDefaultParameterName == previous.Name) NewDefaultParameterName = replacement.Name;
        if (NewDefaultValuePrefix == previous.ValuePrefix) NewDefaultValuePrefix = replacement.ValuePrefix;

        // A service account's scopes are always full Google URLs, and nothing else in the form
        // hints at that. Offered only into an empty box, so it cannot overwrite anything.
        if (newValue == CredentialKind.GoogleServiceAccount && string.IsNullOrWhiteSpace(NewScopes))
        {
            NewScopes = "https://www.googleapis.com/auth/cloud-platform";
        }

        OnPropertyChanged(nameof(IsApiKeyKind));
        OnPropertyChanged(nameof(IsOAuthKind));
        OnPropertyChanged(nameof(IsClientCredentialsKind));
        OnPropertyChanged(nameof(IsServiceAccountKind));
        OnPropertyChanged(nameof(IsDeviceCodeKind));
        OnPropertyChanged(nameof(UsesClientPair));
        OnPropertyChanged(nameof(UsesScopes));
        OnPropertyChanged(nameof(UsesTokenEndpoint));
        OnPropertyChanged(nameof(UsesPreset));
        OnPropertyChanged(nameof(UsesExtraParams));
        OnPropertyChanged(nameof(ExtraParamsLabel));
        OnPropertyChanged(nameof(ClientSecretLabel));
        OnPropertyChanged(nameof(PresetHelpText));
        OnPropertyChanged(nameof(KindBlurb));
        OnPropertyChanged(nameof(SaveHintText));

        // Only for the kinds a preset describes. The same provider publishes different addresses
        // for the browser flow and the device flow, so the endpoints filled in for the one being
        // left behind are wrong for the one being entered — but re-running this for, say, a
        // service account would overwrite its scopes with an OAuth preset's.
        if (UsesPreset) ApplyPresetDefaults(SelectedPreset);
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
        ActivityLog activityLog)
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

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _statusTimer.Tick += (_, _) => RefreshStatuses();
        _statusTimer.Start();
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
        OnPropertyChanged(nameof(PresetHelpText));
    }

    private void ApplyPresetDefaults(OAuthProviderPreset preset)
    {
        NewAuthority = preset.Authority ?? "";
        NewAuthorizationEndpoint = preset.AuthorizationEndpointHint ?? "";
        NewDeviceAuthorizationEndpoint = preset.DeviceAuthorizationEndpointHint ?? "";
        NewScopes = string.Join(", ", preset.DefaultScopes);
        NewUsesPkce = preset.UsesPkce;

        // Google is the exception: it discovers its browser-flow token endpoint from the
        // Authority, so the preset carries no hint — but the device flow has no discovery to lean
        // on and needs the address spelled out.
        NewTokenEndpoint = preset.TokenEndpointHint
                           ?? (IsDeviceCodeKind && ReferenceEquals(preset, OAuthProviderPreset.Google)
                               ? "https://oauth2.googleapis.com/token"
                               : "");

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
        switch (SelectedKind)
        {
            case CredentialKind.ApiKey:
                await SaveApiKeyCredentialAsync();
                return;
            case CredentialKind.GoogleServiceAccount:
                await SaveServiceAccountCredentialAsync();
                return;
            case CredentialKind.ClientCredentials:
                await SaveClientCredentialsCredentialAsync();
                return;
            case CredentialKind.DeviceCode:
                await SaveDeviceCodeCredentialAsync();
                return;
        }

        if (string.IsNullOrWhiteSpace(NewName) || string.IsNullOrWhiteSpace(NewClientId))
        {
            StatusMessage = "Name and Client ID are required.";
            return;
        }

        var scopes = ParseScopes();
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
                // Set explicitly: the kind picker stays live during an edit, so a credential can
                // be changed from one sort to another and the record has to follow.
                record.Kind = CredentialKind.OAuth2;
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

    /// <summary>
    /// The Google service account branch of Save.
    ///
    /// There is no client id, no endpoint and no browser here — the downloaded key file is the
    /// whole identity — so it shares only the name, the scopes, and the placement fields with the
    /// other kinds.
    /// </summary>
    private async Task SaveServiceAccountCredentialAsync()
    {
        if (string.IsNullOrWhiteSpace(NewName))
        {
            StatusMessage = "Name is required.";
            return;
        }

        // On an edit, a blank box means "keep the current key file" — it holds a private key and
        // is never redisplayed, exactly as for a client secret.
        var keepExistingKey = IsEditing && string.IsNullOrWhiteSpace(NewServiceAccountJson);
        var scopes = ParseScopes();

        var json = keepExistingKey ? _editingItem?.Record.ServiceAccountJson : NewServiceAccountJson.Trim();

        if (CredentialValidation.ValidateServiceAccount(json, scopes, NewServiceAccountSubject) is { } keyError)
        {
            StatusMessage = keyError;
            return;
        }

        if (ValidatePlacementAndTestEndpoint() is { } placementError)
        {
            StatusMessage = placementError;
            return;
        }

        var subject = string.IsNullOrWhiteSpace(NewServiceAccountSubject) ? null : NewServiceAccountSubject.Trim();

        if (_editingItem is { } editing)
        {
            var record = editing.Record;

            await _configStoreCache.MutateAsync(_ =>
            {
                record.Name = NewName.Trim();
                record.Kind = CredentialKind.GoogleServiceAccount;
                if (!keepExistingKey) record.ServiceAccountJson = NewServiceAccountJson.Trim();
                record.ServiceAccountSubject = subject;
                record.Scopes = scopes;
                ClearPreviousFailure(record);
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
            Kind = CredentialKind.GoogleServiceAccount,
            ServiceAccountJson = NewServiceAccountJson.Trim(),
            ServiceAccountSubject = subject,
            Scopes = scopes,
        };
        ApplyPlacementAndTestEndpoint(created);

        await _configStoreCache.MutateAsync(store => store.Credentials.Add(created));
        Credentials.Add(new CredentialItemViewModel(created).Refresh());

        NewName = "";
        NewServiceAccountJson = "";
        NewServiceAccountSubject = "";
        StatusMessage = $"Added '{created.Name}'. It mints its own tokens — click Get token to check the key now.";
    }

    /// <summary>
    /// The client credentials branch of Save.
    ///
    /// Shares the client id/secret pair with the interactive OAuth path but none of the rest:
    /// nothing opens a browser, so there is no redirect URI, no authorization endpoint, no PKCE
    /// and no id_token to validate.
    /// </summary>
    private async Task SaveClientCredentialsCredentialAsync()
    {
        if (string.IsNullOrWhiteSpace(NewName))
        {
            StatusMessage = "Name is required.";
            return;
        }

        var keepExistingSecret = IsEditing && string.IsNullOrWhiteSpace(NewClientSecret);
        var hasSecret = keepExistingSecret
            ? !string.IsNullOrEmpty(_editingItem?.Record.ClientSecret)
            : !string.IsNullOrWhiteSpace(NewClientSecret);

        var tokenEndpoint = string.IsNullOrWhiteSpace(NewTokenEndpoint) ? null : NewTokenEndpoint.Trim();

        var validationError = CredentialValidation.ValidateClientCredentials(NewClientId, hasSecret, tokenEndpoint)
                              ?? ValidatePlacementAndTestEndpoint();
        if (validationError is not null)
        {
            StatusMessage = validationError;
            return;
        }

        var scopes = ParseScopes();
        var extraParams = string.IsNullOrWhiteSpace(NewExtraParams) ? null : NewExtraParams.Trim();

        if (_editingItem is { } editing)
        {
            var record = editing.Record;

            await _configStoreCache.MutateAsync(_ =>
            {
                record.Name = NewName.Trim();
                record.Kind = CredentialKind.ClientCredentials;
                record.ClientId = NewClientId.Trim();
                if (!keepExistingSecret) record.ClientSecret = NewClientSecret.Trim();
                record.Scopes = scopes;
                record.TokenEndpoint = tokenEndpoint;
                record.ExtraAuthParams = extraParams;
                record.SendClientCredentialsInBody = NewSendClientCredentialsInBody;

                // Cleared rather than left behind. These belong to the browser flow, and a stale
                // authorization endpoint or Google flag on a credential that no longer uses one
                // would be read back into the editor and shown as though it still applied.
                record.Authority = null;
                record.AuthorizationEndpoint = null;
                record.IsGoogleProvider = false;
                record.RequiresIdToken = false;

                ClearPreviousFailure(record);
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
            Kind = CredentialKind.ClientCredentials,
            ClientId = NewClientId.Trim(),
            ClientSecret = NewClientSecret.Trim(),
            Scopes = scopes,
            TokenEndpoint = tokenEndpoint,
            ExtraAuthParams = extraParams,
            SendClientCredentialsInBody = NewSendClientCredentialsInBody,
        };
        ApplyPlacementAndTestEndpoint(created);

        await _configStoreCache.MutateAsync(store => store.Credentials.Add(created));
        Credentials.Add(new CredentialItemViewModel(created).Refresh());

        NewName = "";
        NewClientId = "";
        NewClientSecret = "";
        StatusMessage = $"Added '{created.Name}'. It mints its own tokens — click Get token to check the settings now.";
    }

    /// <summary>
    /// The device code branch of Save.
    ///
    /// Shares the client pair and scopes with the browser flow, but has no redirect URI, no
    /// authorization endpoint and no PKCE — nothing comes back to this machine. What it does have
    /// that nothing else does is the device authorization endpoint.
    /// </summary>
    private async Task SaveDeviceCodeCredentialAsync()
    {
        if (string.IsNullOrWhiteSpace(NewName))
        {
            StatusMessage = "Name is required.";
            return;
        }

        var deviceEndpoint = string.IsNullOrWhiteSpace(NewDeviceAuthorizationEndpoint)
            ? null
            : NewDeviceAuthorizationEndpoint.Trim();
        var tokenEndpoint = string.IsNullOrWhiteSpace(NewTokenEndpoint) ? null : NewTokenEndpoint.Trim();

        var validationError = CredentialValidation.ValidateDeviceCode(NewClientId, deviceEndpoint, tokenEndpoint)
                              ?? ValidatePlacementAndTestEndpoint();
        if (validationError is not null)
        {
            StatusMessage = validationError;
            return;
        }

        var scopes = ParseScopes();
        var extraParams = string.IsNullOrWhiteSpace(NewExtraParams) ? null : NewExtraParams.Trim();
        var keepExistingSecret = IsEditing && string.IsNullOrWhiteSpace(NewClientSecret);

        if (_editingItem is { } editing)
        {
            var record = editing.Record;

            await _configStoreCache.MutateAsync(_ =>
            {
                record.Name = NewName.Trim();
                record.Kind = CredentialKind.DeviceCode;
                record.ClientId = NewClientId.Trim();
                if (!keepExistingSecret) record.ClientSecret = NewClientSecret.Trim();
                record.Scopes = scopes;
                record.DeviceAuthorizationEndpoint = deviceEndpoint;
                record.TokenEndpoint = tokenEndpoint;
                record.ExtraAuthParams = extraParams;

                // Cleared for the same reason as on a client credentials save: these describe a
                // browser round trip this credential does not make, and leaving them behind would
                // read back into the editor as though they still applied.
                record.Authority = null;
                record.AuthorizationEndpoint = null;
                record.IsGoogleProvider = false;
                record.RequiresIdToken = false;

                ClearPreviousFailure(record);
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
            Kind = CredentialKind.DeviceCode,
            ClientId = NewClientId.Trim(),
            ClientSecret = NewClientSecret.Trim(),
            Scopes = scopes,
            DeviceAuthorizationEndpoint = deviceEndpoint,
            TokenEndpoint = tokenEndpoint,
            ExtraAuthParams = extraParams,
        };
        ApplyPlacementAndTestEndpoint(created);

        await _configStoreCache.MutateAsync(store => store.Credentials.Add(created));
        Credentials.Add(new CredentialItemViewModel(created).Refresh());

        NewName = "";
        NewClientId = "";
        NewClientSecret = "";
        StatusMessage = $"Added '{created.Name}'. Click Connect to get a code to enter.";
    }

    /// <summary>
    /// Forgets that a previous token request was refused.
    ///
    /// Editing an app login is the user saying "this is what was wrong" — and a refused mint is
    /// what stops the proxy retrying on the next request. Leaving the flag set would keep a
    /// corrected credential blocked until the background loop's backoff next came round, which
    /// can be an hour.
    /// </summary>
    private void ClearPreviousFailure(CredentialRecord record)
    {
        record.NeedsReconnect = false;
        _tokenRefreshService.ResetBackoff(record);
    }

    /// <summary>Splits the scopes box the same way for every kind that has one.</summary>
    private List<string> ParseScopes() =>
        NewScopes.Split([',', ' ', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    /// <summary>Checks the two fields every kind shares.</summary>
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
        SelectedPreset = MatchPreset(item.Record);

        NewName = item.Record.Name;
        NewClientId = item.Record.ClientId;
        NewClientSecret = "";
        NewApiKey = "";
        NewServiceAccountJson = "";
        NewServiceAccountSubject = item.Record.ServiceAccountSubject ?? "";
        NewScopes = string.Join(", ", item.Record.Scopes);
        NewAuthority = item.Record.Authority ?? "";
        NewAuthorizationEndpoint = item.Record.AuthorizationEndpoint ?? "";
        NewTokenEndpoint = item.Record.TokenEndpoint ?? "";
        NewDeviceAuthorizationEndpoint = item.Record.DeviceAuthorizationEndpoint ?? "";
        NewUsesPkce = item.Record.UsesPkce;
        NewExtraParams = item.Record.ExtraAuthParams ?? "";
        NewSendClientCredentialsInBody = item.Record.SendClientCredentialsInBody;
        NewDefaultPlacement = item.Record.DefaultPlacement;
        NewDefaultParameterName = item.Record.DefaultParameterName;
        NewDefaultValuePrefix = item.Record.DefaultValuePrefix;
        NewTestEndpoint = item.Record.TestEndpoint ?? "";

        StatusMessage = item.Record.Kind switch
        {
            CredentialKind.ApiKey => "Leave API key blank to keep the current one.",
            CredentialKind.GoogleServiceAccount => "Leave the key file blank to keep the current one.",
            _ => "Leave Client secret blank to keep the current one.",
        };
    }

    /// <summary>
    /// Which preset's hints to show while editing. Matched on the token endpoint rather than
    /// stored, because a preset is a prefill template and is deliberately not persisted — without
    /// this, editing a GitHub credential showed Custom's generic help and Custom's redirect
    /// advice, which is not what GitHub needs.
    /// </summary>
    private static OAuthProviderPreset MatchPreset(CredentialRecord record)
    {
        if (record.IsGoogleProvider) return OAuthProviderPreset.Google;

        // The device authorization endpoint is checked first and separately: a device credential
        // has no browser-flow token endpoint to match on, and its own token endpoint may be
        // shared with a provider whose preset is otherwise the wrong one.
        if (record.DeviceAuthorizationEndpoint is { Length: > 0 } deviceEndpoint)
        {
            return OAuthProviderPreset.All.FirstOrDefault(preset =>
                string.Equals(preset.DeviceAuthorizationEndpointHint, deviceEndpoint, StringComparison.OrdinalIgnoreCase))
                ?? OAuthProviderPreset.Custom;
        }

        return OAuthProviderPreset.All.FirstOrDefault(preset =>
            preset.TokenEndpointHint is { } hint &&
            string.Equals(hint, record.TokenEndpoint, StringComparison.OrdinalIgnoreCase))
            ?? OAuthProviderPreset.Custom;
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
        NewServiceAccountJson = "";
        NewServiceAccountSubject = "";
        NewExtraParams = "";
        NewSendClientCredentialsInBody = false;
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

        // Same command for both, because both end in a stored token — but only one of them opens
        // a browser, and telling someone to watch for a consent screen that never appears is how
        // a working credential gets reported as broken.
        var interactive = item.Record.IsInteractiveOAuth;

        StatusMessage = item.Record.Kind switch
        {
            CredentialKind.DeviceCode => $"Asking the provider for a code for '{item.Name}'…",
            CredentialKind.OAuth2 => $"Opening browser to authorize '{item.Name}'…",
            _ => $"Requesting a token for '{item.Name}'…",
        };
        _activityLog.Log(interactive
            ? $"CONNECT '{item.Name}' starting OAuth flow"
            : $"CONNECT '{item.Name}' requesting a token (no browser — app login)");

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
            var outcome = await _oAuth2Service.StartAuthorizationAsync(item.Record, DeviceCodeProgress(item));
            if (outcome.Success)
            {
                await _configStoreCache.SaveAsync();
                StatusMessage = interactive ? $"'{item.Name}' connected." : $"'{item.Name}' got a token.";
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

    /// <summary>
    /// Puts a device flow's code where the user can act on it, the moment the provider issues it
    /// rather than when the flow finishes — the flow only finishes <em>because</em> they acted on
    /// it, so reporting it at the end would be reporting it too late.
    ///
    /// <see cref="Progress{T}"/> captures the dispatcher's synchronization context here on the UI
    /// thread, so the callback lands back on it: the poll loop reporting from a thread-pool thread
    /// must not touch the clipboard or a bound property directly.
    /// </summary>
    private IProgress<DeviceCodePrompt> DeviceCodeProgress(CredentialItemViewModel item) =>
        new Progress<DeviceCodePrompt>(prompt =>
        {
            StatusMessage = $"Enter code {prompt.UserCode} at {prompt.VerificationUri} "
                            + $"(expires {prompt.ExpiresAtUtc.ToLocalTime():t}). Copied to your clipboard. "
                            + $"Waiting for '{item.Name}' to be approved…";

            // Typing a hyphenated code by hand is the one manual step this flow has, and the
            // clipboard removes it for the common case of approving on this same machine.
            // Best-effort: another process can hold the clipboard open, and losing a convenience
            // must not fail a sign-in that is otherwise proceeding.
            try
            {
                System.Windows.Clipboard.SetText(prompt.UserCode);
            }
            catch (Exception ex)
            {
                _activityLog.LogError($"Could not copy the device code for '{item.Name}' to the clipboard", ex);
            }
        });

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
                StatusMessage = item.Record.IsSelfIssuing
                    ? $"Could not get a token for '{item.Name}' — check its stored secret and settings."
                    : $"Could not refresh '{item.Name}' — reconnect may be required.";
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
