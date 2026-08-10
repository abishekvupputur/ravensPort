namespace RavensPort.Core.Auth;

/// <summary>
/// Parses the "a=1&amp;b=2" extra-parameters field a credential carries.
///
/// Shared by the interactive flow, which puts them on the authorization request, and the client
/// credentials flow, which has no authorization request and puts them on the token request
/// instead. One parser, because "audience=https://api.example.com/" has to survive both paths
/// identically.
/// </summary>
public static class ExtraParameters
{
    /// <summary>
    /// Values are percent-decoded, because a user copying a parameter out of a provider's docs
    /// gets it in encoded form — leaving it encoded meant it was encoded a second time on the
    /// wire and the provider saw a literal "%2F" where a "/" was intended.
    /// </summary>
    public static IEnumerable<KeyValuePair<string, string>> Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) yield break;

        foreach (var segment in raw.Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = segment.Split('=', 2);
            if (parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[0]))
            {
                yield return new KeyValuePair<string, string>(
                    Uri.UnescapeDataString(parts[0].Trim()),
                    Uri.UnescapeDataString(parts[1].Trim()));
            }
        }
    }
}
