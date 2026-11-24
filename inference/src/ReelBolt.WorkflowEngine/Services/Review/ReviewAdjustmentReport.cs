using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows.ChangeRequests;
using ReelBolt.Shared.Workflows.Review;

namespace ReelBolt.WorkflowEngine.Services.Review;

/// <summary>
/// How review-loop adjustments (docs/design/review-loop-adjustments.md) reach the reviewer and the record:
/// <list type="bullet">
/// <item><see cref="SettingsBlock"/>: the bounded <c>[ADJUSTABLE SETTINGS]</c> block appended to the reviewer's input,
/// listing each setting the reviewer may change this round with its current value and allowed range.</item>
/// <item><see cref="ReadProposals"/>: the reviewer's <c>adjustments</c> array, read from its verdict object.</item>
/// <item><see cref="AttachToOutput"/>: replaces that array with the engine's record (<c>from</c>, <c>to</c>, <c>status</c>,
/// <c>why</c>), adds <c>adjustmentRerun</c> and one run-page warning per applied change.</item>
/// </list>
/// </summary>
public static class ReviewAdjustmentReport
{
    public const string NodeName = "adjustments";
    public const string RerunNodeName = "adjustmentRerun";
    public const string AppliedWarningCode = "review_adjusted_setting";

    /// <summary>The run-page warning written when a paid re-run inside the loop was refused and the loop stopped.</summary>
    public const string RefusedWarningCode = "review_rerun_refused";

    /// <summary>The node (on a review that follows an adjusted re-run) recording what that re-run did and charged.</summary>
    public const string RerunChargesNodeName = "previousAdjustmentRerun";

    /// <summary>The longest settings block appended to the reviewer's input.</summary>
    public const int MaxBlockChars = 4500;

    /// <summary>How many items of a step the block names (with a few words of each).</summary>
    public const int MaxItemsShown = 12;

    private const string Header = "[ADJUSTABLE SETTINGS]";
    private const string Footer = "[END ADJUSTABLE SETTINGS]";

    /// <summary>The block for this round, or null when no step offers a setting the reviewer may change.</summary>
    public static string? SettingsBlock(ReviewAdjustmentRequest request)
    {
        IReadOnlyList<WorkflowStep> steps = ReviewAdjustmentPlanner.AdjustableSteps(request);
        if (steps.Count == 0)
            return null;

        StringBuilder block = new();
        block.Append(Header).Append('\n')
            .Append("Settings of earlier steps you may change for THIS run only (the user's saved workflow is never changed). ")
            .Append("When a problem you report is caused by one of them, add an entry to `adjustments`: ")
            .Append("{\"stepOrder\": <step>, \"setting\": \"<name>\", \"value\": <new value>, \"reason\": \"<one short sentence>\"}. ")
            .Append("Use only the names and ranges below; anything else is refused. The loop then re-runs from the earliest changed step.");
        if (steps.Any(ReviewAdjustmentPlanner.AcceptsChanges))
        {
            block.Append('\n')
                .Append("A step marked CHANGES takes the same changes a customer can ask for: add one `adjustments` entry per change with ")
                .Append("\"item\": \"<id below, or * for all>\", \"setting\": \"<change name>\" and \"value\" as text. ")
                .Append("Re-making such a step is a paid call charged to this run, so ask only for what fixes a problem you report.");
        }

        foreach (WorkflowStep step in steps)
        {
            if (ReviewAdjustmentPlanner.AcceptsChanges(step))
            {
                string changeSection = ChangeSection(request, step);
                if (block.Length + changeSection.Length + Footer.Length + 1 > MaxBlockChars)
                    break;
                block.Append(changeSection);
                continue;
            }

            StringBuilder section = new();
            section.Append('\n').Append("Step ").Append(step.StepOrder.ToString(CultureInfo.InvariantCulture))
                .Append(" (").Append(step.StepType).Append("):");
            string? json = ReviewAdjustableSettings.ConfigJson(step);
            foreach (ReviewAdjustableSetting setting in ReviewAdjustableSettings.For(step.StepType))
            {
                int? baseLength = ReviewAdjustmentPlanner.BaseLength(request, step, setting, json);
                string? range = ReviewAdjustableSettings.RangeText(setting, baseLength);
                if (range is null)
                    continue;
                section.Append('\n').Append("- ").Append(setting.Name).Append(" = ")
                    .Append(ReviewAdjustableSettings.Display(ReviewAdjustableSettings.ReadCurrent(setting, json)))
                    .Append(" (").Append(KindWord(setting.Kind)).Append(", ").Append(range).Append("): ").Append(setting.Description);
            }

            if (block.Length + section.Length + Footer.Length + 1 > MaxBlockChars)
                break;
            block.Append(section);
        }

        block.Append('\n').Append(Footer);
        return block.ToString();
    }

