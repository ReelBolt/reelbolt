using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.VideoEvents;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Services.Storage;

namespace ReelBolt.WorkflowEngine.Agents.Tools;

/// <summary>
/// The <see cref="ReelBolt.Shared.Agents.ToolGroup.EventsRead"/> tool group: <c>ListVideoEvents</c> and
/// <c>DescribeVideoWindow</c>, the read tools of the progressive context (docs/video-editing.md "Outline and windows").
/// They read the newest <c>VideoAnalyze</c> artifact of THIS execution only: no argument names a project, an execution or
/// a file, so there is nothing a prompt injection could aim at another tenant. Free and read-only; at most
/// <see cref="VideoEventReadAllowance.MaxCallsPerTurn"/> calls per step, and at most 2,000 tokens a call. Answers are ids
/// and words (speech lines are fenced as untrusted); never a time.
/// </summary>
public partial class VideoEventsAgentTools
{
    /// <summary>The largest analysis artifact these tools will bring into memory.</summary>
    internal const long MaxArtifactBytes = 64L * 1024 * 1024;

    private const int MaxTrackedSteps = 256;

    private readonly IWorkflowExecutionContextAccessor _executionContextAccessor;
    private readonly IServiceScopeFactory _scopes;
    private readonly IProjectFileWorkspace _workspace;
    private readonly ILogger<VideoEventsAgentTools> _logger;
    private readonly StepKeyedState<VideoEventReadAllowance> _allowances = new(MaxTrackedSteps);

    public VideoEventsAgentTools(
        IWorkflowExecutionContextAccessor executionContextAccessor,
        IServiceScopeFactory scopes,
        IProjectFileWorkspace workspace,
        ILogger<VideoEventsAgentTools>? logger = null)
    {
        _executionContextAccessor = executionContextAccessor;
        _scopes = scopes;
        _workspace = workspace;
        _logger = logger ?? NullLogger<VideoEventsAgentTools>.Instance;
    }

    [Description(
        "List the moments (events) found in the analysed video, strongest first: each is {id, type, strength word, region word, " +
        "nearest speech line t{n} and shot s{n}} with no time. Narrow it with section (an outline section id like c2), types " +
        "(comma-separated event types such as hit,appear) and limit (default 25, at most 100). Free and read-only; at most 6 " +
        "event reads per turn. Returns about 2,000 tokens at most.")]
    public Task<string> ListVideoEvents(
        [Description("Optional outline section id, e.g. c2.")] string? section = null,
        [Description("Optional comma-separated event types, e.g. hit,appear.")] string? types = null,
        [Description("How many events to return (default 25, at most 100).")] int? limit = null,
        [Description("Optional analysed source number (0 is the first source).")] int? source = null) =>
        ReadAsync(artifact => VideoEventReader.List(
            artifact, source, section,
            string.IsNullOrWhiteSpace(types) ? null : [.. types.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries)],
            limit));

    [Description(
        "Describe what happens between two events (ids like e12 and e31): every event in between including weak ones, a coarse " +
        "motion curve in words, and the speech lines (t{n}) spoken in the window. No times. Free and read-only; at most 6 event " +
        "reads per turn. Returns about 2,000 tokens at most.")]
    public Task<string> DescribeVideoWindow(
        [Description("The first event id, e.g. e12.")] string fromEventId,
        [Description("The last event id, e.g. e31.")] string toEventId) =>
        ReadAsync(artifact => VideoEventReader.Describe(artifact, fromEventId, toEventId));

    private async Task<string> ReadAsync(Func<VideoAnalysisArtifact, string> read, bool requireEvents = true, bool countCall = true)
    {
        WorkflowExecutionContext context = _executionContextAccessor.Current
            ?? throw new InvalidOperationException("Video event tools can only be used inside a running workflow.");

        VideoEventReadAllowance allowance = _allowances.GetOrAdd(context.StepResultId ?? context.ExecutionId, () => new VideoEventReadAllowance());
        if (countCall && !allowance.TryTake())
            return VideoEventReadAllowance.LimitJson;

        VideoAnalysisArtifact? artifact = await LoadAsync(context, requireEvents);
        return artifact is null
            ? new JsonObject { ["available"] = false, ["reason"] = requireEvents ? "This run has no video analysis with events." : "This run has no video analysis." }.ToJsonString()
            : read(artifact);
    }

    /// <summary>The newest VideoAnalyze artifact of the running execution, or null when it has none or it cannot be read.</summary>
    private async Task<VideoAnalysisArtifact?> LoadAsync(WorkflowExecutionContext context, bool requireEvents = true) =>
        (await LoadAnalysisAsync(context, requireEvents))?.Artifact;

    /// <summary>
    /// The analysis every tool of this class reads, with what a look at one of its moments needs beside it: where the
    /// artifact is stored, the analysis step's own config, and the stamps the quarantine gate judges it by.
    /// </summary>
    internal sealed record RunAnalysis(
        VideoAnalysisArtifact Artifact, string ArtifactStorageKey, string? AnalyzeConfigJson,
        DateTime ExecutedAt, DateTime? RunFirstStartedAt, DateTime? SupersededAt);

    /// <summary>The newest VideoAnalyze result of the running execution, or null when it has none or it cannot be read.</summary>
    internal async Task<RunAnalysis?> LoadAnalysisAsync(WorkflowExecutionContext context, bool requireEvents = true)
    {
        string prefix = $"projects/{context.ProjectId}/";
        string tempPath = Path.Combine(Path.GetTempPath(), $"video-events-{Guid.NewGuid():N}.json");
        try
        {
            string? key;
            string? configJson;
            DateTime executedAt;
            DateTime? runFirstStartedAt;
            DateTime? supersededAt;
            using (IServiceScope scope = _scopes.CreateScope())
            {
                WorkflowEngineDbContext db = scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();
                var row = await db.WorkflowStepResults
                    .AsNoTracking()
                    .Where(r => r.WorkflowExecutionId == context.ExecutionId &&
                                r.WorkflowStep.StepType == StepType.VideoAnalyze &&
                                r.SupersededAt == null &&
                                r.ArtifactStorageKey != null &&
                                r.ArtifactStorageKey.StartsWith(prefix))
                    .OrderByDescending(r => r.ExecutedAt)
                    .Select(r => new
                    {
                        r.ArtifactStorageKey,
                        r.WorkflowStep.VideoAnalyzeConfigJson,
                        r.ExecutedAt,
                        r.WorkflowExecution.FirstStartedAt,
                        r.SupersededAt
                    })
                    .FirstOrDefaultAsync(CancellationToken.None);
                key = row?.ArtifactStorageKey;
                configJson = row?.VideoAnalyzeConfigJson;
                executedAt = row?.ExecutedAt ?? default;
                runFirstStartedAt = row?.FirstStartedAt;
                supersededAt = row?.SupersededAt;
            }

            if (string.IsNullOrEmpty(key))
                return null;

            await _workspace.DownloadStorageKeyToFileAsync(context.ProjectId, key, tempPath, MaxArtifactBytes, CancellationToken.None);
            VideoAnalysisArtifact? artifact = VideoEventReader.Parse(await File.ReadAllTextAsync(tempPath, CancellationToken.None));
            return artifact is null || (requireEvents && artifact.Events is not { Count: > 0 })
                ? null
                : new RunAnalysis(artifact, key, configJson, executedAt, runFirstStartedAt, supersededAt);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Video event tools could not read the analysis of execution {ExecutionId}", context.ExecutionId);
            return null;
        }
        finally
        {
            try { File.Delete(tempPath); } catch (IOException) { }
        }
    }
}
