using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ReelBolt.Shared.Data;
using ReelBolt.WorkflowEngine.Data;

namespace ReelBolt.WorkflowEngine;

/// <summary>
/// How the engine meets its database at startup (infra/postgres/engine-role.sql).
/// <list type="bullet">
/// <item><c>Database:MigrateAtStartup</c> = <c>true</c> (default, single host): create the database when it is
/// missing and apply the engine's migrations, as always.</item>
/// <item><c>Database:MigrateAtStartup</c> = <c>false</c> (the render host): the engine logs in as a role that
/// can only read and write rows, so it creates nothing. It reads which migrations are applied and refuses to
/// start while one is pending.</item>
/// <item><c>--migrate-only</c>: apply the migrations and exit, with <c>ConnectionStrings:MigrationConnection</c>
/// when set (the migrator role, held by the control host only), else <c>DefaultConnection</c>. It builds no
/// engine service, so it needs no RabbitMQ, JWT or storage configuration.</item>
/// </list>
/// </summary>
public static class EngineDatabaseStartup
{
    public const string MigrateAtStartupKey = "Database:MigrateAtStartup";
    public const string MigrateOnlyArgument = "--migrate-only";
    public const string MigrationConnectionName = "MigrationConnection";
    public const string DefaultConnectionName = "DefaultConnection";
    public const string HistoryTable = "__EFMigrationsHistory_Workflow";

    // Fixed data source names: the default would put connection-string text into the Npgsql meter tags (Security S-1).
    private const string BootstrapDataSourceName = "reelbolt-workflow-engine-bootstrap";
    private const string MigratorDataSourceName = "reelbolt-workflow-engine-migrator";

    /// <summary>True when the command line asks for the migrate-and-exit mode.</summary>
    public static bool IsMigrateOnly(IEnumerable<string> args)
        => args.Any(a => string.Equals(a, MigrateOnlyArgument, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// <c>Database:MigrateAtStartup</c>, default <c>true</c>. Anything that is not a boolean stops startup:
    /// a typo must not silently turn migrations on for a role that cannot run them, or off for a single host.
    /// </summary>
    public static bool MigrateAtStartup(IConfiguration configuration)
    {
        string? raw = configuration[MigrateAtStartupKey];
        if (string.IsNullOrWhiteSpace(raw))
            return true;
        return bool.TryParse(raw.Trim(), out bool value)
            ? value
            : throw new InvalidOperationException($"{MigrateAtStartupKey} must be true or false, got '{raw}'.");
    }

    /// <summary>The connection string the migrate-only mode uses: <c>MigrationConnection</c> when set, else <c>DefaultConnection</c>.</summary>
    public static string? MigrationConnectionString(IConfiguration configuration)
    {
        string? migration = configuration.GetConnectionString(MigrationConnectionName);
        return string.IsNullOrWhiteSpace(migration) ? configuration.GetConnectionString(DefaultConnectionName) : migration;
    }

    /// <summary>
    /// The migrate-only mode: bootstrap the database, apply the engine's migrations, return the process exit
    /// code (0 applied or nothing to do, 1 failed). The failure is logged without the connection string.
    /// </summary>
    public static async Task<int> RunMigrateOnlyAsync(IConfiguration configuration, ILogger logger, CancellationToken cancellationToken = default)
    {
        string? connectionString = MigrationConnectionString(configuration);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            logger.LogError(
                "{Argument}: no connection string. Set ConnectionStrings:{Migration} or ConnectionStrings:{Default}.",
                MigrateOnlyArgument, MigrationConnectionName, DefaultConnectionName);
            return 1;
        }

        try
        {
            await PostgresDatabaseBootstrap.EnsureDatabaseExistsAsync(connectionString, BootstrapDataSourceName, logger, cancellationToken);
            DbContextOptions<WorkflowEngineDbContext> options = new DbContextOptionsBuilder<WorkflowEngineDbContext>()
                .UseNpgsql(connectionString, npgsql => npgsql
                    .MigrationsHistoryTable(HistoryTable)
                    .ConfigureDataSource(dataSource => dataSource.Name = MigratorDataSourceName))
                .Options;
            await using WorkflowEngineDbContext db = new(options);
            List<string> pending = [.. await db.Database.GetPendingMigrationsAsync(cancellationToken)];
            logger.LogInformation("{Argument}: applying {Count} pending WorkflowEngine migration(s)", MigrateOnlyArgument, pending.Count);
            await db.Database.MigrateAsync(cancellationToken);
            logger.LogInformation("{Argument}: WorkflowEngine database migrations completed", MigrateOnlyArgument);
            return 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "{Argument}: WorkflowEngine database migrations failed", MigrateOnlyArgument);
            return 1;
        }
    }

    /// <summary>
    /// Startup of the serving engine. With <c>Database:MigrateAtStartup</c> on it migrates as before; with it
    /// off it throws <see cref="PendingMigrationsException"/> when a migration has not been applied yet.
    /// </summary>
    public static async Task PrepareAsync(
        WorkflowEngineDbContext db, IConfiguration configuration, ILogger logger, CancellationToken cancellationToken = default)
    {
        if (MigrateAtStartup(configuration))
        {
            logger.LogInformation("Applying WorkflowEngine database migrations");
            // Security S-1: create a missing database through a named data source, so EF never opens its unnamed
            // admin connection. The raw configured string: GetConnectionString() has no Password under ConfigureDataSource.
            string? connectionString = configuration.GetConnectionString(DefaultConnectionName);
            if (db.Database.IsNpgsql() && !string.IsNullOrEmpty(connectionString))
            {
                await PostgresDatabaseBootstrap.EnsureDatabaseExistsAsync(connectionString, BootstrapDataSourceName, logger, cancellationToken);
            }

            await db.Database.MigrateAsync(cancellationToken);
            logger.LogInformation("WorkflowEngine database migrations completed");
            return;
        }

        List<string> pending = [.. await db.Database.GetPendingMigrationsAsync(cancellationToken)];
        if (pending.Count > 0)
        {
            throw new PendingMigrationsException(pending);
        }

        logger.LogInformation("{Key} is false: no migration is pending, the engine applies none", MigrateAtStartupKey);
    }
}

/// <summary>The engine was told not to migrate (<c>Database:MigrateAtStartup=false</c>) and its schema is behind.</summary>
public sealed class PendingMigrationsException(IReadOnlyList<string> pending) : InvalidOperationException(
    $"{EngineDatabaseStartup.MigrateAtStartupKey} is false and {pending.Count} WorkflowEngine migration(s) are not applied yet: "
    + string.Join(", ", pending)
    + $". Run the engine with {EngineDatabaseStartup.MigrateOnlyArgument} on the control host (the migrator role), "
    + "then apply infra/postgres/engine-role.sql, then start this engine again.")
{
    /// <summary>The ids of the migrations that are not applied yet, oldest first.</summary>
    public IReadOnlyList<string> Pending { get; } = pending;
}
