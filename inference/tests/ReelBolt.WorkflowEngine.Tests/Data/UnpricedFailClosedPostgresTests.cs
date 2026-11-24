#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Npgsql;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Inference;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Tenancy;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Services.Inference;
using ReelBolt.WorkflowEngine.Tests.Infrastructure;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Data;

/// <summary>
/// The fail-closed pricing rule on a real PostgreSQL (when <c>REELBOLT_TEST_PG</c> is set): the <c>AddUsageEventUnpriced</c> migration adds
/// <c>usage_events.unpriced boolean NOT NULL DEFAULT false</c>, the engine writes and reads the flag through its read-only
/// mapping, and every query the fail-closed rules add (the stores' unpriced set, the picker filter, the settle's
/// persisted-flag check, price coverage) translates and answers correctly.
/// </summary>
[Collection("Postgres")]
[Trait("Category", "Postgres")]
public sealed class UnpricedFailClosedPostgresTests
{
    [PostgresFact]
    public async Task The_unpriced_flag_and_the_fail_closed_queries_work_on_postgres()
    {
        string? baseConnection = PostgresTestEnvironment.BaseConnectionString();
        if (string.IsNullOrWhiteSpace(baseConnection))
        {
            return;
        }

        string dbName = "reelbolt_unpriced_test_" + Guid.NewGuid().ToString("N")[..12];
        await using NpgsqlDataSource admin = PostgresTestEnvironment.CreateAdminDataSource(baseConnection);
        await using (NpgsqlCommand create = admin.CreateCommand($"CREATE DATABASE {dbName}"))
        {
            await create.ExecuteNonQueryAsync();
        }

        NpgsqlConnectionStringBuilder csb = new(baseConnection) { Database = dbName };
        try
        {
            await using InferenceApiDbContext api = new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseNpgsql(csb.ConnectionString).Options);
            await api.Database.MigrateAsync();

            await using NpgsqlDataSource data = NpgsqlDataSource.Create(csb.ConnectionString);
            await using (NpgsqlCommand column = data.CreateCommand(
                "SELECT data_type, is_nullable, column_default FROM information_schema.columns " +
                "WHERE table_name = 'usage_events' AND column_name = 'unpriced'"))
            await using (NpgsqlDataReader reader = await column.ExecuteReaderAsync())
            {
                (await reader.ReadAsync()).Should().BeTrue("the migration adds usage_events.unpriced");
                (reader.GetString(0), reader.GetString(1), reader.GetString(2)).Should().Be(("boolean", "NO", "false"));
            }

            DateTime now = DateTime.UtcNow;
            Guid org = Guid.NewGuid();
            api.Organizations.Add(new Organization { Id = org, Name = "o", Slug = "o-" + org.ToString("N"), Kind = OrganizationKind.Team });
            ModelCapabilityView Row(string name, Guid? owner = null, ModelPriceSource source = ModelPriceSource.Table) => new()
            {
                Id = Guid.NewGuid(),
                Name = name,
                ModelName = name,
                Kind = InferenceProviderKind.AzureOpenAI,
                Endpoint = "https://x.example",
                Capability = InferenceProviderCapability.Chat,
                OrganizationId = owner,
                PriceSource = source
            };
            ModelCapabilityView priced = Row("priced"), expired = Row("expired"), none = Row("none"), byo = Row("byo", org),
                oracle = Row("oracle", source: ModelPriceSource.Oracle);
            api.AddFlat(priced, expired, none, byo, oracle);
            foreach (UsageKind unit in new[] { UsageKind.LlmInputTokens, UsageKind.LlmOutputTokens, UsageKind.LlmCachedInputTokens })
            {
                api.ModelCapabilityPrices.Add(new ModelCapabilityPrice
                {
                    Id = Guid.NewGuid(),
                    ModelCapabilityId = priced.Id,
                    Unit = unit,
                    CostUsdPerUnit = 0.000001m,
                    EffectiveFrom = now.AddDays(-1)
                });
            }

            api.ModelCapabilityPrices.Add(new ModelCapabilityPrice
            {
                Id = Guid.NewGuid(),
                ModelCapabilityId = expired.Id,
                Unit = UsageKind.LlmInputTokens,
                CostUsdPerUnit = 0.000001m,
                EffectiveFrom = now.AddDays(-10),
                EffectiveTo = now.AddDays(-1)
            });
            await api.SaveChangesAsync();
            api.ChangeTracker.Clear();

            // The stores' unpriced set, on both contexts.
            await using WorkflowEngineDbContext engine = new(new DbContextOptionsBuilder<WorkflowEngineDbContext>().UseNpgsql(csb.ConnectionString).Options);
            IReadOnlySet<Guid> fromApi = await ((IInferenceProviderStore)new InferenceApiProviderStore(api)).LoadUnpricedCapabilityIdsAsync(now, CancellationToken.None);
            IReadOnlySet<Guid> fromEngine = await ((IInferenceProviderStore)new WorkflowEngineProviderStore(engine)).LoadUnpricedCapabilityIdsAsync(now, CancellationToken.None);
            fromApi.Should().BeEquivalentTo([expired.Id, none.Id]);
            fromEngine.Should().BeEquivalentTo([expired.Id, none.Id]);

            // The picker filter.
            Mock<IInferenceProviderResolver> resolver = new();
            resolver.SetupGet(r => r.UnpricedModelsFailClosed).Returns(true);
            InferenceProviderAdminService service = new(api, new Mock<ISecretProtector>().Object,
                new InferenceProviderTester(api, new Mock<ISecretProtector>().Object, new Mock<IChatClientFactory>().Object,
                    new Mock<ITranscriptionClientFactory>().Object, new Mock<IEmbeddingClientFactory>().Object,
                    new Mock<IDecisionClientFactory>().Object, new Mock<IVideoGenerationClientFactory>().Object,
                    new Mock<ISpeechSynthesisClientFactory>().Object, NullLogger<InferenceProviderTester>.Instance),
                resolver.Object);
            List<SelectableProviderResponse> offered = await service.ListSelectableAsync(org, null, CancellationToken.None);
            offered.Select(o => o.Id).Should().BeEquivalentTo([priced.Id, byo.Id, oracle.Id]);

            // The engine writes the flag through its read-only mapping, and the settle's persisted check reads it back.
            Guid step = Guid.NewGuid();
            engine.UsageEvents.Add(new UsageEvent
            {
                Id = Guid.NewGuid(),
                OrganizationId = org,
                StepResultId = step,
                Source = UsageSource.Workflow,
                Kind = UsageKind.LlmInputTokens,
                Quantity = 10,
                ModelName = "none",
                BillingMode = UsageBillingMode.Platform,
                Unpriced = true,
                IdempotencyKey = $"{step:N}:1:in"
            });
            await engine.SaveChangesAsync();
            (await engine.UsageEvents.AsNoTracking().AnyAsync(e => e.StepResultId == step && e.Unpriced)).Should().BeTrue();
            (await api.UsageEvents.AsNoTracking().CountAsync(e => e.Unpriced)).Should().Be(1);

            // Price coverage (the daily report's error section) names exactly the two gaps.
            IReadOnlyList<PriceCoverageGap> gaps = await PriceCoverage.FindAsync(api, now);
            gaps.Select(g => g.CapabilityId).Should().BeEquivalentTo([expired.Id, none.Id]);
        }
        finally
        {
            await using NpgsqlCommand drop = admin.CreateCommand($"DROP DATABASE IF EXISTS {dbName} WITH (FORCE)");
            await drop.ExecuteNonQueryAsync();
        }
    }
}
