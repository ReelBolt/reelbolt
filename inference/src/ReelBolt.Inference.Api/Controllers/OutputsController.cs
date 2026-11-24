using ReelBolt.Inference.Api.Services.Auth;
using Amazon.S3;
using ReelBolt.Inference.Api.Services.Telemetry;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Graphics;
using ReelBolt.Inference.Api.Services.Moderation;
using ReelBolt.Inference.Api.Services.Outputs;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Moderation;
using ReelBolt.Shared.Storage;
using ReelBolt.Shared.Workflows.Replay;
using System.Net;
using System.Text.Json;

namespace ReelBolt.Inference.Api.Controllers;

/// <summary>
/// Provides access to video/image artifacts produced by workflow step executions.
/// </summary>
[ApiController]
[Route("api/v1/projects/{projectId:guid}/outputs")]
[Authorize]
public class OutputsController : ControllerBase
{
    private readonly InferenceApiDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IProjectAccessService _projectAccess;
    private readonly IAmazonS3 _s3Client;
    private readonly IFileStorageService _fileStorage;
    private readonly string _bucketName;
    /// <summary>The pending view's Retry-After (config, default 5 s): a bounded poll, now a 202 rather than an error.</summary>
    private readonly string _pendingRetryAfter;
    /// <summary>T-004: publish/egress gate; null in tests that do not exercise publication.</summary>
    private readonly PublishModeration? _publishModeration;
    private readonly IProductEventEmitter? _productEvents;

    public OutputsController(
        InferenceApiDbContext db,
        ICurrentUser currentUser,
        IProjectAccessService projectAccess,
        IAmazonS3 s3Client,
        IFileStorageService fileStorage,
        IConfiguration configuration,
        PublishModeration? publishModeration = null,
        IProductEventEmitter? productEvents = null)
    {
        _productEvents = productEvents;
        _db = db;
        _currentUser = currentUser;
        _projectAccess = projectAccess;
        _s3Client = s3Client;
        _fileStorage = fileStorage;
        _bucketName = configuration["MinIO:BucketName"] ?? "reelforge";
        _pendingRetryAfter = OutputModerationTiming.PendingRetryAfterHeader(configuration);
        _publishModeration = publishModeration;
    }

