using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.DesignSystems;
using ReelBolt.Shared.DesignSystems.Diff;
using ReelBolt.Shared.DesignSystems.Importers;
using ReelBolt.Shared.DesignSystems.ProductMap;
using ReelBolt.Shared.IO;

namespace ReelBolt.Inference.Api.Services.DesignSystems;

/// <summary>
/// The Phase-2 design-system store (SPEC 3.2, 9.3). Version numbers are computed with a bounded
/// read-then-insert retry rather than a raw <c>SELECT max+1 FOR UPDATE</c> so the same code runs on
/// the InMemory test provider and on Postgres, and the 100-version quota is enforced by pruning the
/// oldest Rejected/Superseded rows only.
/// </summary>
public sealed class DesignSystemService(
    InferenceApiDbContext db,
    IFileStorageService storage,
    IPlanGateService planGate,
    ILogger<DesignSystemService> logger) : IDesignSystemService
{
    /// <summary>SPEC 9.3: at most 100 versions per system; Approved and Draft are never pruned.</summary>
    private const int MaxVersionsPerSystem = 100;

    /// <summary>Drafts kept per system; the oldest beyond this are rejected (a Pending proposal's is exempt).</summary>
    internal const int MaxDraftsPerSystem = 20;

    /// <summary>SPEC 9 <c>POST /import</c>: the most project files one import may list.</summary>
    private const int MaxImportFiles = 50;

    /// <summary>SPEC 9 <c>PATCH /versions/{id}/locks</c>: the most paths one call may name.</summary>
    private const int MaxLockPathsPerCall = 200;

    /// <summary>
    /// The closed set of fonts the deployment bundles, defined once in
    /// <see cref="DesignSystemFonts.BundledNames"/> so the importer and the validator agree.
    /// </summary>
    internal static readonly IReadOnlySet<string> BundledFontNames = DesignSystemFonts.BundledNames;

    public async Task<DesignSystemsSummaryResponse> GetSummaryAsync(Guid projectId, CancellationToken ct)
    {
        DesignSystemSummaryResponse? design = await BuildSummaryAsync(projectId, DesignSystemKinds.Design, ct);
        DesignSystemSummaryResponse? productMap = await BuildSummaryAsync(projectId, DesignSystemKinds.ProductMap, ct);
        return new DesignSystemsSummaryResponse(design, productMap);
    }

    public async Task<IReadOnlyList<DesignVersionSummaryResponse>> ListVersionsAsync(
        Guid projectId, string kind, CancellationToken ct)
    {
        DesignSystem? system = await db.DesignSystems
            .FirstOrDefaultAsync(s => s.ProjectId == projectId && s.Kind == kind, ct);
        return system is null ? [] : await ListVersionsForSystemAsync(system, ct);
    }

    /// <summary>The version list for one resolved container, shared by the project and library paths.</summary>
    private async Task<IReadOnlyList<DesignVersionSummaryResponse>> ListVersionsForSystemAsync(
        DesignSystem system, CancellationToken ct)
    {
        List<DesignSystemVersion> versions = await db.DesignSystemVersions
            .Where(v => v.DesignSystemId == system.Id)
            .OrderByDescending(v => v.VersionNumber)
            .Take(MaxVersionsPerSystem)
            .ToListAsync(ct);

        return versions.Select(ToSummary).ToList();
    }

    public async Task<DesignVersionResponse?> GetVersionAsync(Guid projectId, Guid versionId, CancellationToken ct)
    {
        DesignSystemVersion? version = await (
            from v in db.DesignSystemVersions
            join s in db.DesignSystems on v.DesignSystemId equals s.Id
            where v.Id == versionId && s.ProjectId == projectId
            select v).FirstOrDefaultAsync(ct);

        return version is null ? null : await ToResponseAsync(version, ct);
    }

    public async Task<DesignSystemVersion?> GetCurrentApprovedAsync(Guid projectId, string kind, CancellationToken ct)
    {
        DesignSystem? system = await db.DesignSystems
            .FirstOrDefaultAsync(s => s.ProjectId == projectId && s.Kind == kind, ct);
        return await CurrentApprovedForSystemAsync(system, ct);
    }

    public async Task<IReadOnlyList<ImportDiagnostic>> AssessImportAsync(
        Guid projectId, JsonObject imported, CancellationToken ct)
    {
        DesignSystemVersion? current = await GetCurrentApprovedAsync(projectId, DesignSystemKinds.Design, ct);
        JsonObject? currentDocument = null;
        if (current is not null)
        {
            try
            {
                currentDocument = JsonNode.Parse(current.DocumentJson) as JsonObject;
            }
            catch (System.Text.Json.JsonException)
            {
                currentDocument = null;
            }
        }

        return DesignImportGuard.Assess(imported, currentDocument, current?.VersionNumber);
    }

    /// <summary>The current approved version of a resolved container, shared by both paths.</summary>
    private async Task<DesignSystemVersion?> CurrentApprovedForSystemAsync(DesignSystem? system, CancellationToken ct)
    {
        if (system?.CurrentVersionId is not { } currentVersionId)
            return null;

        return await db.DesignSystemVersions
            .FirstOrDefaultAsync(
                v => v.Id == currentVersionId
                    && v.DesignSystemId == system.Id
                    && v.State == DesignVersionState.Approved,
                ct);
    }

    public async Task<DesignSystemWriteResult> CreateDraftAsync(
        Guid projectId, Guid userId, string kind, JsonObject document, DesignVersionOrigin origin,
        Guid? workflowExecutionId, string? coverageJson, IReadOnlyList<ImportSourceRef> sources,
        Guid? baseVersionId, CancellationToken ct)
    {
        DesignValidationContext context = await BuildValidationContextAsync(projectId, ct);

        DesignSystem? system = await db.DesignSystems
            .FirstOrDefaultAsync(s => s.ProjectId == projectId && s.Kind == kind, ct);
        if (system is null)
        {
            Project? project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, ct);
            DateTime now = DateTime.UtcNow;
            system = new DesignSystem
            {
                Id = Guid.NewGuid(),
                ProjectId = projectId,
                Kind = kind,
                Name = project?.Name ?? string.Empty,
                CreatedByUserId = userId,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.DesignSystems.Add(system);
        }

        _ = sources;

        return await CreateDraftOnSystemAsync(
            DesignSystemScope.Project(system.Id, kind, projectId), system, userId, document, origin,
            workflowExecutionId, coverageJson, baseVersionId, context, ct);
    }

    /// <summary>
    /// Persists the provenance an import route (zip, PDF, Figma) collected, against the Draft it
    /// created. Not done inside <c>CreateDraftAsync</c>: its other callers (project-file import, drift)
    /// already persist their own source rows and would double them.
    /// </summary>
    internal static Task PersistImportSourcesAsync(
        InferenceApiDbContext db, DesignVersionResponse created, IReadOnlyList<ImportSourceRef> sources, CancellationToken ct)
    {
        List<DesignSourceInput> inputs = [.. sources.Select(source => new DesignSourceInput(
            source.Path, source.Importer, source.Sha256, source.ProjectFileId, 0, null))];
        return PersistSourcesAsync(db, created.DesignSystemId, created.Id, inputs, ct, SourceKindOf);
    }

    /// <summary>
    /// The kind a route-supplied import source is recorded under: a project file when it names one,
    /// Figma for the Figma variables importer, otherwise an upload (a zip entry or a PDF).
    /// </summary>
    private static DesignSourceKind SourceKindOf(DesignSourceInput source) =>
        source.ProjectFileId is not null ? DesignSourceKind.ProjectFile
        : source.Importer.StartsWith("figma", StringComparison.Ordinal) ? DesignSourceKind.Figma
        : DesignSourceKind.Upload;

    /// <summary>
    /// The one Draft-creation core: validates and canonicalizes the document for the scope's kind,
    /// recomputes the digest server-side, inserts version <c>max+1</c> on the resolved container and
    /// prunes the quota. Shared verbatim by the project and library paths.
    /// </summary>
    private async Task<DesignSystemWriteResult> CreateDraftOnSystemAsync(
        DesignSystemScope scope, DesignSystem system, Guid userId, JsonObject document,
        DesignVersionOrigin origin, Guid? workflowExecutionId, string? coverageJson, Guid? baseVersionId,
        DesignValidationContext context, CancellationToken ct)
    {
        string canonical;
        if (scope.Kind == DesignSystemKinds.Design)
        {
            DesignValidationResult validation = DesignDocumentValidator.Validate(document, context);
            if (!validation.IsValid)
                return new DesignSystemWriteResult(DesignSystemWriteStatus.ValidationFailed, Violations: validation.Violations);

            canonical = CanonicalJson.Serialize(document);
        }
        else if (scope.Kind == DesignSystemKinds.ProductMap)
        {
            ProductMapDocument? map = ProductMapJson.Parse(document.ToJsonString());
            if (map is null)
            {
                return new DesignSystemWriteResult(
                    DesignSystemWriteStatus.ValidationFailed,
                    Violations: [new DesignViolation("PM_PARSE", "", "not a valid product-map document")]);
            }

            DesignValidationResult validation = ProductMapValidator.Validate(map);
            if (!validation.IsValid)
                return new DesignSystemWriteResult(DesignSystemWriteStatus.ValidationFailed, Violations: validation.Violations);

            canonical = ProductMapJson.Serialize(map);
        }
        else
        {
            return new DesignSystemWriteResult(
                DesignSystemWriteStatus.ValidationFailed,
                Violations: [new DesignViolation("DS_KIND", "", "invalid design-system kind")]);
        }

        // The digest is ALWAYS recomputed here; the request carries no digest field.
        string digest = CanonicalJson.Sha256Hex(canonical);
        DateTime now = DateTime.UtcNow;

        var version = new DesignSystemVersion
        {
            Id = Guid.NewGuid(),
            DesignSystemId = system.Id,
            State = DesignVersionState.Draft,
            DocumentJson = canonical,
            DocumentDigest = digest,
            Origin = origin,
            BaseVersionId = baseVersionId,
            WorkflowExecutionId = workflowExecutionId,
            CoverageJson = coverageJson,
            CreatedByUserId = userId,
            CreatedAt = now,
        };

        await InsertVersionAsync(system, version, ct);
        return new DesignSystemWriteResult(DesignSystemWriteStatus.Ok, await ToResponseAsync(version, ct));
    }

    /// <summary>
    /// SPEC 9 <c>POST /import</c>: reads the listed project files (bounded, server-side SHA-256),
    /// runs the static importers and/or the product-map extractor, and creates <c>Import</c> Draft
    /// versions plus their <c>design_system_sources</c> rows. No build, pin, proposals or drift.
    /// </summary>
    public async Task<DesignSystemImportResult> ImportAsync(
        Guid projectId, Guid userId, IReadOnlyList<string> kinds, IReadOnlyList<Guid> projectFileIds,
        CancellationToken ct)
    {
        List<Guid> ids = (projectFileIds ?? []).Distinct().ToList();

        Project? project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, ct);
        if (project is null)
            return new DesignSystemImportResult(DesignSystemImportStatus.NotFound);

        if (ids.Count > MaxImportFiles)
            return new DesignSystemImportResult(DesignSystemImportStatus.TooManyFiles);

        if (ids.Count == 0)
            return new DesignSystemImportResult(DesignSystemImportStatus.NoFiles);

        // Any id whose row is missing or belongs to another project is a 404, checked BEFORE any
        // download so a foreign id never causes storage IO.
        List<ProjectFile> files = await db.ProjectFiles
            .Where(f => ids.Contains(f.Id) && f.CsamStatus != CsamScanStatus.Quarantined)
            .ToListAsync(ct);
        if (files.Count != ids.Count || files.Exists(f => f.ProjectId != projectId))
            return new DesignSystemImportResult(DesignSystemImportStatus.NotFound);

        Dictionary<Guid, ProjectFile> byId = files.ToDictionary(f => f.Id);

        // SPEC 9.3: the catalog mandates the plan-gate call with the summed file sizes even though
        // these files are already in storage.
        long totalBytes = files.Sum(f => f.SizeBytes);
        if (planGate is not null)
        {
            PlanGateViolation? violation = await planGate.CheckUploadAsync(project.OrganizationId, totalBytes, ct);
            if (violation is not null)
                return new DesignSystemImportResult(DesignSystemImportStatus.QuotaExceeded, Quota: violation);
        }

        var diagnostics = new List<ImportDiagnostic>();
        var importFiles = new List<ImportFile>();

        foreach (ProjectFile file in files)
        {
            string path = file.OriginalPath ?? file.OriginalFileName;

            // Size gate BEFORE the download: 2 MiB is the largest importer cap.
            if (file.SizeBytes > ImportLimits.JsonMaxBytes)
            {
                diagnostics.Add(new ImportDiagnostic(
                    ImportDiagnosticSeverity.Warn,
                    "FILE_TOO_LARGE",
                    path,
                    $"the file exceeds the {ImportLimits.JsonMaxBytes}-byte importer cap and was skipped."));
                continue;
            }

            await using Stream stream = await storage.DownloadAsync(projectId, file.StorageKey, ct);
            (byte[] bytes, bool truncated) = await ReadBytesBoundedAsync(stream, ImportLimits.JsonMaxBytes, ct);
            if (truncated)
            {
                diagnostics.Add(new ImportDiagnostic(
                    ImportDiagnosticSeverity.Warn,
                    "FILE_TOO_LARGE",
                    path,
                    $"the file exceeds the {ImportLimits.JsonMaxBytes}-byte importer cap and was skipped."));
                continue;
            }

            string sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
            importFiles.Add(new ImportFile(path, bytes, sha256, file.Id));
        }

        bool wantDesign = kinds.Contains(DesignSystemKinds.Design, StringComparer.Ordinal);
        bool wantProductMap = kinds.Contains(DesignSystemKinds.ProductMap, StringComparer.Ordinal);

        DesignImportResult importResult = new(null, null, [], []);
        ProductMapExtraction? extraction = null;

        if (wantDesign && importFiles.Count > 0)
        {
            importResult = DesignImportRegistry.ImportAll(importFiles);
            diagnostics.AddRange(importResult.Diagnostics);
        }

        if (wantProductMap && importFiles.Count > 0)
        {
            extraction = ProductMapExtractor.Extract(importFiles);
            diagnostics.AddRange(extraction.Diagnostics);
        }

        bool hasDesign = importResult.Design is not null;
        ProductMapDocument? productMap = extraction?.Map;
        if (!hasDesign && (!wantProductMap || productMap is null))
            return new DesignSystemImportResult(DesignSystemImportStatus.NothingRecognized, Diagnostics: diagnostics);

        var versions = new List<DesignVersionSummaryResponse>();
        DesignVersionResponse? designVersion = null;

        if (hasDesign)
        {
            // An import that found nothing, or less than the approved design has, says so in its result.
            diagnostics.AddRange(await AssessImportAsync(projectId, importResult.Design!, ct));

            DesignSystemWriteResult write = await CreateDraftAsync(
                projectId, userId, DesignSystemKinds.Design, importResult.Design!,
                DesignVersionOrigin.Import, workflowExecutionId: null, coverageJson: null,
                sources: importResult.Sources, baseVersionId: null, ct);
            if (write.Status == DesignSystemWriteStatus.ValidationFailed)
                return new DesignSystemImportResult(
                    DesignSystemImportStatus.ValidationFailed, Diagnostics: diagnostics, Violations: write.Violations);

            designVersion = write.Version;
            if (designVersion is not null)
                versions.Add(ToSummary(designVersion));
        }

        if (wantProductMap && productMap is not null)
        {
            string mapJson = ProductMapJson.Serialize(productMap);
            JsonObject mapNode = JsonNode.Parse(mapJson)!.AsObject();

            DesignSystemWriteResult write = await CreateDraftAsync(
                projectId, userId, DesignSystemKinds.ProductMap, mapNode,
                DesignVersionOrigin.Import, workflowExecutionId: null, coverageJson: null,
                sources: [], baseVersionId: null, ct);
            if (write.Status == DesignSystemWriteStatus.ValidationFailed)
                return new DesignSystemImportResult(
                    DesignSystemImportStatus.ValidationFailed, Diagnostics: diagnostics, Violations: write.Violations);

            if (write.Version is { } mapVersion)
                versions.Add(ToSummary(mapVersion));
        }

        // The product_map kind records no sources in Phase 2 (the extractor returns none and there is
        // no registered product-map importer id in SPEC 22).
        if (designVersion is not null)
        {
            var inputs = new List<DesignSourceInput>();
            foreach (ImportSourceRef source in importResult.Sources)
            {
                ProjectFile? row = source.ProjectFileId is { } fileId && byId.TryGetValue(fileId, out ProjectFile? found)
                    ? found
                    : null;
                inputs.Add(new DesignSourceInput(
                    source.Path, source.Importer, source.Sha256, source.ProjectFileId,
                    row?.SizeBytes ?? 0, row?.SourceRevision));
            }

            if (inputs.Count > 0)
                await PersistSourcesAsync(db, designVersion.DesignSystemId, designVersion.Id, inputs, ct);
        }

        logger.LogInformation(
            "design_system.import {ProjectId} {UserId} {VersionIds}",
            projectId, userId, string.Join(',', versions.Select(v => v.Id)));

        return new DesignSystemImportResult(DesignSystemImportStatus.Ok, versions, diagnostics);
    }

    public async Task<DesignSystemWriteResult> ApproveAsync(
        Guid projectId, Guid versionId, Guid userId, CancellationToken ct)
    {
        (DesignSystemVersion? version, DesignSystem? system)? loaded = await LoadOwnedVersionAsync(projectId, versionId, ct);
        if (loaded is not { } owned)
            return new DesignSystemWriteResult(DesignSystemWriteStatus.NotFound);

        DesignSystemVersion version = owned.version!;
        DesignSystem system = owned.system!;

        DesignSystemWriteStatus guard = GuardApproval(system, version);
        if (guard != DesignSystemWriteStatus.Ok)
            return new DesignSystemWriteResult(guard);

        await ApplyApprovalAsync(system, version, userId, DateTime.UtcNow, ct);

        if (!await TrySaveDecisionAsync(ct))
            return new DesignSystemWriteResult(DesignSystemWriteStatus.StaleBase);

        return new DesignSystemWriteResult(DesignSystemWriteStatus.Ok, await ToResponseAsync(version, ct));
    }

    /// <summary>
    /// Saves an approve/reject decision. <c>design_systems.current_version_id</c> is a concurrency
    /// token and every decision writes the system row, so two decisions that read the same system
    /// cannot both commit: the loser gets <c>false</c> and the caller answers 409 STALE_BASE.
    /// </summary>
    private async Task<bool> TrySaveDecisionAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateConcurrencyException ex)
        {
            logger.LogInformation(ex, "A concurrent design-system decision won; refusing this one as stale");
            return false;
        }
    }

    /// <summary>
    /// The approve state machine's guard, shared verbatim by the version and proposal approval paths
    /// (SPEC 9). A non-null base means the user reviewed against one approved version; a system that
    /// has moved on since is <see cref="DesignSystemWriteStatus.StaleBase"/>.
    /// </summary>
    private static DesignSystemWriteStatus GuardApproval(DesignSystem system, DesignSystemVersion version)
    {
        if (version.State != DesignVersionState.Draft)
            return DesignSystemWriteStatus.NotDraft;

        // A restore's base is the old version it copies, which is by definition not current; its
        // approval is still serialised by the concurrency token on the system row.
        if (version.Origin != DesignVersionOrigin.Restore
            && version.BaseVersionId is { } baseVersionId && baseVersionId != system.CurrentVersionId)
            return DesignSystemWriteStatus.StaleBase;

        return DesignSystemWriteStatus.Ok;
    }

    /// <summary>
    /// The approve state machine's mutation, shared by both approval paths and deliberately not
    /// saving: the caller folds it into its own single <c>SaveChangesAsync</c> so the version's
    /// approval and (for a proposal) the proposal's decision commit together.
    /// </summary>
    private async Task ApplyApprovalAsync(
        DesignSystem system, DesignSystemVersion version, Guid userId, DateTime now, CancellationToken ct)
    {
        if (system.CurrentVersionId is { } currentVersionId && currentVersionId != version.Id)
        {
            DesignSystemVersion? previous = await db.DesignSystemVersions
                .FirstOrDefaultAsync(v => v.Id == currentVersionId && v.DesignSystemId == system.Id, ct);
            if (previous is { State: DesignVersionState.Approved })
                previous.State = DesignVersionState.Superseded;
        }

        version.State = DesignVersionState.Approved;
        version.ApprovedByUserId = userId;
        version.ApprovedAt = now;
        system.CurrentVersionId = version.Id;
        system.UpdatedAt = now;
    }

    public async Task<DesignSystemWriteResult> RejectAsync(
        Guid projectId, Guid versionId, Guid userId, string? note, CancellationToken ct)
    {
        // SPEC 9.2 does not give reject a note column; Phase 3 proposals persist `decision_note`.
        _ = note;

        (DesignSystemVersion? version, DesignSystem? system)? loaded = await LoadOwnedVersionAsync(projectId, versionId, ct);
        if (loaded is not { } owned)
            return new DesignSystemWriteResult(DesignSystemWriteStatus.NotFound);

        DesignSystemVersion version = owned.version!;
        if (version.State != DesignVersionState.Draft)
            return new DesignSystemWriteResult(DesignSystemWriteStatus.NotDraft);

        version.State = DesignVersionState.Rejected;
        owned.system!.UpdatedAt = DateTime.UtcNow;
        if (!await TrySaveDecisionAsync(ct))
            return new DesignSystemWriteResult(DesignSystemWriteStatus.StaleBase);

        return new DesignSystemWriteResult(DesignSystemWriteStatus.Ok, await ToResponseAsync(version, ct));
    }

    public async Task<IReadOnlyList<ProposalResponse>> ListProposalsAsync(
        Guid projectId, string kind, DesignProposalStatus? status, CancellationToken ct)
    {
        DesignSystem? system = await db.DesignSystems
            .FirstOrDefaultAsync(s => s.ProjectId == projectId && s.Kind == kind, ct);
        if (system is null)
            return [];

        IQueryable<DesignSystemChangeProposal> query = db.DesignSystemChangeProposals
            .Where(p => p.DesignSystemId == system.Id);
        if (status is { } wanted)
            query = query.Where(p => p.Status == wanted);

        List<DesignSystemChangeProposal> proposals = await query
            .OrderByDescending(p => p.CreatedAt)
            .Take(100)
            .ToListAsync(ct);

        return proposals.Select(ToProposalResponse).ToList();
    }

    public async Task<DesignSystemProposalResult> ApproveProposalAsync(
        Guid projectId, Guid proposalId, Guid userId, string? note, CancellationToken ct)
    {
        (DesignSystemChangeProposal? proposal, DesignSystem? system)? loaded =
            await LoadOwnedProposalAsync(projectId, proposalId, ct);
        if (loaded is not { } owned)
            return new DesignSystemProposalResult(DesignSystemProposalDecisionStatus.NotFound);

        DesignSystemChangeProposal proposal = owned.proposal!;
        DesignSystem system = owned.system!;

        // A decided proposal is a 409, not a second state transition (double-approve).
        if (proposal.Status != DesignProposalStatus.Pending)
            return new DesignSystemProposalResult(DesignSystemProposalDecisionStatus.NotPending);

        DesignSystemVersion? version = await db.DesignSystemVersions
            .FirstOrDefaultAsync(v => v.Id == proposal.ProposedVersionId && v.DesignSystemId == system.Id, ct);
        if (version is null)
            return new DesignSystemProposalResult(DesignSystemProposalDecisionStatus.NotFound);

        DesignSystemWriteStatus guard = GuardApproval(system, version);
        if (guard != DesignSystemWriteStatus.Ok)
        {
            return new DesignSystemProposalResult(guard == DesignSystemWriteStatus.StaleBase
                ? DesignSystemProposalDecisionStatus.StaleBase
                : DesignSystemProposalDecisionStatus.VersionNotDraft);
        }

        DateTime now = DateTime.UtcNow;

        // The proposal decision rides the version approval's single save, so the two rows can never
        // disagree about what the system points at.
        await ApplyApprovalAsync(system, version, userId, now, ct);
        proposal.Status = DesignProposalStatus.Approved;
        proposal.DecidedByUserId = userId;
        proposal.DecidedAt = now;
        proposal.DecisionNote = note;

        if (!await TrySaveDecisionAsync(ct))
            return new DesignSystemProposalResult(DesignSystemProposalDecisionStatus.StaleBase);

        return new DesignSystemProposalResult(
            DesignSystemProposalDecisionStatus.Ok,
            ToProposalResponse(proposal),
            await ToResponseAsync(version, ct));
    }

    public async Task<DesignSystemProposalResult> RejectProposalAsync(
        Guid projectId, Guid proposalId, Guid userId, string? note, CancellationToken ct)
    {
        (DesignSystemChangeProposal? proposal, DesignSystem? system)? loaded =
            await LoadOwnedProposalAsync(projectId, proposalId, ct);
        if (loaded is not { } owned)
            return new DesignSystemProposalResult(DesignSystemProposalDecisionStatus.NotFound);

        DesignSystemChangeProposal proposal = owned.proposal!;
        DesignSystem system = owned.system!;

        if (proposal.Status != DesignProposalStatus.Pending)
            return new DesignSystemProposalResult(DesignSystemProposalDecisionStatus.NotPending);

        DesignSystemVersion? version = await db.DesignSystemVersions
            .FirstOrDefaultAsync(v => v.Id == proposal.ProposedVersionId && v.DesignSystemId == system.Id, ct);

        DateTime now = DateTime.UtcNow;
        if (version is { State: DesignVersionState.Draft })
        {
            version.State = DesignVersionState.Rejected;
            system.UpdatedAt = now;
        }

        proposal.Status = DesignProposalStatus.Rejected;
        proposal.DecidedByUserId = userId;
        proposal.DecidedAt = now;
        proposal.DecisionNote = note;

        if (!await TrySaveDecisionAsync(ct))
            return new DesignSystemProposalResult(DesignSystemProposalDecisionStatus.StaleBase);

        return new DesignSystemProposalResult(
            DesignSystemProposalDecisionStatus.Ok,
            ToProposalResponse(proposal),
            version is null ? null : await ToResponseAsync(version, ct));
    }

    public async Task<DesignSystemLockResult> ApplyLocksAsync(
        Guid projectId, Guid versionId, IReadOnlyList<string>? lockPaths, IReadOnlyList<string>? unlockPaths,
        Guid userId, CancellationToken ct)
    {
        int requested = (lockPaths?.Count ?? 0) + (unlockPaths?.Count ?? 0);
        if (requested > MaxLockPathsPerCall)
            return new DesignSystemLockResult(DesignSystemLockStatus.TooManyPaths);

        (DesignSystemVersion? version, DesignSystem? system)? loaded = await LoadOwnedVersionAsync(projectId, versionId, ct);
        if (loaded is not { } owned)
            return new DesignSystemLockResult(DesignSystemLockStatus.NotFound);

        DesignSystemVersion version = owned.version!;
        DesignSystem system = owned.system!;

        if (version.State != DesignVersionState.Draft)
            return new DesignSystemLockResult(DesignSystemLockStatus.NotDraft);

        JsonObject? document = ParseObject(version.DocumentJson);
        if (document is null)
            return new DesignSystemLockResult(DesignSystemLockStatus.InvalidDocument);

        // Only real tokens (a node with $value) may be locked; a group path is an unknown path.
        HashSet<string> tokens = DesignDocument.Tokens(document)
            .Select(t => t.Path)
            .ToHashSet(StringComparer.Ordinal);

        foreach (string path in Paths(lockPaths).Concat(Paths(unlockPaths)))
        {
            if (!tokens.Contains(path))
                return new DesignSystemLockResult(DesignSystemLockStatus.UnknownPath, Path: path);
        }

        foreach (string path in Paths(lockPaths))
            SetLocked(document, path, locked: true);
        foreach (string path in Paths(unlockPaths))
            SetLocked(document, path, locked: false);

        string canonical = CanonicalJson.Serialize(document);
        version.DocumentJson = canonical;
        version.DocumentDigest = CanonicalJson.Sha256Hex(canonical);
        system.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);

        // Who locked what: the version row has no column for it, so the audit trail is this line (user,
        // version and counts only — never a token path or value).
        logger.LogInformation(
            "design_system.locks {ProjectId} {VersionId} {UserId} locked={Locked} unlocked={Unlocked}",
            projectId, versionId, userId, Paths(lockPaths).Count(), Paths(unlockPaths).Count());

        return new DesignSystemLockResult(
            DesignSystemLockStatus.Ok,
            await ToResponseAsync(version, ct));
    }

    /// <summary>
    /// SPEC 9 <c>GET /versions/{id}/diff?against={otherId}</c>. The <c>against</c> row is the older
    /// side, the addressed row the newer, so added/removed/changed read oldest → newest. Both rows
    /// are loaded through <c>design_systems.project_id</c> so a foreign id is a 404.
    /// </summary>
    public async Task<DesignVersionDiffResult> GetDiffAsync(
        Guid projectId, Guid versionId, Guid againstVersionId, CancellationToken ct)
    {
        DesignSystemVersion? after = await OwnedVersionAsync(projectId, versionId, ct);
        DesignSystemVersion? before = await OwnedVersionAsync(projectId, againstVersionId, ct);
        if (after is null || before is null)
            return new DesignVersionDiffResult(DesignVersionDiffStatus.NotFound);

        if (after.DesignSystemId != before.DesignSystemId)
            return new DesignVersionDiffResult(DesignVersionDiffStatus.KindMismatch);

        DesignSystem? system = await db.DesignSystems
            .FirstOrDefaultAsync(s => s.Id == after.DesignSystemId, ct);
        if (system is null)
            return new DesignVersionDiffResult(DesignVersionDiffStatus.NotFound);

        if (system.Kind == DesignSystemKinds.ProductMap)
        {
            ProductMapDocument? beforeMap = ProductMapJson.Parse(before.DocumentJson);
            ProductMapDocument? afterMap = ProductMapJson.Parse(after.DocumentJson);
            JsonObject product = ProductMapDiffer.Diff(beforeMap, afterMap).ToJson();
            return new DesignVersionDiffResult(
                DesignVersionDiffStatus.Ok,
                DesignSystemKinds.ProductMap,
                new JsonObject { ["product"] = product });
        }

        JsonObject? beforeDoc = ParseObject(before.DocumentJson);
        JsonObject afterDoc = ParseObject(after.DocumentJson) ?? new JsonObject();
        return new DesignVersionDiffResult(
            DesignVersionDiffStatus.Ok,
            DesignSystemKinds.Design,
            DesignDocumentDiffer.Diff(beforeDoc, afterDoc).ToJson());
    }

    /// <summary>The version when it belongs to a system of <paramref name="projectId"/>, else null.</summary>
    private Task<DesignSystemVersion?> OwnedVersionAsync(Guid projectId, Guid versionId, CancellationToken ct) =>
        (from v in db.DesignSystemVersions
         join s in db.DesignSystems on v.DesignSystemId equals s.Id
         where v.Id == versionId && s.ProjectId == projectId
         select v).FirstOrDefaultAsync(ct);

    private static IEnumerable<string> Paths(IReadOnlyList<string>? paths) =>
        (paths ?? []).Where(p => !string.IsNullOrWhiteSpace(p));

    /// <summary>
    /// Sets <c>$value</c>'s sibling <c>$extensions.com.reelbolt.locked</c>, creating the extension
    /// objects when a token has none. <paramref name="path"/> is known to name a real token.
    /// </summary>
    private static void SetLocked(JsonObject document, string path, bool locked)
    {
        JsonObject? token = DesignDocument.FindToken(document, path);
        if (token is null)
            return;

        if (token["$extensions"] is not JsonObject extensions)
        {
            extensions = new JsonObject();
            token["$extensions"] = extensions;
        }

        if (extensions["com.reelbolt"] is not JsonObject reelbolt)
        {
            reelbolt = new JsonObject();
            extensions["com.reelbolt"] = reelbolt;
        }

        reelbolt["locked"] = locked;
    }

    private async Task<(DesignSystemChangeProposal? proposal, DesignSystem? system)?> LoadOwnedProposalAsync(
        Guid projectId, Guid proposalId, CancellationToken ct)
    {
        DesignSystemChangeProposal? proposal = await db.DesignSystemChangeProposals
            .FirstOrDefaultAsync(p => p.Id == proposalId, ct);
        if (proposal is null)
            return null;

        DesignSystem? system = await db.DesignSystems
            .FirstOrDefaultAsync(s => s.Id == proposal.DesignSystemId, ct);
        if (system is null || system.ProjectId != projectId)
            return null;

        return (proposal, system);
    }

    public async Task<DesignSystemWriteResult> RestoreAsync(
        Guid projectId, Guid versionId, Guid userId, CancellationToken ct)
    {
        (DesignSystemVersion? version, DesignSystem? system)? loaded = await LoadOwnedVersionAsync(projectId, versionId, ct);
        if (loaded is not { } owned)
            return new DesignSystemWriteResult(DesignSystemWriteStatus.NotFound);

        DesignSystemVersion source = owned.version!;
        DesignSystem system = owned.system!;

        if (source.State == DesignVersionState.Draft)
            return new DesignSystemWriteResult(DesignSystemWriteStatus.NotDraft);

        // A restore is a NEW Draft that copies the document; the source keeps its state and its
        // sources, which stay attached to it (Phase 3 drift owns source refresh).
        var copy = new DesignSystemVersion
        {
            Id = Guid.NewGuid(),
            DesignSystemId = system.Id,
            State = DesignVersionState.Draft,
            Origin = DesignVersionOrigin.Restore,
            DocumentJson = source.DocumentJson,
            DocumentDigest = source.DocumentDigest,
            CoverageJson = source.CoverageJson,
            BaseVersionId = source.Id,
            CreatedByUserId = userId,
            CreatedAt = DateTime.UtcNow,
        };

        await InsertVersionAsync(system, copy, ct);
        return new DesignSystemWriteResult(DesignSystemWriteStatus.Ok, await ToResponseAsync(copy, ct));
    }

    public async Task<IReadOnlyList<DesignSourceResponse>> ListSourcesAsync(
        Guid projectId, string kind, CancellationToken ct)
    {
        DesignSystem? system = await db.DesignSystems
            .FirstOrDefaultAsync(s => s.ProjectId == projectId && s.Kind == kind, ct);
        if (system?.CurrentVersionId is not { } currentVersionId)
            return [];

        List<DesignSystemSource> sources = await db.DesignSystemSources
            .Where(s => s.VersionId == currentVersionId)
            .OrderBy(s => s.Path)
            .ToListAsync(ct);

        return sources.Select(ToSourceResponse).ToList();
    }

    private async Task<DesignSystemSummaryResponse?> BuildSummaryAsync(Guid projectId, string kind, CancellationToken ct)
    {
        DesignSystem? system = await db.DesignSystems
            .FirstOrDefaultAsync(s => s.ProjectId == projectId && s.Kind == kind, ct);
        return system is null ? null : await BuildSummaryForSystemAsync(system, ct);
    }

    /// <summary>
    /// The organization library a project's design system descends from, or null. Only a library of the
    /// project's own organization is ever reported (a stale or foreign link reads as unlinked).
    /// </summary>
    private async Task<DesignSystemParentLibraryResponse?> ParentLibraryOfAsync(DesignSystem system, CancellationToken ct)
    {
        if (system.ProjectId is not { } projectId || system.ParentLibraryId is not { } libraryId)
            return null;

        Guid? organizationId = await db.Projects
            .Where(p => p.Id == projectId)
            .Select(p => (Guid?)p.OrganizationId)
            .FirstOrDefaultAsync(ct);
        if (organizationId is null)
            return null;

        return await db.DesignSystemLibraries
            .Where(l => l.Id == libraryId && l.OrganizationId == organizationId)
            .Select(l => new DesignSystemParentLibraryResponse(l.Id, l.Name))
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>The summary for one resolved container, shared by the project and library paths.</summary>
    private async Task<DesignSystemSummaryResponse> BuildSummaryForSystemAsync(DesignSystem system, CancellationToken ct)
    {
        DesignSystemVersion? current = null;
        if (system.CurrentVersionId is { } currentVersionId)
        {
            current = await db.DesignSystemVersions
                .FirstOrDefaultAsync(v => v.Id == currentVersionId && v.DesignSystemId == system.Id, ct);
        }

        DesignSystemVersion? latestDraft = await db.DesignSystemVersions
            .Where(v => v.DesignSystemId == system.Id && v.State == DesignVersionState.Draft)
            .OrderByDescending(v => v.VersionNumber)
            .FirstOrDefaultAsync(ct);

        int pendingProposals = await db.DesignSystemChangeProposals
            .CountAsync(p => p.DesignSystemId == system.Id && p.Status == DesignProposalStatus.Pending, ct);

        return new DesignSystemSummaryResponse(
            system.Id,
            system.Kind,
            system.Name,
            current is null
                ? null
                : new DesignSystemCurrentVersionResponse(current.Id, current.VersionNumber, current.DocumentDigest, current.ApprovedAt),
            latestDraft is null ? null : ToSummary(latestDraft),
            pendingProposals,
            await ParentLibraryOfAsync(system, ct));
    }

    // ---------------------------------------------------------------------------------------------
    // Phase 4 (P4-04, SPEC 16.1): the same core over a DesignSystemScope. The library resolver has
    // already proved org membership on the filtered design_system_libraries set; these methods never
    // look a container up by a caller-supplied design_systems id.
    // ---------------------------------------------------------------------------------------------

    public async Task<DesignSystemSummaryResponse?> GetSummaryAsync(DesignSystemScope scope, CancellationToken ct)
    {
        DesignSystem? system = await FindScopedSystemAsync(scope, ct);
        return system is null ? null : await BuildSummaryForSystemAsync(system, ct);
    }

    public async Task<IReadOnlyList<DesignVersionSummaryResponse>> ListVersionsAsync(
        DesignSystemScope scope, CancellationToken ct)
    {
        DesignSystem? system = await FindScopedSystemAsync(scope, ct);
        return system is null ? [] : await ListVersionsForSystemAsync(system, ct);
    }

    public async Task<DesignVersionResponse?> GetVersionAsync(
        DesignSystemScope scope, Guid versionId, CancellationToken ct)
    {
        DesignSystemVersion? version = await OwnedVersionAsync(scope, versionId, ct);
        return version is null ? null : await ToResponseAsync(version, ct);
    }

    public async Task<DesignSystemVersion?> GetCurrentApprovedAsync(DesignSystemScope scope, CancellationToken ct)
    {
        DesignSystem? system = await FindScopedSystemAsync(scope, ct);
        return await CurrentApprovedForSystemAsync(system, ct);
    }

    public async Task<DesignSystemWriteResult> CreateDraftAsync(
        DesignSystemScope scope, Guid userId, JsonObject document, DesignVersionOrigin origin,
        Guid? workflowExecutionId, string? coverageJson, Guid? baseVersionId, CancellationToken ct)
    {
        DesignSystem? system = await FindScopedSystemAsync(scope, ct);
        if (system is null)
            return new DesignSystemWriteResult(DesignSystemWriteStatus.NotFound);

        // A library container owns no project files, so every project-file id in the document is
        // foreign. Its OWN library assets are referenceable, though, so the library context resolves
        // the asset ids of the library row this container belongs to. The controller is responsible
        // for creating the container row before the first Draft.
        DesignValidationContext context = await BuildScopeValidationContextAsync(scope, system, ct);
        return await CreateDraftOnSystemAsync(
            scope, system, userId, document, origin, workflowExecutionId, coverageJson, baseVersionId, context, ct);
    }

    public async Task<DesignSystemWriteResult> ApproveAsync(
        DesignSystemScope scope, Guid versionId, Guid userId, CancellationToken ct)
    {
        (DesignSystemVersion? version, DesignSystem? system)? loaded = await LoadOwnedVersionAsync(scope, versionId, ct);
        if (loaded is not { } owned)
            return new DesignSystemWriteResult(DesignSystemWriteStatus.NotFound);

        DesignSystemVersion version = owned.version!;
        DesignSystem system = owned.system!;

        DesignSystemWriteStatus guard = GuardApproval(system, version);
        if (guard != DesignSystemWriteStatus.Ok)
            return new DesignSystemWriteResult(guard);

        await ApplyApprovalAsync(system, version, userId, DateTime.UtcNow, ct);

        if (!await TrySaveDecisionAsync(ct))
            return new DesignSystemWriteResult(DesignSystemWriteStatus.StaleBase);

        return new DesignSystemWriteResult(DesignSystemWriteStatus.Ok, await ToResponseAsync(version, ct));
    }

    public async Task<DesignSystemWriteResult> RejectAsync(
        DesignSystemScope scope, Guid versionId, Guid userId, string? note, CancellationToken ct)
    {
        _ = note;

        (DesignSystemVersion? version, DesignSystem? system)? loaded = await LoadOwnedVersionAsync(scope, versionId, ct);
        if (loaded is not { } owned)
            return new DesignSystemWriteResult(DesignSystemWriteStatus.NotFound);

        DesignSystemVersion version = owned.version!;
        if (version.State != DesignVersionState.Draft)
            return new DesignSystemWriteResult(DesignSystemWriteStatus.NotDraft);

        version.State = DesignVersionState.Rejected;
        owned.system!.UpdatedAt = DateTime.UtcNow;
        if (!await TrySaveDecisionAsync(ct))
            return new DesignSystemWriteResult(DesignSystemWriteStatus.StaleBase);

        return new DesignSystemWriteResult(DesignSystemWriteStatus.Ok, await ToResponseAsync(version, ct));
    }

    public async Task<DesignSystemWriteResult> RestoreAsync(
        DesignSystemScope scope, Guid versionId, Guid userId, CancellationToken ct)
    {
        (DesignSystemVersion? version, DesignSystem? system)? loaded = await LoadOwnedVersionAsync(scope, versionId, ct);
        if (loaded is not { } owned)
            return new DesignSystemWriteResult(DesignSystemWriteStatus.NotFound);

        DesignSystemVersion source = owned.version!;
        DesignSystem system = owned.system!;

        if (source.State == DesignVersionState.Draft)
            return new DesignSystemWriteResult(DesignSystemWriteStatus.NotDraft);

        // A restore is a NEW Draft that copies the document; the source keeps its state and its
        // sources, which stay attached to it (Phase 3 drift owns source refresh).
        var copy = new DesignSystemVersion
        {
            Id = Guid.NewGuid(),
            DesignSystemId = system.Id,
            State = DesignVersionState.Draft,
            Origin = DesignVersionOrigin.Restore,
            DocumentJson = source.DocumentJson,
            DocumentDigest = source.DocumentDigest,
            CoverageJson = source.CoverageJson,
            BaseVersionId = source.Id,
            CreatedByUserId = userId,
            CreatedAt = DateTime.UtcNow,
        };

        await InsertVersionAsync(system, copy, ct);
        return new DesignSystemWriteResult(DesignSystemWriteStatus.Ok, await ToResponseAsync(copy, ct));
    }

    /// <summary>The scope's resolved container row, or null when the scope names none.</summary>
    private async Task<DesignSystem?> FindScopedSystemAsync(DesignSystemScope scope, CancellationToken ct)
    {
        if (scope.DesignSystemId == Guid.Empty)
            return null;

        if (!scope.IsLibrary)
        {
            return await db.DesignSystems.FirstOrDefaultAsync(
                s => s.Id == scope.DesignSystemId && s.ProjectId == scope.ProjectId, ct);
        }

        // A library container is a project-less row. The org check runs on the filtered library set
        // (the named Tenant filter), so a hand-built scope can never name another org's container.
        if (scope.OrganizationId == Guid.Empty)
            return null;

        bool ownedByOrganization = await db.DesignSystemLibraries.AnyAsync(
            l => l.DesignSystemId == scope.DesignSystemId && l.OrganizationId == scope.OrganizationId, ct);
        if (!ownedByOrganization)
            return null;

        return await db.DesignSystems.FirstOrDefaultAsync(
            s => s.Id == scope.DesignSystemId && s.ProjectId == null, ct);
    }

    /// <summary>The version when it belongs to the scope's container, else null (foreign id is 404).</summary>
    private async Task<DesignSystemVersion?> OwnedVersionAsync(
        DesignSystemScope scope, Guid versionId, CancellationToken ct)
    {
        (DesignSystemVersion? version, DesignSystem? system)? loaded = await LoadOwnedVersionAsync(scope, versionId, ct);
        return loaded?.version;
    }

    /// <summary>The scope's owned-version load: both absent, or a version of a foreign container, is null.</summary>
    private async Task<(DesignSystemVersion? version, DesignSystem? system)?> LoadOwnedVersionAsync(
        DesignSystemScope scope, Guid versionId, CancellationToken ct)
    {
        DesignSystemVersion? version = await db.DesignSystemVersions
            .FirstOrDefaultAsync(v => v.Id == versionId, ct);
        if (version is null)
            return null;

        DesignSystem? system = await FindScopedSystemAsync(scope, ct);
        if (system is null || version.DesignSystemId != system.Id)
            return null;

        return (version, system);
    }

    /// <summary>
    /// Loads a version together with its owning system, answering null when either is absent or the
    /// system belongs to another project (SPEC 9: foreign ids are 404, never 403).
    /// </summary>
    private async Task<(DesignSystemVersion? version, DesignSystem? system)?> LoadOwnedVersionAsync(
        Guid projectId, Guid versionId, CancellationToken ct)
    {
        DesignSystemVersion? version = await db.DesignSystemVersions
            .FirstOrDefaultAsync(v => v.Id == versionId, ct);
        if (version is null)
            return null;

        DesignSystem? system = await db.DesignSystems
            .FirstOrDefaultAsync(s => s.Id == version.DesignSystemId, ct);
        if (system is null || system.ProjectId != projectId)
            return null;

        return (version, system);
    }

    private async Task<DesignValidationContext> BuildValidationContextAsync(Guid projectId, CancellationToken ct)
    {
        List<Guid> ids = await db.ProjectFiles
            .Where(f => f.ProjectId == projectId && f.CsamStatus != CsamScanStatus.Quarantined)
            .Select(f => f.Id)
            .ToListAsync(ct);
        HashSet<Guid> set = ids.ToHashSet();

        // A project's design system may descend from one organization library. That library's assets
        // are referenceable by a library-asset: ref; assets of any other library are not, and a
        // project with no link has no library context at all.
        HashSet<Guid> assetIds = await LoadLinkedLibraryAssetIdsAsync(projectId, ct);
        return new DesignValidationContext(set.Contains, BundledFontNames, assetIds.Contains);
    }

    /// <summary>
    /// The validator context for a scope's container. A library sees the assets of its own library row;
    /// a project sees the assets of the library it descends from (normally one). Every answer here is
    /// already scoped: the library row is resolved against the scope's organization.
    /// </summary>
    private async Task<DesignValidationContext> BuildScopeValidationContextAsync(
        DesignSystemScope scope, DesignSystem system, CancellationToken ct)
    {
        HashSet<Guid> assetIds;
        if (scope.IsLibrary)
        {
            Guid? libraryId = await db.DesignSystemLibraries
                .Where(l => l.DesignSystemId == system.Id && l.OrganizationId == scope.OrganizationId)
                .Select(l => (Guid?)l.Id)
                .FirstOrDefaultAsync(ct);
            assetIds = await LoadLibraryAssetIdsAsync(
                libraryId is { } id ? [id] : [], ct);
            return new DesignValidationContext(_ => false, BundledFontNames, assetIds.Contains);
        }

        assetIds = system.ParentLibraryId is { } parentLibraryId
            ? await LoadLibraryAssetIdsAsync([parentLibraryId], ct)
            : [];
        return new DesignValidationContext(_ => false, BundledFontNames, assetIds.Contains);
    }

    /// <summary>The asset ids of every library a project's design-system row descends from.</summary>
    private async Task<HashSet<Guid>> LoadLinkedLibraryAssetIdsAsync(Guid projectId, CancellationToken ct)
    {
        List<Guid> libraryIds = await db.DesignSystems
            .Where(s => s.ProjectId == projectId && s.ParentLibraryId != null)
            .Select(s => s.ParentLibraryId!.Value)
            .ToListAsync(ct);
        return await LoadLibraryAssetIdsAsync(libraryIds, ct);
    }

    /// <summary>The asset ids of the named libraries (empty when none are named).</summary>
    private async Task<HashSet<Guid>> LoadLibraryAssetIdsAsync(
        IReadOnlyCollection<Guid> libraryIds, CancellationToken ct)
    {
        if (libraryIds.Count == 0)
            return [];

        List<Guid> ids = await db.DesignSystemLibraryAssets
            .Where(a => libraryIds.Contains(a.LibraryId))
            .Select(a => a.Id)
            .ToListAsync(ct);
        return ids.ToHashSet();
    }

    /// <summary>
    /// Inserts a version with <c>max(VersionNumber) + 1</c> and prunes the quota. A unique violation
    /// on <c>(design_system_id, version_number)</c> (a concurrent insert) is retried by reloading the
    /// max, at most three attempts.
    /// </summary>
    private async Task InsertVersionAsync(DesignSystem system, DesignSystemVersion version, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            int max = await db.DesignSystemVersions
                .Where(v => v.DesignSystemId == system.Id)
                .Select(v => (int?)v.VersionNumber)
                .MaxAsync(ct) ?? 0;

            version.VersionNumber = max + 1;
            db.DesignSystemVersions.Add(version);

            try
            {
                await db.SaveChangesAsync(ct);
                break;
            }
            catch (DbUpdateException) when (attempt < 3)
            {
                db.Entry(version).State = EntityState.Detached;

                // The first Draft of a system inserts the system row in the same save. When a concurrent
                // request committed that row first, re-adding the tracked, still-Added row would collide
                // again on every retry: drop it and continue under the row that won.
                if (db.Entry(system).State == EntityState.Added)
                {
                    db.Entry(system).State = EntityState.Detached;
                    DesignSystem? winner = await db.DesignSystems
                        .FirstOrDefaultAsync(s => s.ProjectId == system.ProjectId && s.Kind == system.Kind, ct);
                    if (winner is null)
                        throw;

                    system = winner;
                    version.DesignSystemId = winner.Id;
                }
            }
        }

        await PruneAsync(system.Id, ct);
    }

    /// <summary>
    /// SPEC 9.3: after an insert, first reject the oldest Drafts beyond
    /// <see cref="MaxDraftsPerSystem"/> (a Draft that a Pending proposal points at is never one of
    /// them), then delete the oldest Rejected/Superseded versions beyond 100 — never an Approved or a
    /// Draft.
    /// </summary>
    private async Task PruneAsync(Guid designSystemId, CancellationToken ct)
    {
        await CapDraftsAsync(designSystemId, ct);

        int count = await db.DesignSystemVersions.CountAsync(v => v.DesignSystemId == designSystemId, ct);
        if (count <= MaxVersionsPerSystem)
            return;

        int excess = count - MaxVersionsPerSystem;
        List<DesignSystemVersion> removable = await db.DesignSystemVersions
            .Where(v => v.DesignSystemId == designSystemId
                && (v.State == DesignVersionState.Rejected || v.State == DesignVersionState.Superseded))
            .OrderBy(v => v.CreatedAt)
            .Take(excess)
            .ToListAsync(ct);

        if (removable.Count == 0)
            return;

        db.DesignSystemVersions.RemoveRange(removable);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Rejects the oldest Drafts of one system beyond <see cref="MaxDraftsPerSystem"/>. Drift raises a
    /// Draft per changed source, so without a cap an always-changing source grows the table without
    /// bound; a Draft a Pending proposal still points at is kept.
    /// </summary>
    private async Task CapDraftsAsync(Guid designSystemId, CancellationToken ct)
    {
        List<DesignSystemVersion> drafts = await db.DesignSystemVersions
            .AsNoTracking()
            .Where(v => v.DesignSystemId == designSystemId && v.State == DesignVersionState.Draft)
            .OrderBy(v => v.CreatedAt)
            .ThenBy(v => v.VersionNumber)
            .ToListAsync(ct);
        if (drafts.Count <= MaxDraftsPerSystem)
            return;

        HashSet<Guid> kept = (await db.DesignSystemChangeProposals
            .Where(p => p.DesignSystemId == designSystemId && p.Status == DesignProposalStatus.Pending)
            .Select(p => p.ProposedVersionId)
            .ToListAsync(ct)).ToHashSet();

        List<Guid> overflow = drafts
            .Where(d => !kept.Contains(d.Id))
            .Take(drafts.Count - MaxDraftsPerSystem)
            .Select(d => d.Id)
            .ToList();

        await RejectStillDraftsAsync(db, overflow, ct);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Rejects the listed versions that are STILL Drafts and are not their system's live version. The
    /// state is re-read from the database (not trusted from a tracked copy), so superseding, expiring or
    /// capping can never reject a version a person approved — or that became current — in the meantime.
    /// The caller saves.
    /// </summary>
    internal static async Task RejectStillDraftsAsync(
        InferenceApiDbContext db, IReadOnlyCollection<Guid> versionIds, CancellationToken ct)
    {
        if (versionIds.Count == 0)
            return;

        for (int attempt = 0; ; attempt++)
        {
            List<DesignSystemVersion> fresh = await db.DesignSystemVersions
                .AsNoTracking()
                .Where(v => versionIds.Contains(v.Id) && v.State == DesignVersionState.Draft)
                .ToListAsync(ct);
            if (fresh.Count == 0)
                return;

            // The systems are re-read so their concurrency token (CurrentVersionId) is the one in force
            // now. Touching each system row in the same save makes an approval that commits between this
            // read and the write conflict instead of being overwritten.
            List<Guid> systemIds = fresh.Select(v => v.DesignSystemId).Distinct().ToList();
            List<DesignSystem> systems = await db.DesignSystems.Where(s => systemIds.Contains(s.Id)).ToListAsync(ct);
            foreach (DesignSystem system in systems)
                await db.Entry(system).ReloadAsync(ct);

            HashSet<Guid> live = systems.Where(s => s.CurrentVersionId != null).Select(s => s.CurrentVersionId!.Value).ToHashSet();
            List<Guid> targets = fresh.Where(v => !live.Contains(v.Id)).Select(v => v.Id).ToList();
            if (targets.Count == 0)
                return;

            List<DesignSystemVersion> tracked = await db.DesignSystemVersions.Where(v => targets.Contains(v.Id)).ToListAsync(ct);
            foreach (DesignSystemVersion version in tracked)
            {
                await db.Entry(version).ReloadAsync(ct);
                if (version.State == DesignVersionState.Draft)
                    version.State = DesignVersionState.Rejected;
            }

            DateTime now = DateTime.UtcNow;
            foreach (DesignSystem system in systems.Where(s => tracked.Any(v => v.DesignSystemId == s.Id)))
                system.UpdatedAt = now;

            try
            {
                await db.SaveChangesAsync(ct);
                return;
            }
            catch (DbUpdateConcurrencyException ex) when (attempt < 2)
            {
                // A decision landed first: drop this attempt's version changes and look again.
                foreach (var entry in ex.Entries)
                    await entry.ReloadAsync(ct);
                foreach (DesignSystemVersion version in tracked)
                    await db.Entry(version).ReloadAsync(ct);
            }
        }
    }

    /// <summary>
    /// The one source-persistence path, shared by import and ingest: one batch insert of
    /// <see cref="DesignSystemSource"/> rows, every one <see cref="DesignSourceKind.ProjectFile"/> and
    /// <see cref="DesignSourceStatus.Current"/>.
    /// </summary>
    internal static async Task PersistSourcesAsync(
        InferenceApiDbContext db,
        Guid designSystemId,
        Guid versionId,
        IReadOnlyList<DesignSourceInput> sources,
        CancellationToken ct,
        Func<DesignSourceInput, DesignSourceKind>? kindOf = null)
    {
        if (sources is null || sources.Count == 0)
            return;

        DateTime now = DateTime.UtcNow;
        foreach (DesignSourceInput source in sources)
        {
            db.DesignSystemSources.Add(new DesignSystemSource
            {
                Id = Guid.NewGuid(),
                DesignSystemId = designSystemId,
                VersionId = versionId,
                SourceKind = kindOf?.Invoke(source) ?? DesignSourceKind.ProjectFile,
                ProjectFileId = source.ProjectFileId,
                Path = source.Path,
                Importer = source.Importer,
                ContentSha256 = source.ContentSha256,
                SourceRevision = source.SourceRevision,
                SizeBytes = source.SizeBytes,
                Status = DesignSourceStatus.Current,
                LastCheckedAt = now,
                LastChangedAt = null,
            });
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// The byte twin of <see cref="BoundedTextReader"/>: pulls at most <c>maxBytes + 1</c> bytes so
    /// truncation is still reportable without ever buffering more than the cap.
    /// </summary>
    private static async Task<(byte[] Bytes, bool Truncated)> ReadBytesBoundedAsync(
        Stream stream, int maxBytes, CancellationToken ct)
    {
        const int chunkSize = 8192;
        int bufferSize = Math.Min(chunkSize, maxBytes + 1);
        byte[] buffer = new byte[bufferSize];
        using MemoryStream buffered = new();

        long limit = (long)maxBytes + 1;
        long total = 0;

        while (total < limit)
        {
            int want = (int)Math.Min(buffer.Length, limit - total);
            int read = await stream.ReadAsync(buffer.AsMemory(0, want), ct);
            if (read == 0)
                break;

            buffered.Write(buffer, 0, read);
            total += read;
        }

        if (total > maxBytes)
            return ([], true);

        return (buffered.ToArray(), false);
    }

    private static DesignVersionSummaryResponse ToSummary(DesignVersionResponse version) => new(
        version.Id,
        version.DesignSystemId,
        version.VersionNumber,
        version.State,
        version.Origin,
        version.DocumentDigest,
        version.BaseVersionId,
        version.WorkflowExecutionId,
        version.CreatedByUserId,
        version.ApprovedByUserId,
        version.ApprovedAt,
        version.CreatedAt);

    private async Task<DesignVersionResponse> ToResponseAsync(DesignSystemVersion version, CancellationToken ct)
    {
        List<DesignSystemSource> sources = await db.DesignSystemSources
            .Where(s => s.VersionId == version.Id)
            .OrderBy(s => s.Path)
            .ToListAsync(ct);

        return new DesignVersionResponse(
            version.Id,
            version.DesignSystemId,
            version.VersionNumber,
            version.State.ToString(),
            version.Origin.ToString(),
            ParseObject(version.DocumentJson) ?? new JsonObject(),
            version.DocumentDigest,
            version.BaseVersionId,
            version.WorkflowExecutionId,
            version.CoverageJson is null ? null : ParseObject(version.CoverageJson),
            sources.Select(ToSourceResponse).ToList(),
            version.CreatedByUserId,
            version.ApprovedByUserId,
            version.ApprovedAt,
            version.CreatedAt);
    }

    private static DesignVersionSummaryResponse ToSummary(DesignSystemVersion version) => new(
        version.Id,
        version.DesignSystemId,
        version.VersionNumber,
        version.State.ToString(),
        version.Origin.ToString(),
        version.DocumentDigest,
        version.BaseVersionId,
        version.WorkflowExecutionId,
        version.CreatedByUserId,
        version.ApprovedByUserId,
        version.ApprovedAt,
        version.CreatedAt);

    private static ProposalResponse ToProposalResponse(DesignSystemChangeProposal proposal) => new(
        proposal.Id,
        proposal.DesignSystemId,
        proposal.BaseVersionId,
        proposal.ProposedVersionId,
        proposal.Trigger.ToString(),
        proposal.DriftScore,
        ParseObject(proposal.DiffJson) ?? new JsonObject(),
        proposal.Status.ToString(),
        proposal.ProposedDigest,
        proposal.DecidedByUserId,
        proposal.DecidedAt,
        proposal.DecisionNote,
        proposal.CreatedAt);

    private static DesignSourceResponse ToSourceResponse(DesignSystemSource source) => new(
        source.Id,
        source.SourceKind.ToString(),
        source.ProjectFileId,
        source.Path,
        source.Importer,
        source.ContentSha256,
        source.SourceRevision,
        source.SizeBytes,
        source.Status.ToString(),
        source.LastCheckedAt,
        source.LastChangedAt);

    private static JsonObject? ParseObject(string json)
    {
        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch
        {
            return null;
        }
    }
}
