using RavensPort.Core.Models;

namespace RavensPort.Core.Tests;

/// <summary>
/// An OAuth access token arrives from a provider and is structurally constrained; an API key is
/// whatever someone pasted. That difference is the whole reason this validation exists.
/// </summary>
public class CredentialValidationTests
{
    [Theory]
    [InlineData("sk-abcdef0123456789")]
    [InlineData("ghp_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("a")]
    public void ValidateApiKey_AcceptsOrdinaryKeys(string key) =>
        Assert.Null(CredentialValidation.ValidateApiKey(key));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateApiKey_RejectsBlank(string? key) =>
        Assert.NotNull(CredentialValidation.ValidateApiKey(key));

    [Theory]
    [InlineData("key\r\nX-Admin: 1")]
    [InlineData("key\n")]
    [InlineData("key\r")]
    [InlineData("key\twith-tab")]
    public void ValidateApiKey_RejectsControlCharacters(string key)
    {
        // A CR or LF in a value written into a header ends the header line and lets the rest be
        // read as further headers — request splitting, aimed at the upstream. A key pasted out
        // of a wrapped email picks those up without anyone noticing.
        var error = CredentialValidation.ValidateApiKey(key);

        Assert.NotNull(error);
        Assert.Contains("control characters", error);
    }

    [Fact]
    public void ValidateTestEndpoint_AcceptsBlankBecauseTheFieldIsOptional()
    {
        Assert.Null(CredentialValidation.ValidateTestEndpoint(null));
        Assert.Null(CredentialValidation.ValidateTestEndpoint(""));
    }

    [Theory]
    [InlineData("https://api.example.com/v1/me")]
    [InlineData("http://127.0.0.1:9000/ping")]
    [InlineData("http://localhost:9000/ping")]
    public void ValidateTestEndpoint_AcceptsHttpsAndLoopback(string url) =>
        Assert.Null(CredentialValidation.ValidateTestEndpoint(url));

    [Fact]
    public void ValidateTestEndpoint_RejectsPlainHttpOffLocalhost()
    {
        // The credential's secret is sent there, so plain http would put it on the wire in
        // cleartext — the same rule every other endpoint in the app is held to.
        var error = CredentialValidation.ValidateTestEndpoint("http://api.example.com/v1/me"); // DevSkim: ignore DS137138

        Assert.NotNull(error);
        Assert.Contains("cleartext", error);
    }

    [Fact]
    public void ValidateTestEndpoint_RejectsSomethingThatIsNotAUrl() =>
        Assert.NotNull(CredentialValidation.ValidateTestEndpoint("api.example.com/v1/me"));

    [Fact]
    public void Validate_AcceptsAWellFormedApiKeyCredential() =>
        Assert.Null(CredentialValidation.Validate(new CredentialRecord
        {
            Name = "shodan",
            Kind = CredentialKind.ApiKey,
            ApiKey = "secret-key",
            DefaultPlacement = CredentialPlacement.Header,
            DefaultParameterName = "X-Api-Key",
            DefaultValuePrefix = "",
            TestEndpoint = "https://api.example.com/account/profile",
        }));

    [Fact]
    public void Validate_RejectsAnApiKeyCredentialDefaultingToTheQueryString()
    {
        // The default placement is what the Test button sends and what prefills a route entry, so
        // a credential may not carry a placement the proxy would refuse. Otherwise the refusal
        // would only appear later, at the route, on a credential the tab called valid.
        var error = CredentialValidation.Validate(new CredentialRecord
        {
            Name = "shodan",
            Kind = CredentialKind.ApiKey,
            ApiKey = "secret-key",
            DefaultPlacement = CredentialPlacement.Query,
            DefaultParameterName = "key",
            DefaultValuePrefix = "",
        });

        Assert.NotNull(error);
        Assert.Contains("not permitted", error);
    }

    [Fact]
    public void Validate_AcceptsAnOAuthCredentialWithNoApiKey()
    {
        // The API-key check must not fire for a grant, which legitimately has none.
        Assert.Null(CredentialValidation.Validate(new CredentialRecord { Name = "gmail" }));
    }

    [Fact]
    public void Validate_RejectsAnApiKeyCredentialWithNoKey()
    {
        var error = CredentialValidation.Validate(new CredentialRecord
        {
            Name = "shodan",
            Kind = CredentialKind.ApiKey,
        });

        Assert.NotNull(error);
        Assert.Contains("API key is required", error);
    }

    [Fact]
    public void Validate_RejectsAPlacementThatCannotGoOnTheWire()
    {
        var error = CredentialValidation.Validate(new CredentialRecord
        {
            Name = "shodan",
            Kind = CredentialKind.ApiKey,
            ApiKey = "secret-key",
            DefaultParameterName = "X Api Key",
        });

        Assert.NotNull(error);
        Assert.Contains("letters, digits", error);
    }

    [Fact]
    public void Validate_RequiresAName() =>
        Assert.NotNull(CredentialValidation.Validate(new CredentialRecord { Name = "  " }));

    [Theory]
    [InlineData(CredentialKind.OAuth2, "Authorization", "Bearer ")]
    [InlineData(CredentialKind.ApiKey, "X-Api-Key", "")]
    public void DefaultInjectionFor_MatchesWhatEachKindOfApiActuallyWants(
        CredentialKind kind, string name, string prefix)
    {
        // Bearer is an OAuth convention; a key-based API almost always documents a bare value in
        // a bespoke header, so offering Bearer there would be wrong for nearly every one of them.
        var injection = CredentialRecord.DefaultInjectionFor(kind);

        Assert.Equal(CredentialPlacement.Header, injection.Placement);
        Assert.Equal(name, injection.Name);
        Assert.Equal(prefix, injection.ValuePrefix);
    }

    [Fact]
    public void HasSecret_ReportsTheRightThingForEachKind()
    {
        // An API key is never "connected" in the OAuth sense; it is stored or it is not.
        Assert.False(new CredentialRecord { Name = "a", Kind = CredentialKind.ApiKey }.HasSecret);
        Assert.True(new CredentialRecord { Name = "a", Kind = CredentialKind.ApiKey, ApiKey = "k" }.HasSecret);

        Assert.False(new CredentialRecord { Name = "a" }.HasSecret);
        Assert.True(new CredentialRecord
        {
            Name = "a",
            Token = new TokenSet("t", null, DateTimeOffset.UtcNow.AddHours(1), "Bearer", DateTimeOffset.UtcNow),
        }.HasSecret);
    }
}
