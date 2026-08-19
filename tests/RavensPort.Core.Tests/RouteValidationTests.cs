using RavensPort.Core.Models;

namespace RavensPort.Core.Tests;

/// <summary>
/// A path prefix is interpolated into an ASP.NET route template, so some ordinary-looking
/// characters have structural meaning. An unparseable template makes YARP reject the whole
/// config update and keep the previous one, while the activity log has already announced the
/// route as active - one bad character used to make every later route edit appear to apply
/// and do nothing.
/// </summary>
public class RouteValidationTests
{
    [Theory]
    [InlineData("/gmail")]
    [InlineData("/app/echo")]
    [InlineData("/a-b_c.d~e")]
    [InlineData("/files/a..b")]   // two dots inside a segment is not a traversal
    [InlineData("/gmail/")]
    public void ValidatePathPrefix_AcceptsOrdinaryPrefixes(string prefix) =>
        Assert.Null(RouteValidation.ValidatePathPrefix(prefix));

    [Theory]
    [InlineData("{")]
    [InlineData("}")]
    [InlineData("?")]
    [InlineData("#")]
    [InlineData("\\")]
    public void ValidatePathPrefix_RejectsRouteTemplateMetacharacters(string character)
    {
        var error = RouteValidation.ValidatePathPrefix($"/api{character}x");

        Assert.NotNull(error);
        Assert.Contains("special meaning", error);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("//")]
    public void ValidatePathPrefix_RejectsCatchAllPrefix(string prefix)
    {
        // "/{**catch-all}" swallows every request to the proxy and points all of it at one
        // upstream with one credential attached.
        var error = RouteValidation.ValidatePathPrefix(prefix);

        Assert.NotNull(error);
        Assert.Contains("every request", error);
    }

    [Theory]
    [InlineData("/api/../admin")]
    [InlineData("/../etc")]
    public void ValidatePathPrefix_RejectsDotSegments(string prefix) =>
        Assert.NotNull(RouteValidation.ValidatePathPrefix(prefix));

