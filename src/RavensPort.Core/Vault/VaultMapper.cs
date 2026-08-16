using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RavensPort.Core.Models;

namespace RavensPort.Core.Vault;

/// <summary>One secret-bearing item, tied back to the record it belongs to.</summary>
public sealed record VaultSecretItem(VaultItemRole Role, Guid RecordId, VaultItemSpec Spec)
{
    /// <summary>
    /// Digest of everything a save would write. Lets a provider skip items whose secret has not
    /// changed, which is what keeps a port change or a single token refresh to one CLI call
    /// instead of one per credential and key in the store.
    /// </summary>
    public string Fingerprint
    {
        get
        {
            // ASCII unit separator between every part, so two different field sets cannot
            // concatenate into the same string and collide.
            const char Separator = '\u001f';

            var builder = new StringBuilder(Spec.Title).Append(Separator).Append(Spec.Category);

            // Not named `field`: C# 14 makes that a keyword inside a property accessor, where it
            // binds to a synthesized backing field rather than the loop variable.
            foreach (var specField in Spec.Fields.OrderBy(f => f.Name, StringComparer.Ordinal))
            {
                builder.Append(Separator).Append(specField.Name).Append(Separator).Append(specField.Value);
            }

            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
        }
    }
}

/// <summary>
/// Translates between a <see cref="ConfigStore"/> and the items that represent it in a vault.
///
/// The split is deliberate. Secrets — client secrets, API keys, tokens, proxy keys — each get
/// their own item, so the password manager can conceal them, show them, and let the user copy one
/// out without reading JSON. Everything else goes in one note, because it is a graph (routes
/// reference upstreams reference credentials) and splitting a graph across items would turn every
/// save into a consistency problem.
///
/// Each field lives on exactly one side. A credential's scopes and endpoints are in the note and
/// nowhere else; its secret is in its item and nowhere else. There is no field with two homes, so
/// there is never a question of which copy wins.
/// </summary>
public static class VaultMapper
{
    /// <summary>
    /// The items that must exist for this store's secrets, in the order they should be written.
    /// Records with nothing secret to store are skipped — an OAuth credential that has never been
    /// connected has no item until it does.
    /// </summary>
    public static List<VaultSecretItem> BuildSecretItems(ConfigStore store, VaultIndex index)
    {
        var items = new List<VaultSecretItem>();

        foreach (var credential in store.Credentials)
        {
            if (BuildCredentialItem(credential, index) is { } item) items.Add(item);
        }

        foreach (var route in store.Routes)
        {
            if (!route.Key.IsConfigured) continue;

            items.Add(new VaultSecretItem(VaultItemRole.RouteKey, route.Id, new VaultItemSpec(
                VaultItemNaming.ForRouteKey(route.Id, route.PathPrefix),
                VaultItemCategory.Password,
                [
                    new VaultItemField(VaultFields.Password, route.Key.Value),
                    new VaultItemField(VaultFields.RecordId, route.Id.ToString("D")),
                ])
            {
                ItemId = index.Find(VaultItemRole.RouteKey, route.Id),
                Caption = $"Proxy key for {route.PathPrefix} — {route.Key.DescribeExpiry(DateTimeOffset.UtcNow)}",
            }));
        }

        foreach (var funnel in store.McpFunnels)
        {
            if (!funnel.Key.IsConfigured) continue;

            items.Add(new VaultSecretItem(VaultItemRole.FunnelKey, funnel.Id, new VaultItemSpec(
                VaultItemNaming.ForFunnelKey(funnel.Id, funnel.Slug),
                VaultItemCategory.Password,
                [
                    new VaultItemField(VaultFields.Password, funnel.Key.Value),
                    new VaultItemField(VaultFields.RecordId, funnel.Id.ToString("D")),
                ])
            {
                ItemId = index.Find(VaultItemRole.FunnelKey, funnel.Id),
                Caption = $"Proxy key for MCP funnel '{funnel.Name}' — {funnel.Key.DescribeExpiry(DateTimeOffset.UtcNow)}",
            }));
        }

        return items;
    }

