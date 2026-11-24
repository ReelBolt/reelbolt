#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Outputs;

/// <summary>Timelines: seeding one from a step result and the render list of a timeline for an output the project keeps back.</summary>
public sealed class EditTimelinesSurfaceTests
{
    private sealed class Rig
    {
        public SurfaceWorld W { get; } = new();
        public EditTimelineEntity Timeline { get; }
        public WorkflowExecution EarlierRender { get; }
        public WorkflowExecution LaterRender { get; }
        public WorkflowStepResult EarlierResult { get; }
        public WorkflowStepResult LaterResult { get; }

        public Rig()
        {
            Timeline = new EditTimelineEntity
            {
                Id = Guid.NewGuid(),
                ProjectId = W.Project.Id,
                Name = "Cut",
                DocumentJson = "{}",
                Revision = 1,
                CreatedAt = SurfaceWorld.Marker.AddDays(-9),
                UpdatedAt = SurfaceWorld.Marker.AddDays(-9),
            };
            WorkflowDefinition render = new()
            {
                Id = Guid.NewGuid(),
                ProjectId = W.Project.Id,
                Name = "Render",
                Kind = WorkflowDefinitionKind.TimelineRender,
                CreatedAt = SurfaceWorld.Marker.AddDays(-9),
                UpdatedAt = SurfaceWorld.Marker.AddDays(-9),
            };
            JsonSerializerOptions json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
            WorkflowStep step = new()
            {
                Id = Guid.NewGuid(),
                WorkflowDefinitionId = render.Id,
                AgentDefinitionId = W.Agent.Id,
                StepOrder = 1,
                StepType = StepType.TimelineRender,
                Label = "Render",
                TimelineRenderConfigJson = JsonSerializer.Serialize(new TimelineRenderStepConfig(Timeline.Id), json),
            };
            EarlierRender = Execution(render, SurfaceWorld.Marker.AddHours(-3));
            LaterRender = Execution(render, SurfaceWorld.Marker.AddDays(2));
            EarlierResult = Result(EarlierRender, step, "render-earlier", SurfaceWorld.Marker.AddHours(-2));
            LaterResult = Result(LaterRender, step, "render-later", SurfaceWorld.Marker.AddDays(2));
            W.Db.EditTimelines.Add(Timeline);
            W.Db.WorkflowDefinitions.Add(render);
            W.Db.WorkflowSteps.Add(step);
            W.Db.WorkflowExecutions.AddRange(EarlierRender, LaterRender);
            W.Db.WorkflowStepResults.AddRange(EarlierResult, LaterResult);
            W.Db.SaveChanges();
        }

        private WorkflowExecution Execution(WorkflowDefinition workflow, DateTime started) => new()
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = workflow.Id,
            ProjectId = W.Project.Id,
            Status = ExecutionStatus.Passed,
            StartedAt = started,
            FirstStartedAt = started,
            CompletedAt = started.AddMinutes(5),
        };

        private WorkflowStepResult Result(WorkflowExecution run, WorkflowStep step, string name, DateTime at) => new()
        {
            Id = Guid.NewGuid(),
            WorkflowExecutionId = run.Id,
            WorkflowStepId = step.Id,
            Status = StepStatus.Completed,
            OutputStorageKey = $"projects/{W.Project.Id}/outputFiles/{name}.mp4",
            OutputJson = "{\"outputDurationSec\":4.5}",
            ExecutedAt = at,
            CompletedAt = at,
        };

