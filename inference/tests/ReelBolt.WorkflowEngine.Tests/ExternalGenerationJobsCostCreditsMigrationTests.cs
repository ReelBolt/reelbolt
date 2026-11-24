#nullable enable
using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Migrations;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Runs the money half of <see cref="ExternalGenerationJobsCostCredits"/> for real, against a
/// relational engine, because a migration that only compiles is a claim and not evidence. The
/// statements under test are the migration's OWN constants, so what ships is what runs here.
/// </summary>
public class ExternalGenerationJobsCostCreditsMigrationTests
{
    /// <summary>A table shaped like the pre-migration one, holding one dollar row and one GPU row.</summary>
    private static SqliteConnection LegacyTable()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var create = connection.CreateCommand();
        create.CommandText = """
            CREATE TABLE external_generation_jobs (
                id TEXT PRIMARY KEY,
                cost_estimate_usd NUMERIC NOT NULL,
                actual_cost_usd NUMERIC NULL,
                cost_unit TEXT NOT NULL DEFAULT 'usd');
            INSERT INTO external_generation_jobs (id, cost_estimate_usd, actual_cost_usd, cost_unit)
                VALUES ('legacy', 0.96, 0.48, 'usd');
            INSERT INTO external_generation_jobs (id, cost_estimate_usd, actual_cost_usd, cost_unit)
                VALUES ('gpu', 12.5, NULL, 'gpuseconds');
            """;
        create.ExecuteNonQuery();
        return connection;
    }

    private static void Rename(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = ExternalGenerationJobsCostCredits.RenameCostEstimateColumnSql
            + ExternalGenerationJobsCostCredits.RenameActualCostColumnSql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Runs the data statement on its own, which is what a re-run would repeat.</summary>
    private static int Convert(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = ExternalGenerationJobsCostCredits.ConvertLegacyUsdRowsSql;
        return cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// The third change, which no SQLite run can show: the column default moves too, so a row
    /// inserted without a unit is a credit row. Proven from the SQL the migration actually
    /// generates for the provider the engine ships on.
    /// </summary>
    [Fact]
    public void The_generated_script_moves_the_column_default_with_the_meaning()
    {
        DbContextOptions<WorkflowEngineDbContext> options = new DbContextOptionsBuilder<WorkflowEngineDbContext>()
            .UseNpgsql("Host=localhost;Database=none;Username=x;Password=y",
                o => o.MigrationsHistoryTable("__EFMigrationsHistory_Workflow"))
            .Options;

        using var db = new WorkflowEngineDbContext(options);
        string script = db.Database.GetService<IMigrator>().GenerateScript(
            toMigration: "20261010120000_ExternalGenerationJobsCostCredits");

        script.Should().Contain("RENAME COLUMN cost_estimate_usd TO cost_estimate");
        script.Should().Contain("RENAME COLUMN actual_cost_usd TO actual_cost");
        script.Should().Contain("cost_estimate = cost_estimate * 1000 / 6");
        script.Should().Contain("WHERE cost_unit = 'usd'");
        script.Should().Contain("SET DEFAULT 'credits'");
    }

    private static (decimal Estimate, decimal? Actual, string Unit) Row(SqliteConnection connection, string id)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT cost_estimate, actual_cost, cost_unit FROM external_generation_jobs WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        reader.Read();
        return (reader.GetDecimal(0), reader.IsDBNull(1) ? null : reader.GetDecimal(1), reader.GetString(2));
    }

    [Fact]
    public void A_legacy_dollar_row_is_rewritten_as_its_credit_equivalent()
    {
        using SqliteConnection connection = LegacyTable();

        Rename(connection);
        Convert(connection).Should().Be(1, "only the one dollar row is converted");

        // $0.96 of vendor cost is 160 credits; $0.48 is 80.
        Row(connection, "legacy").Should().Be((160m, 80m, ExternalGenerationJob.CreditsUnit));
    }

    [Fact]
    public void A_gpu_seconds_row_is_never_touched_by_the_conversion()
    {
        using SqliteConnection connection = LegacyTable();

        Rename(connection);
        Convert(connection);

        // GPU time is not money: the guard is cost_unit, so its figure and its unit both survive.
        var gpu = Row(connection, "gpu");
        gpu.Estimate.Should().Be(12.5m);
        gpu.Actual.Should().BeNull();
        gpu.Unit.Should().Be(ExternalGenerationJob.GpuSecondsUnit);
    }

    [Fact]
    public void Running_the_conversion_a_second_time_is_a_no_op()
    {
        using SqliteConnection connection = LegacyTable();

        Rename(connection);
        Convert(connection).Should().Be(1);
        var afterFirst = Row(connection, "legacy");

        Convert(connection).Should().Be(0, "the guard is cost_unit = 'usd', which the first run removed");

        Row(connection, "legacy").Should().Be(afterFirst);
        afterFirst.Estimate.Should().Be(160m, "a second multiplication would have made it 26666.66…");
    }
}
