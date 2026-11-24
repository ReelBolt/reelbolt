using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;
using System;
using System.Linq;
using System.Threading.Tasks;
using ReelBolt.WorkflowEngine.Tests.Infrastructure;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Data;

/// <summary>
/// E4a (device pairing schema) coverage: the append-only status enum, the partial
/// unique index that makes a short user code unique among LIVE requests only, and —
/// when <c>REELBOLT_TEST_PG</c> is set — the real migration chain against PostgreSQL.
/// </summary>
[Collection("Postgres")]
[Trait("Category", "Postgres")]
public class RunnerPairingSchemaTests
{
    private static RunnerPairingRequest Pending(string userCode, string fingerprint) => new()
    {
        Id = Guid.NewGuid(),
        DeviceCodeHash = Guid.NewGuid().ToString("N"),
        UserCode = userCode,
        PublicKey = "AAAA",
        PublicKeyFingerprint = fingerprint,
        Name = "box",
        CreatedAt = DateTime.UtcNow,
        ExpiresAt = DateTime.UtcNow.AddMinutes(10),
        Status = RunnerPairingStatus.Pending,
    };

    [Fact]
    public void RunnerPairingStatus_IsAppendOnly_InTheDocumentedOrder()
    {
        Enum.GetNames<RunnerPairingStatus>().Should().Equal("Pending", "Approved", "Denied", "Expired");
    }

