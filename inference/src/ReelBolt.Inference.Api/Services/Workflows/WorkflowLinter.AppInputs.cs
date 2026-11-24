using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Workflows.Apps;
using ReelBolt.Shared.Workflows.Lint;

namespace ReelBolt.Inference.Api.Services.Workflows;

/// <summary>
/// WL033's seam: what one generation step would hold with its config as given (the dearest values its app inputs allow),
/// in credits, or null when it cannot be priced. The registered implementation prices exactly as the estimate does
/// (<see cref="VideoGenerateCreditPricing"/>, <see cref="AudioGenerateCreditPricing"/>); none registered = no WL033.
/// </summary>
public interface IAppInputSpendPricer
{
    /// <summary>The step's hold in credits, or null.</summary>
    Task<double?> CreditsAsync(WorkflowStep step, CancellationToken ct);
}

/// <summary>The real WL033 pricer: the estimate's own per-step pricing, from the request scope's services.</summary>
public sealed class AppInputSpendPricer(
    IInferenceProviderResolver providers,
    IVideoGenerationClientFactory clients,
    IModelPriceService prices) : IAppInputSpendPricer
{
    /// <inheritdoc />
    public async Task<double?> CreditsAsync(WorkflowStep step, CancellationToken ct)
    {
        long? millis = step.StepType switch
        {
            StepType.VideoGenerate => await VideoGenerateCreditPricing.TryHoldMillisAsync(step.VideoGenerateConfigJson, providers, clients, prices, ct),
            StepType.AudioGenerate => await AudioGenerateCreditPricing.TryHoldMillisAsync(step.AudioGenerateConfigJson, providers, prices, ct),
            _ => null
        };
        return millis / 1000.0;
    }
}

