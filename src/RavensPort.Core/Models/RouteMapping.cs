namespace RavensPort.Core.Models;

public sealed class RouteMapping
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string PathPrefix { get; set; }
    public Guid UpstreamId { get; set; }

    /// <summary>
    /// The filter profile requests through this route are checked with before they are forwarded
    /// (their query values and a JSON, form or text body). Null: not filtered.
    /// </summary>
    public Guid? FilterProfileId { get; set; }
    public bool StripPrefix { get; set; } = true;
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// The secret a caller must present to use this route, and nothing else. It does not open any
    /// other route and it does not open a funnel — see <see cref="ProxyKey"/> for why the proxy
    /// no longer has one key for everything.
    /// </summary>
    public ProxyKey Key { get; set; } = new();

    /// <summary>
    /// Every credential this route attaches, each with its own placement. Empty is legitimate
    /// and means "forward without attaching anything" — the route is then an ordinary reverse
    /// proxy hop. See <see cref="RouteCredential"/>.
    /// </summary>
    public List<RouteCredential> Credentials { get; set; } = [];

    /// <summary>Short description of everything this route attaches, for grids and logs.</summary>
    public string DescribeCredentials(Func<Guid, string?>? nameOf = null)
    {
        if (Credentials.Count == 0) return "no credential — forwarded unauthenticated";

        return string.Join(", ", Credentials.Select(c =>
        {
            var name = nameOf?.Invoke(c.CredentialId);
            var described = c.ToCredentialInjection().Describe();
            return name is null ? described : $"{name} as {described}";
        }));
    }
}
