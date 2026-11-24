using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.DesignSystems;
using ReelBolt.Shared.DesignSystems.Diff;
using ReelBolt.Shared.DesignSystems.Importers;
using ReelBolt.Shared.DesignSystems.Merge;
using ReelBolt.Shared.DesignSystems.ProductMap;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Inference.Api.Services.DesignSystems;

/// <summary>What asked for a drift check (SPEC 13.2). Append-only.</summary>
public enum DesignDriftTrigger
{
    GitHubPush,
    CloudSync,
    FolderSync,
    Sweep,
    Manual,
}

/// <summary>The outcome of one drift check: <c>nochange</c>|<c>proposal</c>|<c>suppressed</c>|<c>skipped</c>.</summary>
public sealed record DesignDriftOutcome(string Result, Guid? ProposalId);

/// <summary>
/// Stage 0/1 change detection for a project's design system and product map (SPEC 13.2). It never
/// calls a model: a changed candidate is produced only by the static importers and the static
/// product-map extractor.
/// </summary>
public interface IDesignSystemDriftService
{
    Task<DesignDriftOutcome> CheckAsync(
        Guid projectId,
        DesignDriftTrigger trigger,
        IReadOnlyCollection<string>? changedPaths,
        CancellationToken ct);
}

/// <summary>
/// The Phase-3 drift checker (SPEC 13.2, stages 0 and 1). Stage 0 compares the current version's
/// <c>design_system_sources</c> with the live <c>project_files</c> rows; only a real change runs
/// stage 1, which re-runs the pure importers/extractor, merges the base version's locked tokens over
/// the candidate and — when the canonical digest moved — writes a <c>StaticExtract</c> Draft plus a
/// proposal. No LLM, no chat client, no agent.
/// </summary>
public sealed class DesignSystemDriftService(
    InferenceApiDbContext db,
    IDesignSystemService designSystems,
    IFileStorageService storage,
    ITenantContext tenant,
    IConfiguration configuration,
    ILogger<DesignSystemDriftService> logger) : IDesignSystemDriftService
{
    /// <summary>The six design globs of SPEC 8/13.1.</summary>
    internal static readonly string[] DefaultDesignGlobs =
    [
        "**/*.css",
        "**/tailwind.config.*",
        "**/*.tokens.json",
        "**/tokens/**/*.json",
        "**/DESIGN.md",
        "**/brand/**",
    ];

    /// <summary>The importer label on a source row for a matching file no importer read (DSM-10).</summary>
    internal const string UnparsedImporter = "seen";

    internal const int MaxCandidateFiles = 3000;
    internal const int MaxBytesPerFile = 262144;
    internal const long MaxTotalBytes = 25165824;

    /// <summary>The uncalibrated product-map threshold below which no proposal is raised.</summary>
    internal const double ProductProposalThreshold = 0.05;

    public async Task<DesignDriftOutcome> CheckAsync(
        Guid projectId,
        DesignDriftTrigger trigger,
        IReadOnlyCollection<string>? changedPaths,
        CancellationToken ct)
    {
        _ = changedPaths;

        Project? project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == projectId, ct);
        if (project is null)
            return new DesignDriftOutcome("skipped", null);

        logger.LogInformation(
            "design_system.drift.start {ProjectId} {Trigger} {OrganizationId}",
            projectId, trigger, tenant.OrganizationId);

        DesignDriftOutcome? design = null;
        DesignSystem? designSystem = await db.DesignSystems
            .FirstOrDefaultAsync(s => s.ProjectId == projectId && s.Kind == DesignSystemKinds.Design, ct);
        if (designSystem?.CurrentVersionId is not null)
            design = await CheckDesignAsync(project, designSystem, trigger, ct);

        if (design is { Result: "proposal" or "suppressed" })
            return design;

        DesignDriftOutcome? product = null;
        DesignSystem? productSystem = await db.DesignSystems
            .FirstOrDefaultAsync(s => s.ProjectId == projectId && s.Kind == DesignSystemKinds.ProductMap, ct);
        if (productSystem?.CurrentVersionId is not null)
            product = await CheckProductMapAsync(project, productSystem, trigger, ct);

        if (product is { Result: "proposal" or "suppressed" })
            return product;

        if (design?.Result == "skipped" || product?.Result == "skipped")
            return new DesignDriftOutcome("skipped", null);

        return new DesignDriftOutcome("nochange", null);
    }

    // -------------------------------------------------------------------------------------------------
    // Stage 0/1 for the design document.
    // -------------------------------------------------------------------------------------------------

    private async Task<DesignDriftOutcome> CheckDesignAsync(
        Project project, DesignSystem system, DesignDriftTrigger trigger, CancellationToken ct)
    {
        DesignSystemVersion? current = await db.DesignSystemVersions
            .FirstOrDefaultAsync(v => v.Id == system.CurrentVersionId, ct);
        if (current is null)
            return new DesignDriftOutcome("nochange", null);

        List<DesignSystemSource> sources = await db.DesignSystemSources
            .Where(s => s.VersionId == current.Id)
            .ToListAsync(ct);
        List<ProjectFile> projectFiles = await db.ProjectFiles
            .Where(f => f.ProjectId == project.Id && f.CsamStatus != CsamScanStatus.Quarantined)
            .ToListAsync(ct);
        Dictionary<Guid, ProjectFile> filesById = projectFiles.ToDictionary(f => f.Id);

        IReadOnlyList<string> globs = DesignGlobs(system.WatchJson);
        DateTime now = DateTime.UtcNow;

        // Stage 0: a source is current when its recorded revision still matches the project file's;
        // otherwise the stored object is streamed once and re-hashed. A missing file is Missing, and
        // any matching file the sources do not name is new.
        bool changedInStage0 = false;
        bool unexplainedChange = false;
        var changedShas = new List<(string Path, string Sha)>();
        var newFiles = new List<ProjectFile>();
        var seenPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (DesignSystemSource source in sources)
        {
            ProjectFile? file = MatchSource(source, projectFiles);
            if (file is null)
            {
                // A source that is already Missing is a known fact, not a new change: re-flagging it
                // would rerun the whole of stage 1 on every push and every sweep.
                if (source.Status != DesignSourceStatus.Missing)
                {
                    source.Status = DesignSourceStatus.Missing;
                    source.LastChangedAt = now;
                    changedInStage0 = true;
                    unexplainedChange = true;
                }

                source.LastCheckedAt = now;
                continue;
            }

            seenPaths.Add(PathOf(file));
            string? expected = string.Equals(source.SourceRevision, file.SourceRevision, StringComparison.Ordinal)
                ? source.ContentSha256
                : null;

            if (source.SourceRevision is null || expected is null)
            {
                byte[]? bytes = await TryDownloadAsync(project.Id, file.StorageKey, MaxBytesPerFile, ct);
                if (bytes is null)
                {
                    if (source.Status != DesignSourceStatus.Missing)
                    {
                        source.Status = DesignSourceStatus.Missing;
                        source.LastChangedAt = now;
                        changedInStage0 = true;
                        unexplainedChange = true;
                    }
                }
                else
                {
                    string sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
                    if (!string.Equals(sha, source.ContentSha256, StringComparison.OrdinalIgnoreCase))
                    {
                        // The recorded digest names the bytes the CURRENT version was read from; a
                        // change is recorded on the status, never by overwriting that digest.
                        source.Status = DesignSourceStatus.Changed;
                        source.LastChangedAt = now;
                        changedInStage0 = true;
                        changedShas.Add((source.Path, sha));
                    }
                    else
                    {
                        source.Status = DesignSourceStatus.Current;
                        source.SizeBytes = bytes.Length;
                        source.SourceRevision = file.SourceRevision;
                    }
                }
            }
            else
            {
                source.Status = DesignSourceStatus.Current;
            }

            source.LastCheckedAt = now;
        }

        foreach (ProjectFile file in projectFiles)
        {
            string path = PathOf(file);
            if (IsDesignSource(path, globs) && !seenPaths.Contains(path))
            {
                changedInStage0 = true;
                newFiles.Add(file);
            }
        }

        await db.SaveChangesAsync(ct);
        if (!changedInStage0)
            return new DesignDriftOutcome("nochange", null);

        // Everything that changed is exactly what a live proposal (Pending, or Rejected inside the
        // suppression window) was already built from: nothing new for a person to look at, and no
        // reason to rerun stage 1. The approved version's own source rows stay as they are, so once the
        // proposal expires or the window ends the same drift is detected and proposed again.
        if (!unexplainedChange && await AllCoveredByLiveProposalAsync(system.Id, project.Id, changedShas, newFiles, ct))
            return new DesignDriftOutcome("nochange", null);

        // Stage 1: the pure importers over the full candidate set, locks from the base merged on top.
        // The current version's own sources are re-read even when they sit outside the six globs
        // (a file the user imported by hand), so a Changed one is resolved rather than left Changed.
        var ownSourcePaths = new HashSet<string>(
            sources.Where(src => src.Status != DesignSourceStatus.Missing).Select(src => src.Path),
            StringComparer.Ordinal);
        Dictionary<Guid, ProjectFile> byId = filesById;
        List<ImportFile> candidates = await BuildCandidatesAsync(
            project.Id,
            projectFiles,
            path => IsDesignSource(path, globs) || ownSourcePaths.Contains(path),
            ct);
        DesignImportResult import = DesignImportRegistry.ImportAll(candidates, ct);

        JsonObject? baseDoc = ParseObject(current.DocumentJson);
        if (import.Design is null || baseDoc is null)
        {
            // Nothing usable came out of the changed files. The approved version's source rows are not
            // touched: they must keep naming the bytes it was read from.
            return new DesignDriftOutcome("nochange", null);
        }

        JsonObject proposed = DesignDocumentMerger.Merge(baseDoc, [import.Design], ct);
        string canonical = CanonicalJson.Serialize(proposed);
        string digest = CanonicalJson.Sha256Hex(canonical);

        // A change that moves only provenance (a source location or id) is not a change a person can
        // review, so it must not raise a proposal with an empty diff.
        if (string.Equals(digest, current.DocumentDigest, StringComparison.OrdinalIgnoreCase)
            || string.Equals(
                DesignDocumentDiffer.SemanticDigest(proposed),
                DesignDocumentDiffer.SemanticDigest(baseDoc),
                StringComparison.Ordinal))
        {
            await UpsertSourcesAsync(
                system.Id, current.Id, candidates, import.Sources, UnparsedImporter, filesById, ct);
            return new DesignDriftOutcome("nochange", null);
        }

        if (await IsSuppressedAsync(system.Id, digest, ct))
            return new DesignDriftOutcome("suppressed", null);

        DesignDiff diff = DesignDocumentDiffer.DiffProposed(baseDoc, proposed, import.Design, ct);
        double score = DriftScore.Design(diff, baseDoc);
        JsonObject diffJson = DiffJson.Build(diff, score, null);

        string diffText = diffJson.ToJsonString();
        DesignSystemWriteResult write = await designSystems.CreateDraftAsync(
            project.Id,
            system.CreatedByUserId,
            DesignSystemKinds.Design,
            proposed,
            DesignVersionOrigin.StaticExtract,
            workflowExecutionId: null,
            coverageJson: null,
            sources: import.Sources,
            baseVersionId: current.Id,
            ct);

        if (write.Status != DesignSystemWriteStatus.Ok || write.Version is null)
        {
            logger.LogWarning("design_system.drift.write_failed {ProjectId} {Status}", project.Id, write.Status);
            return new DesignDriftOutcome("skipped", null);
        }

        await PersistDesignSourcesAsync(system.Id, write.Version.Id, candidates, import.Sources, byId, ct);

        DesignSystemChangeProposal proposal = await CreateProposalAsync(
            system.Id, current.Id, write.Version.Id, trigger, score, diffText, digest, ct);

        return new DesignDriftOutcome("proposal", proposal.Id);
    }

    // -------------------------------------------------------------------------------------------------
    // Stage 1 for the product map (debounced, with its own threshold).
    // -------------------------------------------------------------------------------------------------

    private async Task<DesignDriftOutcome> CheckProductMapAsync(
        Project project, DesignSystem system, DesignDriftTrigger trigger, CancellationToken ct)
    {
        DesignSystemVersion? current = await db.DesignSystemVersions
            .FirstOrDefaultAsync(v => v.Id == system.CurrentVersionId, ct);
        if (current is null)
            return new DesignDriftOutcome("nochange", null);

        List<DesignSystemSource> sources = await db.DesignSystemSources
            .Where(s => s.VersionId == current.Id)
            .ToListAsync(ct);

        int intervalMinutes = configuration.GetValue("DesignSystem:Drift:ProductMapMinIntervalMinutes", 360);
        if (trigger != DesignDriftTrigger.Manual && sources.Count > 0)
        {
            DateTime lastChecked = sources.Max(s => s.LastCheckedAt);
            if ((DateTime.UtcNow - lastChecked).TotalMinutes < intervalMinutes)
                return new DesignDriftOutcome("skipped", null);
        }

        List<ProjectFile> projectFiles = await db.ProjectFiles
            .Where(f => f.ProjectId == project.Id && f.CsamStatus != CsamScanStatus.Quarantined)
            .ToListAsync(ct);

        List<ImportFile> candidates = await BuildCandidatesAsync(
            project.Id, projectFiles, IsProductMapCandidate, ct);
        ProductMapExtraction extraction = ProductMapExtractor.Extract(candidates, ct);

        // The proposed document is the static extraction layered over the base (SPEC 13.2), so fields
        // only a model could fill (architecture, patterns, directories, entry points) are not dropped.
        ProductMapDocument? baseMap = ProductMapJson.Parse(current.DocumentJson);
        ProductMapDocument map = baseMap is null
            ? extraction.Map
            : ProductMapMerger.Merge(extraction.Map, baseMap, ct);

        string canonical = ProductMapJson.Serialize(map);
        string digest = CanonicalJson.Sha256Hex(canonical);

        if (baseMap is null || string.Equals(digest, current.DocumentDigest, StringComparison.OrdinalIgnoreCase))
        {
            await UpsertSourcesAsync(system.Id, current.Id, candidates, null, "product-map", null, ct);
            return new DesignDriftOutcome("nochange", null);
        }

        ProductMapDiff diff = ProductMapDiffer.Diff(baseMap, map, ct);
        double score = DriftScore.Product(diff, baseMap);
        if (score < ProductProposalThreshold)
        {
            await UpsertSourcesAsync(system.Id, current.Id, candidates, null, "product-map", null, ct);
            return new DesignDriftOutcome("nochange", null);
        }

        if (await IsSuppressedAsync(system.Id, digest, ct))
            return new DesignDriftOutcome("suppressed", null);

        JsonObject diffJson = DiffJson.Build(null, score, diff);
        string diffText = diffJson.ToJsonString();

        JsonObject mapNode = ProductMapJson.ToNode(map);
        DesignSystemWriteResult write = await designSystems.CreateDraftAsync(
            project.Id,
            system.CreatedByUserId,
            DesignSystemKinds.ProductMap,
            mapNode,
            DesignVersionOrigin.StaticExtract,
            workflowExecutionId: null,
            coverageJson: null,
            sources: [],
            baseVersionId: current.Id,
            ct);

        if (write.Status != DesignSystemWriteStatus.Ok || write.Version is null)
            return new DesignDriftOutcome("skipped", null);

        await UpsertSourcesAsync(system.Id, write.Version.Id, candidates, null, "product-map", null, ct);

        DesignSystemChangeProposal proposal = await CreateProposalAsync(
            system.Id, current.Id, write.Version.Id, trigger, score, diffText, digest, ct);

        return new DesignDriftOutcome("proposal", proposal.Id);
    }

    // -------------------------------------------------------------------------------------------------
    // Shared helpers.
    // -------------------------------------------------------------------------------------------------

    private async Task<DesignSystemChangeProposal> CreateProposalAsync(
        Guid designSystemId,
        Guid baseVersionId,
        Guid proposedVersionId,
        DesignDriftTrigger trigger,
        double driftScore,
        string diffJson,
        string proposedDigest,
        CancellationToken ct)
    {
        DateTime now = DateTime.UtcNow;

        // At most one Pending proposal per system: a newer one supersedes the older ones.
        List<DesignSystemChangeProposal> pending = await db.DesignSystemChangeProposals
            .Where(p => p.DesignSystemId == designSystemId && p.Status == DesignProposalStatus.Pending)
            .ToListAsync(ct);
        foreach (DesignSystemChangeProposal older in pending)
            older.Status = DesignProposalStatus.Superseded;

        // The Draft a superseded proposal pointed at can no longer be approved through it, so it must
        // not linger as an orphan Draft.
        await DesignSystemService.RejectStillDraftsAsync(db, pending.Select(p => p.ProposedVersionId).ToList(), ct);

        var proposal = new DesignSystemChangeProposal
        {
            Id = Guid.NewGuid(),
            DesignSystemId = designSystemId,
            BaseVersionId = baseVersionId,
            ProposedVersionId = proposedVersionId,
            Trigger = MapTrigger(trigger),
            DriftScore = driftScore,
            DiffJson = diffJson,
            Status = DesignProposalStatus.Pending,
            ProposedDigest = proposedDigest,
            CreatedAt = now,
        };

        db.DesignSystemChangeProposals.Add(proposal);
        await db.SaveChangesAsync(ct);
        return proposal;
    }

    /// <summary>
    /// True when every changed source and every new file carries exactly the bytes (SHA-256) a live
    /// proposal's draft was built from. The draft's own source rows are the memory; nothing on the
    /// approved version is written.
    /// </summary>
    private async Task<bool> AllCoveredByLiveProposalAsync(
        Guid designSystemId,
        Guid projectId,
        IReadOnlyList<(string Path, string Sha)> changedShas,
        IReadOnlyList<ProjectFile> newFiles,
        CancellationToken ct)
    {
        if (changedShas.Count == 0 && newFiles.Count == 0)
            return false;

        int suppressDays = configuration.GetValue("DesignSystem:Drift:SuppressRejectedDays", 30);
        DateTime cutoff = DateTime.UtcNow.AddDays(-suppressDays);
        List<DesignSystemChangeProposal> proposals = await db.DesignSystemChangeProposals
            .Where(p => p.DesignSystemId == designSystemId
                && (p.Status == DesignProposalStatus.Pending || p.Status == DesignProposalStatus.Rejected))
            .ToListAsync(ct);
        List<Guid> versionIds = proposals
            .Where(p => p.Status == DesignProposalStatus.Pending || (p.DecidedAt ?? p.CreatedAt) >= cutoff)
            .Select(p => p.ProposedVersionId)
            .ToList();
        if (versionIds.Count == 0)
            return false;

        List<DesignSystemSource> known = await db.DesignSystemSources
            .AsNoTracking()
            .Where(src => versionIds.Contains(src.VersionId))
            .ToListAsync(ct);
        ILookup<string, string> shasByPath = known.ToLookup(k => k.Path, k => k.ContentSha256, StringComparer.Ordinal);

        foreach ((string path, string sha) in changedShas)
        {
            if (!shasByPath[path].Contains(sha, StringComparer.OrdinalIgnoreCase))
                return false;
        }

        foreach (ProjectFile file in newFiles)
        {
            string path = PathOf(file);
            if (!shasByPath.Contains(path))
                return false;

            byte[]? bytes = await TryDownloadAsync(projectId, file.StorageKey, MaxBytesPerFile, ct);
            if (bytes is null)
                return false;

            string sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
            if (!shasByPath[path].Contains(sha, StringComparer.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    private async Task<bool> IsSuppressedAsync(Guid designSystemId, string proposedDigest, CancellationToken ct)
    {
        int suppressDays = configuration.GetValue("DesignSystem:Drift:SuppressRejectedDays", 30);
        DateTime cutoff = DateTime.UtcNow.AddDays(-suppressDays);

        List<DesignSystemChangeProposal> matches = await db.DesignSystemChangeProposals
            .Where(p => p.DesignSystemId == designSystemId && p.ProposedDigest == proposedDigest)
            .ToListAsync(ct);

        return matches.Any(p =>
            p.Status == DesignProposalStatus.Pending
            || (p.Status == DesignProposalStatus.Rejected && (p.DecidedAt ?? p.CreatedAt) >= cutoff));
    }

    private static DesignProposalTrigger MapTrigger(DesignDriftTrigger trigger) => trigger switch
    {
        DesignDriftTrigger.Manual => DesignProposalTrigger.Manual,
        DesignDriftTrigger.Sweep => DesignProposalTrigger.Scheduled,
        _ => DesignProposalTrigger.SourceChanged,
    };

    /// <summary>
    /// Persists one source row per candidate file against <paramref name="versionId"/>, using the
    /// digest the importer actually recomputed when it has one.
    /// </summary>
    private async Task PersistDesignSourcesAsync(
        Guid designSystemId,
        Guid versionId,
        IReadOnlyList<ImportFile> candidates,
        IReadOnlyList<ImportSourceRef> importSources,
        IReadOnlyDictionary<Guid, ProjectFile> filesById,
        CancellationToken ct)
    {
        var facts = new List<SourceFact>();
        foreach (ImportSourceRef source in importSources ?? [])
        {
            ProjectFile? file = source.ProjectFileId is { } id && filesById.TryGetValue(id, out ProjectFile? found)
                ? found
                : null;
            int size = file is null
                ? candidates.FirstOrDefault(c => string.Equals(c.Path, source.Path, StringComparison.Ordinal))?.Content.Length ?? 0
                : (int)file.SizeBytes;
            facts.Add(new SourceFact(
                source.Path, source.Importer, source.Sha256, source.ProjectFileId, size, file?.SourceRevision));
        }

        foreach (ImportFile file in candidates)
        {
            if (facts.Any(f => string.Equals(f.Path, file.Path, StringComparison.Ordinal)))
                continue;

            ProjectFile? known = file.ProjectFileId is { } fid && filesById.TryGetValue(fid, out ProjectFile? hit) ? hit : null;
            facts.Add(new SourceFact(
                file.Path, UnparsedImporter, file.Sha256, file.ProjectFileId, file.Content.Length, known?.SourceRevision));
        }

        await PersistFactsAsync(designSystemId, versionId, facts, ct);
    }

    /// <summary>
    /// Refreshes (or inserts) the source rows of one version from the candidate set, so a run that
    /// changed nothing still records the current digests and the check time.
    /// </summary>
    private async Task UpsertSourcesAsync(
        Guid designSystemId,
        Guid versionId,
        IReadOnlyList<ImportFile> candidates,
        IReadOnlyList<ImportSourceRef>? importSources,
        string defaultImporter,
        IReadOnlyDictionary<Guid, ProjectFile>? filesById,
        CancellationToken ct)
    {
        string? RevisionOf(Guid? projectFileId) =>
            projectFileId is { } id && filesById is not null && filesById.TryGetValue(id, out ProjectFile? f)
                ? f.SourceRevision
                : null;

        var facts = new List<SourceFact>();
        if (importSources is { Count: > 0 })
        {
            var covered = new HashSet<string>(StringComparer.Ordinal);
            foreach (ImportSourceRef source in importSources)
            {
                covered.Add(source.Path);
                int size = candidates
                    .FirstOrDefault(c => string.Equals(c.Path, source.Path, StringComparison.Ordinal))
                    ?.Content.Length ?? 0;
                facts.Add(new SourceFact(
                    source.Path, source.Importer, source.Sha256, source.ProjectFileId, size, RevisionOf(source.ProjectFileId)));
            }

            // A matching file no importer read is still recorded, so it is a known file next time.
            foreach (ImportFile file in candidates)
            {
                if (!covered.Contains(file.Path))
                {
                    facts.Add(new SourceFact(
                        file.Path, UnparsedImporter, file.Sha256, file.ProjectFileId, file.Content.Length, RevisionOf(file.ProjectFileId)));
                }
            }
        }
        else
        {
            foreach (ImportFile file in candidates)
            {
                facts.Add(new SourceFact(
                    file.Path, defaultImporter, file.Sha256, file.ProjectFileId, file.Content.Length, RevisionOf(file.ProjectFileId)));
            }
        }

        List<DesignSystemSource> existing = await db.DesignSystemSources
            .Where(s => s.VersionId == versionId)
            .ToListAsync(ct);
        var byPath = existing.ToDictionary(s => s.Path, StringComparer.Ordinal);

        DateTime now = DateTime.UtcNow;
        foreach (SourceFact fact in facts)
        {
            if (byPath.TryGetValue(fact.Path, out DesignSystemSource? row))
            {
                row.ContentSha256 = fact.Sha256;
                row.SizeBytes = fact.SizeBytes;
                row.SourceRevision = fact.SourceRevision;
                row.ProjectFileId = fact.ProjectFileId;
                row.Status = DesignSourceStatus.Current;
                row.LastCheckedAt = now;
            }
            else
            {
                db.DesignSystemSources.Add(new DesignSystemSource
                {
                    Id = Guid.NewGuid(),
                    DesignSystemId = designSystemId,
                    VersionId = versionId,
                    SourceKind = DesignSourceKind.ProjectFile,
                    ProjectFileId = fact.ProjectFileId,
                    Path = fact.Path,
                    Importer = fact.Importer,
                    ContentSha256 = fact.Sha256,
                    SourceRevision = fact.SourceRevision,
                    SizeBytes = fact.SizeBytes,
                    Status = DesignSourceStatus.Current,
                    LastCheckedAt = now,
                });
            }
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>The one source-row insert path, mirroring <see cref="DesignSystemService.PersistSourcesAsync"/>.</summary>
    private async Task PersistFactsAsync(
        Guid designSystemId, Guid versionId, IReadOnlyList<SourceFact> facts, CancellationToken ct)
    {
        if (facts.Count == 0)
            return;

        DateTime now = DateTime.UtcNow;
        foreach (SourceFact fact in facts)
        {
            db.DesignSystemSources.Add(new DesignSystemSource
            {
                Id = Guid.NewGuid(),
                DesignSystemId = designSystemId,
                VersionId = versionId,
                SourceKind = DesignSourceKind.ProjectFile,
                ProjectFileId = fact.ProjectFileId,
                Path = fact.Path,
                Importer = fact.Importer,
                ContentSha256 = fact.Sha256,
                SourceRevision = fact.SourceRevision,
                SizeBytes = fact.SizeBytes,
                Status = DesignSourceStatus.Current,
                LastCheckedAt = now,
            });
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task<List<ImportFile>> BuildCandidatesAsync(
        Guid projectId,
        IReadOnlyList<ProjectFile> files,
        Func<string, bool> selector,
        CancellationToken ct)
    {
        var result = new List<ImportFile>();
        long total = 0;

        foreach (ProjectFile file in files
            .Select(f => (File: f, Path: PathOf(f)))
            .Where(entry => selector(entry.Path))
            .OrderBy(entry => entry.Path, StringComparer.Ordinal)
            .Select(entry => entry.File))
        {
            if (result.Count >= MaxCandidateFiles)
                break;
            if (file.SizeBytes > MaxBytesPerFile)
                continue;
            if (total + file.SizeBytes > MaxTotalBytes)
                break;

            byte[]? bytes = await TryDownloadAsync(projectId, file.StorageKey, MaxBytesPerFile, ct);
            if (bytes is null)
                continue;

            total += bytes.Length;
            result.Add(new ImportFile(
                PathOf(file), bytes, Convert.ToHexStringLower(SHA256.HashData(bytes)), file.Id));
        }

        return result;
    }

    private async Task<byte[]?> TryDownloadAsync(
        Guid projectId, string storageKey, int maxBytes, CancellationToken ct)
    {
        try
        {
            await using Stream stream = await storage.DownloadAsync(projectId, storageKey, ct);
            (byte[] bytes, bool truncated) = await ReadBoundedAsync(stream, maxBytes, ct);
            return truncated ? null : bytes;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<(byte[] Bytes, bool Truncated)> ReadBoundedAsync(
        Stream stream, int maxBytes, CancellationToken ct)
    {
        using var buffered = new MemoryStream();
        byte[] buffer = new byte[Math.Min(8192, maxBytes + 1)];
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

        return total > maxBytes ? ([], true) : (buffered.ToArray(), false);
    }

    private static ProjectFile? MatchSource(DesignSystemSource source, IReadOnlyList<ProjectFile> files)
    {
        if (source.ProjectFileId is { } id)
        {
            ProjectFile? byId = files.FirstOrDefault(f => f.Id == id);
            if (byId is not null)
                return byId;
        }

        return files.FirstOrDefault(f => string.Equals(PathOf(f), source.Path, StringComparison.Ordinal));
    }

    private static string PathOf(ProjectFile file) =>
        string.IsNullOrEmpty(file.OriginalPath) ? file.OriginalFileName : file.OriginalPath;

    private static IReadOnlyList<string> DesignGlobs(string? watchJson)
    {
        if (!string.IsNullOrWhiteSpace(watchJson))
        {
            try
            {
                if (JsonNode.Parse(watchJson) is JsonObject watch
                    && watch["globs"] is JsonArray globs
                    && globs.Count > 0)
                {
                    var parsed = new List<string>();
                    foreach (JsonNode? item in globs)
                    {
                        if (item is JsonValue value && value.GetValueKind() == System.Text.Json.JsonValueKind.String)
                            parsed.Add(value.GetValue<string>());
                    }

                    if (parsed.Count > 0)
                        return parsed;
                }
            }
            catch
            {
                // A malformed watch_json falls back to the six first-party globs.
            }
        }

        return DefaultDesignGlobs;
    }

    private static bool IsDesignSource(string path, IReadOnlyList<string> globs)
    {
        foreach (string glob in globs)
        {
            if (SimpleGlob.IsMatch(glob, path))
                return true;
        }

        return false;
    }

    /// <summary>The SPEC 7 candidate rules, applied to a project-relative path.</summary>
    internal static bool IsProductMapCandidate(string path)
    {
        if (string.IsNullOrEmpty(path))
            return false;

        string name = path[(path.LastIndexOf('/') + 1)..];
        string lower = name.ToLowerInvariant();

        if (lower == "package.json" || lower == "components.json" || lower == "angular.json")
            return true;
        if (lower.StartsWith("tsconfig", StringComparison.Ordinal) && lower.EndsWith(".json", StringComparison.Ordinal))
            return true;
        if (lower.StartsWith("next.config.", StringComparison.Ordinal)
            || lower.StartsWith("vite.config.", StringComparison.Ordinal)
            || lower.StartsWith("remix.config.", StringComparison.Ordinal)
            || lower.StartsWith("nuxt.config.", StringComparison.Ordinal)
            || lower.StartsWith("svelte.config.", StringComparison.Ordinal))
        {
            return true;
        }

        if (path.Contains("rout", StringComparison.OrdinalIgnoreCase))
            return true;

        string extension = System.IO.Path.GetExtension(lower);
        if (extension is not (".tsx" or ".jsx" or ".ts" or ".js" or ".vue" or ".svelte" or ".astro" or ".mdx"))
            return false;

        return IsUnderProductDirectory(path);
    }

    private static bool IsUnderProductDirectory(string path)
    {
        if (path.Contains("lib/components", StringComparison.Ordinal))
            return true;

        foreach (string segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment is "app" or "pages" or "routes" or "components" or "ui")
                return true;
        }

        return false;
    }

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

    /// <summary>One source row's facts while a check is in flight.</summary>
    private sealed record SourceFact(
        string Path,
        string Importer,
        string Sha256,
        Guid? ProjectFileId,
        int SizeBytes,
        string? SourceRevision);
}
