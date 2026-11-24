using Microsoft.EntityFrameworkCore.Storage;
using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.ApiKeys;

namespace ReelBolt.Inference.Api.Services.PublicApi;

/// <summary>
/// One request's rate-limit verdict: the new totals, whether it is admitted, and the window end (which a
/// refusal turns into <c>Retry-After</c> and <c>error.resetsAt</c>).
/// </summary>
public sealed record ApiKeyRateLimitDecision(
    bool Allowed,
    bool Degraded,
    int RequestsLimit,
    int WritesLimit,
    int Requests,
    int Writes,
    DateTimeOffset WindowStart,
    string ViolatedLimit)
{
    public DateTimeOffset ResetsAt => WindowStart.AddMinutes(1);

    public int Remaining => Math.Max(0, RequestsLimit - Requests);

    /// <summary>Seconds to the window end, at least one (<c>Retry-After</c> / <c>RateLimit-Reset</c>).</summary>
    public int ResetSeconds => Math.Max(1, (int)Math.Ceiling((ResetsAt - DateTimeOffset.UtcNow).TotalSeconds));
}

/// <summary>The counter store the limiter increments. One statement per request, shared by every replica.</summary>
public interface IApiKeyRateCounterStore
{
    /// <summary>
    /// Adds this request to the key's window and returns the new (total, writes). A refused request still
    /// counts: the decision is made after the increment, so a key hammering a full window keeps it full.
    /// </summary>
    Task<(int Total, int Writes)> IncrementAsync(Guid apiKeyId, DateTime windowStart, bool isWrite, CancellationToken ct);

    /// <summary>Deletes counters older than the given window start (the hourly sweep).</summary>
    Task<int> DeleteOlderThanAsync(DateTime windowStart, CancellationToken ct);
}

/// <summary>
/// The per-key fixed one-minute rate limiter. The database failure mode is FAIL OPEN with a
/// warning and a metric: an outage of the limiter must not take the public API down - the key's credit caps
/// are the money brake, and a retry is cheap. A refusal is a 429 the caller sees with <c>Retry-After</c>.
/// </summary>
public sealed class ApiKeyRateLimiter(
    IApiKeyRateCounterStore store,
    IOptionsMonitor<PublicApiOptions> options,
    ILogger<ApiKeyRateLimiter> logger,
    TimeProvider? time = null)
{
    /// <summary>The aligned UTC minute <paramref name="now"/> falls in.</summary>
    public static DateTime WindowStart(DateTimeOffset now) =>
        DateTimeOffset.FromUnixTimeSeconds(now.ToUnixTimeSeconds() / 60 * 60).UtcDateTime;

    public async Task<ApiKeyRateLimitDecision> CheckAsync(Guid apiKeyId, bool isWrite, DateTimeOffset now, CancellationToken ct)
    {
        PublicApiOptions.RateLimitOptions limits = options.CurrentValue.RateLimit;
        int requestsLimit = Math.Max(1, limits.RequestsPerMinute);
        int writesLimit = Math.Max(1, limits.WritesPerMinute);
        DateTime windowStart = WindowStart(now);

        try
        {
            (int total, int writes) = await store.IncrementAsync(apiKeyId, windowStart, isWrite, ct);
            string violated = total > requestsLimit ? "requests"
                : isWrite && writes > writesLimit ? "writes"
                : string.Empty;
            if (violated.Length > 0)
                PublicApiDiagnostics.RateLimitRefused.Add(1, new KeyValuePair<string, object?>("limit", violated));
            return new ApiKeyRateLimitDecision(violated.Length == 0, false, requestsLimit, writesLimit, total, writes,
                new DateTimeOffset(windowStart, TimeSpan.Zero), violated);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Fail open (see the class comment). Never the exception details beyond its type/message shape -
            // this path touches no key material, but keep the log to the failure that matters.
            PublicApiDiagnostics.RateLimitFailOpen.Add(1);
            logger.LogWarning("Public API rate counter unavailable; admitting the request: {Error}", ex.GetType().Name);
            return new ApiKeyRateLimitDecision(true, true, requestsLimit, writesLimit, 0, 0,
                new DateTimeOffset(windowStart, TimeSpan.Zero), string.Empty);
        }
    }
}

/// <summary>
/// The counters on PostgreSQL/SQLite through the DbContext's own connection. The single upsert is the
/// whole read-modify-write, so concurrent replicas cannot lose an increment.
/// </summary>
public sealed class ApiKeyRateCounterStore(InferenceApiDbContext db) : IApiKeyRateCounterStore
{
    /// <summary>PostgreSQL: the C2-style upsert, one statement, returning the new totals.</summary>
    public const string PostgresIncrementSql =
        "INSERT INTO api_key_rate_counters (api_key_id, window_start, total, writes) VALUES ($1, $2, 1, $3) "
        + "ON CONFLICT (api_key_id, window_start) DO UPDATE SET total = api_key_rate_counters.total + 1, "
        + "writes = api_key_rate_counters.writes + EXCLUDED.writes RETURNING total, writes";

    /// <summary>SQLite (the relational test double): the same statement with positional parameters.</summary>
    public const string SqliteIncrementSql =
        "INSERT INTO api_key_rate_counters (api_key_id, window_start, total, writes) VALUES (@p0, @p1, 1, @p2) "
        + "ON CONFLICT (api_key_id, window_start) DO UPDATE SET total = api_key_rate_counters.total + 1, "
        + "writes = api_key_rate_counters.writes + EXCLUDED.writes RETURNING total, writes";

    public async Task<(int Total, int Writes)> IncrementAsync(
        Guid apiKeyId, DateTime windowStart, bool isWrite, CancellationToken ct)
    {
        bool postgres = db.Database.IsNpgsql();
        string sql = postgres ? PostgresIncrementSql : SqliteIncrementSql;
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using DbCommand command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = sql;
            command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
            AddParameter(command, postgres ? "$1" : "@p0", apiKeyId);
            AddParameter(command, postgres ? "$2" : "@p1", DateTime.SpecifyKind(windowStart, DateTimeKind.Utc));
            AddParameter(command, postgres ? "$3" : "@p2", isWrite ? 1 : 0);
            await using DbDataReader reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                return (1, isWrite ? 1 : 0);
            return (reader.GetInt32(0), reader.GetInt32(1));
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    public Task<int> DeleteOlderThanAsync(DateTime windowStart, CancellationToken ct) =>
        db.ApiKeyRateCounters
            .Where(c => c.WindowStart < windowStart)
            .ExecuteDeleteAsync(ct);

    private static void AddParameter(DbCommand command, string name, object value)
    {
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