    /// <summary>The reviewer's proposals, read from its verdict object; empty when it made none or they cannot be read.</summary>
    public static IReadOnlyList<ReviewAdjustmentProposal> ReadProposals(string? reviewOutput, AgentType? agentType)
    {
        if (ReviewOutputNodes.Verdict(reviewOutput, agentType)?[NodeName] is not JsonArray items)
            return [];

        List<ReviewAdjustmentProposal> proposals = [];
        foreach (JsonNode? item in items)
        {
            if (item is not JsonObject entry)
                continue;
            int? stepOrder = entry["stepOrder"] is JsonValue o && o.TryGetValue(out double d) && double.IsFinite(d) && d == Math.Floor(d)
                && d is >= int.MinValue and <= int.MaxValue
                ? (int)d
                : null;
            string? setting = entry["setting"] is JsonValue s && s.TryGetValue(out string? name) ? name : null;
            string? reason = entry["reason"] is JsonValue r && r.TryGetValue(out string? why) ? why : null;
            string? itemName = entry["item"] is JsonValue it && it.TryGetValue(out string? itemId) && !string.IsNullOrWhiteSpace(itemId) ? itemId : null;
            proposals.Add(new ReviewAdjustmentProposal(stepOrder, setting, entry["value"]?.DeepClone(), reason, itemName));
            // Read one past the cap so the planner can say the rest were refused, never more.
            if (proposals.Count > ReviewAdjustableSettings.MaxPerRound)
                break;
        }

        return proposals;
    }

