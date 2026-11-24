using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Editing;
using ReelBolt.Shared.Tenancy;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Billing;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Security ENG-3 on the timeline render: a clip whose asset is a playlist or list file is refused as
/// the author's input. A playlist name is refused before the download; a list file under a media
/// name is refused by the probe's demuxer whitelist. Neither reaches the renderer.
/// </summary>
public class TimelineRenderUserMediaRefusalTests
{
    private static readonly Guid ProjectId = Guid.Parse("c6c6c6c6-0000-4000-8000-0000000000c6");
    private static readonly Guid OrgId = Guid.Parse("0b0b0b0b-0000-4000-8000-00000000000b");
    private static readonly Guid FileId = Guid.Parse("f2f2f2f2-0000-4000-8000-0000000000f2");

    [Fact]
    public async Task An_asset_named_as_a_playlist_is_refused_before_it_is_downloaded()
    {
        (StepExecutionResult result, Mock<IProjectFileWorkspace> workspace, Mock<IVideoToolRunner> runner) = await RunAsync(".m3u8", probeRefuses: false);

        result.Status.Should().Be(StepStatus.Failed);
        result.ErrorDetails.Should().StartWith(UserMediaInputFormats.RefusedCode).And.Contain("playlist");
        result.Retryable.Should().BeFalse("the same refusal comes back on every retry");
        FailureClassifier.ForCode(UserMediaInputFormats.RefusedCode).Should().Be(FailureClass.UserInput);
        workspace.Verify(
            w => w.DownloadStorageKeyToFileAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        runner.Invocations.Should().BeEmpty("nothing is rendered from a refused asset");
    }

    [Fact]
    public async Task An_asset_the_probe_refuses_for_its_format_is_the_authors_input_and_is_staged_under_a_media_name()
    {
        (StepExecutionResult result, Mock<IProjectFileWorkspace> workspace, Mock<IVideoToolRunner> runner) = await RunAsync(".mp4", probeRefuses: true);

        result.Status.Should().Be(StepStatus.Failed);
        result.ErrorDetails.Should().StartWith(UserMediaInputFormats.RefusedCode);
        result.Retryable.Should().BeFalse("the same refusal comes back on every retry");
        workspace.Verify(
            w => w.DownloadStorageKeyToFileAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.Is<string>(dest => Path.GetFileName(dest) == "asset-000.mp4"), It.IsAny<CancellationToken>()),
            Times.Once);
        runner.Invocations.Should().BeEmpty("nothing is rendered from a refused asset");
    }

    [Fact]
    public async Task An_asset_with_an_unknown_extension_is_staged_under_the_neutral_one()
    {
        (_, Mock<IProjectFileWorkspace> workspace, _) = await RunAsync(".txt", probeRefuses: true);

        workspace.Verify(
            w => w.DownloadStorageKeyToFileAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.Is<string>(dest => Path.GetFileName(dest) == "asset-000.bin"), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    private static async Task<(StepExecutionResult Result, Mock<IProjectFileWorkspace> Workspace, Mock<IVideoToolRunner> Runner)> RunAsync(
        string extension, bool probeRefuses)
    {
        WorkflowEngineDbContext db = new(new DbContextOptionsBuilder<WorkflowEngineDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        Guid timelineId = Guid.NewGuid(), workflowId = Guid.NewGuid();
        MediaClip clip = new(Guid.NewGuid(), "clip", 0, 60, new AssetRef(AssetRefKind.ProjectFile, FileId, null));
        Track video = new(Guid.NewGuid(), TrackKind.Video, "V1", null, 64, false, true, false, false, null, [clip]);
        EditTimeline document = new(timelineId, ProjectId, "doc", new EditTimelineSettings(1920, 1080, new Rational(30, 1), 48000, new Rational(1, 1), AspectPolicy.Fit, 0), [video], [], null, null);
        db.WorkflowDefinitions.Add(new WorkflowDefinition { Id = workflowId, ProjectId = ProjectId, Name = "w", Kind = WorkflowDefinitionKind.TimelineRender, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        db.ProjectFiles.Add(new ProjectFile
        {
            Id = FileId,
            ProjectId = ProjectId,
            OriginalFileName = "a" + extension,
            // The shape a real upload's object has, built by the same helper the platform uses.
            StorageKey = ReelBolt.Shared.ProjectFilePath.BuildStorageKey(ProjectId, "userFiles", null, "a" + extension),
            MimeType = "video/mp4",
            SizeBytes = 100
        });
        db.EditTimelines.Add(new EditTimelineEntity { Id = timelineId, ProjectId = ProjectId, Name = "doc", DocumentJson = JsonSerializer.Serialize(document, EditTimelineJson.Options), Revision = 1 });
        await db.SaveChangesAsync();

        Mock<IServiceProvider> provider = new();
        provider.Setup(p => p.GetService(typeof(WorkflowEngineDbContext))).Returns(db);
        Mock<IOrganizationEntitlements> entitlements = new();
        entitlements.Setup(e => e.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).Returns(new ValueTask<OrganizationEntitlementSet>(new OrganizationEntitlementSet { Tier = "test", MaxOutputHeight = 2160 }));
        provider.Setup(p => p.GetService(typeof(IOrganizationEntitlements))).Returns(entitlements.Object);
        Mock<IServiceScope> scope = new();
        scope.Setup(s => s.ServiceProvider).Returns(provider.Object);
        Mock<IServiceScopeFactory> scopeFactory = new();
        scopeFactory.Setup(f => f.CreateScope()).Returns(scope.Object);

        Mock<IProjectFileWorkspace> workspace = new();
        workspace.Setup(w => w.DownloadStorageKeyToFileAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((Guid _, string _, string dest, CancellationToken _) => File.WriteAllBytesAsync(dest, [0, 1, 2]));

        Mock<IMediaProbe> probe = new();
        if (probeRefuses)
        {
            probe.Setup(p => p.ProbeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new MediaFormatNotAllowedException("asset-000"));
        }

        Mock<IVideoToolRunner> runner = new();
        TimelineRenderStepExecutor executor = new(
            runner.Object, probe.Object, workspace.Object,
            Options.Create(new VideoEditingOptions { ScratchPath = Path.Combine(Path.GetTempPath(), "tr-eng3-" + Guid.NewGuid().ToString("N")) }),
            scopeFactory.Object, NullLogger<TimelineRenderStepExecutor>.Instance);

        WorkflowStep step = new()
        {
            Id = Guid.NewGuid(),
            StepOrder = 1,
            StepType = StepType.TimelineRender,
            TimelineRenderConfigJson = JsonSerializer.Serialize(new TimelineRenderStepConfig(timelineId, Preset: TimelineRenderPreset.Hd1080p16x9, RegisterProjectFile: false), TimelineRenderStepExecutor.ConfigJsonOptions)
        };
        StepExecutionResult result = await executor.ExecuteAsync(new StepExecutionContext
        {
            Execution = new WorkflowExecution { Id = Guid.NewGuid(), WorkflowDefinitionId = workflowId, ProjectId = ProjectId, OrganizationId = OrgId },
            Step = step,
            AllSteps = [step],
            AccumulatedOutput = string.Empty,
            StepOutputHistory = [],
            CurrentStepIndex = 0,
            IterationCount = 0,
            CorrelationId = "test",
            CancellationToken = CancellationToken.None
        });
        return (result, workspace, runner);
    }
}
