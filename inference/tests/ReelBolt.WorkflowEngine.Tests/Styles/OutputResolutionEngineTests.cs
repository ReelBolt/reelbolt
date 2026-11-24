using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Styles;
using ReelBolt.Shared.Tenancy;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Agents.Tools;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.Caching;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Billing;
using ReelBolt.WorkflowEngine.Services.Styles;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Styles;

/// <summary>
/// How the engine applies a run's output resolution (short side, null = Auto): the compile canvas,
/// the plan cap, the Remotion scale, the step cache, the agent nudge, the credit hold and the
/// metering tiers the credits come from.
/// </summary>
public class OutputResolutionEngineTests
{
    private static WorkflowExecution Execution(int? resolution = null, StyleConfig? style = null, RunOutputFormat? format = null) => new()
    {
        Id = Guid.NewGuid(),
        StyleConfigJson = style is null ? null : StyleJson.Serialize(new RunStyleSnapshot("s", null, null, style)),
        OutputFormatOverride = format,
        OutputResolution = resolution
    };

    // ---------------------------------------------------------------- canvas

    [Theory]
    [InlineData(VideoOutputFormat.Landscape, null, 1920, 1080)]
    [InlineData(VideoOutputFormat.Landscape, 720, 1280, 720)]
    [InlineData(VideoOutputFormat.Landscape, 1080, 1920, 1080)]
    [InlineData(VideoOutputFormat.Landscape, 2160, 3840, 2160)]
    [InlineData(VideoOutputFormat.Vertical, null, 1080, 1920)]
    [InlineData(VideoOutputFormat.Vertical, 720, 720, 1280)]
    [InlineData(VideoOutputFormat.Vertical, 1080, 1080, 1920)]
    [InlineData(VideoOutputFormat.Vertical, 2160, 2160, 3840)]
    [InlineData(VideoOutputFormat.Square, null, 1080, 1080)]
    [InlineData(VideoOutputFormat.Square, 720, 720, 720)]
    [InlineData(VideoOutputFormat.Square, 1080, 1080, 1080)]
    [InlineData(VideoOutputFormat.Square, 2160, 2160, 2160)]
    [InlineData(VideoOutputFormat.Portrait, null, 1080, 1350)]
    [InlineData(VideoOutputFormat.Portrait, 720, 720, 900)]
    [InlineData(VideoOutputFormat.Portrait, 1080, 1080, 1350)]
    [InlineData(VideoOutputFormat.Portrait, 2160, 2160, 2700)]
    public void A_fixed_canvas_is_the_1080_class_size_scaled_to_the_chosen_short_side(
        VideoOutputFormat format, int? shortSide, int width, int height)
    {
        VideoCompileStepExecutor.FixedCanvasSize(format, shortSide).Should().Be((width, height));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(720)]
    [InlineData(2160)]
    public void A_source_canvas_stays_the_source(int? shortSide)
    {
        VideoCompileStepExecutor.FixedCanvasSize(VideoOutputFormat.Source, shortSide).Should().BeNull();
    }

    // ---------------------------------------------------------------- plan cap

    [Theory]
    [InlineData(2160, null, 1080)] // Pro, Auto: today's output
    [InlineData(2160, 2160, 2160)] // Pro, 4K chosen
    [InlineData(1080, 2160, 1080)] // 4K chosen, then downgraded to Creator: clamped
    [InlineData(720, null, 720)] // Trial, Auto
    [InlineData(2160, 720, 720)] // Pro, 720p chosen
    [InlineData(OrganizationEntitlementSet.UnlimitedInt, null, OrganizationEntitlementSet.UnlimitedInt)] // Enterprise, Auto
    [InlineData(OrganizationEntitlementSet.UnlimitedInt, 2160, 2160)]
    public void The_output_cap_is_the_plan_under_auto_and_the_choice_clamped_to_the_plan(int planMax, int? chosen, int expected)
    {
        var set = new OrganizationEntitlementSet { MaxOutputHeight = planMax };

        RunStyleOverrides.OutputCap(set, Execution(chosen)).Should().Be(expected);
    }

