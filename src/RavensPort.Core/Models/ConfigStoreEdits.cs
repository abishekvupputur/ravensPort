namespace RavensPort.Core.Models;

/// <summary>
/// The rules for editing a <see cref="ConfigStore"/> that are not validation — slugs and aliases
/// derived from names, and what else has to go when a record is deleted.
///
/// Shared between the tabs and the headless admin API, which edit the same store and have to leave
/// it in the same shape. A cascade that lived only in a view model would be skipped by every other
/// caller, and the result — a funnel membership pointing at nothing, a source naming a bridge that
/// is gone — serves, but cannot be shown or removed afterwards.
/// </summary>
public static class ConfigStoreEdits
{
    /// <summary>Turns a display name into something usable as a path segment.</summary>
    public static string Slugify(string name)
    {
        var slug = new string([.. name.Trim().ToLowerInvariant()
            .Select(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) ? c : '-')]);

        while (slug.Contains("--", StringComparison.Ordinal))
        {
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        }

        return slug.Trim('-');
    }

    /// <summary>
    /// The alias a source gets when none was given: its name, lower-cased, keeping only what a tool
    /// name may contain. It ends up in every tool name the agent sees.
    /// </summary>
    public static string DefaultAlias(string sourceName) =>
        new([.. sourceName.Trim().ToLowerInvariant().Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')]);

    /// <summary>
    /// Why this prefix cannot be added, or null. Two routes with the same prefix produce two
    /// endpoints with identical match patterns, which load without complaint and then throw
    /// AmbiguousMatchException on every request — so the whole prefix 500s.
    /// </summary>
    public static string? ValidateUniquePrefix(ConfigStore store, string prefix, Guid? editingId = null)
    {
        var normalized = prefix.TrimEnd('/');

        return store.Routes.Any(r =>
            r.Id != editingId &&
            string.Equals(r.PathPrefix.TrimEnd('/'), normalized, StringComparison.OrdinalIgnoreCase))
            ? $"A route for '{prefix}' already exists. Path prefixes must be unique — duplicates make "
              + "every request to that prefix fail with an ambiguous-match error."
            : null;
    }

    /// <summary>
    /// Deletes a source and its membership in every funnel. Returns how many funnels it was in.
    /// </summary>
    public static int RemoveSource(ConfigStore store, Guid sourceId)
    {
        var affected = store.McpFunnels.Count(f => f.Sources.Any(s => s.SourceId == sourceId));

        store.McpSources.RemoveAll(s => s.Id == sourceId);

        // Otherwise the funnel keeps a membership row pointing at nothing, which serves fine but
        // leaves the UI unable to show or remove it.
        foreach (var funnel in store.McpFunnels)
        {
            funnel.Sources.RemoveAll(s => s.SourceId == sourceId);
        }

        return affected;
    }

    /// <summary>
    /// Deletes an API bridge and every funnel source that exposed it. Returns the ids of the
    /// sources removed, so the caller can drop their pooled sessions.
    /// </summary>
    public static IReadOnlyList<Guid> RemoveBridge(ConfigStore store, Guid bridgeId)
    {
        store.McpApiBridges.RemoveAll(b => b.Id == bridgeId);

        // A source left pointing at it would name a bridge that no longer exists and fail on every
        // connect, with nothing in the funnel tab able to explain why.
        var stranded = store.McpSources
            .Where(s => s.Kind == McpSourceKind.ApiBridge && s.BridgeId == bridgeId)
            .Select(s => s.Id)
            .ToList();

        foreach (var sourceId in stranded) RemoveSource(store, sourceId);

        return stranded;
    }
}
