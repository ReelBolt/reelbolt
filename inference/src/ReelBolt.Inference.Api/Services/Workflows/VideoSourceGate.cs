using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Editing;
using ReelBolt.Inference.Api.Services.PublicApi;
using ReelBolt.Shared.Briefs;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.Inference.Api.Services.Workflows;

/// <summary>A source video the run cannot edit: the code, the plain sentence and the facts the run page shows.</summary>
public sealed record VideoSourceRefusal(
    string Code, string Message, string FileName, long? LimitGb, int? LimitMinutes, long? FileGb = null, int? FileMinutes = null)
{
    /// <summary>The 422 every run start answers with.</summary>
    public ObjectResult ToResult() => new(new
    {
        code = Code,
        message = Message,
        fileName = FileName,
        limitGb = LimitGb,
        limitMinutes = LimitMinutes,
        fileGb = FileGb,
        fileMinutes = FileMinutes
    })
    { StatusCode = StatusCodes.Status422UnprocessableEntity };
}

/// <summary>
/// The run-start check on the footage itself: every <c>VideoAnalyze</c> source that already names a project file
/// (a pinned file, or the files the run's brief chose for a slot) is held against <see cref="VideoSourceLimits"/>
/// before anything is spent. Size comes from the file row and length from the probe stored at upload; nothing is
/// probed here, so a file with no stored probe is judged on size only. A source that is another step's output is
/// unknowable now and is left to the engine, which applies the same limits.
/// </summary>
public sealed class VideoSourceGate(InferenceApiDbContext db, VideoSourceLimits limits, int maxTooDemandingPerDay = VideoSourceGate.DefaultMaxTooDemandingPerDay, ILogger<VideoSourceGate>? logger = null)
{
    public const int DefaultMaxTooDemandingPerDay = 3;
    public const string TooDemandingLimitCode = "TOO_DEMANDING_LIMIT";
    public const string TooDemandingLimitMessage = "This video has been too demanding for us to analyse several times today. Try a shorter clip or a smaller export, for example 1080p, or try again tomorrow. Nothing was spent.";
    private const long HeavyBytesWithoutProbe = 1024L * 1024 * 1024;
    private const double HeavyMinDurationSec = 120;
    private const int HeavyMinShortSide = 1080;
    private const int RetryAfterSeconds = 3600;

    private static readonly JsonSerializerOptions ConfigOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    /// <param name="steps">The steps that will run; null = the workflow as saved.</param>
    /// <param name="chargedStepOrders">When resuming, only these steps run.</param>
    public async Task<VideoSourceRefusal?> CheckAsync(
        Project project,
        Guid workflowId,
        IReadOnlyList<WorkflowStep>? steps,
        RunBrief? brief,
        IReadOnlyCollection<int>? chargedStepOrders,
        CancellationToken ct) =>
        (await FindAsync(project, workflowId, steps, brief, chargedStepOrders, firstOnly: true, ct)).FirstOrDefault();

    /// <summary>Every source the run cannot edit (the run page lists them before Run); empty when all fit.</summary>
    public Task<IReadOnlyList<VideoSourceRefusal>> FindAllAsync(
        Project project, Guid workflowId, IReadOnlyList<WorkflowStep>? steps, RunBrief? brief, CancellationToken ct) =>
        FindAsync(project, workflowId, steps, brief, null, firstOnly: false, ct);

    private async Task<IReadOnlyList<VideoSourceRefusal>> FindAsync(
        Project project,
        Guid workflowId,
        IReadOnlyList<WorkflowStep>? steps,
        RunBrief? brief,
        IReadOnlyCollection<int>? chargedStepOrders,
        bool firstOnly,
        CancellationToken ct)
    {
        List<(Guid FileId, long StepMaxBytes, int StepMaxSeconds)> wanted = await WantedAsync(project, workflowId, steps, brief, chargedStepOrders, ct);
        if (wanted.Count == 0)
            return [];
        return await JudgeAsync(project, wanted, firstOnly, ct);
    }

