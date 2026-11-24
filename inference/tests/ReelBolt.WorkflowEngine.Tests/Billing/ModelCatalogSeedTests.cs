#nullable enable

using AwesomeAssertions;
using ReelBolt.Inference.Api.Billing;
using ReelBolt.Shared.Data.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Billing;

/// <summary>
/// The typed loader and the validation guard for <c>model-catalog.seed.json</c> (T-015a): the committed
/// seed loads and is valid, every Validate() rule rejects its own bad input, and the lookups answer by
/// exact model code and effective date.
/// </summary>
public sealed class ModelCatalogSeedTests
{
    private static readonly DateTimeOffset AtRetrieval = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    // ------------------------------------------------------------------ the committed seed

    [Fact]
    public void CommittedSeed_IsValid()
    {
        ModelCatalogSeed seed = ModelCatalogSeed.LoadDefault();

        seed.SchemaVersion.Should().Be(1);
        seed.Rows.Should().NotBeEmpty();
        seed.Capabilities.Should().NotBeEmpty();
        seed.Validate().Should().BeEmpty();
    }

    [Fact]
    public void SuggestFor_PicksRowEffectiveAtDate()
    {
        ModelCatalogSeed seed = ModelCatalogSeed.LoadDefault();

        IReadOnlyList<SeedPriceRow> before = seed.SuggestFor(InferenceProviderKind.Gemini, "Chat", "gemini-3.8-flash",
            new DateTimeOffset(2026, 12, 31, 23, 0, 0, TimeSpan.Zero));
        IReadOnlyList<SeedPriceRow> after = seed.SuggestFor(InferenceProviderKind.Gemini, "Chat", "gemini-3.8-flash",
            new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero));

