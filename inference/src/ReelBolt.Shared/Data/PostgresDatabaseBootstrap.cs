using Microsoft.Extensions.Logging;
using Npgsql;

namespace ReelBolt.Shared.Data;

/// <summary>
/// Creates a missing database before <c>MigrateAsync</c>, so EF Core never takes its own create path.
/// That path (Npgsql EF 10.0.3 <c>NpgsqlRelationalConnection.CreateAdminConnection</c>) opens an admin
/// connection on an unnamed data source whose <c>db.client.connection.pool.name</c> metric tag is the
/// connection string minus <c>Password</c> (host, user, <c>SSL Password</c>; Security S-1). Here both
/// data sources carry a fixed name, never pool, and are disposed before returning.
/// </summary>
public static class PostgresDatabaseBootstrap
{
    // Npgsql EF 10.0.3 connects to NpgsqlOptionsExtension.AdminDatabase ?? "postgres"; neither service
    // calls UseAdminDatabase, so this is the database EF itself would use.
    private const string AdminDatabase = "postgres";

    private const string InvalidCatalogName = "3D000";
    private const string DuplicateDatabase = "42P04";
    private const string UniqueViolation = "23505";

    /// <summary>
    /// Opens the target database through a data source named <paramref name="dataSourceName"/>; when it
    /// does not exist (3D000), creates it through <c>{dataSourceName}-admin</c> on the <c>postgres</c>
    /// database with the statement EF would have run (42P04, or 23505 on <c>pg_database_datname_index</c>,
    /// from a concurrent creator count as success).
    /// </summary>
    /// <param name="connectionString">The configured connection string, with its Password (not
    /// <c>DatabaseFacade.GetConnectionString()</c>, which drops it under <c>ConfigureDataSource</c>).</param>
    /// <exception cref="InvalidOperationException">Any other failure of the probe or the create (Security
    /// S-16): startup fails here, as <c>MigrateAsync</c> would have, so EF's unnamed admin data source never
    /// runs after a failed bootstrap. The message names the step's data source, the step, the exception type
    /// and the SqlState, never connection-string text; the original exception is the InnerException.</exception>
    public static async Task EnsureDatabaseExistsAsync(
        string connectionString, string dataSourceName, ILogger logger, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataSourceName);
        ArgumentNullException.ThrowIfNull(logger);

        string step = "probe";
        string stepSource = dataSourceName;
        try
        {
            var target = new NpgsqlConnectionStringBuilder(PostgresPoolSizing.RemoveMaxPoolSize(connectionString))
            {
                Pooling = false,
                Multiplexing = false,
            };
            // Npgsql 10.0.3 sends a database only when Database is set; otherwise the server opens the database
            // named after the user. That user is Username, else PGUSER, else the Kerberos principal, else
            // Environment.UserName (NpgsqlConnector.GetUsernameAsync); PGDATABASE is never read. Without
            // Username the name is left to Npgsql's own inference and read back as session_user on the admin
            // connection (Security S-25), so it always matches the connection EF will open. When no user can
            // be found the probe throws, and startup stops below.
            string? database = !string.IsNullOrEmpty(target.Database) ? target.Database
                : !string.IsNullOrEmpty(target.Username) ? target.Username
                : null;

            if (await TargetExistsAsync(target.ConnectionString, dataSourceName, cancellationToken).ConfigureAwait(false))
                return;

            step = "create";
            stepSource = dataSourceName + "-admin";
            var admin = new NpgsqlConnectionStringBuilder(target.ConnectionString) { Database = AdminDatabase };
            if (await CreateAsync(admin.ConnectionString, stepSource, database, cancellationToken).ConfigureAwait(false))
                logger.LogInformation("Bootstrap data source {DataSourceName} created the missing database", dataSourceName);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Neither the inner exception's message nor the connection string: both can carry host, user or
            // other connection-string text. The type and SqlState are enough to tell 42501 from 57P03/53300.
            string sqlState = (ex as PostgresException)?.SqlState is { } state ? $" (SqlState {state})" : string.Empty;
            throw new InvalidOperationException(
                $"Database bootstrap failed at the {step} step on data source '{stepSource}' with {ex.GetType().Name}{sqlState}; " +
                "startup stops before migrations. See the inner exception for details.",
                ex);
        }
    }

    private static async Task<bool> TargetExistsAsync(string connectionString, string name, CancellationToken cancellationToken)
    {
        await using NpgsqlDataSource dataSource = Build(connectionString, name);
        try
        {
            await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (PostgresException ex) when (ex.SqlState == InvalidCatalogName)
        {
            return false;
        }
    }

    /// <summary>True when this call created the database; false when another caller won the race.</summary>
    /// <param name="database">The target name, or <c>null</c> for the database the server defaults to: the
    /// name of the user this connection authenticated as, which Npgsql inferred exactly as for the probe.</param>
    private static async Task<bool> CreateAsync(string adminConnectionString, string name, string? database, CancellationToken cancellationToken)
    {
        await using NpgsqlDataSource dataSource = Build(adminConnectionString, name);
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        if (database is null)
        {
            await using NpgsqlCommand whoami = new("SELECT session_user", connection);
            database = (string)(await whoami.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        }

        // NpgsqlMigrationsSqlGenerator.Generate(NpgsqlCreateDatabaseOperation): neither model annotates a
        // template, tablespace or collation, so EF emits a bare CREATE DATABASE with no TEMPLATE,
        // TABLESPACE or LC_COLLATE clause.
        await using NpgsqlCommand create = new("CREATE DATABASE " + DelimitIdentifier(database) + ";", connection);
        try
        {
            await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (PostgresException ex) when (ex.SqlState == DuplicateDatabase
            || (ex.SqlState == UniqueViolation && ex.ConstraintName == "pg_database_datname_index"))
        {
            // Created concurrently (another replica or service); NpgsqlDatabaseCreator also accepts the 23505 form.
            return false;
        }
    }

    private static NpgsqlDataSource Build(string connectionString, string name)
        => new NpgsqlDataSourceBuilder(connectionString) { Name = name }.Build();

    // Always quoted, embedded quotes doubled. EF leaves a name unquoted only when it is lower-case letters,
    // digits, '_' or '$' and not reserved, and such a name folds to itself, so the database is the same.
    private static string DelimitIdentifier(string identifier)
        => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
