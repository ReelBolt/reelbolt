using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Migrations;
using ReelBolt.WorkflowEngine.Tests.Infrastructure;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The new-user funnel Privacy bump (docs/design/new-user-funnel.md section 7): Privacy alone moves from
/// 2026-11-10 to 2026-11-11, only while the row still holds the previously shipped default and was not
/// published by an operator. The Terms row is never named, so the bump is an acknowledgement and never a
/// re-acceptance.
/// </summary>
public class AcquisitionPrivacyVersionMigrationTests
{
    private const string Before = "20261111101323_AddAcquisitionFunnel";
    private const string Target = "20261111180000_AcquisitionPrivacyVersion";

    private static string[] Sql(IReadOnlyList<MigrationOperation> ops) =>
        ops.OfType<SqlOperation>().Select(o => o.Sql).ToArray();

    [Fact]
    public void Up_MovesOnlyPrivacy_FromTheShippedDefault_OnlyWhenNotOperatorPublished()
    {
        AcquisitionPrivacyVersion migration = new();
        migration.UpOperations.Should().AllBeOfType<SqlOperation>("the bump changes no schema");
        string only = Sql(migration.UpOperations).Single();

        only.Should().Contain("SET version = '2026-11-11'");
        only.Should().Contain("document = 'Privacy'");
        only.Should().NotContain("Terms", "a Privacy-only bump must not ask anyone to re-accept the Terms");
        only.Should().Contain("version = '2026-11-10'");
        only.Should().Contain("updated_by IN ('config', 'migration:MarketplaceLegalVersions')");
        only.Should().Contain("updated_by = 'migration:AcquisitionPrivacyVersion'");
    }

    [Fact]
    public void Down_RevertsOnlyTheRowThisMigrationMoved()
    {
        AcquisitionPrivacyVersion migration = new();
        migration.DownOperations.Should().AllBeOfType<SqlOperation>();
        string only = Sql(migration.DownOperations).Single();

        only.Should().Contain("SET version = '2026-11-10'");
        only.Should().Contain("document = 'Privacy'");
        only.Should().NotContain("Terms");
        only.Should().Contain("version = '2026-11-11'");
        only.Should().Contain("updated_by = 'migration:AcquisitionPrivacyVersion'");
    }

    [Fact]
    public void The_migration_is_the_one_after_the_funnel_schema_and_carries_the_same_model()
    {
        typeof(AcquisitionPrivacyVersion).GetCustomAttribute<MigrationAttribute>()!.Id.Should().Be(Target);
        typeof(AcquisitionPrivacyVersion).GetCustomAttribute<DbContextAttribute>()!.ContextType.Should().Be(typeof(InferenceApiDbContext));

        using InferenceApiDbContext db = new(
            new DbContextOptionsBuilder<InferenceApiDbContext>().UseNpgsql("Host=unused;Database=unused").Options);
        string[] ids = db.GetService<IMigrationsAssembly>().Migrations.Keys.Order(StringComparer.Ordinal).ToArray();
        int at = Array.IndexOf(ids, Target);
        at.Should().BeGreaterThan(0);
        ids[at - 1].Should().Be(Before, "the bump follows the schema it describes");

        // No schema change: the model this migration targets is the model of the one before it, and
        // the model the snapshot holds.
        IModelRuntimeInitializer initializer = db.GetService<IModelRuntimeInitializer>();
        IRelationalModel Relational(IModel model) => initializer
            .Initialize(model is IMutableModel mutable ? mutable.FinalizeModel() : model, designTime: true, validationLogger: null)
            .GetRelationalModel();
        IMigrationsModelDiffer differ = db.GetService<IMigrationsModelDiffer>();
        IRelationalModel own = Relational(new AcquisitionPrivacyVersion().TargetModel);

        differ.HasDifferences(Relational(new AddAcquisitionFunnel().TargetModel), own).Should().BeFalse(
            "the Designer file is the previous migration's model: this migration changes no schema");
        // Only while it is the latest migration is its model the snapshot's; a later schema migration moves the snapshot on.
        if (at == ids.Length - 1)
        {
            differ.HasDifferences(own, Relational(db.GetService<IMigrationsAssembly>().ModelSnapshot!.Model)).Should().BeFalse(
                "it is the latest migration, so its model is the snapshot's");
        }
    }

    [Fact]
    public void FreshInstalls_StartAtTheSameVersions()
    {
        LegalDocumentSeeder.DefaultPrivacyVersion.Should().Be("2026-11-17", "AvatarLibraryLegalVersions moved it on");
        LegalDocumentSeeder.DefaultTermsVersion.Should().Be("2026-11-17");
    }

}

/// <summary>
/// The same bump against a real database: every state the Privacy row can be in before the release, taken
/// up and down again, with the Terms row watched throughout.
/// </summary>
[Collection("Postgres")]
[Trait("Category", "Postgres")]
public class AcquisitionPrivacyVersionPostgresTests
{
    private const string Before = "20261111101323_AddAcquisitionFunnel";
    private const string Target = "20261111180000_AcquisitionPrivacyVersion";

    private sealed record Row(string Version, string UpdatedBy, DateTime EffectiveAt);

