#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.Shared.Editing;
using ReelBolt.WorkflowEngine.Services.Video.TimelineRender;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Editor v2, P8: the audio processing chain and every channel map, run through the REAL ffmpeg on
/// this host. The exact-string tests in <see cref="EditorP8BackendTests"/> prove what the builders
/// write; this proves ffmpeg accepts it (every filter exists, every option name and value parses) —
/// the half an exact-string test cannot see. Skipped, visibly, where no ffmpeg is on PATH.
/// </summary>
public class EditorP8RealFfmpegTests
{
    [FfmpegFact]
    public void Every_processing_stage_and_channel_map_is_accepted_by_the_real_ffmpeg()
    {
        string processing = string.Join(',', AudioMixBuilder.ProcessingChain(
            new AudioDynamics(new AudioCompressor(-24, 6, 5, 400, 3), new AudioLimiter(-2), new AudioGate(-50, 3, 10, 300), new AudioDeesser(0.7)),
            new AudioDenoise(Strength: 0.8),
            new AudioLeveler("speech", -16)));
        string dynamicLeveler = string.Join(',', AudioMixBuilder.ProcessingChain(null, null, new AudioLeveler("dynamic", -20)));

        foreach (string map in new[] { "left", "right", "mono", "swap" })
        {
            RunFfmpeg(AudioMixBuilder.ChannelMapFilter(map) + processing).Should().Be(0, $"channel map '{map}' + the processing chain must run");
        }

        RunFfmpeg(dynamicLeveler).Should().Be(0);
    }

    private static int RunFfmpeg(string audioFilter)
    {
        ProcessStartInfo info = new("ffmpeg")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        foreach (string arg in new[] { "-hide_banner", "-nostdin", "-loglevel", "error", "-f", "lavfi", "-i", "sine=f=440:d=1:sample_rate=48000", "-ac", "2", "-af", audioFilter.TrimEnd(','), "-f", "null", "-" })
        {
            info.ArgumentList.Add(arg);
        }

        using Process process = Process.Start(info)!;
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(30_000);
        process.ExitCode.Should().Be(0, $"ffmpeg rejected '{audioFilter}': {stderr}");
        return process.ExitCode;
    }
}
