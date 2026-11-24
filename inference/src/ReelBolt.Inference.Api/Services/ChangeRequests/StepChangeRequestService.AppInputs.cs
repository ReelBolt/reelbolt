using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Services.Compositions;
using ReelBolt.Inference.Api.Services.Styles;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Briefs;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows.Apps;
using ReelBolt.Shared.Workflows.ChangeRequests;
using ReelBolt.Shared.Workflows.Replay;

namespace ReelBolt.Inference.Api.Services.ChangeRequests;

/// <summary>
/// The app-input kind of change request (docs/design/app-inputs.md 10.2, 25): a finished app run redone with one or more
/// of its own fields changed, as a NEW LINKED RUN through the same replay machinery as a step change. The seeds of the
/// replay plan are the steps that consume a changed value (<see cref="AppInputChangePlanner"/>); every other step is
/// reused free; the estimate prices only what is redone. Values go through the run gate's own checks
/// (<see cref="AppInputValueValidator.Evaluate"/>, the <c>APP_INPUT_*</c> codes).
/// </summary>
public sealed partial class StepChangeRequestService
{
    /// <summary>The metric tag and the stored <c>step_type</c> tag of the request kind.</summary>
    internal const string AppInputsTag = "app_inputs";

    /// <summary>The target kind of a step re-made because a field it uses changed.</summary>
    internal const string AppInputsTargetKind = "inputs";

    private static bool UsesInputs(CreateStepChangeRequest? body) => body?.Inputs is not null;

    /// <summary>The prepared app-input part: the new run's values and request, and what changes, in words.</summary>
    private sealed record PreparedAppInputs(
        IReadOnlyDictionary<string, JsonElement>? Values,
        IReadOnlyDictionary<string, JsonElement> Changed,
        string? UserRequest,
        string Summary,
        IReadOnlyList<StepChangeInputChangeResponse> Lines,
        bool BriefChanged = false,
        StoredRunBrief? Brief = null,
        IReadOnlyList<BriefChangeLine>? BriefLines = null);

    /// <summary>What a finished app run used: its page (labels of the time), its values and the app's authored steps.</summary>
    private sealed record AppRunContext(
        AppManifest Manifest,
        IReadOnlyDictionary<string, JsonElement> Values,
        List<WorkflowStep> Authored,
        Guid? OriginWorkflowId,
        MediaEgressGate? MediaEgress = null);

    private Task<(Prepared?, StepChangeRequestResult?)> PrepareAnyAsync(
        Project project, Guid executionId, CreateStepChangeRequest? body, CancellationToken ct) =>
        UsesInputs(body) || UsesBrief(body) ? PrepareAppInputsAsync(project, executionId, body!, ct) : PrepareAsync(project, executionId, body, ct);

    /// <summary>The run's app page and values, and the authored steps of the app it was made from; null when it is no app run.</summary>
    private async Task<AppRunContext?> LoadAppContextAsync(Project project, StepChangeSource source, CancellationToken ct)
    {
        Guid manifestDefinition = source.Execution.SnapshotDefinitionId ?? source.Execution.WorkflowDefinitionId;
        string? manifestJson = await _db.WorkflowDefinitions.AsNoTracking()
            .Where(w => w.Id == manifestDefinition && w.ProjectId == project.Id)
            .Select(w => w.AppManifestJson)
            .FirstOrDefaultAsync(ct);
        AppManifest? manifest = AppManifestJson.TryParse(manifestJson);
        if (manifest is null || !AppInputUsage.Declares(manifest))
            return null;

        IReadOnlyDictionary<string, JsonElement> values = RunBriefJson.TryParse(source.Execution.RunBriefJson)?.Inputs
            ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        List<WorkflowStep> authored = source.OriginWorkflowDefinitionId is { } origin
            ? await _db.WorkflowSteps.AsNoTracking()
                .Include(s => s.AgentDefinition)
                .Where(s => s.WorkflowDefinitionId == origin && s.WorkflowDefinition.ProjectId == project.Id)
                .OrderBy(s => s.StepOrder)
                .ToListAsync(ct)
            : [];
        return new AppRunContext(manifest, values, authored, source.OriginWorkflowDefinitionId,
            MediaEgressGate.FromStored(RunBriefJson.TryParse(source.Execution.RunBriefJson)));
    }

