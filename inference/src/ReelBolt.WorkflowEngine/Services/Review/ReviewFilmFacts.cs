using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Services.Compute;
using ReelBolt.WorkflowEngine.Services.Video;

namespace ReelBolt.WorkflowEngine.Services.Review;

/// <summary>A part of the frame, in 0..1 fractions of its width and height.</summary>
public sealed record ReviewLookRegion(double X, double Y, double W, double H)
{
    /// <summary>The smallest region side kept (a smaller crop shows nothing readable).</summary>
    public const double MinSide = 0.05;

    /// <summary>The region clamped inside the frame, or null when it is not a usable region.</summary>
    public static ReviewLookRegion? Clamp(double x, double y, double w, double h)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(w) || !double.IsFinite(h) || w <= 0 || h <= 0)
            return null;
        x = Math.Clamp(x, 0, 1 - MinSide);
        y = Math.Clamp(y, 0, 1 - MinSide);
        w = Math.Clamp(w, MinSide, 1 - x);
        h = Math.Clamp(h, MinSide, 1 - y);
        return w >= 0.999 && h >= 0.999 ? null : new ReviewLookRegion(Math.Round(x, 3), Math.Round(y, 3), Math.Round(w, 3), Math.Round(h, 3));
    }
}

/// <summary>One frame a reviewer asked to see: a time of the finished film and, optionally, a region of it.</summary>
public sealed record ReviewLookFrame(double AtSec, ReviewLookRegion? Region);

/// <summary>
/// First-party facts about the film under review that need the media workspace: the compile's timeline manifest,
/// the sound measurement and the frames a reviewer asks for. Every method fails soft (null or an empty grab with a
/// fixed reason), never fails the review, and builds every argv from engine-owned values.
/// </summary>
public interface IReviewFilmFacts
{
    /// <summary>The compile's timeline manifest at <paramref name="storageKey"/> (this project's own object only), or null.</summary>
    Task<TimelineManifest?> ReadTimelineAsync(StepExecutionContext context, string storageKey, CancellationToken ct);

    /// <summary>The film's ebur128 measurement, or null when it has no sound or could not be measured.</summary>
    Task<ReviewAudioMeasurement?> MeasureAudioAsync(StepExecutionContext context, VisualCheckSource film, CancellationToken ct);

    /// <summary>The asked-for frames, each optionally cropped to its region and enlarged; numbered 1..n in the order given.</summary>
    Task<VisualCheckFrameGrab> GrabAtAsync(StepExecutionContext context, VisualCheckSource film, IReadOnlyList<ReviewLookFrame> frames, CancellationToken ct);
}

/// <summary>The <see cref="IReviewFilmFacts"/> built on the engine's ffmpeg through the pinned-local media workspace.</summary>
public sealed class FfmpegReviewFilmFacts : IReviewFilmFacts
{
    /// <summary>The largest timeline manifest read.</summary>
    public const int MaxTimelineBytes = 2 * 1024 * 1024;

    /// <summary>The width of a whole-frame look (larger than the even samples).</summary>
    public const int LookWidth = 1024;

    /// <summary>The width a cropped region is scaled to, so small text becomes legible.</summary>
    public const int RegionLookWidth = 1280;

    private static readonly TimeSpan ToolTimeout = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions ManifestJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private readonly IMediaWorkspaceFactory _workspaces;
    private readonly IMediaProbe _probe;
    private readonly ILogger<FfmpegReviewFilmFacts> _logger;

    public FfmpegReviewFilmFacts(IMediaWorkspaceFactory workspaces, IMediaProbe probe, ILogger<FfmpegReviewFilmFacts> logger)
    {
        _workspaces = workspaces;
        _probe = probe;
        _logger = logger;
    }

    /// <summary>Composed from services every engine host registers; an explicitly registered one wins; null when missing.</summary>
    public static IReviewFilmFacts? TryCreate(IServiceProvider services)
    {
        if (services.GetService<IReviewFilmFacts>() is { } registered)
            return registered;
        IMediaWorkspaceFactory? workspaces = services.GetService<IMediaWorkspaceFactory>();
        IMediaProbe? probe = services.GetService<IMediaProbe>();
        if (workspaces is null || probe is null)
            return null;
        ILoggerFactory loggers = services.GetService<ILoggerFactory>() ?? Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance;
        return new FfmpegReviewFilmFacts(workspaces, probe, loggers.CreateLogger<FfmpegReviewFilmFacts>());
    }

