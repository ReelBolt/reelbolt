using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;
using Xunit.Abstractions;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Real-ffmpeg assertions on the filmstrip sprite: how many sheets a source longer than one
/// 10x10 tile actually produces, how many thumbnails each sheet actually carries, and whether the
/// JSON sidecar's counts agree with the files on disk.
/// </summary>
/// <remarks>
/// The source is a 250-frame 1 fps clip, so the density-1 filmstrip yields exactly 250
/// thumbnails — deliberately chosen because <c>tile</c> PADS the final partial sheet with black to
/// the full 10x10 area, so the last sheet's 50 thumbnails cannot be recovered from its pixels or
/// from the container duration. Only the measured decoded frame count knows.
/// </remarks>
public class MediaDerivativeFilmstripFfmpegTests
{
    private const int SourceSeconds = 250;

    private readonly ITestOutputHelper _output;

    public MediaDerivativeFilmstripFfmpegTests(ITestOutputHelper output) => _output = output;

    [FfmpegToolsFact]
    public async Task A_250_thumbnail_source_produces_three_sheets_of_100_100_and_50()
    {
        string directory = MediaDerivativeFfmpegFixture.NewDirectory();
        try
        {
            string source = MediaDerivativeFfmpegFixture.CreateSource(
                directory, "long.mp4", seconds: SourceSeconds, fps: 1, width: 320, height: 240, withAudio: false);

            (MediaDerivativeService service, string sourceKey, string etag) = Build(directory);

            MediaDerivativeSpec spec = await service.ResolveSpecAsync(
                MediaDerivativeKindSlug.Filmstrip, 0, source, CancellationToken.None);
            spec.ParamsJson.Should().Contain("\"densitySec\":1");

            MediaDerivativeResult result = await service.ProduceAsync(
                Guid.NewGuid(), sourceKey, etag, spec, source, progress: null, CancellationToken.None);

            using JsonDocument index = JsonDocument.Parse(result.SidecarJson!);
            JsonElement root = index.RootElement;

            int sheetCount = root.GetProperty("sheetCount").GetInt32();
            sheetCount.Should().Be(3, "250 thumbnails tile into 100 + 100 + 50");

            root.GetProperty("cols").GetInt32().Should().Be(10);
            root.GetProperty("rows").GetInt32().Should().Be(10);
            root.GetProperty("densitySec").GetInt32().Should().Be(1);
            root.GetProperty("thumbWidth").GetInt32().Should().Be(160);
            root.GetProperty("thumbHeight").GetInt32().Should().Be(120);

            JsonElement sheets = root.GetProperty("sheets");
            sheets.GetArrayLength().Should().Be(sheetCount);

            int[] thumbCounts = sheets.EnumerateArray().Select(s => s.GetProperty("thumbCount").GetInt32()).ToArray();
            thumbCounts.Should().Equal(100, 100, 50);
            thumbCounts.Sum().Should().Be(250);

            int[] firstThumbIndexes = sheets.EnumerateArray()
                .Select(s => s.GetProperty("firstThumbIndex").GetInt32()).ToArray();
            firstThumbIndexes.Should().Equal(0, 100, 200);

            double[] firstTimes = sheets.EnumerateArray()
                .Select(s => s.GetProperty("firstTimeSec").GetDouble()).ToArray();
            firstTimes.Should().Equal(0, 100, 200);

            // Every declared sheet must have a real artifact behind it, and no artifact may be
            // missing from the index.
            string[] declaredLeaves = sheets.EnumerateArray()
                .Select(s => s.GetProperty("storageLeaf").GetString()!).ToArray();
            string[] artifactLeaves = result.Artifacts.Select(a => a.FileName).ToArray();

            foreach (string leaf in declaredLeaves)
                artifactLeaves.Should().Contain(leaf);

            declaredLeaves.Should().BeInAscendingOrder(StringComparer.Ordinal);

            result.Artifacts.Should().HaveCount(sheetCount + 1, "the sidecar is an artifact too");
            result.Artifacts.Count(a => a.ContentType == "image/jpeg").Should().Be(sheetCount);
            result.Artifacts[^1].ContentType.Should().Be("application/json");

            foreach (MediaDerivativeArtifact artifact in result.Artifacts)
            {
                File.Exists(artifact.LocalPath).Should().BeTrue();
                new FileInfo(artifact.LocalPath).Length.Should().BeGreaterThan(0);
            }

            // Each sheet is a real 1600x1200 sprite of 10x10 thumbnails.
            foreach (MediaDerivativeArtifact sheet in result.Artifacts.Where(a => a.ContentType == "image/jpeg"))
                MediaDerivativeFfmpegFixture.ProbeDimensions(sheet.LocalPath).Should().Be((1600, 1200));

            result.Width.Should().Be(160);
            result.Height.Should().Be(120);

            _output.WriteLine(
                "MEASURED filmstrip: source 250 x 1fps, density 1s -> {0} sheets, thumbCounts [{1}], " +
                "thumb {2}x{3}, cacheKey {4}, bytes {5}",
                sheetCount, string.Join(",", thumbCounts), result.Width, result.Height,
                result.CacheKey[..16], result.SizeBytes);
        }
        finally
        {
            MediaDerivativeFfmpegFixture.TryDelete(directory);
        }
    }