    /// <summary>
    /// The page of an app run that chose an agent's model (the same rule as a revision's, F2 28.4), or null: the page the run
    /// ran with (its snapshot's copy, else its workflow's).
    /// </summary>
    private async Task<string?> AgentModelPageAsync(Project project, StepChangeSource source, CancellationToken ct)
    {
        Guid manifestDefinition = source.Execution.SnapshotDefinitionId ?? source.Execution.WorkflowDefinitionId;
        string? manifestJson = await _db.WorkflowDefinitions.AsNoTracking()
            .Where(w => w.Id == manifestDefinition && w.ProjectId == project.Id)
            .Select(w => w.AppManifestJson)
            .FirstOrDefaultAsync(ct);
        return AppModelRules.HasAgentModelValue(AppManifestJson.TryParse(manifestJson), RunBriefJson.TryParse(source.Execution.RunBriefJson)?.Inputs)
            ? manifestJson
            : null;
    }

    private async Task<IReadOnlyList<AppModelCandidate>?> ModelCandidatesAsync(Project project, AppManifest manifest, CancellationToken ct)
    {
        bool usesModels = (manifest.Inputs ?? []).Any(i => i.Kind == AppManifestVocabulary.KindInput
            && AppInputTypes.TryParse(i.Type, out AppInputType type) && type == AppInputType.Model);
        if (!usesModels || _services?.GetService(typeof(IAppInputModelResolverSource)) is not IAppInputModelResolverSource source)
            return null;
        return await source.CandidatesAsync(project.OrganizationId, project.Id, ct);
    }

    /// <summary>
    /// The notes the person typed for the source run, recovered from its request text: the request is the composed
    /// template followed by the notes under <see cref="AppRequestComposer.NotesHeading"/> (or the notes alone when the
    /// template composed nothing). False when the text does not have that shape (it cannot be recomposed safely).
    /// </summary>
    internal static bool TryRecoverNotes(AppManifest manifest, IReadOnlyDictionary<string, JsonElement> values, string? request, out string? notes)
    {
        notes = null;
        string? text = AppRequestComposer.Compose(manifest, values, null);
        if (string.IsNullOrEmpty(text))
        {
            notes = request;
            return true;
        }

        if (string.Equals(request, text, StringComparison.Ordinal))
            return true;
        string framed = $"{text}\n\n{AppRequestComposer.NotesHeading}\n";
        if (request is not null && request.StartsWith(framed, StringComparison.Ordinal))
        {
            notes = request[framed.Length..];
            return true;
        }

        return false;
    }

