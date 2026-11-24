using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Shared.Inference;
using ReelBolt.WorkflowEngine.Tests.Infrastructure;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// F3: the assistant confirmation-token key moves from <c>/keys/assistant-confirmation-token</c> into
/// <c>platform_secrets</c> without invalidating tokens already issued. Needs a real PostgreSQL
/// (<c>INSERT ... ON CONFLICT</c>), so it runs only when <c>REELBOLT_TEST_PG</c> is set (a connection
/// string with CREATEDB rights); otherwise it returns immediately, like the A1 backfill test.
/// </summary>
[Collection("Postgres")]
[Trait("Category", "Postgres")]
public class ConfirmationTokenKeyMigrationTests
{
    [Fact]
    public async Task File_key_is_imported_into_the_database_and_tokens_survive_removing_the_file_on_Postgres()
    {
        string? baseConnection = PostgresTestEnvironment.BaseConnectionString();
        if (string.IsNullOrWhiteSpace(baseConnection))
        {
            return;
        }

        string dbName = "reelbolt_f3_test_" + Guid.NewGuid().ToString("N")[..12];
        string keysDir = Path.Combine(Path.GetTempPath(), "rb-ct-" + Guid.NewGuid().ToString("N"));
        await using NpgsqlDataSource admin = PostgresTestEnvironment.CreateAdminDataSource(baseConnection);
        await using (NpgsqlCommand create = admin.CreateCommand($"CREATE DATABASE {dbName}"))
        {
            await create.ExecuteNonQueryAsync();
        }

        var csb = new NpgsqlConnectionStringBuilder(PostgresTestEnvironment.ForDatabase(baseConnection, dbName));
        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddDbContext<InferenceApiDbContext>(o => o.UseNpgsql(csb.ConnectionString));
            using ServiceProvider provider = services.BuildServiceProvider();
            using (IServiceScope scope = provider.CreateScope())
            {
                await scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>().Database.MigrateAsync();
            }

            ISecretProtector protector = new DataProtectionSecretProtector(
                new EphemeralDataProtectionProvider(), NullLogger<DataProtectionSecretProtector>.Instance);
            Guid user = Guid.NewGuid();

            ConfirmationTokenService Build(bool database)
            {
                var settings = new Dictionary<string, string?> { ["DataProtection:KeysPath"] = keysDir };
                if (database)
                {
                    settings["DataProtection:Store"] = "Database";
                }

                return new ConfirmationTokenService(
                    protector,
                    new ConfigurationBuilder().AddInMemoryCollection(settings).Build(),
                    NullLogger<ConfirmationTokenService>.Instance,
                    provider.GetRequiredService<IServiceScopeFactory>());
            }

            // File mode generates and persists the key; a token is issued under it.
            string issued = Build(database: false).Compute("DeleteProject", "payload", user);
            File.Exists(Path.Combine(keysDir, "assistant-confirmation-token")).Should().BeTrue();

            // Switch to the database: the file key is imported, so the token still verifies.
            Build(database: true).Verify("DeleteProject", "payload", user, issued).Should().BeTrue();

            // The file is no longer needed.
            File.Delete(Path.Combine(keysDir, "assistant-confirmation-token"));
            Build(database: true).Verify("DeleteProject", "payload", user, issued).Should().BeTrue();

            using IServiceScope check = provider.CreateScope();
            (await check.ServiceProvider.GetRequiredService<InferenceApiDbContext>().PlatformSecrets.CountAsync())
                .Should().Be(1);
        }
        finally
        {
            if (Directory.Exists(keysDir))
            {
                Directory.Delete(keysDir, recursive: true);
            }

            NpgsqlConnection.ClearAllPools();
            await using NpgsqlCommand drop = admin.CreateCommand($"DROP DATABASE IF EXISTS {dbName} WITH (FORCE)");
            await drop.ExecuteNonQueryAsync();
        }
    }
}
