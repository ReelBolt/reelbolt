#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Metering;

/// <summary>
/// The guard that keeps "remember to price new models" true without anyone remembering it (rewrites the old
/// <c>RateCardCoverageTests</c> against the model catalog, docs/design/model-catalog.md §5.5).
/// </summary>
/// <remarks>
/// The failure mode is specific and quiet: <see cref="ModelPriceService.Price"/> throws
/// <see cref="UnpricedUsageException"/> for a Platform event, <c>UsageRecorder</c> writes the row anyway with
/// <c>credits_millis = 0</c> and no <c>price_id</c>, and the work is then free in the ledger with only a log line.
/// </remarks>
public class PriceCoverageTests
{
    private static readonly DateTime At = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Past = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // ---------------------------------------------------------------- the capability matrix

    /// <summary>
    /// The capability table in <c>ReelBolt.Shared</c> and the admin boundary in the Inference API are two statements of
    /// one rule. This test makes adding a provider kind or a capability a two-file change, whichever file you start in.
    /// </summary>
    [Fact]
    public void Capability_table_agrees_with_the_admin_boundary_for_every_pair()
    {
        List<string> disagreements = [];
        foreach (InferenceProviderKind kind in ProviderCapabilityUsage.Kinds)
        {
            foreach (InferenceProviderCapability capability in ProviderCapabilityUsage.Capabilities)
            {
                bool rejected = InferenceProvidersController.IsUnsupportedCombination(kind, capability, out _);
                bool servableHere = ProviderCapabilityUsage.ServableByKind[kind].Contains(capability);
                if (rejected != servableHere) continue;

                disagreements.Add(
                    kind + "/" + capability + ": the API " + (rejected ? "rejects" : "allows") +
                    " it, but ProviderCapabilityUsage says it is " + (servableHere ? "servable" : "not servable"));
            }
        }

        disagreements.Should().BeEmpty();
    }

    [Fact]
    public void Every_capability_declares_at_least_one_unit()
    {
        foreach (InferenceProviderCapability capability in ProviderCapabilityUsage.Capabilities)
        {
            ProviderCapabilityUsage.ByCapability.Should().ContainKey(capability);
            ProviderCapabilityUsage.ByCapability[capability].Should().NotBeEmpty(
                capability + " produces no billable unit, so no price could ever cover it");
        }
    }

    [Fact]
    public void Compute_meters_the_three_compute_units()
    {
        ProviderCapabilityUsage.ByCapability[InferenceProviderCapability.Compute].Should().BeEquivalentTo(PricingDefaults.ComputeUnits);
        ProviderCapabilityUsage.DefaultCapabilityFor(UsageKind.RenderOutputSeconds).Should().Be(InferenceProviderCapability.Compute);
        ProviderCapabilityUsage.DefaultCapabilityFor(UsageKind.LlmInputTokens).Should().Be(InferenceProviderCapability.Chat);
        ProviderCapabilityUsage.DefaultCapabilityFor(UsageKind.ImageEditImages).Should().Be(InferenceProviderCapability.ImageEdit);
    }

    /// <summary>
    /// Every capability must be served by some kind, or the enum has grown a member nothing serves. Compute is the one
    /// deliberate exception: only the system compute provider serves it (design §2.3), and every kind refuses it.
    /// </summary>
    [Fact]
    public void Every_capability_is_served_by_some_provider_kind()
    {
        foreach (InferenceProviderKind kind in ProviderCapabilityUsage.Kinds)
        {
            ProviderCapabilityUsage.ServableByKind[kind].Should().NotContain(InferenceProviderCapability.Compute);
        }

        foreach (InferenceProviderCapability capability in ProviderCapabilityUsage.Capabilities
                     .Where(c => c != InferenceProviderCapability.Compute))
        {
            ProviderCapabilityUsage.ServableByKind.Values.Should().Contain(
                capabilities => capabilities.Contains(capability),
                "no provider kind serves " + capability);
        }
    }

