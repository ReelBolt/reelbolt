#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Data.Models;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Workflows;

/// <summary>
/// The API's approximation of where a resume continues (T-004): the steps it prices are those whose StepOrder is
/// at or after the first step without a usable result. Storage HEADs are not checked here (the engine does that).
/// </summary>
public sealed class ResumeEstimateFrontierTests
{
    private static readonly DateTime T0 = new(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);

    private static List<WorkflowStep> Steps(int count) =>
        [.. Enumerable.Range(1, count).Select(i => new WorkflowStep { Id = Guid.NewGuid(), StepOrder = i, StepType = StepType.Agent })];

    private static WorkflowStepResult Row(WorkflowStep step, StepStatus status, int minute, DateTime? supersededAt = null) => new()
    {
        Id = Guid.NewGuid(),
        WorkflowStepId = step.Id,
        Status = status,
        ExecutedAt = T0.AddMinutes(minute),
        SupersededAt = supersededAt
    };

    [Fact]
    public void Charges_from_the_first_failed_step_on()
    {
        List<WorkflowStep> steps = Steps(4);

        IReadOnlyCollection<int> charged = ResumeEstimateFrontier.ChargedStepOrders(steps,
            [Row(steps[0], StepStatus.Completed, 0), Row(steps[1], StepStatus.Completed, 1), Row(steps[2], StepStatus.Failed, 2)]);

        charged.Should().BeEquivalentTo([3, 4]);
    }

    [Fact]
    public void A_run_lost_after_its_last_completed_step_charges_the_steps_after_it()
    {
        List<WorkflowStep> steps = Steps(3);

        ResumeEstimateFrontier.ChargedStepOrders(steps, [Row(steps[0], StepStatus.Completed, 0)])
            .Should().BeEquivalentTo([2, 3]);
    }

    [Fact]
    public void A_run_with_no_results_charges_every_step()
    {
        List<WorkflowStep> steps = Steps(2);

        ResumeEstimateFrontier.ChargedStepOrders(steps, []).Should().BeEquivalentTo([1, 2]);
    }

    [Fact]
    public void A_failed_attempt_followed_by_a_completed_rerun_of_the_same_step_counts_as_completed()
    {
        List<WorkflowStep> steps = Steps(3);

        ResumeEstimateFrontier.ChargedStepOrders(steps,
            [Row(steps[0], StepStatus.Failed, 0), Row(steps[0], StepStatus.Completed, 1), Row(steps[1], StepStatus.Running, 2)])
            .Should().BeEquivalentTo([2, 3]);
    }

    [Fact]
    public void Superseded_rows_are_ignored()
    {
        List<WorkflowStep> steps = Steps(2);

        ResumeEstimateFrontier.ChargedStepOrders(steps, [Row(steps[0], StepStatus.Completed, 0, supersededAt: T0)])
            .Should().BeEquivalentTo([1, 2]);
    }

    [Fact]
    public void A_row_of_a_step_the_snapshot_does_not_have_charges_every_step()
    {
        List<WorkflowStep> steps = Steps(2);
        WorkflowStep foreign = new() { Id = Guid.NewGuid(), StepOrder = 9 };

        ResumeEstimateFrontier.ChargedStepOrders(steps, [Row(steps[0], StepStatus.Completed, 0), Row(foreign, StepStatus.Completed, 1)])
            .Should().BeEquivalentTo([1, 2]);
    }
}
