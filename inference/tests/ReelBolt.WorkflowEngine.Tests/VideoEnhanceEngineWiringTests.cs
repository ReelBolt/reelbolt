using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.ChangeRequests;
using ReelBolt.Shared.Workflows.Replay;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.Caching;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Billing;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>The engine-wide decisions for VideoEnhance: cache, credits, rate class, failures, dependencies, providers, schema.</summary>
public sealed class VideoEnhanceEngineWiringTests
{
    private const string File = "11111111-1111-1111-1111-111111111111";
    private static readonly string Local = $$$"""{"op":"Denoise","engine":"Local","denoiseStrength":"Light","source":{"kind":"ProjectFile","projectFileId":"{{{File}}}"}}""";
    private static readonly string Hosted = $$$"""{"op":"Upscale","engine":"Hosted","upscaleFactor":2,"source":{"kind":"ProjectFile","projectFileId":"{{{File}}}"},"allowSourceMediaEgress":true}""";
    private static readonly string HostedInterpolate = $$$"""{"op":"Interpolate","engine":"Hosted","frameMultiplier":2,"source":{"kind":"ProjectFile","projectFileId":"{{{File}}}"},"allowSourceMediaEgress":true}""";

    private static WorkflowStep Step(int order, string? configJson) => new()
    {
        Id = Guid.NewGuid(),
        StepOrder = order,
        StepType = StepType.VideoEnhance,
        VideoEnhanceConfigJson = configJson
    };

    // ---------------- cache ----------------

    [Fact]
    public void VideoEnhance_is_cacheable_and_has_a_deterministic_revision()
    {
        new StepCachePolicy().IsCacheable(Step(2, Local)).Should().BeTrue();
        DeterministicStepRevisions.For(StepType.VideoEnhance).Should().Be("2026-11-03.video-enhance-1");
    }

    private static StepCacheKeyInputs Inputs(string? configJson, string? providerFingerprint = "prov", string digest = "src") => new()
    {
        ProjectId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
        StepType = StepType.VideoEnhance,
        AgentType = AgentType.Custom,
        AgentDefinitionId = Guid.Empty,
        AgentInputContextMode = AgentInputContextMode.PreviousStepOnly,
        ProjectFileFingerprint = digest,
        ResolvedProviderFingerprint = providerFingerprint,
        MaxIterations = 1,
        VideoEnhanceConfigJson = configJson
    };

    [Fact]
    public void The_cache_key_covers_the_source_the_op_the_parameters_the_engine_and_the_provider()
    {
        var builder = new StepCacheKeyBuilder();
        string baseKey = builder.Build(Inputs(Local));

        builder.Build(Inputs(Local, digest: "another-source-digest")).Should().NotBe(baseKey, "a different source content");
        builder.Build(Inputs(Local.Replace("Denoise", "MotionBlur"))).Should().NotBe(baseKey, "a different op");
        builder.Build(Inputs(Local.Replace("Light", "Strong"))).Should().NotBe(baseKey, "a different parameter");
        builder.Build(Inputs(Local.Replace(File, "22222222-2222-2222-2222-222222222222"))).Should().NotBe(baseKey, "a different source file");
        builder.Build(Inputs(Hosted)).Should().NotBe(builder.Build(Inputs(Hosted.Replace("\"Hosted\"", "\"Local\"").Replace(",\"allowSourceMediaEgress\":true", string.Empty))), "a different engine");
        builder.Build(Inputs(Hosted, "other-provider")).Should().NotBe(builder.Build(Inputs(Hosted)), "a different resolved provider");
        builder.Build(Inputs(null)).Should().NotBe(baseKey);
        builder.Build(Inputs(Local)).Should().Be(baseKey, "the same inputs are the same key");
    }

