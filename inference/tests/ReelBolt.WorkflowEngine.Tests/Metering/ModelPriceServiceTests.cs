#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Metering;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Metering;

/// <summary>
/// The price lookup (docs/design/model-catalog.md §5.3): which capability prices a unit (four steps), which row
/// of it (variant, effective dating), and what it costs (markup, override, multiplier, rounding). Rewrites the
/// pricing half of the old <c>RateCardServiceTests</c> against <see cref="ModelPriceService"/>.
/// </summary>
public class ModelPriceServiceTests
{
    private static readonly DateTime Past = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Now = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    private static readonly Guid Account = Guid.NewGuid();
    private static readonly Guid OtherAccount = Guid.NewGuid();

    private static PricedCapability Cap(
        string code,
        InferenceProviderCapability capability = InferenceProviderCapability.ImageEdit,
        Guid? account = null,
        InferenceProviderKind kind = InferenceProviderKind.FalAi,
        Guid? org = null) =>
        new(Guid.NewGuid(), account ?? Account, org, kind, capability, code);

    private static ModelCapabilityPrice Row(
        PricedCapability cap, decimal costUsd, UsageKind unit = UsageKind.ImageEditImages, string? variant = null,
        decimal? overrideMillis = null, DateTime? from = null, DateTime? to = null) => new()
        {
            Id = Guid.NewGuid(),
            ModelCapabilityId = cap.Id,
            Unit = unit,
            Variant = variant,
            CostUsdPerUnit = costUsd,
            CreditsMillisPerUnitOverride = overrideMillis,
            EffectiveFrom = from ?? Past,
            EffectiveTo = to
        };

    private static RateMultiplier Mult(UsageBillingMode mode, decimal value, UsageKind? kind = null, DateTime? from = null) => new()
    {
        Id = Guid.NewGuid(),
        BillingMode = mode,
        UsageKind = kind,
        Multiplier = value,
        EffectiveFrom = from ?? Past
    };

    private static ModelPriceSnapshot Snapshot(
        IEnumerable<PricedCapability> caps, IEnumerable<ModelCapabilityPrice> rows,
        IEnumerable<RateMultiplier>? multipliers = null, decimal markup = 1.2m) =>
        new(caps.ToList(), rows.ToList(), (multipliers ?? []).ToList(), markup, 0.006m);

    private static PriceRequest Request(
        PricedCapability? cap, string? model, UsageKind unit = UsageKind.ImageEditImages, string? variant = null,
        UsageBillingMode mode = UsageBillingMode.Platform, decimal quantity = 1, Guid? account = null,
        InferenceProviderKind kind = InferenceProviderKind.FalAi,
        InferenceProviderCapability capability = InferenceProviderCapability.ImageEdit) =>
        new(cap?.Id, account, cap?.Kind ?? kind, cap?.Capability ?? capability, model, unit, variant, mode, quantity, Now);

    private static PriceQuote Price(ModelPriceSnapshot snapshot, PriceRequest request) =>
        ModelPriceService.Price(snapshot, request, request.At ?? Now);

    /// <summary>$0.006 is one credit, times the 1.2 markup: 1200 millicredits per unit.</summary>
    [Fact]
    public void Price_ReferencedCapability_CostsCostTimesMarkupOverCreditValue()
    {
        PricedCapability cap = Cap("fal-ai/flux");
        ModelCapabilityPrice row = Row(cap, 0.006m);

        PriceQuote quote = Price(Snapshot([cap], [row]), Request(cap, "fal-ai/flux", quantity: 3));

        quote.Should().Be(new PriceQuote(3600, 0.018m, row.Id));
    }

    [Fact]
    public void Price_ReferencedCapability_WithNoReportedModel_UsesItsOwnRow()
    {
        PricedCapability cap = Cap("fal-ai/flux");
        ModelCapabilityPrice row = Row(cap, 0.006m);

        Price(Snapshot([cap], [row]), Request(cap, null)).PriceId.Should().Be(row.Id);
        Price(Snapshot([cap], [row]), Request(cap, "")).PriceId.Should().Be(row.Id);
    }

