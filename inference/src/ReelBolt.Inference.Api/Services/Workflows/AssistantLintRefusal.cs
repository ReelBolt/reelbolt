using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Briefs;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows.Lint;

namespace ReelBolt.Inference.Api.Services.Workflows;

/// <summary>
/// The pre-run lint gate the assistant's propose paths share (ProposeWorkflow, ProposePatchWorkflow, ProposeCloneWorkflow):
/// a proposal that introduces a structural lint error is refused to the model with the findings, never offered as a card.
/// An edit is refused only for errors the stored workflow did not already have, so fixing one problem is never blocked by
/// another that was there before. Lint never breaks a tool: a missing linter or a linter that throws means "no findings".
/// </summary>
public static class AssistantLintRefusal
{
    /// <summary>Lints stored steps of a workflow of <paramref name="project"/>; null when no linter is wired or it failed.</summary>
    public static async Task<WorkflowLintReport?> LintStoredAsync(
        IWorkflowLinter? linter, InferenceApiDbContext db, Project project, Guid workflowId, IReadOnlyList<WorkflowStep> steps, CancellationToken ct)
    {
        if (linter is null || steps.Count == 0)
            return null;
        try
        {
            List<WorkflowStep> copy = [.. steps];
            await WorkflowLinterAgents.FillAsync(db, project.OrganizationId, copy, ct);
            return await linter.LintAsync(new WorkflowLintRequest(project.OrganizationId, project.Id, copy, null, workflowId), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>Lints a draft of <paramref name="project"/>'s steps; null when no linter is wired or it failed.</summary>
    /// <param name="manifest">
    /// The app page the draft will be saved with (docs/design/app-inputs.md 13.2), so the app-input rules read the inputs
    /// the steps bind; null (every caller without one) lints exactly as before.
    /// </param>
    public static async Task<WorkflowLintReport?> LintDraftAsync(
        IWorkflowLinter? linter, InferenceApiDbContext db, Project project, IReadOnlyList<CreateWorkflowStepRequest> steps, RunBrief? brief, CancellationToken ct,
        ReelBolt.Shared.Workflows.Apps.AppManifest? manifest = null)
    {
        if (linter is null || steps.Count == 0)
            return null;
        try
        {
            Guid[] agentIds = [.. steps.Select(s => s.AgentDefinitionId).Distinct()];
            Dictionary<Guid, AgentDefinition> agents = await WorkflowLinterAgents.LoadAsync(db, project.OrganizationId, agentIds, ct);
            return await linter.LintAsync(
                new WorkflowLintRequest(project.OrganizationId, project.Id, WorkflowLinter.FromRequests(steps, agents), brief, Manifest: manifest), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// The refusal (the object returned to the model) when <paramref name="draft"/> has structural errors that
    /// <paramref name="stored"/> did not; null when nothing new blocks. <paramref name="stored"/> null = a new workflow.
    /// </summary>
    public static object? Evaluate(WorkflowLintReport? draft, WorkflowLintReport? stored)
    {
        if (draft is null)
            return null;
        // A finding that names a step is the same finding when the rule and the step order match (a message can embed a
        // label, which a rename changes); a workflow-level finding is matched by its message.
        static (string, string) Identity(LintFinding f) => (f.RuleId, f.StepOrder is { } order ? $"#{order}" : f.Message);
        HashSet<(string, string)> before = [.. (stored?.Errors ?? []).Select(Identity)];
        List<LintFinding> blocking = [.. draft.Findings.Where(AssistantLintPolicy.IsStructuralError).Where(f => !before.Contains(Identity(f)))];
        if (blocking.Count == 0)
            return null;
        return new
        {
            error = blocking.Count == 1
                ? "This workflow has a problem that would make a run fail, so it was not proposed. Fix it and propose again."
                : $"This workflow has {blocking.Count} problems that would make a run fail, so it was not proposed. Fix them and propose again.",
            code = WorkflowLintGate.ErrorsCode,
            findings = blocking.Select(LintFindingResponse.ForAssistant),
        };
    }
}
