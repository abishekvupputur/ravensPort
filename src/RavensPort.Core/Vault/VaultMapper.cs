using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RavensPort.Core.Models;
using RavensPort.Core.Storage;

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
/// An API bridge's manifest is split out too, though it holds no secret. It is the largest thing
/// a user writes here, and the note is rewritten in full on every save — including every token
/// refresh — so leaving it in the note meant re-encrypting and re-syncing every manifest each time
/// a token aged out. Its own item is also simply where someone would look for it.
///
/// Each field lives on exactly one side. A credential's scopes and endpoints are in the note and
/// nowhere else; its secret is in its item and nowhere else. There is no field with two homes, so
/// there is never a question of which copy wins.
/// </summary>
public static class VaultMapper
{
    /// <summary>
    /// Writes every bridge's manifest to its local file — called once per real save, alongside
    /// <see cref="BuildSecretItems"/> but from each provider's save method rather than from inside
    /// it. <see cref="BuildSecretItems"/> is also called by <see cref="VaultIntegrityService"/> to
    /// compute "what should exist" for its read-only report, and that call must never have this
    /// side effect — a status check that writes to disk is not a status check.
    ///
    /// Best-effort, the same way every other local-file store in this app is on the automatic path:
    /// the interactive save in the UI checks <see cref="Storage.ManifestLocalStore.Save"/> itself
    /// and reports a failure before persisting anything, but a background save — a token refresh, a
    /// settings toggle — must not fail the whole vault write over a manifest file that happens not
    /// to be writable right now.
    /// </summary>
    public static void PersistManifestsLocally(ConfigStore store)
    {
        foreach (var bridge in store.McpApiBridges)
        {
            ManifestLocalStore.Save(bridge.Id, bridge.Manifest);
        }
    }

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

