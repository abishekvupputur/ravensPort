using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using RavensPort.Core.Models;

namespace RavensPort.Core.Vault;

/// <summary>
/// Serializes a <see cref="ConfigStore"/> with every secret removed, for the topology note.
///
/// Done by dropping properties from the real type's JSON contract rather than by copying into a
/// hand-written DTO. A DTO would be a second definition of every model that has to be updated in
/// lockstep — and the failure mode of forgetting is a field that silently stops being stored,
/// which nobody notices until a restart. Here, a new property is included automatically and only
/// the ones named below are held back.
/// </summary>
public static class VaultRedaction
{
    /// <summary>
    /// The note's contract: everything except the secrets, which live in their own items so the
    /// password manager can show and protect them properly.
    /// </summary>
    public static readonly JsonSerializerOptions NoteOptions = new()
    {
        WriteIndented = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver
        {
            Modifiers = { StripSecrets },
        },
    };

    /// <summary>The full contract, used for the in-memory round trip that reassembles a store.</summary>
    public static readonly JsonSerializerOptions FullOptions = new() { WriteIndented = true };

    private static void StripSecrets(JsonTypeInfo typeInfo)
    {
        if (typeInfo.Type == typeof(CredentialRecord))
        {
            Remove(typeInfo,
                nameof(CredentialRecord.ClientSecret),
                nameof(CredentialRecord.ApiKey),
                // The key file holds a private key. ServiceAccountSubject is deliberately not
                // here: it is an email address, and the note is where non-secret config lives.
                nameof(CredentialRecord.ServiceAccountJson),
                nameof(CredentialRecord.Token));
        }
        else if (typeInfo.Type == typeof(ProxyKey))
        {
            // Only the value. CreatedUtc and ExpiresUtc are policy, not secret, and keeping them
            // in the note means expiry survives even if the key item is lost and reissued.
            Remove(typeInfo, nameof(ProxyKey.Value));
        }
    }

    private static void Remove(JsonTypeInfo typeInfo, params string[] propertyNames)
    {
        foreach (var name in propertyNames)
        {
            var property = typeInfo.Properties.FirstOrDefault(p =>
                string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

            if (property is not null) typeInfo.Properties.Remove(property);
        }
    }
}
