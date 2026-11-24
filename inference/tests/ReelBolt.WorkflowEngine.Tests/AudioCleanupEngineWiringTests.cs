using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
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
using ReelBolt.WorkflowEngine.Services.Billing;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>The engine-wide decisions for AudioCleanup: cache, credits, rate class, failures, dependencies, providers, schema.</summary>
public sealed class AudioCleanupEngineWiringTests
{
    private const string Model = "fal-ai/elevenlabs/audio-isolation";
    private const string Local = """{"target":"Hum","source":{"kind":"ProjectFile","projectFileId":"11111111-1111-1111-1111-111111111111"}}""";
    private const string Hosted = """{"target":"IsolateVoice","source":{"kind":"ProjectFile","projectFileId":"11111111-1111-1111-1111-111111111111"},"allowSourceMediaEgress":true}""";

    private static WorkflowStep Step(int order, string? configJson) => new()
    {
        Id = Guid.NewGuid(),
        StepOrder = order,
        StepType = StepType.AudioCleanup,
        AudioCleanupConfigJson = configJson
    };

    // ---------------- cache ----------------

    [Fact]
    public void AudioCleanup_is_cacheable_and_has_a_deterministic_revision()
    {
        new StepCachePolicy().IsCacheable(Step(2, Local)).Should().BeTrue();
        DeterministicStepRevisions.For(StepType.AudioCleanup).Should().Be("2026-11-02.audio-cleanup-1");
    }

    private static StepCacheKeyInputs Inputs(string? configJson, string? providerFingerprint = "prov") => new()
    {
        ProjectId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
        StepType = StepType.AudioCleanup,
        AgentType = AgentType.Custom,
        AgentDefinitionId = Guid.Empty,
        AgentInputContextMode = AgentInputContextMode.PreviousStepOnly,
        ProjectFileFingerprint = "fp",
        ResolvedProviderFingerprint = providerFingerprint,
        MaxIterations = 1,
        AudioCleanupConfigJson = configJson
    };

    [Fact]
    public void The_cache_key_moves_with_the_target_the_source_and_the_provider_and_never_serves_another_target()
    {
        var builder = new StepCacheKeyBuilder();
        string hum = builder.Build(Inputs(Local));

        builder.Build(Inputs(Local.Replace("Hum", "Wind"))).Should().NotBe(hum, "a different target is a different result");
        builder.Build(Inputs(Local.Replace("11111111-1111-1111-1111-111111111111", "22222222-2222-2222-2222-222222222222"))).Should().NotBe(hum, "a different source");
        builder.Build(Inputs(Local, "other-provider")).Should().NotBe(hum, "a different resolved provider");
        builder.Build(Inputs(Hosted)).Should().NotBe(builder.Build(Inputs(Hosted.Replace("true", "false"))), "the consent is part of the config, so a refusal never poisons an allowed run");
        builder.Build(Inputs(null)).Should().NotBe(hum);
        builder.Build(Inputs(Local)).Should().Be(hum, "the same inputs are the same key");
    }

    [Fact]
    public void A_step_with_no_cleanup_config_keeps_the_key_it_had_before()
    {
        var builder = new StepCacheKeyBuilder();
        StepCacheKeyInputs other = Inputs(null) with { StepType = StepType.PinInsert };
        builder.Build(other).Should().Be(builder.Build(other with { AudioCleanupConfigJson = null }));
    }

    // ---------------- credits ----------------