    /// <summary>
    /// Lists all workflow step results that produced a media output for this project,
    /// ordered most-recent first.
    /// </summary>
    [HttpGet]
    [Filters.ConditionalGet]
    public async Task<ActionResult<List<OutputVideoResponse>>> ListOutputs(Guid projectId, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return denied;
        Project project = access.Project!;

        // A generated clip under agentFiles/generated/ is an addressable output exactly like a render
        // (ProjectOutputKeys; GetOutput and the output links already accept it), so this lists it too.
        // Each prefix keeps its trailing slash, which makes it a true path-segment prefix — without it, a key
        // like "projects/{id}/outputFiles-tmp/..." would also match, letting this endpoint reach objects
        // outside its intended scope. EF Core can't translate the StartsWith overload that takes a
        // StringComparison, so the simpler form is used, which maps to SQL LIKE.
        string outputPrefix = ProjectOutputKeys.OutputFilesPrefix(projectId);
        string generatedPrefix = ProjectOutputKeys.GeneratedPrefix(projectId);
        // A row a composition revision COPIED from an earlier run (ReusedFromStepResultId) is not a render
        // of its own: its file is the original row's take, listed once, there.
        var rows = await _db.WorkflowStepResults
            .Where(r =>
                r.OutputStorageKey != null &&
                r.ReusedFromStepResultId == null &&
                r.WorkflowExecution.ProjectId == projectId &&
                (r.OutputStorageKey.StartsWith(outputPrefix) || r.OutputStorageKey.StartsWith(generatedPrefix)))
            .OrderByDescending(r => r.CompletedAt ?? r.ExecutedAt)
            .Select(r => new
            {
                r.Id,
                r.WorkflowExecutionId,
                r.WorkflowStepId,
                StorageKey = r.OutputStorageKey!,
                ProducedAt = r.CompletedAt ?? r.ExecutedAt,
                r.OutputJson,
                r.IterationNumber,
                r.Status,
                r.IsPinned,
            })
            .ToListAsync(ct);

        // An output that is not served is omitted from the list.
        HashSet<Guid> unavailable = await OutputGate.HeldResultIdsAsync(_db, projectId, rows.Select(r => r.Id).ToList(), ct);
        if (unavailable.Count > 0) rows = rows.Where(r => !unavailable.Contains(r.Id)).ToList();

        // Context for each render, resolved with separate lookups (never navigation projections) so a
        // render whose step or workflow has since been deleted still lists, just with null context.
        List<Guid> executionIds = rows.Select(r => r.WorkflowExecutionId).Distinct().ToList();
        List<Guid> stepIds = rows.Select(r => r.WorkflowStepId).Distinct().ToList();

        var executions = await _db.WorkflowExecutions
            .Where(e => executionIds.Contains(e.Id))
            .Select(e => new { e.Id, e.WorkflowDefinitionId, e.Status, e.StartedAt, e.CompletedAt })
            .ToDictionaryAsync(e => e.Id, ct);

        List<Guid> workflowIds = executions.Values.Select(e => e.WorkflowDefinitionId).Distinct().ToList();
        Dictionary<Guid, string> workflowNames = await _db.WorkflowDefinitions
            .Where(w => workflowIds.Contains(w.Id))
            .ToDictionaryAsync(w => w.Id, w => w.Name, ct);

        var steps = await _db.WorkflowSteps
            .Where(s => stepIds.Contains(s.Id))
            .Select(s => new { s.Id, s.Label, s.StepType, s.StepOrder })
            .ToDictionaryAsync(s => s.Id, ct);
        Dictionary<Guid, Guid> replayOwners = await ReplayTakeOwnersAsync(projectId, workflowIds, ct);

        // "Run 3" = the third execution of that workflow, by start time — the same ordinal the
        // execution page shows, so a customer can match a render to the run that produced it.
        var siblingRuns = await _db.WorkflowExecutions
            .Where(e => e.ProjectId == projectId && workflowIds.Contains(e.WorkflowDefinitionId))
            .Select(e => new { e.Id, e.WorkflowDefinitionId, e.StartedAt })
            .ToListAsync(ct);
        Dictionary<Guid, int> runNumbers = siblingRuns
            .GroupBy(e => e.WorkflowDefinitionId)
            .SelectMany(g => g
                .OrderBy(e => e.StartedAt ?? DateTime.MaxValue)
                .ThenBy(e => e.Id)
                .Select((e, i) => (e.Id, Number: i + 1)))
            .ToDictionary(x => x.Id, x => x.Number);

        // Contract C1: any step result's OutputJson may carry top-level "warnings"; a render shows
        // every warning its run raised (deduplicated), since e.g. a Voiceover step's warning explains
        // what is wrong with the compile step's render.
        var runOutputs = await _db.WorkflowStepResults
            .Where(r => executionIds.Contains(r.WorkflowExecutionId) && r.OutputJson != null)
            .Select(r => new { r.WorkflowExecutionId, r.OutputJson })
            .ToListAsync(ct);
        Dictionary<Guid, List<OutputWarning>> warningsByExecution = runOutputs
            .GroupBy(r => r.WorkflowExecutionId)
            .ToDictionary(
                g => g.Key,
                g => g.SelectMany(r => RenderMetadataReader.ReadWarnings(r.OutputJson))
                    .DistinctBy(w => (w.Code, w.Message))
                    .ToList());

        // The finished video of a run is the LAST render of a run that passed; every earlier render
        // of that run (e.g. a review loop's rejected attempts) and every render of a run that did not
        // pass is a draft.
        HashSet<Guid> finalRenderIds = FinalRenders.FinalRenderIds(
            rows.Select(r => new RenderCandidate(r.Id, r.WorkflowExecutionId, r.Status, r.ProducedAt)),
            id => executions.TryGetValue(id, out var e) && e.Status == ExecutionStatus.Passed);
        // Takes (docs/video-editing.md "Takes: the latest render first"): every render of the same
        // workflow step — across runs, review-loop passes and retries — is one take of that step's
        // video. Rows are already newest first, so the first of each group is the latest take; a
        // render that is not the last one its own run produced for that step is intermediate.
        Dictionary<Guid, string> groupKeyById = rows.ToDictionary(r => r.Id, r =>
        {
            executions.TryGetValue(r.WorkflowExecutionId, out var e);
            steps.TryGetValue(r.WorkflowStepId, out var s);
            (Guid? groupWorkflow, StepType? groupType) = TakeOwner(replayOwners, e?.WorkflowDefinitionId, s?.StepType);
            return TakeGroupKey(groupWorkflow, s?.StepOrder, groupType, r.WorkflowStepId);
        });
        // A render of a run still in progress is never the latest take: the latest is the newest
        // render of a run that has ended (in-progress renders rank after it, and are never deleted).
        bool InProgress(Guid executionId) =>
            executions.TryGetValue(executionId, out var e) && e.Status is ExecutionStatus.Queued or ExecutionStatus.Running;
        Dictionary<Guid, (int Index, int Count)> takeById = rows
            .GroupBy(r => groupKeyById[r.Id])
            .SelectMany(g => g.OrderBy(r => InProgress(r.WorkflowExecutionId)).Select((r, i) => (r.Id, Index: i + 1, Count: g.Count())))
            .ToDictionary(x => x.Id, x => (x.Index, x.Count));
        HashSet<Guid> lastOfRun = rows
            .GroupBy(r => (r.WorkflowExecutionId, r.WorkflowStepId))
            .Select(g => g.First().Id)
            .ToHashSet();

        List<OutputVideoResponse> outputs = rows.Select(r =>
        {
            executions.TryGetValue(r.WorkflowExecutionId, out var execution);
            steps.TryGetValue(r.WorkflowStepId, out var step);
            (int takeIndex, int takeCount) = takeById[r.Id];
            string? workflowName = execution != null && workflowNames.TryGetValue(execution.WorkflowDefinitionId, out string? name) ? name : null;
            RenderMetadata meta = RenderMetadataReader.Read(r.OutputJson);
            return new OutputVideoResponse(
                r.Id,
                r.WorkflowExecutionId,
                r.WorkflowStepId,
                r.StorageKey,
                Path.GetFileName(r.StorageKey),
                r.ProducedAt,
                WorkflowDefinitionId: execution?.WorkflowDefinitionId,
                WorkflowName: workflowName,
                RunNumber: runNumbers.TryGetValue(r.WorkflowExecutionId, out int run) ? run : null,
                RunStartedAt: execution?.StartedAt,
                ExecutionStatus: execution?.Status.ToString(),
                StepLabel: step?.Label,
                StepType: step?.StepType.ToString(),
                IterationNumber: r.IterationNumber,
                DurationSec: meta.DurationSec,
                Width: meta.Width,
                Height: meta.Height,
                IsFinal: finalRenderIds.Contains(r.Id),
                Warnings: warningsByExecution.TryGetValue(r.WorkflowExecutionId, out List<OutputWarning>? w) ? w : [],
                TakeGroupKey: groupKeyById[r.Id],
                TakeIndex: takeIndex,
                TakeCount: takeCount,
                IsLatestTake: takeIndex == 1,
                IsIntermediate: !lastOfRun.Contains(r.Id),
                IsPinned: r.IsPinned);
        }).ToList();

        return Ok(outputs);
    }