    [Fact]
    public void Price_ModelOfASiblingCapability_UsesTheSiblingUnderTheSameAccount()
    {
        PricedCapability configured = Cap("fal-ai/cheap");
        PricedCapability sibling = Cap("fal-ai/other");
        PricedCapability elsewhere = Cap("fal-ai/other", account: OtherAccount);
        ModelCapabilityPrice configuredRow = Row(configured, 0.006m);
        ModelCapabilityPrice siblingRow = Row(sibling, 0.012m);
        ModelCapabilityPrice elsewhereRow = Row(elsewhere, 0.5m);

        PriceQuote quote = Price(Snapshot([configured, sibling, elsewhere], [configuredRow, siblingRow, elsewhereRow]),
            Request(configured, "fal-ai/other"));

        quote.PriceId.Should().Be(siblingRow.Id, "step 2: same capability, same account, the model that ran");
    }

    [Fact]
    public void Price_LegacyConfigFallback_UsesPlatformCapabilityOfSameKindAndCode()
    {
        PricedCapability platform = Cap("gpt-4o", InferenceProviderCapability.Chat, kind: InferenceProviderKind.AzureOpenAI);
        PricedCapability byo = Cap("gpt-4o", InferenceProviderCapability.Chat, account: OtherAccount,
            kind: InferenceProviderKind.AzureOpenAI, org: Guid.NewGuid());
        PricedCapability otherKind = Cap("gpt-4o", InferenceProviderCapability.Chat, kind: InferenceProviderKind.OpenAICompatible);
        ModelCapabilityPrice platformRow = Row(platform, 0.000002m, UsageKind.LlmInputTokens);
        ModelCapabilityPrice byoRow = Row(byo, 1m, UsageKind.LlmInputTokens);
        ModelCapabilityPrice otherKindRow = Row(otherKind, 1m, UsageKind.LlmInputTokens);

        // The AzureOpenAI:* configuration fallback has no capability row and no account.
        PriceQuote quote = Price(Snapshot([byo, otherKind, platform], [byoRow, otherKindRow, platformRow]),
            Request(null, "gpt-4o", UsageKind.LlmInputTokens, kind: InferenceProviderKind.AzureOpenAI,
                capability: InferenceProviderCapability.Chat, quantity: 1000));

        quote.PriceId.Should().Be(platformRow.Id);
        quote.CreditsMillis.Should().Be(400);
    }

    /// <summary>
    /// Review focus 3: a picture op or a video step can ask for a model nobody configured. It must never price
    /// below the configured capability, so it takes the dearest row the account has for that capability.
    /// </summary>
    [Fact]
    public void Price_ExplicitUnknownModel_FailsExpensive()
    {
        PricedCapability cheap = Cap("fal-ai/cheap");
        PricedCapability dear = Cap("fal-ai/dear");
        PricedCapability otherCapability = Cap("fal-ai/dearest", InferenceProviderCapability.ImageGeneration);
        ModelCapabilityPrice cheapRow = Row(cheap, 0.006m);
        ModelCapabilityPrice dearRow = Row(dear, 0.06m);
        ModelCapabilityPrice dearTier = Row(dear, 0.03m, variant: "low");
        ModelCapabilityPrice otherRow = Row(otherCapability, 9m, UsageKind.ImageGenImages);

        PriceQuote quote = Price(Snapshot([cheap, dear, otherCapability], [cheapRow, dearRow, dearTier, otherRow]),
            Request(cheap, "fal-ai/not-configured-anywhere"));

        quote.PriceId.Should().Be(dearRow.Id);
        quote.CreditsMillis.Should().BeGreaterThan(Price(Snapshot([cheap], [cheapRow]), Request(cheap, "fal-ai/cheap")).CreditsMillis);
    }

