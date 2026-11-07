using System.Collections.Concurrent;
using System.Text.Json;
using RavensPort.Core.Models;
using RavensPort.Core.Storage;

namespace RavensPort.Core.Filters;

/// <summary>How close one call came to a funnel's deny list.</summary>
/// <param name="Closeness">0 = no closer than everyday text is, 1 = identical to a deny entry.</param>
/// <param name="Closest">The deny entry it was closest to.</param>
/// <param name="FieldPath">The argument whose text came closest.</param>
/// <param name="AllowedBy">When every text in the call was outranked by an allow example: the first such example.</param>
public sealed record Judgement(double Closeness, double Threshold, string Closest, string FieldPath, string? AllowedBy)
{
    public bool Denied => AllowedBy is null && Closeness >= Threshold;
}

/// <summary>What the four steps made of one call.</summary>
/// <param name="Hits">Every match, Block ones first.</param>
/// <param name="StoppedAt">The step whose Block ended the check, or null if the call may go on.</param>
/// <param name="TextsForMeaning">The texts step 4 read — or would read — with earlier matches replaced.</param>
/// <param name="Judgement">Step 4's score, when it ran.</param>
public sealed record FilterOutcome(
    IReadOnlyList<FilterHit> Hits,
    FilterCategory? StoppedAt,
    IReadOnlyList<(string Path, string Text)> TextsForMeaning,
    Judgement? Judgement);

/// <summary>
/// Runs a funnel's four filter steps on one call, in order:
///
///   1 Secrets        the gitleaks rule set — Off or Deny
///   2 RegEx          patterns: editable built-ins (with checksums) and the user's own
///   3 Entities       a GLiNER model looking for labels the user chose
///   4 Meaning        an embedding model judging the call against a deny list
///
/// A Block in any step ends the check there: later steps never run, and an embedding API never sees
/// a call that step 1–3 already refused. What a step matched — Block or Watch — is replaced with
/// [its name] before the next step reads the text, so step 4 never receives a secret or personal
/// data an earlier step found, even when that step only watches.
///
/// Fails closed: a model that cannot load or answer, or a pattern that throws, counts as a match.
/// </summary>
public sealed class FilterService(ConfigStoreCache store, EmbeddingClientFactory clients)
{
    private const int MinTextChars = 8;
    private const int MaxTexts = 32;
    private const int MaxTextChars = 1000;

    /// <summary>Text nobody would deny. How close it comes to a deny entry is that entry's zero point.</summary>
    internal static readonly string[] EverydayText =
    [
        "README.md",
        "src/components/Button.tsx",
        "weather in Munich tomorrow",
        "list open issues assigned to me",
        "https://example.com/docs/getting-started",
        "fix the failing unit test in the parser",
        "summarise this pull request",
        "departures from Marienplatz",
        "translate good morning to German",
        "meeting notes for the sprint planning on Monday",
        "SELECT count(*) FROM orders WHERE created_at > now() - interval '1 day'",
        "npm run build",
    ];

    /// <summary>Meaning: closeness presets. Entities: model-score presets.</summary>
    public static double PresetThreshold(FilterLevel level, FilterCategory category = FilterCategory.RiskyActions) => (category, level) switch
    {
        (FilterCategory.Entities, FilterLevel.Relaxed) => 0.85,
        (FilterCategory.Entities, FilterLevel.Balanced) => 0.65,
        (FilterCategory.Entities, FilterLevel.Strict) => 0.45,
        (_, FilterLevel.Relaxed) => 0.55,
        (_, FilterLevel.Balanced) => 0.40,
        (_, FilterLevel.Strict) => 0.25,
        _ => double.PositiveInfinity,
    };

    private readonly ConcurrentDictionary<(string Signature, EmbeddingRole Role, string Text), float[]> _cache = new();

