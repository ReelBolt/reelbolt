#nullable enable

using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Npgsql;
using ReelBolt.Inference.Api.Billing;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using ReelBolt.WorkflowEngine.Tests.Infrastructure;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Billing;

/// <summary>
/// T-015b: <see cref="ModelCatalogPriceSeeder"/> loads <c>model-catalog.seed.json</c> into
/// <c>model_capability_prices</c> for platform capabilities only, idempotently, never touching admin-owned rows.
/// </summary>
public sealed class ModelCatalogPriceSeederTests
{
    private static readonly DateTime Now = new(2026, 10, 13, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly SeedDay = new(2026, 10, 12);

    private readonly string _dbName = Guid.NewGuid().ToString();

    // ------------------------------------------------------------------ against the committed seed

    [Fact]
    public async Task Run_PricesMatchingPlatformCapability_AllUnits()
    {
        Guid cap = await AddCapabilityAsync(InferenceProviderKind.Anthropic, "claude-opus-5-5", InferenceProviderCapability.Chat);
        ModelCatalogSeed seed = ModelCatalogSeed.LoadDefault();

        SeedRunReport report = await RunAsync(seed);

        List<SeedPriceRow> expected = seed.Rows.Where(r => r.Kind == InferenceProviderKind.Anthropic
            && r.ModelCode == "claude-opus-5-5" && r.Capability == "Chat").ToList();
        List<ModelCapabilityPrice> prices = await PricesAsync(cap);
        prices.Should().HaveCount(expected.Count);
        foreach (SeedPriceRow row in expected)
        {
            ModelCapabilityPrice price = prices.Single(p => p.Unit == row.Unit && p.Variant == row.Variant);
            price.CostUsdPerUnit.Should().Be(row.CostUsdPerUnit);
            price.CreditsMillisPerUnitOverride.Should().BeNull();
            price.EffectiveFrom.Should().Be(Now);
            price.EffectiveTo.Should().BeNull();
            price.Notes.Should().StartWith($"{ModelCatalogPriceSeeder.NotesPrefix}{seed.CatalogVersion} | {row.Quoted} | {row.SourceUrl} | retrieved 2026-10-12");
        }

        report.Inserted.Should().Be(expected.Count);
        report.Closed.Should().Be(0);
        report.NotInSeed.Should().BeEmpty();
    }

    [Fact]
    public async Task Run_Twice_SecondRunWritesNothing()
    {
        Guid cap = await AddCapabilityAsync(InferenceProviderKind.DeepSeek, "deepseek-flash", InferenceProviderCapability.Chat);
        ModelCatalogSeed seed = ModelCatalogSeed.LoadDefault();
        await RunAsync(seed);
        List<(Guid, DateTime, DateTime?)> before = (await PricesAsync(cap)).Select(p => (p.Id, p.EffectiveFrom, p.EffectiveTo)).ToList();
        before.Should().NotBeEmpty();

        SeedRunReport second = await RunAsync(seed, Now.AddDays(3));

        second.Inserted.Should().Be(0);
        second.Closed.Should().Be(0);
        second.Unchanged.Should().Be(before.Count);
        (await PricesAsync(cap)).Select(p => (p.Id, p.EffectiveFrom, p.EffectiveTo)).Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task Run_NotesAndVersionOnlyChange_WritesNothing_AndTheStoredNotesStay()
    {
        // A reseed that only rewords a quote and bumps catalogVersion (the upscaler notes) must not churn an
        // install: the same cost is "unchanged", so no row is closed or inserted, and the stored notes are kept.
        Guid cap = await AddCapabilityAsync(InferenceProviderKind.DeepSeek, "deepseek-flash", InferenceProviderCapability.Chat);
        SeedPriceRow first = Row(UsageKind.LlmInputTokens, null, 0.2m);
        await RunAsync(Synthetic("2026.10.12.9", first));
        List<ModelCapabilityPrice> before = await PricesAsync(cap);

        SeedRunReport reworded = await RunAsync(
            Synthetic("2026.10.12.10", first with { Quoted = "$x / unit, factor capped at x2" }), Now.AddDays(3));

        reworded.Inserted.Should().Be(0);
        reworded.Closed.Should().Be(0);
        reworded.Unchanged.Should().Be(1);
        List<ModelCapabilityPrice> after = await PricesAsync(cap);
        after.Should().HaveCount(before.Count);
        after.Single().Id.Should().Be(before.Single().Id);
        (after.Single().CostUsdPerUnit, after.Single().EffectiveFrom, after.Single().EffectiveTo)
            .Should().Be((before.Single().CostUsdPerUnit, before.Single().EffectiveFrom, before.Single().EffectiveTo));
        after.Single().Notes.Should().StartWith($"{ModelCatalogPriceSeeder.NotesPrefix}2026.10.12.9 |");
    }

    [Fact]
    public async Task Run_FutureDatedRow_InsertedAndPreviousClosedAtThatDate()
    {
        DateTime switchDate = new(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        ModelCatalogSeed seed = ModelCatalogSeed.LoadDefault();
        SeedPriceRow currentInput = seed.Rows.Single(r => r.Kind == InferenceProviderKind.Gemini && r.ModelCode == "gemini-3.8-flash"
            && r.Capability == "Chat" && r.Unit == UsageKind.LlmInputTokens && r.EffectiveTo != null);
        SeedPriceRow futureInput = seed.Rows.Single(r => r.Kind == InferenceProviderKind.Gemini && r.ModelCode == "gemini-3.8-flash"
            && r.Capability == "Chat" && r.Unit == UsageKind.LlmInputTokens && r.EffectiveTo == null);

        // An earlier run (before the seed learned the 2027 price) left an open seed-owned row at today's price.
        Guid chat = await AddCapabilityAsync(InferenceProviderKind.Gemini, "gemini-3.8-flash", InferenceProviderCapability.Chat);
        DateTime earlier = Now.AddDays(-1);
        Guid previousId = await AddPriceAsync(chat, UsageKind.LlmInputTokens, null, currentInput.CostUsdPerUnit, earlier, null, "seed:old");
        // A fresh capability gets both windows straight away.
        Guid vision = await AddCapabilityAsync(InferenceProviderKind.Gemini, "gemini-3.8-flash", InferenceProviderCapability.Vision, displayName: "Gemini Vision");

        SeedRunReport report = await RunAsync(seed);

        List<ModelCapabilityPrice> chatInput = (await PricesAsync(chat)).Where(p => p.Unit == UsageKind.LlmInputTokens).OrderBy(p => p.EffectiveFrom).ToList();
        chatInput.Should().HaveCount(2);
        chatInput[0].Id.Should().Be(previousId);
        chatInput[0].EffectiveFrom.Should().Be(earlier);
        chatInput[0].EffectiveTo.Should().Be(switchDate);
        chatInput[0].CostUsdPerUnit.Should().Be(currentInput.CostUsdPerUnit);
        chatInput[1].EffectiveFrom.Should().Be(switchDate);
        chatInput[1].EffectiveTo.Should().BeNull();
        chatInput[1].CostUsdPerUnit.Should().Be(futureInput.CostUsdPerUnit);

        List<ModelCapabilityPrice> visionInput = (await PricesAsync(vision)).Where(p => p.Unit == UsageKind.LlmInputTokens).OrderBy(p => p.EffectiveFrom).ToList();
        visionInput.Select(p => (p.EffectiveFrom, p.EffectiveTo, p.CostUsdPerUnit)).Should().Equal(
            (Now, switchDate, currentInput.CostUsdPerUnit),
            (switchDate, null, futureInput.CostUsdPerUnit));

        report.Closed.Should().Be(1);
    }

    [Fact]
    public async Task Run_ByoCapability_IsNeverPriced()
    {
        Guid org = Guid.NewGuid();
        Guid cap = await AddCapabilityAsync(InferenceProviderKind.Anthropic, "claude-opus-5-5", InferenceProviderCapability.Chat, organizationId: org);
        Guid platform = await AddCapabilityAsync(InferenceProviderKind.Anthropic, "claude-opus-5-5", InferenceProviderCapability.Chat, displayName: "Platform Opus");

        SeedRunReport report = await RunAsync(ModelCatalogSeed.LoadDefault());

        (await PricesAsync(cap)).Should().BeEmpty();
        (await PricesAsync(platform)).Should().HaveCount(report.Inserted).And.NotBeEmpty();
        report.NotInSeed.Should().BeEmpty();
    }

    [Fact]
    public async Task Run_ModelCodeDiffersByCase_IsNotMatched()
    {
        Guid cap = await AddCapabilityAsync(InferenceProviderKind.Anthropic, "Claude-Opus-5-5", InferenceProviderCapability.Chat);

        SeedRunReport report = await RunAsync(ModelCatalogSeed.LoadDefault());

        (await PricesAsync(cap)).Should().BeEmpty();
        report.NotInSeed.Should().Equal("Anthropic/Claude-Opus-5-5/Chat");
    }

    [Fact]
    public async Task Run_OracleAndUnpriced_WriteNothing()
    {
        Guid oracle = await AddCapabilityAsync(InferenceProviderKind.Higgsfield, "kling-video/v3.0/pro/text-to-video",
            InferenceProviderCapability.VideoGeneration, priceSource: ModelPriceSource.Oracle);
        Guid unpriced = await AddCapabilityAsync(InferenceProviderKind.MiniMax, "T2V-01", InferenceProviderCapability.VideoGeneration);

        SeedRunReport report = await RunAsync(ModelCatalogSeed.LoadDefault());

        (await PricesAsync(oracle)).Should().BeEmpty();
        (await PricesAsync(unpriced)).Should().BeEmpty();
        report.Inserted.Should().Be(0);
        report.Unpriced.Should().Equal("MiniMax/T2V-01/VideoGeneration");
        report.NotInSeed.Should().BeEmpty();
    }

    [Fact]
    public async Task Run_DryRun_WritesNothing()
    {
        Guid cap = await AddCapabilityAsync(InferenceProviderKind.Anthropic, "claude-opus-5-5", InferenceProviderCapability.Chat);
        await AddPriceAsync(cap, UsageKind.LlmInputTokens, null, 0.5m, Now.AddDays(-5), null, "seed:old");

        SeedRunReport report = await RunAsync(ModelCatalogSeed.LoadDefault(), dryRun: true);

        report.Inserted.Should().BeGreaterThan(1);
        report.Closed.Should().Be(1);
        List<ModelCapabilityPrice> prices = await PricesAsync(cap);
        prices.Should().ContainSingle();
        prices[0].EffectiveTo.Should().BeNull();
    }

    [Fact]
    public async Task Run_StagingLikeInstall_EveryStagingRowPriced()
    {
        // docs/design/model-catalog-research/staging-rows.prd.md: the 19 platform rows on staging.
        (InferenceProviderKind Kind, InferenceProviderCapability Capability, string Model, string Name)[] staging =
        [
            (InferenceProviderKind.Anthropic, InferenceProviderCapability.Chat, "claude-opus-5-5", "Opus 5.5 Chat"),
            (InferenceProviderKind.DeepSeek, InferenceProviderCapability.Chat, "deepseek-flash", "DeepSeek Chat"),
            (InferenceProviderKind.TypeSafe, InferenceProviderCapability.Decision, "jev-latest", "Jev Decision"),
            (InferenceProviderKind.OpenAICompatible, InferenceProviderCapability.Embedding, "@cf/qwen/qwen3-embedding-0.6b", "CloudFlare Qwen Embed"),
            (InferenceProviderKind.FalAi, InferenceProviderCapability.ImageBackgroundReplace, "fal-ai/bria/background/replace", "Fal.AI Segmentation - Replace background"),
            (InferenceProviderKind.FalAi, InferenceProviderCapability.ImageEdit, "google/nano-banana-2.1/edit", "Fal.AI Edit"),
            (InferenceProviderKind.FalAi, InferenceProviderCapability.ImageErase, "fal-ai/object-removal/mask", "Fal.AI Segmentation - Remove objects"),
            (InferenceProviderKind.FalAi, InferenceProviderCapability.ImageExpand, "fal-ai/bria/expand", "Fal.AI Segmentation - Expand"),
            (InferenceProviderKind.FalAi, InferenceProviderCapability.ImageFill, "fal-ai/flux-pro/v1/fill", "Fal.AI Segmentation - Fill an area"),
            (InferenceProviderKind.FalAi, InferenceProviderCapability.ImageGeneration, "google/nano-banana-2.1", "Fal.AI"),
            (InferenceProviderKind.FalAi, InferenceProviderCapability.ImageRelight, "fal-ai/image-apps-v2/relighting", "Fal.AI Segmentation - Relight"),
            (InferenceProviderKind.FalAi, InferenceProviderCapability.ImageUpscale, "fal-ai/seedvr/upscale/image", "Fal.AI - Upscale"),
            (InferenceProviderKind.FalAi, InferenceProviderCapability.Segmentation, "fal-ai/sam-3-1/image", "Fal.AI Segmentation"),
            (InferenceProviderKind.FishAudio, InferenceProviderCapability.SpeechSynthesis, "s2.1-pro", "Fish TTS"),
            (InferenceProviderKind.FishAudio, InferenceProviderCapability.Transcription, "transcribe-1-pro", "Fish ASR"),
            (InferenceProviderKind.MiniMax, InferenceProviderCapability.VideoGeneration, "MiniMax-H3-Max", "MiniMax"),
            (InferenceProviderKind.Anthropic, InferenceProviderCapability.Vision, "claude-haiku-5-5", "Haiku 5.5 Vision"),
            (InferenceProviderKind.Anthropic, InferenceProviderCapability.Vision, "claude-haiku-5-5", "Haiku 5.5 Chat"),
            (InferenceProviderKind.DeepSeek, InferenceProviderCapability.Vision, "deepseek-flash", "DeepSeek Vision"),
        ];
        staging.Should().HaveCount(19);
        List<Guid> caps = [];
        foreach ((InferenceProviderKind kind, InferenceProviderCapability capability, string model, string name) in staging)
        {
            caps.Add(await AddCapabilityAsync(kind, model, capability, displayName: name));
        }

        SeedRunReport report = await RunAsync(ModelCatalogSeed.LoadDefault());

        report.NotInSeed.Should().BeEmpty();
        report.Unpriced.Should().BeEmpty();
        report.SkippedAdminOwned.Should().BeEmpty();
        foreach (Guid cap in caps)
        {
            (await PricesAsync(cap)).Should().NotBeEmpty();
        }
    }

    // ------------------------------------------------------------------ against a synthetic seed

    [Fact]
    public async Task Run_ChangedPrice_ClosesSeedOwnedRowAndOpensNew()
    {
        Guid cap = await AddCapabilityAsync(InferenceProviderKind.DeepSeek, "deepseek-flash", InferenceProviderCapability.Chat);
        DateTime earlier = Now.AddDays(-10);
        Guid oldId = await AddPriceAsync(cap, UsageKind.LlmInputTokens, null, 0.1m, earlier, null, "seed:2026.01.01.1 | old");

        SeedRunReport report = await RunAsync(Synthetic(Row(UsageKind.LlmInputTokens, null, 0.2m)));

        List<ModelCapabilityPrice> prices = (await PricesAsync(cap)).OrderBy(p => p.EffectiveFrom).ToList();
        prices.Should().HaveCount(2);
        prices[0].Id.Should().Be(oldId);
        prices[0].CostUsdPerUnit.Should().Be(0.1m);
        prices[0].EffectiveFrom.Should().Be(earlier);
        prices[0].EffectiveTo.Should().Be(Now);
        prices[1].CostUsdPerUnit.Should().Be(0.2m);
        prices[1].EffectiveFrom.Should().Be(Now);
        prices[1].EffectiveTo.Should().BeNull();
        report.Closed.Should().Be(1);
        report.Inserted.Should().Be(1);
    }

    [Fact]
    public async Task Run_AdminOwnedRow_IsNeverTouched()
    {
        Guid cap = await AddCapabilityAsync(InferenceProviderKind.DeepSeek, "deepseek-flash", InferenceProviderCapability.Chat);
        await AddPriceAsync(cap, UsageKind.LlmInputTokens, null, 0.1m, Now.AddDays(-10), null, "set by an admin");

        SeedRunReport report = await RunAsync(Synthetic(Row(UsageKind.LlmInputTokens, null, 0.2m)));

        List<ModelCapabilityPrice> prices = await PricesAsync(cap);
        prices.Should().ContainSingle();
        prices[0].CostUsdPerUnit.Should().Be(0.1m);
        prices[0].EffectiveTo.Should().BeNull();
        report.SkippedAdminOwned.Should().Equal("DeepSeek/deepseek-flash/Chat/LlmInputTokens/-");
        report.Inserted.Should().Be(0);
        report.Closed.Should().Be(0);
    }

    [Fact]
    public async Task Run_RowWithCreditsOverride_IsNeverTouched()
    {
        Guid cap = await AddCapabilityAsync(InferenceProviderKind.DeepSeek, "deepseek-flash", InferenceProviderCapability.Chat);
        await AddPriceAsync(cap, UsageKind.LlmInputTokens, null, 0.1m, Now.AddDays(-10), null, "seed:2026.01.01.1 | old", creditsOverride: 7m);

        SeedRunReport report = await RunAsync(Synthetic(Row(UsageKind.LlmInputTokens, null, 0.2m)));

        List<ModelCapabilityPrice> prices = await PricesAsync(cap);
        prices.Should().ContainSingle();
        prices[0].EffectiveTo.Should().BeNull();
        report.SkippedAdminOwned.Should().ContainSingle();
    }

    [Theory]
    [InlineData("PLACEHOLDER (indicative, not measured) - derived from D9 and B0's cost model; B13 replaces with provider-measured costs.")]
    [InlineData("PLACEHOLDER (indicative, not measured) - derived from D9 and B0's cost model; B13 replaces with provider-measured costs. Basis: DeepSeek list price.")]
    [InlineData("PLACEHOLDER (published vendor list price, not reconciled against a provider invoice) - Basis: DeepSeek list price.")]
    public async Task Run_PlaceholderPortedRow_IsSuperseded(string notes)
    {
        Guid cap = await AddCapabilityAsync(InferenceProviderKind.DeepSeek, "deepseek-flash", InferenceProviderCapability.Chat);
        Guid ported = await AddPriceAsync(cap, UsageKind.LlmInputTokens, null, Suggested(InferenceProviderKind.DeepSeek,
            InferenceProviderCapability.Chat, "deepseek-flash", UsageKind.LlmInputTokens), Now.AddDays(-1), null, notes);

        SeedRunReport report = await RunAsync(Synthetic(Row(UsageKind.LlmInputTokens, null, 0.2m)));

        List<ModelCapabilityPrice> prices = (await PricesAsync(cap)).OrderBy(p => p.EffectiveFrom).ToList();
        prices.Should().HaveCount(2);
        prices[0].Id.Should().Be(ported);
        prices[0].EffectiveTo.Should().Be(Now);
        prices[1].CostUsdPerUnit.Should().Be(0.2m);
        report.Closed.Should().Be(1);
        report.SkippedAdminOwned.Should().BeEmpty();
    }

    [Fact]
    public async Task Run_AdminNoteMentioningPlaceholderMidString_IsNotTouched()
    {
        Guid cap = await AddCapabilityAsync(InferenceProviderKind.DeepSeek, "deepseek-flash", InferenceProviderCapability.Chat);
        await AddPriceAsync(cap, UsageKind.LlmInputTokens, null, 0.1m, Now.AddDays(-10), null, "admin: replaced the PLACEHOLDER figure");

        SeedRunReport report = await RunAsync(Synthetic(Row(UsageKind.LlmInputTokens, null, 0.2m)));

        List<ModelCapabilityPrice> prices = await PricesAsync(cap);
        prices.Should().ContainSingle();
        prices[0].CostUsdPerUnit.Should().Be(0.1m);
        prices[0].EffectiveTo.Should().BeNull();
        report.SkippedAdminOwned.Should().Equal("DeepSeek/deepseek-flash/Chat/LlmInputTokens/-");
        report.Inserted.Should().Be(0);
        report.Closed.Should().Be(0);
    }

    [Fact]
    public async Task Run_SeedRowRemoved_ReportsOrphanLeavesRow()
    {
        Guid cap = await AddCapabilityAsync(InferenceProviderKind.DeepSeek, "deepseek-flash", InferenceProviderCapability.Chat);
        Guid orphan = await AddPriceAsync(cap, UsageKind.LlmCachedInputTokens, null, 0.05m, Now.AddDays(-10), null, "seed:2026.01.01.1 | old");

        SeedRunReport report = await RunAsync(Synthetic(Row(UsageKind.LlmInputTokens, null, 0.2m)));

        ModelCapabilityPrice kept = (await PricesAsync(cap)).Single(p => p.Id == orphan);
        kept.EffectiveTo.Should().BeNull();
        kept.CostUsdPerUnit.Should().Be(0.05m);
        report.OrphanedSeedRows.Should().Equal("DeepSeek/deepseek-flash/Chat/LlmCachedInputTokens/-");
    }

    [Fact]
    public async Task SeedAsync_ConfigOff_WritesNothing_AndDryRunWritesNothing()
    {
        Guid cap = await AddCapabilityAsync(InferenceProviderKind.Anthropic, "claude-opus-5-5", InferenceProviderCapability.Chat);

        foreach (string mode in new[] { "Off", "DryRun" })
        {
            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["ModelCatalog:SeedPrices"] = mode })
                .Build();
            using InferenceApiDbContext db = CreateContext();
            await ModelCatalogPriceSeeder.SeedAsync(db, configuration, NullLogger.Instance);
        }

        (await PricesAsync(cap)).Should().BeEmpty();

        using (InferenceApiDbContext db = CreateContext())
        {
            await ModelCatalogPriceSeeder.SeedAsync(db, new ConfigurationBuilder().Build(), NullLogger.Instance);
        }

        (await PricesAsync(cap)).Should().NotBeEmpty();
    }

    // ------------------------------------------------------------------ T-023: hardening against the catalog upgrader

    [Fact]
    public async Task Run_VariantDiffersOnlyByCase_SupersedesThePlaceholder()
    {
        // MiniMax's table keys are upper case ("480P"); the seed lists the same series in lower case.
        (string resolution, decimal usd) = PriceBook.VideoResolutionPrices(InferenceProviderKind.MiniMax, "MiniMax-H3-Max").First();
        string seedVariant = resolution.ToLowerInvariant();
        Guid cap = await AddCapabilityAsync(InferenceProviderKind.MiniMax, "MiniMax-H3-Max", InferenceProviderCapability.VideoGeneration);
        Guid placeholder = await AddPriceAsync(cap, UsageKind.VideoGenSeconds, resolution, usd, Now.AddDays(-1), null, PlaceholderNote);

        SeedRunReport report = await RunAsync(SyntheticFor(InferenceProviderKind.MiniMax, "MiniMax-H3-Max", "VideoGeneration",
            [RowOn(InferenceProviderKind.MiniMax, "MiniMax-H3-Max", "VideoGeneration", UsageKind.VideoGenSeconds, seedVariant, 0.2m)]));

        List<ModelCapabilityPrice> prices = (await PricesAsync(cap)).OrderBy(p => p.EffectiveFrom).ToList();
        prices.Should().HaveCount(2);
        prices[0].Id.Should().Be(placeholder);
        prices[0].EffectiveTo.Should().Be(Now);
        prices[1].Variant.Should().Be(seedVariant);
        prices[1].CostUsdPerUnit.Should().Be(0.2m);
        prices[1].EffectiveTo.Should().BeNull();
        report.Closed.Should().Be(1);
        report.PlaceholdersInForce.Should().BeEmpty();
        report.OrphanedSeedRows.Should().BeEmpty();
    }

    [Fact]
    public async Task Run_PlaceholderAtTheSeedsCost_IsSupersededSoTheSourcedRowIsInForce()
    {
        decimal suggestion = Suggested(InferenceProviderKind.DeepSeek, InferenceProviderCapability.Chat, "deepseek-flash", UsageKind.LlmInputTokens);
        Guid cap = await AddCapabilityAsync(InferenceProviderKind.DeepSeek, "deepseek-flash", InferenceProviderCapability.Chat);
        Guid placeholder = await AddPriceAsync(cap, UsageKind.LlmInputTokens, null, suggestion, Now.AddDays(-1), null, PlaceholderNote);

        SeedRunReport report = await RunAsync(Synthetic(Row(UsageKind.LlmInputTokens, null, suggestion)));

        List<ModelCapabilityPrice> prices = (await PricesAsync(cap)).OrderBy(p => p.EffectiveFrom).ToList();
        prices.Should().HaveCount(2);
        prices[0].Id.Should().Be(placeholder);
        prices[0].EffectiveTo.Should().Be(Now);
        prices[1].Notes.Should().StartWith(ModelCatalogPriceSeeder.NotesPrefix);
        prices[1].EffectiveTo.Should().BeNull();
        report.Closed.Should().Be(1);
        report.Unchanged.Should().Be(0);

        SeedRunReport second = await RunAsync(Synthetic(Row(UsageKind.LlmInputTokens, null, suggestion)), Now.AddDays(1));
        (second.Inserted, second.Closed, second.Unchanged).Should().Be((0, 0, 1));
    }

    [Fact]
    public async Task Run_UnpricedEntry_EndDatesSeedOwnedRows_LeavesAdminRows()
    {
        Guid cap = await AddCapabilityAsync(InferenceProviderKind.DeepSeek, "deepseek-flash", InferenceProviderCapability.Chat);
        Guid placeholder = await AddPriceAsync(cap, UsageKind.LlmInputTokens, null, Suggested(InferenceProviderKind.DeepSeek,
            InferenceProviderCapability.Chat, "deepseek-flash", UsageKind.LlmInputTokens), Now.AddDays(-3), null, PlaceholderNote);
        Guid seeded = await AddPriceAsync(cap, UsageKind.LlmOutputTokens, null, 0.4m, Now.AddDays(-3), null, "seed:2026.01.01.1 | old");
        Guid future = await AddPriceAsync(cap, UsageKind.LlmOutputTokens, null, 0.5m, Now.AddDays(5), null, "seed:2026.01.01.1 | later");
        Guid admin = await AddPriceAsync(cap, UsageKind.LlmCachedInputTokens, null, 0.01m, Now.AddDays(-3), null, "set by an admin");

        SeedRunReport report = await RunAsync(Synthetic([], priceSource: "Unpriced"));

        List<ModelCapabilityPrice> prices = await PricesAsync(cap);
        prices.Single(p => p.Id == placeholder).EffectiveTo.Should().Be(Now);
        prices.Single(p => p.Id == seeded).EffectiveTo.Should().Be(Now);
        prices.Should().NotContain(p => p.Id == future, "a row not yet in effect is removed, never given an end before its start");
        prices.Single(p => p.Id == admin).EffectiveTo.Should().BeNull();
        report.Closed.Should().Be(3);
        report.Inserted.Should().Be(0);
        report.Unpriced.Should().Equal("DeepSeek/deepseek-flash/Chat");
        report.SkippedAdminOwned.Should().Equal("DeepSeek/deepseek-flash/Chat/LlmCachedInputTokens/-");

        SeedRunReport second = await RunAsync(Synthetic([], priceSource: "Unpriced"), Now.AddHours(1));
        second.Inserted.Should().Be(0);
        second.Closed.Should().Be(0);
    }

    [Fact]
    public async Task Run_OracleCapability_GetsNoTableRows_AndItsSeedOwnedRowsEnd()
    {
        Guid cap = await AddCapabilityAsync(InferenceProviderKind.DeepSeek, "deepseek-flash", InferenceProviderCapability.Chat,
            priceSource: ModelPriceSource.Oracle);
        Guid placeholder = await AddPriceAsync(cap, UsageKind.LlmInputTokens, null, Suggested(InferenceProviderKind.DeepSeek,
            InferenceProviderCapability.Chat, "deepseek-flash", UsageKind.LlmInputTokens), Now.AddDays(-3), null, PlaceholderNote);
        Guid admin = await AddPriceAsync(cap, UsageKind.LlmOutputTokens, null, 0.4m, Now.AddDays(-3), null, "set by an admin");

        // The seed lists a Table price, but the capability is quoted by the vendor: rows would never be read.
        SeedRunReport report = await RunAsync(Synthetic(Row(UsageKind.LlmInputTokens, null, 0.2m)));

        List<ModelCapabilityPrice> prices = await PricesAsync(cap);
        prices.Should().HaveCount(2);
        prices.Single(p => p.Id == placeholder).EffectiveTo.Should().Be(Now);
        prices.Single(p => p.Id == admin).EffectiveTo.Should().BeNull();
        report.Inserted.Should().Be(0);
        report.Closed.Should().Be(1);
        report.Oracle.Should().Equal("DeepSeek/deepseek-flash/Chat");
        report.SkippedAdminOwned.Should().Equal("DeepSeek/deepseek-flash/Chat/LlmOutputTokens/-");
    }

    [Fact]
    public async Task Run_OracleEntry_HiggsfieldGetsNoRows_AndItsPlaceholderEnds()
    {
        Guid cap = await AddCapabilityAsync(InferenceProviderKind.Higgsfield, "kling-video/v3.0/pro/text-to-video",
            InferenceProviderCapability.VideoGeneration);
        Guid placeholder = await AddPriceAsync(cap, UsageKind.VideoGenSeconds, null, 0.3m, Now.AddDays(-3), null, PlaceholderNote);

        // Higgsfield has no suggestion, so the operator adopts this exact row by its token.
        string token = await AdoptTokenAsync(cap, placeholder, "Higgsfield/kling-video/v3.0/pro/text-to-video/VideoGeneration");
        SeedRunReport report = await RunAsync(ModelCatalogSeed.LoadDefault(),
            adopted: new HashSet<string>(StringComparer.Ordinal) { token });

        List<ModelCapabilityPrice> prices = await PricesAsync(cap);
        prices.Should().ContainSingle();
        prices[0].Id.Should().Be(placeholder);
        prices[0].EffectiveTo.Should().Be(Now);
        report.Oracle.Should().Equal("Higgsfield/kling-video/v3.0/pro/text-to-video/VideoGeneration");
    }

    [Fact]
    public async Task Run_SeriesEndedByTheSeedAsUnpriced_IsRepricedWhenTheSeedPricesItAgain()
    {
        Guid cap = await AddCapabilityAsync(InferenceProviderKind.DeepSeek, "deepseek-flash", InferenceProviderCapability.Chat);
        await AddPriceAsync(cap, UsageKind.LlmInputTokens, null, Suggested(InferenceProviderKind.DeepSeek,
            InferenceProviderCapability.Chat, "deepseek-flash", UsageKind.LlmInputTokens), Now.AddDays(-3), null, PlaceholderNote);
        await RunAsync(Synthetic([], priceSource: "Unpriced"));

        SeedRunReport report = await RunAsync(Synthetic(Row(UsageKind.LlmInputTokens, null, 0.2m)), Now.AddDays(1));

        report.AdminRetired.Should().BeEmpty();
        report.Inserted.Should().Be(1);
        (await PricesAsync(cap)).Should().ContainSingle(p => p.EffectiveTo == null && p.CostUsdPerUnit == 0.2m);
    }

    [Theory]
    [InlineData(0.2)]
    [InlineData(0.3)]
    public async Task Run_AdminRetiredSeries_IsNeverReopened(double seedCost)
    {
        Guid cap = await AddCapabilityAsync(InferenceProviderKind.DeepSeek, "deepseek-flash", InferenceProviderCapability.Chat);
        // A seed row, then RetirePriceAsync ended it two hours ago with no successor.
        Guid retired = await AddPriceAsync(cap, UsageKind.LlmInputTokens, null, 0.2m, Now.AddDays(-10), Now.AddHours(-2),
            "seed:2026.10.12.9 | $x / unit | https://example.test/pricing | retrieved 2026-10-12");

        SeedRunReport report = await RunAsync(Synthetic(Row(UsageKind.LlmInputTokens, null, (decimal)seedCost)));

        List<ModelCapabilityPrice> prices = await PricesAsync(cap);
        prices.Should().ContainSingle();
        prices[0].Id.Should().Be(retired);
        prices[0].EffectiveTo.Should().Be(Now.AddHours(-2));
        report.Inserted.Should().Be(0);
        report.Closed.Should().Be(0);
        report.AdminRetired.Should().Equal("DeepSeek/deepseek-flash/Chat/LlmInputTokens/-");
    }

    [Fact]
    public async Task Run_SeriesEndingOnTheSeedsOwnDate_IsNotAdminRetired()
    {
        Guid cap = await AddCapabilityAsync(InferenceProviderKind.DeepSeek, "deepseek-flash", InferenceProviderCapability.Chat);
        DateOnly end = new(2026, 12, 1);
        ModelCatalogSeed seed = Synthetic(Row(UsageKind.LlmInputTokens, null, 0.2m, to: end));
        await RunAsync(seed);

        SeedRunReport second = await RunAsync(seed, Now.AddDays(1));

        second.AdminRetired.Should().BeEmpty();
        second.Unchanged.Should().Be(1);
        second.Inserted.Should().Be(0);
    }

    [Fact]
    public async Task Run_PlaceholderVariantTheSeedDoesNotList_StaysInForceAndIsReported()
    {
        Guid cap = await AddCapabilityAsync(InferenceProviderKind.DeepSeek, "deepseek-flash", InferenceProviderCapability.Chat);
        Guid uhd = await AddPriceAsync(cap, UsageKind.LlmInputTokens, "4K", 0.9m, Now.AddDays(-1), null, PlaceholderNote);
        string token = await AdoptTokenAsync(cap, uhd, "DeepSeek/deepseek-flash/Chat");

        SeedRunReport report = await RunAsync(Synthetic(Row(UsageKind.LlmInputTokens, null, 0.2m)),
            adopted: new HashSet<string>(StringComparer.Ordinal) { token });

        (await PricesAsync(cap)).Single(p => p.Id == uhd).EffectiveTo.Should().BeNull();
        report.PlaceholdersInForce.Should().Equal("DeepSeek/deepseek-flash/Chat/LlmInputTokens/4K");
        report.OrphanedSeedRows.Should().BeEmpty();
    }

    [Fact]
    public async Task Run_SeedVersionBump_LeavesAdminRowsAlone()
    {
        Guid cap = await AddCapabilityAsync(InferenceProviderKind.DeepSeek, "deepseek-flash", InferenceProviderCapability.Chat);
        Guid admin = await AddPriceAsync(cap, UsageKind.LlmOutputTokens, null, 0.4m, Now.AddDays(-10), null, "set by an admin");
        await RunAsync(Synthetic(Row(UsageKind.LlmInputTokens, null, 0.2m), Row(UsageKind.LlmOutputTokens, null, 0.3m)));

        SeedRunReport bumped = await RunAsync(Synthetic("2026.10.20.1",
            Row(UsageKind.LlmInputTokens, null, 0.25m), Row(UsageKind.LlmOutputTokens, null, 0.35m)), Now.AddDays(7));

        List<ModelCapabilityPrice> prices = await PricesAsync(cap);
        ModelCapabilityPrice kept = prices.Single(p => p.Id == admin);
        (kept.CostUsdPerUnit, kept.EffectiveTo, kept.Notes).Should().Be((0.4m, (DateTime?)null, "set by an admin"));
        prices.Where(p => p.Unit == UsageKind.LlmOutputTokens).Should().ContainSingle();
        prices.Single(p => p.Unit == UsageKind.LlmInputTokens && p.EffectiveTo == null).CostUsdPerUnit.Should().Be(0.25m);
        bumped.SkippedAdminOwned.Should().Equal("DeepSeek/deepseek-flash/Chat/LlmOutputTokens/-");
        bumped.Closed.Should().Be(1);
        bumped.Inserted.Should().Be(1);
    }

    [Fact]
    public void AdvisoryLockKey_IsItsOwnFixedKey()
    {
        ModelCatalogPriceSeeder.AdvisoryLockKey.Should().Be(0x4D444C5345454450); // ASCII "MDLSEEDP"
        ModelCatalogPriceSeeder.AdvisoryLockKey.Should().NotBe(ModelCatalogUpgrader.AdvisoryLockKey);
    }

    [Fact]
    public async Task Run_WithWrites_InvalidatesThePriceAndResolverCaches_AndANoOpDoesNot()
    {
        await AddCapabilityAsync(InferenceProviderKind.DeepSeek, "deepseek-flash", InferenceProviderCapability.Chat);
        Mock<IModelPriceService> prices = new();
        Mock<IInferenceProviderResolver> resolver = new();
        ModelCatalogSeed seed = Synthetic(Row(UsageKind.LlmInputTokens, null, 0.2m));

        using (InferenceApiDbContext db = CreateContext())
        {
            await ModelCatalogPriceSeeder.RunAsync(db, seed, Now, dryRun: false, NullLogger.Instance, prices.Object, resolver.Object);
        }

        prices.Verify(p => p.Invalidate(), Times.Once);
        resolver.Verify(r => r.InvalidateAll(), Times.Once);

        using (InferenceApiDbContext db = CreateContext())
        {
            await ModelCatalogPriceSeeder.RunAsync(db, seed, Now.AddDays(1), dryRun: false, NullLogger.Instance, prices.Object, resolver.Object);
        }

        prices.Verify(p => p.Invalidate(), Times.Once);
        resolver.Verify(r => r.InvalidateAll(), Times.Once);
    }

    // ------------------------------------------------------------------ SEC-6: config wiring, default Apply convergence

    [Fact]
    public async Task SeedAsync_DefaultApply_ConvergesAndLeavesUnverifiedRowsAlone()
    {
        decimal suggestion = Suggested(InferenceProviderKind.DeepSeek, InferenceProviderCapability.Chat, "deepseek-flash", UsageKind.LlmInputTokens);
        Guid cap = await AddCapabilityAsync(InferenceProviderKind.DeepSeek, "deepseek-flash", InferenceProviderCapability.Chat);
        Guid unverified = await AddPriceAsync(cap, UsageKind.LlmInputTokens, null, suggestion + 1m, Now.AddDays(-1), null, PlaceholderNote);

        // No ModelCatalog keys at all: the default is Apply, with no adopt tokens.
        using (InferenceApiDbContext db = CreateContext())
        {
            await ModelCatalogPriceSeeder.SeedAsync(db, new ConfigurationBuilder().Build(), NullLogger.Instance);
        }
        List<(Guid Id, DateTime? To, decimal Cost, string? Notes)> afterFirst = await SnapshotAsync(cap);

        using (InferenceApiDbContext db = CreateContext())
        {
            await ModelCatalogPriceSeeder.SeedAsync(db, new ConfigurationBuilder().Build(), NullLogger.Instance);
        }

        (await SnapshotAsync(cap)).Should().BeEquivalentTo(afterFirst, "a second Apply writes nothing");
        afterFirst.Should().Contain(r => r.Id == unverified && r.To == null && r.Cost == suggestion + 1m);
    }

    [Fact]
    public async Task SeedAsync_ReadsTheAdoptAndLogLimitKeys()
    {
        decimal suggestion = Suggested(InferenceProviderKind.DeepSeek, InferenceProviderCapability.Chat, "deepseek-flash", UsageKind.LlmInputTokens);
        Guid cap = await AddCapabilityAsync(InferenceProviderKind.DeepSeek, "deepseek-flash", InferenceProviderCapability.Chat);
        Guid unverified = await AddPriceAsync(cap, UsageKind.LlmInputTokens, null, suggestion + 1m, Now.AddDays(-1), null, PlaceholderNote);
        string token = await AdoptTokenAsync(cap, unverified, "DeepSeek/deepseek-flash/Chat");
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ModelCatalog:SeedPricesAdopt"] = $" ; {token} ;",
            ["ModelCatalog:SeedPricesLogLimit"] = "1",
        }).Build();
        CapturingLogger logger = new();

