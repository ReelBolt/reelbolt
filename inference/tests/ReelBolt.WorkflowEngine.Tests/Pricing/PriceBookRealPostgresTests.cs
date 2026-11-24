#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using ReelBolt.WorkflowEngine.Tests.Infrastructure;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Pricing;

/// <summary>
/// A FRESH install against a <b>real PostgreSQL</b> (rewrites the old <c>RateCardSeedRealPostgresTests</c>, whose rate-card
/// seeder no longer exists): migrations applied, platform capabilities configured, the boot-time
/// <see cref="ModelCatalogUpgrader"/> run with no <c>rate_cards</c> at all, and the resulting price table asked what a
/// Fish Audio transcription costs (the staging ledger's old unpriced row) through the real
/// <see cref="EfModelPriceSource{TContext}"/> — decimals, enum names and the variant column round-tripped by Npgsql.
/// </summary>
/// <remarks>
/// Creates a throwaway database and drops it; does nothing unless <c>REELBOLT_TEST_PG</c> names a connection string
/// with CREATEDB rights. The database name is asserted to be the throwaway one before any command runs.
/// </remarks>
[Collection("Postgres")]
[Trait("Category", "Postgres")]
public class PriceBookRealPostgresTests
{
    [PostgresFact]
    public async Task A_fresh_install_prices_every_catalogued_platform_capability_and_a_Fish_Audio_transcription()
    {
        string? baseConnection = PostgresTestEnvironment.BaseConnectionString();
        if (string.IsNullOrWhiteSpace(baseConnection))
        {
            return;
        }

        string dbName = "reelbolt_bi_test_" + Guid.NewGuid().ToString("N")[..12];
        NpgsqlConnectionStringBuilder target = new(baseConnection) { Database = dbName };
        target.Database.Should().Be(dbName);
        target.Database.Should().StartWith("reelbolt_bi_test_");

        await using NpgsqlDataSource admin = PostgresTestEnvironment.CreateAdminDataSource(baseConnection);
        await using (NpgsqlCommand create = admin.CreateCommand("CREATE DATABASE " + dbName))
        {
            await create.ExecuteNonQueryAsync();
        }

        try
        {
            ServiceCollection services = new();
            services.AddLogging();
            services.AddDbContext<InferenceApiDbContext>(o => o.UseNpgsql(target.ConnectionString));
            await using ServiceProvider provider = services.BuildServiceProvider();
            Guid fishAsr = Guid.NewGuid(), video = Guid.NewGuid();

            using (IServiceScope scope = provider.CreateScope())
            {
                InferenceApiDbContext db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
                await db.Database.MigrateAsync();
                db.AddFlat(new ModelCapabilityView
                {
                    Id = fishAsr,
                    Name = "Fish ASR",
                    Kind = InferenceProviderKind.FishAudio,
                    Capability = InferenceProviderCapability.Transcription,
                    ModelName = "transcribe-1-pro",
                    Endpoint = "https://api.fish.audio"
                });
                db.AddFlat(new ModelCapabilityView
                {
                    Id = video,
                    Name = "MiniMax H3",
                    Kind = InferenceProviderKind.MiniMax,
                    Capability = InferenceProviderCapability.VideoGeneration,
                    ModelName = "MiniMax-H3",
                    Endpoint = "https://api.minimax.io"
                });
                await db.SaveChangesAsync();

                ModelCatalogUpgradeResult result = await new ModelCatalogUpgrader(db, new NoKeys(), new ConfigurationBuilder().Build(),
                    NullLogger<ModelCatalogUpgrader>.Instance).RunAsync();
                result.PricesStamped.Should().BeTrue();
                result.ComputeProviderCreated.Should().BeTrue();
            }

            using (IServiceScope scope = provider.CreateScope())
            {
                InferenceApiDbContext db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
                (await PriceCoverage.FindAsync(db, DateTime.UtcNow)).Should().BeEmpty("a fresh install prices what the book knows");
                (await db.RateCards.CountAsync()).Should().Be(0, "nothing seeds rate cards any more");
            }

            ModelPriceService prices = new(new EfModelPriceSource<InferenceApiDbContext>(provider.GetRequiredService<IServiceScopeFactory>()));
            PriceQuote asr = await prices.PriceAsync(new PriceRequest(fishAsr, null, InferenceProviderKind.FishAudio,
                InferenceProviderCapability.Transcription, "transcribe-1-pro", UsageKind.AsrAudioSeconds, null, UsageBillingMode.Platform, 0.3m));
            asr.PriceId.Should().NotBeNull();
            asr.CostUsd.Should().Be(0.3m * 0.36m / 3600m);
            asr.CreditsMillis.Should().Be((long)Math.Round(0.3m * 0.36m / 3600m * 1.2m * 1000m / 0.006m, MidpointRounding.AwayFromZero));

            PriceQuote twoK = await prices.PriceAsync(new PriceRequest(video, null, InferenceProviderKind.MiniMax,
                InferenceProviderCapability.VideoGeneration, "MiniMax-H3", UsageKind.VideoGenSeconds, "2K", UsageBillingMode.Platform, 10));
            twoK.CostUsd.Should().Be(1.3m, "a 2K second costs the vendor's 2K rate");

            PriceQuote compute = await prices.PriceAsync(new PriceRequest(WellKnownModelCatalog.ComputeCapabilityId, null,
                ComputeMetering.ProviderKind, InferenceProviderCapability.Compute, WellKnownModelCatalog.ComputeModelCode,
                UsageKind.RenderOutputSeconds, "uhd", UsageBillingMode.Platform, 10));
            compute.CostUsd.Should().Be(10m * PricingDefaults.ComputeRenderUsdPerSecond * 9m);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using NpgsqlCommand drop = admin.CreateCommand($"DROP DATABASE IF EXISTS {dbName} WITH (FORCE)");
            await drop.ExecuteNonQueryAsync();
        }
    }

    private sealed class NoKeys : ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;

        public bool TryUnprotect(string? ciphertext, out string plaintext)
        {
            plaintext = string.Empty;
            return false;
        }
    }
}