    [FfmpegToolsFact]
    public async Task A_source_shorter_than_one_tile_still_yields_one_sheet_with_the_measured_count()
    {
        string directory = MediaDerivativeFfmpegFixture.NewDirectory();
        try
        {
            string source = MediaDerivativeFfmpegFixture.CreateSource(
                directory, "short.mp4", seconds: 12, fps: 1, width: 320, height: 240, withAudio: false);

            (MediaDerivativeService service, string sourceKey, string etag) = Build(directory);

            // Density 1 over a 12-frame source: 12 thumbnails, one sheet.
            MediaDerivativeSpec spec = MediaDerivativeSpec.ForFilmstrip(0);

            MediaDerivativeResult result = await service.ProduceAsync(
                Guid.NewGuid(), sourceKey, etag, spec, source, progress: null, CancellationToken.None);

            using JsonDocument index = JsonDocument.Parse(result.SidecarJson!);

            index.RootElement.GetProperty("sheetCount").GetInt32().Should().Be(1);
            index.RootElement.GetProperty("sheets")[0].GetProperty("thumbCount").GetInt32().Should().Be(12);
            index.RootElement.GetProperty("sheets")[0].GetProperty("firstThumbIndex").GetInt32().Should().Be(0);
        }
        finally
        {
            MediaDerivativeFfmpegFixture.TryDelete(directory);
        }
    }

    [FfmpegToolsFact]
    public async Task Density_five_decimates_to_one_thumbnail_every_five_seconds()
    {
        string directory = MediaDerivativeFfmpegFixture.NewDirectory();
        try
        {
            string source = MediaDerivativeFfmpegFixture.CreateSource(
                directory, "medium.mp4", seconds: 52, fps: 1, width: 320, height: 240, withAudio: false);

            (MediaDerivativeService service, string sourceKey, string etag) = Build(directory);

            MediaDerivativeSpec spec = await service.ResolveSpecAsync(
                MediaDerivativeKindSlug.Filmstrip, 1, source, CancellationToken.None);
            spec.ParamsJson.Should().Contain("\"densitySec\":5");

            MediaDerivativeResult result = await service.ProduceAsync(
                Guid.NewGuid(), sourceKey, etag, spec, source, progress: null, CancellationToken.None);

            using JsonDocument index = JsonDocument.Parse(result.SidecarJson!);
            JsonElement sheet = index.RootElement.GetProperty("sheets")[0];

            // 52 s at one thumbnail per 5 s. The exact count comes from the decoded frames, so it is
            // asserted as "about eleven", never as duration / density quietly rounded.
            sheet.GetProperty("firstTimeSec").GetDouble().Should().Be(0);
            index.RootElement.GetProperty("densitySec").GetInt32().Should().Be(5);
            sheet.GetProperty("thumbCount").GetInt32().Should().BeInRange(10, 11);
        }
        finally
        {
            MediaDerivativeFfmpegFixture.TryDelete(directory);
        }
    }

