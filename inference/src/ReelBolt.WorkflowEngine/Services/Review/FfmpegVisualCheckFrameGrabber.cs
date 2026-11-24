using System.Globalization;
using System.Text.Json;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Services.Compute;
using ReelBolt.WorkflowEngine.Services.Video;

namespace ReelBolt.WorkflowEngine.Services.Review;

/// <summary>The frames a grab produced, or a fixed reason it produced none.</summary>
public sealed record VisualCheckFrameGrab(IReadOnlyList<VisualCheckFrameImage> Frames, string? Failure);

/// <summary>Samples the scheduled frames of a playable output. May throw; the caller turns any failure into "unavailable".</summary>
public interface IVisualCheckFrameGrabber
{
    Task<VisualCheckFrameGrab> GrabAsync(StepExecutionContext context, VisualCheckSource source, int frames, CancellationToken ct);
}

/// <summary>
/// Samples a render or compile with the engine's own ffmpeg, through the same seams every media step uses:
/// <see cref="IMediaWorkspaceFactory"/> (pinned local — <c>RunnerEligible: false</c>, as ThumbnailGenerate is,
/// because the media session's job types are a closed set with no review job), <see cref="IMediaProbe"/> for
/// the duration and <see cref="IKeyframeExtractor"/> for each frame. Every argv is built by those types'
/// builders from engine-owned values (the staged virtual path, a number of seconds, a width); nothing a model
/// wrote reaches ffmpeg. A frame ffmpeg cannot extract is skipped; no frame at all is a failure.
/// </summary>
public sealed class FfmpegVisualCheckFrameGrabber : IVisualCheckFrameGrabber
{
    /// <summary>The width each sampled frame is scaled down to (enough to read a caption, small enough to send nine).</summary>
    public const int FrameMaxWidth = 768;

    private const string Directory = "visual-check/";

    private readonly IMediaWorkspaceFactory _workspaces;
    private readonly IMediaProbe _probe;
    private readonly IKeyframeExtractor _keyframes;
    private readonly ILogger<FfmpegVisualCheckFrameGrabber> _logger;

    public FfmpegVisualCheckFrameGrabber(
        IMediaWorkspaceFactory workspaces, IMediaProbe probe, IKeyframeExtractor keyframes, ILogger<FfmpegVisualCheckFrameGrabber> logger)
    {
        _workspaces = workspaces;
        _probe = probe;
        _keyframes = keyframes;
        _logger = logger;
    }