    private static VaultSecretItem? BuildCredentialItem(CredentialRecord credential, VaultIndex index)
    {
        var fields = new List<VaultItemField>
        {
            new(VaultFields.RecordId, credential.Id.ToString("D")),
            new(VaultFields.Kind, credential.Kind.ToString()),
        };

        // The client id goes in the username slot and the secret in the password slot so the item
        // reads as a real login in the manager's UI, with the usual copy and conceal behaviour.
        if (!string.IsNullOrEmpty(credential.ClientId)) fields.Add(new(VaultFields.Username, credential.ClientId));
        if (!string.IsNullOrEmpty(credential.ClientSecret)) fields.Add(new(VaultFields.Password, credential.ClientSecret));
        if (!string.IsNullOrWhiteSpace(credential.Authority)) fields.Add(new(VaultFields.Website, credential.Authority));
        if (!string.IsNullOrEmpty(credential.ApiKey)) fields.Add(new(VaultFields.ApiKey, credential.ApiKey));

        if (!string.IsNullOrWhiteSpace(credential.ServiceAccountJson))
        {
            fields.Add(new(VaultFields.ServiceAccountJson, credential.ServiceAccountJson));
        }

        if (credential.Token is { } token)
        {
            fields.Add(new(VaultFields.AccessToken, token.AccessToken));
            fields.Add(new(VaultFields.TokenType, token.TokenType));
            fields.Add(new(VaultFields.ObtainedUtc, VaultItemNaming.FormatTimestamp(token.ObtainedUtc)));

            // Written only when there is one. A provider that advertises no lifetime leaves the
            // field absent, and an absent field reads back as "no expiry" — writing a placeholder
            // instead would turn a token that never expires into one that expired long ago.
            if (token.ExpiresAtUtc is { } expiresAt)
            {
                fields.Add(new(VaultFields.ExpiresAtUtc, VaultItemNaming.FormatTimestamp(expiresAt)));
            }

            if (token.RefreshToken is { Length: > 0 } refreshToken)
            {
                fields.Add(new(VaultFields.RefreshToken, refreshToken));
            }
        }

        // Nothing secret yet — an OAuth credential that has never been connected. Writing an item
        // holding only a record id would clutter the vault with entries that mean nothing to the
        // user; the note already knows the credential exists.
        var hasSecret = fields.Any(f => f.Concealed);
        if (!hasSecret) return null;

        return new VaultSecretItem(VaultItemRole.Credential, credential.Id, new VaultItemSpec(
            VaultItemNaming.ForCredential(credential.Id, credential.Name),
            VaultItemCategory.Login,
            fields)
        {
            ItemId = index.Find(VaultItemRole.Credential, credential.Id),
            Caption = credential.Kind switch
            {
                CredentialKind.ApiKey => $"API key for '{credential.Name}'",
                CredentialKind.GoogleServiceAccount => $"Google service account key for '{credential.Name}'",
                CredentialKind.ClientCredentials => $"OAuth client credentials for '{credential.Name}'",
                _ => $"OAuth credential for '{credential.Name}'",
            },
        });
    }

    /// <summary>
    /// The topology note. Built <em>after</em> the secret items are written, with the index they
    /// produced, so the note can never reference an item that does not exist — a crash mid-save
    /// leaves orphan items, which the next save sweeps, rather than a dangling pointer.
    /// </summary>
    public static VaultItemSpec BuildConfigNote(ConfigStore store, VaultIndex index, long revision)
    {
        var document = new VaultDocument
        {
            Revision = revision,
            WrittenBy = SafeMachineName(),
            WrittenUtc = DateTimeOffset.UtcNow,
            Store = store,
            Index = index,
        };

        return new VaultItemSpec(
            VaultItemNaming.ConfigTitle,
            VaultItemCategory.SecureNote,
            [new VaultItemField(VaultFields.NoteContent, document.Serialize())]);
    }

