using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Compute;
using ReelBolt.WorkflowEngine.Services.Video;

namespace ReelBolt.WorkflowEngine.Services.Review;

/// <summary>Measures the duration of the film under review. Null = could not be measured (fail open).</summary>
public interface IReviewFilmDurationProbe
{
    Task<double?> ProbeDurationSecAsync(StepExecutionContext context, VisualCheckSource film, CancellationToken ct);
}

/// <summary>One iteration's length check: requested vs measured seconds.</summary>
public sealed record LengthCheckOutcome(int RequestedSec, double? ActualSec, string? Reason)
{
    public const double Tolerance = 0.15;
    public const int CapScore = 6;

    public bool IsMeasured => ActualSec is > 0;

    public double DeviationPct => IsMeasured ? Math.Abs(ActualSec!.Value - RequestedSec) / RequestedSec * 100.0 : 0;

    public bool WithinTolerance => !IsMeasured || DeviationPct <= Tolerance * 100.0;

    public bool Capped => IsMeasured && !WithinTolerance;
}

/// <summary>Probes the film with the engine's own ffprobe through the pinned-local media workspace.</summary>
public sealed class FfmpegReviewFilmDurationProbe : IReviewFilmDurationProbe
{
    private readonly IMediaWorkspaceFactory _workspaces;
    private readonly IMediaProbe _probe;
    private readonly ILogger<FfmpegReviewFilmDurationProbe> _logger;

    public FfmpegReviewFilmDurationProbe(IMediaWorkspaceFactory workspaces, IMediaProbe probe, ILogger<FfmpegReviewFilmDurationProbe> logger)
    {
        _workspaces = workspaces;
        _probe = probe;
        _logger = logger;
    }

    public static IReviewFilmDurationProbe? TryCreate(IServiceProvider services)
    {
        if (services.GetService<IReviewFilmDurationProbe>() is { } registered)
            return registered;
        IMediaWorkspaceFactory? workspaces = services.GetService<IMediaWorkspaceFactory>();
        IMediaProbe? probe = services.GetService<IMediaProbe>();
        if (workspaces is null || probe is null)
            return null;
        ILoggerFactory loggers = services.GetService<ILoggerFactory>() ?? Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance;
        return new FfmpegReviewFilmDurationProbe(workspaces, probe, loggers.CreateLogger<FfmpegReviewFilmDurationProbe>());
    }

    public async Task<double?> ProbeDurationSecAsync(StepExecutionContext context, VisualCheckSource film, CancellationToken ct)
    {
        using CancellationTokenSource bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(TimeSpan.FromSeconds(90));
        try
        {
            MediaWorkspaceOpenResult opened = await _workspaces.OpenAsync(
                new MediaWorkspaceRequest(
                    ExecutionId: context.Execution.Id,
                    OrganizationId: context.Execution.OrganizationId,
                    ProjectId: context.Execution.ProjectId,
                    StepId: context.Step.Id,
                    StepOrder: context.Step.StepOrder,
                    JobType: RunnerJobType.VideoCompile,
                    Purpose: $"review.length-check:{context.Step.StepOrder.ToString(CultureInfo.InvariantCulture)}",
                    Iteration: context.IterationCount,
                    RunnerEligible: false),
                bounded.Token).ConfigureAwait(false);
            if (opened.Refused || opened.Lease is null)
                return null;

            await using MediaWorkspaceLease lease = opened.Lease;
            string path = "length-check/source" + Path.GetExtension(film.StorageKey).ToLowerInvariant();
            await lease.Workspace.StageAsync(film.StorageKey, path, bounded.Token).ConfigureAwait(false);
            MediaProbeResult probe = await _probe.ProbeAsync(lease.Workspace, path, bounded.Token).ConfigureAwait(false);
            return probe.DurationSec > 0 && double.IsFinite(probe.DurationSec) ? probe.DurationSec : null;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Length check: the film could not be measured; continuing without the length cap");
            return null;
        }
    }
}

/// <summary>
/// The <c>lengthCheck</c> node: recorded in the iteration output like <c>visualCheck</c>, read back for the
/// loop's score cap and for the plain sentence the next pass is given. Read-back is untrusted: a forged node
/// can only lower the score.
/// </summary>
public static class LengthCheckReport
{
    public const string NodeName = "lengthCheck";
    public const string StatusChecked = "checked";
    public const string StatusUnavailable = "unavailable";
    public const string CapReason = "the film length is more than 15% off the request";

    private static readonly JsonSerializerOptions NodeJson = new(JsonSerializerDefaults.Web);

    public static async Task<LengthCheckOutcome?> RunAsync(
        StepExecutionContext context, int? requestedSec, IReviewFilmDurationProbe? probe, CancellationToken ct)
    {
        if (requestedSec is not >= 1)
            return null;
        VisualCheckSource? film = ReviewFilmResolver.Resolve(context);

        // Encode-once: a plan-only compile wrote no file, but its outputDurationSec is the length the one encode will
        // have (the same number a real compile reports), so the gate measures that instead of going quiet.
        if (film is null && PlannedFilmDurationSec(context) is { } planned)
            return new LengthCheckOutcome(requestedSec.Value, planned, null);

        if (film is null || probe is null)
            return new LengthCheckOutcome(requestedSec.Value, null, film is null ? "no playable film to measure" : "the film could not be measured in this host");
        double? actual = await probe.ProbeDurationSecAsync(context, film, ct).ConfigureAwait(false);
        return new LengthCheckOutcome(requestedSec.Value, actual, actual is null ? "the film could not be measured" : null);
    }

