using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.VideoEvents;
using ReelBolt.WorkflowEngine.Services.Video;
using ReelBolt.WorkflowEngine.Services.Video.Events;
using ReelBolt.WorkflowEngine.Tests.Quality;
using Xunit;
using Xunit.Abstractions;

namespace ReelBolt.WorkflowEngine.Tests.Video.Events;

/// <summary>
/// Opt-in local measurement on a real, already-edited motion-graphics clip. Set FOOTAGE_EDIT_CLIP_DIR to a directory holding
/// <c>clip.mp4</c>. Without it the test does nothing. It writes <c>events.tsv</c> (every event), <c>events-moments.tsv</c>
/// (the edit moments alone) and <c>events-offered.tsv</c> (what a sound designer is offered, with the plain words) into that
/// directory, for the scorer kept with the answer key. Nothing from the directory is ever written to the repo.
/// </summary>
public class RealClipMomentsLocalTests(ITestOutputHelper output)
{
    private static string Line(VideoEvent e) => string.Create(CultureInfo.InvariantCulture,
        $"{e.Frame / 60.0:F3}\t{e.Type}\t{e.Strength:F2}\t{(e.EndFrame ?? e.Frame) / 60.0:F3}\t{e.Direction ?? "-"}\t{e.Speed ?? "-"}\t{(e.Region is { } r ? $"{r.X:F2},{r.Y:F2},{r.X + r.W:F2},{r.Y + r.H:F2}" : "-")}");

    [FfmpegToolsFact]
    public async Task Writes_the_events_of_the_real_clip_for_the_scorer()
    {
        string? dir = Environment.GetEnvironmentVariable("FOOTAGE_EDIT_CLIP_DIR");
        if (string.IsNullOrEmpty(dir) || !File.Exists(Path.Combine(dir, "clip.mp4")))
            return;

        IOptions<VideoEditingOptions> options = Options.Create(new VideoEditingOptions());
        FfmpegVideoToolRunner runner = new(options, NullLogger<FfmpegVideoToolRunner>.Instance);
        LocalMediaWorkspace workspace = LocalMediaWorkspace.CreateAt(dir, runner);
        MediaProbeResult probed = await new FfprobeMediaProbe(runner, options, NullLogger<FfprobeMediaProbe>.Instance)
            .ProbeAsync(workspace, "clip.mp4", CancellationToken.None);
        VideoEventDetector detector = new(
            new FrameSignalSampler(options, NullLogger<FrameSignalSampler>.Instance),
            new FfmpegSilenceDetector(runner, options, NullLogger<FfmpegSilenceDetector>.Instance),
            options, NullLogger<VideoEventDetector>.Instance);
        VideoEventSet set = await detector.DetectAsync(workspace, "clip.mp4", probed, new VideoEventOptions { MaxEvents = 5000, DetectBeats = false, DetectSpeech = false });

        // the edit moments on their own, for comparison with the prototype
        EditMomentAnalyzer only = new(FrameSignalSampler.PlanRate(SourceTimeoutPolicy.FpsOf(probed.FpsNum, probed.FpsDen)).Fps, probed.Width, probed.Height);
        await new FrameSignalSampler(options, NullLogger<FrameSignalSampler>.Instance).SampleAsync(workspace, "clip.mp4", probed.FpsNum, probed.FpsDen, probed.DurationSec, only, CancellationToken.None);
        File.WriteAllLines(Path.Combine(dir, "events-moments.tsv"), only.Complete().Select(Line));

        double fps = set.Fps;
        List<(double T, string Type, double Strength)> events = [.. set.Events.Select(e => (e.Frame / fps, e.Type.ToString(), e.Strength))];
        File.WriteAllLines(Path.Combine(dir, "events.tsv"), set.Events.Select(Line));
        output.WriteLine($"detected {events.Count} events, suppressed {set.Suppressed}");
        foreach (IGrouping<string, (double T, string Type, double Strength)> g in events.GroupBy(e => e.Type))
            output.WriteLine($"  {g.Key}: {g.Count()}");

        // what a sound designer is offered: the same merge, words and thinning the analysis step uses
        List<VideoAnalysisRenderEvent> merged = VideoEventOffers.Merge([], [new VideoEventOffers.DetectedSource(0, set)]);
        List<VideoAnalysisRenderEvent> offered = SfxWorthyEvents.Select(merged, _ => probed.DurationSec);
        List<VideoAnalysisRenderEvent> offeredAll = SfxWorthyEvents.Select(merged, _ => probed.DurationSec, onFootageToo: true);
        File.WriteAllLines(Path.Combine(dir, "events-offered.tsv"), offered.Select(e => string.Create(CultureInfo.InvariantCulture,
            $"{e.ContentSec:F3}\t{e.Type}\t{e.Strength:F2}\t{(e.EndFrame ?? e.Frame) / fps:F3}\t{e.Direction ?? "-"}\t{e.SpeedWord ?? "-"}\t{(e.Region is { } r ? $"{r.X:F2},{r.Y:F2},{r.X + r.W:F2},{r.Y + r.H:F2}" : "-")}\t{e.Id}\t{VideoEventWords.What(e) ?? "-"}\t{e.Kind ?? "-"}")));
        File.WriteAllLines(Path.Combine(dir, "events-offered-all.tsv"), offeredAll.Select(e => string.Create(CultureInfo.InvariantCulture,
            $"{e.ContentSec:F3}\t{e.Type}\t{e.Strength:F2}\t{(e.EndFrame ?? e.Frame) / fps:F3}\t{e.Direction ?? "-"}\t{e.SpeedWord ?? "-"}\t{(e.Region is { } r ? $"{r.X:F2},{r.Y:F2},{r.X + r.W:F2},{r.Y + r.H:F2}" : "-")}\t{e.Id}\t{VideoEventWords.What(e) ?? "-"}\t{e.Kind ?? "-"}")));
        output.WriteLine($"worthy {merged.Count(SfxWorthyEvents.IsWorthy)}, offered {offered.Count}");
        foreach (IGrouping<string?, VideoAnalysisRenderEvent> g in offered.GroupBy(e => e.Type))
            output.WriteLine($"  offered {g.Key}: {g.Count()}");
    }
}
