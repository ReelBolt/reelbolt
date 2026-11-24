using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.Shared.Briefs;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution.Caching;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// <see cref="MediaOverlayOfferBuilder"/> — the deterministic analysis-time offer of pictures/videos
/// (<c>m{n}</c>) and the moments to show them (<c>o{n}</c>) (initiative "picture-overlay", T-002).
/// </summary>
public class MediaOverlayOfferBuilderTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly byte[] Png1200x1500 =
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, // signature
        0x00, 0x00, 0x00, 0x0D,                         // IHDR length
        0x49, 0x48, 0x44, 0x52,                         // "IHDR"
        0x00, 0x00, 0x04, 0xB0,                         // width  = 1200
        0x00, 0x00, 0x05, 0xDC                          // height = 1500
    ];

    private static ProjectWorkspaceFile File(
        Guid id, string name, string mime, DateTime uploadedAt, long size = 1024, string? storageKey = null) =>
        new(
            Id: id,
            ProjectId: Guid.Parse("11111111-1111-1111-1111-111111111111"),
            OriginalFileName: name,
            OriginalPath: null,
            Category: "userFiles",
            StorageKey: storageKey ?? $"projects/p/userFiles/{name}",
            MimeType: mime,
            SizeBytes: size,
            UploadedAt: uploadedAt,
            AgentSummary: null);

    private static MediaSourceProbe Probe(double durationSec, int width, int height) =>
        new(durationSec, 30, 1, width, height, "h264", "aac", "mp4", 2, 48_000, 5_000_000, true);

    [Fact]
    public async Task No_named_set_offers_every_usable_project_image_and_video_ordered_by_uploaded_at_then_id()
    {
        Guid imageLate = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
        Guid imageEarly = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
        Guid video = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000003");
        Guid audio = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000004");
        Guid document = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000005");

        List<ProjectWorkspaceFile> files =
        [
            File(imageLate, "late.png", "image/png", T0.AddMinutes(5), storageKey: "posts/late.png"),
            File(video, "clip.mp4", "video/mp4", T0, storageKey: "posts/clip.mp4"),
            File(imageEarly, "early.jpg", "image/jpeg", T0, storageKey: "posts/early.jpg"),
            File(audio, "bed.mp3", "audio/mpeg", T0),
            File(document, "notes.pdf", "application/pdf", T0)
        ];

        FakeWorkspace workspace = new(new Dictionary<string, byte[]>
        {
            ["posts/late.png"] = Png1200x1500,
            ["posts/early.jpg"] = Png1200x1500
        });
        Dictionary<string, MediaSourceProbe> probes = new() { ["posts/clip.mp4"] = Probe(12.5, 1080, 1920) };

        IReadOnlyList<VideoAnalysisMediaOffer> offers = await MediaOverlayOfferBuilder.BuildAsync(
            Guid.Empty, namedFileIds: null, workspace, files, probes, maxMedia: 20, CancellationToken.None);

        offers.Select(o => o.Id).Should().Equal("m0", "m1", "m2");
        // UploadedAt ascending, then Id: the early image and the video share T0, and the image's id
        // (...0002) sorts before the video's (...0003).
        offers.Select(o => o.Name).Should().Equal("early.jpg", "clip.mp4", "late.png");
        offers.Should().OnlyContain(o => !o.FromUser);
        offers.Select(o => o.Kind).Should().Equal("Image", "Video", "Image");
    }

    [Fact]
    public async Task Named_set_binds_the_offers_in_brief_order_and_marks_them_from_user()
    {
        Guid first = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");
        Guid second = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
        Guid third = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000003");

        List<ProjectWorkspaceFile> files =
        [
            File(first, "one.png", "image/png", T0.AddDays(2), storageKey: "posts/one.png"),
            File(second, "two.mp4", "video/mp4", T0.AddDays(1), storageKey: "posts/two.mp4"),
            File(third, "three.png", "image/png", T0, storageKey: "posts/three.png")
        ];

        IReadOnlyList<VideoAnalysisMediaOffer> offers = await MediaOverlayOfferBuilder.BuildAsync(
            Guid.Empty, [third, first], new FakeWorkspace(new Dictionary<string, byte[]>()), files, probes: null, maxMedia: 20, CancellationToken.None);

        offers.Select(o => o.Name).Should().Equal("three.png", "one.png");
        offers.Select(o => o.ProjectFileId).Should().Equal(third, first);
        offers.Should().OnlyContain(o => o.FromUser);
    }

    [Fact]
    public async Task A_named_set_never_falls_back_to_the_whole_project_when_a_named_file_is_gone()
    {
        Guid named = Guid.Parse("cccccccc-0000-0000-0000-000000000001");
        Guid unnamed = Guid.Parse("cccccccc-0000-0000-0000-000000000002");
        Guid missing = Guid.Parse("cccccccc-0000-0000-0000-000000000003");

        List<ProjectWorkspaceFile> files =
        [
            File(named, "named.png", "image/png", T0, storageKey: "posts/named.png"),
            File(unnamed, "unnamed.png", "image/png", T0, storageKey: "posts/unnamed.png")
        ];

        IReadOnlyList<VideoAnalysisMediaOffer> offers = await MediaOverlayOfferBuilder.BuildAsync(
            Guid.Empty, [named, missing], new FakeWorkspace(new Dictionary<string, byte[]>()), files, probes: null, maxMedia: 20, CancellationToken.None);

        offers.Should().ContainSingle();
        offers[0].ProjectFileId.Should().Be(named);
    }

    [Fact]
    public async Task MaxOverlayMedia_caps_the_offers_exactly()
    {
        List<ProjectWorkspaceFile> files = Enumerable.Range(0, 5)
            .Select(i => File(Guid.NewGuid(), $"p{i}.png", "image/png", T0.AddMinutes(i), storageKey: $"posts/p{i}.png"))
            .ToList();

        IReadOnlyList<VideoAnalysisMediaOffer> offers = await MediaOverlayOfferBuilder.BuildAsync(
            Guid.Empty, namedFileIds: null, new FakeWorkspace(new Dictionary<string, byte[]>()), files, probes: null, maxMedia: 2, CancellationToken.None);

        offers.Should().HaveCount(2);
        offers.Select(o => o.Id).Should().Equal("m0", "m1");
    }

    [Fact]
    public async Task Zero_cap_offers_nothing()
    {
        List<ProjectWorkspaceFile> files =
            [File(Guid.NewGuid(), "p0.png", "image/png", T0, storageKey: "posts/p0.png")];

        (await MediaOverlayOfferBuilder.BuildAsync(
            Guid.Empty, namedFileIds: null, new FakeWorkspace(new Dictionary<string, byte[]>()), files, probes: null, maxMedia: 0, CancellationToken.None))
            .Should().BeEmpty();
    }

    [Fact]
    public async Task Image_facts_come_from_the_file_header_only()
    {
        Guid id = Guid.NewGuid();
        List<ProjectWorkspaceFile> files =
            [File(id, "testimonial.png", "image/png", T0, size: 4096, storageKey: "posts/testimonial.png")];
        FakeWorkspace workspace = new(new Dictionary<string, byte[]> { ["posts/testimonial.png"] = Png1200x1500 });

        VideoAnalysisMediaOffer offer = (await MediaOverlayOfferBuilder.BuildAsync(
            Guid.Empty, namedFileIds: null, workspace, files, probes: null, maxMedia: 20, CancellationToken.None)).Single();

        offer.Width.Should().Be(1200);
        offer.Height.Should().Be(1500);
        offer.Aspect.Should().Be(0.8);
        offer.Orientation.Should().Be("Portrait");
        offer.DurationSec.Should().BeNull();
        offer.SizeBytes.Should().Be(4096);
        offer.Kind.Should().Be("Image");
        offer.ProjectFileId.Should().Be(id);
    }

    [Fact]
    public async Task An_unreadable_image_is_offered_with_no_dimensions()
    {
        List<ProjectWorkspaceFile> files =
            [File(Guid.NewGuid(), "broken.png", "image/png", T0, storageKey: "posts/broken.png")];
        FakeWorkspace workspace = new(new Dictionary<string, byte[]> { ["posts/broken.png"] = [1, 2, 3] });

        VideoAnalysisMediaOffer offer = (await MediaOverlayOfferBuilder.BuildAsync(
            Guid.Empty, namedFileIds: null, workspace, files, probes: null, maxMedia: 20, CancellationToken.None)).Single();

        offer.Width.Should().BeNull();
        offer.Height.Should().BeNull();
        offer.Aspect.Should().BeNull();
        offer.Orientation.Should().BeNull();
    }

    [Fact]
    public async Task Video_facts_come_from_the_stored_ingest_probe_and_are_unknown_without_one()
    {
        Guid probed = Guid.NewGuid();
        Guid unprobed = Guid.NewGuid();
        List<ProjectWorkspaceFile> files =
        [
            File(probed, "probed.mp4", "video/mp4", T0, storageKey: "posts/probed.mp4"),
            File(unprobed, "unprobed.mp4", "video/mp4", T0.AddMinutes(1), storageKey: "posts/unprobed.mp4")
        ];
        Dictionary<string, MediaSourceProbe> probes = new() { ["posts/probed.mp4"] = Probe(12.5, 1080, 1920) };

        IReadOnlyList<VideoAnalysisMediaOffer> offers = await MediaOverlayOfferBuilder.BuildAsync(
            Guid.Empty, namedFileIds: null, new FakeWorkspace(new Dictionary<string, byte[]>()), files, probes, maxMedia: 20, CancellationToken.None);

        offers[0].Width.Should().Be(1080);
        offers[0].Height.Should().Be(1920);
        offers[0].Aspect.Should().Be(0.5625);
        offers[0].Orientation.Should().Be("Portrait");
        offers[0].DurationSec.Should().Be(12.5);

        offers[1].Width.Should().BeNull();
        offers[1].Height.Should().BeNull();
        offers[1].DurationSec.Should().BeNull();
        offers[1].Kind.Should().Be("Video");
    }

    [Fact]
    public void Overlay_windows_are_split_capped_and_numbered_contiguously_in_shot_order()
    {
        VideoAnalysisShotVisual visual = Visual(
            motionMean: 0.005,
            stillWindows: [new VideoAnalysisStillWindow(7.0, 42.5, 0.01)]);
        List<VideoAnalysisShot> shots =
        [
            new("s0", 0, 45.226, Visual: visual),
            new("s1", 50, 60, Visual: Visual(motionMean: 0.08, stillWindows: [])),
            new("s2", 70, 80, Visual: null)
        ];

        IReadOnlyList<VideoAnalysisOverlayWindow> windows =
            MediaOverlayOfferBuilder.BuildWindows(shots, maxWindows: 4);

        windows.Should().HaveCount(4);
        windows.Select(w => w.Id).Should().Equal("o0", "o1", "o2", "o3");
        windows.Select(w => w.ShotId).Should().Equal("s0", "s0", "s0", "s1");

        // The calm 35.5 s still window's three slices survive the cap before the busier s1 moment.
        List<VideoAnalysisOverlayWindow> s0 = windows.Where(w => w.ShotId == "s0").ToList();
        s0.Should().HaveCount(3);
        s0[0].StartSec.Should().Be(7.0);
        s0[^1].EndSec.Should().Be(42.5);
        for (int i = 1; i < s0.Count; i++)
            s0[i].StartSec.Should().Be(s0[i - 1].EndSec);
        s0.Should().BeInAscendingOrder(w => w.StartSec);
    }

    [Fact]
    public void Overlay_windows_are_capped_exactly_and_ties_break_by_shot_then_start()
    {
        List<VideoAnalysisShot> shots =
        [
            new("s0", 0, 10, Visual: Visual(0.005, [])),
            new("s1", 10, 20, Visual: Visual(0.005, []))
        ];

        IReadOnlyList<VideoAnalysisOverlayWindow> windows =
            MediaOverlayOfferBuilder.BuildWindows(shots, maxWindows: 1);

        windows.Should().ContainSingle();
        windows[0].Id.Should().Be("o0");
        windows[0].ShotId.Should().Be("s0");
    }

    [Fact]
    public void No_windows_for_shots_without_visual_data_or_a_zero_cap()
    {
        List<VideoAnalysisShot> shots = [new("s0", 0, 10, Visual: null)];

        MediaOverlayOfferBuilder.BuildWindows(shots, maxWindows: 24).Should().BeEmpty();
        MediaOverlayOfferBuilder.BuildWindows(
            [new("s0", 0, 10, Visual: Visual(0.005, []))], maxWindows: 0).Should().BeEmpty();
    }

    [Fact]
    public void NamedOverlayFileIds_is_empty_for_a_brief_that_names_no_overlay_files()
    {
        MediaOverlayOfferBuilder.NamedOverlayFileIds(null).Should().BeEmpty();
        MediaOverlayOfferBuilder.NamedOverlayFileIds(new StoredRunBrief()).Should().BeEmpty();
    }

    private static VideoAnalysisShotVisual Visual(
        double motionMean, IReadOnlyList<VideoAnalysisStillWindow> stillWindows) => new(
        MotionMean: motionMean, MotionPeak: 0.02, MotionStdDev: 0.01, MotionClass: "Static",
        CameraMove: "Static", CameraMoveConfidence: 0.9, HeadMotion: 0.01, TailMotion: 0.01,
        StillWindows: stillWindows, BrightnessMean: 0.5, BrightnessStdDev: 0.05, ContrastRms: 0.1,
        ClippedHighlightRatio: 0, CrushedBlackRatio: 0, SaturationMean: 0.3,
        DominantColors: [], Regions: [], BestOverlayRegion: null, ActiveCrop: null,
        Sharpness: null, KenBurnsCandidate: false, KenBurnsReason: null,
        DuplicateGroupId: null, GroupRank: null, IsBestTake: false);

    /// <summary>The two members this builder reads; every other member is a programming error here.</summary>
    private sealed class FakeWorkspace(IReadOnlyDictionary<string, byte[]> bytes) : IProjectFileWorkspace
    {
        public Task<IReadOnlyList<ProjectWorkspaceFile>> ListFilesAsync(Guid projectId, CancellationToken ct) =>
            throw new InvalidOperationException("The builder under test is given the listing directly.");

        public Task<string> ReadFileAsync(Guid projectId, string fileReference, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<ProjectWorkspaceFile> WriteTextFileAsync(
            Guid projectId, string fileName, string content, string contentType, CancellationToken ct,
            string category = "agentFiles", string? originalPath = null) => throw new NotSupportedException();

        public Task DownloadStorageKeyToFileAsync(
            Guid projectId, string storageKey, string destinationPath, CancellationToken ct)
        {
            System.IO.File.WriteAllBytes(destinationPath, bytes[storageKey]);
            return Task.CompletedTask;
        }

        public Task DownloadStorageKeyToFileAsync(
            Guid projectId, string storageKey, string destinationPath, long maxBytes, CancellationToken ct)
        {
            byte[] content = bytes[storageKey];
            if (content.LongLength > maxBytes)
                throw new StorageObjectTooLargeException(maxBytes, content.LongLength);
            System.IO.File.WriteAllBytes(destinationPath, content);
            return Task.CompletedTask;
        }

        public Task<ProjectWorkspaceFile> UploadBinaryFileAsync(
            Guid projectId, string localFilePath, string fileName, string contentType,
            SummaryStatus summaryStatus, FileIndexingStatus indexingStatus, CancellationToken ct,
            string category = "outputFiles", string? originalPath = null) => throw new NotSupportedException();

        public Task<ProjectWorkspaceFile> RegisterStoredObjectAsync(
            Guid projectId, string storageKey, string fileName, string contentType, long sizeBytes,
            SummaryStatus summaryStatus, FileIndexingStatus indexingStatus, CancellationToken ct,
            string category = "outputFiles") => throw new NotSupportedException();

        public Task<string> UploadArtifactAsync(
            Guid projectId, string localFilePath, string fileName, string contentType, CancellationToken ct,
            string category = "agentFiles") => throw new NotSupportedException();

        public Task<bool> ArtifactExistsAsync(Guid projectId, string storageKey, CancellationToken ct) =>
            Task.FromResult(false);
    }
}

