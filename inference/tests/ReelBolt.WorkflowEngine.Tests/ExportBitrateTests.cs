using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Editing;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Video.TimelineRender;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>Rate control on the delivery encode and on VideoCompile: argv, validation, estimate, save paths.</summary>
public class ExportBitrateTests
{
    private static readonly EditTimelineSettings Hd = new(1920, 1080, new Rational(30, 1), 48000, new Rational(1, 1), AspectPolicy.Fit, 0);

    private static ResolvedRenderTarget Resolve(TimelineRenderEncodeSettings? encode, TimelineRenderPreset preset = TimelineRenderPreset.Hd1080p16x9) =>
        TimelineRenderPresets.Resolve(new TimelineRenderStepConfig(Guid.NewGuid(), Preset: preset, Encode: encode), Hd);

    [Fact]
    public void Quality_argv_is_exactly_the_historical_crf()
    {
        TimelineRenderer.RateControlArgs(Resolve(null)).Should().Equal("-crf", "18");
        TimelineRenderer.RateControlArgs(Resolve(new TimelineRenderEncodeSettings(Crf: 24))).Should().Equal("-crf", "24");
    }

    [Theory]
    [InlineData(TimelineRenderVideoCodec.H264)]
    [InlineData(TimelineRenderVideoCodec.H265)]
    public void Capped_adds_a_maximum_to_the_crf_encode(TimelineRenderVideoCodec codec)
    {
        ResolvedRenderTarget target = Resolve(new TimelineRenderEncodeSettings(Codec: codec, Crf: 20, RateControl: TimelineRenderRateControl.Capped, VideoBitrateKbps: 5000));
        TimelineRenderer.RateControlArgs(target).Should().Equal("-crf", "20", "-maxrate", "5000k", "-bufsize", "10000k");
        target.Encoder.Should().Be(codec == TimelineRenderVideoCodec.H265 ? "libx265" : "libx264");
    }

    [Theory]
    [InlineData(TimelineRenderVideoCodec.H264)]
    [InlineData(TimelineRenderVideoCodec.H265)]
    public void Average_is_a_single_pass_target_with_no_crf(TimelineRenderVideoCodec codec)
    {
        ResolvedRenderTarget target = Resolve(new TimelineRenderEncodeSettings(Codec: codec, RateControl: TimelineRenderRateControl.Average, VideoBitrateKbps: 4000));
        TimelineRenderer.RateControlArgs(target).Should().Equal("-b:v", "4000k", "-maxrate", "6000k", "-bufsize", "8000k");
    }

    [Fact]
    public void The_plan_cap_keeps_the_bitrate()
    {
        TimelineRenderStepConfig config = new(Guid.NewGuid(), Preset: TimelineRenderPreset.Uhd4k16x9,
            Encode: new TimelineRenderEncodeSettings(RateControl: TimelineRenderRateControl.Average, VideoBitrateKbps: 30000));
        TimelineRenderStepConfig capped = TimelineRenderPresets.CapToShortSide(config, Hd, 1080, out bool applied);
        applied.Should().BeTrue();
        capped.Encode!.RateControl.Should().Be(TimelineRenderRateControl.Average);
        capped.Encode.VideoBitrateKbps.Should().Be(30000);
        TimelineRenderer.RateControlArgs(TimelineRenderPresets.Resolve(capped, Hd)).Should().StartWith("-b:v", "30000k");
    }

    [Theory]
    [InlineData(TimelineRenderRateControl.Quality, 4000, "encode.videoBitrateKbps can only be set when rateControl is Capped or Average.")]
    [InlineData(TimelineRenderRateControl.Capped, null, "encode.videoBitrateKbps is required when rateControl is Capped.")]
    [InlineData(TimelineRenderRateControl.Average, null, "encode.videoBitrateKbps is required when rateControl is Average.")]
    [InlineData(TimelineRenderRateControl.Average, 199, "encode.videoBitrateKbps must be between 200 and 100000, found 199.")]
    [InlineData(TimelineRenderRateControl.Capped, 100001, "encode.videoBitrateKbps must be between 200 and 100000, found 100001.")]
    [InlineData((TimelineRenderRateControl)9, null, "encode.rateControl '9' is not a known rate control.")]
    public void Rate_control_combinations_are_refused_with_a_plain_message(TimelineRenderRateControl rc, int? kbps, string expected)
    {
        new TimelineRenderStepConfig(Guid.NewGuid(), Encode: new TimelineRenderEncodeSettings(RateControl: rc, VideoBitrateKbps: kbps))
            .Validate().Should().Contain(expected);
    }

