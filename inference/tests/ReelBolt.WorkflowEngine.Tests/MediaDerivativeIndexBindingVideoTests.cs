#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;
using Xunit.Abstractions;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Coverage for <see cref="MediaDerivativeIndexBinding"/> and its use by the dispatcher: a produced
/// filmstrip row must name the JSON index sidecar that describes it, so the editor's hover-scrub has
/// a data source carrying density, sheet geometry and per-sheet thumbnail ranges.
///
/// <list type="number">
/// <item><description>the binding resolves the sidecar artifact and its leaf is the one
/// <see cref="FilmstripIndex.IndexLeaf"/> predicts;</description></item>
/// <item><description>a non-filmstrip result, and a filmstrip with no JSON artifact, both bind to
/// null — the column's normal value;</description></item>
/// <item><description>a leaf that is NOT the predicted one is a <b>finding</b>: no pointer is
/// produced, rather than one chosen between two disagreeing names;</description></item>
/// <item><description>end to end, against real ffmpeg: a produced filmstrip row's
/// <c>IndexStorageKey</c> names an object the run actually uploaded, every other kind's row leaves it
/// null, and two densities get two different keys.</description></item>
/// </list>
/// </remarks>
public class MediaDerivativeIndexBindingVideoTests
{
    private const string StoragePrefix = "projects/11111111-2222-3333-4444-555555555555/agentFiles/media-derivatives/0123456789abcdef";

    private readonly ITestOutputHelper _output;

    public MediaDerivativeIndexBindingVideoTests(ITestOutputHelper output) => _output = output;

    // =========================================================================================
    // The binding — pure
    // =========================================================================================

    [Fact]
    public void A_filmstrip_binds_to_the_index_leaf_the_builder_writes()
    {
        MediaDerivativeSpec spec = MediaDerivativeSpec.ForFilmstrip(variant: 1);
        MediaDerivativeResult result = FilmstripResult(spec, sheetCount: 2);

        MediaDerivativeIndexResolution resolution = MediaDerivativeIndexBinding.Resolve(spec, result);

        resolution.Status.Should().Be(MediaDerivativeIndexBindingStatus.Bound);
        resolution.IsBound.Should().BeTrue();
        resolution.Reference.Should().NotBeNull();

        // Computed here from the spec, independently of the artifact list, so the assertion is that
        // the binding agrees with the BUILDER's convention rather than with itself.
        string expectedHash = MediaDerivativeCacheKey.ComputeParamsHash(spec)[..16];
        string expectedLeaf = FilmstripIndex.IndexLeaf(expectedHash);

        resolution.Reference!.IndexLeaf.Should().Be(expectedLeaf);
        resolution.Reference.IndexLeaf.Should().Be(FilmstripIndex.IndexLeaf(expectedHash),
            "the row must point at the leaf FilmstripIndex.IndexLeaf(paramsHash16) predicts");
        resolution.Reference.IndexLeaf.Should().Be($"filmstrip-{expectedHash}-index.json");
        resolution.Reference.StoragePrefix.Should().Be(StoragePrefix);
        resolution.Reference.IndexStorageKey.Should().Be($"{StoragePrefix}/{expectedLeaf}");
        resolution.ActualIndexLeaf.Should().Be(expectedLeaf);

        // The artifact it selected really is the JSON one, not a sprite sheet.
        result.Artifacts.Single(a => a.FileName == expectedLeaf).ContentType.Should().Be("application/json");
    }

    [Fact]
    public void The_prediction_follows_the_density_so_two_densities_are_two_documents()
    {
        MediaDerivativeSpec oneSecond = MediaDerivativeSpec.ForFilmstrip(0);
        MediaDerivativeSpec thirtySeconds = MediaDerivativeSpec.ForFilmstrip(2);

        string first = MediaDerivativeIndexBinding.PredictIndexLeaf(oneSecond);
        string second = MediaDerivativeIndexBinding.PredictIndexLeaf(thirtySeconds);

        first.Should().NotBe(second,
            "one density is one row with its own params hash, so two densities must not share one index key");
        first.Should().Contain(MediaDerivativeCacheKey.ComputeParamsHash(oneSecond)[..16]);
        second.Should().Contain(MediaDerivativeCacheKey.ComputeParamsHash(thirtySeconds)[..16]);
    }