    /// <summary>
    /// The per-organization limit on repeat refunded SOURCE_TOO_DEMANDING runs (<c>VideoSources:MaxTooDemandingFailuresPerDay</c>;
    /// 0 or negative disables). Refuses only a run that has a known-heavy VideoAnalyze source, so other runs are never affected.
    /// </summary>
    public async Task<ObjectResult?> CheckTooDemandingLimitAsync(
        Project project,
        Guid workflowId,
        IReadOnlyList<WorkflowStep>? steps,
        RunBrief? brief,
        IReadOnlyCollection<int>? chargedStepOrders,
        CancellationToken ct)
    {
        if (maxTooDemandingPerDay <= 0)
            return null;
        try
        {
            DateTime since = DateTime.UtcNow.AddHours(-24);
            // By the organization through the project (never a caller-supplied id); superseded attempts count too.
            int failures = await db.WorkflowStepResults.AsNoTracking()
                .Where(r => r.FailureCode == VideoSourceLimits.TooDemandingCode
                    && r.ExecutedAt >= since
                    && r.WorkflowExecution.Project.OrganizationId == project.OrganizationId)
                .Take(maxTooDemandingPerDay)
                .CountAsync(ct);
            if (failures < maxTooDemandingPerDay)
                return null;

            var wanted = await WantedAsync(project, workflowId, steps, brief, chargedStepOrders, ct);
            if (wanted.Count == 0 || !await AnyHeavyAsync(project, wanted, ct))
                return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Fail open: a read failure must not block a paying user's run (the engine still fails the step closed and refunds).
            logger?.LogWarning(ex, "Too-demanding limit could not be read; allowing the run.");
            return null;
        }

        var result = new RetryAfterResult(new { code = TooDemandingLimitCode, message = TooDemandingLimitMessage }, RetryAfterSeconds)
        { StatusCode = StatusCodes.Status429TooManyRequests };
        return result;
    }

    private sealed class RetryAfterResult : ObjectResult, IRetryAfterResult
    {
        private readonly int _seconds;

        public RetryAfterResult(object value, int seconds) : base(value) => _seconds = seconds;

        public int RetryAfterSeconds => _seconds;

        public override Task ExecuteResultAsync(ActionContext context)
        {
            context.HttpContext.Response.Headers.RetryAfter = _seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return base.ExecuteResultAsync(context);
        }
    }

    private async Task<bool> AnyHeavyAsync(Project project, List<(Guid FileId, long StepMaxBytes, int StepMaxSeconds)> wanted, CancellationToken ct)
    {
        List<Guid> ids = [.. wanted.Select(w => w.FileId).Distinct()];
        var files = await db.ProjectFiles.AsNoTracking()
            .Where(f => f.ProjectId == project.Id && ids.Contains(f.Id))
            .Select(f => new { f.StorageKey, f.SizeBytes })
            .ToListAsync(ct);
        Dictionary<string, MediaSourceProbe> probes = await StoredMediaProbes.LoadAsync(db, project.Id, [.. files.Select(f => f.StorageKey)], ct);
        return files.Any(f => probes.TryGetValue(f.StorageKey, out MediaSourceProbe? p)
            ? p.DurationSec >= HeavyMinDurationSec && Math.Min(p.Width, p.Height) > HeavyMinShortSide
            : f.SizeBytes >= HeavyBytesWithoutProbe);
    }

    private async Task<List<(Guid FileId, long StepMaxBytes, int StepMaxSeconds)>> WantedAsync(
        Project project,
        Guid workflowId,
        IReadOnlyList<WorkflowStep>? steps,
        RunBrief? brief,
        IReadOnlyCollection<int>? chargedStepOrders,
        CancellationToken ct)
    {
        IReadOnlyList<WorkflowStep> toCheck = steps
            ?? await db.WorkflowSteps.AsNoTracking()
                .Where(s => s.WorkflowDefinitionId == workflowId && s.WorkflowDefinition.ProjectId == project.Id)
                .ToListAsync(ct);

        List<(Guid FileId, long StepMaxBytes, int StepMaxSeconds)> wanted = [];
        foreach (WorkflowStep step in toCheck.Where(s => s.StepType == StepType.VideoAnalyze).OrderBy(s => s.StepOrder))
        {
            if (chargedStepOrders is not null && !chargedStepOrders.Contains(step.StepOrder))
                continue;
            if (string.IsNullOrWhiteSpace(step.VideoAnalyzeConfigJson))
                continue;

            VideoAnalyzeStepConfig? config;
            try
            {
                config = JsonSerializer.Deserialize<VideoAnalyzeStepConfig>(step.VideoAnalyzeConfigJson, ConfigOptions);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException or InvalidOperationException)
            {
                continue;
            }

            if (config is null)
                continue;
            IEnumerable<VideoSourceRef> sources = config.Sources is { Count: > 0 } many ? many : [config.Source];
            foreach (VideoSourceRef source in sources)
            {
                if (source is null)
                    continue;
                if (source.Kind == VideoSourceKind.ProjectFile && source.ProjectFileId is { } pinned)
                    wanted.Add((pinned, config.MaxInputBytes, config.MaxDurationSeconds));
                else if (source.Kind == VideoSourceKind.ProjectFile && source.ProjectFileId is null)
                {
                    // An open slot (the default template's source) is filled with the brief's video files at run time.
                    foreach (Guid id in brief?.SourceFiles?.Where(f => f.Role == RunBriefFileRole.Video).SelectMany(f => f.FileIds ?? []) ?? [])
                        wanted.Add((id, config.MaxInputBytes, config.MaxDurationSeconds));
                }
                else if (source.Kind == VideoSourceKind.BriefFiles && source.Role is { } role)
                {
                    foreach (Guid id in brief?.SourceFiles?.Where(f => f.Role == role).SelectMany(f => f.FileIds ?? []) ?? [])
                        wanted.Add((id, config.MaxInputBytes, config.MaxDurationSeconds));
                }
            }
        }

        return wanted;
    }