    [Theory]
    [InlineData(200)]
    [InlineData(100000)]
    public void The_bitrate_bounds_are_accepted(int kbps) =>
        new TimelineRenderStepConfig(Guid.NewGuid(), Encode: new TimelineRenderEncodeSettings(RateControl: TimelineRenderRateControl.Average, VideoBitrateKbps: kbps))
            .Validate().Should().BeEmpty();

    [Fact]
    public void Audio_only_rejects_video_rate_fields_and_accepts_none()
    {
        new TimelineRenderStepConfig(Guid.NewGuid(), Preset: TimelineRenderPreset.AudioOnly,
                Encode: new TimelineRenderEncodeSettings(RateControl: TimelineRenderRateControl.Average, VideoBitrateKbps: 4000))
            .Validate().Should().Contain("encode.rateControl and encode.videoBitrateKbps do not apply to an audio-only render.");
        new TimelineRenderStepConfig(Guid.NewGuid(), Preset: TimelineRenderPreset.AudioOnly, Encode: new TimelineRenderEncodeSettings())
            .Validate().Should().BeEmpty();
    }

    [Fact]
    public void Rate_control_binds_by_name_only()
    {
        JsonSerializerOptions web = new(JsonSerializerDefaults.Web);
        TimelineRenderEncodeSettings ok = JsonSerializer.Deserialize<TimelineRenderEncodeSettings>("""{"rateControl":"average","videoBitrateKbps":3000}""", web)!;
        ok.RateControl.Should().Be(TimelineRenderRateControl.Average);
        ok.VideoBitrateKbps.Should().Be(3000);
        Action act = () => JsonSerializer.Deserialize<TimelineRenderEncodeSettings>("""{"rateControl":1}""", web);
        act.Should().Throw<JsonException>();
    }

    [Fact]
    public void Rate_control_enum_is_append_ordered()
    {
        Enum.GetNames<TimelineRenderRateControl>().Should().Equal("Quality", "Capped", "Average");
        TimelineRenderRateControl.Average.Should().Be(Enum.GetValues<TimelineRenderRateControl>().Last());
    }

    // ── VideoCompile ─────────────────────────────────────────────────────────

    [Fact]
    public void VideoCompile_without_rate_control_is_byte_identical()
    {
        List<string> plain = [];
        VideoCompileStepExecutor.AddOutputVideoArgs(plain, "libx264", 20, "veryfast");
        plain.Should().Equal("-c:v", "libx264", "-crf", "20", "-preset", "veryfast", "-pix_fmt", "yuv420p", "-color_range", "tv", "-movflags", "+faststart");

        List<string> quality = [];
        VideoCompileStepExecutor.AddOutputVideoArgs(quality, "libx264", 20, "veryfast", rateControl: TimelineRenderRateControl.Quality);
        quality.Should().Equal(plain);
    }

    [Fact]
    public void VideoCompile_capped_and_average_argv()
    {
        List<string> capped = [];
        VideoCompileStepExecutor.AddOutputVideoArgs(capped, "libx264", 20, "veryfast", rateControl: TimelineRenderRateControl.Capped, videoBitrateKbps: 6000);
        capped.Should().Equal("-c:v", "libx264", "-crf", "20", "-maxrate", "6000k", "-bufsize", "12000k", "-preset", "veryfast",
            "-pix_fmt", "yuv420p", "-color_range", "tv", "-movflags", "+faststart");

        List<string> average = [];
        VideoCompileStepExecutor.AddOutputVideoArgs(average, "libx265", 20, "fast", rateControl: TimelineRenderRateControl.Average, videoBitrateKbps: 2000);
        average.Should().Equal("-c:v", "libx265", "-b:v", "2000k", "-maxrate", "3000k", "-bufsize", "4000k", "-preset", "fast",
            "-pix_fmt", "yuv420p", "-color_range", "tv", "-tag:v", "hvc1", "-movflags", "+faststart");
    }

