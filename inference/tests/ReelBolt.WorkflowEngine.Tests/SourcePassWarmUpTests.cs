#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.IntegrationEvents;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.VideoEvents;
using ReelBolt.WorkflowEngine.Drain;
using ReelBolt.WorkflowEngine.Services.Video;
using ReelBolt.WorkflowEngine.Services.Video.Caching;
using ReelBolt.WorkflowEngine.Services.Video.Events;
using Xunit;
using static ReelBolt.WorkflowEngine.Tests.VideoAnalyzeEventsTests;
using InMemoryPassCache = ReelBolt.WorkflowEngine.Tests.VideoAnalyzePassCacheTests.InMemoryPassCache;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The pass cache warm-up of a derivative run (<see cref="SourcePassWarmUp"/>): it stores exactly the entries a
/// <c>VideoAnalyze</c> step with default settings reads (proved by running the step afterwards on the same cache and
/// counting zero passes), it skips what it must skip, it runs at low priority, and nothing it does can fail or
/// change the derivatives of the run it rides on.
/// </summary>
public class SourcePassWarmUpTests
{
    /// <summary>The probe every file of the step harness answers: half a minute of 1080p with sound.</summary>
    private static readonly MediaProbeResult Probe1080 = new(30, 30, 1, 1920, 1080, "h264", "aac", 48000);

    /// <summary>Long and above 1080p: a step analyses it through the small analysis copy.</summary>
    private static readonly MediaProbeResult LargeSource = new(300, 30, 1, 3840, 2160, "h264", "aac", 48000);

    /// <summary>
    /// The four passes as stubs that answer what the stubs of <see cref="VideoAnalyzeEventsTests.RunAsync"/> answer,
    /// so a step that reads the warmed cache must give the output of a step that ran the passes itself.
    /// </summary>
    private sealed class Passes
    {
        public PassCalls Calls { get; } = new();

        public StubDetector Detector { get; } = NewDetector();

        /// <summary>When set, the shot pass throws it.</summary>
        public Exception? ShotsFailure { get; set; }

        /// <summary>When set, the silence pass first runs it with the workspace it was handed.</summary>
        public Func<IMediaWorkspace, CancellationToken, Task>? OnSilence { get; set; }

        public int Total => Calls.Silence + Calls.Shots + Calls.Grid + Detector.Seen.Count;

        public SourcePassWarmUp Build(ISourcePassCache cache, IVideoToolRunner runner, VideoEditingOptions options)
        {
            var silence = new Mock<ISilenceDetector>();
            silence.Setup(s => s.DetectAsync(It.IsAny<IMediaWorkspace>(), It.IsAny<string>(), It.IsAny<double>(), It.IsAny<double>(), It.IsAny<double?>(), It.IsAny<CancellationToken>()))
                .Returns<IMediaWorkspace, string, double, double, double?, CancellationToken>(async (workspace, _, _, _, _, ct) =>
                {
                    Calls.Silence++;
                    if (OnSilence is not null)
                        await OnSilence(workspace, ct);
                    return new[] { (4.0, 5.5), (20.25, 21.0) };
                });
            var shots = new Mock<IShotDetector>();
            shots.Setup(s => s.DetectShotsAsync(It.IsAny<IMediaWorkspace>(), It.IsAny<string>(), It.IsAny<double>(), It.IsAny<double>(), It.IsAny<CancellationToken>()))
                .Returns<IMediaWorkspace, string, double, double, CancellationToken>((_, path, _, _, _) =>
                {
                    Calls.Shots++;
                    Calls.ShotPaths.Add(path);
                    return ShotsFailure is not null
                        ? Task.FromException<IReadOnlyList<(double StartSec, double EndSec)>>(ShotsFailure)
                        : Task.FromResult<IReadOnlyList<(double StartSec, double EndSec)>>(new[] { (0.0, 10.0), (10.0, 30.0) });
                });

            return new SourcePassWarmUp(
                silence.Object, shots.Object, new StubGridSampler(Calls), cache, runner,
                Options.Create(options), NullLogger<SourcePassWarmUp>.Instance, Detector);
        }
    }

    private static StubDetector NewDetector() =>
        new(_ => Set(2, Ev(VideoEventType.Hit, 90, 0.9), Ev(VideoEventType.Appear, 15, 0.5)));

    /// <summary>The step's settings with every cached pass on. Everything else is the default.</summary>
    private static VideoAnalyzeStepConfig AllPasses(VideoAnalyzeStepConfig c) => c with { AnalyzeEvents = true, DetectSilence = true };

    private static VideoEditingOptions NewOptions(string scratch, Action<VideoEditingOptions>? configure = null)
    {
        VideoEditingOptions options = new() { ScratchPath = scratch, MaxConcurrentJobs = 1, AnalyzeTimeoutSeconds = 30, CompileTimeoutSeconds = 30 };
        configure?.Invoke(options);
        return options;
    }

