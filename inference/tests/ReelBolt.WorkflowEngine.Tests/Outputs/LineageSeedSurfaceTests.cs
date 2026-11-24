#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Editing;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Outputs;

/// <summary>
/// Seeding a timeline from the chain of renders that made a video reads the analysis files and chained manifests of
/// the runs the project serves, and leaves unread those of runs it keeps back.
/// </summary>
public sealed class LineageSeedSurfaceTests
{
    private sealed class Rig
    {
        public SurfaceWorld W { get; } = new();
        public WorkflowStepResult EarlierAnalysis { get; }
        public WorkflowStepResult LaterAnalysis { get; }
        public List<string> Opened { get; } = [];
        public Mock<IFileStorageService> Storage { get; } = new();

        public Rig()
        {
            WorkflowStep analyze = new()
            {
                Id = Guid.NewGuid(),
                WorkflowDefinitionId = W.Workflow.Id,
                AgentDefinitionId = W.Agent.Id,
                StepOrder = 2,
                StepType = StepType.VideoAnalyze,
                Label = "Analyze",
            };
            EarlierAnalysis = Analysis(W.EarlierRun, analyze, "earlier", SurfaceWorld.Marker.AddHours(-2));
            LaterAnalysis = Analysis(W.LaterRun, analyze, "later", SurfaceWorld.Marker.AddDays(2));
            W.Db.WorkflowSteps.Add(analyze);
            W.Db.WorkflowStepResults.AddRange(EarlierAnalysis, LaterAnalysis);
            W.Db.SaveChanges();
            Storage.Setup(s => s.DownloadAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Callback<Guid, string, CancellationToken>((_, key, _) => Opened.Add(key))
                .ReturnsAsync(() => new MemoryStream(Encoding.UTF8.GetBytes("{}")));
        }

        private WorkflowStepResult Analysis(WorkflowExecution run, WorkflowStep step, string name, DateTime at) => new()
        {
            Id = Guid.NewGuid(),
            WorkflowExecutionId = run.Id,
            WorkflowStepId = step.Id,
            Status = StepStatus.Completed,
            ExecutedAt = at,
            CompletedAt = at,
            ArtifactStorageKey = $"projects/{W.Project.Id}/agentFiles/video-analysis/{run.Id:D}/{name}.json",
        };

        /// <summary>Runs the seeding of the later render, whose compile step names <paramref name="analysis"/> and whose manifest names <paramref name="input"/>.</summary>
        public async Task Seed(WorkflowStepResult analysis, WorkflowStepResult input, InferenceApiDbContext? db = null)
        {
            InferenceApiDbContext d = db ?? W.Db;
            W.Step.VideoCompileConfigJson = "{\"analysisStepOrder\":2,\"analysisStepResultId\":\"" + analysis.Id + "\",\"censorLabels\":[\"person\"]}";
            W.Db.SaveChanges();
            TimelineManifest manifest = new(
                1, W.Project.Id, W.LaterRun.Id, new TimelineOutputVideo(1920, 1080, 30, 4, W.LaterResult.OutputStorageKey!),
                [new TimelineSource(0, "projects/x/userFiles/in.mp4", 4, 1920, 1080, 30, StepResultId: input.Id)], []);
            SeedAssetResolver assets = await SeedAssetResolver.LoadAsync(d, W.Project.Id, CancellationToken.None);
            await LineageSeedResolver.TrySeedAsync(
                d, Storage.Object, NullLogger.Instance, W.Project.Id, W.LaterResult, manifest, Guid.NewGuid(), assets, CancellationToken.None);
        }
    }

    [Fact]
    public async Task The_analysis_file_and_chained_manifest_of_an_earlier_run_are_not_opened()
    {
        Rig r = new();
        r.W.In(SurfaceState.Held);
        await r.Seed(r.EarlierAnalysis, r.W.EarlierResult);
        r.Opened.Should().NotContain(r.EarlierAnalysis.ArtifactStorageKey!).And.NotContain(r.W.EarlierResult.TimelineStorageKey!);
    }

    [Fact]
    public async Task The_analysis_file_and_chained_manifest_of_a_later_run_are_opened_while_earlier_outputs_are_kept()
    {
        Rig r = new();
        r.W.In(SurfaceState.Held);
        await r.Seed(r.LaterAnalysis, r.W.LaterResult);
        r.Opened.Should().Contain(r.LaterAnalysis.ArtifactStorageKey!);
        r.Opened.Should().Contain(r.W.LaterResult.TimelineStorageKey!);
    }

    [Theory]
    [MemberData(nameof(SurfaceWorld.ServedStates), MemberType = typeof(SurfaceWorld))]
    public async Task The_files_of_an_earlier_run_are_opened_when_the_project_keeps_nothing(SurfaceState state)
    {
        Rig r = new();
        r.W.In(state);
        await r.Seed(r.EarlierAnalysis, r.W.EarlierResult);
        r.Opened.Should().Contain(r.EarlierAnalysis.ArtifactStorageKey!).And.Contain(r.W.EarlierResult.TimelineStorageKey!);
    }

    [Fact]
    public async Task No_file_is_opened_when_the_marker_cannot_be_read()
    {
        Rig r = new();
        r.W.In(SurfaceState.Clear);
        using MarkerReadFailsContext failing = r.W.MarkerFailingDb();
        await r.Seed(r.LaterAnalysis, r.W.LaterResult, failing);
        r.Opened.Should().NotContain(r.LaterAnalysis.ArtifactStorageKey!).And.NotContain(r.W.LaterResult.TimelineStorageKey!);
    }
}
