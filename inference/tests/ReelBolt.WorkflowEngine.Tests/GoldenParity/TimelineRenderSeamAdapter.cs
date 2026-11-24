#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using ReelBolt.Shared.Editing;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Services.Video;
using ReelBolt.WorkflowEngine.Services.Video.TimelineRender;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// P4-Q1: the production renderer behind the golden-parity seam.
/// </summary>
/// <remarks>
/// <para>
/// Drives <see cref="TimelineRenderer"/> — the exact class <c>TimelineRenderStepExecutor</c> runs —
/// over the harness's generated fixture files, probing each asset through the production
/// <see cref="FfprobeMediaProbe"/> so the renderer sees what it sees in the engine. The step
/// executor's own concerns (database lookups, storage uploads, the step contract) are deliberately
/// outside the seam: the gate measures the RENDER, not the plumbing around it.
/// </para>
/// <para>
/// <see cref="NullRegionCache"/> on purpose: P1 requires two runs of the same document to produce
/// byte-identical ffmpeg invocations, which a cache hit on the second run would break by design.
/// The encode settings pin the fixture's own geometry (<see cref="TimelineRenderPreset.Master"/>)
/// at the spike's calibration point (x264 veryfast, CRF 18), so the parity numbers compare renderers
/// rather than encoder presets.
/// </para>
/// </remarks>
public sealed class TimelineRenderSeamAdapter : ITimelineRenderSeam
{
    private readonly IVideoToolRunner _runner;
    private readonly Func<AssetRef, string> _resolveAssetPath;
    private readonly string _scratchRoot;
    private readonly TimeSpan _timeout;
    private readonly FfprobeMediaProbe _probe;
    private int _runs;

    public TimelineRenderSeamAdapter(
        IVideoToolRunner runner,
        Func<AssetRef, string> resolveAssetPath,
        string scratchRoot,
        TimeSpan timeout)
    {
        _runner = runner;
        _resolveAssetPath = resolveAssetPath;
        _scratchRoot = scratchRoot;
        _timeout = timeout;
        _probe = new FfprobeMediaProbe(
            runner,
            Microsoft.Extensions.Options.Options.Create(new VideoEditingOptions()),
            NullLogger<FfprobeMediaProbe>.Instance);
    }

    /// <summary>The config every parity render uses — the fixture's own size and rate, x264 veryfast CRF 18, no loudness normalisation.</summary>
    public static TimelineRenderStepConfig ParityConfig(Guid timelineId) => new(
        EditTimelineId: timelineId,
        Preset: TimelineRenderPreset.Master,
        Encode: new TimelineRenderEncodeSettings(Codec: TimelineRenderVideoCodec.H264, Crf: 18, Speed: TimelineRenderEncoderSpeed.Fast, AudioBitrateKbps: 128),
        OutputFileName: "parity.mp4",
        RegisterProjectFile: false);

    public async Task<TimelineRenderProduct> RenderAsync(
        EditTimeline document,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        Dictionary<AssetRef, LocalAsset> assets = [];
        foreach (AssetRef reference in document.Tracks.SelectMany(t => t.Items).OfType<MediaClip>().Select(c => c.AssetRef).Distinct())
        {
            string path = _resolveAssetPath(reference);
            MediaProbeResult probe = await _probe.ProbeAsync(path, cancellationToken).ConfigureAwait(false);
            assets[reference] = new LocalAsset(path, $"{Path.GetFileName(path)}|{new FileInfo(path).Length}", probe);
        }

        // ONE scratch directory for every render, emptied first. P1 compares the two runs' argv
        // byte for byte (only each run's own output path is normalised away), and every region,
        // concat-list and mix path the renderer builds lives under this directory — a per-run
        // directory would make the two invocation lists differ on nothing but a run number. The
        // harness renders sequentially, so sharing the directory is safe; the count is kept for
        // the report only.
        Interlocked.Increment(ref _runs);
        string scratch = Path.Combine(_scratchRoot, "work");
        if (Directory.Exists(scratch))
            Directory.Delete(scratch, recursive: true);
        Directory.CreateDirectory(scratch);

        TimelineRenderEnvironment env = new(
            Runner: _runner,
            ScratchDirectory: scratch,
            FontFilePath: new VideoEditingOptions().FontFilePath,
            Timeout: _timeout,
            Threads: 2,
            Cache: NullRegionCache.Instance,
            ProjectId: document.ProjectId);

        TimelineRenderResult result = await new TimelineRenderer(NullLogger.Instance)
            .RenderAsync(document, ParityConfig(document.Id), assets, outputPath, env, cancellationToken)
            .ConfigureAwait(false);

        return new TimelineRenderProduct(result.OutputPath, result.Invocations);
    }
}