    /// <summary>
    /// QA-2: a caller's AccountId cached before a consolidation names the merged-away provider. The referenced
    /// capability's account is the truth now, so fail-expensive still finds the account's rows and never prices
    /// an explicit unknown model at 0.
    /// </summary>
    [Fact]
    public void Price_ExplicitUnknownModel_WithStaleAccountId_UsesTheReferencedCapabilitysAccount()
    {
        PricedCapability cheap = Cap("fal-ai/cheap");
        PricedCapability dear = Cap("fal-ai/dear");
        ModelCapabilityPrice cheapRow = Row(cheap, 0.006m);
        ModelCapabilityPrice dearRow = Row(dear, 0.06m);
        Guid mergedAway = Guid.NewGuid();

        PriceQuote quote = Price(Snapshot([cheap, dear], [cheapRow, dearRow]),
            Request(cheap, "fal-ai/not-configured-anywhere", account: mergedAway));

        quote.PriceId.Should().Be(dearRow.Id);
        quote.CreditsMillis.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Price_ExplicitUnknownModel_WithNothingPricedOnTheAccount_FailsClosed()
    {
        PricedCapability cap = Cap("fal-ai/cheap");
        PricedCapability elsewhere = Cap("fal-ai/x", account: OtherAccount);

        Action act = () => Price(Snapshot([cap, elsewhere], [Row(elsewhere, 1m)]), Request(cap, "fal-ai/unknown"));

        act.Should().Throw<UnpricedUsageException>().Which.ModelName.Should().Be("fal-ai/unknown");
    }

    [Fact]
    public void Price_Variant_ExactThenNullThenHighest()
    {
        PricedCapability cap = Cap("MiniMax-H3", InferenceProviderCapability.VideoGeneration, kind: InferenceProviderKind.MiniMax);
        ModelCapabilityPrice p768 = Row(cap, 0.08m, UsageKind.VideoGenSeconds, "768P");
        ModelCapabilityPrice p2k = Row(cap, 0.13m, UsageKind.VideoGenSeconds, "2K");
        ModelCapabilityPrice any = Row(cap, 0.07m, UsageKind.VideoGenSeconds);

        ModelPriceSnapshot withNull = Snapshot([cap], [p768, p2k, any]);
        Price(withNull, Request(cap, "MiniMax-H3", UsageKind.VideoGenSeconds, "2K")).PriceId.Should().Be(p2k.Id);
        Price(withNull, Request(cap, "MiniMax-H3", UsageKind.VideoGenSeconds, "2k")).PriceId.Should().Be(p2k.Id, "variants compare case-insensitively");
        Price(withNull, Request(cap, "MiniMax-H3", UsageKind.VideoGenSeconds, "4K")).PriceId.Should().Be(any.Id);
        Price(withNull, Request(cap, "MiniMax-H3", UsageKind.VideoGenSeconds, null)).PriceId.Should().Be(any.Id);

        ModelPriceSnapshot variantsOnly = Snapshot([cap], [p768, p2k]);
        Price(variantsOnly, Request(cap, "MiniMax-H3", UsageKind.VideoGenSeconds, "4K")).PriceId.Should().Be(p2k.Id);
        Price(variantsOnly, Request(cap, "MiniMax-H3", UsageKind.VideoGenSeconds, null)).PriceId.Should().Be(p2k.Id);
    }

    [Fact]
    public void Price_Override_ReplacesCostTimesMarkup_ButCostIsStillRecorded()
    {
        PricedCapability cap = Cap("m");
        ModelCapabilityPrice row = Row(cap, 0.006m, overrideMillis: 250m);

        Price(Snapshot([cap], [row]), Request(cap, "m", quantity: 4)).Should().Be(new PriceQuote(1000, 0.024m, row.Id));
    }

    [Fact]
    public void Price_Markup_ComesFromTheSettings()
    {
        PricedCapability cap = Cap("m");
        ModelCapabilityPrice row = Row(cap, 0.006m);

        Price(Snapshot([cap], [row], markup: 1.5m), Request(cap, "m")).CreditsMillis.Should().Be(1500);
        Price(Snapshot([cap], [row], markup: 1m), Request(cap, "m")).CreditsMillis.Should().Be(1000);
    }

    [Fact]
    public void Price_EffectiveDating_PicksTheRowInForceAtTheUsageTime()
    {
        PricedCapability cap = Cap("m");
        DateTime switchover = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        ModelCapabilityPrice old = Row(cap, 0.006m, from: Past, to: switchover);
        ModelCapabilityPrice current = Row(cap, 0.012m, from: switchover);
        ModelCapabilityPrice future = Row(cap, 1m, from: Now.AddDays(1));
        ModelPriceSnapshot snapshot = Snapshot([cap], [old, current, future]);

        Price(snapshot, Request(cap, "m") with { At = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc) }).PriceId.Should().Be(old.Id);
        Price(snapshot, Request(cap, "m")).PriceId.Should().Be(current.Id);

        Action beforeAnyPrice = () => Price(snapshot, Request(cap, "m") with { At = Past.AddDays(-1) });
        beforeAnyPrice.Should().Throw<UnpricedUsageException>();
    }

