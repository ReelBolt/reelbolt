using System.Text.Json.Nodes;
using ReelBolt.Shared.Data.OutputSchemas;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>One motion cue's outcome, as reported in the step output.</summary>
internal sealed record MotionReportEntry(string EventId, string Action, bool Applied, string? Reason);

/// <summary>
/// What a <see cref="MotionPlanOutput"/> turned into: the primitives' own request lists (event moves, speed ramps, flash
/// cues), the graphics anchored on events, and a per-cue report. The lists are the plan's entries only; the caller merges
/// them after any hand-written ones.
/// </summary>
internal sealed record MotionTranslation(
    IReadOnlyList<EventMoveRequest> Moves,
    IReadOnlyList<SpeedRampRequest> Ramps,
    IReadOnlyList<FlashCueRequest> Flashes,
    IReadOnlyList<MotionGraphicsOverlay> Graphics,
    IReadOnlyList<MotionReportEntry> Report,
    string? Reason = null)
{
    internal static readonly MotionTranslation Empty = new([], [], [], [], []);

    internal static MotionTranslation Unavailable(string reason) => new([], [], [], [], [], reason);

    internal JsonObject ToNode()
    {
        var applied = new JsonArray();
        var dropped = new JsonArray();
        foreach (MotionReportEntry e in Report)
        {
            if (e.Applied)
                applied.Add(new JsonObject { ["eventId"] = e.EventId, ["action"] = e.Action });
            else
                dropped.Add(new JsonObject { ["eventId"] = e.EventId, ["action"] = e.Action, ["reason"] = e.Reason });
        }

        var node = new JsonObject { ["cuesApplied"] = applied, ["dropped"] = dropped };
        if (Reason is not null)
            node["reason"] = Reason;
        return node;
    }
}

/// <summary>
/// Translates a MotionDirector plan into the compile's event-anchored primitives (docs/video-editing.md "Motion on events").
/// The words map 1:1 onto the primitives' own enums; no number is read from the plan. Whether an event exists, is in the
/// edit, and whether a move overlaps a reframe is decided by the primitives themselves, with their own drop reasons.
/// Pure and deterministic.
/// </summary>
internal static class MotionPlanTranslator
{
    public const int DefaultMaxCues = 8;
    public const int MaxCuesCeiling = 16;

    public const string UnknownAnchorReason = "unknown_anchor_id";
    public const string NotOfferedReason = "event_not_offered";
    public const string UnknownActionReason = "unknown_action";
    public const string UnknownWordReason = "unknown_word";
    public const string MaxCuesReason = "max_motion_cues";
    public const string PrimitiveCapReason = "primitive_cap";
    public const string DuplicateReason = "duplicate_cue";
    public const string GraphicTextMissingReason = "graphic_text_missing";
    public const string GraphicsOffReason = "graphics_not_enabled";

    /// <summary>The cap the config asks for, clamped to 1..16 (null reads as 8).</summary>
    internal static int ClampMaxCues(int? requested) => Math.Clamp(requested ?? DefaultMaxCues, 1, MaxCuesCeiling);

