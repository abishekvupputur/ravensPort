using System.Text.Json.Serialization;

namespace RavensPort.Core.Models;

/// <summary>
/// Which secret <see cref="CredentialKind.TokenExchange"/> trades for a token. Persisted by name,
/// like <see cref="CredentialKind"/>, so a stored value survives a member being added later.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TokenExchangeMode
{
    /// <summary>A static key, sent on a header of its own to the exchange endpoint.</summary>
    ApiKey,

    /// <summary>
    /// A JSON body the user authored by hand, posted to the exchange endpoint verbatim. Opaque to
    /// this app — a username/password pair is the common case, but nothing here inspects it.
    /// </summary>
    CustomBody,
}