    private async Task<IReadOnlyList<VideoSourceRefusal>> JudgeAsync(
        Project project, List<(Guid FileId, long StepMaxBytes, int StepMaxSeconds)> wanted, bool firstOnly, CancellationToken ct)
    {
        List<Guid> ids = [.. wanted.Select(w => w.FileId).Distinct()];
        var files = await db.ProjectFiles.AsNoTracking()
            .Where(f => f.ProjectId == project.Id && ids.Contains(f.Id))
            .Select(f => new { f.Id, f.OriginalFileName, f.StorageKey, f.SizeBytes })
            .ToListAsync(ct);
        Dictionary<Guid, (string Name, string Key, long Size)> byId = files.ToDictionary(f => f.Id, f => (f.OriginalFileName, f.StorageKey, f.SizeBytes));
        Dictionary<string, MediaSourceProbe> probes = await StoredMediaProbes.LoadAsync(db, project.Id, [.. files.Select(f => f.StorageKey)], ct);

        List<VideoSourceRefusal> found = [];
        HashSet<Guid> reported = [];
        foreach ((Guid fileId, long stepBytes, int stepSeconds) in wanted)
        {
            if (reported.Contains(fileId))
                continue;
            if (!byId.TryGetValue(fileId, out var file))
                continue;
            string name = string.IsNullOrWhiteSpace(file.Name) ? "This video" : file.Name;

            long maxBytes = limits.EffectiveBytes(stepBytes);
            if (file.Size > maxBytes)
            {
                string gb = VideoSourceLimits.FormatGigabytes(maxBytes);
                found.Add(new VideoSourceRefusal(
                    VideoSourceLimits.TooLargeCode,
                    $"\"{name}\" is {VideoSourceLimits.FormatGigabytes(file.Size)} GB, and the largest video we can edit is {gb} GB. Trim it or export a smaller copy, then run again. Nothing was spent.",
                    name,
                    long.Parse(gb, System.Globalization.CultureInfo.InvariantCulture),
                    null,
                    FileGb: long.Parse(VideoSourceLimits.FormatGigabytes(file.Size), System.Globalization.CultureInfo.InvariantCulture)));
                reported.Add(fileId);
                if (firstOnly)
                    return found;
                continue;
            }

            int maxSeconds = limits.EffectiveSeconds(stepSeconds);
            if (probes.TryGetValue(file.Key, out MediaSourceProbe? probe) && probe.DurationSec > maxSeconds)
            {
                string minutes = VideoSourceLimits.FormatMinutes(maxSeconds);
                found.Add(new VideoSourceRefusal(
                    VideoSourceLimits.TooLongCode,
                    $"\"{name}\" runs {VideoSourceLimits.FormatMinutes((int)Math.Ceiling(probe.DurationSec))} minutes, and the longest video we can edit is {minutes} minutes. Trim it or export a shorter copy, then run again. Nothing was spent.",
                    name,
                    null,
                    int.Parse(minutes, System.Globalization.CultureInfo.InvariantCulture),
                    FileMinutes: int.Parse(VideoSourceLimits.FormatMinutes((int)Math.Ceiling(probe.DurationSec)), System.Globalization.CultureInfo.InvariantCulture)));
                reported.Add(fileId);
                if (firstOnly)
                    return found;
            }
        }

        return found;
    }
}