    /// <summary>
    /// The key every take of one step's video shares: the workflow plus the step's place and type
    /// in it (so a workflow re-saved with new step rows keeps its takes together), or the step id
    /// when the workflow or step is gone.
    /// </summary>
    internal static string TakeGroupKey(Guid? workflowDefinitionId, int? stepOrder, StepType? stepType, Guid stepId) =>
        workflowDefinitionId is Guid w && stepOrder is int order
            ? $"{w:N}:{order}:{stepType}"
            : $"step:{stepId:N}";

    /// <summary>
    /// The workflow and step type a render's take belongs to. A composition revision's replay runs a
    /// per-revision snapshot of the user's workflow (docs/design/composition-revisions.md), so its renders
    /// are takes of the ORIGINAL workflow's steps — the revision's final cut is the newest take of the
    /// compile, and its re-rendered film the newest take of the film step it replaced (a Revise step
    /// standing in for an Agent step).
    /// </summary>
    internal static (Guid? WorkflowId, StepType? StepType) TakeOwner(
        IReadOnlyDictionary<Guid, Guid> replayOwners, Guid? workflowDefinitionId, StepType? stepType)
    {
        if (workflowDefinitionId is Guid w && replayOwners.TryGetValue(w, out Guid origin))
            return (origin, stepType == StepType.Revise ? StepType.Agent : stepType);
        return (workflowDefinitionId, stepType);
    }

    /// <summary>Replay snapshot workflow id → the user's workflow it replays (from each revision's plan).</summary>
    private async Task<Dictionary<Guid, Guid>> ReplayTakeOwnersAsync(Guid projectId, IReadOnlyCollection<Guid> workflowIds, CancellationToken ct)
    {
        List<Guid> replayWorkflowIds = await _db.WorkflowDefinitions
            .Where(w => workflowIds.Contains(w.Id) && w.ProjectId == projectId && w.Kind == WorkflowDefinitionKind.CompositionReplay)
            .Select(w => w.Id)
            .ToListAsync(ct);
        if (replayWorkflowIds.Count == 0)
            return [];

        var plans = await _db.CompositionRevisions
            .Where(r => r.ProjectId == projectId && replayWorkflowIds.Contains(r.WorkflowDefinitionId))
            .Select(r => new { r.WorkflowDefinitionId, r.ReplayPlanJson })
            .ToListAsync(ct);
        Dictionary<Guid, Guid> owners = [];
        foreach (var plan in plans)
        {
            if (CompositionReplayPlan.TryParse(plan.ReplayPlanJson)?.OriginWorkflowDefinitionId is { } origin)
                owners[plan.WorkflowDefinitionId] = origin;
        }

        return owners;
    }

    /// <summary>One take as the cleanup endpoints see it: its place in its group (1 = latest) and whether its run is still going.</summary>
    private sealed record Take(WorkflowStepResult Result, string GroupKey, int Index, bool InProgress);

