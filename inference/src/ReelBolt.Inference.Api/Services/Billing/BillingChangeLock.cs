using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace ReelBolt.Inference.Api.Services.Billing;

/// <summary>
/// Security R1: a per-organization claim that serializes subscription change requests (seats,
/// change-plan, cancel, resume), so two parallel requests cannot both be judged against the same live
/// Paddle state. Never blocks: a claim someone else holds answers null and the caller refuses.
/// </summary>
public interface IBillingChangeLock
{
    /// <summary>The claim (dispose it to release), or null when another request holds it.</summary>
    Task<IAsyncDisposable?> TryAcquireAsync(Guid organizationId, CancellationToken ct);
}

/// <summary>
/// One process only: used when the database is not Postgres (Sqlite/InMemory tests, a single dev
/// process). <see cref="Shared"/> is the instance every such request uses.
/// </summary>
public sealed class InProcessBillingChangeLock : IBillingChangeLock
{
    public static InProcessBillingChangeLock Shared { get; } = new();

    private readonly ConcurrentDictionary<Guid, object> _held = new();

    public Task<IAsyncDisposable?> TryAcquireAsync(Guid organizationId, CancellationToken ct)
    {
        object token = new();
        IAsyncDisposable? claim = _held.TryAdd(organizationId, token) ? new Claim(_held, organizationId, token) : null;
        return Task.FromResult(claim);
    }

    private sealed class Claim(ConcurrentDictionary<Guid, object> held, Guid organizationId, object token) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            // Removes only this claim: a second dispose must not free a later holder's claim.
            held.TryRemove(new KeyValuePair<Guid, object>(organizationId, token));
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>
/// Across every API replica: a session-level <c>pg_try_advisory_lock</c> on a dedicated connection held
/// for the request's duration (the Paddle calls happen outside any EF transaction, so an xact lock would
/// not cover them), released with <c>pg_advisory_unlock</c> on that same connection. The connection is
/// unpooled, so closing it ends the session and frees the lock even if the unlock itself failed.
/// Connections come from a named, unpooled <see cref="NpgsqlDataSource"/> (Security S-1): a bare
/// <c>new NpgsqlConnection(cs)</c> gets an implicit data source whose metrics pool name is the connection
/// string minus Password, which would put SSL Password, host, user and database on the Npgsql meter.
/// <para>
/// Security S-4/S-23: those connections are capped per process. A claim this process already holds for the
/// organization is refused (<c>null</c>) before anything connects; any other attempt first takes one of
/// <see cref="BillingChangeLockOptions.MaxConnections"/> slots, waiting at most
/// <see cref="BillingChangeLockOptions.SlotWaitMilliseconds"/>, and throws
/// <see cref="BillingChangeLockCapacityException"/> when none frees. The slot and the in-process claim are
/// returned on a refusal, on an exception and when the claim is disposed.
/// </para>
/// </summary>
public sealed class PostgresBillingChangeLock : IBillingChangeLock
{
    /// <summary>The <c>db.client.connection.pool.name</c> the lock's connections report under.</summary>
    public const string DataSourceName = "reelbolt-inference-api-billing-lock";

    /// <summary>The <c>Application Name</c> (<c>pg_stat_activity.application_name</c>) of every lock connection.</summary>
    public const string ApplicationName = "reelbolt-billing-lock";

    private const string KeyPrefix = "billing-change:";

    // One data source per connection string for the process, as Npgsql's own implicit pool manager
    // keeps for a bare NpgsqlConnection: the lock is resolved per request, and every data source
    // registers metric instruments that live until it is disposed.
    // Lazy: a GetOrAdd race must not build (and leak the instruments of) a second data source.
    private static readonly ConcurrentDictionary<string, Lazy<NpgsqlDataSource>> DataSources = new(StringComparer.Ordinal);

    // One gate (slots + in-process claims) per data source and cap, for the process: the lock is resolved
    // per request, so the cap must outlive any one instance.
    private static readonly ConcurrentDictionary<string, Lazy<Gate>> Gates = new(StringComparer.Ordinal);

    private readonly Gate _gate;
    private readonly TimeSpan _slotWait;
    private readonly Func<long, CancellationToken, Task<IAsyncDisposable?>> _trySession;

    /// <summary>
    /// The (unpooled) connection string the lock's data source was built from, password included:
    /// <c>NpgsqlDataSource.ConnectionString</c> drops the password, so tests read this instead.
    /// </summary>
    internal string ConnectionString { get; }

    public PostgresBillingChangeLock(string connectionString)
        : this(connectionString, new BillingChangeLockOptions())
    {
    }

