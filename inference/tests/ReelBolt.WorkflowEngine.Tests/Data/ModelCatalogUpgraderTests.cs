#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using ReelBolt.WorkflowEngine.Tests.Pricing;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Data;

/// <summary>
/// <see cref="ModelCatalogUpgrader"/> (docs/design/model-catalog.md §4.2) against a real (Sqlite) schema: the system
/// compute provider, the one-time price port (rewrites the old <c>RateCardSeedTopUpTests</c>: an install's own prices
/// survive, a retired price is not resurrected, a second run adds nothing), the video variants and consolidation.
/// </summary>
public sealed class ModelCatalogUpgraderTests : IAsyncLifetime
{
    private static readonly DateTime SeedFrom = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly ISecretProtector _secrets =
        new DataProtectionSecretProtector(new EphemeralDataProtectionProvider(), NullLogger<DataProtectionSecretProtector>.Instance);
    private InferenceApiDbContext _db = null!;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _db = NewContext();
        await _db.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private InferenceApiDbContext NewContext() =>
        new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseSqlite(_connection).Options);

    private ModelCatalogUpgrader Upgrader(
        InferenceApiDbContext? db = null, string? consolidation = null, IInferenceProviderResolver? resolver = null,
        IModelPriceService? prices = null, ILogger<ModelCatalogUpgrader>? logger = null) =>
        new(db ?? _db, _secrets,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                [ModelCatalogUpgrader.ConsolidationKey] = consolidation
            }).Build(),
            logger ?? NullLogger<ModelCatalogUpgrader>.Instance, resolver, prices);

    private sealed class CapturingLogger : ILogger<ModelCatalogUpgrader>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    // ------------------------------------------------------------------ fixture rows

    private InferenceProvider Account(
        string name, InferenceProviderKind kind, string? plaintextKey, DateTime created, Guid? org = null,
        string endpoint = "https://api.example", int? timeout = null, bool enabled = true, string? cipher = null,
        string? headers = null)
    {
        InferenceProvider account = new()
        {
            Id = Guid.NewGuid(),
            OrganizationId = org,
            Name = name,
            Kind = kind,
            Endpoint = endpoint,
            ApiKeyEncrypted = cipher ?? (plaintextKey is null ? null : _secrets.Protect(plaintextKey)),
            ApiKeyLastFour = plaintextKey is { Length: >= 4 } ? plaintextKey[^4..] : plaintextKey,
            IsEnabled = enabled,
            TimeoutSeconds = timeout,
            ExtraHeadersJson = headers,
            CreatedAt = created,
            UpdatedAt = created
        };
        _db.InferenceProviders.Add(account);
        return account;
    }

    private ModelCapability Capability(
        InferenceProvider account, string code, InferenceProviderCapability capability, string? display = null,
        DateTime? created = null, bool isDefault = false, ModelPriceSource source = ModelPriceSource.Table)
    {
        InferenceModel model = new()
        {
            Id = Guid.NewGuid(),
            ProviderId = account.Id,
            OrganizationId = account.OrganizationId,
            ModelCode = code,
            DisplayName = display ?? account.Name,
            CreatedAt = created ?? account.CreatedAt,
            UpdatedAt = created ?? account.CreatedAt
        };
        ModelCapability row = new()
        {
            Id = Guid.NewGuid(),
            ModelId = model.Id,
            OrganizationId = account.OrganizationId,
            Capability = capability,
            IsDefault = isDefault,
            PriceSource = source,
            CreatedAt = model.CreatedAt,
            UpdatedAt = model.CreatedAt
        };
        _db.InferenceModels.Add(model);
        _db.ModelCapabilities.Add(row);
        return row;
    }

    private static RateCard Card(
        InferenceProviderKind kind, UsageKind unit, string pattern, decimal cost, decimal? credits = null,
        DateTime? from = null, DateTime? to = null, string? notes = null) => new()
        {
            Id = Guid.NewGuid(),
            ProviderKind = kind,
            UsageKind = unit,
            ModelPattern = pattern,
            CostUsdPerUnit = cost,
            CreditsMillisPerUnit = credits ?? cost * 1000m / 0.006m,
            EffectiveFrom = from ?? SeedFrom,
            EffectiveTo = to,
            Notes = notes ?? "seeded"
        };

    private async Task<List<ModelCapabilityPrice>> PricesOf(Guid capabilityId)
    {
        await using InferenceApiDbContext db = NewContext();
        return await db.ModelCapabilityPrices.AsNoTracking().Where(p => p.ModelCapabilityId == capabilityId).ToListAsync();
    }

    private async Task<List<ModelCapabilityView>> Resolvable(Guid? org = null)
    {
        await using InferenceApiDbContext db = NewContext();
        return await ModelCapabilityView.EnabledFor(db.ModelCapabilities.IgnoreQueryFilters().AsNoTracking(), org).ToListAsync();
    }

    // ------------------------------------------------------------------ 1. compute provider

    [Fact]
    public async Task ComputeProvider_IsIdempotent()
    {
        (await Upgrader().EnsureComputeProviderAsync()).Should().BeTrue();
        (await Upgrader(NewContext()).EnsureComputeProviderAsync()).Should().BeFalse("the second run finds all three rows");

        await using InferenceApiDbContext db = NewContext();
        InferenceProvider provider = await db.InferenceProviders.IgnoreQueryFilters().SingleAsync();
        provider.Id.Should().Be(WellKnownModelCatalog.ComputeProviderId);
        provider.IsSystem.Should().BeTrue();
        provider.Name.Should().Be(WellKnownModelCatalog.ComputeProviderName);
        provider.ApiKeyEncrypted.Should().BeNull();
        InferenceModel model = await db.InferenceModels.IgnoreQueryFilters().SingleAsync();
        (model.Id, model.ModelCode, model.DisplayName).Should().Be(
            (WellKnownModelCatalog.ComputeModelId, WellKnownModelCatalog.ComputeModelCode, WellKnownModelCatalog.ComputeModelDisplayName));
        ModelCapability capability = await db.ModelCapabilities.IgnoreQueryFilters().SingleAsync();
        (capability.Id, capability.Capability, capability.OrganizationId).Should().Be(
            (WellKnownModelCatalog.ComputeCapabilityId, InferenceProviderCapability.Compute, (Guid?)null));
    }

    // ------------------------------------------------------------------ 2. the one-time price port

    [Fact]
    public async Task Port_SeededCard_DerivesFromCost()
    {
        InferenceProvider anthropic = Account("Anthropic", InferenceProviderKind.Anthropic, "sk-ant-1", SeedFrom);
        ModelCapability chat = Capability(anthropic, "claude-sonnet-5-5", InferenceProviderCapability.Chat);
        RateCard input = Card(InferenceProviderKind.Anthropic, UsageKind.LlmInputTokens, "claude-sonnet-5-5", 0.000002m,
            notes: "PLACEHOLDER published");
        _db.RateCards.AddRange(input,
            Card(InferenceProviderKind.Anthropic, UsageKind.LlmOutputTokens, "claude-sonnet-5-5", 0.00001m),
            Card(InferenceProviderKind.Anthropic, UsageKind.LlmCachedInputTokens, "*", 0.0000001m),
            Card(InferenceProviderKind.Anthropic, UsageKind.LlmInputTokens, "*", 0.5m));
        await _db.SaveChangesAsync();

        (int ported, bool stamped) = await Upgrader().PortPricesOnceAsync();

        ported.Should().Be(3);
        stamped.Should().BeTrue();
        List<ModelCapabilityPrice> rows = await PricesOf(chat.Id);
        ModelCapabilityPrice inputRow = rows.Single(r => r.Unit == UsageKind.LlmInputTokens);
        inputRow.CostUsdPerUnit.Should().Be(0.000002m, "the exact-name card beats the wildcard, as the old rule did");
        inputRow.CreditsMillisPerUnitOverride.Should().BeNull("a cost-derived card picks up the markup");
        inputRow.Variant.Should().BeNull();
        inputRow.EffectiveFrom.Should().Be(SeedFrom);
        inputRow.Notes.Should().Be("PLACEHOLDER published");
        rows.Single(r => r.Unit == UsageKind.LlmCachedInputTokens).CostUsdPerUnit.Should().Be(0.0000001m, "the wildcard fills the rest");
        rows.Should().OnlyContain(r => r.CreditsMillisPerUnitOverride == null);

        // Priced at cost × 1.2 ÷ $0.006: 2e-6 × 1.2 × 1000 / 0.006 = 0.4 millicredits per token.
        ModelPriceService prices = new(new SnapshotOf(NewContext));
        (await prices.PriceAsync(new PriceRequest(chat.Id, null, InferenceProviderKind.Anthropic, InferenceProviderCapability.Chat,
            "claude-sonnet-5-5", UsageKind.LlmInputTokens, null, UsageBillingMode.Platform, 1000))).CreditsMillis.Should().Be(400);
    }

    [Fact]
    public async Task Port_AdminEditedCredits_BecomesOverride()
    {
        InferenceProvider fish = Account("Fish", InferenceProviderKind.FishAudio, "fish-key-1", SeedFrom);
        ModelCapability tts = Capability(fish, "s2.1-pro", InferenceProviderCapability.SpeechSynthesis);
        // An operator priced this by hand: 20.576 millicredits per byte, where cost × 1000 ÷ 0.006 would be 20 576.
        _db.RateCards.Add(Card(InferenceProviderKind.FishAudio, UsageKind.TtsInputBytes, "s2.1-pro", 0.123456m, credits: 20.576m));
        await _db.SaveChangesAsync();

        await Upgrader().PortPricesOnceAsync();

        ModelCapabilityPrice row = (await PricesOf(tts.Id)).Single();
        row.CostUsdPerUnit.Should().Be(0.123456m);
        row.CreditsMillisPerUnitOverride.Should().Be(20.576m, "the admin's credits are kept exactly, the markup does not apply");
        ModelPriceService prices = new(new SnapshotOf(NewContext));
        (await prices.PriceAsync(new PriceRequest(tts.Id, null, InferenceProviderKind.FishAudio, InferenceProviderCapability.SpeechSynthesis,
            "s2.1-pro", UsageKind.TtsInputBytes, null, UsageBillingMode.Platform, 1000))).CreditsMillis.Should().Be(20_576);
    }

    [Fact]
    public async Task Port_TierCards_BecomeVariants_AndARetiredCardIsNotPorted()
    {
        InferenceProvider fal = Account("fal", InferenceProviderKind.FalAi, "fal-key-1", SeedFrom);
        ModelCapability erase = Capability(fal, "fal-ai/object-removal", InferenceProviderCapability.ImageErase);
        _db.RateCards.AddRange(
            Card(InferenceProviderKind.FalAi, UsageKind.ImageEditImages, "*", 0.6m),
            Card(InferenceProviderKind.FalAi, UsageKind.ImageEditImages, "fal-ai/object-removal#low_quality", 0.006m),
            Card(InferenceProviderKind.FalAi, UsageKind.ImageEditImages, "fal-ai/object-removal#best_quality", 0.024m),
            // An admin ended this tier deliberately: the port must not resurrect it.
            Card(InferenceProviderKind.FalAi, UsageKind.ImageEditImages, "fal-ai/object-removal#high_quality", 0.018m,
                to: new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc)),
            // Another model's tier is not this capability's.
            Card(InferenceProviderKind.FalAi, UsageKind.ImageEditImages, "fal-ai/object-removal/mask#low_quality", 0.5m));
        await _db.SaveChangesAsync();

        await Upgrader().PortPricesOnceAsync();

        (await PricesOf(erase.Id)).Select(r => (r.Variant, r.CostUsdPerUnit)).Should().BeEquivalentTo(new[]
        {
            ((string?)null, 0.6m), ("low_quality", 0.006m), ("best_quality", 0.024m)
        });
    }

    [Fact]
    public async Task Port_ComputeCards_BecomeTheComputeCapabilitysTierVariants()
    {
        _db.RateCards.AddRange(LegacyRateCardSeed.ComputeCards(SeedFrom));
        await _db.SaveChangesAsync();
        await Upgrader().EnsureComputeProviderAsync();

        await Upgrader().PortPricesOnceAsync();

        (await PricesOf(WellKnownModelCatalog.ComputeCapabilityId)).Select(r => (r.Unit, r.Variant, r.CostUsdPerUnit))
            .Should().BeEquivalentTo(LegacyRateCardSeed.ComputeCards(SeedFrom).Select(c => (c.UsageKind, (string?)c.ModelPattern, c.CostUsdPerUnit)));
    }

    [Fact]
    public async Task Port_FreshInstall_UsesPriceBook()
    {
        InferenceProvider fish = Account("Fish", InferenceProviderKind.FishAudio, "fish-key-1", SeedFrom);
        ModelCapability asr = Capability(fish, "transcribe-1-pro", InferenceProviderCapability.Transcription);
        InferenceProvider minimax = Account("MiniMax", InferenceProviderKind.MiniMax, "mm-key-1", SeedFrom);
        ModelCapability video = Capability(minimax, "MiniMax-H3", InferenceProviderCapability.VideoGeneration);
        InferenceProvider azure = Account("Azure", InferenceProviderKind.AzureOpenAI, "az-key-1", SeedFrom);
        ModelCapability unpriced = Capability(azure, "gpt-4o", InferenceProviderCapability.Chat);
        await _db.SaveChangesAsync();
        await Upgrader().EnsureComputeProviderAsync();

        (int ported, bool stamped) = await Upgrader().PortPricesOnceAsync();

        stamped.Should().BeTrue();
        ported.Should().BeGreaterThan(0);
        (await PricesOf(asr.Id)).Should().ContainSingle().Which.CostUsdPerUnit.Should().Be(0.36m / 3600m);
        (await PricesOf(video.Id)).Select(r => (r.Variant, r.CostUsdPerUnit)).Should().BeEquivalentTo(new[]
        {
            ((string?)null, 0.08m), ("768P", 0.08m), ("2K", 0.13m)
        });
        (await PricesOf(unpriced.Id)).Should().BeEmpty("the book has no published price for it: a coverage gap, not a guess");
        (await PricesOf(WellKnownModelCatalog.ComputeCapabilityId)).Select(r => (r.Unit, r.Variant, r.CostUsdPerUnit))
            .Should().BeEquivalentTo(LegacyRateCardSeed.ComputeCards(SeedFrom, PricingDefaults.ComputeRenderUsdPerSecond).Select(c => (c.UsageKind, (string?)c.ModelPattern, c.CostUsdPerUnit)),
                "a fresh install prices compute at the old seed's shape, on the current render anchor");
    }

    [Fact]
    public async Task Port_RunsOnce_AndNeverPricesAByoCapability()
    {
        Organization org = new() { Id = Guid.NewGuid(), Name = "o", Slug = "o" };
        _db.Organizations.Add(org);
        InferenceProvider byo = Account("Own", InferenceProviderKind.Anthropic, "own-key", SeedFrom, org.Id);
        ModelCapability byoChat = Capability(byo, "claude-sonnet-5-5", InferenceProviderCapability.Chat);
        InferenceProvider platform = Account("Anthropic", InferenceProviderKind.Anthropic, "sk-ant-1", SeedFrom);
        ModelCapability chat = Capability(platform, "claude-sonnet-5-5", InferenceProviderCapability.Chat);
        _db.RateCards.Add(Card(InferenceProviderKind.Anthropic, UsageKind.LlmInputTokens, "*", 0.000002m));
        await _db.SaveChangesAsync();

        (await Upgrader().PortPricesOnceAsync()).Should().Be((1, true));
        (await Upgrader(NewContext()).PortPricesOnceAsync()).Should().Be((0, false), "catalog_upgraded_at is stamped");

        (await PricesOf(byoChat.Id)).Should().BeEmpty("BYO is ×0 and never priced");
        (await PricesOf(chat.Id)).Should().ContainSingle();
        await using InferenceApiDbContext db = NewContext();
        (await db.PricingSettings.SingleAsync()).CatalogUpgradedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Port_FutureDatedCards_ArePortedWithTheirEffectiveFrom_AsTheOldRuleWouldHaveSwitched()
    {
        DateTime nextMonth = DateTime.UtcNow.Date.AddDays(30);
        DateTime later = DateTime.UtcNow.Date.AddDays(60);
        InferenceProvider anthropic = Account("Anthropic", InferenceProviderKind.Anthropic, "sk-ant-1", SeedFrom);
        ModelCapability chat = Capability(anthropic, "claude-sonnet-5-5", InferenceProviderCapability.Chat);
        InferenceProvider fal = Account("fal", InferenceProviderKind.FalAi, "fal-key-1", SeedFrom);
        ModelCapability erase = Capability(fal, "fal-ai/object-removal", InferenceProviderCapability.ImageErase);
        await _db.SaveChangesAsync();
        await Upgrader().EnsureComputeProviderAsync();
        _db.RateCards.AddRange(
            Card(InferenceProviderKind.Anthropic, UsageKind.LlmInputTokens, "claude-sonnet-5-5", 0.000002m),
            // An announced price change: the old rule would have switched to it on its first day.
            Card(InferenceProviderKind.Anthropic, UsageKind.LlmInputTokens, "claude-sonnet-5-5", 0.000003m, from: nextMonth),
            // A future wildcard never beat an exact card under the old rule, so it must not win here either.
            Card(InferenceProviderKind.Anthropic, UsageKind.LlmInputTokens, "*", 0.9m, from: nextMonth),
            // An exact output price that ends later: the wildcard underneath takes over then.
            Card(InferenceProviderKind.Anthropic, UsageKind.LlmOutputTokens, "claude-sonnet-5-5", 0.00001m, to: later),
            Card(InferenceProviderKind.Anthropic, UsageKind.LlmOutputTokens, "*", 0.00002m),
            Card(InferenceProviderKind.FalAi, UsageKind.ImageEditImages, "*", 0.6m),
            Card(InferenceProviderKind.FalAi, UsageKind.ImageEditImages, "fal-ai/object-removal#low_quality", 0.006m),
            Card(InferenceProviderKind.FalAi, UsageKind.ImageEditImages, "fal-ai/object-removal#low_quality", 0.007m, from: nextMonth),
            Card(ComputeMetering.ProviderKind, UsageKind.RenderOutputSeconds, ComputeMetering.Tier720, 0.000017m),
            Card(ComputeMetering.ProviderKind, UsageKind.RenderOutputSeconds, ComputeMetering.Tier720, 0.00002m, from: nextMonth));
        await _db.SaveChangesAsync();

        await Upgrader().PortPricesOnceAsync();

        List<ModelCapabilityPrice> chatRows = await PricesOf(chat.Id);
        chatRows.Where(r => r.Unit == UsageKind.LlmInputTokens).Select(r => (r.EffectiveFrom, r.CostUsdPerUnit)).Should().BeEquivalentTo(new[]
        {
            (SeedFrom, 0.000002m), (nextMonth, 0.000003m)
        });
        chatRows.Where(r => r.Unit == UsageKind.LlmOutputTokens).Select(r => (r.EffectiveFrom, r.EffectiveTo, r.CostUsdPerUnit))
            .Should().BeEquivalentTo(new[] { (SeedFrom, (DateTime?)later, 0.00001m), (later, (DateTime?)null, 0.00002m) });
        (await PricesOf(erase.Id)).Where(r => r.Variant == "low_quality").Select(r => (r.EffectiveFrom, r.CostUsdPerUnit))
            .Should().BeEquivalentTo(new[] { (SeedFrom, 0.006m), (nextMonth, 0.007m) });
        (await PricesOf(WellKnownModelCatalog.ComputeCapabilityId)).Where(r => r.Variant == ComputeMetering.Tier720)
            .Select(r => (r.EffectiveFrom, r.CostUsdPerUnit)).Should().BeEquivalentTo(new[] { (SeedFrom, 0.000017m), (nextMonth, 0.00002m) });

        // And the price service follows the same calendar.
        ModelPriceService prices = new(new SnapshotOf(NewContext));
        PriceRequest input(DateTime at) => new(chat.Id, null, InferenceProviderKind.Anthropic, InferenceProviderCapability.Chat,
            "claude-sonnet-5-5", UsageKind.LlmInputTokens, null, UsageBillingMode.Platform, 1_000_000, At: at);
        (await prices.PriceAsync(input(nextMonth.AddDays(-1)))).CostUsd.Should().Be(2m);
        (await prices.PriceAsync(input(nextMonth.AddDays(1)))).CostUsd.Should().Be(3m);
    }

    // ------------------------------------------------------------------ 3. video resolution variants

    [Fact]
    public async Task VideoVariants_AddedForMiniMaxAndFal()
    {
        InferenceProvider minimax = Account("MiniMax", InferenceProviderKind.MiniMax, "mm-key", SeedFrom);
        ModelCapability h3max = Capability(minimax, "MiniMax-H3-Max", InferenceProviderCapability.VideoGeneration);
        InferenceProvider fal = Account("fal", InferenceProviderKind.FalAi, "fal-key", SeedFrom);
        ModelCapability falVideo = Capability(fal, "fal-ai/vidu/q4/image-to-video", InferenceProviderCapability.VideoGeneration);
        InferenceProvider higgs = Account("Higgsfield", InferenceProviderKind.Higgsfield, "hf-key", SeedFrom);
        ModelCapability higgsVideo = Capability(higgs, "dop-turbo", InferenceProviderCapability.VideoGeneration, source: ModelPriceSource.Oracle);
        InferenceProvider comfy = Account("ComfyUI", InferenceProviderKind.ComfyUI, null, SeedFrom);
        ModelCapability comfyVideo = Capability(comfy, "wan-2.2", InferenceProviderCapability.VideoGeneration);
        // An operator already priced 480P by hand: left alone.
        _db.ModelCapabilityPrices.Add(new ModelCapabilityPrice
        {
            Id = Guid.NewGuid(),
            ModelCapabilityId = h3max.Id,
            Unit = UsageKind.VideoGenSeconds,
            Variant = "480P",
            CostUsdPerUnit = 0.04m,
            EffectiveFrom = SeedFrom
        });
        await _db.SaveChangesAsync();

        int added = await Upgrader().AddVideoResolutionVariantsAsync();

        (await PricesOf(h3max.Id)).Select(r => (r.Variant, r.CostUsdPerUnit)).Should().BeEquivalentTo(new[]
        {
            ("480P", 0.04m), ("768P", 0.08m)
        });
        (await PricesOf(falVideo.Id)).Select(r => (r.Variant, r.CostUsdPerUnit)).Should().BeEquivalentTo(
            FalVideoCatalog.Instance.Find("fal-ai/vidu/q4/image-to-video")!.PricePerSecondByResolution.Select(p => ((string?)p.Key, p.Value)));
        (await PricesOf(higgsVideo.Id)).Should().BeEmpty("Higgsfield stays oracle-priced");
        (await PricesOf(comfyVideo.Id)).Should().BeEmpty("ComfyUI budgets GPU-seconds, never a USD variant source");
        added.Should().Be(1 + FalVideoCatalog.Instance.Find("fal-ai/vidu/q4/image-to-video")!.PricePerSecondByResolution.Count);

        (await Upgrader(NewContext()).AddVideoResolutionVariantsAsync()).Should().Be(0, "every variant now has a row");
    }

    // ------------------------------------------------------------------ 4. consolidation

    [Fact]
    public async Task Consolidation_GroupsSameKey()
    {
        // The realistic install: Anthropic Chat + Vision rows sharing one key (encrypted separately, random IV), and a
        // fal row with two image-tool rows whose ciphertext was copied byte for byte.
        InferenceProvider anthChat = Account("Anthropic", InferenceProviderKind.Anthropic, "sk-ant-shared", SeedFrom, endpoint: "https://api.anthropic.com", timeout: 120);
        ModelCapability chat = Capability(anthChat, "claude-sonnet-5-5", InferenceProviderCapability.Chat, isDefault: true);
        InferenceProvider anthVision = Account("Anthropic Vision", InferenceProviderKind.Anthropic, "sk-ant-shared", SeedFrom.AddDays(1),
            endpoint: "https://api.anthropic.com ", timeout: 120);
        ModelCapability vision = Capability(anthVision, "claude-sonnet-5-5", InferenceProviderCapability.Vision, isDefault: true);
        InferenceProvider fal = Account("fal", InferenceProviderKind.FalAi, "fal-shared", SeedFrom.AddDays(2), endpoint: "https://fal.run");
        ModelCapability falEdit = Capability(fal, "fal-ai/flux-pro/kontext", InferenceProviderCapability.ImageEdit);
        InferenceProvider falErase = Account("fal - Remove objects", InferenceProviderKind.FalAi, null, SeedFrom.AddDays(2),
            endpoint: "https://fal.run", cipher: fal.ApiKeyEncrypted);
        ModelCapability erase = Capability(falErase, "fal-ai/object-removal", InferenceProviderCapability.ImageErase);
        InferenceProvider falUpscale = Account("fal - Upscale", InferenceProviderKind.FalAi, null, SeedFrom.AddDays(2),
            endpoint: "https://fal.run", cipher: fal.ApiKeyEncrypted);
        ModelCapability upscale = Capability(falUpscale, "fal-ai/clarity-upscaler", InferenceProviderCapability.ImageUpscale);
        await _db.SaveChangesAsync();
        Dictionary<Guid, ModelCapabilityView> before = (await Resolvable()).ToDictionary(v => v.Id);

        ConsolidationReport report = await Upgrader().ApplyConsolidationAsync();

        report.Groups.Should().HaveCount(2);
        report.Groups.Should().ContainEquivalentOf(new ConsolidationGroup(anthChat.Id, "Anthropic", [anthVision.Id], 1));
        ConsolidationGroup falGroup = report.Groups.Single(g => g.SurvivorId != anthChat.Id);
        // The three fal rows were created at the same instant: the id breaks the tie, so any of them may survive.
        falGroup.MergedProviderIds.Append(falGroup.SurvivorId).Should().BeEquivalentTo([fal.Id, falErase.Id, falUpscale.Id]);
        falGroup.MergedModelCount.Should().Be(2);

        await using InferenceApiDbContext db = NewContext();
        InferenceProvider anthropic = await db.InferenceProviders.IgnoreQueryFilters().Include(p => p.Models).ThenInclude(m => m.Capabilities)
            .SingleAsync(p => p.Kind == InferenceProviderKind.Anthropic);
        anthropic.Id.Should().Be(anthChat.Id, "the oldest provider survives");
        anthropic.Models.Should().ContainSingle("the same model code with no capability collision merges into one model")
            .Which.Capabilities.Select(c => c.Id).Should().BeEquivalentTo([chat.Id, vision.Id]);
        anthropic.Models.Single().DisplayName.Should().Be("Anthropic", "the older model's display name wins");
        (await db.InferenceProviders.IgnoreQueryFilters().CountAsync(p => p.Kind == InferenceProviderKind.FalAi)).Should().Be(1);
        (await db.InferenceModels.IgnoreQueryFilters().CountAsync(m => m.Provider.Kind == InferenceProviderKind.FalAi)).Should().Be(3);

        // Capability rows never change, so every old id resolves exactly as before.
        Dictionary<Guid, ModelCapabilityView> after = (await Resolvable()).ToDictionary(v => v.Id);
        after.Keys.Should().BeEquivalentTo(before.Keys);
        foreach ((Guid id, ModelCapabilityView old) in before)
        {
            ModelCapabilityView now = after[id];
            (now.Kind, now.Capability, now.Endpoint.Trim(), now.ModelName, now.IsDefault, now.TimeoutSeconds)
                .Should().Be((old.Kind, old.Capability, old.Endpoint.Trim(), old.ModelName, old.IsDefault, old.TimeoutSeconds));
            _secrets.TryUnprotect(now.ApiKeyEncrypted, out string nowKey).Should().BeTrue();
            _secrets.TryUnprotect(old.ApiKeyEncrypted, out string oldKey).Should().BeTrue();
            nowKey.Should().Be(oldKey);
        }
    }

    private static readonly InferenceProviderCapability[] ImageToolCapabilities =
    [
        InferenceProviderCapability.ImageGeneration, InferenceProviderCapability.ImageEdit,
        InferenceProviderCapability.ImageErase, InferenceProviderCapability.ImageFill,
        InferenceProviderCapability.ImageExpand, InferenceProviderCapability.ImageBackgroundReplace,
        InferenceProviderCapability.ImageRelight, InferenceProviderCapability.ImageUpscale
    ];

    /// <summary>The fal row plus the eight image-tool rows copied from it (same ciphertext byte for byte).</summary>
    private (InferenceProvider Fal, List<InferenceProvider> Tools) FalWithEightTools(string? falHeaders = null, string? toolHeaders = null)
    {
        InferenceProvider fal = Account("fal", InferenceProviderKind.FalAi, "placeholder-fal", SeedFrom, endpoint: "https://fal.run",
            timeout: 90, headers: falHeaders);
        Capability(fal, "fal-ai/kling-video", InferenceProviderCapability.VideoGeneration, isDefault: true);
        List<InferenceProvider> tools = [];
        foreach (InferenceProviderCapability c in ImageToolCapabilities)
        {
            InferenceProvider tool = Account($"fal - {c}", InferenceProviderKind.FalAi, null, SeedFrom.AddDays(3), endpoint: "https://fal.run",
                timeout: 90, cipher: fal.ApiKeyEncrypted, headers: toolHeaders);
            Capability(tool, $"fal-ai/{c.ToString().ToLowerInvariant()}", c, isDefault: true);
            tools.Add(tool);
        }

        return (fal, tools);
    }

    /// <summary>QA-5: RF1 at the brief's scale: fal + eight image-tool rows become one provider with nine models.</summary>
    [Fact]
    public async Task Consolidation_FalRowPlusEightImageToolRows_BecomeOneProviderWithNineModels_AndEveryOldIdResolvesSame()
    {
        (InferenceProvider fal, List<InferenceProvider> tools) = FalWithEightTools();
        await _db.SaveChangesAsync();
        Dictionary<Guid, ModelCapabilityView> before = (await Resolvable()).ToDictionary(v => v.Id);
        before.Should().HaveCount(9);

        ConsolidationReport report = await Upgrader().ApplyConsolidationAsync();

        report.Groups.Should().ContainSingle().Which.SurvivorId.Should().Be(fal.Id, "the fal row is the oldest");
        report.Groups[0].MergedProviderIds.Should().BeEquivalentTo(tools.Select(t => t.Id));
        await using InferenceApiDbContext db = NewContext();
        (await db.InferenceProviders.IgnoreQueryFilters().CountAsync(p => !p.IsSystem)).Should().Be(1);
        (await db.InferenceModels.IgnoreQueryFilters().CountAsync()).Should().Be(9, "nine distinct model codes stay nine models");

        Dictionary<Guid, ModelCapabilityView> after = (await Resolvable()).ToDictionary(v => v.Id);
        after.Keys.Should().BeEquivalentTo(before.Keys);
        foreach ((Guid id, ModelCapabilityView old) in before)
        {
            ModelCapabilityView now = after[id];
            (now.Kind, now.Capability, now.Endpoint, now.ModelName, now.TimeoutSeconds, now.IsDefault)
                .Should().Be((old.Kind, old.Capability, old.Endpoint, old.ModelName, old.TimeoutSeconds, old.IsDefault));
            _secrets.TryUnprotect(now.ApiKeyEncrypted, out string key).Should().BeTrue();
            key.Should().Be("placeholder-fal");
        }

        (await Upgrader(NewContext()).ApplyConsolidationAsync()).Groups.Should().BeEmpty("idempotent");
    }

    /// <summary>
    /// QA-3: the pre-catalog CreateImageToolRowsAsync copied the fal row's ciphertext but not its extra headers. With
    /// byte-identical ciphertext the header difference is that copy gap, not a second account: one provider, and the
    /// survivor keeps the headers.
    /// </summary>
    [Fact]
    public async Task Consolidation_ByteIdenticalCiphertext_IgnoresMissingExtraHeaders_AndTheSurvivorKeepsThem()
    {
        const string headers = "{\"X-Fal-Region\":\"eu\"}";
        (InferenceProvider fal, List<InferenceProvider> tools) = FalWithEightTools(falHeaders: headers);
        await _db.SaveChangesAsync();

        ConsolidationReport report = await Upgrader().ApplyConsolidationAsync();

        report.Groups.Should().ContainSingle().Which.MergedProviderIds.Should().BeEquivalentTo(tools.Select(t => t.Id));
        await using InferenceApiDbContext db = NewContext();
        InferenceProvider survivor = await db.InferenceProviders.IgnoreQueryFilters().SingleAsync(p => !p.IsSystem);
        survivor.Id.Should().Be(fal.Id);
        survivor.ExtraHeadersJson.Should().Be(headers);
        (await db.InferenceModels.IgnoreQueryFilters().CountAsync()).Should().Be(9);
    }

    [Fact]
    public async Task Consolidation_ByteIdenticalCiphertext_AnOlderSurvivorWithoutHeadersGainsThem()
    {
        const string headers = "{\"X-Fal-Region\":\"eu\"}";
        // The tool row is older here and carries no headers; the fal row with headers is younger.
        InferenceProvider tool = Account("fal - Upscale", InferenceProviderKind.FalAi, "placeholder-fal", SeedFrom, endpoint: "https://fal.run");
        Capability(tool, "fal-ai/seedvr/upscale/image", InferenceProviderCapability.ImageUpscale);
        InferenceProvider fal = Account("fal", InferenceProviderKind.FalAi, null, SeedFrom.AddDays(1), endpoint: "https://fal.run",
            cipher: tool.ApiKeyEncrypted, headers: headers);
        Capability(fal, "fal-ai/flux-pro/kontext", InferenceProviderCapability.ImageEdit);
        await _db.SaveChangesAsync();

        ConsolidationReport report = await Upgrader().ApplyConsolidationAsync();

        report.Groups.Should().ContainEquivalentOf(new ConsolidationGroup(tool.Id, "fal - Upscale", [fal.Id], 1));
        await using InferenceApiDbContext db = NewContext();
        (await db.InferenceProviders.IgnoreQueryFilters().SingleAsync(p => !p.IsSystem)).ExtraHeadersJson.Should().Be(headers);
    }

    [Fact]
    public async Task Consolidation_SameKeyEncryptedSeparately_KeepsDifferentExtraHeadersApart()
    {
        // Same plaintext, different ciphertext (random IV): the headers were set deliberately, so they stay two accounts.
        InferenceProvider a = Account("fal", InferenceProviderKind.FalAi, "placeholder-fal", SeedFrom, endpoint: "https://fal.run",
            headers: "{\"X-Fal-Region\":\"eu\"}");
        Capability(a, "fal-ai/flux-pro/kontext", InferenceProviderCapability.ImageEdit);
        InferenceProvider b = Account("fal us", InferenceProviderKind.FalAi, "placeholder-fal", SeedFrom.AddDays(1), endpoint: "https://fal.run");
        Capability(b, "fal-ai/seedvr/upscale/image", InferenceProviderCapability.ImageUpscale);
        // Same ciphertext but two different non-null headers: not the copy gap either.
        InferenceProvider c = Account("fal ap", InferenceProviderKind.FalAi, null, SeedFrom.AddDays(2), endpoint: "https://fal.run",
            cipher: a.ApiKeyEncrypted, headers: "{\"X-Fal-Region\":\"ap\"}");
        Capability(c, "fal-ai/bria/expand", InferenceProviderCapability.ImageExpand);
        await _db.SaveChangesAsync();

        (await Upgrader().ApplyConsolidationAsync()).Groups.Should().BeEmpty();
    }

    [Fact]
    public async Task Consolidation_SkipsUndecryptableKeys()
    {
        string foreignCipher = new DataProtectionSecretProtector(new EphemeralDataProtectionProvider(),
            NullLogger<DataProtectionSecretProtector>.Instance).Protect("sk-ant-shared");
        InferenceProvider a = Account("A", InferenceProviderKind.Anthropic, null, SeedFrom, cipher: foreignCipher);
        ModelCapability aChat = Capability(a, "claude", InferenceProviderCapability.Chat);
        InferenceProvider b = Account("B", InferenceProviderKind.Anthropic, null, SeedFrom.AddDays(1), cipher: foreignCipher);
        Capability(b, "claude", InferenceProviderCapability.Vision);
        InferenceProvider c = Account("C", InferenceProviderKind.Anthropic, "sk-ant-shared", SeedFrom.AddDays(2));
        Capability(c, "claude", InferenceProviderCapability.Vision, display: "C2");
        await _db.SaveChangesAsync();

        (await Upgrader().PlanConsolidationAsync()).Groups.Should().BeEmpty();
        (await Upgrader().ApplyConsolidationAsync()).Groups.Should().BeEmpty(
            "a key the key ring cannot decrypt is never grouped, even with an identical ciphertext");

        await using InferenceApiDbContext db = NewContext();
        (await db.InferenceProviders.IgnoreQueryFilters().CountAsync()).Should().Be(3);
        (await db.InferenceProviders.IgnoreQueryFilters().SingleAsync(p => p.Id == a.Id)).ApiKeyEncrypted.Should().Be(foreignCipher,
            "the undecryptable key is never touched or lost");
        (await db.ModelCapabilities.IgnoreQueryFilters().SingleAsync(x => x.Id == aChat.Id)).ModelId.Should().Be(aChat.ModelId);
    }

    /// <summary>
    /// The T-100 boot crash: the survivor's OWN model is younger than a moved sibling with the same code, so it is the
    /// one merged away. It used to be removed from <c>survivor.Models</c> (EF deleted the orphan in the first save) and
    /// then removed again (the second save's DELETE matched no row: <c>DbUpdateConcurrencyException</c>).
    /// </summary>
    [Fact]
    public async Task Consolidation_TheSurvivorsOwnYoungerModel_MergesIntoTheMovedOne_WithoutBeingDeletedTwice()
    {
        InferenceProvider a = Account("A", InferenceProviderKind.OpenAICompatible, "k", SeedFrom);
        ModelCapability aVision = Capability(a, "m", InferenceProviderCapability.Vision, display: "A m", created: SeedFrom.AddDays(2));
        InferenceProvider b = Account("B", InferenceProviderKind.OpenAICompatible, "k", SeedFrom.AddDays(1));
        ModelCapability bChat = Capability(b, "m", InferenceProviderCapability.Chat, display: "B m", created: SeedFrom.AddDays(1));
        await _db.SaveChangesAsync();

        ConsolidationReport report = await Upgrader().ApplyConsolidationAsync();

        report.Groups.Should().ContainSingle().Which.Should().BeEquivalentTo(new ConsolidationGroup(a.Id, "A", [b.Id], 1));
        await using InferenceApiDbContext db = NewContext();
        InferenceModel model = (await db.InferenceModels.IgnoreQueryFilters().Include(m => m.Capabilities).ToListAsync())
            .Should().ContainSingle().Subject;
        (model.Id, model.ProviderId).Should().Be((bChat.ModelId, a.Id), "the older model wins, under the oldest provider");
        model.Capabilities.Select(c => c.Id).Should().BeEquivalentTo([aVision.Id, bChat.Id]);
        (await db.InferenceProviders.IgnoreQueryFilters().Select(p => p.Id).ToListAsync()).Should().Equal(a.Id);
    }

    [Fact]
    public async Task Consolidation_NeverMergesKeylessRows_OfOneKindAndEndpoint()
    {
        InferenceProvider whisper = Account("Whisper", InferenceProviderKind.OpenAICompatible, null, SeedFrom, endpoint: "http://whisper:8000");
        Capability(whisper, "whisper-1", InferenceProviderCapability.Transcription);
        InferenceProvider large = Account("Whisper large", InferenceProviderKind.OpenAICompatible, null, SeedFrom, endpoint: "http://whisper:8000",
            cipher: string.Empty);
        Capability(large, "large-v3", InferenceProviderCapability.Transcription);
        InferenceProvider chat = Account("Local chat", InferenceProviderKind.OpenAICompatible, null, SeedFrom, endpoint: "http://whisper:8000");
        Capability(chat, "whisper-1", InferenceProviderCapability.Chat, display: "Local chat model");
        await _db.SaveChangesAsync();

        (await Upgrader().PlanConsolidationAsync()).Groups.Should().BeEmpty();
        (await Upgrader().ApplyConsolidationAsync()).Groups.Should().BeEmpty(
            "no key is no account identity: key-less rows are never one account");
        await using InferenceApiDbContext db = NewContext();
        (await db.InferenceProviders.IgnoreQueryFilters().CountAsync()).Should().Be(3);
    }

    [Fact]
    public async Task Consolidation_NeverMergesAmbientCredentialRows()
    {
        InferenceProvider a = Account("Gemini", InferenceProviderKind.Gemini, "env:", SeedFrom);
        Capability(a, "gemini-3-pro", InferenceProviderCapability.Chat);
        InferenceProvider b = Account("Gemini vision", InferenceProviderKind.Gemini, "env:", SeedFrom.AddDays(1));
        Capability(b, "gemini-3-pro", InferenceProviderCapability.Vision);
        await _db.SaveChangesAsync();

        (await Upgrader().PlanConsolidationAsync()).Groups.Should().BeEmpty();
        (await Upgrader().ApplyConsolidationAsync()).Groups.Should().BeEmpty(
            "the ambient credential is not an account identity we can verify");
    }

    [Fact]
    public async Task Consolidation_KeepsDifferentTimeoutsApart()
    {
        InferenceProvider a = Account("A", InferenceProviderKind.OpenAICompatible, "k", SeedFrom, timeout: 30);
        Capability(a, "m", InferenceProviderCapability.Chat);
        InferenceProvider b = Account("B", InferenceProviderKind.OpenAICompatible, "k", SeedFrom, timeout: 60);
        Capability(b, "m", InferenceProviderCapability.Vision);
        InferenceProvider c = Account("C", InferenceProviderKind.OpenAICompatible, "k", SeedFrom, timeout: 30, endpoint: "https://other.example");
        Capability(c, "m", InferenceProviderCapability.Embedding);
        Organization org = new() { Id = Guid.NewGuid(), Name = "o", Slug = "o" };
        _db.Organizations.Add(org);
        InferenceProvider d = Account("D", InferenceProviderKind.OpenAICompatible, "k", SeedFrom, timeout: 30, org: org.Id);
        Capability(d, "m", InferenceProviderCapability.Decision);
        await _db.SaveChangesAsync();

        (await Upgrader().ApplyConsolidationAsync()).Groups.Should().BeEmpty(
            "timeout, endpoint and organization are part of the account; none of these four share one");
    }

    [Fact]
    public async Task Consolidation_MergesModelsWithoutCapabilityCollision()
    {
        InferenceProvider a = Account("A", InferenceProviderKind.OpenAICompatible, "k", SeedFrom);
        ModelCapability aChat = Capability(a, "gpt-x", InferenceProviderCapability.Chat, display: "GPT X");
        InferenceProvider b = Account("B", InferenceProviderKind.OpenAICompatible, "k", SeedFrom.AddDays(1));
        ModelCapability bChat = Capability(b, "gpt-x", InferenceProviderCapability.Chat, display: "GPT X again");
        InferenceProvider c = Account("C", InferenceProviderKind.OpenAICompatible, "k", SeedFrom.AddDays(2));
        ModelCapability cVision = Capability(c, "gpt-x", InferenceProviderCapability.Vision, display: "GPT X vision");
        InferenceProvider d = Account("D", InferenceProviderKind.OpenAICompatible, "k", SeedFrom.AddDays(3));
        ModelCapability dEmbed = Capability(d, "embed-x", InferenceProviderCapability.Embedding, display: "Embed");
        await _db.SaveChangesAsync();

        await Upgrader().ApplyConsolidationAsync();

        await using InferenceApiDbContext db = NewContext();
        List<InferenceModel> models = await db.InferenceModels.IgnoreQueryFilters().Include(m => m.Capabilities).ToListAsync();
        models.Should().OnlyContain(m => m.ProviderId == a.Id);
        models.Should().HaveCount(3, "two Chat capabilities collide, so those models stay apart");
        models.Single(m => m.Id == aChat.ModelId).Capabilities.Select(x => x.Id).Should().BeEquivalentTo([aChat.Id, cVision.Id]);
        models.Single(m => m.Id == bChat.ModelId).Capabilities.Select(x => x.Id).Should().BeEquivalentTo([bChat.Id]);
        models.Single(m => m.Id == dEmbed.ModelId).DisplayName.Should().Be("Embed");
    }

    [Fact]
    public async Task Consolidation_KeepsADisabledMembersCapabilitiesDisabled()
    {
        InferenceProvider on = Account("On", InferenceProviderKind.OpenAICompatible, "k", SeedFrom);
        ModelCapability onChat = Capability(on, "m", InferenceProviderCapability.Chat);
        InferenceProvider off = Account("Off", InferenceProviderKind.OpenAICompatible, "k", SeedFrom.AddDays(-1), enabled: false);
        ModelCapability offVision = Capability(off, "m", InferenceProviderCapability.Vision);
        await _db.SaveChangesAsync();

        ConsolidationReport report = await Upgrader().ApplyConsolidationAsync();

        report.Groups.Should().ContainSingle().Which.SurvivorId.Should().Be(off.Id, "the oldest survives, even a disabled one");
        await using InferenceApiDbContext db = NewContext();
        (await db.InferenceProviders.IgnoreQueryFilters().SingleAsync()).IsEnabled.Should().BeTrue("a member was enabled");
        (await Resolvable()).Select(v => v.Id).Should().BeEquivalentTo([onChat.Id],
            "what was off stays off: the disabled member's model is disabled instead of its provider");
        (await db.ModelCapabilities.IgnoreQueryFilters().SingleAsync(x => x.Id == offVision.Id)).ModelId.Should().NotBe(onChat.ModelId,
            "an enabled and a disabled model do not merge");
    }

    [Fact]
    public async Task Consolidation_IsIdempotent()
    {
        InferenceProvider a = Account("A", InferenceProviderKind.Anthropic, "k", SeedFrom);
        Capability(a, "claude", InferenceProviderCapability.Chat);
        InferenceProvider b = Account("B", InferenceProviderKind.Anthropic, "k", SeedFrom.AddDays(1));
        Capability(b, "claude", InferenceProviderCapability.Vision);
        await _db.SaveChangesAsync();

        (await Upgrader().ApplyConsolidationAsync()).Groups.Should().ContainSingle();
        (await Upgrader(NewContext()).ApplyConsolidationAsync()).Groups.Should().BeEmpty();
        (await Upgrader(NewContext()).PlanConsolidationAsync()).Groups.Should().BeEmpty();
    }

    [Fact]
    public async Task Consolidation_DryRunWritesNothing()
    {
        InferenceProvider a = Account("A", InferenceProviderKind.Anthropic, "k", SeedFrom);
        Capability(a, "claude", InferenceProviderCapability.Chat);
        InferenceProvider b = Account("B", InferenceProviderKind.Anthropic, "k", SeedFrom.AddDays(1));
        Capability(b, "claude", InferenceProviderCapability.Vision);
        await _db.SaveChangesAsync();

        ModelCatalogUpgradeResult result = await Upgrader(consolidation: "DryRun").RunAsync();

        result.Consolidated.Groups.Should().BeEmpty("a dry run reports, it does not merge");
        (await Upgrader(NewContext()).PlanConsolidationAsync()).Groups.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new ConsolidationGroup(a.Id, "A", [b.Id], 1));
        await using InferenceApiDbContext db = NewContext();
        (await db.InferenceProviders.IgnoreQueryFilters().CountAsync(p => !p.IsSystem)).Should().Be(2);
        (await db.InferenceModels.IgnoreQueryFilters().CountAsync(m => !m.Provider.IsSystem)).Should().Be(2);

        (await Upgrader(NewContext(), consolidation: "Off").RunAsync()).Consolidated.Groups.Should().BeEmpty();
        (await db.InferenceProviders.IgnoreQueryFilters().CountAsync(p => !p.IsSystem)).Should().Be(2);
    }

    [Theory]
    [InlineData(null, ConsolidationMode.Apply, false)]
    [InlineData("", ConsolidationMode.Apply, false)]
    [InlineData("apply", ConsolidationMode.Apply, false)]
    [InlineData("DryRun", ConsolidationMode.DryRun, false)]
    [InlineData("off", ConsolidationMode.Off, false)]
    [InlineData("Aply", ConsolidationMode.DryRun, true)]
    [InlineData("7", ConsolidationMode.DryRun, true)]
    public void Mode_AnUnknownValue_FallsBackToTheDryRun_WithAWarning(string? configured, ConsolidationMode expected, bool warns)
    {
        CapturingLogger logger = new();

        Upgrader(consolidation: configured, logger: logger).Mode.Should().Be(expected);

        logger.Entries.Any(e => e.Level == LogLevel.Warning && e.Message.Contains(ModelCatalogUpgrader.ConsolidationKey))
            .Should().Be(warns);
    }

    [Fact]
    public async Task RunAsync_WarnsOnceAboutVideoGenerationRowsThatNameATalkingHeadModelAndWritesNothing()
    {
        InferenceProvider fal = Account("fal", InferenceProviderKind.FalAi, "fal-key", SeedFrom);
        ModelCapability stale = Capability(fal, "minimax/h3-max/lip-sync/image-to-video", InferenceProviderCapability.VideoGeneration);
        Capability(fal, "fal-ai/kling-video/ai-avatar/v2/standard", InferenceProviderCapability.LipSync);
        Capability(fal, "fal-ai/kling-video/v3/turbo/pro/image-to-video", InferenceProviderCapability.VideoGeneration);
        await _db.SaveChangesAsync();
        CapturingLogger logger = new();
        int before = await _db.ModelCapabilities.CountAsync(c => c.Capability != InferenceProviderCapability.Compute);

        await Upgrader(logger: logger).RunAsync();

        var warnings = logger.Entries.Where(e => e.Level == LogLevel.Warning && e.Message.Contains("Lip-sync", StringComparison.Ordinal)).ToList();
        warnings.Should().ContainSingle();
        warnings[0].Message.Should().Contain("1 VideoGeneration").And.Contain(stale.Id.ToString());
        (await _db.ModelCapabilities.CountAsync(c => c.Capability != InferenceProviderCapability.Compute)).Should().Be(before, "the boot check is read-only");

        // Nothing stale: nothing logged.
        await using (InferenceApiDbContext other = NewContext())
            await other.ModelCapabilities.Where(c => c.Id == stale.Id).ExecuteDeleteAsync();
        CapturingLogger quiet = new();
        await Upgrader(logger: quiet).RunAsync();
        quiet.Entries.Should().NotContain(e => e.Message.Contains("Lip-sync", StringComparison.Ordinal));
    }

    private sealed class ThrowOnStaleLipSyncQuery : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        public int Thrown { get; private set; }
        public List<string> Seen { get; } = [];

        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command, Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> result, System.Threading.CancellationToken ct = default)
        {
            Seen.Add(command.CommandText);
            // The boot check is the only query that selects just a capability id and its model code, with no price or owner column.
            string sql = command.CommandText;
            if (sql.Contains("ModelCode", StringComparison.Ordinal) && !sql.Contains("Price", StringComparison.Ordinal)
                && !sql.Contains("OrganizationId", StringComparison.Ordinal) && !sql.Contains("ProviderId", StringComparison.Ordinal))
            {
                Thrown++;
                throw new InvalidOperationException("boom");
            }

            return ValueTask.FromResult(result);
        }
    }

    [Fact]
    public async Task RunAsync_DoesNotFailBoot_WhenTheStaleLipSyncCheckThrows()
    {
        ThrowOnStaleLipSyncQuery interceptor = new();
        await using InferenceApiDbContext db = new(new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseSqlite(_connection).AddInterceptors(interceptor).Options);
        CapturingLogger logger = new();

        Func<Task> run = () => Upgrader(db: db, logger: logger).RunAsync();

        await run.Should().NotThrowAsync();
        interceptor.Thrown.Should().BeGreaterThan(0, string.Join("\n----\n", interceptor.Seen));
        logger.Entries.Should().Contain(e => e.Level == LogLevel.Error && e.Message.Contains("stale lip-sync row check failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_WithAMistypedMode_MergesNothing()
    {
        InferenceProvider a = Account("A", InferenceProviderKind.Anthropic, "k", SeedFrom);
        Capability(a, "claude", InferenceProviderCapability.Chat);
        InferenceProvider b = Account("B", InferenceProviderKind.Anthropic, "k", SeedFrom.AddDays(1));
        Capability(b, "claude", InferenceProviderCapability.Vision);
        await _db.SaveChangesAsync();

        (await Upgrader(consolidation: "Of").RunAsync()).Consolidated.Groups.Should().BeEmpty("a typo must not merge accounts");

        await using InferenceApiDbContext db = NewContext();
        (await db.InferenceProviders.IgnoreQueryFilters().CountAsync(p => !p.IsSystem)).Should().Be(2);
    }

    // ------------------------------------------------------------------ the whole run

    [Fact]
    public async Task RunAsync_IsIdempotent_AndInvalidatesTheCachesOnlyWhenItChangedSomething()
    {
        InferenceProvider a = Account("A", InferenceProviderKind.Anthropic, "k", SeedFrom);
        Capability(a, "claude-sonnet-5-5", InferenceProviderCapability.Chat);
        InferenceProvider b = Account("B", InferenceProviderKind.Anthropic, "k", SeedFrom.AddDays(1));
        Capability(b, "claude-sonnet-5-5", InferenceProviderCapability.Vision);
        InferenceProvider mm = Account("MiniMax", InferenceProviderKind.MiniMax, "mm", SeedFrom);
        Capability(mm, "MiniMax-H3", InferenceProviderCapability.VideoGeneration);
        _db.RateCards.AddRange(LegacyRateCardSeed.Cards(SeedFrom).Concat(LegacyRateCardSeed.ComputeCards(SeedFrom)));
        await _db.SaveChangesAsync();
        Mock<IInferenceProviderResolver> resolver = new();
        Mock<IModelPriceService> prices = new();

        ModelCatalogUpgradeResult first = await Upgrader(resolver: resolver.Object, prices: prices.Object).RunAsync();

        first.ComputeProviderCreated.Should().BeTrue();
        first.PricesStamped.Should().BeTrue();
        first.VideoVariantsAdded.Should().Be(2);
        first.Consolidated.Groups.Should().ContainSingle();
        resolver.Verify(r => r.InvalidateAll(), Times.Once);
        prices.Verify(p => p.Invalidate(), Times.Once);
        (await PriceCoverage.FindAsync(NewContext(), DateTime.UtcNow)).Should().BeEmpty("every platform capability is priced");

        ModelCatalogUpgradeResult second = await Upgrader(NewContext(), resolver: resolver.Object, prices: prices.Object).RunAsync();

        second.Changed.Should().BeFalse();
        resolver.Verify(r => r.InvalidateAll(), Times.Once);
        prices.Verify(p => p.Invalidate(), Times.Once);
    }

    // ------------------------------------------------------------------ default multipliers (moved out of the rate-card seed)

    [Fact]
    public async Task DefaultMultipliers_AreSeededOnlyIntoAnEmptyTable()
    {
        await DatabaseSeeder.SeedDefaultRateMultipliersAsync(_db);

        await using (InferenceApiDbContext db = NewContext())
        {
            (await db.RateMultipliers.AsNoTracking().ToListAsync())
                .Select(m => (m.BillingMode, m.UsageKind, m.Multiplier, m.EffectiveTo))
                .Should().BeEquivalentTo(new (UsageBillingMode, UsageKind?, decimal, DateTime?)[]
                {
                    (UsageBillingMode.Platform, null, 1m, null),
                    (UsageBillingMode.Byo, null, 0m, null),
                    (UsageBillingMode.Runner, null, 0m, null)
                }, "the defaults the price service assumes without a row, made visible to the admin");
        }

        await using (InferenceApiDbContext db = NewContext())
        {
            RateMultiplier edited = await db.RateMultipliers.SingleAsync(m => m.BillingMode == UsageBillingMode.Byo);
            edited.Multiplier = 0.1m;
            await db.SaveChangesAsync();
            await DatabaseSeeder.SeedDefaultRateMultipliersAsync(db);
        }

        await using InferenceApiDbContext check = NewContext();
        (await check.RateMultipliers.CountAsync()).Should().Be(3, "a second boot adds nothing");
        (await check.RateMultipliers.SingleAsync(m => m.BillingMode == UsageBillingMode.Byo)).Multiplier
            .Should().Be(0.1m, "an admin's multiplier is never overwritten");
    }

    /// <summary>Loads a price snapshot straight from the test database.</summary>
    private sealed class SnapshotOf(Func<InferenceApiDbContext> context) : IModelPriceSource
    {
        public async Task<ModelPriceSnapshot> LoadAsync(System.Threading.CancellationToken ct)
        {
            await using InferenceApiDbContext db = context();
            return new ModelPriceSnapshot(
                await db.ModelCapabilities.IgnoreQueryFilters().Select(c => new PricedCapability(
                    c.Id, c.Model.ProviderId, c.OrganizationId, c.Model.Provider.Kind, c.Capability, c.Model.ModelCode)).ToListAsync(ct),
                await db.ModelCapabilityPrices.AsNoTracking().ToListAsync(ct),
                await db.RateMultipliers.AsNoTracking().ToListAsync(ct));
        }
    }
}
