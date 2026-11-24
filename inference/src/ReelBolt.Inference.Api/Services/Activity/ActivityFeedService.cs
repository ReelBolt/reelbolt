using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Text.Json;
using System.Text.Json.Serialization;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.Shared.Tenancy;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.Inference.Api.Services.Activity;

/// <summary>
/// Builds the activity feed from existing tables only (no migration): workflow runs of every kind and media analysis
/// requests. Read-only, AsNoTracking, and a small fixed number of queries: the candidates come from two bounded queries,
/// are ordered and cut to the limit, and only then enriched with a handful of keyed queries over the page.
/// <para>
/// Not listed, on purpose: media prep (<c>media_derivatives</c> has no queued or running state, a row exists only once built;
/// progress is live over SSE only), cloud drive sync (<c>project_cloud_links</c> has no syncing state, only last-synced and last-error
/// without a time for the error), folder sync and file indexing.
/// </para>
/// </summary>
public sealed class ActivityFeedService : IActivityFeedService
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 100;

    /// <summary>Queued or running jobs older than this are no longer shown.</summary>
    public static readonly TimeSpan ActiveWindow = TimeSpan.FromDays(7);

    /// <summary>Finished jobs stay listed this long.</summary>
    public static readonly TimeSpan FinishedWindow = TimeSpan.FromHours(24);

    private const int MaxActiveScan = 1000;
    private const int MaxFinishedScan = 300;

    private static readonly JsonSerializerOptions ConfigJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly InferenceApiDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IOptions<BillingOptions> _billing;
    private readonly TimeProvider _clock;

    public ActivityFeedService(InferenceApiDbContext db, ICurrentUser currentUser, IOptions<BillingOptions> billing, TimeProvider? clock = null)
    {
        _db = db;
        _currentUser = currentUser;
        _billing = billing;
        _clock = clock ?? TimeProvider.System;
    }

    public static int ClampLimit(int? limit) => Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);

    private sealed record RunRow(
        Guid Id, Guid WorkflowDefinitionId, Guid ProjectId, string ProjectName, ExecutionStatus Status,
        DateTime? QueuedAt, DateTime? StartedAt, DateTime? CompletedAt, Guid? CurrentStepId, Guid? SnapshotDefinitionId,
        Guid? InitiatedByUserId, WorkflowDefinitionKind Kind, string DefinitionName, string? TemplateKey);

    private sealed record AnalysisRow(
        Guid Id, Guid ProjectId, string ProjectName, string MediaRef, string Kind, string State, long HoldMillis, bool HoldSettled,
        Guid? RequestedByUserId, DateTime CreatedAt, DateTime UpdatedAt);

    /// <summary>A listed job before the page enrichment.</summary>
    private sealed record Candidate(string State, DateTime Created, DateTime? Started, DateTime? Ended, RunRow? Run, AnalysisRow? Analysis);

    private static RunRow ToRunRow(WorkflowExecution e, WorkflowDefinition d, Project p) =>
        new(e.Id, e.WorkflowDefinitionId, e.ProjectId, p.Name, e.Status, e.QueuedAt, e.StartedAt, e.CompletedAt,
            e.CurrentStepId, e.SnapshotDefinitionId, e.InitiatedByUserId, d.Kind, d.Name, d.TemplateKey);

    private static AnalysisRow ToAnalysisRow(MediaAnalysisRequest a, Project p) =>
        new(a.Id, a.ProjectId, p.Name, a.MediaRef, a.Kind, a.State, a.HoldMillis, a.HoldSettled,
            a.RequestedByUserId, a.CreatedAt, a.UpdatedAt);

    public async Task<ActivityFeedDto> GetAsync(int limit, CancellationToken ct)
    {
        DateTime now = _clock.GetUtcNow().UtcDateTime;
        limit = Math.Clamp(limit, 1, MaxLimit);

        Guid? orgId = _currentUser.OrganizationId;
        OrganizationRole? role = _currentUser.OrgRole;
        // No workspace, or Billing (which has no project access at all and is hidden from the SSE stream too): nothing to show.
        // Every other role may View every project of its organization (ProjectAccessService.Allowed), so there is no
        // per-project filter to apply beyond the organization itself.
        if (orgId is not { } org || role is not { } r || !ProjectAccessService.Allowed(r, ProjectPermission.View, isCreator: false))
            return new ActivityFeedDto([], now, 0);

        bool canEdit = ProjectAccessService.Allowed(r, ProjectPermission.Edit, isCreator: false);
        bool billingOn = _billing.Value.Enabled;
        Guid me = _currentUser.UserId;

        DateTime activeCutoff = now - ActiveWindow;
        DateTime finishedCutoff = now - FinishedWindow;

        List<Candidate> candidates = [];
        int activeCount = 0;

        // Runs: bounded to the window, never the hidden snapshot copies, only this organization's projects.
        // Filters and ordering run on the entity columns (Npgsql translates them); the rows are mapped to records afterwards.
        var runs =
            from e in _db.WorkflowExecutions.AsNoTracking()
            join d in _db.WorkflowDefinitions.AsNoTracking() on e.WorkflowDefinitionId equals d.Id
            join p in _db.Projects.AsNoTracking() on e.ProjectId equals p.Id
            where e.OrganizationId == org && p.OrganizationId == org && d.Kind != WorkflowDefinitionKind.ExecutionSnapshot
            select new { e, d, p };

        var activeRaw = await runs
            .Where(x => (x.e.Status == ExecutionStatus.Queued || x.e.Status == ExecutionStatus.Running)
                && ((x.e.QueuedAt ?? x.e.StartedAt) == null || (x.e.QueuedAt ?? x.e.StartedAt) >= activeCutoff))
            .OrderBy(x => x.e.QueuedAt ?? x.e.StartedAt)
            .Take(MaxActiveScan)
            .ToListAsync(ct);
        var finishedRaw = await runs
            .Where(x => x.e.Status != ExecutionStatus.Queued && x.e.Status != ExecutionStatus.Running
                && (x.e.CompletedAt ?? x.e.StartedAt ?? x.e.QueuedAt) >= finishedCutoff)
            .OrderByDescending(x => x.e.CompletedAt ?? x.e.StartedAt ?? x.e.QueuedAt)
            .Take(MaxFinishedScan)
            .ToListAsync(ct);
        List<RunRow> activeRuns = activeRaw.Select(x => ToRunRow(x.e, x.d, x.p)).ToList();
        List<RunRow> finishedRuns = finishedRaw.Select(x => ToRunRow(x.e, x.d, x.p)).ToList();

        foreach (RunRow x in activeRuns.Concat(finishedRuns))
        {
            DateTime? ended = x.CompletedAt ?? (IsActive(x.Status) ? null : x.StartedAt ?? x.QueuedAt);
            candidates.Add(new Candidate(RunState(x.Status), Utc(x.QueuedAt ?? x.StartedAt ?? ended ?? now), Utc(x.StartedAt), Utc(ended), x, null));
        }

        // Media analysis requests.
        var analyses =
            from a in _db.MediaAnalysisRequests.AsNoTracking()
            join p in _db.Projects.AsNoTracking() on a.ProjectId equals p.Id
            where a.OrganizationId == org && p.OrganizationId == org
            select new { a, p };
        var activeAnalysisRaw = await analyses
            .Where(x => (x.a.State == MediaAnalysisStates.Queued || x.a.State == MediaAnalysisStates.Running) && x.a.CreatedAt >= activeCutoff)
            .OrderBy(x => x.a.CreatedAt).Take(MaxActiveScan).ToListAsync(ct);
        var finishedAnalysisRaw = await analyses
            .Where(x => (x.a.State == MediaAnalysisStates.Ready || x.a.State == MediaAnalysisStates.Failed) && x.a.UpdatedAt >= finishedCutoff)
            .OrderByDescending(x => x.a.UpdatedAt).Take(MaxFinishedScan).ToListAsync(ct);
        List<AnalysisRow> activeAnalyses = activeAnalysisRaw.Select(x => ToAnalysisRow(x.a, x.p)).ToList();
        List<AnalysisRow> finishedAnalyses = finishedAnalysisRaw.Select(x => ToAnalysisRow(x.a, x.p)).ToList();
        foreach (AnalysisRow a in activeAnalyses)
            candidates.Add(new Candidate(AnalysisState(a.State), Utc(a.CreatedAt), a.State == MediaAnalysisStates.Running ? Utc(a.UpdatedAt) : null, null, null, a));
        foreach (AnalysisRow a in finishedAnalyses)
            candidates.Add(new Candidate(AnalysisState(a.State), Utc(a.CreatedAt), Utc(a.CreatedAt), Utc(a.UpdatedAt), null, a));

        activeCount = candidates.Count(c => c.State is "running" or "waiting");

        // Running (oldest first), then waiting (oldest first), then finished by endedAt, newest first.
        List<Candidate> page = candidates
            .OrderBy(c => c.State == "running" ? 0 : c.State == "waiting" ? 1 : 2)
            .ThenBy(c => c.State is "running" or "waiting" ? c.Created : DateTime.MinValue)
            .ThenByDescending(c => c.State is "running" or "waiting" ? DateTime.MinValue : c.Ended ?? c.Created)
            .ThenBy(c => c.Run?.Id ?? c.Analysis!.Id)
            .Take(limit)
            .ToList();

        Enrichment e2 = await EnrichAsync(page, org, billingOn, ct);

        List<ActivityItemDto> items = [];
        foreach (Candidate c in page)
        {
            items.Add(c.Run is { } run
                ? BuildRun(c, run, e2, billingOn, canEdit, me)
                : BuildAnalysis(c, c.Analysis!, e2, billingOn, me));
        }

        return new ActivityFeedDto(items, now, activeCount);
    }

    // ---- enrichment: keyed queries over the page only ----

    private sealed record StepInfo(Guid Id, Guid DefinitionId, int Order, StepType Type);

    private sealed record ResultInfo(Guid ExecutionId, StepStatus Status, FailureClass FailureClass, string? FailureCode, long Charged, DateTime ExecutedAt);

    private sealed class Enrichment
    {
        public Dictionary<Guid, StepInfo> StepsById { get; } = [];
        public Dictionary<Guid, int> StepCount { get; } = [];
        public Dictionary<Guid, long> HeldMillis { get; } = [];
        public Dictionary<Guid, List<ResultInfo>> Results { get; } = [];
        public Dictionary<Guid, Guid> ChangeRequestSource { get; } = [];
        public Dictionary<Guid, Guid> RevisionSource { get; } = [];
        public Dictionary<Guid, Guid> GraphicDocument { get; } = [];
        public Dictionary<Guid, (string Prompt, string MediaRef, Guid? TimelineId)> ObjectRequest { get; } = [];
        public Dictionary<Guid, TimelineRenderStepConfig> RenderConfig { get; } = [];
        public Dictionary<Guid, string> TimelineNames { get; } = [];
        public Dictionary<Guid, string> DocumentNames { get; } = [];
        public Dictionary<Guid, string> FileNames { get; } = [];
    }

    private async Task<Enrichment> EnrichAsync(List<Candidate> page, Guid org, bool billingOn, CancellationToken ct)
    {
        Enrichment e = new();
        List<RunRow> runs = page.Where(c => c.Run is not null).Select(c => c.Run!).ToList();
        List<Guid> runIds = runs.Select(x => x.Id).ToList();
        HashSet<Guid> fileIds = [];
        HashSet<Guid> timelineIds = [];

        foreach (AnalysisRow a in page.Where(c => c.Analysis is not null).Select(c => c.Analysis!))
            if (FileIdOf(a.MediaRef) is { } fid) fileIds.Add(fid);

        if (runIds.Count > 0)
        {
            List<Guid> defIds = runs.SelectMany(x => new[] { (Guid?)x.WorkflowDefinitionId, x.SnapshotDefinitionId }).OfType<Guid>().Distinct().ToList();
            var steps = await _db.WorkflowSteps.AsNoTracking()
                .Where(s => defIds.Contains(s.WorkflowDefinitionId))
                .Select(s => new StepInfo(s.Id, s.WorkflowDefinitionId, s.StepOrder, s.StepType))
                .ToListAsync(ct);
            foreach (StepInfo s in steps) e.StepsById[s.Id] = s;
            foreach (var g in steps.GroupBy(s => s.DefinitionId)) e.StepCount[g.Key] = g.Count();

            List<Guid> renderDefs = runs.Where(x => x.Kind == WorkflowDefinitionKind.TimelineRender).Select(x => x.WorkflowDefinitionId).Distinct().ToList();
            if (renderDefs.Count > 0)
            {
                var configs = await _db.WorkflowSteps.AsNoTracking()
                    .Where(s => renderDefs.Contains(s.WorkflowDefinitionId) && s.StepType == StepType.TimelineRender)
                    .Select(s => new { s.WorkflowDefinitionId, s.TimelineRenderConfigJson })
                    .ToListAsync(ct);
                foreach (var c in configs)
                {
                    if (ParseConfig(c.TimelineRenderConfigJson) is { } cfg && !e.RenderConfig.ContainsKey(c.WorkflowDefinitionId))
                    {
                        e.RenderConfig[c.WorkflowDefinitionId] = cfg;
                        timelineIds.Add(cfg.EditTimelineId);
                    }
                }
            }

            var results = await _db.WorkflowStepResults.AsNoTracking()
                .Where(r => runIds.Contains(r.WorkflowExecutionId) && r.SupersededAt == null)
                .Select(r => new ResultInfo(r.WorkflowExecutionId, r.Status, r.FailureClass, r.FailureCode, r.CreditsChargedMillis, r.ExecutedAt))
                .ToListAsync(ct);
            foreach (var g in results.GroupBy(r => r.ExecutionId)) e.Results[g.Key] = g.ToList();

            if (billingOn)
            {
                // Open holds: a Reserve of the run with no Settle or Release pointing at it (how UnsettledHoldSweeper finds them).
                var reserves = await _db.CreditLedgerEntries.AsNoTracking()
                    .Where(l => l.OrganizationId == org && l.Kind == CreditLedgerEntryKind.Reserve && l.ExecutionId != null && runIds.Contains(l.ExecutionId.Value))
                    .Select(l => new { l.Id, ExecutionId = l.ExecutionId!.Value, l.ReservedDeltaMillis })
                    .ToListAsync(ct);
                if (reserves.Count > 0)
                {
                    List<Guid> reserveIds = reserves.Select(r => r.Id).ToList();
                    List<Guid?> closed = await _db.CreditLedgerEntries.AsNoTracking()
                        .Where(l => (l.Kind == CreditLedgerEntryKind.Settle || l.Kind == CreditLedgerEntryKind.Release) && l.RefEntryId != null && reserveIds.Contains(l.RefEntryId.Value))
                        .Select(l => l.RefEntryId)
                        .ToListAsync(ct);
                    HashSet<Guid> closedIds = closed.OfType<Guid>().ToHashSet();
                    foreach (var r in reserves.Where(r => !closedIds.Contains(r.Id)))
                        e.HeldMillis[r.ExecutionId] = e.HeldMillis.GetValueOrDefault(r.ExecutionId) + r.ReservedDeltaMillis;
                }
            }

            foreach (var c in await _db.StepChangeRequests.AsNoTracking().Where(c => runIds.Contains(c.ExecutionId))
                         .Select(c => new { c.ExecutionId, c.SourceExecutionId }).ToListAsync(ct))
                e.ChangeRequestSource[c.ExecutionId] = c.SourceExecutionId;
            foreach (var c in await _db.CompositionRevisions.AsNoTracking().Where(c => runIds.Contains(c.ExecutionId))
                         .Select(c => new { c.ExecutionId, c.SourceExecutionId }).ToListAsync(ct))
                e.RevisionSource[c.ExecutionId] = c.SourceExecutionId;
            foreach (var g in await _db.EditImageGraphicRequests.AsNoTracking().Where(g => runIds.Contains(g.ExecutionId))
                         .Select(g => new { g.ExecutionId, g.DocumentId }).ToListAsync(ct))
                e.GraphicDocument[g.ExecutionId] = g.DocumentId;
            foreach (var o in await _db.ObjectEffectRequests.AsNoTracking().Where(o => runIds.Contains(o.ExecutionId))
                         .Select(o => new { o.ExecutionId, o.Prompt, o.SourceKind, o.SourceId, o.TimelineId }).ToListAsync(ct))
            {
                e.ObjectRequest[o.ExecutionId] = (o.Prompt, $"{o.SourceKind}:{o.SourceId}", o.TimelineId);
                if (o.TimelineId is { } t) timelineIds.Add(t);
                if (o.SourceKind == ObjectEffectRequest.SourceFile && Guid.TryParse(o.SourceId, out Guid f)) fileIds.Add(f);
            }
        }

        if (timelineIds.Count > 0)
            foreach (var t in await _db.EditTimelines.AsNoTracking().Where(t => timelineIds.Contains(t.Id))
                         .Select(t => new { t.Id, t.Name }).ToListAsync(ct))
                e.TimelineNames[t.Id] = t.Name;

        List<Guid> docIds = e.GraphicDocument.Values.Distinct().ToList();
        if (docIds.Count > 0)
            foreach (var d in await _db.EditImageDocuments.AsNoTracking().Where(d => docIds.Contains(d.Id))
                         .Select(d => new { d.Id, d.Name }).ToListAsync(ct))
                if (!string.IsNullOrWhiteSpace(d.Name)) e.DocumentNames[d.Id] = d.Name!;

        if (fileIds.Count > 0)
            foreach (var f in await _db.ProjectFiles.AsNoTracking().Where(f => fileIds.Contains(f.Id))
                         .Select(f => new { f.Id, f.OriginalFileName }).ToListAsync(ct))
                e.FileNames[f.Id] = f.OriginalFileName;

        return e;
    }

    // ---- mapping ----

    private ActivityItemDto BuildRun(Candidate c, RunRow x, Enrichment e, bool billingOn, bool canEdit, Guid me)
    {
        e.Results.TryGetValue(x.Id, out List<ResultInfo>? results);
        results ??= [];
        e.RenderConfig.TryGetValue(x.WorkflowDefinitionId, out TimelineRenderStepConfig? cfg);

        string type;
        string? title = null;
        ActivitySubjectDto subject = new();
        switch (x.Kind)
        {
            case WorkflowDefinitionKind.Standard:
                type = string.Equals(x.TemplateKey, WorkflowTemplateCatalog.ProductProfileFromCodeTemplateKey, StringComparison.Ordinal)
                    ? "designSystemBuild" : "workflowRun";
                title = NameOrNull(x.DefinitionName);
                break;
            case WorkflowDefinitionKind.CompositionRevision:
                type = "revision";
                if (e.RevisionSource.TryGetValue(x.Id, out Guid revSource)) subject = new ActivitySubjectDto(SourceExecutionId: revSource);
                break;
            case WorkflowDefinitionKind.CompositionReplay:
                if (e.ChangeRequestSource.TryGetValue(x.Id, out Guid crSource))
                {
                    type = "changeRequest";
                    subject = new ActivitySubjectDto(SourceExecutionId: crSource);
                }
                else
                {
                    type = "revision";
                    if (e.RevisionSource.TryGetValue(x.Id, out Guid rSource)) subject = new ActivitySubjectDto(SourceExecutionId: rSource);
                }

                break;
            case WorkflowDefinitionKind.GraphicDesign:
                type = "graphic";
                if (e.GraphicDocument.TryGetValue(x.Id, out Guid docId))
                {
                    subject = new ActivitySubjectDto(DocumentId: docId);
                    title = e.DocumentNames.GetValueOrDefault(docId);
                }

                break;
            case WorkflowDefinitionKind.TimelineRender:
                type = cfg?.AudioJobKind switch
                {
                    "preview" => "preview",
                    "loudness" => "loudness",
                    "audition" => "audition",
                    _ => "render"
                };
                if (cfg is not null)
                {
                    subject = new ActivitySubjectDto(TimelineId: cfg.EditTimelineId);
                    title = e.TimelineNames.GetValueOrDefault(cfg.EditTimelineId);
                }

                break;
            case WorkflowDefinitionKind.ObjectAnalysis:
                type = "objectAnalysis";
                if (e.ObjectRequest.TryGetValue(x.Id, out var obj))
                {
                    subject = new ActivitySubjectDto(MediaRef: obj.MediaRef, TimelineId: obj.TimelineId);
                    title = NameOrNull(obj.Prompt);
                }

                break;
            case WorkflowDefinitionKind.AudioCleanup:
                type = "audioCleanup";
                break;
            case WorkflowDefinitionKind.SubjectClip:
                // A clip with the project's avatars and objects (the editor's "Generate clip"): the web's own job type, titled
                // with the names it shows, as the editor tracks it.
                type = "subjectClip";
                title = ReelBolt.Inference.Api.Services.Avatars.SubjectClipNames.TitleOf(x.DefinitionName);
                break;
            // Any other kind is a hiddenRun.
            default:
                type = "hiddenRun";
                subject = new ActivitySubjectDto(KindNumber: (int)x.Kind);
                break;
        }

        bool active = c.State is "running" or "waiting";

        ActivityStepDto? step = null;
        if (active)
        {
            Guid stepsOf = x.SnapshotDefinitionId ?? x.WorkflowDefinitionId;
            int count = e.StepCount.GetValueOrDefault(stepsOf);
            if (count == 0) count = e.StepCount.GetValueOrDefault(x.WorkflowDefinitionId);
            StepInfo? current = x.CurrentStepId is { } sid && e.StepsById.TryGetValue(sid, out StepInfo? si) ? si : null;
            step = new ActivityStepDto(current?.Order ?? 0, count, current?.Type.ToString());
        }

        ActivityCreditsDto credits = new(billingOn && active && e.HeldMillis.GetValueOrDefault(x.Id) > 0 ? e.HeldMillis[x.Id] / 1000.0 : null,
            billingOn ? results.Sum(r => r.Charged) / 1000.0 : null);

        ActivityFailureDto? failure = null;
        if (c.State is "failed" or "cancelled")
        {
            ResultInfo? failed = results.Where(r => r.Status == StepStatus.Failed).OrderByDescending(r => r.ExecutedAt).FirstOrDefault();
            bool? refunded = billingOn && failed is not null && failed.FailureClass != FailureClass.None
                ? ActivityFailureRefund.IsRefunded(failed.FailureClass) : null;
            failure = new ActivityFailureDto(failed?.FailureCode, refunded);
        }

        return new ActivityItemDto(
            $"run:{x.Id}", type, c.State, x.ProjectId, x.ProjectName, title, x.Id, x.WorkflowDefinitionId, subject, step, null, credits, failure,
            x.InitiatedByUserId == me, active && canEdit, c.Created, c.Started, c.Ended);
    }

    private static ActivityItemDto BuildAnalysis(Candidate c, AnalysisRow a, Enrichment e, bool billingOn, Guid me)
    {
        bool active = c.State is "running" or "waiting";
        string? title = FileIdOf(a.MediaRef) is { } fid ? e.FileNames.GetValueOrDefault(fid) : null;
        return new ActivityItemDto(
            $"analysis:{a.Id}", "mediaAnalysis", c.State, a.ProjectId, a.ProjectName, title, null, null,
            new ActivitySubjectDto(MediaRef: a.MediaRef, AnalysisKind: a.Kind), null, null,
            new ActivityCreditsDto(billingOn && active && !a.HoldSettled && a.HoldMillis > 0 ? a.HoldMillis / 1000.0 : null, null),
            c.State == "failed" ? new ActivityFailureDto(null, null) : null,
            a.RequestedByUserId == me, false, c.Created, c.Started, c.Ended);
    }

    private static bool IsActive(ExecutionStatus s) => s is ExecutionStatus.Queued or ExecutionStatus.Running;

    private static string RunState(ExecutionStatus s) => s switch
    {
        ExecutionStatus.Queued => "waiting",
        ExecutionStatus.Running => "running",
        ExecutionStatus.Passed => "done",
        ExecutionStatus.Failed => "failed",
        ExecutionStatus.Cancelled => "cancelled",
        _ => "failed"
    };

    private static string AnalysisState(string s) => s switch
    {
        MediaAnalysisStates.Queued => "waiting",
        MediaAnalysisStates.Running => "running",
        MediaAnalysisStates.Ready => "done",
        _ => "failed"
    };

    private static DateTime Utc(DateTime v) => DateTime.SpecifyKind(v, DateTimeKind.Utc);

    private static DateTime? Utc(DateTime? v) => v is { } d ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : null;

    private static string? NameOrNull(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    /// <summary>The project file id of a <c>file:&lt;guid&gt;</c> media reference; null for a step-result reference.</summary>
    private static Guid? FileIdOf(string mediaRef) =>
        mediaRef.StartsWith("file:", StringComparison.Ordinal) && Guid.TryParse(mediaRef.AsSpan(5), out Guid id) ? id : null;

    private static TimelineRenderStepConfig? ParseConfig(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<TimelineRenderStepConfig>(json, ConfigJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
