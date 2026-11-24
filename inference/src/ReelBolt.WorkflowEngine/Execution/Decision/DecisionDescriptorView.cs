using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ReelBolt.WorkflowEngine.Execution.Decision;

/// <summary>
/// Builds the <b>bounded descriptor state</b> a Gate-mode <c>Colorist</c>/<c>MusicSupervisor</c>
/// step sends to the configured Decision provider.
///
/// <para>
/// <b>Why this exists.</b> The Shadow path deliberately sends the agent's full input — phase-1
/// behaviour, reviewed and unchanged (decisions.md §8). The Gate path may not: its state goes to a
/// <i>hosted third party</i> that is an additional party beyond the model backend the agent itself
/// uses, and the question set it is asked is a handful of enum-word choices that need far less than
/// the agent's whole prompt. <see cref="DecisionGate"/>'s class remarks make that the caller's
/// responsibility; this type is how this caller discharges it. Never widen a projection here without
/// re-reading that decision.
/// </para>
///
/// <para>
/// <b>Two independent disciplines, both load-bearing.</b>
/// <list type="number">
/// <item><b>Narrow.</b> Only the descriptor family the question set actually reasons about is
/// projected — the per-shot measured colour words for the colourist, the offered candidate
/// descriptors for the music supervisor. Values are read out of the analyze step's
/// <c>view</c> object field by field; the view is never forwarded wholesale, so no transcript text
/// (a sibling key in the same object) can ride along — <see cref="ForColorist"/> and
/// <see cref="ForMusicSupervisor"/> each list the exact keys they admit and the ones they
/// deliberately leave behind.</item>
/// <item><b>Bounded.</b> Whatever the source view's size, the result never exceeds
/// <see cref="MaxStateChars"/> — the projection is truncated by whole entries with an explicit
/// omission marker, the same "degrade rather than overflow" discipline the analyze view itself
/// uses.</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Absent is not empty.</b> When no upstream step produced a view envelope, or the envelope
/// carries none of the descriptors this question set needs, the result is
/// <see cref="string.Empty"/> and the caller MUST escalate to the agent rather than send an empty
/// state — asking a decision model to choose between options from no evidence at all would produce
/// a confidently-arbitrary answer that the accept rule cannot detect.
/// </para>
/// </summary>
public static class DecisionDescriptorView
{
    /// <summary>
    /// The hard character cap on any projection this type returns, asserted in a test.
    ///
    /// <para>
    /// Chosen well inside the Decision provider's 4 k-token context: 2 000 characters is roughly
    /// 500 tokens of ordinary text, leaving the largest possible share of the budget to the question
    /// set and the instructions. The cap is enforced by construction (whole entries are admitted
    /// until one would not fit), so it holds for a 500-shot source exactly as it does for a 5-shot
    /// one.
    /// </para>
    /// </summary>
    public const int MaxStateChars = 2000;

    /// <summary>
    /// Head-room reserved for the "N further entries omitted" marker, so a projection that fills the
    /// body to the cap can still say honestly that it was truncated.
    /// </summary>
    private const int OmissionMarkerReserve = 80;

    /// <summary>Per-entry cap on a music candidate's file name, so one pathological name cannot consume the whole budget.</summary>
    private const int MaxMusicNameChars = 96;

    private const string OmittedSuffix = " entries omitted for length)";

    /// <summary>
    /// Projects the <b>per-shot measured colour words</b> a <c>VideoAnalyze</c> step wrote under
    /// <c>view.shots[].v</c> — <c>temp</c>, <c>tone</c> and <c>sat</c>, the Phase-4 D1-D3
    /// classifications the <c>Colorist</c> prompt names as its measured evidence.
    ///
    /// <para>
    /// Those three keys are emitted only at <c>VisualDetail.Full</c> and only when
    /// <c>AnalyzeColorGrading</c> is on, so a default (<c>Compact</c>) analyze run yields an empty
    /// projection and the gate escalates. That is the correct outcome — the alternative is deciding
    /// a grade from a view that carries no measured colour — and it is why the colorist gate is only
    /// meaningful for a workflow whose analyze step requests full visual detail.
    /// </para>
    ///
    /// <para>
    /// Deliberately <b>excluded</b>, though they also live in <c>v</c>: <c>motion</c>/<c>move</c>/
    /// <c>still</c> (editing concerns, not grading), <c>bright</c>/<c>contrast</c>/<c>colors</c>/
    /// <c>safe</c>/<c>dup</c> (exposure and framing, or hex values rather than words), and every key
    /// of a shot's sibling <c>c</c> (vision caption) node — none of it is a colour word, and the
    /// caption node is media-derived prose that has no business leaving the host for this question
    /// set.
    /// </para>
    ///
    /// <para>
    /// Read through <see cref="StepExecutionContext.OutputForPrompt"/>, so a prompt-only enrichment
    /// installed by <c>SetPromptOutputOverride</c> is honoured and the projection is exactly what
    /// the agent's own prompt would have seen. <see cref="StepExecutionContext.BuildAgentInput"/> is
    /// never called.
    /// </para>
    /// </summary>
    public static string ForColorist(StepExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        foreach (JsonObject view in ViewsNearestFirst(context))
        {
            var lines = new List<string>();

            if (view["shots"] is JsonArray shots)
            {
                foreach (JsonObject shot in Objects(shots))
                {
                    if (shot["v"] is not JsonObject visual)
                        continue;

                    string? id = AsString(shot["id"]);
                    string? temp = AsString(visual["temp"]);
                    string? tone = AsString(visual["tone"]);
                    string? sat = AsString(visual["sat"]);

                    // A shot carrying none of the three measured colour words is not evidence: it
                    // is the Compact-detail / AnalyzeColorGrading=false shape, and projecting it as
                    // "temp=-" would be inventing a measurement.
                    if (temp is null && tone is null && sat is null)
                        continue;

                    lines.Add($"{id ?? "?"} temp={temp ?? "-"} tone={tone ?? "-"} sat={sat ?? "-"}");
                }
            }

            // Outline mode: shots past the first section carry their colour words in outline.colour ("s12 temp=warm tone=mid sat=low").
            if (view["outline"]?["colour"] is JsonArray outlineColour)
            {
                var seen = new HashSet<string>(lines.Select(l => l.Split(' ')[0]), StringComparer.Ordinal);
                foreach (JsonNode? row in outlineColour)
                {
                    string? text = row is JsonValue v && v.TryGetValue(out string? t) ? t : null;
                    if (text is not null && seen.Add(text.Split(' ')[0]))
                        lines.Add(text);
                }
            }

            if (lines.Count == 0)
                continue;

            return Compose(
                "Measured colour descriptors, one line per analyzed shot (enum words only — no "
                + "timestamps, no transcript, no pixel values):",
                lines,
                "shots");
        }

        return string.Empty;
    }

