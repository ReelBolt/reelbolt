using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows.ChangeRequests;
using ReelBolt.Shared.Workflows.Replay;

namespace ReelBolt.Shared.Workflows.Review;

/// <summary>
/// One change a reviewer asked for, as read from its answer (every field untrusted). With no <paramref name="Item"/> on a
/// step whose settings are on the allowlist it is a plain setting change; on a step that accepts a change request
/// (<see cref="StepChangeCapabilities"/>) <paramref name="Setting"/> is one key of that step's override
/// catalogue (or <c>instruction</c>) and <paramref name="Item"/> the line, shot, take or scene it is about.
/// </summary>
public sealed record ReviewAdjustmentProposal(int? StepOrder, string? Setting, JsonNode? Value, string? Reason, string? Item = null);

/// <summary>What became of one proposal: applied (with from/to) or refused (with why).</summary>
public sealed record ReviewAdjustmentDecision(
    int? StepOrder,
    string? Setting,
    string? From,
    string? To,
    string Reason,
    string Status,
    string? Why)
{
    public const string Applied = "applied";
    public const string Refused = "refused";

    /// <summary>A plain setting of the step's config (<see cref="ReviewAdjustableSettings"/>).</summary>
    public const string SettingKind = "setting";

    /// <summary>One entry of a step change request applied inside this run (the customer change-request catalogue).</summary>
    public const string ChangeKind = "change";

    public bool IsApplied => Status == Applied;

    /// <summary><see cref="SettingKind"/> or <see cref="ChangeKind"/>.</summary>
    public string Kind { get; init; } = SettingKind;

    /// <summary>The line, shot, take or scene a change is about (<c>*</c> = all); null for a plain setting.</summary>
    public string? Item { get; init; }
}

/// <summary>
/// A step change request the review asked for, applied INSIDE this run on the step's next dispatch
/// (docs/design/review-loop-adjustments.md "Change requests inside the run"). <paramref name="Payload"/> passed the same
/// <see cref="ChangeRequestValidator"/> a customer request passes.
/// </summary>
public sealed record ReviewStepChange(int StepOrder, ChangeRequestPayload Payload);

/// <summary>The new config JSON of one adjusted step of the run's snapshot.</summary>
public sealed record ReviewAdjustmentChange(int StepOrder, string ConfigJson);

/// <summary>
/// The outcome of one review round's adjustments: every decision, the configs to write to the run's snapshot,
/// the earliest step the loop must now re-run from (null when no applied change sits before the loop target), and
/// the steps between that point and the loop target whose earlier result is reused instead of being re-run.
/// </summary>
public sealed record ReviewAdjustmentPlan(
    IReadOnlyList<ReviewAdjustmentDecision> Decisions,
    IReadOnlyList<ReviewAdjustmentChange> Changes,
    int? RerunFromStepOrder,
    IReadOnlyList<int> ReusedStepOrders)
{
    public static ReviewAdjustmentPlan Empty { get; } = new([], [], null, []);

    /// <summary>The step change requests to carry out on the steps' next dispatch in this run.</summary>
    public IReadOnlyList<ReviewStepChange> StepChanges { get; init; } = [];

    public bool HasChanges => Changes.Count > 0 || StepChanges.Count > 0;
}

/// <summary>Everything the planner reads, so it stays a pure function.</summary>
/// <param name="Steps">The run's steps (its snapshot), in order, with their current configs.</param>
/// <param name="ReviewStep">The ReviewLoop step whose reviewer made the proposals.</param>
/// <param name="RunHasOwnSnapshot">True only for a run executing its engine-taken snapshot: nothing else is ever changed.</param>
/// <param name="LoopsBack">True when this round sends the loop round again; otherwise nothing is applied.</param>
/// <param name="BriefLengthSec">The run brief's length, the first base of a target-length change.</param>
/// <param name="SavedTargetDurationSec">The target length the user saved for a step (by step order), the second base.</param>
/// <param name="LatestOutputJson">A step's latest output in this run (by step order): the items a change request names are read from it.</param>
public sealed record ReviewAdjustmentRequest(
    IReadOnlyList<WorkflowStep> Steps,
    WorkflowStep ReviewStep,
    bool RunHasOwnSnapshot,
    bool LoopsBack,
    int? BriefLengthSec = null,
    Func<int, int?>? SavedTargetDurationSec = null,
    Func<int, string?>? LatestOutputJson = null);

