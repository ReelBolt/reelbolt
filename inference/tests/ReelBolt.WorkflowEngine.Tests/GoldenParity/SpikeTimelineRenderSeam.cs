#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Editing;
using ReelBolt.WorkflowEngine.Services.Video;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>Which of the two equivalent encode paths the spike adapter should use.</summary>
public enum SpikeSeamPath
{
    /// <summary>
    /// One ffmpeg invocation that trims and concatenates inside a single <c>filter_complex</c> —
    /// the shape <c>TimelineRenderStepExecutor</c> (P4-M1) is specified to use.
    /// </summary>
    SingleFilterGraph,

    /// <summary>
    /// Independently encoded per-cut segments joined by the concat demuxer, with the audio mixed in
    /// a separate pass — the shape <c>VideoCompileStepExecutor</c> already uses. This is the
    /// divergence the parity gate exists to police.
    /// </summary>
    SegmentedConcat
}

/// <summary>
/// <b>THROWAWAY SPIKE SCAFFOLDING — not a renderer, not production-reachable, delete in P4-Q1.</b>
/// </summary>
/// <remarks>
/// <para>
/// A reference implementation of <see cref="ITimelineRenderSeam"/> that renders the seeded
/// document's cut list directly. It exists only so the golden-parity harness (plan task P0-M2) can
/// be proven end-to-end <i>today</i>, before <c>StepType.TimelineRender</c> exists (P4-M1). Wave 0
/// opens no PR and this type is deliberately not a quality bar for anybody: it is a ruler for
/// measuring two real encodes, not a product surface.
/// </para>
/// <para>
/// <b>It implements exactly the fixture's shape, and throws otherwise.</b> Supported: one video
/// track whose items are <see cref="MediaClip"/>s that all resolve to the same source file and tile
/// the record timeline without overlap; plus at most one audio clip starting at record frame 0. A
/// document outside that shape raises <see cref="NotSupportedException"/> rather than guessing —
/// silently rendering something approximated would make every parity number meaningless.
/// </para>
/// <para>
/// <b>Everything here is only what the two paths need.</b> No presets, no loudness, no cache, no
/// effects, no registration, no storage: those belong to the real executor, and adding them here
/// would create a second renderer to maintain. In P4-Q1 this type is replaced by an adapter over
/// the real <c>TimelineRenderStepExecutor</c>.
/// </para>
/// <para>
/// <b>Both paths share one encoder configuration.</b> The whole point of the P2 calibration pair is
/// that the two outputs differ <i>only</i> by the encode path, so every codec, rate-control, GOP and
/// audio parameter is identical between them.
/// </para>
/// </remarks>
public sealed class SpikeTimelineRenderSeam : ITimelineRenderSeam
{
    /// <summary>Video encoder arguments shared by both paths.</summary>
    /// <remarks>
    /// <c>-preset veryfast -crf 18</c> is a deliberately ordinary, fast, high-quality H.264
    /// configuration: a parity gate calibrated against a slow preset would measure the encoder
    /// rather than the renderer.
    /// </remarks>
    private const string VideoEncoderName = "libx264";

    private readonly IVideoToolRunner _runner;
    private readonly Func<AssetRef, string> _resolveAssetPath;
    private readonly SpikeSeamPath _path;
    private readonly TimeSpan _timeout;
    private readonly double? _audioGainOverrideDb;

    /// <param name="runner">The single ffmpeg seam. Every invocation goes through it.</param>
    /// <param name="resolveAssetPath">
    /// Maps a document <see cref="AssetRef"/> to a local file path. A renderer resolves assets from
    /// the project's storage; the harness owns the equivalent lookup for its generated fixtures.
    /// </param>
    /// <param name="path">Which of the two equivalent encode paths to use.</param>
    /// <param name="timeout">Per-invocation wall-clock ceiling.</param>
    /// <param name="audioGainOverrideDb">
    /// Spike-only escape hatch used by the audio-leg sensitivity probe: when set, every audio clip
    /// renders at this gain instead of the document's own. It exists because the document model's
    /// clip gain is immutable, and a probe needs a genuinely different mix to prove the audio
    /// threshold can reject one. <c>null</c> (the default) renders the document as written.
    /// </param>
    public SpikeTimelineRenderSeam(
        IVideoToolRunner runner,
        Func<AssetRef, string> resolveAssetPath,
        SpikeSeamPath path,
        TimeSpan timeout,
        double? audioGainOverrideDb = null)
    {
        _runner = runner;
        _resolveAssetPath = resolveAssetPath;
        _path = path;
        _timeout = timeout;
        _audioGainOverrideDb = audioGainOverrideDb;
    }

