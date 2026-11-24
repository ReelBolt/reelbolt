using System;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using ReelBolt.Inference.Api.Data;
using ReelBolt.WorkflowEngine.Tests.Infrastructure;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Data;

/// <summary>
/// billing-paid-amounts T-001: the <c>AddBillingPayments</c> migration against a real PostgreSQL. It
/// migrates a database of its own and drops it afterwards, and proves three things the entity and the
/// migration declare: the promoted columns and their types, the unique key on
/// <c>(provider, provider_transaction_id)</c> that makes a replay unable to double-count, and that the
/// migration's guarded <c>powerbi_ro</c> grant is a no-op when the role does not exist (it does not, in
/// this database), so the migration still applies and the API still starts.
/// </summary>
[Collection("Postgres")]
[Trait("Category", "Postgres")]
public class BillingPaymentsSchemaTests
{
    [PostgresFact]
    public async Task The_billing_payments_migration_creates_the_table_and_its_unique_key()
    {
        string baseConnection = PostgresTestEnvironment.BaseConnectionString()!;
        string dbName = "reelbolt_billing_payments_" + Guid.NewGuid().ToString("N")[..12];
        await using NpgsqlDataSource admin = PostgresTestEnvironment.CreateAdminDataSource(baseConnection);
        await using (NpgsqlCommand create = admin.CreateCommand($"CREATE DATABASE {dbName}"))
        {
            await create.ExecuteNonQueryAsync();
        }

        NpgsqlConnectionStringBuilder csb = new(PostgresTestEnvironment.ForDatabase(baseConnection, dbName));
        try
        {
            // The migration applies to a fresh database. Its powerbi_ro grant is guarded, and the role
            // does not exist here, so the DO block runs the GRANT for nobody and the migration succeeds.
            await using (InferenceApiDbContext api = new(
                new DbContextOptionsBuilder<InferenceApiDbContext>().UseNpgsql(csb.ConnectionString).Options))
            {
                await api.GetService<IMigrator>().MigrateAsync();
            }

            await using NpgsqlConnection c = new(csb.ConnectionString);
            await c.OpenAsync();

            async Task<object?> Scalar(string sql)
            {
                await using NpgsqlCommand cmd = new(sql, c);
                return await cmd.ExecuteScalarAsync();
            }

            // The guarded grant in this migration follows the role's existence, and either way the
            // migration must apply — which reaching this line proves. It is deliberately not asserted
            // here: roles are cluster-wide, so a sibling Postgres-gated test that creates powerbi_ro
            // makes "the role is absent" false depending on run order. The grant's registry is
            // PowerBiReadOnlyRoleSql (this table is in FullSelectTables and its SQL), and both branches
            // are covered by PowerBiReadOnlyRolePostgresTests.

            // The table's columns, exactly as the entity and the migration declare them.
            (await Scalar("SELECT data_type FROM information_schema.columns WHERE table_name='billing_payments' AND column_name='amount_minor'"))
                .Should().Be("bigint");
            (await Scalar("SELECT data_type FROM information_schema.columns WHERE table_name='billing_payments' AND column_name='fee_minor'"))
                .Should().Be("bigint");
            (await Scalar("SELECT data_type FROM information_schema.columns WHERE table_name='billing_payments' AND column_name='currency_code'"))
                .Should().Be("character varying");
            (await Scalar("SELECT character_maximum_length::int FROM information_schema.columns WHERE table_name='billing_payments' AND column_name='currency_code'"))
                .Should().Be(10);
            (await Scalar("SELECT data_type FROM information_schema.columns WHERE table_name='billing_payments' AND column_name='occurred_at'"))
                .Should().Be("timestamp with time zone");
            (await Scalar("SELECT is_nullable FROM information_schema.columns WHERE table_name='billing_payments' AND column_name='organization_id'"))
                .Should().Be("YES", "organization_id is a bare nullable uuid: there is no FK, so the trail outlives an account purge");

            // The unique key on (provider, provider_transaction_id): a second row for the same
            // transaction is refused, which is what makes a webhook replay unable to double-count.
            async Task InsertAsync(string provider, string txn)
            {
                await using NpgsqlCommand insert = new(
                    "INSERT INTO billing_payments (id, provider, provider_transaction_id, amount_minor, occurred_at, created_at) " +
                    "VALUES (gen_random_uuid(), @provider, @txn, 1450, now(), now())", c);
                insert.Parameters.AddWithValue("provider", provider);
                insert.Parameters.AddWithValue("txn", txn);
                await insert.ExecuteNonQueryAsync();
            }

            await InsertAsync("paddle", "txn_1");
            Func<Task> duplicate = () => InsertAsync("paddle", "txn_1");
            (await duplicate.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23505");

            // A different provider may reuse the same transaction id.
            await InsertAsync("other", "txn_1");
        }
        finally
        {
            // Drop on a data source created *after* the pools are cleared. Reusing the admin source that
            // opened (and pooled) a connection before ClearAllPools can hand back a connector the clear
            // has just closed, which surfaces as "Exception while reading from stream" at the DROP; a
            // source created afterwards has no pooled connector to reuse, so the DROP runs on a fresh
            // connection. Same shape as MailRateCounterStorePostgresTests' teardown.
            NpgsqlConnection.ClearAllPools();
            await using NpgsqlDataSource dropAdmin = PostgresTestEnvironment.CreateAdminDataSource(baseConnection);
            await using NpgsqlCommand drop = dropAdmin.CreateCommand($"DROP DATABASE IF EXISTS {dbName} WITH (FORCE)");
            await drop.ExecuteNonQueryAsync();
        }
    }
}
