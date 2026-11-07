namespace RavensPort.Core.Models;

/// <summary>
/// A named set of filter steps — Secrets, Personal data, Entities, Meaning — that funnels and the
/// upstreams inside them are pointed at. One profile can serve many funnels; editing it changes
/// them all on their next call.
/// </summary>
public sealed class FilterProfileRecord
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public required string Name { get; set; }

    public List<FunnelFilter> Filters { get; set; } = [];
}

/// <summary>Which filters a call is checked with, and why.</summary>
/// <param name="Filters">The steps to run; empty for none.</param>
/// <param name="Source">What chose them, for the activity log: a profile's name, or why there is none.</param>
public sealed record FilterChoice(IReadOnlyList<FunnelFilter> Filters, string Source);

/// <summary>
/// The rule for which filters a funnel call runs: the profile set on the upstream it goes to, in
/// that funnel, if there is one; otherwise the funnel's profile; otherwise the funnel's own filters
/// from before profiles existed; otherwise none.
///
/// A profile id that names no profile — deleted underneath, say — is not skipped to the next rule:
/// that would quietly hand a call to a less strict profile than the one chosen. It is reported as
/// missing, and the caller blocks.
/// </summary>
public static class FilterProfiles
{
    public static FilterChoice Choose(ConfigStore store, McpFunnelRecord funnel, McpFunnelSource? link)
    {
        if (link?.FilterProfileId is { } upstreamId) return Profile(store, upstreamId, "upstream");
        if (funnel.FilterProfileId is { } funnelId) return Profile(store, funnelId, "funnel");
        if (funnel.Filters.Count > 0) return new FilterChoice(funnel.Filters, "the funnel's own filters");
        return new FilterChoice([], "no filters");
    }

    private static FilterChoice Profile(ConfigStore store, Guid id, string level) =>
        store.FilterProfiles.FirstOrDefault(p => p.Id == id) is { } profile
            ? new FilterChoice(profile.Filters, $"{level} profile '{profile.Name}'")
            : throw new FilterProfileMissingException($"the {level} filter profile no longer exists");
}

public sealed class FilterProfileMissingException(string message) : Exception(message);
