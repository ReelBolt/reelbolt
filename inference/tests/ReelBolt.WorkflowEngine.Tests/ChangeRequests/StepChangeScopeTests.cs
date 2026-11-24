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
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows.ChangeRequests;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Services.Billing;
using ReelBolt.WorkflowEngine.Services.Messaging;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.ChangeRequests;

/// <summary>
/// The share of a target step's items a change request re-makes, and what it does to the step's credit hold and the
/// API estimate (docs/change-requests.md "What it costs"). Pure maths plus the engine's per-step hold; no paid call.
/// </summary>
public class StepChangeScopeTests
{
    private static readonly Guid ProjectId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    private static string VoiceoverOutput(int lines) => JsonSerializer.Serialize(new
    {
        view = new { lines = Enumerable.Range(0, lines).Select(i => new { id = $"vo{i}", status = "ok", durationSec = 2.0, text = $"Line {i}" }).ToArray() }
    });

    private static string ClipsOutput(int shots, int takes) => JsonSerializer.Serialize(new
    {
        clips = Enumerable.Range(1, shots).SelectMany(s => Enumerable.Range(1, takes).Select(t => new
        {
            clipId = takes == 1 ? $"v{s}" : $"v{s}t{t}",
            status = "succeeded",
            description = $"Prompt of shot {s}",
            durationSeconds = 6
        })).ToArray()
    });

    private static ChangeRequestPayload Payload(string[]? ids = null, params (string Item, string Key, object Value)[] overrides)
    {
        Dictionary<string, IReadOnlyDictionary<string, JsonElement>> map = [];
        foreach (var group in overrides.GroupBy(o => o.Item))
        {
            map[group.Key] = group.ToDictionary(o => o.Key, o => JsonSerializer.SerializeToElement(o.Value));
        }

        return new ChangeRequestPayload("instruction", ids, map);
    }

    // ------------------------------------------------------------------ the fraction

    [Fact]
    public void One_of_five_voiceover_lines_is_a_fifth()
    {
        double f = StepChangeScope.Compute(StepType.Voiceover, Payload(["vo2"], ("vo2", "text", "New")), VoiceoverOutput(5));

        f.Should().BeApproximately(0.2, 1e-9);
    }

    private static string MultibyteVoiceover() => JsonSerializer.Serialize(new
    {
        view = new
        {
            lines = new[]
        {
            new { id = "vo0", status = "ok", durationSec = 2.0, text = "\u4f60\u597d\u4f60\u597d" }, // 4 chars, 12 bytes
            new { id = "vo1", status = "ok", durationSec = 2.0, text = "abcd" },                         // 4 chars, 4 bytes
            new { id = "vo2", status = "ok", durationSec = 2.0, text = "abcd" },
            new { id = "vo3", status = "ok", durationSec = 2.0, text = "abcd" }
        }
        }
    });

    [Fact]
    public void Voiceover_is_weighted_by_utf8_bytes_not_characters()
    {
        // old text: 12 of 24 bytes (by characters it would be 4 of 16 = 0.25)
        StepChangeScope.Compute(StepType.Voiceover, Payload(["vo0"], ("vo0", "speed", 1.1)), MultibyteVoiceover())
            .Should().BeApproximately(0.5, 1e-9);
        // override text: 4 emoji = 16 bytes > the old 4; 16 of 24
        StepChangeScope.Compute(StepType.Voiceover, Payload(["vo1"], ("vo1", "text", "\U0001F600\U0001F600\U0001F600\U0001F600")), MultibyteVoiceover())
            .Should().BeApproximately(16d / 24d, 1e-9);
    }

    [Fact]
    public void Two_of_five_lines_named_by_an_override_each_is_two_fifths()
    {
        double f = StepChangeScope.Compute(StepType.Voiceover, Payload(null, ("vo0", "text", "A"), ("vo4", "speed", 1.2)), VoiceoverOutput(5));

        f.Should().BeApproximately(0.4, 1e-9);
    }

