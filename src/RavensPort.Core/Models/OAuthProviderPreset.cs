namespace RavensPort.Core.Models;

/// <summary>
/// Prefill template for the Add-Credential form. Never persisted — once a credential is
/// created, it owns a full copy of the provider config and keeps working even if the
/// preset it came from changes later.
/// </summary>
/// <param name="DeviceAuthorizationEndpointHint">
/// Where the device code grant asks for a user code, when the provider offers one at all. Null
/// means "this provider has no device flow, or does not publish a fixed address for it" — the
/// editor then leaves the field to be filled in by hand rather than guessing.
/// </param>
/// <param name="DeviceCodeHelpText">
/// Replaces <paramref name="HelpText"/> when the device flow is selected. The two flows are
/// enabled and registered differently at the same provider often enough that one paragraph
/// cannot serve both: GitHub wants a checkbox ticked, Google wants a different client type
/// entirely, and neither has anything to do with the redirect URI advice.
/// </param>
public sealed record OAuthProviderPreset(
    string Name,
    string? Authority,
    string? AuthorizationEndpointHint,
    string? TokenEndpointHint,
    bool RequiresIdToken,
    bool UsesPkce,
    IReadOnlyList<string> DefaultScopes,
    string? HelpText,
    string? DeviceAuthorizationEndpointHint = null,
    string? DeviceCodeHelpText = null)
{
    public static readonly OAuthProviderPreset Google = new(
        Name: "Google",
        Authority: "https://accounts.google.com",
        AuthorizationEndpointHint: null,
        TokenEndpointHint: null,
        RequiresIdToken: true,
        UsesPkce: true,
        DefaultScopes: ["openid", "email", "profile"],
        HelpText: "Register the OAuth client as a 'Desktop app' type in Google Cloud Console — " +
                   "only Desktop-type clients allow the arbitrary-port loopback redirect this app uses.",
        DeviceAuthorizationEndpointHint: "https://oauth2.googleapis.com/device/code",
        DeviceCodeHelpText: "Google issues device codes only to a client registered as 'TVs and Limited "
                   + "Input devices' — a Desktop-app client is refused here. That client type also "
                   + "supports a narrower set of scopes than the browser flow does.");

    /// <summary>
    /// GitHub. Deliberately not an Authority: GitHub publishes no OIDC discovery document for
    /// its OAuth endpoints, so discovery would 404 and the endpoints have to be given directly.
    ///
    /// An OAuth App token has no expiry and no refresh token — the app records that as "no
    /// expiry advertised" rather than inventing one. A GitHub App acting on behalf of a user
    /// with expiring tokens turned on returns both, and refreshes like any other provider.
    /// </summary>
    public static readonly OAuthProviderPreset GitHub = new(
        Name: "GitHub",
        Authority: null,
        AuthorizationEndpointHint: "https://github.com/login/oauth/authorize",
        TokenEndpointHint: "https://github.com/login/oauth/access_token",
        RequiresIdToken: false,
        UsesPkce: true,
        DefaultScopes: ["read:user"],
        HelpText: "Register an OAuth App under GitHub Settings → Developer settings, and paste the "
                   + "redirect URI above into its 'Authorization callback URL' — GitHub matches it "
                   + "exactly. Scopes are GitHub's own names ('repo', 'read:org', 'gist'), not URLs.",
        DeviceAuthorizationEndpointHint: "https://github.com/login/device/code",
        DeviceCodeHelpText: "Tick 'Enable Device Flow' in the OAuth App's settings — it is off by "
                   + "default, and without it GitHub refuses the request. No callback URL is "
                   + "involved. Scopes are GitHub's own names ('repo', 'read:org'), not URLs.");

    public static readonly OAuthProviderPreset Nextcloud = new(
        Name: "Nextcloud",
        Authority: null,
        AuthorizationEndpointHint: "https://<your-nextcloud>/apps/oauth2/authorize",
        TokenEndpointHint: "https://<your-nextcloud>/apps/oauth2/api/v1/token",
        RequiresIdToken: false,
        UsesPkce: false,
        DefaultScopes: [],
        HelpText: "Create an OAuth2 client under Nextcloud Settings → Security → OAuth2. " +
                   "Fill in the Authorization/Token endpoints using your instance's domain.");

    public static readonly OAuthProviderPreset Custom = new(
        Name: "Custom",
        Authority: null,
        AuthorizationEndpointHint: null,
        TokenEndpointHint: null,
        RequiresIdToken: false,
        UsesPkce: true,
        DefaultScopes: [],
        HelpText: "Enter the authorization and token endpoints (or an Authority for OIDC discovery) for any OAuth2 app.",
        DeviceCodeHelpText: "Enter the device authorization and token endpoints for any provider "
                   + "implementing RFC 8628. An OIDC provider publishes the first as "
                   + "'device_authorization_endpoint' in its discovery document.");

    public static readonly IReadOnlyList<OAuthProviderPreset> All = [Google, GitHub, Nextcloud, Custom];

    /// <summary>The help paragraph that applies to the flow being configured.</summary>
    public string? HelpTextFor(CredentialKind kind) => kind == CredentialKind.DeviceCode
        ? DeviceCodeHelpText ?? "This provider does not publish a device authorization endpoint. "
                                + "Enter one by hand if it has one."
        : HelpText;
}