    private async Task<List<Take>> LoadTakesAsync(Guid projectId, CancellationToken ct)
    {
        string outputPrefix = ProjectOutputKeys.OutputFilesPrefix(projectId);
        string generatedPrefix = ProjectOutputKeys.GeneratedPrefix(projectId);
        List<WorkflowStepResult> results = await _db.WorkflowStepResults
            .Where(r => r.OutputStorageKey != null && r.ReusedFromStepResultId == null && r.WorkflowExecution.ProjectId == projectId &&
                        (r.OutputStorageKey.StartsWith(outputPrefix) || r.OutputStorageKey.StartsWith(generatedPrefix)))
            .ToListAsync(ct);
        List<Guid> executionIds = results.Select(r => r.WorkflowExecutionId).Distinct().ToList();
        List<Guid> stepIds = results.Select(r => r.WorkflowStepId).Distinct().ToList();
        var executionInfo = await _db.WorkflowExecutions
            .Where(e => executionIds.Contains(e.Id))
            .Select(e => new { e.Id, e.WorkflowDefinitionId, e.Status })
            .ToDictionaryAsync(e => e.Id, ct);
        Dictionary<Guid, Guid> workflowByExecution = executionInfo.ToDictionary(e => e.Key, e => e.Value.WorkflowDefinitionId);
        bool InProgress(Guid executionId) =>
            executionInfo.TryGetValue(executionId, out var e) && e.Status is ExecutionStatus.Queued or ExecutionStatus.Running;
        var steps = await _db.WorkflowSteps
            .Where(s => stepIds.Contains(s.Id))
            .Select(s => new { s.Id, s.StepOrder, s.StepType })
            .ToDictionaryAsync(s => s.Id, ct);
        Dictionary<Guid, Guid> replayOwners = await ReplayTakeOwnersAsync(projectId, [.. workflowByExecution.Values.Distinct()], ct);

        string GroupOf(WorkflowStepResult r)
        {
            steps.TryGetValue(r.WorkflowStepId, out var s);
            (Guid? groupWorkflow, StepType? groupType) = TakeOwner(
                replayOwners, workflowByExecution.TryGetValue(r.WorkflowExecutionId, out Guid wf) ? wf : null, s?.StepType);
            return TakeGroupKey(groupWorkflow, s?.StepOrder, groupType, r.WorkflowStepId);
        }

        return results
            .OrderByDescending(r => r.CompletedAt ?? r.ExecutedAt)
            .GroupBy(GroupOf)
            .SelectMany(g => g.OrderBy(r => InProgress(r.WorkflowExecutionId))
                .Select((r, i) => new Take(r, g.Key, i + 1, InProgress(r.WorkflowExecutionId))))
            .ToList();
    }

    /// <summary>
    /// Pins (or unpins) one take: a pinned take is never removed by a take delete or by
    /// "delete earlier takes".
    /// </summary>
    [HttpPost("{stepResultId:guid}/pin")]
    public async Task<IActionResult> PinTake(Guid projectId, Guid stepResultId, [FromBody] PinTakeRequest request, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;
        Project project = access.Project!;

        WorkflowStepResult? result = await _db.WorkflowStepResults
            .FirstOrDefaultAsync(r => r.Id == stepResultId && r.WorkflowExecution.ProjectId == projectId && r.OutputStorageKey != null, ct);
        if (result == null) return NotFound();
        if (await OutputGate.IsResultHeldAsync(_db, result.Id, ct)) return OutputGate.Unavailable();

        result.IsPinned = request.Pinned;
        await _db.SaveChangesAsync(ct);
        return Ok(new { stepResultId, pinned = result.IsPinned });
    }

    /// <summary>
    /// Deletes one earlier take: its stored video, its project-file row (if it was saved to Files) and
    /// the run's link to it. The latest take of its step and a pinned take are refused with 409 — the
    /// latest take is the video, and a pin is the owner's explicit "keep this".
    /// </summary>
    [HttpDelete("{stepResultId:guid}")]
    public async Task<IActionResult> DeleteTake(Guid projectId, Guid stepResultId, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;
        Project project = access.Project!;

        List<Take> takes = await LoadTakesAsync(projectId, ct);
        Take? take = takes.FirstOrDefault(t => t.Result.Id == stepResultId);
        if (take == null) return NotFound();
        if ((await HeldTakeIdsAsync(projectId, [take.Result], ct)).Contains(take.Result.Id))
            return NotFound(new { code = OutputHoldResponse.Code, message = OutputHoldResponse.Message });
        if (take.Result.IsPinned)
            return Conflict(new { message = "This take is pinned. Unpin it first to delete it." });
        // An editor render is a one-off file from its own single-step render workflow, so it has no earlier take
        // to fall back to: the Deliver card deletes it directly. Every other video keeps the latest-take rule.
        bool editorRender = await _db.WorkflowExecutions
            .AnyAsync(e => e.Id == take.Result.WorkflowExecutionId && e.WorkflowDefinition.Kind == WorkflowDefinitionKind.TimelineRender, ct);
        if (take.Index == 1 && !editorRender)
            return Conflict(new { message = "This is the latest take of its video and is kept. Delete earlier takes instead." });
        if (take.InProgress)
            return Conflict(new { message = "This take's run is still going. Wait for it to finish." });

        await DeleteTakesAsync(projectId, [take.Result], takes, ct);
        return NoContent();
    }

