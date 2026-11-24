#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
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
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;
using ReelBolt.WorkflowEngine.Tests.Infrastructure;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.PublicApi;

/// <summary>
/// The webhook worker's <c>FOR UPDATE SKIP LOCKED</c> claim on a real Postgres (skipped unless
/// <c>REELBOLT_TEST_PG</c> is set): two workers ticking at once deliver every due delivery exactly once.
/// </summary>
[Collection("Postgres")]
[Trait("Category", "Postgres")]
public class WebhookDeliveryWorkerPostgresTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; } = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public ConcurrentQueue<string> Ids { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Ids.Enqueue(request.Headers.GetValues("webhook-id").Single());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IWebhookHttpClientFactory
    {
        public HttpClient Create() => new(handler, disposeHandler: false);
    }

    private const int Endpoints = 8;
    private const int PerEndpoint = 2;

    [PostgresFact]
    public async Task Two_concurrent_workers_deliver_each_due_delivery_exactly_once()
    {
        string baseConnection = PostgresTestEnvironment.BaseConnectionString()!;
        string dbName = "reelbolt_webhook_worker_" + Guid.NewGuid().ToString("N")[..12];
        await using NpgsqlDataSource admin = PostgresTestEnvironment.CreateAdminDataSource(baseConnection);
        await using (NpgsqlCommand create = admin.CreateCommand($"CREATE DATABASE {dbName}"))
        {
            await create.ExecuteNonQueryAsync();
        }

        try
        {
            string connection = PostgresTestEnvironment.ForDatabase(baseConnection, dbName, maxPoolSize: 8);
            DbContextOptions<InferenceApiDbContext> dbOptions = new DbContextOptionsBuilder<InferenceApiDbContext>()
                .UseNpgsql(connection).Options;
            await using (InferenceApiDbContext migrate = new(dbOptions))
            {
                await migrate.Database.MigrateAsync();
            }

            Clock clock = new();
            Guid org = Guid.NewGuid();
            Guid owner = Guid.NewGuid();
            WebhookSecretProtector protector = new(new EphemeralDataProtectionProvider());
            List<string> seeded = [];
            await using (InferenceApiDbContext db = new(dbOptions))
            {
                db.Organizations.Add(new Organization { Id = org, Name = "o", Slug = "o", Kind = OrganizationKind.Team });
                db.ApplicationUsers.Add(new ApplicationUser { Id = owner, Email = "o@example.com", DisplayName = "o", EmailVerifiedAt = DateTime.UtcNow });
                for (int e = 0; e < Endpoints; e++)
                {
                    WebhookEndpoint endpoint = new()
                    {
                        Id = Guid.NewGuid(),
                        OrganizationId = org,
                        Url = $"https://hooks.example.com/{e}",
                        Events = [WebhookEventTypes.RunSucceeded],
                        SecretEncrypted = protector.Protect("whsec_" + Convert.ToBase64String(Guid.NewGuid().ToByteArray())),
                        CreatedByUserId = owner,
                        CreatedAt = clock.Now.UtcDateTime
                    };
                    db.WebhookEndpoints.Add(endpoint);
                    for (int d = 0; d < PerEndpoint; d++)
                    {
                        // An endpoint's deliveries are due one second apart (d seconds in the past): a pass claims
                        // at most one per endpoint (S-M6), so the later one must already be due for the next pass
                        // rather than waiting for a clock this test never advances.
                        WebhookDelivery delivery = WebhookEventWriter.NewDelivery(
                            endpoint, WebhookEventTypes.RunSucceeded, $"k:{e}:{d}", "{\"type\":\"x\"}", clock.Now.UtcDateTime.AddSeconds(-d));
                        db.WebhookDeliveries.Add(delivery);
                        seeded.Add(delivery.Id);
                    }
                }

                await db.SaveChangesAsync();
            }

            CountingHandler handler = new();
            PublicApiOptions options = new() { Enabled = true };
            options.Webhooks.BatchSize = 4;
            ServiceCollection services = new();
            services.AddScoped(_ => new InferenceApiDbContext(dbOptions));
            using ServiceProvider sp = services.BuildServiceProvider();
            WebhookDeliveryWorker Worker() => new(
                sp.GetRequiredService<IServiceScopeFactory>(), Options.Create(options), new Factory(handler),
                protector, NullLogger<WebhookDeliveryWorker>.Instance, clock);
            WebhookDeliveryWorker a = Worker();
            WebhookDeliveryWorker b = Worker();

            int sent = 0;
            for (int round = 0; round < 20; round++)
            {
                int[] counts = await Task.WhenAll(a.TickAsync(default), b.TickAsync(default));
                sent += counts.Sum();
                await using InferenceApiDbContext check = new(dbOptions);
                if (!await check.WebhookDeliveries.AnyAsync(d => d.Status != WebhookDelivery.Delivered)) break;
            }

            sent.Should().Be(seeded.Count, "no delivery is claimed twice");
            handler.Ids.Should().HaveCount(seeded.Count);
            handler.Ids.Should().OnlyHaveUniqueItems("each delivery is POSTed exactly once");
            handler.Ids.Should().BeEquivalentTo(seeded);

            await using InferenceApiDbContext final = new(dbOptions);
            List<WebhookDelivery> rows = await final.WebhookDeliveries.AsNoTracking().ToListAsync();
            rows.Should().OnlyContain(r => r.Status == WebhookDelivery.Delivered && r.Attempts == 1);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using NpgsqlCommand drop = admin.CreateCommand($"DROP DATABASE IF EXISTS {dbName} WITH (FORCE)");
            await drop.ExecuteNonQueryAsync();
        }
    }
}