    [Fact]
    public void A_resolution_that_is_not_an_allowed_value_is_ignored()
    {
        RunStyleOverrides.OutputResolutionOf(Execution(1440)).Should().BeNull();
        RunStyleOverrides.OutputResolutionOf(Execution(-5)).Should().BeNull();
        RunStyleOverrides.OutputResolutionOf(Execution(2160)).Should().Be(2160);
    }

    // ---------------------------------------------------------------- compile config

    [Fact]
    public void A_chosen_resolution_turns_a_stream_copy_step_into_a_reencode()
    {
        var config = new VideoCompileStepConfig(Version: 1, Decision: new ExtractInputRef(ExtractInputSource.Previous), AnalysisStepOrder: 1)
        {
            Mode = VideoCompileMode.StreamCopy
        };

        VideoCompileStepConfig result = RunStyleOverrides.ApplyToCompile(config, Execution(720), out var applied);

        result.Mode.Should().Be(VideoCompileMode.Reencode);
        applied.Should().Equal("mode=Reencode");
    }

    [Fact]
    public void A_chosen_resolution_on_a_reencoding_step_changes_nothing_in_its_config()
    {
        var config = new VideoCompileStepConfig(Version: 1, Decision: new ExtractInputRef(ExtractInputSource.Previous), AnalysisStepOrder: 1)
        {
            Mode = VideoCompileMode.Reencode
        };

        RunStyleOverrides.ApplyToCompile(config, Execution(2160), out var applied).Should().BeSameAs(config);
        applied.Should().BeEmpty();
    }

    // ---------------------------------------------------------------- cache key component

    [Fact]
    public void A_resolution_alone_gives_a_component_with_the_resolution_suffix()
    {
        RunStyleOverrides.CacheKeyComponent(Execution(2160), []).Should().Be("style=1:-|format=-|target=-|resolution=2160");
    }

    [Fact]
    public void A_resolution_is_appended_to_an_existing_component()
    {
        RunStyleOverrides.CacheKeyComponent(Execution(720, new StyleConfig(Pacing: StylePacing.Fast)), [])
            .Should().Be("style=17:{\"pacing\":\"Fast\"}|format=-|target=-|resolution=720");
    }

    [Fact]
    public void Different_resolutions_give_different_step_cache_keys()
    {
        var builder = new StepCacheKeyBuilder();
        StepCacheKeyInputs Inputs(WorkflowExecution e) => new()
        {
            ProjectId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            StepType = StepType.VideoCompile,
            AgentType = AgentType.Custom,
            AgentDefinitionId = Guid.Empty,
            AgentInputContextMode = AgentInputContextMode.PreviousStepOnly,
            MaxIterations = 0,
            ProjectFileFingerprint = "fp",
            RunStyle = RunStyleOverrides.CacheKeyComponent(e, [])
        };

        new[] { builder.Build(Inputs(Execution())), builder.Build(Inputs(Execution(720))), builder.Build(Inputs(Execution(2160))) }
            .Should().OnlyHaveUniqueItems();
    }

    // ---------------------------------------------------------------- ambient + nudge

    [Fact]
    public void A_run_with_only_a_resolution_has_a_run_style_state()
    {
        RunStyleState? state = RunStyleState.From(Execution(2160), StyleNudgeService.Instance);

        state.Should().NotBeNull();
        state!.OutputResolution.Should().Be(2160);
        state.Style.Should().BeNull();
        state.Format.Should().BeNull();
    }

    [Fact]
    public void The_author_is_told_the_output_resolution_and_to_keep_its_usual_size()
    {
        string? block = StyleNudgeService.Instance.BuildInstructions(AgentType.AuthorAgent, null, null, 2160);

        block.Should().NotBeNull();
        block.Should().Contain("- Output resolution: 4K (2160p). Author the composition at its usual 1080-class size; the renderer scales it to 2160p.\n");
        StyleNudgeService.Instance.BuildInstructions(AgentType.CompositionReviser, null, null, 720)
            .Should().Contain("Output resolution: 720p.");
    }

