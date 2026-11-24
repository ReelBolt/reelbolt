using ReelBolt.Shared.Compositions;

namespace ReelBolt.Inference.Api.Controllers.Dto;

/// <summary>Body of <c>POST /api/v1/projects/{projectId}/executions/{executionId}/revisions</c>.</summary>
/// <param name="Instruction">What should change, in plain words (1-4000 characters).</param>
/// <param name="Targets">Optional scenes / frame ranges / time region the change is about.</param>
/// <param name="FromVersionId">Revise this version instead of the run's own (must be in the same lineage).</param>
/// <param name="Mode"><c>Render</c> (default) or <c>PreviewOnly</c>.</param>
/// <param name="CutPolicy">
/// <c>KeepCut</c> (default): keep the approved edit when the changed film still has the same scenes at the
/// same places; <c>ReEdit</c>: re-make every editorial decision that depends on the film too. Ignored when
/// nothing depends on the film (the animation is the deliverable).
/// </param>
public sealed record CreateCompositionRevisionRequest(
    string? Instruction,
    CompositionRevisionTargets? Targets = null,
    Guid? FromVersionId = null,
    string? Mode = null,
    string? CutPolicy = null);

/// <summary>One revision request and the run that carries it out.</summary>
public sealed record CompositionRevisionResponse(
    Guid Id,
    Guid ExecutionId,
    Guid WorkflowId,
    string Status,
    string Instruction,
    CompositionRevisionTargets? Targets,
    string Mode,
    Guid? BaseVersionId,
    int? BaseVersionNumber,
    Guid? ResultVersionId,
    int? ResultVersionNumber,
    DateTime CreatedAt,
    DateTime? CompletedAt,
    string? ErrorMessage,
    string? CutPolicy = null);

/// <summary>One version of a composition workspace.</summary>
public sealed record CompositionVersionResponse(
    Guid Id,
    int VersionNumber,
    Guid? ParentVersionId,
    int? ParentVersionNumber,
    DateTime CreatedAt,
    string? Instruction,
    string Source,
    Guid? ExecutionId,
    Guid? WorkflowId,
    Guid? RenderStepResultId,
    bool HasRender,
    string? CompositionId,
    int? DurationInFrames,
    double? Fps,
    int? Width,
    int? Height,
    int SceneCount,
    IReadOnlyList<RevisionDeliverableResponse>? Deliverables = null,
    string? RunStatus = null,
    bool FinalVideoExpected = false,
    string? CutPolicy = null,
    bool? CutKept = null);

/// <summary>
/// One final video a version's run delivered — the workflow's real deliverable (the last compile, or each
/// compile no later step re-cuts), as opposed to the raw animation render. Play it with
/// <c>GET .../outputs/{stepResultId}/download</c>.
/// </summary>
public sealed record RevisionDeliverableResponse(Guid StepResultId, int StepOrder, string? Label, bool IsPrimary);

/// <summary>One step a revision would make again, and how under each cut choice.</summary>
/// <param name="KeepCut">The step's disposition with "Keep the same cut": <c>Run</c>, <c>KeepIfCompatible</c> or <c>Skip</c>.</param>
/// <param name="ReEdit">…and with "Re-edit the video too".</param>
public sealed record RevisionReplayStepResponse(int StepOrder, string Label, string StepType, string KeepCut, string ReEdit);

/// <summary>
/// What a revision of the run's current version does after the animation is re-rendered: which steps of
/// the workflow run again so the run ends with the workflow's real deliverable.
/// </summary>
/// <param name="Available">False when nothing depends on the animation (it is the deliverable) or the original steps cannot be replayed.</param>
/// <param name="Reason">Plain language, when the steps cannot be replayed although the workflow has some.</param>
/// <param name="HasEditDecisions">True when the cut choice matters (an editorial decision depends on the film).</param>
public sealed record RevisionReplayResponse(
    bool Available,
    string? Reason,
    bool HasEditDecisions,
    IReadOnlyList<RevisionReplayStepResponse> Steps,
    RevisionCostEstimateResponse? KeepCutEstimate,
    RevisionCostEstimateResponse? ReEditEstimate);

/// <summary>A scene of the current version, for the target chips.</summary>
public sealed record RevisionSceneResponse(string Name, int FromFrame, int DurationInFrames, string? Description);

/// <summary>What a revision is expected to cost, in whole credits.</summary>
public sealed record RevisionCostEstimateResponse(
    bool BillingEnabled,
    double LowCredits,
    double HighCredits,
    double RequiredCredits,
    double? BalanceCredits,
    bool Affordable);

/// <summary>The lineage a run belongs to: its versions, its revisions, and whether it can be revised now.</summary>
public sealed record CompositionRevisionHistoryResponse(
    Guid? WorkspaceId,
    string Availability,
    bool CanRevise,
    string? UnavailableReason,
    Guid? CurrentVersionId,
    IReadOnlyList<RevisionSceneResponse> Scenes,
    IReadOnlyList<CompositionVersionResponse> Versions,
    IReadOnlyList<CompositionRevisionResponse> Revisions,
    RevisionCostEstimateResponse? Estimate,
    RevisionReplayResponse? Replay = null,
    Guid? HeadVersionId = null,
    int? HeadVersionNumber = null);