    [Fact]
    public void A_consent_false_hosted_step_has_its_own_key_so_it_can_never_poison_an_allowed_run()
    {
        var builder = new StepCacheKeyBuilder();
        string refused = builder.Build(Inputs(Hosted.Replace("\"allowSourceMediaEgress\":true", "\"allowSourceMediaEgress\":false")));
        string allowed = builder.Build(Inputs(Hosted));

        refused.Should().NotBe(allowed);
        // And a refusal is a failure, and failures are never cached.
        new StepCachePolicy().IsCacheable(Step(2, Hosted)).Should().BeTrue("the type is cacheable; only a completed result is ever stored");
    }

    [Fact]
    public void A_step_with_no_enhance_config_keeps_the_key_it_had_before()
    {
        var builder = new StepCacheKeyBuilder();
        StepCacheKeyInputs other = Inputs(null) with { StepType = StepType.PinInsert };
        builder.Build(other).Should().Be(builder.Build(other with { VideoEnhanceConfigJson = null }));
    }

    // ---------------- credits ----------------

    [Fact]
    public void A_local_operation_is_free_holds_nothing_and_a_hosted_one_is_charged()
    {
        StepCreditDefaults.IsNotCharged(StepType.VideoEnhance).Should().BeFalse("the type is chargeable; only a local config is free");
        (long low, long high) = StepCreditDefaults.MillisFor(StepType.VideoEnhance);
        low.Should().BePositive();
        high.Should().BeGreaterThanOrEqualTo(low);

        var estimator = new StepCreditEstimator([]);
        estimator.Estimate(StepType.VideoEnhance, null, computeOnly: true).Should().Be(new StepCreditEstimate(0, 0, 0, CreditEstimateBasis.NotCharged));
        StepCreditEstimate hosted = estimator.Estimate(StepType.VideoEnhance, null);
        hosted.HoldMillis.Should().BePositive();
        RunCreditHolds.IsCold(StepType.VideoEnhance, hosted).Should().BeFalse("a fixed-price media step is never re-planned up");
    }

    [Fact]
    public void The_usage_profile_of_a_step_is_empty_and_it_is_not_agent_shaped()
    {
        StepUsageProfiles.For(Step(2, Local), null).Quantities.Should().BeEmpty();
        StepUsageProfiles.For(Step(2, Hosted), null).Quantities.Should().BeEmpty();
        ColdStartStepPricing.IsAgentShaped(StepType.VideoEnhance).Should().BeFalse();
    }