    /// <inheritdoc />
    public async Task<TimelineRenderProduct> RenderAsync(
        EditTimeline document,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        double fps = Fps(document);
        IReadOnlyList<Cut> cuts = ReadCuts(document, fps);
        AudioBed? bed = ReadAudioBed(document, fps);

        return _path switch
        {
            SpikeSeamPath.SingleFilterGraph => await RenderSingleFilterGraphAsync(cuts, bed, fps, outputPath, cancellationToken).ConfigureAwait(false),
            SpikeSeamPath.SegmentedConcat => await RenderSegmentedConcatAsync(cuts, bed, fps, outputPath, cancellationToken).ConfigureAwait(false),
            _ => throw new NotSupportedException($"Unknown spike seam path '{_path}'.")
        };
    }

    /// <summary>The document's output frame rate as a plain double (its own exact rational).</summary>
    private static double Fps(EditTimeline document) =>
        document.Settings.Rate.Den == 0
            ? EditTimelineSeeder.FallbackFps
            : document.Settings.Rate.Num / (double)document.Settings.Rate.Den;

    /// <summary>
    /// The video track's clips, in record order, together with the source path each resolves to.
    /// </summary>
    private IReadOnlyList<Cut> ReadCuts(EditTimeline document, double fps)
    {
        List<Track> videoTracks = document.Tracks.Where(t => t.Kind == TrackKind.Video).ToList();
        if (videoTracks.Count != 1)
        {
            throw new NotSupportedException(
                $"The spike seam renders exactly one video track; the document has {videoTracks.Count}.");
        }

        List<MediaClip> clips = videoTracks[0].Items.OfType<MediaClip>().OrderBy(c => c.RecordIn).ToList();
        if (clips.Count == 0)
        {
            throw new NotSupportedException("The spike seam needs at least one MediaClip on the video track.");
        }

        var cuts = new List<Cut>(clips.Count);
        foreach (MediaClip clip in clips)
        {
            if (!clip.Enabled)
            {
                continue;
            }

            if (clip.PlaybackRate != 1.0)
            {
                throw new NotSupportedException(
                    $"The spike seam does not implement retime (clip '{clip.Name}' is {clip.PlaybackRate}x).");
            }

            cuts.Add(new Cut(
                SourcePath: _resolveAssetPath(clip.AssetRef),
                SourceInSec: clip.SourceIn / fps,
                SourceOutSec: clip.SourceIn / fps + (clip.RecordOut - clip.RecordIn) / fps,
                RecordIn: clip.RecordIn,
                RecordOut: clip.RecordOut));
        }

        if (cuts.Count == 0)
        {
            throw new NotSupportedException("The spike seam needs at least one enabled MediaClip on the video track.");
        }

        // Contiguity from record frame 0 is what lets both paths render the same program length
        // without a compositor. Anything else (a gap, an overlap, a second track) needs one.
        long expected = 0;
        foreach (Cut cut in cuts)
        {
            if (cut.RecordIn != expected)
            {
                throw new NotSupportedException(
                    $"The spike seam renders a gapless, non-overlapping video track; expected record frame " +
                    $"{expected} but found {cut.RecordIn}.");
            }

            expected = cut.RecordOut;
        }

        return cuts;
    }

    /// <summary>
    /// The single audio clip (the fixture's music bed), or <c>null</c> when the document has none.
    /// </summary>
    private AudioBed? ReadAudioBed(EditTimeline document, double fps)
    {
        List<MediaClip> audioClips = document.Tracks
            .Where(t => t.Kind == TrackKind.Audio)
            .SelectMany(t => t.Items.OfType<MediaClip>())
            .OrderBy(c => c.RecordIn)
            .ToList();

        if (audioClips.Count == 0)
        {
            return null;
        }

        if (audioClips.Count > 1)
        {
            throw new NotSupportedException(
                $"The spike seam mixes exactly one audio clip; the document has {audioClips.Count}.");
        }

        MediaClip clip = audioClips[0];
        if (clip.RecordIn != 0)
        {
            throw new NotSupportedException(
                "The spike seam anchors its single audio clip at record frame 0; a later start needs " +
                "sample-accurate placement the real renderer owns.");
        }

        double gainDb = _audioGainOverrideDb ?? clip.GainDb?.Value ?? 0.0;

        return new AudioBed(
            SourcePath: _resolveAssetPath(clip.AssetRef),
            SourceInSec: clip.SourceIn / fps,
            SourceOutSec: clip.SourceIn / fps + (clip.RecordOut - clip.RecordIn) / fps,
            GainDb: gainDb);
    }

