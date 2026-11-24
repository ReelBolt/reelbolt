using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.VideoEvents;
using ReelBolt.WorkflowEngine.Execution.Caching;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Services.Video;
using ReelBolt.WorkflowEngine.Services.Video.Caching;
using ReelBolt.WorkflowEngine.Services.Video.Events;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>The per-source pass cache: how an entry is keyed, what a stored result looks like, and the store itself.</summary>
public class SourcePassCacheTests
{
    private static readonly Guid ProjectId = Guid.Parse("11111111-2222-4333-8444-555555555555");
    private static readonly SourcePassIdentity Source = new(ProjectId, $"projects/{ProjectId}/userFiles/clip.mp4", "etag-1");
    private static readonly MediaProbeResult Probe = new(30, 30, 1, 1920, 1080, "h264", "aac", 48000);

    // ---- keys ----

    [Fact]
    public void An_entry_key_is_a_project_scoped_hash_over_the_length_prefixed_components_and_the_analyzer_revision()
    {
        string key = SourcePassCache.EntryKey(Source, "shots", "1", "p=1");

        string material = string.Concat(
            new[] { Source.StorageKey, Source.Version, "shots", "1", "p=1", DeterministicStepRevisions.For(StepType.VideoAnalyze) }
                .Select(v => $"{v.Length}:{v}|"));
        string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
        key.Should().Be($"projects/{ProjectId:D}/agentFiles/analysis-cache/{hash}.bin");
        SourcePassCache.IsEntryKey(ProjectId, key).Should().BeTrue();
        SourcePassCache.IsEntryKey(Guid.NewGuid(), key).Should().BeFalse("an entry belongs to one project");
        SourcePassCache.IsEntryKey(ProjectId, $"projects/{ProjectId:D}/userFiles/clip.mp4").Should().BeFalse();
    }

    [Fact]
    public void Any_changed_component_is_another_entry()
    {
        string key = SourcePassCache.EntryKey(Source, "shots", "1", "p=1");

        string[] others =
        [
            SourcePassCache.EntryKey(Source with { StorageKey = Source.StorageKey + "x" }, "shots", "1", "p=1"),
            SourcePassCache.EntryKey(Source with { Version = "etag-2" }, "shots", "1", "p=1"),
            SourcePassCache.EntryKey(Source, "grid", "1", "p=1"),
            SourcePassCache.EntryKey(Source, "shots", "2", "p=1"),
            SourcePassCache.EntryKey(Source, "shots", "1", "p=2"),
            SourcePassCache.EntryKey(Source with { ProjectId = Guid.NewGuid() }, "shots", "1", "p=1")
        ];

        others.Should().NotContain(key);
        others.Should().OnlyHaveUniqueItems();
        SourcePassCache.EntryKey(Source, "shots", "1", "p=1").Should().Be(key, "the same question is the same entry");
    }

    [Fact]
    public void Components_are_length_prefixed_so_text_cannot_move_between_them()
    {
        SourcePassCache.EntryKey(Source, "a|b", "1", "p").Should().NotBe(SourcePassCache.EntryKey(Source, "ab|", "1", "p"));
        SourcePassCache.EntryKey(Source, "x", "y1", "p").Should().NotBe(SourcePassCache.EntryKey(Source, "xy", "1", "p"));
        SourcePassCache.EntryKey(Source, "shots", "1", "").Should().NotBe(SourcePassCache.EntryKey(Source, "shots", "", "1"));
    }

