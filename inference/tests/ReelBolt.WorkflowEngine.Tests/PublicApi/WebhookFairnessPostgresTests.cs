#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.ApiKeys;
using ReelBolt.Inference.Api.Services.Webhooks;
using ReelBolt.WorkflowEngine.Tests.Infrastructure;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.PublicApi;

/// <summary>
/// S-M6 against a real PostgreSQL: the claim selects at most one due row per endpoint IN SQL (a
/// <c>ROW_NUMBER() OVER (PARTITION BY endpoint_id)</c> filter) before its batch LIMIT, so one endpoint with a
/// backlog cannot crowd the others out of the batch window. Env-gated like every Postgres test: without
/// <c>REELBOLT_TEST_PG</c> (or <c>REELBOLT_TEST_DATABASE_URL</c>) it skips rather than passes.
/// </summary>
[Collection("Postgres")]
[Trait("Category", "Postgres")]
public sealed class WebhookFairnessPostgresTests
{
    // Only the columns the claim reads/writes plus those the delivery entity materializes. The test owns the
    // schema because the project_id migration is generated outside this ticket.
    private const string Schema = """
        CREATE TABLE webhook_endpoints (
            id uuid PRIMARY KEY,
            enabled boolean NOT NULL
        );
        CREATE TABLE webhook_deliveries (
            id varchar(64) PRIMARY KEY,
            endpoint_id uuid NOT NULL,
            organization_id uuid NOT NULL,
            project_id uuid NULL,
            event_type varchar(64) NOT NULL,
            event_key varchar(200) NOT NULL,
            payload_json jsonb NOT NULL,
            status varchar(16) NOT NULL,
            attempts integer NOT NULL,
            next_attempt_at timestamp with time zone NOT NULL,
            last_status_code integer NULL,
            last_error varchar(300) NULL,
            created_at timestamp with time zone NOT NULL,
            delivered_at timestamp with time zone NULL
        );
        """;

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class StubFactory : IWebhookHttpClientFactory
    {
        public HttpClient Create() => new();
    }

    [PostgresFact]
    public async Task Claim_takes_at_most_one_due_row_per_endpoint_before_the_batch_limit()
    {
        string? baseConnection = PostgresTestEnvironment.BaseConnectionString();
        if (string.IsNullOrWhiteSpace(baseConnection)) return;

        string database = "reelbolt_webhook_fairness_" + Guid.NewGuid().ToString("N")[..12];
        await using NpgsqlDataSource admin = PostgresTestEnvironment.CreateAdminDataSource(baseConnection);
        await using (NpgsqlCommand create = admin.CreateCommand($"CREATE DATABASE {database}"))
            await create.ExecuteNonQueryAsync();

        try
        {
            string connection = PostgresTestEnvironment.ForDatabase(baseConnection, database);
            await using (NpgsqlConnection setup = new(connection))
            {
                await setup.OpenAsync();
                await using NpgsqlCommand sql = new(Schema, setup);
                await sql.ExecuteNonQueryAsync();
            }

            DbContextOptions<InferenceApiDbContext> options = new DbContextOptionsBuilder<InferenceApiDbContext>()
                .UseNpgsql(connection).Options;

            Clock clock = new();
            DateTime now = clock.Now.UtcDateTime;
            Guid busy = Guid.NewGuid();
            Guid quiet = Guid.NewGuid();
            Guid notDue = Guid.NewGuid();
            Guid organization = Guid.NewGuid();

            await using (InferenceApiDbContext seed = new(options))
            {
                // ten old due rows for one endpoint, one newer due row for another, one not due at all
                for (int i = 0; i < 10; i++) seed.WebhookDeliveries.Add(Delivery(busy, organization, now.AddMinutes(-60 + i)));
                seed.WebhookDeliveries.Add(Delivery(quiet, organization, now.AddMinutes(-5)));
                seed.WebhookDeliveries.Add(Delivery(notDue, organization, now.AddMinutes(5)));
                await seed.SaveChangesAsync();
            }

            await using (NpgsqlConnection endpoints = new(connection))
            {
                await endpoints.OpenAsync();
                await using NpgsqlCommand sql = new(
                    $"INSERT INTO webhook_endpoints (id, enabled) VALUES ('{busy}', true), ('{quiet}', true), ('{notDue}', true)", endpoints);
                await sql.ExecuteNonQueryAsync();
            }

            WebhookOptions settings = new() { BatchSize = 2 };
            PublicApiOptions apiOptions = new() { Enabled = true, Webhooks = settings };
            ServiceProvider services = new ServiceCollection().BuildServiceProvider();
            WebhookDeliveryWorker worker = new(
                services.GetRequiredService<IServiceScopeFactory>(), Options.Create(apiOptions), new StubFactory(),
                new WebhookSecretProtector(new EphemeralDataProtectionProvider()), NullLogger<WebhookDeliveryWorker>.Instance, clock);

            List<string> claimed;
            List<Guid> endpointsOfClaimed;
            await using (InferenceApiDbContext db = new(options))
            {
                claimed = await worker.ClaimAsync(db, default);
                endpointsOfClaimed = await db.WebhookDeliveries.AsNoTracking()
                    .Where(d => claimed.Contains(d.Id)).Select(d => d.EndpointId).ToListAsync();
            }

            // The old implementation's LIMIT batch*4 window would have held only the busy endpoint's rows.
            claimed.Should().HaveCount(2);
            endpointsOfClaimed.Should().BeEquivalentTo([busy, quiet]);
            endpointsOfClaimed.Should().OnlyHaveUniqueItems("one due row per endpoint is claimed");
            endpointsOfClaimed.Should().NotContain(notDue, "the third row is not due yet");
        }
        finally
        {
            await using NpgsqlCommand drop = admin.CreateCommand($"DROP DATABASE IF EXISTS {database} WITH (FORCE)");
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static WebhookDelivery Delivery(Guid endpointId, Guid organizationId, DateTime dueAt) => new()
    {
        Id = WebhookDelivery.NewId(),
        EndpointId = endpointId,
        OrganizationId = organizationId,
        EventType = WebhookEventTypes.RunSucceeded,
        EventKey = $"{WebhookEventTypes.RunSucceeded}:{Guid.NewGuid():N}",
        PayloadJson = "{}",
        Status = WebhookDelivery.Pending,
        NextAttemptAt = dueAt,
        CreatedAt = dueAt
    };
}
