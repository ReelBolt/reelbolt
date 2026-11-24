using ReelBolt.Shared.Workflows.VideoEvents;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ReelBolt.Shared.Data.OutputSchemas;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>
/// One source clip ("src") as the editors were offered it: the ids they could keep, which of those
/// are scenes (shot ids, in offered order — the boundaries between consecutive ones are where another
/// clip can be placed inside a composition), and what is known about its kind and shape.
/// </summary>
public sealed record InterleaveSource(
    int Src,
    IReadOnlyList<string> OfferedIds,
    IReadOnlyList<string> SceneIds,
    bool IsComposition,
    bool IsStill = false,
    string? Orientation = null,
    bool? MatchesTarget = null)
{
    /// <summary>A source the editors could actually use: at least one id of it was offered.</summary>
    public bool IsUsable => OfferedIds.Count > 0;
}

/// <summary>
/// Everything <see cref="InterleaveGuard"/> needs to know about what was OFFERED: every source clip,
/// and which source each offered id belongs to. Built from the bounded view the editors saw
/// (<see cref="FromView"/>, used where the decision is made) or from the full analysis artifact
/// (<see cref="FromArtifact"/>, used by the compile step); both describe the same offer.
/// </summary>
public sealed record InterleaveOffer(IReadOnlyList<InterleaveSource> Sources, IReadOnlyDictionary<string, int> IdSources)
{
    public IReadOnlyList<InterleaveSource> UsableSources => Sources.Where(s => s.IsUsable).ToList();

    public InterleaveSource? Source(int src) => Sources.FirstOrDefault(s => s.Src == src);

    /// <summary>
    /// Reads the offer out of a <c>VideoAnalyze</c> envelope (<c>{view, meta}</c>) or a bare view.
    /// Never throws: anything unreadable contributes nothing, which can only make the guard LESS
    /// demanding (fewer usable sources), never fabricate a requirement.
    /// </summary>
    public static InterleaveOffer FromView(JsonNode? root)
    {
        JsonNode? view = root?["view"] ?? root;
        var idSources = new Dictionary<string, int>(StringComparer.Ordinal);
        var offered = new SortedDictionary<int, List<string>>();
        var scenes = new SortedDictionary<int, List<string>>();

        try
        {
            Collect(view?["shots"], isScene: true);
            Collect(view?["silences"], isScene: false);
            Collect(view?["segments"], isScene: false);
            // Outline mode: the rest of the film is listed as id ranges.
            foreach ((string id, int src, bool isShot) in OutlineViewIds.Expand(view, root?["meta"]))
            {
                if (!idSources.TryAdd(id, src))
                    continue;
                if (!offered.TryGetValue(src, out List<string>? list))
                    offered[src] = list = [];
                list.Add(id);
                if (isShot)
                {
                    if (!scenes.TryGetValue(src, out List<string>? sceneList))
                        scenes[src] = sceneList = [];
                    sceneList.Add(id);
                }
            }
        }
        catch (InvalidOperationException)
        {
            // A non-string id or similar malformed node: keep whatever was read before it.
        }

        var compositions = new HashSet<int>();
        var known = new HashSet<int>(offered.Keys);
        if (view?["sources"] is JsonArray sourcesArray)
        {
            foreach (JsonNode? s in sourcesArray)
            {
                if (s?["src"] is not JsonValue srcValue || !srcValue.TryGetValue(out int src))
                    continue;
                known.Add(src);
                if (s["composition"] is JsonValue c && c.TryGetValue(out bool isComposition) && isComposition)
                    compositions.Add(src);
            }
        }
        else if (view?["media"]?["composition"] is JsonValue mediaComposition
                 && mediaComposition.TryGetValue(out bool allComposition) && allComposition)
        {
            // Single-source (or all-composition) view: the media node carries the flag instead.
            foreach (int src in known)
                compositions.Add(src);
        }

        var stills = new HashSet<int>();
        if (root?["meta"]?["stillImages"] is JsonArray stillArray)
        {
            foreach (JsonNode? s in stillArray)
            {
                if (s is JsonValue v && v.TryGetValue(out int src))
                    stills.Add(src);
            }
        }

        var orientation = new Dictionary<int, (string? Orientation, bool? Matches)>();
        if (view?["orientation"]?["sources"] is JsonArray orientationArray)
        {
            foreach (JsonNode? o in orientationArray)
            {
                if (o?["src"] is not JsonValue srcValue || !srcValue.TryGetValue(out int src))
                    continue;
                string? shape = o["orientation"] is JsonValue sv && sv.TryGetValue(out string? s) ? s : null;
                bool? matches = o["matchesTarget"] is JsonValue mv && mv.TryGetValue(out bool m) ? m : null;
                orientation[src] = (shape, matches);
            }
        }

        List<InterleaveSource> sources = known.OrderBy(s => s).Select(src =>
        {
            orientation.TryGetValue(src, out (string? Orientation, bool? Matches) o);
            return new InterleaveSource(
                src,
                offered.TryGetValue(src, out List<string>? ids) ? ids : [],
                scenes.TryGetValue(src, out List<string>? sceneIds) ? sceneIds : [],
                compositions.Contains(src),
                stills.Contains(src),
                o.Orientation,
                o.Matches);
        }).ToList();

        return new InterleaveOffer(sources, idSources);

        void Collect(JsonNode? array, bool isScene)
        {
            if (array is not JsonArray items)
                return;

            foreach (JsonNode? item in items)
            {
                if (item?["id"] is not JsonValue idValue || !idValue.TryGetValue(out string? id) || string.IsNullOrEmpty(id))
                    continue;

                int src = item["src"] is JsonValue srcValue && srcValue.TryGetValue(out int parsed) ? parsed : 0;
                if (!idSources.TryAdd(id, src))
                    continue;

                Append(offered, src, id);
                if (isScene)
                    Append(scenes, src, id);
            }
        }
    }

