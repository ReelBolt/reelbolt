using System.Text.Json.Nodes;

namespace ReelBolt.Inference.Api.Controllers.Dto;

// Phase-2 design-system API surface (SPEC 9, 22). Enums travel as their NAME string so the wire
// shape never changes when a member is appended to an enum.

/// <summary>The approved version a system currently points at, without its document.</summary>
public sealed record DesignSystemCurrentVersionResponse(Guid Id, int VersionNumber, string Digest, DateTime? ApprovedAt);

/// <summary>Phase 4: the library a design system descends from. Always null in Phase 2.</summary>
public sealed record DesignSystemParentLibraryResponse(Guid Id, string Name);

/// <summary>One system summary row (SPEC 9 <c>SystemSummary</c>).</summary>
public sealed record DesignSystemSummaryResponse(
    Guid Id, string Kind, string Name,
    DesignSystemCurrentVersionResponse? CurrentVersion,
    DesignVersionSummaryResponse? LatestDraft,
    int PendingProposals,
    DesignSystemParentLibraryResponse? ParentLibrary);

/// <summary>A version without its document, for lists and the latest-draft slot.</summary>
public sealed record DesignVersionSummaryResponse(
    Guid Id, Guid DesignSystemId, int VersionNumber, string State, string Origin,
    string DocumentDigest, Guid? BaseVersionId, Guid? WorkflowExecutionId,
    Guid CreatedByUserId, Guid? ApprovedByUserId, DateTime? ApprovedAt, DateTime CreatedAt);

/// <summary>One source a version was read from (SPEC 9).</summary>
public sealed record DesignSourceResponse(
    Guid Id, string SourceKind, Guid? ProjectFileId, string Path, string Importer,
    string ContentSha256, string? SourceRevision, long SizeBytes, string Status,
    DateTime LastCheckedAt, DateTime? LastChangedAt);

/// <summary>A full version: document, coverage and sources.</summary>
public sealed record DesignVersionResponse(
    Guid Id, Guid DesignSystemId, int VersionNumber, string State, string Origin,
    JsonObject Document, string DocumentDigest, Guid? BaseVersionId, Guid? WorkflowExecutionId,
    JsonObject? Coverage, IReadOnlyList<DesignSourceResponse> Sources,
    Guid CreatedByUserId, Guid? ApprovedByUserId, DateTime? ApprovedAt, DateTime CreatedAt);

/// <summary>Manual create body. The digest is never carried: it is always recomputed server-side.</summary>
public sealed record CreateDesignVersionRequest(string Kind, JsonObject Document, string? Note = null);

/// <summary>Reject body. Phase 2 has no column for <c>note</c>, so it is accepted and not persisted.</summary>
public sealed record ApproveRejectRequest(string? Note = null);

/// <summary>The two kinds' summaries (SPEC 9 <c>GET /design-system</c>).</summary>
public sealed record DesignSystemsSummaryResponse(
    DesignSystemSummaryResponse? Design, DesignSystemSummaryResponse? ProductMap);

/// <summary>
/// Import body: the listed project files (max 50 after de-duplication) plus the kinds to read them
/// as. <see cref="Kinds"/> absent or null means <c>["design"]</c>.
/// </summary>
public sealed record ImportRequest(IReadOnlyList<Guid> ProjectFileIds, IReadOnlyList<string>? Kinds = null);

/// <summary>Import result: the Draft versions created and everything the importers noticed.</summary>
public sealed record ImportResponse(
    IReadOnlyList<DesignVersionSummaryResponse> Versions,
    IReadOnlyList<ImportDiagnosticDto> Diagnostics,
    ImportReportResponse? Report = null);

/// <summary>One importer diagnostic, with its severity as its NAME string.</summary>
public sealed record ImportDiagnosticDto(string Severity, string Code, string Path, string Message);

/// <summary>
/// The axis-tagged import report as the routes publish it. Every enum-like member is its NAME
/// string, exactly like the capability matrix, so the wire shape does not depend on a serializer
/// setting.
/// </summary>
public sealed record ImportReportResponse(
    string ImporterId, string? Vendor, IReadOnlyList<ImportAxisReportResponse> Axes);

/// <summary>One axis row: what was imported, what was dropped, and the grouped reasons.</summary>
public sealed record ImportAxisReportResponse(
    string Axis, string Support, int Imported, int Dropped,
    IReadOnlyList<ImportDropGroupResponse> Reasons);

/// <summary>One grouped reason code on an axis.</summary>
public sealed record ImportDropGroupResponse(string Code, string Severity, int Count, string Message);