    /// <summary>
    /// "Delete earlier takes": removes every take of one step's video except the newest
    /// <see cref="PruneTakesRequest.KeepLatest"/> (at least 1) and every pinned take.
    /// </summary>
    [HttpPost("prune")]
    public async Task<ActionResult<PruneTakesResponse>> PruneTakes(Guid projectId, [FromBody] PruneTakesRequest request, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;
        Project project = access.Project!;
        if (string.IsNullOrWhiteSpace(request.TakeGroupKey))
            return BadRequest(new { message = "takeGroupKey is required." });

        List<Take> takes = await LoadTakesAsync(projectId, ct);
        List<Take> group = takes.Where(t => t.GroupKey == request.TakeGroupKey).ToList();
        if (group.Count == 0) return NotFound();

        int keep = Math.Max(1, request.KeepLatest);
        List<WorkflowStepResult> doomed = group
            .Where(t => t.Index > keep && !t.Result.IsPinned && !t.InProgress)
            .Select(t => t.Result)
            .ToList();
        HashSet<Guid> heldIds = await HeldTakeIdsAsync(projectId, doomed, ct);
        if (heldIds.Count > 0) doomed = doomed.Where(r => !heldIds.Contains(r.Id)).ToList();
        await DeleteTakesAsync(projectId, doomed, takes, ct);
        return Ok(new PruneTakesResponse(doomed.Count, group.Count - doomed.Count));
    }

    /// <summary>
    /// Ids of the given results whose output the project keeps: by timestamp, or because the stored key is held.
    /// All of them when the state cannot be read.
    /// </summary>
    private async Task<HashSet<Guid>> HeldTakeIdsAsync(Guid projectId, List<WorkflowStepResult> results, CancellationToken ct)
    {
        ProjectOutputHoldState state = await OutputGate.ReadAsync(_db, projectId, ct);
        if (!state.IsActive || results.Count == 0) return [];
        if (state.Unreadable) return results.Select(r => r.Id).ToHashSet();
        HashSet<Guid> held = await OutputGate.HeldResultIdsAsync(_db, projectId, results.Select(r => r.Id).ToList(), ct);
        foreach (WorkflowStepResult r in results.Where(r => !held.Contains(r.Id)))
        {
            if (await OutputGate.IsKeyHeldAsync(_db, projectId, r.OutputStorageKey, ct))
                held.Add(r.Id);
        }
        return held;
    }

