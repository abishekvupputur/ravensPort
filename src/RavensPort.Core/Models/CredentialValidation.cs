namespace RavensPort.Core.Models;

/// <summary>
/// One place deciding whether a credential's own fields are usable, so the editor and the code
/// that puts the secret on the wire cannot disagree about it.
/// </summary>
public static class CredentialValidation
{
    /// <summary>
    /// Validates a typed-in API key. Returns null when acceptable, or a message suitable for
    /// showing in the UI footer.
    ///
    /// The control-character check is the load-bearing one. An OAuth access token arrives from a
    /// provider and is structurally constrained; an API key is whatever someone pasted, and a
    /// stray CR or LF in a value written into a header ends the header line and lets the rest be
    /// read as further headers — request splitting, aimed at the upstream. A key pasted out of a
    /// wrapped email or a text editor picks those up without anyone noticing.
    /// </summary>
    public static string? ValidateApiKey(string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return "API key is required.";
        }

        return apiKey.Any(char.IsControl)
            ? "API key may not contain control characters (including newlines and tabs). "
              + "Check for a line break picked up when the key was copied."
            : null;
    }

    /// <summary>
    /// Validates the optional test endpoint. Returns null when acceptable — including when it is
    /// blank, since the field is optional — or a message suitable for the UI footer.
    ///
    /// Held to the same transport rule as every other endpoint: the credential's secret is sent
    /// there, so plain http off-localhost would put it on the wire in cleartext.
    /// </summary>
    public static string? ValidateTestEndpoint(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        return UrlValidation.ValidateEndpoint(url, "Test endpoint");
    }

    /// <summary>
    /// Validates a pasted Google service account key file. Returns null when acceptable, or a
    /// message suitable for the UI footer.
    ///
    /// <paramref name="scopes"/> is checked alongside it because a service account token is
    /// worthless without them: unlike a browser flow, where the provider's consent screen refuses
    /// an empty scope list out loud, Google's token endpoint accepts a scopeless assertion and
    /// returns a token that every API then rejects.
    /// </summary>
    public static string? ValidateServiceAccount(string? json, IReadOnlyList<string> scopes, string? subject)
    {
        if (GoogleServiceAccountKey.TryParse(json, out var parseError) is null)
        {
            return parseError;
        }

        if (scopes.Count == 0)
        {
            return "At least one scope is required for a service account — Google issues the token "
                   + "without complaint and every API then rejects it. Use the full URLs, e.g. "
                   + "https://www.googleapis.com/auth/drive.readonly";
        }

        // The subject becomes a claim in a signed JWT. It is an address, not free text, and a
        // stray newline from a copy-paste would be signed along with everything else.
        return !string.IsNullOrWhiteSpace(subject) && (subject.Any(char.IsControl) || subject.Any(char.IsWhiteSpace))
            ? "Impersonated user must be a single email address with no spaces or line breaks."
            : null;
    }

    /// <summary>
    /// Validates the fields an OAuth2 client_credentials grant needs. Returns null when
    /// acceptable, or a message suitable for the UI footer.
    ///
    /// <paramref name="hasClientSecret"/> rather than the secret itself: on an edit a blank box
    /// means "keep the stored one", so the check is whether a secret exists at all, not what it is.
    /// </summary>
    public static string? ValidateClientCredentials(string? clientId, bool hasClientSecret, string? tokenEndpoint)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return "Client ID is required.";
        }

        if (!hasClientSecret)
        {
            return "Client secret is required — a client credentials grant has nothing else to prove who it is.";
        }

        // No authorization endpoint is involved: nothing opens a browser, so the token endpoint is
        // the only address, and saying so is more use than an empty-field complaint.
        return string.IsNullOrWhiteSpace(tokenEndpoint)
            ? "Token endpoint is required. A client credentials grant never opens a browser, so this "
              + "is the only endpoint it uses."
            : UrlValidation.ValidateEndpoint(tokenEndpoint, "Token endpoint");
    }

    /// <summary>
    /// Validates the fields a device code grant needs. Returns null when acceptable, or a message
    /// suitable for the UI footer.
    ///
    /// No client secret is required. RFC 8628 exists precisely for clients that cannot keep one —
    /// most providers issue device codes to public clients, and demanding a secret here would
    /// block the ordinary case.
    /// </summary>
    public static string? ValidateDeviceCode(string? clientId, string? deviceEndpoint, string? tokenEndpoint)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return "Client ID is required.";
        }

        if (string.IsNullOrWhiteSpace(deviceEndpoint))
        {
            return "Device authorization endpoint is required — it is where the user code is issued. "
                   + "This is not the same address as the browser authorization endpoint.";
        }

        return UrlValidation.ValidateEndpoint(deviceEndpoint, "Device authorization endpoint")
               ?? (string.IsNullOrWhiteSpace(tokenEndpoint)
                   ? "Token endpoint is required — it is polled until the code is approved."
                   : UrlValidation.ValidateEndpoint(tokenEndpoint, "Token endpoint"));
    }

    /// <summary>
    /// Validates everything about a credential that does not depend on which provider it is:
    /// its name, the secret it holds, where that secret goes, and the optional test endpoint.
    /// </summary>
    public static string? Validate(CredentialRecord credential)
    {
        if (string.IsNullOrWhiteSpace(credential.Name))
        {
            return "Name is required.";
        }

        var secretError = credential.Kind switch
        {
            CredentialKind.ApiKey => ValidateApiKey(credential.ApiKey),
            CredentialKind.GoogleServiceAccount => ValidateServiceAccount(
                credential.ServiceAccountJson, credential.Scopes, credential.ServiceAccountSubject),
            CredentialKind.ClientCredentials => ValidateClientCredentials(
                credential.ClientId, !string.IsNullOrEmpty(credential.ClientSecret), credential.TokenEndpoint),
            CredentialKind.DeviceCode => ValidateDeviceCode(
                credential.ClientId, credential.DeviceAuthorizationEndpoint, credential.TokenEndpoint),
            _ => null,
        };

        return secretError
               ?? RouteValidation.ValidateCredentialInjection(
                   credential.DefaultPlacement, credential.DefaultParameterName, credential.DefaultValuePrefix)
               ?? ValidateTestEndpoint(credential.TestEndpoint);
    }
}
