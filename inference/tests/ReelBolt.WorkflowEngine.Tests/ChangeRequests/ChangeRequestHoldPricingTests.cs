using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Inference.Api.Services.ChangeRequests;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.ChangeRequests;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Services.Billing;
using ReelBolt.WorkflowEngine.Services.Messaging;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.ChangeRequests;

/// <summary>
/// A change request that re-makes an audio take LONGER than configured is held (and estimated) at the longer price;
/// an ordinary run, a carried take and a shorter override are priced exactly as before.
/// </summary>
public class ChangeRequestHoldPricingTests
{
    private static readonly Guid ProjectId = Guid.NewGuid();

    private const string AudioConfig = """{"kind":"Music","prompt":"calm","durationSec":10,"takes":2}""";

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, JsonElement>> Overrides(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, JsonElement>>>(json)!
            .ToDictionary(p => p.Key, p => (IReadOnlyDictionary<string, JsonElement>)p.Value);

    private static double DurationOf(string? configJson) =>
        JsonSerializer.Deserialize<AudioGenerateStepConfig>(configJson!, AudioGenerateStepConfig.JsonOptions)!.DurationSec;

    [Fact]
    public void A_longer_override_raises_the_priced_duration_and_nothing_else_changes_it()
    {
        DurationOf(StepChangePricing.AudioConfigWithOverrides(AudioConfig, Overrides("""{"a2":{"durationSec":100}}"""))).Should().Be(100);
        DurationOf(StepChangePricing.AudioConfigWithOverrides(AudioConfig, Overrides("""{"a1":{"durationSec":30},"*":{"durationSec":60}}"""))).Should().Be(60);

        StepChangePricing.AudioConfigWithOverrides(AudioConfig, Overrides("""{"a2":{"durationSec":5}}""")).Should().BeSameAs(AudioConfig, "a shorter take is held at the configured length");
        StepChangePricing.AudioConfigWithOverrides(AudioConfig, Overrides("""{"a2":{"prompt":"x"}}""")).Should().BeSameAs(AudioConfig);
        StepChangePricing.AudioConfigWithOverrides(AudioConfig, (IReadOnlyDictionary<string, IReadOnlyDictionary<string, JsonElement>>?)null).Should().BeSameAs(AudioConfig);
        StepChangePricing.AudioConfigWithOverrides(AudioConfig, """{"a2":{"durationSec":100}}""").Should().Contain("100");
    }

    /// <summary>Prices an audio config at 1 credit-millis per second per take, and records the config it was asked about.</summary>
    private sealed class RecordingHold : ICreditHoldEstimator
    {
        public string? AudioConfigSeen { get; private set; }

        public ValueTask<long> HoldMillisAsync(Guid organizationId, StepType stepType, Guid? agentDefinitionId, CancellationToken ct = default) =>
            ValueTask.FromResult(1L);

        public ValueTask<long> RunHoldMillisAsync(Guid organizationId, IReadOnlyList<WorkflowStep> runSteps, WorkflowStep step, int? outputResolution, CancellationToken ct = default) =>
            ValueTask.FromResult(1L);

        public ValueTask<long?> TryVideoGenerateHoldMillisAsync(string? videoGenerateConfigJson, CancellationToken ct = default) =>
            ValueTask.FromResult<long?>(null);

        public ValueTask<long?> TryAudioGenerateHoldMillisAsync(string? audioGenerateConfigJson, CancellationToken ct = default)
        {
            AudioConfigSeen = audioGenerateConfigJson;
            AudioGenerateStepConfig c = JsonSerializer.Deserialize<AudioGenerateStepConfig>(audioGenerateConfigJson!, AudioGenerateStepConfig.JsonOptions)!;
            return ValueTask.FromResult<long?>((long)(c.Takes * c.DurationSec * 1000));
        }
    }

    private static (WorkflowExecutorService Executor, WorkflowExecution Execution, RecordingHold Hold) Engine(
        WorkflowDefinitionKind kind, string? overridesJson, double fraction)
    {
        string dbName = "hold-pricing-" + Guid.NewGuid().ToString("N");
        ServiceCollection services = new();
        services.AddScoped(_ => new WorkflowEngineDbContext(new DbContextOptionsBuilder<WorkflowEngineDbContext>().UseInMemoryDatabase(dbName).Options));
        ServiceProvider provider = services.BuildServiceProvider();
        WorkflowExecution execution = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = ProjectId,
            OrganizationId = Guid.NewGuid(),
            WorkflowDefinitionId = Guid.NewGuid(),
            WorkflowDefinition = new WorkflowDefinition { Kind = kind }
        };
        using (IServiceScope scope = provider.CreateScope())
        {
            WorkflowEngineDbContext db = scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();
            db.StepChangeRequests.Add(new StepChangeRequest
            {
                Id = Guid.NewGuid(),
                ProjectId = ProjectId,
                OrganizationId = execution.OrganizationId,
                SourceExecutionId = Guid.NewGuid(),
                TargetStepOrder = 2,
                StepType = "AudioGenerate",
                ExecutionId = execution.Id,
                ScopeFraction = fraction,
                OverridesJson = overridesJson
            });
            db.SaveChanges();
        }

        RecordingHold hold = new();
        WorkflowExecutorService executor = new(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Mock.Of<IWorkflowEventPublisher>(),
            NullLogger<WorkflowExecutorService>.Instance,
            [],
            new RabbitMqHelper(new ConfigurationBuilder().Build()),
            Options.Create(new WorkflowHardeningOptions()),
            new ExecutionCancellationRegistry(),
            creditOptions: Options.Create(new StepCreditOptions()),
            creditHoldEstimator: hold);
        return (executor, execution, hold);
    }