/// <summary>
/// The app-input rules WL028 to WL033 (docs/design/app-inputs.md 11.2), all over the same pure pieces the run gate uses
/// (<see cref="AppInputValueChecker"/>, <see cref="AppInputValueValidator.Evaluate"/>, <see cref="StaticBranchResolver"/>),
/// so the linter cannot say a run is fine that the gate then refuses. They run only for an app that uses inputs. With a brief, the values are the run's;
/// without one (the author's view), the defaults.
/// </summary>
public sealed partial class WorkflowLinter
{
    /// <summary>
    /// The app-input findings, and (with a brief of an app that uses inputs, whose values check out) the steps the run reaches
    /// with those values: the caller drops the "permission to send media" finding (WL015) of a step the run never reaches.
    /// </summary>
    private async Task<(IEnumerable<LintFinding> Findings, IReadOnlySet<int>? Reach)> AppInputChecksAsync(WorkflowLintRequest request, List<WorkflowStep> steps, CancellationToken ct)
    {
        AppManifest? manifest;
        try
        {
            manifest = await _manifestOrphans.ManifestAsync(request, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ([], null);
        }

        IReadOnlyList<AppBindingStep> bindingSteps = [.. steps.Select(AppBindingStep.From)];
        if (manifest is null || !AppInputValueValidator.UsesAppInputs(manifest, bindingSteps))
            return ([], null);

        // The permission to send media is one of the page's fields (derived, never stored): its value is checked like
        // the others, as the run gate checks it.
        MediaEgressGate egressGate = new(request.Brief?.Avatar?.AvatarId is not null, null);
        manifest = MediaEgressPermission.WithField(
            manifest, bindingSteps, egressGate.AvatarChosen ? MediaEgressBriefAvatar.Chosen : MediaEgressBriefAvatar.Offered) ?? manifest;

        List<LintFinding> findings = [];
        IReadOnlyDictionary<string, JsonElement>? raw = request.Brief?.Inputs;
        bool hasBrief = request.Brief is not null;
        AppInputCheckResult check = AppInputValueChecker.Check(manifest, bindingSteps, raw);

        if (hasBrief)
        {
            foreach (AppInputValueProblem problem in check.Problems)
                findings.Add(ValueFinding(manifest, problem.Code, problem.InputId, problem.Message, isDefault: false));
        }

        if (!hasBrief || check.Problems.Count == 0)
        {
            IReadOnlyList<AppModelCandidate>? candidates = null;
            if (models is not null)
            {
                try
                {
                    candidates = await models.CandidatesAsync(request.OrganizationId, request.ProjectId, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    candidates = null;
                }
            }

            AppInputGateResult gate = AppInputValueValidator.Evaluate(manifest, steps, raw, null, forEstimate: true, candidates: candidates, mediaEgress: egressGate);
            if (gate.Refused)
                findings.Add(ValueFinding(manifest, gate.Code!, gate.InputId, gate.Message!, isDefault: !hasBrief));
        }

        findings.AddRange(BranchFindings(manifest, bindingSteps, check.Values));
        findings.AddRange(SharedAgentFindings(manifest, bindingSteps));
        findings.AddRange(await SpendCapFindingsAsync(manifest, steps, ct));
        IReadOnlySet<int>? reach = hasBrief && check.Problems.Count == 0 ? StaticBranchResolver.ReachableStepOrders(bindingSteps, check.Values) : null;
        return (findings, reach);
    }

    /// <summary>WL028 (missing), WL029 (does not fit) or WL030 (model), for one refused value.</summary>
    private static LintFinding ValueFinding(AppManifest manifest, string code, string? inputId, string message, bool isDefault)
    {
        string input = InputLabel(manifest, inputId);
        if (code == AppInputValueProblem.Missing)
        {
            StepCompatibilityRule missing = StepCompatibilityRules.Get("WL028");
            return new LintFinding(missing.Id, missing.Severity, null,
                new LintMessage(missing.Format(("input", input)).Text, LintArgs.Of(("input", input), ("inputId", inputId))),
                new LintFix(LintFixKind.FillInput, $"Fill in “{input}”", LabelId: "WL028.fillInput", Args: LintArgs.Of(("input", input))));
        }

        if (code == AppInputValueValidator.ModelUnavailableCode)
        {
            StepCompatibilityRule model = StepCompatibilityRules.Get("WL030");
            // The author's view (no run values yet): the default or recommended model is a warning, a chosen one an error.
            return new LintFinding(model.Id, isDefault ? LintSeverity.Warning : model.Severity, null,
                new LintMessage(model.Format(("input", input)).Text, LintArgs.Of(("input", input), ("inputId", inputId))),
                new LintFix(LintFixKind.ChooseModel, "Choose another model", LabelId: "WL030.chooseModel"),
                messageKey: isDefault ? "WL030.recommended" : null);
        }

        // The validator's own sentence goes into the English text; the args stay tokens.
        StepCompatibilityRule unfit = StepCompatibilityRules.Get("WL029");
        return new LintFinding(unfit.Id, unfit.Severity, StepOrderOf(StripLabel(message)),
            new LintMessage($"{unfit.Format(("input", input)).Text} {message}", LintArgs.Of(("input", input), ("inputId", inputId))),
            new LintFix(LintFixKind.FillInput, $"Change “{input}”", LabelId: "WL029.changeInput", Args: LintArgs.Of(("input", input))));
    }

    /// <summary>WL031: one info per input-decided Conditional whose chosen branch leaves steps out.</summary>
    private static IEnumerable<LintFinding> BranchFindings(
        AppManifest manifest, IReadOnlyList<AppBindingStep> steps, IReadOnlyDictionary<string, JsonElement> values)
    {
        StepCompatibilityRule rule = StepCompatibilityRules.Get("WL031");
        IReadOnlySet<int> reachable = StaticBranchResolver.ReachableStepOrders(steps, values);
        foreach (AppBindingStep step in steps.Where(s => s.StepType == StepType.Conditional))
        {
            IReadOnlyList<string> ids = StaticBranchResolver.InputIds(step.ConditionExpression);
            if (ids.Count == 0 || !StaticBranchResolver.IsInputDecided(step.ConditionExpression)
                || StaticBranchResolver.Decide(step.ConditionExpression, values) is null
                || !reachable.Contains(step.StepOrder))
                continue;

            // What this condition alone leaves out: the reach with its inputs unknown, minus the reach with them known.
            Dictionary<string, JsonElement> without = values.Where(v => !ids.Contains(v.Key)).ToDictionary(v => v.Key, v => v.Value, StringComparer.Ordinal);
            List<int> skipped = [.. StaticBranchResolver.ReachableStepOrders(steps, without).Where(o => !reachable.Contains(o)).Order()];
            if (skipped.Count == 0)
                continue;

            string id = ids[0];
            AppManifestInput? input = InputOf(manifest, id);
            string label = InputLabel(manifest, id);
            string value = input is null || !values.TryGetValue(id, out JsonElement v) ? string.Empty : ValueToken(input, v);
            string count = skipped.Count.ToString(CultureInfo.InvariantCulture);
            yield return new LintFinding(rule.Id, rule.Severity, step.StepOrder,
                new LintMessage(
                    rule.Format(("input", label), ("value", value), ("count", count)).Text,
                    LintArgs.Of([("input", label), ("inputId", id), ("value", value), ("count", count), .. LintArgs.Indexed("skipped", [.. skipped.Select(o => o.ToString(CultureInfo.InvariantCulture))])])));
        }
    }

    /// <summary>WL032: a model input on an agent that more than one step uses.</summary>
    private static IEnumerable<LintFinding> SharedAgentFindings(AppManifest manifest, IReadOnlyList<AppBindingStep> steps)
    {
        StepCompatibilityRule rule = StepCompatibilityRules.Get("WL032");
        AppBindingMap map = AppBindingAnalysis.Analyze(manifest, steps);
        foreach (IGrouping<string, AppInputUse> uses in map.Uses.Where(u => u.Kind == AppInputUseKind.AgentModel).GroupBy(u => u.InputId))
        {
            int count = uses.Select(u => u.StepOrder).Distinct().Count();
            if (count < 2)
                continue;
            string label = InputLabel(manifest, uses.Key);
            string steps2 = count.ToString(CultureInfo.InvariantCulture);
            yield return new LintFinding(rule.Id, rule.Severity, null,
                new LintMessage(rule.Format(("input", label), ("count", steps2)).Text, LintArgs.Of(("input", label), ("inputId", uses.Key), ("count", steps2))));
        }
    }

    /// <summary>
    /// WL033: a VideoGenerate or AudioGenerate step whose length or takes come from inputs, priced at the dearest values
    /// those inputs allow, against the step's own spend cap.
    /// </summary>
    private async Task<IEnumerable<LintFinding>> SpendCapFindingsAsync(AppManifest manifest, List<WorkflowStep> steps, CancellationToken ct)
    {
        if (spendPricer is null)
            return [];

        StepCompatibilityRule rule = StepCompatibilityRules.Get("WL033");
        List<LintFinding> findings = [];
        foreach (WorkflowStep step in steps.Where(s => s.StepType is StepType.VideoGenerate or StepType.AudioGenerate))
        {
            string? json = AppBindingStep.ConfigJsonOf(step);
            if (json is null || JsonNode.Parse(json) is not JsonObject config
                || AppBindingAnalysis.FindAppInputs(config) is not (_, JsonObject map)
                || SpendCap(config) is not { } cap || cap <= 0)
                continue;

            Dictionary<string, JsonElement> dearest = new(StringComparer.Ordinal);
            foreach ((string setting, JsonNode? target) in map)
            {
                if (setting is not ("durationSeconds" or "durationSec" or "takes") || !AppValueBinding.TryRead(target, out AppValueBinding? binding, out _)
                    || binding!.InputId is not { } id || InputOf(manifest, id) is not { } input || Dearest(input) is not { } value)
                    continue;
                dearest[id] = JsonSerializer.SerializeToElement(value);
            }

            if (dearest.Count == 0)
                continue;

            AppInputBindResult bound = AppInputBinder.Bind(manifest, [AppBindingStep.From(step)], dearest);
            if (bound.Problems.Count > 0 || !bound.Steps[0].Changed)
                continue;
            WorkflowStep priced = Replay(step, bound.Steps[0].Step.ConfigJson);

            double? credits;
            try
            {
                credits = await spendPricer.CreditsAsync(priced, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                continue;
            }

            if (credits is not { } dear || dear <= cap)
                continue;

            string label = InputLabel(manifest, dearest.Keys.Order(StringComparer.Ordinal).First());
            string creditsText = Math.Ceiling(dear).ToString(CultureInfo.InvariantCulture);
            string capText = cap.ToString("0.##", CultureInfo.InvariantCulture);
            string stepOrder = Num(step.StepOrder);
            findings.Add(new LintFinding(rule.Id, rule.Severity, step.StepOrder,
                new LintMessage(
                    rule.Format(("input", label), ("step", $"step {stepOrder}"), ("credits", creditsText), ("cap", capText)).Text,
                    LintArgs.Of(("input", label), ("stepOrder", stepOrder), ("stepLabel", step.Label), ("credits", creditsText), ("cap", capText))),
                new LintFix(LintFixKind.ChangeSetting, "Narrow the input's range or raise the step's cap", LabelId: "WL033.narrowRange")));
        }

        return findings;
    }

    /// <summary>The largest value an input may take: its last duration choice, its max, or its largest choice option.</summary>
    private static double? Dearest(AppManifestInput input)
    {
        List<double> candidates = [];
        if (input.Max is { } max)
            candidates.Add(max);
        candidates.AddRange((input.Choices ?? []).Where(c => c.ValueKind == JsonValueKind.Number).Select(c => c.GetDouble()));
        candidates.AddRange((input.Options ?? []).Where(o => o.Value.ValueKind == JsonValueKind.Number).Select(o => o.Value.GetDouble()));
        if (input.Default is { ValueKind: JsonValueKind.Number } def)
            candidates.Add(def.GetDouble());
        return candidates.Count == 0 ? null : candidates.Max();
    }

    private static WorkflowStep Replay(WorkflowStep step, string? configJson)
    {
        WorkflowStep copy = ReelBolt.Shared.Workflows.Replay.WorkflowStepCloner.Clone(step, step.WorkflowDefinitionId);
        copy.Id = step.Id;
        AppBindingStep.SetConfigJson(copy, configJson);
        return copy;
    }

    private static double? SpendCap(JsonObject config)
    {
        foreach ((string key, JsonNode? node) in config)
        {
            if (string.Equals(key, "maxSpendCredits", StringComparison.OrdinalIgnoreCase) && node is JsonValue value && value.TryGetValue(out double number))
                return number;
        }

        return null;
    }

    /// <summary>A value as a token: a toggle as <c>on</c>/<c>off</c>, a choice by its label, anything else as written.</summary>
    private static string ValueToken(AppManifestInput input, JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.True => "on",
            JsonValueKind.False => "off",
            _ => AppRequestComposer.Display(input, value)
        };

    private static AppManifestInput? InputOf(AppManifest manifest, string? id) =>
        id is null ? null : (manifest.Inputs ?? []).FirstOrDefault(i => i.Kind == AppManifestVocabulary.KindInput && i.Id == id);

    private static string InputLabel(AppManifest manifest, string? id) =>
        InputOf(manifest, id) is { } input ? AppInputValueChecker.Label(input) : id ?? "an app input";

    /// <summary>The unfit message may lead with the input's label; the step prefix ("Step N") follows it.</summary>
    private static string StripLabel(string message)
    {
        int at = message.IndexOf("Step ", StringComparison.Ordinal);
        return at < 0 ? message : message[at..];
    }
}
