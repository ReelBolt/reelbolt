#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using MassTransit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Data.Models;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Outputs;

/// <summary>Run details, run history and one step result: text, stored keys and the run's result are left out for an earlier output.</summary>
public sealed class WorkflowRunsSurfaceTests
{
    private static WorkflowsController NewController(SurfaceWorld w, InferenceApiDbContext? db = null)
    {
        InferenceApiDbContext d = db ?? w.Db;
        return new WorkflowsController(
            d, w.User, w.Access(), Mock.Of<IPublishEndpoint>(),
            new WorkflowTemplateProvisioningService(d, NullLogger<WorkflowTemplateProvisioningService>.Instance, new ConfigurationBuilder().Build()),
            new WorkflowExecutionService(d, Mock.Of<IPublishEndpoint>(), NullLogger<WorkflowExecutionService>.Instance),
            NullLogger<WorkflowsController>.Instance);
    }

    private static T Body<T>(ActionResult<T> r) => (T)r.Result.Should().BeOfType<OkObjectResult>().Subject.Value!;

    private static void ShouldBeOmitted(StepResultResponse r)
    {
        r.Output.Should().BeEmpty();
        r.OutputJson.Should().BeNull();
        r.InputJson.Should().BeNull();
        r.OutputStorageKey.Should().BeNull();
        r.ArtifactStorageKey.Should().BeNull();
    }

    private static void ShouldBeShown(StepResultResponse r)
    {
        r.Output.Should().Be("result text");
        r.OutputJson.Should().NotBeNull();
        r.OutputStorageKey.Should().NotBeNull();
        r.ArtifactStorageKey.Should().NotBeNull();
    }