    /// <summary>
    /// The output with the engine's record in place of the reviewer's proposals. A plan with no decisions leaves
    /// the output exactly as it was, unless the reviewer wrote an <c>adjustmentRerun</c> node of its own, which is
    /// removed: only the engine says what a rerun reuses.
    /// </summary>
    public static string AttachToOutput(string reviewOutput, ReviewAdjustmentPlan plan, AgentType? agentType)
    {
        // Whatever record the reviewer's text carries is not the engine's: it is dropped, and the engine writes its own.
        string input = ReviewOutputNodes.WithoutRecord(reviewOutput);
        // The keys are found by parsing, never by searching the text: a JSON-escaped key reads the same once parsed.
        if (plan.Decisions.Count == 0 && !WritesEngineNode(input, agentType))
            return input;

        JsonObject? engineRecord = null;
        string written = ReviewOutputNodes.Mutate(input, agentType, target =>
        {
            target.Remove(RerunNodeName);
            target.Remove(RerunChargesNodeName);
            if (plan.Decisions.Count == 0)
                return;

            JsonArray records = [];
            foreach (ReviewAdjustmentDecision d in plan.Decisions)
            {
                JsonObject record = new()
                {
                    ["stepOrder"] = d.StepOrder,
                    ["setting"] = d.Setting,
                    ["from"] = d.From,
                    ["to"] = d.To,
                    ["reason"] = d.Reason,
                    ["status"] = d.Status,
                };
                if (d.Kind == ReviewAdjustmentDecision.ChangeKind)
                {
                    record["kind"] = d.Kind;
                    record["item"] = d.Item;
                }

                if (d.Why is not null)
                    record["why"] = d.Why;
                records.Add(record);
            }

            target[NodeName] = records;
            if (plan.HasChanges)
            {
                JsonArray reused = [];
                foreach (int order in plan.ReusedStepOrders)
                    reused.Add(order);
                JsonObject rerun = new()
                {
                    ["fromStepOrder"] = plan.RerunFromStepOrder,
                    ["reusedStepOrders"] = reused,
                    ["scope"] = "this run only",
                };
                if (plan.StepChanges.Count > 0)
                {
                    // The validated requests themselves, so a resumed run carries out the same changes.
                    JsonArray requests = [];
                    foreach (ReviewStepChange change in plan.StepChanges)
                    {
                        requests.Add(new JsonObject
                        {
                            ["stepOrder"] = change.StepOrder,
                            ["request"] = JsonNode.Parse(change.Payload.ToCanonicalJson()),
                        });
                    }

                    rerun["stepChanges"] = requests;
                }

                target[RerunNodeName] = rerun;
                engineRecord = new JsonObject { [RerunNodeName] = rerun.DeepClone() };
            }

            foreach (ReviewAdjustmentDecision d in plan.Decisions.Where(d => d.IsApplied))
            {
                string what = d.Kind == ReviewAdjustmentDecision.ChangeKind
                    ? $"The reviewer asked step {d.StepOrder} to re-make {(d.Item == ChangeRequestValidator.AllItems ? "every item" : d.Item)} with {d.Setting} set to \"{d.To}\" for this run only (a paid re-run, charged to this run)"
                    : $"The reviewer changed {d.Setting} on step {d.StepOrder} from {d.From} to {d.To} for this run only";
                ReviewOutputNodes.AddWarning(target, AppliedWarningCode,
                    what +
                    (d.Reason.Length > 0 ? $": {d.Reason}" : ".") +
                    " Your saved workflow is unchanged.");
            }
        });
        return engineRecord is null ? written : ReviewOutputNodes.WithRecord(written, engineRecord);
    }

    /// <summary>
    /// Whether a step failure inside a review loop's re-run is the platform refusing PAID work (the credit gate, a daily, global or
    /// step spend cap, the plan, the run rate) rather than the step breaking: such a refusal stops the loop and keeps the finished
    /// film instead of failing the run.
    /// </summary>
    public static bool IsRefusedPaidRerun(Exception? exception, string? failureCode) =>
        exception is Billing.CreditRefusalException
        || failureCode is "BUDGET_EXCEEDED" or "API_KEY_BUDGET_EXCEEDED" or "PLAN_NOT_ENTITLED"
            or "INSUFFICIENT_CREDITS" or "COMPUTE_NOT_AVAILABLE";

    /// <summary>
    /// The review output with <c>adjustmentRerun.refused</c> (the step, the refusal's code and message) and one run-page warning:
    /// why the loop stopped before finishing its re-run. The best pass is what the run keeps.
    /// </summary>
    public static string AttachRerunRefusal(
        string reviewOutput, AgentType? agentType, int stepOrder, string stepType, string code, string message) =>
        ReviewOutputNodes.Mutate(reviewOutput ?? string.Empty, agentType, target =>
        {
            JsonObject rerun = target[RerunNodeName] as JsonObject ?? [];
            rerun["refused"] = new JsonObject
            {
                ["stepOrder"] = stepOrder,
                ["stepType"] = stepType,
                ["code"] = code,
                ["message"] = message,
            };
            target[RerunNodeName] = rerun;
            ReviewOutputNodes.AddWarning(target, RefusedWarningCode,
                $"The reviewer's re-run stopped at step {stepOrder} ({stepType}): {code}. {message} The best finished version was kept.");
        });

    private static bool WritesEngineNode(string? reviewOutput, AgentType? agentType) =>
        ReviewOutputNodes.Verdict(reviewOutput, agentType) is { } verdict
        && (verdict.ContainsKey(RerunNodeName) || verdict.ContainsKey(RerunChargesNodeName));