    [Fact]
    public void ContentModeration_is_served_only_by_OpenAICompatible()
    {
        ProviderCapabilityUsage.ServableByKind[InferenceProviderKind.OpenAICompatible]
            .Should().Contain(InferenceProviderCapability.ContentModeration);

        foreach (InferenceProviderKind kind in ProviderCapabilityUsage.Kinds
                     .Where(k => k != InferenceProviderKind.OpenAICompatible))
        {
            InferenceProvidersController
                .IsUnsupportedCombination(kind, InferenceProviderCapability.ContentModeration, out _)
                .Should().BeTrue($"{kind} must not serve ContentModeration");
        }
    }

    [Fact]
    public void Every_provider_kind_is_accounted_for()
    {
        foreach (InferenceProviderKind kind in ProviderCapabilityUsage.Kinds)
        {
            ProviderCapabilityUsage.ServableByKind.Should().ContainKey(kind, kind + " has no entry, so nothing says what it must price");
        }
    }

    // ---------------------------------------------------------------- the price book

    [Fact]
    public void The_price_book_suggests_every_published_price_for_its_own_capability()
    {
        List<string> missing = [];
        foreach (ModelPrice entry in ModelPriceCatalog.Priced)
        {
            (string code, string? tier) = MeteringImageClient.SplitBillingKey(entry.Model);
            IReadOnlyList<SuggestedPrice> suggestions = PriceBook.SuggestFor(entry.ProviderKind, entry.Capability, code);
            foreach ((UsageKind unit, decimal usd) in entry.UsdPerUnit)
            {
                if (!suggestions.Any(s => s.Unit == unit && s.Variant == tier && s.CostUsdPerUnit == usd))
                    missing.Add($"{entry.ProviderKind}/{entry.Capability}/{entry.Model}/{unit}");
            }
        }

        missing.Should().BeEmpty();
    }

    /// <summary>The staging ledger's old unpriced row: Fish Audio transcription is a published price in the book.</summary>
    [Fact]
    public void FishAudio_transcription_uses_the_published_per_second_rate()
    {
        SuggestedPrice price = PriceBook.SuggestFor(InferenceProviderKind.FishAudio, InferenceProviderCapability.Transcription,
            "transcribe-1-pro").Single();

        price.Unit.Should().Be(UsageKind.AsrAudioSeconds);
        price.CostUsdPerUnit.Should().Be(0.36m / 3600m, "$0.36 per audio hour, the vendor's published ASR price");
        price.Notes.Should().Contain("docs.fish.audio").And.Contain("published vendor list price");
    }

    [Fact]
    public void A_vision_capability_is_priced_from_its_models_entries_and_an_erase_capability_from_the_edit_entries()
    {
        PriceBook.SuggestFor(InferenceProviderKind.Anthropic, InferenceProviderCapability.Vision, "claude-sonnet-5-5")
            .Select(s => (s.Unit, s.CostUsdPerUnit)).Should().BeEquivalentTo(new[]
            {
                (UsageKind.LlmInputTokens, 2m / 1_000_000m),
                (UsageKind.LlmOutputTokens, 10m / 1_000_000m),
                (UsageKind.LlmCachedInputTokens, 0.10m / 1_000_000m)
            });
        PriceBook.SuggestFor(InferenceProviderKind.FalAi, InferenceProviderCapability.ImageErase, "fal-ai/object-removal/mask")
            .Select(s => s.Variant).Should().BeEquivalentTo(["low_quality", "medium_quality", "high_quality", "best_quality"]);
    }

