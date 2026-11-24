#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using ReelBolt.Inference.Api.Billing;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.Shared.IntegrationEvents;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Billing;

/// <summary>
/// B8c against a REAL broker: the plan entitlement becomes the AMQP priority on the published
/// message, and the queue's <c>x-max-priority</c> declaration is what turns that property into a
/// dequeue order.
///
/// <para>
/// <b>Why this file exists.</b> <see cref="RunPriorityTests"/> pins the decision in-process, but it
/// cannot see the wire: MassTransit keeps the priority inside its own send context, and mocking
/// <c>IPublishEndpoint</c> proves only which overload was called. Two things could therefore be
/// wrong while every unit test stayed green — <c>SetPriority</c> might not map onto the AMQP basic
/// property at all, and an engine that never declared the queue with <c>x-max-priority</c> would
/// accept that property and ignore it, leaving a silently FIFO queue. This test reads the priority
/// off the message the production publish path actually produced, and it runs the same pair of runs
/// into a priority queue AND a plain one, so the declaration is shown to be load-bearing rather than
/// assumed.
/// </para>
///
/// <para>
/// <b>Gating.</b> <see cref="RealRabbitMqFactAttribute"/> skips at discovery time unless
/// <c>REELBOLT_IT_RABBITMQ_URL</c> is set, so an ordinary <c>dotnet test</c> reports these as
/// skipped rather than passing vacuously. To run them against a THROWAWAY broker:
/// <code>
/// docker run -d --rm -p 5673:5672 rabbitmq:3-alpine
/// export REELBOLT_IT_RABBITMQ_URL=amqp://guest:guest@127.0.0.1:5673/
/// dotnet test inference/ReelBolt.sln --filter FullyQualifiedName~RunPriorityQueueIntegrationTests
/// </code>
/// Never point this at the development broker: it publishes real messages onto the shared
/// <c>workflow-execution</c> exchange.
/// </para>
/// </summary>
public class RunPriorityQueueIntegrationTests
{
    [RealRabbitMqFact]
    public async Task A_pro_plan_run_reaches_the_queue_carrying_its_plan_priority()
    {
        await using BrokerHarness harness = await BrokerHarness.StartAsync();
        string queue = await harness.DeclareQueueAsync(maxPriority: RunPriority.DefaultQueueMax);

        Guid standardExecution = await harness.PublishRunAsync(PlanKind.Trial);
        Guid priorityExecution = await harness.PublishRunAsync(PlanKind.Pro);

        // The queue was empty and no consumer is attached, so RabbitMQ hands back the HIGHEST
        // priority message first - which is the whole feature in one assertion.
        QueuedMessage? first = await harness.ReadAsync(queue);
        first.Should().NotBeNull();
        first!.ExecutionId.Should().Be(priorityExecution, "the Pro run must be dequeued before the standard one");
        first.Priority.Should().Be((byte)1, "the run's plan priority must be on the wire as the AMQP priority");

        QueuedMessage? second = await harness.ReadAsync(queue);
        second.Should().NotBeNull();
        second!.ExecutionId.Should().Be(standardExecution);
        // Absent and 0 are the same thing to RabbitMQ; a standard run deliberately carries no
        // property at all, so the test accepts either spelling of "no priority".
        (second.Priority ?? 0).Should().Be((byte)0);
    }

    [RealRabbitMqFact]
    public async Task The_x_max_priority_declaration_is_what_makes_the_queue_order_by_priority()
    {
        await using BrokerHarness harness = await BrokerHarness.StartAsync();
        string priorityQueue = await harness.DeclareQueueAsync(maxPriority: RunPriority.DefaultQueueMax);
        string plainQueue = await harness.DeclareQueueAsync(maxPriority: null);

        // Standard first, on purpose: creation order alone would deliver it first from both queues.
        Guid standardExecution = await harness.PublishRunAsync(PlanKind.Trial);
        Guid priorityExecution = await harness.PublishRunAsync(PlanKind.Pro);

        (await harness.ReadAsync(priorityQueue))!.ExecutionId.Should()
            .Be(priorityExecution, "a queue declared with x-max-priority reorders on the stamped priority");
        (await harness.ReadAsync(plainQueue))!.ExecutionId.Should()
            .Be(standardExecution, "without x-max-priority the broker accepts the same property and stays FIFO - "
                + "which is why the engine's EnablePriority call is load-bearing, not decoration");
    }