    /// <summary>
    /// The same offer read from the full analysis artifact: <see cref="VideoAnalysisArtifact.OfferedIds"/>
    /// (exactly what the bounded view showed), each id's <c>SourceIndex</c>, and each source's
    /// composition flag. Orientation and still-image facts are not needed here and are left unset.
    /// </summary>
    public static InterleaveOffer FromArtifact(VideoAnalysisArtifact artifact)
    {
        var sourceOfId = new Dictionary<string, (int Src, bool IsScene)>(StringComparer.Ordinal);
        foreach (VideoAnalysisShot shot in artifact.Shots ?? [])
            sourceOfId.TryAdd(shot.Id, (shot.SourceIndex, true));
        foreach (VideoAnalysisSilenceSpan silence in artifact.SilenceSpans ?? [])
            sourceOfId.TryAdd(silence.Id, (silence.SourceIndex, false));
        foreach (VideoAnalysisSegment segment in artifact.Segments ?? [])
            sourceOfId.TryAdd(segment.Id, (segment.SourceIndex, false));

        var idSources = new Dictionary<string, int>(StringComparer.Ordinal);
        var offered = new SortedDictionary<int, List<string>>();
        var scenes = new SortedDictionary<int, List<string>>();
        foreach (string id in artifact.OfferedIds ?? [])
        {
            if (!sourceOfId.TryGetValue(id, out (int Src, bool IsScene) owner) || !idSources.TryAdd(id, owner.Src))
                continue;
            Append(offered, owner.Src, id);
            if (owner.IsScene)
                Append(scenes, owner.Src, id);
        }

        var known = new HashSet<int>(offered.Keys);
        var compositions = new HashSet<int>();
        foreach (VideoAnalysisSourceInfo info in artifact.Sources ?? [])
        {
            known.Add(info.SourceIndex);
            if (info.IsComposition)
                compositions.Add(info.SourceIndex);
        }

        List<InterleaveSource> sources = known.OrderBy(s => s).Select(src => new InterleaveSource(
            src,
            offered.TryGetValue(src, out List<string>? ids) ? ids : [],
            scenes.TryGetValue(src, out List<string>? sceneIds) ? sceneIds : [],
            compositions.Contains(src))).ToList();

        return new InterleaveOffer(sources, idSources);
    }