        using (InferenceApiDbContext db = CreateContext())
        {
            await ModelCatalogPriceSeeder.SeedAsync(db, configuration, logger);
        }

        (await PricesAsync(cap)).Should().Contain(p => p.Id == unverified && p.EffectiveTo != null, "the adopted token closes exactly that row");
        logger.Messages.Count(m => m.Contains(" reason=")).Should().Be(1);
        logger.Messages.Should().ContainSingle(m => m.Contains("more series action(s) not logged"));
    }

    // ------------------------------------------------------------------ SEC-6: cost-verified placeholder ownership

    // ------------------------------------------------------------------ T-035: self-hosted local defaults priced at zero

    /// <summary>The five self-hosted local defaults priced at 0 on 2026-10-17, keyed as the seed keys them.</summary>
    public static TheoryData<InferenceProviderKind, string, InferenceProviderCapability> SelfHostedZeroDefaults => new()
    {
        { InferenceProviderKind.OpenAICompatible, "speaches-ai/Kokoro-82M-v1.0-ONNX", InferenceProviderCapability.SpeechSynthesis },
        { InferenceProviderKind.OpenAICompatible, "Systran/faster-whisper-small", InferenceProviderCapability.Transcription },
        { InferenceProviderKind.OpenAICompatible, "Systran/faster-whisper-large-v3", InferenceProviderCapability.Transcription },
        { InferenceProviderKind.OpenAICompatible, "Qwen/Qwen3-Embedding-0.6B", InferenceProviderCapability.Embedding },
        { InferenceProviderKind.OpenJev, "jev-latest", InferenceProviderCapability.Decision },
    };

    /// <summary>Noon UTC on the day the zero rows take effect: after their effective date, so they are in force.</summary>
    private static readonly DateTime ZeroDefaultAt = new(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [MemberData(nameof(SelfHostedZeroDefaults))]
    public void SelfHostedZeroDefault_IsATableEntryWithAZeroRowPerMeteredUnit(
        InferenceProviderKind kind, string modelCode, InferenceProviderCapability capability)
    {
        ModelCatalogSeed seed = ModelCatalogSeed.LoadDefault();

        SeedCapabilityEntry entry = seed.Capabilities.Single(c => c.Kind == kind && c.ModelCode == modelCode && c.Capability == capability.ToString());
        entry.PriceSource.Should().Be("Table");
        entry.UnpricedReason.Should().BeNull();

        List<SeedPriceRow> rows = seed.Rows.Where(r => r.Kind == kind && r.ModelCode == modelCode && r.Capability == capability.ToString()).ToList();
        rows.Select(r => r.Unit).Should().BeEquivalentTo(ProviderCapabilityUsage.ByCapability[capability]);
        foreach (SeedPriceRow row in rows)
        {
            row.Variant.Should().BeNull();
            row.CostUsdPerUnit.Should().Be(0m);
            row.EffectiveFrom.Should().Be(new DateOnly(2026, 10, 12));
            row.EffectiveTo.Should().BeNull();
            row.Notes.Should().Contain("priced at 0 by product decision only while its endpoint is local");
        }
    }

    [Theory]
    [MemberData(nameof(SelfHostedZeroDefaults))]
    public async Task SelfHostedZeroDefault_IsFullyPricedOnAPlatformCapabilityAfterTheSeed(
        InferenceProviderKind kind, string modelCode, InferenceProviderCapability capability)
    {
        Guid cap = await AddCapabilityAsync(kind, modelCode, capability, endpoint: LocalEndpoint);

        await RunAsync(ModelCatalogSeed.LoadDefault(), ZeroDefaultAt);

        List<ModelCapabilityPrice> inForce = (await PricesAsync(cap))
            .Where(p => p.EffectiveFrom <= ZeroDefaultAt && (p.EffectiveTo == null || p.EffectiveTo > ZeroDefaultAt)).ToList();
        ModelCapabilityView.IsFullyPriced(capability, inForce.Select(p => p.Unit).ToList()).Should().BeTrue();
        inForce.Should().NotBeEmpty().And.OnlyContain(p => p.CostUsdPerUnit == 0m);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SelfHostedZeroDefault_AdminRowOnTheSeededUnit_SurvivesTheSeedRun(bool creditsOverride)
    {
        Guid cap = await AddCapabilityAsync(InferenceProviderKind.OpenAICompatible, "Qwen/Qwen3-Embedding-0.6B", InferenceProviderCapability.Embedding, endpoint: LocalEndpoint);
        Guid admin = await AddPriceAsync(cap, UsageKind.EmbeddingTokens, null, 0.00000002m, Now.AddDays(-3), null, "set by an admin",
            creditsOverride: creditsOverride ? 5m : null);

        SeedRunReport report = await RunAsync(ModelCatalogSeed.LoadDefault(), ZeroDefaultAt);

        ModelCapabilityPrice row = (await PricesAsync(cap)).Single(p => p.Id == admin);
        row.EffectiveTo.Should().BeNull();
        row.CostUsdPerUnit.Should().Be(0.00000002m);
        row.Notes.Should().Be("set by an admin");
        report.SkippedAdminOwned.Should().Contain($"{InferenceProviderKind.OpenAICompatible}/Qwen/Qwen3-Embedding-0.6B/Embedding/EmbeddingTokens/-");
        // The seed now prices this unit (a zero row the admin row supersedes), so the entry is no longer reported Unpriced.
        report.Unpriced.Should().NotContain($"{InferenceProviderKind.OpenAICompatible}/Qwen/Qwen3-Embedding-0.6B/Embedding");
    }

    [Fact]
    public async Task SelfHostedZeroDefault_UsageRecordsZeroCredits_WithoutUnpricedUsageException()
    {
        Guid cap = await AddCapabilityAsync(InferenceProviderKind.OpenAICompatible, "Systran/faster-whisper-small", InferenceProviderCapability.Transcription, endpoint: LocalEndpoint);
        await RunAsync(ModelCatalogSeed.LoadDefault(), ZeroDefaultAt);
        ModelPriceSnapshot snapshot = new([await PricedCapabilityAsync(cap)], await PricesAsync(cap), []);
        PriceRequest request = new(cap, null, InferenceProviderKind.OpenAICompatible, InferenceProviderCapability.Transcription,
            "Systran/faster-whisper-small", UsageKind.AsrAudioSeconds, null, UsageBillingMode.Platform, 600m, ZeroDefaultAt);

        PriceQuote quote = ModelPriceService.Price(snapshot, request, ZeroDefaultAt);

        quote.CreditsMillis.Should().Be(0);
        quote.CostUsd.Should().Be(0m);
        quote.PriceId.Should().NotBeNull();
    }

    // ------------------------------------------------------------------ T-037: a self-hosted zero only on a local endpoint

    private const string LocalEndpoint = "http://whisper:8000/v1";
    private const string HostedEndpoint = "https://93.184.216.34/v1";
    private const string DeepInfraEndpoint = "https://api.deepinfra.com/v1/openai";

    /// <summary>The six self-hosted-only entries: the five defaults plus OpenAICompatible/local/Chat.</summary>
    public static TheoryData<InferenceProviderKind, string, InferenceProviderCapability> SelfHostedOnlyEntries => new()
    {
        { InferenceProviderKind.OpenAICompatible, "speaches-ai/Kokoro-82M-v1.0-ONNX", InferenceProviderCapability.SpeechSynthesis },
        { InferenceProviderKind.OpenAICompatible, "Systran/faster-whisper-small", InferenceProviderCapability.Transcription },
        { InferenceProviderKind.OpenAICompatible, "Systran/faster-whisper-large-v3", InferenceProviderCapability.Transcription },
        { InferenceProviderKind.OpenAICompatible, "Qwen/Qwen3-Embedding-0.6B", InferenceProviderCapability.Embedding },
        { InferenceProviderKind.OpenJev, "jev-latest", InferenceProviderCapability.Decision },
        { InferenceProviderKind.OpenAICompatible, "local", InferenceProviderCapability.Chat },
    };

    [Theory]
    [MemberData(nameof(SelfHostedOnlyEntries))]
    public async Task SelfHostedOnly_LocalEndpoint_GetsTheZeroAndIsFullyPriced(
        InferenceProviderKind kind, string modelCode, InferenceProviderCapability capability)
    {
        Guid cap = await AddCapabilityAsync(kind, modelCode, capability, endpoint: LocalEndpoint);

        SeedRunReport report = await RunAsync(ModelCatalogSeed.LoadDefault(), ZeroDefaultAt);

        List<ModelCapabilityPrice> inForce = InForceAt(await PricesAsync(cap), ZeroDefaultAt);
        ModelCapabilityView.IsFullyPriced(capability, inForce.Select(p => p.Unit).ToList()).Should().BeTrue();
        inForce.Should().NotBeEmpty().And.OnlyContain(p => p.CostUsdPerUnit == 0m);
        report.Unpriced.Should().NotContain($"{kind}/{modelCode}/{capability}");
    }

    [Theory]
    [MemberData(nameof(SelfHostedOnlyEntries))]
    public async Task SelfHostedOnly_HostedEndpoint_GetsNoZeroAndStaysUnpriced(
        InferenceProviderKind kind, string modelCode, InferenceProviderCapability capability)
    {
        Guid cap = await AddCapabilityAsync(kind, modelCode, capability, endpoint: HostedEndpoint);

        SeedRunReport report = await RunAsync(ModelCatalogSeed.LoadDefault(), ZeroDefaultAt);

        (await PricesAsync(cap)).Should().BeEmpty();
        ModelCapabilityView.IsFullyPriced(capability, new List<UsageKind>()).Should().BeFalse();
        report.Inserted.Should().Be(0);
        report.Unpriced.Should().Contain($"{kind}/{modelCode}/{capability}");
        report.Actions.Should().Contain(a => a.Action == "skip" && a.Reason == ModelCatalogPriceSeeder.SelfHostedOnlyNotLocal
            && a.Kind == kind.ToString() && a.ModelCode == modelCode);
    }

    /// <summary>
    /// SEC-19: an endpoint whose host cannot be normalised (Uri.IdnHost throws) is hosted. The boot seed must not crash on it,
    /// the self-hosted default stays unpriced, and every other capability in the same run is still priced.
    /// </summary>
    [Theory]
    [InlineData("http://api﹒openai﹒com/v1")]
    [InlineData("http://api․openai․com/v1")]
    [InlineData("http://⒈example.com/v1")]
    public async Task SelfHostedOnly_EndpointWhoseHostCannotBeNormalised_IsHostedAndTheRunStillPricesTheOthers(string endpoint)
    {
        Guid hosted = await AddCapabilityAsync(InferenceProviderKind.OpenAICompatible, "Systran/faster-whisper-small",
            InferenceProviderCapability.Transcription, endpoint: endpoint);
        Guid other = await AddCapabilityAsync(InferenceProviderKind.Anthropic, "claude-opus-5-5", InferenceProviderCapability.Chat);

        SeedRunReport report = await RunAsync(ModelCatalogSeed.LoadDefault(), ZeroDefaultAt);

        (await PricesAsync(hosted)).Should().BeEmpty();
        ModelCapabilityView.IsFullyPriced(InferenceProviderCapability.Transcription, new List<UsageKind>()).Should().BeFalse();
        report.Unpriced.Should().Contain($"{InferenceProviderKind.OpenAICompatible}/Systran/faster-whisper-small/Transcription");
        report.Actions.Should().Contain(a => a.Action == "skip" && a.Reason == ModelCatalogPriceSeeder.SelfHostedOnlyNotLocal
            && a.ModelCode == "Systran/faster-whisper-small");

        List<ModelCapabilityPrice> otherPrices = InForceAt(await PricesAsync(other), ZeroDefaultAt);
        otherPrices.Should().NotBeEmpty();
        ModelCapabilityView.IsFullyPriced(InferenceProviderCapability.Chat, otherPrices.Select(p => p.Unit).ToList()).Should().BeTrue();
        report.Inserted.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task SelfHostedOnly_ExistingZeroRow_ClosesWhenHostedAndReopensWhenLocalAgain()
    {
        ModelCatalogSeed seed = ModelCatalogSeed.LoadDefault();
        Guid cap = await AddCapabilityAsync(InferenceProviderKind.OpenAICompatible, "Systran/faster-whisper-small",
            InferenceProviderCapability.Transcription, endpoint: LocalEndpoint);
        await RunAsync(seed, ZeroDefaultAt);

        await SetEndpointAsync(cap, HostedEndpoint);
        DateTime hostedAt = ZeroDefaultAt.AddDays(1);
        SeedRunReport hosted = await RunAsync(seed, hostedAt);

        List<ModelCapabilityPrice> afterHosted = await PricesAsync(cap);
        afterHosted.Should().ContainSingle();
        afterHosted[0].CostUsdPerUnit.Should().Be(0m);
        afterHosted[0].EffectiveTo.Should().Be(hostedAt);
        afterHosted[0].Notes.Should().Contain(ModelCatalogPriceSeeder.EndedMarker);
        hosted.Closed.Should().Be(1);
        InForceAt(afterHosted, hostedAt).Should().BeEmpty();

        await SetEndpointAsync(cap, LocalEndpoint);
        DateTime localAgainAt = ZeroDefaultAt.AddDays(2);
        SeedRunReport local = await RunAsync(seed, localAgainAt);

        List<ModelCapabilityPrice> inForce = InForceAt(await PricesAsync(cap), localAgainAt);
        ModelCapabilityView.IsFullyPriced(InferenceProviderCapability.Transcription, inForce.Select(p => p.Unit).ToList()).Should().BeTrue();
        inForce.Should().NotBeEmpty().And.OnlyContain(p => p.CostUsdPerUnit == 0m);
        local.Inserted.Should().Be(1);
    }

    [Fact]
    public async Task SelfHostedOnly_AdminPriceOnAHostedCopy_IsSkippedAndStaysInForce()
    {
        Guid cap = await AddCapabilityAsync(InferenceProviderKind.OpenAICompatible, "Systran/faster-whisper-small",
            InferenceProviderCapability.Transcription, endpoint: HostedEndpoint);
        Guid admin = await AddPriceAsync(cap, UsageKind.AsrAudioSeconds, null, 0.00001m, Now.AddDays(-3), null, "set by an admin");

        SeedRunReport report = await RunAsync(ModelCatalogSeed.LoadDefault(), ZeroDefaultAt);

        ModelCapabilityPrice row = (await PricesAsync(cap)).Single();
        row.Id.Should().Be(admin);
        row.EffectiveTo.Should().BeNull();
        row.CostUsdPerUnit.Should().Be(0.00001m);
        report.SkippedAdminOwned.Should().Contain($"{InferenceProviderKind.OpenAICompatible}/Systran/faster-whisper-small/Transcription/AsrAudioSeconds/-");
        report.Unpriced.Should().Contain($"{InferenceProviderKind.OpenAICompatible}/Systran/faster-whisper-small/Transcription");
    }

    [Fact]
    public async Task SelfHostedOnly_PlaceholderOnAHostedCopy_IsNotClosedInFavourOfAZero()
    {
        // The price book has no suggestion for this model, so the PLACEHOLDER is one the seeder cannot verify: it is left alone.
        Guid cap = await AddCapabilityAsync(InferenceProviderKind.OpenAICompatible, "Qwen/Qwen3-Embedding-0.6B",
            InferenceProviderCapability.Embedding, endpoint: HostedEndpoint);
        Guid placeholder = await AddPriceAsync(cap, UsageKind.EmbeddingTokens, null, 0.00000003m, Now.AddDays(-3), null, PlaceholderNote);

        SeedRunReport report = await RunAsync(ModelCatalogSeed.LoadDefault(), ZeroDefaultAt);

        List<ModelCapabilityPrice> prices = await PricesAsync(cap);
        prices.Should().ContainSingle().Which.Id.Should().Be(placeholder);
        prices[0].EffectiveTo.Should().BeNull();
        prices[0].CostUsdPerUnit.Should().Be(0.00000003m);
        report.Inserted.Should().Be(0);
        report.UnverifiedPlaceholders.Should().Contain($"{InferenceProviderKind.OpenAICompatible}/Qwen/Qwen3-Embedding-0.6B/Embedding/EmbeddingTokens/-");
    }

    [Fact]
    public async Task SelfHostedOnly_HostedCopyWithASystemOwnedPlaceholder_IsClosedAndStaysUnpriced()
    {
        // The price book has no figure for this model code, so the PLACEHOLDER is system-owned only by operator adoption:
        // the same classification as a row that sits at the book figure (the seeder's SystemPlaceholder).
        Guid cap = await AddCapabilityAsync(InferenceProviderKind.OpenAICompatible, "Qwen/Qwen3-Embedding-0.6B",
            InferenceProviderCapability.Embedding, endpoint: DeepInfraEndpoint);
        Guid placeholder = await AddPriceAsync(cap, UsageKind.EmbeddingTokens, null, 0.00000003m, Now.AddDays(-3), null, PlaceholderNote);
        ModelCatalogSeed seed = ModelCatalogSeed.LoadDefault();
        SeedRunReport dry = await RunAsync(seed, ZeroDefaultAt, dryRun: true);
        string token = dry.Actions.Single(a => a.AdoptToken is not null).AdoptToken!;

        SeedRunReport report = await RunAsync(seed, ZeroDefaultAt, adopted: new HashSet<string>(StringComparer.Ordinal) { token });

        List<ModelCapabilityPrice> prices = await PricesAsync(cap);
        prices.Should().ContainSingle().Which.Id.Should().Be(placeholder);
        prices[0].EffectiveTo.Should().Be(ZeroDefaultAt, "the seeder ends the placeholder at the run instant");
        prices[0].Notes.Should().Contain(ModelCatalogPriceSeeder.EndedMarker);
        prices.Should().NotContain(p => p.CostUsdPerUnit == 0m, "no zero row is opened in its place");
        InForceAt(prices, ZeroDefaultAt).Should().BeEmpty();
        ModelCapabilityView.IsFullyPriced(InferenceProviderCapability.Embedding, new List<UsageKind>()).Should().BeFalse();
        report.Inserted.Should().Be(0);
        report.Closed.Should().Be(1);
        report.Unpriced.Should().Contain($"{InferenceProviderKind.OpenAICompatible}/Qwen/Qwen3-Embedding-0.6B/Embedding");
        report.Actions.Should().Contain(a => a.Action == "close" && a.Reason == ModelCatalogPriceSeeder.SelfHostedOnlyNotLocal
            && a.ModelCode == "Qwen/Qwen3-Embedding-0.6B" && a.Unit == nameof(UsageKind.EmbeddingTokens));
        report.Actions.Should().Contain(a => a.Action == "skip" && a.Reason == ModelCatalogPriceSeeder.SelfHostedOnlyNotLocal
            && a.ModelCode == "Qwen/Qwen3-Embedding-0.6B");
    }

    [Fact]
    public async Task SelfHostedOnly_UnflaggedEntryWithAnEmptyEndpoint_IsStillPriced()
    {
        Guid cap = await AddCapabilityAsync(InferenceProviderKind.OpenAICompatible, "text-embedding-3-small", InferenceProviderCapability.Embedding);

        SeedRunReport report = await RunAsync(ModelCatalogSeed.LoadDefault(), ZeroDefaultAt);

        List<ModelCapabilityPrice> inForce = InForceAt(await PricesAsync(cap), ZeroDefaultAt);
        ModelCapabilityView.IsFullyPriced(InferenceProviderCapability.Embedding, inForce.Select(p => p.Unit).ToList()).Should().BeTrue();
        inForce.Should().OnlyContain(p => p.CostUsdPerUnit > 0m);
        report.Unpriced.Should().NotContain($"{InferenceProviderKind.OpenAICompatible}/text-embedding-3-small/Embedding");
    }

    private static List<ModelCapabilityPrice> InForceAt(List<ModelCapabilityPrice> prices, DateTime at) =>
        prices.Where(p => p.EffectiveFrom <= at && (p.EffectiveTo == null || p.EffectiveTo > at)).ToList();

    private async Task SetEndpointAsync(Guid capabilityId, string endpoint)
    {
        using InferenceApiDbContext db = CreateContext();
        ModelCapability capability = await db.ModelCapabilities.IgnoreQueryFilters()
            .Include(c => c.Model).ThenInclude(m => m.Provider)
            .SingleAsync(c => c.Id == capabilityId);
        capability.Model.Provider.Endpoint = endpoint;
        await db.SaveChangesAsync();
    }

    private const string PortedCardNote =
        "PLACEHOLDER (indicative, not measured) - derived from D9 and B0's cost model; B13 replaces with provider-measured costs.";

    [Fact]
    public async Task Run_AdminFormRowWithEditedCost_KeepingPriceBookNote_IsNeverTouched()
    {
        decimal suggestion = Suggested(InferenceProviderKind.DeepSeek, InferenceProviderCapability.Chat, "deepseek-flash", UsageKind.LlmInputTokens);
        Guid cap = await AddCapabilityAsync(InferenceProviderKind.DeepSeek, "deepseek-flash", InferenceProviderCapability.Chat);
        Guid form = await AddPriceAsync(cap, UsageKind.LlmInputTokens, null, suggestion * 2, Now.AddDays(-1), null, PriceBook.PublishedNote("x"));

        SeedRunReport report = await RunAsync(Synthetic(Row(UsageKind.LlmInputTokens, null, 0.2m)));

        await AssertLeftAloneAsUnverifiedAsync(cap, form, suggestion * 2, report);
    }

    [Fact]
    public async Task Run_AdminFormRowAtSuggestedCost_IsSuperseded()
    {
        decimal suggestion = Suggested(InferenceProviderKind.DeepSeek, InferenceProviderCapability.Chat, "deepseek-flash", UsageKind.LlmInputTokens);
        Guid cap = await AddCapabilityAsync(InferenceProviderKind.DeepSeek, "deepseek-flash", InferenceProviderCapability.Chat);
        Guid form = await AddPriceAsync(cap, UsageKind.LlmInputTokens, null, suggestion, Now.AddDays(-1), null, PriceBook.PublishedNote("x"));

        SeedRunReport report = await RunAsync(Synthetic(Row(UsageKind.LlmInputTokens, null, 0.2m)));

        List<ModelCapabilityPrice> prices = (await PricesAsync(cap)).OrderBy(p => p.EffectiveFrom).ToList();
        prices.Should().HaveCount(2);
        prices[0].Id.Should().Be(form);
        prices[0].EffectiveTo.Should().Be(Now);
        prices[1].Notes.Should().StartWith(ModelCatalogPriceSeeder.NotesPrefix);
        prices[1].EffectiveTo.Should().BeNull();
        report.UnverifiedPlaceholders.Should().BeEmpty();
    }

    [Fact]
    public async Task Run_PortedAdminEditedCard_Survives()
    {
        decimal suggestion = Suggested(InferenceProviderKind.DeepSeek, InferenceProviderCapability.Chat, "deepseek-flash", UsageKind.LlmInputTokens);
        Guid cap = await AddCapabilityAsync(InferenceProviderKind.DeepSeek, "deepseek-flash", InferenceProviderCapability.Chat);
        Guid card = await AddPriceAsync(cap, UsageKind.LlmInputTokens, null, suggestion * 2, Now.AddDays(-1), null, PortedCardNote);

        SeedRunReport report = await RunAsync(Synthetic(Row(UsageKind.LlmInputTokens, null, 0.2m)));

        await AssertLeftAloneAsUnverifiedAsync(cap, card, suggestion * 2, report);
    }

    [Fact]
    public async Task Run_PortedUntouchedCard_IsSuperseded()
    {
        decimal suggestion = Suggested(InferenceProviderKind.DeepSeek, InferenceProviderCapability.Chat, "deepseek-flash", UsageKind.LlmInputTokens);
        Guid cap = await AddCapabilityAsync(InferenceProviderKind.DeepSeek, "deepseek-flash", InferenceProviderCapability.Chat);
        Guid card = await AddPriceAsync(cap, UsageKind.LlmInputTokens, null, suggestion, Now.AddDays(-1), null, PortedCardNote);

        SeedRunReport report = await RunAsync(Synthetic(Row(UsageKind.LlmInputTokens, null, 0.2m)));

        List<ModelCapabilityPrice> prices = (await PricesAsync(cap)).OrderBy(p => p.EffectiveFrom).ToList();
        prices[0].Id.Should().Be(card);
        prices[0].EffectiveTo.Should().Be(Now);
        report.Closed.Should().Be(1);
    }

    [Fact]
    public async Task Run_ComputeDefaults_AreSuperseded()
    {
        IReadOnlyList<SuggestedPrice> defaults = PricingDefaults.ComputePrices();
        Guid cap = await AddCapabilityAsync(InferenceProviderKind.OpenAICompatible, "compute", InferenceProviderCapability.Compute);
        List<Guid> placeholders = [];
        foreach (SuggestedPrice s in defaults)
        {
            placeholders.Add(await AddPriceAsync(cap, s.Unit, s.Variant, s.CostUsdPerUnit, Now.AddDays(-1), null, s.Notes));
        }

        ModelCatalogSeed seed = SyntheticFor(InferenceProviderKind.OpenAICompatible, "compute", "Compute",
            [.. defaults.Select(s => RowOn(InferenceProviderKind.OpenAICompatible, "compute", "Compute", s.Unit, s.Variant, s.CostUsdPerUnit))]);
        SeedRunReport report = await RunAsync(seed);

        List<ModelCapabilityPrice> prices = await PricesAsync(cap);
        prices.Where(p => placeholders.Contains(p.Id)).Should().HaveCount(defaults.Count)
            .And.OnlyContain(p => p.EffectiveTo == Now);
        prices.Where(p => p.EffectiveTo == null).Should().HaveCount(defaults.Count)
            .And.OnlyContain(p => p.Notes!.StartsWith(ModelCatalogPriceSeeder.NotesPrefix));
        report.Closed.Should().Be(defaults.Count);
        report.Inserted.Should().Be(defaults.Count);
        report.UnverifiedPlaceholders.Should().BeEmpty();
    }

    [Fact]
    public async Task Run_AdoptedUnverifiedPlaceholder_IsSuperseded()
    {
        decimal suggestion = Suggested(InferenceProviderKind.DeepSeek, InferenceProviderCapability.Chat, "deepseek-flash", UsageKind.LlmInputTokens);
        Guid cap = await AddCapabilityAsync(InferenceProviderKind.DeepSeek, "deepseek-flash", InferenceProviderCapability.Chat);
        Guid placeholder = await AddPriceAsync(cap, UsageKind.LlmInputTokens, null, suggestion + 1m, Now.AddDays(-1), null, PlaceholderNote);
        ModelCatalogSeed seed = Synthetic(Row(UsageKind.LlmInputTokens, null, 0.2m));

        // The runbook order: DryRun first, then paste the token it lists into ModelCatalog:SeedPricesAdopt.
        SeedRunReport dry = await RunAsync(seed, dryRun: true);
        string token = dry.Actions.Single(a => a.Reason == "placeholder-cost-unverified").AdoptToken!;
        token.Should().Be(await AdoptTokenAsync(cap, placeholder, "DeepSeek/deepseek-flash/Chat"),
            "the dry run lists the token the operator pastes, and it binds to this row");

        SeedRunReport report = await RunAsync(seed, adopted: new HashSet<string>(StringComparer.Ordinal) { token });

        List<ModelCapabilityPrice> prices = (await PricesAsync(cap)).OrderBy(p => p.EffectiveFrom).ToList();
        prices.Should().HaveCount(2);
        prices[0].Id.Should().Be(placeholder);
        prices[0].EffectiveTo.Should().Be(Now);
        prices[1].CostUsdPerUnit.Should().Be(0.2m);
        report.Closed.Should().Be(1);
        report.UnverifiedPlaceholders.Should().BeEmpty();
    }

    [Fact]
    public async Task Run_AdoptTokenWithStaleCost_DoesNothing()
    {
        decimal suggestion = Suggested(InferenceProviderKind.DeepSeek, InferenceProviderCapability.Chat, "deepseek-flash", UsageKind.LlmInputTokens);
        Guid cap = await AddCapabilityAsync(InferenceProviderKind.DeepSeek, "deepseek-flash", InferenceProviderCapability.Chat);
        Guid placeholder = await AddPriceAsync(cap, UsageKind.LlmInputTokens, null, suggestion + 1m, Now.AddDays(-1), null, PlaceholderNote);

        // Copied when the row cost suggestion + 0.5; the row has since moved to suggestion + 1.
        string stale = await AdoptTokenAsync(cap, placeholder, "DeepSeek/deepseek-flash/Chat", cost: suggestion + 0.5m);
        SeedRunReport report = await RunAsync(Synthetic(Row(UsageKind.LlmInputTokens, null, 0.2m)),
            adopted: new HashSet<string>(StringComparer.Ordinal) { stale });

        List<ModelCapabilityPrice> prices = await PricesAsync(cap);
        prices.Should().ContainSingle();
        prices[0].Id.Should().Be(placeholder);
        prices[0].EffectiveTo.Should().BeNull();
        prices[0].CostUsdPerUnit.Should().Be(suggestion + 1m);
        report.UnverifiedPlaceholders.Should().Equal("DeepSeek/deepseek-flash/Chat/LlmInputTokens/-");
        report.Inserted.Should().Be(0);
        report.Closed.Should().Be(0);
    }

    /// <summary>
    /// The adopt token for one stored row, <c>{label}/{unit}/{variant|-}@{cost}#{capability:N}/{effective_from}</c>, built from the
    /// row itself so a test names exactly the row it adopts. <paramref name="cost"/> builds the token of a row's earlier cost.
    /// </summary>
    private async Task<string> AdoptTokenAsync(Guid capabilityId, Guid rowId, string label, decimal? cost = null)
    {
        ModelCapabilityPrice row = (await PricesAsync(capabilityId)).Single(p => p.Id == rowId);
        string variant = string.IsNullOrWhiteSpace(row.Variant) ? "-" : row.Variant.Trim().ToUpperInvariant();
        string costText = (cost ?? row.CostUsdPerUnit).ToString("0.############", CultureInfo.InvariantCulture);
        string stamp = row.EffectiveFrom.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);
        return $"{label}/{row.Unit}/{variant}@{costText}#{row.ModelCapabilityId:N}/{stamp}";
    }

    [Fact]
    public async Task Run_AdoptToken_DoesNotMatchALaterSameCostRow()
    {
        decimal suggestion = Suggested(InferenceProviderKind.DeepSeek, InferenceProviderCapability.Chat, "deepseek-flash", UsageKind.LlmInputTokens);
        Guid cap = await AddCapabilityAsync(InferenceProviderKind.DeepSeek, "deepseek-flash", InferenceProviderCapability.Chat);
        // One series, two unverified rows at the same cost: the earlier one ends where the later one starts.
        Guid earlier = await AddPriceAsync(cap, UsageKind.LlmInputTokens, null, suggestion + 1m, Now.AddDays(-5), Now.AddDays(-2), PlaceholderNote);
        Guid later = await AddPriceAsync(cap, UsageKind.LlmInputTokens, null, suggestion + 1m, Now.AddDays(-2), null, PlaceholderNote);
        string earlierToken = await AdoptTokenAsync(cap, earlier, "DeepSeek/deepseek-flash/Chat");
        string laterToken = await AdoptTokenAsync(cap, later, "DeepSeek/deepseek-flash/Chat");
        laterToken.Should().NotBe(earlierToken, "the effective-from instant tells two same-cost rows apart");

        SeedRunReport report = await RunAsync(Synthetic(Row(UsageKind.LlmInputTokens, null, 0.2m)),
            adopted: new HashSet<string>(StringComparer.Ordinal) { earlierToken });

        (await PricesAsync(cap)).Single(p => p.Id == later).EffectiveTo.Should().BeNull("the token for the earlier row does not adopt the later one");
        report.Closed.Should().Be(0);
        report.Inserted.Should().Be(0);
        report.UnverifiedPlaceholders.Should().Equal("DeepSeek/deepseek-flash/Chat/LlmInputTokens/-");
        report.Actions.Should().ContainSingle(a => a.Action == "skip" && a.Reason == "placeholder-cost-unverified")
            .Which.AdoptToken.Should().Be(laterToken);
    }

    [Fact]
    public async Task Run_AdoptToken_DoesNotMatchAnotherProvidersCapability()
    {
        decimal suggestion = Suggested(InferenceProviderKind.DeepSeek, InferenceProviderCapability.Chat, "deepseek-flash", UsageKind.LlmInputTokens);
        // Two providers of the same kind, model and capability: their rows share the label, unit, variant and cost.
        Guid first = await AddCapabilityAsync(InferenceProviderKind.DeepSeek, "deepseek-flash", InferenceProviderCapability.Chat);
        Guid second = await AddCapabilityAsync(InferenceProviderKind.DeepSeek, "deepseek-flash", InferenceProviderCapability.Chat);
        Guid firstRow = await AddPriceAsync(first, UsageKind.LlmInputTokens, null, suggestion + 1m, Now.AddDays(-1), null, PlaceholderNote);
        Guid secondRow = await AddPriceAsync(second, UsageKind.LlmInputTokens, null, suggestion + 1m, Now.AddDays(-1), null, PlaceholderNote);
        ModelCatalogSeed seed = Synthetic(Row(UsageKind.LlmInputTokens, null, 0.2m));
        string firstToken = await AdoptTokenAsync(first, firstRow, "DeepSeek/deepseek-flash/Chat");
        string secondToken = await AdoptTokenAsync(second, secondRow, "DeepSeek/deepseek-flash/Chat");

        SeedRunReport dry = await RunAsync(seed, dryRun: true);

        dry.Actions.Where(a => a.Action == "skip" && a.Reason == "placeholder-cost-unverified").Select(a => a.AdoptToken)
            .Should().BeEquivalentTo(new[] { firstToken, secondToken }, "the dry run lists both series, one token each");

        SeedRunReport report = await RunAsync(seed, adopted: new HashSet<string>(StringComparer.Ordinal) { firstToken });

        (await PricesAsync(first)).Single(p => p.Id == firstRow).EffectiveTo.Should().Be(Now, "the token closes the row it names");
        (await PricesAsync(second)).Single().EffectiveTo.Should().BeNull("the other provider's row is untouched");
        (await PricesAsync(second)).Single().CostUsdPerUnit.Should().Be(suggestion + 1m);
        report.Closed.Should().Be(1);
        report.Inserted.Should().Be(1);
        report.UnverifiedPlaceholders.Should().Equal("DeepSeek/deepseek-flash/Chat/LlmInputTokens/-");
    }

    [Fact]
    public async Task Run_UnpricedCapability_LeavesUnverifiedPlaceholderInForce()
    {
        decimal suggestion = Suggested(InferenceProviderKind.DeepSeek, InferenceProviderCapability.Chat, "deepseek-flash", UsageKind.LlmInputTokens);
        Guid cap = await AddCapabilityAsync(InferenceProviderKind.DeepSeek, "deepseek-flash", InferenceProviderCapability.Chat);
        Guid placeholder = await AddPriceAsync(cap, UsageKind.LlmInputTokens, null, suggestion + 1m, Now.AddDays(-1), null, PlaceholderNote);

        SeedRunReport report = await RunAsync(Synthetic([], priceSource: "Unpriced"));

        List<ModelCapabilityPrice> prices = await PricesAsync(cap);
        prices.Should().ContainSingle();
        prices[0].Id.Should().Be(placeholder);
        prices[0].EffectiveTo.Should().BeNull();
        report.Closed.Should().Be(0);
        report.Unpriced.Should().Equal("DeepSeek/deepseek-flash/Chat");
        report.UnverifiedPlaceholders.Should().Equal("DeepSeek/deepseek-flash/Chat/LlmInputTokens/-");
        report.Actions.Should().ContainSingle(a => a.Action == "skip" && a.Reason == "placeholder-cost-unverified" && a.AdoptToken != null);
    }

    [Fact]
    public async Task Run_DryRun_ListsEverySeriesWithoutWriting()
    {
        decimal suggestion = Suggested(InferenceProviderKind.DeepSeek, InferenceProviderCapability.Chat, "deepseek-flash", UsageKind.LlmInputTokens);
        Guid cap = await AddCapabilityAsync(InferenceProviderKind.DeepSeek, "deepseek-flash", InferenceProviderCapability.Chat);
        await AddPriceAsync(cap, UsageKind.LlmInputTokens, null, suggestion, Now.AddDays(-1), null, PlaceholderNote);
        Guid xl = await AddPriceAsync(cap, UsageKind.LlmInputTokens, "XL", 0.9m, Now.AddDays(-1), null, PlaceholderNote);
        await AddPriceAsync(cap, UsageKind.LlmCachedInputTokens, null, 0.01m, Now.AddDays(-1), null, "set by an admin");
        ModelCatalogSeed seed = SyntheticFor(InferenceProviderKind.DeepSeek, "deepseek-flash", "Chat",
        [
            RowOn(InferenceProviderKind.DeepSeek, "deepseek-flash", "Chat", UsageKind.LlmInputTokens, null, suggestion),
            RowOn(InferenceProviderKind.DeepSeek, "deepseek-flash", "Chat", UsageKind.LlmInputTokens, "XL", 0.3m),
            RowOn(InferenceProviderKind.DeepSeek, "deepseek-flash", "Chat", UsageKind.LlmCachedInputTokens, null, 0.05m),
            RowOn(InferenceProviderKind.DeepSeek, "deepseek-flash", "Chat", UsageKind.LlmOutputTokens, null, 0.4m),
        ]);
        List<(Guid Id, DateTime? To, decimal Cost, string? Notes)> before = await SnapshotAsync(cap);
        CapturingLogger logger = new();

        SeedRunReport report = await RunAsync(seed, dryRun: true, logger: logger);

        (await SnapshotAsync(cap)).Should().BeEquivalentTo(before);
        report.Actions.Should().HaveCount(5);
        report.Actions.Should().ContainSingle(a => a.Action == "close" && a.OldCost == suggestion && a.NewCost == suggestion);
        report.Actions.Count(a => a.Action == "insert").Should().Be(2);
        report.Actions.Should().ContainSingle(a => a.Action == "skip" && a.Reason == "admin-owned" && a.Unit == "LlmCachedInputTokens");
        report.Actions.Should().ContainSingle(a => a.Action == "skip" && a.Reason == "placeholder-cost-unverified" && a.Variant == "XL" && a.AdoptToken != null);
        report.Closed.Should().Be(1);
        report.Inserted.Should().Be(2);
        report.SkippedAdminOwned.Should().Equal("DeepSeek/deepseek-flash/Chat/LlmCachedInputTokens/-");
        report.UnverifiedPlaceholders.Should().Equal("DeepSeek/deepseek-flash/Chat/LlmInputTokens/XL");

        List<string> actionLines = logger.Messages.Where(m => m.Contains(" reason=")).ToList();
        actionLines.Should().HaveCount(5).And.OnlyContain(m => m.Contains("DeepSeek/deepseek-flash/Chat unit="));
        string xlToken = await AdoptTokenAsync(cap, xl, "DeepSeek/deepseek-flash/Chat");
        actionLines.Should().Contain(m => m.Contains("skip DeepSeek/deepseek-flash/Chat unit=LlmInputTokens variant=XL reason=placeholder-cost-unverified old=0.9 new=0.3 adopt=" + xlToken));
        actionLines.Should().Contain(m => m.Contains("skip DeepSeek/deepseek-flash/Chat unit=LlmCachedInputTokens variant=- reason=admin-owned old=0.01 new=0.05"));
        logger.Entries.Where(e => e.Message.Contains(": skip ")).Should().OnlyContain(e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task Run_LogLimit_CapsLinesAndSummarisesTheRest()
    {
        Guid cap = await AddCapabilityAsync(InferenceProviderKind.DeepSeek, "deepseek-flash", InferenceProviderCapability.Chat);
        ModelCatalogSeed seed = SyntheticFor(InferenceProviderKind.DeepSeek, "deepseek-flash", "Chat",
        [
            RowOn(InferenceProviderKind.DeepSeek, "deepseek-flash", "Chat", UsageKind.LlmInputTokens, null, 0.1m),
            RowOn(InferenceProviderKind.DeepSeek, "deepseek-flash", "Chat", UsageKind.LlmInputTokens, "A", 0.2m),
            RowOn(InferenceProviderKind.DeepSeek, "deepseek-flash", "Chat", UsageKind.LlmOutputTokens, null, 0.3m),
            RowOn(InferenceProviderKind.DeepSeek, "deepseek-flash", "Chat", UsageKind.LlmCachedInputTokens, null, 0.4m),
        ]);
        CapturingLogger logger = new();

        SeedRunReport report = await RunAsync(seed, logger: logger, logLimit: 2);

        report.Actions.Should().HaveCount(4).And.OnlyContain(a => a.Action == "insert");
        logger.Messages.Count(m => m.Contains(" reason=")).Should().Be(2);
        logger.Messages.Should().ContainSingle(m => m.Contains("2 more series action(s) not logged"));
        (await PricesAsync(cap)).Should().HaveCount(4);
    }

    /// <summary>
    /// Documented residual (SEC-6): a row whose notes start with <c>seed:</c> is the seeder's own, so the seeder treats it
    /// as seed-owned. The admin API now refuses to write such a note (<c>InferenceProviderAdminService.Prices.cs</c>, #343
    /// T-099: "Notes cannot start with 'seed:'"), so the residual is reachable only by a direct database write, which is
    /// what this test does. The seeder's behaviour is unchanged; this pins it.
    /// </summary>
    [Fact]
    public async Task Run_ForgedSeedNote_IsTreatedAsSeedOwned_OnlyReachableByDirectDbWrite()
    {
        Guid cap = await AddCapabilityAsync(InferenceProviderKind.DeepSeek, "deepseek-flash", InferenceProviderCapability.Chat);
        Guid forged = await AddPriceAsync(cap, UsageKind.LlmInputTokens, null, 0.1m, Now.AddDays(-1), null, "seed:forged-by-an-admin");

        SeedRunReport report = await RunAsync(Synthetic(Row(UsageKind.LlmInputTokens, null, 0.2m)));

        List<ModelCapabilityPrice> prices = (await PricesAsync(cap)).OrderBy(p => p.EffectiveFrom).ToList();
        prices.Should().HaveCount(2);
        prices[0].Id.Should().Be(forged);
        prices[0].EffectiveTo.Should().Be(Now);
        report.Closed.Should().Be(1);
        report.SkippedAdminOwned.Should().BeEmpty();
    }

    /// <summary>
    /// QA-1 for the seed: the seeded long-prompt tier rows are the ones <see cref="ModelPriceService"/> bills. The expected
    /// costs are read from the seed, never hard-coded. Pricing runs through <see cref="ModelPriceService"/> with a snapshot
    /// of the seeded rows (the same pattern <c>PromptTierPricingTests</c> uses), so no database is needed for the price step.
    /// </summary>
    [Fact]
    public async Task Seed_LongPromptTiers_PriceThroughModelPriceService()
    {
        const decimal Quantity = 1_000_000m;
        ModelCatalogSeed seed = ModelCatalogSeed.LoadDefault();
        Guid haiku = await AddCapabilityAsync(InferenceProviderKind.Anthropic, "claude-haiku-5-5", InferenceProviderCapability.Chat);
        Guid gemini = await AddCapabilityAsync(InferenceProviderKind.Gemini, "gemini-2.5-pro", InferenceProviderCapability.Chat);
        await RunAsync(seed);

        PricedCapability haikuCap = await PricedCapabilityAsync(haiku);
        PricedCapability geminiCap = await PricedCapabilityAsync(gemini);
        List<ModelCapabilityPrice> rows;
        using (InferenceApiDbContext db = CreateContext())
        {
            rows = await db.ModelCapabilityPrices.AsNoTracking()
                .Where(p => p.ModelCapabilityId == haiku || p.ModelCapabilityId == gemini).ToListAsync();
        }
        ModelPriceService prices = new(new SnapshotSource(new ModelPriceSnapshot([haikuCap, geminiCap], rows, [])));

        async Task<PriceQuote> InputAt(PricedCapability cap, long promptTokens) =>
            await prices.PriceAsync(new PriceRequest(cap.Id, null, cap.Kind, cap.Capability, cap.ModelCode,
                UsageKind.LlmInputTokens, null, UsageBillingMode.Platform, Quantity, Now, promptTokens));

        PriceQuote haikuShort = await InputAt(haikuCap, 50_000);
        PriceQuote haikuLong = await InputAt(haikuCap, 150_000);
        PriceQuote geminiLong = await InputAt(geminiCap, 250_000);
        PriceQuote geminiShort = await InputAt(geminiCap, 150_000);

        SeedPriceRow haikuBase = SeedRow(seed, InferenceProviderKind.Anthropic, "claude-haiku-5-5", null);
        SeedPriceRow haikuTier = SeedRow(seed, InferenceProviderKind.Anthropic, "claude-haiku-5-5", "gt100k");
        SeedPriceRow geminiBase = SeedRow(seed, InferenceProviderKind.Gemini, "gemini-2.5-pro", null);
        SeedPriceRow geminiTier = SeedRow(seed, InferenceProviderKind.Gemini, "gemini-2.5-pro", "gt200k");

        // Non-vacuous: each tier costs more than its short-context row, so a lookup that ignored the prompt length would fail.
        haikuTier.CostUsdPerUnit.Should().BeGreaterThan(haikuBase.CostUsdPerUnit);
        geminiTier.CostUsdPerUnit.Should().BeGreaterThan(geminiBase.CostUsdPerUnit);

        haikuShort.CostUsd.Should().Be(Quantity * haikuBase.CostUsdPerUnit);
        haikuShort.PriceId.Should().Be(RowId(rows, haiku, null));
        haikuShort.Variant.Should().BeNull();

        haikuLong.CostUsd.Should().Be(Quantity * haikuTier.CostUsdPerUnit);
        haikuLong.PriceId.Should().Be(RowId(rows, haiku, haikuTier.Variant));
        haikuLong.Variant.Should().Be(haikuTier.Variant);

        geminiLong.CostUsd.Should().Be(Quantity * geminiTier.CostUsdPerUnit);
        geminiLong.PriceId.Should().Be(RowId(rows, gemini, geminiTier.Variant));
        geminiLong.Variant.Should().Be(geminiTier.Variant);

        geminiShort.CostUsd.Should().Be(Quantity * geminiBase.CostUsdPerUnit);
        geminiShort.PriceId.Should().Be(RowId(rows, gemini, null));
        geminiShort.Variant.Should().BeNull();
    }

    [PostgresFact]
    public async Task Run_TwoBootsAtOnce_OnPostgres_LeaveOneOpenRowPerSeries()
    {
        string baseConnection = PostgresTestEnvironment.BaseConnectionString()!;
        string dbName = "reelbolt_seed_lock_test_" + Guid.NewGuid().ToString("N")[..12];
        await using NpgsqlDataSource admin = NpgsqlDataSource.Create(baseConnection);
        await using (NpgsqlCommand create = admin.CreateCommand($"CREATE DATABASE {dbName}"))
        {
            await create.ExecuteNonQueryAsync();
        }

        NpgsqlConnectionStringBuilder csb = new(baseConnection) { Database = dbName };
        InferenceApiDbContext Pg() => new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseNpgsql(csb.ConnectionString).Options);
        try
        {
            Guid capId;
            await using (InferenceApiDbContext setup = Pg())
            {
                await setup.Database.MigrateAsync();
                InferenceProvider provider = new() { Id = Guid.NewGuid(), Name = "DeepSeek lock test", Kind = InferenceProviderKind.DeepSeek };
                InferenceModel model = new() { Id = Guid.NewGuid(), ProviderId = provider.Id, ModelCode = "deepseek-flash", DisplayName = "DeepSeek lock test" };
                ModelCapability cap = new() { Id = Guid.NewGuid(), ModelId = model.Id, Capability = InferenceProviderCapability.Chat };
                setup.InferenceProviders.Add(provider);
                setup.InferenceModels.Add(model);
                setup.ModelCapabilities.Add(cap);
                setup.ModelCapabilityPrices.Add(new ModelCapabilityPrice
                {
                    Id = Guid.NewGuid(),
                    ModelCapabilityId = cap.Id,
                    Unit = UsageKind.LlmInputTokens,
                    CostUsdPerUnit = Suggested(InferenceProviderKind.DeepSeek, InferenceProviderCapability.Chat, "deepseek-flash", UsageKind.LlmInputTokens),
                    EffectiveFrom = Now.AddDays(-1),
                    Notes = PlaceholderNote,
                });
                await setup.SaveChangesAsync();
                capId = cap.Id;
            }

            ModelCatalogSeed seed = Synthetic(Row(UsageKind.LlmInputTokens, null, 0.2m), Row(UsageKind.LlmOutputTokens, null, 0.3m));
            async Task<SeedRunReport> Boot(DateTime at)
            {
                await using InferenceApiDbContext db = Pg();
                return await ModelCatalogPriceSeeder.RunAsync(db, seed, at, dryRun: false, NullLogger.Instance);
            }

            SeedRunReport[] reports = await Task.WhenAll(Boot(Now), Boot(Now.AddMilliseconds(5)));

            reports.Sum(r => r.Inserted).Should().Be(2, "the second boot waits for the lock and then finds the rows");
            await using InferenceApiDbContext check = Pg();
            List<ModelCapabilityPrice> open = await check.ModelCapabilityPrices.AsNoTracking()
                .Where(p => p.ModelCapabilityId == capId && p.EffectiveTo == null).ToListAsync();
            open.GroupBy(p => (p.Unit, p.Variant)).Should().HaveCount(2).And.OnlyContain(g => g.Count() == 1);

            (await Boot(Now.AddDays(1))).Should().Match<SeedRunReport>(r => r.Inserted == 0 && r.Closed == 0);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using NpgsqlCommand drop = admin.CreateCommand($"DROP DATABASE IF EXISTS {dbName} WITH (FORCE)");
            await drop.ExecuteNonQueryAsync();
        }
    }

    /// <summary>The pricing view of a seeded capability: its account (provider), kind, capability and model code.</summary>
    private async Task<PricedCapability> PricedCapabilityAsync(Guid capabilityId)
    {
        using InferenceApiDbContext db = CreateContext();
        ModelCapability cap = await db.ModelCapabilities.AsNoTracking().SingleAsync(c => c.Id == capabilityId);
        InferenceModel model = await db.InferenceModels.AsNoTracking().SingleAsync(m => m.Id == cap.ModelId);
        InferenceProvider provider = await db.InferenceProviders.AsNoTracking().SingleAsync(p => p.Id == model.ProviderId);
        return new PricedCapability(cap.Id, provider.Id, cap.OrganizationId, provider.Kind, cap.Capability, model.ModelCode);
    }

    /// <summary>The seed's Chat LlmInputTokens row for one variant (null = variant-less), read from the seed, never hard-coded.</summary>
    private static SeedPriceRow SeedRow(ModelCatalogSeed seed, InferenceProviderKind kind, string model, string? variant) =>
        seed.Rows.Single(r => r.Kind == kind && r.ModelCode == model && r.Capability == "Chat"
            && r.Unit == UsageKind.LlmInputTokens && string.Equals(r.Variant ?? string.Empty, variant ?? string.Empty, StringComparison.OrdinalIgnoreCase));

    /// <summary>The id of the seeded LlmInputTokens row of one capability and variant (null = variant-less).</summary>
    private static Guid RowId(List<ModelCapabilityPrice> rows, Guid capabilityId, string? variant) =>
        rows.Single(p => p.ModelCapabilityId == capabilityId && p.Unit == UsageKind.LlmInputTokens
            && string.Equals(p.Variant ?? string.Empty, variant ?? string.Empty, StringComparison.OrdinalIgnoreCase)).Id;

    private sealed class SnapshotSource(ModelPriceSnapshot snapshot) : IModelPriceSource
    {
        public Task<ModelPriceSnapshot> LoadAsync(CancellationToken ct) => Task.FromResult(snapshot);
    }

    private const string PlaceholderNote =
        "PLACEHOLDER (published vendor list price, not reconciled against a provider invoice) - Basis: test.";

    // ------------------------------------------------------------------ helpers

    private InferenceApiDbContext CreateContext()
    {
        DbContextOptions<InferenceApiDbContext> options = new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase(_dbName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new InferenceApiDbContext(options);
    }

    private async Task<SeedRunReport> RunAsync(ModelCatalogSeed seed, DateTime? now = null, bool dryRun = false,
        IReadOnlySet<string>? adopted = null, int logLimit = 200, ILogger? logger = null)
    {
        using InferenceApiDbContext db = CreateContext();
        return await ModelCatalogPriceSeeder.RunAsync(db, seed, now ?? Now, dryRun, logger ?? NullLogger.Instance,
            adopted: adopted, logLimit: logLimit);
    }

    private async Task<List<(Guid Id, DateTime? To, decimal Cost, string? Notes)>> SnapshotAsync(Guid capabilityId) =>
        (await PricesAsync(capabilityId)).OrderBy(p => p.Id)
            .Select(p => (Id: p.Id, To: p.EffectiveTo, Cost: p.CostUsdPerUnit, Notes: p.Notes)).ToList();

    /// <summary>A PLACEHOLDER row the seed cannot verify and the operator has not adopted: it stays exactly as it was.</summary>
    private async Task AssertLeftAloneAsUnverifiedAsync(Guid cap, Guid row, decimal cost, SeedRunReport report)
    {
        List<ModelCapabilityPrice> prices = await PricesAsync(cap);
        prices.Should().ContainSingle();
        prices[0].Id.Should().Be(row);
        prices[0].EffectiveTo.Should().BeNull();
        prices[0].CostUsdPerUnit.Should().Be(cost);
        report.UnverifiedPlaceholders.Should().Equal("DeepSeek/deepseek-flash/Chat/LlmInputTokens/-");
        report.Actions.Should().ContainSingle(a => a.Action == "skip" && a.Reason == "placeholder-cost-unverified" && a.AdoptToken != null);
        report.Inserted.Should().Be(0);
    }

    /// <summary>The figure the price book suggests for one series, read from <see cref="PriceBook.SuggestFor"/>, never hard-coded.</summary>
    private static decimal Suggested(InferenceProviderKind kind, InferenceProviderCapability capability, string model, UsageKind unit,
        string? variant = null)
    {
        string key = variant?.Trim() ?? string.Empty;
        return PriceBook.SuggestFor(kind, capability, model)
            .Single(s => s.Unit == unit && string.Equals((s.Variant ?? string.Empty).Trim(), key, StringComparison.OrdinalIgnoreCase))
            .CostUsdPerUnit;
    }

    private static SeedPriceRow RowOn(InferenceProviderKind kind, string model, string capability, UsageKind unit, string? variant, decimal cost) =>
        new(kind, model, capability, unit, variant, cost, SeedDay, null, "$x / unit", "https://example.test/pricing", SeedDay, "");

    private static ModelCatalogSeed SyntheticFor(InferenceProviderKind kind, string model, string capability, SeedPriceRow[] rows,
        string priceSource = "Table", string version = "2026.10.12.9") =>
        new(1, version, SeedDay, rows,
            [new SeedCapabilityEntry(kind, model, "Test capability", "current", "Test", null, capability, priceSource,
                priceSource == "Unpriced" ? "no published price" : null)]);

    /// <summary>Captures every rendered log line with its level.</summary>
    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IEnumerable<string> Messages => Entries.Select(e => e.Message);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception)));
    }

    private async Task<Guid> AddCapabilityAsync(InferenceProviderKind kind, string modelCode, InferenceProviderCapability capability,
        Guid? organizationId = null, ModelPriceSource priceSource = ModelPriceSource.Table, string? displayName = null, string endpoint = "")
    {
        using InferenceApiDbContext db = CreateContext();
        InferenceProvider provider = new() { Id = Guid.NewGuid(), OrganizationId = organizationId, Name = $"{kind} {Guid.NewGuid():N}", Kind = kind, Endpoint = endpoint };
        InferenceModel model = new()
        {
            Id = Guid.NewGuid(),
            ProviderId = provider.Id,
            OrganizationId = organizationId,
            ModelCode = modelCode,
            DisplayName = displayName ?? $"{modelCode} {capability}",
        };
        ModelCapability cap = new()
        {
            Id = Guid.NewGuid(),
            ModelId = model.Id,
            OrganizationId = organizationId,
            Capability = capability,
            PriceSource = priceSource,
        };
        db.InferenceProviders.Add(provider);
        db.InferenceModels.Add(model);
        db.ModelCapabilities.Add(cap);
        await db.SaveChangesAsync();
        return cap.Id;
    }

    private async Task<Guid> AddPriceAsync(Guid capabilityId, UsageKind unit, string? variant, decimal cost, DateTime from, DateTime? to,
        string? notes, decimal? creditsOverride = null)
    {
        using InferenceApiDbContext db = CreateContext();
        ModelCapabilityPrice price = new()
        {
            Id = Guid.NewGuid(),
            ModelCapabilityId = capabilityId,
            Unit = unit,
            Variant = variant,
            CostUsdPerUnit = cost,
            CreditsMillisPerUnitOverride = creditsOverride,
            EffectiveFrom = from,
            EffectiveTo = to,
            Notes = notes,
        };
        db.ModelCapabilityPrices.Add(price);
        await db.SaveChangesAsync();
        return price.Id;
    }

    private async Task<List<ModelCapabilityPrice>> PricesAsync(Guid capabilityId)
    {
        using InferenceApiDbContext db = CreateContext();
        return await db.ModelCapabilityPrices.AsNoTracking().Where(p => p.ModelCapabilityId == capabilityId).ToListAsync();
    }

    private static SeedPriceRow Row(UsageKind unit, string? variant, decimal cost, DateOnly? from = null, DateOnly? to = null) =>
        new(InferenceProviderKind.DeepSeek, "deepseek-flash", "Chat", unit, variant, cost, from ?? SeedDay, to,
            "$x / unit", "https://example.test/pricing", SeedDay, "");

    private static ModelCatalogSeed Synthetic(params SeedPriceRow[] rows) => Synthetic("2026.10.12.9", rows);

    private static ModelCatalogSeed Synthetic(string version, params SeedPriceRow[] rows) => Synthetic(rows, version: version);

    private static ModelCatalogSeed Synthetic(SeedPriceRow[] rows, string priceSource = "Table", string version = "2026.10.12.9") =>
        new(1, version, SeedDay, rows,
            [new SeedCapabilityEntry(InferenceProviderKind.DeepSeek, "deepseek-flash", "DeepSeek Flash", "current", "DeepSeek", null, "Chat",
                priceSource, priceSource == "Unpriced" ? "no published price" : null)]);
}