        before.Should().NotBeEmpty();
        after.Should().NotBeEmpty();
        before.Should().OnlyContain(r => r.EffectiveFrom <= new DateOnly(2026, 12, 31)
            && (r.EffectiveTo == null || r.EffectiveTo > new DateOnly(2026, 12, 31)));
        after.Should().OnlyContain(r => r.EffectiveFrom <= new DateOnly(2027, 1, 1)
            && (r.EffectiveTo == null || r.EffectiveTo > new DateOnly(2027, 1, 1)));
        SeedPriceRow inputBefore = before.Single(r => r.Unit == UsageKind.LlmInputTokens && r.Variant == null);
        SeedPriceRow inputAfter = after.Single(r => r.Unit == UsageKind.LlmInputTokens && r.Variant == null);
        inputBefore.CostUsdPerUnit.Should().NotBe(inputAfter.CostUsdPerUnit);
    }

    [Fact]
    public void SuggestFor_ComparesOnTheUtcDate_NotTheOffsetsLocalDate()
    {
        ModelCatalogSeed seed = ModelCatalogSeed.LoadDefault();
        decimal Input(DateTimeOffset at) => seed.SuggestFor(InferenceProviderKind.Gemini, "Chat", "gemini-3.8-flash", at)
            .Single(r => r.Unit == UsageKind.LlmInputTokens && r.Variant == null).CostUsdPerUnit;
        decimal price2026 = Input(new DateTimeOffset(2026, 12, 31, 12, 0, 0, TimeSpan.Zero));
        decimal price2027 = Input(new DateTimeOffset(2027, 1, 1, 12, 0, 0, TimeSpan.Zero));

        // 00:30 on 2027-01-01 at +02:00 is 22:30 on 2026-12-31 UTC: still the 2026 price.
        Input(new DateTimeOffset(2027, 1, 1, 0, 30, 0, TimeSpan.FromHours(2))).Should().Be(price2026);
        // 23:30 on 2026-12-31 at -05:00 is 04:30 on 2027-01-01 UTC: already the 2027 price.
        Input(new DateTimeOffset(2026, 12, 31, 23, 30, 0, TimeSpan.FromHours(-5))).Should().Be(price2027);
        price2026.Should().NotBe(price2027);
    }

    [Fact]
    public void CommittedSeed_CarriesModelNotes()
    {
        ModelCatalogSeed seed = ModelCatalogSeed.LoadDefault();

        List<SeedCapabilityEntry> sol = seed.Capabilities.Where(c => c.ModelCode == "gpt-5.6-sol").ToList();
        sol.Should().NotBeEmpty();
        sol.Should().OnlyContain(c => c.ModelNotes != null && c.ModelNotes.Contains("at least through November 21, 2026"));
    }

    [Fact]
    public void SuggestFor_OrdersByUnitThenVariantNullFirst_AndMatchesModelCodeExactly()
    {
        ModelCatalogSeed seed = ModelCatalogSeed.LoadDefault();

        IReadOnlyList<SeedPriceRow> rows = seed.SuggestFor(InferenceProviderKind.DeepSeek, "Chat", "deepseek-flash", AtRetrieval);

        rows.Select(r => (r.Unit, r.Variant)).Should().Equal(
            rows.OrderBy(r => r.Unit).ThenBy(r => r.Variant != null).ThenBy(r => r.Variant, StringComparer.Ordinal)
                .Select(r => (r.Unit, r.Variant)));
        rows.Where(r => r.Unit == UsageKind.LlmInputTokens).Select(r => r.Variant).First().Should().BeNull();
        seed.SuggestFor(InferenceProviderKind.DeepSeek, "Chat", "DeepSeek-Flash", AtRetrieval).Should().BeEmpty();
        seed.SuggestFor(InferenceProviderKind.DeepSeek, "Chat", "deepseek-fla", AtRetrieval).Should().BeEmpty();
        seed.Find(InferenceProviderKind.DeepSeek, "Chat", "DeepSeek-Flash").Should().BeNull();
    }

    [Theory]
    [InlineData(InferenceProviderKind.MiniMax, "VideoGeneration", "MiniMax-H3", "768P")]
    [InlineData(InferenceProviderKind.DeepSeek, "Chat", "deepseek-flash", null)]
    [InlineData(InferenceProviderKind.FishAudio, "SpeechSynthesis", "s2.1-pro", null)]
    [InlineData(InferenceProviderKind.FalAi, "ImageEdit", "google/nano-banana-2.1/edit", null)]
    [InlineData(InferenceProviderKind.FalAi, "Segmentation", "fal-ai/sam-3-1/image", null)]
    [InlineData(InferenceProviderKind.OpenAICompatible, "Embedding", "@cf/qwen/qwen3-embedding-0.6b", null)]
    public void TemplateAndStagingDefaults_ArePriced(InferenceProviderKind kind, string capability, string modelCode, string? variant)
    {
        ModelCatalogSeed seed = ModelCatalogSeed.LoadDefault();

        SeedCapabilityEntry? entry = seed.Find(kind, capability, modelCode);
        entry.Should().NotBeNull();
        entry!.PriceSource.Should().Be("Table");

        IReadOnlyList<SeedPriceRow> rows = seed.SuggestFor(kind, capability, modelCode, AtRetrieval);
        UsageKind[] required = ReelBolt.Shared.Metering.ProviderCapabilityUsage.ByCapability[Enum.Parse<InferenceProviderCapability>(capability)];
        foreach (UsageKind unit in required)
        {
            rows.Should().Contain(r => r.Unit == unit && r.Variant == null && r.CostUsdPerUnit > 0m);
            if (variant is not null)
            {
                rows.Should().Contain(r => r.Unit == unit && r.Variant == variant && r.CostUsdPerUnit > 0m);
            }
        }
    }

    [Fact]
    public void Higgsfield_IsOracleOnly()
    {
        ModelCatalogSeed seed = ModelCatalogSeed.LoadDefault();

        List<SeedCapabilityEntry> higgsfield = seed.Capabilities.Where(c => c.Kind == InferenceProviderKind.Higgsfield).ToList();
        higgsfield.Should().NotBeEmpty();
        higgsfield.Should().OnlyContain(c => c.PriceSource == "Oracle");
        seed.Rows.Should().NotContain(r => r.Kind == InferenceProviderKind.Higgsfield);
        seed.Capabilities.Where(c => c.PriceSource == "Oracle").Should().OnlyContain(c => c.Kind == InferenceProviderKind.Higgsfield);
    }

    // ------------------------------------------------------------------ in-memory baseline

    [Fact]
    public void Validate_AcceptsTheMinimalBaseline()
    {
        ModelCatalogSeed.Parse(Seed(Baseline())).Validate().Should().BeEmpty();
    }

    [Fact]
    public void Load_ReadsFieldsFromFile()
    {
        string path = Path.Combine(Path.GetTempPath(), $"model-catalog-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, Seed(Baseline()));
        try
        {
            ModelCatalogSeed seed = ModelCatalogSeed.Load(path);
            seed.CatalogVersion.Should().Be("test.1");
            seed.RetrievedAt.Should().Be(new DateOnly(2026, 10, 12));
            seed.Rows.Should().HaveCount(3);
            SeedPriceRow row = seed.Rows.Single(r => r.Unit == UsageKind.LlmInputTokens);
            row.Should().Be(new SeedPriceRow(InferenceProviderKind.Anthropic, "claude-x", "Chat", UsageKind.LlmInputTokens,
                null, 0.000001m, new DateOnly(2026, 10, 12), null, "$1 / 1M", "https://example.test/p",
                new DateOnly(2026, 10, 12), ""));
            seed.Capabilities.Should().ContainSingle().Which.Should().Be(new SeedCapabilityEntry(
                InferenceProviderKind.Anthropic, "claude-x", "Claude X", "current", null, null, "Chat", "Table", null));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_ReadsOptionalModelNotes()
    {
        JsonObject model = Model("claude-x", "Claude X", ChatTable());
        model["notes"] = "Promotional price until 2026-11-21.";

        ModelCatalogSeed.Parse(Seed(Provider("Anthropic", model))).Capabilities.Should().ContainSingle()
            .Which.ModelNotes.Should().Be("Promotional price until 2026-11-21.");
        ModelCatalogSeed.Parse(Seed(Baseline())).Capabilities.Should().ContainSingle()
            .Which.ModelNotes.Should().BeNull();
    }

    // ------------------------------------------------------------------ rule 1: kind and capability parse

    [Fact]
    public void Validate_Rejects_UnknownKind_AtLoad()
    {
        string json = Seed(Provider("Anthropik", Model("claude-x", "Claude X", ChatTable())));

        Action load = () => ModelCatalogSeed.Parse(json);

        load.Should().Throw<InvalidDataException>().WithMessage("*Anthropik*");
    }

    [Fact]
    public void Validate_Rejects_UnknownUnit_AtLoad()
    {
        string json = Seed(Provider("Anthropic", Model("claude-x", "Claude X",
            Cap("Chat", "Table", null, Row("LlmInputTokenz")))));

        Action load = () => ModelCatalogSeed.Parse(json);

        load.Should().Throw<InvalidDataException>().WithMessage("*LlmInputTokenz*");
    }

    [Fact]
    public void Validate_Rejects_MalformedJson_AtLoad()
    {
        Action load = () => ModelCatalogSeed.Parse("{ not json");

        load.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void Validate_Rejects_UnknownCapability()
    {
        string json = Seed(Provider("Anthropic", Model("claude-x", "Claude X",
            Cap("Chatt", "Table", null, Row("LlmInputTokens")))));

        ModelCatalogSeed.Parse(json).Validate().Should().Contain(l => l.Contains("Chatt") && l.Contains("capability"));
    }

    [Theory]
    [InlineData("AzureOpenAI", "compute")]
    [InlineData("OpenAICompatible", "render")]
    public void Validate_Rejects_ComputeOutsideItsOneKindAndModel(string kind, string modelCode)
    {
        string json = Seed(Provider(kind, Model(modelCode, "Compute", ComputeTable())));

        ModelCatalogSeed.Parse(json).Validate().Should().Contain(l => l.Contains("Compute"));
    }

    [Fact]
    public void Validate_AcceptsCompute_OnOpenAICompatibleCompute()
    {
        string json = Seed(Provider("OpenAICompatible", Model("compute", "Compute", ComputeTable())));

        ModelCatalogSeed.Parse(json).Validate().Should().BeEmpty();
    }

    // ------------------------------------------------------------------ rule 2: servable

    [Fact]
    public void Validate_Rejects_CapabilityTheKindCannotServe()
    {
        string json = Seed(Provider("Anthropic", Model("claude-x", "Claude X",
            Cap("Embedding", "Table", null, Row("EmbeddingTokens")))));

        ModelCatalogSeed.Parse(json).Validate().Should().Contain(l => l.Contains("Anthropic") && l.Contains("Embedding") && l.Contains("servable"));
    }

    // ------------------------------------------------------------------ rule 3: unit allowed for capability

    [Fact]
    public void Validate_Rejects_UnitTheCapabilityDoesNotMeter()
    {
        JsonObject cap = ChatTable();
        ((JsonArray)cap["prices"]!).Add(Row("EmbeddingTokens"));
        string json = Seed(Provider("Anthropic", Model("claude-x", "Claude X", cap)));

        ModelCatalogSeed.Parse(json).Validate().Should().Contain(l => l.Contains("EmbeddingTokens") && l.Contains("Chat"));
    }

    [Fact]
    public void Validate_Rejects_ComputeUnitOutsideTheThreeSeconds()
    {
        JsonObject cap = ComputeTable();
        ((JsonArray)cap["prices"]!).Add(Row("VideoGenSeconds"));
        string json = Seed(Provider("OpenAICompatible", Model("compute", "Compute", cap)));

        ModelCatalogSeed.Parse(json).Validate().Should().Contain(l => l.Contains("VideoGenSeconds") && l.Contains("Compute"));
    }

    // ------------------------------------------------------------------ rule 4: price source shape

    [Fact]
    public void Validate_Rejects_TableMissingANullVariantRowForARequiredUnit()
    {
        string json = Seed(Provider("Anthropic", Model("claude-x", "Claude X",
            Cap("Chat", "Table", null, Row("LlmInputTokens"), Row("LlmOutputTokens"), Row("LlmCachedInputTokens", variant: "gt200k")))));

        ModelCatalogSeed.Parse(json).Validate().Should().Contain(l => l.Contains("LlmCachedInputTokens") && l.Contains("null-variant"));
    }

    [Fact]
    public void Validate_Rejects_TableWhoseNullRowIsNotEffectiveOnRetrievedAt()
    {
        string json = Seed(Provider("Anthropic", Model("claude-x", "Claude X",
            Cap("Chat", "Table", null, Row("LlmInputTokens", from: "2026-11-01"), Row("LlmOutputTokens"), Row("LlmCachedInputTokens")))));

        ModelCatalogSeed.Parse(json).Validate().Should().Contain(l => l.Contains("LlmInputTokens") && l.Contains("null-variant"));
    }

    [Fact]
    public void Validate_Rejects_NullSeriesThatClosesWithoutASuccessor()
    {
        string json = Seed(Provider("Anthropic", Model("claude-x", "Claude X",
            Cap("Chat", "Table", null, Row("LlmInputTokens", to: "2027-01-01"), Row("LlmOutputTokens"), Row("LlmCachedInputTokens")))));

        ModelCatalogSeed.Parse(json).Validate().Should().ContainSingle(l => l.Contains("LlmInputTokens")
            && l.Contains("null-variant") && l.Contains("2027-01-01"));
    }

    [Fact]
    public void Validate_Rejects_NullRowMissingWhereAVariantRowStarts()
    {
        string json = Seed(Provider("Anthropic", Model("claude-x", "Claude X",
            Cap("Chat", "Table", null, Row("LlmInputTokens", to: "2026-12-01"), Row("LlmInputTokens", variant: "gt200k", from: "2026-12-15"),
                Row("LlmOutputTokens"), Row("LlmCachedInputTokens")))));

        ModelCatalogSeed.Parse(json).Validate().Should().Contain(l => l.Contains("LlmInputTokens")
            && l.Contains("null-variant") && l.Contains("2026-12-15"));
    }

    [Fact]
    public void Validate_Rejects_UnpricedWithRowsOrWithoutReason()
    {
        string withRows = Seed(Provider("Anthropic", Model("claude-x", "Claude X",
            Cap("Chat", "Unpriced", "no public price", Row("LlmInputTokens")))));
        string noReason = Seed(Provider("Anthropic", Model("claude-x", "Claude X", Cap("Chat", "Unpriced", " "))));

        ModelCatalogSeed.Parse(withRows).Validate().Should().Contain(l => l.Contains("Unpriced") && l.Contains("rows"));
        ModelCatalogSeed.Parse(noReason).Validate().Should().Contain(l => l.Contains("Unpriced") && l.Contains("reason"));
    }

    [Fact]
    public void Validate_Rejects_OracleOutsideHiggsfieldOrWithRows()
    {
        string wrongKind = Seed(Provider("Anthropic", Model("claude-x", "Claude X", Cap("Chat", "Oracle", null))));
        string withRows = Seed(Provider("Higgsfield", Model("kling-video/v3.0/std/text-to-video", "Kling",
            Cap("VideoGeneration", "Oracle", null, Row("VideoGenSeconds")))));

        ModelCatalogSeed.Parse(wrongKind).Validate().Should().Contain(l => l.Contains("Oracle") && l.Contains("Higgsfield"));
        ModelCatalogSeed.Parse(withRows).Validate().Should().Contain(l => l.Contains("Oracle") && l.Contains("rows"));
    }

    [Fact]
    public void Validate_Rejects_UnknownPriceSource()
    {
        string json = Seed(Provider("Anthropic", Model("claude-x", "Claude X", Cap("Chat", "Tabel", null))));

        ModelCatalogSeed.Parse(json).Validate().Should().Contain(l => l.Contains("Tabel"));
    }

    // ------------------------------------------------------------------ rule 5: row integrity

    [Fact]
    public void Validate_Rejects_DuplicateRow()
    {
        JsonObject cap = ChatTable();
        ((JsonArray)cap["prices"]!).Add(Row("LlmInputTokens"));
        string json = Seed(Provider("Anthropic", Model("claude-x", "Claude X", cap)));

        ModelCatalogSeed.Parse(json).Validate().Should().Contain(l => l.Contains("duplicate") && l.Contains("LlmInputTokens"));
    }

    [Fact]
    public void Validate_Rejects_OverlappingWindows()
    {
        JsonObject cap = Cap("Chat", "Table", null,
            Row("LlmInputTokens", to: "2027-01-01"), Row("LlmInputTokens", from: "2026-12-01"),
            Row("LlmOutputTokens"), Row("LlmCachedInputTokens"));
        string json = Seed(Provider("Anthropic", Model("claude-x", "Claude X", cap)));

        ModelCatalogSeed.Parse(json).Validate().Should().Contain(l => l.Contains("overlap") && l.Contains("LlmInputTokens"));
    }

    [Fact]
    public void Validate_AcceptsAdjacentWindows()
    {
        JsonObject cap = Cap("Chat", "Table", null,
            Row("LlmInputTokens", to: "2027-01-01"), Row("LlmInputTokens", from: "2027-01-01"),
            Row("LlmOutputTokens"), Row("LlmCachedInputTokens"));
        string json = Seed(Provider("Anthropic", Model("claude-x", "Claude X", cap)));

        ModelCatalogSeed.Parse(json).Validate().Should().BeEmpty();
    }

    [Fact]
    public void Validate_Rejects_EffectiveToNotAfterEffectiveFrom()
    {
        JsonObject cap = ChatTable();
        ((JsonArray)cap["prices"]!).Add(Row("LlmInputTokens", variant: "gt200k", from: "2026-10-12", to: "2026-10-12"));
        string json = Seed(Provider("Anthropic", Model("claude-x", "Claude X", cap)));

        ModelCatalogSeed.Parse(json).Validate().Should().Contain(l => l.Contains("effectiveTo"));
    }

    [Fact]
    public void Validate_Rejects_NegativeCost()
    {
        JsonObject cap = ChatTable();
        ((JsonArray)cap["prices"]!).Add(Row("LlmInputTokens", variant: "gt200k", cost: "-0.1"));
        string json = Seed(Provider("Anthropic", Model("claude-x", "Claude X", cap)));

        ModelCatalogSeed.Parse(json).Validate().Should().Contain(l => l.Contains("cost") && l.Contains("-0.1"));
    }

    // ------------------------------------------------------------------ rule 6: codes and variants the code knows

    [Fact]
    public void Validate_Rejects_FalAiModelCodeNotInTheCatalogs()
    {
        string json = Seed(Provider("FalAi", Model("fal-ai/sam-3-1/imagex", "SAM",
            Cap("Segmentation", "Table", null, Row("SegmentationImages")))));

        ModelCatalogSeed.Parse(json).Validate().Should().Contain(l => l.Contains("fal-ai/sam-3-1/imagex"));
    }

    [Fact]
    public void Validate_Accepts_FalAiCodesFromEachCatalog()
    {
        string json = Seed(Provider("FalAi",
            Model("fal-ai/sam-3-1/image", "SAM 3.1", Cap("Segmentation", "Table", null, Row("SegmentationImages"))),
            Model("google/nano-banana-2.1/edit", "Nano Banana Edit",
                Cap("ImageEdit", "Table", null, Row("ImageEditImages"), Row("ImageEditImages", variant: "2K"))),
            Model("fal-ai/veo3.1", "Veo 3.1",
                Cap("VideoGeneration", "Table", null, Row("VideoGenSeconds"), Row("VideoGenSeconds", variant: "1080p")))));

        ModelCatalogSeed.Parse(json).Validate().Should().BeEmpty();
    }

    [Fact]
    public void Validate_Rejects_FalAiImageTierTheCatalogDoesNotEmit()
    {
        string json = Seed(Provider("FalAi", Model("google/nano-banana-2.1/edit", "Nano Banana Edit",
            Cap("ImageEdit", "Table", null, Row("ImageEditImages"), Row("ImageEditImages", variant: "8K")))));

        ModelCatalogSeed.Parse(json).Validate().Should().Contain(l => l.Contains("8K") && l.Contains("google/nano-banana-2.1/edit"));
    }

    [Fact]
    public void Validate_Rejects_FalAiVideoVariantNotAResolutionOfTheEndpoint()
    {
        string json = Seed(Provider("FalAi", Model("fal-ai/veo3.1", "Veo 3.1",
            Cap("VideoGeneration", "Table", null, Row("VideoGenSeconds"), Row("VideoGenSeconds", variant: "1080P")))));

        ModelCatalogSeed.Parse(json).Validate().Should().Contain(l => l.Contains("1080P") && l.Contains("fal-ai/veo3.1"));
    }

    [Fact]
    public void Validate_Rejects_MiniMaxVideoVariantDescribeDoesNotList()
    {
        string json = Seed(Provider("MiniMax", Model("MiniMax-H3", "MiniMax H3",
            Cap("VideoGeneration", "Table", null, Row("VideoGenSeconds"), Row("VideoGenSeconds", variant: "768p")))));

        ModelCatalogSeed.Parse(json).Validate().Should().Contain(l => l.Contains("768p") && l.Contains("MiniMax-H3"));
    }

    [Fact]
    public void Validate_Rejects_HiggsfieldCodeNotAClientEndpoint()
    {
        string json = Seed(Provider("Higgsfield", Model("kling-video/v3.0/std/text-to-vid", "Kling",
            Cap("VideoGeneration", "Oracle", null))));

        ModelCatalogSeed.Parse(json).Validate().Should().Contain(l => l.Contains("kling-video/v3.0/std/text-to-vid"));
    }

    [Fact]
    public void Validate_Rejects_ComfyUiCodeNotATemplate()
    {
        string json = Seed(Provider("ComfyUI", Model("wan2.2-t2v", "Wan",
            Cap("VideoGeneration", "Table", null, Row("VideoGenSeconds")))));

        ModelCatalogSeed.Parse(json).Validate().Should().Contain(l => l.Contains("wan2.2-t2v"));
    }

    // ------------------------------------------------------------------ rule 7: display names

    [Fact]
    public void Validate_Rejects_DuplicateDisplayNameWithinAKind()
    {
        string json = Seed(Provider("Anthropic",
            Model("claude-x", "Claude X", ChatTable()),
            Model("claude-y", "Claude X", ChatTable())));

        ModelCatalogSeed.Parse(json).Validate().Should().Contain(l => l.Contains("Claude X") && l.Contains("display name"));
    }

    [Fact]
    public void Validate_Accepts_SameDisplayNameAcrossKinds()
    {
        string json = Seed(
            Provider("Anthropic", Model("claude-x", "Shared Name", ChatTable())),
            Provider("Gemini", Model("gemini-x", "Shared Name", ChatTable())));

        ModelCatalogSeed.Parse(json).Validate().Should().BeEmpty();
    }

    // ------------------------------------------------------------------ T-037: selfHostedOnly

    [Fact]
    public void CommittedSeed_FlagsExactlyTheSelfHostedOnlyEntries()
    {
        ModelCatalogSeed.LoadDefault().Capabilities.Where(c => c.SelfHostedOnly)
            .Select(c => $"{c.Kind}/{c.ModelCode}/{c.Capability}")
            .Should().BeEquivalentTo(new[]
            {
                "OpenAICompatible/speaches-ai/Kokoro-82M-v1.0-ONNX/SpeechSynthesis",
                "OpenAICompatible/Systran/faster-whisper-small/Transcription",
                "OpenAICompatible/Systran/faster-whisper-large-v3/Transcription",
                "OpenAICompatible/Qwen/Qwen3-Embedding-0.6B/Embedding",
                "OpenJev/jev-latest/Decision",
                "OpenAICompatible/local/Chat",
            });
    }

    [Fact]
    public void SelfHostedOnly_IsAnOptionalFlag_DefaultingToFalse()
    {
        ModelCatalogSeed.Parse(Seed(Baseline())).Capabilities.Single().SelfHostedOnly.Should().BeFalse();
        ModelCatalogSeed.Parse(Seed(Provider("OpenAICompatible", Model("local", "Local", SelfHosted(ZeroChat())))))
            .Capabilities.Single().SelfHostedOnly.Should().BeTrue();
    }

    [Fact]
    public void Validate_RefusesSelfHostedOnly_OnAnEntryThatIsNotATable()
    {
        string json = Seed(Provider("OpenAICompatible", Model("local", "Local", SelfHosted(Cap("Chat", "Unpriced", "no price")))));

        ModelCatalogSeed.Parse(json).Validate().Should().Contain(l => l.Contains("selfHostedOnly") && l.Contains("Table"));
    }

    [Fact]
    public void Validate_RefusesSelfHostedOnly_WhenAnyRowIsNotZero()
    {
        string json = Seed(Provider("OpenAICompatible", Model("local", "Local", SelfHosted(
            Cap("Chat", "Table", null, Row("LlmInputTokens", cost: "0"), Row("LlmOutputTokens", cost: "0.5"), Row("LlmCachedInputTokens", cost: "0"))))));

        ModelCatalogSeed.Parse(json).Validate().Should().Contain(l => l.Contains("selfHostedOnly") && l.Contains("LlmOutputTokens"));
    }

    [Fact]
    public void Validate_RefusesSelfHostedOnly_OnAKindThatIsNotSelfHosted()
    {
        string json = Seed(Provider("DeepSeek", Model("deepseek-flash", "DeepSeek Flash", SelfHosted(ZeroChat()))));

        ModelCatalogSeed.Parse(json).Validate().Should().Contain(l => l.Contains("selfHostedOnly") && l.Contains("DeepSeek"));
    }

    [Fact]
    public void Validate_RefusesAnUnflaggedZeroRow_OnAnOpenAICompatibleTable()
    {
        string json = Seed(Provider("OpenAICompatible", Model("local", "Local", ZeroChat())));

        ModelCatalogSeed.Parse(json).Validate().Should().Contain(l => l.Contains("selfHostedOnly") && l.Contains("local"));
    }

    [Fact]
    public void Validate_RefusesAnUnflaggedZeroRow_OnAnOpenJevTable()
    {
        string json = Seed(Provider("OpenJev", Model("jev-latest", "Jev", Cap("Decision", "Table", null,
            Row("LlmInputTokens", cost: "0"), Row("LlmOutputTokens", cost: "0"), Row("LlmCachedInputTokens", cost: "0")))));

        ModelCatalogSeed.Parse(json).Validate().Should().Contain(l => l.Contains("selfHostedOnly") && l.Contains("jev-latest"));
    }

    [Fact]
    public void Validate_AcceptsAFlaggedZeroTable_AndAnUnflaggedNonZeroOpenAICompatibleTable()
    {
        string flagged = Seed(Provider("OpenAICompatible", Model("local", "Local", SelfHosted(ZeroChat()))));
        string priced = Seed(Provider("OpenAICompatible", Model("gpt-x", "GPT X", ChatTable())));

        ModelCatalogSeed.Parse(flagged).Validate().Should().NotContain(l => l.Contains("selfHostedOnly"));
        ModelCatalogSeed.Parse(priced).Validate().Should().NotContain(l => l.Contains("selfHostedOnly"));
    }

    // ------------------------------------------------------------------ JSON builders

    private static JsonObject Baseline() => Provider("Anthropic", Model("claude-x", "Claude X", ChatTable()));

    private static JsonObject ZeroChat() => Cap("Chat", "Table", null,
        Row("LlmInputTokens", cost: "0"), Row("LlmOutputTokens", cost: "0"), Row("LlmCachedInputTokens", cost: "0"));

    /// <summary>Marks a capability as self-hosted-only (T-037), as the generator writes it.</summary>
    private static JsonObject SelfHosted(JsonObject capability)
    {
        capability["selfHostedOnly"] = true;
        return capability;
    }

    private static JsonObject ChatTable() => Cap("Chat", "Table", null,
        Row("LlmInputTokens", cost: "0.000001", quoted: "$1 / 1M"), Row("LlmOutputTokens"), Row("LlmCachedInputTokens"));

    private static JsonObject ComputeTable() => Cap("Compute", "Table", null,
        Row("RenderOutputSeconds"), Row("EncodeOutputSeconds"), Row("AnalyzeMediaSeconds"),
        Row("RenderOutputSeconds", variant: "1080p"));

    private static string Seed(params JsonObject[] providers) => new JsonObject
    {
        ["schemaVersion"] = 1,
        ["catalogVersion"] = "test.1",
        ["retrievedAt"] = "2026-10-12",
        ["providers"] = new JsonArray(providers.Cast<JsonNode?>().ToArray()),
    }.ToJsonString();

    private static JsonObject Provider(string kind, params JsonObject[] models) => new()
    {
        ["kind"] = kind,
        ["models"] = new JsonArray(models.Cast<JsonNode?>().ToArray()),
    };

    private static JsonObject Model(string code, string displayName, params JsonObject[] capabilities) => new()
    {
        ["modelCode"] = code,
        ["displayName"] = displayName,
        ["lineup"] = "current",
        ["vendor"] = null,
        ["retiresOn"] = null,
        ["replacement"] = null,
        ["capabilities"] = new JsonArray(capabilities.Cast<JsonNode?>().ToArray()),
    };

    private static JsonObject Cap(string capability, string priceSource, string? unpricedReason, params JsonObject[] rows) => new()
    {
        ["capability"] = capability,
        ["priceSource"] = priceSource,
        ["unpricedReason"] = unpricedReason,
        ["prices"] = new JsonArray(rows.Cast<JsonNode?>().ToArray()),
    };

    private static JsonObject Row(string unit, string? variant = null, string cost = "0.5", string from = "2026-10-12",
        string? to = null, string quoted = "q") => new()
        {
            ["unit"] = unit,
            ["variant"] = variant,
            ["costUsdPerUnit"] = cost,
            ["effectiveFrom"] = from,
            ["effectiveTo"] = to,
            ["quoted"] = quoted,
            ["sourceUrl"] = "https://example.test/p",
            ["retrievedAt"] = "2026-10-12",
            ["notes"] = "",
        };
}
