using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Data;
using System;
using System.Linq;
using System.Threading.Tasks;
using ReelBolt.WorkflowEngine.Tests.Infrastructure;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Data;

/// <summary>
/// D3 (runner schema) coverage: enum encoding and constraints against Sqlite, and the real
/// migration chain of BOTH contexts against PostgreSQL when <c>REELBOLT_TEST_PG</c> is set (same
/// convention as <see cref="OrganizationBackfillTests"/>).
/// </summary>
[Collection("Postgres")]
[Trait("Category", "Postgres")]
public class RunnerSchemaTests
{
    [Fact]
    public void RunnerEnums_AreAppendOnly_InTheDocumentedOrder()
    {
        Enum.GetNames<RunnerKind>().Should().Equal("Desktop", "CloudPool");
        Enum.GetNames<RunnerJobType>().Should().Equal("RemotionWorkspaceSession", "VideoCompile", "VideoAnalyzeExtract");
        Enum.GetNames<RunnerJobStatus>().Should().Equal(
            "Queued", "Assigned", "Running", "Succeeded", "Failed", "Cancelled", "Expired");
        Enum.GetNames<RunnerJobTarget>().Should().Equal("CloudPool", "OrgRunner");
    }

    [Fact]
    public void RunnerDevice_FingerprintIsUnique_AndDesktopNeedsAnOrg()
    {
        using SqliteConnection connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using InferenceApiDbContext db = new InferenceApiDbContext(
            new DbContextOptionsBuilder<InferenceApiDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();

        Organization org = new Organization { Id = Guid.NewGuid(), Name = "o", Slug = "o", Kind = ReelBolt.Shared.Tenancy.OrganizationKind.Team };
        db.Organizations.Add(org);
        db.RunnerDevices.Add(new RunnerDevice { Id = Guid.NewGuid(), OrganizationId = org.Id, Name = "a", PublicKeyFingerprint = "fp" });
        // Pool workers carry no org and no key; several may coexist (NULL fingerprints are distinct).
        db.RunnerDevices.Add(new RunnerDevice { Id = Guid.NewGuid(), Kind = RunnerKind.CloudPool, Name = "p1" });
        db.RunnerDevices.Add(new RunnerDevice { Id = Guid.NewGuid(), Kind = RunnerKind.CloudPool, Name = "p2" });
        db.SaveChanges();

        db.Database.SqlQuery<string>($"SELECT kind AS \"Value\" FROM runner_devices ORDER BY name").ToList()
            .Should().Equal("Desktop", "CloudPool", "CloudPool");

        db.RunnerDevices.Add(new RunnerDevice { Id = Guid.NewGuid(), OrganizationId = org.Id, Name = "dup", PublicKeyFingerprint = "fp" });
        Action dup = () => db.SaveChanges();
        dup.Should().Throw<DbUpdateException>();

        db.ChangeTracker.Clear();
        db.RunnerDevices.Add(new RunnerDevice { Id = Guid.NewGuid(), Kind = RunnerKind.Desktop, Name = "orphan" });
        Action noOrg = () => db.SaveChanges();
        noOrg.Should().Throw<DbUpdateException>();
    }

    [Fact]
    public async Task MigrationsApply_ToAFreshPostgres_AndEnforceTheRunnerConstraints()
    {
        string? baseConnection = PostgresTestEnvironment.BaseConnectionString();
        if (string.IsNullOrWhiteSpace(baseConnection))
        {
            return;
        }

        string dbName = "reelbolt_d3_test_" + Guid.NewGuid().ToString("N")[..12];
        await using NpgsqlDataSource admin = PostgresTestEnvironment.CreateAdminDataSource(baseConnection);
        await using (NpgsqlCommand create = admin.CreateCommand($"CREATE DATABASE {dbName}"))
        {
            await create.ExecuteNonQueryAsync();
        }

        NpgsqlConnectionStringBuilder csb = new NpgsqlConnectionStringBuilder(PostgresTestEnvironment.ForDatabase(baseConnection, dbName));
        try
        {
            // Compose startup order: the Inference API migrates first, then the engine.
            await using (InferenceApiDbContext api = new InferenceApiDbContext(
                new DbContextOptionsBuilder<InferenceApiDbContext>().UseNpgsql(csb.ConnectionString).Options))
            {
                await api.GetService<IMigrator>().MigrateAsync();
            }

            await using WorkflowEngineDbContext engine = new WorkflowEngineDbContext(
                new DbContextOptionsBuilder<WorkflowEngineDbContext>().UseNpgsql(csb.ConnectionString).Options);
            await engine.GetService<IMigrator>().MigrateAsync();

            // Neither context has a pending model change.
            (await engine.Database.GetPendingMigrationsAsync()).Should().BeEmpty();

            await using NpgsqlConnection c = new NpgsqlConnection(csb.ConnectionString);
            await c.OpenAsync();

            async Task<long> Count(string sql)
            {
                await using NpgsqlCommand cmd = new NpgsqlCommand(sql, c);
                return (long)(await cmd.ExecuteScalarAsync())!;
            }

            (await Count("SELECT count(*) FROM information_schema.tables WHERE table_name IN ('runner_devices','runner_jobs','runner_sessions')"))
                .Should().Be(3);
            (await Count("SELECT count(*) FROM information_schema.columns WHERE table_name='runner_devices' AND column_name IN " +
                "('paired_by_user_id','os','arch','public_key_fingerprint','runner_version','revoked_by_user_id','organization_id')"))
                .Should().Be(7);

            // A real job + session round trip, then the two uniqueness rules.
            Guid exec = Guid.NewGuid();
            await using (NpgsqlCommand seed = new NpgsqlCommand(
                "INSERT INTO runner_devices (id,kind,name,created_at) VALUES (gen_random_uuid(),'CloudPool','pool-1',now())", c))
            {
                await seed.ExecuteNonQueryAsync();
            }

            Func<string, Task> run = async sql =>
            {
                await using NpgsqlCommand cmd = new NpgsqlCommand(sql, c);
                await cmd.ExecuteNonQueryAsync();
            };

            // runner_jobs.execution_id is a real FK, so a job needs an execution; seed the chain.
            Guid user = Guid.NewGuid(), org = Guid.NewGuid(), project = Guid.NewGuid(), wf = Guid.NewGuid();
            await run($"INSERT INTO application_users (id,email,display_name,created_at,must_change_password,is_admin) VALUES ('{user}','d3@t.local','d3',now(),false,false)");
            await run($"INSERT INTO organizations (id,name,slug,kind,created_at,updated_at,compute_preference) VALUES ('{org}','o','o-{org:N}','Team',now(),now(),'Auto')");
            await run($"INSERT INTO projects (id,name,owner_id,organization_id,status,created_at,updated_at) VALUES ('{project}','p','{user}','{org}','Draft',now(),now())");
            await run($"INSERT INTO workflow_definitions (id,name,project_id,created_at,updated_at) VALUES ('{wf}','w','{project}',now(),now())");
            await run($"INSERT INTO workflow_executions (id,workflow_definition_id,project_id,organization_id,status,iteration_count,correlation_id) VALUES ('{exec}','{wf}','{project}','{org}','Queued',0,'c')");

            Guid job = Guid.NewGuid();
            await run($"INSERT INTO runner_jobs (id,idempotency_key,organization_id,execution_id,type,target,status,priority,attempt,max_attempts,lease_epoch,created_at,updated_at) VALUES ('{job}','k1','{org}','{exec}','VideoCompile','CloudPool','Queued',0,0,2,0,now(),now())");
            await run($"INSERT INTO runner_sessions (id,job_id,execution_id,runner_id,opened_at) VALUES (gen_random_uuid(),'{job}','{exec}',gen_random_uuid(),now())");

            Func<Task> dupKey = () => run($"INSERT INTO runner_jobs (id,idempotency_key,organization_id,execution_id,type,target,status,priority,attempt,max_attempts,lease_epoch,created_at,updated_at) VALUES (gen_random_uuid(),'k1','{org}','{exec}','VideoCompile','CloudPool','Queued',0,0,2,0,now(),now())");
            (await dupKey.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23505");

            // Deleting the execution cascades through jobs to sessions.
            await run($"DELETE FROM workflow_executions WHERE id='{exec}'");
            (await Count("SELECT count(*) FROM runner_jobs")).Should().Be(0);
            (await Count("SELECT count(*) FROM runner_sessions")).Should().Be(0);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using NpgsqlCommand drop = admin.CreateCommand($"DROP DATABASE IF EXISTS {dbName} WITH (FORCE)");
            await drop.ExecuteNonQueryAsync();
        }
    }
}