    [Theory]
    [InlineData(MediaDerivativeKindSlug.Proxy)]
    [InlineData(MediaDerivativeKindSlug.WaveformPeaks)]
    [InlineData(MediaDerivativeKindSlug.KeyframeIndex)]
    public void A_non_filmstrip_kind_binds_to_null_even_when_it_carries_a_json_artifact(MediaDerivativeKindSlug slug)
    {
        // Peaks in particular DOES carry an application/json sidecar (its byte-layout header), so
        // deciding on content type alone would bind a peaks row to a document that describes no
        // filmstrip. The slug check is what prevents that.
        MediaDerivativeSpec spec = slug switch
        {
            MediaDerivativeKindSlug.Proxy => MediaDerivativeSpec.ForProxy(1080, 30, 1, hasAudio: true, threads: 4),
            MediaDerivativeKindSlug.WaveformPeaks => MediaDerivativeSpec.ForWaveformPeaks(0),
            _ => MediaDerivativeSpec.ForKeyframeIndex()
        };

        MediaDerivativeResult result = new(
            slug,
            spec.Variant,
            spec.ParamsJson,
            "cache-key",
            StoragePrefix,
            0,
            0,
            3,
            10,
            [
                new MediaDerivativeArtifact("/tmp/lod.raw", "peaks-abcdef0123456789-lod20ms.raw", "application/octet-stream"),
                new MediaDerivativeArtifact("/tmp/index.json", "peaks-abcdef0123456789-index.json", "application/json")
            ],
            SidecarJson: "{}");

        MediaDerivativeIndexResolution resolution = MediaDerivativeIndexBinding.Resolve(spec, result);

        resolution.Status.Should().Be(MediaDerivativeIndexBindingStatus.NotApplicable);
        resolution.Reference.Should().BeNull();
        resolution.IsBound.Should().BeFalse();
    }

    [Fact]
    public void A_filmstrip_with_no_json_artifact_binds_to_null()
    {
        MediaDerivativeSpec spec = MediaDerivativeSpec.ForFilmstrip(0);
        MediaDerivativeResult result = FilmstripResult(spec, includeIndex: false);

        MediaDerivativeIndexResolution resolution = MediaDerivativeIndexBinding.Resolve(spec, result);

        resolution.Status.Should().Be(MediaDerivativeIndexBindingStatus.NotApplicable);
        resolution.Reference.Should().BeNull();
    }

    [Fact]
    public void A_filmstrip_whose_json_leaf_is_not_the_predicted_one_is_a_finding_and_binds_nothing()
    {
        MediaDerivativeSpec spec = MediaDerivativeSpec.ForFilmstrip(0);
        string wrongLeaf = FilmstripIndex.IndexLeaf("0000000000000000");
        MediaDerivativeResult result = FilmstripResult(spec, indexLeafOverride: wrongLeaf);

        MediaDerivativeIndexBinding.PredictIndexLeaf(spec).Should().NotBe(wrongLeaf);

        MediaDerivativeIndexResolution resolution = MediaDerivativeIndexBinding.Resolve(spec, result);

        // NOT "prefer one of the two": a pointer to a leaf the builder did not write would be a row
        // naming an object that does not exist, which is exactly the silent-wrong failure this ticket
        // exists to prevent. The status names the finding and carries the actual leaf so a caller can
        // report which one it saw.
        resolution.Status.Should().Be(MediaDerivativeIndexBindingStatus.LeafMismatch);
        resolution.Reference.Should().BeNull();
        resolution.IsBound.Should().BeFalse();
        resolution.ActualIndexLeaf.Should().Be(wrongLeaf);
    }

    [Fact]
    public void A_result_with_no_artifacts_binds_to_null_rather_than_throwing()
    {
        MediaDerivativeSpec spec = MediaDerivativeSpec.ForFilmstrip(0);

        MediaDerivativeResult result = new(
            MediaDerivativeKindSlug.Filmstrip, 0, spec.ParamsJson, "cache-key", StoragePrefix,
            0, 0, 3, 0, [], SidecarJson: null);

        MediaDerivativeIndexResolution resolution = MediaDerivativeIndexBinding.Resolve(spec, result);

        resolution.Status.Should().Be(MediaDerivativeIndexBindingStatus.NotApplicable);
        resolution.Reference.Should().BeNull();
    }

    [Fact]
    public void A_filmstrip_with_no_storage_prefix_binds_to_null()
    {
        MediaDerivativeSpec spec = MediaDerivativeSpec.ForFilmstrip(0);
        MediaDerivativeResult result = FilmstripResult(spec) with { StoragePrefix = string.Empty };

        MediaDerivativeIndexResolution resolution = MediaDerivativeIndexBinding.Resolve(spec, result);

        resolution.Status.Should().Be(MediaDerivativeIndexBindingStatus.NotApplicable,
            "a pointer must never be composed from an empty prefix");
        resolution.Reference.Should().BeNull();
    }

    // =========================================================================================
    // The engine's model must describe the column the API created
    // =========================================================================================

