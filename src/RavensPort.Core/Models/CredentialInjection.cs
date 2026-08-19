using System.Text.Json.Serialization;

namespace RavensPort.Core.Models;

/// <summary>Where a route puts the credential on the outgoing request.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CredentialPlacement
{
    /// <summary>A request header — "Authorization: Bearer &lt;token&gt;" by default.</summary>
    Header,

    /// <summary>
    /// A query-string parameter, e.g. "?access_token=&lt;token&gt;". <b>No longer permitted.</b>
    ///
    /// Query strings are recorded by everything they pass through — the upstream's own access
    /// log, any intermediary, browser history, and the Referer header of anything the response
    /// loads — so a secret placed here is written to disk in several places outside this
    /// machine's control, none of which RavensPort can redact. Header and body placements are
    /// not logged that way.
    ///
    /// The member remains so that a store written by an older build still deserializes; it is
    /// refused by <see cref="RouteValidation.ValidateCredentialInjection"/> and never attached to
    /// a request. See <see cref="IsPermitted"/>.
    /// </summary>
    Query,

    /// <summary>A field in the request body (JSON object or urlencoded form).</summary>
    Body,
}

/// <summary>Placements a route may actually use, in the order the UI offers them.</summary>
public static class CredentialPlacements
{
    /// <summary>
    /// Every placement except <see cref="CredentialPlacement.Query"/>, which is retained in the
    /// enum only so old stores parse. Bind pickers to this, never to Enum.GetValues.
    /// </summary>
    public static IReadOnlyList<CredentialPlacement> Permitted { get; } =
        [CredentialPlacement.Header, CredentialPlacement.Body];

    /// <summary>Whether a credential may be sent this way at all.</summary>
    public static bool IsPermitted(CredentialPlacement placement) =>
        placement != CredentialPlacement.Query;
}

/// <summary>
/// The resolved "how do I attach the token" decision for one route: placement, the header /
/// parameter / field name, and a prefix stuck in front of the token value.
///
/// Bearer-in-a-header is the default and covers nearly every OAuth upstream. The body shape
/// exists because plenty of real APIs never adopted RFC 6750: some want a bespoke header
/// ("X-Api-Key", "PRIVATE-TOKEN"), and some want the token as a field in a JSON or form body.
/// An upstream that accepts the token only as "?access_token=" cannot be used through RavensPort
/// at all — see <see cref="CredentialPlacement.Query"/>.
/// </summary>
/// <param name="Placement">Header, query, or body.</param>
/// <param name="Name">Header name, query parameter name, or body field name.</param>
/// <param name="ValuePrefix">Text placed immediately before the token, e.g. "Bearer ".</param>
public sealed record CredentialInjection(CredentialPlacement Placement, string Name, string ValuePrefix)
{
    /// <summary>What every route gets unless the user says otherwise.</summary>
    public static CredentialInjection BearerHeader { get; } = new(CredentialPlacement.Header, "Authorization", "Bearer ");

    /// <summary>
    /// The name/prefix pair a placement starts with. The UI offers these when the user switches
    /// placement, so picking "Body" does something sensible without further typing.
    /// </summary>
    public static CredentialInjection DefaultFor(CredentialPlacement placement) => placement switch
    {
        CredentialPlacement.Body => new CredentialInjection(placement, "access_token", ""),
        _ => BearerHeader,
    };

    /// <summary>Whether this entry may be attached to a request. See <see cref="CredentialPlacements"/>.</summary>
    public bool IsPermitted => CredentialPlacements.IsPermitted(Placement);

    /// <summary>The value actually sent: prefix + token.</summary>
    public string FormatValue(string token) => ValuePrefix + token;

    /// <summary>Short one-line description for grids and tooltips.</summary>
    public string Describe() => Placement switch
    {
        CredentialPlacement.Query => $"query ?{Name}= (not permitted)",
        CredentialPlacement.Body => $"body field \"{Name}\": \"{ValuePrefix}<token>\"",
        _ => $"header {Name}: {ValuePrefix}<token>",
    };
}
