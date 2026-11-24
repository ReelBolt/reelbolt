#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.Caching;
using ReelBolt.WorkflowEngine.Services.Messaging;
using ReelBolt.WorkflowEngine.Tests.Infrastructure;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The project quarantine marker on a real schema (skipped unless <c>REELBOLT_TEST_PG</c> is set): the backfill
/// migration with rows shaped the way the real writers store them, the engine's project-file query filter (which reads the
/// marker), the marker reads from both contexts, and the claim's COALESCE of the run's first start.
/// </summary>
[Collection("Postgres")]
public class QuarantineMarkerPostgresTests
{
    private const string BeforeMarkerMigration = "20261028181725_AllowMultiTargetStepChangeRequests";
    private static readonly DateTime Old = new(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);

    private sealed class Db(string baseConnection, string name, string connection) : IAsyncDisposable
    {
        public string Connection { get; } = connection;
        public DbContextOptions<InferenceApiDbContext> Api { get; } = new DbContextOptionsBuilder<InferenceApiDbContext>().UseNpgsql(connection).Options;
        public DbContextOptions<WorkflowEngineDbContext> Engine { get; } = new DbContextOptionsBuilder<WorkflowEngineDbContext>().UseNpgsql(connection).Options;

        public static async Task<Db> CreateAsync()
        {
            string baseConnection = PostgresTestEnvironment.BaseConnectionString()!;
            string name = "reelbolt_quarantine_test_" + Guid.NewGuid().ToString("N")[..12];
            await using NpgsqlDataSource admin = PostgresTestEnvironment.CreateAdminDataSource(baseConnection);
            await using (NpgsqlCommand create = admin.CreateCommand($"CREATE DATABASE {name}"))
                await create.ExecuteNonQueryAsync();
            return new Db(baseConnection, name, PostgresTestEnvironment.ForDatabase(baseConnection, name));
        }

        public async Task MigrateApiAsync(string? target = null)
        {
            await using InferenceApiDbContext api = new(Api);
            await api.GetService<IMigrator>().MigrateAsync(target);
        }

        public async Task MigrateEngineAsync()
        {
            await using WorkflowEngineDbContext engine = new(Engine);
            await engine.GetService<IMigrator>().MigrateAsync();
        }

        /// <summary>
        /// Inserts one row with only the columns given; every other NOT NULL column without a default gets a neutral
        /// value by type. Foreign keys are not checked (the seeding connection is a replica-role session).
        /// </summary>
        public async Task InsertAsync(string table, Dictionary<string, object?> values)
        {
            await using NpgsqlConnection conn = new(Connection);
            await conn.OpenAsync();
            await using (NpgsqlCommand role = new("SET session_replication_role = replica", conn))
                await role.ExecuteNonQueryAsync();

            Dictionary<string, string> filler = [];
            await using (NpgsqlCommand cols = new(
                "SELECT column_name, data_type FROM information_schema.columns WHERE table_name = @t AND is_nullable = 'NO' AND column_default IS NULL AND is_identity = 'NO'", conn))
            {
                cols.Parameters.AddWithValue("t", table);
                await using NpgsqlDataReader r = await cols.ExecuteReaderAsync();
                while (await r.ReadAsync())
                {
                    string column = r.GetString(0);
                    if (values.ContainsKey(column))
                        continue;
                    filler[column] = r.GetString(1) switch
                    {
                        "uuid" => "gen_random_uuid()",
                        "boolean" => "false",
                        "integer" or "bigint" or "smallint" or "numeric" or "double precision" => "0",
                        "timestamp with time zone" or "timestamp without time zone" => "now()",
                        "jsonb" or "json" => "'{}'",
                        _ => "''"
                    };
                }
            }

            List<string> names = [.. values.Keys.Select(k => $"\"{k}\""), .. filler.Keys.Select(k => $"\"{k}\"")];
            List<string> exprs = [.. values.Keys.Select((_, i) => $"@p{i}"), .. filler.Values];
            await using NpgsqlCommand insert = new($"INSERT INTO \"{table}\" ({string.Join(",", names)}) VALUES ({string.Join(",", exprs)})", conn);
            int index = 0;
            foreach (object? value in values.Values)
                insert.Parameters.AddWithValue($"p{index++}", value ?? DBNull.Value);
            await insert.ExecuteNonQueryAsync();
        }