    /// <summary>
    /// Pins the deployment note on the engine's receive-endpoint registration, so the default it
    /// documents cannot be "cleaned up" by someone who has not read it.
    ///
    /// <para>
    /// <c>x-max-priority</c> is a queue ARGUMENT, and a broker answers a re-declaration that differs
    /// from the existing queue with PRECONDITION_FAILED (406) rather than adjusting it. That is the
    /// one real cost of shipping the priority queue ON by default: an engine upgrading onto a broker
    /// that already holds the argument-less <c>workflow-execution</c> queue fails to start its bus
    /// until that queue is deleted (drained first — its messages are not migrated), which is why
    /// <c>WorkflowEngine:QueuePriority:MaxPriority=0</c> exists as the escape hatch.
    /// </para>
    /// </summary>
    [RealRabbitMqFact]
    public async Task A_queue_declared_without_x_max_priority_cannot_be_redeclared_with_it()
    {
        await using BrokerHarness harness = await BrokerHarness.StartAsync();
        string queue = await harness.DeclareQueueAsync(maxPriority: null);

        Func<Task> redeclare = () => harness.RedeclareQueueWithPriorityAsync(queue, RunPriority.DefaultQueueMax);

        await redeclare.Should().ThrowAsync<Exception>()
            .WithMessage("*PRECONDITION_FAILED*",
                "if this ever stops throwing, the escape hatch and the upgrade note have both become wrong");
    }

    /// <summary>
    /// The broker, the bus and the production publish path, torn down together. Everything it
    /// declares is exclusive and auto-delete, so a failed run cannot leave a queue behind and a
    /// concurrent run cannot collide with it.
    /// </summary>
    private sealed class BrokerHarness : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly IConnection _connection;
        private readonly IChannel _channel;
        private readonly InferenceApiDbContext _db;
        private readonly Guid _standardOrg = Guid.NewGuid();
        private readonly Guid _priorityOrg = Guid.NewGuid();
        private readonly string _exchange;

        private BrokerHarness(
            ServiceProvider provider, IConnection connection, IChannel channel,
            InferenceApiDbContext db, string exchange)
        {
            _provider = provider;
            _connection = connection;
            _channel = channel;
            _db = db;
            _exchange = exchange;
        }

        public static async Task<BrokerHarness> StartAsync()
        {
            Uri uri = new(RunPriorityIntegrationEnvironment.RabbitMqUrl);

            ServiceProvider provider = new ServiceCollection()
                .AddMassTransit(x => x.UsingRabbitMq((_, cfg) => cfg.Host(uri)))
                .BuildServiceProvider();
            IBusControl bus = provider.GetRequiredService<IBusControl>();
            await bus.StartAsync();

            ConnectionFactory factory = new() { Uri = uri };
            IConnection connection = await factory.CreateConnectionAsync();
            IChannel channel = await connection.CreateChannelAsync();

            // The publish exchange is MassTransit's, named after the message URN. A probe publish
            // forces the topology to be created, and the passive declare below proves the name this
            // harness binds to is really the one the production publish path uses - a rename would
            // fail here loudly instead of quietly binding a queue nothing is ever routed to.
            await bus.Publish(new WorkflowExecutionRequested { CorrelationId = "b8c-topology-probe" });
            string exchange = MessageUrn.ForType(typeof(WorkflowExecutionRequested)).ToString()
                .Replace("urn:message:", string.Empty, StringComparison.Ordinal);
            try
            {
                await channel.ExchangeDeclarePassiveAsync(exchange);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"The published message's exchange was expected at '{exchange}' " +
                    $"(MessageUrn minus the 'urn:message:' prefix) but the broker has no such exchange. " +
                    "The harness cannot bind a queue to the production publish path.", ex);
            }