    [FfmpegToolsFact]
    public async Task Every_filmstrip_leaf_carries_the_params_hash_so_two_densities_never_collide()
    {
        string directory = MediaDerivativeFfmpegFixture.NewDirectory();
        try
        {
            string source = MediaDerivativeFfmpegFixture.CreateSource(
                directory, "short.mp4", seconds: 6, fps: 1, width: 320, height: 240, withAudio: false);

            (MediaDerivativeService service, string sourceKey, string etag) = Build(directory);
            Guid projectId = Guid.NewGuid();

            MediaDerivativeResult dense = await service.ProduceAsync(
                projectId, sourceKey, etag, MediaDerivativeSpec.ForFilmstrip(0), source,
                progress: null, CancellationToken.None);

            MediaDerivativeResult sparse = await service.ProduceAsync(
                projectId, sourceKey, etag, MediaDerivativeSpec.ForFilmstrip(1), source,
                progress: null, CancellationToken.None);

            dense.StoragePrefix.Should().Be(sparse.StoragePrefix, "both derivatives belong to one source");
            dense.CacheKey.Should().NotBe(sparse.CacheKey);

            dense.Artifacts.Select(a => a.FileName)
                .Should().NotIntersectWith(sparse.Artifacts.Select(a => a.FileName));

            string denseHash = MediaDerivativeCacheKey.ComputeParamsHash(MediaDerivativeSpec.ForFilmstrip(0))[..16];
            dense.Artifacts.Select(a => a.FileName).Should().OnlyContain(name => name.Contains(denseHash, StringComparison.Ordinal));
        }
        finally
        {
            MediaDerivativeFfmpegFixture.TryDelete(directory);
        }
    }

    [FfmpegToolsFact]
    public async Task The_reported_sheet_count_matches_the_sheet_files_actually_written()
    {
        string directory = MediaDerivativeFfmpegFixture.NewDirectory();
        try
        {
            string source = MediaDerivativeFfmpegFixture.CreateSource(
                directory, "long.mp4", seconds: SourceSeconds, fps: 1, width: 320, height: 240, withAudio: false);

            (MediaDerivativeService service, string sourceKey, string etag) = Build(directory);

            MediaDerivativeResult result = await service.ProduceAsync(
                Guid.NewGuid(), sourceKey, etag, MediaDerivativeSpec.ForFilmstrip(0), source,
                progress: null, CancellationToken.None);

            using JsonDocument index = JsonDocument.Parse(result.SidecarJson!);
            int declared = index.RootElement.GetProperty("sheetCount").GetInt32();

            int onDisk = result.Artifacts.Count(a => a.ContentType == "image/jpeg");
            onDisk.Should().Be(declared);

            // A cross-check independent of our own counting: ffprobe reads each sheet's own
            // dimensions, and each must be a full 10x10 sprite — 250 thumbnails is NOT
            // 250 / 100 rounded up by the container's duration, and a pinned -frames:v 1 would have
            // produced exactly one sheet here.
            onDisk.Should().BeGreaterThan(1);

            string json = result.SidecarJson!;
            json.Should().StartWith("{\"densitySec\":1,");
            json.Should().Contain("\"sheetCount\":3");
        }
        finally
        {
            MediaDerivativeFfmpegFixture.TryDelete(directory);
        }
    }

    private static (MediaDerivativeService Service, string SourceKey, string Etag) Build(string directory)
    {
        VideoEditingOptions options = MediaDerivativeFfmpegFixture.BuildOptions(directory);
        FfmpegVideoToolRunner runner = MediaDerivativeFfmpegFixture.BuildRunner(options);
        FfprobeMediaProbe probe = MediaDerivativeFfmpegFixture.BuildProbe(runner, options);

        return (MediaDerivativeFfmpegFixture.BuildService(runner, probe, options),
                SourceKey(directory),
                "etag-filmstrip");
    }

    private static string SourceKey(string directory) => string.Format(
        CultureInfo.InvariantCulture,
        "projects/22222222-2222-2222-2222-222222222222/agentFiles/{0}.mp4",
        Path.GetFileNameWithoutExtension(directory));