        public async Task<T?> ScalarAsync<T>(string sql, params (string Name, object Value)[] parameters)
        {
            await using NpgsqlConnection conn = new(Connection);
            await conn.OpenAsync();
            await using NpgsqlCommand cmd = new(sql, conn);
            foreach ((string n, object v) in parameters)
                cmd.Parameters.AddWithValue(n, v);
            object? result = await cmd.ExecuteScalarAsync();
            return result is null or DBNull ? default : (T)result;
        }

        public async ValueTask DisposeAsync()
        {
            await using NpgsqlDataSource cleanup = PostgresTestEnvironment.CreateAdminDataSource(baseConnection);
            await using NpgsqlCommand drop = cleanup.CreateCommand($"DROP DATABASE IF EXISTS {name} WITH (FORCE)");
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static Task<Guid> ProjectAsync(Db db, string name)
    {
        Guid id = Guid.NewGuid();
        return db.InsertAsync("projects", new() { ["id"] = id, ["name"] = name }).ContinueWith(_ => id);
    }

    // ------------------------------------------------------------------ the backfill

    [PostgresFact]
    [Trait("Category", "Postgres")]
    public async Task The_backfill_marks_projects_with_past_quarantine_audit_rows_as_the_real_writers_store_them()
    {
        await using Db db = await Db.CreateAsync();
        await db.MigrateApiAsync(BeforeMarkerMigration);

        Guid deletedCsam = await ProjectAsync(db, "csam audit, file gone");
        Guid moderationBlock = await ProjectAsync(db, "ingest block, file gone");
        Guid quarantinedRow = await ProjectAsync(db, "quarantined file row");
        Guid clean = await ProjectAsync(db, "clean audit only");
        Guid allowed = await ProjectAsync(db, "ingest allow");
        Guid promptBlock = await ProjectAsync(db, "prompt block");
        Guid untouched = await ProjectAsync(db, "nothing");

        // CsamHashMatcher: status 'match', quarantined true, the row's CreatedAt is the instant of the match.
        await db.InsertAsync("csam_scan_audits", new() { ["id"] = Guid.NewGuid(), ["project_id"] = deletedCsam, ["status"] = "match", ["quarantined"] = true, ["quarantine_succeeded"] = true, ["created_at"] = Old });
        await db.InsertAsync("csam_scan_audits", new() { ["id"] = Guid.NewGuid(), ["project_id"] = clean, ["status"] = "clean", ["quarantined"] = false, ["quarantine_succeeded"] = false, ["created_at"] = Old });
        // ContentModerationService/Gate: Surface and Action are enum names.
        await db.InsertAsync("content_moderation_decisions", new() { ["id"] = Guid.NewGuid(), ["project_id"] = moderationBlock, ["surface"] = "Ingest", ["action"] = "Block", ["created_at"] = Old });
        await db.InsertAsync("content_moderation_decisions", new() { ["id"] = Guid.NewGuid(), ["project_id"] = allowed, ["surface"] = "Ingest", ["action"] = "Allow", ["created_at"] = Old });
        await db.InsertAsync("content_moderation_decisions", new() { ["id"] = Guid.NewGuid(), ["project_id"] = promptBlock, ["surface"] = "GenerationPrompt", ["action"] = "Block", ["created_at"] = Old });
        await db.InsertAsync("project_files", new() { ["id"] = Guid.NewGuid(), ["project_id"] = quarantinedRow, ["csam_status"] = "Quarantined" });

        Dictionary<string, Guid> rows = [];
        async Task Row(string label, string category, string key, string? sourceKind = null)
        {
            rows[label] = Guid.NewGuid();
            await db.InsertAsync("project_files", new()
            {
                ["id"] = rows[label],
                ["project_id"] = untouched,
                ["category"] = category,
                ["storage_key"] = key,
                ["source_kind"] = sourceKind,
                ["uploaded_at"] = Old,
                ["original_file_name"] = label
            });
        }

        await Row("output", "outputFiles", "projects/p/outputFiles/a.mp4");
        await Row("copy", "userFiles", "projects/p/userFiles/c.mp4", "output_copy");
        await Row("take", "agentFiles", "projects/p/agentFiles/generated/t.mp4");
        await Row("upload", "userFiles", "projects/p/userFiles/u.mp4");

        await db.MigrateApiAsync();
        await db.MigrateEngineAsync();

        async Task<DateTime?> Produced(string label) => await db.ScalarAsync<DateTime?>("SELECT output_produced_at FROM project_files WHERE id = @id", ("id", rows[label]));
        (await Produced("output")).Should().Be(Old);
        (await Produced("copy")).Should().Be(Old);
        (await Produced("take")).Should().Be(Old);
        (await Produced("upload")).Should().BeNull("a plain upload is not an engine output");

        async Task<DateTime?> Marker(Guid id) => await db.ScalarAsync<DateTime?>("SELECT content_quarantined_at FROM projects WHERE id = @id", ("id", id));
        (await Marker(deletedCsam)).Should().Be(Old, "the match audit's own time, although the file row is gone");
        (await Marker(moderationBlock)).Should().Be(Old);
        (await Marker(quarantinedRow)).Should().NotBeNull();
        (await Marker(clean)).Should().BeNull();
        (await Marker(allowed)).Should().BeNull();
        (await Marker(promptBlock)).Should().BeNull("a blocked prompt quarantines no file");
        (await Marker(untouched)).Should().BeNull();
    }

    [PostgresFact]
    [Trait("Category", "Postgres")]
    public async Task The_output_marker_backfill_and_write_touch_only_projects_with_a_verdict()
    {
        await using Db db = await Db.CreateAsync();
        await db.MigrateApiAsync(BeforeMarkerMigration);
        Guid deletedCsam = await ProjectAsync(db, "csam audit, file gone");
        Guid quarantinedRow = await ProjectAsync(db, "quarantined file row");
        Guid clean = await ProjectAsync(db, "clean audit only");
        Guid untouched = await ProjectAsync(db, "nothing");
        await db.InsertAsync("csam_scan_audits", new() { ["id"] = Guid.NewGuid(), ["project_id"] = deletedCsam, ["status"] = "match", ["quarantined"] = true, ["quarantine_succeeded"] = true, ["created_at"] = Old });
        await db.InsertAsync("csam_scan_audits", new() { ["id"] = Guid.NewGuid(), ["project_id"] = clean, ["status"] = "clean", ["quarantined"] = false, ["quarantine_succeeded"] = false, ["created_at"] = Old });
        await db.InsertAsync("project_files", new() { ["id"] = Guid.NewGuid(), ["project_id"] = quarantinedRow, ["csam_status"] = "Quarantined" });

        await db.MigrateApiAsync();

        async Task<DateTime?> Hold(Guid id) => await db.ScalarAsync<DateTime?>($"SELECT csam_output_hold_at FROM projects WHERE id = '{id}'");
        (await Hold(deletedCsam)).Should().NotBeNull();
        (await Hold(quarantinedRow)).Should().NotBeNull();
        (await Hold(clean)).Should().BeNull();
        (await Hold(untouched)).Should().BeNull();

        DateTime first = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        await using InferenceApiDbContext api = new(db.Api);
        await ReelBolt.Inference.Api.Services.ProjectContentQuarantineMarker.StampCsamAsync(api, untouched, first, CancellationToken.None);
        (await Hold(untouched)).Should().Be(first);
        await ReelBolt.Inference.Api.Services.ProjectContentQuarantineMarker.StampCsamAsync(api, untouched, first.AddDays(-1), CancellationToken.None);
        (await Hold(untouched)).Should().Be(first, "the marker only moves forward");
    }

    // ------------------------------------------------------------------ the engine's filter and marker reads

    [PostgresFact]
    [Trait("Category", "Postgres")]
    public async Task The_engine_filter_and_marker_reads_run_on_the_real_schema_and_a_project_without_a_marker_is_untouched()
    {
        await using Db db = await Db.CreateAsync();
        await db.MigrateApiAsync();
        await db.MigrateEngineAsync();

        DateTime marker = DateTime.UtcNow.AddDays(-1);
        Guid marked = await ProjectAsync(db, "marked");
        Guid plain = await ProjectAsync(db, "plain");
        await using (NpgsqlConnection c = new(db.Connection))
        {
            await c.OpenAsync();
            await using NpgsqlCommand set = new("UPDATE projects SET content_quarantined_at = @m WHERE id = @id", c);
            set.Parameters.AddWithValue("m", marker);
            set.Parameters.AddWithValue("id", marked);
            await set.ExecuteNonQueryAsync();
        }

        Dictionary<string, Guid> ids = [];
        async Task File(string label, Guid project, string category, string key, DateTime uploaded, string? sourceKind = null, DateTime? produced = null)
        {
            Guid id = Guid.NewGuid();
            ids[label] = id;
            await db.InsertAsync("project_files", new()
            {
                ["id"] = id,
                ["project_id"] = project,
                ["category"] = category,
                ["storage_key"] = key.Replace("{p}", project.ToString()),
                ["uploaded_at"] = uploaded,
                ["source_kind"] = sourceKind,
                ["output_produced_at"] = produced,
                ["original_file_name"] = label
            });
        }

        DateTime old = marker.AddHours(-2), fresh = marker.AddHours(2);
        await File("outputOld", marked, "outputFiles", "projects/{p}/outputFiles/a/o.mp4", old, produced: old);
        await File("outputNew", marked, "outputFiles", "projects/{p}/outputFiles/b/o.mp4", fresh, produced: fresh);
        await File("movedOldOutput", marked, "userFiles", "projects/{p}/userFiles/m.mp4", fresh, produced: old);
        await File("copyOld", marked, "userFiles", "projects/{p}/userFiles/c.mp4", fresh, "output_copy", produced: old);
        await File("takeOld", marked, "agentFiles", "projects/{p}/agentFiles/generated/t.mp4", fresh, produced: old);
        await File("uploadOld", marked, "userFiles", "projects/{p}/userFiles/u.mp4", old);
        await File("plainOutputOld", plain, "outputFiles", "projects/{p}/outputFiles/a/o.mp4", old, produced: old);

        await using WorkflowEngineDbContext engine = new(db.Engine);
        HashSet<Guid> visible = [.. await engine.ProjectFiles.Select(f => f.Id).ToListAsync()];
        foreach ((string label, Guid id) in ids)
            visible.Contains(id).Should().Be(label is "outputNew" or "uploadOld" or "plainOutputOld", label);

        (await ProjectQuarantineState.ReadMarkerAsync(engine, marked, CancellationToken.None)).Should().BeCloseTo(marker, TimeSpan.FromSeconds(1));
        (await ProjectQuarantineState.ReadMarkerAsync(engine, plain, CancellationToken.None)).Should().BeNull();
        (await ProjectQuarantineState.ReadAsync(engine, marked, CancellationToken.None)).Should().NotBeNull();

        await using InferenceApiDbContext api = new(db.Api);
        (await api.Projects.IgnoreQueryFilters().Where(p => p.Id == marked).Select(p => p.ContentQuarantinedAt).SingleAsync())
            .Should().BeCloseTo(marker, TimeSpan.FromSeconds(1), "the API reads the same column the engine does");
    }

    // ------------------------------------------------------------------ the claim

    [PostgresFact]
    [Trait("Category", "Postgres")]
    public async Task The_claim_writes_the_first_start_once_and_a_reset_and_second_claim_keep_it()
    {
        await using Db db = await Db.CreateAsync();
        await db.MigrateApiAsync();
        await db.MigrateEngineAsync();
        Guid projectId = await ProjectAsync(db, "run");
        Guid definitionId = Guid.NewGuid(), executionId = Guid.NewGuid();
        await using WorkflowEngineDbContext engine = new(db.Engine);
        engine.WorkflowDefinitions.Add(new WorkflowDefinition { Id = definitionId, Name = "t", ProjectId = projectId });
        engine.WorkflowExecutions.Add(new WorkflowExecution
        {
            Id = executionId,
            WorkflowDefinitionId = definitionId,
            ProjectId = projectId,
            Status = ExecutionStatus.Queued
        });
        await engine.SaveChangesAsync();

        WorkflowExecutorService service = new(
            new FakeScopeFactory(engine), new NoOpPublisher(), NullLogger<WorkflowExecutorService>.Instance,
            Array.Empty<IStepExecutor>(), new RabbitMqHelper(new ConfigurationBuilder().Build()),
            Options.Create(new WorkflowHardeningOptions()), new ExecutionCancellationRegistry());

        async Task<(DateTime? First, DateTime? Started)> Read() => (
            await db.ScalarAsync<DateTime?>("SELECT first_started_at FROM workflow_executions WHERE id = @id", ("id", executionId)),
            await db.ScalarAsync<DateTime?>("SELECT started_at FROM workflow_executions WHERE id = @id", ("id", executionId)));

        await service.ExecuteAsync(executionId, "c1", CancellationToken.None);
        (DateTime? first, DateTime? started) = await Read();
        first.Should().NotBeNull();
        first.Should().Be(started, "the first claim stamps both with the same instant");

        // What a Retry does: back to Queued, start time cleared.
        await using (NpgsqlConnection c = new(db.Connection))
        {
            await c.OpenAsync();
            await using NpgsqlCommand reset = new("UPDATE workflow_executions SET status = 'Queued', started_at = NULL, completed_at = NULL, pending_resume = false WHERE id = @id", c);
            reset.Parameters.AddWithValue("id", executionId);
            await reset.ExecuteNonQueryAsync();
        }

        engine.ChangeTracker.Clear();
        await Task.Delay(50);
        await service.ExecuteAsync(executionId, "c2", CancellationToken.None);
        (DateTime? firstAgain, DateTime? startedAgain) = await Read();
        firstAgain.Should().Be(first, "a Retry and a second claim never touch the first start");
        startedAgain.Should().BeAfter(started!.Value);
    }

    private sealed class NoOpPublisher : IWorkflowEventPublisher
    {
        public Task PublishExecutionRunningAsync(WorkflowExecution execution, CancellationToken ct) => Task.CompletedTask;
        public Task PublishStepStartedAsync(WorkflowExecution execution, WorkflowStep step, WorkflowStepResult stepResult, string? inputPreview, CancellationToken ct) => Task.CompletedTask;
        public Task PublishStepCompletedAsync(WorkflowExecution execution, WorkflowStep step, WorkflowStepResult stepResult, StepExecutionResult stepExecutionResult, CancellationToken ct) => Task.CompletedTask;
        public Task PublishStepProgressAsync(WorkflowExecution execution, WorkflowStep step, WorkflowStepResult stepResult, string stage, int? percentComplete, CancellationToken ct, int? tokensUsedSoFar = null, int? inputTokensSoFar = null, int? outputTokensSoFar = null) => Task.CompletedTask;
        public Task PublishStepChatTurnAsync(WorkflowExecution execution, WorkflowStep step, WorkflowStepResult stepResult, int turnIndex, int? totalTurns, string speaker, string speakerRole, string text, IReadOnlyList<string> idsMentioned, CancellationToken ct) => Task.CompletedTask;
        public Task PublishStepDiagnosticsAsync(WorkflowExecution execution, WorkflowStep step, WorkflowStepResult stepResult, StepExecutionResult stepExecutionResult, CancellationToken ct) => Task.CompletedTask;
        public Task PublishExecutionCompletedAsync(WorkflowExecution execution, CancellationToken ct) => Task.CompletedTask;
        public Task PublishExecutionFailedAsync(WorkflowExecution execution, CancellationToken ct) => Task.CompletedTask;
    }
}