    [Fact]
    public void A_local_target_is_free_holds_nothing_and_a_hosted_one_is_charged()
    {
        StepCreditDefaults.IsLocalAudioCleanup(Local).Should().BeTrue();
        StepCreditDefaults.IsLocalAudioCleanup(Hosted).Should().BeFalse();
        StepCreditDefaults.IsLocalAudioCleanup(null).Should().BeFalse("no config cannot prove a local target");
        StepCreditDefaults.IsLocalAudioCleanup("{nope").Should().BeFalse();

        StepCreditDefaults.IsComputeOnly(Step(2, Local)).Should().BeTrue();
        StepCreditDefaults.IsComputeOnly(Step(2, Hosted)).Should().BeFalse();
        StepCreditDefaults.MillisFor(StepType.AudioCleanup, 2m, computeOnly: true).Should().Be((0L, 0L));
        StepCreditDefaults.IsNotCharged(StepType.AudioCleanup).Should().BeFalse("the type is chargeable; only a local config is free");
        (long low, long high) = StepCreditDefaults.MillisFor(StepType.AudioCleanup);
        low.Should().BePositive();
        high.Should().BeGreaterThanOrEqualTo(low);

        var estimator = new StepCreditEstimator([]);
        estimator.Estimate(StepType.AudioCleanup, null, computeOnly: true).Should().Be(new StepCreditEstimate(0, 0, 0, CreditEstimateBasis.NotCharged));
        StepCreditEstimate hosted = estimator.Estimate(StepType.AudioCleanup, null);
        hosted.HoldMillis.Should().BePositive();
        // Parity: the cold-start (API estimate) row and the price-book hold are both the cap at the default endpoint price.
        StepCreditDefaults.MillisFor(StepType.AudioCleanup, PricingDefaults.DefaultMarkup).Low.Should()
            .Be((long)Math.Round(30 * AudioIsolationModelCatalog.Instance.Default.UsdPerMinute * PricingDefaults.CreditsMillisPerUsd * PricingDefaults.DefaultMarkup, MidpointRounding.AwayFromZero));
        RunCreditHolds.IsCold(StepType.AudioCleanup, hosted).Should().BeFalse("a fixed-price media step is never re-planned up");
    }

    [Fact]
    public void The_usage_profile_of_a_local_target_has_no_quantities_and_a_hosted_one_is_the_fixed_price_row()
    {
        StepUsageProfiles.For(Step(2, Local), null).Quantities.Should().BeEmpty();
        StepUsageProfiles.For(Step(2, Hosted), null).Should().BeSameAs(StepUsageProfiles.For(StepType.AudioCleanup, null));
        ColdStartStepPricing.IsAgentShaped(StepType.AudioCleanup).Should().BeFalse();
    }

