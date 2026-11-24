#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.IntegrationEvents;
using ReelBolt.WorkflowEngine.Consumers;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>Editor v2 Wave 2 (R09.1): the engine side of analysis on demand (deterministic kinds, transcript, digest cache).</summary>
public class MediaAnalysisEngineTests
{
    private static readonly MediaProbeResult VideoWithAudio = new(60, 30, 1, 1280, 720, "h264", "aac", 48000);
    private static readonly MediaProbeResult AudioOnly = new(60, 0, 1, 0, 0, null, "aac", 48000);
    private static readonly MediaProbeResult SilentVideo = new(60, 30, 1, 1280, 720, "h264", null, null);
    private static readonly MediaProbeResult LongAudio = new(2400, 0, 1, 0, 0, null, "aac", 48000);

    [Fact]
    public void Motion_samples_are_averaged_into_one_second_windows_and_classified()
    {
        string stdout = string.Join('\n',
            "frame:0    pts:0       pts_time:0", "lavfi.signalstats.YDIF=0.100",
            "frame:1    pts:15      pts_time:0.5", "lavfi.signalstats.YDIF=0.300",
            "frame:2    pts:30      pts_time:1", "lavfi.signalstats.YDIF=2.000",
            "frame:3    pts:45      pts_time:1.5", "lavfi.signalstats.YDIF=3.000",
            "frame:4    pts:60      pts_time:2", "lavfi.signalstats.YDIF=9.000");

        IReadOnlyList<AnalysisMotionWindow> windows = MotionSignalParser.BuildWindows(MotionSignalParser.ParseSamples(stdout));

        windows.Select(w => (w.StartSec, w.EndSec, w.Mean, w.MotionClass)).Should().Equal(
            (0d, 1d, 0.2, "still"), (1d, 2d, 2.5, "low"), (2d, 3d, 9d, "high"));
    }

    [Fact]
    public void Segment_only_transcripts_get_estimated_word_times_and_measured_words_stay_measured()
    {
        MediaAnalysisKindOutcome estimated = MediaAnalysisService.TranscriptOutcome(
            [new TranscriptSegment("ab cdef", 10, 17)], [], "en", 60);
        AnalysisTranscriptResult est = JsonSerializer.Deserialize<AnalysisTranscriptResult>(estimated.ResultJson!, MediaAnalysisJson.Options)!;
        est.Words.Select(w => (w.Text, w.StartSec, w.EndSec, w.Estimated)).Should().Equal(("ab", 10d, 12.333, true), ("cdef", 12.333, 17d, true));
        estimated.SummaryJson.Should().Contain("\"estimatedWordCount\":2").And.Contain("\"measuredWordCount\":0");
        estimated.MeasuredSeconds.Should().Be(60);

        MediaAnalysisKindOutcome measured = MediaAnalysisService.TranscriptOutcome(
            [new TranscriptSegment("hi there", 0, 1)], [new TranscriptWord("hi", 0.1, 0.4), new TranscriptWord("there", 0.5, 0.9)], null, 60);
        JsonSerializer.Deserialize<AnalysisTranscriptResult>(measured.ResultJson!, MediaAnalysisJson.Options)!.Words.Should().OnlyContain(w => !w.Estimated);
    }

