using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using ReelBolt.Inference.Api;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Builds the Inference API's REAL service registrations (<see cref="InferenceApiServiceCollectionExtensions.AddInferenceApiServices"/>,
/// the exact method Program.cs calls) and activates every controller from a scope. Controller unit
/// tests inject mocks, so a constructor dependency that Program.cs never registers stays green there
/// and 500s every action in production — this is the test that catches it (it is how the missing
/// <c>IVideoGenerationClientFactory</c> for <c>InferenceProvidersController</c> was found).
///
/// <para>Offline by construction: configuration is in-memory only, and nothing here connects —
/// EF/Npgsql, the S3 client, MassTransit's bus and the Azure embedding client are all only
/// constructed (or not even that: the embedding generator is lazy); hosted services are never
/// started because the host is built but never run.</para>
/// </summary>
public class InferenceApiCompositionRootTests
{
    [Fact]
    public void Every_controller_activates_from_the_real_service_registrations()
    {
        using var root = CompositionRootHarness.Build(
            "inference-api",
            (services, configuration) => services.AddInferenceApiServices(configuration));

        IReadOnlyList<Type> controllers = CompositionRootHarness.ControllerTypes(typeof(InferenceApiServiceCollectionExtensions).Assembly);
        (int activated, IReadOnlyList<string> failures) = root.ActivateAll(controllers);

        failures.Should().BeEmpty(
            "every Inference API controller must be constructible from the registrations Program.cs uses; failures:\n{0}",
            string.Join("\n", failures));
        controllers.Count.Should().BeGreaterThan(5, "reflection must actually find the API's controllers");
        activated.Should().Be(controllers.Count);
    }

    /// <summary>
    /// a turn wall clock longer than half the stale turn-hold age is a configuration error that
    /// fails the boot (the host runs the startup validators before it serves anything), not the first assistant turn.
    /// </summary>
    [Fact]
    public void A_turn_wall_clock_past_half_the_stale_hold_age_fails_startup_validation()
    {
        using var bad = CompositionRootHarness.Build(
            "inference-api-turn-clock-bad",
            (services, configuration) => services.AddInferenceApiServices(configuration),
            new Dictionary<string, string> { ["Assistant:MaxTurnSeconds"] = "1801" });

        Action boot = () => bad.Services.GetRequiredService<Microsoft.Extensions.Options.IStartupValidator>().Validate();

        boot.Should().Throw<Microsoft.Extensions.Options.OptionsValidationException>()
            .Which.Message.Should().Contain("Assistant:MaxTurnSeconds");

        using var good = CompositionRootHarness.Build(
            "inference-api-turn-clock-good",
            (services, configuration) => services.AddInferenceApiServices(configuration),
            new Dictionary<string, string> { ["Assistant:MaxTurnSeconds"] = "1800" });
        Action fine = () => good.Services.GetRequiredService<Microsoft.Extensions.Options.IStartupValidator>().Validate();
        fine.Should().NotThrow();
    }

    /// <summary>
    /// with <c>Billing:Enabled=true</c> a host that does not register <c>INonWorkflowChargeService</c>
    /// fails the boot instead of letting the four optional-charge controllers skip charging; with billing off it boots.
    /// </summary>
    [Fact]
    public void A_missing_charge_service_with_billing_on_fails_startup_validation()
    {
        static void WithoutChargeService(IServiceCollection services, IConfiguration configuration)
        {
            services.AddInferenceApiServices(configuration);
            services.RemoveAll<ReelBolt.Inference.Api.Services.Billing.INonWorkflowChargeService>();
        }

        using var on = CompositionRootHarness.Build(
            "inference-api-no-charges-on", WithoutChargeService, new Dictionary<string, string?> { ["Billing:Enabled"] = "true" });
        Action bootOn = () => on.Services.GetRequiredService<Microsoft.Extensions.Options.IStartupValidator>().Validate();
        bootOn.Should().Throw<Microsoft.Extensions.Options.OptionsValidationException>()
            .Which.Message.Should().Contain("INonWorkflowChargeService");

        using var off = CompositionRootHarness.Build(
            "inference-api-no-charges-off", WithoutChargeService, new Dictionary<string, string?> { ["Billing:Enabled"] = "false" });
        Action bootOff = () => off.Services.GetRequiredService<Microsoft.Extensions.Options.IStartupValidator>().Validate();
        bootOff.Should().NotThrow();
    }

