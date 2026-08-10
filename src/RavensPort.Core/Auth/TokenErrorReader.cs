using System.Text.Json;
using IdentityModel.Client;

namespace RavensPort.Core.Auth;

/// <summary>
/// Reads what a token endpoint actually said when it refused.
///
/// RFC 6749 §5.2 permits either 400 or 401 for a rejected client and real providers use both, but
/// the OAuth library only treats 400 as a protocol error — anything else is classified as a
/// transport failure and <c>Error</c> becomes the bare HTTP reason phrase. A response naming
/// <c>invalid_client</c> precisely therefore arrived as the word "Unauthorized", which is the one
/// detail worth having, discarded.
///
/// It matters twice over for the device flow, where the error code is not an aside but the
/// control flow: <c>authorization_pending</c> means keep waiting and <c>slow_down</c> means wait
/// longer, and a poller that could not read them would have no way to tell either from a real
/// failure.
/// </summary>
public static class TokenErrorReader
{
    public static (string? Error, string? Description) Read(ProtocolResponse response)
    {
        if (response.Json is { ValueKind: JsonValueKind.Object } json &&
            ReadString(json, "error") is { } error)
        {
            return (error, ReadString(json, "error_description"));
        }

        // No OAuth error body at all, so this was a transport failure and the description that
        // would have carried the detail does not exist. The exception behind it does — a DNS
        // failure, a refused TLS handshake — and it is the only account of what happened.
        return (response.Error, response.Exception?.Message);
    }

    private static string? ReadString(JsonElement json, string name) =>
        json.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
        && value.GetString() is { Length: > 0 } text
            ? text
            : null;
}