/// <summary>
/// The flag-on cache bump (design §6.3): a <c>VideoAnalyze</c> that emits overlay offers must not be
/// served a cached analysis from before the offers existed, and a flag-off analysis must keep its
/// exact old key.
/// </summary>
public class MediaOverlayOfferRevisionTests
{
    [Fact]
    public void EmitOverlayOffers_true_appends_the_media_overlay_offers_revision()
    {
        string tag = DeterministicStepRevisions.For(
            StepType.VideoAnalyze, """{"detectShots":true,"emitOverlayOffers":true}""");

        tag.Should().EndWith("+media-overlay-offers-1");
    }

    [Fact]
    public void Flag_off_or_absent_keeps_the_exact_previous_video_analyze_revision()
    {
        string baseline = DeterministicStepRevisions.For(StepType.VideoAnalyze);

        baseline.Should().Contain("+untrusted-view-names-1",
            "the name sanitisation changed the view for every analysis, so pre-fix cache entries must miss");

        DeterministicStepRevisions.For(StepType.VideoAnalyze, """{"detectShots":true}""").Should().Be(baseline);
        DeterministicStepRevisions.For(StepType.VideoAnalyze, """{"emitOverlayOffers":false}""").Should().Be(baseline);
    }

    [Fact]
    public void Both_revision_suffixes_compose_when_both_features_are_on()
    {
        string tag = DeterministicStepRevisions.For(
            StepType.VideoAnalyze, """{"sceneDetection":"Sensitive","emitOverlayOffers":true}""");

        tag.Should().Contain("+sensitive-scenes-1");
        tag.Should().EndWith("+media-overlay-offers-1");
    }
    [Fact]
    public void View_budget_suffix_is_added_only_for_configs_that_produce_an_offer_list()
    {
        string baseline = DeterministicStepRevisions.For(StepType.VideoAnalyze);

        DeterministicStepRevisions.For(StepType.VideoAnalyze, """{"emitOverlayPlacements":true}""").Should().Be(baseline + "+view-budget-1");
        DeterministicStepRevisions.For(StepType.VideoAnalyze, """{"offerMusicTracks":true}""").Should().Be(baseline + "+view-budget-1");
        DeterministicStepRevisions.For(StepType.VideoAnalyze, """{"trackObjects":[{"label":"cup"}]}""").Should().Be(baseline + "+view-budget-1");
        DeterministicStepRevisions.For(StepType.VideoAnalyze, """{"emitOverlayPlacements":false,"trackObjects":[]}""").Should().Be(baseline);
        DeterministicStepRevisions.For(StepType.VideoAnalyze, """{"emitOverlayOffers":true}""")
            .Should().Be(baseline + "+view-budget-1+media-overlay-offers-1");
    }

    [Fact]
    public void Beat_sync_suffix_is_added_only_for_a_compile_that_cuts_to_the_beat()
    {
        string baseline = DeterministicStepRevisions.For(StepType.VideoCompile);

        DeterministicStepRevisions.For(StepType.VideoCompile, null).Should().Be(baseline);
        DeterministicStepRevisions.For(StepType.VideoCompile, """{"beatSync":"Off"}""").Should().Be(baseline);
        DeterministicStepRevisions.For(StepType.VideoCompile, """{"beatSync":"Beat"}""").Should().Be(baseline + "+beat-sync-target-1+beat-grid-read-1");
        DeterministicStepRevisions.For(StepType.VideoCompile, """{"beatSync":2}""").Should().Be(baseline + "+beat-sync-target-1+beat-grid-read-1");
        DeterministicStepRevisions.For(StepType.VideoAnalyze, """{"beatSync":"Beat"}""").Should().Be(DeterministicStepRevisions.For(StepType.VideoAnalyze));
    }
}
