using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Reflection;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Services.Video;
using ReelBolt.WorkflowEngine.Services.VideoGeneration;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Proves <see cref="VideoGenerationServiceCollectionExtensions.AddVideoGeneration"/> makes
/// <c>StepType.VideoGenerate</c> runnable: the executor and everything it needs resolve from a
/// provider built with scope validation on (so a singleton capturing a scoped service fails the
/// build), and the <c>VideoGeneration:*</c> section actually binds.
/// </summary>
public class VideoGenerationRegistrationTests
{
    private static ServiceProvider BuildProvider(IDictionary<string, string> config)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(config)
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();

        // External dependencies registered elsewhere in Program.cs — mocked here. The last three
        // are what the plan path cuts a planned shot's frame with: the sole seam to the machine that
        // holds the media, the ffmpeg keyframe operation, and the runner entry point the seam uses.
        services.AddSingleton(Mock.Of<IInferenceProviderResolver>());
        services.AddSingleton(Mock.Of<IProjectFileWorkspace>());
        services.AddSingleton(Mock.Of<IVideoToolRunner>());
        services.AddSingleton(Mock.Of<ReelBolt.WorkflowEngine.Services.Compute.IMediaWorkspaceFactory>());
        services.AddSingleton(Mock.Of<IKeyframeExtractor>());
        services.AddDbContextFactory<WorkflowEngineDbContext>(o =>
            o.UseInMemoryDatabase($"video-generation-registration-{Guid.NewGuid()}"));

        services.AddVideoGeneration(configuration);

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        });
    }

    [Fact]
    public void AddVideoGeneration_registers_exactly_one_VideoGenerateStepExecutor()
    {
        using ServiceProvider provider = BuildProvider(new Dictionary<string, string>());

        IEnumerable<IStepExecutor> executors = provider.GetRequiredService<IEnumerable<IStepExecutor>>();

        executors.OfType<VideoGenerateStepExecutor>().Should().ContainSingle();
    }

    [Fact]
    public void AddVideoGeneration_registers_gate_client_factory_and_normalizer_as_singletons()
    {
        using ServiceProvider provider = BuildProvider(new Dictionary<string, string>());

        provider.GetRequiredService<IVideoGenerationBudgetGate>()
            .Should().BeOfType<VideoGenerationBudgetGate>()
            .And.BeSameAs(provider.GetRequiredService<IVideoGenerationBudgetGate>());
        provider.GetRequiredService<IVideoGenerationClientFactory>()
            .Should().BeOfType<VideoGenerationClientFactory>()
            .And.BeSameAs(provider.GetRequiredService<IVideoGenerationClientFactory>());
        provider.GetRequiredService<GeneratedClipNormalizer>()
            .Should().BeSameAs(provider.GetRequiredService<GeneratedClipNormalizer>());
    }

    [Fact]
    public void AddVideoGeneration_binds_the_VideoGeneration_section_into_the_gates_options()
    {
        using ServiceProvider provider = BuildProvider(new Dictionary<string, string>
        {
            ["VideoGeneration:GlobalDailyBudgetCredits"] = "123.45",
            ["VideoGeneration:DailyBudgetCredits"] = "6.78"
        });

        VideoGenerationOptions bound = provider.GetRequiredService<IOptions<VideoGenerationOptions>>().Value;
        bound.GlobalDailyBudgetCredits.Should().Be(123.45m);
        bound.DailyBudgetCredits.Should().Be(6.78m);

        // And the gate the executor actually gets was constructed from exactly that bound value.
        var gate = (VideoGenerationBudgetGate)provider.GetRequiredService<IVideoGenerationBudgetGate>();
        FieldInfo optionsField = typeof(VideoGenerationBudgetGate)
            .GetField("_options", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var gateOptions = (VideoGenerationOptions)optionsField.GetValue(gate)!;
        gateOptions.GlobalDailyBudgetCredits.Should().Be(123.45m);
        gateOptions.DailyBudgetCredits.Should().Be(6.78m);
    }

    /// <summary>
    /// The shipped engine <c>appsettings.json</c> states the daily caps in credits now, and their
    /// figures must be the credit equivalents of the dollar defaults they replaced ($20 and $50), not
    /// the bare numbers — a cap silently divided by 166 would be a 166x tighter spend limit.
    /// </summary>
    [Fact]
    public void The_shipped_safety_ceiling_is_the_credit_equivalent_of_five_thousand_dollars_and_no_project_cap_ships()
    {
        // The shipped file is JSONC: it carries comments, so they are skipped rather than stripped.
        using JsonDocument appsettings = JsonDocument.Parse(
            File.ReadAllText("WorkflowEngine.appsettings.json"),
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        JsonElement section = appsettings.RootElement.GetProperty(VideoGenerationOptions.SectionName);

        // No per-project figure ships: the daily limit is the user's own setting.
        section.TryGetProperty(nameof(VideoGenerationOptions.DailyBudgetCredits), out _).Should().BeFalse();
        section.GetProperty(nameof(VideoGenerationOptions.GlobalDailyBudgetCredits)).GetDecimal()
            .Should().Be(CreditCurrency.CreditsFromUsd(5000m));

        // And the code's own defaults are the same, so a deployment that omits the section behaves like one that ships it.
        var defaults = new VideoGenerationOptions();
        defaults.DailyBudgetCredits.Should().BeNull();
        defaults.GlobalDailyBudgetCredits.Should().Be(CreditCurrency.CreditsFromUsd(5000m));
    }

    [Fact]
    public void AddDbContextFactory_resolves_both_the_singleton_factory_and_a_scoped_context()
    {
        // Mirrors Program.cs's registration shape: the factory the singleton gate/executor need,
        // plus the scoped WorkflowEngineDbContext every other consumer already injects.
        var services = new ServiceCollection();
        services.AddDbContextFactory<WorkflowEngineDbContext>(options =>
            options.UseNpgsql("Host=localhost;Database=none;Username=x;Password=y",
                npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory_Workflow")));

        using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        });

        services.Single(d => d.ServiceType == typeof(IDbContextFactory<WorkflowEngineDbContext>))
            .Lifetime.Should().Be(ServiceLifetime.Singleton);
        services.Single(d => d.ServiceType == typeof(WorkflowEngineDbContext))
            .Lifetime.Should().Be(ServiceLifetime.Scoped);

        provider.GetRequiredService<IDbContextFactory<WorkflowEngineDbContext>>().Should().NotBeNull();
        using IServiceScope scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>().Should().NotBeNull();
    }
}
