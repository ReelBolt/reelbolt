#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using Npgsql;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Shared.Auth;
using ReelBolt.WorkflowEngine.Tests.Infrastructure;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Project tags and folders on a real Postgres (skipped unless <c>REELBOLT_TEST_PG</c> is set): the migration chain,
/// the filtered unique indexes and FKs, and the per-organization advisory lock under concurrent creates.
/// </summary>
[Collection("Postgres")]
public class ProjectOrganizationPostgresTests
{
    private static readonly DateTime Old = new(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);

    private sealed class Db(string baseConnection, string name, string connection) : IAsyncDisposable
    {
        public string Connection { get; } = connection;
        public DbContextOptions<InferenceApiDbContext> Api { get; } =
            new DbContextOptionsBuilder<InferenceApiDbContext>().UseNpgsql(connection).Options;

        public static async Task<Db> CreateAsync()
        {
            string baseConnection = PostgresTestEnvironment.BaseConnectionString()!;
            string name = "reelbolt_projorg_test_" + Guid.NewGuid().ToString("N")[..12];
            await using NpgsqlDataSource admin = PostgresTestEnvironment.CreateAdminDataSource(baseConnection);
            await using (NpgsqlCommand create = admin.CreateCommand($"CREATE DATABASE {name}"))
                await create.ExecuteNonQueryAsync();
            Db db = new(baseConnection, name, PostgresTestEnvironment.ForDatabase(baseConnection, name, 30));
            await using InferenceApiDbContext ctx = new(db.Api);
            await ctx.Database.MigrateAsync();
            return db;
        }

        public InferenceApiDbContext Context() => new(Api);

        public async Task<int> ExecAsync(string sql, params (string Name, object Value)[] parameters)
        {
            await using NpgsqlConnection conn = new(Connection);
            await conn.OpenAsync();
            await using NpgsqlCommand cmd = new(sql, conn);
            foreach ((string n, object v) in parameters) cmd.Parameters.AddWithValue(n, v);
            return await cmd.ExecuteNonQueryAsync();
        }

        public async Task<T?> ScalarAsync<T>(string sql, params (string Name, object Value)[] parameters)
        {
            await using NpgsqlConnection conn = new(Connection);
            await conn.OpenAsync();
            await using NpgsqlCommand cmd = new(sql, conn);
            foreach ((string n, object v) in parameters) cmd.Parameters.AddWithValue(n, v);
            object? result = await cmd.ExecuteScalarAsync();
            return result is null or DBNull ? default : (T)result;
        }

