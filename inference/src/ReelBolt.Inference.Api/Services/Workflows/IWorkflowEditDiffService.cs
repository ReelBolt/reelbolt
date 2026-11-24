using System;
using System.Collections.Generic;
using System.Linq;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Services.Workflows;

public interface IWorkflowEditDiffService
{
    WorkflowEditDiff ComputeDiff(
        Guid? workflowId,
        IReadOnlyList<WorkflowStep> current,
        IReadOnlyList<CreateWorkflowStepRequest> proposed);

    WorkflowValidationResult ValidateProposedSteps(
        Guid projectId,
        IReadOnlyList<CreateWorkflowStepRequest> proposed);

    /// <summary>
    /// Builds a WorkflowStep entity from a CreateWorkflowStepRequest, setting all field values
    /// including StepType, config JSON fields, and optional fields like MaxIterations and CacheMode.
    /// Used by both WorkflowAssistantController and AssistantToolProvider to ensure consistent
    /// step construction.
    /// </summary>
    WorkflowStep BuildStep(Guid workflowId, CreateWorkflowStepRequest req);
}

/// <param name="WorkflowId">
/// The workflow this proposal was computed against; <c>null</c> for "create a new workflow". The page that
/// shows the proposal applies it to THIS workflow, not to whichever one it happens to be open on — a token
/// binds the workflow id, so applying to any other one can only ever answer "out of date".
/// </param>
/// <param name="Name">The name the proposal would give a workflow it creates (or renames to); optional.</param>
public record WorkflowEditDiff(
    IReadOnlyList<WorkflowStepDiffEntry> Additions,
    IReadOnlyList<WorkflowStepDiffEntry> Removals,
    IReadOnlyList<WorkflowStepDiffEntry> Modifications,
    string ConfirmationToken,
    IReadOnlyList<CreateWorkflowStepRequest> ProposedSteps,
    Guid? WorkflowId = null,
    string? Name = null)
{
    /// <summary>
    /// The review-loop normalizations applied to <see cref="ProposedSteps"/> before the diff and token
    /// were computed (empty when the proposal was already normal). A proposal surface renders these
    /// rather than recomputing the normalization.
    /// </summary>
    public IReadOnlyList<WorkflowNormalizationChange> NormalizationChanges { get; init; } = [];

    /// <summary>
    /// True when the proposed steps change nothing about the steps already stored: no step added, removed
    /// or modified. A proposal in that state has nothing left to apply, which is how an apply whose token no
    /// longer verifies is told apart from a genuine conflict — see <c>WorkflowAssistantController.Apply</c>.
    /// </summary>
    public bool ChangesNothing =>
        Additions.Count == 0 && Removals.Count == 0 && Modifications.Count == 0
        // Steps are matched by their order, so a proposal that gives two steps the same order is not "the same
        // as what is stored" however the matching turned out — it could never be stored as written.
        && ProposedSteps.Select(step => step.StepOrder).Distinct().Count() == ProposedSteps.Count;
}

public record WorkflowStepDiffEntry(
    int StepOrder,
    Guid AgentDefinitionId,
    string StepType,
    string? Summary);

public record WorkflowValidationResult(
    bool Valid,
    IReadOnlyList<string> Errors);