    private static (Mock<IInferenceProviderResolver>, Mock<IModelPriceService>, List<PriceRequest>) Wire(ResolvedAudioGenerationProvider? row)
    {
        Mock<IInferenceProviderResolver> resolver = new();
        resolver.Setup(r => r.ResolveAudioIsolationAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>())).ReturnsAsync(row);
        List<PriceRequest> asked = [];
        Mock<IModelPriceService> prices = new();
        prices.Setup(p => p.PriceAsync(It.IsAny<PriceRequest>(), It.IsAny<CancellationToken>()))
            .Callback<PriceRequest, CancellationToken>((r, _) => asked.Add(r))
            .ReturnsAsync((PriceRequest r, CancellationToken _) => new PriceQuote((long)(r.Quantity * 1000), 0m, Guid.NewGuid()));
        return (resolver, prices, asked);
    }

    private static ResolvedAudioGenerationProvider Row() =>
        new(Guid.NewGuid(), "row", InferenceProviderKind.FalAi, InferenceProviderCapability.AudioIsolation, "", Model, "test", 60, AccountId: Guid.NewGuid());

    [Fact]
    public async Task The_hold_uses_the_price_book_is_zero_for_local_and_null_when_unpriced()
    {
        var (resolver, prices, asked) = Wire(Row());

        (await AudioCleanupCreditPricing.TryStepHoldMillisAsync(Local, resolver.Object, prices.Object)).Should().Be(0L, "a local target holds nothing");
        asked.Should().BeEmpty();

        long? hold = await AudioCleanupCreditPricing.TryStepHoldMillisAsync(Hosted, resolver.Object, prices.Object);
        hold.Should().Be(AudioCleanupCreditPricing.DefaultMaxSourceMinutes * 1000L, "the hold is the effective cap");
        asked.Should().ContainSingle().Which.Unit.Should().Be(UsageKind.AudioCleanupMinutes);

        asked.Clear();
        (await AudioCleanupCreditPricing.TryStepHoldMillisAsync(Hosted.Replace("}", ",\"maxSourceMinutes\":3}"), resolver.Object, prices.Object)).Should().Be(3000L);
        (await AudioCleanupCreditPricing.TryStepHoldMillisAsync(Hosted.Replace("}", ",\"maxSourceMinutes\":600}"), resolver.Object, prices.Object)).Should().Be(30_000L, "the platform cap clamps");

        (await AudioCleanupCreditPricing.TryStepHoldMillisAsync(Hosted, Wire(null).Item1.Object, prices.Object)).Should().BeNull("no provider: fail closed");
        (await AudioCleanupCreditPricing.TryStepHoldMillisAsync(null, resolver.Object, prices.Object)).Should().BeNull();
        (await AudioCleanupCreditPricing.TryStepHoldMillisAsync("{nope", resolver.Object, prices.Object)).Should().BeNull();

        Mock<IModelPriceService> unpriced = new();
        unpriced.Setup(p => p.PriceAsync(It.IsAny<PriceRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new UnpricedUsageException(UsageKind.AudioCleanupMinutes, InferenceProviderKind.FalAi, Model));
        (await AudioCleanupCreditPricing.TryStepHoldMillisAsync(Hosted, resolver.Object, unpriced.Object)).Should().BeNull("unpriced is never free");
    }

    [Fact]
    public async Task The_engine_hold_estimator_arm_answers_zero_for_local_and_falls_back_to_null_without_pricing_context()
    {
        var factory = new Mock<Microsoft.EntityFrameworkCore.IDbContextFactory<ReelBolt.WorkflowEngine.Data.WorkflowEngineDbContext>>();
        var bare = new UsageHistoryCreditHoldEstimator(
            factory.Object,
            Microsoft.Extensions.Options.Options.Create(new StepCreditEstimateOptions()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<UsageHistoryCreditHoldEstimator>.Instance);

        (await bare.TryAudioCleanupHoldMillisAsync(Local)).Should().Be(0L);
        (await bare.TryAudioCleanupHoldMillisAsync(Hosted)).Should().BeNull("no provider or price context: the cold-start row, never a zero hold");

        var (resolver, prices, _) = Wire(Row());
        var priced = new UsageHistoryCreditHoldEstimator(
            factory.Object,
            Microsoft.Extensions.Options.Options.Create(new StepCreditEstimateOptions()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<UsageHistoryCreditHoldEstimator>.Instance,
            videoProviders: resolver.Object,
            prices: prices.Object);
        (await priced.TryAudioCleanupHoldMillisAsync(Hosted)).Should().Be(AudioCleanupCreditPricing.DefaultMaxSourceMinutes * 1000L);
    }

    [Fact]
    public void The_credit_entitlement_of_the_step_is_none_because_the_plan_feature_is_config_driven()
    {
        WorkflowExecutorService.ResolveCreditEntitlement(StepType.AudioCleanup).Should().Be(CreditEntitlement.None);
    }

    // ---------------- rate class, failures, change requests ----------------

    [Fact]
    public void AudioCleanup_is_free_for_a_local_target_and_paid_for_a_hosted_one()
    {
        RunRateClassifier.Classify([Step(2, Local)]).Should().Be(RunRateBucket.Free);
        RunRateClassifier.Classify([Step(2, Hosted)]).Should().Be(RunRateBucket.Paid);
    }

    [Theory]
    [InlineData("EGRESS_REFUSED", FailureClass.UserInput)]
    [InlineData("NO_AUDIO_ISOLATION_PROVIDER", FailureClass.UserInput)]
    [InlineData("AUDIO_SOURCE_TOO_LONG", FailureClass.UserInput)]
    [InlineData("AUDIO_SOURCE_NO_AUDIO", FailureClass.UserInput)]
    [InlineData("AUDIO_OUTPUT_KIND_UNSUPPORTED", FailureClass.UserInput)]
    [InlineData("PLAN_NOT_ENTITLED", FailureClass.EntitlementDenied)]
    [InlineData("AUDIO_EGRESS_UNAVAILABLE", FailureClass.PlatformInternal)]
    [InlineData("AUDIO_CLEANUP_UPLOAD_FAILED", FailureClass.PlatformInternal)]
    [InlineData("AUDIO_CLEANUP_FAILED", FailureClass.PlatformInternal)]
    [InlineData("AUDIO_DURATION_MISMATCH", FailureClass.PlatformProviderError)]
    [InlineData("AUDIO_VENDOR_FAILED", FailureClass.PlatformProviderError)]
    public void The_failure_codes_are_classified_deliberately(string code, FailureClass expected)
    {
        FailureClassifier.TryGetCode(code, out FailureClass mapped).Should().BeTrue();
        mapped.Should().Be(expected);
        // Every refundable class is refunded; the user-input and entitlement ones are not charged for what was never consumed.
        if (expected is FailureClass.PlatformInternal or FailureClass.PlatformProviderError)
            CreditGate.IsRefundable(mapped).Should().BeTrue();
    }

    [Fact]
    public void A_change_request_cannot_target_AudioCleanup()
    {
        StepChangeCapabilities.For(StepType.AudioCleanup).Supported.Should().BeFalse();
    }

    // ---------------- graph, providers, schema ----------------

    [Fact]
    public void AudioCleanup_depends_on_the_step_whose_output_it_cleans()
    {
        WorkflowStep make = new() { Id = Guid.NewGuid(), StepOrder = 1, StepType = StepType.VideoCompile, VideoCompileConfigJson = "{}" };
        WorkflowStep clean = Step(2, """{"target":"Wind","source":{"kind":"StepOutput","stepOrder":1}}""");

        WorkflowStepDependencyGraph.Build([make, clean]).DependsOn(2).Keys.Should().Contain(1);
    }

    [Fact]
    public void A_hosted_target_names_its_isolation_provider_and_a_local_one_names_none()
    {
        Guid id = Guid.NewGuid();
        IReadOnlyList<StepProviderReference> hosted = StepProviderReferences.Extract(
            StepType.AudioCleanup, audioCleanupConfigJson: Hosted.Replace("}", $",\"providerId\":\"{id}\"}}"));
        hosted.Should().ContainSingle().Which.Should().Be(new StepProviderReference(InferenceProviderCapability.AudioIsolation, id, "providerId"));

        StepProviderReferences.Extract(StepType.AudioCleanup, audioCleanupConfigJson: Hosted).Should().BeEmpty("no explicit id: the default row is fingerprinted by capability");
        StepProviderReferences.Extract(StepType.AudioCleanup, audioCleanupConfigJson: Local.Replace("}", $",\"providerId\":\"{id}\"}}")).Should().BeEmpty();
    }

    [Fact]
    public void ListStepSchemas_exports_the_AudioCleanup_config()
    {
        StepConfigSchemaResult schema = new StepConfigSchemaExporter().GetSchema(StepType.AudioCleanup);

        schema.HasConfig.Should().BeTrue();
        schema.ConfigTypeName.Should().Be(nameof(AudioCleanupStepConfig));
        schema.Schema!.ToJsonString().Should().Contain("allowSourceMediaEgress").And.Contain("outputKind").And.Contain("maxSourceMinutes")
            .And.NotContain("isHosted").And.NotContain("callsAnyProvider");
    }

    [Fact]
    public void The_save_validator_reads_the_cleanup_config_column_and_a_step_result_audio_is_a_usable_source()
    {
        typeof(WorkflowStep).GetProperty(nameof(WorkflowStep.AudioCleanupConfigJson)).Should().NotBeNull();
        StepResultMediaExtensions.Audio.Should().Contain([".wav", ".mp3", ".m4a", ".aac", ".ogg", ".flac"]);
        StepResultMediaExtensions.Audio.Intersect(StepResultMediaExtensions.Video).Should().BeEmpty();
    }
}