    /// <summary>
    /// The recorded change requests a resumed run may carry out: each must target a step inside the loop window that takes
    /// changes and pass the same checks as a live proposal (the customer validator and the keys a review may never set).
    /// </summary>
    public static IReadOnlyList<ReviewStepChange> ValidRecordedChanges(
        IReadOnlyList<ReviewStepChange> recorded, IReadOnlyList<WorkflowStep> steps, int windowStart, int reviewStepOrder,
        Func<int, string?> latestOutputJson)
    {
        List<ReviewStepChange> valid = [];
        foreach (ReviewStepChange change in recorded)
        {
            WorkflowStep? step = steps.FirstOrDefault(s => s.StepOrder == change.StepOrder);
            if (step is null || change.StepOrder < windowStart || change.StepOrder >= reviewStepOrder
                || !ReviewAdjustmentPlanner.AcceptsChanges(step) || valid.Any(v => v.StepOrder == change.StepOrder))
                continue;
            if (change.Payload.Overrides?.Values.Any(o => o.Keys.Any(k => ReviewAdjustableSettings.ForbiddenChangeKeyReason(k) is not null)) == true)
                continue;
            if (ChangeRequestValidator.Validate(step.StepType, step.AgentDefinition?.AgentType, change.Payload, latestOutputJson(change.StepOrder)).Count > 0)
                continue;
            valid.Add(change);
        }

        return valid;
    }

    /// <summary>The steps a recorded rerun reuses (used when a resume replays a review round); empty when there is none.</summary>
    public static IReadOnlyList<int> ReadReusedStepOrders(string? reviewOutput, AgentType? agentType)
    {
        if (ReviewOutputNodes.Record(reviewOutput)?[RerunNodeName]?["reusedStepOrders"] is not JsonArray items)
            return [];
        List<int> orders = [];
        foreach (JsonNode? item in items)
        {
            if (item is JsonValue v && v.TryGetValue(out int order))
                orders.Add(order);
        }

        return orders;
    }

    /// <summary>The change requests a recorded rerun carries out (used when a resume replays a review round); empty when there are none.</summary>
    public static IReadOnlyList<ReviewStepChange> ReadStepChanges(string? reviewOutput, AgentType? agentType)
    {
        if (ReviewOutputNodes.Record(reviewOutput)?[RerunNodeName]?["stepChanges"] is not JsonArray items)
            return [];
        List<ReviewStepChange> changes = [];
        foreach (JsonNode? item in items)
        {
            if (item is JsonObject entry && entry["stepOrder"] is JsonValue o && o.TryGetValue(out int order)
                && entry["request"] is JsonObject request
                && ChangeRequestPayload.TryParse(request.ToJsonString()) is { } payload)
            {
                changes.Add(new ReviewStepChange(order, payload));
            }
        }

        return changes;
    }

    /// <summary>One step of an adjusted re-run as the next review records it.</summary>
    /// <param name="Outcome"><c>changed</c> (a change request carried out), <c>rerun</c>, <c>reused</c> (copied, free) or <c>stopped</c> (refused by the credit gate).</param>
    public sealed record RerunStep(int StepOrder, string StepType, string Outcome, long CreditsChargedMillis);

    /// <summary>
    /// The review output with <c>previousAdjustmentRerun</c>: what the re-run an earlier round asked for did, step by step,
    /// and what each re-run step was charged. Unchanged when there was no such re-run.
    /// </summary>
    public static string AttachRerunCharges(string reviewOutput, int? fromStepOrder, IReadOnlyList<RerunStep> steps, AgentType? agentType)
    {
        if (steps.Count == 0)
            return reviewOutput;
        return ReviewOutputNodes.Mutate(reviewOutput ?? string.Empty, agentType, target =>
        {
            JsonArray list = [];
            foreach (RerunStep step in steps)
            {
                list.Add(new JsonObject
                {
                    ["stepOrder"] = step.StepOrder,
                    ["stepType"] = step.StepType,
                    ["outcome"] = step.Outcome,
                    ["creditsCharged"] = Math.Round(step.CreditsChargedMillis / 1000.0, 3),
                });
            }

            target[RerunChargesNodeName] = new JsonObject
            {
                ["fromStepOrder"] = fromStepOrder,
                ["steps"] = list,
                ["creditsCharged"] = Math.Round(steps.Sum(s => s.CreditsChargedMillis) / 1000.0, 3),
            };
        });
    }

