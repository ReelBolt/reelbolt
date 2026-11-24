#nullable enable
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using ReelBolt.Inference.Api;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Services.Outputs;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Outputs;

/// <summary>The output-link services, the route and the output tools all build from the registrations Program.cs uses.</summary>
public class OutputServicesRegistrationTests
{
    [Fact]
    public void The_output_link_route_and_tools_resolve_from_the_real_registrations()
    {
        using var root = CompositionRootHarness.Build(
            "inference-api",
            (services, configuration) => services.AddInferenceApiServices(configuration));

        (int activated, var failures) = root.ActivateAll([typeof(OutputLinksController)]);
        failures.Should().BeEmpty();
        activated.Should().Be(1);

        using IServiceScope scope = root.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<AssistantOutputTools>().Should().NotBeNull();
        scope.ServiceProvider.GetRequiredService<IOutputLinkService>().Should().BeSameAs(root.Services.GetRequiredService<IOutputLinkService>());
        scope.ServiceProvider.GetRequiredService<IOutputLinkResolver>().Should().BeOfType<OutputLinkResolver>();
        scope.ServiceProvider.GetRequiredService<IStorageObjectCopier>().Should().BeOfType<S3StorageObjectCopier>();
    }
}