/// <summary>
/// Turns a reviewer's proposals into a plan (docs/design/review-loop-adjustments.md). Pure: it writes nothing.
///
/// <para>
/// <b>Two kinds of change.</b> A plain setting on the closed allowlist (<see cref="ReviewAdjustableSettings"/>) is written
/// to the step's config in the run's snapshot. On a step that accepts a customer change request
/// (<see cref="StepChangeCapabilities"/>: a Voiceover line, a VideoGenerate shot or take, an AudioGenerate take, a
/// ThumbnailGenerate step, a script scene) the proposals for that step are gathered into ONE
/// <see cref="ChangeRequestPayload"/> that must pass the very validator a customer request passes
/// (<see cref="ChangeRequestValidator"/>: same catalogue, same limits); the step's executor then applies it on its next
/// dispatch in this run, exactly as it applies a customer request (consent-guarded fields such as a voice stay guarded
/// there). A review never writes a prompt (<see cref="ReviewAdjustableSettings.ForbiddenChangeKeyReason"/>), consent,
/// a provider, a model or a spend cap.
/// </para>
/// <para>
/// <b>Re-entry rule.</b> A change to a step at or after the loop target takes effect on the loop's next round. A change to
/// a step BEFORE the loop target widens the round back to that step; every step between it and the loop target that
/// depends on a changed step re-runs too (paid ones included: each re-run takes its own hold and is charged like any loop
/// pass), and the steps in that stretch that do not depend on a change are reused (their earlier result copied, never
/// re-run, never charged).
/// </para>
/// </summary>
public static class ReviewAdjustmentPlanner
{
    /// <summary>The pseudo-setting a reviewer uses for a change request's free instruction (a script scene).</summary>
    public const string InstructionSetting = "instruction";

    private sealed record ChangeEntry(int Index, string Item, string Key, JsonElement Value, string Display, string Reason);

    public static ReviewAdjustmentPlan Plan(ReviewAdjustmentRequest request, IReadOnlyList<ReviewAdjustmentProposal> proposals)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (proposals is null || proposals.Count == 0)
            return ReviewAdjustmentPlan.Empty;

        WorkflowStepDependencyGraph graph = WorkflowStepDependencyGraph.Build(request.Steps);
        Dictionary<int, string> working = [];
        HashSet<(int, string)> changed = [];
        ReviewAdjustmentDecision?[] decisions = new ReviewAdjustmentDecision?[proposals.Count];
        // Change-request entries, gathered per step and judged together once every proposal was read.
        Dictionary<int, List<ChangeEntry>> changeEntries = [];

        for (int i = 0; i < proposals.Count; i++)
        {
            ReviewAdjustmentProposal p = proposals[i];
            string reason = SanitizeReason(p.Reason);
            string? item = Clip(p.Item, 64);
            ReviewAdjustmentDecision Refuse(string why, string? from = null, string? to = null) =>
                new(p.StepOrder, Clip(p.Setting, 64), from, to, reason, ReviewAdjustmentDecision.Refused, why) { Item = item };

            if (i >= ReviewAdjustableSettings.MaxPerRound)
            {
                decisions[i] = Refuse($"more than {ReviewAdjustableSettings.MaxPerRound} changes in one round");
                continue;
            }

            if (!request.RunHasOwnSnapshot)
            {
                decisions[i] = Refuse("this run has no copy of its own workflow to change");
                continue;
            }

            if (!request.LoopsBack)
            {
                decisions[i] = Refuse("the review loop is not going round again, so nothing was changed");
                continue;
            }

            WorkflowStep? step = p.StepOrder is { } order ? request.Steps.FirstOrDefault(s => s.StepOrder == order) : null;
            if (step is null)
            {
                decisions[i] = Refuse("there is no such step in this workflow");
                continue;
            }

            if (step.StepOrder >= request.ReviewStep.StepOrder)
            {
                decisions[i] = Refuse("that step runs after the review");
                continue;
            }

            StepChangeCapability capability = StepChangeCapabilities.For(step.StepType, step.AgentDefinition?.AgentType);
            if (capability.Supported)
            {
                decisions[i] = ReadChangeEntry(p, i, step, capability, reason, item, changed, changeEntries);
                continue;
            }

            if (ReviewAdjustableSettings.ForbiddenReason(p.Setting) is { } forbidden)
            {
                decisions[i] = Refuse("never adjustable by a review: " + forbidden);
                continue;
            }

            if (ReviewAdjustableSettings.NeverAdjustableStepTypes.Contains(step.StepType))
            {
                decisions[i] = Refuse($"a {step.StepType} step's settings are never changed by a review");
                continue;
            }

            ReviewAdjustableSetting? setting = ReviewAdjustableSettings.Find(step.StepType, p.Setting);
            if (setting is null)
            {
                decisions[i] = Refuse($"not on the list of {step.StepType} settings a review may change");
                continue;
            }

            string? currentJson = working.TryGetValue(step.StepOrder, out string? w) ? w : ReviewAdjustableSettings.ConfigJson(step);
            JsonNode? current = ReviewAdjustableSettings.ReadCurrent(setting, currentJson);
            string from = ReviewAdjustableSettings.Display(current);
            int? baseLength = BaseLength(request, step, setting, currentJson);
            ReviewAdjustmentValue parsed = ReviewAdjustableSettings.Parse(setting, p.Value, baseLength);
            if (parsed.Refused is { } badValue)
            {
                decisions[i] = Refuse(badValue, from) with { Setting = setting.Name };
                continue;
            }

            string to = ReviewAdjustableSettings.Display(parsed.Value);
            if (!changed.Add((step.StepOrder, setting.Name)))
            {
                decisions[i] = Refuse("the same setting was changed twice in one round", from, to) with { Setting = setting.Name };
                continue;
            }

            if (JsonNode.DeepEquals(current, parsed.Value))
            {
                changed.Remove((step.StepOrder, setting.Name));
                decisions[i] = Refuse("it already has that value", from, to) with { Setting = setting.Name };
                continue;
            }

            string? newJson = ReviewAdjustableSettings.Apply(setting, currentJson, parsed.Value!);
            if (newJson is null)
            {
                changed.Remove((step.StepOrder, setting.Name));
                decisions[i] = Refuse("the step's settings would not be valid with that value", from, to) with { Setting = setting.Name };
                continue;
            }

            working[step.StepOrder] = newJson;
            decisions[i] = new ReviewAdjustmentDecision(
                step.StepOrder, setting.Name, from, to, reason, ReviewAdjustmentDecision.Applied, null);
        }