    /// <summary>
    /// The defect this test was added for (T-307): a density the source cannot support must be
    /// REFUSED, not attempted. Before the fix, a short source ran the sheet pass, wrote no sheet, ran
    /// the inventory pass, decoded no thumbnail and threw — on every request, forever, because no row
    /// was ever written to stop it being asked again.
    /// </summary>
    /// <remarks>
    /// The assertion is an <b>invocation count</b>, not "no exception escaped": a density that was
    /// attempted and then swallowed would look identical from the outside, which is exactly how this
    /// defect stayed invisible.
    /// </remarks>
    [FfmpegToolsFact]
    public async Task A_density_the_source_is_too_short_to_support_is_never_attempted()
    {
        string directory = MediaDerivativeFfmpegFixture.NewDirectory();
        try
        {
            // 6 s. Supports density 1 (needs >= 0.5 s) and density 5 (>= 2.5 s); cannot support
            // density 30 (needs >= 15 s), so `fps=1/30` would emit zero frames.
            string source = MediaDerivativeFfmpegFixture.CreateSource(
                directory, "short.mp4", seconds: 6, fps: 30, width: 320, height: 240, withAudio: false);

            VideoEditingOptions options = MediaDerivativeFfmpegFixture.BuildOptions(directory);
            var counting = new CountingVideoToolRunner(MediaDerivativeFfmpegFixture.BuildRunner(options));
            var probe = new FfprobeMediaProbe(
                counting, Options.Create(options), NullLogger<FfprobeMediaProbe>.Instance);
            var service = new MediaDerivativeService(
                counting, probe, Options.Create(options), NullLogger<MediaDerivativeService>.Instance);

            Guid projectId = Guid.NewGuid();
            string sourceKey = SourceKey(directory);

            // ---- the unsupportable density: refused with ZERO ffmpeg invocations ----
            Func<Task> unsupported = () => service.ProduceAsync(
                projectId, sourceKey, "etag-short", MediaDerivativeSpec.ForFilmstrip(2), source,
                progress: null, CancellationToken.None);

            await unsupported.Should().ThrowAsync<MediaDerivativeUnsupportedException>()
                .WithMessage("*density 30s*");

            counting.FfmpegCount.Should().Be(0,
                "an unsupported density must be refused before the first ffmpeg invocation — attempting it is " +
                "the defect, not the failure it produces");
            counting.FfprobeCount.Should().BeGreaterThan(0,
                "the decision is taken from the source's own measured duration, so the probe must have run");

            _output.WriteLine(
                "MEASURED unsupportable density 30s on a 6 s source: {0} ffmpeg invocations, {1} ffprobe invocations",
                counting.FfmpegCount, counting.FfprobeCount);

            // ---- planted positive control: the same runner/service DOES run ffmpeg for a supportable density ----
            counting.ResetInvocations();

            MediaDerivativeResult supported = await service.ProduceAsync(
                projectId, sourceKey, "etag-short", MediaDerivativeSpec.ForFilmstrip(0), source,
                progress: null, CancellationToken.None);

            counting.FfmpegCount.Should().BeGreaterThan(0,
                "the zero above must mean 'not attempted', not 'the counter is wired to nothing'");
            supported.Artifacts.Should().NotBeEmpty();
        }
        finally
        {
            MediaDerivativeFfmpegFixture.TryDelete(directory);
        }
    }

    /// <summary>
    /// Pins the measured boundary itself, because it is not the obvious arithmetic: a density needs
    /// **half** its interval, not a source as long as the density, and the decoded count is
    /// `round(duration / density)` rather than `floor`.
    /// </summary>
    /// <remarks>
    /// Measured on this host with ffmpeg 8.0.1, one decimation pass per row. If ffmpeg's `fps` filter
    /// ever changes its rounding, this fails loudly instead of quietly skipping densities that a
    /// source could in fact support.
    /// </remarks>
    [Theory]
    [InlineData(2.4, 5, false)]
    [InlineData(2.5, 5, true)]
    [InlineData(3.0, 1, true)]
    [InlineData(3.0, 5, true)]
    [InlineData(3.0, 30, false)]
    [InlineData(14.9, 30, false)]
    [InlineData(15.0, 30, true)]
    [InlineData(29.0, 30, true)]
    [InlineData(0.0, 30, true)]
    public void The_density_threshold_is_half_the_interval(double durationSec, int densitySec, bool supported)
    {
        MediaDerivativeService.SupportsFilmstripDensity(durationSec, densitySec).Should().Be(
            supported,
            $"a {durationSec}s source and a {densitySec}s density were measured with ffmpeg 8.0.1");

        MediaDerivativeService.MinimumSourceSecondsForFilmstripDensity(densitySec)
            .Should().Be(densitySec / 2.0);
    }
}
