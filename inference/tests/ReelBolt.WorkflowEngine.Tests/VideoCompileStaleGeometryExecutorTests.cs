using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

public partial class VideoCompileStepExecutorTests
{
    /// <summary>
    /// End-to-end (ExecuteAsync) proof of the stale-geometry refusal: the analysis recorded the CODED
    /// 1920x1080 of a rotated clip, the compile's source probe sees the display 1080x1920. Nested so it
    /// reuses the executor harness (private static helpers) without editing any shared file.
    /// </summary>
    public class VideoCompileStaleGeometryExecutorTests
    {
        private const string MaskKey = "projects/p/agentFiles/segmentation/g0.mkv";
        private static readonly byte[] MaskBytes = new byte[512];

        private static string MaskHash() => Convert.ToHexString(SHA256.HashData(MaskBytes)).ToLowerInvariant();

        private static VideoAnalysisArtifact Artifact(
            int recordedWidth, int recordedHeight, bool withTrack, string? geometry = null, bool censorTrack = true)
        {
            VideoAnalysisArtifact a = BuildArtifact(shots: new[] { ("s0", 0.0, 10.0) }, offeredIds: new[] { "s0" });
            var media = new VideoAnalysisMedia(10, 30, 1, recordedWidth, recordedHeight);
            return a with
            {
                Media = media,
                Sources = [new VideoAnalysisSourceInfo(0, SourceVideoKey, media, DecodeGeometry: geometry)],
                InsertRegions = censorTrack && !withTrack
                    ? [new VideoInsertRegionTrack("r0", "s0", 1, 2,
                        [new VideoInsertQuadKeyframe(1, 0.1, 0.1, 0.2, 0.1, 0.2, 0.2, 0.1, 0.2)],
                        0.9, 0.05, 1.0, "Static", "green", SourceIndex: 0, Method: "feature", Label: "licence plate")]
                    : null,
                Provenance = a.Provenance with
                {
                    ObjectTrackingApplied = true,
                    ObjectTrackingLabels = ["licence plate"],
                    SegmentationPrompts = withTrack ? ["cat"] : null
                },
                SegmentationTracks = withTrack
                    ? [new VideoSegmentationTrack("g0", 0, "cat", 1, 2, FrameCount: 24, Fps: 24.0, Coverage: 0.05, Confidence: 0.9,
                        MaskStorageKey: MaskKey, MaskContentHash: MaskHash(), Model: "fal-ai/sam-3-1/video-rle",
                        Facts: new VideoSegmentationFacts(0, 0, 0.5, false, null, null))]
                    : null
            };
        }

