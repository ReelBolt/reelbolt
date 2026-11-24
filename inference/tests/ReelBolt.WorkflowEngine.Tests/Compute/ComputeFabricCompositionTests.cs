using System;
using System.Net.Http;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ReelBolt.WorkflowEngine.Services.Compute;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Compute;

/// <summary>
/// D6's registrations, exercised through the REAL composition root (the same
/// <c>AddWorkflowEngineServices</c> Program.cs calls), so the selector, the gateway client and the
/// session gate are proven to resolve here rather than at the first runner-backed step in production.
/// <para>
/// The singleton assertions are not cosmetic: the session gate and the client are the two pieces that
/// must be process-wide for "one open session per execution" to hold at all, and a lifetime change
/// that quietly made them per-scope would break that invariant while every other test stayed green.
/// </para>
/// </summary>
public class ComputeFabricCompositionTests
{
    private static CompositionRootHarness Build() =>
        CompositionRootHarness.Build(
            "workflow-engine",
            (services, configuration) => services.AddWorkflowEngineServices(configuration));

    [Fact]
    public void The_compute_graph_resolves_from_the_real_registrations()
    {
        using CompositionRootHarness root = Build();
        using IServiceScope scope = root.Services.CreateScope();

        scope.ServiceProvider.GetRequiredService<IComputeTarget>().Should().BeOfType<ComputeTargetSelector>();
        scope.ServiceProvider.GetRequiredService<IComputeEntitlements>().Should().BeOfType<DbComputeEntitlements>();
        scope.ServiceProvider.GetRequiredService<IExecutionSessionGate>().Should().BeOfType<ExecutionSessionGate>();
        scope.ServiceProvider.GetRequiredService<RunnerGatewayClient>().Should().NotBeNull();

        // The named client exists and points at the gateway. It is cluster-internal and never
        // published, so its base address is the only place that address is written down.
        HttpClient gateway = root.Services.GetRequiredService<IHttpClientFactory>().CreateClient(RunnerGatewayClient.HttpClientName);
        gateway.BaseAddress.Should().Be(new Uri("http://runner-gateway:8090"));
    }

    [Fact]
    public void The_session_gate_and_the_client_are_process_wide()
    {
        using CompositionRootHarness root = Build();
        using IServiceScope first = root.Services.CreateScope();
        using IServiceScope second = root.Services.CreateScope();

        first.ServiceProvider.GetRequiredService<IExecutionSessionGate>()
            .Should().BeSameAs(second.ServiceProvider.GetRequiredService<IExecutionSessionGate>(),
                "a per-scope gate would let two scopes each hold 'the' session of one execution");
        first.ServiceProvider.GetRequiredService<RunnerGatewayClient>()
            .Should().BeSameAs(second.ServiceProvider.GetRequiredService<RunnerGatewayClient>());
        first.ServiceProvider.GetRequiredService<IComputeTarget>()
            .Should().BeSameAs(second.ServiceProvider.GetRequiredService<IComputeTarget>());
    }

    [Fact]
    public void The_media_workspace_factory_is_wired_to_the_session_registry_the_parking_needs()
    {
        // The parking parameter is optional ONLY so a test can build the factory without one. When the
        // engine builds it, the registry must be there: a factory without one never parks the
        // execution's workspace session, and a runner-backed VideoAnalyze/VideoCompile after a sandbox
        // step would then wait on that session's slot for Compute:SessionWaitSeconds and fail
        // RUNNER_SESSION_BUSY - the deadlock the parking exists to close. Resolved through the real
        // root, so a registration that lives only in a test harness cannot satisfy this.
        using CompositionRootHarness root = Build();
        using IServiceScope scope = root.Services.CreateScope();

        IMediaWorkspaceFactory factory = scope.ServiceProvider.GetRequiredService<IMediaWorkspaceFactory>();

        MediaWorkspaceFactory concrete = factory.Should().BeOfType<MediaWorkspaceFactory>().Subject;
        concrete.SandboxSessions.Should().BeSameAs(
            scope.ServiceProvider.GetRequiredService<ISandboxSessionRegistry>(),
            "the engine's own container is what supplies the parking seam");
        concrete.SandboxSessions.Should().NotBeNull();
    }

    [Fact]
    public void WithNo_compute_configuration_at_all_the_engine_is_in_Local_mode()
    {
        // The whole self-host guarantee, asserted from the composition root: the harness configures
        // nothing under Compute, and the engine still comes up in Local mode with the defaults the
        // plan requires (a 5 s runner query cache, a 30 s job wait).
        using CompositionRootHarness root = Build();
        using IServiceScope scope = root.Services.CreateScope();

        ComputeOptions options = scope.ServiceProvider.GetRequiredService<IOptions<ComputeOptions>>().Value;

        options.Mode.Should().Be(ComputeMode.Local);
        options.RunnerQueryCacheSeconds.Should().Be(5);
        options.JobWaitSeconds.Should().Be(30);
        options.SessionWaitSeconds.Should().Be(900);
        options.ResolveInternalToken().Should().Be(
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("RUNNER_INTERNAL_TOKEN"))
                ? null
                : Environment.GetEnvironmentVariable("RUNNER_INTERNAL_TOKEN")!.Trim());
    }
}
