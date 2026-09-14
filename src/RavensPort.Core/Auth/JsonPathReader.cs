using System.Text.Json;

namespace RavensPort.Core.Auth;

/// <summary>
/// Reads one value out of a JSON document by a dot-separated path ("access_token", "data.token").
///
/// The only place this is needed: every other flow in this app gets a typed OAuth token response
/// with a fixed shape, but a token exchange endpoint is whatever API the user pointed it at, and
/// nothing here can assume where the token lives in the answer.
/// </summary>
internal static class JsonPathReader
{
    /// <summary>The value at <paramref name="path"/>, read as a string. False if not found or empty.</summary>
    public static bool TryGetString(JsonElement root, string? path, out string? value)
    {
        value = null;
        if (!TryNavigate(root, path, out var element)) return false;

        value = element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            // A token that happens to be numeric in the response is still a token — read rather
            // than refused, since the wire representation is the API's choice, not this app's.
            JsonValueKind.Number => element.GetRawText(),
            _ => null,
        };

        return value is { Length: > 0 };
    }

    /// <summary>The value at <paramref name="path"/>, read as a whole number of seconds.</summary>
    public static bool TryGetSeconds(JsonElement root, string? path, out int seconds)
    {
        seconds = 0;
        if (!TryNavigate(root, path, out var element)) return false;

        return element.ValueKind switch
        {
            JsonValueKind.Number => element.TryGetInt32(out seconds),
            JsonValueKind.String => int.TryParse(element.GetString(), out seconds),
            _ => false,
        };
    }

    private static bool TryNavigate(JsonElement root, string? path, out JsonElement result)
    {
        result = root;
        if (string.IsNullOrWhiteSpace(path)) return false;

        var current = root;
        foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
            {
                return false;
            }
        }

        result = current;
        return true;
    }
}