    [Fact]
    public async Task Free_kinds_run_the_detectors_and_never_touch_the_transcription_provider()
    {
        Rig rig = new(VideoWithAudio);
        rig.Silence.Setup(s => s.DetectAsync(It.IsAny<string>(), It.IsAny<double>(), It.IsAny<double>(), It.IsAny<double?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([(1.0, 2.5), (10.0, 12.0)]);
        rig.Shots.Setup(s => s.DetectShotsAsync(It.IsAny<string>(), It.IsAny<double>(), It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([(0.0, 30.0), (30.0, 60.0)]);

        IReadOnlyList<MediaAnalysisKindOutcome> outcomes = await rig.Service.AnalyseAsync(
            "/tmp/x.mp4", ["silence", "shots"], null, null, null, default);

        outcomes.Should().OnlyContain(o => o.Ok && !o.FromCache);
        outcomes[0].SummaryJson.Should().Contain("\"rangeCount\":2").And.Contain("\"totalSeconds\":3.5");
        outcomes[1].SummaryJson.Should().Contain("\"shotCount\":2");
        rig.Providers.Verify(p => p.ResolveTranscriptionAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("audio", "shots", MediaAnalysisService.NoVideo)]
    [InlineData("audio", "motion", MediaAnalysisService.NoVideo)]
    [InlineData("silent", "silence", MediaAnalysisService.NoAudio)]
    [InlineData("silent", "transcript", MediaAnalysisService.NoAudio)]
    [InlineData("silent", "beats", MediaAnalysisService.NoAudio)]
    public async Task A_file_without_the_stream_fails_with_a_plain_sentence(string shape, string kind, string expected)
    {
        Rig rig = new(shape == "audio" ? AudioOnly : SilentVideo);
        MediaAnalysisKindOutcome outcome = (await rig.Service.AnalyseAsync("/tmp/x.mp4", [kind], null, null, null, default)).Single();
        outcome.Ok.Should().BeFalse();
        outcome.Reason.Should().Be(expected);
    }

    [Fact]
    public async Task The_events_kind_runs_the_detector_and_stores_a_bounded_list_with_the_step_view_ids_and_type_words_with_a_count_by_type()
    {
        Mock<ReelBolt.WorkflowEngine.Services.Video.Events.IVideoEventDetector> detector = new();
        detector.Setup(d => d.DetectAsync(It.IsAny<IMediaWorkspace>(), It.IsAny<string>(), It.IsAny<MediaProbeResult>(),
                It.IsAny<ReelBolt.WorkflowEngine.Services.Video.Events.VideoEventOptions?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReelBolt.Shared.Workflows.VideoEvents.VideoEventSet(30, 1800, 1280, 720,
            [
                new(ReelBolt.Shared.Workflows.VideoEvents.VideoEventType.Appear, 30, null, new(0.1, 0.2, 0.3, 0.4), 0.8, ReelBolt.Shared.Workflows.VideoEvents.VideoEventOrigin.Pixels, 0.9),
                new(ReelBolt.Shared.Workflows.VideoEvents.VideoEventType.SpeechStart, 60, null, null, 0.5, ReelBolt.Shared.Workflows.VideoEvents.VideoEventOrigin.Audio, 0.7),
                new(ReelBolt.Shared.Workflows.VideoEvents.VideoEventType.Appear, 90, 120, null, 0.6, ReelBolt.Shared.Workflows.VideoEvents.VideoEventOrigin.Pixels, 0.8, "left", "fast", "Graphic")
            ], 4));
        Rig rig = new(VideoWithAudio) { Events = detector.Object };

        MediaAnalysisKindOutcome outcome = (await rig.Service.AnalyseAsync("/tmp/x.mp4", ["events"], null, null, null, default)).Single();

        outcome.Ok.Should().BeTrue();
        outcome.ResultJson.Should().Contain("\"type\":\"appear\"").And.Contain("\"type\":\"speech_start\"").And.Contain("\"timeSec\":1").And.Contain("\"id\":\"e2\"").And.Contain("\"suppressed\":4");
        outcome.SummaryJson.Should().Contain("\"eventCount\":3").And.Contain("\"appear\":2");
        rig.Providers.Verify(p => p.ResolveTranscriptionAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Never);

        // What the detector measured beside the frame is stored when it gave it, and left out when it did not.
        AnalysisEventsResult stored = System.Text.Json.JsonSerializer.Deserialize<AnalysisEventsResult>(outcome.ResultJson!, MediaAnalysisJson.Options)!;
        AnalysisEvent last = stored.Events[2];
        (last.EndFrame, last.Direction, last.Speed, last.Kind).Should().Be((120, "left", "fast", "graphic"));
        (stored.Events[0].EndFrame, stored.Events[0].Direction, stored.Events[0].Speed, stored.Events[0].Kind).Should().Be((null, null, null, null));
        outcome.ResultJson.Should().NotContain("\"what\"", "the plain words are worked out when the result is read");
        stored.Version.Should().Be(MediaAnalysisJson.EventsVersion);
        MediaAnalysisJson.EventsAreCurrent(outcome.ResultJson).Should().BeTrue();
        MediaAnalysisJson.EventsAreCurrent(outcome.SummaryJson).Should().BeTrue();
    }

    [Fact]
    public async Task An_incomplete_events_detection_is_stored_marked_degraded_and_is_not_current()
    {
        Mock<ReelBolt.WorkflowEngine.Services.Video.Events.IVideoEventDetector> detector = new();
        detector.Setup(d => d.DetectAsync(It.IsAny<IMediaWorkspace>(), It.IsAny<string>(), It.IsAny<MediaProbeResult>(),
                It.IsAny<ReelBolt.WorkflowEngine.Services.Video.Events.VideoEventOptions?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReelBolt.Shared.Workflows.VideoEvents.VideoEventSet(30, 1800, 1280, 720,
            [
                new(ReelBolt.Shared.Workflows.VideoEvents.VideoEventType.Appear, 30, null, null, 0.8, ReelBolt.Shared.Workflows.VideoEvents.VideoEventOrigin.Pixels, 0.9)
            ], 0)
            { Degraded = true });
        Rig rig = new(VideoWithAudio) { Events = detector.Object };

        MediaAnalysisKindOutcome outcome = (await rig.Service.AnalyseAsync("/tmp/x.mp4", ["events"], null, null, null, default)).Single();

        // The marks it found are kept and shown, so the outcome is a result, not a failure.
        outcome.Ok.Should().BeTrue();
        outcome.ResultJson.Should().Contain("\"type\":\"appear\"").And.Contain("\"degraded\":true");
        outcome.SummaryJson.Should().Contain("\"eventCount\":1").And.Contain("\"degraded\":true");
        // It is of this version, yet it reads like an outdated row everywhere the version is checked.
        MediaAnalysisJson.EventsAreThisVersion(outcome.ResultJson).Should().BeTrue();
        MediaAnalysisJson.EventsAreThisVersion(outcome.SummaryJson).Should().BeTrue();
        MediaAnalysisJson.EventsAreCurrent(outcome.ResultJson).Should().BeFalse();
        MediaAnalysisJson.EventsAreCurrent(outcome.SummaryJson).Should().BeFalse();
    }

    [Fact]
    public async Task A_cached_events_result_marked_degraded_is_not_a_hit()
    {
        string bytes = "the-same-video";
        string digest = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(bytes)));
        Guid org = Guid.NewGuid();
        using ConsumerRig rig = new(bytes);
        rig.Seed(new MediaAnalysisResult
        {
            Id = Guid.NewGuid(),
            OrganizationId = org,
            Digest = digest,
            Kind = "events",
            ResultJson = $$"""{"fps":30,"frameCount":90,"events":[],"suppressed":0,"truncated":false,"version":"{{MediaAnalysisJson.EventsVersion}}","degraded":true}""",
            SummaryJson = $$"""{"eventCount":0,"version":"{{MediaAnalysisJson.EventsVersion}}","degraded":true}"""
        });

        // The rig has no detector, so a run that is not served from the cache fails in plain words: proof the kind was run again.
        MediaAnalysisKindOutcome rerun = (await rig.RunAsync(org, ["events"])).Outcomes.Single();
        (rerun.FromCache, rerun.Ok).Should().Be((false, false));
    }

    [Fact]
    public async Task A_cached_events_result_of_another_detector_version_is_not_a_hit_and_a_current_one_is()
    {
        string bytes = "the-same-video";
        string digest = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(bytes)));
        Guid stale = Guid.NewGuid(), current = Guid.NewGuid();
        using ConsumerRig rig = new(bytes);
        rig.Seed(new MediaAnalysisResult { Id = Guid.NewGuid(), OrganizationId = stale, Digest = digest, Kind = "events", ResultJson = """{"fps":30,"frameCount":90,"events":[],"suppressed":0,"truncated":false}""", SummaryJson = """{"eventCount":0}""" });
        rig.Seed(new MediaAnalysisResult { Id = Guid.NewGuid(), OrganizationId = current, Digest = digest, Kind = "events", ResultJson = $$"""{"fps":30,"frameCount":90,"events":[],"suppressed":0,"truncated":false,"version":"{{MediaAnalysisJson.EventsVersion}}"}""", SummaryJson = "{}" });

        // The rig has no detector, so a run that is not served from the cache fails in plain words: proof the kind was run again.
        MediaAnalysisKindOutcome rerun = (await rig.RunAsync(stale, ["events"])).Outcomes.Single();
        (rerun.FromCache, rerun.Ok).Should().Be((false, false));

        MediaAnalysisKindOutcome hit = (await rig.RunAsync(current, ["events"])).Outcomes.Single();
        (hit.FromCache, hit.Ok).Should().Be((true, true));
    }

    [Fact]
    public async Task The_events_kind_on_an_audio_only_file_fails_with_a_plain_sentence()
    {
        Rig rig = new(AudioOnly);
        MediaAnalysisKindOutcome outcome = (await rig.Service.AnalyseAsync("/tmp/x.mp4", ["events"], null, null, null, default)).Single();
        outcome.Reason.Should().Be(MediaAnalysisService.NoVideo);
    }

    [Fact]
    public async Task Failures_are_plain_sentences_never_exception_text()
    {
        Rig unreadable = new(null);
        (await unreadable.Service.AnalyseAsync("/tmp/x.mp4", ["silence", "transcript"], null, null, null, default))
            .Should().OnlyContain(o => !o.Ok && o.Reason == MediaAnalysisService.CouldNotRead);

        Rig boom = new(VideoWithAudio);
        boom.Silence.Setup(s => s.DetectAsync(It.IsAny<string>(), It.IsAny<double>(), It.IsAny<double>(), It.IsAny<double?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("ffmpeg exploded at /var/tmp/secret/path"));
        MediaAnalysisKindOutcome silence = (await boom.Service.AnalyseAsync("/tmp/x.mp4", ["silence"], null, null, null, default)).Single();
        silence.Reason.Should().Be(MediaAnalysisService.Generic).And.NotContain("secret");

        Rig noAsr = new(VideoWithAudio) { ProviderResolved = false };
        (await noAsr.Service.AnalyseAsync("/tmp/x.mp4", ["transcript"], null, null, null, default)).Single().Reason.Should().Be(MediaAnalysisService.NoProvider);
    }

    [Fact]
    public async Task A_transcription_service_failure_is_a_plain_not_charged_message()
    {
        string dir = Directory.CreateTempSubdirectory("analysis-test").FullName;
        try
        {
            Rig rig = new(VideoWithAudio);
            rig.Audio.Setup(a => a.ExtractWavAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns<string, string, CancellationToken>((_, wav, _) => { File.WriteAllBytes(wav, new byte[1000]); return Task.CompletedTask; });
            rig.Client.Setup(c => c.TranscribeAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new HttpRequestException("401 from api.vendor.example with key sk-secret"));

            MediaAnalysisKindOutcome outcome = (await rig.Service.AnalyseAsync(Path.Combine(dir, "s.mp4"), ["silence", "transcript"], "en", null, null, default))[1];
            outcome.Ok.Should().BeFalse();
            outcome.Reason.Should().Be(MediaAnalysisService.TranscriptionFailed).And.NotContain("sk-secret");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task A_transcript_run_calls_the_provider_with_word_timestamps_and_the_language()
    {
        string dir = Directory.CreateTempSubdirectory("analysis-test").FullName;
        try
        {
            Rig rig = new(VideoWithAudio);
            rig.Audio.Setup(a => a.ExtractWavAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns<string, string, CancellationToken>((_, wav, _) => { File.WriteAllBytes(wav, new byte[1000]); return Task.CompletedTask; });
            rig.Client.Setup(c => c.TranscribeAsync(It.IsAny<Stream>(), It.IsAny<string>(), "fr", true, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new TranscriptResult("salut", [new TranscriptSegment("salut", 0, 1)], [new TranscriptWord("salut", 0.2, 0.8)], "fr"));

            MediaAnalysisKindOutcome outcome = (await rig.Service.AnalyseAsync(Path.Combine(dir, "s.mp4"), ["transcript"], "fr", null, null, default)).Single();
            outcome.Ok.Should().BeTrue();
            outcome.MeasuredSeconds.Should().Be(60);
            outcome.SummaryJson.Should().Contain("\"wordCount\":1").And.Contain("\"language\":\"fr\"");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task A_source_whose_header_under_reports_its_duration_is_still_extracted_only_up_to_the_hold()
    {
        string dir = Directory.CreateTempSubdirectory("analysis-test").FullName;
        try
        {
            Rig rig = new(VideoWithAudio); // probed 60 s, held 90 s: the untrimmed path used to run with no -t
            double? clip = null;
            rig.Audio.Setup(a => a.ExtractWavRangeAsync(It.IsAny<string>(), It.IsAny<string>(), 0, It.IsAny<double>(), It.IsAny<CancellationToken>()))
                .Returns<string, string, double, double, CancellationToken>((_, wav, _, end, _) => { clip = end; File.WriteAllBytes(wav, new byte[1000]); return Task.CompletedTask; });
            rig.Client.Setup(c => c.TranscribeAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string?>(), true, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new TranscriptResult("hi", [], [new TranscriptWord("hi", 0.1, 0.4)], null));

            await rig.Service.AnalyseAsync(Path.Combine(dir, "s.mp4"), ["transcript"], null, 90, null, default);

            clip.Should().Be(90, "extraction is always bounded by the held seconds");
            rig.Audio.Verify(a => a.ExtractWavAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task A_source_whose_audio_is_longer_than_the_held_seconds_is_transcribed_only_up_to_the_hold()
    {
        string dir = Directory.CreateTempSubdirectory("analysis-test").FullName;
        try
        {
            Rig rig = new(VideoWithAudio); // probed 60 s
            rig.Audio.Setup(a => a.ExtractWavRangeAsync(It.IsAny<string>(), It.IsAny<string>(), 0, 20, It.IsAny<CancellationToken>()))
                .Returns<string, string, double, double, CancellationToken>((_, wav, _, _, _) => { File.WriteAllBytes(wav, new byte[1000]); return Task.CompletedTask; });
            rig.Client.Setup(c => c.TranscribeAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string?>(), true, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new TranscriptResult("hi", [], [new TranscriptWord("hi", 0.1, 0.4)], null));

            MediaAnalysisKindOutcome outcome = (await rig.Service.AnalyseAsync(Path.Combine(dir, "s.mp4"), ["transcript"], null, 20, null, default)).Single();
            outcome.Ok.Should().BeTrue();
            outcome.MeasuredSeconds.Should().Be(20, "billed seconds never exceed the hold");
            rig.Audio.Verify(a => a.ExtractWavAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Theory]
    [InlineData(0L, false, 4_000L)]
    [InlineData(null, false, 4_000L)]
    [InlineData(1_000L, true, 4_000L)]
    [InlineData(1_000L, false, 1_000L)]
    [InlineData(6_000L, true, 6_000L)]
    public void A_produced_transcript_never_settles_free_when_unmetered_or_unpriced(long? actual, bool unpriced, long expected)
    {
        MediaAnalysisKindOutcome o = new("transcript", true, "{}", "{}", null, 60, false, actual, unpriced);
        ReelBolt.Inference.Api.Consumers.MediaAnalysisFinishedConsumer.SettleAmount(o, 4_000).Should().Be(expected);
    }

    [Fact]
    public async Task The_same_bytes_in_the_same_organization_reuse_the_cached_result_and_another_organization_does_not()
    {
        string bytes = "the-same-bytes";
        string digest = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(bytes)));
        Guid org = Guid.NewGuid();
        using ConsumerRig rig = new(bytes);
        rig.Seed(new MediaAnalysisResult
        {
            Id = Guid.NewGuid(),
            OrganizationId = org,
            Digest = digest,
            Kind = "silence",
            ResultJson = """{"ranges":[]}""",
            SummaryJson = """{"rangeCount":0}"""
        });

        MediaAnalysisFinished hit = await rig.RunAsync(org, ["silence", "shots"]);
        hit.Digest.Should().Be(digest);
        hit.Outcomes.Single(o => o.Kind == "silence").FromCache.Should().BeTrue();
        hit.Outcomes.Single(o => o.Kind == "shots").FromCache.Should().BeFalse();
        rig.SilenceCalls.Should().Be(0, "the cached kind was not run");
        rig.ShotCalls.Should().Be(1);

        MediaAnalysisFinished other = await rig.RunAsync(Guid.NewGuid(), ["silence"]);
        other.Outcomes.Single().FromCache.Should().BeFalse("results are never shared across organizations");
        rig.SilenceCalls.Should().Be(1);
    }

    [Fact]
    public async Task An_unreadable_download_answers_every_kind_failed_in_plain_words_and_still_finishes()
    {
        using ConsumerRig rig = new("x") { DownloadFails = true };
        MediaAnalysisFinished finished = await rig.RunAsync(Guid.NewGuid(), ["silence", "transcript"]);
        finished.Outcomes.Should().OnlyContain(o => !o.Ok && o.Reason == MediaAnalysisService.CouldNotRead);
    }

    [Fact]
    public async Task Beats_tracks_the_grid_and_reports_a_small_summary()
    {
        string dir = Directory.CreateTempSubdirectory("analysis-test").FullName;
        try
        {
            Rig rig = new(VideoWithAudio);
            rig.Audio.Setup(a => a.ExtractWavRangeAsync(It.IsAny<string>(), It.IsAny<string>(), 0, MediaAnalysisService.MaxBeatSeconds, It.IsAny<CancellationToken>()))
                .Returns<string, string, double, double, CancellationToken>((_, wav, _, _, _) =>
                {
                    File.WriteAllBytes(wav, Wav(MusicBeatAnalyzerTests.ClickTrack(120, 0.1, 20), 16000));
                    return Task.CompletedTask;
                });

            MediaAnalysisKindOutcome outcome = (await rig.Service.AnalyseAsync(
                Path.Combine(dir, "song.mp4"), ["beats"], null, null, null, default)).Single();

            outcome.Ok.Should().BeTrue();
            outcome.MeasuredSeconds.Should().BeNull("beats is a free kind");
            AnalysisBeatsResult result = JsonSerializer.Deserialize<AnalysisBeatsResult>(outcome.ResultJson!, MediaAnalysisJson.Options)!;
            result.Bpm.Should().BeApproximately(120, 0.5);
            result.BeatTimesSec.Should().NotBeEmpty();
            result.BeatsPerBar.Should().Be(4);
            result.AnalysedSec.Should().BeApproximately(20, 0.001);
            result.DurationSec.Should().Be(60);
            result.Truncated.Should().BeFalse();
            outcome.SummaryJson.Should().Contain("\"beatCount\"").And.Contain("\"tempoWord\"").And.Contain("\"bpm\"").And.Contain("\"confidence\"");
            rig.Audio.Verify(a => a.ExtractWavAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Beats_on_a_silent_track_fails_with_a_plain_no_beat_sentence()
    {
        string dir = Directory.CreateTempSubdirectory("analysis-test").FullName;
        try
        {
            Rig rig = new(VideoWithAudio);
            rig.Audio.Setup(a => a.ExtractWavRangeAsync(It.IsAny<string>(), It.IsAny<string>(), 0, MediaAnalysisService.MaxBeatSeconds, It.IsAny<CancellationToken>()))
                .Returns<string, string, double, double, CancellationToken>((_, wav, _, _, _) =>
                {
                    File.WriteAllBytes(wav, Wav(new float[16000 * 20], 16000));
                    return Task.CompletedTask;
                });

            MediaAnalysisKindOutcome outcome = (await rig.Service.AnalyseAsync(
                Path.Combine(dir, "silence.mp4"), ["beats"], null, null, null, default)).Single();

            outcome.Ok.Should().BeFalse();
            outcome.Reason.Should().Be(MediaAnalysisService.NoBeat);
            outcome.ResultJson.Should().BeNull();
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Beats_with_an_unknown_length_still_decodes_at_most_1200_seconds()
    {
        string dir = Directory.CreateTempSubdirectory("analysis-test").FullName;
        try
        {
            Rig rig = new(VideoWithAudio with { DurationSec = 0 });
            double? clipEnd = null;
            rig.Audio.Setup(a => a.ExtractWavRangeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<double>(), It.IsAny<double>(), It.IsAny<CancellationToken>()))
                .Returns<string, string, double, double, CancellationToken>((_, wav, _, end, _) =>
                {
                    clipEnd = end;
                    File.WriteAllBytes(wav, Wav(MusicBeatAnalyzerTests.ClickTrack(120, 0.1, 20), 16000));
                    return Task.CompletedTask;
                });

            MediaAnalysisKindOutcome outcome = (await rig.Service.AnalyseAsync(
                Path.Combine(dir, "unknown.mp4"), ["beats"], null, null, null, default)).Single();

            outcome.Ok.Should().BeTrue();
            clipEnd.Should().Be(1200, "a file with no known length is never decoded whole");
            rig.Audio.Verify(a => a.ExtractWavAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
            AnalysisBeatsResult result = JsonSerializer.Deserialize<AnalysisBeatsResult>(outcome.ResultJson!, MediaAnalysisJson.Options)!;
            result.Truncated.Should().BeFalse("20 s were decoded, well under the cap");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Beats_never_trusts_a_short_probed_length_and_marks_a_capped_decode_truncated()
    {
        string dir = Directory.CreateTempSubdirectory("analysis-test").FullName;
        try
        {
            // The header claims 60 s; the decode (capped by the extractor at MaxBeatSeconds) runs the full cap.
            Rig rig = new(VideoWithAudio);
            double? clipEnd = null;
            rig.Audio.Setup(a => a.ExtractWavRangeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<double>(), It.IsAny<double>(), It.IsAny<CancellationToken>()))
                .Returns<string, string, double, double, CancellationToken>((_, wav, _, end, _) =>
                {
                    clipEnd = end;
                    File.WriteAllBytes(wav, Wav(MusicBeatAnalyzerTests.ClickTrack(120, 0.1, MediaAnalysisService.MaxBeatSeconds), 16000));
                    return Task.CompletedTask;
                });

            MediaAnalysisKindOutcome outcome = (await rig.Service.AnalyseAsync(
                Path.Combine(dir, "liar.mp4"), ["beats"], null, null, null, default)).Single();

            outcome.Ok.Should().BeTrue();
            clipEnd.Should().Be(MediaAnalysisService.MaxBeatSeconds, "the probed length is never used as the decode bound");
            rig.Audio.Verify(a => a.ExtractWavAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
            AnalysisBeatsResult result = JsonSerializer.Deserialize<AnalysisBeatsResult>(outcome.ResultJson!, MediaAnalysisJson.Options)!;
            result.AnalysedSec.Should().BeApproximately(MediaAnalysisService.MaxBeatSeconds, 1);
            result.Truncated.Should().BeTrue("the decoded audio itself reached the cap");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Beats_analyses_only_the_first_1200_seconds_and_marks_the_result_truncated()
    {
        string dir = Directory.CreateTempSubdirectory("analysis-test").FullName;
        try
        {
            Rig rig = new(LongAudio);
            double? clipStart = null, clipEnd = null;
            rig.Audio.Setup(a => a.ExtractWavRangeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<double>(), It.IsAny<double>(), It.IsAny<CancellationToken>()))
                .Returns<string, string, double, double, CancellationToken>((_, wav, start, end, _) =>
                {
                    clipStart = start;
                    clipEnd = end;
                    File.WriteAllBytes(wav, Wav(MusicBeatAnalyzerTests.ClickTrack(120, 0.1, 20), 16000));
                    return Task.CompletedTask;
                });

            MediaAnalysisKindOutcome outcome = (await rig.Service.AnalyseAsync(
                Path.Combine(dir, "long.mp4"), ["beats"], null, null, null, default)).Single();

            outcome.Ok.Should().BeTrue();
            clipStart.Should().Be(0);
            clipEnd.Should().Be(1200, "the beat grid is tracked only up to MaxBeatSeconds");
            rig.Audio.Verify(a => a.ExtractWavAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
            AnalysisBeatsResult result = JsonSerializer.Deserialize<AnalysisBeatsResult>(outcome.ResultJson!, MediaAnalysisJson.Options)!;
            result.DurationSec.Should().Be(2400);
            result.AnalysedSec.Should().BeApproximately(20, 0.001);
            result.Truncated.Should().BeTrue("the source was cut at 1200 s");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>A canonical 16-bit mono PCM RIFF/WAVE file the mocked extractor can hand back.</summary>
    private static byte[] Wav(float[] samples, int sampleRate)
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);
        int dataBytes = samples.Length * 2;
        writer.Write("RIFF"u8);
        writer.Write(36 + dataBytes);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(dataBytes);
        foreach (float sample in samples)
            writer.Write((short)Math.Clamp((int)Math.Round(sample * 32767), short.MinValue, short.MaxValue));
        writer.Flush();
        return stream.ToArray();
    }

    private sealed class Rig
    {
        public Mock<IMediaProbe> Probe { get; } = new();
        public Mock<ISilenceDetector> Silence { get; } = new();
        public Mock<IShotDetector> Shots { get; } = new();
        public Mock<IAudioExtractor> Audio { get; } = new();
        public Mock<IInferenceProviderResolver> Providers { get; } = new();
        public Mock<ITranscriptionClient> Client { get; } = new();
        public bool ProviderResolved { get; init; } = true;
        public ReelBolt.WorkflowEngine.Services.Video.Events.IVideoEventDetector? Events { get; init; }
        private readonly MediaProbeResult? _probe;

        public Rig(MediaProbeResult? probe)
        {
            _probe = probe;
            Silence.Setup(s => s.DetectAsync(It.IsAny<string>(), It.IsAny<double>(), It.IsAny<double>(), It.IsAny<double?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync([]);
        }

        public MediaAnalysisService Service
        {
            get
            {
                if (_probe is null)
                    Probe.Setup(p => p.ProbeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("not media"));
                else
                    Probe.Setup(p => p.ProbeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(_probe);
                Providers.Setup(p => p.ResolveTranscriptionAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(ProviderResolved ? new ResolvedTranscriptionProvider(Guid.NewGuid(), "asr", InferenceProviderKind.OpenAICompatible, "http://x", "m", "k", 60) : null);
                Mock<ITranscriptionClientFactory> factory = new();
                factory.Setup(f => f.Get(It.IsAny<ResolvedTranscriptionProvider>())).Returns(Client.Object);
                return new MediaAnalysisService(
                    Probe.Object, Silence.Object, Shots.Object, new Mock<IVideoToolRunner>().Object, Audio.Object,
                    Providers.Object, factory.Object, Options.Create(new VideoEditingOptions()), NullLogger<MediaAnalysisService>.Instance, Events);
            }
        }
    }

    private sealed class ConsumerRig : IDisposable
    {
        private readonly string _bytes;
        private readonly string _scratch = Directory.CreateTempSubdirectory("analysis-consumer").FullName;
        private readonly DbContextOptions<WorkflowEngineDbContext> _options = new DbContextOptionsBuilder<WorkflowEngineDbContext>()
            .UseInMemoryDatabase($"engine-analysis-{Guid.NewGuid()}").Options;
        public int SilenceCalls { get; private set; }
        public int ShotCalls { get; private set; }
        public bool DownloadFails { get; init; }

        public ConsumerRig(string bytes) => _bytes = bytes;

        public void Seed(MediaAnalysisResult row)
        {
            using WorkflowEngineDbContext db = new(_options);
            db.MediaAnalysisResults.Add(row);
            db.SaveChanges();
        }

        public async Task<MediaAnalysisFinished> RunAsync(Guid org, string[] kinds)
        {
            Mock<IProjectFileWorkspace> workspace = new();
            workspace.Setup(w => w.DownloadStorageKeyToFileAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns<Guid, string, string, CancellationToken>((_, _, path, _) =>
                    DownloadFails ? throw new IOException("storage down") : File.WriteAllTextAsync(path, _bytes));
            Mock<IMediaProbe> probe = new();
            probe.Setup(p => p.ProbeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(VideoWithAudio);
            Mock<ISilenceDetector> silence = new();
            silence.Setup(s => s.DetectAsync(It.IsAny<string>(), It.IsAny<double>(), It.IsAny<double>(), It.IsAny<double?>(), It.IsAny<CancellationToken>()))
                .Returns(() => { SilenceCalls++; return Task.FromResult<IReadOnlyList<(double, double)>>([(1, 2)]); });
            Mock<IShotDetector> shots = new();
            shots.Setup(s => s.DetectShotsAsync(It.IsAny<string>(), It.IsAny<double>(), It.IsAny<double>(), It.IsAny<CancellationToken>()))
                .Returns(() => { ShotCalls++; return Task.FromResult<IReadOnlyList<(double, double)>>([(0, 60)]); });
            MediaAnalysisService service = new(
                probe.Object, silence.Object, shots.Object, new Mock<IVideoToolRunner>().Object, new Mock<IAudioExtractor>().Object,
                new Mock<IInferenceProviderResolver>().Object, new Mock<ITranscriptionClientFactory>().Object,
                Options.Create(new VideoEditingOptions()), NullLogger<MediaAnalysisService>.Instance);
            Mock<IDbContextFactory<WorkflowEngineDbContext>> factory = new();
            factory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => new WorkflowEngineDbContext(_options));
            MediaAnalysisConsumer consumer = new(
                factory.Object, workspace.Object, service, new Mock<IMediaDerivativeProgressSink>().Object,
                Options.Create(new VideoEditingOptions { ScratchPath = _scratch }), NullLogger<MediaAnalysisConsumer>.Instance);

            MediaAnalysisFinished? finished = null;
            Mock<ConsumeContext<MediaAnalysisRequested>> context = new();
            context.SetupGet(c => c.Message).Returns(new MediaAnalysisRequested(Guid.NewGuid(), Guid.NewGuid(), org, null, "projects/p/userFiles/a.mp4", kinds, null));
            context.SetupGet(c => c.CancellationToken).Returns(CancellationToken.None);
            context.Setup(c => c.Publish(It.IsAny<MediaAnalysisFinished>(), It.IsAny<CancellationToken>()))
                .Callback<MediaAnalysisFinished, CancellationToken>((m, _) => finished = m).Returns(Task.CompletedTask);
            context.Setup(c => c.Publish(It.IsAny<MediaAnalysisStarted>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

            await consumer.Consume(context.Object);
            return finished!;
        }

        public void Dispose()
        {
            if (Directory.Exists(_scratch)) Directory.Delete(_scratch, true);
        }
    }
}
