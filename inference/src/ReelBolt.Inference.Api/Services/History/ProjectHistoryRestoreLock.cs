using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ReelBolt.Inference.Api.Services.History;

/// <summary>
/// The per-project claim a restore holds while it runs (contract section 8 step 3), so two restores of
/// the same project cannot interleave. Never blocks: a claim someone else holds answers null and the
/// caller refuses with <c>409 RESTORE_IN_PROGRESS</c>.
/// </summary>
public interface IProjectRestoreLock
{
    /// <summary>The claim (dispose it to release), or null when another restore holds it.</summary>
    Task<IAsyncDisposable?> TryAcquireAsync(Guid projectId, CancellationToken ct);
}

/// <summary>
/// One process only: used when the database is not Postgres (InMemory tests, a single dev process).
/// </summary>
public sealed class InProcessProjectRestoreLock : IProjectRestoreLock
{
    public static InProcessProjectRestoreLock Shared { get; } = new();

    private readonly ConcurrentDictionary<Guid, object> _held = new();

    public Task<IAsyncDisposable?> TryAcquireAsync(Guid projectId, CancellationToken ct)
    {
        object token = new();
        IAsyncDisposable? claim = _held.TryAdd(projectId, token)
            ? new Claim(_held, projectId, token)
            : null;
        return Task.FromResult(claim);
    }

    private sealed class Claim(
        ConcurrentDictionary<Guid, object> held, Guid projectId, object token) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            held.TryRemove(new KeyValuePair<Guid, object>(projectId, token));
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>
/// Across every API replica: a session-level <c>pg_try_advisory_lock</c> on a dedicated, unpooled
/// connection held for the restore's duration (the billing-change lock's pattern: closing the session
/// frees the lock even when the unlock failed). Resolved to
/// <see cref="InProcessProjectRestoreLock"/> when the database is not Postgres, where advisory locks do
/// not exist.
/// </summary>
public sealed class PostgresProjectRestoreLock : IProjectRestoreLock
{
    /// <summary>The <c>db.client.connection.pool.name</c> the lock's connections report under.</summary>
    public const string DataSourceName = "reelbolt-inference-api-restore-lock";

    private const string KeyPrefix = "project-restore:";

    // One data source per connection string for the process (the lock is resolved per request, and
    // every data source registers metric instruments that live until disposed).
    private static readonly ConcurrentDictionary<string, Lazy<NpgsqlDataSource>> DataSources =
        new(StringComparer.Ordinal);

    private readonly NpgsqlDataSource _dataSource;

    public PostgresProjectRestoreLock(string connectionString)
    {
        string unpooled = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Pooling = false,
            ApplicationName = "reelbolt-restore-lock"
        }.ConnectionString;

        _dataSource = DataSources.GetOrAdd(unpooled, static cs =>
            new Lazy<NpgsqlDataSource>(() => new NpgsqlDataSourceBuilder(cs) { Name = DataSourceName }.Build())).Value;
    }

    /// <summary>The advisory lock key: the first 8 bytes of SHA-256 of <c>"project-restore:" + projectId</c>.</summary>
    public static long AdvisoryKey(Guid projectId) =>
        BitConverter.ToInt64(SHA256.HashData(Encoding.UTF8.GetBytes(KeyPrefix + projectId.ToString("D"))), 0);

    public async Task<IAsyncDisposable?> TryAcquireAsync(Guid projectId, CancellationToken ct)
    {
        NpgsqlConnection connection = _dataSource.CreateConnection();
        try
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            long key = AdvisoryKey(projectId);
            await using NpgsqlCommand command = new("SELECT pg_try_advisory_lock(@key)", connection);
            command.Parameters.AddWithValue("key", key);
            if (await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is true)
                return new Session(connection, key);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        await connection.DisposeAsync().ConfigureAwait(false);
        return null;
    }

    private sealed class Session(NpgsqlConnection connection, long key) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await using NpgsqlCommand command = new("SELECT pg_advisory_unlock(@key)", connection);
                command.Parameters.AddWithValue("key", key);
                await command.ExecuteScalarAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (NpgsqlException)
            {
                // Closing the unpooled session below frees the lock anyway.
            }
            finally
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}

/// <summary>
/// A non-blocking look at whether a restore holds the project's lock, for the run start: a run must not
/// begin while a restore is rewriting the project. Answers false (a no-op) off Npgsql, where no advisory
/// lock exists.
/// </summary>
/// <remarks>
/// Reads <c>pg_locks</c> through the request's own pooled <see cref="DbContext"/> connection instead of
/// probing with <c>pg_try_advisory_lock</c>. A try-lock would briefly take the lock (so a restore starting
/// at that instant could be refused by the probe itself) and, on an unpooled connection, would open a new
/// physical connection on every run start. The catalog read takes nothing, so it cannot hold or leak the
/// lock, and it reuses a connection the request already has. A bigint advisory key shows up in
/// <c>pg_locks</c> as <c>classid</c> (high 32 bits), <c>objid</c> (low 32 bits) and <c>objsubid = 1</c>.
/// </remarks>
public static class ProjectRestoreLockProbe
{
    public static async Task<bool> IsHeldAsync(DbContext db, Guid projectId, CancellationToken ct)
    {
        if (!db.Database.IsNpgsql())
            return false;

        long key = PostgresProjectRestoreLock.AdvisoryKey(projectId);
        long high = (key >> 32) & 0xFFFFFFFFL;
        long low = key & 0xFFFFFFFFL;

        List<bool> rows = await db.Database
            .SqlQuery<bool>($"""
                SELECT EXISTS (
                    SELECT 1 FROM pg_locks
                    WHERE locktype = 'advisory'
                      AND granted
                      AND objsubid = 1
                      AND classid::bigint = {high}
                      AND objid::bigint = {low}
                      AND database = (SELECT oid FROM pg_database WHERE datname = current_database())) AS "Value"
                """)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return rows.Count > 0 && rows[0];
    }
}