    public PostgresBillingChangeLock(string connectionString, BillingChangeLockOptions options)
    {
        string unpooled = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Pooling = false,
            ApplicationName = ApplicationName
        }.ConnectionString;
        ConnectionString = unpooled;
        NpgsqlDataSource dataSource = DataSources.GetOrAdd(unpooled, static cs => new Lazy<NpgsqlDataSource>(
            () => new NpgsqlDataSourceBuilder(cs) { Name = DataSourceName }.Build())).Value;
        _gate = GateFor(unpooled, options);
        _slotWait = TimeSpan.FromMilliseconds(options.SlotWaitMilliseconds);
        _trySession = (key, ct) => TrySessionAsync(dataSource, key, ct);
    }

    /// <summary>
    /// Tests: the gate keyed by <paramref name="gateKey"/>, with <paramref name="trySession"/> standing in for
    /// "open a lock connection and try the advisory lock" (a held session, or null when another holds it).
    /// </summary>
    internal PostgresBillingChangeLock(string gateKey, BillingChangeLockOptions options, Func<long, CancellationToken, Task<IAsyncDisposable?>> trySession)
    {
        ConnectionString = gateKey;
        _gate = GateFor(gateKey, options);
        _slotWait = TimeSpan.FromMilliseconds(options.SlotWaitMilliseconds);
        _trySession = trySession;
    }

    private static Gate GateFor(string dataSourceKey, BillingChangeLockOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxConnections, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(options.SlotWaitMilliseconds);
        int max = options.MaxConnections;
        return Gates.GetOrAdd($"{max}|{dataSourceKey}", _ => new Lazy<Gate>(() => new Gate(max))).Value;
    }

    /// <summary>The advisory lock key: the first 8 bytes of SHA-256 of <c>"billing-change:" + orgId</c>, stable across processes.</summary>
    public static long AdvisoryKey(Guid organizationId)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(KeyPrefix + organizationId));
        return BitConverter.ToInt64(hash, 0);
    }

    public async Task<IAsyncDisposable?> TryAcquireAsync(Guid organizationId, CancellationToken ct)
    {
        // This process already holds the org's claim: refuse without connecting.
        if (!_gate.Claims.TryAdd(organizationId, 0)) return null;

        bool slotTaken = false;
        try
        {
            slotTaken = await _gate.Slots.WaitAsync(_slotWait, ct).ConfigureAwait(false);
            if (!slotTaken) throw new BillingChangeLockCapacityException();

            IAsyncDisposable? session = await _trySession(AdvisoryKey(organizationId), ct).ConfigureAwait(false);
            if (session is not null) return new Claim(session, _gate, organizationId);
        }
        catch
        {
            _gate.Return(organizationId, slotTaken);
            throw;
        }

        _gate.Return(organizationId, slotTaken);
        return null;
    }

    /// <summary>Opens one lock connection and tries the org's advisory lock on it; null (connection closed) when another session holds it.</summary>
    private static async Task<IAsyncDisposable?> TrySessionAsync(NpgsqlDataSource dataSource, long key, CancellationToken ct)
    {
        NpgsqlConnection connection = dataSource.CreateConnection();
        try
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await using NpgsqlCommand command = new("SELECT pg_try_advisory_lock(@key)", connection);
            command.Parameters.AddWithValue("key", key);
            if (await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is true)
            {
                return new Session(connection, key);
            }
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        await connection.DisposeAsync().ConfigureAwait(false);
        return null;
    }

    /// <summary>The process-wide connection slots and in-process org claims of one data source.</summary>
    private sealed class Gate(int maxConnections)
    {
        public SemaphoreSlim Slots { get; } = new(maxConnections, maxConnections);

        public ConcurrentDictionary<Guid, byte> Claims { get; } = new();

        public void Return(Guid organizationId, bool slotTaken)
        {
            if (slotTaken) Slots.Release();
            Claims.TryRemove(organizationId, out _);
        }
    }

    /// <summary>A held claim: disposing it ends the session, then returns the slot and the in-process claim, once.</summary>
    private sealed class Claim(IAsyncDisposable session, Gate gate, Guid organizationId) : IAsyncDisposable
    {
        private int _released;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 1) return;
            try
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                gate.Return(organizationId, slotTaken: true);
            }
        }
    }

    private sealed class Session(NpgsqlConnection connection, long key) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                // Same connection (session) that took it, and not the request's token: an aborted
                // request still releases.
                await using NpgsqlCommand command = new("SELECT pg_advisory_unlock(@key)", connection);
                command.Parameters.AddWithValue("key", key);
                await command.ExecuteScalarAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (NpgsqlException)
            {
                // The change already reached Paddle (or failed on its own); closing the unpooled
                // session below frees the lock, so a failed unlock must not turn that into a 500.
            }
            finally
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