    /// <summary>
    /// Projects the <b>offered background-music candidate descriptors</b> a <c>VideoAnalyze</c>
    /// step wrote under <c>view.musicTracks</c> — the opaque <c>m{n}</c> id and the file name, which
    /// is the whole of what a <c>MusicSupervisor</c> is given about a candidate.
    ///
    /// <para>
    /// Empty when no candidate was offered (the step ran without <c>OfferMusicTracks</c>, or the
    /// array was suppressed for budget), which escalates — correctly, since there would be nothing
    /// for the model to choose between.
    /// </para>
    /// </summary>
    public static string ForMusicSupervisor(StepExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        foreach (JsonObject view in ViewsNearestFirst(context))
        {
            var lines = new List<string>();

            if (view["musicTracks"] is JsonArray tracks)
            {
                foreach (JsonObject track in Objects(tracks))
                {
                    string? id = AsString(track["id"]);
                    if (id is null)
                        continue;

                    string name = AsString(track["name"]) ?? string.Empty;
                    if (name.Length > MaxMusicNameChars)
                        name = name[..MaxMusicNameChars];

                    lines.Add(string.IsNullOrWhiteSpace(name) ? id : $"{id} {name}");
                }
            }

            if (lines.Count == 0)
                continue;

            return Compose(
                "Offered background-music candidate descriptors (opaque id and file name only — no "
                + "duration, no tempo, no audio content):",
                lines,
                "candidates");
        }

        return string.Empty;
    }

    /// <summary>
    /// The <c>view</c> objects of every history entry strictly upstream of the consuming step,
    /// nearest first. Nearest-first matters on a <c>ReviewLoop</c> loop-back, where an earlier
    /// <c>StepOrder</c> appears twice: the retry's analyze artifact is the one that belongs to this
    /// attempt.
    /// </summary>
    private static IEnumerable<JsonObject> ViewsNearestFirst(StepExecutionContext context)
    {
        foreach (StepOutputHistoryEntry entry in context.StepOutputHistory
            .Where(h => h.StepOrder < context.Step.StepOrder && !string.IsNullOrWhiteSpace(h.Output))
            .OrderByDescending(h => h.StepOrder))
        {
            if (TryReadView(context.OutputForPrompt(entry)) is { } view)
                yield return view;
        }
    }

    private static JsonObject? TryReadView(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return null;

        try
        {
            return JsonNode.Parse(output) is JsonObject root && root["view"] is JsonObject view
                ? view
                : null;
        }
        catch (JsonException)
        {
            // An upstream step that did not emit JSON (or emitted a truncated blob) is simply not a
            // descriptor source. Not an error: the gate escalates.
            return null;
        }
    }

    private static IEnumerable<JsonObject> Objects(JsonArray array)
    {
        foreach (JsonNode? node in array)
        {
            if (node is JsonObject obj)
                yield return obj;
        }
    }

    private static string? AsString(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    /// <summary>
    /// Renders the header plus as many whole lines as fit under <see cref="MaxStateChars"/>, then an
    /// explicit omission marker when any were dropped. Reserving the marker's head-room up front is
    /// what keeps the return value inside the cap for ANY input size.
    /// </summary>
    private static string Compose(string header, List<string> lines, string noun)
    {
        StringBuilder builder = new(header);

        int included = 0;
        foreach (string line in lines)
        {
            if (builder.Length + 1 + line.Length > MaxStateChars - OmissionMarkerReserve)
                break;

            builder.Append('\n').Append(line);
            included++;
        }

        if (included < lines.Count)
            builder.Append('\n').Append('(').Append(lines.Count - included).Append(' ').Append(noun).Append(OmittedSuffix);

        return builder.ToString();
    }
}