    /// <summary>
    /// Rebuilds the store: the note's redacted graph with each record's secret merged back from
    /// its item.
    ///
    /// A credential whose item the note points at, but which is no longer in the vault, is
    /// <em>removed</em> rather than loaded empty. The vault is the only copy, so an item deleted in
    /// the password manager's own UI is the user saying that credential is gone — keeping a record
    /// of it made the app behave as though the credential still existed, and every launch raised
    /// the same ghost because the note was never rewritten. <paramref name="report"/> carries what
    /// went, so the caller can say so and write the note back without it.
    ///
    /// The rule is deliberately "the note claims an item that is missing", not "this record has no
    /// secret". A public OAuth client with a client id and no secret has no item and never did, and
    /// a manager that returns masked values still returns the item — neither is a ghost, and
    /// dropping either would delete a credential the user still has.
    /// </summary>
    public static ConfigStore ComposeStore(
        VaultDocument document,
        IReadOnlyDictionary<(VaultItemRole Role, Guid Id), VaultItemContents> secrets,
        VaultLoadReport report)
    {
        // Round-trip through the full contract so the caller gets a store detached from the
        // document — mutating one must not silently edit the other.
        var store = JsonSerializer.Deserialize<ConfigStore>(
            JsonSerializer.Serialize(document.Store, VaultRedaction.FullOptions),
            VaultRedaction.FullOptions) ?? new ConfigStore();

        var abandoned = new List<CredentialRecord>();

        foreach (var credential in store.Credentials)
        {
            if (secrets.TryGetValue((VaultItemRole.Credential, credential.Id), out var item))
            {
                ApplyCredentialSecrets(credential, item);
                continue;
            }

            // The index is the evidence: it is written only after an item has actually been
            // created, so an entry pointing at nothing means that item was deleted.
            if (document.Index.Find(VaultItemRole.Credential, credential.Id) is not null)
            {
                abandoned.Add(credential);
            }
        }

        foreach (var credential in abandoned)
        {
            store.Credentials.Remove(credential);

            // A route left pointing at it would look configured and forward nothing, which is the
            // same silent failure one layer down.
            var strandedRoutes = store.Routes
                .Where(route => route.Credentials.RemoveAll(c => c.CredentialId == credential.Id) > 0)
                .Select(route => route.PathPrefix)
                .ToList();

            var affected = strandedRoutes.Count == 0
                ? ""
                : $" {strandedRoutes.Count} route(s) now forward unauthenticated: {string.Join(", ", strandedRoutes)}.";

            report.Removals.Add(
                $"Credential '{credential.Name}' was removed: the vault item holding its secret is gone.{affected}");
        }

        foreach (var route in store.Routes)
        {
            if (secrets.TryGetValue((VaultItemRole.RouteKey, route.Id), out var item))
            {
                route.Key.Value = item.Field(VaultFields.Password) ?? "";
            }
        }

        foreach (var funnel in store.McpFunnels)
        {
            if (secrets.TryGetValue((VaultItemRole.FunnelKey, funnel.Id), out var item))
            {
                funnel.Key.Value = item.Field(VaultFields.Password) ?? "";
            }
        }

        return store;
    }

    private static void ApplyCredentialSecrets(CredentialRecord credential, VaultItemContents item)
    {
        credential.ClientSecret = item.Field(VaultFields.Password) ?? "";
        credential.ApiKey = item.Field(VaultFields.ApiKey);
        credential.ServiceAccountJson = item.Field(VaultFields.ServiceAccountJson);

        var accessToken = item.Field(VaultFields.AccessToken);

        // TokenSet requires an access token, and the rest of the app reads a null Token as "not
        // connected" — so a half-written item must produce null rather than a token set with an
        // empty string in it, which would look connected and fail at the upstream.
        if (string.IsNullOrEmpty(accessToken))
        {
            credential.Token = null;
            return;
        }

        credential.Token = new TokenSet(
            accessToken,
            item.Field(VaultFields.RefreshToken),
            // No fallback: an absent expiry means the provider advertised none, and substituting
            // "now" would present a token that never expires as one that just did.
            VaultItemNaming.ParseTimestamp(item.Field(VaultFields.ExpiresAtUtc)),
            item.Field(VaultFields.TokenType) ?? "Bearer",
            VaultItemNaming.ParseTimestamp(item.Field(VaultFields.ObtainedUtc)) ?? DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// The machine name is written into the note so a concurrent-write conflict can name the other
    /// side. Guarded because it is the one piece of environment data here that can throw.
    /// </summary>
    private static string SafeMachineName()
    {
        try
        {
            return Environment.MachineName;
        }
        catch
        {
            return "unknown";
        }
    }
}