    [Fact]
    public void VideoCompile_step_cache_key_is_unchanged_for_existing_configs()
    {
        VideoCompileStepConfig config = new(1, new ExtractInputRef(ExtractInputSource.Previous), 1);
        string json = JsonSerializer.Serialize(config);
        json.Should().NotContain("RateControl").And.NotContain("VideoBitrateKbps");
        JsonSerializer.Serialize(config with { RateControl = TimelineRenderRateControl.Average, VideoBitrateKbps = 3000 }).Should().Contain("VideoBitrateKbps");
    }

    [Theory]
    [InlineData(TimelineRenderRateControl.Quality, 3000, "VideoBitrateKbps can only be set when RateControl is Capped or Average.")]
    [InlineData(TimelineRenderRateControl.Average, null, "VideoBitrateKbps is required when RateControl is Average.")]
    [InlineData(TimelineRenderRateControl.Capped, 50, "VideoBitrateKbps must be between 200 and 100000; got 50.")]
    [InlineData(null, 3000, "VideoBitrateKbps needs a RateControl of Capped or Average.")]
    public void VideoCompile_rate_control_is_validated(TimelineRenderRateControl? rc, int? kbps, string expected)
    {
        new VideoCompileStepConfig(1, new ExtractInputRef(ExtractInputSource.Previous), 1, RateControl: rc, VideoBitrateKbps: kbps).Validate().Should().Contain(expected);
        new VideoCompileStepConfig(1, new ExtractInputRef(ExtractInputSource.Previous), 1).Validate().Should().NotContain(e => e.Contains("VideoBitrateKbps"));
    }

    // ── Save paths: a bad value is refused ───────────────────────────────────