    [Fact]
    public void A_star_override_or_the_whole_step_is_one()
    {
        StepChangeScope.Compute(StepType.Voiceover, Payload(null, ("*", "speed", 1.1)), VoiceoverOutput(5)).Should().Be(1d);
        StepChangeScope.Compute(StepType.Voiceover, Payload(["vo1"], ("*", "speed", 1.1)), VoiceoverOutput(5)).Should().Be(1d);
        StepChangeScope.Compute(StepType.Voiceover, Payload(), VoiceoverOutput(5)).Should().Be(1d);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"view":{"lines":[]}}""")]
    public void An_output_that_cannot_be_read_prices_the_whole_step(string? output)
    {
        StepChangeScope.Compute(StepType.Voiceover, Payload(["vo0"], ("vo0", "text", "x")), output).Should().Be(1d);
        StepChangeScope.Compute(StepType.VideoGenerate, Payload(["v1"], ("v1", "retake", true)), output).Should().Be(1d);
        StepChangeScope.Compute(StepType.Voiceover, null, VoiceoverOutput(5)).Should().Be(1d);
    }

    [Fact]
    public void One_of_three_shots_is_a_third_and_a_take_is_a_share_of_its_shots_takes()
    {
        StepChangeScope.Compute(StepType.VideoGenerate, Payload(["v2"], ("v2", "prompt", "New")), ClipsOutput(3, 1))
            .Should().BeApproximately(1d / 3, 1e-9);
        // 3 shots x 2 takes = 6 clips: the whole shot v2 is two of them, one named take is one of them.
        StepChangeScope.Compute(StepType.VideoGenerate, Payload(["v2"], ("v2", "retake", true)), ClipsOutput(3, 2))
            .Should().BeApproximately(2d / 6, 1e-9);
        StepChangeScope.Compute(StepType.VideoGenerate, Payload(null, ("v2t1", "retake", true)), ClipsOutput(3, 2))
            .Should().BeApproximately(1d / 6, 1e-9);
    }

    // ------------------------------------------------------------------ cost-weighted (uneven items)

    private static string UnevenClips(params double[] seconds) => JsonSerializer.Serialize(new
    {
        clips = seconds.Select((d, i) => new { clipId = $"v{i + 1}", status = d > 0 ? "succeeded" : "failed", description = "p", durationSeconds = d }).ToArray()
    });

    private static string UnevenLines(params int[] chars) => JsonSerializer.Serialize(new
    {
        view = new { lines = chars.Select((c, i) => new { id = $"vo{i}", status = "ok", durationSec = 1.0, text = new string('x', c) }).ToArray() }
    });

    private static string UnevenTakes(params double[] seconds) => JsonSerializer.Serialize(new
    {
        takes = seconds.Select((d, i) => new { take = i + 1, status = "succeeded", durationSec = d, prompt = "p" }).ToArray()
    });

    [Fact]
    public void Video_is_weighted_by_the_clips_seconds_not_their_count()
    {
        string output = UnevenClips(2, 2, 2, 14);
        StepChangeScope.Compute(StepType.VideoGenerate, Payload(["v4"], ("v4", "retake", true)), output).Should().BeApproximately(0.7, 1e-9);
        StepChangeScope.Compute(StepType.VideoGenerate, Payload(["v1"], ("v1", "prompt", "New")), output).Should().BeApproximately(0.1, 1e-9);
    }

    [Fact]
    public void Voiceover_is_weighted_by_characters_and_a_longer_text_override_raises_the_weight()
    {
        string output = UnevenLines(10, 10, 10, 270);
        StepChangeScope.Compute(StepType.Voiceover, Payload(["vo3"], ("vo3", "speed", 1.2)), output).Should().BeApproximately(0.9, 1e-9);
        StepChangeScope.Compute(StepType.Voiceover, Payload(["vo0"], ("vo0", "speed", 1.2)), output).Should().BeApproximately(10d / 300, 1e-9);
        StepChangeScope.Compute(StepType.Voiceover, Payload(["vo0"], ("vo0", "text", new string('y', 40))), output).Should().BeApproximately(40d / 300, 1e-9);
        StepChangeScope.Compute(StepType.Voiceover, Payload(["vo0"], ("vo0", "text", "short")), output).Should().BeApproximately(10d / 300, 1e-9, "a shorter text is not cheaper than the old line");
    }

    [Fact]
    public void Audio_is_weighted_by_the_takes_seconds_and_a_longer_duration_override_raises_it()
    {
        string output = UnevenTakes(5, 55);
        StepChangeScope.Compute(StepType.AudioGenerate, Payload(["a2"], ("a2", "prompt", "n")), output).Should().BeApproximately(55d / 60, 1e-9);
        StepChangeScope.Compute(StepType.AudioGenerate, Payload(["a1"], ("a1", "prompt", "n")), output).Should().BeApproximately(5d / 60, 1e-9);
        StepChangeScope.Compute(StepType.AudioGenerate, Payload(["a1"], ("a1", "durationSec", 15)), output).Should().BeApproximately(15d / 60, 1e-9);
        StepChangeScope.Compute(StepType.AudioGenerate, Payload(["a1"], ("a1", "durationSec", 1)), output).Should().BeApproximately(5d / 60, 1e-9);
        StepChangeScope.Compute(StepType.AudioGenerate, Payload(["a1"], ("a1", "retake", true)), output).Should().BeApproximately(5d / 60, 1e-9);
    }

    [Fact]
    public void Any_item_of_unknown_size_holds_the_whole_step()
    {
        StepChangeScope.Compute(StepType.VideoGenerate, Payload(["v1"], ("v1", "retake", true)), UnevenClips(2, 0, 2, 14)).Should().Be(1d);
        StepChangeScope.Compute(StepType.Voiceover, Payload(["vo0"], ("vo0", "speed", 1.2)),
            JsonSerializer.Serialize(new { view = new { lines = new object[] { new { id = "vo0", text = "abc" }, new { id = "vo1", status = "failed" } } } })).Should().Be(1d);
        StepChangeScope.Compute(StepType.AudioGenerate, Payload(["a1"], ("a1", "prompt", "n")), UnevenTakes(5, -1)).Should().Be(1d);
    }

    [Fact]
    public void A_star_and_a_thumbnail_are_the_whole_step()
    {
        StepChangeScope.Compute(StepType.VideoGenerate, Payload(null, ("*", "retake", true)), UnevenClips(2, 14)).Should().Be(1d);
        StepChangeScope.Compute(StepType.ThumbnailGenerate, Payload(["thumbnail"], ("thumbnail", "overlayText", "x")),
            """{"thumbnailStorageKey":"k","thumbnailFileName":"t.jpg","status":"completed"}""").Should().Be(1d);
    }

    [Fact]
    public async Task An_unpriced_step_keeps_its_unscaled_minimum_and_a_positive_hold_is_never_zero()
    {
        (WorkflowExecutorService unpriced, WorkflowExecution e0) = Engine(0.2, targetOrder: 3, holdPerStep: 0);
        WorkflowStep target = new() { StepOrder = 3, StepType = StepType.Voiceover };
        (await unpriced.ResolveStepReserveMillisAsync(e0, target, CancellationToken.None, [target])).Should().Be(0);

        (WorkflowExecutorService tiny, WorkflowExecution e1) = Engine(0.0001, targetOrder: 3, holdPerStep: 5);
        (long reserve, long full) = await tiny.ResolveStepReserveAsync(e1, target, CancellationToken.None, [target]);
        reserve.Should().BeGreaterThan(0);
        full.Should().Be(5);
        StepChangeScope.ApplyToHold(1, 0.0001).Should().Be(1);
    }

    // ------------------------------------------------------------------ the hold

    [Theory]
    [InlineData(10_000L, 0.2, 2_000L)]
    [InlineData(10_000L, 1.0, 10_000L)]
    [InlineData(10_000L, 1.5, 10_000L)]
    [InlineData(10_000L, 0.0, 10_000L)]
    [InlineData(10_000L, double.NaN, 10_000L)]
    [InlineData(7L, 0.2, 2L)]
    [InlineData(1L, 0.01, 1L)]
    [InlineData(0L, 0.2, 0L)]
    public void The_hold_is_the_whole_step_hold_times_the_fraction_never_below_one_never_above_the_whole(long full, double fraction, long expected)
    {
        StepChangeScope.ApplyToHold(full, fraction).Should().Be(expected);
        StepChangeScope.ApplyToHold(full, null).Should().Be(full);
    }

    private static WorkflowCostEstimate Estimate() => new(
        true, false, 8_000, 12_000, 40_000,
        [
            new WorkflowStepCostEstimate(3, "Voiceover", null, "VO", 5_000, 10_000, 10_000, "Default", 7_000, 1, 0),
            new WorkflowStepCostEstimate(5, "VideoCompile", null, "Compile", 3_000, 9_000, 30_000, "Default", 4_000, 1, 0)
        ]);

    [Fact]
    public void The_api_estimate_scales_the_target_step_only_and_never_beyond_the_whole_step()
    {
        WorkflowCostEstimate scaled = WorkflowCostEstimator.ScaleSteps(Estimate(), new Dictionary<int, double> { [3] = 0.2 })!;

        scaled.Steps.Single(s => s.StepOrder == 3).HoldMillis.Should().Be(2_000, "one of five lines holds a fifth");
        scaled.Steps.Single(s => s.StepOrder == 5).HoldMillis.Should().Be(30_000, "every other step holds as always");
        scaled.RequiredMillis.Should().Be(32_000);
        scaled.LowMillis.Should().Be(8_000 - 4_000);
        scaled.HighMillis.Should().Be(12_000 - 8_000);

        WorkflowCostEstimator.ScaleSteps(Estimate(), new Dictionary<int, double> { [3] = 1d })!.RequiredMillis.Should().Be(40_000, "the whole step is priced as before");
        WorkflowCostEstimator.ScaleSteps(Estimate(), new Dictionary<int, double> { [99] = 0.2 })!.RequiredMillis.Should().Be(40_000, "an unknown step order changes nothing");
        WorkflowCostEstimator.ScaleSteps(Estimate(), null)!.RequiredMillis.Should().Be(40_000);
        WorkflowCostEstimator.ScaleSteps(null, new Dictionary<int, double> { [3] = 0.2 }).Should().BeNull();
    }

    // ------------------------------------------------------------------ the engine's per-step hold

    private sealed class FixedHold(long perStep) : ICreditHoldEstimator
    {
        public ValueTask<long> HoldMillisAsync(Guid organizationId, StepType stepType, Guid? agentDefinitionId, CancellationToken ct = default) =>
            ValueTask.FromResult(perStep);

        public ValueTask<long> RunHoldMillisAsync(Guid organizationId, IReadOnlyList<WorkflowStep> runSteps, WorkflowStep step, int? outputResolution, CancellationToken ct = default) =>
            ValueTask.FromResult(perStep);

        public ValueTask<long?> TryVideoGenerateHoldMillisAsync(string? videoGenerateConfigJson, CancellationToken ct = default) =>
            ValueTask.FromResult<long?>(perStep);
    }

    private static (WorkflowExecutorService Executor, WorkflowExecution Execution) Engine(double? fraction, int targetOrder, WorkflowDefinitionKind kind = WorkflowDefinitionKind.CompositionReplay, long holdPerStep = 10_000)
    {
        string dbName = "scope-hold-" + Guid.NewGuid().ToString("N");
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
                TargetStepOrder = targetOrder,
                StepType = "Voiceover",
                ExecutionId = execution.Id,
                ScopeFraction = fraction
            });
            db.SaveChanges();
        }

        WorkflowExecutorService executor = new(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Mock.Of<IWorkflowEventPublisher>(),
            NullLogger<WorkflowExecutorService>.Instance,
            [],
            new RabbitMqHelper(new ConfigurationBuilder().Build()),
            Options.Create(new WorkflowHardeningOptions()),
            new ExecutionCancellationRegistry(),
            creditOptions: Options.Create(new StepCreditOptions()),
            creditHoldEstimator: new FixedHold(holdPerStep));
        return (executor, execution);
    }

    [Fact]
    public async Task The_replays_target_step_holds_its_share_and_every_other_step_holds_in_full()
    {
        (WorkflowExecutorService executor, WorkflowExecution execution) = Engine(0.2, targetOrder: 3);
        WorkflowStep target = new() { StepOrder = 3, StepType = StepType.Voiceover };
        WorkflowStep other = new() { StepOrder = 5, StepType = StepType.VideoCompile };

        (await executor.ResolveStepReserveMillisAsync(execution, target, CancellationToken.None, [target, other])).Should().Be(2_000);
        (await executor.ResolveStepReserveMillisAsync(execution, other, CancellationToken.None, [target, other])).Should().Be(10_000);
    }

    [Fact]
    public async Task A_video_generate_target_holds_its_share_of_its_own_priced_hold()
    {
        (WorkflowExecutorService executor, WorkflowExecution execution) = Engine(1d / 3, targetOrder: 4);
        WorkflowStep target = new() { StepOrder = 4, StepType = StepType.VideoGenerate, VideoGenerateConfigJson = "{}" };

        (await executor.ResolveStepReserveMillisAsync(execution, target, CancellationToken.None, [target])).Should().Be(3_334);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1.0)]
    public async Task A_whole_step_or_unknown_scope_holds_in_full(double? fraction)
    {
        (WorkflowExecutorService executor, WorkflowExecution execution) = Engine(fraction, targetOrder: 3);
        WorkflowStep target = new() { StepOrder = 3, StepType = StepType.Voiceover };

        (await executor.ResolveStepReserveMillisAsync(execution, target, CancellationToken.None, [target])).Should().Be(10_000);
    }

    [Fact]
    public async Task An_ordinary_run_is_never_scaled()
    {
        (WorkflowExecutorService executor, WorkflowExecution execution) = Engine(0.2, targetOrder: 3, WorkflowDefinitionKind.ExecutionSnapshot);
        WorkflowStep target = new() { StepOrder = 3, StepType = StepType.Voiceover };

        (await executor.ResolveStepReserveMillisAsync(execution, target, CancellationToken.None, [target])).Should().Be(10_000);
    }
}
