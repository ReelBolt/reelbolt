using System;
using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Workflows;

/// <summary>
/// <see cref="ReviewScoreLineage"/>: a score that records the step result that produced it
/// (<c>review_scores.step_result_id</c>, engine migration) is current exactly when that row is not
/// superseded, whatever the clocks say. Only a legacy score with no id falls back to the timestamp link.
/// </summary>
public class ReviewScoreLineageTests
{
    private static readonly DateTime T0 = new(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);

    private static WorkflowStepResult Result(int seconds, bool superseded) => new()
    {
        Id = Guid.NewGuid(),
        WorkflowStepId = Guid.NewGuid(),
        Status = StepStatus.Completed,
        ExecutedAt = T0.AddSeconds(seconds - 1),
        CompletedAt = T0.AddSeconds(seconds),
        SupersededAt = superseded ? T0.AddHours(1) : null
    };

    private static ReviewScore Score(int seconds, Guid? stepResultId, int iteration = 1) => new()
    {
        Id = Guid.NewGuid(),
        IterationNumber = iteration,
        Score = 5,
        CreatedAt = T0.AddSeconds(seconds),
        StepResultId = stepResultId
    };

    private static List<ReviewScore> Current(IEnumerable<ReviewScore> scores, IEnumerable<WorkflowStepResult> results) =>
        [.. ReviewScoreLineage.Current(scores, s => s.CreatedAt, results, s => s.StepResultId)];

    [Fact]
    public void A_score_whose_recorded_step_result_is_superseded_is_hidden_even_when_the_clock_points_elsewhere()
    {
        WorkflowStepResult replaced = Result(10, superseded: true);
        // A newer, current row completed before the score was written (clock skew, or a slow score write):
        // the timestamp link alone would credit the score to it.
        WorkflowStepResult current = Result(20, superseded: false);
        ReviewScore score = Score(30, replaced.Id);

        Current([score], [replaced, current]).Should().BeEmpty();
    }

    [Fact]
    public void A_score_whose_recorded_step_result_is_current_is_kept_even_when_the_clock_points_at_a_superseded_row()
    {
        WorkflowStepResult producer = Result(10, superseded: false);
        WorkflowStepResult replacedLater = Result(20, superseded: true);
        ReviewScore score = Score(30, producer.Id);

        Current([score], [producer, replacedLater]).Should().Equal([score]);
    }

    [Fact]
    public void A_legacy_score_with_no_step_result_id_still_resolves_by_timestamp()
    {
        WorkflowStepResult replaced = Result(10, superseded: true);
        WorkflowStepResult current = Result(40, superseded: false);
        ReviewScore old = Score(11, stepResultId: null, iteration: 1);
        ReviewScore fresh = Score(41, stepResultId: null, iteration: 1);

        Current([old, fresh], [replaced, current]).Should().Equal([fresh]);
    }

    [Fact]
    public void A_recorded_step_result_that_is_gone_keeps_the_score_because_nothing_proves_it_was_replaced()
    {
        WorkflowStepResult replaced = Result(10, superseded: true);
        ReviewScore score = Score(11, Guid.NewGuid());

        Current([score], [replaced]).Should().Equal([score]);
    }
}
