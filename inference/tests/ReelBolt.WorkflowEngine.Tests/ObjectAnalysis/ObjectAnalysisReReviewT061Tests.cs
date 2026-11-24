#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.ObjectAnalysis;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Editing;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Tenancy;
using ReelBolt.Shared.Workflows;
using Xunit;
using Xunit.Abstractions;

namespace ReelBolt.WorkflowEngine.Tests.ObjectAnalysis;

/// <summary>QA T-061 re-review of PR #913 (uncommitted, QA-owned): migration replay, worked price examples, fps cap vs engine budget.</summary>
public class ObjectAnalysisReReviewT061Tests(ITestOutputHelper output)
{
    /// <summary>
    /// Worked examples: what the estimate/402 gate prices (probe step + definition hint) equals what the engine holds for the saved
    /// step (real definition row), add-on by add-on, through SourceFootageCredits and SegmentationCreditPricing, with the cap from
    /// ObjectAnalysisConfig.MaxSecondsFor that both controller paths use.
    /// </summary>
    [Theory]
    [InlineData(45.0, 60, 1920, 1080, 90, 281_723L)]
    [InlineData(45.0, 30, 1920, 1080, 45, 141_695L)]
    [InlineData(300.0, 60, 3840, 2160, 600, 1_875_375L)]
    public async Task Estimate_price_equals_engine_hold_price_worked_example(
        double duration, int fps, int width, int height, int expectedCap, long expectedSegmentationMillis)
    {
        await using InferenceApiDbContext db = new(new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);
        Guid owner = Guid.NewGuid();
        Guid org = TestTenancy.OrgOf(owner);
        Project project = new() { Id = Guid.NewGuid(), Name = "p", OwnerId = owner, OrganizationId = org, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.Projects.Add(project);
        string key = $"projects/{project.Id}/userFiles/v.mp4";
        ProjectFile video = new() { Id = Guid.NewGuid(), ProjectId = project.Id, OriginalFileName = "v.mp4", StorageKey = key, MimeType = "video/mp4", SizeBytes = 100_000_000 };
        db.ProjectFiles.Add(video);
        db.MediaDerivatives.Add(new MediaDerivative
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            SourceProjectFileId = video.Id,
            SourceStorageKey = key,
            Kind = MediaDerivativeKind.Proxy,
            StorageKey = "d/proxy.mp4",
            CacheKey = "k",
            SourceProbeJson = new MediaSourceProbe(duration, fps, 1, width, height, "h264", "aac", "mp4", 2, 48000, null, true).ToJson()
        });
        WorkflowDefinition saved = new() { Id = Guid.NewGuid(), ProjectId = project.Id, Name = "Find object: car", Kind = WorkflowDefinitionKind.ObjectAnalysis, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.WorkflowDefinitions.Add(saved);
        await db.SaveChangesAsync();

        int cap = ObjectAnalysisConfig.MaxSecondsFor(duration, fps)!.Value;
        cap.Should().Be(expectedCap);
        string json = ObjectAnalysisConfig.Serialize(ObjectAnalysisConfig.Build(
            new VideoSourceRef(VideoSourceKind.ProjectFile, ProjectFileId: video.Id), "car", ObjectMatchScope.AllInstances, cap));

        Guid probeDefinition = Guid.NewGuid();
        WorkflowStep probe = new() { StepOrder = 1, StepType = StepType.VideoAnalyze, VideoAnalyzeConfigJson = json, WorkflowDefinitionId = probeDefinition };
        WorkflowStep real = new() { StepOrder = 1, StepType = StepType.VideoAnalyze, VideoAnalyzeConfigJson = json, WorkflowDefinitionId = saved.Id };

        IReadOnlyDictionary<int, IReadOnlyList<SourceFootage>> engineSide = await SourceFootageCredits.ResolveAsync(
            db.ProjectFiles, db.MediaDerivatives, db.WorkflowDefinitions, [real], org, CancellationToken.None);
        IReadOnlyDictionary<int, IReadOnlyList<SourceFootage>> estimateSide;
        using (SourceFootageDefinitions.Enter(probeDefinition, project.Id))
        {
            estimateSide = await SourceFootageCredits.ResolveAsync(
                db.ProjectFiles, db.MediaDerivatives, db.WorkflowDefinitions, [probe], org, CancellationToken.None);
        }

        long footageHold = SourceFootageCredits.AddOnMillis(real, engineSide.GetValueOrDefault(1), SeedProfilePricer.Instance);
        long footageEstimate = SourceFootageCredits.AddOnMillis(probe, estimateSide.GetValueOrDefault(1), SeedProfilePricer.Instance);
        long segmentation = SegmentationCreditPricing.AddOnMillis(json);
        int pricedFrames = SegmentationCreditPricing.PricedFramesFor(cap);
        int sourceFrames = (int)Math.Ceiling(duration * fps);

        output.WriteLine($"T061 {duration}s {width}x{height} {fps}fps: cap={cap}s pricedFrames={pricedFrames} sourceFrames={sourceFrames} " +
                         $"segmentationMillis={segmentation} footageEstimate={footageEstimate} footageHold={footageHold} " +
                         $"estimateAddOns={segmentation + footageEstimate} holdAddOns={segmentation + footageHold}");

        segmentation.Should().Be(expectedSegmentationMillis);
        footageEstimate.Should().Be(footageHold, "the estimate must price the footage add-on the engine holds");
        sourceFrames.Should().BeLessThanOrEqualTo(pricedFrames, "the engine refuses SEGMENTATION_FRAME_BUDGET_EXCEEDED above it");
    }

    /// <summary>The cap against the engine's two refusals (SegmentationTrackingService steps 4 and 4b) at 24/30/60/120 fps and NTSC rates.</summary>
    [Fact]
    public void Cap_never_trips_the_engine_duration_or_frame_budget_and_refuses_exactly_past_600_equivalent_seconds()
    {
        double[] rates = [23.976, 24, 25, 29.97, 30, 50, 59.94, 60, 119.88, 120];
        List<string> bad = [];
        foreach (double fps in rates)
        {
            for (double d = 0.04; d <= 650; d += d < 5 ? 0.37 : d < 100 ? 1.013 : 3.7)
            {
                int? cap = ObjectAnalysisConfig.MaxSecondsFor(d, fps);
                double equivalent = d * Math.Max(fps, 30) / 30;
                if (cap is null)
                {
                    if (equivalent <= 600) bad.Add($"{d:0.###}s@{fps}: refused at {equivalent:0.###} equivalent s");
                    continue;
                }

                if (equivalent > 600 + 1e-6) bad.Add($"{d:0.###}s@{fps}: accepted at {equivalent:0.###} equivalent s");
                int clamped = SegmentationLimits.ClampSeconds(cap.Value);
                if (d > clamped + 1e-6) bad.Add($"{d:0.###}s@{fps}: SOURCE_TOO_LONG at cap {clamped}");
                int frames = Math.Max(1, (int)Math.Ceiling(d * fps));
                if (frames > SegmentationCreditPricing.PricedFramesFor(clamped)) bad.Add($"{d:0.###}s@{fps}: FRAME_BUDGET {frames} > {SegmentationCreditPricing.PricedFramesFor(clamped)}");
            }
        }

        ObjectAnalysisConfig.MaxSecondsFor(600, 30).Should().Be(600);
        ObjectAnalysisConfig.MaxSecondsFor(300, 60).Should().Be(600);
        ObjectAnalysisConfig.MaxSecondsFor(300.1, 60).Should().BeNull();
        ObjectAnalysisConfig.MaxSecondsFor(150, 120).Should().Be(600);
        ObjectAnalysisConfig.MaxSecondsFor(150.1, 120).Should().BeNull();
        ObjectAnalysisConfig.MaxSecondsFor(600.5, 24).Should().BeNull();
        ObjectAnalysisConfig.MaxSecondsFor(0.2, 24).Should().Be(1);
        ObjectAnalysisConfig.MaxSecondsFor(null, 60).Should().Be(600);
        bad.Should().BeEmpty();
    }
}
