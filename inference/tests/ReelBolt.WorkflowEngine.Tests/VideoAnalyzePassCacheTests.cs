using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.VideoEvents;
using ReelBolt.WorkflowEngine.Services.Billing;
using ReelBolt.WorkflowEngine.Services.Video;
using ReelBolt.WorkflowEngine.Services.Video.Caching;
using ReelBolt.WorkflowEngine.Services.Video.Events;
using Xunit;
using static ReelBolt.WorkflowEngine.Tests.VideoAnalyzeEventsTests;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// <c>VideoAnalyze</c> reading and writing the per-source pass cache, through the stub detectors of
/// <see cref="VideoAnalyzeEventsTests.RunAsync"/>: a second run on the same source runs no pass again, a changed
/// argument runs only its own pass, and a cache that is off, absent or broken changes nothing in the output.
/// </summary>
public class VideoAnalyzePassCacheTests
{
    /// <summary>The pass cache as a dictionary, counting every call.</summary>
    internal sealed class InMemoryPassCache : ISourcePassCache
    {
        public Dictionary<string, byte[]> Entries { get; } = new(StringComparer.Ordinal);
        public List<SourcePassIdentity> Identities { get; } = [];
        public int Reads { get; private set; }
        public int Hits { get; private set; }
        public int Writes { get; private set; }

        /// <summary>How often the source was identified.</summary>
        public int IdentifyCalls { get; private set; }

        /// <summary>A mark a test sets to count identify calls from a later point.</summary>
        public int IdentifyCallsAtReset { get; set; }

        /// <summary>The version the store answers for the n-th identify call (from 1); null = the object is gone. Unset = always "v1".</summary>
        public Func<int, string?>? VersionAt { get; set; }

        public Task<SourcePassIdentity?> IdentifyAsync(Guid projectId, string storageKey, CancellationToken ct)
        {
            IdentifyCalls++;
            string? version = VersionAt is null ? "v1" : VersionAt(IdentifyCalls);
            if (version is null)
                return Task.FromResult<SourcePassIdentity?>(null);
            SourcePassIdentity identity = new(projectId, storageKey, version);
            Identities.Add(identity);
            return Task.FromResult<SourcePassIdentity?>(identity);
        }

        public Task<byte[]?> TryReadAsync(SourcePassIdentity source, string entryKey, CancellationToken ct)
        {
            Reads++;
            if (!Entries.TryGetValue(entryKey, out byte[]? payload))
                return Task.FromResult<byte[]?>(null);
            Hits++;
            return Task.FromResult<byte[]?>(payload);
        }

        public Task WriteAsync(SourcePassIdentity source, string entryKey, byte[] payload, CancellationToken ct)
        {
            Writes++;
            Entries[entryKey] = payload;
            return Task.CompletedTask;
        }
    }

    /// <summary>A cache that knows the source and then fails every read and every write.</summary>
    private sealed class BrokenPassCache(bool identifyThrows) : ISourcePassCache
    {
        public int Calls { get; private set; }

        public Task<SourcePassIdentity?> IdentifyAsync(Guid projectId, string storageKey, CancellationToken ct)
        {
            Calls++;
            return identifyThrows
                ? throw new IOException("store down")
                : Task.FromResult<SourcePassIdentity?>(new SourcePassIdentity(projectId, storageKey, "v1"));
        }

        public Task<byte[]?> TryReadAsync(SourcePassIdentity source, string entryKey, CancellationToken ct)
        {
            Calls++;
            throw new IOException("store down");
        }

        public Task WriteAsync(SourcePassIdentity source, string entryKey, byte[] payload, CancellationToken ct)
        {
            Calls++;
            throw new IOException("store down");
        }
    }

    private static StubDetector Detector() =>
        new(_ => Set(2, Ev(VideoEventType.Hit, 90, 0.9), Ev(VideoEventType.Appear, 15, 0.5)));

    /// <summary>Every cached pass on: silence, shots, the visual grid and events.</summary>
    private static VideoAnalyzeStepConfig AllPasses(VideoAnalyzeStepConfig c) => c with { AnalyzeEvents = true, DetectSilence = true };

    private static void ShouldHaveRun(PassCalls calls, StubDetector detector, int silence, int shots, int grid, int events)
    {
        (calls.Silence, calls.Shots, calls.Grid, detector.Seen.Count).Should().Be((silence, shots, grid, events));
    }