    /// <summary>
    /// A step that accepts a change request, as the reviewer sees it: what its items are (a few words of each) and which
    /// changes it takes, with the customer catalogue's limits. Prompts are never offered.
    /// </summary>
    private static string ChangeSection(ReviewAdjustmentRequest request, WorkflowStep step)
    {
        AgentType? agent = step.AgentDefinition?.AgentType;
        StepChangeCapability capability = StepChangeCapabilities.For(step.StepType, agent);
        StringBuilder section = new();
        section.Append('\n').Append("Step ").Append(step.StepOrder.ToString(CultureInfo.InvariantCulture))
            .Append(" (").Append(step.StepType == StepType.Agent && agent is { } a ? a.ToString() : step.StepType.ToString())
            .Append("), CHANGES");
        IReadOnlyList<StepItem> items = StepChangeCapabilities.ListItems(step.StepType, agent, request.LatestOutputJson?.Invoke(step.StepOrder));
        if (capability.ItemKind != StepItemKind.None)
        {
            section.Append(" by ").Append(capability.ItemKind.ToString().ToLowerInvariant()).Append(": ");
            if (items.Count == 0)
            {
                section.Append("item ids unknown (use * for all)");
            }
            else
            {
                section.Append(string.Join(", ", items.Take(MaxItemsShown).Select(i =>
                    i.Text is { Length: > 0 } text ? $"{i.Id} \"{Shorten(text, 40)}\"" : i.Id)));
                if (items.Count > MaxItemsShown)
                    section.Append(", ... (").Append(items.Count.ToString(CultureInfo.InvariantCulture)).Append(" in all)");
            }
        }

        section.Append(':');
        foreach (ChangeOverrideSpec spec in capability.Overrides.Where(o => ReviewAdjustableSettings.ForbiddenChangeKeyReason(o.Key) is null))
            section.Append('\n').Append("- ").Append(spec.Key).Append(" (").Append(SpecWords(spec)).Append(')');
        if (capability.AcceptsInstructionWithItems)
        {
            section.Append('\n').Append("- ").Append(ReviewAdjustmentPlanner.InstructionSetting).Append(" (text up to ")
                .Append(StepChangeCapabilities.MaxInstructionLength.ToString(CultureInfo.InvariantCulture))
                .Append(" characters: what to rewrite in the named item; one per step)");
        }

        return section.ToString();
    }

    private static string SpecWords(ChangeOverrideSpec spec) => spec.Type switch
    {
        ChangeOverrideType.Number => string.Create(CultureInfo.InvariantCulture, $"number, {spec.Min}..{spec.Max}"),
        ChangeOverrideType.Flag => "true to re-make it",
        ChangeOverrideType.Enum => "one of " + string.Join(" | ", spec.Values ?? []),
        ChangeOverrideType.PronunciationList => string.Create(CultureInfo.InvariantCulture,
            $"term=spelling; term=spelling, up to {spec.MaxEntries} (a written accent, e.g. Novara=Novàra)"),
        _ => spec.Key == "voiceId"
            ? "a voice id of this project; refused unless its consent is in order"
            : spec.MaxLength is int max ? string.Create(CultureInfo.InvariantCulture, $"text up to {max} characters") : "text"
    };

    private static string Shorten(string text, int max)
    {
        string t = ReviewAdjustmentPlanner.SanitizeReason(text);
        return t.Length <= max ? t : t[..max] + "...";
    }

    private static string KindWord(ReviewAdjustableValueKind kind) => kind switch
    {
        ReviewAdjustableValueKind.Integer => "whole number",
        ReviewAdjustableValueKind.Number => "number",
        _ => "one of"
    };
}
