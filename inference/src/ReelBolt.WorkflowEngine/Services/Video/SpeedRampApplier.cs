using System.Globalization;
using System.Text.RegularExpressions;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;

namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>The result of <see cref="SpeedRampApplier.ApplyAsync"/>.</summary>
internal sealed record SpeedRampApplyResult(bool Succeeded, string LocalPath, string VirtualPath, IReadOnlyList<string> Warnings, string? Error);

/// <summary>
/// Runs the speed-ramp pass: one ffmpeg re-encode of the finished program through <see cref="SpeedRampFilterBuilder"/>. The
/// program already carries its captions, overlays, graphics, inserts, region effects, music and effects, so they are retimed
/// with the picture and stay where the event is. Argv is a list; the filter text comes only from typed numbers.
/// </summary>
internal static class SpeedRampApplier
{
    internal static async Task<SpeedRampApplyResult> ApplyAsync(
        IMediaWorkspace workspace, string inputPath, string inputVirtualPath, RampTimeline timeline, bool hasAudio,
        int fpsNum, int fpsDen, string videoCodec, string audioCodec, string preset, int crf, TimeSpan timeout, CancellationToken ct)
    {
        var warnings = new List<string>();
        string graph = SpeedRampFilterBuilder.Build(timeline, hasAudio, fpsNum, fpsDen, warnings);
        string outVirtual = "ramps-" + inputVirtualPath;
        string outPath = await workspace.MaterializeAsync(outVirtual, ct);
        var args = new List<string> { "-nostdin", "-hide_banner", "-y", "-loglevel", "error", "-i", inputPath };
        if (graph.Length > 4000)
        {
            const string ScriptVirtualPath = "speed_ramps_filter.txt";
            // Into the session, where ffmpeg runs: on a remote session MaterializeAsync answers a
            // session-relative name, so a host-side File write would never reach the runner.
            await workspace.WriteTextAsync(ScriptVirtualPath, graph, new System.Text.UTF8Encoding(false), ct);
            args.Add("-filter_complex_script");
            args.Add(await workspace.MaterializeAsync(ScriptVirtualPath, ct));
        }
        else
        {
            args.Add("-filter_complex");
            args.Add(graph);
        }

        args.Add("-map"); args.Add("[vout]");
        if (hasAudio)
        {
            args.Add("-map"); args.Add("[aout]");
        }

        VideoCompileStepExecutor.AddOutputVideoArgs(args, videoCodec, crf, preset);
        if (hasAudio)
        {
            args.Add("-c:a"); args.Add(string.Equals(audioCodec, "copy", StringComparison.OrdinalIgnoreCase) ? "aac" : audioCodec);
        }

        args.Add(outPath);
        VideoToolResult run = await workspace.RunFfmpegAsync(args, timeout, ct);
        if (!run.Succeeded)
            return new SpeedRampApplyResult(false, inputPath, inputVirtualPath, warnings, run.StdErr);
        return new SpeedRampApplyResult(true, outPath, outVirtual, warnings, null);
    }

    private static readonly Regex Stamp = new(@"(\d{2}):(\d{2}):(\d{2})([,.])(\d{3})", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Retimes every timestamp of an SRT/VTT text through the ramps (cue lines only; text is untouched).</summary>
    internal static string RemapSubtitles(string text, RampTimeline timeline) =>
        string.Join('\n', text.Split('\n').Select(line =>
            line.Contains("-->", StringComparison.Ordinal)
                ? Stamp.Replace(line, m =>
                {
                    double sec = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 3600 + int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) * 60 +
                                 int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) + int.Parse(m.Groups[5].Value, CultureInfo.InvariantCulture) / 1000.0;
                    return SubtitleFiles.Stamp(timeline.MapToOutput(sec), m.Groups[4].Value[0]);
                })
                : line));
}