    [Fact]
    public async Task A_second_run_on_the_same_source_runs_no_pass_again_and_answers_the_same()
    {
        InMemoryPassCache cache = new();
        PassCalls calls = new();
        StubDetector detector = Detector();
        PassCalls plainCalls = new();
        Run plain = await RunAsync(Detector(), AllPasses, calls: plainCalls);

        Run first = await RunAsync(detector, AllPasses, passCache: cache, calls: calls);
        first.Result.Status.Should().Be(StepStatus.Completed, because: first.Result.ErrorDetails ?? first.Result.Output);
        ShouldHaveRun(calls, detector, 1, 1, 1, 1);
        cache.Entries.Should().HaveCount(4, "one entry a pass");
        cache.Hits.Should().Be(0);

        Run second = await RunAsync(detector, AllPasses, passCache: cache, calls: calls);
        ShouldHaveRun(calls, detector, 1, 1, 1, 1);
        cache.Hits.Should().Be(4);
        cache.Entries.Should().HaveCount(4);

        // A miss and a hit both give exactly what a run with no cache gives.
        first.Result.Output.Should().Be(plain.Result.Output);
        second.Result.Output.Should().Be(plain.Result.Output);
        Stable(first.Artifact!).Should().Be(Stable(plain.Artifact!));
        Stable(second.Artifact!).Should().Be(Stable(plain.Artifact!));
        second.Artifact!["events"]!.AsArray().Should().HaveCount(2);
    }

    [Fact]
    public async Task A_changed_argument_runs_only_its_own_pass_again()
    {
        InMemoryPassCache cache = new();
        PassCalls calls = new();
        StubDetector detector = Detector();
        await RunAsync(detector, AllPasses, passCache: cache, calls: calls);
        ShouldHaveRun(calls, detector, 1, 1, 1, 1);

        await RunAsync(detector, c => AllPasses(c) with { SceneThreshold = 0.42 }, passCache: cache, calls: calls);
        ShouldHaveRun(calls, detector, 1, 2, 1, 1);

        await RunAsync(detector, c => AllPasses(c) with { SilenceThresholdDb = -40 }, passCache: cache, calls: calls);
        ShouldHaveRun(calls, detector, 2, 2, 1, 1);

        await RunAsync(detector, c => AllPasses(c) with { VisualSampleFps = 1.0 }, passCache: cache, calls: calls);
        ShouldHaveRun(calls, detector, 2, 2, 2, 1);

        await RunAsync(detector, c => AllPasses(c) with { MaxEvents = 50 }, passCache: cache, calls: calls);
        ShouldHaveRun(calls, detector, 2, 2, 2, 2);

        // The first arguments again: every answer is still there.
        await RunAsync(detector, AllPasses, passCache: cache, calls: calls);
        ShouldHaveRun(calls, detector, 2, 2, 2, 2);
    }

    [Fact]
    public async Task A_changed_source_version_runs_every_pass_again()
    {
        SourcePassCacheTests.DictionaryFiles files = new();
        SourcePassCache store = SourcePassCacheTests.NewStore(files);
        PassCalls calls = new();
        StubDetector detector = Detector();

        await RunAsync(detector, AllPasses, passCache: store, calls: calls);
        await RunAsync(detector, AllPasses, passCache: store, calls: calls);
        ShouldHaveRun(calls, detector, 1, 1, 1, 1);
        files.Objects.Should().HaveCount(4);
        files.Objects.Keys.Should().OnlyContain(k => k.Contains("/agentFiles/analysis-cache/"));

        files.Version = "\"etag-2\"";
        await RunAsync(detector, AllPasses, passCache: store, calls: calls);
        ShouldHaveRun(calls, detector, 2, 2, 2, 2);
        files.Objects.Should().HaveCount(8);
    }

