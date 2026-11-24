using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.OutputSchemas;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.Caching;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Wave-A acceptance test for the claim that a PLAN-SOURCED <c>VideoGenerate</c> step's step-result
/// cache key covers the planner's output transitively: the key is a SHA-256 over the fully resolved
/// step input, and for a deterministic step type that input IS the accumulated output of the step
/// that ran before it — which, in the shipped <c>video-derush-generate</c> pipeline
/// (<c>VideoAnalyze</c> → <c>ShotDirector</c> → <c>VideoGenerate</c>), is exactly the
/// <c>GeneratedShotPlanOutput</c> the plan path will walk.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately built on the REAL <see cref="StepResultCache"/>, <see cref="StepCacheKeyBuilder"/>
/// and <see cref="StepCachePolicy"/> over an InMemory <see cref="WorkflowEngineDbContext"/> — not
/// on a reimplementation of the hashing — so a change to the key's field set or framing shows up
/// here rather than passing quietly.
/// </para>
/// <para>
/// The resolved input is derived through the engine's own
/// <see cref="WorkflowExecutorService.ResolveInputJsonForPersistence"/> (the same call
/// <c>WorkflowExecutorService.ExecuteAsync</c> makes) applied to the previous step's
/// <c>Output</c>, which is how the execution loop accumulates it
/// (<c>accumulatedOutput = result.Output</c>). That is what keeps this test from being a tautology
/// about "two different strings hash differently": the input half of the pair under test is
/// produced by production code, from a planner output the executor would really be handed.
/// </para>
/// </remarks>
public class VideoGeneratePlanSourcedCacheKeyTests
{
    private static readonly Guid ProjectId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    private static readonly Guid AgentDefinitionId = Guid.Parse("ffeeddcc-bbaa-9988-7766-554433221100");
    private const string ProjectFileFingerprint = "fingerprint-0001";
    private const int PlanStepOrder = 2;

    // ==========================================================================================
    // The claim under test
    // ==========================================================================================

    [Fact]
    public async Task The_key_follows_the_planner_output_so_changing_only_that_output_misses_the_cache()
    {
        using WorkflowEngineDbContext db = NewDb();
        StepResultCache cache = NewCache(db);

        // Two planner outputs that differ by exactly ONE byte — and that byte is inside a planned
        // shot's own prompt, i.e. a change the generated clips must differ for. Everything else
        // about the two runs (project, step, agent, config, user request, file fingerprint) is
        // held constant.
        string plannerOutputA = PlanJson("A drone shot over a bay at dawn");
        string plannerOutputB = PlanJson("A drone shot over a bay at dusk");
        plannerOutputA.Length.Should().Be(plannerOutputB.Length, "the two plans differ by one byte, not by length");

        StepCacheKeyInputs inputsA = InputsForGenerateStep(plannerOutputA);
        StepCacheKeyInputs inputsB = InputsForGenerateStep(plannerOutputB);

        // ---- The key itself moves. ----
        var keyBuilder = new StepCacheKeyBuilder();
        keyBuilder.Build(inputsA).Should().NotBe(keyBuilder.Build(inputsB));

        // ---- And the cache behaves accordingly: a stored run is served, the changed plan is not. ----
        await cache.StoreAsync(inputsA, CompletedResult(), CancellationToken.None);

        (await cache.TryGetAsync(inputsA, CancellationToken.None)).Hit
            .Should().BeTrue("nothing changed, so the plan-sourced generate step must not pay again");

        StepCacheLookupResult changed = await cache.TryGetAsync(inputsB, CancellationToken.None);
        changed.Hit.Should().BeFalse("the planner emitted a different plan, so the generated clips must be regenerated");
        changed.Entry.Should().BeNull();
    }

    [Fact]
    public void The_resolved_input_really_carries_the_planners_own_words()
    {
        // Guards against the cache-key test above degenerating into "some opaque string differs":
        // the value that feeds the hash is the planner's output, so its plan content is visible in
        // the hashed material.
        string plannerOutput = PlanJson("A drone shot over a bay at dawn");

        StepCacheKeyInputs inputs = InputsForGenerateStep(plannerOutput);

        inputs.ResolvedInput.Should().NotBeNull();
        inputs.ResolvedInput.Should().Contain("A drone shot over a bay at dawn");
        inputs.ResolvedInput.Should().Contain("\"shots\"");

        // The step's own config is hashed too, so the plan SOURCE (StepRef + which step) is part of
        // the key rather than an invisible property of the executor.
        inputs.VideoGenerateConfigJson.Should().NotBeNull();
        inputs.VideoGenerateConfigJson.Should().Contain("\"planSource\":\"StepRef\"");
        inputs.VideoGenerateConfigJson.Should().Contain($"\"stepOrder\":{PlanStepOrder}");
    }

    [Fact]
    public void The_key_is_stable_when_nothing_changes()
    {
        string plannerOutput = PlanJson("A drone shot over a bay at dawn");

        var keyBuilder = new StepCacheKeyBuilder();
        string first = keyBuilder.Build(InputsForGenerateStep(plannerOutput));
        string second = keyBuilder.Build(InputsForGenerateStep(plannerOutput));

        second.Should().Be(first, "an identical re-run of an unchanged plan must produce the identical key");
    }

    [Fact]
    public async Task An_unchanged_plan_is_served_from_the_cache_round_trip()
    {
        using WorkflowEngineDbContext db = NewDb();
        StepResultCache cache = NewCache(db);

        StepCacheKeyInputs inputs = InputsForGenerateStep(PlanJson("A drone shot over a bay at dawn"));
        StepExecutionResult result = CompletedResult();

        await cache.StoreAsync(inputs, result, CancellationToken.None);
        StepCacheLookupResult lookup = await cache.TryGetAsync(
            InputsForGenerateStep(PlanJson("A drone shot over a bay at dawn")), CancellationToken.None);

        lookup.Hit.Should().BeTrue();
        lookup.Entry!.Output.Should().Be(result.Output);
        lookup.Entry.ProjectId.Should().Be(ProjectId);
    }

