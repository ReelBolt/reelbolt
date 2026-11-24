using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Briefs;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows.Apps;
using ReelBolt.Shared.Workflows.Lint;

namespace ReelBolt.Inference.Api.Services.Workflows;

/// <summary>Metrics of the pre-run lint gate.</summary>
public static class WorkflowLintDiagnostics
{
    /// <summary>Meter name; the API's telemetry setup adds it with <c>AddMeter</c>.</summary>
    public const string MeterName = "ReelBolt.Inference.Lint";

    private static readonly Meter Meter = new(MeterName);

    /// <summary>
    /// A run start that the linter's error findings would have blocked (the flag was off, so the run went on), or
    /// did block (the flag was on). Tagged by rule and by whether it was enforced.
    /// </summary>
    public static readonly Counter<long> WouldBlock = Meter.CreateCounter<long>(
        "reelbolt.inference.lint_would_block", "findings",
        "Lint error findings that would block (or did block) a run start, by rule");
}

/// <summary>
/// The run-start lint check (docs/workflow-linter.md "Rolling it out"). With <c>Workflows:Lint:Enforce</c> false
/// (the default) it runs the linter anyway in warn-only mode: it writes one structured log line and counts the
/// error findings that would have blocked, and never changes the answer. With it true, any error finding is a
/// <c>409 WORKFLOW_LINT_ERRORS</c> carrying the findings, before credits are looked at. In both modes the lint is
/// bounded in time and fails open: a lint that cannot answer in time, or throws, lets the run start.
/// </summary>
public sealed class WorkflowLintGate(
    IWorkflowLinter linter,
    InferenceApiDbContext db,
    IConfiguration configuration,
    ILogger<WorkflowLintGate> logger)
{
    /// <summary>The 409 code.</summary>
    public const string ErrorsCode = "WORKFLOW_LINT_ERRORS";

    /// <summary>Configuration key of the enforcement flag.</summary>
    public const string EnforceKey = "Workflows:Lint:Enforce";

    /// <summary>How long a warn-only lint may take before it is abandoned (it must not slow the run start).</summary>
    public static readonly TimeSpan WarnOnlyBudget = TimeSpan.FromSeconds(2);

    /// <summary>How long an enforcing lint may take before the run is let through.</summary>
    public static readonly TimeSpan EnforceBudget = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The part of a stored run brief a retry or resume can lint with: the fields the run kept (language, length,
    /// voice, design system, source files, overlay-card audio, avatar, app inputs: every StoredRunBrief field); null when the run had none.
    /// </summary>
    public static RunBrief? BriefFromStored(string? runBriefJson) =>
        RunBriefJson.TryParse(runBriefJson) is { IsEmpty: false } stored
            ? new RunBrief(
                Language: stored.Language,
                SourceLanguage: stored.SourceLanguage,
                LengthSec: stored.LengthSec,
                DesignSystem: stored.DesignSystem,
                Voice: stored.Voice,
                SourceFiles: stored.SourceFiles,
                OverlayCardAudio: stored.OverlayCardAudio,
                Avatar: stored.Avatar,
                Inputs: stored.Inputs)
            : null;

    /// <summary>
    /// Rules that never block a run start, whatever their severity elsewhere. WL018 (an app input pointing at
    /// nothing) is a dead control, not a failing run: it is an error where a manifest is saved and on the lint
    /// endpoints, and a note here.
    /// </summary>
    private static readonly HashSet<string> NeverBlocksRunStart = ["WL018"];

    /// <summary>The 400 code of a run refused because a video step has no video chosen (WL010's open-slot variant).</summary>
    public const string VideoNotChosenCode = "VIDEO_SOURCE_NOT_CHOSEN";

    /// <summary>The 400 code of a run refused because an avatar step has no avatar chosen (WL027's variant).</summary>
    public const string AvatarNotChosenCode = "AVATAR_NOT_CHOSEN";

    /// <summary>The 400 code of a run refused because a video model is not supported by its provider (WL006's variant).</summary>
    public const string VideoModelUnsupportedCode = "VIDEO_MODEL_UNSUPPORTED";

    /// <summary>The 400 code of a run refused because a talking-head step has no lip-sync model set up (WL006's variant).</summary>
    public const string LipSyncModelNotSetCode = "LIP_SYNC_MODEL_NOT_SET";

    /// <summary>
    /// The finding keys that refuse a run start whatever <see cref="EnforceKey"/> says: failures that are certain (the
    /// engine fails the step with the same words) and that the person fixes in one move, so there is nothing to
    /// observe before turning them on. Everything else stays behind the flag.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> StartBlockers = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [WorkflowLinter.OpenVideoSlotKey] = VideoNotChosenCode,
        [WorkflowLinter.NoAvatarChosenKey] = AvatarNotChosenCode,
        [WorkflowLinter.ModelUnsupportedKey] = VideoModelUnsupportedCode,
        [WorkflowLinter.LipSyncModelNotSetKey] = LipSyncModelNotSetCode
    };

    /// <summary>
    /// The wire code of a refusal <see cref="CheckAsync"/> returned: a start blocker's own code (400), else
    /// <see cref="ErrorsCode"/> (409), so a refusal is recorded under the code the caller saw.
    /// </summary>
    public static string CodeOf(ActionResult refusal) =>
        (refusal as ObjectResult)?.Value?.GetType().GetProperty("code")?.GetValue(((ObjectResult)refusal).Value) as string
        ?? ErrorsCode;

    /// <summary>True when error findings block run starts.</summary>
    public bool Enforce => configuration.GetValue<bool>(EnforceKey);

    /// <summary>
    /// Lints the run about to start. Returns the 400 <see cref="VideoNotChosenCode"/> when a video step has no video chosen
    /// (always), the 409 when enforcing and the lint found errors; otherwise null. <paramref name="atRunStart"/> false (a
    /// lint that only observes, such as a marketplace copy) judges nothing run-start-only.
    /// </summary>
    /// <param name="steps">The steps that will run (a retry's snapshot); null = the saved workflow.</param>
    /// <param name="chargedStepOrders">When resuming, only these steps will run: findings on other steps are ignored.</param>
    public async Task<ActionResult?> CheckAsync(
        Project project,
        Guid workflowId,
        IReadOnlyList<WorkflowStep>? steps,
        RunBrief? brief,
        IReadOnlyCollection<int>? chargedStepOrders,
        CancellationToken ct,
        bool atRunStart = true)
    {
        bool enforce = Enforce;
        using CancellationTokenSource budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(enforce ? EnforceBudget : WarnOnlyBudget);
        Stopwatch clock = Stopwatch.StartNew();
        try
        {
            List<WorkflowStep>? toLint = steps is null
                ? await LoadSavedAsync(project.Id, workflowId, budget.Token)
                : [.. steps];
            if (toLint is null || toLint.Count == 0)
                return null;
            await WorkflowLinterAgents.FillAsync(db, project.OrganizationId, toLint, budget.Token);

            WorkflowLintReport report = await linter.LintAsync(
                // A retry or resume runs the run's own snapshot steps; the live manifest says nothing about them.
                new WorkflowLintRequest(project.OrganizationId, project.Id, toLint, brief, steps is null ? workflowId : null, AtRunStart: atRunStart), budget.Token);
            List<LintFinding> errors =
            [
                .. report.Errors.Where(e => !NeverBlocksRunStart.Contains(e.RuleId))
                .Where(e => chargedStepOrders is null || e.StepOrder is not { } order || chargedStepOrders.Contains(order))
            ];
            // A start blocker is a certain failure only on a step the run will certainly reach (a straight workflow, or the
            // branch the app's inputs pick); on a branch the run may not take, the engine decides, and the run is not refused.
            if (atRunStart && errors.Any(e => StartBlockers.ContainsKey(e.MessageKey)))
            {
                IReadOnlySet<int> certain = StaticBranchResolver.CertainStepOrders([.. toLint.Select(AppBindingStep.From)], brief?.Inputs);
                errors.RemoveAll(e => StartBlockers.ContainsKey(e.MessageKey) && e.StepOrder is { } o && !certain.Contains(o));
            }

            if (errors.Count == 0)
                return null;

            if (atRunStart && errors.Where(e => StartBlockers.ContainsKey(e.MessageKey)).ToList() is { Count: > 0 } blockers)
            {
                foreach (LintFinding blocker in blockers)
                {
                    WorkflowLintDiagnostics.WouldBlock.Add(
                        1,
                        new KeyValuePair<string, object?>("rule", blocker.RuleId),
                        new KeyValuePair<string, object?>("enforced", true));
                }

                logger.LogWarning(
                    "Lint blocked a run start (ProjectId={ProjectId}, WorkflowId={WorkflowId}, Code={Code}, Steps={Steps})",
                    project.Id, workflowId, StartBlockers[blockers[0].MessageKey], string.Join(',', blockers.Select(b => b.StepOrder)));
                return new BadRequestObjectResult(new
                {
                    code = StartBlockers[blockers[0].MessageKey],
                    message = blockers[0].Message,
                    stepOrder = blockers[0].StepOrder,
                    stepLabel = blockers[0].Args.TryGetValue("stepLabel", out string? label) ? label : null,
                    findings = blockers.Select(LintFindingResponse.From).ToList()
                });
            }

            foreach (LintFinding error in errors)
            {
                WorkflowLintDiagnostics.WouldBlock.Add(
                    1,
                    new KeyValuePair<string, object?>("rule", error.RuleId),
                    new KeyValuePair<string, object?>("enforced", enforce));
            }

            logger.LogWarning(
                "Lint {Outcome} a run start (ProjectId={ProjectId}, WorkflowId={WorkflowId}, Errors={ErrorCount}, Rules={Rules}, ElapsedMs={ElapsedMs})",
                enforce ? "blocked" : "would have blocked",
                project.Id,
                workflowId,
                errors.Count,
                string.Join(',', errors.Select(e => e.RuleId).Distinct()),
                clock.ElapsedMilliseconds);

            if (!enforce)
                return null;

            return new ConflictObjectResult(new
            {
                code = ErrorsCode,
                message = errors.Count == 1
                    ? "This workflow has a problem that would make the run fail. Fix it and run again; nothing was spent."
                    : $"This workflow has {errors.Count} problems that would make the run fail. Fix them and run again; nothing was spent.",
                findings = errors.Select(LintFindingResponse.From).ToList()
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Lint is advisory: a timeout or a failure to read lets the run start (and is visible in the log).
            logger.LogWarning(
                ex,
                "Lint skipped for a run start (ProjectId={ProjectId}, WorkflowId={WorkflowId}, ElapsedMs={ElapsedMs})",
                project.Id,
                workflowId,
                clock.ElapsedMilliseconds);
            return null;
        }
    }

    private async Task<List<WorkflowStep>?> LoadSavedAsync(Guid projectId, Guid workflowId, CancellationToken ct) =>
        await db.WorkflowSteps.AsNoTracking()
            .Where(s => s.WorkflowDefinitionId == workflowId && s.WorkflowDefinition.ProjectId == projectId)
            .ToListAsync(ct);
}

/// <summary>Loads the agent definitions a lint needs, scoped to the organization (its own plus the built-ins).</summary>
public static class WorkflowLinterAgents
{
    /// <summary>The agent definitions with these ids that <paramref name="organizationId"/> may see.</summary>
    public static async Task<Dictionary<Guid, AgentDefinition>> LoadAsync(
        InferenceApiDbContext db, Guid organizationId, IReadOnlyCollection<Guid> ids, CancellationToken ct) =>
        ids.Count == 0
            ? []
            : await db.AgentDefinitions.AsNoTracking()
                .IgnoreQueryFilters([InferenceApiDbContext.TenantFilterName])
                .Where(a => ids.Contains(a.Id) && (a.IsBuiltIn || a.OrganizationId == organizationId))
                .ToDictionaryAsync(a => a.Id, ct);

    /// <summary>Fills the agent definition of steps loaded without it, so the agent-type rules can judge them.</summary>
    public static async Task FillAsync(InferenceApiDbContext db, Guid organizationId, List<WorkflowStep> steps, CancellationToken ct)
    {
        List<Guid> missing = [.. steps.Where(s => s.AgentDefinition is null).Select(s => s.AgentDefinitionId).Distinct()];
        Dictionary<Guid, AgentDefinition> agents = await LoadAsync(db, organizationId, missing, ct);
        foreach (WorkflowStep step in steps.Where(s => s.AgentDefinition is null))
        {
            if (agents.TryGetValue(step.AgentDefinitionId, out AgentDefinition? agent))
                step.AgentDefinition = agent;
        }
    }
}
