#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Outputs;

/// <summary>Bins: a pinned item or a smart-bin match whose bytes are not served resolves like a missing one.</summary>
public sealed class MediaBinsSurfaceTests
{
    private sealed class Rig
    {
        public SurfaceWorld W { get; } = new();
        public MediaBin Manual { get; }
        public MediaBin Smart { get; }
        public WorkflowStepResult PoolEarlier { get; }
        public WorkflowStepResult PoolLater { get; }

        public Rig()
        {
            // Two renders no file row names, so the smart bin lists them from the step result alone.
            PoolEarlier = PoolResult(W.EarlierRun, "pool-earlier.mp4", SurfaceWorld.Marker.AddHours(-2));
            PoolLater = PoolResult(W.LaterRun, "pool-later.mp4", SurfaceWorld.Marker.AddDays(2));
            W.Db.WorkflowStepResults.AddRange(PoolEarlier, PoolLater);
            Manual = new MediaBin { Id = Guid.NewGuid(), ProjectId = W.Project.Id, Name = "Pinned" };
            Smart = new MediaBin { Id = Guid.NewGuid(), ProjectId = W.Project.Id, Name = "Everything", SmartFilterJson = "{}" };
            W.Db.MediaBins.AddRange(Manual, Smart);
            int order = 0;
            Pin(MediaSourceKind.ProjectFile, W.EarlierFile.Id, order++);
            Pin(MediaSourceKind.ProjectFile, W.LaterFile.Id, order++);
            Pin(MediaSourceKind.ProjectFile, W.Upload.Id, order++);
            Pin(MediaSourceKind.StepResult, W.EarlierResult.Id, order++);
            Pin(MediaSourceKind.StepResult, W.LaterResult.Id, order++);
            W.Db.SaveChanges();
        }

        private WorkflowStepResult PoolResult(WorkflowExecution run, string name, DateTime at) => new()
        {
            Id = Guid.NewGuid(),
            WorkflowExecutionId = run.Id,
            WorkflowStepId = W.Step.Id,
            Status = StepStatus.Completed,
            OutputStorageKey = $"projects/{W.Project.Id}/outputFiles/{name}",
            ExecutedAt = at,
            CompletedAt = at,
        };

        private void Pin(MediaSourceKind kind, Guid id, int order) => W.Db.MediaBinItems.Add(new MediaBinItem
        {
            Id = Guid.NewGuid(),
            BinId = Manual.Id,
            SourceKind = kind,
            SortOrder = order,
            SourceProjectFileId = kind == MediaSourceKind.ProjectFile ? id : null,
            SourceStepResultId = kind == MediaSourceKind.StepResult ? id : null,
        });

        public string RefOf(string kind, Guid id) => $"{kind}:{id:D}";

        public async Task<List<MediaBinItemDetailResponse>> Items(MediaBin bin, InferenceApiDbContext? db = null)
        {
            InferenceApiDbContext d = db ?? W.Db;
            ActionResult<List<MediaBinItemDetailResponse>> r = await new MediaBinsController(d, W.Access()).ListItems(W.Project.Id, bin.Id, CancellationToken.None);
            return (List<MediaBinItemDetailResponse>)r.Result.Should().BeOfType<OkObjectResult>().Subject.Value!;
        }
    }

    private static HashSet<string> Resolved(IEnumerable<MediaBinItemDetailResponse> items) =>
        items.Where(i => i.FileName is not null).Select(i => i.MediaRef).ToHashSet();

    [Fact]
    public async Task A_pinned_earlier_output_resolves_like_a_missing_one()
    {
        Rig r = new();
        r.W.In(SurfaceState.Held);
        HashSet<string> resolved = Resolved(await r.Items(r.Manual));
        resolved.Should().BeEquivalentTo([r.RefOf("file", r.W.LaterFile.Id), r.RefOf("file", r.W.Upload.Id), r.RefOf("step_result", r.W.LaterResult.Id)]);
    }

    [Theory]
    [MemberData(nameof(SurfaceWorld.ServedStates), MemberType = typeof(SurfaceWorld))]
    public async Task Every_pin_resolves_when_the_project_keeps_nothing(SurfaceState state)
    {
        Rig r = new();
        r.W.In(state);
        Resolved(await r.Items(r.Manual)).Should().HaveCount(5);
    }

    [Fact]
    public async Task A_pin_on_a_failed_read_resolves_like_a_missing_one_unless_it_is_an_upload()
    {
        Rig r = new();
        r.W.In(SurfaceState.Clear);
        using MarkerReadFailsContext failing = r.W.MarkerFailingDb();
        Resolved(await r.Items(r.Manual, failing)).Should().BeEquivalentTo([r.RefOf("file", r.W.Upload.Id)]);
    }

    [Fact]
    public async Task A_smart_bin_leaves_out_earlier_outputs()
    {
        Rig r = new();
        r.W.In(SurfaceState.Held);
        List<string> refs = (await r.Items(r.Smart)).Select(i => i.MediaRef).ToList();
        refs.Should().Contain(r.RefOf("file", r.W.LaterFile.Id)).And.Contain(r.RefOf("file", r.W.Upload.Id))
            .And.NotContain(r.RefOf("file", r.W.EarlierFile.Id)).And.NotContain(r.RefOf("step_result", r.PoolEarlier.Id))
            .And.Contain(r.RefOf("step_result", r.PoolLater.Id));
    }

    [Theory]
    [MemberData(nameof(SurfaceWorld.ServedStates), MemberType = typeof(SurfaceWorld))]
    public async Task A_smart_bin_lists_earlier_outputs_when_the_project_keeps_nothing(SurfaceState state)
    {
        Rig r = new();
        r.W.In(state);
        List<string> refs = (await r.Items(r.Smart)).Select(i => i.MediaRef).ToList();
        refs.Should().Contain(r.RefOf("file", r.W.EarlierFile.Id)).And.Contain(r.RefOf("step_result", r.PoolEarlier.Id));
    }

    [Fact]
    public async Task A_smart_bin_on_a_failed_read_leaves_out_output_files()
    {
        Rig r = new();
        r.W.In(SurfaceState.Clear);
        using MarkerReadFailsContext failing = r.W.MarkerFailingDb();
        List<string> refs = (await r.Items(r.Smart, failing)).Select(i => i.MediaRef).ToList();
        refs.Should().NotContain(r.RefOf("file", r.W.LaterFile.Id)).And.Contain(r.RefOf("file", r.W.Upload.Id));
    }
}