        private static async Task<(StepExecutionResult Result, Mock<IProjectFileWorkspace> Workspace)> Run(
            VideoAnalysisArtifact artifact, bool region, int probeWidth = 1080, int probeHeight = 1920, bool altersPixels = false)
        {
            StepExecutionContext context = CreateGraphicsContext(
                artifact, BuildDecisionJson(("s0", "s0", "keep")), "{}",
                out Mock<IProjectFileWorkspace> workspace,
                configOverride: cfg => cfg with
                {
                    GraphicsPlan = null,
                    EnableGraphics = false,
                    Mode = VideoCompileMode.Reencode,
                    CensorLabels = region ? null : ["licence plate"],
                    RegionEffects = region ? [new VideoRegionEffect("cat", RegionEffect.Blur, false, 1.0, 0, 1.0)] : null
                });
            workspace
                .Setup(w => w.DownloadStorageKeyToFileAsync(It.IsAny<Guid>(), MaskKey, It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns<Guid, string, string, CancellationToken>((_, _, dest, _) =>
                {
                    File.WriteAllBytes(dest, MaskBytes);
                    return Task.CompletedTask;
                });

            // The staged source decodes to the rotated (display) size.
            StepExecutionResult result = await CreateExecutor(workspace, configureMediaProbe: probe => probe
                .Setup(p => p.ProbeAsync(It.IsAny<IMediaWorkspace>(), It.Is<string>(s => s.Contains("source-")), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new MediaProbeResult(10, 30, 1, probeWidth, probeHeight, "h264", "aac", 48000, AltersPixelsOnDecode: altersPixels)))
                .ExecuteAsync(context);
            return (result, workspace);
        }

        private static void AssertLeaksNothing(StepExecutionResult result, VideoAnalysisArtifact artifact)
        {
            string text = (result.ErrorDetails ?? "") + "\n" + result.Output;
            text.Should().Contain("1920x1080").And.Contain("1080x1920").And.Contain("Re-run the analyze step");
            text.Should().NotContain(artifact.Sources![0].StorageKey)
                .And.NotContain(MaskKey)
                .And.NotContain("projects/")
                .And.NotContain(Path.GetTempPath().TrimEnd('/'));
        }

        [Fact]
        public async Task Stale_artifact_on_a_rotated_source_fails_CENSOR_UNVERIFIED_without_leaking_keys()
        {
            VideoAnalysisArtifact artifact = Artifact(1920, 1080, withTrack: false);

            (StepExecutionResult result, _) = await Run(artifact, region: false);

            result.Status.Should().Be(StepStatus.Failed);
            ErrorCode(result).Should().Be("CENSOR_UNVERIFIED");
            AssertLeaksNothing(result, artifact);
        }

        [Fact]
        public async Task Stale_artifact_on_a_rotated_source_fails_SEGMENTATION_MASK_UNVERIFIED_before_any_mask_download()
        {
            VideoAnalysisArtifact artifact = Artifact(1920, 1080, withTrack: true);

            (StepExecutionResult result, Mock<IProjectFileWorkspace> workspace) = await Run(artifact, region: true);

            result.Status.Should().Be(StepStatus.Failed);
            ErrorCode(result).Should().Be("SEGMENTATION_MASK_UNVERIFIED");
            AssertLeaksNothing(result, artifact);
            workspace.Verify(w => w.DownloadStorageKeyToFileAsync(
                It.IsAny<Guid>(), MaskKey, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task Matching_artifact_is_not_refused_as_stale_for_censor()
        {
            (StepExecutionResult result, _) = await Run(Artifact(1080, 1920, withTrack: false), region: false);

            (result.ErrorDetails ?? "").Should().NotContain("Re-run the analyze step");
            if (result.Status == StepStatus.Failed) ErrorCode(result).Should().NotBe("CENSOR_UNVERIFIED", result.ErrorDetails);
        }

        [Fact]
        public async Task Matching_artifact_is_not_refused_as_stale_for_region_effects_and_the_mask_is_fetched()
        {
            (StepExecutionResult result, Mock<IProjectFileWorkspace> workspace) = await Run(Artifact(1080, 1920, withTrack: true), region: true);

            (result.ErrorDetails ?? "").Should().NotContain("Re-run the analyze step");
            workspace.Verify(w => w.DownloadStorageKeyToFileAsync(
                It.IsAny<Guid>(), MaskKey, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        }

        // M3 (#776): same size, but the source is 180/mirrored and the analysis carries no upright marker.
        [Theory]
        [InlineData(false, "CENSOR_UNVERIFIED")]
        [InlineData(true, "SEGMENTATION_MASK_UNVERIFIED")]
        public async Task A_same_size_unmarked_analysis_of_a_pixel_altering_source_is_refused(bool region, string code)
        {
            VideoAnalysisArtifact artifact = Artifact(1920, 1080, withTrack: region);

            (StepExecutionResult result, Mock<IProjectFileWorkspace> workspace) =
                await Run(artifact, region, probeWidth: 1920, probeHeight: 1080, altersPixels: true);

            result.Status.Should().Be(StepStatus.Failed);
            ErrorCode(result).Should().Be(code);
            (result.ErrorDetails ?? "").Should().Contain("Re-run the analyze step").And.NotContain("projects/").And.NotContain(MaskKey);
            workspace.Verify(w => w.DownloadStorageKeyToFileAsync(
                It.IsAny<Guid>(), MaskKey, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task A_marked_analysis_or_an_unaltered_source_is_not_refused_as_stale(bool marked)
        {
            VideoAnalysisArtifact artifact = Artifact(1920, 1080, withTrack: true,
                geometry: marked ? VideoAnalysisSourceInfo.UprightGeometry : null);

            (StepExecutionResult result, _) = await Run(
                artifact, region: true, probeWidth: 1920, probeHeight: 1080, altersPixels: marked);

            (result.ErrorDetails ?? "").Should().NotContain("Re-run the analyze step");
        }

        [Fact]
        public async Task The_censor_check_ignores_a_mismatching_source_that_carries_no_censor_track()
        {
            VideoAnalysisArtifact artifact = Artifact(1920, 1080, withTrack: false, censorTrack: false);

            (StepExecutionResult result, _) = await Run(artifact, region: false);

            (result.ErrorDetails ?? "").Should().NotContain("Re-run the analyze step");
        }
    }
}