    /// <summary>
    /// Deletes the given takes: the stored object (only when no surviving take shares the same key —
    /// a cache hit reuses an earlier run's object), any project-file row for it, and the result's
    /// link to it. The step results themselves stay, so the run's history is intact.
    /// </summary>
    private async Task DeleteTakesAsync(Guid projectId, List<WorkflowStepResult> doomed, List<Take> all, CancellationToken ct)
    {
        if (doomed.Count == 0) return;
        HashSet<Guid> doomedIds = doomed.Select(r => r.Id).ToHashSet();
        HashSet<string> survivingKeys = all.Where(t => !doomedIds.Contains(t.Result.Id)).Select(t => t.Result.OutputStorageKey!).ToHashSet(StringComparer.Ordinal);
        HashSet<string> keys = doomed.Select(r => r.OutputStorageKey!).ToHashSet(StringComparer.Ordinal);
        // A composition version can still name a render that no surviving take owns: an unchanged
        // re-render dedupes onto the parent version, which keeps the EARLIER iteration's key. That
        // object is live, so it survives this delete (the take still loses its own link below). The
        // union has to happen before the row query and the delete below both read survivingKeys.
        List<string> versionKeys = await _db.CompositionWorkspaceVersions
            .Where(v => v.ProjectId == projectId && v.RenderOutputStorageKey != null && keys.Contains(v.RenderOutputStorageKey))
            .Select(v => v.RenderOutputStorageKey!)
            .ToListAsync(ct);
        survivingKeys.UnionWith(versionKeys);
        // A take saved as a project file that an avatar now lists as a reference keeps that file and its object (the
        // link is ON DELETE NO ACTION and the avatar's consent covers those bytes); the take still loses its own link.
        List<string> avatarKeys = await _db.ProjectFiles
            .Where(f => f.ProjectId == projectId && keys.Contains(f.StorageKey)
                        && _db.ProjectAvatarReferences.Any(r => r.ProjectFileId == f.Id))
            .Select(f => f.StorageKey)
            .ToListAsync(ct);
        survivingKeys.UnionWith(avatarKeys);

        // A failed-move quarantine keeps the original key on the row (CsamQuarantineKey names where
        // it should have gone). That key is evidence: the take prune must not delete the object or
        // the row. Successful moves point at /quarantine/, which is not in `keys` at all.
        HashSet<string> heldKeys = (await _db.ProjectFiles
                .Where(f => f.ProjectId == projectId && keys.Contains(f.StorageKey)
                            && (f.CsamStatus == CsamScanStatus.Quarantined || f.CsamQuarantineKey != null))
                .Select(f => f.StorageKey)
                .ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);

        List<ProjectFile> rows = await _db.ProjectFiles
            .Where(f => f.ProjectId == projectId && keys.Contains(f.StorageKey) && !survivingKeys.Contains(f.StorageKey)
                        && f.CsamStatus != CsamScanStatus.Quarantined && f.CsamQuarantineKey == null)
            .ToListAsync(ct);
        // While the project keeps its outputs, a file row it holds stays, with its object.
        if (rows.Count > 0)
        {
            HashSet<Guid> heldRowIds = await OutputGate.HeldFileIdsAsync(_db, projectId, rows, ct);
            if (heldRowIds.Count > 0)
            {
                heldKeys.UnionWith(rows.Where(f => heldRowIds.Contains(f.Id)).Select(f => f.StorageKey));
                rows = rows.Where(f => !heldRowIds.Contains(f.Id)).ToList();
            }
        }
        // D10: a picture made from one of these output images keeps its own copy before the object goes.
        await PictureSourceCopies.CopyBeforeDeleteAsync(
            _db, _fileStorage, projectId, rows, null, ct,
            async (key, token) => _publishModeration is null || await _publishModeration.GateStoredCopyAsync(projectId, key, token) is null);

        foreach (string key in keys.Where(k => ProjectOutputKeys.IsAddressableOutput(projectId, k)
                                               && !survivingKeys.Contains(k) && !heldKeys.Contains(k)))
        {
            try
            {
                await _s3Client.DeleteObjectAsync(_bucketName, key, ct);
                await ImagePreviewStore.DeleteAllAsync(_s3Client, _bucketName, projectId, key, ct);
            }
            catch (AmazonS3Exception ex) when (IsMissingObjectError(ex))
            {
                // Already gone — nothing to delete.
            }
        }

        _db.ProjectFiles.RemoveRange(rows);
        foreach (WorkflowStepResult r in doomed)
            r.OutputStorageKey = null;

        // Copies a composition revision made of a deleted take (never listed themselves) point at the same
        // object: they lose the link with it, so nothing keeps pointing at a file that is gone.
        List<string> deletedKeys = [.. keys.Where(k => !survivingKeys.Contains(k) && !heldKeys.Contains(k))];
        // The stored moderation verdict goes with the object it judged.
        _db.OutputModerationVerdicts.RemoveRange(await _db.OutputModerationVerdicts
            .Where(v => v.ProjectId == projectId && deletedKeys.Contains(v.StorageKey))
            .ToListAsync(ct));
        List<WorkflowStepResult> copies = await _db.WorkflowStepResults
            .Where(r => r.ReusedFromStepResultId != null && r.OutputStorageKey != null
                        && r.WorkflowExecution.ProjectId == projectId && deletedKeys.Contains(r.OutputStorageKey))
            .ToListAsync(ct);
        foreach (WorkflowStepResult copy in copies)
            copy.OutputStorageKey = null;
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Streams the media artifact for a specific step result directly from S3/MinIO.
    /// The Content-Disposition header is set to inline so browsers can play it directly; HTTP Range requests are honoured.
    /// With <c>?saveAs=name</c> it is an attachment under that name instead (the Deliver queue's Download button), so a
    /// click saves a file rather than navigating to a bare player. With <c>?w=</c> (snapped up to 160..1280) a picture is
    /// answered as a smaller WebP preview, cached by ETag.
    /// </summary>
    [HttpGet("{stepResultId:guid}/download")]
    public async Task<IActionResult> DownloadOutput(Guid projectId, Guid stepResultId, CancellationToken ct, [FromQuery] string? saveAs = null, [FromQuery] string? w = null)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return denied;
        Project project = access.Project!;

        WorkflowStepResult? stepResult = await _db.WorkflowStepResults
            .Include(r => r.WorkflowExecution)
            .FirstOrDefaultAsync(r => r.Id == stepResultId && r.WorkflowExecution.ProjectId == projectId, ct);

        if (stepResult == null) return NotFound();
        if (stepResult.OutputStorageKey == null) return NotFound("This step result has no media output.");
        if (await OutputGate.IsResultHeldAsync(_db, stepResult.Id, ct)) return OutputGate.Unavailable();

        // Validate storage key scope: must be one of this project's addressable outputs — a render under
        // outputFiles/ or a generated clip under agentFiles/generated/ — exactly like GetOutput and the
        // output links (ProjectOutputKeys). The trailing slash on each prefix makes it a true path-segment
        // prefix, so a key like "projects/{id}/outputFiles-tmp/..." never matches.
        if (!ProjectOutputKeys.IsAddressableOutput(projectId, stepResult.OutputStorageKey))
            return Forbid();

        string fileName = Path.GetFileName(stepResult.OutputStorageKey);
        // A render stored under a bare id downloads under the workflow's name, not "<id>.mp4".
        string? workflowName = await _db.WorkflowDefinitions.AsNoTracking()
            .Where(w => w.Id == stepResult.WorkflowExecution.WorkflowDefinitionId)
            .Select(w => w.Name)
            .FirstOrDefaultAsync(ct);
        string downloadName = ReadableDownloadName(fileName, workflowName);

        // T-004: publication is the only fail-closed surface. Refuse a blocked or unclassifiable
        // render before a byte is streamed.
        if (_publishModeration is not null)
        {
            PublishModerationOutcome outcome = await _publishModeration.EvaluateObjectAsync(
                projectId,
                stepResult.OutputStorageKey,
                InferContentType(fileName) ?? "application/octet-stream",
                new ModerationAttribution(
                    project.OrganizationId, projectId, _currentUser.UserId, stepResult.WorkflowExecutionId,
                    stepResult.Id, ModerationSurface.Publish, ModerationContentKind.Text),
                ct);
            if (!outcome.Allowed)
            {
                // Pending: the video is still being checked. Answer 202 (accepted, not ready yet) with a short
                // Retry-After so the page polls instead of showing an error; no byte is served until a verdict allows it.
                if (outcome.Code == PublishModerationOutcome.PendingCode)
                {
                    Response.Headers["Retry-After"] = _pendingRetryAfter;
                    return StatusCode(
                        StatusCodes.Status202Accepted,
                        new { message = outcome.Message, code = outcome.Code, @final = outcome.Final });
                }

                // Blocked and unavailable keep their fail-closed answers (422 and 409) exactly as before.
                return outcome.Code == "CONTENT_BLOCKED"
                    ? StatusCode(StatusCodes.Status422UnprocessableEntity, new { message = outcome.Message, code = outcome.Code, @final = outcome.Final })
                    : StatusCode(StatusCodes.Status409Conflict, new { message = outcome.Message, code = outcome.Code, @final = outcome.Final });
            }
        }

        // Best-effort first-download marker (never fails the download).
        if (_productEvents is not null)
        {
            await _productEvents.FirstAsync(
                _currentUser.UserId, project.OrganizationId, ProductEventNames.OutputDownloaded, ct);
        }

        try
        {
            // Range-aware so browsers can seek and iOS Safari can play at all.
            // Pictures also get ETag/Cache-Control and an optional `w` WebP preview; video and audio are untouched.
            string contentType = InferContentType(fileName);
            bool isPicture = ImageThumbnailer.IsThumbnailable(contentType) || contentType == "image/gif";
            return await RangedObjectStreamer.StreamAsync(
                this, _s3Client, _bucketName, stepResult.OutputStorageKey, contentType, downloadName, ct, ReadableSaveAs(saveAs, downloadName),
                isPicture, ImageThumbnailer.IsThumbnailable(contentType) ? ImageThumbnailer.SnapWidth(int.TryParse(w, out int requestedWidth) ? requestedWidth : null) : null, projectId);
        }
        catch (AmazonS3Exception ex) when (IsMissingObjectError(ex))
        {
            return NotFound("Output artifact not found in storage.");
        }
    }