            return new BrokerHarness(provider, connection, channel, NewDb(), exchange);
        }

        /// <summary>An exclusive, auto-delete queue bound to the production publish exchange.</summary>
        public async Task<string> DeclareQueueAsync(byte? maxPriority)
        {
            string queue = $"b8c-priority-it-{Guid.NewGuid():N}";
            Dictionary<string, object?>? arguments = maxPriority is { } max
                ? new Dictionary<string, object?> { ["x-max-priority"] = (int)max }
                : null;

            await _channel.QueueDeclareAsync(queue, durable: false, exclusive: true, autoDelete: true, arguments: arguments);
            await _channel.QueueBindAsync(queue, _exchange, routingKey: string.Empty);
            return queue;
        }

        /// <summary>
        /// Runs one execution through the REAL production publish path - WorkflowExecutionService,
        /// the plan-backed entitlements and the shipped plan catalog - and returns its execution id.
        /// </summary>
        public async Task<Guid> PublishRunAsync(PlanKind plan)
        {
            Guid organizationId = plan == PlanKind.Pro ? _priorityOrg : _standardOrg;
            Guid projectId = Guid.NewGuid();
            Guid workflowId = Guid.NewGuid();
            DateTime now = DateTime.UtcNow;

            _db.Projects.Add(new Project
            {
                Id = projectId,
                OrganizationId = organizationId,
                Name = "b8c",
                CreatedAt = now,
                UpdatedAt = now
            });
            _db.WorkflowDefinitions.Add(new WorkflowDefinition
            {
                Id = workflowId,
                ProjectId = projectId,
                Name = "b8c",
                RequiresUserInput = false,
                CreatedAt = now,
                UpdatedAt = now
            });
            await _db.SaveChangesAsync();

            WorkflowExecutionService service = new(
                _db,
                _provider.GetRequiredService<IBus>(),
                NullLogger<WorkflowExecutionService>.Instance,
                planGate: null,
                entitlements: PlanEntitlementsFor(plan));

            WorkflowExecutionResponse response = await service.ExecuteAsync(
                projectId, workflowId, Guid.NewGuid(), null, ExecutionInitiator.Human, CancellationToken.None);

            return response.Id;
        }

        /// <summary>Re-declares an existing queue with x-max-priority, which the broker must refuse.</summary>
        public async Task RedeclareQueueWithPriorityAsync(string queue, byte maxPriority)
        {
            // A fresh channel: the broker closes the offending one, and the harness may still need its own.
            await using IChannel channel = await _connection.CreateChannelAsync();
            await channel.QueueDeclareAsync(
                queue, durable: false, exclusive: true, autoDelete: true,
                arguments: new Dictionary<string, object?> { ["x-max-priority"] = (int)maxPriority });
        }

        /// <summary>Reads one message off a queue, with the AMQP priority it arrived carrying.</summary>
        public async Task<QueuedMessage?> ReadAsync(string queue)
        {
            BasicGetResult? result = await _channel.BasicGetAsync(queue, autoAck: true);
            if (result is null)
            {
                return null;
            }

            return new QueuedMessage(result.BasicProperties.Priority, ReadExecutionId(result));
        }

        private IOrganizationEntitlements PlanEntitlementsFor(PlanKind plan)
        {
            Guid organizationId = plan == PlanKind.Pro ? _priorityOrg : _standardOrg;
            FakePlans source = new()
            {
                Subscribed = new Dictionary<Guid, Plan> { [organizationId] = ShippedPlans()[plan == PlanKind.Pro ? "pro" : "trial"] },
                Fallback = ShippedPlans()["trial"]
            };
            return new PlanBackedOrganizationEntitlements(
                source,
                Options.Create(new BillingOptions { Enabled = true, EntitlementCacheSeconds = 0 }),
                new TenancyOptions());
        }

        private static Dictionary<string, Plan> ShippedPlans()
        {
            if (s_catalog is null)
            {
                s_catalog = PlanCatalogSeeder.Load(File.ReadAllText(PlanCatalogSeeder.DefaultPath))
                    .ToDictionary(p => p.Key);
            }

            return s_catalog;
        }

        private static Dictionary<string, Plan>? s_catalog;

        /// <summary>
        /// The execution id out of MassTransit's envelope (camelCase on the wire, so a
        /// case-insensitive read is required - the same trap RabbitMqHelper documents).
        /// </summary>
        private static Guid ReadExecutionId(BasicGetResult result)
        {
            using JsonDocument document = JsonDocument.Parse(result.Body);
            JsonElement message = document.RootElement.TryGetProperty("message", out JsonElement inner)
                ? inner
                : document.RootElement;

            foreach (JsonProperty property in message.EnumerateObject())
            {
                if (string.Equals(property.Name, "executionId", StringComparison.OrdinalIgnoreCase))
                {
                    return property.Value.GetGuid();
                }
            }

            throw new InvalidOperationException("The queued message carries no executionId.");
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await _channel.CloseAsync();
                await _channel.DisposeAsync();
                await _connection.CloseAsync();
                await _connection.DisposeAsync();
            }
            catch
            {
                // Best-effort teardown; exclusive/auto-delete queues disappear with the connection.
            }

            await _provider.GetRequiredService<IBusControl>().StopAsync();
            await _provider.DisposeAsync();
            await _db.DisposeAsync();
        }

        private static InferenceApiDbContext NewDb() =>
            new(new DbContextOptionsBuilder<InferenceApiDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    }

    /// <summary>One dequeued message: which run it was, and the AMQP priority it carried.</summary>
    private sealed record QueuedMessage(byte? Priority, Guid ExecutionId);

    private enum PlanKind
    {
        Trial,
        Pro
    }

    /// <summary>A plan source with a per-org subscription and one fallback ("trial") plan.</summary>
    private sealed class FakePlans : IPlanSource
    {
        public Dictionary<Guid, Plan> Subscribed { get; init; } = [];
        public Plan? Fallback { get; init; }

        public Task<Plan?> GetSubscribedPlanAsync(Guid organizationId, CancellationToken cancellationToken)
            => Task.FromResult(Subscribed.TryGetValue(organizationId, out Plan? plan) ? plan : null);

        public Task<Plan?> GetPlanByKeyAsync(string key, CancellationToken cancellationToken)
            => Task.FromResult(Fallback);
    }
}

/// <summary>
/// Skips at discovery time unless <c>REELBOLT_IT_RABBITMQ_URL</c> points at a throwaway broker (see
/// <see cref="RunPriorityQueueIntegrationTests"/> for the one-line docker command).
/// </summary>
public sealed class RealRabbitMqFactAttribute : FactAttribute
{
    public RealRabbitMqFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(RunPriorityIntegrationEnvironment.RabbitMqUrl))
        {
            Skip = "Requires a real RabbitMQ broker. Set REELBOLT_IT_RABBITMQ_URL "
                + "(e.g. amqp://guest:guest@127.0.0.1:5673/ from a throwaway "
                + "'docker run -d --rm -p 5673:5672 rabbitmq:3-alpine'). "
                + "Never point it at the development broker.";
        }
    }
}

/// <summary>The environment contract of <see cref="RunPriorityQueueIntegrationTests"/>.</summary>
public static class RunPriorityIntegrationEnvironment
{
    public const string RabbitMqUrlVariable = "REELBOLT_IT_RABBITMQ_URL";

    public static string RabbitMqUrl =>
        Environment.GetEnvironmentVariable(RabbitMqUrlVariable) ?? string.Empty;
}
