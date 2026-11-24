using System.Text.Json.Nodes;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.DesignSystems;
using ReelBolt.Shared.DesignSystems.Importers;

namespace ReelBolt.Inference.Api.Services.DesignSystems;

/// <summary>The outcome of a state-changing design-system write (this task's own shape).</summary>
public enum DesignSystemWriteStatus
{
    Ok,
    NotFound,
    NotDraft,
    StaleBase,
    ValidationFailed,
}

/// <summary>
/// A write outcome plus the version it produced and, when validation failed, every violation.
/// The controller is the only place that turns this into HTTP.
/// </summary>
public sealed record DesignSystemWriteResult(
    DesignSystemWriteStatus Status,
    DesignVersionResponse? Version = null,
    IReadOnlyList<DesignViolation>? Violations = null);

/// <summary>What an import run did (this task's own shape).</summary>
public enum DesignSystemImportStatus
{
    Ok,
    NotFound,
    NoFiles,
    TooManyFiles,
    QuotaExceeded,
    NothingRecognized,
    ValidationFailed,
}

/// <summary>
/// The outcome of one import: the Draft versions it created, the diagnostics it accumulated and,
/// when the plan gate refused it, the violation the controller maps to 413/402/429.
/// </summary>
public sealed record DesignSystemImportResult(
    DesignSystemImportStatus Status,
    IReadOnlyList<DesignVersionSummaryResponse>? Versions = null,
    IReadOnlyList<ImportDiagnostic>? Diagnostics = null,
    IReadOnlyList<DesignViolation>? Violations = null,
    PlanGateViolation? Quota = null);

/// <summary>The outcome of deciding a proposal (SPEC 9, 13.4).</summary>
public enum DesignSystemProposalDecisionStatus
{
    Ok,
    NotFound,

    /// <summary>The proposal was already Approved, Rejected or Superseded (double-decide is a 409).</summary>
    NotPending,

    /// <summary>The system's current version moved since the proposal was raised (409 STALE_BASE).</summary>
    StaleBase,

    /// <summary>The proposal's proposed version is no longer a Draft (409 NOT_DRAFT).</summary>
    VersionNotDraft,
}

/// <summary>A proposal decision plus the proposal and version it touched.</summary>
public sealed record DesignSystemProposalResult(
    DesignSystemProposalDecisionStatus Status,
    ProposalResponse? Proposal = null,
    DesignVersionResponse? Version = null);

/// <summary>The outcome of a lock edit on a Draft version (SPEC 9).</summary>
public enum DesignSystemLockStatus
{
    Ok,
    NotFound,
    NotDraft,
    UnknownPath,
    TooManyPaths,
    InvalidDocument,
}

/// <summary>A lock edit's outcome plus the re-digested version, or the offending path.</summary>
public sealed record DesignSystemLockResult(
    DesignSystemLockStatus Status,
    DesignVersionResponse? Version = null,
    string? Path = null);

/// <summary>One source row to persist against a version, with the facts the source table carries.</summary>
public sealed record DesignSourceInput(
    string Path,
    string Importer,
    string ContentSha256,
    Guid? ProjectFileId,
    long SizeBytes,
    string? SourceRevision);

/// <summary>
/// The Phase-2 Inference API core for design systems (SPEC 9): summaries, version lists and reads,
/// manual Draft creation with server-side digest and validation, the approve/reject/restore state
/// machine and the current version's sources. Import lives here too; ingest, builds, pinning and
/// proposals belong to P2-11..P2-13 and Phase 3.
/// </summary>
public interface IDesignSystemService
{
    Task<DesignSystemsSummaryResponse> GetSummaryAsync(Guid projectId, CancellationToken ct);

    Task<IReadOnlyList<DesignVersionSummaryResponse>> ListVersionsAsync(Guid projectId, string kind, CancellationToken ct);

    Task<DesignVersionResponse?> GetVersionAsync(Guid projectId, Guid versionId, CancellationToken ct);

    /// <summary>
    /// The current version of the project's <paramref name="kind"/> system, but only when it is
    /// <see cref="DesignVersionState.Approved"/>; null when there is no system, no current version
    /// or the current version is not approved.
    /// </summary>
    Task<DesignSystemVersion?> GetCurrentApprovedAsync(Guid projectId, string kind, CancellationToken ct);

    /// <summary>
    /// The plain-language warnings for an import's design document against the project's current
    /// approved design: nothing found, or tokens the current version has that the import lacks
    /// (<see cref="DesignImportGuard"/>). Reads only; never refuses or writes.
    /// </summary>
    Task<IReadOnlyList<ImportDiagnostic>> AssessImportAsync(Guid projectId, JsonObject imported, CancellationToken ct);

    Task<DesignSystemWriteResult> CreateDraftAsync(Guid projectId, Guid userId, string kind, JsonObject document,
        DesignVersionOrigin origin, Guid? workflowExecutionId, string? coverageJson,
        IReadOnlyList<ImportSourceRef> sources, Guid? baseVersionId, CancellationToken ct);

