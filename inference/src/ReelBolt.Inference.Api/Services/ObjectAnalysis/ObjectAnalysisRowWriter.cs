using Microsoft.EntityFrameworkCore;
using Npgsql;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Services.ObjectAnalysis;

/// <summary>Small guarded writes on <see cref="ObjectEffectRequest"/> rows.</summary>
public static class ObjectAnalysisRowWriter
{
    /// <summary>
    /// Moves a row off <c>queued</c> and only off <c>queued</c>: a conditional UPDATE (<c>WHERE status = 'queued'</c>), so a read that
    /// raced a later finalizer cannot move an <c>applied</c> row back to <c>succeeded</c>. The in-memory test provider cannot run
    /// ExecuteUpdate, so it falls back to a tracked write guarded by the row's last-read status. Returns whether this call moved it.
    /// </summary>
    public static async Task<bool> TryFinalizeAsync(
        InferenceApiDbContext db, ObjectEffectRequest row, string status, string? failureCode, string? failureMessage, DateTime now, CancellationToken ct)
    {
        if (row.Status != ObjectEffectRequest.StatusQueued) return false;

        if (db.Database.IsRelational())
        {
            int moved = await db.ObjectEffectRequests
                .Where(r => r.Id == row.Id && r.Status == ObjectEffectRequest.StatusQueued)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.Status, status)
                    .SetProperty(r => r.FailureCode, failureCode)
                    .SetProperty(r => r.FailureMessage, failureMessage)
                    .SetProperty(r => r.FinalizedAt, now), ct);
            if (moved == 0)
            {
                await db.Entry(row).ReloadAsync(ct);
                return false;
            }

            await db.Entry(row).ReloadAsync(ct);
            return true;
        }

        row.Status = status;
        row.FailureCode = failureCode;
        row.FailureMessage = failureMessage;
        row.FinalizedAt = now;
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>True when <paramref name="ex"/> is a unique-index violation (Postgres 23505, SQLite constraint 19).</summary>
    public static bool IsUniqueViolation(DbUpdateException ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is PostgresException { SqlState: "23505" }) return true;
            // SQLite (the test fixture) is not referenced here: match by type name and message.
            if (e.GetType().Name == "SqliteException" && e.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }
}