    [Fact]
    public void UserCodeIsUniqueAmongLiveRequestsOnly()
    {
        using SqliteConnection connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using InferenceApiDbContext db = new InferenceApiDbContext(
            new DbContextOptionsBuilder<InferenceApiDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();

        var index = db.Model.FindEntityType(typeof(RunnerPairingRequest))!
            .GetIndexes()
            .Single(i => i.Properties.Count == 1 && i.Properties[0].Name == nameof(RunnerPairingRequest.UserCode));
        index.IsUnique.Should().BeTrue();
        // The filter is what recycles a code the moment its request settles.
        index.GetFilter().Should().Be("status = 'Pending'");

        db.RunnerPairingRequests.Add(Pending("ABCDEFGH", "fp1"));
        db.RunnerPairingRequests.Add(Pending("ABCDEFGJ", "fp2"));
        db.SaveChanges();

        // Two live requests cannot share a code...
        db.RunnerPairingRequests.Add(Pending("ABCDEFGH", "fp3"));
        Action collide = () => db.SaveChanges();
        collide.Should().Throw<DbUpdateException>();

        // ...but the code is free again once the first one leaves Pending.
        db.ChangeTracker.Clear();
        RunnerPairingRequest first = db.RunnerPairingRequests.Single(p => p.UserCode == "ABCDEFGH");
        first.Status = RunnerPairingStatus.Denied;
        db.SaveChanges();

        db.RunnerPairingRequests.Add(Pending("ABCDEFGH", "fp4"));
        db.SaveChanges();
        db.RunnerPairingRequests.Count(p => p.UserCode == "ABCDEFGH").Should().Be(2);
    }

    [Fact]
    public void DeviceCodeHashIsUnique_AndTheStatusIsStoredAsItsName()
    {
        using SqliteConnection connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using InferenceApiDbContext db = new InferenceApiDbContext(
            new DbContextOptionsBuilder<InferenceApiDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();

        RunnerPairingRequest a = Pending("ABCDEFGH", "fp1");
        db.RunnerPairingRequests.Add(a);
        db.SaveChanges();

        db.Database.SqlQueryRaw<string>("SELECT status AS \"Value\" FROM runner_pairing_requests").ToList()
            .Should().Equal("Pending");

        RunnerPairingRequest b = Pending("ABCDEFGJ", "fp2");
        b.DeviceCodeHash = a.DeviceCodeHash;
        db.RunnerPairingRequests.Add(b);
        Action dup = () => db.SaveChanges();
        dup.Should().Throw<DbUpdateException>();
    }

    [Fact]
    public async Task MigrationsApply_ToAFreshPostgres_AndCreateThePairingTable()
    {
        string? baseConnection = PostgresTestEnvironment.BaseConnectionString();
        if (string.IsNullOrWhiteSpace(baseConnection))
        {
            return;
        }

        string dbName = "reelbolt_e4a_test_" + Guid.NewGuid().ToString("N")[..12];
        await using NpgsqlDataSource admin = PostgresTestEnvironment.CreateAdminDataSource(baseConnection);
        await using (NpgsqlCommand create = admin.CreateCommand($"CREATE DATABASE {dbName}"))
        {
            await create.ExecuteNonQueryAsync();
        }

        NpgsqlConnectionStringBuilder csb = new NpgsqlConnectionStringBuilder(PostgresTestEnvironment.ForDatabase(baseConnection, dbName));
        try
        {
            await using InferenceApiDbContext api = new InferenceApiDbContext(
                new DbContextOptionsBuilder<InferenceApiDbContext>().UseNpgsql(csb.ConnectionString).Options);
            await api.GetService<IMigrator>().MigrateAsync();
            (await api.Database.GetPendingMigrationsAsync()).Should().BeEmpty();

            await using NpgsqlConnection c = new NpgsqlConnection(csb.ConnectionString);
            await c.OpenAsync();

            async Task<long> Count(string sql)
            {
                await using NpgsqlCommand cmd = new NpgsqlCommand(sql, c);
                return (long)(await cmd.ExecuteScalarAsync())!;
            }

            (await Count("SELECT count(*) FROM information_schema.tables WHERE table_name = 'runner_pairing_requests'"))
                .Should().Be(1);
            (await Count(
                "SELECT count(*) FROM information_schema.columns WHERE table_name = 'runner_pairing_requests' AND column_name IN " +
                "('device_code_hash','user_code','public_key','public_key_fingerprint','name','os','arch','runner_version'," +
                "'request_ip','created_at','expires_at','last_polled_at','status','approved_at','denied_at'," +
                "'approved_organization_id','approved_by_user_id','approved_device_id','consumed_at')"))
                .Should().Be(19);
            (await Count(
                "SELECT count(*) FROM pg_indexes WHERE tablename = 'runner_pairing_requests' " +
                "AND indexname = 'ix_runner_pairing_requests_user_code_pending' AND indexdef LIKE '%UNIQUE%' " +
                "AND indexdef LIKE '%status%'"))
                .Should().Be(1);

            Func<string, Task> run = async sql =>
            {
                await using NpgsqlCommand cmd = new NpgsqlCommand(sql, c);
                await cmd.ExecuteNonQueryAsync();
            };

            // The partial index really is partial: two rows may share a code while
            // only one of them is pending.
            await run("INSERT INTO runner_pairing_requests (id,device_code_hash,user_code,public_key,public_key_fingerprint,name,status,created_at,expires_at) " +
                "VALUES (gen_random_uuid(),'h1','ABCDEFGH','k','fp1','box','Pending',now(),now() + interval '10 minutes')");
            await run("INSERT INTO runner_pairing_requests (id,device_code_hash,user_code,public_key,public_key_fingerprint,name,status,created_at,expires_at) " +
                "VALUES (gen_random_uuid(),'h2','ABCDEFGH','k','fp2','box','Denied',now(),now() + interval '10 minutes')");

            Func<Task> dupPending = () => run(
                "INSERT INTO runner_pairing_requests (id,device_code_hash,user_code,public_key,public_key_fingerprint,name,status,created_at,expires_at) " +
                "VALUES (gen_random_uuid(),'h3','ABCDEFGH','k','fp3','box','Pending',now(),now() + interval '10 minutes')");
            (await dupPending.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23505");

            Func<Task> dupHash = () => run(
                "INSERT INTO runner_pairing_requests (id,device_code_hash,user_code,public_key,public_key_fingerprint,name,status,created_at,expires_at) " +
                "VALUES (gen_random_uuid(),'h1','ABCDEFGJ','k','fp4','box','Pending',now(),now() + interval '10 minutes')");
            (await dupHash.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23505");
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using NpgsqlCommand drop = admin.CreateCommand($"DROP DATABASE IF EXISTS {dbName} WITH (FORCE)");
            await drop.ExecuteNonQueryAsync();
        }
    }
}