    [Fact]
    public void The_engine_model_declares_the_index_column_at_the_same_bound_as_the_api()
    {
        var options = new DbContextOptionsBuilder<WorkflowEngineDbContext>()
            .UseInMemoryDatabase($"media-derivative-index-mapping-{Guid.NewGuid()}")
            .Options;

        using var db = new WorkflowEngineDbContext(options);
        var property = db.Model.FindEntityType(typeof(MediaDerivative))!
            .FindProperty(nameof(MediaDerivative.IndexStorageKey));

        property.Should().NotBeNull("an unmapped property would leave the engine unable to describe the column it writes");
        property!.IsNullable.Should().BeTrue();
        property.GetMaxLength().Should().Be(1000,
            "the API's mapping and AddMediaDerivativeIndexStorageKey both bound index_storage_key to 1000 characters");
    }

    // =========================================================================================
    // End to end — real ffmpeg, real rows
    // =========================================================================================

    [FfmpegToolsFact]
    public async Task A_produced_filmstrip_row_carries_the_index_key_of_an_object_the_run_actually_uploaded()
    {
        string directory = MediaDerivativeFfmpegFixture.NewDirectory();
        using MediaDerivativeDispatchFixture fixture = MediaDerivativeDispatchFixture.Create(directory);
        try
        {
            // A deliberately SHORT source, because this test's subject is the pointer, not the
            // filmstrip: three seconds is enough for two densities to produce, and it keeps a
            // multi-process ffmpeg fixture off the shared test host — an earlier 35 s version ran
            // long enough to perturb other suites' timing-sensitive tests when xUnit ran the classes
            // in parallel.
            //
            // Consequence, and it is a documented one rather than a surprise: ffmpeg's `fps=1/30`
            // filter emits NOTHING for an input shorter than HALF its own interval (15 s), so the 30 s
            // density is never attempted for a 3 s source and leaves no row (T-307; see
            // docs/video-editing.md, "Media derivatives"). Two densities is exactly what this test
            // needs and exactly what the acceptance asks for.
            string source = MediaDerivativeFfmpegFixture.CreateSource(
                directory, "src.mp4", seconds: 3, fps: 30, width: 320, height: 240, withAudio: true);
            fixture.ServeAsSourceObject(source);

            Guid projectId = Guid.NewGuid();
            string storageKey = $"projects/{projectId:D}/userFiles/{Guid.NewGuid():N}.mp4";

            await fixture.Dispatcher.RunAsync(
                fixture.Request(projectId, storageKey, "\"etag-index\"", [MediaDerivativeKind.Filmstrip]),
                CancellationToken.None);

            IReadOnlyList<MediaDerivative> rows = fixture.ReadRows();

            rows.Should().HaveCount(2, "the 1 s and 5 s densities produce; the 30 s one has no frames to sample");
            rows.Should().OnlyContain(r => r.Kind == MediaDerivativeKind.Filmstrip);
            rows.Should().OnlyContain(r => !string.IsNullOrWhiteSpace(r.IndexStorageKey));

            // Two densities must produce TWO different index keys — one density is one row with its
            // own params hash, so a shared key would mean two densities collapsing onto one document.
            List<string> indexKeys = rows.Select(r => r.IndexStorageKey!).ToList();
            indexKeys.Distinct(StringComparer.Ordinal).Should().HaveCount(2);

            foreach (MediaDerivative row in rows)
            {
                string indexKey = row.IndexStorageKey!;

                indexKey.Should().StartWith($"projects/{projectId:D}/agentFiles/media-derivatives/");
                Path.GetFileName(indexKey).Should().EndWith("-index.json");

                // The pointer names an object this run actually wrote.
                fixture.Workspace.UploadedKeys.Should().Contain(indexKey);

                // The row's own sheet and its index belong to the same density: they carry the same
                // short params hash, which is what makes the sidecar the index OF these sheets.
                string sheetLeaf = Path.GetFileName(row.StorageKey);
                string indexLeaf = Path.GetFileName(indexKey);

                sheetLeaf.Should().StartWith("filmstrip-").And.EndWith("-sheet-001.jpg");
                HashOf(sheetLeaf).Should().Be(HashOf(indexLeaf));

                // And the document itself names that very sheet — the membership rule the media
                // endpoint's filmstrip resolver applies to map a density's sheets to their row.
                IReadOnlyList<string> describedLeaves = ReadSidecarSheetLeaves(fixture, indexKey);
                describedLeaves.Should().Contain(sheetLeaf);

                _output.WriteLine(
                    "MEASURED filmstrip density {0}: sheet={1} index={2} describedLeaves=[{3}]",
                    row.Width, sheetLeaf, indexLeaf, string.Join(",", describedLeaves));
            }

            _output.WriteLine("MEASURED index keys: {0}", string.Join(" | ", indexKeys));
        }
        finally
        {
            MediaDerivativeFfmpegFixture.TryDelete(directory);
        }
    }