        // Each step's change-request entries become one payload, judged as a whole by the customer validator.
        List<ReviewStepChange> stepChanges = [];
        foreach ((int stepOrder, List<ChangeEntry> entries) in changeEntries.OrderBy(kv => kv.Key))
        {
            WorkflowStep step = request.Steps.First(s => s.StepOrder == stepOrder);
            ChangeRequestPayload payload = BuildPayload(entries.Select(e => (e.Item, e.Key, e.Value)));
            IReadOnlyList<ChangeRequestViolation> violations = ChangeRequestValidator.Validate(
                step.StepType, step.AgentDefinition?.AgentType, payload, request.LatestOutputJson?.Invoke(stepOrder));
            string? why = violations.Count == 0
                ? null
                : "the change is not valid: " + string.Join("; ", violations.Take(3).Select(v => string.IsNullOrEmpty(v.Path) ? v.Message : $"{v.Path}: {v.Message}"));
            foreach (ChangeEntry e in entries)
            {
                decisions[e.Index] = new ReviewAdjustmentDecision(
                    stepOrder, e.Key, null, e.Display, e.Reason,
                    why is null ? ReviewAdjustmentDecision.Applied : ReviewAdjustmentDecision.Refused, why)
                {
                    Kind = ReviewAdjustmentDecision.ChangeKind,
                    Item = e.Item,
                };
            }

            if (why is null)
                stepChanges.Add(new ReviewStepChange(stepOrder, payload));
        }

        List<ReviewAdjustmentChange> changes = [.. working.OrderBy(kv => kv.Key).Select(kv => new ReviewAdjustmentChange(kv.Key, kv.Value))];
        List<ReviewAdjustmentDecision> recorded = [.. decisions.Select(d => d!)];
        int loopTarget = request.ReviewStep.LoopTargetStepOrder ?? request.ReviewStep.StepOrder;
        List<int> upstream = [.. changes.Select(c => c.StepOrder).Concat(stepChanges.Select(c => c.StepOrder)).Where(o => o < loopTarget).Distinct()];
        if (upstream.Count == 0)
            return new ReviewAdjustmentPlan(recorded, changes, null, []) { StepChanges = stepChanges };