    [Fact]
    public void Price_TwoOpenRowsForOneVariant_TheLaterStartWins()
    {
        PricedCapability cap = Cap("m");
        ModelCapabilityPrice first = Row(cap, 0.006m, from: Past);
        ModelCapabilityPrice second = Row(cap, 0.012m, from: Past.AddDays(10));

        Price(Snapshot([cap], [second, first]), Request(cap, "m")).PriceId.Should().Be(second.Id);
    }

    [Fact]
    public void Price_Multipliers_DefaultToPlatformOneAndByoRunnerZero()
    {
        PricedCapability cap = Cap("m");
        ModelPriceSnapshot snapshot = Snapshot([cap], [Row(cap, 0.006m)]);

        Price(snapshot, Request(cap, "m", mode: UsageBillingMode.Platform)).CreditsMillis.Should().Be(1200);
        Price(snapshot, Request(cap, "m", mode: UsageBillingMode.Byo)).CreditsMillis.Should().Be(0);
        Price(snapshot, Request(cap, "m", mode: UsageBillingMode.Runner)).CreditsMillis.Should().Be(0);
    }

    [Fact]
    public void Price_Multipliers_KindSpecificBeatsTheModeDefault_AndTheLaterStartWins()
    {
        PricedCapability cap = Cap("compute", InferenceProviderCapability.Compute, kind: InferenceProviderKind.OpenAICompatible);
        ModelPriceSnapshot snapshot = Snapshot(
            [cap],
            [Row(cap, 0.006m, UsageKind.RenderOutputSeconds, "720p"), Row(cap, 0.006m, UsageKind.EncodeOutputSeconds, "720p")],
            [
                Mult(UsageBillingMode.Runner, 0m),
                Mult(UsageBillingMode.Runner, 0.1m, UsageKind.EncodeOutputSeconds),
                Mult(UsageBillingMode.Runner, 0.25m, UsageKind.EncodeOutputSeconds, Past.AddDays(1)),
                Mult(UsageBillingMode.Platform, 2m)
            ]);

        Price(snapshot, Request(cap, null, UsageKind.RenderOutputSeconds, "720p", UsageBillingMode.Runner, 10)).CreditsMillis.Should().Be(0);
        Price(snapshot, Request(cap, null, UsageKind.EncodeOutputSeconds, "720p", UsageBillingMode.Runner, 10)).CreditsMillis.Should().Be(3000);
        Price(snapshot, Request(cap, null, UsageKind.RenderOutputSeconds, "720p", UsageBillingMode.Platform, 10)).CreditsMillis.Should().Be(24000);
    }

    [Fact]
    public void Price_Byo_IsZeroCreditsAndZeroCost()
    {
        PricedCapability cap = Cap("m");
        ModelCapabilityPrice row = Row(cap, 2m);

        PriceQuote quote = Price(Snapshot([cap], [row]), Request(cap, "m", mode: UsageBillingMode.Byo, quantity: 3));

        quote.CreditsMillis.Should().Be(0);
        quote.CostUsd.Should().Be(0m, "a BYO call is the organization's own cost, never ours");
    }

