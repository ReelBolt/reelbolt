using System;
using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using ReelBolt.Shared.Metering;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Builds the WorkflowEngine's REAL service registrations (<see cref="WorkflowEngineServiceCollectionExtensions.AddWorkflowEngineServices"/>,
/// the exact method Program.cs calls) with scope validation on, then activates every controller and
/// resolves the full <see cref="IStepExecutor"/> set. Offline: in-memory configuration only, and the
/// host is built but never started, so no hosted service (worker pool, execution runner, MassTransit
/// bus) runs and nothing connects to Postgres/RabbitMQ/S3.
/// </summary>
public class WorkflowEngineCompositionRootTests
{
    private static CompositionRootHarness BuildEngine() =>
        CompositionRootHarness.Build(
            "workflow-engine",
            (services, configuration) => services.AddWorkflowEngineServices(configuration));

    [Fact]
    public void Every_controller_activates_from_the_real_service_registrations()
    {
        using CompositionRootHarness root = BuildEngine();

        IReadOnlyList<Type> controllers = CompositionRootHarness.ControllerTypes(typeof(WorkflowEngineServiceCollectionExtensions).Assembly);
        (int activated, IReadOnlyList<string> failures) = root.ActivateAll(controllers);

        failures.Should().BeEmpty(
            "every WorkflowEngine controller must be constructible from the registrations Program.cs uses; failures:\n{0}",
            string.Join("\n", failures));
        controllers.Should().NotBeEmpty("reflection must actually find the engine's controllers");
        activated.Should().Be(controllers.Count);
    }

    [Fact]
    public void Every_step_executor_resolves_from_the_real_service_registrations()
    {
        using CompositionRootHarness root = BuildEngine();
        using IServiceScope scope = root.Services.CreateScope();

        IStepExecutor[] executors = scope.ServiceProvider.GetServices<IStepExecutor>().ToArray();

        // Every concrete executor in the engine assembly must be registered, not only "some".
        Type[] expected = typeof(WorkflowEngineServiceCollectionExtensions).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(IStepExecutor).IsAssignableFrom(t))
            .ToArray();
        expected.Should().Contain(typeof(VideoGenerateStepExecutor));
        executors.Select(e => e.GetType()).Should().BeEquivalentTo(expected);
    }

    /// <summary>Collects the names of the metrics the engine's OpenTelemetry meter provider exports.</summary>
    private sealed class CollectingExporter : BaseExporter<Metric>
    {
        public List<string> Names { get; } = [];

        public override ExportResult Export(in Batch<Metric> batch)
        {
            foreach (Metric metric in batch) Names.Add(metric.Name);
            return ExportResult.Success;
        }
    }

    [Fact]
    public void The_engine_exports_the_unpriced_usage_counter()
    {
        CollectingExporter exporter = new();
        using CompositionRootHarness root = CompositionRootHarness.Build(
            "workflow-engine-meters",
            (services, configuration) =>
            {
                services.AddWorkflowEngineServices(configuration);
                services.ConfigureOpenTelemetryMeterProvider(b => b.AddReader(new BaseExportingMetricReader(exporter)));
            });
        MeterProvider meters = root.Services.GetRequiredService<MeterProvider>();

        UnpricedUsageDiagnostics.UnpricedUsage.Add(1,
            new KeyValuePair<string, object>("source", "Workflow"), new KeyValuePair<string, object>("capability", "Chat"));
        meters.ForceFlush().Should().BeTrue();

        exporter.Names.Should().Contain("reelbolt.billing.unpriced_usage",
            "a step's unpriced platform usage is metered in the engine, so the engine must export the counter");
    }

    [Fact]
    public void Workflow_executor_service_resolves_from_a_scope()
    {
        using CompositionRootHarness root = BuildEngine();
        using IServiceScope scope = root.Services.CreateScope();

        scope.ServiceProvider.GetRequiredService<WorkflowExecutorService>().Should().NotBeNull();
    }
}