    public async Task<TimelineManifest?> ReadTimelineAsync(StepExecutionContext context, string storageKey, CancellationToken ct)
    {
        if (!IsOwnJson(storageKey, context.Execution.ProjectId))
            return null;
        try
        {
            await using MediaWorkspaceLease? lease = await OpenAsync(context, "timeline", ct).ConfigureAwait(false);
            if (lease is null)
                return null;
            const string path = "review-facts/timeline.json";
            await lease.Workspace.StageAsync(storageKey, path, MaxTimelineBytes, ct).ConfigureAwait(false);
            byte[] bytes = await lease.Workspace.ReadAsync(path, MaxTimelineBytes, ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<TimelineManifest>(bytes, ManifestJson);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Review step {StepOrder}: the compile's timeline could not be read; the facts use the compile output only", context.Step.StepOrder);
            return null;
        }
    }

    public async Task<ReviewAudioMeasurement?> MeasureAudioAsync(StepExecutionContext context, VisualCheckSource film, CancellationToken ct)
    {
        try
        {
            await using MediaWorkspaceLease? lease = await OpenAsync(context, "audio", ct).ConfigureAwait(false);
            if (lease is null)
                return null;
            string path = "review-facts/film" + Path.GetExtension(film.StorageKey).ToLowerInvariant();
            await lease.Workspace.StageAsync(film.StorageKey, path, ct).ConfigureAwait(false);
            string local = await lease.Workspace.MaterializeAsync(path, ct).ConfigureAwait(false);
            VideoToolResult result = await lease.Workspace.RunFfmpegAsync(ReviewAudioFacts.MeasureArgs(local), ToolTimeout, ct).ConfigureAwait(false);
            return result.Succeeded ? ReviewAudioFacts.Parse(result.StdErr) : null;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Review step {StepOrder}: the film's sound could not be measured", context.Step.StepOrder);
            return null;
        }
    }

    public async Task<VisualCheckFrameGrab> GrabAtAsync(
        StepExecutionContext context, VisualCheckSource film, IReadOnlyList<ReviewLookFrame> frames, CancellationToken ct)
    {
        await using MediaWorkspaceLease? lease = await OpenAsync(context, "look", ct).ConfigureAwait(false);
        if (lease is null)
            return new VisualCheckFrameGrab([], "the media workspace could not be opened");

        IMediaWorkspace workspace = lease.Workspace;
        string sourcePath = "review-look/film" + Path.GetExtension(film.StorageKey).ToLowerInvariant();
        await workspace.StageAsync(film.StorageKey, sourcePath, ct).ConfigureAwait(false);
        MediaProbeResult probe = await _probe.ProbeAsync(workspace, sourcePath, ct).ConfigureAwait(false);
        if (!double.IsFinite(probe.DurationSec) || probe.DurationSec <= 0)
            return new VisualCheckFrameGrab([], "the output's duration could not be read");

        string local = await workspace.MaterializeAsync(sourcePath, ct).ConfigureAwait(false);
        List<VisualCheckFrameImage> images = [];
        for (int i = 0; i < frames.Count; i++)
        {
            double at = Math.Round(Math.Clamp(frames[i].AtSec, 0, Math.Max(0, probe.DurationSec - 0.05)), 2);
            string framePath = $"review-look/frame-{(i + 1).ToString("D2", CultureInfo.InvariantCulture)}.jpg";
            try
            {
                string output = await workspace.MaterializeAsync(framePath, ct).ConfigureAwait(false);
                VideoToolResult result = await workspace.RunFfmpegAsync(LookArgs(local, output, at, frames[i].Region), ToolTimeout, ct).ConfigureAwait(false);
                if (!result.Succeeded)
                    continue;
                byte[] jpeg = await workspace.ReadAsync(framePath, VisualFrameReviewer.MaxJpegBytes, ct).ConfigureAwait(false);
                if (jpeg.Length > 0)
                    images.Add(new VisualCheckFrameImage(i + 1, at, jpeg));
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Review step {StepOrder}: frame at {AtSec}s could not be extracted; skipping it", context.Step.StepOrder, at);
            }
        }

        return images.Count == 0
            ? new VisualCheckFrameGrab([], "no frame could be extracted from the output under review")
            : new VisualCheckFrameGrab(images, null);
    }

    /// <summary>One frame at <paramref name="atSec"/>, cropped to <paramref name="region"/> when given, scaled up for reading.</summary>
    public static IReadOnlyList<string> LookArgs(string input, string output, double atSec, ReviewLookRegion? region)
    {
        string scale = region is null
            ? $"scale={LookWidth.ToString(CultureInfo.InvariantCulture)}:-2"
            : $"crop=iw*{F(region.W)}:ih*{F(region.H)}:iw*{F(region.X)}:ih*{F(region.Y)},scale={RegionLookWidth.ToString(CultureInfo.InvariantCulture)}:-2";
        return
        [
            "-hide_banner", "-nostdin", "-y", .. UserMediaInputFormats.InputOptions(),
            "-ss", atSec.ToString("0.###", CultureInfo.InvariantCulture), "-i", input,
            "-frames:v", "1", "-vf", scale, "-q:v", "3", output
        ];
    }

    private static string F(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static bool IsOwnJson(string key, Guid projectId) =>
        !string.IsNullOrWhiteSpace(key)
        && key.StartsWith($"projects/{projectId:D}/", StringComparison.Ordinal)
        && !key.Contains("..", StringComparison.Ordinal)
        && !key.Contains('\\')
        && key.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

    private async Task<MediaWorkspaceLease?> OpenAsync(StepExecutionContext context, string purpose, CancellationToken ct)
    {
        MediaWorkspaceOpenResult opened = await _workspaces.OpenAsync(
            new MediaWorkspaceRequest(
                ExecutionId: context.Execution.Id,
                OrganizationId: context.Execution.OrganizationId,
                ProjectId: context.Execution.ProjectId,
                StepId: context.Step.Id,
                StepOrder: context.Step.StepOrder,
                // Inert: RunnerEligible is false, so the selector answers Local before it reads the job type.
                JobType: RunnerJobType.VideoCompile,
                Purpose: $"review.{purpose}:{context.Step.StepOrder.ToString(CultureInfo.InvariantCulture)}",
                Iteration: context.IterationCount,
                RunnerEligible: false),
            ct).ConfigureAwait(false);
        return opened.Refused ? null : opened.Lease;
    }
}
