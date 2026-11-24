namespace ReelBolt.Shared.Data.Models;

/// <summary>
/// A quality review score for a workflow execution iteration.
/// </summary>
public class ReviewScore
{
    public Guid Id { get; set; }
    public Guid WorkflowExecutionId { get; set; }
    public int IterationNumber { get; set; }
    public int Score { get; set; }
    public string Comments { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// The ReviewLoop step result whose dispatch produced this score, written by the engine with the score
    /// (engine migration <c>AddReviewScoreStepResult</c>; FK ON DELETE SET NULL). <c>ReviewScoreLineage</c>
    /// reads it to tell a current score from one a Retry or resume superseded. Null on every row written
    /// before the column existed; those still resolve by the timestamp link.
    /// </summary>
    public Guid? StepResultId { get; set; }

    public WorkflowExecution WorkflowExecution { get; set; } = null!;
}
