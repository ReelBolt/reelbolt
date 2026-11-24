using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// <c>MusicGainDb: 0</c> against a real ffmpeg: the music branch the compile builds, run over a
/// known track, must reproduce that track's RMS and peak at unity (within 0.1 dB) — while the
/// intensity bed it replaces did not (it played the same track at -1 dB through a limiter, and at
/// -6 dB or lower under dialogue). Skipped, visibly, on a host without ffmpeg on PATH.
/// </summary>
public class MusicUnityPassThroughRealFfmpegTests
{
    private const double DurationSec = 4;

    private static ResolvedMusic Music(string path, MusicBedLevel level, double fadeSec)
    {
        double gain = Math.Round(Math.Pow(10, level.BedDb / 20.0), 5);
        return new ResolvedMusic(
            TrackId: "m1", ProjectFileId: Guid.NewGuid(), TrackName: "tone", LocalPath: path,
            TrackDurationSec: DurationSec, OutputDurationSec: DurationSec, PlayEndSec: DurationSec, LoopInput: false,
            BedGainLinear: gain, DuckGainLinear: gain, RampSec: 0.2, FadeInSec: fadeSec, FadeOutSec: fadeSec,
            LiftWindows: [], Limit: level.Limit, SkipZeroFades: level.Source == "absolute");
    }

    [FfmpegToolsFact]
    public void Unity_gain_reproduces_the_tracks_rms_and_peak_within_a_tenth_of_a_dB_on_a_silent_source()
    {
        string dir = Path.Combine(Path.GetTempPath(), "music-unity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // A hot track (peak ~ -0.9 dBFS), like a mastered one.
            string track = Path.Combine(dir, "track.wav");
            Run($"-f lavfi -i \"sine=frequency=440:duration={DurationSec}:sample_rate=48000\" -af volume=0.9,aformat=channel_layouts=stereo \"{track}\"");
            (double inRms, double inPeak) = Measure(track);

            MusicBedLevel unity = MusicGainPolicy.Resolve(0, -20, soleSoundtrack: true, VideoCompileStepExecutor.SoleSoundtrackMusicDb, false, VideoCompileStepExecutor.NarrationGapLiftDb);
            MusicBedLevel legacy = MusicGainPolicy.Resolve(null, -20, soleSoundtrack: true, VideoCompileStepExecutor.SoleSoundtrackMusicDb, false, VideoCompileStepExecutor.NarrationGapLiftDb);

            // Music alone (the sole-soundtrack shape), then music over the silent anullsrc base.
            string alone = Path.Combine(dir, "alone.wav");
            Run($"-i \"{track}\" -filter_complex \"{MusicMixFilterBuilder.BuildMusicBranch(0, Music(track, unity, 0), "[aout]")}\" -map [aout] \"{alone}\"");
            string mixed = Path.Combine(dir, "mixed.wav");
            string silentBase = $"anullsrc=r=48000:cl=stereo:d={DurationSec}[adial]";
            Run($"-i \"{track}\" -filter_complex \"{silentBase};{MusicMixFilterBuilder.BuildMusicBranch(0, Music(track, unity, 0))};" +
                $"{MusicMixFilterBuilder.BuildMixStage("[adial]")}\" -map [aout] \"{mixed}\"");
            string old = Path.Combine(dir, "old.wav");
            Run($"-i \"{track}\" -filter_complex \"{MusicMixFilterBuilder.BuildMusicBranch(0, Music(track, legacy, 0), "[aout]")}\" -map [aout] \"{old}\"");

            foreach (string output in new[] { alone, mixed })
            {
                (double rms, double peak) = Measure(output);
                Math.Abs(rms - inRms).Should().BeLessThan(0.1, $"RMS of {Path.GetFileName(output)} (in {inRms}, out {rms})");
                Math.Abs(peak - inPeak).Should().BeLessThan(0.1, $"peak of {Path.GetFileName(output)} (in {inPeak}, out {peak})");
            }

            (double oldRms, _) = Measure(old);
            (inRms - oldRms).Should().BeGreaterThan(0.5, "the pre-feature sole-soundtrack bed is not a pass-through");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static (double RmsDb, double PeakDb) Measure(string file)
    {
        string stderr = Run($"-i \"{file}\" -af astats=metadata=0:reset=0 -f null -");
        string overall = stderr[stderr.LastIndexOf("Overall", StringComparison.Ordinal)..];
        double Read(string label) => double.Parse(
            Regex.Match(overall, label + @" level dB: (-?[0-9.]+)").Groups[1].Value, CultureInfo.InvariantCulture);
        return (Read("RMS"), Read("Peak"));
    }

    private static string Run(string args)
    {
        var psi = new ProcessStartInfo("ffmpeg") { RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (string a in Split("-nostdin -hide_banner -y " + args))
            psi.ArgumentList.Add(a);
        using Process p = Process.Start(psi)!;
        string err = p.StandardError.ReadToEnd();
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        p.ExitCode.Should().Be(0, err);
        return err;
    }

    private static IEnumerable<string> Split(string s) =>
        Regex.Matches(s, "\"([^\"]*)\"|(\\S+)").Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
}