    private async Task<(Prepared?, StepChangeRequestResult?)> PrepareAppInputsAsync(
        Project project, Guid executionId, CreateStepChangeRequest body, CancellationToken ct)
    {
        // docs/design/app-inputs.md 29: the app's fields and the run settings may come with step targets, but only in the
        // targets form (one plan over every kind of seed); the single-target fields stay a step change of their own.
        if (body.StepOrder is not null || body.StepResultId is not null || body.Instruction is not null
            || body.ItemIds is not null || body.Overrides is not null)
        {
            return (null, Invalid([new("inputs", "To change steps as well as the app's fields or run settings, name the steps under targets.")]));
        }

        StepChangeSourceResolution loaded = await StepChangeRequestSources.LoadAsync(_db, project.Id, executionId, ct);
        if (loaded.Source is not { } source)
            return (null, StepChangeRequestResult.Refuse(loaded.StatusCode, loaded.Code, loaded.Message ?? "Run not found."));
        if (!StepChangeRequestSources.IsFinished(source.Execution.Status))
            return (null, StepChangeRequestResult.Refuse(409, "RUN_NOT_FINISHED", "You can request changes once this run has finished."));
        if (await RunChangeGate.IsClosedAsync(_db, project.Id, source.Execution, ct))
            return (null, StepChangeRequestResult.Refuse(RunChangeGate.StatusCode, RunChangeGate.Code, RunChangeGate.Message));

        bool hasInputs = body.Inputs is not null;
        AppRunContext? app = await LoadAppContextAsync(project, source, ct);
        if (app is null && hasInputs)
        {
            return (null, StepChangeRequestResult.Refuse(400, AppInputValueProblem.Invalid,
                "This run was not made by an app with its own fields, so there is nothing to change here."));
        }

        // The permission to send media belongs to the run it was given to: a change request carries it over, never changes it.
        if (hasInputs && body.Inputs!.Keys.Any(k => string.Equals(k, MediaEgressPermission.InputId, StringComparison.OrdinalIgnoreCase)))
        {
            return (null, StepChangeRequestResult.Refuse(400, AppInputValueProblem.Invalid, MediaEgressPermission.NotChangeableMessage,
                new Dictionary<string, object?> { ["inputId"] = MediaEgressPermission.InputId }));
        }

        if (hasInputs && app!.Authored.Count == 0)
            return (null, StepChangeRequestResult.Refuse(409, AppInputChangeCodes.Stale, AppInputChangeCodes.StaleMessage));

        // The notes the person typed for the run: recovered from the composed request of an app run, the request itself otherwise.
        string? notes = source.Execution.UserRequest;
        bool notesKnown = app is null || TryRecoverNotes(app.Manifest, app.Values, source.Execution.UserRequest, out notes);
        if (!notesKnown)
        {
            // The request text was not composed from the page (a brief file, a hand-written request): only a change that
            // leaves the composed text as it is can keep it.
            notes = null;
        }

        IReadOnlyDictionary<string, JsonElement> runValues = app?.Values ?? Empty;
        IReadOnlySet<int> reachable = StaticBranchResolver.ReachableStepOrders([.. source.Steps.Select(AppBindingStep.From)], runValues);
        IReadOnlyList<AppModelCandidate>? candidates = app is null ? null : await ModelCandidatesAsync(project, app.Manifest, ct);

        // ---- the app's own fields ----------------------------------------------------------------------------------
        IReadOnlySet<int> inputSeeds = new HashSet<int>();
        Dictionary<int, WorkflowStep> boundAfter = [];
        IReadOnlyDictionary<string, JsonElement>? newValues = app?.Values;
        Dictionary<string, JsonElement> changed = new(StringComparer.Ordinal);
        List<StepChangeInputChangeResponse> lines = [];
        string? userRequest = source.Execution.UserRequest;
        if (hasInputs)
        {
            // The values the new run is asked with: the run's own, with the request's changes laid over (null clears a field).
            Dictionary<string, JsonElement> raw = new(app!.Values, StringComparer.Ordinal);
            foreach ((string id, JsonElement value) in body.Inputs!)
            {
                if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                    raw.Remove(id);
                else
                    raw[id] = value.Clone();
            }

            AppInputGateResult before = AppInputValueValidator.Evaluate(app.Manifest, app.Authored, app.Values, notes, candidates: candidates, mediaEgress: app.MediaEgress);
            if (before.Refused || before.BoundSteps is null)
                return (null, StepChangeRequestResult.Refuse(409, AppInputChangeCodes.Stale, AppInputChangeCodes.StaleMessage));

            AppInputGateResult after = AppInputValueValidator.Evaluate(app.Manifest, app.Authored, raw, notes, candidates: candidates, mediaEgress: app.MediaEgress);
            if (after.Refused || after.BoundSteps is null)
            {
                Dictionary<string, object?>? extra = after.InputId is { } refusedId ? new() { ["inputId"] = refusedId } : null;
                return (null, StepChangeRequestResult.Refuse(400, after.Code, after.Message ?? "That value cannot be used.", extra));
            }

            List<AppBindingStep> authoredSlice = [.. app.Authored.Select(AppBindingStep.From)];
            AppInputChangeDecision decision = AppInputChangePlanner.Decide(app.Manifest, authoredSlice, before.Inputs, after.Inputs);
            // Unchanged fields next to changed settings or steps are not a refusal: the other parts carry the request.
            bool fieldsUnchanged = decision.RefusalCode == AppInputChangeCodes.Unchanged && (body.Brief is not null || body.Targets is not null);
            if (decision.Refused && !fieldsUnchanged)
            {
                Dictionary<string, object?>? extra = decision.InputId is { } refusedId ? new() { ["inputId"] = refusedId } : null;
                int status = decision.RefusalCode == AppInputChangeCodes.Unchanged ? 400 : 409;
                return (null, StepChangeRequestResult.Refuse(status, decision.RefusalCode, decision.RefusalMessage!, extra));
            }

            if (!fieldsUnchanged)
            {
                // The run's steps can be re-made from the app only while the app still binds them exactly as the run had them:
                // every step re-made here, bound with the run's own values, must equal the step the run executed.
                Dictionary<int, WorkflowStep> sourceByOrder = source.Steps.ToDictionary(s => s.StepOrder);
                Dictionary<int, WorkflowStep> boundBefore = before.BoundSteps.ToDictionary(s => s.StepOrder);
                foreach (int seed in decision.Seeds)
                {
                    if (!sourceByOrder.TryGetValue(seed, out WorkflowStep? ran) || !boundBefore.TryGetValue(seed, out WorkflowStep? authored)
                        || !SameStep(ran, authored))
                    {
                        return (null, StepChangeRequestResult.Refuse(409, AppInputChangeCodes.Stale, AppInputChangeCodes.StaleMessage));
                    }
                }

                // The request text: recomposed only when the composed part changes; one that cannot be recomposed is stale.
                if (!string.Equals(AppRequestComposer.Compose(app.Manifest, before.Inputs ?? Empty, null),
                        AppRequestComposer.Compose(app.Manifest, after.Inputs ?? Empty, null), StringComparison.Ordinal))
                {
                    if (!notesKnown)
                        return (null, StepChangeRequestResult.Refuse(409, AppInputChangeCodes.Stale, AppInputChangeCodes.StaleMessage));
                    userRequest = after.UserRequest;
                }

                reachable = after.ReachableStepOrders ?? reachable;
                inputSeeds = decision.Seeds;
                boundAfter = after.BoundSteps.ToDictionary(s => s.StepOrder);
                newValues = after.Inputs;
                foreach (string id in decision.Changed)
                    changed[id] = after.Inputs is { } values && values.TryGetValue(id, out JsonElement v) ? v : JsonSerializer.SerializeToElement<object?>(null);
                lines.AddRange(DescribeInputChanges(app.Manifest, decision.Changed, before.Inputs, after.Inputs, candidates));
            }
        }

        // ---- the run settings (docs/design/app-inputs.md 29) -------------------------------------------------------
        PreparedBrief? brief = null;
        if (body.Brief is { } briefChange)
        {
            (brief, StepChangeRequestResult? briefRefusal) = await PrepareBriefAsync(
                project, source, app?.Manifest, briefChange, reachable, notes, notesKnown, ct);
            if (brief is null)
                return (null, briefRefusal);
            if (brief.NotesChanged)
            {
                userRequest = app is null
                    ? brief.Notes
                    : AppRequestComposer.Compose(app.Manifest, newValues ?? Empty, brief.Notes);
            }

            lines.AddRange(brief.Lines.Select(l => new StepChangeInputChangeResponse(l.Key, l.Label, "brief", l.Before, l.After, null)));
        }

        HashSet<int> seeds = [.. inputSeeds, .. brief?.Seeds ?? new HashSet<int>()];
        if (seeds.Count == 0 && body.Targets is null)
        {
            return (null, StepChangeRequestResult.Refuse(400, AppInputChangeCodes.Unchanged,
                "Nothing would change: every field and setting already has this value."));
        }

        // ---- step targets (docs/design/app-inputs.md 29): one plan over both kinds of seed --------------------------
        List<PreparedTarget> stepTargets = [];
        CompositionRevisionService.FilmContext? film = null;
        if (body.Targets is not null)
        {
            (Prepared? steps, StepChangeRequestResult? stepRefusal) = await PrepareAsync(
                project, executionId, new CreateStepChangeRequest(null, null, null, null, null, body.Targets), ct, seeds);
            if (steps is null)
                return (null, stepRefusal);
            for (int i = 0; i < body.Targets.Count; i++)
            {
                StepChangeTargetRequest requested = body.Targets[i];
                PreparedTarget? named = steps.Targets.FirstOrDefault(t => (requested.StepOrder is { } o && t.Result.StepOrder == o)
                    || (requested.StepResultId is { } r && t.Result.StepResultId == r));
                if (named is not null && seeds.Contains(named.Result.StepOrder))
                {
                    return (null, Invalid([new($"targets[{i}]",
                        "This step is already made again because a field or setting it uses changes; leave it out of the targets.")]));
                }
            }

            stepTargets = [.. steps.Targets];
            film = steps.Film;
        }

        List<int> orders = [.. seeds.Concat(stepTargets.Select(t => t.Result.StepOrder)).Distinct().Order()];
        int? filmOrder = stepTargets.FirstOrDefault(t => t.IsFilm)?.Result.StepOrder;
        StepChangeResult first = TargetResultOf(source, orders[0]);
        CompositionReplayPlan planned = CompositionReplayPlanner.Plan(
            source.Steps, source.ResultIds, orders, CompositionCutPolicy.KeepCut,
            source.Execution.Id, source.OriginWorkflowDefinitionId, TargetLabel, first.Step.StepType.ToString(), filmOrder);
        CompositionReplayPlan plan = OffPathSkipped(planned, reachable);

        WorkflowDefinition snapshot = BuildSnapshot(project.Id, source, plan, null, filmOrder, film?.ReviserAgentId);
        WorkflowDefinition pricing = BuildSnapshot(
            project.Id, source, plan, stepTargets.Where(t => !t.IsFilm).ToDictionary(t => t.Result.StepOrder, t => t.Payload),
            filmOrder, film?.ReviserAgentId);
        if (app is not null)
        {
            string manifestJson = AppManifestJson.Serialize(app.Manifest);
            foreach (WorkflowDefinition copy in new[] { snapshot, pricing })
            {
                copy.AppManifestJson = manifestJson;
                foreach (WorkflowStep step in copy.Steps.Where(s => inputSeeds.Contains(s.StepOrder)))
                    AppBindingStep.SetConfigJson(step, AppBindingStep.ConfigJsonOf(boundAfter[step.StepOrder]));
            }
        }

        List<PreparedTarget> targets = [.. seeds.Order().Select(order => new PreparedTarget(
            TargetResultOf(source, order), new ChangeRequestPayload(null, null, null), 1d, false, null, IsInputSeed: true))];
        targets.AddRange(stepTargets);
        targets = [.. targets.OrderBy(t => t.Result.StepOrder)];

        return (new Prepared(source, targets, plan, snapshot, pricing, film,
            new PreparedAppInputs(newValues, changed, userRequest, lines.Count > 0 ? SummaryOf(lines) : string.Empty, lines, brief is { Lines.Count: > 0 }, brief?.Stored, brief?.Lines)), null);
    }

