using RavensPort.Core.Models;
using RavensPort.Core.Storage;
using RavensPort.Core.Vault;

namespace RavensPort.Core.Tests.Vault;

/// <summary>
/// A token exchange credential's two possible secrets — the static API key and the custom request
/// body — round-trip through the vault and never land in the topology note, exactly like every
/// other credential secret.
/// </summary>
public class TokenExchangeVaultRoundTripTests
{
    [Fact]
    public async Task TheApiKeyRoundTripsAndNeverReachesTheNote()
    {
        var store = new ConfigStore();
        var credential = new CredentialRecord
        {
            Name = "legacy-api",
            Kind = CredentialKind.TokenExchange,
            ExchangeMode = TokenExchangeMode.ApiKey,
            ExchangeEndpoint = "https://api.example.com/login",
            ExchangeApiKey = "sk-super-secret",
            ExchangeApiKeyHeaderName = "X-Api-Key",
            ExchangeApiKeyValuePrefix = "",
            ExchangeTokenPath = "data.token",
            ExchangeExpiresInPath = "data.ttl",
        };
        store.Credentials.Add(credential);

        var vault = InMemoryVault.Empty();
        await vault.SaveAsync(store);

        var note = vault.Items.Single(i => i.Title == VaultItemNaming.ConfigTitle);
        Assert.DoesNotContain("sk-super-secret", note.Field(VaultFields.NoteContent), StringComparison.Ordinal);

        var reloaded = await vault.LoadAsync();
        var loaded = Assert.Single(reloaded.Credentials);

        Assert.Equal(CredentialKind.TokenExchange, loaded.Kind);
        Assert.Equal(TokenExchangeMode.ApiKey, loaded.ExchangeMode);
        Assert.Equal("https://api.example.com/login", loaded.ExchangeEndpoint);
        Assert.Equal("sk-super-secret", loaded.ExchangeApiKey);
        Assert.Equal("X-Api-Key", loaded.ExchangeApiKeyHeaderName);
        Assert.Equal("data.token", loaded.ExchangeTokenPath);
        Assert.Equal("data.ttl", loaded.ExchangeExpiresInPath);
    }

    [Fact]
    public async Task TheCustomBodyRoundTripsAndNeverReachesTheNote()
    {
        var store = new ConfigStore();
        var credential = new CredentialRecord
        {
            Name = "legacy-login",
            Kind = CredentialKind.TokenExchange,
            ExchangeMode = TokenExchangeMode.CustomBody,
            ExchangeEndpoint = "https://api.example.com/login",
            ExchangeRequestBody = """{"username":"bob","password":"hunter2"}""",
            ExchangeTokenPath = "token",
        };
        store.Credentials.Add(credential);

        var vault = InMemoryVault.Empty();
        await vault.SaveAsync(store);

        var note = vault.Items.Single(i => i.Title == VaultItemNaming.ConfigTitle);
        Assert.DoesNotContain("hunter2", note.Field(VaultFields.NoteContent), StringComparison.Ordinal);

        var reloaded = await vault.LoadAsync();
        var loaded = Assert.Single(reloaded.Credentials);

        Assert.Equal(TokenExchangeMode.CustomBody, loaded.ExchangeMode);
        Assert.Equal("""{"username":"bob","password":"hunter2"}""", loaded.ExchangeRequestBody);
    }

    /// <summary>
    /// A credential of this kind with neither secret filled in yet — just created, nothing
    /// typed — gets no vault item at all, the same rule an unconnected OAuth credential follows.
    /// </summary>
    [Fact]
    public async Task AnUnconfiguredTokenExchangeCredentialGetsNoVaultItem()
    {
        var store = new ConfigStore();
        store.Credentials.Add(new CredentialRecord
        {
            Name = "not yet set up",
            Kind = CredentialKind.TokenExchange,
        });

        var vault = InMemoryVault.Empty();
        await vault.SaveAsync(store);

        Assert.DoesNotContain(vault.Items, i => i.Title.Contains("credential —", StringComparison.Ordinal));
    }
}