        int rerunFrom = upstream.Min();
        HashSet<int> affected = [.. upstream];
        foreach (int order in upstream)
            affected.UnionWith(graph.DownstreamOf(order));
        List<int> reused = [.. request.Steps
            .Where(s => s.StepOrder >= rerunFrom && s.StepOrder < loopTarget && !affected.Contains(s.StepOrder))
            .Select(s => s.StepOrder)
            .OrderBy(o => o)];
        return new ReviewAdjustmentPlan(recorded, changes, rerunFrom, reused) { StepChanges = stepChanges };
    }

    /// <summary>
    /// One proposal on a step that accepts a change request: refused at once (with the reason) or gathered into the step's
    /// entries (null is returned and the decision is made when the step's whole payload is validated).
    /// </summary>
    private static ReviewAdjustmentDecision? ReadChangeEntry(
        ReviewAdjustmentProposal p, int index, WorkflowStep step, StepChangeCapability capability, string reason, string? item,
        HashSet<(int, string)> changed, Dictionary<int, List<ChangeEntry>> changeEntries)
    {
        string key = (p.Setting ?? string.Empty).Trim();
        ReviewAdjustmentDecision Refuse(string why, string? setting = null) =>
            new(p.StepOrder, setting ?? Clip(p.Setting, 64), null, null, reason, ReviewAdjustmentDecision.Refused, why)
            {
                Kind = ReviewAdjustmentDecision.ChangeKind,
                Item = item,
            };

        if (ReviewAdjustableSettings.ForbiddenChangeKeyReason(key) is { } forbiddenKey)
            return Refuse("never adjustable by a review: " + forbiddenKey);

        bool isInstruction = string.Equals(key, InstructionSetting, StringComparison.OrdinalIgnoreCase);
        ChangeOverrideSpec? spec = isInstruction
            ? null
            : capability.Overrides.FirstOrDefault(o => string.Equals(o.Key, key, StringComparison.OrdinalIgnoreCase));
        if (isInstruction ? !capability.AcceptsInstructionWithItems : spec is null)
            return Refuse($"a change to a {StepLabel(step)} step can set only {string.Join(", ", ChangeKeys(capability))}");

        string canonicalKey = isInstruction ? InstructionSetting : spec!.Key;
        string? target = item ?? (isInstruction ? null : ChangeRequestValidator.AllItems);
        if (target is null)
            return Refuse("an instruction must name the scene it is about in `item`", canonicalKey);

        if (!changed.Add((step.StepOrder, "change:" + target + ":" + canonicalKey)))
            return Refuse("the same change was asked twice in one round", canonicalKey);

        if (!changeEntries.TryGetValue(step.StepOrder, out List<ChangeEntry>? entries))
            changeEntries[step.StepOrder] = entries = [];
        if (isInstruction && entries.Any(e => e.Key == InstructionSetting))
            return Refuse("one instruction per step per round", canonicalKey);

        JsonElement value = ChangeValue(spec, p.Value);
        entries.Add(new ChangeEntry(index, target, canonicalKey, value, DisplayChange(value), reason));
        return null;
    }

    /// <summary>
    /// The steps (before the review) the reviewer may change this round, in order: those with allowlisted settings and
    /// those that accept a change request. Any step may be re-run (paid ones included); nothing else limits the offer.
    /// </summary>
    public static IReadOnlyList<WorkflowStep> AdjustableSteps(ReviewAdjustmentRequest request) =>
        [.. request.Steps.Where(s =>
            s.StepOrder < request.ReviewStep.StepOrder
            && (ReviewAdjustableSettings.For(s.StepType).Count > 0 || AcceptsChanges(s)))];

    /// <summary>True when <paramref name="step"/> accepts a change request (and so a review's change entries).</summary>
    public static bool AcceptsChanges(WorkflowStep step) =>
        StepChangeCapabilities.For(step.StepType, step.AgentDefinition?.AgentType).Supported;

    /// <summary>The keys a review may set on a step of <paramref name="capability"/>: its catalogue minus prompts, plus <c>instruction</c> when taken.</summary>
    public static IReadOnlyList<string> ChangeKeys(StepChangeCapability capability) =>
        [.. capability.Overrides.Where(o => ReviewAdjustableSettings.ForbiddenChangeKeyReason(o.Key) is null).Select(o => o.Key)
            .Concat(capability.AcceptsInstructionWithItems ? [InstructionSetting] : [])];

    /// <summary>A change request payload from (item, key, value) entries: <c>instruction</c> entries set the instruction and name their item.</summary>
    public static ChangeRequestPayload BuildPayload(IEnumerable<(string Item, string Key, JsonElement Value)> entries)
    {
        string? instruction = null;
        List<string> itemIds = [];
        Dictionary<string, Dictionary<string, JsonElement>> overrides = new(StringComparer.Ordinal);
        foreach ((string item, string key, JsonElement value) in entries)
        {
            if (key == InstructionSetting)
            {
                instruction = value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
                if (!itemIds.Contains(item, StringComparer.Ordinal))
                    itemIds.Add(item);
                continue;
            }

            if (!overrides.TryGetValue(item, out Dictionary<string, JsonElement>? keys))
                overrides[item] = keys = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            keys[key] = value;
        }

        return new ChangeRequestPayload(
            instruction,
            itemIds.Count == 0 ? null : itemIds,
            overrides.Count == 0 ? null : overrides.ToDictionary(kv => kv.Key, kv => (IReadOnlyDictionary<string, JsonElement>)kv.Value));
    }

    /// <summary>
    /// The reviewer's value as the JSON the catalogue entry takes (the reviewer writes every value as text): a number, a
    /// flag, a pronunciation list (a JSON array, or <c>term=spelling; term=spelling</c>), else the text itself. A value that
    /// cannot be converted is passed on as text, so the validator names what is wrong with it.
    /// </summary>
    public static JsonElement ChangeValue(ChangeOverrideSpec? spec, JsonNode? raw)
    {
        string? text = raw switch
        {
            null => null,
            JsonValue v when v.TryGetValue(out string? s) => s,
            _ => raw.ToJsonString()
        };
        JsonElement AsText() => JsonSerializer.SerializeToElement(text ?? string.Empty);
        if (spec is null || text is null)
            return AsText();

        string t = text.Trim();
        switch (spec.Type)
        {
            case ChangeOverrideType.Number:
                return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && double.IsFinite(d)
                    ? JsonSerializer.SerializeToElement(d)
                    : AsText();
            case ChangeOverrideType.Flag:
                return t.ToLowerInvariant() switch
                {
                    "true" or "yes" or "1" => JsonSerializer.SerializeToElement(true),
                    "false" or "no" or "0" => JsonSerializer.SerializeToElement(false),
                    _ => AsText()
                };
            case ChangeOverrideType.PronunciationList:
                if (t.StartsWith('['))
                {
                    try
                    {
                        return JsonSerializer.Deserialize<JsonElement>(t);
                    }
                    catch (JsonException)
                    {
                        return AsText();
                    }
                }

                List<Dictionary<string, string>> pairs = [];
                foreach (string part in t.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    int eq = part.IndexOf('=', StringComparison.Ordinal);
                    if (eq <= 0)
                        return AsText();
                    pairs.Add(new Dictionary<string, string> { ["term"] = part[..eq].Trim(), ["spelling"] = part[(eq + 1)..].Trim() });
                }

                return JsonSerializer.SerializeToElement(pairs);
            default:
                return AsText();
        }
    }

    private static string DisplayChange(JsonElement value)
    {
        string shown = SanitizeReason(value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText());
        return shown.Length > 120 ? shown[..120] + "..." : shown;
    }

    private static string StepLabel(WorkflowStep step) =>
        step.StepType == StepType.Agent && step.AgentDefinition is { } agent ? agent.AgentType.ToString() : step.StepType.ToString();

    /// <summary>The base length a target-length change moves around: the brief, else the saved value, else the current value.</summary>
    public static int? BaseLength(ReviewAdjustmentRequest request, WorkflowStep step, ReviewAdjustableSetting setting, string? currentJson)
    {
        if (setting.Bound != ReviewAdjustableBound.AroundTargetLength)
            return null;
        if (request.BriefLengthSec is > 0)
            return request.BriefLengthSec;
        if (request.SavedTargetDurationSec?.Invoke(step.StepOrder) is > 0 and var saved)
            return saved;
        return ReviewAdjustableSettings.ReadCurrent(setting, currentJson) is JsonValue v && v.TryGetValue(out int current) && current > 0
            ? current
            : null;
    }

    /// <summary>The reviewer's reason as kept on the record: control characters removed, whitespace collapsed, capped.</summary>
    public static string SanitizeReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return string.Empty;
        StringBuilder sb = new(Math.Min(reason.Length, ReviewAdjustableSettings.MaxReasonChars));
        bool space = false;
        foreach (char c in reason)
        {
            if (char.IsWhiteSpace(c))
            {
                space = sb.Length > 0;
                continue;
            }

            if (char.IsControl(c) || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format)
                continue;

            if (space)
                sb.Append(' ');
            space = false;
            sb.Append(c);
            if (sb.Length >= ReviewAdjustableSettings.MaxReasonChars)
                break;
        }

        return sb.ToString();
    }

    private static string? Clip(string? value, int max) =>
        value is null ? null : SanitizeReason(value) is { Length: > 0 } s ? (s.Length > max ? s[..max] : s) : null;
}