    private static WorkflowExecution BareRun(SurfaceWorld w, DateTime started)
    {
        WorkflowExecution run = new()
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = w.Workflow.Id,
            ProjectId = w.Project.Id,
            Status = ExecutionStatus.Passed,
            StartedAt = started,
            FirstStartedAt = started,
            ResultJson = "{\"final\":true}",
        };
        w.Db.WorkflowExecutions.Add(run);
        w.Db.SaveChanges();
        return run;
    }

    [Fact]
    public async Task Run_details_leave_out_the_text_keys_and_result_of_an_earlier_run()
    {
        SurfaceWorld w = new SurfaceWorld().In(SurfaceState.Held);
        WorkflowExecutionResponse run = Body(await NewController(w).GetExecution(w.Project.Id, w.EarlierRun.Id, CancellationToken.None));
        run.ResultJson.Should().BeNull();
        ShouldBeOmitted(run.StepResults.Single());
    }

    [Fact]
    public async Task Run_details_of_a_later_run_are_shown_in_full()
    {
        SurfaceWorld w = new SurfaceWorld().In(SurfaceState.Held);
        WorkflowExecutionResponse run = Body(await NewController(w).GetExecution(w.Project.Id, w.LaterRun.Id, CancellationToken.None));
        run.ResultJson.Should().NotBeNull();
        ShouldBeShown(run.StepResults.Single());
    }

    [Theory]
    [MemberData(nameof(SurfaceWorld.ServedStates), MemberType = typeof(SurfaceWorld))]
    public async Task Run_details_of_an_earlier_run_are_shown_in_full_when_nothing_is_kept(SurfaceState state)
    {
        SurfaceWorld w = new SurfaceWorld().In(state);
        WorkflowExecutionResponse run = Body(await NewController(w).GetExecution(w.Project.Id, w.EarlierRun.Id, CancellationToken.None));
        run.ResultJson.Should().NotBeNull();
        ShouldBeShown(run.StepResults.Single());
    }

    [Fact]
    public async Task Run_details_on_a_failed_read_leave_everything_out()
    {
        SurfaceWorld w = new SurfaceWorld().In(SurfaceState.Clear);
        using ProjectReadFailsContext failing = w.FailingDb();
        WorkflowExecutionResponse run = Body(await NewController(w, failing).GetExecution(w.Project.Id, w.LaterRun.Id, CancellationToken.None));
        run.ResultJson.Should().BeNull();
        ShouldBeOmitted(run.StepResults.Single());
    }

    [Fact]
    public async Task The_result_of_an_earlier_run_with_no_step_results_is_left_out()
    {
        SurfaceWorld w = new SurfaceWorld().In(SurfaceState.Held);
        WorkflowExecution bare = BareRun(w, SurfaceWorld.Marker.AddHours(-4));
        Body(await NewController(w).GetExecution(w.Project.Id, bare.Id, CancellationToken.None)).ResultJson.Should().BeNull();
    }

    [Theory]
    [InlineData(SurfaceState.Held, true)]
    [InlineData(SurfaceState.Clear, false)]
    [InlineData(SurfaceState.ModerationOnly, false)]
    public async Task The_result_of_a_run_with_no_step_results_is_shown_unless_it_is_an_earlier_run_of_a_project_that_keeps_them(SurfaceState state, bool later)
    {
        SurfaceWorld w = new SurfaceWorld().In(state);
        WorkflowExecution bare = BareRun(w, later ? SurfaceWorld.Marker.AddDays(2) : SurfaceWorld.Marker.AddHours(-4));
        Body(await NewController(w).GetExecution(w.Project.Id, bare.Id, CancellationToken.None)).ResultJson.Should().NotBeNull();
    }

    [Fact]
    public async Task The_result_of_a_run_with_no_step_results_is_left_out_on_a_failed_read()
    {
        SurfaceWorld w = new SurfaceWorld().In(SurfaceState.Clear);
        WorkflowExecution bare = BareRun(w, SurfaceWorld.Marker.AddDays(2));
        using ProjectReadFailsContext failing = w.FailingDb();
        Body(await NewController(w, failing).GetExecution(w.Project.Id, bare.Id, CancellationToken.None)).ResultJson.Should().BeNull();
    }

    [Fact]
    public async Task One_step_result_of_an_earlier_run_is_left_out()
    {
        SurfaceWorld w = new SurfaceWorld().In(SurfaceState.Held);
        ShouldBeOmitted(Body(await NewController(w).GetStepResult(w.Project.Id, w.EarlierRun.Id, w.EarlierResult.Id, CancellationToken.None)));
    }

    [Fact]
    public async Task One_step_result_of_a_later_run_is_shown()
    {
        SurfaceWorld w = new SurfaceWorld().In(SurfaceState.Held);
        ShouldBeShown(Body(await NewController(w).GetStepResult(w.Project.Id, w.LaterRun.Id, w.LaterResult.Id, CancellationToken.None)));
    }

    [Theory]
    [MemberData(nameof(SurfaceWorld.ServedStates), MemberType = typeof(SurfaceWorld))]
    public async Task One_step_result_of_an_earlier_run_is_shown_when_nothing_is_kept(SurfaceState state)
    {
        SurfaceWorld w = new SurfaceWorld().In(state);
        ShouldBeShown(Body(await NewController(w).GetStepResult(w.Project.Id, w.EarlierRun.Id, w.EarlierResult.Id, CancellationToken.None)));
    }

    [Fact]
    public async Task One_step_result_on_a_failed_read_is_left_out()
    {
        SurfaceWorld w = new SurfaceWorld().In(SurfaceState.Clear);
        using ProjectReadFailsContext failing = w.FailingDb();
        ShouldBeOmitted(Body(await NewController(w, failing).GetStepResult(w.Project.Id, w.LaterRun.Id, w.LaterResult.Id, CancellationToken.None)));
    }

    private static Dictionary<Guid, StepResultResponse> History(ActionResult<List<WorkflowExecutionResponse>> r) =>
        Body(r).ToDictionary(e => e.Id, e => e.StepResults.Single());

    [Fact]
    public async Task Run_history_leaves_out_the_keys_of_an_earlier_run_and_keeps_those_of_a_later_one()
    {
        SurfaceWorld w = new SurfaceWorld().In(SurfaceState.Held);
        Dictionary<Guid, StepResultResponse> history = History(await NewController(w).ListExecutions(w.Project.Id, w.Workflow.Id, CancellationToken.None));
        history[w.EarlierRun.Id].OutputStorageKey.Should().BeNull();
        history[w.EarlierRun.Id].ArtifactStorageKey.Should().BeNull();
        history[w.LaterRun.Id].OutputStorageKey.Should().NotBeNull();
    }

    [Theory]
    [MemberData(nameof(SurfaceWorld.ServedStates), MemberType = typeof(SurfaceWorld))]
    public async Task Run_history_keeps_every_key_when_nothing_is_kept(SurfaceState state)
    {
        SurfaceWorld w = new SurfaceWorld().In(state);
        History(await NewController(w).ListExecutions(w.Project.Id, w.Workflow.Id, CancellationToken.None)).Values
            .Should().OnlyContain(s => s.OutputStorageKey != null);
    }

    [Fact]
    public async Task Run_history_on_a_failed_read_leaves_every_key_out()
    {
        SurfaceWorld w = new SurfaceWorld().In(SurfaceState.Clear);
        using ProjectReadFailsContext failing = w.FailingDb();
        History(await NewController(w, failing).ListExecutions(w.Project.Id, w.Workflow.Id, CancellationToken.None)).Values
            .Should().OnlyContain(s => s.OutputStorageKey == null);
    }
}