    [Fact]
    public void ValidatePathPrefix_RequiresALeadingSlash() =>
        Assert.NotNull(RouteValidation.ValidatePathPrefix("gmail"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidatePathPrefix_RejectsBlank(string? prefix) =>
        Assert.NotNull(RouteValidation.ValidatePathPrefix(prefix));

    [Fact]
    public void ValidatePathPrefix_RejectsSpacesAndControlCharacters()
    {
        Assert.NotNull(RouteValidation.ValidatePathPrefix("/two words"));
        Assert.NotNull(RouteValidation.ValidatePathPrefix("/tab\there"));
    }

    [Theory]
    [InlineData(CredentialPlacement.Header, "Authorization", "Bearer ")]
    [InlineData(CredentialPlacement.Header, "X-Api-Key", "")]
    [InlineData(CredentialPlacement.Header, "PRIVATE-TOKEN", "")]
    [InlineData(CredentialPlacement.Body, "access_token", "")]
    [InlineData(CredentialPlacement.Body, "auth.token", "Bearer ")]
    public void ValidateCredentialInjection_AcceptsOrdinarySettings(
        CredentialPlacement placement, string name, string prefix) =>
        Assert.Null(RouteValidation.ValidateCredentialInjection(placement, name, prefix));

    [Theory]
    [InlineData(CredentialPlacement.Header)]
    [InlineData(CredentialPlacement.Body)]
    public void ValidateCredentialInjection_RequiresAName(CredentialPlacement placement)
    {
        Assert.NotNull(RouteValidation.ValidateCredentialInjection(placement, "", ""));
        Assert.NotNull(RouteValidation.ValidateCredentialInjection(placement, "   ", ""));
    }

    [Theory]
    [InlineData("X Api Key")]      // space is not a token character
    [InlineData("X-Api-Key:")]
    [InlineData("Auth\r\nX-Evil")]
    public void ValidateCredentialInjection_RejectsHeaderNamesThatAreNotHttpTokens(string name) =>
        Assert.NotNull(RouteValidation.ValidateCredentialInjection(CredentialPlacement.Header, name, ""));

    [Theory]
    [InlineData("Host")]
    [InlineData("content-length")]
    [InlineData("Transfer-Encoding")]
    public void ValidateCredentialInjection_RejectsHeadersTheProxyOwns(string name)
    {
        // Writing these would not attach a credential, it would break the forward — a rewritten
        // Host lands on the wrong virtual host, a stale length desynchronizes the framing.
        var error = RouteValidation.ValidateCredentialInjection(CredentialPlacement.Header, name, "");

        Assert.NotNull(error);
        Assert.Contains("cannot carry a credential", error);
    }

    [Theory]
    [InlineData(CredentialPlacement.Header)]
    [InlineData(CredentialPlacement.Body)]
    public void ValidateCredentialInjection_RejectsNewlinesInThePrefix(CredentialPlacement placement)
    {
        // A CR or LF ends the header line and lets the rest be read as further headers — request
        // splitting, aimed at the upstream.
        var error = RouteValidation.ValidateCredentialInjection(placement, "Authorization", "Bearer \r\nX-Admin: 1");

        Assert.NotNull(error);
        Assert.Contains("control characters", error);
    }

    [Theory]
    [InlineData("access_token")]
    [InlineData("api-key")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("two words")]
    [InlineData("proxy_key")]
    public void ValidateCredentialInjection_RejectsEveryQueryPlacement(string name)
    {
        // No parameter name rescues this placement, so the answer must not depend on the name -
        // including for the empty one, where a "name is required" reply would send the user off
        // to fill in a field that cannot help. Reachable because a store written by an older
        // build can still hold a query entry; the picker no longer offers one.
        var error = RouteValidation.ValidateCredentialInjection(CredentialPlacement.Query, name, "");

        Assert.NotNull(error);
        Assert.Contains("not permitted", error);
    }

    [Fact]
    public void ValidateCredentialInjection_RejectsAQueryPlacementBeforeCheckingThePrefix()
    {
        // Same reasoning one field over: a control character in the prefix is a real problem, but
        // reporting it here would imply that fixing it makes the entry usable.
        var error = RouteValidation.ValidateCredentialInjection(
            CredentialPlacement.Query, "access_token", "Bearer \r\n");

        Assert.NotNull(error);
        Assert.Contains("not permitted", error);
    }

    // ---- credential lists --------------------------------------------------------------------

    private static RouteCredential Entry(
        CredentialPlacement placement, string name, string prefix = "", Guid? credentialId = null) =>
        new()
        {
            CredentialId = credentialId ?? Guid.NewGuid(),
            Placement = placement,
            ParameterName = name,
            ValuePrefix = prefix,
        };

    [Fact]
    public void ValidateCredentials_AcceptsAnEmptyList()
    {
        // A route that attaches nothing is a plain forwarding hop, not a half-finished route.
        Assert.Null(RouteValidation.ValidateCredentials([]));
    }

    [Fact]
    public void ValidateCredentials_AcceptsSeveralHeaders()
    {
        Assert.Null(RouteValidation.ValidateCredentials([
            Entry(CredentialPlacement.Header, "Authorization", "Bearer "),
            Entry(CredentialPlacement.Header, "X-Api-Key"),
            Entry(CredentialPlacement.Header, "PRIVATE-TOKEN"),
        ]));
    }

    [Fact]
    public void ValidateCredentials_AcceptsHeaderAndBodyTogether()
    {
        Assert.Null(RouteValidation.ValidateCredentials([
            Entry(CredentialPlacement.Header, "Authorization", "Bearer "),
            Entry(CredentialPlacement.Body, "auth_token"),
            Entry(CredentialPlacement.Body, "project_key"),
        ]));
    }

    [Fact]
    public void ValidateCredentials_RejectsTheWholeSetForOneQueryEntry()
    {
        // How an inherited route reaches the editor: three usable entries and one written by a
        // build that still allowed query placements. Saving it as-is has to be refused, or the
        // withdrawal would hold only for newly created routes.
        var error = RouteValidation.ValidateCredentials([
            Entry(CredentialPlacement.Header, "Authorization", "Bearer "),
            Entry(CredentialPlacement.Query, "access_token"),
            Entry(CredentialPlacement.Body, "auth_token"),
        ]);

        Assert.NotNull(error);
        Assert.Contains("not permitted", error);
    }

    [Fact]
    public void ValidateCredentials_AcceptsTheSameCredentialInTwoDifferentPlaces()
    {
        // A real pattern: an API that wants the token in a header for auth and echoed in the
        // body for its own audit trail.
        var id = Guid.NewGuid();

        Assert.Null(RouteValidation.ValidateCredentials([
            Entry(CredentialPlacement.Header, "Authorization", "Bearer ", id),
            Entry(CredentialPlacement.Body, "access_token", credentialId: id),
        ]));
    }

    [Theory]
    [InlineData(CredentialPlacement.Header, "Authorization")]
    [InlineData(CredentialPlacement.Body, "token")]
    public void ValidateCredentials_RejectsTwoCredentialsInTheSameSlot(CredentialPlacement placement, string name)
    {
        // The second silently overwrites the first, so the upstream sees one token while the UI
        // shows two — there is no reading of that configuration that does what it says.
        var error = RouteValidation.ValidateCredentials([Entry(placement, name), Entry(placement, name)]);

        Assert.NotNull(error);
        Assert.Contains("only one credential", error);
    }

    [Fact]
    public void ValidateCredentials_TreatsHeaderNamesAsCaseInsensitive()
    {
        // HTTP does. An upstream cannot receive both "Authorization" and "authorization".
        Assert.NotNull(RouteValidation.ValidateCredentials([
            Entry(CredentialPlacement.Header, "Authorization", "Bearer "),
            Entry(CredentialPlacement.Header, "authorization", "Bearer "),
        ]));
    }

    [Fact]
    public void ValidateCredentials_TreatsBodyFieldNamesAsCaseSensitive()
    {
        // Plenty of APIs distinguish "Token" from "token"; refusing the pair would block a
        // legitimate configuration.
        Assert.Null(RouteValidation.ValidateCredentials([
            Entry(CredentialPlacement.Body, "Token"),
            Entry(CredentialPlacement.Body, "token"),
        ]));
    }

    [Fact]
    public void ValidateCredentials_IgnoresSurroundingWhitespaceWhenComparingSlots()
    {
        // Names are trimmed before they go on the wire, so " Authorization" and "Authorization"
        // are the same header — comparing them raw would let a collision through.
        Assert.NotNull(RouteValidation.ValidateCredentials([
            Entry(CredentialPlacement.Header, "Authorization", "Bearer "),
            Entry(CredentialPlacement.Header, " Authorization ", "Bearer "),
        ]));
    }

    [Fact]
    public void ValidateCredentials_RejectsAnEntryWithNoCredentialChosen()
    {
        var error = RouteValidation.ValidateCredentials([
            new RouteCredential { CredentialId = Guid.Empty, ParameterName = "Authorization" },
        ]);

        Assert.NotNull(error);
        Assert.Contains("Pick a credential", error);
    }

    [Fact]
    public void ValidateCredentials_RejectsAListWhereAnyEntryIsUnusable()
    {
        // One bad entry is enough: the route as a whole cannot be put on the wire as configured.
        var error = RouteValidation.ValidateCredentials([
            Entry(CredentialPlacement.Header, "Authorization", "Bearer "),
            Entry(CredentialPlacement.Header, "X-Evil", "value\r\nX-Admin: 1"),
        ]);

        Assert.NotNull(error);
        Assert.Contains("control characters", error);
    }
}