    /// <summary>
    /// The name a download is offered under. A stored name that is only an id (a render is stored as
    /// <c>&lt;guid&gt;.mp4</c>) becomes the workflow's name, sanitized, with the stored extension; a name that
    /// already reads as words is kept.
    /// </summary>
    internal static string ReadableDownloadName(string storedFileName, string? workflowName)
    {
        if (string.IsNullOrWhiteSpace(workflowName)
            || !Guid.TryParse(Path.GetFileNameWithoutExtension(storedFileName), out _))
            return storedFileName;
        return SafeContentPolicy.WithExtensionOf(workflowName, storedFileName);
    }
    /// <summary>
    /// The web's Download button sends the stored name as <c>saveAs</c>; when that is only an id, the readable
    /// name is offered instead. A name that reads as words is the caller's choice and is kept.
    /// </summary>
    internal static string? ReadableSaveAs(string? saveAs, string readableName)
        => saveAs is not null && Guid.TryParse(Path.GetFileNameWithoutExtension(saveAs), out _) ? readableName : saveAs;

    private static bool IsMissingObjectError(AmazonS3Exception ex)
    {
        return ex.StatusCode == HttpStatusCode.NotFound ||
               string.Equals(ex.ErrorCode, "NoSuchKey", StringComparison.Ordinal) ||
               string.Equals(ex.ErrorCode, "NotFound", StringComparison.Ordinal);
    }

