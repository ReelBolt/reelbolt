using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.Shared.Workflows.VideoEvents;
using ReelBolt.WorkflowEngine.Services.Video;
using ReelBolt.WorkflowEngine.Services.Video.Events;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Video.Events;

/// <summary>Merge/ordering/dedup/cap, speech edges, the beat detector and the sampling rate plan.</summary>
public class EventSupportTests
{
    private static VideoEvent Event(VideoEventType type, int frame, double strength, VideoEventRegion? region = null, int? end = null) =>
        new(type, frame, end, region, strength, VideoEventOrigin.Pixels, 0.5);

    [Fact]
    public void Merge_orders_by_frame_and_keeps_the_stronger_of_two_duplicates()
    {
        (List<VideoEvent> events, int suppressed) = VideoEventMerger.Merge(
            [
                Event(VideoEventType.Hit, 80, 0.4),
                Event(VideoEventType.Appear, 10, 0.5, end: 14),
                Event(VideoEventType.Appear, 12, 0.9, end: 20),
                Event(VideoEventType.Hit, 30, 0.2)
            ],
            frameCount: 100, maxEvents: 400);

        suppressed.Should().Be(0);
        events.Select(e => (e.Type, e.Frame)).Should().Equal(
            (VideoEventType.Appear, 12), (VideoEventType.Hit, 30), (VideoEventType.Hit, 80));
        events[0].Strength.Should().Be(0.9);
        events[0].EndFrame.Should().Be(20);
    }

    [Fact]
    public void Merge_keeps_events_of_different_types_or_far_apart_regions_separate()
    {
        VideoEventRegion left = new(0, 0, 0.2, 0.2), right = new(0.7, 0.7, 0.2, 0.2);
        (List<VideoEvent> events, _) = VideoEventMerger.Merge(
            [Event(VideoEventType.Appear, 10, 0.5, left), Event(VideoEventType.Appear, 11, 0.5, right), Event(VideoEventType.Disappear, 10, 0.5, left)],
            100, 400);

        events.Should().HaveCount(3);
    }

    [Fact]
    public void Merge_drops_events_outside_the_video_and_clamps_strength()
    {
        (List<VideoEvent> events, _) = VideoEventMerger.Merge(
            [Event(VideoEventType.Hit, -1, 0.5), Event(VideoEventType.Hit, 100, 0.5), Event(VideoEventType.Hit, 99, 7.0)], 100, 400);

        events.Should().ContainSingle().Which.Strength.Should().Be(1.0);
    }

    [Fact]
    public void Merge_caps_at_MaxEvents_keeps_the_strongest_and_reports_the_suppressed_count()
    {
        List<VideoEvent> many = Enumerable.Range(0, 450)
            .Select(i => Event(VideoEventType.Hit, i * 10, i / 450.0))
            .ToList();

        (List<VideoEvent> events, int suppressed) = VideoEventMerger.Merge(many, frameCount: 5000, maxEvents: 400);

        events.Should().HaveCount(400);
        suppressed.Should().Be(50);
        events.Min(e => e.Strength).Should().BeGreaterThanOrEqualTo(50 / 450.0 - 0.001);
        events.Select(e => e.Frame).Should().BeInAscendingOrder();
    }

    [Fact]
    public void Speech_edges_come_from_the_silence_gaps_and_skip_the_edges_of_the_file()
    {
        List<VideoEvent> events = VideoEventDetector.SpeechEdges(
            [(0.0, 0.5), (2.0, 2.6), (5.0, 6.0)], durationSec: 6.0, fps: 30).ToList();

        events.Select(e => (e.Type, e.Frame)).Should().BeEquivalentTo(
            [(VideoEventType.SpeechStart, 15), (VideoEventType.SpeechEnd, 60), (VideoEventType.SpeechStart, 78), (VideoEventType.SpeechEnd, 150)]);
        events.Should().OnlyContain(e => e.Origin == VideoEventOrigin.Audio);
    }

    [Theory]
    [InlineData(30, 30, 1)]
    [InlineData(60, 60, 1)]
    [InlineData(120, 60, 2)]
    [InlineData(240, 60, 4)]
    [InlineData(144, 48, 3)]
    public void Sampling_rate_is_capped_at_60_by_an_integer_divisor(double source, double expectedFps, int expectedDivisor)
    {
        (double fps, int divisor) = FrameSignalSampler.PlanRate(source);

        divisor.Should().Be(expectedDivisor);
        fps.Should().BeApproximately(expectedFps, 1e-9);
    }

    [Fact]
    public void Sampler_argv_scales_to_160x90_gray_without_resampling_and_caps_frames_and_bytes()
    {
        string[] args = FrameSignalSampler.BuildArgs("in.mp4", "out.raw", 1, 2, 500);

        string filter = args[Array.IndexOf(args, "-vf") + 1];
        filter.Should().Be("scale=160:90:flags=area,format=gray");
        args.Should().ContainInOrder("-fps_mode", "passthrough");
        args.Should().ContainInOrder("-f", "rawvideo", "-pix_fmt", "gray");
        args[Array.IndexOf(args, "-frames:v") + 1].Should().Be("500");
        args[Array.IndexOf(args, "-fs") + 1].Should().Be((500 * 14400).ToString());
        args[Array.IndexOf(args, "-format_whitelist") + 1].Should().Be(MediaDerivativeInputFormats.Whitelist);
    }