    private static void Append(SortedDictionary<int, List<string>> map, int src, string id)
    {
        if (!map.TryGetValue(src, out List<string>? list))
            map[src] = list = [];
        list.Add(id);
    }
}

/// <summary>
/// What <see cref="InterleaveGuard.Evaluate"/> concluded about one decision. <see cref="Required"/>
/// is false when the view offered fewer than two usable sources — then nothing is asked of the
/// decision and nothing is recorded anywhere.
/// </summary>
public sealed record InterleaveVerdict(
    bool Required,
    bool Satisfied,
    string Reason,
    IReadOnlyList<int> OfferedSources,
    IReadOnlyList<int> UsedSources,
    IReadOnlyList<int> UnusedSources,
    IReadOnlyList<int> UnbrokenCompositions,
    string? Justification,
    bool JustificationVerified)
{
    /// <summary>
    /// The recorded finding — the <c>interleave</c> node on a decision step's output and on the
    /// compile step's review facts. <paramref name="retried"/> is set only where the decision was
    /// made (whether the one retry with feedback was spent), never by the compile step.
    /// </summary>
    public JsonObject ToJson(bool? retried = null)
    {
        var node = new JsonObject
        {
            ["required"] = Required,
            ["satisfied"] = Satisfied,
            ["reason"] = Reason,
            ["offeredSources"] = Ints(OfferedSources),
            ["usedSources"] = Ints(UsedSources),
            ["unusedSources"] = Ints(UnusedSources),
            ["unbrokenCompositions"] = Ints(UnbrokenCompositions),
            ["justification"] = Justification,
            ["justificationVerified"] = JustificationVerified
        };
        if (retried is { } r)
            node["retried"] = r;
        return node;
    }

    private static JsonArray Ints(IReadOnlyList<int> values) =>
        new(values.Select(v => (JsonNode)JsonValue.Create(v)).ToArray());
}

/// <summary>
/// The deterministic interleave guard (docs/video-editing.md "Using every clip offered"). An editor
/// that was offered several source clips must at least TRY to use more than one of them, and a
/// finished composition must not be kept as one unbroken run while other material sat unused —
/// unless the user's brief itself excludes that material, which the decision proves by quoting the
/// brief VERBATIM in <see cref="VideoEditDecisionOutput.SingleSourceJustification"/>.
///
/// <para>
/// Pure and total: no I/O, never throws, and every input it cannot read makes it less demanding
/// rather than more. It judges only what code can judge — which clips were used, where, and whether
/// a quoted sentence occurs in the brief. Whether a genuinely quoted sentence actually EXCLUDES other
/// material is a question of meaning, left to the reviewer (whose prompt says so).
/// </para>
/// </summary>
public static class InterleaveGuard
{
    /// <summary>Usable sources fewer than this: nothing is required.</summary>
    public const string ReasonSingleSourceOffered = "single_source_offered";
    /// <summary>Two or more sources used, and no composition left unbroken.</summary>
    public const string ReasonInterleaved = "interleaved";
    /// <summary>A rule was broken, but the decision quoted the brief verbatim to justify it.</summary>
    public const string ReasonJustified = "justified";
    /// <summary>Every kept span comes from one source, with no justification at all.</summary>
    public const string ReasonSingleSourceUnjustified = "single_source_unjustified";
    /// <summary>A composition was kept as one unbroken run with nothing placed between its scenes.</summary>
    public const string ReasonCompositionUnbroken = "composition_unbroken";
    /// <summary>A justification was given, but it is too short or does not occur in the brief.</summary>
    public const string ReasonJustificationNotInBrief = "justification_not_in_brief";
    /// <summary>No valid keep span at all — another check's concern (EMPTY_KEEP), never retried for here.</summary>
    public const string ReasonNothingKept = "nothing_kept";

