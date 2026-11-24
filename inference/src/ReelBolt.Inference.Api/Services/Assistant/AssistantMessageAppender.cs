using Microsoft.EntityFrameworkCore;
using Npgsql;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Services.Assistant;

/// <summary>
/// The one writer of <c>assistant_messages</c> rows. The next 1-based <c>Sequence</c> of a thread is read as
/// max + 1, which two writers of the same thread (a second send, a run-wake turn, a reply landing while another turn
/// appends) can both compute; the unique index on <c>(thread_id, sequence)</c> lets exactly one win. The loser
/// re-reads the maximum and tries again, so no turn is dropped and no sequence is reused. A save that still fails
/// after the attempts is thrown, never swallowed.
/// </summary>
public static class AssistantMessageAppender
{
    /// <summary>Attempts before a persistent conflict is surfaced to the caller.</summary>
    public const int MaxAttempts = 8;

    public static async Task<AssistantMessage> AppendAsync(
        InferenceApiDbContext db,
        AssistantThread thread,
        string role,
        string content,
        CancellationToken ct,
        string? metadataJson = null)
    {
        for (int attempt = 1; ; attempt++)
        {
            int lastSequence = await db.AssistantMessages
                .Where(m => m.ThreadId == thread.Id)
                .Select(m => (int?)m.Sequence)
                .MaxAsync(ct) ?? 0;

            DateTime now = DateTime.UtcNow;
            var turn = new AssistantMessage
            {
                Id = Guid.NewGuid(),
                ThreadId = thread.Id,
                Role = role,
                Content = content,
                Sequence = lastSequence + 1,
                MetadataJson = metadataJson,
                CreatedAt = now
            };

            db.AssistantMessages.Add(turn);
            thread.UpdatedAt = now;
            try
            {
                await db.SaveChangesAsync(ct);
                return turn;
            }
            catch (DbUpdateException ex) when (IsSequenceConflict(ex) && attempt < MaxAttempts)
            {
                // Another writer took this sequence first: forget the row that lost and read the new maximum.
                db.Entry(turn).State = EntityState.Detached;
                await Task.Delay(Random.Shared.Next(5, 25 * attempt), CancellationToken.None);
            }
        }
    }

    internal static bool IsSequenceConflict(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation }
        || (ex.InnerException?.GetType().Name == "SqliteException"
            && ex.InnerException.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase));
}
