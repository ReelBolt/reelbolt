using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Shared.Workflows;

/// <summary>
/// Which <see cref="ReviewScore"/> rows are the run's current scores. A score is the verdict of one ReviewLoop
/// dispatch, and when a Retry or a resume's first miss supersedes that dispatch's step result, its score
/// must stop showing too. The engine records the producing row on the score (<see cref="ReviewScore.StepResultId"/>)
/// and that id decides when present. A score written before that column existed is linked by the order the engine
/// writes in: one dispatch at a time, it sets the ReviewLoop row's <c>CompletedAt</c> and then creates
/// the score (<c>WorkflowExecutorService</c>, same iteration, same clock). The score's producing row is therefore
/// the execution's result with the latest <c>CompletedAt</c> at or before the score's <c>CreatedAt</c>; the score
/// is current unless that row is superseded. A score with no such row is kept (nothing proves it was replaced).
/// Nothing is deleted: superseded scores stay for audit, as their step results do.
/// </summary>
public static class ReviewScoreLineage
{
    /// <summary>
    /// One completed step result of the run: when it completed, whether it was superseded, and its id (null when
    /// the caller did not load it; a score's recorded <see cref="ReviewScore.StepResultId"/> then cannot be matched).
    /// </summary>
    public readonly record struct Completion(DateTime CompletedAt, bool Superseded, Guid? Id = null);

    /// <summary>The completions of <paramref name="results"/> (every row of the run, superseded ones included).</summary>
    public static List<Completion> CompletionsOf(IEnumerable<WorkflowStepResult> results) =>
        [.. results.Where(r => r.CompletedAt is not null).Select(r => new Completion(r.CompletedAt!.Value, r.SupersededAt is not null, r.Id))];

    /// <summary>
    /// The scores in <paramref name="scores"/> whose producing step result is not superseded. With
    /// <paramref name="stepResultId"/>, a score that records its producer (<see cref="ReviewScore.StepResultId"/>)
    /// is resolved by that id alone; only a score without one (written before the column existed) uses the
    /// timestamp link.
    /// </summary>
    public static IEnumerable<T> Current<T>(
        IEnumerable<T> scores, Func<T, DateTime> createdAt, IEnumerable<WorkflowStepResult> results, Func<T, Guid?>? stepResultId = null) =>
        Current(scores, createdAt, CompletionsOf(results), stepResultId);

    /// <inheritdoc cref="Current{T}(IEnumerable{T}, Func{T, DateTime}, IEnumerable{WorkflowStepResult}, Func{T, Guid?})"/>
    public static IEnumerable<T> Current<T>(
        IEnumerable<T> scores, Func<T, DateTime> createdAt, IEnumerable<Completion> completions, Func<T, Guid?>? stepResultId = null)
    {
        List<Completion> ordered = [.. completions.OrderBy(c => c.CompletedAt)];
        if (!ordered.Any(c => c.Superseded))
            return scores;

        Dictionary<Guid, bool> supersededById = ordered
            .Where(c => c.Id is not null)
            .GroupBy(c => c.Id!.Value)
            .ToDictionary(g => g.Key, g => g.Any(c => c.Superseded));

        return scores.Where(score => stepResultId?.Invoke(score) is { } producerId
            // A recorded producer decides alone. One that is not among the run's rows keeps the score:
            // nothing proves it was replaced (the FK sets the id null when the row itself is deleted).
            ? !supersededById.GetValueOrDefault(producerId)
            : !ProducedBySuperseded(createdAt(score), ordered));
    }

    private static bool ProducedBySuperseded(DateTime scoreCreatedAt, List<Completion> ordered)
    {
        Completion? producer = null;
        foreach (Completion completion in ordered)
        {
            if (completion.CompletedAt > scoreCreatedAt)
                break;
            producer = completion;
        }

        return producer is { Superseded: true };
    }
}