    /// <summary>A quote shorter than this (normalized) proves nothing — "no", "only this" and "keep it" occur in every brief.</summary>
    public const int MinJustificationChars = 12;

    /// <summary>A quote must also carry at least this many words.</summary>
    public const int MinJustificationWords = 3;

    /// <summary>How many composition scene boundaries the retry feedback lists before summarizing the rest.</summary>
    internal const int MaxListedBoundaries = 24;

    /// <summary>The cap on a quoted room proposal in the retry feedback.</summary>
    internal const int MaxProposalChars = 600;

    private static readonly Regex SrcMention = new(@"\bsrc\s*:?\s*(\d+)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Decides whether <paramref name="decision"/> uses the offered material honestly.
    /// A span counts only when both its ids were offered AND come from the same source — exactly the
    /// spans the room filter and the compile step keep — so an unoffered or clip-bridging span can
    /// neither satisfy the rule nor trip it.
    /// </summary>
    public static InterleaveVerdict Evaluate(InterleaveOffer offer, VideoEditDecisionOutput decision, string? userRequest)
    {
        List<int> usable = offer.UsableSources.Select(s => s.Src).ToList();
        string? justification = string.IsNullOrWhiteSpace(decision.SingleSourceJustification)
            ? null
            : decision.SingleSourceJustification.Trim();

        if (usable.Count < 2)
        {
            return new InterleaveVerdict(
                Required: false, Satisfied: true, ReasonSingleSourceOffered,
                usable, usable, [], [], justification, JustificationVerified: false);
        }

        // Each valid span's source, in list order (the order the edit plays across clips).
        List<int> spanSources = [];
        foreach (VideoEditKeepSpan span in decision.Keep ?? [])
        {
            if (span?.FromId is null || span.ToId is null
                || !offer.IdSources.TryGetValue(span.FromId, out int fromSrc)
                || !offer.IdSources.TryGetValue(span.ToId, out int toSrc)
                || fromSrc != toSrc)
            {
                continue;
            }

            spanSources.Add(fromSrc);
        }

        List<int> used = spanSources.Distinct().OrderBy(s => s).ToList();
        List<int> unused = usable.Where(s => !used.Contains(s)).ToList();
        bool verified = QuoteAppearsInBrief(justification, userRequest);

        if (used.Count == 0)
        {
            return new InterleaveVerdict(
                Required: true, Satisfied: false, ReasonNothingKept,
                usable, used, unused, [], justification, verified);
        }

        // A composition is "unbroken" when no span of another source is placed between its first and
        // last kept span — whether it is kept as one run or as consecutive runs with nothing between.
        // Only a composition with at least two offered scenes has a boundary to place anything at.
        List<int> unbroken = [];
        foreach (int src in used)
        {
            InterleaveSource? source = offer.Source(src);
            if (source is not { IsComposition: true } || source.SceneIds.Count < 2)
                continue;

            int first = spanSources.IndexOf(src);
            int last = spanSources.LastIndexOf(src);
            bool somethingBetween = false;
            for (int i = first + 1; i < last; i++)
            {
                if (spanSources[i] != src)
                {
                    somethingBetween = true;
                    break;
                }
            }

            if (!somethingBetween)
                unbroken.Add(src);
        }

        string violation;
        if (used.Count == 1)
            violation = ReasonSingleSourceUnjustified;
        else if (unbroken.Count > 0)
            violation = ReasonCompositionUnbroken;
        else
            return new InterleaveVerdict(true, true, ReasonInterleaved, usable, used, unused, unbroken, justification, verified);

        string reason = verified
            ? ReasonJustified
            : justification is not null ? ReasonJustificationNotInBrief : violation;
        return new InterleaveVerdict(true, verified, reason, usable, used, unused, unbroken, justification, verified);
    }

    /// <summary>
    /// True when <paramref name="quote"/> is long enough to mean something and occurs in
    /// <paramref name="brief"/> after both are normalized identically (case, whitespace, typographic
    /// quotes and dashes, markdown emphasis, and the quote's own surrounding quote marks and
    /// punctuation). A substring test, deliberately: a paraphrase, a summary or an invented
    /// restriction does not occur in the brief, and that is exactly what this must refuse.
    /// </summary>
    public static bool QuoteAppearsInBrief(string? quote, string? brief)
    {
        if (string.IsNullOrWhiteSpace(quote) || string.IsNullOrWhiteSpace(brief))
            return false;

        string q = TrimQuoteEdges(Normalize(quote));
        if (q.Length < MinJustificationChars)
            return false;
        if (q.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length < MinJustificationWords)
            return false;

        return Normalize(brief).Contains(q, StringComparison.Ordinal);
    }

    /// <summary>The normalization both sides of <see cref="QuoteAppearsInBrief"/> go through.</summary>
    internal static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        bool pendingSpace = false;
        foreach (char raw in text.Normalize(NormalizationForm.FormKC))
        {
            char c = raw switch
            {
                '‘' or '’' or '‚' or '‛' or '′' or '`' or '´' => '\'',
                '“' or '”' or '„' or '‟' or '«' or '»' or '″' => '"',
                '‐' or '‑' or '‒' or '–' or '—' or '―' or '−' => '-',
                '…' => '.',
                _ => raw
            };

            // Markdown emphasis is presentation, not words: "**bold** lines" quotes as "bold lines".
            if (c is '*' or '_')
                continue;

            if (char.IsWhiteSpace(c))
            {
                pendingSpace = sb.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                sb.Append(' ');
                pendingSpace = false;
            }

            sb.Append(char.ToLower(c, CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }

    /// <summary>Strips what surrounds a quotation rather than belonging to it: quote marks, a bullet, and edge punctuation.</summary>
    private static string TrimQuoteEdges(string normalized) =>
        normalized.Trim().Trim('"', '\'', '-', '.', ',', ';', ':', '!', '?', '(', ')', '[', ']', ' ');

    /// <summary>
    /// The most recent room turn that proposed using another clip — one that names material (by
    /// offered id, or as "src N") from two or more sources, or from a source the decision left
    /// unused. Quoted back to the director on the retry: the room's own proposal is the most
    /// persuasive feedback there is. Null when no turn did.
    /// </summary>
    public static string? FindIntercutProposal(
        IEnumerable<(string Speaker, string Text)> turns, InterleaveOffer offer, InterleaveVerdict verdict)
    {
        string? found = null;
        foreach ((string speaker, string text) in turns)
        {
            if (string.IsNullOrWhiteSpace(text))
                continue;

            var sources = new HashSet<int>();
            foreach (Match m in SrcMention.Matches(text))
            {
                if (int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int src)
                    && offer.Source(src) is { IsUsable: true })
                {
                    sources.Add(src);
                }
            }

            foreach (Match m in Regex.Matches(text, @"\b[sgt]\d+\b"))
            {
                if (offer.IdSources.TryGetValue(m.Value, out int src))
                    sources.Add(src);
            }

            if (sources.Count >= 2 || sources.Any(s => verdict.UnusedSources.Contains(s)))
            {
                string trimmed = text.Trim();
                if (trimmed.Length > MaxProposalChars)
                    trimmed = trimmed[..MaxProposalChars] + "...";
                found = $"{speaker}: {trimmed}";
            }
        }

        return found;
    }

    /// <summary>
    /// The precise, actionable feedback a decision that failed the guard is sent back with: which
    /// clips were offered and left unused (and their ids), the composition's scene boundaries where a
    /// clip fits, the room's own intercut proposal when there was one, and the opt-out rule.
    /// </summary>
    public static string BuildRetryFeedback(InterleaveOffer offer, InterleaveVerdict verdict, string? roomProposal)
    {
        var sb = new StringBuilder();
        string used = string.Join(", ", verdict.UsedSources.Select(s => $"src{s}"));

        if (verdict.UsedSources.Count == 1)
        {
            sb.Append($"INTERLEAVE CHECK FAILED: every Keep span comes from one clip ({used}), but the view offered ")
              .Append($"{verdict.OfferedSources.Count} usable clips. ");
        }
        else
        {
            sb.Append("INTERLEAVE CHECK FAILED: ")
              .Append(string.Join(", ", verdict.UnbrokenCompositions.Select(s => $"src{s}")))
              .Append(" is a finished composition kept as one unbroken run — the other clips only sit before or after it, ")
              .Append("never between its scenes. ");
        }

        if (verdict.UnusedSources.Count > 0)
        {
            sb.Append("Clips you were given and did not use: ");
            sb.Append(string.Join("; ", verdict.UnusedSources
                .Select(offer.Source)
                .Where(s => s is not null)
                .Select(s => Describe(s!))));
            sb.Append(". ");
        }

        foreach (int src in verdict.UsedSources)
        {
            InterleaveSource? composition = offer.Source(src);
            if (composition is not { IsComposition: true } || composition.SceneIds.Count < 2)
                continue;

            List<string> boundaries = composition.SceneIds
                .Zip(composition.SceneIds.Skip(1), (a, b) => $"{a}|{b}")
                .ToList();
            string listed = string.Join(", ", boundaries.Take(MaxListedBoundaries));
            if (boundaries.Count > MaxListedBoundaries)
                listed += $", and {boundaries.Count - MaxListedBoundaries} more";

            sb.Append($"src{src} is a finished composition: keep every scene in order, but split it into consecutive Keep spans ")
              .Append("AT SCENE BOUNDARIES and place another clip's span between two of them — end one span on the scene before ")
              .Append($"the boundary and resume with the scene after it. Its scene boundaries are: {listed}. ")
              .Append("\"Keep every scene in order\" does NOT mean \"insert nothing\". ");
        }

        if (!string.IsNullOrWhiteSpace(roomProposal))
            sb.Append($"The room itself proposed using the other material — \"{roomProposal}\". Synthesize that proposal rather than discarding it. ");

        if (verdict.Justification is not null && !verdict.JustificationVerified)
        {
            sb.Append($"Your singleSourceJustification \"{verdict.Justification}\" was REJECTED: it is not a verbatim quote of the user's brief ")
              .Append($"(it must occur in the brief word for word and be at least {MinJustificationChars} characters and {MinJustificationWords} words). ");
        }

        sb.Append("Either interleave the other clips (a clip whose shape does not match the target format is a reason to keep its span short, ")
          .Append("not to leave it out), or — ONLY if the user's brief explicitly excludes the other material — set singleSourceJustification ")
          .Append("to the brief's own words that say so, copied verbatim. Never invent a restriction the brief does not state: a rule about ")
          .Append("on-screen text, colour or pacing is not a rule about which clips to use.");

        return sb.ToString();
    }

    private static string Describe(InterleaveSource s)
    {
        string kind = s.IsComposition ? "composition" : s.IsStill ? "still photo" : "video clip";
        string ids = s.OfferedIds.Count switch
        {
            1 => s.OfferedIds[0],
            <= 3 => string.Join(", ", s.OfferedIds),
            _ => $"{s.OfferedIds[0]}..{s.OfferedIds[^1]}"
        };
        string shape = s.MatchesTarget switch
        {
            true => $", {s.Orientation ?? "shape"} — matches the target format",
            false => $", {s.Orientation ?? "shape"} — cropped to fit the target format, so keep it short",
            null => s.Orientation is null ? string.Empty : $", {s.Orientation}"
        };
        return $"src{s.Src} ({kind}, ids {ids}{shape})";
    }
}