        public EditTimelinesController Controller(InferenceApiDbContext? db = null)
        {
            InferenceApiDbContext d = db ?? W.Db;
            return new EditTimelinesController(d, W.User, TestTenancy.Access(d, W.User), Mock.Of<IFileStorageService>(), Mock.Of<IWorkflowExecutionService>());
        }
    }

    private static bool Refused(IActionResult? r) =>
        r is ObjectResult { StatusCode: 404, Value: { } v } && v.ToString()!.Contains("OUTPUT_UNAVAILABLE");

    // ---- seed a timeline from a step result (389) ----------------------------------------------

    [Fact]
    public async Task A_timeline_cannot_be_seeded_from_an_earlier_result()
    {
        Rig r = new();
        r.W.In(SurfaceState.Held);
        Refused((await r.Controller().CreateEditTimeline(r.W.Project.Id, new CreateEditTimelineRequest("Seeded", r.W.EarlierResult.Id), CancellationToken.None)).Result).Should().BeTrue();
    }

    [Fact]
    public async Task A_timeline_can_be_seeded_from_a_later_result_while_earlier_outputs_are_kept()
    {
        Rig r = new();
        r.W.In(SurfaceState.Held);
        Refused((await r.Controller().CreateEditTimeline(r.W.Project.Id, new CreateEditTimelineRequest("Seeded", r.W.LaterResult.Id), CancellationToken.None)).Result).Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(SurfaceWorld.ServedStates), MemberType = typeof(SurfaceWorld))]
    public async Task A_timeline_can_be_seeded_from_the_earlier_result_when_the_project_keeps_nothing(SurfaceState state)
    {
        Rig r = new();
        r.W.In(state);
        Refused((await r.Controller().CreateEditTimeline(r.W.Project.Id, new CreateEditTimelineRequest("Seeded", r.W.EarlierResult.Id), CancellationToken.None)).Result).Should().BeFalse();
    }

    [Fact]
    public async Task A_timeline_cannot_be_seeded_when_the_marker_cannot_be_read()
    {
        Rig r = new();
        r.W.In(SurfaceState.Clear);
        using MarkerReadFailsContext failing = r.W.MarkerFailingDb();
        Refused((await r.Controller(failing).CreateEditTimeline(r.W.Project.Id, new CreateEditTimelineRequest("Seeded", r.W.LaterResult.Id), CancellationToken.None)).Result).Should().BeTrue();
    }

    // ---- render list (2150) --------------------------------------------------------------------

    private static async Task<Dictionary<Guid, EditTimelineRenderResponse>> Renders(Rig r, InferenceApiDbContext? db = null)
    {
        ActionResult<List<EditTimelineRenderResponse>> result = await r.Controller(db).ListRenders(r.W.Project.Id, r.Timeline.Id, CancellationToken.None);
        return ((List<EditTimelineRenderResponse>)result.Result.Should().BeOfType<OkObjectResult>().Subject.Value!).ToDictionary(x => x.ExecutionId);
    }

    [Fact]
    public async Task The_render_list_shows_an_earlier_render_without_its_result()
    {
        Rig r = new();
        r.W.In(SurfaceState.Held);
        Dictionary<Guid, EditTimelineRenderResponse> renders = await Renders(r);
        renders[r.EarlierRender.Id].StepResultId.Should().BeNull();
        renders[r.EarlierRender.Id].HasOutput.Should().BeFalse();
        renders[r.LaterRender.Id].StepResultId.Should().Be(r.LaterResult.Id);
        renders[r.LaterRender.Id].HasOutput.Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(SurfaceWorld.ServedStates), MemberType = typeof(SurfaceWorld))]
    public async Task The_render_list_shows_every_result_when_the_project_keeps_nothing(SurfaceState state)
    {
        Rig r = new();
        r.W.In(state);
        Dictionary<Guid, EditTimelineRenderResponse> renders = await Renders(r);
        renders[r.EarlierRender.Id].HasOutput.Should().BeTrue();
        renders[r.LaterRender.Id].HasOutput.Should().BeTrue();
    }

    [Fact]
    public async Task The_render_list_on_a_failed_read_shows_no_result()
    {
        Rig r = new();
        r.W.In(SurfaceState.Clear);
        using MarkerReadFailsContext failing = r.W.MarkerFailingDb();
        (await Renders(r, failing)).Values.Should().OnlyContain(x => x.StepResultId == null && !x.HasOutput);
    }
}