        /// <summary>Inserts one row giving only the columns listed; other NOT NULL columns without a default get a neutral value. FKs are not checked.</summary>
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
                        _ => "gen_random_uuid()::text"
                    };
                }
            }

            List<string> names = [.. values.Keys.Select(k => $"\"{k}\""), .. filler.Keys.Select(k => $"\"{k}\"")];
            List<string> exprs = [.. values.Keys.Select((_, i) => $"@p{i}"), .. filler.Values];
            await using NpgsqlCommand insert = new($"INSERT INTO \"{table}\" ({string.Join(",", names)}) VALUES ({string.Join(",", exprs)})", conn);
            int index = 0;
            foreach (object? value in values.Values) insert.Parameters.AddWithValue($"p{index++}", value ?? DBNull.Value);
            await insert.ExecuteNonQueryAsync();
        }

        public async ValueTask DisposeAsync()
        {
            NpgsqlConnection.ClearAllPools();
            await using NpgsqlDataSource cleanup = PostgresTestEnvironment.CreateAdminDataSource(baseConnection);
            await using NpgsqlCommand drop = cleanup.CreateCommand($"DROP DATABASE IF EXISTS {name} WITH (FORCE)");
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static async Task<Guid> OrgAsync(Db db)
    {
        Guid id = Guid.NewGuid();
        await db.InsertAsync("organizations", new() { ["id"] = id });
        return id;
    }

    private static async Task<Guid> ProjectAsync(Db db, Guid org, Guid? folder = null)
    {
        Guid id = Guid.NewGuid();
        await db.InsertAsync("projects", new()
        {
            ["id"] = id,
            ["organization_id"] = org,
            ["folder_id"] = folder,
            ["updated_at"] = Old,
            ["created_at"] = Old
        });
        return id;
    }

    private static async Task<Guid> TagAsync(Db db, Guid org, string name)
    {
        Guid id = Guid.NewGuid();
        await db.ExecAsync(
            "INSERT INTO project_tags (id, organization_id, name, name_key, color, created_at) VALUES (@i, @o, @n, lower(@n), 'gray', now())",
            ("i", id), ("o", org), ("n", name));
        return id;
    }

    private static Task SeedTagsAsync(Db db, Guid org, int count) => db.ExecAsync(
        "INSERT INTO project_tags (id, organization_id, name, name_key, color, created_at) SELECT gen_random_uuid(), @o, 'seed' || g, 'seed' || g, 'gray', now() FROM generate_series(1, @c) g",
        ("o", org), ("c", count));

    private static Task SeedFoldersAsync(Db db, Guid org, int count) => db.ExecAsync(
        "INSERT INTO project_folders (id, organization_id, name, name_key, created_at) SELECT gen_random_uuid(), @o, 'seed' || g, 'seed' || g, now() FROM generate_series(1, @c) g",
        ("o", org), ("c", count));

    private static (ICurrentUser, IProjectAccessService) Auth(Guid org)
    {
        Mock<ICurrentUser> user = new();
        user.SetupGet(u => u.OrganizationId).Returns(org);
        Mock<IProjectAccessService> access = new();
        access.Setup(a => a.AuthorizeOrganization(It.IsAny<ProjectPermission>())).Returns(ProjectAccessOutcome.Found);
        return (user.Object, access.Object);
    }

    private static async Task<int> CreateTagAsync(Db db, Guid org, string name)
    {
        await using InferenceApiDbContext ctx = db.Context();
        (ICurrentUser u, IProjectAccessService a) = Auth(org);
        ActionResult<ProjectTagResponse> r = await new ProjectTagsController(ctx, u, a).Create(new ProjectTagRequest(name, null), CancellationToken.None);
        return StatusOf(r.Result);
    }

    private static async Task<int> CreateFolderAsync(Db db, Guid org, string name)
    {
        await using InferenceApiDbContext ctx = db.Context();
        (ICurrentUser u, IProjectAccessService a) = Auth(org);
        ActionResult<ProjectFolderResponse> r = await new ProjectFoldersController(ctx, u, a).Create(new ProjectFolderRequest(name), CancellationToken.None);
        return StatusOf(r.Result);
    }

    private static int StatusOf(IActionResult? result) => result switch
    {
        ObjectResult o => o.StatusCode ?? 200,
        StatusCodeResult s => s.StatusCode,
        _ => throw new InvalidOperationException($"Unexpected result {result?.GetType().Name ?? "null"}")
    };

    [PostgresFact]
    [Trait("Category", "Postgres")]
    public async Task The_migration_chain_creates_the_tables_filtered_unique_indexes_and_foreign_keys()
    {
        await using Db db = await Db.CreateAsync();

        foreach (string table in new[] { "project_tags", "project_tag_links", "project_folders" })
            (await db.ScalarAsync<string>("SELECT to_regclass(@t)::text", ("t", table))).Should().Be(table);

        string? parentNull = await db.ScalarAsync<string>(
            "SELECT indexdef FROM pg_indexes WHERE indexname = 'IX_project_folders_organization_id_name_key'");
        parentNull.Should().Contain("UNIQUE").And.Contain("parent_folder_id IS NULL");
        string? parentSet = await db.ScalarAsync<string>(
            "SELECT indexdef FROM pg_indexes WHERE indexname = 'IX_project_folders_organization_id_parent_folder_id_name_key'");
        parentSet.Should().Contain("UNIQUE").And.Contain("parent_folder_id IS NOT NULL");
        string? tagKey = await db.ScalarAsync<string>(
            "SELECT indexdef FROM pg_indexes WHERE indexname = 'IX_project_tags_organization_id_name_key'");
        tagKey.Should().Contain("UNIQUE");

        (await db.ScalarAsync<string>(
            "SELECT confdeltype::text FROM pg_constraint WHERE conname = 'fk_projects__project_folders_folder_id'")).Should().Be("n");
        (await db.ScalarAsync<long>(
            "SELECT count(*) FROM pg_constraint WHERE contype = 'f' AND confdeltype = 'c' AND conrelid = 'project_tag_links'::regclass")).Should().Be(2);

        // Behaviour of the filtered indexes: a NULL parent still counts as one value.
        Guid org = await OrgAsync(db);
        await db.ExecAsync("INSERT INTO project_folders (id, organization_id, name, name_key, created_at) VALUES (gen_random_uuid(), @o, 'A', 'a', now())", ("o", org));
        Func<Task> dup = () => db.ExecAsync("INSERT INTO project_folders (id, organization_id, name, name_key, created_at) VALUES (gen_random_uuid(), @o, 'a', 'a', now())", ("o", org));
        (await dup.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
    }

    [PostgresFact]
    [Trait("Category", "Postgres")]
    public async Task Deleting_a_folder_nulls_projects_folder_id_and_deleting_a_project_cascades_its_tag_links()
    {
        await using Db db = await Db.CreateAsync();
        Guid org = await OrgAsync(db);
        Guid folder = Guid.NewGuid();
        await db.ExecAsync("INSERT INTO project_folders (id, organization_id, name, name_key, created_at) VALUES (@i, @o, 'F', 'f', now())", ("i", folder), ("o", org));
        Guid inFolder = await ProjectAsync(db, org, folder);
        Guid tag = await TagAsync(db, org, "T");
        Guid tagged = await ProjectAsync(db, org);
        await db.ExecAsync("INSERT INTO project_tag_links (project_id, tag_id) VALUES (@p, @t)", ("p", tagged), ("t", tag));

        await db.ExecAsync("DELETE FROM project_folders WHERE id = @i", ("i", folder));
        (await db.ScalarAsync<Guid?>("SELECT folder_id FROM projects WHERE id = @p", ("p", inFolder))).Should().BeNull();
        (await db.ScalarAsync<long>("SELECT count(*) FROM projects WHERE id = @p", ("p", inFolder))).Should().Be(1);

        await db.ExecAsync("DELETE FROM projects WHERE id = @p", ("p", tagged));
        (await db.ScalarAsync<long>("SELECT count(*) FROM project_tag_links WHERE tag_id = @t", ("t", tag))).Should().Be(0);
        (await db.ScalarAsync<long>("SELECT count(*) FROM project_tags WHERE id = @t", ("t", tag))).Should().Be(1);
    }

    [PostgresFact]
    [Trait("Category", "Postgres")]
    public async Task Concurrent_creates_of_one_tag_name_give_exactly_one_201_and_the_rest_409()
    {
        await using Db db = await Db.CreateAsync();
        Guid org = await OrgAsync(db);

        string[] names = ["Client X", "client x", "CLIENT X", "Client X", "client X", "Client X", "client x", "CLIENT x"];
        int[] statuses = await Task.WhenAll(names.Select(n => Task.Run(() => CreateTagAsync(db, org, n))));

        statuses.Count(s => s == 201).Should().Be(1);
        statuses.Count(s => s == 409).Should().Be(names.Length - 1);
        (await db.ScalarAsync<long>("SELECT count(*) FROM project_tags WHERE organization_id = @o", ("o", org))).Should().Be(1);
    }

    [PostgresFact]
    [Trait("Category", "Postgres")]
    public async Task Concurrent_creates_of_one_folder_name_give_exactly_one_201_and_the_rest_409()
    {
        await using Db db = await Db.CreateAsync();
        Guid org = await OrgAsync(db);

        string[] names = ["Q4", "q4", "Q4", "q4", "Q4", "q4"];
        int[] statuses = await Task.WhenAll(names.Select(n => Task.Run(() => CreateFolderAsync(db, org, n))));

        statuses.Count(s => s == 201).Should().Be(1);
        statuses.Count(s => s == 409).Should().Be(names.Length - 1);
    }

    [PostgresFact]
    [Trait("Category", "Postgres")]
    public async Task Concurrent_creates_racing_the_tag_cap_let_exactly_one_through()
    {
        await using Db db = await Db.CreateAsync();
        Guid org = await OrgAsync(db);
        await SeedTagsAsync(db, org, 199);

        int[] statuses = await Task.WhenAll(Enumerable.Range(0, 5).Select(i => Task.Run(() => CreateTagAsync(db, org, "race" + i))));

        statuses.Count(s => s == 201).Should().Be(1);
        statuses.Count(s => s == 409).Should().Be(4);
        (await db.ScalarAsync<long>("SELECT count(*) FROM project_tags WHERE organization_id = @o", ("o", org))).Should().Be(200);
    }

    [PostgresFact]
    [Trait("Category", "Postgres")]
    public async Task Concurrent_creates_racing_the_folder_cap_let_exactly_one_through()
    {
        await using Db db = await Db.CreateAsync();
        Guid org = await OrgAsync(db);
        await SeedFoldersAsync(db, org, 499);

        int[] statuses = await Task.WhenAll(Enumerable.Range(0, 5).Select(i => Task.Run(() => CreateFolderAsync(db, org, "race" + i))));

        statuses.Count(s => s == 201).Should().Be(1);
        statuses.Count(s => s == 409).Should().Be(4);
        (await db.ScalarAsync<long>("SELECT count(*) FROM project_folders WHERE organization_id = @o", ("o", org))).Should().Be(500);
    }

    [PostgresFact]
    [Trait("Category", "Postgres")]
    public async Task A_tag_rename_that_hits_a_duplicate_name_is_409_and_leaves_projects_updated_at_untouched()
    {
        await using Db db = await Db.CreateAsync();
        Guid org = await OrgAsync(db);
        Guid a = await TagAsync(db, org, "Alpha");
        await TagAsync(db, org, "Beta");
        Guid p1 = await ProjectAsync(db, org);
        Guid p2 = await ProjectAsync(db, org);
        foreach (Guid p in new[] { p1, p2 })
            await db.ExecAsync("INSERT INTO project_tag_links (project_id, tag_id) VALUES (@p, @t)", ("p", p), ("t", a));

        await using (InferenceApiDbContext ctx = db.Context())
        {
            (ICurrentUser u, IProjectAccessService acc) = Auth(org);
            ActionResult<ProjectTagResponse> r = await new ProjectTagsController(ctx, u, acc).Update(a, new ProjectTagRequest("beta", null), CancellationToken.None);
            StatusOf(r.Result).Should().Be(409);
        }

        (await db.ScalarAsync<long>("SELECT count(*) FROM projects WHERE updated_at <> @old", ("old", Old))).Should().Be(0);
        (await db.ScalarAsync<string>("SELECT name FROM project_tags WHERE id = @a", ("a", a))).Should().Be("Alpha");

        // A successful rename does move them.
        await using (InferenceApiDbContext ctx = db.Context())
        {
            (ICurrentUser u, IProjectAccessService acc) = Auth(org);
            ActionResult<ProjectTagResponse> r = await new ProjectTagsController(ctx, u, acc).Update(a, new ProjectTagRequest("Gamma", null), CancellationToken.None);
            StatusOf(r.Result).Should().Be(200);
        }
        (await db.ScalarAsync<long>("SELECT count(*) FROM projects WHERE updated_at > @old", ("old", Old))).Should().Be(2);
    }

    [PostgresFact]
    [Trait("Category", "Postgres")]
    public async Task Two_organizations_can_each_hold_the_same_tag_and_folder_name()
    {
        await using Db db = await Db.CreateAsync();
        Guid orgA = await OrgAsync(db);
        Guid orgB = await OrgAsync(db);

        (await CreateTagAsync(db, orgA, "Shared")).Should().Be(201);
        (await CreateTagAsync(db, orgB, "shared")).Should().Be(201);
        (await CreateTagAsync(db, orgA, "SHARED")).Should().Be(409);
        (await CreateFolderAsync(db, orgA, "Shared")).Should().Be(201);
        (await CreateFolderAsync(db, orgB, "Shared")).Should().Be(201);
        (await CreateFolderAsync(db, orgB, "shared")).Should().Be(409);
    }
}