    private static string InferContentType(string fileName)
    {
        string ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".mp4" => "video/mp4",
            ".webm" => "video/webm",
            ".gif" => "image/gif",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            _ => "application/octet-stream"
        };
    }
}

/// <summary>
/// One render. The first six members are the original shape; everything after <c>ProducedAt</c> was
/// appended so the Renders tab can say WHICH video this is (workflow, run, final vs draft, length,
/// size) and what went wrong in its run, instead of listing bare file names.
/// </summary>
public record OutputVideoResponse(
    Guid StepResultId,
    Guid WorkflowExecutionId,
    Guid WorkflowStepId,
    string StorageKey,
    string FileName,
    DateTime ProducedAt,
    Guid? WorkflowDefinitionId = null,
    string? WorkflowName = null,
    int? RunNumber = null,
    DateTime? RunStartedAt = null,
    string? ExecutionStatus = null,
    string? StepLabel = null,
    string? StepType = null,
    int? IterationNumber = null,
    double? DurationSec = null,
    int? Width = null,
    int? Height = null,
    bool IsFinal = false,
    IReadOnlyList<OutputWarning>? Warnings = null,
    // Takes (docs/video-editing.md "Takes: the latest render first"), appended: which video this
    // render is a take of, its place among that video's takes (1 = latest), whether it is an
    // intermediate render of its run (a review-loop pass or retry), and the owner's pin.
    string? TakeGroupKey = null,
    int TakeIndex = 1,
    int TakeCount = 1,
    bool IsLatestTake = true,
    bool IsIntermediate = false,
    bool IsPinned = false);

/// <summary>Body of <c>POST …/outputs/{stepResultId}/pin</c>.</summary>
public record PinTakeRequest(bool Pinned);

/// <summary>Body of <c>POST …/outputs/prune</c>: the take group to clean and how many of its newest takes to keep (at least 1).</summary>
public record PruneTakesRequest(string TakeGroupKey, int KeepLatest = 1);

/// <summary>What <c>POST …/outputs/prune</c> removed and kept.</summary>
public record PruneTakesResponse(int Deleted, int Kept);

/// <summary>A customer-facing run warning (contract C1: <c>{ code, message }</c>).</summary>
public record OutputWarning(string Code, string Message);

/// <summary>Length and frame size of a render, when its step's OutputJson records them.</summary>
public readonly record struct RenderMetadata(double? DurationSec, int? Width, int? Height);

/// <summary>
/// Tolerant reader for a step result's OutputJson: never throws, returns nulls/empty for anything
/// missing, malformed or of the wrong JSON kind.
/// </summary>
public static class RenderMetadataReader
{
    public static RenderMetadata Read(string? outputJson)
    {
        if (string.IsNullOrWhiteSpace(outputJson)) return default;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(outputJson);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return default;
            double? duration = Number(root, "outputDurationSec") ?? Number(root, "durationSec");
            int? width = Int(root, "outputWidth") ?? Int(root, "width");
            int? height = Int(root, "outputHeight") ?? Int(root, "height");
            foreach (string nested in new[] { "output", "resolution", "canvas" })
            {
                if (width is not null && height is not null) break;
                if (root.TryGetProperty(nested, out JsonElement n) && n.ValueKind == JsonValueKind.Object)
                {
                    width ??= Int(n, "width");
                    height ??= Int(n, "height");
                }
            }
            return new RenderMetadata(duration, width, height);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    public static IEnumerable<OutputWarning> ReadWarnings(string? outputJson)
    {
        if (string.IsNullOrWhiteSpace(outputJson) || !outputJson.Contains("warnings", StringComparison.Ordinal)) return [];
        try
        {
            using JsonDocument doc = JsonDocument.Parse(outputJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("warnings", out JsonElement arr)
                || arr.ValueKind != JsonValueKind.Array)
                return [];
            List<OutputWarning> list = [];
            foreach (JsonElement w in arr.EnumerateArray())
            {
                if (w.ValueKind != JsonValueKind.Object) continue;
                string? code = w.TryGetProperty("code", out JsonElement c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
                string? message = w.TryGetProperty("message", out JsonElement m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
                if (!string.IsNullOrWhiteSpace(message)) list.Add(new OutputWarning(code ?? "warning", message));
            }
            return list;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static double? Number(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double d) && d > 0 ? d : null;

    private static int? Int(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i) && i > 0 ? i : null;
}