    Task<DesignSystemImportResult> ImportAsync(Guid projectId, Guid userId, IReadOnlyList<string> kinds,
        IReadOnlyList<Guid> projectFileIds, CancellationToken ct);

    Task<DesignSystemWriteResult> ApproveAsync(Guid projectId, Guid versionId, Guid userId, CancellationToken ct);

    Task<DesignSystemWriteResult> RejectAsync(Guid projectId, Guid versionId, Guid userId, string? note, CancellationToken ct);

    Task<DesignSystemWriteResult> RestoreAsync(Guid projectId, Guid versionId, Guid userId, CancellationToken ct);

    Task<IReadOnlyList<DesignSourceResponse>> ListSourcesAsync(Guid projectId, string kind, CancellationToken ct);

    /// <summary>SPEC 9 <c>GET /proposals</c>: the current system's proposals, newest first.</summary>
    Task<IReadOnlyList<ProposalResponse>> ListProposalsAsync(
        Guid projectId, string kind, DesignProposalStatus? status, CancellationToken ct);

    /// <summary>
    /// SPEC 9/13.4 <c>POST /proposals/{id}/approve</c>: the proposal and its proposed Draft become
    /// approved in one save, following the exact <see cref="ApproveAsync"/> state machine.
    /// </summary>
    Task<DesignSystemProposalResult> ApproveProposalAsync(
        Guid projectId, Guid proposalId, Guid userId, string? note, CancellationToken ct);

    /// <summary>
    /// SPEC 9/13.4 <c>POST /proposals/{id}/reject</c>: the proposal becomes Rejected and its Draft
    /// (when still a Draft) becomes Rejected, in one save.
    /// </summary>
    Task<DesignSystemProposalResult> RejectProposalAsync(
        Guid projectId, Guid proposalId, Guid userId, string? note, CancellationToken ct);

    /// <summary>
    /// SPEC 9 <c>PATCH /versions/{id}/locks</c>: sets or clears
    /// <c>$extensions.com.reelbolt.locked</c> on the named Draft tokens and re-digests the document.
    /// This is the only mutation path for a Draft version's document.
    /// </summary>
    Task<DesignSystemLockResult> ApplyLocksAsync(
        Guid projectId, Guid versionId, IReadOnlyList<string>? lockPaths, IReadOnlyList<string>? unlockPaths,
        Guid userId, CancellationToken ct);

    /// <summary>
    /// SPEC 9 <c>GET /versions/{id}/diff?against={otherId}</c>: the arbitrary two-version diff, read
    /// older (<paramref name="againstVersionId"/>) → newer (<paramref name="versionId"/>). Both rows
    /// must belong to a system of <paramref name="projectId"/> (else NotFound) and to the same system
    /// (else KindMismatch). The output carries no <c>driftScore</c>.
    /// </summary>
    Task<DesignVersionDiffResult> GetDiffAsync(
        Guid projectId, Guid versionId, Guid againstVersionId, CancellationToken ct);

    // ---------------------------------------------------------------------------------------------
    // Phase 4 (P4-04, SPEC 16.1): the same core over a DesignSystemScope, so the org library surface
    // reuses every validator, quota and state transition instead of a second copy. The project
    // methods above stay the project-facing wrappers; the library path calls these.
    // ---------------------------------------------------------------------------------------------

    /// <summary>The scope's container summary, or null when the scope names no existing row.</summary>
    Task<DesignSystemSummaryResponse?> GetSummaryAsync(DesignSystemScope scope, CancellationToken ct);

    Task<IReadOnlyList<DesignVersionSummaryResponse>> ListVersionsAsync(DesignSystemScope scope, CancellationToken ct);

    Task<DesignVersionResponse?> GetVersionAsync(DesignSystemScope scope, Guid versionId, CancellationToken ct);

    /// <summary>
    /// The scope's current version, but only when it is <see cref="DesignVersionState.Approved"/>; null
    /// when there is no container, no current version or the current version is not approved.
    /// </summary>
    Task<DesignSystemVersion?> GetCurrentApprovedAsync(DesignSystemScope scope, CancellationToken ct);

    Task<DesignSystemWriteResult> CreateDraftAsync(DesignSystemScope scope, Guid userId, JsonObject document,
        DesignVersionOrigin origin, Guid? workflowExecutionId, string? coverageJson, Guid? baseVersionId,
        CancellationToken ct);

    Task<DesignSystemWriteResult> ApproveAsync(DesignSystemScope scope, Guid versionId, Guid userId, CancellationToken ct);

    Task<DesignSystemWriteResult> RejectAsync(DesignSystemScope scope, Guid versionId, Guid userId, string? note, CancellationToken ct);

    Task<DesignSystemWriteResult> RestoreAsync(DesignSystemScope scope, Guid versionId, Guid userId, CancellationToken ct);
}
