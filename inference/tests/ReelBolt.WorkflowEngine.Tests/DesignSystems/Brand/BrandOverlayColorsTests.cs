using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.DesignSystems;
using ReelBolt.Shared.DesignSystems.Color;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Services.DesignSystems;
using Xunit;
using StepTypeEnum = ReelBolt.Shared.Data.Models.StepType;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Brand;

/// <summary>
/// P3-07 coverage for the brand-aware overlay colour derivation (SPEC 14.2): the WCAG contrast
/// helper, <see cref="BrandOverlayColors.Pick"/>, and the <see cref="DesignSystemRunState"/>
/// derivation the executor and the step-cache key both read. Off must be a no-op — no override, no
/// warning, no cache component.
/// </summary>
public class BrandOverlayColorsTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [Theory]
    [InlineData("#000000", "#ffffff", 21.0)]
    [InlineData("#767676", "#ffffff", 4.54)]
    [InlineData("#ffffff", "#ffffff", 1.0)]
    [InlineData("#000000", "#000000", 1.0)]
    public void Ratio_matches_known_wcag_pairs(string a, string b, double expected)
    {
        ColorContrast.Ratio(a, b).Should().BeApproximately(expected, 0.05);
    }

    [Fact]
    public void Ratio_is_symmetric_and_malformed_input_is_the_no_contrast_ratio()
    {
        ColorContrast.Ratio("#0b1f3a", "#ffe066")
            .Should().BeApproximately(ColorContrast.Ratio("#ffe066", "#0b1f3a"), 1e-9);
        ColorContrast.Ratio("not-a-colour", "#ffffff").Should().Be(1.0);
    }

    [Fact]
    public void Pick_chooses_the_first_candidate_that_clears_the_floor()
    {
        BrandOverlayPick pick = BrandOverlayColors.Pick(Palette(primary: "#ffffff", secondary: "#ffe066"));

        pick.FontColor.Should().Be("#ffffff");
        pick.BoxColor.Should().Be("black@0.6");
        pick.Warning.Should().BeNull();
    }

    [Fact]
    public void Pick_skips_a_low_contrast_primary_for_the_next_candidate()
    {
        // Dark navy fails the 7.0 contrast floor against black; light yellow clears it.
        BrandOverlayPick pick = BrandOverlayColors.Pick(Palette(primary: "#0b1f3a", secondary: "#ffe066"));

        pick.FontColor.Should().Be("#ffe066");
        pick.Warning.Should().BeNull();
    }

    [Fact]
    public void Pick_warns_when_no_candidate_qualifies()
    {
        BrandOverlayPick pick = BrandOverlayColors.Pick(Palette(
            primary: "#0b1f3a", secondary: "#101820", accent: "#111111", text: "#222222"));

        pick.FontColor.Should().BeNull();
        pick.Warning.Should().Be(BrandOverlayColors.LowContrastWarning);
    }

    [Fact]
    public void Pick_follows_the_spec_candidate_order_across_all_four_paths()
    {
        var palette = new JsonObject
        {
            ["color"] = new JsonObject
            {
                ["brand"] = new JsonObject
                {
                    ["primary"] = ColorToken("#0b1f3a"),
                    ["secondary"] = ColorToken("#0b1f3a")
                },
                ["accent"] = ColorToken("#ffe066"),
                ["text"] = ColorToken("#ffffff")
            }
        };

        BrandOverlayColors.Pick(palette).FontColor.Should().Be("#ffe066");
    }

    [Fact]
    public void Pick_returns_the_brand_colour_of_a_canonical_document()
    {
        JsonObject design = CanonicalDocument(("color.brand.primary", "#0b1f3a"), ("color.brand.secondary", "#ffe066"));

        BrandOverlayPick pick = BrandOverlayColors.Pick(design);

        pick.FontColor.Should().Be("#ffe066");
        pick.Warning.Should().BeNull();
    }

    [Fact]
    public void Pick_still_reads_a_legacy_string_colour_value()
    {
        var legacy = new JsonObject
        {
            ["color"] = new JsonObject
            {
                ["brand"] = new JsonObject { ["primary"] = new JsonObject { ["$type"] = "color", ["$value"] = "#ffffff" } }
            }
        };

        BrandOverlayColors.Pick(legacy).FontColor.Should().Be("#ffffff");
    }

    [Fact]
    public void Derive_on_uses_the_brand_colour_of_a_canonical_document()
    {
        VideoCompileStepConfig config = Config(colors: true, fonts: false);
        DesignSystemRunState state = State(CanonicalDocument(("color.brand.primary", "#ffffff")));

        VideoCompileBrandOverrides? overrides = state.DeriveVideoCompileOverrides(config);

        overrides.Should().NotBeNull();
        overrides!.OverlayFontColor.Should().Be("#ffffff");
        overrides.Warnings.Should().BeEmpty();
    }

    private static JsonObject CanonicalDocument(params (string Path, string Hex)[] colours)
    {
        JsonObject design = DesignDocument.New();
        foreach ((string path, string hex) in colours)
        {
            DesignDocument.SetToken(
                design,
                path,
                "color",
                new JsonObject { ["hex"] = hex, ["colorSpace"] = "srgb" },
                new TokenProvenance("imported", "test", null, null),
                1.0);
        }

        return design;
    }

    [Fact]
    public void Derive_off_is_a_noop_for_the_config_and_the_cache_key()
    {
        VideoCompileStepConfig config = Config(colors: false, fonts: false);
        string before = JsonSerializer.Serialize(config, JsonOptions);

        DesignSystemRunState state = State(Palette(primary: "#0b1f3a", secondary: "#ffe066"));
        state.DeriveVideoCompileOverrides(config).Should().BeNull();

        // The derivation never mutates the config, so the serialized config is byte-identical …
        string after = JsonSerializer.Serialize(config, JsonOptions);
        after.Should().Be(before);
        // … and the cache key keeps the exact shape it had before the feature existed.
        state.CacheKeyComponent(StepTypeEnum.VideoCompile, AgentType.VideoTransform, Step(before)).Should().BeNull();
    }

    [Fact]
    public void Derive_on_applies_colours_and_fonts_and_reports_the_cache_component()
    {
        VideoCompileStepConfig config = Config(colors: true, fonts: true);
        DesignSystemRunState state = State(FullPalette());

        VideoCompileBrandOverrides? overrides = state.DeriveVideoCompileOverrides(config);

        overrides.Should().NotBeNull();
        overrides!.OverlayFontColor.Should().Be("#ffe066");
        overrides.OverlayBoxColor.Should().Be("black@0.6");
        overrides.Font.Should().Be(CaptionFont.Inter);
        overrides.Warnings.Should().BeEmpty();
        overrides.CacheKeyComponent.Should().NotBeNull();
        overrides.CacheKeyComponent.Should().StartWith("c=");
        overrides.CacheKeyComponent!.Length.Should().Be(18);
        state.CacheKeyComponent(
                StepTypeEnum.VideoCompile, AgentType.VideoTransform, Step(JsonSerializer.Serialize(config, JsonOptions)))
            .Should().Be(overrides.CacheKeyComponent);
    }

    [Fact]
    public void Derive_warns_and_keeps_the_config_values_when_nothing_qualifies()
    {
        VideoCompileStepConfig config = Config(colors: true, fonts: true);
        DesignSystemRunState state = State(new JsonObject
        {
            ["color"] = new JsonObject
            {
                ["brand"] = new JsonObject { ["primary"] = ColorToken("#0b1f3a") }
            },
            ["typography"] = new JsonObject
            {
                ["family"] = new JsonObject { ["heading"] = FontStack("Satoshi") }
            }
        });

        VideoCompileBrandOverrides? overrides = state.DeriveVideoCompileOverrides(config);

        overrides.Should().NotBeNull();
        overrides!.OverlayFontColor.Should().BeNull();
        overrides.Font.Should().Be(CaptionFont.Inter);
        overrides.Warnings.Should().Contain(warning => warning.Code == BrandOverlayColors.LowContrastWarning);
        overrides.Warnings.Should().Contain(warning => warning.Code == "font_substituted:Satoshi->Inter");
        // Nothing usable was derived, but the low-contrast warning means the cache key still moves.
        overrides.CacheKeyComponent.Should().StartWith("c=");
    }

    [Fact]
    public void Derive_leaves_a_non_auto_caption_font_alone()
    {
        VideoCompileStepConfig config = new(
            1, new ExtractInputRef(ExtractInputSource.Previous), 1,
            DesignSystemFonts: true, CaptionFont: CaptionFont.Anton);
        DesignSystemRunState state = State(FullPalette());

        // Only CaptionFont.Auto is derived from the brand families, so a pinned font is untouched
        // and — with no other flag on — the derivation is a complete no-op.
        state.DeriveVideoCompileOverrides(config).Should().BeNull();
    }

    private static VideoCompileStepConfig Config(bool colors, bool fonts) => new(
        1, new ExtractInputRef(ExtractInputSource.Previous), 1,
        DesignSystemColors: colors, DesignSystemFonts: fonts);

    private static DesignSystemRunState State(JsonObject design) =>
        new(new LoadedProfile(design, 1, null, 0), new DesignSystemAmbientOptions());

    private static WorkflowStep Step(string configJson) =>
        new() { StepType = StepTypeEnum.VideoCompile, VideoCompileConfigJson = configJson };

    private static JsonObject FullPalette() => new()
    {
        ["color"] = new JsonObject
        {
            ["brand"] = new JsonObject
            {
                ["primary"] = ColorToken("#0b1f3a"),
                ["secondary"] = ColorToken("#ffe066")
            },
            ["accent"] = ColorToken("#ffe066"),
            ["text"] = ColorToken("#ffffff")
        },
        ["typography"] = new JsonObject
        {
            ["family"] = new JsonObject { ["heading"] = FontStack("Satoshi", "Inter") }
        }
    };

    private static JsonObject Palette(string primary, string secondary, string? accent = null, string? text = null)
    {
        var color = new JsonObject
        {
            ["brand"] = new JsonObject { ["primary"] = ColorToken(primary), ["secondary"] = ColorToken(secondary) }
        };
        if (accent is not null)
            color["accent"] = ColorToken(accent);
        if (text is not null)
            color["text"] = ColorToken(text);
        return new JsonObject { ["color"] = color };
    }

    /// <summary>A colour token in the canonical shape the validator accepts: <c>{hex, colorSpace}</c>.</summary>
    private static JsonObject ColorToken(string value) =>
        new() { ["$type"] = "color", ["$value"] = new JsonObject { ["hex"] = value, ["colorSpace"] = "srgb" } };

    private static JsonObject FontStack(params string[] families) =>
        new() { ["$type"] = "fontFamily", ["$value"] = new JsonArray(families.Select(family => (JsonNode)JsonValue.Create(family)!).ToArray()) };
}