    [Theory]
    [InlineData(UsageBillingMode.Byo)]
    [InlineData(UsageBillingMode.Runner)]
    public void Price_ByoOrRunnerWithoutAnyRow_IsFreeNotAnError(UsageBillingMode mode)
    {
        PricedCapability cap = Cap("m", org: Guid.NewGuid());

        Price(Snapshot([cap], []), Request(cap, "m", mode: mode)).Should().Be(new PriceQuote(0, 0m, null));
        Price(Snapshot([], []), Request(null, "m", mode: mode)).Should().Be(new PriceQuote(0, 0m, null));
    }

    [Fact]
    public void Price_PlatformWithNothingPriced_FailsClosed()
    {
        PricedCapability cap = Cap("m");

        Action unknownCapability = () => Price(Snapshot([], []), Request(null, "m"));
        Action wrongUnit = () => Price(Snapshot([cap], [Row(cap, 1m, UsageKind.ImageGenImages)]), Request(cap, "m"));

        unknownCapability.Should().Throw<UnpricedUsageException>().Which.Kind.Should().Be(UsageKind.ImageEditImages);
        wrongUnit.Should().Throw<UnpricedUsageException>();
    }

    [Fact]
    public void Price_TinyPlatformCharge_FloorsAtOneMillicredit_ButAZeroPriceStaysZero()
    {
        PricedCapability cap = Cap("m");

        Price(Snapshot([cap], [Row(cap, 0.000000001m)]), Request(cap, "m")).CreditsMillis.Should().Be(1);
        Price(Snapshot([cap], [Row(cap, 0m)]), Request(cap, "m")).CreditsMillis.Should().Be(0);
    }

    [Fact]
    public void Price_RoundsHalfAwayFromZeroOncePerEvent()
    {
        PricedCapability cap = Cap("m");

        Price(Snapshot([cap], [Row(cap, 0m, overrideMillis: 0.5m)]), Request(cap, "m", quantity: 3)).CreditsMillis.Should().Be(2);
    }

    [Fact]
    public async Task PriceQuotedUsd_ConvertsAtTheSameMarkupAndMultiplier_WithNoPriceRow()
    {
        FakeSource source = new(Snapshot([], [], [Mult(UsageBillingMode.Byo, 0.5m)]));
        ModelPriceService service = new(source, new FakeTime(Now));

        (await service.PriceQuotedUsdAsync(0.6m, UsageKind.VideoGenSeconds, UsageBillingMode.Platform))
            .Should().Be(new PriceQuote(120_000, 0.6m, null));
        (await service.PriceQuotedUsdAsync(0.6m, UsageKind.VideoGenSeconds, UsageBillingMode.Byo))
            .Should().Be(new PriceQuote(60_000, 0m, null));
    }

    [Fact]
    public async Task Snapshot_IsCachedForSixtySeconds_ThenReloaded_AndInvalidateForcesAReload()
    {
        PricedCapability cap = Cap("m");
        FakeSource source = new(Snapshot([cap], [Row(cap, 0.006m)]));
        FakeTime time = new(Now);
        ModelPriceService service = new(source, time);
        PriceRequest request = Request(cap, "m") with { At = null };

        await service.PriceAsync(request);
        await service.PriceAsync(request);
        source.Loads.Should().Be(1);

        time.Now = time.Now.AddSeconds(61);
        await service.PriceAsync(request);
        source.Loads.Should().Be(2);

        service.Invalidate();
        await service.PriceAsync(request);
        source.Loads.Should().Be(3);
    }

    private sealed class FakeSource(ModelPriceSnapshot snapshot) : IModelPriceSource
    {
        public int Loads;

        public Task<ModelPriceSnapshot> LoadAsync(CancellationToken ct)
        {
            Loads++;
            return Task.FromResult(snapshot);
        }
    }

    private sealed class FakeTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