    /// <summary>Every hit on this call, Block ones first. What the funnel acts on.</summary>
    public async Task<IReadOnlyList<FilterHit>> EvaluateAsync(
        IReadOnlyList<FunnelFilter> filters,
        string toolName,
        IEnumerable<KeyValuePair<string, JsonElement>>? arguments,
        CancellationToken ct) =>
        (await RunAsync(filters, toolName, arguments, ct).ConfigureAwait(false)).Hits;

    /// <summary>All four steps, with what each saw. Try it shows this whole.</summary>
    public async Task<FilterOutcome> RunAsync(
        IReadOnlyList<FunnelFilter> filters,
        string toolName,
        IEnumerable<KeyValuePair<string, JsonElement>>? arguments,
        CancellationToken ct)
    {
        var texts = FilterEngine.Read(arguments);
        var spans = texts.Select(_ => new List<Span>()).ToArray();
        var hits = new List<FilterHit>();
        Judgement? judgement = null;

        FilterOutcome Outcome(FilterCategory? stoppedAt) => new(
            [.. hits.OrderBy(h => h.Action == FilterAction.Block ? 0 : 1)],
            stoppedAt,
            TextsToJudge(toolName, texts, spans),
            judgement);

        FunnelFilter? On(FilterCategory category) =>
            filters.FirstOrDefault(f => f.Category == category && f.Level != FilterLevel.Off);

        // ---- 1 Secrets: always Block when on ----------------------------------------------
        if (On(FilterCategory.Secrets) is { } secrets)
        {
            var disabled = secrets.DisabledDetectors.ToHashSet(StringComparer.Ordinal);
            for (var i = 0; i < texts.Count; i++)
            {
                List<Span> found;
                try
                {
                    found = [.. FilterEngine.FindSecrets(texts[i], disabled)];
                }
                catch (Exception)
                {
                    found = [new Span(0, texts[i].Text.Length, "scanner error")];
                }

                spans[i].AddRange(found);
                foreach (var name in found.Select(s => s.Name).Distinct())
                    AddOnce(hits, new FilterHit(FilterCategory.Secrets, FilterAction.Block, name, texts[i].Path));
            }

            if (hits.Count > 0) return Outcome(FilterCategory.Secrets);
        }

        // ---- 2 RegEx: patterns ----------------------------------------------------------------
        if (On(FilterCategory.PersonalData) is { } personal)
        {
            var patterns = PersonalPatterns.Active(personal);
            for (var i = 0; i < texts.Count; i++)
            {
                var found = FilterEngine.FindPersonal(patterns, texts[i].Text).ToList();
                spans[i].AddRange(found);
                foreach (var name in found.Select(s => s.Name).Distinct())
                    AddOnce(hits, new FilterHit(FilterCategory.PersonalData, personal.Action, name, texts[i].Path));
            }

            if (Blocked(hits, FilterCategory.PersonalData)) return Outcome(FilterCategory.PersonalData);
        }

        // ---- 3 Entities: a GLiNER model, on text with steps 1–2's matches replaced ---------
        if (On(FilterCategory.Entities) is { } entities && entities.EntityLabels.Count > 0)
        {
            try
            {
                var record = store.Current.EntityModels.FirstOrDefault(m => m.Id == entities.EntityModelId)
                             ?? throw new InvalidOperationException("no entity model is chosen");
                var find = clients.GetEntityModel(record);
                var threshold = entities.Threshold ?? PresetThreshold(entities.Level, FilterCategory.Entities);

                for (var i = 0; i < texts.Count; i++)
                {
                    if (!texts[i].IsValue || texts[i].Text.Trim().Length < 3) continue;

                    // Read the redacted text, then map what it finds back onto the original —
                    // redaction never makes text longer than [name], so offsets are re-found by search.
                    var redacted = FilterEngine.Redact(texts[i].Text, spans[i]);
                    foreach (var entity in await find(redacted, entities.EntityLabels, threshold, ct).ConfigureAwait(false))
                    {
                        var value = redacted.Substring(entity.Start, entity.Length);
                        var at = texts[i].Text.IndexOf(value, StringComparison.Ordinal);
                        if (at >= 0) spans[i].Add(new Span(at, value.Length, entity.Label));

                        AddOnce(hits, new FilterHit(FilterCategory.Entities, entities.Action, $"{entity.Label} ({entity.Score:0.00})", texts[i].Path));
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                hits.Add(new FilterHit(FilterCategory.Entities, entities.Action, $"entity model unavailable — {ex.Message}", "call"));
            }

            if (Blocked(hits, FilterCategory.Entities)) return Outcome(FilterCategory.Entities);
        }

        // ---- 4 Meaning: an embedding model, on text with everything above replaced --------
        if (On(FilterCategory.RiskyActions) is { } meaning && DenyAnchors(meaning).Count > 0)
        {
            try
            {
                judgement = await JudgeAsync(meaning, TextsToJudge(toolName, texts, spans), ct).ConfigureAwait(false);
                if (judgement.Denied)
                {
                    hits.Add(new FilterHit(FilterCategory.RiskyActions, meaning.Action,
                        $"close to \"{Shorten(judgement.Closest)}\" ({judgement.Closeness:0.00})", judgement.FieldPath));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                hits.Add(new FilterHit(FilterCategory.RiskyActions, meaning.Action, $"embedding model unavailable — {ex.Message}", "call"));
            }

            if (Blocked(hits, FilterCategory.RiskyActions)) return Outcome(FilterCategory.RiskyActions);
        }

        return Outcome(null);
    }

    private static bool Blocked(List<FilterHit> hits, FilterCategory step) =>
        hits.Any(h => h.Category == step && h.Action == FilterAction.Block);

    /// <summary>One hit per detector and step: the second email in the same call changes nothing.</summary>
    private static void AddOnce(List<FilterHit> hits, FilterHit hit)
    {
        if (!hits.Any(h => h.Category == hit.Category && h.Detector == hit.Detector)) hits.Add(hit);
    }

    /// <summary>
    /// What step 4 reads: each string value long enough to mean something, by its path, with earlier
    /// steps' matches replaced. A call with none (only ids, flags and numbers) is read whole, so no
    /// call goes unjudged.
    /// </summary>
    internal static List<(string Path, string Text)> TextsToJudge(string toolName, IReadOnlyList<CallText> texts, IReadOnlyList<List<Span>>? spans = null)
    {
        var judged = texts
            .Select((t, i) => (t, Redacted: spans is null ? t.Text : FilterEngine.Redact(t.Text, spans[i])))
            .Where(x => x.t.IsValue && x.t.Text.Trim().Length >= MinTextChars)
            .Select(x => (x.t.Path, Text: x.Redacted.Length <= MaxTextChars ? x.Redacted : x.Redacted[..MaxTextChars]))
            .DistinctBy(x => x.Text)
            .Take(MaxTexts)
            .ToList();

        if (judged.Count == 0)
        {
            var call = toolName + " " + string.Join(" ", texts.Select((t, i) => spans is null ? t.Text : FilterEngine.Redact(t.Text, spans[i])));
            judged.Add(("call", call.Length <= MaxTextChars ? call : call[..MaxTextChars]));
        }

        return judged;
    }

    /// <summary>Step 4 on texts already read (and redacted). Used by the funnel through <see cref="RunAsync"/>, and by tests.</summary>
    public async Task<Judgement> JudgeAsync(FunnelFilter filter, IReadOnlyList<(string Path, string Text)> texts, CancellationToken ct)
    {
        var provider = store.Current.EmbeddingProviders.FirstOrDefault(p => p.Id == filter.EmbeddingProviderId)
                       ?? throw new InvalidOperationException("no embedding model is chosen for Meaning");

        var deny = DenyAnchors(filter);
        if (deny.Count == 0) throw new InvalidOperationException("there is nothing to deny yet");

        var client = clients.Get(provider);
        var signature = $"{provider.Kind}|{provider.BaseUrl}|{provider.Model}|{provider.ModelFolder}";

        var textVectors = await client.EmbedAsync([.. texts.Select(t => t.Text)], EmbeddingRole.Query, ct).ConfigureAwait(false);
        var denyVectors = await EmbedCachedAsync(client, signature, deny, EmbeddingRole.Document, ct).ConfigureAwait(false);
        var allowVectors = await EmbedCachedAsync(client, signature, filter.AllowExamples, EmbeddingRole.Document, ct).ConfigureAwait(false);
        var everyday = await EmbedCachedAsync(client, signature, EverydayText, EmbeddingRole.Query, ct).ConfigureAwait(false);

        // Each deny entry's zero point: the closest any everyday text comes to it.
        var zero = denyVectors.Select(d => everyday.Max(e => Dot(e, d))).ToArray();

        var best = (Closeness: 0.0, Entry: deny[0], Path: texts[0].Path);
        string? firstAllow = null;
        var anyJudged = false;

        for (var t = 0; t < texts.Count; t++)
        {
            var (rawDeny, _) = Best(textVectors[t], denyVectors, deny);
            var (rawAllow, allowText) = Best(textVectors[t], allowVectors, filter.AllowExamples);

            // A text closer to something the user allowed than to anything denied is not judged.
            if (rawAllow > rawDeny)
            {
                firstAllow ??= allowText;
                continue;
            }

            anyJudged = true;
            for (var d = 0; d < denyVectors.Length; d++)
            {
                var closeness = Math.Clamp((Dot(textVectors[t], denyVectors[d]) - zero[d]) / Math.Max(1e-6, 1 - zero[d]), 0, 1);
                if (closeness > best.Closeness) best = (closeness, deny[d], texts[t].Path);
            }
        }

        var threshold = filter.Threshold ?? PresetThreshold(filter.Level);
        return new Judgement(best.Closeness, threshold, best.Entry, best.Path, anyJudged ? null : firstAllow);
    }

    /// <summary>Convenience for tests: step 4 on raw arguments, nothing redacted.</summary>
    public Task<Judgement> JudgeAsync(FunnelFilter filter, string toolName, IReadOnlyList<KeyValuePair<string, JsonElement>>? arguments, CancellationToken ct) =>
        JudgeAsync(filter, TextsToJudge(toolName, FilterEngine.Read(arguments)), ct);

    private static List<string> DenyAnchors(FunnelFilter filter) =>
        [.. filter.DenyInstructions.Concat(filter.DenyExamples).Where(s => !string.IsNullOrWhiteSpace(s))];

    /// <summary>
    /// The deny list, allow list and everyday text are cached: they repeat on every call. The
    /// call's own text never goes through here, so nothing an agent sent is kept.
    /// </summary>
    private async Task<float[][]> EmbedCachedAsync(
        IEmbeddingClient client, string signature, IReadOnlyList<string> texts, EmbeddingRole role, CancellationToken ct)
    {
        var missing = texts.Where(t => !_cache.ContainsKey((signature, role, t))).Distinct().ToList();

        if (missing.Count > 0)
        {
            var vectors = await client.EmbedAsync(missing, role, ct).ConfigureAwait(false);
            for (var i = 0; i < missing.Count; i++) _cache[(signature, role, missing[i])] = vectors[i];
        }

        return [.. texts.Select(t => _cache[(signature, role, t)])];
    }

    private static (double Score, string Text) Best(float[] query, float[][] vectors, IReadOnlyList<string> texts)
    {
        var best = (Score: double.NegativeInfinity, Text: "");
        for (var i = 0; i < vectors.Length; i++)
        {
            var score = Dot(query, vectors[i]);
            if (score > best.Score) best = (score, texts[i]);
        }
        return best;
    }

    private static double Dot(float[] a, float[] b)
    {
        // Vectors are unit length, so the dot product is the cosine. Mismatched lengths mean the
        // model changed under a cached vector, which the signature in the cache key rules out.
        double sum = 0;
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++) sum += a[i] * b[i];
        return sum;
    }

    private static string Shorten(string s) => s.Length <= 60 ? s : s[..57] + "...";
}