        foreach (var bridge in store.McpApiBridges)
        {
            // The manifest itself is no longer written here — see ManifestLocalStore. A real vault
            // backend (Proton Pass, measured directly) rejects a note past a few tens of KB, well
            // under what a manifest is allowed to be, so the vault bought nothing here but a size
            // ceiling nobody could see coming. Disk is now the only copy; this bridge's proxy key
            // still belongs in the vault exactly as before.
            if (!bridge.Key.IsConfigured) continue;

            items.Add(new VaultSecretItem(VaultItemRole.ApiBridgeKey, bridge.Id, new VaultItemSpec(
                VaultItemNaming.ForApiBridgeKey(bridge.Id, bridge.Slug),
                VaultItemCategory.Password,
                [
                    new VaultItemField(VaultFields.Password, bridge.Key.Value),
                    new VaultItemField(VaultFields.RecordId, bridge.Id.ToString("D")),
                ])
            {
                ItemId = index.Find(VaultItemRole.ApiBridgeKey, bridge.Id),
                Caption = $"Proxy key for API bridge '{bridge.Name}' — {bridge.Key.DescribeExpiry(DateTimeOffset.UtcNow)}",
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

        if (!string.IsNullOrEmpty(credential.ExchangeApiKey))
        {
            fields.Add(new(VaultFields.ExchangeApiKey, credential.ExchangeApiKey));
        }

        if (!string.IsNullOrWhiteSpace(credential.ExchangeRequestBody))
        {
            fields.Add(new(VaultFields.ExchangeRequestBody, credential.ExchangeRequestBody));
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
                CredentialKind.TokenExchange => $"Token exchange secret for '{credential.Name}'",
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

        RestoreCredentials(store, document, secrets, report);
        RestoreKeys(store, secrets);
        RestoreApiBridges(store, secrets, report);

        return store;
    }

    /// <summary>
    /// Merges each credential's secret back, and drops the ones whose item has been deleted.
    /// </summary>
    private static void RestoreCredentials(
        ConfigStore store,
        VaultDocument document,
        IReadOnlyDictionary<(VaultItemRole Role, Guid Id), VaultItemContents> secrets,
        VaultLoadReport report)
    {
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
            DropCredential(store, credential, report);
        }
    }

    private static void DropCredential(ConfigStore store, CredentialRecord credential, VaultLoadReport report)
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

    /// <summary>
    /// Puts every per-endpoint key back. A key whose item is missing is left empty rather than
    /// treated as a ghost: ConfigStoreCache issues a fresh one on load, which is a working
    /// endpoint with a new secret rather than a deleted one.
    /// </summary>
    private static void RestoreKeys(
        ConfigStore store,
        IReadOnlyDictionary<(VaultItemRole Role, Guid Id), VaultItemContents> secrets)
    {
        foreach (var route in store.Routes)
        {
            ApplyKey(route.Key, VaultItemRole.RouteKey, route.Id, secrets);
        }

        foreach (var funnel in store.McpFunnels)
        {
            ApplyKey(funnel.Key, VaultItemRole.FunnelKey, funnel.Id, secrets);
        }

        foreach (var bridge in store.McpApiBridges)
        {
            ApplyKey(bridge.Key, VaultItemRole.ApiBridgeKey, bridge.Id, secrets);
        }
    }

    private static void ApplyKey(
        ProxyKey key,
        VaultItemRole role,
        Guid recordId,
        IReadOnlyDictionary<(VaultItemRole Role, Guid Id), VaultItemContents> secrets)
    {
        if (secrets.TryGetValue((role, recordId), out var item))
        {
            key.Value = item.Field(VaultFields.Password) ?? "";
        }
    }

    /// <summary>
    /// Loads each bridge's manifest from local disk — the canonical copy since manifests stopped
    /// being written to the vault (see the comment in <see cref="BuildSecretItems"/>).
    ///
    /// One migration path, for an install that still has a bridge whose manifest was never written
    /// locally: if the vault still holds the old item, read it once and save it to
    /// <see cref="ManifestLocalStore"/> so it survives from here on. The old vault item itself is
    /// left alone — it is no longer in what <see cref="BuildSecretItems"/> expects, so the next
    /// ordinary save sweeps it away the same way a deleted credential's item disappears.
    ///
    /// A bridge is never dropped for a missing manifest, migrated or not: unlike a credential's
    /// secret, the vault has not been the only copy of a manifest for a while now, and destroying a
    /// bridge — its proxy key, its funnel sources — over a file that is merely absent would be a far
    /// worse outcome than the bridge simply serving no tools until one is imported again.
    /// </summary>
    private static void RestoreApiBridges(
        ConfigStore store,
        IReadOnlyDictionary<(VaultItemRole Role, Guid Id), VaultItemContents> secrets,
        VaultLoadReport report)
    {
        var migrated = 0;

        foreach (var bridge in store.McpApiBridges)
        {
            if (ManifestLocalStore.TryLoad(bridge.Id) is { } local)
            {
                bridge.Manifest = local;
                continue;
            }

            if (secrets.TryGetValue((VaultItemRole.ApiBridgeManifest, bridge.Id), out var item))
            {
                bridge.Manifest = ParseManifest(item.Field(VaultFields.NoteContent));
                ManifestLocalStore.Save(bridge.Id, bridge.Manifest);
                migrated++;
                continue;
            }

            bridge.Manifest = new McpApiBridgeManifest();
        }

        if (migrated > 0)
        {
            report.Warnings.Add(
                $"{migrated} API bridge manifest(s) were moved from the vault to local storage "
                + "(%LocalAppData%\\RavensPort\\manifests\\bridges\\) — they no longer sync across machines.");
        }
    }

    /// <summary>
    /// Reads a manifest item back. Unparseable content yields an empty manifest rather than
    /// throwing: the item is free text a user can edit by hand, so broken JSON is a real case, and
    /// a bridge that serves nothing is recoverable where a load that dies is not.
    /// </summary>
    private static McpApiBridgeManifest ParseManifest(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new McpApiBridgeManifest();

        try
        {
            var manifest = JsonSerializer.Deserialize<McpApiBridgeManifest>(json, VaultRedaction.FullOptions)
                           ?? new McpApiBridgeManifest();

            // An Undefined schema would throw on the next save instead of here. See
            // McpApiBridgeSchema for why that is the worst place for it to surface.
            McpApiBridgeSchema.Normalize(manifest);

            return manifest;
        }
        catch (JsonException)
        {
            return new McpApiBridgeManifest();
        }
    }

    private static void ApplyCredentialSecrets(CredentialRecord credential, VaultItemContents item)
    {
        credential.ClientSecret = item.Field(VaultFields.Password) ?? "";
        credential.ApiKey = item.Field(VaultFields.ApiKey);
        credential.ServiceAccountJson = item.Field(VaultFields.ServiceAccountJson);
        credential.ExchangeApiKey = item.Field(VaultFields.ExchangeApiKey);
        credential.ExchangeRequestBody = item.Field(VaultFields.ExchangeRequestBody);

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
