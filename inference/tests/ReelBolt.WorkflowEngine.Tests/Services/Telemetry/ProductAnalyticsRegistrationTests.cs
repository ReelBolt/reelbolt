#nullable enable
using System;
using System.Linq;
using System.Reflection;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Middleware;
using ReelBolt.Inference.Api.Services.Projects;
using ReelBolt.Inference.Api.Services.Telemetry;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Services.Telemetry;

public class ProductAnalyticsRegistrationTests
{
    [Fact]
    public void The_credits_filter_emitter_and_retention_service_are_registered_in_the_real_root()
    {
        using var root = CompositionRootHarness.Build(
            "inference-api",
            (services, configuration) => services.AddInferenceApiServices(configuration));

        MvcOptions mvc = root.Services.GetRequiredService<IOptions<MvcOptions>>().Value;
        mvc.Filters.OfType<ServiceFilterAttribute>().Should()
            .Contain(f => f.ServiceType == typeof(CreditsExhaustedResultFilter), "every 402 INSUFFICIENT_CREDITS passes the filter");

        using IServiceScope scope = root.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<CreditsExhaustedResultFilter>().Should().NotBeNull();
        scope.ServiceProvider.GetRequiredService<IProductEventEmitter>().Should().BeOfType<ProductEventEmitter>();
        root.Services.GetServices<IHostedService>().Should().ContainSingle(h => h is ProductEventsRetentionService);
    }

    /// <summary>
    /// The emitter reaches the first-upload paths and the project-starter path through OPTIONAL constructor
    /// parameters, so a missing registration would compile, run and silently record nothing. Each type is
    /// built the way the application builds it and must come out holding the registered emitter.
    /// </summary>
    [Fact]
    public void The_upload_paths_and_the_project_creation_path_are_given_the_emitter_by_the_real_root()
    {
        using var root = CompositionRootHarness.Build(
            "inference-api",
            (services, configuration) => services.AddInferenceApiServices(configuration));
        using IServiceScope scope = root.Services.CreateScope();
        IServiceProvider sp = scope.ServiceProvider;

        static object? Field(object owner, string name) =>
            owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner);

        Field(sp.GetRequiredService<AssistantUploadTools>(), "_productEvents")
            .Should().BeOfType<ProductEventEmitter>("the assistant's upload and import tools record a first upload");
        Field(ActivatorUtilities.CreateInstance<ProjectFilesController>(sp), "_productEvents")
            .Should().BeOfType<ProductEventEmitter>("the form upload, the direct upload completion and the import from a link record a first upload");

        object creation = sp.GetRequiredService<IProjectCreationService>();
        creation.Should().BeOfType<ProjectCreationService>();
        creation.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Select(f => f.GetValue(creation)).OfType<IProductEventEmitter>()
            .Should().ContainSingle("a project created with a starter recipe records the choice").Which.Should().BeOfType<ProductEventEmitter>();

        // The welcome answers are written through the event log itself, and the controller builds with or without a logger.
        ActivatorUtilities.CreateInstance<OnboardingController>(sp).Should().NotBeNull();
        sp.GetRequiredService<IProductEventLog>().Should().BeOfType<ProductEventLog>();
    }
}