    [Fact]
    public void Agents_that_do_not_build_a_composition_get_no_resolution_line()
    {
        StyleNudgeService.Instance.BuildInstructions(AgentType.VideoStoryEditor, null, null, 2160).Should().BeNull();
        StyleNudgeService.Instance.BuildInstructions(AgentType.VideoStoryEditor, null, RunOutputFormat.Vertical, 2160)
            .Should().Be(StyleNudgeService.Instance.BuildInstructions(AgentType.VideoStoryEditor, null, RunOutputFormat.Vertical));
    }

    [Fact]
    public void Without_a_resolution_every_block_is_byte_identical()
    {
        var style = new RunStyleSnapshot("s", null, null, new StyleConfig(Pacing: StylePacing.Fast, Graphics: StyleGraphicsDensity.Rich));
        foreach (AgentType agentType in Enum.GetValues<AgentType>())
        {
            StyleNudgeService.Instance.BuildInstructions(agentType, style, RunOutputFormat.Square, null)
                .Should().Be(StyleNudgeService.Instance.BuildInstructions(agentType, style, RunOutputFormat.Square), agentType.ToString());
        }
    }

    // ---------------------------------------------------------------- Remotion args

    [Fact]
    public void A_chosen_resolution_replaces_the_models_separate_scale_argument()
    {
        IReadOnlyList<string>? args = ReactRemotionSandboxTools.ApplyOutputResolutionArgs(
            ["--props={}", "--scale", "4"], 2160, 1920, 1080);

        args.Should().Equal("--props={}", "--scale=2");
    }

    [Fact]
    public void A_chosen_resolution_replaces_the_models_inline_scale_argument()
    {
        ReactRemotionSandboxTools.ApplyOutputResolutionArgs(["--scale=4"], 720, 1920, 1080)
            .Should().Equal("--scale=0.6666667");
    }

    [Fact]
    public void A_chosen_resolution_removes_width_and_height_overrides_with_their_values()
    {
        ReactRemotionSandboxTools.ApplyOutputResolutionArgs(
                ["--width", "3840", "--height=2160", "--frames=0-10", "--width=9", "--height", "99"], 1080, 1080, 1920)
            .Should().Equal("--frames=0-10", "--scale=1");
    }

    [Fact]
    public void A_chosen_resolution_adds_a_scale_when_the_model_passed_no_arguments()
    {
        ReactRemotionSandboxTools.ApplyOutputResolutionArgs(null, 1080, 1280, 720).Should().Equal("--scale=1.5");
    }

    [Fact]
    public void Without_a_choice_the_models_arguments_are_untouched()
    {
        string[] model = ["--scale", "4", "--width=3840"];

        ReactRemotionSandboxTools.ApplyOutputResolutionArgs(model, null, 1920, 1080).Should().BeSameAs(model);
        ReactRemotionSandboxTools.ApplyOutputResolutionArgs(null, null, 1920, 1080).Should().BeNull();
    }

    [Fact]
    public void An_unknown_composition_size_strips_the_models_sizing_but_adds_no_scale()
    {
        ReactRemotionSandboxTools.ApplyOutputResolutionArgs(["--scale", "4", "--props={}"], 2160, 0, 0)
            .Should().Equal("--props={}");
    }

    // ---------------------------------------------------------------- cache validate-on-hit

    private static async Task<(StepResultCache Cache, StepCacheKeyInputs Inputs)> CacheWithEntryAsync(
        WorkflowEngineDbContext db, StepType stepType, string output)
    {
        var cache = new StepResultCache(
            db,
            new StepCacheKeyBuilder(),
            new StepCachePolicy(),
            Options.Create(new StepCacheOptions { Enabled = true, TtlHours = 24 }),
            NullLogger<StepResultCache>.Instance);

        StepCacheKeyInputs inputs = new()
        {
            ProjectId = Guid.NewGuid(),
            StepType = stepType,
            AgentType = AgentType.Custom,
            AgentDefinitionId = Guid.Empty,
            AgentInputContextMode = AgentInputContextMode.PreviousStepOnly,
            MaxIterations = 0,
            ProjectFileFingerprint = "fp"
        };

        await cache.StoreAsync(inputs, new StepExecutionResult
        {
            Output = output,
            NextStepIndex = 1,
            Status = StepStatus.Completed
        }, CancellationToken.None);
        (await db.WorkflowStepCacheEntries.CountAsync()).Should().Be(1);
        return (cache, inputs);
    }

