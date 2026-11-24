#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.ObjectAnalysis;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Editing;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Tenancy;
using ReelBolt.Shared.Workflows;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.ObjectAnalysis;

/// <summary>
/// The object-analysis estimate and 402 gate price a probe step of a hidden workflow that is saved only after every gate passes.
/// A probe with no resolvable definition dropped the source-footage add-on the engine adds to the hold of the saved step, so the
/// estimate was lower than the reservation. The controller now enters a definition hint around the preflight; the probe then resolves
/// the same footage as the saved step.
/// </summary>
public class ObjectAnalysisEstimateFootageTests
{
    [Fact]
    public async Task Estimate_probe_step_resolves_the_same_source_footage_as_the_saved_step()
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
        ProjectFile video = new() { Id = Guid.NewGuid(), ProjectId = project.Id, OriginalFileName = "v.mp4", StorageKey = key, MimeType = "video/mp4", SizeBytes = 500_000_000 };
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
            SourceProbeJson = new MediaSourceProbe(540, 30, 1, 3840, 2160, "h264", "aac", "mp4", 2, 48000, null, true).ToJson()
        });
        WorkflowDefinition saved = new() { Id = Guid.NewGuid(), ProjectId = project.Id, Name = "Find object: car", Kind = WorkflowDefinitionKind.ObjectAnalysis, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.WorkflowDefinitions.Add(saved);
        await db.SaveChangesAsync();

        string json = ObjectAnalysisConfig.Serialize(ObjectAnalysisConfig.Build(
            new VideoSourceRef(VideoSourceKind.ProjectFile, ProjectFileId: video.Id), "car", ObjectMatchScope.AllInstances, 540));

        Guid probeDefinition = Guid.NewGuid();
        WorkflowStep probe = new() { StepOrder = 1, StepType = StepType.VideoAnalyze, VideoAnalyzeConfigJson = json, WorkflowDefinitionId = probeDefinition };
        WorkflowStep real = new() { StepOrder = 1, StepType = StepType.VideoAnalyze, VideoAnalyzeConfigJson = json, WorkflowDefinitionId = saved.Id };

        IReadOnlyDictionary<int, IReadOnlyList<SourceFootage>> engineSide = await SourceFootageCredits.ResolveAsync(
            db.ProjectFiles, db.MediaDerivatives, db.WorkflowDefinitions, [real], org, CancellationToken.None);

        // Without the hint the probe resolves nothing (the defect).
        (await SourceFootageCredits.ResolveAsync(
            db.ProjectFiles, db.MediaDerivatives, db.WorkflowDefinitions, [probe], org, CancellationToken.None)).Should().BeEmpty();

        IReadOnlyDictionary<int, IReadOnlyList<SourceFootage>> estimateSide;
        using (SourceFootageDefinitions.Enter(probeDefinition, project.Id))
        {
            estimateSide = await SourceFootageCredits.ResolveAsync(
                db.ProjectFiles, db.MediaDerivatives, db.WorkflowDefinitions, [probe], org, CancellationToken.None);
        }

        engineSide.Should().ContainKey(1, "the engine's hold sees the 9-minute 4K source");
        estimateSide.Should().ContainKey(1, "the estimate and the 402 gate must price the same source footage the hold does");
        estimateSide[1].Should().BeEquivalentTo(engineSide[1]);
        SourceFootageDefinitions.Current.Should().BeNull("the hint is scoped to the preflight call");
    }
}
