using System;
using System.Linq;
using System.Reflection;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Review;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Review;

/// <summary>
/// The visual check needs no registration of its own: it composes from the engine's REAL registrations
/// (<see cref="WorkflowEngineServiceCollectionExtensions.AddWorkflowEngineServices"/>), the ones the
/// ReviewLoop executor's <c>IServiceProvider</c> sees. Offline: the host is built, never started.
/// </summary>
public class ReviewVisualCheckCompositionTests
{
    [Fact]
    public void The_visual_check_composes_from_the_real_engine_registrations()
    {
        using CompositionRootHarness root = CompositionRootHarness.Build(
            "workflow-engine",
            (services, configuration) => services.AddWorkflowEngineServices(configuration));

        ReviewVisualCheck.TryCreate(root.Services).Should().BeOfType<ReviewVisualCheck>();
    }

    [Fact]
    public void The_registered_review_loop_executor_is_handed_the_check()
    {
        using CompositionRootHarness root = CompositionRootHarness.Build(
            "workflow-engine",
            (services, configuration) => services.AddWorkflowEngineServices(configuration));
        using IServiceScope scope = root.Services.CreateScope();

        ReviewLoopStepExecutor executor = scope.ServiceProvider.GetServices<IStepExecutor>().OfType<ReviewLoopStepExecutor>().Single();
        var check = (Lazy<IReviewVisualCheck?>)typeof(ReviewLoopStepExecutor)
            .GetField("_visualCheck", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(executor)!;

        check.Value.Should().BeOfType<ReviewVisualCheck>();
    }
}
