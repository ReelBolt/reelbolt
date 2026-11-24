using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Services.PublicApi;

public enum IdempotencyBeginStatus
{
    /// <summary>No record: this request owns the key and must run (and store its response).</summary>
    Proceed,

    /// <summary>A finished record with the same request hash: replay its status and body.</summary>
    Replay,

    /// <summary>The record's request hash differs: <c>422 idempotency_key_reused</c>.</summary>
    Reused,

    /// <summary>The first request is still running: <c>409 idempotency_in_progress</c>.</summary>
    InProgress,

    /// <summary>Finished, but its body was too large to store: <c>409 idempotency_response_unavailable</c>.</summary>
    ResponseUnavailable
}

/// <summary>The verdict of claiming an <c>Idempotency-Key</c>.</summary>
public sealed record IdempotencyBegin(IdempotencyBeginStatus Status, int ResponseStatus = 0, string? ResponseBody = null);

/// <summary>The per-key idempotency records. Keys are scoped by the API key id.</summary>
public interface IIdempotencyStore
{
    /// <summary>Claims the key or reports the stored outcome. A collision never reads another key's rows.</summary>
    Task<IdempotencyBegin> BeginAsync(Guid apiKeyId, string key, string requestHash, DateTime now, CancellationToken ct);

    /// <summary>Stores a finished 2xx/4xx response (or marks it Done with no body when it is over 64 KB).</summary>
    Task CompleteAsync(Guid apiKeyId, string key, int status, ReadOnlyMemory<byte> body, DateTime now, CancellationToken ct);

    /// <summary>
    /// Marks the key Done with NO body: a replay answers <c>409 idempotency_response_unavailable</c> instead of
    /// re-running. Used when the action threw after its write had been committed (a second run must not start) and
    /// for a stored response that must not be persisted (a secret, a body over the bound).
    /// </summary>
    Task CompleteWithoutBodyAsync(Guid apiKeyId, string key, int status, DateTime now, CancellationToken ct);

    /// <summary>Deletes the record so a failed (5xx/exception) request can be retried.</summary>
    Task AbandonAsync(Guid apiKeyId, string key, CancellationToken ct);
}

/// <inheritdoc />
public sealed class IdempotencyStore(InferenceApiDbContext db, ILogger<IdempotencyStore> logger) : IIdempotencyStore
{
    /// <summary>The largest stored response body; larger responses are replayed as <c>409</c>, not truncated.</summary>
    public const int MaxResponseBodyBytes = 64 * 1024;

    public async Task<IdempotencyBegin> BeginAsync(
        Guid apiKeyId, string key, string requestHash, DateTime now, CancellationToken ct)
    {
        ApiIdempotencyRecord record = new()
        {
            ApiKeyId = apiKeyId,
            Key = key,
            RequestHash = requestHash,
            Status = ApiIdempotencyRecord.InProgress,
            CreatedAt = now
        };
        db.ApiIdempotencyRecords.Add(record);
        try
        {
            await db.SaveChangesAsync(ct);
            return new IdempotencyBegin(IdempotencyBeginStatus.Proceed);
        }
        catch (DbUpdateException)
        {
            // The composite primary key (api_key_id, key) is the arbitration. Detach the loser and read the
            // winner - scoped to THIS key id, so a key can never observe another key's record.
            db.Entry(record).State = EntityState.Detached;
            ApiIdempotencyRecord? existing = await db.ApiIdempotencyRecords.AsNoTracking()
                .FirstOrDefaultAsync(r => r.ApiKeyId == apiKeyId && r.Key == key, ct);
            if (existing is null)
            {
                logger.LogWarning("Idempotency insert conflicted but no record was found");
                throw;
            }

            return Evaluate(existing, requestHash);
        }
    }

    private static IdempotencyBegin Evaluate(ApiIdempotencyRecord existing, string requestHash)
    {
        if (!string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal))
            return new IdempotencyBegin(IdempotencyBeginStatus.Reused);

        if (existing.Status == ApiIdempotencyRecord.InProgress)
            return new IdempotencyBegin(IdempotencyBeginStatus.InProgress);

        return existing.ResponseBody is null
            ? new IdempotencyBegin(IdempotencyBeginStatus.ResponseUnavailable)
            : new IdempotencyBegin(IdempotencyBeginStatus.Replay, existing.ResponseStatus ?? 200, existing.ResponseBody);
    }

    public async Task CompleteAsync(
        Guid apiKeyId, string key, int status, ReadOnlyMemory<byte> body, DateTime now, CancellationToken ct)
    {
        ApiIdempotencyRecord? record = await db.ApiIdempotencyRecords
            .FirstOrDefaultAsync(r => r.ApiKeyId == apiKeyId && r.Key == key, ct);
        if (record is null) return;

        record.Status = ApiIdempotencyRecord.Done;
        record.ResponseStatus = status;
        // Bounded: a bigger body is not stored (and is not truncated), so a replay answers 409 instead.
        string? text = body.Length <= MaxResponseBodyBytes ? System.Text.Encoding.UTF8.GetString(body.Span) : null;
        // Generic guard: a body carrying a webhook secret or an API key is never persisted. The record is Done with no
        // body, so a replay answers 409 idempotency_response_unavailable.
        if (text is not null && ContainsSecret(text)) text = null;
        record.ResponseBody = text;
        record.CreatedAt = now;
        await db.SaveChangesAsync(ct);
    }

    public async Task CompleteWithoutBodyAsync(
        Guid apiKeyId, string key, int status, DateTime now, CancellationToken ct)
    {
        ApiIdempotencyRecord? record = await db.ApiIdempotencyRecords
            .FirstOrDefaultAsync(r => r.ApiKeyId == apiKeyId && r.Key == key, ct);
        if (record is null) return;

        record.Status = ApiIdempotencyRecord.Done;
        record.ResponseStatus = status;
        record.ResponseBody = null;
        record.CreatedAt = now;
        await db.SaveChangesAsync(ct);
    }

    internal static bool ContainsSecret(string text) =>
        text.Contains("whsec_", StringComparison.Ordinal) || text.Contains("rbk_", StringComparison.Ordinal);

    public async Task AbandonAsync(Guid apiKeyId, string key, CancellationToken ct)
    {
        ApiIdempotencyRecord? record = await db.ApiIdempotencyRecords
            .FirstOrDefaultAsync(r => r.ApiKeyId == apiKeyId && r.Key == key, ct);
        if (record is null) return;
        db.ApiIdempotencyRecords.Remove(record);
        await db.SaveChangesAsync(ct);
    }
}