    [PostgresTheory]
    // The shipped default, as a fresh install seeded it and as the previous migration left it: moved.
    [InlineData("2026-11-10", "config", true)]
    [InlineData("2026-11-10", "migration:MarketplaceLegalVersions", true)]
    // The same version, but an operator published it: theirs, never touched.
    [InlineData("2026-11-10", "ada@example.com", false)]
    // An operator's own later or earlier version: never touched.
    [InlineData("2026-12-01", "ada@example.com", false)]
    [InlineData("2026-11-05", "config", false)]
    // Already at the new version on a fresh install of this release: nothing to move.
    [InlineData("2026-11-11", "config", false)]
    public async Task The_privacy_row_moves_only_from_the_shipped_default_and_the_terms_row_never_moves(
        string version, string updatedBy, bool moves)
    {
        await WithDatabase(async (db, connection) =>
        {
            IMigrator migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync(Before);
            DateTime published = new(2026, 11, 10, 8, 0, 0, DateTimeKind.Utc);
            await Exec(connection,
                "INSERT INTO legal_documents (document, version, effective_at, updated_at, updated_by) VALUES "
                + "('Terms', '2026-11-10', @at, @at, 'migration:MarketplaceLegalVersions'), "
                + "('Privacy', @version, @at, @at, @by)",
                ("at", published), ("version", version), ("by", updatedBy));

            await migrator.MigrateAsync(Target);

            Row privacy = await Read(connection, "Privacy");
            if (moves)
            {
                privacy.Version.Should().Be("2026-11-11");
                privacy.UpdatedBy.Should().Be("migration:AcquisitionPrivacyVersion");
                privacy.EffectiveAt.Should().BeAfter(published, "the new version takes effect when it is published");
            }
            else
            {
                privacy.Should().Be(new Row(version, updatedBy, published));
            }

            Row terms = await Read(connection, "Terms");
            terms.Should().Be(new Row("2026-11-10", "migration:MarketplaceLegalVersions", published),
                "a Privacy-only bump never asks anyone to accept the Terms again");

            await migrator.MigrateAsync(Before);

            Row back = await Read(connection, "Privacy");
            if (moves)
            {
                back.Version.Should().Be("2026-11-10");
                back.UpdatedBy.Should().Be("migration:AcquisitionPrivacyVersion:down");
            }
            else
            {
                back.Should().Be(new Row(version, updatedBy, published), "Down undoes only what Up did");
            }

            (await Read(connection, "Terms")).Should().Be(
                new Row("2026-11-10", "migration:MarketplaceLegalVersions", published));
        });
    }

    [PostgresFact]
    public async Task A_database_with_no_legal_rows_yet_migrates_cleanly_and_the_seeder_then_starts_at_the_new_version()
    {
        await WithDatabase(async (db, connection) =>
        {
            await db.GetService<IMigrator>().MigrateAsync();
            (await Count(connection, "SELECT count(*) FROM legal_documents")).Should().Be(0L, "the rows are seeded at boot, not by a migration");

            await LegalDocumentSeeder.SeedAsync(
                db,
                new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
                Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

            (await Read(connection, "Privacy")).Version.Should().Be("2026-11-17");
            (await Read(connection, "Terms")).Version.Should().Be("2026-11-17");
        });
    }

    private static async Task WithDatabase(Func<InferenceApiDbContext, NpgsqlConnection, Task> body)
    {
        string baseConnection = PostgresTestEnvironment.BaseConnectionString()!;
        string dbName = "reelbolt_privacy_bump_" + Guid.NewGuid().ToString("N")[..12];
        await using NpgsqlDataSource admin = PostgresTestEnvironment.CreateAdminDataSource(baseConnection);
        await using (NpgsqlCommand create = admin.CreateCommand($"CREATE DATABASE {dbName}"))
        {
            await create.ExecuteNonQueryAsync();
        }

        string connectionString = PostgresTestEnvironment.ForDatabase(baseConnection, dbName);
        try
        {
            await using InferenceApiDbContext db = new(
                new DbContextOptionsBuilder<InferenceApiDbContext>().UseNpgsql(connectionString).Options);
            await using NpgsqlConnection connection = new(connectionString);
            await connection.OpenAsync();
            await body(db, connection);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using NpgsqlDataSource dropAdmin = PostgresTestEnvironment.CreateAdminDataSource(baseConnection);
            await using NpgsqlCommand drop = dropAdmin.CreateCommand($"DROP DATABASE IF EXISTS {dbName} WITH (FORCE)");
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static async Task Exec(NpgsqlConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using NpgsqlCommand command = new(sql, connection);
        foreach ((string name, object value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> Count(NpgsqlConnection connection, string sql)
    {
        await using NpgsqlCommand command = new(sql, connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<Row> Read(NpgsqlConnection connection, string document)
    {
        await using NpgsqlCommand command = new(
            "SELECT version, updated_by, effective_at FROM legal_documents WHERE document = @document", connection);
        command.Parameters.AddWithValue("document", document);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue($"the {document} row exists");
        return new Row(reader.GetString(0), reader.GetString(1), reader.GetDateTime(2));
    }
}