    [Fact]
    public void The_workflow_save_validator_refuses_a_bad_bitrate_on_a_timeline_render_step()
    {
        string config = JsonSerializer.Serialize(new TimelineRenderStepConfig(Guid.NewGuid(),
            Encode: new TimelineRenderEncodeSettings(RateControl: TimelineRenderRateControl.Average, VideoBitrateKbps: 999999)),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        JsonObject step = new()
        {
            ["agentDefinitionId"] = Guid.NewGuid().ToString(),
            ["stepOrder"] = 1,
            ["label"] = "Render",
            ["stepType"] = nameof(StepType.TimelineRender),
            ["timelineRenderConfigJson"] = config
        };
        CreateWorkflowStepRequest request = JsonSerializer.Deserialize<CreateWorkflowStepRequest>(step.ToJsonString(), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        StepConfigSaveValidator.Validate([request]).Should().Contain(e => e.Contains("encode.videoBitrateKbps must be between 200 and 100000, found 999999."));
    }

    [Theory]
    [InlineData("{\"source\":{\"kind\":\"Previous\"},\"rateControl\":\"Average\",\"videoBitrateKbps\":999999}", "VideoBitrateKbps must be between 200 and 100000")]
    [InlineData("{\"source\":{\"kind\":\"Previous\"},\"rateControl\":\"Average\"}", "VideoBitrateKbps is required when RateControl is Average.")]
    [InlineData("{\"source\":{\"kind\":\"Previous\"},\"videoBitrateKbps\":3000}", "VideoBitrateKbps needs a RateControl of Capped or Average.")]
    public void The_workflow_save_validator_refuses_a_bad_rate_control_on_a_video_compile_step(string configJson, string expected)
    {
        CreateWorkflowStepRequest request = new(Guid.NewGuid(), 3, null, null, StepType: "VideoCompile", VideoCompileConfigJson: configJson);

        StepConfigSaveValidator.ValidateStep(request).Should().Contain(e => e.Contains(expected));
    }

    // ── Size estimate ────────────────────────────────────────────────────────

    [Fact]
    public void Average_size_is_video_plus_audio_over_the_length_with_a_container_allowance()
    {
        (long bytes, bool approx)? size = RenderSizeEstimator.Estimate(60, 1920, 1080, 30, false, 18, TimelineRenderVideoCodec.H264,
            TimelineRenderEncoderSpeed.Medium, 192, TimelineRenderRateControl.Average, 4000);
        size.Should().NotBeNull();
        size!.Value.approx.Should().BeTrue("single-pass Average lands within about 10-15% of the target");
        size.Value.bytes.Should().Be((long)Math.Round(((4000 + 192) * 1000.0 * 60 / 8) * 1.02));
    }

    [Fact]
    public void Capped_size_never_exceeds_its_ceiling_and_quality_is_approximate()
    {
        (long bytes, bool approx) quality = RenderSizeEstimator.Estimate(60, 1920, 1080, 30, false, 18, TimelineRenderVideoCodec.H264,
            TimelineRenderEncoderSpeed.Medium, 192, TimelineRenderRateControl.Quality, null)!.Value;
        (long bytes, bool approx) capped = RenderSizeEstimator.Estimate(60, 1920, 1080, 30, false, 18, TimelineRenderVideoCodec.H264,
            TimelineRenderEncoderSpeed.Medium, 192, TimelineRenderRateControl.Capped, 1000)!.Value;
        quality.approx.Should().BeTrue();
        capped.bytes.Should().BeLessThan(quality.bytes);
        capped.bytes.Should().BeLessThanOrEqualTo((long)Math.Round(((1000 + 192) * 1000.0 * 60 / 8) * 1.02));
    }

    [Fact]
    public void Size_estimate_has_no_answer_for_an_empty_timeline_and_audio_only_is_just_audio()
    {
        RenderSizeEstimator.Estimate(0, 1920, 1080, 30, false, 18, TimelineRenderVideoCodec.H264, TimelineRenderEncoderSpeed.Medium, 192,
            TimelineRenderRateControl.Quality, null).Should().BeNull();
        RenderSizeEstimator.Estimate(10, 0, 0, 30, true, 18, TimelineRenderVideoCodec.H264, TimelineRenderEncoderSpeed.Medium, 192,
            TimelineRenderRateControl.Quality, null)!.Value.Bytes.Should().Be((long)Math.Round(192 * 1000.0 * 10 / 8 * 1.02));
    }

    [Fact]
    public void The_estimate_response_is_additive_so_credit_fields_are_untouched()
    {
        EditTimelineRenderEstimateResponse legacy = new(true, 1.5, 1.5, 1.5, 20, 100, true, null);
        legacy.EstimatedSizeBytes.Should().BeNull();
        legacy.SizeIsApproximate.Should().BeNull();
        (legacy.Credits, legacy.CreditsLow, legacy.CreditsHigh, legacy.EstimateSeconds, legacy.Affordable).Should().Be((1.5, 1.5, 1.5, 20.0, true));
    }

    // ── Measured bitrate (needs ffmpeg and ffprobe) ──────────────────────────

    [Theory]
    [InlineData("libx264")]
    [InlineData("libx265")]
    public void A_real_encode_lands_near_the_average_target_and_under_the_cap(string encoder)
    {
        // Silently skipped (reported as passed) when ffmpeg or ffprobe is not installed.
        if (!OnPath("ffmpeg") || !OnPath("ffprobe")) return;

        string dir = Path.Combine(Path.GetTempPath(), "bitrate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string tag = encoder == "libx265" ? "hvc1" : "avc1";
            double average = Measure(dir, encoder, tag, ["-b:v", "1500k", "-maxrate", "2250k", "-bufsize", "3000k"], "avg");
            average.Should().BeInRange(1500 * 1000 * 0.6, 1500 * 1000 * 1.4, "single-pass average lands near its target");

            double capped = Measure(dir, encoder, tag, ["-crf", "18", "-maxrate", "800k", "-bufsize", "1600k"], "cap");
            capped.Should().BeLessThan(800 * 1000 * 1.35, "a capped encode stays near or under its ceiling");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private static double Measure(string dir, string encoder, string tag, string[] rate, string name)
    {
        string output = Path.Combine(dir, name + ".mp4");
        List<string> args = ["-y", "-nostdin", "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=30:duration=8", "-c:v", encoder, "-preset", "veryfast"];
        args.AddRange(rate);
        args.AddRange(["-pix_fmt", "yuv420p", "-an", output]);
        Run("ffmpeg", args);
        string stdout = Run("ffprobe", ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=bit_rate:format=bit_rate", "-of", "default=nw=1:nk=1", output]);
        return stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => double.TryParse(l.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0).First(v => v > 0);
    }

    private static string Run(string exe, List<string> args)
    {
        ProcessStartInfo info = new(exe) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string a in args) info.ArgumentList.Add(a);
        using Process process = Process.Start(info)!;
        string stdout = process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        process.ExitCode.Should().Be(0, $"{exe} should succeed");
        return stdout;
    }

    private static bool OnPath(string exe) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Any(d => File.Exists(Path.Combine(d, exe)));
}
