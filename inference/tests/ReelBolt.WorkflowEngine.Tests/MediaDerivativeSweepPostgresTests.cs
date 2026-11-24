#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using MassTransit;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Npgsql;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Media;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.IntegrationEvents;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Tests.Infrastructure;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The derivative sweep's candidate query on a real schema (skipped unless <c>REELBOLT_TEST_PG</c> is set). The query
/// compares a stored time plus the clock margin (a date and an interval) inside SQL, which the in-memory provider
/// evaluates in process and so cannot show translates: an earlier output of a project with a stored marker must not
/// take a slot of the oldest-first batch, and an upload or a later output must.
/// </summary>
[Collection("Postgres")]
public class MediaDerivativeSweepPostgresTests
{
    private static readonly DateTime Marker = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    private sealed class Db(string baseConnection, string name, string connection) : IAsyncDisposable
    {
        public string Connection { get; } = connection;
        public DbContextOptions<InferenceApiDbContext> Api { get; } = new DbContextOptionsBuilder<InferenceApiDbContext>().UseNpgsql(connection).Options;
        public DbContextOptions<WorkflowEngineDbContext> Engine { get; } = new DbContextOptionsBuilder<WorkflowEngineDbContext>().UseNpgsql(connection).Options;

        public static async Task<Db> CreateAsync()
        {
            string baseConnection = PostgresTestEnvironment.BaseConnectionString()!;
            string name = "reelbolt_sweep_test_" + Guid.NewGuid().ToString("N")[..12];
            await using NpgsqlDataSource admin = PostgresTestEnvironment.CreateAdminDataSource(baseConnection);
            await using (NpgsqlCommand create = admin.CreateCommand($"CREATE DATABASE {name}"))
                await create.ExecuteNonQueryAsync();
            return new Db(baseConnection, name, PostgresTestEnvironment.ForDatabase(baseConnection, name));
        }

        public async Task MigrateAsync()
        {
            await using (InferenceApiDbContext api = new(Api))
                await api.GetService<IMigrator>().MigrateAsync();
            await using WorkflowEngineDbContext engine = new(Engine);
            await engine.GetService<IMigrator>().MigrateAsync();
        }

        /// <summary>Inserts one row with only the columns given; every other NOT NULL column without a default gets a neutral value by type. Foreign keys are not checked.</summary>
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
                    if (values.ContainsKey(column)) continue;
                    filler[column] = r.GetString(1) switch
                    {
                        "uuid" => "gen_random_uuid()",
                        "boolean" => "false",
                        "integer" or "bigint" or "smallint" or "numeric" or "double precision" => "0",
                        "timestamp with time zone" or "timestamp without time zone" => "now()",
                        "jsonb" or "json" => "'{}'",
                        _ => "''",
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

        public async ValueTask DisposeAsync()
        {
            await using NpgsqlDataSource cleanup = PostgresTestEnvironment.CreateAdminDataSource(baseConnection);
            await using NpgsqlCommand drop = cleanup.CreateCommand($"DROP DATABASE IF EXISTS {name} WITH (FORCE)");
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static async Task<Guid> ProjectAsync(Db db, DateTime? hold)
    {
        Guid id = Guid.NewGuid();
        await db.InsertAsync("projects", new() { ["id"] = id, ["name"] = "p" + id.ToString("N")[..6], ["csam_output_hold_at"] = hold });
        return id;
    }

    private static async Task<string> FileAsync(Db db, Guid project, string name, DateTime uploadedAt, DateTime? producedAt)
    {
        string key = $"projects/{project}/{(producedAt is null ? "userFiles" : "outputFiles")}/{name}.mp4";
        await db.InsertAsync("project_files", new()
        {
            ["id"] = Guid.NewGuid(),
            ["project_id"] = project,
            ["category"] = producedAt is null ? "userFiles" : "outputFiles",
            ["storage_key"] = key,
            ["mime_type"] = "video/mp4",
            ["original_file_name"] = name + ".mp4",
            ["uploaded_at"] = uploadedAt,
            ["output_produced_at"] = producedAt,
        });
        return key;
    }

    private static async Task<string> RenderAsync(Db db, Guid project, string name, DateTime startedAt, DateTime executedAt)
    {
        Guid execution = Guid.NewGuid();
        string key = $"projects/{project}/outputFiles/{name}.mp4";
        await db.InsertAsync("workflow_executions", new()
        {
            ["id"] = execution,
            ["workflow_definition_id"] = Guid.NewGuid(),
            ["project_id"] = project,
            ["status"] = "Passed",
            ["started_at"] = startedAt,
            ["first_started_at"] = startedAt,
        });
        await db.InsertAsync("workflow_step_results", new()
        {
            ["id"] = Guid.NewGuid(),
            ["workflow_execution_id"] = execution,
            ["workflow_step_id"] = Guid.NewGuid(),
            ["status"] = "Completed",
            ["output_storage_key"] = key,
            ["executed_at"] = executedAt,
            ["completed_at"] = executedAt,
        });
        return key;
    }

    [PostgresFact]
    [Trait("Category", "Postgres")]
    public async Task An_earlier_output_of_a_project_with_a_stored_marker_takes_no_slot_of_the_batch_and_an_upload_or_later_output_does()
    {
        await using Db db = await Db.CreateAsync();
        await db.MigrateAsync();

        Guid held = await ProjectAsync(db, Marker);
        Guid open = await ProjectAsync(db, null);

        // Older than everything else, so they would fill the oldest-first batch if the query did not leave them out.
        string earlierFile = await FileAsync(db, held, "earlier-file", Marker.AddHours(-3), Marker.AddHours(-3));
        string earlierRender = await RenderAsync(db, held, "earlier-render", Marker.AddHours(-4), Marker.AddHours(-3));
        // Inside the clock margin of the marker: still not served.
        string nearFile = await FileAsync(db, held, "near-file", Marker.AddMinutes(2), Marker.AddMinutes(2));

        string upload = await FileAsync(db, held, "upload", Marker.AddDays(-5), null);
        string laterFile = await FileAsync(db, held, "later-file", Marker.AddDays(2), Marker.AddDays(2));
        string laterRender = await RenderAsync(db, held, "later-render", Marker.AddDays(2), Marker.AddDays(2));
        string openFile = await FileAsync(db, open, "open-file", Marker.AddHours(-2), Marker.AddHours(-2));
        string openRender = await RenderAsync(db, open, "open-render", Marker.AddHours(-2), Marker.AddHours(-2));

        List<string> requested = [];
        Mock<IPublishEndpoint> publish = new();
        publish.Setup(p => p.Publish(It.IsAny<MediaDerivativesRequested>(), It.IsAny<CancellationToken>()))
            .Callback<MediaDerivativesRequested, CancellationToken>((m, _) => requested.Add(m.SourceStorageKey))
            .Returns(Task.CompletedTask);
        Mock<IFileStorageService> storage = new();
        storage.Setup(s => s.GetObjectETagAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("\"etag\"");

        await using InferenceApiDbContext context = new(db.Api);
        MediaDerivativeCoverageService service = new(
            context, storage.Object, publish.Object, new TenantContext(new HttpContextAccessor()), NullLogger<MediaDerivativeCoverageService>.Instance);

        // Exactly as many slots as there are sources the platform serves.
        await service.SweepOnceAsync("test", batchSize: 5, CancellationToken.None);

        requested.Should().BeEquivalentTo([upload, laterFile, laterRender, openFile, openRender]);
        requested.Should().NotContain([earlierFile, earlierRender, nearFile]);
    }
}