    private static WorkflowEngineDbContext Db() => new(
        new DbContextOptionsBuilder<WorkflowEngineDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private const string Uhd = "{\"status\":\"completed\",\"output\":{\"format\":\"Landscape\",\"width\":3840,\"height\":2160,\"fit\":\"Crop\"}}";

    [Theory]
    [InlineData(1080, false)]
    [InlineData(2160, true)]
    [InlineData(OrganizationEntitlementSet.UnlimitedInt, true)]
    public async Task A_cached_compile_above_the_current_cap_is_a_miss(int cap, bool hit)
    {
        using WorkflowEngineDbContext db = Db();
        (StepResultCache cache, StepCacheKeyInputs inputs) = await CacheWithEntryAsync(db, StepType.VideoCompile, Uhd);

        StepCacheLookupResult lookup = await cache.TryGetAsync(inputs with { MaxOutputShortSide = cap }, CancellationToken.None);

        lookup.Hit.Should().Be(hit);
    }

    [Fact]
    public async Task A_cached_compile_is_a_hit_when_no_cap_is_known()
    {
        using WorkflowEngineDbContext db = Db();
        (StepResultCache cache, StepCacheKeyInputs inputs) = await CacheWithEntryAsync(db, StepType.VideoCompile, Uhd);

        (await cache.TryGetAsync(inputs, CancellationToken.None)).Hit.Should().BeTrue();
    }

    [Fact]
    public async Task A_compile_that_was_capped_when_encoded_is_judged_by_its_encoded_short_side()
    {
        using WorkflowEngineDbContext db = Db();
        (StepResultCache cache, StepCacheKeyInputs inputs) = await CacheWithEntryAsync(
            db, StepType.VideoCompile,
            "{\"output\":{\"format\":\"Vertical\",\"width\":1080,\"height\":1920,\"fit\":\"Crop\",\"encodedShortSide\":720}}");

        (await cache.TryGetAsync(inputs with { MaxOutputShortSide = 720 }, CancellationToken.None)).Hit.Should().BeTrue();
    }

    [Theory]
    [InlineData("{\"status\":\"completed\"}")]
    [InlineData("not json")]
    [InlineData("{\"output\":{\"width\":\"wide\"}}")]
    public async Task A_cached_compile_whose_size_is_unknown_stays_a_hit(string output)
    {
        using WorkflowEngineDbContext db = Db();
        (StepResultCache cache, StepCacheKeyInputs inputs) = await CacheWithEntryAsync(db, StepType.VideoCompile, output);

        (await cache.TryGetAsync(inputs with { MaxOutputShortSide = 720 }, CancellationToken.None)).Hit.Should().BeTrue();
    }

    [Fact]
    public async Task Only_a_compile_is_checked_against_the_cap()
    {
        using WorkflowEngineDbContext db = Db();
        (StepResultCache cache, StepCacheKeyInputs inputs) = await CacheWithEntryAsync(db, StepType.VideoAnalyze, Uhd);

        (await cache.TryGetAsync(inputs with { MaxOutputShortSide = 720 }, CancellationToken.None)).Hit.Should().BeTrue();
    }

    [Fact]
    public void The_cap_is_not_part_of_the_cache_key()
    {
        var builder = new StepCacheKeyBuilder();
        StepCacheKeyInputs inputs = new()
        {
            ProjectId = Guid.NewGuid(),
            StepType = StepType.VideoCompile,
            AgentType = AgentType.Custom,
            AgentDefinitionId = Guid.Empty,
            AgentInputContextMode = AgentInputContextMode.PreviousStepOnly,
            MaxIterations = 0,
            ProjectFileFingerprint = "fp"
        };

        builder.Build(inputs with { MaxOutputShortSide = 720 }).Should().Be(builder.Build(inputs));
    }

    // ---------------------------------------------------------------- credit hold

    private sealed class ThrowingFactory : IDbContextFactory<WorkflowEngineDbContext>
    {
        public WorkflowEngineDbContext CreateDbContext() => throw new InvalidOperationException("no history");
    }

    private static UsageHistoryCreditHoldEstimator HoldEstimator() => new(
        new ThrowingFactory(),
        Options.Create(new StepCreditEstimateOptions()),
        NullLogger<UsageHistoryCreditHoldEstimator>.Instance);

    [Fact]
    public async Task A_4k_compile_holds_four_times_the_1080p_hold()
    {
        long baseline = await HoldEstimator().HoldMillisAsync(Guid.NewGuid(), StepType.VideoCompile, null, CancellationToken.None);

        long hold = await HoldEstimator().HoldMillisAsync(Guid.NewGuid(), StepType.VideoCompile, null, 2160, CancellationToken.None);

        baseline.Should().BePositive();
        hold.Should().Be(4 * baseline);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(720)]
    [InlineData(1080)]
    public async Task A_compile_hold_never_shrinks_below_the_1080p_hold(int? resolution)
    {
        long baseline = await HoldEstimator().HoldMillisAsync(Guid.NewGuid(), StepType.VideoCompile, null, CancellationToken.None);

        (await HoldEstimator().HoldMillisAsync(Guid.NewGuid(), StepType.VideoCompile, null, resolution, CancellationToken.None))
            .Should().Be(baseline);
    }

    [Fact]
    public async Task A_resolution_does_not_scale_the_hold_of_other_steps()
    {
        long baseline = await HoldEstimator().HoldMillisAsync(Guid.NewGuid(), StepType.Agent, null, CancellationToken.None);

        (await HoldEstimator().HoldMillisAsync(Guid.NewGuid(), StepType.Agent, null, 2160, CancellationToken.None))
            .Should().Be(baseline);
    }

    // ---------------------------------------------------------------- metering ratio pin

    [Theory]
    [InlineData(3840, 2160, ComputeMetering.TierUhd)]
    [InlineData(1280, 720, ComputeMetering.Tier720)]
    [InlineData(720, 1280, ComputeMetering.Tier720)]
    [InlineData(1920, 1080, ComputeMetering.Tier1080)]
    [InlineData(1080, 1920, ComputeMetering.Tier1080)]
    [InlineData(2160, 3840, ComputeMetering.TierUhd)]
    public void Frames_are_billed_by_their_short_side_tier(int width, int height, string tier)
    {
        ComputeMetering.TierFor(width, height).Should().Be(tier);
    }

    [Theory]
    [InlineData(UsageKind.RenderOutputSeconds)]
    [InlineData(UsageKind.EncodeOutputSeconds)]
    public void Render_and_encode_seconds_cost_four_ninths_one_and_four_times_1080p(UsageKind kind)
    {
        // The compute prices every install is seeded with (the system compute capability's tier variants).
        IReadOnlyList<SuggestedPrice> prices = PricingDefaults.ComputePrices();
        decimal Credits(string tier) => prices.Single(p => p.Unit == kind && p.Variant == tier).CostUsdPerUnit;

        decimal hd = Credits(ComputeMetering.Tier1080);
        (Credits(ComputeMetering.Tier720) / hd).Should().BeApproximately(4m / 9m, 0.0000001m);
        (Credits(ComputeMetering.TierUhd) / hd).Should().BeApproximately(4m, 0.0000001m);
        ((double)(Credits(ComputeMetering.TierUhd) / hd)).Should().Be(OutputResolution.PixelFactor(2160));
        ((double)(Credits(ComputeMetering.Tier720) / hd)).Should().BeApproximately(OutputResolution.PixelFactor(720), 1e-9);
    }
}