/// <summary>Ingest body: the execution whose Compose artifact is read, and its step order.</summary>
public sealed record IngestRequest(Guid ExecutionId, int StepOrder);

/// <summary>Ingest result: the version ids created (or already present) and their summaries.</summary>
public sealed record IngestResponse(
    IReadOnlyList<Guid> VersionIds,
    IReadOnlyList<DesignVersionSummaryResponse> Versions);

/// <summary>
/// The exact text an agent would be shown for the current approved version, and its length. An
/// empty block with zero characters is the answer when there is nothing to show.
/// </summary>
public sealed record PreviewResponse(string Block, int Chars);

/// <summary>
/// SPEC 16.3 <c>GET /design-system/editor-defaults</c>: the current approved design member's
/// heading font and text colour, already mapped to what the thumbnail editor offers. A null
/// <see cref="FontFamily"/> means the brand's face is not one of the editor's thirteen (the editor
/// keeps its own default); a null <see cref="Color"/> means the member carries no
/// <c>color.text</c>. <see cref="Warnings"/> carries one
/// <c>font_substituted:requested-&gt;used</c> code per substitution.
/// </summary>
public sealed record EditorDefaultsResponse(
    string? FontFamily,
    int FontWeight,
    string? Color,
    string StrokeColor,
    IReadOnlyList<string> Warnings);

/// <summary>
/// SPEC 9.4 <c>POST /builds</c> body: which template to provision (only
/// <c>product-profile-from-code</c> is accepted) and the high figure the caller has seen and
/// accepted, in whole credits.
/// </summary>
public sealed record BuildRequest(string? TemplateKey = null, double? AcceptEstimateCredits = null);

/// <summary>The estimate range a build is priced at, in whole credits.</summary>
public sealed record BuildEstimateResponse(double Low, double High);

/// <summary>SPEC 9.4 <c>202</c>: the queued execution, the template workflow and its estimate.</summary>
public sealed record BuildResponse(Guid ExecutionId, Guid WorkflowId, BuildEstimateResponse Estimate);

/// <summary>
/// SPEC 9 <c>PATCH /versions/{id}/locks</c> body: the token dot-paths to lock, and the ones to
/// unlock. Either list is optional; at most 200 entries may be sent per call.
/// </summary>
public sealed record LockRequest(IReadOnlyList<string>? Lock = null, IReadOnlyList<string>? Unlock = null);

/// <summary>
/// SPEC 9/13.4 change proposal, with its diff as a JSON document. Enum members travel as their
/// NAME string, like every other design-system DTO.
/// </summary>
public sealed record ProposalResponse(
    Guid Id, Guid DesignSystemId, Guid BaseVersionId, Guid ProposedVersionId,
    string Trigger, double DriftScore, JsonObject Diff, string Status, string ProposedDigest,
    Guid? DecidedByUserId, DateTime? DecidedAt, string? DecisionNote, DateTime CreatedAt);

/// <summary>The outcome of an arbitrary two-version diff (SPEC 9 <c>GET /versions/{id}/diff</c>).</summary>
public enum DesignVersionDiffStatus
{
    Ok,
    NotFound,

    /// <summary>The two versions belong to different design systems (kinds), a 400.</summary>
    KindMismatch,
}

/// <summary>
/// A two-version diff plus the kind it was computed for. <see cref="Diff"/> is the
/// <c>DesignDocumentDiffer</c>/<c>ProductMapDiffer</c> output in its SPEC 13.3 wire shape (without
/// <c>driftScore</c>, which only the stored proposal carries).
/// </summary>
public sealed record DesignVersionDiffResult(
    DesignVersionDiffStatus Status, string? Kind = null, object? Diff = null);

/// <summary>
/// Phase 4 (SPEC 16.1) <c>POST /api/v1/org/design-libraries</c> body: the library name and an
/// optional first Draft document validated exactly like a project Draft. Controller-local, like the
/// project DTOs above.
/// </summary>
public sealed record CreateLibraryRequest(string Name, JsonObject? Document);

/// <summary>
/// Phase 4 (SPEC 16.1) one organization library row plus its document container's summary. The
/// container is a <c>design_systems</c> row with <c>project_id NULL</c>; the library is the
/// addressable, tenant-filtered identity.
/// </summary>
public sealed record LibrarySummaryResponse(
    Guid Id,
    Guid OrganizationId,
    string Name,
    Guid DesignSystemId,
    DesignSystemSummaryResponse DesignSystem);