    private static readonly IReadOnlyDictionary<string, JsonElement> Empty = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

    /// <summary>The source run's result of a re-made step, or a stand-in when that step has no finished result there.</summary>
    private static StepChangeResult TargetResultOf(StepChangeSource source, int order) =>
        source.Results.TryGetValue(order, out StepChangeResult? result)
            ? result
            : new StepChangeResult(order, Guid.Empty, source.Steps.First(s => s.StepOrder == order), StepStatus.Pending, null);

    /// <summary>
    /// The plan with every step the values' branches never reach marked Skip (a step off the run's path has no result to
    /// reuse, and the planner would otherwise price it as run again).
    /// </summary>
    internal static CompositionReplayPlan OffPathSkipped(CompositionReplayPlan plan, IReadOnlySet<int> reachable) =>
        plan.Steps.All(s => reachable.Contains(s.StepOrder))
            ? plan
            : plan with
            {
                Steps = [.. plan.Steps.Select(s => reachable.Contains(s.StepOrder) || s.SourceStepResultId is not null
                    ? s
                    : s with { Disposition = ReplayStepDisposition.Skip, Reason = "not on the path these values take" })]
            };

    /// <summary>The step the run executed and the authored step bound with the run's values are the same step.</summary>
    internal static bool SameStep(WorkflowStep ran, WorkflowStep authored)
    {
        if (ran.StepType != authored.StepType || ran.AgentDefinitionId != authored.AgentDefinitionId
            || !string.Equals(ran.ConditionExpression, authored.ConditionExpression, StringComparison.Ordinal))
            return false;
        string? a = AppBindingStep.ConfigJsonOf(ran);
        string? b = AppBindingStep.ConfigJsonOf(authored);
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
            return string.IsNullOrWhiteSpace(a) == string.IsNullOrWhiteSpace(b);
        try
        {
            return JsonNode.DeepEquals(JsonNode.Parse(a), JsonNode.Parse(b));
        }
        catch (JsonException)
        {
            return string.Equals(a, b, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The run choices of the replay: the source run's, with the brief's app-input values replaced by the new ones and, when
    /// the request changes run settings (docs/design/app-inputs.md 29), the stored brief replaced by <paramref name="settings"/>.
    /// </summary>
    private static RunChoices? ReplayRunChoicesWith(
        WorkflowExecution sourceRun, IReadOnlyDictionary<string, JsonElement>? values, bool settingsChanged = false, StoredRunBrief? settings = null)
    {
        RunChoices? choices = CompositionRevisionService.ReplayRunChoices(sourceRun);
        StoredRunBrief baseBrief = settingsChanged ? settings ?? new StoredRunBrief() : choices?.Brief ?? new StoredRunBrief();
        // Who allowed sending media stays with the value it stands beside (a settings change builds a fresh brief).
        string? allowedBy = MediaEgressPermission.IsRequested(values) ? RunBriefJson.TryParse(sourceRun.RunBriefJson)?.AllowMediaEgressBy : null;
        StoredRunBrief brief = baseBrief with { Inputs = values is { Count: > 0 } ? values : null, AllowMediaEgressBy = allowedBy };
        return choices is null
            ? new RunChoices(null, null, null, brief.IsEmpty ? null : brief)
            : choices with { Brief = brief.IsEmpty ? null : brief };
    }

    /// <summary>Each changed field by label, before and after in words.</summary>
    internal static IReadOnlyList<StepChangeInputChangeResponse> DescribeInputChanges(
        AppManifest manifest, IReadOnlyList<string> changed,
        IReadOnlyDictionary<string, JsonElement>? before, IReadOnlyDictionary<string, JsonElement>? after,
        IReadOnlyList<AppModelCandidate>? candidates)
    {
        List<StepChangeInputChangeResponse> lines = [];
        foreach (string id in changed)
        {
            AppManifestInput? input = (manifest.Inputs ?? []).FirstOrDefault(i => i.Kind == AppManifestVocabulary.KindInput && i.Id == id);
            if (input is null)
                continue;
            JsonElement? old = before is not null && before.TryGetValue(id, out JsonElement b) ? b : null;
            JsonElement? now = after is not null && after.TryGetValue(id, out JsonElement a) ? a : null;
            lines.Add(new StepChangeInputChangeResponse(
                id, AppInputValueChecker.Label(input), input.Type ?? string.Empty,
                ValueWords(input, old, candidates), ValueWords(input, now, candidates), now));
        }

        return lines;
    }

    /// <summary>A value in words: text as typed (cut), a choice by its label, a switch by its words, a model by its name.</summary>
    internal static string ValueWords(AppManifestInput input, JsonElement? value, IReadOnlyList<AppModelCandidate>? candidates)
    {
        if (value is not { } v || v.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
            || (v.ValueKind == JsonValueKind.String && v.GetString()!.Length == 0))
            return "(empty)";
        AppInputTypes.TryParse(input.Type, out AppInputType type);
        if (type == AppInputType.Model)
        {
            return v.ValueKind == JsonValueKind.String && Guid.TryParse(v.GetString(), out Guid id)
                && candidates?.FirstOrDefault(c => c.Id == id) is { } model
                    ? string.IsNullOrWhiteSpace(model.Name) ? model.ModelCode : model.Name
                    : "a model of this workspace";
        }

        string words = AppRequestComposer.Display(input, v);
        return words.Length > 200 ? words[..200] + "…" : words;
    }

    /// <summary>
    /// The changed fields of the listed app-input requests, per request run (docs/design/app-inputs.md 26): the source
    /// run's values before, the request's stored values after, labelled by the source run's own page, models by name.
    /// </summary>
    private async Task<IReadOnlyDictionary<Guid, IReadOnlyList<StepChangeInputChangeResponse>>> ListedInputChangesAsync(
        Project project, IReadOnlyList<StepChangeRequest> rows, IReadOnlyDictionary<Guid, Guid> sourceDefinitions, CancellationToken ct)
    {
        Dictionary<Guid, IReadOnlyList<StepChangeInputChangeResponse>> result = [];
        var requests = rows
            .Select(r => (Row: r, Values: AppInputChangeRows.Values(r.OverridesJson)))
            .Where(r => r.Values is { Count: > 0 } || AppInputChangeRows.BriefLines(r.Row.OverridesJson).Count > 0)
            .GroupBy(r => r.Row.ExecutionId)
            .Select(g => g.First())
            .ToList();
        if (requests.Count == 0)
            return result;

        List<Guid> sourceIds = [.. requests.Select(r => r.Row.SourceExecutionId).Distinct()];
        Dictionary<Guid, string?> briefs = await _db.WorkflowExecutions.AsNoTracking()
            .Where(e => e.ProjectId == project.Id && sourceIds.Contains(e.Id))
            .Select(e => new { e.Id, e.RunBriefJson })
            .ToDictionaryAsync(e => e.Id, e => e.RunBriefJson, ct);
        List<Guid> definitionIds = [.. sourceIds.Where(sourceDefinitions.ContainsKey).Select(id => sourceDefinitions[id]).Distinct()];
        Dictionary<Guid, string?> pages = await _db.WorkflowDefinitions.AsNoTracking()
            .Where(w => w.ProjectId == project.Id && definitionIds.Contains(w.Id))
            .Select(w => new { w.Id, w.AppManifestJson })
            .ToDictionaryAsync(w => w.Id, w => w.AppManifestJson, ct);

        List<(Guid Run, AppManifest Page, IReadOnlyDictionary<string, JsonElement>? Before, IReadOnlyDictionary<string, JsonElement> After)> found = [];
        foreach ((StepChangeRequest row, IReadOnlyDictionary<string, JsonElement>? values) in requests)
        {
            if (!sourceDefinitions.TryGetValue(row.SourceExecutionId, out Guid definition)
                || AppManifestJson.TryParse(pages.GetValueOrDefault(definition)) is not { } page)
                continue;
            found.Add((row.ExecutionId, page, RunBriefJson.TryParse(briefs.GetValueOrDefault(row.SourceExecutionId))?.Inputs, values!));
        }

        // Names only (platform rows and this organization's own, through the tenant filter); never an id.
        Guid[] modelIds = [.. found.SelectMany(f => f.After.Values.Concat(f.Before?.Values ?? []))
            .Where(v => v.ValueKind == JsonValueKind.String && Guid.TryParse(v.GetString(), out _))
            .Select(v => Guid.Parse(v.GetString()!)).Distinct()];
        List<AppModelCandidate> names = modelIds.Length == 0 ? [] : [.. (await _db.ModelCapabilities.AsNoTracking()
            .Where(c => modelIds.Contains(c.Id))
            .Select(c => new { c.Id, c.Model.DisplayName, c.Model.ModelCode })
            .ToListAsync(ct))
            .Select(c => new AppModelCandidate(c.Id, string.Empty, string.Empty, c.ModelCode, c.DisplayName ?? string.Empty, IsPlatform: false))];

        foreach ((Guid run, AppManifest page, IReadOnlyDictionary<string, JsonElement>? before, IReadOnlyDictionary<string, JsonElement> after) in found)
        {
            IReadOnlyList<StepChangeInputChangeResponse> lines = DescribeInputChanges(page, [.. after.Keys.Order(StringComparer.Ordinal)], before, after, names);
            if (lines.Count > 0)
                result[run] = lines;
        }

        // The run settings a request changed (docs/design/app-inputs.md 29), stored in words on the row.
        foreach ((StepChangeRequest row, _) in requests)
        {
            IReadOnlyList<BriefChangeLine> settings = AppInputChangeRows.BriefLines(row.OverridesJson);
            if (settings.Count == 0)
                continue;
            List<StepChangeInputChangeResponse> all = [.. result.GetValueOrDefault(row.ExecutionId) ?? []];
            all.AddRange(settings.Select(l => new StepChangeInputChangeResponse(l.Key, l.Label, "brief", l.Before, l.After, null)));
            result[row.ExecutionId] = all;
        }

        return result;
    }

    /// <summary>The one-line summary stored as each row's instruction: <c>Changed “Label”: before → after; …</c>.</summary>
    internal static string SummaryOf(IReadOnlyList<StepChangeInputChangeResponse> lines)
    {
        string text = "Changed " + string.Join("; ", lines.Select(l => $"“{l.Label}”: {l.Before} → {l.After}"));
        return text.Length > 4000 ? text[..3999] + "…" : text;
    }

    // ------------------------------------------------------------------
    // Capability
    // ------------------------------------------------------------------

    /// <summary>The app-input capability of a run (docs/design/app-inputs.md 25), or null when the run is not an app run.</summary>
    private async Task<StepChangeAppInputsResponse?> AppInputCapabilitiesAsync(Project project, StepChangeSource source, bool finished, CancellationToken ct)
    {
        AppRunContext? app = await LoadAppContextAsync(project, source, ct);
        if (app is null)
            return null;

        List<AppBindingStep> slice = [.. app.Authored.Select(AppBindingStep.From)];
        IReadOnlySet<int> reachable = StaticBranchResolver.ReachableStepOrders(slice, app.Values);
        Dictionary<int, WorkflowStep> byOrder = source.Steps.ToDictionary(s => s.StepOrder);
        IReadOnlyList<AppModelCandidate>? candidates = await ModelCandidatesAsync(project, app.Manifest, ct);
        bool stale = app.Authored.Count == 0;
        if (!stale)
        {
            AppInputGateResult before = AppInputValueValidator.Evaluate(app.Manifest, app.Authored, app.Values, null, candidates: candidates, mediaEgress: app.MediaEgress);
            stale = before.Refused || before.BoundSteps is null;
        }

        List<StepChangeAppInputFieldResponse> fields = [];
        foreach (AppManifestInput input in (app.Manifest.Inputs ?? []).Where(i => i.Kind == AppManifestVocabulary.KindInput && i.Id is not null))
        {
            string id = input.Id!;
            JsonElement? value = app.Values.TryGetValue(id, out JsonElement v) ? v : null;
            IReadOnlySet<int> consumers = AppInputChangePlanner.Consumers(app.Manifest, slice, id);
            List<string> usedBy = [.. consumers.Where(reachable.Contains).Order()
                .Select(o => byOrder.TryGetValue(o, out WorkflowStep? s) ? LabelOf(s) : $"Step {o}")];
            bool decides = AppInputChangePlanner.DecidesBranch(slice, id);
            IReadOnlyList<JsonElement>? branchValues = AppInputChangePlanner.BranchChangingValues(input, slice, app.Values);

            (bool supported, string? reason, string? code) = (true, null, null);
            if (!finished)
                (supported, reason, code) = (false, "the run has not finished", "RUN_NOT_FINISHED");
            else if (stale)
                (supported, reason, code) = (false, "the app changed after this run, so run it again instead", AppInputChangeCodes.Stale);
            else if (usedBy.Count == 0 && decides)
                (supported, reason, code) = (false, "it picks another path through the app, so run it again instead", AppInputChangeCodes.ChangesBranch);
            else if (usedBy.Count == 0)
                (supported, reason, code) = (false, "nothing in this run uses it", AppInputChangeCodes.NotUsed);

            fields.Add(new StepChangeAppInputFieldResponse(
                id, AppInputValueChecker.Label(input), input.Type ?? string.Empty, value, ValueWords(input, value, candidates),
                supported, reason, code, usedBy, decides, branchValues, input));
        }

        // The run settings the page shows (docs/design/app-inputs.md 29), beside the app's own fields.
        IReadOnlyList<StepChangeBriefFieldResponse>? settings = await BriefCapabilitiesAsync(project, source, app.Manifest, app.Values, finished, ct);
        return fields.Count == 0 && settings is null ? null : new StepChangeAppInputsResponse(app.OriginWorkflowId, fields, settings);
    }
}
