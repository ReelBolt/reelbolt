using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using ReelBolt.Shared.Observability;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

public class ReelBoltTelemetryTests
{
    private static IConfiguration Config(string? endpoint) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection([new KeyValuePair<string, string?>("OTEL_EXPORTER_OTLP_ENDPOINT", endpoint)])
            .Build();

    private sealed class Capture(List<Activity> sink) : BaseProcessor<Activity>
    {
        public override void OnEnd(Activity data) => sink.Add(data);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoEndpoint_DoesNotEnableExport_AndProvidersStillBuild(string? endpoint)
    {
        var services = new ServiceCollection();

        bool exporting = services.AddReelBoltTelemetry(Config(endpoint), "Test.Service", _ => null);

        exporting.Should().BeFalse();
        using ServiceProvider sp = services.BuildServiceProvider();
        // Startup unchanged: both providers resolve without any collector configured.
        sp.GetRequiredService<TracerProvider>().Should().NotBeNull();
        sp.GetRequiredService<MeterProvider>().Should().NotBeNull();
    }

    [Fact]
    public void EndpointSet_EnablesExport()
    {
        var services = new ServiceCollection();

        services.AddReelBoltTelemetry(Config("http://127.0.0.1:1"), "Test.Service", _ => null).Should().BeTrue();
        using ServiceProvider sp = services.BuildServiceProvider();
        sp.GetRequiredService<TracerProvider>().Should().NotBeNull();
    }

    [Fact]
    public void OrganizationProcessor_TagsOnEnd_WhenUnknownAtStart_AndNeverOverwrites()
    {
        Guid? org = null;
        var exported = new List<Activity>();
        using var source = new ActivitySource("Test.Org");
        using TracerProvider provider = Sdk.CreateTracerProviderBuilder()
            .AddSource("Test.Org")
            .AddProcessor(new OrganizationSpanProcessor(() => org))
            .AddProcessor(new Capture(exported))
            .Build()!;

        Guid late = Guid.NewGuid();
        using (source.StartActivity("late"))
        {
            org = late; // e.g. the JWT was validated after the span began
        }
        using (Activity? b = source.StartActivity("explicit"))
        {
            b!.SetTag(ReelBoltTelemetry.OrgIdTag, "explicit-org");
        }
        org = null;
        using (source.StartActivity("none")) { }

        exported.Single(x => x.OperationName == "late").GetTagItem(ReelBoltTelemetry.OrgIdTag).Should().Be(late.ToString());
        exported.Single(x => x.OperationName == "explicit").GetTagItem(ReelBoltTelemetry.OrgIdTag).Should().Be("explicit-org");
        exported.Single(x => x.OperationName == "none").GetTagItem(ReelBoltTelemetry.OrgIdTag).Should().BeNull();
    }

    [Theory]
    [InlineData("pro", "pro")]
    [InlineData("PRO", "pro")]
    [InlineData(null, "unknown")]
    [InlineData("acme-corp-42", "other")]
    public void PlanTier_IsBoundedSet(string? input, string expected)
        => ReelBoltTelemetry.NormalizePlanTier(input).Should().Be(expected);

    [Fact]
    public void ComputeTarget_IsBoundedSet()
    {
        ReelBoltTelemetry.NormalizeComputeTarget("cloud").Should().Be("cloud");
        ReelBoltTelemetry.NormalizeComputeTarget("Runner").Should().Be("runner");
        ReelBoltTelemetry.NormalizeComputeTarget("some-vm-id").Should().Be("unknown");
    }
}