    /// <summary>
    /// Path A — one invocation: every cut trimmed and concatenated inside one <c>filter_complex</c>,
    /// with the audio bed mixed in the same pass.
    /// </summary>
    private async Task<TimelineRenderProduct> RenderSingleFilterGraphAsync(
        IReadOnlyList<Cut> cuts,
        AudioBed? bed,
        double fps,
        string outputPath,
        CancellationToken cancellationToken)
    {
        List<string> args = ["-y", "-v", "error", "-nostdin"];

        // One -i per distinct source file, video assets first, so the filtergraph's input indices
        // are deterministic.
        List<string> videoSources = cuts.Select(c => c.SourcePath).Distinct(StringComparer.Ordinal).ToList();
        foreach (string source in videoSources)
        {
            args.Add("-i");
            args.Add(source);
        }

        int bedInputIndex = -1;
        if (bed is not null)
        {
            bedInputIndex = videoSources.Count;
            args.Add("-i");
            args.Add(bed.SourcePath);
        }

        List<string> graph = [];
        for (int i = 0; i < cuts.Count; i++)
        {
            Cut cut = cuts[i];
            int inputIndex = videoSources.IndexOf(cut.SourcePath);
            graph.Add(
                $"[{inputIndex}:v]trim=start={Seconds(cut.SourceInSec)}:end={Seconds(cut.SourceOutSec)}," +
                $"setpts=PTS-STARTPTS[v{i}]");
        }

        if (cuts.Count == 1)
        {
            graph.Add("[v0]null[vout]");
        }
        else
        {
            string inputs = string.Concat(Enumerable.Range(0, cuts.Count).Select(i => $"[v{i}]"));
            graph.Add($"{inputs}concat=n={cuts.Count}:v=1:a=0[vout]");
        }

        if (bed is not null)
        {
            graph.Add($"[{bedInputIndex}:a]{AudioFilter(bed)}[aout]");
        }

        args.Add("-filter_complex");
        args.Add(string.Join(';', graph));
        args.Add("-map");
        args.Add("[vout]");
        if (bed is not null)
        {
            args.Add("-map");
            args.Add("[aout]");
        }

        args.AddRange(VideoEncoderArgs(fps));
        if (bed is not null)
        {
            args.AddRange(AudioEncoderArgs());
        }
        else
        {
            args.Add("-an");
        }

        args.Add(outputPath);

        await RunSingleAsync(args, "TimelineRender-shaped filter_complex render", cancellationToken).ConfigureAwait(false);

        return new TimelineRenderProduct(outputPath, [args]);
    }