    private static WorkflowStep AudioStep() => new() { StepOrder = 2, StepType = StepType.AudioGenerate, AudioGenerateConfigJson = AudioConfig };

    [Fact]
    public async Task A_take_re_made_ten_times_longer_is_held_at_least_at_its_real_cost()
    {
        // a2 re-made at 100 s (10x the configured 10 s): the scope fraction is capped at 1 (100 / 20 s of the step).
        (WorkflowExecutorService executor, WorkflowExecution execution, RecordingHold hold) =
            Engine(WorkflowDefinitionKind.CompositionReplay, """{"a2":{"durationSec":100}}""", fraction: 1d);
        WorkflowStep target = AudioStep();

        long reserve = await executor.ResolveStepReserveMillisAsync(execution, target, CancellationToken.None, [target]);

        long realCostOfTheTake = 100 * 1000;
        reserve.Should().BeGreaterThanOrEqualTo(realCostOfTheTake);
        DurationOf(hold.AudioConfigSeen).Should().Be(100);
        DurationOf(target.AudioGenerateConfigJson).Should().Be(10, "the run itself still executes the unchanged step config");
    }

    [Fact]
    public async Task A_partial_scope_with_a_long_override_still_covers_the_re_made_take()
    {
        // 2 takes x 10 s configured; a2 at 100 s: redone weight 100 of a total 20 -> fraction 1; a1 at 12 s -> 12/20.
        (WorkflowExecutorService executor, WorkflowExecution execution, _) =
            Engine(WorkflowDefinitionKind.CompositionReplay, """{"a1":{"durationSec":12}}""", fraction: 12d / 20d);
        WorkflowStep target = AudioStep();

        long reserve = await executor.ResolveStepReserveMillisAsync(execution, target, CancellationToken.None, [target]);

        reserve.Should().BeGreaterThanOrEqualTo(12 * 1000L);
    }

    [Fact]
    public async Task An_ordinary_run_and_a_shorter_override_are_priced_from_the_unchanged_config()
    {
        (WorkflowExecutorService executor, WorkflowExecution execution, RecordingHold hold) =
            Engine(WorkflowDefinitionKind.ExecutionSnapshot, """{"a2":{"durationSec":100}}""", fraction: 1d);
        (await executor.ResolveStepReserveMillisAsync(execution, AudioStep(), CancellationToken.None, [AudioStep()])).Should().Be(20_000);
        hold.AudioConfigSeen.Should().Be(AudioConfig, "byte-identical");

        (WorkflowExecutorService e2, WorkflowExecution x2, RecordingHold h2) =
            Engine(WorkflowDefinitionKind.CompositionReplay, """{"a2":{"durationSec":5}}""", fraction: 0.25);
        (await e2.ResolveStepReserveMillisAsync(x2, AudioStep(), CancellationToken.None, [AudioStep()])).Should().Be(5_000);
        h2.AudioConfigSeen.Should().Be(AudioConfig);
    }
}