    [Fact]
    public void StepCachePolicy_reports_the_VideoGenerate_step_type_as_cacheable()
    {
        // The premise the whole key argument rests on: if VideoGenerate were not cacheable at all,
        // WorkflowExecutorService would never build a key for it and the planner-output coverage
        // would be moot. Asserted directly (rather than inferred from the round trip above) so a
        // policy change that silently drops the step type fails HERE, with the reason.
        var policy = new StepCachePolicy();

        policy.IsCacheable(new WorkflowStep
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = Guid.NewGuid(),
            StepOrder = 3,
            StepType = StepType.VideoGenerate,
            VideoGenerateConfigJson = PlanConfigJson()
        }).Should().BeTrue();

        // ...and an explicit per-step override still wins, which is the documented escape hatch for
        // an author who wants fresh takes rather than a replayed (already paid for) one.
        policy.IsCacheable(new WorkflowStep
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = Guid.NewGuid(),
            StepOrder = 3,
            StepType = StepType.VideoGenerate,
            CacheMode = StepCacheMode.Never,
            VideoGenerateConfigJson = PlanConfigJson()
        }).Should().BeFalse();
    }

    // ==========================================================================================
    // Harness
    // ==========================================================================================

    private static WorkflowEngineDbContext NewDb() =>
        new(new DbContextOptionsBuilder<WorkflowEngineDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static StepResultCache NewCache(WorkflowEngineDbContext db) =>
        new(
            db,
            new StepCacheKeyBuilder(),
            new StepCachePolicy(),
            Options.Create(new StepCacheOptions()),
            NullLogger<StepResultCache>.Instance);

    private static StepExecutionResult CompletedResult() => new()
    {
        Output = """{"clips":[{"clipId":"v1","status":"succeeded","stored":true}],"meta":{"clipsGenerated":1}}""",
        NextStepIndex = 4,
        Status = StepStatus.Completed,
        TokensUsed = 0,
        DurationMs = 1234,
        OutputStorageKey = "projects/p/agentFiles/generated/e/step-3-v1.mp4",
        ArtifactStorageKey = "projects/p/agentFiles/video-analysis/e/step-3-generated-clips.json"
    };

    /// <summary>
    /// The plan-sourced step's own config exactly as the step stores it: <c>PlanSource = StepRef</c>
    /// plus the <c>Plan</c> step reference. JSON, not the object, because that JSON blob is what
    /// <see cref="StepCacheKeyInputs.VideoGenerateConfigJson"/> hashes.
    /// </summary>
    private static string PlanConfigJson() => JsonSerializer.Serialize(new VideoGenerateStepConfig
    {
        PlanSource = VideoGeneratePlanSource.StepRef,
        Plan = new ExtractInputRef(ExtractInputSource.Step, StepOrder: PlanStepOrder),
        Model = "MiniMax-H3",
        Resolution = "768P",
        Ratio = "16:9",
        Takes = 1,
        MaxClips = 4,
        MaxSpendCredits = 20m,
        TimeoutSeconds = 1800,
        PollIntervalSeconds = 10
    }, new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    });

    /// <summary>
    /// The planner step's stored <c>Output</c> — a real <see cref="GeneratedShotPlanOutput"/>,
    /// serialized the way the agent's structured output is persisted.
    /// </summary>
    private static string PlanJson(string prompt) => JsonSerializer.Serialize(
        new GeneratedShotPlanOutput
        {
            Shots =
            [
                new ShotPlan
                {
                    Purpose = "Cutaway",
                    Anchor = "s1",
                    Camera = "PushIn",
                    Duration = "Short",
                    Prompt = prompt,
                    Reason = "covers the cut"
                }
            ],
            PlanRationale = "one b-roll cutaway"
        },
        new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter() }
        });

    /// <summary>
    /// The key inputs <c>WorkflowExecutorService</c> would assemble for a plan-sourced
    /// <c>VideoGenerate</c> step whose previous step (the <c>ShotDirector</c>) emitted
    /// <paramref name="plannerOutput"/>.
    /// <para>
    /// The resolved input is derived through production code — the execution loop sets
    /// <c>accumulatedOutput = result.Output</c> for the preceding step, and
    /// <c>ResolveInputJsonForPersistence</c> is what turns that (plus the step's own resolved agent
    /// input, null for a non-Agent step type) into the value handed to the key builder.
    /// </para>
    /// </summary>
    private static StepCacheKeyInputs InputsForGenerateStep(string plannerOutput)
    {
        string? resolvedInput = WorkflowExecutorService.ResolveInputJsonForPersistence(
            resolvedAgentInput: null, accumulatedOutput: plannerOutput);

        return new StepCacheKeyInputs
        {
            ProjectId = ProjectId,
            StepType = StepType.VideoGenerate,
            AgentType = AgentType.VideoTransform,
            AgentDefinitionId = AgentDefinitionId,
            AgentInputContextMode = AgentInputContextMode.FullWorkflow,
            MaxIterations = 1,
            VideoGenerateConfigJson = PlanConfigJson(),
            ResolvedInput = resolvedInput,
            UserRequest = "cut the launch video down",
            ProjectFileFingerprint = ProjectFileFingerprint,
            WorkflowDefinitionId = Guid.NewGuid()
        };
    }
}