    [Fact]
    public void NonWorkflowCharging_IsRegistered_AndBindsTheBillingSection()
    {
        using var root = CompositionRootHarness.Build(
            "inference-api-charging",
            (services, configuration) => services.AddInferenceApiServices(configuration),
            new Dictionary<string, string?>
            {
                ["Billing:Enabled"] = "true",
                ["Billing:OverdraftTolerancePercent"] = "15",
                ["Billing:NonWorkflow:TimelineProposalHoldCredits"] = "7",
                ["Billing:NonWorkflow:AssistantTurnHoldCredits"] = "9"
            });

        using IServiceScope scope = root.Services.CreateScope();
        var charges = scope.ServiceProvider.GetRequiredService<ReelBolt.Inference.Api.Services.Billing.INonWorkflowChargeService>();
        charges.Should().BeOfType<ReelBolt.Inference.Api.Services.Billing.NonWorkflowChargeService>();
        (charges.Options.Enabled, charges.Options.OverdraftTolerancePercent, charges.Options.OverdraftFloorCredits, charges.Options.NonWorkflow.TimelineProposalHoldCredits)
            .Should().Be((true, 15, 100L, 7L));
        charges.Options.NonWorkflow.AssistantTurnHoldCredits.Should().Be(9);
        scope.ServiceProvider.GetRequiredService<ReelBolt.Inference.Api.Services.Billing.IAssistantAllowanceService>()
            .Should().BeOfType<ReelBolt.Inference.Api.Services.Billing.AssistantAllowanceService>();
    }
}

/// <summary>
/// Shared by the Inference API and WorkflowEngine composition-root tests. Uses a real
/// <see cref="WebApplicationBuilder"/> so every host-provided service a controller may take
/// (<c>IWebHostEnvironment</c>, <c>IConfiguration</c>, logging, MVC's infrastructure) exists exactly
/// as in production, with all configuration sources replaced by an in-memory set.
/// </summary>
internal sealed class CompositionRootHarness : IDisposable
{
    private readonly WebApplication _app;
    private readonly string _keysPath;

    private CompositionRootHarness(WebApplication app, string keysPath)
    {
        _app = app;
        _keysPath = keysPath;
    }

    public IServiceProvider Services => _app.Services;

    public static CompositionRootHarness Build(
        string name,
        Action<IServiceCollection, IConfiguration> register,
        IDictionary<string, string?>? extraConfig = null)
    {
        string keysPath = Path.Combine(Path.GetTempPath(), $"reelbolt-composition-root-{name}-{Guid.NewGuid():N}");

        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "CompositionRootTest",
            ContentRootPath = AppContext.BaseDirectory
        });

        // Drop appsettings.json / env vars / command line: nothing from the machine leaks in.
        builder.Configuration.Sources.Clear();
        var config = new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = "Host=127.0.0.1;Port=1;Database=composition_root_test;Username=x;Password=x",
            ["Jwt:Issuer"] = "test",
            ["Jwt:Audience"] = "test",
            ["Jwt:SigningKey"] = "composition-root-test-signing-key-0123456789",
            ["DataProtection:KeysPath"] = keysPath,
            ["MinIO:Endpoint"] = "http://127.0.0.1:1",
            ["MinIO:AccessKey"] = "test",
            ["MinIO:SecretKey"] = "test",
            ["RabbitMQ:Host"] = "127.0.0.1",
            ["AzureOpenAI:Endpoint"] = "http://127.0.0.1:1",
            ["AzureOpenAI:ApiKey"] = "test"
        };
        if (extraConfig is not null)
        {
            foreach (KeyValuePair<string, string?> kv in extraConfig)
            {
                config[kv.Key] = kv.Value;
            }
        }
        builder.Configuration.AddInMemoryCollection(config);

        builder.Host.UseDefaultServiceProvider(options =>
        {
            options.ValidateScopes = true;
            options.ValidateOnBuild = true;
        });

        register(builder.Services, builder.Configuration);

        // Build only: hosted services start in RunAsync/StartAsync, which is never called.
        return new CompositionRootHarness(builder.Build(), keysPath);
    }

    public static IReadOnlyList<Type> ControllerTypes(Assembly assembly) =>
        assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(ControllerBase).IsAssignableFrom(t))
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Activates each type from its own scope and collects every failure (controller name plus the
    /// container's message, which names the unresolvable service) so one run reports all gaps.
    /// </summary>
    public (int Activated, IReadOnlyList<string> Failures) ActivateAll(IEnumerable<Type> types)
    {
        int activated = 0;
        var failures = new List<string>();
        foreach (Type type in types)
        {
            using IServiceScope scope = Services.CreateScope();
            try
            {
                object instance = ActivatorUtilities.CreateInstance(scope.ServiceProvider, type);
                if (instance is not null)
                {
                    activated++;
                }
            }
            catch (Exception ex)
            {
                failures.Add($"{type.Name}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        return (activated, failures);
    }

    public void Dispose()
    {
        ((IDisposable)_app).Dispose();
        if (Directory.Exists(_keysPath))
        {
            Directory.Delete(_keysPath, recursive: true);
        }
    }
}