    /// <summary>
    /// Path B — the <c>VideoCompile</c>-shaped route: each cut encoded on its own, the segments
    /// joined by the concat demuxer with stream copy, and the audio mixed in a separate pass before
    /// a final mux.
    /// </summary>
    private async Task<TimelineRenderProduct> RenderSegmentedConcatAsync(
        IReadOnlyList<Cut> cuts,
        AudioBed? bed,
        double fps,
        string outputPath,
        CancellationToken cancellationToken)
    {
        var invocations = new List<IReadOnlyList<string>>();
        string segmentDirectory = Path.Combine(
            Path.GetDirectoryName(outputPath) ?? ".",
            Path.GetFileNameWithoutExtension(outputPath) + ".segments");
        Directory.CreateDirectory(segmentDirectory);

        var segmentPaths = new List<string>(cuts.Count);
        for (int i = 0; i < cuts.Count; i++)
        {
            Cut cut = cuts[i];
            string segmentPath = Path.Combine(segmentDirectory, $"segment-{i:D3}.mp4");
            segmentPaths.Add(segmentPath);

            List<string> segmentArgs =
            [
                "-y", "-v", "error", "-nostdin",
                "-ss", Seconds(cut.SourceInSec),
                "-i", cut.SourcePath,
                "-t", Seconds(cut.SourceOutSec - cut.SourceInSec),
                "-an"
            ];
            segmentArgs.AddRange(VideoEncoderArgs(fps));
            segmentArgs.Add(segmentPath);

            await RunSingleAsync(segmentArgs, $"segment {i} encode", cancellationToken).ConfigureAwait(false);
            invocations.Add(segmentArgs);
        }

        string listPath = Path.Combine(segmentDirectory, "concat-list.txt");
        await File.WriteAllLinesAsync(
            listPath,
            segmentPaths.Select(p => $"file '{EscapeConcatPath(p)}'"),
            cancellationToken).ConfigureAwait(false);

        string videoOnlyPath = Path.Combine(segmentDirectory, "video-only.mp4");
        List<string> concatArgs =
        [
            "-y", "-v", "error", "-nostdin",
            "-f", "concat", "-safe", "0",
            "-i", listPath,
            "-c", "copy",
            videoOnlyPath
        ];
        await RunSingleAsync(concatArgs, "segment concat (stream copy)", cancellationToken).ConfigureAwait(false);
        invocations.Add(concatArgs);

        if (bed is null)
        {
            List<string> muxOnlyArgs =
            [
                "-y", "-v", "error", "-nostdin",
                "-i", videoOnlyPath,
                "-c", "copy",
                "-movflags", "+faststart",
                outputPath
            ];
            await RunSingleAsync(muxOnlyArgs, "final mux", cancellationToken).ConfigureAwait(false);
            invocations.Add(muxOnlyArgs);

            return new TimelineRenderProduct(outputPath, invocations);
        }

        string audioPath = Path.Combine(segmentDirectory, "audio.m4a");
        List<string> audioArgs = ["-y", "-v", "error", "-nostdin", "-i", bed.SourcePath, "-af", AudioFilter(bed)];
        audioArgs.AddRange(AudioEncoderArgs());
        audioArgs.Add(audioPath);
        await RunSingleAsync(audioArgs, "audio mix", cancellationToken).ConfigureAwait(false);
        invocations.Add(audioArgs);

        List<string> muxArgs =
        [
            "-y", "-v", "error", "-nostdin",
            "-i", videoOnlyPath,
            "-i", audioPath,
            "-map", "0:v",
            "-map", "1:a",
            "-c", "copy",
            "-movflags", "+faststart",
            outputPath
        ];
        await RunSingleAsync(muxArgs, "final mux", cancellationToken).ConfigureAwait(false);
        invocations.Add(muxArgs);

        return new TimelineRenderProduct(outputPath, invocations);
    }

    /// <summary>
    /// The audio filter chain, identical in both paths so the two outputs can differ only by the
    /// video route.
    /// </summary>
    private static string AudioFilter(AudioBed bed) =>
        $"atrim=start={Seconds(bed.SourceInSec)}:end={Seconds(bed.SourceOutSec)}," +
        $"asetpts=PTS-STARTPTS,volume={Seconds(bed.GainDb)}dB";

    private static IEnumerable<string> VideoEncoderArgs(double fps)
    {
        yield return "-c:v";
        yield return VideoEncoderName;
        yield return "-preset";
        yield return "veryfast";
        yield return "-crf";
        yield return "18";
        yield return "-pix_fmt";
        yield return "yuv420p";
        yield return "-r";
        yield return Seconds(fps);
        yield return "-g";
        yield return Seconds(fps);
    }

    private static IEnumerable<string> AudioEncoderArgs()
    {
        yield return "-c:a";
        yield return "aac";
        yield return "-b:a";
        yield return "128k";
        yield return "-ar";
        yield return "48000";
        yield return "-ac";
        yield return "2";
    }

    /// <summary>
    /// The single ffmpeg-seam call every step of every path funnels through.
    /// </summary>
    /// <remarks>
    /// A non-zero exit throws rather than returning a partial product: a failed step must never look
    /// like a measurement, and the concat/mux steps will happily produce a short or silent file.
    /// </remarks>
    private async Task RunSingleAsync(
        IReadOnlyList<string> args,
        string what,
        CancellationToken cancellationToken)
    {
        VideoToolResult result = await _runner.RunFfmpegAsync(args, _timeout, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"Spike seam: {what} failed (exitCode={result.ExitCode}, timedOut={result.TimedOut}). " +
                $"argv: ffmpeg {string.Join(' ', args)}. stderr: {result.StdErr.Trim()}");
        }
    }

    /// <summary>Invariant, round-trippable seconds — never a culture-formatted number.</summary>
    private static string Seconds(double value) =>
        value.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>
    /// Quotes a path for the concat demuxer's <c>file '…'</c> directive. A single quote is escaped
    /// with the demuxer's own shell-style convention.
    /// </summary>
    private static string EscapeConcatPath(string path) => path.Replace("'", @"'\''");

    /// <summary>One video cut: the source range it draws and the record range it occupies.</summary>
    private sealed record Cut(string SourcePath, double SourceInSec, double SourceOutSec, long RecordIn, long RecordOut);

    /// <summary>The document's single audio clip, resolved.</summary>
    private sealed record AudioBed(string SourcePath, double SourceInSec, double SourceOutSec, double GainDb);
}
