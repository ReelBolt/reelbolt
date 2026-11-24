namespace ReelBolt.WorkflowEngine.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.OutputSchemas;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

/// <summary>
/// Comprehensive unit tests for <see cref="TimelineManifestBuilder"/> and <see cref="TimelineManifest"/>
/// in Unified Program Mode (Expanded Studio Tracks).
/// </summary>
public class TimelineManifestBuilderTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static VideoAnalysisArtifact CreateMinimalArtifact(
        double durationSec = 60.0,
        int fpsNum = 30,
        int fpsDen = 1,
        int width = 1920,
        int height = 1080,
        IReadOnlyList<VideoAnalysisShot>? shots = null,
        IReadOnlyList<VideoAnalysisSegment>? segments = null,
        IReadOnlyList<VideoAnalysisSourceInfo>? sources = null,
        IReadOnlyList<VideoAnalysisLookGroup>? lookGroups = null,
        IReadOnlyList<VideoInsertRegionTrack>? insertRegions = null)
    {
        shots ??= Array.Empty<VideoAnalysisShot>();
        segments ??= Array.Empty<VideoAnalysisSegment>();

        return new VideoAnalysisArtifact(
            Version: 1,
            Media: new VideoAnalysisMedia(durationSec, fpsNum, fpsDen, width, height),
            Shots: shots,
            SilenceSpans: Array.Empty<VideoAnalysisSilenceSpan>(),
            Segments: segments,
            Words: Array.Empty<VideoAnalysisWord>(),
            OfferedIds: shots.Select(s => s.Id).ToList(),
            Provenance: new VideoAnalysisProvenance(VideoTranscriptionMode.Off, false, false),
            Sources: sources,
            LookGroups: lookGroups,
            InsertRegions: insertRegions);
    }

    [Fact]
    public void Build_CarriesTheSourceIdentityTheAnalysisRecorded_AndOmitsItForAnOldArtifact()
    {
        Guid file = Guid.NewGuid();
        Guid render = Guid.NewGuid();
        var media = new VideoAnalysisMedia(4, 30, 1, 1920, 1080);
        var artifact = CreateMinimalArtifact(sources:
        [
            new VideoAnalysisSourceInfo(0, "scratch/trim-0.mp4", media, TrimInSec: 1, TrimOutSec: 5, ProjectFileId: file),
            new VideoAnalysisSourceInfo(1, "scratch/still-1.mp4", media, StepResultId: render),
            new VideoAnalysisSourceInfo(2, "sources/old.mp4", media)
        ]);
        var spans = new List<VideoCompileStepExecutor.ResolvedSpan>();

        var manifest = TimelineManifestBuilder.Build(
            Guid.NewGuid(), Guid.NewGuid(), artifact, spans,
            OutputTimeline.Build(spans, Array.Empty<SeamPlan>()), "projects/p/output.mp4", 1920, 1080, 30.0);

        manifest.Sources[0].ProjectFileId.Should().Be(file);
        manifest.Sources[1].StepResultId.Should().Be(render);
        manifest.Sources[2].ProjectFileId.Should().BeNull();
        manifest.Sources[2].StepResultId.Should().BeNull();

        // An artifact written before the identity existed still reads, and writes no identity.
        string oldJson = "{\"sourceIndex\":0,\"storageKey\":\"k\",\"media\":{\"durationSec\":1,\"fpsNum\":30,\"fpsDen\":1,\"width\":2,\"height\":3}}";
        var old = JsonSerializer.Deserialize<VideoAnalysisSourceInfo>(oldJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        old.ProjectFileId.Should().BeNull();
        JsonSerializer.Serialize(old, new JsonSerializerOptions(JsonSerializerDefaults.Web)).Should().NotContain("projectFileId");
    }

    [Fact]
    public void Build_WithMinimalInputs_ProducesValidManifest()
    {
        // Arrange
        var projectId = Guid.NewGuid();
        var executionId = Guid.NewGuid();
        var artifact = CreateMinimalArtifact();

        var spans = new List<VideoCompileStepExecutor.ResolvedSpan>();
        var timeline = OutputTimeline.Build(spans, Array.Empty<SeamPlan>());

        // Act
        var manifest = TimelineManifestBuilder.Build(
            projectId: projectId,
            executionId: executionId,
            artifact: artifact,
            resolvedSpans: spans,
            timeline: timeline,
            outputStorageKey: "projects/p/output.mp4",
            width: 1920,
            height: 1080,
            fps: 30.0);

        // Assert
        manifest.Should().NotBeNull();
        manifest.Version.Should().Be(1);
        manifest.ProjectId.Should().Be(projectId);
        manifest.ExecutionId.Should().Be(executionId);
        manifest.OutputVideo.Width.Should().Be(1920);
        manifest.OutputVideo.Height.Should().Be(1080);
        manifest.OutputVideo.Fps.Should().Be(30.0);
        manifest.OutputVideo.StorageKey.Should().Be("projects/p/output.mp4");
        manifest.Sources.Should().HaveCount(1);
        manifest.Sources[0].Index.Should().Be(0);
        manifest.Sources[0].DurationSec.Should().Be(60.0);
        manifest.Tracks.Should().BeEmpty();
    }

    /// <summary>
    /// <see cref="OutputTimeline.Build"/> indexes <c>seams[i - 1]</c>, so a span list of N entries
    /// needs exactly N-1 seams. This returns N-1 zero-overlap hard cuts, i.e. plain concatenation.
    /// </summary>
    private static List<SeamPlan> HardCuts(int spanCount) =>
        Enumerable.Range(0, Math.Max(0, spanCount - 1))
            .Select(i => new SeamPlan(i, SeamTreatment.HardCut, 0.0, 0.0, string.Empty, 0.0, "Rule"))
            .ToList();

    private static VideoShotCaption CreateCaption(string shotId, string summary) =>
        new(shotId, summary, Array.Empty<string>(), "", "", "", "", "", Array.Empty<string>(), Array.Empty<string>(), "", "", "", "", Array.Empty<string>());


    [Fact]
    public void Build_WithCuts_ProducesVideoTrackWithLookGroupsAndTransitions()
    {
        // Arrange
        var projectId = Guid.NewGuid();
        var executionId = Guid.NewGuid();
        var shots = new List<VideoAnalysisShot>
        {
            new(
                Id: "shot-1",
                StartSec: 0.0,
                EndSec: 10.0,
                Caption: CreateCaption("shot-1", "Host speaking in kitchen")),
            new(
                Id: "shot-2",
                StartSec: 10.0,
                EndSec: 20.0,
                Caption: CreateCaption("shot-2", "Close up of food preparation")),
        };

        var lookGroups = new List<VideoAnalysisLookGroup>
        {
            new("look-warm-kitchen", new List<string> { "shot-1" }, "shot-1", 0.95),
            new("look-macro", new List<string> { "shot-2" }, "shot-2", 0.90),
        };

        var artifact = CreateMinimalArtifact(durationSec: 120.0, shots: shots, lookGroups: lookGroups);

        var spans = new List<VideoCompileStepExecutor.ResolvedSpan>
        {
            new(RequestedStart: 0.0, RequestedEnd: 5.0, SnappedStart: 0.0, SnappedEnd: 5.0, StartFrame: 0, EndFrame: 150, SourceIndex: 0, OriginKeepFromIds: new[] { "shot-1" }),
            new(RequestedStart: 10.0, RequestedEnd: 15.0, SnappedStart: 10.0, SnappedEnd: 15.0, StartFrame: 300, EndFrame: 450, SourceIndex: 0, OriginKeepFromIds: new[] { "shot-2" }),
        };

        var seams = new List<SeamPlan>
        {
            new(0, SeamTreatment.Dissolve, 0.5, 0.5, "fade", 0.0, "Rule"),
        };

        var timeline = OutputTimeline.Build(spans, seams);

        var colorGrade = new ColorGradePlanOutput
        {
            Look = "WarmAutumn",
            Strength = "Medium",
        };

        var decision = new VideoEditDecisionOutput
        {
            Keep = new List<VideoEditKeepSpan>
            {
                new() { FromId = "shot-1", ToId = "shot-1", Reason = "Strong opening host hook" },
                new() { FromId = "shot-2", ToId = "shot-2", Reason = "High engagement food preparation" },
            },
            EditRationale = "Paced kitchen cooking workflow",
        };

        // Act
        var manifest = TimelineManifestBuilder.Build(
            projectId: projectId,
            executionId: executionId,
            artifact: artifact,
            resolvedSpans: spans,
            timeline: timeline,
            outputStorageKey: "output.mp4",
            width: 1920,
            height: 1080,
            fps: 30.0,
            colorGrade: colorGrade,
            seamPlans: seams,
            decision: decision);

        // Assert
        manifest.Tracks.Should().ContainSingle(t => t.Type == "video");
        var videoTrack = manifest.Tracks.Single(t => t.Type == "video");
        videoTrack.Id.Should().Be("track-video");
        videoTrack.Name.Should().Be("Video");
        videoTrack.Items.Should().HaveCount(2);

        var cut1 = videoTrack.Items[0];
        cut1.Id.Should().Be("cut-1");
        cut1.ShotId.Should().Be("shot-1");
        cut1.SourceIndex.Should().Be(0);
        cut1.SourceStartSec.Should().Be(0.0);
        cut1.SourceEndSec.Should().Be(5.0);
        cut1.Label.Should().Be("Host speaking in kitchen");
        cut1.AgentRationale.Should().Be("Strong opening host hook");
        cut1.LookGroup.Should().Be("look-warm-kitchen");
        cut1.Color.Should().Be("WarmAutumn (Medium)");
        cut1.ColorGrade.Should().Be("WarmAutumn (Medium)");
        cut1.TransitionOut.Should().Be("fade");

        var cut2 = videoTrack.Items[1];
        cut2.Id.Should().Be("cut-2");
        cut2.ShotId.Should().Be("shot-2");
        cut2.Label.Should().Be("Close up of food preparation");
        cut2.AgentRationale.Should().Be("High engagement food preparation");
        cut2.LookGroup.Should().Be("look-macro");
        cut2.TransitionOut.Should().BeNull();
    }

    /// <summary>
    /// F2: a duplicate <c>FromId</c> in the decision's Keep list must not throw (the builder's
    /// documented invariant is "never throws", and the decision is untrusted model output). The
    /// documented resolution rule is deterministic: among entries sharing a FromId the FIRST one
    /// carrying a non-blank Reason wins, and when none of them carries one the FIRST seen wins
    /// (which resolves the cut to null and therefore falls back to the edit-level rationale).
    /// </summary>
    [Fact]
    public void Build_WithDuplicateKeepFromIds_DoesNotThrow_AndJoinsTheFirstNonBlankReason()
    {
        // Arrange
        var artifact = CreateMinimalArtifact(durationSec: 120.0);

        var spans = new List<VideoCompileStepExecutor.ResolvedSpan>
        {
            // Duplicate where only the SECOND entry carries a reason -> the later one must win.
            new(RequestedStart: 0.0, RequestedEnd: 5.0, SnappedStart: 0.0, SnappedEnd: 5.0, StartFrame: 0, EndFrame: 150, SourceIndex: 0, OriginKeepFromIds: new[] { "shot-1" }),
            // Duplicate where BOTH carry a reason -> the FIRST one must win, never the later one.
            new(RequestedStart: 10.0, RequestedEnd: 15.0, SnappedStart: 10.0, SnappedEnd: 15.0, StartFrame: 300, EndFrame: 450, SourceIndex: 0, OriginKeepFromIds: new[] { "shot-2" }),
            // Duplicate where NO entry carries a reason -> first seen wins, so the reason is blank
            // and the cut falls back to the edit-level rationale.
            new(RequestedStart: 20.0, RequestedEnd: 25.0, SnappedStart: 20.0, SnappedEnd: 25.0, StartFrame: 600, EndFrame: 750, SourceIndex: 0, OriginKeepFromIds: new[] { "shot-3" }),
        };

        var timeline = OutputTimeline.Build(spans, HardCuts(spans.Count));

        var decision = new VideoEditDecisionOutput
        {
            Keep = new List<VideoEditKeepSpan>
            {
                new() { FromId = "shot-1", ToId = "shot-1", Reason = string.Empty },
                new() { FromId = "shot-1", ToId = "shot-1", Reason = "Second carries the reason" },
                new() { FromId = "shot-2", ToId = "shot-2", Reason = "First reason wins" },
                new() { FromId = "shot-2", ToId = "shot-2", Reason = "Later duplicate reason loses" },
                new() { FromId = "shot-3", ToId = "shot-3", Reason = string.Empty },
                new() { FromId = "shot-3", ToId = "shot-3", Reason = "   " },
            },
            EditRationale = "Edit-level fallback rationale",
        };

        // Act — the whole point of F2 is that this does not throw.
        var manifest = TimelineManifestBuilder.Build(
            projectId: Guid.NewGuid(),
            executionId: Guid.NewGuid(),
            artifact: artifact,
            resolvedSpans: spans,
            timeline: timeline,
            outputStorageKey: "output.mp4",
            width: 1920,
            height: 1080,
            fps: 30.0,
            decision: decision);

        // Assert
        var videoTrack = manifest.Tracks.Single(t => t.Type == "video");
        videoTrack.Items.Should().HaveCount(3);
        videoTrack.Items[0].AgentRationale.Should().Be("Second carries the reason");
        videoTrack.Items[1].AgentRationale.Should().Be("First reason wins");
        videoTrack.Items[2].AgentRationale.Should().Be("Edit-level fallback rationale");
    }

    /// <summary>
    /// F2: a null or blank <c>FromId</c> — including a null element in the Keep list — must not
    /// throw, and must be skipped entirely rather than keying the join map, so a span that
    /// references a blank id simply does not resolve and falls back to the edit-level rationale.
    /// These values are reachable despite the non-nullable <c>string</c> properties, because the
    /// decision arrives as JSON.
    /// </summary>
    [Fact]
    public void Build_WithNullOrBlankKeepFromId_DoesNotThrow_AndSkipsThoseEntries()
    {
        // Arrange
        var artifact = CreateMinimalArtifact(durationSec: 120.0);

        var spans = new List<VideoCompileStepExecutor.ResolvedSpan>
        {
            new(RequestedStart: 0.0, RequestedEnd: 5.0, SnappedStart: 0.0, SnappedEnd: 5.0, StartFrame: 0, EndFrame: 150, SourceIndex: 0, OriginKeepFromIds: new[] { "shot-1" }),
            new(RequestedStart: 10.0, RequestedEnd: 15.0, SnappedStart: 10.0, SnappedEnd: 15.0, StartFrame: 300, EndFrame: 450, SourceIndex: 0, OriginKeepFromIds: new[] { string.Empty }),
            new(RequestedStart: 20.0, RequestedEnd: 25.0, SnappedStart: 20.0, SnappedEnd: 25.0, StartFrame: 600, EndFrame: 750, SourceIndex: 0, OriginKeepFromIds: null),
        };

        var timeline = OutputTimeline.Build(spans, HardCuts(spans.Count));

        var decision = new VideoEditDecisionOutput
        {
            Keep = new List<VideoEditKeepSpan>
            {
                new() { FromId = null!, ToId = "shot-1", Reason = "Blank id must not be keyed" },
                new() { FromId = string.Empty, ToId = "shot-1", Reason = "Empty id must not be keyed" },
                new() { FromId = "   ", ToId = "shot-1", Reason = "Whitespace id must not be keyed" },
                null!,
                new() { FromId = "shot-1", ToId = "shot-1", Reason = "Real reason" },
            },
            EditRationale = "Edit-level fallback rationale",
        };

        // Act — null/blank FromIds and a null Keep element must all be survivable.
        var manifest = TimelineManifestBuilder.Build(
            projectId: Guid.NewGuid(),
            executionId: Guid.NewGuid(),
            artifact: artifact,
            resolvedSpans: spans,
            timeline: timeline,
            outputStorageKey: "output.mp4",
            width: 1920,
            height: 1080,
            fps: 30.0,
            decision: decision);

        // Assert
        var videoTrack = manifest.Tracks.Single(t => t.Type == "video");
        videoTrack.Items.Should().HaveCount(3);
        videoTrack.Items[0].AgentRationale.Should().Be("Real reason");
        // The blank-id entries were skipped, so an empty origin id resolves to nothing at all.
        videoTrack.Items[1].AgentRationale.Should().Be("Edit-level fallback rationale");
        videoTrack.Items[2].AgentRationale.Should().Be("Edit-level fallback rationale");
    }

    /// <summary>
    /// F2 regression guard: the pre-existing rationale join for the NON-duplicate case is
    /// unchanged — each cut still joins to its own Keep entry's reason by FromId, and not by
    /// loop index.
    /// </summary>
    [Fact]
    public void Build_WithUniqueKeepFromIds_JoinsEachCutToItsOwnReason()
    {
        // Arrange
        var artifact = CreateMinimalArtifact(durationSec: 120.0);

        var spans = new List<VideoCompileStepExecutor.ResolvedSpan>
        {
            new(RequestedStart: 0.0, RequestedEnd: 5.0, SnappedStart: 0.0, SnappedEnd: 5.0, StartFrame: 0, EndFrame: 150, SourceIndex: 0, OriginKeepFromIds: new[] { "shot-1" }),
            new(RequestedStart: 10.0, RequestedEnd: 15.0, SnappedStart: 10.0, SnappedEnd: 15.0, StartFrame: 300, EndFrame: 450, SourceIndex: 0, OriginKeepFromIds: new[] { "shot-2" }),
        };

        var timeline = OutputTimeline.Build(spans, HardCuts(spans.Count));

        var decision = new VideoEditDecisionOutput
        {
            Keep = new List<VideoEditKeepSpan>
            {
                new() { FromId = "shot-1", ToId = "shot-1", Reason = "Opening hook" },
                new() { FromId = "shot-2", ToId = "shot-2", Reason = "Payoff moment" },
            },
            EditRationale = "Edit-level fallback rationale",
        };

        // Act
        var manifest = TimelineManifestBuilder.Build(
            projectId: Guid.NewGuid(),
            executionId: Guid.NewGuid(),
            artifact: artifact,
            resolvedSpans: spans,
            timeline: timeline,
            outputStorageKey: "output.mp4",
            width: 1920,
            height: 1080,
            fps: 30.0,
            decision: decision);

        // Assert
        var videoTrack = manifest.Tracks.Single(t => t.Type == "video");
        videoTrack.Items.Should().HaveCount(2);
        videoTrack.Items[0].AgentRationale.Should().Be("Opening hook");
        videoTrack.Items[1].AgentRationale.Should().Be("Payoff moment");
    }

    [Fact]
    public void Build_WithMultiSourceArtifact_PopulatesSourcesProperly()
    {
        // Arrange
        var sources = new List<VideoAnalysisSourceInfo>
        {
            new(
                SourceIndex: 0,
                StorageKey: "projects/p/cam-a.mp4",
                Media: new VideoAnalysisMedia(50.0, 60, 1, 1920, 1080)),
            new(
                SourceIndex: 1,
                StorageKey: "projects/p/cam-b.mp4",
                Media: new VideoAnalysisMedia(50.0, 24, 1, 3840, 2160)),
        };

        var artifact = CreateMinimalArtifact(durationSec: 100.0, sources: sources);

        var spans = new List<VideoCompileStepExecutor.ResolvedSpan>
        {
            new(0, 5, 0, 5, 0, 300, 0),
            new(10, 15, 10, 15, 240, 360, 1),
        };
        var seams = new List<SeamPlan>
        {
            new(0, SeamTreatment.HardCut, 0, 0, string.Empty, 0, "hardCut"),
        };
        var timeline = OutputTimeline.Build(spans, seams);

        // Act
        var manifest = TimelineManifestBuilder.Build(
            Guid.NewGuid(),
            Guid.NewGuid(),
            artifact,
            spans,
            timeline,
            "out.mp4",
            1920,
            1080,
            30.0);

        // Assert
        manifest.Sources.Should().HaveCount(2);
        manifest.Sources[0].Index.Should().Be(0);
        manifest.Sources[0].StorageKey.Should().Be("projects/p/cam-a.mp4");
        manifest.Sources[0].Fps.Should().Be(60.0);
        manifest.Sources[0].Width.Should().Be(1920);

        manifest.Sources[1].Index.Should().Be(1);
        manifest.Sources[1].StorageKey.Should().Be("projects/p/cam-b.mp4");
        manifest.Sources[1].Fps.Should().Be(24.0);
        manifest.Sources[1].Width.Should().Be(3840);
    }

    [Fact]
    public void Build_WithAllSixStudioTracks_ProducesExpectedTracksAndItems()
    {
        // Arrange
        var projectId = Guid.NewGuid();
        var executionId = Guid.NewGuid();

        var shots = new List<VideoAnalysisShot>
        {
            new(
                Id: "shot-1",
                StartSec: 0.0,
                EndSec: 10.0,
                Audio: new VideoAnalysisShotAudio(-18.5, -3.0, 0.8, "Medium")),
        };

        var segments = new List<VideoAnalysisSegment>
        {
            new(
                Id: "seg-1",
                Shot: "shot-1",
                StartSec: 1.0,
                EndSec: 4.0,
                Text: "Welcome to ReelBolt studio.",
                SourceIndex: 0),
        };

        var insertRegions = new List<VideoInsertRegionTrack>
        {
            new(
                Id: "screen-laptop",
                ShotId: "shot-1",
                StartSec: 0.0,
                EndSec: 10.0,
                Keyframes: new List<VideoInsertQuadKeyframe>
                {
                    new(0.5, 0.1, 0.1, 0.3, 0.1, 0.1, 0.25, 0.3, 0.25),
                },
                Confidence: 0.94,
                MeanAreaRatio: 0.2,
                MeanAspectRatio: 1.77,
                MotionClass: "Static",
                ColorName: "green",
                SourceIndex: 0,
                MeanCurvature: 0.0,
                ChromaKey: new VideoInsertChromaKey(0, 255, 0, 0.15, 0.05, 0.98)),
        };

        var artifact = CreateMinimalArtifact(
            durationSec: 60.0,
            shots: shots,
            segments: segments,
            insertRegions: insertRegions);

        var spans = new List<VideoCompileStepExecutor.ResolvedSpan>
        {
            new(0.0, 10.0, 0.0, 10.0, 0, 300, 0),
        };
        var timeline = OutputTimeline.Build(spans, Array.Empty<SeamPlan>());

        var screenInserts = new List<ResolvedScreenInsert>
        {
            new(
                RegionId: "screen-laptop",
                LocalAssetPath: "/tmp/demo.png",
                OutputStartSec: 0.5,
                OutputEndSec: 5.0,
                Keyframes: Array.Empty<InsertQuadFrame>(),
                FpsNum: 30,
                FpsDen: 1,
                Mesh: null,
                Matte: null,
                AssetStorageKey: "projects/p/demo.png"),
        };

        var overlays = new List<ResolvedOverlay>
        {
            new(
                PlacementId: "title-header",
                Kind: "headline",
                SanitizedText: "REELBOLT",
                SanitizedSubtext: "AI Video Editor",
                DurationMs: 3000,
                Emphasis: "bold-accent",
                OutputStartSec: 1.0,
                OutputEndSec: 4.0,
                Rect: new VideoAnalysisRect(10, 10, 200, 50),
                TextColor: "white"),
        };

        var sfx = new List<ResolvedSfxCue>
        {
            new(
                SfxId: "whoosh-1",
                AnchorId: "s0",
                ProjectFileId: Guid.NewGuid(),
                ClipName: "whoosh_fast.wav",
                LocalPath: "/tmp/whoosh.wav",
                OutputStartSec: 0.5,
                PlayDurationSec: 1.2,
                GainLinear: 0.5, // -6 dB
                FadeOutSec: 0.05,
                Timing: "OnBeat"),
        };

        var music = new ResolvedMusic(
            TrackId: "music-cyber",
            ProjectFileId: Guid.NewGuid(),
            TrackName: "Cyberpunk Pulse",
            LocalPath: "/tmp/music.mp3",
            TrackDurationSec: 120.0,
            OutputDurationSec: 10.0,
            PlayEndSec: 10.0,
            LoopInput: false,
            BedGainLinear: 1.0, // 0 dB
            DuckGainLinear: 0.25, // -12 dB
            RampSec: 0.5,
            FadeInSec: 0.5,
            FadeOutSec: 0.5,
            LiftWindows: new List<MusicLiftWindow>
            {
                new(StartSec: 5.0, EndSec: 8.0),
            });

        // Act
        var manifest = TimelineManifestBuilder.Build(
            projectId: projectId,
            executionId: executionId,
            artifact: artifact,
            resolvedSpans: spans,
            timeline: timeline,
            outputStorageKey: "compiled.mp4",
            width: 1920,
            height: 1080,
            fps: 30.0,
            overlays: overlays,
            inserts: screenInserts,
            music: music,
            sfx: sfx);

        // Assert: 6 tracks
        manifest.Tracks.Should().HaveCount(6);

        // 1. Video track
        var videoTrack = manifest.Tracks.Single(t => t.Type == "video");
        videoTrack.Items.Should().HaveCount(1);
        videoTrack.Items[0].Id.Should().Be("cut-1");
        videoTrack.Items[0].TimelineStartSec.Should().Be(0.0);
        videoTrack.Items[0].TimelineEndSec.Should().Be(10.0);

        // 2. Inserts track
        var insertsTrack = manifest.Tracks.Single(t => t.Type == "inserts");
        insertsTrack.Items.Should().HaveCount(1);
        var insertItem = insertsTrack.Items[0];
        insertItem.Id.Should().Be("insert-screen-laptop");
        insertItem.RegionId.Should().Be("screen-laptop");
        insertItem.AssetStorageKey.Should().Be("projects/p/demo.png");
        insertItem.Surface.Should().Be("planar");
        insertItem.Confidence.Should().Be(0.94);
        insertItem.SolvedColorKey.Should().Be("#00FF00");
        insertItem.QuadKeyframes.Should().NotBeNull().And.HaveCount(1);

        // 3. Graphics track
        var graphicsTrack = manifest.Tracks.Single(t => t.Type == "graphics");
        graphicsTrack.Items.Should().HaveCount(1);
        var overlayItem = graphicsTrack.Items[0];
        overlayItem.Id.Should().Be("overlay-title-header");
        overlayItem.PlacementId.Should().Be("title-header");
        overlayItem.Text.Should().Be("REELBOLT");
        overlayItem.Subtext.Should().Be("AI Video Editor");
        overlayItem.Style.Should().Be("bold-accent");
        overlayItem.TimelineStartSec.Should().Be(1.0);
        overlayItem.TimelineEndSec.Should().Be(4.0);

        // 4. Dialogue track
        var dialogueTrack = manifest.Tracks.Single(t => t.Type == "dialogue");
        dialogueTrack.Items.Should().HaveCount(1);
        var dialogueItem = dialogueTrack.Items[0];
        dialogueItem.Id.Should().Be("seg-1");
        dialogueItem.Text.Should().Be("Welcome to ReelBolt studio.");
        dialogueItem.TimelineStartSec.Should().Be(1.0);
        dialogueItem.TimelineEndSec.Should().Be(4.0);
        dialogueItem.RmsDbfs.Should().Be(-18.5);

        // 5. SFX track
        var sfxTrack = manifest.Tracks.Single(t => t.Type == "sfx");
        sfxTrack.Items.Should().HaveCount(1);
        var sfxItem = sfxTrack.Items[0];
        sfxItem.Id.Should().Be("sfx-whoosh-1");
        sfxItem.SfxId.Should().Be("whoosh-1");
        sfxItem.ClipName.Should().Be("whoosh_fast.wav");
        sfxItem.TimelineStartSec.Should().Be(0.5);
        sfxItem.TimelineEndSec.Should().Be(1.7);
        sfxItem.DurationSec.Should().Be(1.2);
        sfxItem.GainDb.Should().BeApproximately(-6.0, 0.1);
        sfxItem.Timing.Should().Be("OnBeat");

        // 6. Music track
        var musicTrack = manifest.Tracks.Single(t => t.Type == "music");
        musicTrack.Items.Should().HaveCount(1);
        var musicItem = musicTrack.Items[0];
        musicItem.Id.Should().Be("music-music-cyber");
        musicItem.TrackId.Should().Be("music-cyber");
        musicItem.TrackName.Should().Be("Cyberpunk Pulse");
        musicItem.BedDbfs.Should().Be(0.0);
        musicItem.DuckedDbfs.Should().BeApproximately(-12.0, 0.1);
        musicItem.DuckingEnvelope.Should().NotBeNull().And.NotBeEmpty();
        musicItem.DuckingEnvelope![0].GainDb.Should().BeApproximately(-12.0, 0.1);
    }

    [Fact]
    public void TimelineManifest_RoundtripSerialization_PreservesAllData()
    {
        // Arrange
        var original = new TimelineManifest(
            Version: 1,
            ProjectId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            OutputVideo: new TimelineOutputVideo(1920, 1080, 29.97, 12.5, "out/key.mp4"),
            Sources: new List<TimelineSource>
            {
                new(0, "src/0.mp4", 30.0, 1920, 1080, 29.97),
            },
            Tracks: new List<TimelineTrack>
            {
                new(
                    Id: "track-video",
                    Type: "video",
                    Name: "Video",
                    Index: 0,
                    Items: new List<TimelineItem>
                    {
                        new(
                            Id: "cut-1",
                            TimelineStartSec: 0.0,
                            TimelineEndSec: 5.0,
                            ShotId: "shot-01",
                            SourceIndex: 0,
                            SourceStartSec: 10.0,
                            SourceEndSec: 15.0,
                            Label: "Introduction",
                            AgentRationale: "Clear hook",
                            LookGroup: "look-A",
                            Color: "Cinematic Teal",
                            ColorGrade: "Cinematic Teal",
                            TransitionOut: "wipeleft"),
                    }),
                new(
                    Id: "track-music",
                    Type: "music",
                    Name: "Music",
                    Index: 1,
                    Items: new List<TimelineItem>
                    {
                        new(
                            Id: "music-1",
                            TimelineStartSec: 0.0,
                            TimelineEndSec: 12.5,
                            TrackId: "synth-01",
                            TrackName: "Synth Wave",
                            BedDbfs: -3.0,
                            DuckedDbfs: -15.0,
                            DuckingEnvelope: new List<TimelineDuckingPoint>
                            {
                                new(0.0, -15.0),
                                new(5.0, -3.0),
                                new(12.5, -15.0),
                            }),
                    }),
            });

        // Act
        string json = JsonSerializer.Serialize(original, JsonOptions);
        var roundtripped = JsonSerializer.Deserialize<TimelineManifest>(json, JsonOptions);

        // Assert
        roundtripped.Should().NotBeNull();
        roundtripped!.Version.Should().Be(original.Version);
        roundtripped.ProjectId.Should().Be(original.ProjectId);
        roundtripped.ExecutionId.Should().Be(original.ExecutionId);
        roundtripped.OutputVideo.Should().BeEquivalentTo(original.OutputVideo);
        roundtripped.Sources.Should().BeEquivalentTo(original.Sources);
        roundtripped.Tracks.Should().BeEquivalentTo(original.Tracks);

        // Ensure null properties are not serialized into the JSON
        json.Should().NotContain("\"solvedColorKey\"");
        json.Should().NotContain("\"quadKeyframes\"");
        json.Should().NotContain("\"rmsDbfs\"");
    }

    [Fact]
    public void Manifest_DoesNotContainForbiddenTrademarks()
    {
        // Verify strict naming rules: zero DaVinci trademarks, compound clip called Unified Program Mode
        var manifest = new TimelineManifest(
            Version: 1,
            ProjectId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            OutputVideo: new TimelineOutputVideo(1920, 1080, 30.0, 10.0, "out.mp4"),
            Sources: new List<TimelineSource>(),
            Tracks: new List<TimelineTrack>());

        string json = JsonSerializer.Serialize(manifest, JsonOptions);
        json.ToLowerInvariant().Should().NotContain("davinci");
        json.ToLowerInvariant().Should().NotContain("resolve");
        json.ToLowerInvariant().Should().NotContain("compound_clip");
        json.ToLowerInvariant().Should().NotContain("compound clip");
    }

    [Fact]
    public void Build_WithWordsDialogue_WhenSegmentsNull_UsesWordsFallback()
    {
        // Arrange
        var words = new List<VideoAnalysisWord>
        {
            new("w1", 1.0, 2.5, "Hello", 0),
            new("w2", 2.6, 4.0, "World", 0),
        };

        var artifact = new VideoAnalysisArtifact(
            Version: 1,
            Media: new VideoAnalysisMedia(30.0, 30, 1, 1920, 1080),
            Shots: Array.Empty<VideoAnalysisShot>(),
            SilenceSpans: Array.Empty<VideoAnalysisSilenceSpan>(),
            Segments: Array.Empty<VideoAnalysisSegment>(),
            Words: words,
            OfferedIds: Array.Empty<string>(),
            Provenance: new VideoAnalysisProvenance(VideoTranscriptionMode.Off, false, false));

        var spans = new List<VideoCompileStepExecutor.ResolvedSpan>
        {
            new(0.0, 5.0, 0.0, 5.0, 0, 150, 0),
        };
        var timeline = OutputTimeline.Build(spans, Array.Empty<SeamPlan>());

        // Act
        var manifest = TimelineManifestBuilder.Build(
            Guid.NewGuid(),
            Guid.NewGuid(),
            artifact,
            spans,
            timeline,
            "out.mp4",
            1920,
            1080,
            30.0);

        // Assert
        manifest.Tracks.Should().ContainSingle(t => t.Type == "dialogue");
        var dialogueTrack = manifest.Tracks.Single(t => t.Type == "dialogue");
        dialogueTrack.Items.Should().HaveCount(2);
        dialogueTrack.Items[0].Id.Should().Be("w1");
        dialogueTrack.Items[0].Text.Should().Be("Hello");
        dialogueTrack.Items[1].Id.Should().Be("w2");
        dialogueTrack.Items[1].Text.Should().Be("World");
    }

    [Fact]
    public void Build_WithMusicNoLiftWindows_GeneratesFlatEnvelope()
    {
        // Arrange
        var artifact = CreateMinimalArtifact();
        var spans = new List<VideoCompileStepExecutor.ResolvedSpan>
        {
            new(0.0, 10.0, 0.0, 10.0, 0, 300, 0),
        };
        var timeline = OutputTimeline.Build(spans, Array.Empty<SeamPlan>());

        var music = new ResolvedMusic(
            TrackId: "music-ambient",
            ProjectFileId: Guid.NewGuid(),
            TrackName: "Ambient Chill",
            LocalPath: "/tmp/chill.mp3",
            TrackDurationSec: 60.0,
            OutputDurationSec: 10.0,
            PlayEndSec: 10.0,
            LoopInput: false,
            BedGainLinear: 0.8,
            DuckGainLinear: 0.2,
            RampSec: 0.5,
            FadeInSec: 0.5,
            FadeOutSec: 0.5,
            LiftWindows: Array.Empty<MusicLiftWindow>());

        // Act
        var manifest = TimelineManifestBuilder.Build(
            Guid.NewGuid(),
            Guid.NewGuid(),
            artifact,
            spans,
            timeline,
            "out.mp4",
            1920,
            1080,
            30.0,
            music: music);

        // Assert
        var musicTrack = manifest.Tracks.Single(t => t.Type == "music");
        var musicItem = musicTrack.Items.Single();
        musicItem.DuckingEnvelope.Should().HaveCount(2);
        musicItem.DuckingEnvelope![0].TimeSec.Should().Be(0.0);
        musicItem.DuckingEnvelope[1].TimeSec.Should().Be(10.0);
    }

    [Fact]
    public void Build_WithZeroOrNegativeGains_ClampsCorrectly()
    {
        // Arrange
        var artifact = CreateMinimalArtifact();
        var spans = new List<VideoCompileStepExecutor.ResolvedSpan>
        {
            new(0.0, 5.0, 0.0, 5.0, 0, 150, 0),
        };
        var timeline = OutputTimeline.Build(spans, Array.Empty<SeamPlan>());

        var sfx = new List<ResolvedSfxCue>
        {
            new(
                SfxId: "sfx-silent",
                AnchorId: "s0",
                ProjectFileId: Guid.NewGuid(),
                ClipName: "silent.wav",
                LocalPath: "/tmp/silent.wav",
                OutputStartSec: 0.0,
                PlayDurationSec: 1.0,
                GainLinear: 0.0, // Should clamp to -96 dB
                FadeOutSec: 0.0),
        };

        var music = new ResolvedMusic(
            TrackId: "music-zero",
            ProjectFileId: Guid.NewGuid(),
            TrackName: "Zero Track",
            LocalPath: "/tmp/zero.mp3",
            TrackDurationSec: 10.0,
            OutputDurationSec: 5.0,
            PlayEndSec: 5.0,
            LoopInput: false,
            BedGainLinear: 0.0,  // Should default to 0.0 dB
            DuckGainLinear: 0.0, // Should default to -12.0 dB
            RampSec: 0.5,
            FadeInSec: 0.0,
            FadeOutSec: 0.0,
            LiftWindows: Array.Empty<MusicLiftWindow>());

        // Act
        var manifest = TimelineManifestBuilder.Build(
            Guid.NewGuid(),
            Guid.NewGuid(),
            artifact,
            spans,
            timeline,
            "out.mp4",
            1920,
            1080,
            30.0,
            music: music,
            sfx: sfx);

        // Assert
        var sfxTrack = manifest.Tracks.Single(t => t.Type == "sfx");
        sfxTrack.Items.Single().GainDb.Should().Be(-96.0);

        var musicTrack = manifest.Tracks.Single(t => t.Type == "music");
        var musicItem = musicTrack.Items.Single();
        musicItem.BedDbfs.Should().Be(0.0);
        musicItem.DuckedDbfs.Should().Be(-12.0);
    }
}