    private static (Mock<IInferenceProviderResolver>, Mock<IModelPriceService>, List<PriceRequest>) Wire(ResolvedAudioGenerationProvider? row)
    {
        Mock<IInferenceProviderResolver> resolver = new();
        resolver.Setup(r => r.ResolveVideoUpscaleAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>())).ReturnsAsync(row);
        resolver.Setup(r => r.ResolveFrameInterpolationAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>())).ReturnsAsync(row);
        List<PriceRequest> asked = [];
        Mock<IModelPriceService> prices = new();
        prices.Setup(p => p.PriceAsync(It.IsAny<PriceRequest>(), It.IsAny<CancellationToken>()))
            .Callback<PriceRequest, CancellationToken>((r, _) => asked.Add(r))
            .ReturnsAsync((PriceRequest r, CancellationToken _) => new PriceQuote((long)(r.Quantity * 1000), 0m, Guid.NewGuid()));
        return (resolver, prices, asked);
    }

    private static ResolvedAudioGenerationProvider Row(string model, InferenceProviderCapability capability) =>
        new(Guid.NewGuid(), "row", InferenceProviderKind.FalAi, capability, "", model, "test", 60, AccountId: Guid.NewGuid());

    [Fact]
    public async Task The_hold_uses_the_price_book_is_zero_for_local_and_null_when_unpriced()
    {
        var (resolver, prices, asked) = Wire(Row(VideoEnhanceModelCatalog.TopazId, InferenceProviderCapability.VideoUpscale));

        (await VideoEnhanceStepPricing.TryStepHoldMillisAsync(Local, resolver.Object, prices.Object)).Should().Be(0L, "a local operation holds nothing");
        asked.Should().BeEmpty();

        long? hold = await VideoEnhanceStepPricing.TryStepHoldMillisAsync(Hosted, resolver.Object, prices.Object);
        hold.Should().Be(VideoEnhanceCreditPricing.DefaultMaxSourceSeconds * 1000L, "the hold is the cap in billed seconds");
        asked.Should().ContainSingle().Which.Unit.Should().Be(UsageKind.VideoEnhanceSeconds);
        asked[0].Variant.Should().Be("uhd-hfr", "the worst case of the reference source");

        asked.Clear();
        (await VideoEnhanceStepPricing.TryStepHoldMillisAsync(Hosted.Replace("}", ",\"maxSourceSeconds\":20}"), resolver.Object, prices.Object)).Should().Be(20_000L);
        (await VideoEnhanceStepPricing.TryStepHoldMillisAsync(Hosted.Replace("\"upscaleFactor\":2", "\"targetHeight\":720"), resolver.Object, prices.Object))
            .Should().Be(120_000L);
        asked.Last().Variant.Should().Be("720p-hfr");

        (await VideoEnhanceStepPricing.TryStepHoldMillisAsync(Hosted, Wire(null).Item1.Object, prices.Object)).Should().BeNull("no provider: fail closed");
        (await VideoEnhanceStepPricing.TryStepHoldMillisAsync(null, resolver.Object, prices.Object)).Should().BeNull();
        (await VideoEnhanceStepPricing.TryStepHoldMillisAsync("{nope", resolver.Object, prices.Object)).Should().BeNull();

        Mock<IModelPriceService> unpriced = new();
        unpriced.Setup(p => p.PriceAsync(It.IsAny<PriceRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new UnpricedUsageException(UsageKind.VideoEnhanceSeconds, InferenceProviderKind.FalAi, VideoEnhanceModelCatalog.TopazId));
        (await VideoEnhanceStepPricing.TryStepHoldMillisAsync(Hosted, resolver.Object, unpriced.Object)).Should().BeNull("unpriced is never free");
    }

    [Fact]
    public async Task A_measured_source_is_priced_at_its_real_tier_not_the_worst_case_reference()
    {
        var (resolver, prices, asked) = Wire(Row(VideoEnhanceModelCatalog.TopazId, InferenceProviderCapability.VideoUpscale));
        string Hints(int w, int h, double fps, string op) =>
            op.Replace("}", $",\"sourceWidth\":{w},\"sourceHeight\":{h},\"sourceFps\":{fps.ToString(System.Globalization.CultureInfo.InvariantCulture)},\"maxSourceSeconds\":10}}");
        string hosted = Hosted.Replace("\"upscaleFactor\":2", "\"targetHeight\":1080");

        // 1080p at 30 fps to 1080p: the 1080p tier, never the doubled high-frame-rate one; ten seconds is ten billed units.
        (await VideoEnhanceStepPricing.TryStepHoldMillisAsync(Hints(1920, 1080, 30, hosted), resolver.Object, prices.Object)).Should().Be(10_000L);
        asked.Last().Variant.Should().Be("1080p");

        // 4K at 30 fps, doubled: the uhd tier.
        (await VideoEnhanceStepPricing.TryStepHoldMillisAsync(Hints(3840, 2160, 30, Hosted), resolver.Object, prices.Object)).Should().Be(10_000L);
        asked.Last().Variant.Should().Be("uhd");

        // 60 fps carries the high-frame-rate twin.
        await VideoEnhanceStepPricing.TryStepHoldMillisAsync(Hints(3840, 2160, 60, Hosted), resolver.Object, prices.Object);
        asked.Last().Variant.Should().Be("uhd-hfr");

        // The hold is the same function the executor prices the real source with.
        VideoEnhanceCreditPricing.UpscaleTierFor(VideoEnhanceModelCatalog.Instance.Find(InferenceProviderCapability.VideoUpscale, VideoEnhanceModelCatalog.TopazId)!,
            VideoEnhanceCreditPricing.OutputShortSide(1920, 1080, 1.0), 30).Should().Be("1080p");
    }

    [Fact]
    public async Task A_measured_interpolation_source_is_priced_on_its_own_frames_and_pixels()
    {
        var (resolver, prices, asked) = Wire(Row(VideoEnhanceModelCatalog.RifeId, InferenceProviderCapability.FrameInterpolation));
        string cfg = HostedInterpolate.Replace("}", ",\"sourceWidth\":1920,\"sourceHeight\":1080,\"sourceFps\":30,\"maxSourceSeconds\":10}");

        // 10 s x 30 fps x2 = 600 frames x 0.05 s at 1080p = 30 compute seconds: exactly what the executor settles for the same source.
        (await VideoEnhanceStepPricing.TryStepHoldMillisAsync(cfg, resolver.Object, prices.Object)).Should().Be(30_000L);
        asked.Should().ContainSingle().Which.Quantity.Should().Be(30m);
    }

    [Fact]
    public async Task The_estimate_the_hold_and_the_route_all_read_the_same_configured_compute_rate_and_cap()
    {
        var (resolver, prices, asked) = Wire(Row(VideoEnhanceModelCatalog.RifeId, InferenceProviderCapability.FrameInterpolation));
        string cfg = HostedInterpolate.Replace("}", ",\"sourceWidth\":1920,\"sourceHeight\":1080,\"sourceFps\":30,\"maxSourceSeconds\":10}");
        Microsoft.Extensions.Configuration.IConfiguration tuned = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["VideoEnhance:Hosted:ComputeSecondsPerOutputFrame"] = "0.1" }).Build();
        VideoEnhanceHostedSettings settings = VideoEnhanceHostedSettings.From(tuned);

        long? defaultHold = await VideoEnhanceStepPricing.TryStepHoldMillisAsync(cfg, resolver.Object, prices.Object);
        long? tunedHold = await VideoEnhanceStepPricing.TryStepHoldMillisAsync(cfg, resolver.Object, prices.Object, default, settings);
        defaultHold.Should().Be(30_000L);
        tunedHold.Should().Be(60_000L, "a re-tune of the rate moves the hold");

        // The engine's estimator arm reads the same configuration, so what it holds is what the route shows.
        var factory = new Mock<Microsoft.EntityFrameworkCore.IDbContextFactory<ReelBolt.WorkflowEngine.Data.WorkflowEngineDbContext>>();
        var estimator = new UsageHistoryCreditHoldEstimator(
            factory.Object, Microsoft.Extensions.Options.Options.Create(new StepCreditEstimateOptions()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<UsageHistoryCreditHoldEstimator>.Instance,
            videoProviders: resolver.Object, prices: prices.Object, configuration: tuned);
        (await estimator.TryVideoEnhanceHoldMillisAsync(cfg)).Should().Be(tunedHold);
        asked.Should().NotBeEmpty();

        // The cap is read the same way, and a deployment can only lower it.
        VideoEnhanceHostedSettings.From(new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["VideoEnhance:Hosted:MaxSourceSeconds"] = "45", ["VideoEnhance:Local:MaxSourceSeconds"] = "9000" }).Build())
            .Should().Be(new VideoEnhanceHostedSettings(45, 600, 0.05m));
        VideoEnhanceHostedSettings.From(null).Should().Be(VideoEnhanceHostedSettings.Default);
        VideoEnhanceHostedSettings.From(new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["VideoEnhance:Hosted:ComputeSecondsPerOutputFrame"] = "-1" }).Build())
            .ComputeSecondsPerOutputFrame.Should().Be(0.05m);
    }

    [Fact]
    public async Task An_interpolation_hold_is_the_worst_case_compute_seconds_at_the_reference_source()
    {
        var (resolver, prices, asked) = Wire(Row(VideoEnhanceModelCatalog.RifeId, InferenceProviderCapability.FrameInterpolation));

        long? hold = await VideoEnhanceStepPricing.TryStepHoldMillisAsync(HostedInterpolate, resolver.Object, prices.Object);

        // 120 s x 60 fps x 2 = 14,400 output frames x 0.05 compute seconds x (1080p / 1080p).
        hold.Should().Be(720_000L);
        asked.Should().ContainSingle().Which.Unit.Should().Be(UsageKind.VideoEnhanceComputeSeconds);
    }

    [Fact]
    public async Task The_engine_hold_estimator_arm_answers_zero_for_local_and_falls_back_to_null_without_pricing_context()
    {
        var factory = new Mock<Microsoft.EntityFrameworkCore.IDbContextFactory<ReelBolt.WorkflowEngine.Data.WorkflowEngineDbContext>>();
        var bare = new UsageHistoryCreditHoldEstimator(
            factory.Object,
            Microsoft.Extensions.Options.Options.Create(new StepCreditEstimateOptions()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<UsageHistoryCreditHoldEstimator>.Instance);

        (await bare.TryVideoEnhanceHoldMillisAsync(Local)).Should().Be(0L);
        (await bare.TryVideoEnhanceHoldMillisAsync(Hosted)).Should().BeNull("no provider or price context: the cold-start row, never a zero hold");

        var (resolver, prices, _) = Wire(Row(VideoEnhanceModelCatalog.TopazId, InferenceProviderCapability.VideoUpscale));
        var priced = new UsageHistoryCreditHoldEstimator(
            factory.Object,
            Microsoft.Extensions.Options.Options.Create(new StepCreditEstimateOptions()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<UsageHistoryCreditHoldEstimator>.Instance,
            videoProviders: resolver.Object,
            prices: prices.Object);
        (await priced.TryVideoEnhanceHoldMillisAsync(Hosted)).Should().Be(VideoEnhanceCreditPricing.DefaultMaxSourceSeconds * 1000L);
    }

    // ---------------- rate class, failures, change requests ----------------

    [Fact]
    public void Every_enhance_failure_code_is_classified_and_the_refunded_ones_are_exactly_ours()
    {
        string[] codes =
        [
            "NO_VIDEO_ENHANCE_PROVIDER", "ENHANCE_PROVIDER_CONFIG", "ENHANCE_VENDOR_QUOTA", "ENHANCE_VENDOR_RETRYABLE", "ENHANCE_VENDOR_FAILED",
            "ENHANCE_VENDOR_REFUSED", "ENHANCE_SOURCE_TOO_LONG", "ENHANCE_SOURCE_NO_VIDEO", "ENHANCE_OUTPUT_TOO_LARGE", "ENHANCE_FPS_TOO_HIGH",
            "ENHANCE_NOT_LARGER", "ENHANCE_INVALID_REQUEST", "ENHANCE_REQUEST_INVALID", "ENHANCE_PROBE_FAILED", "ENHANCE_FFMPEG_FAILED",
            "ENHANCE_PICTURE_INVALID", "ENHANCE_DURATION_MISMATCH", "ENHANCE_EGRESS_UNAVAILABLE", "ENHANCE_UPLOAD_FAILED", "ENHANCE_FAILED",
            "ENHANCE_STILL_RUNNING"
        ];
        foreach (string code in codes)
            FailureClassifier.TryGetCode(code, out _).Should().BeTrue(code + " needs a deliberate class");
    }

    [Theory]
    [InlineData("EGRESS_REFUSED", FailureClass.UserInput)]
    [InlineData("NO_VIDEO_ENHANCE_PROVIDER", FailureClass.UserInput)]
    [InlineData("ENHANCE_SOURCE_TOO_LONG", FailureClass.UserInput)]
    [InlineData("ENHANCE_SOURCE_NO_VIDEO", FailureClass.UserInput)]
    [InlineData("ENHANCE_REQUEST_INVALID", FailureClass.UserInput)]
    [InlineData("ENHANCE_NOT_LARGER", FailureClass.UserInput)]
    [InlineData("PLAN_NOT_ENTITLED", FailureClass.EntitlementDenied)]
    [InlineData("ENHANCE_EGRESS_UNAVAILABLE", FailureClass.PlatformInternal)]
    [InlineData("ENHANCE_UPLOAD_FAILED", FailureClass.PlatformInternal)]
    [InlineData("ENHANCE_FAILED", FailureClass.PlatformInternal)]
    [InlineData("ENHANCE_FFMPEG_FAILED", FailureClass.PlatformInternal)]
    [InlineData("ENHANCE_DURATION_MISMATCH", FailureClass.PlatformProviderError)]
    [InlineData("ENHANCE_VENDOR_FAILED", FailureClass.PlatformProviderError)]
    [InlineData("ENHANCE_VENDOR_QUOTA", FailureClass.PlatformProviderError)]
    public void The_failure_codes_are_classified_deliberately(string code, FailureClass expected)
    {
        FailureClassifier.TryGetCode(code, out FailureClass mapped).Should().BeTrue();
        mapped.Should().Be(expected);
    }

    [Fact]
    public void VideoEnhance_cannot_be_changed_by_a_change_request()
    {
        StepChangeCapabilities.For(StepType.VideoEnhance).Supported.Should().BeFalse();
    }

    [Fact]
    public void VideoEnhance_depends_on_the_step_whose_output_it_enhances()
    {
        WorkflowStep make = new() { Id = Guid.NewGuid(), StepOrder = 1, StepType = StepType.VideoCompile, VideoCompileConfigJson = "{}" };
        WorkflowStep enhance = Step(2, """{"op":"Denoise","engine":"Local","denoiseStrength":"Light","source":{"kind":"StepOutput","stepOrder":1}}""");

        WorkflowStepDependencyGraph.Build([make, enhance]).DependsOn(2).Keys.Should().Contain(1);
    }

    [Fact]
    public void A_hosted_operation_names_its_provider_capability_and_a_local_one_names_none()
    {
        Guid id = Guid.NewGuid();
        StepProviderReferences.Extract(StepType.VideoEnhance, videoEnhanceConfigJson: Hosted.Replace("}", $",\"providerId\":\"{id}\"}}"))
            .Should().ContainSingle().Which.Should().Be(new StepProviderReference(InferenceProviderCapability.VideoUpscale, id, "providerId"));
        StepProviderReferences.Extract(StepType.VideoEnhance, videoEnhanceConfigJson: HostedInterpolate.Replace("}", $",\"providerId\":\"{id}\"}}"))
            .Should().ContainSingle().Which.Capability.Should().Be(InferenceProviderCapability.FrameInterpolation);
        StepProviderReferences.Extract(StepType.VideoEnhance, videoEnhanceConfigJson: Hosted).Should().BeEmpty("no explicit id: the default row is fingerprinted by capability");
        StepProviderReferences.Extract(StepType.VideoEnhance, videoEnhanceConfigJson: Local.Replace("}", $",\"providerId\":\"{id}\"}}")).Should().BeEmpty();
    }

    [Fact]
    public void ListStepSchemas_exports_the_VideoEnhance_config()
    {
        StepConfigSchemaResult schema = new StepConfigSchemaExporter().GetSchema(StepType.VideoEnhance);

        schema.HasConfig.Should().BeTrue();
        schema.ConfigTypeName.Should().Be(nameof(VideoEnhanceStepConfig));
        schema.Schema!.ToJsonString().Should().Contain("allowSourceMediaEgress").And.Contain("upscaleFactor").And.Contain("maxSourceSeconds")
            .And.NotContain("isHosted").And.NotContain("callsAnyProvider");
    }

    [Fact]
    public void The_executor_is_registered_for_the_step_type_and_the_column_is_on_the_step()
    {
        typeof(WorkflowStep).GetProperty(nameof(WorkflowStep.VideoEnhanceConfigJson)).Should().NotBeNull();
        typeof(VideoEnhanceStepExecutor).GetProperty(nameof(VideoEnhanceStepExecutor.StepType)).Should().NotBeNull();
        Enum.GetValues<StepType>().Last().Should().Be(StepType.ImageGenerate, "appended at the end (ImageGenerate follows VideoEnhance)");
        ((int)StepType.VideoEnhance).Should().Be(23, "the integer is the order (AudioCleanup is 22); the enum is persisted by name but never renumbered");
    }
}
