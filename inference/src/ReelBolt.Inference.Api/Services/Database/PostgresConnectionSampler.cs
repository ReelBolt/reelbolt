using System.Data;
using System.Diagnostics.Metrics;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data;

namespace ReelBolt.Inference.Api.Services.Database;

/// <summary>
/// Every <c>Database:ServerConnectionSampleSeconds</c> (default 30, range 1..86400; 0 disables, anything
/// else out of range logs a Warning and disables) reads how many connections the Postgres server holds and its <c>max_connections</c>, and publishes both as
/// gauges, so the near-limit alert (docs/database-connections.md) sees every service's connections,
/// not just this process's pool. Runs only on the Npgsql provider, on its own one-connection data
/// source (<see cref="MaxPoolSize"/>), so a saturated API pool cannot stop it; a failed sample is logged at
/// Warning and the next tick tries again, so a database outage never stops or crashes the host.
/// </summary>
public sealed class PostgresConnectionSampler : BackgroundService
{
    /// <summary>Meter name; the Inference API's telemetry registration adds it with <c>AddMeter</c>.</summary>
    public const string MeterName = "ReelBolt.Inference.Database";

    public const string IntervalKey = "Database:ServerConnectionSampleSeconds";
    public const int DefaultIntervalSeconds = 30;
    public const int MaxIntervalSeconds = 86400;

    // QA-3: the sampler has its own data source rather than borrowing from the API's EF pool, so a
    // saturated pool (exactly when the near-limit alert matters) cannot blind it. Pool budget: this
    // source accounts for exactly one connection per Inference API process, on top of its EF pool.
    public const int MaxPoolSize = 1;

    // Security S-1: a fixed pool name keeps connection-string material out of the Npgsql meter's
    // db.client.connection.pool.name tag.
    public const string DataSourceName = "reelbolt-inference-api-sampler";

    private const string ConnectionStringName = "DefaultConnection";
    private const int CommandTimeoutSeconds = 5;

    // Security S-2: pg_stat_database.numbackends, not pg_stat_activity. pg_stat_activity hides other
    // roles' backend_type (NULL) unless this role has pg_monitor or is superuser, so on a managed
    // Postgres the count would silently drop to this role's own sessions and the near-limit alert would
    // never fire. numbackends is visible to any role and needs no grant. It also counts autovacuum and
    // parallel workers attached to a database, which over-counts slightly: the conservative direction
    // for an alert (it fires a little early, never late).
    private const string Sql =
        "SELECT (SELECT coalesce(sum(numbackends), 0) FROM pg_stat_database)::int, " +
        "current_setting('max_connections')::int";

    private static readonly Meter Meter = new(MeterName);

    // (connections, max) of the last successful sample; null until one succeeds, so a gauge never
    // reports a made-up zero.
    private static (int Connections, int Max)? _last;

    static PostgresConnectionSampler()
    {
        Meter.CreateObservableGauge(
            "reelbolt.db.server.connections", () => Observe(s => s.Connections), "connections",
            "Backends connected to the Postgres server's databases (all services), from pg_stat_database.numbackends");
        Meter.CreateObservableGauge(
            "reelbolt.db.server.max_connections", () => Observe(s => s.Max), "connections",
            "The Postgres server's max_connections setting");
    }

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<PostgresConnectionSampler> _logger;
    private NpgsqlDataSource? _dataSource; // created once, on the first Npgsql run; disposed with the service

    public PostgresConnectionSampler(IServiceScopeFactory scopeFactory, IConfiguration configuration, ILogger<PostgresConnectionSampler> logger)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// The sampler's own source on <paramref name="connectionString"/>: named <see cref="DataSourceName"/>
    /// and capped at <see cref="MaxPoolSize"/> connection, whatever pool size (in any spelling
    /// <see cref="PostgresPoolSizing"/> accepts) the string carries.
    /// </summary>
    public static NpgsqlDataSource CreateDataSource(string connectionString)
    {
        // Npgsql rejects the SqlClient spelling "Max Pool Size"; the size is set below anyway.
        var builder = new NpgsqlDataSourceBuilder(PostgresPoolSizing.RemoveMaxPoolSize(connectionString)) { Name = DataSourceName };
        builder.ConnectionStringBuilder.MaxPoolSize = MaxPoolSize;
        builder.ConnectionStringBuilder.MinPoolSize = 0; // a Minimum Pool Size above 1 would be rejected
        return builder.Build();
    }

    public override void Dispose()
    {
        base.Dispose();
        _dataSource?.Dispose();
        _dataSource = null;
    }

    private static IEnumerable<Measurement<int>> Observe(Func<(int Connections, int Max), int> pick)
    {
        if (_last is { } sample) yield return new Measurement<int>(pick(sample));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string? raw = _configuration[IntervalKey];
        int seconds = DefaultIntervalSeconds;
        if (!string.IsNullOrWhiteSpace(raw) && !int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out seconds))
        {
            _logger.LogWarning("Postgres server connection sampling disabled: {Key}='{Value}' is not a whole number", IntervalKey, raw);
            return;
        }
        if (seconds == 0)
        {
            _logger.LogInformation("Postgres server connection sampling disabled ({Key}=0)", IntervalKey);
            return;
        }
        // Security S-5: PeriodicTimer throws above ~49.7 days, outside the OCE-only catch below, and the
        // default BackgroundServiceExceptionBehavior would then stop the whole host.
        if (seconds is < 1 or > MaxIntervalSeconds)
        {
            _logger.LogWarning(
                "Postgres server connection sampling disabled: {Key}={Seconds} is outside 1..{Max} (0 turns it off)",
                IntervalKey, seconds, MaxIntervalSeconds);
            return;
        }
        if (stoppingToken.IsCancellationRequested) return;

        try
        {
            await Task.Yield(); // never hold up host startup, even for the provider check
            if (!IsNpgsql()) return;
            if (!TryCreateDataSource()) return;

            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(seconds));
            do
            {
                await SampleOnceAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // host shutting down
        }
        finally
        {
            _last = null; // a stopped sampler leaves the gauges absent, never frozen at its last value
        }
    }

    private bool TryCreateDataSource()
    {
        if (_dataSource is not null) return true;
        string? connectionString = _configuration.GetConnectionString(ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            _logger.LogWarning("Postgres server connection sampling disabled: ConnectionStrings:{Name} is not set", ConnectionStringName);
            return false;
        }
        try
        {
            _dataSource = CreateDataSource(connectionString);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Postgres server connection sampling disabled: its data source could not be created");
            return false;
        }
    }

    private bool IsNpgsql()
    {
        try
        {
            using IServiceScope scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
            return db.Database.IsNpgsql();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Postgres server connection sampling disabled: the database provider could not be resolved");
            return false;
        }
    }

    private async Task SampleOnceAsync(CancellationToken ct)
    {
        try
        {
            await using NpgsqlConnection connection = await _dataSource!.OpenConnectionAsync(ct);
            await using NpgsqlCommand command = connection.CreateCommand();
            command.CommandText = Sql;
            command.CommandTimeout = CommandTimeoutSeconds;
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, ct);
            if (await reader.ReadAsync(ct))
                _last = (reader.GetInt32(0), reader.GetInt32(1));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _last = null; // a stale reading must not keep feeding the alert while the database is unreachable
            _logger.LogWarning(ex, "Postgres server connection sample failed; retrying at the next interval");
        }
    }
}