    [Fact]
    public async Task Switched_off_every_pass_runs_every_time_and_nothing_is_stored()
    {
        SourcePassCacheTests.DictionaryFiles files = new();
        SourcePassCache store = SourcePassCacheTests.NewStore(files, enabled: false);
        PassCalls calls = new();
        StubDetector detector = Detector();
        Run plain = await RunAsync(Detector(), AllPasses, calls: new PassCalls());

        Run first = await RunAsync(detector, AllPasses, passCache: store, calls: calls);
        Run second = await RunAsync(detector, AllPasses, passCache: store, calls: calls);

        ShouldHaveRun(calls, detector, 2, 2, 2, 2);
        files.Objects.Should().BeEmpty();
        first.Result.Output.Should().Be(plain.Result.Output);
        second.Result.Output.Should().Be(plain.Result.Output);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_cache_that_throws_on_every_call_changes_nothing_in_the_output(bool identifyThrows)
    {
        BrokenPassCache broken = new(identifyThrows);
        PassCalls calls = new();
        StubDetector detector = Detector();
        Run plain = await RunAsync(Detector(), AllPasses, calls: new PassCalls());

        Run run = await RunAsync(detector, AllPasses, passCache: broken, calls: calls);

        run.Result.Status.Should().Be(StepStatus.Completed, because: run.Result.ErrorDetails ?? run.Result.Output);
        broken.Calls.Should().BeGreaterThan(0);
        ShouldHaveRun(calls, detector, 1, 1, 1, 1);
        run.Result.Output.Should().Be(plain.Result.Output);
        Stable(run.Artifact!).Should().Be(Stable(plain.Artifact!));
    }

    // ---- the version belongs to the bytes that were read ----

    [Fact]
    public async Task A_source_replaced_while_it_was_staged_is_analysed_but_nothing_is_read_or_stored_for_it()
    {
        // The first read (before the download) sees the old object, every later one the object that replaced it.
        InMemoryPassCache cache = new() { VersionAt = call => call == 1 ? "v1" : "v2" };
        PassCalls calls = new();
        StubDetector detector = Detector();
        Run plain = await RunAsync(Detector(), AllPasses, calls: new PassCalls());

        Run run = await RunAsync(detector, AllPasses, passCache: cache, calls: calls);

        run.Result.Status.Should().Be(StepStatus.Completed, because: run.Result.ErrorDetails ?? run.Result.Output);
        ShouldHaveRun(calls, detector, 1, 1, 1, 1);
        cache.IdentifyCalls.Should().Be(2, "once before the bytes are staged and once after, and never again for a source with no known version");
        (cache.Reads, cache.Writes).Should().Be((0, 0), "the staged bytes have no known version");
        run.Result.Output.Should().Be(plain.Result.Output);
    }

    [Theory]
    [InlineData("v2")]
    [InlineData(null)]
    public async Task A_source_replaced_or_deleted_while_its_passes_ran_gets_no_entry(string? laterVersion)
    {
        // Both reads around the staging agree. By the time the first result is ready the object is another one, or gone.
        InMemoryPassCache cache = new() { VersionAt = call => call <= 2 ? "v1" : laterVersion };
        PassCalls calls = new();
        StubDetector detector = Detector();
        Run plain = await RunAsync(Detector(), AllPasses, calls: new PassCalls());

        Run run = await RunAsync(detector, AllPasses, passCache: cache, calls: calls);

        run.Result.Status.Should().Be(StepStatus.Completed, because: run.Result.ErrorDetails ?? run.Result.Output);
        ShouldHaveRun(calls, detector, 1, 1, 1, 1);
        cache.Reads.Should().Be(4, "each pass looked for an answer of the bytes it was about to read");
        cache.Writes.Should().Be(0, "results of bytes that are no longer the stored object are not kept");
        run.Result.Output.Should().Be(plain.Result.Output);
    }

    [Fact]
    public async Task The_event_pass_uses_the_version_read_around_the_staging_and_not_a_later_one()
    {
        InMemoryPassCache cache = new();
        PassCalls calls = new();
        StubDetector detector = Detector();
        await RunAsync(detector, AllPasses, passCache: cache, calls: calls);
        cache.Entries.Should().HaveCount(4);
        int reads = cache.Reads;

        // A second run stages "v1" bytes; the object is replaced right after. Every read asks for "v1", the event
        // pass too, although it runs long after the staging.
        cache.VersionAt = call => call <= cache.IdentifyCallsAtReset + 2 ? "v1" : "v2";
        cache.IdentifyCallsAtReset = cache.IdentifyCalls;
        await RunAsync(detector, AllPasses, passCache: cache, calls: calls);

        ShouldHaveRun(calls, detector, 1, 1, 1, 1);
        (cache.Reads - reads, cache.Hits).Should().Be((4, 4));
        cache.Identities.Should().NotContain(i => i.Version == "v2", "nothing asked the store again: every pass was a hit");
    }

    // ---- an incomplete event result ----

    [Fact]
    public async Task An_incomplete_event_result_is_used_by_the_step_but_never_stored()
    {
        InMemoryPassCache cache = new();
        PassCalls calls = new();
        bool degraded = true;
        StubDetector detector = new(_ => Set(2, Ev(VideoEventType.Hit, 90, 0.9), Ev(VideoEventType.Appear, 15, 0.5)) with { Degraded = degraded });
        Run plain = await RunAsync(Detector(), AllPasses, calls: new PassCalls());

        // The speech or beat part failed in this run: its events are used, and the cache keeps nothing of them.
        Run first = await RunAsync(detector, AllPasses, passCache: cache, calls: calls);
        first.Result.Status.Should().Be(StepStatus.Completed, because: first.Result.ErrorDetails ?? first.Result.Output);
        // The same events are offered; the output only adds the mark that keeps it out of the step cache too.
        first.Meta["events"]!["degraded"]!.GetValue<bool>().Should().BeTrue();
        first.Result.Output.Replace(",\"degraded\":true}}}", "}}}", StringComparison.Ordinal).Should().Be(plain.Result.Output);
        cache.Entries.Should().HaveCount(3, "silence, shots and the grid; not the incomplete events");

        // The next run finds the part again, and that complete result is the one that is kept.
        degraded = false;
        await RunAsync(detector, AllPasses, passCache: cache, calls: calls);
        ShouldHaveRun(calls, detector, 1, 1, 1, 2);
        cache.Entries.Should().HaveCount(4);

        await RunAsync(detector, AllPasses, passCache: cache, calls: calls);
        ShouldHaveRun(calls, detector, 1, 1, 1, 2);
    }

    /// <summary>ffmpeg that always works: it writes a few bytes where its last argument points.</summary>
    private static VideoToolResult WritesOutput(IReadOnlyList<string> args)
    {
        File.WriteAllBytes(args[^1], [1, 2, 3, 4]);
        return new VideoToolResult(0, "", "", TimedOut: false);
    }

    [Fact]
    public async Task A_trimmed_source_never_touches_the_cache()
    {
        InMemoryPassCache cache = new();
        PassCalls calls = new();
        StubDetector detector = Detector();
        VideoAnalyzeStepConfig Trimmed(VideoAnalyzeStepConfig c) => AllPasses(c) with { Source = c.Source with { InSec = 2, OutSec = 12 } };

        Run first = await RunAsync(detector, Trimmed, passCache: cache, calls: calls, ffmpeg: WritesOutput);
        await RunAsync(detector, Trimmed, passCache: cache, calls: calls, ffmpeg: WritesOutput);

        first.Result.Status.Should().Be(StepStatus.Completed, because: first.Result.ErrorDetails ?? first.Result.Output);
        ShouldHaveRun(calls, detector, 2, 2, 2, 2);
        (cache.Identities.Count, cache.Reads, cache.Writes).Should().Be((0, 0, 0));
    }

    [Fact]
    public async Task A_still_image_never_touches_the_cache()
    {
        InMemoryPassCache cache = new();
        PassCalls calls = new();
        string photo = SourceKeyWithExtension(".png");

        Run first = await RunAsync(null, c => c with { DetectSilence = true }, passCache: cache, calls: calls, sourceKey: photo, ffmpeg: WritesOutput);
        await RunAsync(null, c => c with { DetectSilence = true }, passCache: cache, calls: calls, sourceKey: photo, ffmpeg: WritesOutput);

        first.Result.Status.Should().Be(StepStatus.Completed, because: first.Result.ErrorDetails ?? first.Result.Output);
        (calls.Silence, calls.Grid).Should().Be((2, 2), "the passes that run on a still's clip run every time");
        (cache.Identities.Count, cache.Reads, cache.Writes).Should().Be((0, 0, 0));
    }

    // ---- the analysis copy ----

    private const string Source = "source.mp4";
    private const string Copy = "analysis.mkv";

    /// <summary>Long and above 1080p: by policy its pixel passes read the small analysis copy.</summary>
    private static readonly MediaProbeResult LargeSource = new(300, 30, 1, 3840, 2160, "h264", "aac", 48000);

    /// <summary>Counts the analysis copies made, and makes them with <paramref name="make"/>.</summary>
    private sealed class CopyCounter(Func<IReadOnlyList<string>, VideoToolResult> make)
    {
        public int Made { get; private set; }

        public VideoToolResult Run(IReadOnlyList<string> args)
        {
            if (!args[^1].EndsWith(Copy, StringComparison.Ordinal))
                return WritesOutput(args);
            Made++;
            return make(args);
        }
    }

    [Fact]
    public async Task The_analysis_copy_is_made_on_a_miss_and_never_when_every_pass_that_reads_it_hits()
    {
        InMemoryPassCache cache = new();
        PassCalls calls = new();
        StubDetector detector = Detector();
        CopyCounter copies = new(WritesOutput);
        CopyCounter plainCopies = new(WritesOutput);
        PassCalls plainCalls = new();
        StubDetector plainDetector = Detector();
        Run plain = await RunAsync(plainDetector, AllPasses, calls: plainCalls, probeResult: LargeSource, ffmpeg: plainCopies.Run);
        plainCopies.Made.Should().Be(1);
        plainCalls.ShotPaths.Should().Equal(Copy);
        plainCalls.GridPaths.Should().Equal(Copy);
        plainDetector.PixelPaths.Should().Equal(Copy);

        Run first = await RunAsync(detector, AllPasses, passCache: cache, calls: calls, probeResult: LargeSource, ffmpeg: copies.Run);
        first.Result.Status.Should().Be(StepStatus.Completed, because: first.Result.ErrorDetails ?? first.Result.Output);
        copies.Made.Should().Be(1, "the first pass that reads the copy missed");
        calls.ShotPaths.Should().Equal(Copy);
        calls.GridPaths.Should().Equal(Copy);
        detector.PixelPaths.Should().Equal(Copy);

        Run second = await RunAsync(detector, AllPasses, passCache: cache, calls: calls, probeResult: LargeSource, ffmpeg: copies.Run);
        copies.Made.Should().Be(1, "every pass that reads the copy was answered from the cache");
        ShouldHaveRun(calls, detector, 1, 1, 1, 1);

        first.Result.Output.Should().Be(plain.Result.Output);
        second.Result.Output.Should().Be(plain.Result.Output);
        Stable(second.Artifact!).Should().Be(Stable(plain.Artifact!));

        // Only the event pass misses now: it makes the copy itself, and reads it.
        await RunAsync(detector, c => AllPasses(c) with { MaxEvents = 50 }, passCache: cache, calls: calls, probeResult: LargeSource, ffmpeg: copies.Run);
        copies.Made.Should().Be(2);
        ShouldHaveRun(calls, detector, 1, 1, 1, 2);
        detector.PixelPaths.Should().Equal(Copy, Copy);
    }

    [Fact]
    public async Task A_copy_that_cannot_be_made_analyses_the_source_and_stores_the_answers_as_read_from_the_source()
    {
        InMemoryPassCache cache = new();
        PassCalls calls = new();
        StubDetector detector = Detector();
        CopyCounter copies = new(_ => new VideoToolResult(1, "", "no encoder", TimedOut: false));
        VideoAnalyzeStepConfig? used = null;

        Run run = await RunAsync(detector, c => used = AllPasses(c), passCache: cache, calls: calls, probeResult: LargeSource, ffmpeg: copies.Run);

        run.Result.Status.Should().Be(StepStatus.Completed, because: run.Result.ErrorDetails ?? run.Result.Output);
        copies.Made.Should().Be(1, "one try, then the source for every pass");
        calls.ShotPaths.Should().Equal(Source);
        calls.GridPaths.Should().Equal(Source);
        detector.PixelPaths.Should().Equal(Source);

        SourcePassIdentity source = cache.Identities[0];
        cache.Entries.Keys.Should().Contain(SourcePassCache.EntryKey(
            source, CachedSourcePasses.ShotsPass, CachedSourcePasses.PassRevision,
            CachedSourcePasses.ShotsParameters(false, used!.SceneThreshold, LargeSource.DurationSec, used.MinShotMs / 1000.0, AnalysisPixels.SourceVariant)));
        cache.Entries.Keys.Should().Contain(SourcePassCache.EntryKey(
            source, CachedSourcePasses.EventsPass, CachedSourcePasses.PassRevision,
            CachedSourcePasses.EventsParameters(LargeSource, new VideoEventOptions(), AnalysisPixels.SourceVariant)));
    }

    [Fact]
    public async Task A_copy_that_runs_out_of_time_still_stops_the_step_as_too_demanding()
    {
        CopyCounter copies = new(_ => new VideoToolResult(-1, "", "", TimedOut: true));

        Run run = await RunAsync(Detector(), AllPasses, passCache: new InMemoryPassCache(), calls: new PassCalls(), probeResult: LargeSource, ffmpeg: copies.Run);

        run.Result.Status.Should().Be(StepStatus.Failed);
        run.Result.ErrorDetails.Should().Be(FailureMessages.UserMessage(VideoSourceLimits.TooDemandingCode));
    }
}