    [FfmpegToolsFact]
    public async Task Every_non_filmstrip_kind_leaves_the_index_key_null()
    {
        string directory = MediaDerivativeFfmpegFixture.NewDirectory();
        using MediaDerivativeDispatchFixture fixture = MediaDerivativeDispatchFixture.Create(directory);
        try
        {
            string source = MediaDerivativeFfmpegFixture.CreateSource(
                directory, "src.mp4", seconds: 2, fps: 30, width: 320, height: 240, withAudio: true);
            fixture.ServeAsSourceObject(source);

            Guid projectId = Guid.NewGuid();
            string storageKey = $"projects/{projectId:D}/userFiles/{Guid.NewGuid():N}.mp4";

            await fixture.Dispatcher.RunAsync(
                fixture.Request(
                    projectId,
                    storageKey,
                    "\"etag-no-index\"",
                    [MediaDerivativeKind.Proxy, MediaDerivativeKind.WaveformPeaks, MediaDerivativeKind.KeyframeIndex]),
                CancellationToken.None);

            IReadOnlyList<MediaDerivative> rows = fixture.ReadRows();

            rows.Should().HaveCount(5, "one proxy, three peaks levels and one keyframe index");
            rows.Should().NotContain(r => r.Kind == MediaDerivativeKind.Filmstrip);
            rows.Should().OnlyContain(r => r.IndexStorageKey == null,
                "a proxy, a peaks series and a keyframe index have no sidecar, so the column must stay null");

            _output.WriteLine("MEASURED non-filmstrip rows: {0}, none carrying an index key", rows.Count);
        }
        finally
        {
            MediaDerivativeFfmpegFixture.TryDelete(directory);
        }
    }

    // =========================================================================================

    /// <summary>
    /// Builds a filmstrip result the way <c>MediaDerivativeService.ProduceFilmstripAsync</c> does:
    /// <c>sheetCount</c> sprite sheets named by <see cref="FilmstripIndex.SheetLeaf"/>, then the JSON
    /// sidecar named by <see cref="FilmstripIndex.IndexLeaf"/>, all carrying the spec's short params
    /// hash. <paramref name="indexLeafOverride"/> is how a test fabricates the disagreeing leaf.
    /// </summary>
    private static MediaDerivativeResult FilmstripResult(
        MediaDerivativeSpec spec,
        int sheetCount = 1,
        bool includeIndex = true,
        string? indexLeafOverride = null,
        string storagePrefix = StoragePrefix)
    {
        string hash = MediaDerivativeCacheKey.ComputeParamsHash(spec)[..16];
        var artifacts = new List<MediaDerivativeArtifact>();

        for (int sheet = 1; sheet <= sheetCount; sheet++)
        {
            string leaf = FilmstripIndex.SheetLeaf(hash, sheet);
            artifacts.Add(new MediaDerivativeArtifact($"/tmp/{leaf}", leaf, "image/jpeg"));
        }

        if (includeIndex)
        {
            string leaf = indexLeafOverride ?? FilmstripIndex.IndexLeaf(hash);
            artifacts.Add(new MediaDerivativeArtifact($"/tmp/{leaf}", leaf, "application/json"));
        }

        return new MediaDerivativeResult(
            spec.Slug,
            spec.Variant,
            spec.ParamsJson,
            "cache-key",
            storagePrefix,
            160,
            120,
            3,
            artifacts.Sum(a => a.FileName.Length),
            artifacts,
            SidecarJson: includeIndex ? "{}" : null);
    }

    /// <summary>
    /// The short params hash embedded in a filmstrip leaf (<c>filmstrip-&lt;hash16&gt;-…</c>), so two
    /// leaves can be shown to belong to the same density without re-deriving the spec.
    /// </summary>
    private static string HashOf(string leaf)
    {
        string[] parts = leaf.Split('-');
        parts.Should().HaveCountGreaterThanOrEqualTo(3, $"'{leaf}' is not a filmstrip leaf");
        return parts[1];
    }

    /// <summary>
    /// Reads an uploaded sidecar back out of the dispatch fixture's local object store and returns
    /// the leaves it describes. The store mirrors the real layout — one file per storage key under a
    /// local root — so this reads the object the row points at rather than re-deriving it.
    /// </summary>
    private static IReadOnlyList<string> ReadSidecarSheetLeaves(
        MediaDerivativeDispatchFixture fixture, string indexStorageKey)
    {
        string root = Path.GetDirectoryName(fixture.ScratchRoot)!;
        string path = Path.Combine(root, "objects", indexStorageKey.Replace('/', Path.DirectorySeparatorChar));

        File.Exists(path).Should().BeTrue($"the row points at '{indexStorageKey}', which must exist in the object store");

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        var leaves = new List<string>();

        foreach (JsonElement sheet in document.RootElement.GetProperty("sheets").EnumerateArray())
            leaves.Add(sheet.GetProperty("storageLeaf").GetString()!);

        return leaves;
    }
}