    public async Task<VisualCheckFrameGrab> GrabAsync(StepExecutionContext context, VisualCheckSource source, int frames, CancellationToken ct)
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
                Purpose: $"review.visual-check:{context.Step.StepOrder.ToString(CultureInfo.InvariantCulture)}",
                Iteration: context.IterationCount,
                RunnerEligible: false),
            ct).ConfigureAwait(false);
        if (opened.Refused || opened.Lease is null)
            return new VisualCheckFrameGrab([], "the media workspace could not be opened");

        await using MediaWorkspaceLease lease = opened.Lease;
        IMediaWorkspace workspace = lease.Workspace;

        string sourcePath = Directory + "source" + Path.GetExtension(source.StorageKey).ToLowerInvariant();
        await workspace.StageAsync(source.StorageKey, sourcePath, ct).ConfigureAwait(false);

        MediaProbeResult probe = await _probe.ProbeAsync(workspace, sourcePath, ct).ConfigureAwait(false);
        IReadOnlyList<VisualCheckFrameTime> schedule = VisualCheckFrameSchedule.For(probe.DurationSec, frames);
        schedule = WithOverlayMidpoints(schedule, context, source, probe.DurationSec);
        if (schedule.Count == 0)
            return new VisualCheckFrameGrab([], "the output's duration could not be read");

        List<VisualCheckFrameImage> images = new(schedule.Count);
        foreach (VisualCheckFrameTime time in schedule)
        {
            string framePath = $"{Directory}frame-{time.Frame.ToString("D2", CultureInfo.InvariantCulture)}.jpg";
            try
            {
                await _keyframes.ExtractKeyframeAsync(workspace, sourcePath, framePath, time.AtSec, FrameMaxWidth, ct).ConfigureAwait(false);
                byte[] jpeg = await workspace.ReadAsync(framePath, VisualFrameReviewer.MaxJpegBytes, ct).ConfigureAwait(false);
                if (jpeg.Length > 0)
                    images.Add(new VisualCheckFrameImage(time.Frame, time.AtSec, jpeg));
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Visual check: frame {Frame} at {AtSec}s could not be extracted; skipping it", time.Frame, time.AtSec);
            }
        }

        return images.Count == 0
            ? new VisualCheckFrameGrab([], "no frame could be extracted from the output under review")
            : new VisualCheckFrameGrab(images, null);
    }

    /// <summary>
    /// Adds the midpoint of every picture/video overlay the compile reported to the schedule, so a short
    /// card in a long film is looked at instead of falling between two even samples. The count stays at
    /// the schedule's size (the <c>Frames</c> samples plus the last frame): when every slot is taken, a
    /// midpoint replaces the nearest interior even sample (the opening frame and the end-card frame are
    /// kept). A compile that is absent, is not a <see cref="StepType.VideoCompile"/>, or whose output is
    /// unreadable leaves the schedule exactly as it was.
    /// </summary>
    private static IReadOnlyList<VisualCheckFrameTime> WithOverlayMidpoints(
        IReadOnlyList<VisualCheckFrameTime> schedule, StepExecutionContext context, VisualCheckSource source, double durationSec)
    {
        if (schedule.Count == 0)
            return schedule;

        IReadOnlyList<double> midpoints = OverlayMidpoints(context, source, durationSec);
        if (midpoints.Count == 0)
            return schedule;

        int cap = schedule.Count;
        List<double> times = [.. schedule.Select(t => t.AtSec)];
        foreach (double midpoint in midpoints)
        {
            if (times.Contains(midpoint))
                continue;

            if (times.Count < cap)
            {
                times.Add(midpoint);
            }
            else if (NearestInteriorSample(times, midpoint) is { } replaceAt)
            {
                times[replaceAt] = midpoint;
            }
            else
            {
                // Only the opening and end-card frames are left; dropping either costs more than
                // skipping this midpoint, and the cap is never exceeded.
                continue;
            }

            times.Sort();
        }

        return [.. times.Select((at, i) => new VisualCheckFrameTime(i + 1, at))];
    }

    /// <summary>The index of the interior even sample closest to <paramref name="midpoint"/>, or null when only the ends remain.</summary>
    private static int? NearestInteriorSample(IReadOnlyList<double> times, double midpoint)
    {
        if (times.Count < 3)
            return null;

        double first = times[0];
        double last = times[^1];
        int best = -1;
        double bestDistance = double.MaxValue;
        for (int i = 1; i < times.Count - 1; i++)
        {
            if (times[i] <= first || times[i] >= last)
                continue;
            double distance = Math.Abs(times[i] - midpoint);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }

        return best < 0 ? null : best;
    }

    /// <summary>
    /// The rounded midpoint of each applied overlay (<c>startSec + (endSec - startSec) / 2</c>) the
    /// source step's compile output reports under <c>mediaOverlays.items</c>, strictly inside the clip.
    /// Empty when there is no compile entry, no such step, or the output/node cannot be read.
    /// </summary>
    private static IReadOnlyList<double> OverlayMidpoints(StepExecutionContext context, VisualCheckSource source, double durationSec)
    {
        if (!context.AllSteps.Any(s => s.StepOrder == source.StepOrder && s.StepType == StepType.VideoCompile))
            return [];

        StepOutputHistoryEntry? entry = null;
        // Last wins among equal orders: after a loop-back the history may hold a stale and a fresh entry.
        for (int i = context.StepOutputHistory.Count - 1; i >= 0; i--)
        {
            if (context.StepOutputHistory[i].StepOrder == source.StepOrder)
            {
                entry = context.StepOutputHistory[i];
                break;
            }
        }

        if (entry is null || string.IsNullOrWhiteSpace(entry.Output))
            return [];

        try
        {
            using JsonDocument document = JsonDocument.Parse(entry.Output);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("mediaOverlays", out JsonElement overlays)
                || overlays.ValueKind != JsonValueKind.Object
                || !overlays.TryGetProperty("items", out JsonElement items)
                || items.ValueKind != JsonValueKind.Array)
                return [];

            List<double> midpoints = [];
            foreach (JsonElement item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !item.TryGetProperty("startSec", out JsonElement startElement)
                    || !item.TryGetProperty("endSec", out JsonElement endElement)
                    || startElement.ValueKind != JsonValueKind.Number
                    || !startElement.TryGetDouble(out double start)
                    || endElement.ValueKind != JsonValueKind.Number
                    || !endElement.TryGetDouble(out double end)
                    || !double.IsFinite(start)
                    || !double.IsFinite(end)
                    || end <= start)
                    continue;

                double midpoint = Math.Round(start + ((end - start) / 2.0), 2, MidpointRounding.AwayFromZero);
                if (!double.IsFinite(midpoint) || midpoint < 0 || midpoint >= durationSec)
                    continue;
                if (!midpoints.Contains(midpoint))
                    midpoints.Add(midpoint);
            }

            return midpoints;
        }
        catch (JsonException)
        {
            // An unreadable compile node falls back to the even schedule; the check still runs.
            return [];
        }
    }
}