    [Fact]
    public void The_two_talking_head_models_are_priced_under_LipSync_in_the_same_unit_and_no_longer_under_VideoGeneration()
    {
        string[] ids = [.. FalVideoCatalog.Instance.IdsFor(FalVideoJob.TalkingHead).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)];
        ids.Should().HaveCount(2);
        foreach (string id in ids)
        {
            ModelPriceCatalog.Priced.Where(e => e.ProviderKind == InferenceProviderKind.FalAi && e.Model == id)
                .Should().ContainSingle(id).Which.Capability.Should().Be(InferenceProviderCapability.LipSync);
            ModelPriceCatalog.Priced.Single(e => e.ProviderKind == InferenceProviderKind.FalAi && e.Model == id)
                .UsdPerUnit.Keys.Should().Equal(UsageKind.VideoGenSeconds);
            PriceBook.SuggestFor(InferenceProviderKind.FalAi, InferenceProviderCapability.LipSync, id)
                .Should().NotBeEmpty("an admin who adds the row under Lip-sync gets the published prices pre-filled");
        }
    }

    [Fact]
    public void Video_suggestions_carry_one_variant_per_resolution_of_the_static_table()
    {
        PriceBook.SuggestFor(InferenceProviderKind.MiniMax, InferenceProviderCapability.VideoGeneration, "MiniMax-H3")
            .Where(s => s.Variant is not null)
            .Select(s => (s.Variant, s.CostUsdPerUnit)).Should().BeEquivalentTo(new[] { ("768P", 0.08m), ("2K", 0.13m) });
        PriceBook.SuggestFor(InferenceProviderKind.Higgsfield, InferenceProviderCapability.VideoGeneration, "dop-turbo")
            .Should().BeEmpty("Higgsfield is oracle-priced and publishes no table");
        PriceBook.SuggestFor(InferenceProviderKind.MiniMax, InferenceProviderCapability.VideoGeneration, "no-such-model")
            .Should().BeEmpty();
    }

    [Fact]
    public void Every_suggestion_carries_the_placeholder_marker_and_is_unique_per_unit_and_variant()
    {
        foreach (ModelPrice entry in ModelPriceCatalog.Priced)
        {
            (string code, _) = MeteringImageClient.SplitBillingKey(entry.Model);
            IReadOnlyList<SuggestedPrice> suggestions = PriceBook.SuggestFor(entry.ProviderKind, entry.Capability, code);
            suggestions.Should().OnlyContain(s => s.Notes.Contains(PricingDefaults.PlaceholderMarker),
                "the marker keeps the load-test invoice comparison lane BLOCKED — no figure is reconciled");
            suggestions.Select(s => (s.Unit, s.Variant)).Should().OnlyHaveUniqueItems();
        }

        PricingDefaults.ComputePrices().Should().OnlyContain(p => p.Notes.Contains(PricingDefaults.PlaceholderMarker));
    }

    [Fact]
    public void Every_unpriced_catalogue_entry_states_why_it_is_unpriced()
    {
        ModelPriceCatalog.Unpriced.Should().NotBeEmpty();
        foreach (ModelPrice model in ModelPriceCatalog.Unpriced)
        {
            bool saysWhy = model.Basis.Contains("no published price", StringComparison.OrdinalIgnoreCase)
                || model.Basis.Contains("self-hosted", StringComparison.OrdinalIgnoreCase);
            saysWhy.Should().BeTrue("every unpriced entry must say why it could not be priced: " + model.Basis);
        }
    }

    [Fact]
    public void Every_priced_catalogue_entry_prices_a_unit_its_capability_meters()
    {
        foreach (ModelPrice model in ModelPriceCatalog.Priced)
        {
            foreach ((UsageKind kind, decimal usd) in model.UsdPerUnit)
            {
                ProviderCapabilityUsage.ByCapability[model.Capability].Should().Contain(kind,
                    model.Model + " is a " + model.Capability + " entry but declares a price for " + kind);
                usd.Should().BeGreaterThanOrEqualTo(0m, "a negative price would mint credits");
            }
        }
    }

    [Fact]
    public void A_free_tier_is_priced_at_zero_rather_than_left_unpriced()
    {
        Guid capabilityId = Guid.NewGuid();
        PricedCapability capability = new(capabilityId, Guid.NewGuid(), null, InferenceProviderKind.FishAudio,
            InferenceProviderCapability.SpeechSynthesis, "s2.1-pro-free");
        ModelPriceSnapshot snapshot = new([capability],
            [.. PriceBook.SuggestFor(capability.Kind, capability.Capability, capability.ModelCode)
                .Select(s => PriceBook.ToRow(capabilityId, s, Past))], []);

        PriceQuote quote = ModelPriceService.Price(snapshot, new PriceRequest(capabilityId, null, capability.Kind,
            capability.Capability, "s2.1-pro-free", UsageKind.TtsInputBytes, null, UsageBillingMode.Platform, 1_000m), At);

        quote.PriceId.Should().NotBeNull("the free tier is a published price, so the row is priced, not unpriced");
        quote.CreditsMillis.Should().Be(0L);
        quote.CostUsd.Should().Be(0m);
    }

    // ---------------------------------------------------------------- the coverage check

    private static CoverageCapability Cap(
        InferenceProviderCapability capability, ModelPriceSource source = ModelPriceSource.Table) =>
        new(Guid.NewGuid(), "Acct", "Model " + capability, InferenceProviderKind.OpenAICompatible, capability, source);

    private static ModelCapabilityPrice Price(CoverageCapability cap, UsageKind unit, DateTime? to = null, string? variant = null) => new()
    {
        Id = Guid.NewGuid(),
        ModelCapabilityId = cap.Id,
        Unit = unit,
        Variant = variant,
        CostUsdPerUnit = 0.001m,
        EffectiveFrom = Past,
        EffectiveTo = to
    };

    [Fact]
    public void Coverage_names_each_unit_a_capability_meters_but_has_no_effective_price_for()
    {
        CoverageCapability chat = Cap(InferenceProviderCapability.Chat);
        CoverageCapability asr = Cap(InferenceProviderCapability.Transcription);
        CoverageCapability compute = Cap(InferenceProviderCapability.Compute);
        CoverageCapability oracle = Cap(InferenceProviderCapability.VideoGeneration, ModelPriceSource.Oracle);

        IReadOnlyList<PriceCoverageGap> gaps = PriceCoverage.Find(
            [chat, asr, compute, oracle],
            [
                Price(chat, UsageKind.LlmInputTokens), Price(chat, UsageKind.LlmOutputTokens),
                // Retiring a price is exactly the admin action that re-opens the hole.
                Price(chat, UsageKind.LlmCachedInputTokens, to: At.AddDays(-1)),
                Price(asr, UsageKind.AsrAudioSeconds),
                Price(compute, UsageKind.RenderOutputSeconds, variant: "720p")
            ],
            At);

        gaps.Should().HaveCount(2, "the oracle capability is exempt and transcription is fully priced");
        gaps.Single(g => g.CapabilityId == chat.Id).MissingUnits.Should().Equal(UsageKind.LlmCachedInputTokens);
        gaps.Single(g => g.CapabilityId == compute.Id).MissingUnits
            .Should().Equal(UsageKind.EncodeOutputSeconds, UsageKind.AnalyzeMediaSeconds);
    }

    [Fact]
    public async Task Coverage_over_a_database_checks_enabled_platform_capabilities_only()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        await using InferenceApiDbContext db = new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        Organization org = new() { Id = Guid.NewGuid(), Name = "o", Slug = "o" };
        db.Organizations.Add(org);

        ModelCapability platform = db.AddFlat(new ModelCapabilityView
        {
            Name = "Platform chat",
            Kind = InferenceProviderKind.OpenAICompatible,
            Capability = InferenceProviderCapability.Chat,
            ModelName = "gpt-x",
            Endpoint = "https://x"
        });
        db.AddFlat(new ModelCapabilityView
        {
            Name = "Disabled",
            Kind = InferenceProviderKind.OpenAICompatible,
            Capability = InferenceProviderCapability.Chat,
            ModelName = "gpt-y",
            Endpoint = "https://x",
            IsEnabled = false
        });
        db.AddFlat(new ModelCapabilityView
        {
            OrganizationId = org.Id,
            Name = "Byo",
            Kind = InferenceProviderKind.OpenAICompatible,
            Capability = InferenceProviderCapability.Chat,
            ModelName = "gpt-z",
            Endpoint = "https://x"
        });
        await db.SaveChangesAsync();

        IReadOnlyList<PriceCoverageGap> gaps = await PriceCoverage.FindAsync(db, At);

        gaps.Should().ContainSingle().Which.CapabilityId.Should().Be(platform.Id);
        gaps[0].ModelDisplayName.Should().Be("Platform chat");
    }
}