    [Fact]
    public void Every_argument_of_a_pass_is_in_its_parameter_string()
    {
        string source = AnalysisPixels.SourceVariant;

        new[]
        {
            CachedSourcePasses.SilenceParameters(-30, 0.5, 60),
            CachedSourcePasses.SilenceParameters(-31, 0.5, 60),
            CachedSourcePasses.SilenceParameters(-30, 0.6, 60),
            CachedSourcePasses.SilenceParameters(-30, 0.5, 61)
        }.Should().OnlyHaveUniqueItems();

        new[]
        {
            CachedSourcePasses.ShotsParameters(false, 0.3, 60, 0.2, source),
            CachedSourcePasses.ShotsParameters(true, 0.3, 60, 0.2, source),
            CachedSourcePasses.ShotsParameters(true, 0.3, 60, 0.3, source),
            CachedSourcePasses.ShotsParameters(false, 0.4, 60, 0.2, source),
            CachedSourcePasses.ShotsParameters(false, 0.3, 61, 0.2, source),
            CachedSourcePasses.ShotsParameters(false, 0.3, 60, 0.2, "copy:fps=0:profile=-")
        }.Should().OnlyHaveUniqueItems();
        CachedSourcePasses.ShotsParameters(false, 0.3, 60, 0.2, source)
            .Should().Be(CachedSourcePasses.ShotsParameters(false, 0.3, 60, 0.9, source), "the standard detector takes no shortest shot");

        new[]
        {
            CachedSourcePasses.GridParameters(2, 32, 18, 60, 600, GridChroma.Default, source),
            CachedSourcePasses.GridParameters(3, 32, 18, 60, 600, GridChroma.Default, source),
            CachedSourcePasses.GridParameters(2, 33, 18, 60, 600, GridChroma.Default, source),
            CachedSourcePasses.GridParameters(2, 32, 19, 60, 600, GridChroma.Default, source),
            CachedSourcePasses.GridParameters(2, 32, 18, 61, 600, GridChroma.Default, source),
            CachedSourcePasses.GridParameters(2, 32, 18, 60, 601, GridChroma.Default, source),
            CachedSourcePasses.GridParameters(2, 32, 18, 60, 600, GridChroma.FullChroma, source),
            CachedSourcePasses.GridParameters(2, 32, 18, 60, 600, GridChroma.Default, "copy:fps=30:profile=-")
        }.Should().OnlyHaveUniqueItems();

        VideoEventOptions options = new();
        new[]
        {
            CachedSourcePasses.EventsParameters(Probe, options, source),
            CachedSourcePasses.EventsParameters(Probe, options with { MaxEvents = 50 }, source),
            CachedSourcePasses.EventsParameters(Probe, options with { DetectSpeech = false }, source),
            CachedSourcePasses.EventsParameters(Probe, options with { DetectBeats = false }, source),
            CachedSourcePasses.EventsParameters(Probe, options with { SilenceThresholdDb = -40 }, source),
            CachedSourcePasses.EventsParameters(Probe, options with { MinSilenceMs = 500 }, source),
            CachedSourcePasses.EventsParameters(Probe with { DurationSec = 31 }, options, source),
            CachedSourcePasses.EventsParameters(Probe with { FpsNum = 60 }, options, source),
            CachedSourcePasses.EventsParameters(Probe with { FpsDen = 2 }, options, source),
            CachedSourcePasses.EventsParameters(Probe with { Width = 1280 }, options, source),
            CachedSourcePasses.EventsParameters(Probe with { Height = 720 }, options, source),
            CachedSourcePasses.EventsParameters(Probe with { AudioCodec = null }, options, source),
            CachedSourcePasses.EventsParameters(Probe, options, "copy:fps=0:profile=-")
        }.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task The_pixels_variant_names_the_copy_its_frame_rate_and_camera_profile_and_turns_to_the_source_when_the_copy_fails()
    {
        AnalysisPixels.OfSource("source.mp4").Variant.Should().Be("source");
        AnalysisPixels.OfCopy("source.mp4", 30, null, _ => Task.FromResult<string?>("copy.mkv")).Variant.Should().Be("copy:fps=0:profile=-");
        AnalysisPixels.OfCopy("source.mp4", 60, null, _ => Task.FromResult<string?>("copy.mkv")).Variant.Should().Be("copy:fps=30:profile=-");
        AnalysisPixels.OfCopy("source.mp4", 30, "appleLog", _ => Task.FromResult<string?>("copy.mkv")).Variant.Should().Be("copy:fps=0:profile=appleLog");

        int made = 0;
        AnalysisPixels good = AnalysisPixels.OfCopy("source.mp4", 60, null, _ => Task.FromResult<string?>($"copy{++made}.mkv"));
        good.VirtualPath.Should().Be("source.mp4", "the copy is not made until a pass needs the pixels");
        (await good.ReadyAsync(CancellationToken.None)).Should().Be("copy1.mkv");
        (await good.ReadyAsync(CancellationToken.None)).Should().Be("copy1.mkv");
        made.Should().Be(1, "the copy is made once");
        good.CopyMade.Should().BeTrue();
        good.ReadsCopy.Should().BeTrue();

        AnalysisPixels failed = AnalysisPixels.OfCopy("source.mp4", 60, "appleLog", _ => Task.FromResult<string?>(null));
        (await failed.ReadyAsync(CancellationToken.None)).Should().Be("source.mp4");
        failed.Variant.Should().Be("source", "what was read is what a result is stored under");
        failed.ReadsCopy.Should().BeFalse();
        failed.CopyMade.Should().BeFalse();
        failed.Thinned.Should().BeFalse();
    }

    [Fact]
    public async Task A_copy_maker_that_throws_turns_the_later_passes_to_the_source()
    {
        AnalysisPixels pixels = AnalysisPixels.OfCopy("source.mp4", 60, "appleLog", _ => throw new IOException("disk"));

        await pixels.Invoking(p => p.ReadyAsync(CancellationToken.None)).Should().ThrowAsync<IOException>();

        // A caller may swallow that exception and go on: what it reads then is the source, and it is stored as such.
        pixels.Variant.Should().Be(AnalysisPixels.SourceVariant);
        (pixels.ReadsCopy, pixels.CopyMade, pixels.Thinned).Should().Be((false, false, false));
        (await pixels.ReadyAsync(CancellationToken.None)).Should().Be("source.mp4");
    }

    [Fact]
    public async Task After_a_hit_from_the_copy_a_copy_that_fails_no_longer_counts_as_analysed_and_later_passes_are_stored_as_the_source()
    {
        VideoAnalyzePassCacheTests.InMemoryPassCache cache = new();
        VideoAnalyzeEventsTests.PassCalls calls = new();
        var shots = new Moq.Mock<IShotDetector>(Moq.MockBehavior.Strict);
        CachedSourcePasses passes = new(
            new Moq.Mock<ISilenceDetector>().Object, shots.Object, new VideoAnalyzeEventsTests.StubGridSampler(calls), events: null,
            cache, new VideoEditingOptions(), NullLogger.Instance);
        AnalysisPixels pixels = AnalysisPixels.OfCopy("source.mp4", 30, "appleLog", _ => Task.FromResult<string?>(null));

        // An earlier run made the copy and stored the shots it found on it.
        cache.Entries[SourcePassCache.EntryKey(
            Source with { Version = "v1" }, CachedSourcePasses.ShotsPass, CachedSourcePasses.PassRevision,
            CachedSourcePasses.ShotsParameters(false, 0.3, 30, 0.2, pixels.Variant))] = SourcePassPayloads.EncodeSpans([(0, 30)])!;
        SourcePassIdentity source = Source with { Version = "v1" };

        (await passes.ShotsAsync(source, null!, pixels, sensitive: false, 0.3, 30, 0.2, CancellationToken.None)).Should().Equal((0.0, 30.0));
        pixels.AnalysedTheCopy.Should().BeTrue("the shots are what the converted copy gave");

        // The grid is not cached, and this time the copy cannot be made: the grid is read from the unconverted source.
        await passes.GridAsync(source, null!, pixels, 2, 32, 18, 30, 600, chroma: null, CancellationToken.None);

        calls.GridPaths.Should().Equal("source.mp4");
        pixels.AnalysedTheCopy.Should().BeFalse("part of the analysis read the source as it is, so the profile is reported as not applied");
        cache.Entries.Keys.Should().Contain(SourcePassCache.EntryKey(
            source, CachedSourcePasses.GridPass, CachedSourcePasses.PassRevision,
            CachedSourcePasses.GridParameters(2, 32, 18, 30, 600, GridChroma.Default, AnalysisPixels.SourceVariant)));
    }

    [Fact]
    public void Two_reads_of_a_source_agree_only_on_the_same_object_and_version()
    {
        SourcePassIdentityCheck.Unchanged(Source, Source with { }).Should().Be(Source);
        SourcePassIdentityCheck.Unchanged(Source, Source with { Version = "etag-2" }).Should().BeNull();
        SourcePassIdentityCheck.Unchanged(Source, Source with { StorageKey = Source.StorageKey + "x" }).Should().BeNull();
        SourcePassIdentityCheck.Unchanged(Source, Source with { ProjectId = Guid.NewGuid() }).Should().BeNull();
        SourcePassIdentityCheck.Unchanged(Source, null).Should().BeNull("the object is gone, or the store gave no version");
        SourcePassIdentityCheck.Unchanged(null, Source).Should().BeNull();
    }

    // ---- payloads ----

    [Fact]
    public void Spans_come_back_exactly()
    {
        (double, double)[] spans = [(0, 0.1 + 0.2), (1.0 / 3.0, 12.345678901234567), (29.97, 30)];

        SourcePassPayloads.TryDecodeSpans(SourcePassPayloads.EncodeSpans(spans), out var back).Should().BeTrue();
        back.Should().Equal(spans);

        SourcePassPayloads.TryDecodeSpans(SourcePassPayloads.EncodeSpans([]), out var none).Should().BeTrue();
        none.Should().BeEmpty();
    }

    [Fact]
    public void A_grid_comes_back_with_the_same_bytes_and_facts()
    {
        byte[] pixels = new byte[(7 * 32 * 18 * 3) + 5];
        new Random(7).NextBytes(pixels);
        FrameGridResult grid = new(pixels, 32, 18, 1.9980019980019981, 7);

        SourcePassPayloads.TryDecodeGrid(SourcePassPayloads.EncodeGrid(grid), out FrameGridResult? back).Should().BeTrue();

        back!.PixelData.Should().Equal(pixels);
        (back.GridWidth, back.GridHeight, back.EffectiveFps, back.FrameCount).Should().Be((32, 18, 1.9980019980019981, 7));
    }

    [Fact]
    public void Events_come_back_with_every_member()
    {
        VideoEventSet set = new(
            29.97002997002997, 900, 1920, 1080,
            [
                new VideoEvent(VideoEventType.Hit, 90, 96, new VideoEventRegion(0.1, 0.2, 0.3, 0.4), 0.87654321, VideoEventOrigin.Pixels, 0.9, "left", "fast", "graphic"),
                new VideoEvent(VideoEventType.SpeechStart, 12, null, null, 0.8, VideoEventOrigin.Audio, 0.9),
                new VideoEvent(VideoEventType.CaptionTick, 300, 301, null, 0.2, VideoEventOrigin.Model, 0.5, Kind: "text")
            ],
            Suppressed: 3);

        SourcePassPayloads.TryDecodeEvents(SourcePassPayloads.EncodeEvents(set), out VideoEventSet? back).Should().BeTrue();

        (back!.Fps, back.FrameCount, back.Width, back.Height, back.Suppressed).Should().Be((set.Fps, 900, 1920, 1080, 3));
        back.Events.Should().Equal(set.Events);
    }

    [Fact]
    public void A_corrupt_truncated_or_foreign_payload_is_a_miss()
    {
        byte[] spans = SourcePassPayloads.EncodeSpans([(0, 1), (1, 2)])!;
        byte[] pixels = new byte[4 * 16 * 9 * 3];
        new Random(3).NextBytes(pixels);
        byte[] grid = SourcePassPayloads.EncodeGrid(new FrameGridResult(pixels, 16, 9, 2, 4))!;

        SourcePassPayloads.TryDecodeSpans(null, out _).Should().BeFalse();
        SourcePassPayloads.TryDecodeSpans([], out _).Should().BeFalse();
        SourcePassPayloads.TryDecodeSpans(Encoding.UTF8.GetBytes("{\"kind\":\"spans\",\"spans\":[]}"), out _).Should().BeFalse("it is not the stored form");
        SourcePassPayloads.TryDecodeSpans(spans[..(spans.Length / 2)], out _).Should().BeFalse("half a payload");
        SourcePassPayloads.TryDecodeGrid(grid[..(grid.Length / 2)], out _).Should().BeFalse("half a payload");
        SourcePassPayloads.TryDecodeGrid(grid[..^40], out _).Should().BeFalse("a payload cut near its end");

        byte[] flipped = (byte[])grid.Clone();
        for (int i = 20; i < flipped.Length - 8; i += 7)
            flipped[i] ^= 0x5A;
        SourcePassPayloads.TryDecodeGrid(flipped, out _).Should().BeFalse("its bytes were changed");

        // One kind is never read as another.
        SourcePassPayloads.TryDecodeGrid(spans, out _).Should().BeFalse();
        SourcePassPayloads.TryDecodeEvents(spans, out _).Should().BeFalse();
        SourcePassPayloads.TryDecodeSpans(grid, out _).Should().BeFalse();

        // A grid that counts more frames than its bytes hold would send a reader past the end.
        byte[] lying = SourcePassPayloads.EncodeGrid(new FrameGridResult(pixels, 16, 9, 2, 5))!;
        SourcePassPayloads.TryDecodeGrid(lying, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData(1 << 16, 1 << 16, 1 << 30)]
    [InlineData(int.MaxValue, int.MaxValue, int.MaxValue)]
    [InlineData(int.MaxValue, 1, 1)]
    [InlineData(3, 1, int.MaxValue)]
    public void A_grid_header_whose_sizes_multiply_past_a_long_is_a_miss(int width, int height, int frames)
    {
        byte[] pixels = new byte[4 * 16 * 9 * 3];
        byte[] lying = SourcePassPayloads.EncodeGrid(new FrameGridResult(pixels, width, height, 2, frames))!;

        // 65536 x 65536 x 2^30 x 3 wraps to a negative number: a plain multiplication would call these bytes enough.
        unchecked((long)(1 << 30) * (1 << 16) * (1 << 16) * 3).Should().BeNegative();
        SourcePassPayloads.TryDecodeGrid(lying, out FrameGridResult? grid).Should().BeFalse();
        grid.Should().BeNull();
    }

    [Fact]
    public void An_incomplete_event_set_is_never_encoded_and_the_mark_is_not_part_of_a_stored_set()
    {
        VideoEventSet set = new(30, 900, 1920, 1080, [new VideoEvent(VideoEventType.Hit, 90, null, null, 0.9, VideoEventOrigin.Pixels, 0.9)]);

        SourcePassPayloads.EncodeEvents(set with { Degraded = true }).Should().BeNull("another run may find the part that failed");

        SourcePassPayloads.TryDecodeEvents(SourcePassPayloads.EncodeEvents(set), out VideoEventSet? back).Should().BeTrue();
        back!.Degraded.Should().BeFalse();
        System.Text.Json.JsonSerializer.Serialize(set with { Degraded = true }).Should().NotContain("egraded", "it is a fact of one run, never written");
    }

    [Fact]
    public void A_result_json_cannot_carry_or_that_is_too_large_is_not_stored()
    {
        SourcePassPayloads.EncodeSpans([(0, double.NaN)]).Should().BeNull();
        SourcePassPayloads.EncodeGrid(new FrameGridResult(new byte[3], 1, 1, double.PositiveInfinity, 1)).Should().BeNull();
    }

    /// <summary>
    /// A grid that cannot fit an entry is given up while it is compressed, not compressed to the end and then
    /// dropped by the store. Random bytes do not compress, so three megabytes of them never fit one.
    /// </summary>
    [Fact]
    public void A_grid_that_will_not_fit_an_entry_is_not_encoded_and_one_that_fits_still_is()
    {
        const int megabyte = 1024 * 1024;
        byte[] noise = new byte[3 * megabyte];
        new Random(11).NextBytes(noise);
        FrameGridResult noisy = new(noise, 1024, 1024, 2, 1);
        FrameGridResult flat = new(new byte[3 * megabyte], 1024, 1024, 2, 1);

        SourcePassPayloads.EncodeGrid(noisy, maxStoredBytes: megabyte).Should().BeNull();
        SourcePassPayloads.EncodeGrid(noisy, maxStoredBytes: 4 * megabyte).Should().NotBeNull();

        // The same size of plain frames compresses well under the limit and comes back whole.
        byte[]? stored = SourcePassPayloads.EncodeGrid(flat, maxStoredBytes: megabyte);
        stored.Should().NotBeNull();
        SourcePassPayloads.TryDecodeGrid(stored, out FrameGridResult? back).Should().BeTrue();
        back!.PixelData.Should().HaveCount(3 * megabyte);

        // The default limit is the store's own: what this returns, the store accepts.
        SourcePassPayloads.EncodeGrid(noisy)!.LongLength.Should().BeLessThanOrEqualTo(SourcePassCache.MaxPayloadBytes);
    }

    // ---- the store ----

    /// <summary>Object storage as a dictionary: just the calls the cache makes.</summary>
    internal sealed class DictionaryFiles : IProjectFileWorkspace
    {
        public Dictionary<string, byte[]> Objects { get; } = new(StringComparer.Ordinal);
        public string? Version { get; set; } = "\"etag-1\"";
        public bool Throw { get; set; }

        public Task<string?> GetObjectVersionAsync(Guid projectId, string storageKey, CancellationToken ct) =>
            Throw ? throw new IOException("store down") : Task.FromResult(Version);

        public Task<bool> ArtifactExistsAsync(Guid projectId, string storageKey, CancellationToken ct) =>
            Throw ? throw new IOException("store down") : Task.FromResult(Objects.ContainsKey(storageKey));

        public Task DownloadStorageKeyToFileAsync(Guid projectId, string storageKey, string destinationPath, long maxBytes, CancellationToken ct)
        {
            if (Throw)
                throw new IOException("store down");
            if (Objects[storageKey].LongLength > maxBytes)
                throw new IOException("too large");
            File.WriteAllBytes(destinationPath, Objects[storageKey]);
            return Task.CompletedTask;
        }

        public Task<string> UploadArtifactAsync(Guid projectId, string localFilePath, string fileName, string contentType, CancellationToken ct, string category = "agentFiles")
        {
            if (Throw)
                throw new IOException("store down");
            string key = $"projects/{projectId}/{category}/{fileName}";
            Objects[key] = File.ReadAllBytes(localFilePath);
            return Task.FromResult(key);
        }

        public Task<IReadOnlyList<ProjectWorkspaceFile>> ListFilesAsync(Guid projectId, CancellationToken ct) => throw new NotSupportedException();
        public Task<string> ReadFileAsync(Guid projectId, string fileReference, CancellationToken ct) => throw new NotSupportedException();
        public Task<ProjectWorkspaceFile> WriteTextFileAsync(Guid projectId, string fileName, string content, string contentType, CancellationToken ct, string category = "agentFiles", string? originalPath = null) => throw new NotSupportedException();
        public Task DownloadStorageKeyToFileAsync(Guid projectId, string storageKey, string destinationPath, CancellationToken ct) => throw new NotSupportedException();
        public Task<ProjectWorkspaceFile> UploadBinaryFileAsync(Guid projectId, string localFilePath, string fileName, string contentType, SummaryStatus summaryStatus, FileIndexingStatus indexingStatus, CancellationToken ct, string category = "outputFiles", string? originalPath = null) => throw new NotSupportedException();
        public Task<ProjectWorkspaceFile> RegisterStoredObjectAsync(Guid projectId, string storageKey, string fileName, string contentType, long sizeBytes, SummaryStatus summaryStatus, FileIndexingStatus indexingStatus, CancellationToken ct, string category = "outputFiles") => throw new NotSupportedException();
    }

    internal static SourcePassCache NewStore(IProjectFileWorkspace files, bool enabled = true) =>
        new(
            files,
            Options.Create(new VideoEditingOptions
            {
                ScratchPath = Path.Combine(Path.GetTempPath(), "source-pass-cache-tests", Guid.NewGuid().ToString("N")),
                SourcePassCache = new SourcePassCacheOptions { Enabled = enabled }
            }),
            NullLogger<SourcePassCache>.Instance);

    [Fact]
    public async Task The_store_identifies_a_source_by_its_etag_and_keeps_an_entry_under_the_project()
    {
        DictionaryFiles files = new();
        SourcePassCache store = NewStore(files);

        SourcePassIdentity? source = await store.IdentifyAsync(ProjectId, Source.StorageKey, CancellationToken.None);
        source.Should().Be(new SourcePassIdentity(ProjectId, Source.StorageKey, "\"etag-1\""));

        string key = SourcePassCache.EntryKey(source!, "shots", "1", "p");
        (await store.TryReadAsync(source!, key, CancellationToken.None)).Should().BeNull("nothing is stored yet");

        byte[] payload = SourcePassPayloads.EncodeSpans([(0, 1.5)])!;
        await store.WriteAsync(source!, key, payload, CancellationToken.None);

        files.Objects.Keys.Should().Equal(key);
        (await store.TryReadAsync(source!, key, CancellationToken.None)).Should().Equal(payload);
        (await store.TryReadAsync(source!, $"projects/{ProjectId:D}/userFiles/clip.mp4", CancellationToken.None))
            .Should().BeNull("only the cache's own prefix is read");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task A_source_with_no_version_has_no_identity(string? version)
    {
        SourcePassCache store = NewStore(new DictionaryFiles { Version = version });

        (await store.IdentifyAsync(ProjectId, Source.StorageKey, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Switched_off_the_store_identifies_reads_and_writes_nothing()
    {
        DictionaryFiles files = new();
        string key = SourcePassCache.EntryKey(Source, "shots", "1", "p");
        files.Objects[key] = SourcePassPayloads.EncodeSpans([(0, 1)])!;
        SourcePassCache store = NewStore(files, enabled: false);

        (await store.IdentifyAsync(ProjectId, Source.StorageKey, CancellationToken.None)).Should().BeNull();
        (await store.TryReadAsync(Source, key, CancellationToken.None)).Should().BeNull();
        await store.WriteAsync(Source, SourcePassCache.EntryKey(Source, "grid", "1", "p"), [1, 2, 3], CancellationToken.None);
        files.Objects.Should().HaveCount(1);
    }

    [Fact]
    public async Task A_store_that_fails_is_a_miss_and_a_skipped_write_never_an_error()
    {
        SourcePassCache store = NewStore(new DictionaryFiles { Throw = true });
        string key = SourcePassCache.EntryKey(Source, "shots", "1", "p");

        (await store.IdentifyAsync(ProjectId, Source.StorageKey, CancellationToken.None)).Should().BeNull();
        (await store.TryReadAsync(Source, key, CancellationToken.None)).Should().BeNull();
        await store.Invoking(s => s.WriteAsync(Source, key, [1, 2, 3], CancellationToken.None)).Should().NotThrowAsync();
    }

    [Fact]
    public async Task A_payload_over_the_size_limit_is_not_written()
    {
        DictionaryFiles files = new();
        SourcePassCache store = NewStore(files);

        await store.WriteAsync(
            Source, SourcePassCache.EntryKey(Source, "grid", "1", "p"), new byte[SourcePassCache.MaxPayloadBytes + 1], CancellationToken.None);

        files.Objects.Should().BeEmpty();
    }
}
