#nullable enable
using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Services.ObjectAnalysis;
using ReelBolt.Inference.Api.Services.Styles;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Tests.EditTimelines;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.ObjectAnalysis;

/// <summary>
/// An intent attached to a search that is already in flight (QA T-063): another timeline attaches as its own row on the same run;
/// an identical attach for the same timeline is answered with the first (plain message, no error, one row), and the finalizer
/// applies the first row's intent.
/// </summary>
public class ObjectEffectInFlightReapplyReproTests
{
    private static (ObjectAnalysisService Service, ObjectAnalysisCaller Caller, string Media) Rig(ServerEditFixture f, Project project)
    {
        Mock<IWorkflowExecutionService> executions = new();
        executions.Setup(e => e.QueueAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string?>(),
                It.IsAny<ExecutionInitiator>(), It.IsAny<CancellationToken>(), It.IsAny<bool>(), It.IsAny<RunChoices?>(),
                It.IsAny<WorkflowDefinitionKind>(), It.IsAny<Guid?>(), It.IsAny<Func<CancellationToken, Task>?>()))
            .ReturnsAsync((Guid _, Guid workflowId, Guid _, string? _, ExecutionInitiator _, CancellationToken _, bool _, RunChoices? _,
                WorkflowDefinitionKind _, Guid? executionId, Func<CancellationToken, Task>? _) =>
                new WorkflowExecutionResponse(executionId ?? Guid.NewGuid(), workflowId, "Queued", null, null, 0, null, null, null, [], []));
        return (new ObjectAnalysisService(f.Db, executions.Object, f.Storage.Object, NullLogger<ObjectAnalysisService>.Instance, TimeProvider.System, null, null),
            new ObjectAnalysisCaller(f.OwnerId, project.OrganizationId, project, true), "file:" + f.FileId.ToString("D"));
    }

    private static Task<ObjectAnalysisResult<ObjectAnalysisQueuedResponse>> Ask(
        ObjectAnalysisService service, ObjectAnalysisCaller caller, string media, string effect, ObjectAnalysisIntent intent) =>
        service.RequestAsync(caller, media, new ObjectAnalysisRequestBody("the license plate", null, effect, 0.7, false), intent, default);

    [Fact]
    public async Task A_request_is_refused_with_409_while_a_restore_holds_the_project_lock()
    {
        using ServerEditFixture f = await ServerEditFixture.CreateAsync(clips: 2);
        Project project = await f.Db.Projects.SingleAsync(p => p.Id == f.ProjectId);
        (ObjectAnalysisService service, ObjectAnalysisCaller caller, string media) = Rig(f, project);

        using IDisposable held = RestoreRunGate.HoldForTests(f.ProjectId);
        ObjectAnalysisResult<ObjectAnalysisQueuedResponse> result = await Ask(service, caller, media, "Blur", new ObjectAnalysisIntent(f.TimelineId, f.ClipIds));

        result.Value.Should().BeNull();
        result.Refusal!.StatusCode.Should().Be(409);
        JsonSerializer.Serialize(result.Refusal.Body).Should().Contain("RESTORE_IN_PROGRESS");
    }

    [Fact]
    public async Task A_second_request_for_the_same_timeline_while_the_search_runs_gets_the_plain_message_not_an_error()
    {
        using ServerEditFixture f = await ServerEditFixture.CreateAsync(clips: 2);
        Project project = await f.Db.Projects.SingleAsync(p => p.Id == f.ProjectId);
        (ObjectAnalysisService service, ObjectAnalysisCaller caller, string media) = Rig(f, project);
        ObjectAnalysisIntent intent = new(f.TimelineId, f.ClipIds);

        ObjectAnalysisResult<ObjectAnalysisQueuedResponse> first = await Ask(service, caller, media, "Blur", intent);
        await f.AddRunAsync(first.Value!.ExecutionId, ExecutionStatus.Running);

        ObjectAnalysisResult<ObjectAnalysisQueuedResponse> answered = await Ask(service, caller, media, "Pixelate", new ObjectAnalysisIntent(f.TimelineId, [f.ClipIds[0]]));

        answered.Refusal.Should().BeNull();
        answered.SuccessStatus.Should().Be(202);
        (answered.Value!.RequestId, answered.Value.ExecutionId).Should().Be((first.Value.RequestId, first.Value.ExecutionId));
        answered.Value.Message.Should().Be("This is already being looked for in that timeline; the effect will appear when it finishes.");
        ObjectEffectRequest only = await f.Db.ObjectEffectRequests.AsNoTracking().SingleAsync();
        only.Effect.Should().Be("Blur", "the first row's intent is the one the finalizer applies");
    }

    [Fact]
    public async Task A_request_for_another_timeline_attaches_to_the_same_run_as_its_own_row()
    {
        using ServerEditFixture f = await ServerEditFixture.CreateAsync(clips: 2);
        Project project = await f.Db.Projects.SingleAsync(p => p.Id == f.ProjectId);
        (ObjectAnalysisService service, ObjectAnalysisCaller caller, string media) = Rig(f, project);

        ObjectAnalysisResult<ObjectAnalysisQueuedResponse> first = await Ask(service, caller, media, "Blur", new ObjectAnalysisIntent(f.TimelineId, f.ClipIds));
        await f.AddRunAsync(first.Value!.ExecutionId, ExecutionStatus.Running);
        Guid other = Guid.NewGuid();

        ObjectAnalysisResult<ObjectAnalysisQueuedResponse> second = await Ask(service, caller, media, "Blur", new ObjectAnalysisIntent(other, f.ClipIds));

        second.Refusal.Should().BeNull();
        second.Value!.Message.Should().BeNull();
        second.Value.RequestId.Should().NotBe(first.Value.RequestId);
        second.Value.ExecutionId.Should().Be(first.Value.ExecutionId);
        (await f.Db.ObjectEffectRequests.AsNoTracking().CountAsync()).Should().Be(2);
    }
}