    /// <summary>A directory with one small file standing in for the downloaded source.</summary>
    private sealed class LocalSource : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "source-pass-warm-up-tests", Guid.NewGuid().ToString("N"));

        public string Path_ { get; }

        public LocalSource()
        {
            System.IO.Directory.CreateDirectory(Directory);
            Path_ = Path.Combine(Directory, "source.mp4");
            File.WriteAllBytes(Path_, [0, 1, 2, 3]);
        }

        public void Dispose()
        {
            try
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }
            catch (IOException)
            {
                // A leaked temp directory must never fail a test.
            }
        }
    }

    private static readonly string UploadKey = SourceKeyWithExtension(".mp4");

    [Fact]
    public async Task It_stores_the_four_entries_a_default_step_reads_so_that_step_runs_no_pass()
    {
        using LocalSource source = new();
        InMemoryPassCache cache = new();
        Passes passes = new();
        SourcePassWarmUp warmUp = passes.Build(cache, new Mock<IVideoToolRunner>().Object, NewOptions(source.Directory));

        SourcePassWarmUpOutcome outcome = await warmUp.RunAsync(ProjectId, UploadKey, "\"v1\"", source.Path_, Probe1080, CancellationToken.None);

        outcome.Should().Be(SourcePassWarmUpOutcome.Done);
        (passes.Calls.Silence, passes.Calls.Shots, passes.Calls.Grid, passes.Detector.Seen.Count).Should().Be((1, 1, 1, 1));
        cache.Entries.Should().HaveCount(4, "one entry a pass");
        cache.Entries.Keys.Should().OnlyContain(k => k.StartsWith($"projects/{ProjectId:D}/agentFiles/analysis-cache/"));

        // The step, with default settings, on the same stored file and the same cache: every pass is answered.
        PassCalls stepCalls = new();
        StubDetector stepDetector = NewDetector();
        Run warmed = await RunAsync(stepDetector, AllPasses, passCache: cache, calls: stepCalls, sourceKey: UploadKey);

        warmed.Result.Status.Should().Be(StepStatus.Completed, because: warmed.Result.ErrorDetails ?? warmed.Result.Output);
        (stepCalls.Silence, stepCalls.Shots, stepCalls.Grid, stepDetector.Seen.Count).Should().Be((0, 0, 0, 0));
        cache.Hits.Should().Be(4);
        cache.Entries.Should().HaveCount(4, "the step computed the same four keys and added none");

        // And what it read is what the passes answer when the step runs them itself.
        Run plain = await RunAsync(NewDetector(), AllPasses, calls: new PassCalls(), sourceKey: UploadKey);
        warmed.Result.Output.Should().Be(plain.Result.Output);
        Stable(warmed.Artifact!).Should().Be(Stable(plain.Artifact!));
    }

    [Fact]
    public async Task A_second_warm_up_of_the_same_file_runs_no_pass()
    {
        using LocalSource source = new();
        InMemoryPassCache cache = new();
        Passes passes = new();
        SourcePassWarmUp warmUp = passes.Build(cache, new Mock<IVideoToolRunner>().Object, NewOptions(source.Directory));

        await warmUp.RunAsync(ProjectId, UploadKey, "v1", source.Path_, Probe1080, CancellationToken.None);
        SourcePassWarmUpOutcome again = await warmUp.RunAsync(ProjectId, UploadKey, "v1", source.Path_, Probe1080, CancellationToken.None);

        again.Should().Be(SourcePassWarmUpOutcome.Done);
        passes.Total.Should().Be(4);
        cache.Writes.Should().Be(4);
    }

    [Fact]
    public async Task A_source_that_takes_the_analysis_copy_is_skipped()
    {
        using LocalSource source = new();
        InMemoryPassCache cache = new();
        Passes passes = new();
        VideoEditingOptions options = NewOptions(source.Directory);
        SourcePassWarmUp warmUp = passes.Build(cache, new Mock<IVideoToolRunner>().Object, options);

        // The policy the step follows says so, and the warm-up asks the same function.
        CachedSourcePasses.WantsAnalysisCopy(
            new VideoAnalyzeStepConfig(1, new VideoSourceRef(VideoSourceKind.ProjectFile)), LargeSource, options,
            cameraProfileChosen: false, scenesAuthored: false, isStill: false).Should().BeTrue();

        SourcePassWarmUpOutcome outcome = await warmUp.RunAsync(ProjectId, UploadKey, "v1", source.Path_, LargeSource, CancellationToken.None);

        outcome.Should().Be(SourcePassWarmUpOutcome.TakesAnalysisCopy);
        passes.Total.Should().Be(0);
        (cache.Identities.Count, cache.Reads, cache.Writes).Should().Be((0, 0, 0));
    }

    [Fact]
    public async Task A_failing_pass_is_only_logged_and_the_other_passes_are_stored()
    {
        using LocalSource source = new();
        InMemoryPassCache cache = new();
        Passes passes = new() { ShotsFailure = new InvalidOperationException("ffmpeg failed") };
        SourcePassWarmUp warmUp = passes.Build(cache, new Mock<IVideoToolRunner>().Object, NewOptions(source.Directory));

        SourcePassWarmUpOutcome outcome = await warmUp.RunAsync(ProjectId, UploadKey, "v1", source.Path_, Probe1080, CancellationToken.None);

        outcome.Should().Be(SourcePassWarmUpOutcome.Partial);
        (passes.Calls.Silence, passes.Calls.Shots, passes.Calls.Grid, passes.Detector.Seen.Count).Should().Be((1, 1, 1, 1));
        cache.Entries.Should().HaveCount(3, "silence, the grid and events are stored; the failed shot pass is not");
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Switched_off_nothing_runs_and_nothing_is_stored(bool cacheEnabled, bool warmOnUpload)
    {
        using LocalSource source = new();
        InMemoryPassCache cache = new();
        Passes passes = new();
        SourcePassWarmUp warmUp = passes.Build(cache, new Mock<IVideoToolRunner>().Object, NewOptions(source.Directory, o =>
        {
            o.SourcePassCache.Enabled = cacheEnabled;
            o.SourcePassCache.WarmOnUpload = warmOnUpload;
        }));

        warmUp.Enabled.Should().BeFalse();
        SourcePassWarmUpOutcome outcome = await warmUp.RunAsync(ProjectId, UploadKey, "v1", source.Path_, Probe1080, CancellationToken.None);

        outcome.Should().Be(SourcePassWarmUpOutcome.Off);
        passes.Total.Should().Be(0);
        (cache.Identities.Count, cache.Reads, cache.Writes).Should().Be((0, 0, 0));
    }

    [Fact]
    public async Task A_file_replaced_since_the_download_is_not_cached_under_the_new_version()
    {
        using LocalSource source = new();
        InMemoryPassCache cache = new();
        Passes passes = new();
        SourcePassWarmUp warmUp = passes.Build(cache, new Mock<IVideoToolRunner>().Object, NewOptions(source.Directory));

        // The store answers "v1" now; the local file was downloaded as another version.
        SourcePassWarmUpOutcome outcome = await warmUp.RunAsync(ProjectId, UploadKey, "\"older\"", source.Path_, Probe1080, CancellationToken.None);

        outcome.Should().Be(SourcePassWarmUpOutcome.SourceChanged);
        passes.Total.Should().Be(0);
        cache.Writes.Should().Be(0);
    }

    [Theory]
    [InlineData("photo")]
    [InlineData("audio")]
    [InlineData("too long")]
    public async Task A_source_a_step_would_not_analyse_as_video_is_left_alone(string what)
    {
        using LocalSource source = new();
        InMemoryPassCache cache = new();
        Passes passes = new();
        SourcePassWarmUp warmUp = passes.Build(cache, new Mock<IVideoToolRunner>().Object, NewOptions(source.Directory));
        (string key, MediaProbeResult probe) = what switch
        {
            "photo" => (SourceKeyWithExtension(".png"), new MediaProbeResult(0.04, 25, 1, 1920, 1080, "png", null, null)),
            "audio" => (SourceKeyWithExtension(".mp3"), new MediaProbeResult(30, 0, 0, 0, 0, null, "mp3", 44100)),
            _ => (UploadKey, Probe1080 with { DurationSec = VideoSourceLimits.DefaultMaxSeconds + 1 })
        };

        SourcePassWarmUpOutcome outcome = await warmUp.RunAsync(ProjectId, key, "v1", source.Path_, probe, CancellationToken.None);

        outcome.Should().Be(SourcePassWarmUpOutcome.NotAnalysable);
        passes.Total.Should().Be(0);
        cache.Writes.Should().Be(0);
    }

    [Fact]
    public async Task A_cancelled_run_never_throws()
    {
        using LocalSource source = new();
        InMemoryPassCache cache = new();
        using CancellationTokenSource cts = new();
        Passes passes = new()
        {
            // The run is cancelled while the first pass runs, as at shutdown.
            OnSilence = (_, ct) =>
            {
                cts.Cancel();
                ct.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            }
        };
        SourcePassWarmUp warmUp = passes.Build(cache, new Mock<IVideoToolRunner>().Object, NewOptions(source.Directory));

        SourcePassWarmUpOutcome outcome = await warmUp.RunAsync(ProjectId, UploadKey, "v1", source.Path_, Probe1080, cts.Token);

        outcome.Should().Be(SourcePassWarmUpOutcome.Partial);
        (passes.Calls.Silence, passes.Calls.Shots, passes.Calls.Grid, passes.Detector.Seen.Count).Should().Be((1, 0, 0, 0));
        cache.Writes.Should().Be(0);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task A_download_with_no_known_version_is_never_cached(string? publishedVersion)
    {
        using LocalSource source = new();
        InMemoryPassCache cache = new();
        Passes passes = new();
        SourcePassWarmUp warmUp = passes.Build(cache, new Mock<IVideoToolRunner>().Object, NewOptions(source.Directory));

        // The store has a version now, but nothing says the local bytes are that version.
        SourcePassWarmUpOutcome outcome = await warmUp.RunAsync(ProjectId, UploadKey, publishedVersion, source.Path_, Probe1080, CancellationToken.None);

        outcome.Should().Be(SourcePassWarmUpOutcome.NoIdentity);
        passes.Total.Should().Be(0);
        (cache.Reads, cache.Writes).Should().Be((0, 0));
    }

    [Theory]
    [InlineData("v2")]
    [InlineData(null)]
    public async Task A_file_replaced_or_deleted_while_the_warm_up_runs_gets_no_entry(string? laterVersion)
    {
        using LocalSource source = new();
        // The check at the start sees the downloaded version. Afterwards the object is another one, or gone (deleted, quarantined).
        InMemoryPassCache cache = new() { VersionAt = call => call == 1 ? "v1" : laterVersion };
        Passes passes = new();
        SourcePassWarmUp warmUp = passes.Build(cache, new Mock<IVideoToolRunner>().Object, NewOptions(source.Directory));

        await warmUp.RunAsync(ProjectId, UploadKey, "v1", source.Path_, Probe1080, CancellationToken.None);

        passes.Total.Should().Be(4, "the passes ran on the local file");
        cache.Writes.Should().Be(0, "nothing is stored for an object that is no longer the one that was downloaded");
    }

    // ---- giving way ----

    [Fact]
    public async Task Asked_to_give_way_before_it_starts_it_runs_no_pass()
    {
        using LocalSource source = new();
        InMemoryPassCache cache = new();
        Passes passes = new();
        SourcePassWarmUp warmUp = passes.Build(cache, new Mock<IVideoToolRunner>().Object, NewOptions(source.Directory));

        SourcePassWarmUpOutcome outcome = await warmUp.RunAsync(
            ProjectId, UploadKey, "v1", source.Path_, Probe1080, CancellationToken.None, shouldYield: () => true);

        outcome.Should().Be(SourcePassWarmUpOutcome.Yielded);
        passes.Total.Should().Be(0);
        cache.Writes.Should().Be(0);
    }

    [Fact]
    public async Task Asked_to_give_way_between_passes_it_starts_no_further_pass_and_keeps_what_finished()
    {
        using LocalSource source = new();
        InMemoryPassCache cache = new();
        bool waiting = false;
        Passes passes = new()
        {
            // Other work starts to wait while the first pass runs.
            OnSilence = (_, _) =>
            {
                waiting = true;
                return Task.CompletedTask;
            }
        };
        SourcePassWarmUp warmUp = passes.Build(cache, new Mock<IVideoToolRunner>().Object, NewOptions(source.Directory));

        SourcePassWarmUpOutcome outcome = await warmUp.RunAsync(
            ProjectId, UploadKey, "v1", source.Path_, Probe1080, CancellationToken.None, shouldYield: () => waiting);

        outcome.Should().Be(SourcePassWarmUpOutcome.Yielded);
        (passes.Calls.Silence, passes.Calls.Shots, passes.Calls.Grid, passes.Detector.Seen.Count).Should().Be((1, 0, 0, 0));
        cache.Entries.Should().HaveCount(1, "the pass that finished stays in the cache");
    }

    // ---- low priority ----

    [Fact]
    public async Task Its_passes_run_ffmpeg_at_low_priority_and_within_the_source_directory()
    {
        using LocalSource source = new();
        var runner = new Mock<IVideoToolRunner>(MockBehavior.Strict);
        VideoToolResult ok = new(0, "", "", TimedOut: false);
        string[] argv = ["-i", "source.mp4"];
        runner.Setup(r => r.RunFfmpegAsync(argv, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>(), VideoToolPriority.Low)).ReturnsAsync(ok);
        runner.Setup(r => r.RunFfprobeAsync(argv, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>(), VideoToolPriority.Low)).ReturnsAsync(ok);
        string? root = null;
        Passes passes = new()
        {
            OnSilence = async (workspace, ct) =>
            {
                root = workspace.RootPath;
                await workspace.RunFfmpegAsync(argv, TimeSpan.FromSeconds(5), ct);
                await workspace.RunFfprobeAsync(argv, TimeSpan.FromSeconds(5), ct);
            }
        };
        SourcePassWarmUp warmUp = passes.Build(new InMemoryPassCache(), runner.Object, NewOptions(source.Directory));

        SourcePassWarmUpOutcome outcome = await warmUp.RunAsync(ProjectId, UploadKey, "v1", source.Path_, Probe1080, CancellationToken.None);

        // Strict mock: a call on any Normal-priority overload would have thrown and left the silence pass out.
        outcome.Should().Be(SourcePassWarmUpOutcome.Done);
        root.Should().Be(Path.GetFullPath(source.Directory));
        runner.Verify(r => r.RunFfmpegAsync(argv, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>(), VideoToolPriority.Low), Times.Once);
        runner.Verify(r => r.RunFfprobeAsync(argv, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>(), VideoToolPriority.Low), Times.Once);
    }

    [Fact]
    public async Task A_workspace_with_no_priority_still_calls_the_plain_runner_overloads()
    {
        using LocalSource source = new();
        var runner = new Mock<IVideoToolRunner>(MockBehavior.Strict);
        VideoToolResult ok = new(0, "", "", TimedOut: false);
        string[] argv = ["-version"];
        Action<string> onLine = _ => { };
        runner.Setup(r => r.RunFfmpegAsync(argv, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync(ok);
        runner.Setup(r => r.RunFfmpegAsync(argv, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>(), onLine)).ReturnsAsync(ok);
        runner.Setup(r => r.RunFfprobeAsync(argv, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync(ok);
        runner.Setup(r => r.RunFfmpegAsync(argv, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>(), onLine, VideoToolPriority.Low)).ReturnsAsync(ok);

        IMediaWorkspace plain = LocalMediaWorkspace.CreateAt(source.Directory, runner.Object);
        await plain.RunFfmpegAsync(argv, TimeSpan.FromSeconds(1), CancellationToken.None);
        await plain.RunFfmpegAsync(argv, TimeSpan.FromSeconds(1), CancellationToken.None, onLine);
        await plain.RunFfprobeAsync(argv, TimeSpan.FromSeconds(1), CancellationToken.None);
        IMediaWorkspace low = LocalMediaWorkspace.CreateAt(source.Directory, runner.Object, VideoToolPriority.Low);
        await low.RunFfmpegAsync(argv, TimeSpan.FromSeconds(1), CancellationToken.None, onLine);

        runner.Verify(r => r.RunFfmpegAsync(argv, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Once);
        runner.Verify(r => r.RunFfmpegAsync(argv, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>(), onLine), Times.Once);
        runner.Verify(r => r.RunFfprobeAsync(argv, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Once);
        runner.Verify(r => r.RunFfmpegAsync(argv, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>(), onLine, VideoToolPriority.Low), Times.Once);
    }

    // ---- wiring ----

    [Fact]
    public void The_registered_dispatcher_is_handed_the_warm_up_and_the_switch_defaults_to_on()
    {
        using CompositionRootHarness root = CompositionRootHarness.Build(
            "workflow-engine",
            (services, configuration) => services.AddWorkflowEngineServices(configuration));

        MediaDerivativeDispatcher dispatcher = root.Services.GetRequiredService<MediaDerivativeDispatcher>();
        object? handed = typeof(MediaDerivativeDispatcher)
            .GetField("_warmUp", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(dispatcher);

        handed.Should().BeSameAs(root.Services.GetRequiredService<SourcePassWarmUp>());
        typeof(MediaDerivativeDispatcher).GetField("_drain", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(dispatcher)
            .Should().BeSameAs(root.Services.GetRequiredService<EngineDrainTarget>(), "the dispatcher must learn that the engine drains");
        typeof(MediaDerivativeDispatcher).GetField("_scratchGuard", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(dispatcher)
            .Should().BeSameAs(root.Services.GetRequiredService<IScratchDiskGuard>());
        ((SourcePassWarmUp)handed!).Enabled.Should().BeTrue();
        new MediaDerivativesRequested(
            Guid.NewGuid(), new MediaSourceRef(MediaSourceKind.ProjectFile, Guid.NewGuid()), null, "k", "e", "video/mp4",
            [MediaDerivativeKind.Proxy], "upload", DateTime.UtcNow).AnalyzeForWorkflows.Should().BeFalse("a request asks for nothing new unless it says so");
    }

    // ---- inside a real derivative run (real ffmpeg for the derivatives, stub passes for the warm-up) ----

    /// <summary>One derivative run of a small real video, with a warm-up over <paramref name="passes"/> and <paramref name="cache"/>.</summary>
    private static async Task<(IReadOnlyList<MediaDerivative> Rows, IReadOnlyList<MediaDerivativeProgressStage> Stages, MediaProbeResult Probe)> DeriveAsync(
        Passes passes, InMemoryPassCache cache, bool analyzeForWorkflows, Action<VideoEditingOptions>? configure = null,
        string mimeType = "video/mp4", MediaDerivativeKind kind = MediaDerivativeKind.Proxy)
    {
        string directory = MediaDerivativeFfmpegFixture.NewDirectory();
        VideoEditingOptions options = MediaDerivativeFfmpegFixture.BuildOptions(Path.Combine(directory, "scratch"));
        configure?.Invoke(options);
        using MediaDerivativeDispatchFixture fixture = MediaDerivativeDispatchFixture.Create(
            directory, options, (runner, o) => passes.Build(cache, runner, o));
        try
        {
            string source = MediaDerivativeFfmpegFixture.CreateSource(
                directory, "src.mp4", seconds: 2, fps: 30, width: 320, height: 240, withAudio: true);
            fixture.ServeAsSourceObject(source);

            // The in-memory cache answers version "v1" for every object, and that is the etag the request carries.
            MediaDerivativesRequested request = fixture.Request(ProjectId, UploadKey, "\"v1\"", [kind], mimeType: mimeType);
            await fixture.Dispatcher.RunAsync(request with { AnalyzeForWorkflows = analyzeForWorkflows }, CancellationToken.None);

            MediaProbeResult probe = await new FfprobeMediaProbe(
                fixture.Runner, Options.Create(options), NullLogger<FfprobeMediaProbe>.Instance).ProbeAsync(source, CancellationToken.None);
            return (fixture.ReadRows(), fixture.Sink.Stages, probe);
        }
        finally
        {
            MediaDerivativeFfmpegFixture.TryDelete(directory);
        }
    }

    [FfmpegToolsFact]
    public async Task Without_the_flag_a_derivative_run_writes_nothing_to_the_pass_cache()
    {
        InMemoryPassCache cache = new();
        Passes passes = new();

        var run = await DeriveAsync(passes, cache, analyzeForWorkflows: false);

        run.Rows.Should().ContainSingle(r => r.Kind == MediaDerivativeKind.Proxy);
        run.Stages.Last().Should().Be(MediaDerivativeProgressStage.Done);
        passes.Total.Should().Be(0);
        (cache.Identities.Count, cache.Reads, cache.Writes).Should().Be((0, 0, 0));
    }

    [FfmpegToolsFact]
    public async Task With_the_flag_a_derivative_run_stores_what_a_default_step_on_that_file_reads()
    {
        InMemoryPassCache cache = new();
        Passes passes = new();

        var run = await DeriveAsync(passes, cache, analyzeForWorkflows: true);

        run.Rows.Should().ContainSingle(r => r.Kind == MediaDerivativeKind.Proxy);
        run.Stages.Last().Should().Be(MediaDerivativeProgressStage.Done, "the run reports its derivatives before the warm-up starts");
        (passes.Calls.Silence, passes.Calls.Shots, passes.Calls.Grid, passes.Detector.Seen.Count).Should().Be((1, 1, 1, 1));
        cache.Entries.Should().HaveCount(4);

        // The step probes the file as the run did, and asks the same four questions of it.
        PassCalls stepCalls = new();
        StubDetector stepDetector = NewDetector();
        Run step = await RunAsync(stepDetector, AllPasses, passCache: cache, calls: stepCalls, probeResult: run.Probe, sourceKey: UploadKey);

        step.Result.Status.Should().Be(StepStatus.Completed, because: step.Result.ErrorDetails ?? step.Result.Output);
        (stepCalls.Silence, stepCalls.Shots, stepCalls.Grid, stepDetector.Seen.Count).Should().Be((0, 0, 0, 0));
        cache.Hits.Should().Be(4);
        cache.Entries.Should().HaveCount(4);
    }

    [FfmpegToolsFact]
    public async Task A_failing_pass_in_the_warm_up_leaves_the_derivatives_as_they_are()
    {
        var without = await DeriveAsync(new Passes(), new InMemoryPassCache(), analyzeForWorkflows: false);
        InMemoryPassCache cache = new();
        Passes failing = new() { ShotsFailure = new InvalidOperationException("ffmpeg failed") };

        var with = await DeriveAsync(failing, cache, analyzeForWorkflows: true);

        failing.Calls.Shots.Should().Be(1, "the warm-up did run");
        with.Rows.Select(r => (r.Kind, r.CacheKey, r.StorageKey, r.Width, r.Height))
            .Should().Equal(without.Rows.Select(r => (r.Kind, r.CacheKey, r.StorageKey, r.Width, r.Height)));
        with.Stages.Distinct().Should().Equal(without.Stages.Distinct(), "the warm-up reports no stage of its own and changes none");
        with.Stages.Last().Should().Be(MediaDerivativeProgressStage.Done);
        with.Stages.Should().NotContain(MediaDerivativeProgressStage.Failed);
    }

    [FfmpegToolsFact]
    public async Task With_the_real_passes_a_run_fills_all_four_entries_from_its_download_directory()
    {
        string directory = MediaDerivativeFfmpegFixture.NewDirectory();
        InMemoryPassCache cache = new();
        using MediaDerivativeDispatchFixture fixture = MediaDerivativeDispatchFixture.Create(directory, videoEditing: null, (runner, options) =>
        {
            IOptions<VideoEditingOptions> o = Options.Create(options);
            FfmpegSilenceDetector silence = new(runner, o, NullLogger<FfmpegSilenceDetector>.Instance);
            return new SourcePassWarmUp(
                silence,
                new FfmpegShotDetector(runner, o, NullLogger<FfmpegShotDetector>.Instance),
                new FfmpegFrameGridSampler(runner, o, NullLogger<FfmpegFrameGridSampler>.Instance),
                cache, runner, o, NullLogger<SourcePassWarmUp>.Instance,
                new VideoEventDetector(new FrameSignalSampler(o, NullLogger<FrameSignalSampler>.Instance), silence, o, NullLogger<VideoEventDetector>.Instance));
        });
        try
        {
            string source = MediaDerivativeFfmpegFixture.CreateSource(
                directory, "src.mp4", seconds: 2, fps: 30, width: 320, height: 240, withAudio: true);
            fixture.ServeAsSourceObject(source);
            MediaDerivativesRequested request = fixture.Request(ProjectId, UploadKey, "v1", [MediaDerivativeKind.Proxy]);

            await fixture.Dispatcher.RunAsync(request with { AnalyzeForWorkflows = true }, CancellationToken.None);

            fixture.ReadRows().Should().ContainSingle(r => r.Kind == MediaDerivativeKind.Proxy);
            cache.Entries.Should().HaveCount(4, "silence, shots, the frame grid and events of a real file");
            cache.Entries.Values.Count(v => SourcePassPayloads.TryDecodeGrid(v, out FrameGridResult? grid) && grid!.FrameCount > 0)
                .Should().Be(1, "one entry is the frame grid, with frames in it");
            cache.Entries.Values.Count(v => SourcePassPayloads.TryDecodeEvents(v, out VideoEventSet? set) && set!.FrameCount > 0)
                .Should().Be(1, "one entry is the event set");

            // Nothing of the warm-up is left on the scratch volume, and a repeat finds every answer.
            Directory.Exists(Path.Combine(fixture.ScratchRoot, MediaDerivativeDispatcher.ScratchSubdirectory)).Should().BeTrue();
            Directory.EnumerateFiles(fixture.ScratchRoot, "source*", SearchOption.AllDirectories).Should().BeEmpty();
            int ffmpegBefore = fixture.Runner.FfmpegCount;
            await fixture.Dispatcher.RunAsync(request with { AnalyzeForWorkflows = true }, CancellationToken.None);
            fixture.Runner.FfmpegCount.Should().Be(ffmpegBefore, "the derivative is a cache hit and so is every pass");
            cache.Entries.Should().HaveCount(4);
        }
        finally
        {
            MediaDerivativeFfmpegFixture.TryDelete(directory);
        }
    }

    [FfmpegToolsFact]
    public async Task A_run_that_starts_to_wait_stops_the_warm_up_and_is_not_held_up_by_it()
    {
        string directory = MediaDerivativeFfmpegFixture.NewDirectory();
        InMemoryPassCache cache = new();
        MediaDerivativeDispatchFixture? made = null;
        MediaDerivativesRequested? request = null;
        Task? repeat = null;
        int silenceCalls = 0;
        Passes passes = new()
        {
            // While the first pass of the warm-up runs, the same source is asked for again (the editor opens it).
            // The one source slot is still held by the warming run, so that request waits. Left alone, the pass
            // would run for minutes.
            OnSilence = async (_, ct) =>
            {
                if (Interlocked.Increment(ref silenceCalls) > 1)
                    return;
                repeat = made!.Dispatcher.RunAsync(request!, CancellationToken.None);
                await Task.Delay(TimeSpan.FromMinutes(5), ct);
            }
        };
        VideoEditingOptions options = MediaDerivativeFfmpegFixture.BuildOptions(Path.Combine(directory, "scratch"));
        using MediaDerivativeDispatchFixture fixture = MediaDerivativeDispatchFixture.Create(
            directory, options, (runner, o) => passes.Build(cache, runner, o));
        made = fixture;
        try
        {
            string source = MediaDerivativeFfmpegFixture.CreateSource(
                directory, "src.mp4", seconds: 2, fps: 30, width: 320, height: 240, withAudio: true);
            fixture.ServeAsSourceObject(source);
            request = fixture.Request(ProjectId, UploadKey, "v1", [MediaDerivativeKind.Proxy]) with { AnalyzeForWorkflows = true };
            fixture.AddSourceFile(request);

            Task first = fixture.Dispatcher.RunAsync(request, CancellationToken.None);

            (await Task.WhenAny(first, Task.Delay(TimeSpan.FromSeconds(60)))).Should().BeSameAs(first, "the warm-up gives way within about a second");
            repeat.Should().NotBeNull("the repeat was a run of its own, not a duplicate dropped because the first run was still warming up");
            await repeat!.WaitAsync(TimeSpan.FromSeconds(60));
            await fixture.Dispatcher.DeferredWarmUpsSettledAsync().WaitAsync(TimeSpan.FromSeconds(60));

            // The first warm-up stopped in its first pass and started no other. The repeat then had the slot: its
            // derivative was already there, nothing waited behind it, and its own warm-up ran to the end.
            (passes.Calls.Silence, passes.Calls.Shots, passes.Calls.Grid, passes.Detector.Seen.Count).Should().Be((2, 1, 1, 1));
            cache.Entries.Should().HaveCount(4);
            fixture.ReadRows().Should().ContainSingle(r => r.Kind == MediaDerivativeKind.Proxy);
            fixture.Sink.Stages.Should().NotContain(MediaDerivativeProgressStage.Failed);
        }
        finally
        {
            MediaDerivativeFfmpegFixture.TryDelete(directory);
        }
    }

    [FfmpegToolsFact]
    public async Task A_warm_up_that_gave_way_is_tried_again_once_no_run_waits()
    {
        string directory = MediaDerivativeFfmpegFixture.NewDirectory();
        InMemoryPassCache cache = new();
        MediaDerivativeDispatchFixture? made = null;
        MediaDerivativesRequested? request = null;
        Task? repeat = null;
        int silenceCalls = 0;
        Passes passes = new()
        {
            // While the first pass of the warm-up runs, the same source is asked for again (the editor opens it).
            // The one source slot is still held by the warming run, so that request waits. Left alone, the pass
            // would run for minutes.
            OnSilence = async (_, ct) =>
            {
                if (Interlocked.Increment(ref silenceCalls) > 1)
                    return;
                repeat = made!.Dispatcher.RunAsync(request! with { AnalyzeForWorkflows = false }, CancellationToken.None);
                await Task.Delay(TimeSpan.FromMinutes(5), ct);
            }
        };
        VideoEditingOptions options = MediaDerivativeFfmpegFixture.BuildOptions(Path.Combine(directory, "scratch"));
        using MediaDerivativeDispatchFixture fixture = MediaDerivativeDispatchFixture.Create(
            directory, options, (runner, o) => passes.Build(cache, runner, o));
        made = fixture;
        try
        {
            string source = MediaDerivativeFfmpegFixture.CreateSource(
                directory, "src.mp4", seconds: 2, fps: 30, width: 320, height: 240, withAudio: true);
            fixture.ServeAsSourceObject(source);
            request = fixture.Request(ProjectId, UploadKey, "v1", [MediaDerivativeKind.Proxy]) with { AnalyzeForWorkflows = true };
            fixture.AddSourceFile(request);

            Task first = fixture.Dispatcher.RunAsync(request, CancellationToken.None);

            (await Task.WhenAny(first, Task.Delay(TimeSpan.FromSeconds(60)))).Should().BeSameAs(first, "the warm-up gives way within about a second");
            repeat.Should().NotBeNull("the repeat was a run of its own, not a duplicate dropped because the first run was still warming up");
            await repeat!.WaitAsync(TimeSpan.FromSeconds(60));

            // The run that made it give way (another upload, or the editor) warms nothing itself.
            await fixture.Dispatcher.DeferredWarmUpsSettledAsync().WaitAsync(TimeSpan.FromSeconds(60));

            // Once nothing waited, the warm-up that gave way ran again as a background run and finished.
            (passes.Calls.Silence, passes.Calls.Shots, passes.Calls.Grid, passes.Detector.Seen.Count).Should().Be((2, 1, 1, 1));
            cache.Entries.Should().HaveCount(4);
            fixture.ReadRows().Should().ContainSingle(r => r.Kind == MediaDerivativeKind.Proxy);
            fixture.Sink.Stages.Should().NotContain(MediaDerivativeProgressStage.Failed);
        }
        finally
        {
            MediaDerivativeFfmpegFixture.TryDelete(directory);
        }
    }

    // ---- what is kept for later, and what is not ----

    private static async Task UntilAsync(Func<bool> condition, string what)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Timed out waiting until " + what);
            await Task.Delay(10);
        }
    }

    [Fact]
    public void The_pure_check_names_every_source_a_warm_up_would_leave_alone()
    {
        using LocalSource source = new();
        VideoEditingOptions options = NewOptions(source.Directory);
        SourcePassWarmUp warmUp = new Passes().Build(new InMemoryPassCache(), new Mock<IVideoToolRunner>().Object, options);

        warmUp.RefusalFor(UploadKey, "v1", Probe1080).Should().BeNull("this one would run its passes");
        warmUp.RefusalFor(UploadKey, "v1", LargeSource).Should().Be(SourcePassWarmUpOutcome.TakesAnalysisCopy);
        warmUp.RefusalFor(UploadKey, "", Probe1080).Should().Be(SourcePassWarmUpOutcome.NoIdentity);
        warmUp.RefusalFor(UploadKey, null, Probe1080).Should().Be(SourcePassWarmUpOutcome.NoIdentity);
        warmUp.RefusalFor(SourceKeyWithExtension(".png"), "v1", new MediaProbeResult(0.04, 25, 1, 1920, 1080, "png", null, null))
            .Should().Be(SourcePassWarmUpOutcome.NotAnalysable);
        warmUp.RefusalFor(UploadKey, "v1", new MediaProbeResult(30, 0, 0, 0, 0, null, "mp3", 44100)).Should().Be(SourcePassWarmUpOutcome.NotAnalysable);
        warmUp.RefusalFor(UploadKey, "v1", Probe1080 with { DurationSec = VideoSourceLimits.DefaultMaxSeconds + 1 })
            .Should().Be(SourcePassWarmUpOutcome.NotAnalysable);

        SourcePassWarmUp off = new Passes().Build(
            new InMemoryPassCache(), new Mock<IVideoToolRunner>().Object, NewOptions(source.Directory, o => o.SourcePassCache.WarmOnUpload = false));
        off.RefusalFor(UploadKey, "v1", Probe1080).Should().Be(SourcePassWarmUpOutcome.Off);
    }

    /// <summary>
    /// A run that ends while another run waits does not warm the cache then. It keeps the warm-up for a quiet
    /// moment only when that warm-up could do something: the retry downloads the whole file again.
    /// </summary>
    [FfmpegToolsTheory]
    [InlineData("", 2, 0)]
    [InlineData("v1", 3, 4)]
    public async Task A_warm_up_is_kept_for_later_only_when_it_could_run(string publishedVersion, int downloads, int entries)
    {
        string directory = MediaDerivativeFfmpegFixture.NewDirectory();
        InMemoryPassCache cache = new();
        Passes passes = new();
        VideoEditingOptions options = MediaDerivativeFfmpegFixture.BuildOptions(Path.Combine(directory, "scratch"));
        using MediaDerivativeDispatchFixture fixture = MediaDerivativeDispatchFixture.Create(
            directory, options, (runner, o) => passes.Build(cache, runner, o));
        try
        {
            string source = MediaDerivativeFfmpegFixture.CreateSource(
                directory, "src.mp4", seconds: 2, fps: 30, width: 320, height: 240, withAudio: true);
            fixture.ServeAsSourceObject(source);

            // With no published version the warm-up caches nothing: the bytes it read have no known version.
            MediaDerivativesRequested first =
                fixture.Request(ProjectId, UploadKey, publishedVersion, [MediaDerivativeKind.Proxy]) with { AnalyzeForWorkflows = true };
            MediaDerivativesRequested other = fixture.Request(ProjectId, SourceKeyWithExtension(".mov"), "v1", [MediaDerivativeKind.Proxy]);
            fixture.AddSourceFile(first);
            fixture.AddSourceFile(other);

            // The first run holds the one source slot inside its download while the other starts to wait.
            TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Workspace.DownloadGate = gate.Task;
            Task a = fixture.Dispatcher.RunAsync(first, CancellationToken.None);
            await UntilAsync(() => fixture.Workspace.DownloadCount == 1, "the first run is in its download");
            Task b = fixture.Dispatcher.RunAsync(other, CancellationToken.None);
            gate.SetResult();

            await Task.WhenAll(a, b).WaitAsync(TimeSpan.FromSeconds(120));
            await fixture.Dispatcher.DeferredWarmUpsSettledAsync().WaitAsync(TimeSpan.FromSeconds(120));

            fixture.Workspace.DownloadCount.Should().Be(downloads, "a file is downloaded again only for a warm-up that can run");
            cache.Entries.Should().HaveCount(entries);
            fixture.Sink.Stages.Should().NotContain(MediaDerivativeProgressStage.Failed);
        }
        finally
        {
            MediaDerivativeFfmpegFixture.TryDelete(directory);
        }
    }

    /// <summary>
    /// With two source slots the run that waited can have the other slot before the stopped pass has ended. The
    /// warm-up was still stopped to give way, so it is tried again; it is not written off as out of time.
    /// </summary>
    [FfmpegToolsFact]
    public async Task A_warm_up_stopped_to_give_way_is_tried_again_even_when_nothing_waits_any_more_as_it_ends()
    {
        string directory = MediaDerivativeFfmpegFixture.NewDirectory();
        InMemoryPassCache cache = new();
        MediaDerivativeDispatchFixture? made = null;
        MediaDerivativesRequested? second = null;
        MediaDerivativesRequested? third = null;
        Task? b = null;
        Task? c = null;
        int silenceCalls = 0;
        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Passes passes = new()
        {
            OnSilence = async (_, ct) =>
            {
                if (Interlocked.Increment(ref silenceCalls) > 1)
                    return;

                // Two more uploads arrive while the first pass runs: one takes the second slot and stays in its
                // download, the other waits.
                made!.Workspace.DownloadGate = gate.Task;
                b = made.Dispatcher.RunAsync(second!, CancellationToken.None);
                c = made.Dispatcher.RunAsync(third!, CancellationToken.None);
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(5), ct);
                }
                catch (OperationCanceledException)
                {
                    // The pass was told to stop. Before it has ended, the run that waited gets the other slot.
                    gate.SetResult();
                    await b;
                    throw;
                }
            }
        };
        VideoEditingOptions options = MediaDerivativeFfmpegFixture.BuildOptions(Path.Combine(directory, "scratch"));
        options.DerivativeMaxConcurrentJobs = 2;
        using MediaDerivativeDispatchFixture fixture = MediaDerivativeDispatchFixture.Create(
            directory, options, (runner, o) => passes.Build(cache, runner, o));
        made = fixture;
        try
        {
            string source = MediaDerivativeFfmpegFixture.CreateSource(
                directory, "src.mp4", seconds: 2, fps: 30, width: 320, height: 240, withAudio: true);
            fixture.ServeAsSourceObject(source);
            MediaDerivativesRequested request = fixture.Request(ProjectId, UploadKey, "v1", [MediaDerivativeKind.Proxy]) with { AnalyzeForWorkflows = true };
            second = fixture.Request(ProjectId, SourceKeyWithExtension(".mov"), "v1", [MediaDerivativeKind.Proxy]);
            third = fixture.Request(ProjectId, SourceKeyWithExtension(".m4v"), "v1", [MediaDerivativeKind.Proxy]);
            fixture.AddSourceFile(request);

            await fixture.Dispatcher.RunAsync(request, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(120));
            await Task.WhenAll(b!, c!).WaitAsync(TimeSpan.FromSeconds(120));
            await fixture.Dispatcher.DeferredWarmUpsSettledAsync().WaitAsync(TimeSpan.FromSeconds(120));

            (passes.Calls.Silence, passes.Calls.Shots, passes.Calls.Grid, passes.Detector.Seen.Count)
                .Should().Be((2, 1, 1, 1), "the warm-up that was stopped ran again once the engine was quiet");
            cache.Entries.Should().HaveCount(4);
            fixture.Sink.Stages.Should().NotContain(MediaDerivativeProgressStage.Failed);
        }
        finally
        {
            MediaDerivativeFfmpegFixture.TryDelete(directory);
        }
    }

    /// <summary>A dispatcher with nothing running, for the tests of what waits. No ffmpeg is run by them.</summary>
    private static MediaDerivativeDispatchFixture QuietFixture(
        string directory, IScratchDiskGuard? scratchGuard = null, EngineDrainTarget? drain = null)
    {
        VideoEditingOptions options = MediaDerivativeFfmpegFixture.BuildOptions(Path.Combine(directory, "scratch"));
        return MediaDerivativeDispatchFixture.Create(
            directory, options, (runner, o) => new Passes().Build(new InMemoryPassCache(), runner, o), scratchGuard, drain);
    }

    private static MediaDerivativesRequested Flagged(MediaDerivativeDispatchFixture fixture, Guid projectId, int file) =>
        fixture.Request(projectId, $"projects/{projectId}/userFiles/clip-{file}.mp4", "v1", [MediaDerivativeKind.Proxy]) with { AnalyzeForWorkflows = true };

    [Fact]
    public void At_most_200_warm_ups_wait_at_once_and_at_most_20_of_them_for_one_project()
    {
        string directory = MediaDerivativeFfmpegFixture.NewDirectory();
        using MediaDerivativeDispatchFixture fixture = QuietFixture(directory);
        try
        {
            // One project's burst of uploads takes its share and no more.
            Guid busy = Guid.NewGuid();
            Enumerable.Range(0, 25).Count(i => fixture.Dispatcher.DeferWarmUp(Flagged(fixture, busy, i)))
                .Should().Be(MediaDerivativeDispatcher.MaxDeferredWarmUpsPerProject);
            MediaDerivativeDispatcher.MaxDeferredWarmUpsPerProject.Should().Be(20);

            // So the other projects still find room, up to the cap for all of them.
            int others = MediaDerivativeDispatcher.MaxDeferredWarmUps / MediaDerivativeDispatcher.MaxDeferredWarmUpsPerProject - 1;
            for (int p = 0; p < others; p++)
            {
                Guid project = Guid.NewGuid();
                Enumerable.Range(0, MediaDerivativeDispatcher.MaxDeferredWarmUpsPerProject)
                    .All(i => fixture.Dispatcher.DeferWarmUp(Flagged(fixture, project, i))).Should().BeTrue();
            }

            fixture.Dispatcher.DeferredWarmUpCount.Should().Be(MediaDerivativeDispatcher.MaxDeferredWarmUps);
            MediaDerivativeDispatcher.MaxDeferredWarmUps.Should().Be(200);
            fixture.Dispatcher.DeferWarmUp(Flagged(fixture, Guid.NewGuid(), 0)).Should().BeFalse("one over the cap is dropped, never queued");
            fixture.Dispatcher.DeferredWarmUpCount.Should().Be(MediaDerivativeDispatcher.MaxDeferredWarmUps);
        }
        finally
        {
            MediaDerivativeFfmpegFixture.TryDelete(directory);
        }
    }

    [Fact]
    public void One_files_warm_up_is_kept_four_times_and_then_given_up()
    {
        string directory = MediaDerivativeFfmpegFixture.NewDirectory();
        using MediaDerivativeDispatchFixture fixture = QuietFixture(directory);
        try
        {
            Guid project = Guid.NewGuid();
            MediaDerivativesRequested request = Flagged(fixture, project, 0);

            Enumerable.Range(0, MediaDerivativeDispatcher.MaxWarmUpDeferrals).All(_ => fixture.Dispatcher.DeferWarmUp(request)).Should().BeTrue();
            MediaDerivativeDispatcher.MaxWarmUpDeferrals.Should().Be(4);
            fixture.Dispatcher.DeferWarmUp(request).Should().BeFalse("the fifth time it is given up");
            fixture.Dispatcher.DeferredWarmUpCount.Should().Be(MediaDerivativeDispatcher.MaxWarmUpDeferrals);

            fixture.Dispatcher.DeferWarmUp(Flagged(fixture, project, 1)).Should().BeTrue("another file has its own count");
        }
        finally
        {
            MediaDerivativeFfmpegFixture.TryDelete(directory);
        }
    }

    /// <summary>
    /// A kept request can be replayed much later. The consumer's own check runs again first: a file that was
    /// deleted or moved, belongs to another project now, or waits for its scan, is dropped before anything is
    /// downloaded and with no failure reported.
    /// </summary>
    [Theory]
    [InlineData("deleted")]
    [InlineData("moved")]
    [InlineData("other project")]
    [InlineData("scan pending")]
    public async Task A_kept_warm_up_of_a_file_that_changed_or_is_gone_is_dropped_before_anything_is_downloaded(string what)
    {
        string directory = MediaDerivativeFfmpegFixture.NewDirectory();
        using MediaDerivativeDispatchFixture fixture = QuietFixture(directory);
        try
        {
            MediaDerivativesRequested request = Flagged(fixture, Guid.NewGuid(), 0);
            fixture.AddSourceFile(request);
            fixture.Dispatcher.DeferWarmUp(request).Should().BeTrue();

            Guid fileId = request.SourceProjectFileId!.Value;
            switch (what)
            {
                case "deleted":
                    fixture.ChangeSourceFile(fileId, null);
                    break;
                case "moved":
                    fixture.ChangeSourceFile(fileId, f => f.StorageKey = f.StorageKey.Replace("clip-0", "folder/clip-0", StringComparison.Ordinal));
                    break;
                case "other project":
                    fixture.ChangeSourceFile(fileId, f => f.ProjectId = Guid.NewGuid());
                    break;
                default:
                    fixture.ChangeSourceFile(fileId, f => f.CsamStatus = CsamScanStatus.Pending);
                    break;
            }

            fixture.Dispatcher.StartDeferredWarmUpIfQuiet();
            await fixture.Dispatcher.DeferredWarmUpsSettledAsync().WaitAsync(TimeSpan.FromSeconds(30));
            await UntilAsync(() => fixture.Dispatcher.ActiveCount == 0, "the replay has ended");

            fixture.Workspace.DownloadCount.Should().Be(0);
            fixture.Sink.Stages.Should().BeEmpty("a file that is gone or fine is not reported as failed");

            // Nothing of the dropped request is remembered: the same file has its four tries again.
            Enumerable.Range(0, MediaDerivativeDispatcher.MaxWarmUpDeferrals).All(_ => fixture.Dispatcher.DeferWarmUp(request)).Should().BeTrue();
        }
        finally
        {
            MediaDerivativeFfmpegFixture.TryDelete(directory);
        }
    }

    /// <summary>
    /// A repeat of a request that is still being worked on is dropped. Its warm-up is kept for later only when the
    /// run in flight was not asked to warm the cache: when it was, it will do so itself.
    /// </summary>
    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    public async Task A_repeat_request_is_kept_only_when_the_run_in_flight_will_not_warm_the_cache_itself(bool inFlightAsksToo, int kept)
    {
        string directory = MediaDerivativeFfmpegFixture.NewDirectory();
        using MediaDerivativeDispatchFixture fixture = QuietFixture(directory);
        try
        {
            MediaDerivativesRequested request = Flagged(fixture, Guid.NewGuid(), 0);
            TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Workspace.DownloadGate = gate.Task;
            Task first = fixture.Dispatcher.RunAsync(request with { AnalyzeForWorkflows = inFlightAsksToo }, CancellationToken.None);
            await UntilAsync(() => fixture.Workspace.DownloadCount == 1, "the first run is in its download");

            await fixture.Dispatcher.RunAsync(request, CancellationToken.None);

            fixture.Dispatcher.DeferredWarmUpCount.Should().Be(kept);

            // The first run ends in a failed download. What was kept has no file row and is dropped.
            fixture.Workspace.DownloadFailure = new IOException("the object is gone");
            gate.SetResult();
            await first.WaitAsync(TimeSpan.FromSeconds(30));
            await fixture.Dispatcher.DeferredWarmUpsSettledAsync().WaitAsync(TimeSpan.FromSeconds(30));
            fixture.Workspace.DownloadCount.Should().Be(1);
        }
        finally
        {
            MediaDerivativeFfmpegFixture.TryDelete(directory);
        }
    }

    [Fact]
    public async Task A_draining_engine_starts_no_kept_warm_up_and_forgets_the_ones_that_wait()
    {
        string directory = MediaDerivativeFfmpegFixture.NewDirectory();
        EngineDrainTarget drain = new(NullLogger<EngineDrainTarget>.Instance);
        using MediaDerivativeDispatchFixture fixture = QuietFixture(directory, drain: drain);
        try
        {
            MediaDerivativesRequested request = Flagged(fixture, Guid.NewGuid(), 0);
            fixture.AddSourceFile(request);
            fixture.Dispatcher.DeferWarmUp(request).Should().BeTrue();
            fixture.Dispatcher.DeferWarmUp(Flagged(fixture, Guid.NewGuid(), 1)).Should().BeTrue();

            await drain.BeginDrainAsync(CancellationToken.None);
            fixture.Dispatcher.StartDeferredWarmUpIfQuiet();

            fixture.Dispatcher.DeferredWarmUpCount.Should().Be(0);
            fixture.Dispatcher.ActiveCount.Should().Be(0, "a drain must not wait for work this engine never started");
            fixture.Workspace.DownloadCount.Should().Be(0);
            fixture.Dispatcher.DeferWarmUp(request).Should().BeFalse("a draining engine keeps nothing for later");
            fixture.Dispatcher.DeferredWarmUpCount.Should().Be(0);
        }
        finally
        {
            MediaDerivativeFfmpegFixture.TryDelete(directory);
        }
    }

    // ---- workflow steps that wait for scratch space ----

    private sealed class Space
    {
        private long _free = long.MaxValue / 4;

        public long Free
        {
            get => Volatile.Read(ref _free);
            set => Volatile.Write(ref _free, value);
        }

        public ScratchDiskGuard Guard() => new(() => Free, TimeSpan.FromMinutes(5), TimeSpan.FromMilliseconds(20));

        /// <summary>A stage that waits for space in a flow of its own, as a workflow step does.</summary>
        public static Task<IDisposable> WaitingStage(ScratchDiskGuard guard, CancellationToken ct)
        {
            using (ExecutionContext.SuppressFlow())
                return Task.Run(() => guard.ReserveAsync(1, ct));
        }
    }

    [Fact]
    public async Task The_scratch_guard_tells_when_a_stage_waits_for_space_and_does_not_count_a_quiet_wait()
    {
        Space space = new();
        ScratchDiskGuard guard = space.Guard();
        using CancellationTokenSource stop = new();

        (await guard.ReserveAsync(1, stop.Token)).Dispose();
        guard.HasWaiters.Should().BeFalse("space that is there at once is no wait");

        space.Free = 0;
        Task<IDisposable> quiet;
        using (ScratchDiskGuard.QuietWaits())
            quiet = guard.ReserveAsync(1, stop.Token);
        await Task.Delay(100);
        guard.HasWaiters.Should().BeFalse("background work that gives way must not give way to its own wait");

        Task<IDisposable> stage = Space.WaitingStage(guard, stop.Token);
        await UntilAsync(() => guard.HasWaiters, "the stage waits");

        space.Free = long.MaxValue / 4;
        (await stage.WaitAsync(TimeSpan.FromSeconds(30))).Dispose();
        (await quiet.WaitAsync(TimeSpan.FromSeconds(30))).Dispose();
        guard.HasWaiters.Should().BeFalse();
    }

    [Fact]
    public async Task No_kept_warm_up_starts_while_a_workflow_step_waits_for_scratch_space()
    {
        string directory = MediaDerivativeFfmpegFixture.NewDirectory();
        Space space = new();
        ScratchDiskGuard guard = space.Guard();
        using CancellationTokenSource stop = new();
        using MediaDerivativeDispatchFixture fixture = QuietFixture(directory, scratchGuard: guard);
        fixture.Dispatcher.DeferredRecheckInterval = TimeSpan.FromMilliseconds(50);
        try
        {
            // No file row: once the replay does start it is dropped at once, which is enough to see that it started.
            fixture.Dispatcher.DeferWarmUp(Flagged(fixture, Guid.NewGuid(), 0)).Should().BeTrue();
            space.Free = 0;
            Task<IDisposable> step = Space.WaitingStage(guard, stop.Token);
            await UntilAsync(() => guard.HasWaiters, "the step waits");

            fixture.Dispatcher.StartDeferredWarmUpIfQuiet();
            await Task.Delay(300);
            fixture.Dispatcher.DeferredWarmUpCount.Should().Be(1);

            // The step got its space: the engine looks again by itself, no run has to end for it.
            space.Free = long.MaxValue / 4;
            (await step.WaitAsync(TimeSpan.FromSeconds(30))).Dispose();
            await fixture.Dispatcher.DeferredWarmUpsSettledAsync().WaitAsync(TimeSpan.FromSeconds(30));
            fixture.Dispatcher.DeferredWarmUpCount.Should().Be(0);
        }
        finally
        {
            stop.Cancel();
            MediaDerivativeFfmpegFixture.TryDelete(directory);
        }
    }

    [FfmpegToolsFact]
    public async Task A_workflow_step_that_starts_to_wait_for_scratch_space_stops_the_warm_up_which_is_tried_again_later()
    {
        string directory = MediaDerivativeFfmpegFixture.NewDirectory();
        InMemoryPassCache cache = new();
        Space space = new();
        ScratchDiskGuard guard = space.Guard();
        using CancellationTokenSource stop = new();
        Task<IDisposable>? step = null;
        int silenceCalls = 0;
        Passes passes = new()
        {
            // While the first pass runs, a workflow step asks for scratch space and does not get it: the run that
            // warms still holds the reservation of its own download.
            OnSilence = async (_, ct) =>
            {
                if (Interlocked.Increment(ref silenceCalls) > 1)
                    return;
                space.Free = 0;
                step = Space.WaitingStage(guard, stop.Token);
                await Task.Delay(TimeSpan.FromMinutes(5), ct);
            }
        };
        VideoEditingOptions options = MediaDerivativeFfmpegFixture.BuildOptions(Path.Combine(directory, "scratch"));
        using MediaDerivativeDispatchFixture fixture = MediaDerivativeDispatchFixture.Create(
            directory, options, (runner, o) => passes.Build(cache, runner, o), guard);
        fixture.Dispatcher.DeferredRecheckInterval = TimeSpan.FromMilliseconds(50);
        try
        {
            string source = MediaDerivativeFfmpegFixture.CreateSource(
                directory, "src.mp4", seconds: 2, fps: 30, width: 320, height: 240, withAudio: true);
            fixture.ServeAsSourceObject(source);
            MediaDerivativesRequested request = fixture.Request(ProjectId, UploadKey, "v1", [MediaDerivativeKind.Proxy]) with { AnalyzeForWorkflows = true };
            fixture.AddSourceFile(request);

            await fixture.Dispatcher.RunAsync(request, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(60));

            (passes.Calls.Silence, passes.Calls.Shots).Should().Be((1, 0), "the warm-up stopped in its first pass");
            fixture.Dispatcher.DeferredWarmUpCount.Should().Be(1, "it waits while the step waits");

            space.Free = long.MaxValue / 4;
            (await step!.WaitAsync(TimeSpan.FromSeconds(30))).Dispose();
            await fixture.Dispatcher.DeferredWarmUpsSettledAsync().WaitAsync(TimeSpan.FromSeconds(60));

            (passes.Calls.Silence, passes.Calls.Shots, passes.Calls.Grid, passes.Detector.Seen.Count).Should().Be((2, 1, 1, 1));
            cache.Entries.Should().HaveCount(4);
            fixture.Sink.Stages.Should().NotContain(MediaDerivativeProgressStage.Failed);
        }
        finally
        {
            stop.Cancel();
            MediaDerivativeFfmpegFixture.TryDelete(directory);
        }
    }

    [FfmpegToolsFact]
    public async Task The_engine_switch_ignores_the_flag_and_only_a_video_is_warmed()
    {
        InMemoryPassCache cache = new();
        Passes passes = new();

        await DeriveAsync(passes, cache, analyzeForWorkflows: true, o => o.SourcePassCache.WarmOnUpload = false);
        // A file sent as audio: its peaks are made, the picture passes are not run.
        var audio = await DeriveAsync(passes, cache, analyzeForWorkflows: true, mimeType: "audio/mp4", kind: MediaDerivativeKind.WaveformPeaks);
        audio.Rows.Should().NotBeEmpty();

        passes.Total.Should().Be(0);
        (cache.Identities.Count, cache.Reads, cache.Writes).Should().Be((0, 0, 0));
    }
}