    internal static MotionTranslation Translate(
        MotionPlanOutput plan,
        IReadOnlyCollection<string> offeredEventIds,
        VideoCompileStepConfig config,
        bool graphicsEnabled)
    {
        HashSet<string> offered = new(offeredEventIds, StringComparer.Ordinal);
        int maxCues = ClampMaxCues(config.MaxMotionCues);
        List<EventMoveRequest> moves = [];
        List<SpeedRampRequest> ramps = [];
        List<FlashCueRequest> flashes = [];
        List<MotionGraphicsOverlay> graphics = [];
        List<MotionReportEntry> report = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        int accepted = 0;
        int handMoves = config.EventMoves?.Count ?? 0, handRamps = config.SpeedRamps?.Count ?? 0, handFlashes = config.FlashCues?.Count ?? 0;

        foreach (MotionCue cue in plan.Cues ?? [])
        {
            string id = (cue.EventId ?? string.Empty).Trim();
            string action = (cue.Action ?? string.Empty).Trim();

            string? Drop(string reason)
            {
                report.Add(new MotionReportEntry(id, action, false, reason));
                return null;
            }

            if (!EventMoveRequest.IsEventId(id))
            {
                Drop(UnknownAnchorReason);
                continue;
            }

            if (!offered.Contains(id))
            {
                Drop(NotOfferedReason);
                continue;
            }

            if (!Word(action, out EventMoveKind _) && !Word(action, out SpeedRampKind _) && !IsWord(action, "Flash") && !IsWord(action, "Graphic"))
            {
                Drop(UnknownActionReason);
                continue;
            }

            if (accepted >= maxCues)
            {
                Drop(MaxCuesReason);
                continue;
            }

            if (!seen.Add($"{id}|{action.ToLowerInvariant()}"))
            {
                Drop(DuplicateReason);
                continue;
            }

            string intensity = (cue.Intensity ?? string.Empty).Trim();
            string length = (cue.Length ?? string.Empty).Trim();
            bool ok = true;

            if (Word(action, out EventMoveKind kind))
            {
                ok = Opt(intensity, EventMoveIntensity.Normal, out EventMoveIntensity i) & Opt(length, EventMoveHold.Medium, out EventMoveHold h);
                if (!ok) { Drop(UnknownWordReason); seen.Remove($"{id}|{action.ToLowerInvariant()}"); continue; }
                if (handMoves + moves.Count >= EventMoveRequest.MaxRequests) { Drop(PrimitiveCapReason); continue; }
                moves.Add(new EventMoveRequest(id, kind, i, h));
            }
            else if (Word(action, out SpeedRampKind rk))
            {
                ok = Opt(intensity, SpeedRampStrength.Normal, out SpeedRampStrength st) & Opt(length, SpeedRampSpan.Medium, out SpeedRampSpan sp);
                if (!ok) { Drop(UnknownWordReason); seen.Remove($"{id}|{action.ToLowerInvariant()}"); continue; }
                if (handRamps + ramps.Count >= SpeedRampRequest.MaxRequests) { Drop(PrimitiveCapReason); continue; }
                ramps.Add(new SpeedRampRequest(id, rk, st, sp));
            }
            else if (IsWord(action, "Flash"))
            {
                ok = Opt((cue.Colour ?? string.Empty).Trim(), FlashColour.White, out FlashColour c)
                    & Opt(intensity, FlashStrength.Normal, out FlashStrength st) & Opt(length, FlashLength.Short, out FlashLength l);
                if (!ok) { Drop(UnknownWordReason); seen.Remove($"{id}|{action.ToLowerInvariant()}"); continue; }
                if (handFlashes + flashes.Count >= FlashCueRequest.MaxRequests) { Drop(PrimitiveCapReason); continue; }
                flashes.Add(new FlashCueRequest(id, c, st, l));
            }
            else
            {
                if (!graphicsEnabled) { Drop(GraphicsOffReason); continue; }
                if (string.IsNullOrWhiteSpace(cue.Text)) { Drop(GraphicTextMissingReason); continue; }
                ok = Opt(intensity, EventMoveIntensity.Normal, out EventMoveIntensity e);
                string kindWord = (cue.Kind ?? string.Empty).Trim();
                if (!ok || (kindWord.Length > 0 && !GraphicKinds.Contains(kindWord, StringComparer.OrdinalIgnoreCase)))
                {
                    Drop(UnknownWordReason);
                    continue;
                }

                graphics.Add(new MotionGraphicsOverlay
                {
                    EventId = id,
                    Kind = kindWord.Length == 0 ? "Callout" : GraphicKinds.First(k => string.Equals(k, kindWord, StringComparison.OrdinalIgnoreCase)),
                    Text = cue.Text.Trim(),
                    Duration = length.Length == 0 ? "Medium" : IsWord(length, "Long") ? "Hold" : IsWord(length, "Short") ? "Short" : "Medium",
                    Emphasis = e.ToString(),
                    Reason = cue.Reason ?? string.Empty
                });
            }

            accepted++;
            report.Add(new MotionReportEntry(id, action, true, null));
        }

        return new MotionTranslation(moves, ramps, flashes, graphics, report);
    }

    private static readonly string[] GraphicKinds = ["LowerThird", "Title", "Callout", "Tag"];

    private static bool IsWord(string value, string word) => string.Equals(value, word, StringComparison.OrdinalIgnoreCase);

    private static bool Word<T>(string value, out T result) where T : struct, Enum =>
        Enum.TryParse(value, ignoreCase: true, out result) && Enum.IsDefined(result) && !int.TryParse(value, out _);

    private static bool Opt<T>(string value, T fallback, out T result) where T : struct, Enum
    {
        if (value.Length == 0)
        {
            result = fallback;
            return true;
        }

        return Word(value, out result);
    }
}