    /// <summary>The planned length of the newest plan-only compile in the review's window (<c>planOnly</c> output), or null.</summary>
    internal static double? PlannedFilmDurationSec(StepExecutionContext context)
    {
        int windowStart = context.Step.LoopTargetStepOrder ?? int.MinValue;
        StepOutputHistoryEntry? best = null;
        for (int i = context.StepOutputHistory.Count - 1; i >= 0; i--)
        {
            StepOutputHistoryEntry entry = context.StepOutputHistory[i];
            if (entry.StepOrder < windowStart || entry.StepOrder >= context.Step.StepOrder || string.IsNullOrWhiteSpace(entry.Output))
                continue;
            if (best is null || entry.StepOrder > best.StepOrder)
            {
                if (ReadPlanned(entry.Output) is not null)
                    best = entry;
            }
        }

        return best is null ? null : ReadPlanned(best.Output);
    }

    private static double? ReadPlanned(string? output)
    {
        try
        {
            if (JsonNode.Parse(output!) is JsonObject node
                && node["planOnly"]?.GetValue<bool>() == true
                && node["outputDurationSec"]?.GetValue<double>() is { } sec
                && sec > 0 && double.IsFinite(sec))
                return sec;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
        }

        return null;
    }

    public static JsonObject ToNode(LengthCheckOutcome o) => o.IsMeasured
        ? new JsonObject
        {
            ["status"] = StatusChecked,
            ["requestedSec"] = o.RequestedSec,
            ["actualSec"] = Math.Round(o.ActualSec!.Value, 1),
            ["deviationPct"] = Math.Round(o.DeviationPct, 1),
            ["withinTolerance"] = o.WithinTolerance,
            ["capScore"] = LengthCheckOutcome.CapScore,
        }
        : new JsonObject
        {
            ["status"] = StatusUnavailable,
            ["requestedSec"] = o.RequestedSec,
            ["reason"] = o.Reason,
            ["withinTolerance"] = true,
        };

    public static string AttachToOutput(string reviewOutput, LengthCheckOutcome outcome, AgentType? agentType)
    {
        JsonObject node = ToNode(outcome);
        string output = reviewOutput ?? string.Empty;
        try
        {
            string? verdictJson = ReviewLoopStepExecutor.ScanReviewVerdicts(output, agentType).Verdict?.Json;
            if (verdictJson is not null)
            {
                int at = output.IndexOf(verdictJson, StringComparison.Ordinal);
                if (at >= 0 && WithNode(verdictJson, node) is { } augmented)
                    return string.Concat(output.AsSpan(0, at), augmented, output.AsSpan(at + verdictJson.Length));
            }

            if (WithNode(output.Trim(), node) is { } whole)
                return whole;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
        {
        }

        JsonObject wrapper = new() { [NodeName] = node };
        return output + (output.Length > 0 ? "\n" : string.Empty) + wrapper.ToJsonString(NodeJson);
    }

    /// <summary><c>min(score, 6)</c> when the recorded node is a measured miss; otherwise the score.</summary>
    public static int ApplyRecordedCap(int score, string reviewOutput, AgentType? agentType)
        => ReadMiss(reviewOutput, agentType) is null ? score : Math.Min(score, LengthCheckOutcome.CapScore);

    /// <summary>The brief with the plain length sentence appended when the recorded node is a measured miss.</summary>
    public static string? WithBriefBlock(string? brief, string reviewOutput, AgentType? agentType)
    {
        string? sentence = ReadMiss(reviewOutput, agentType);
        if (sentence is null)
            return brief;
        return brief is null ? sentence : brief + "\n" + sentence;
    }

    public static string Sentence(double actualSec, int requestedSec) =>
        string.Create(CultureInfo.InvariantCulture,
            $"The film is {Math.Round(actualSec, 1):0.#} s; the request was {requestedSec} s. Make it {requestedSec} s (within 15 %).");

    private static string? ReadMiss(string reviewOutput, AgentType? agentType)
    {
        if (string.IsNullOrEmpty(reviewOutput) || !reviewOutput.Contains("\"" + NodeName + "\"", StringComparison.Ordinal))
            return null;
        try
        {
            string? verdictJson = ReviewLoopStepExecutor.ScanReviewVerdicts(reviewOutput, agentType).Verdict?.Json;
            JsonObject? node = (verdictJson is not null ? NodeOf(verdictJson) : null)
                ?? NodeOf(RobustJsonExtractor.ExtractJsonObject(reviewOutput) ?? reviewOutput);
            if (node is null)
            {
                int appended = reviewOutput.LastIndexOf("{\"" + NodeName + "\":", StringComparison.Ordinal);
                node = appended >= 0 ? NodeOf(reviewOutput[appended..]) : null;
            }

            if (node is null
                || node["status"]?.GetValue<string>() != StatusChecked
                || node["requestedSec"] is not JsonValue rv || !rv.TryGetValue(out int requested) || requested < 1
                || node["actualSec"] is not JsonValue av || !av.TryGetValue(out double actual) || !double.IsFinite(actual) || actual <= 0)
                return null;

            LengthCheckOutcome outcome = new(requested, actual, null);
            return outcome.Capped ? Sentence(actual, requested) : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException or FormatException)
        {
            return null;
        }
    }

    private static JsonObject? NodeOf(string json)
    {
        try
        {
            return JsonNode.Parse(json) is JsonObject root && root[NodeName] is JsonObject node ? node : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? WithNode(string json, JsonObject node)
    {
        if (JsonNode.Parse(json) is not JsonObject root)
            return null;
        root[NodeName] = node.DeepClone();
        return root.ToJsonString(NodeJson);
    }
}