    [Fact]
    public void Beat_pcm_decode_argv_is_held_to_the_demuxer_whitelist()
    {
        string[] args = VideoEventDetector.BuildBeatPcmArgs("in.mp4", "out.pcm", 2);

        args[Array.IndexOf(args, "-format_whitelist") + 1].Should().Be(MediaDerivativeInputFormats.Whitelist);
        Array.IndexOf(args, "-format_whitelist").Should().BeLessThan(Array.IndexOf(args, "-i"));
    }

    [Fact]
    public void Sampler_keeps_every_nth_decoded_frame_above_60_fps()
    {
        string[] args = FrameSignalSampler.BuildArgs("in.mp4", "out.raw", 4, 2, 500);

        args[Array.IndexOf(args, "-vf") + 1].Should().Be("select='not(mod(n,4))',scale=160:90:flags=area,format=gray");
    }

    [Fact]
    public void Variable_rate_sources_are_retimed_with_the_fps_filter_and_keep_every_bound()
    {
        string[] args = FrameSignalSampler.BuildArgs("in.mkv", "out.raw", 2, 2, 700, 120, 1);

        args[Array.IndexOf(args, "-vf") + 1].Should().Be("fps=120/2,scale=160:90:flags=area,format=gray");
        args.Should().ContainInOrder("-fps_mode", "cfr");
        args[Array.IndexOf(args, "-frames:v") + 1].Should().Be("700");
        args[Array.IndexOf(args, "-fs") + 1].Should().Be((700L * 14400).ToString());
    }

    [Theory]
    [InlineData(30, 30, false)]
    [InlineData(29.97, 29.97, false)]
    [InlineData(29.95, 29.97, false)]
    [InlineData(29.5, 30, true)]
    [InlineData(28, 30, true)]
    [InlineData(0, 30, true)]
    public void Variable_frame_rate_is_an_average_rate_apart_from_the_declared_one(double avg, double declared, bool vfr) =>
        FrameSignalSampler.IsVariableFrameRate(avg, declared).Should().Be(vfr);

    [Theory]
    [InlineData(1.5e17)]
    [InlineData(double.MaxValue)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NaN)]
    [InlineData(-5)]
    [InlineData(0)]
    public void A_forged_or_missing_duration_still_gives_a_positive_bounded_frame_cap(double duration)
    {
        long cap = FrameSignalSampler.PlanMaxFrames(duration, 60);

        cap.Should().BeInRange(1, (long)(6 * 3600 * 60 * 1.02) + 8);
        if (double.IsNaN(duration) || duration <= 0)
            cap.Should().BeLessThan(40_000, "a missing duration reserves for about ten minutes, not an hour");
    }

    [Fact]
    public void A_huge_duration_is_clamped_to_the_platform_limit()
    {
        FrameSignalSampler.PlanMaxFrames(1.5e17, 30, 3600).Should().Be(FrameSignalSampler.PlanMaxFrames(3600, 30, 3600));
    }

    [Fact]
    public void Frame_cap_is_the_duration_times_the_planned_rate_plus_a_small_margin()
    {
        FrameSignalSampler.PlanMaxFrames(10, 30).Should().BeInRange(300, 330);
        FrameSignalSampler.PlanMaxFrames(0, 30).Should().BeInRange(600 * 30, 700 * 30);
    }

    [Fact]
    public void Clicks_every_half_second_are_found_as_beats_within_60_ms()
    {
        const int rate = SpectralFluxBeatDetector.SampleRate;
        short[] samples = new short[rate * 4];
        Random noise = new(7);
        for (int i = 0; i < samples.Length; i++)
            samples[i] = (short)(noise.Next(-40, 41) + Math.Sin(2 * Math.PI * 440 * i / rate) * 1500); // a sustained tone bed: bare clicks with dead air between are not music
        double[] clicks = [0.5, 1.0, 1.5, 2.0, 2.5, 3.0, 3.5];
        foreach (double click in clicks)
        {
            int start = (int)(click * rate);
            for (int i = 0; i < rate / 30; i++)
                samples[start + i] += (short)(Math.Sin(2 * Math.PI * 1500 * i / rate) * 20000 * Math.Exp(-i / 60.0));
        }

        using MemoryStream pcm = new(samples.SelectMany(BitConverter.GetBytes).ToArray());
        var beats = SpectralFluxBeatDetector.Detect(pcm);

        foreach (double click in clicks)
            beats.Any(b => Math.Abs(b.TimeSec - click) <= 0.06).Should().BeTrue($"a beat near {click}s in [{string.Join(", ", beats.Select(b => b.TimeSec.ToString("F2")))}]");
        beats.Count(b => clicks.All(c => Math.Abs(b.TimeSec - c) > 0.1)).Should().BeLessThanOrEqualTo(1);
        beats.Should().OnlyContain(b => b.Strength >= 0 && b.Strength <= 1);
    }

    [Fact]
    public void Steady_noise_has_no_beats()
    {
        Random noise = new(11);
        short[] samples = Enumerable.Range(0, SpectralFluxBeatDetector.SampleRate * 3).Select(_ => (short)noise.Next(-3000, 3001)).ToArray();

        using MemoryStream pcm = new(samples.SelectMany(BitConverter.GetBytes).ToArray());

        SpectralFluxBeatDetector.Detect(pcm).Count.Should().BeLessThanOrEqualTo(2);
    }
}
