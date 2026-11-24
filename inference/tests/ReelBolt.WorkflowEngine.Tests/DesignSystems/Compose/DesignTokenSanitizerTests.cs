using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using ReelBolt.Shared.DesignSystems;
using ReelBolt.Shared.DesignSystems.Importers;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Compose;

public class DesignTokenSanitizerTests
{
    private static readonly DesignValidationContext Context = new(_ => true, new HashSet<string>());

    private static JsonObject Doc(params (string Group, string Key, string Type, JsonNode Value)[] tokens)
    {
        var doc = new JsonObject { ["schema"] = DesignDocument.Schema };
        foreach ((string group, string key, string type, JsonNode value) in tokens)
        {
            JsonObject g = doc[group] as JsonObject ?? new JsonObject();
            g[key] = new JsonObject { ["$type"] = type, ["$value"] = value };
            doc[group] = g;
        }

        return doc;
    }

    private static JsonObject Hex(string hex) => new() { ["hex"] = hex, ["colorSpace"] = "srgb" };

    [Fact]
    public void Valid_input_is_left_byte_identical_and_reports_nothing()
    {
        JsonObject doc = Doc(
            ("color", "a", "color", Hex("#7c3aed")),
            ("spacing", "b", "dimension", new JsonObject { ["value"] = 8, ["unit"] = "px" }));
        string before = doc.ToJsonString();
        var diagnostics = new List<ImportDiagnostic>();

        int dropped = DesignTokenSanitizer.Sanitize(doc, Context, diagnostics);

        dropped.Should().Be(0);
        diagnostics.Should().BeEmpty();
        doc.ToJsonString().Should().Be(before);
    }

    [Fact]
    public void A_non_canonical_colour_is_normalised_not_dropped()
    {
        JsonObject doc = Doc(("color", "a", "color", JsonValue.Create("#7C3AED")!), ("color", "b", "color", new JsonObject { ["hex"] = "#ABC" }));
        var diagnostics = new List<ImportDiagnostic>();

        int dropped = DesignTokenSanitizer.Sanitize(doc, Context, diagnostics);

        dropped.Should().Be(0);
        diagnostics.Should().BeEmpty();
        doc["color"]!["a"]!["$value"]!.ToJsonString().Should().Be("""{"hex":"#7c3aed","colorSpace":"srgb"}""");
        doc["color"]!["b"]!["$value"]!["hex"]!.GetValue<string>().Should().Be("#aabbcc");
        DesignDocumentValidator.Validate(doc, Context).IsValid.Should().BeTrue();
    }

    [Fact]
    public void An_unparseable_colour_is_dropped_with_a_clipped_warning_and_siblings_survive()
    {
        string longProse = "green (Mantine green) " + new string('x', 300);
        JsonObject doc = Doc(
            ("color", "good", "color", Hex("#112233")),
            ("color", "grad", "color", JsonValue.Create("linear-gradient(red, blue)")!),
            ("color", "prose", "color", JsonValue.Create(longProse)!));
        var diagnostics = new List<ImportDiagnostic>();

        int dropped = DesignTokenSanitizer.Sanitize(doc, Context, diagnostics);

        dropped.Should().Be(2);
        ((JsonObject)doc["color"]!).Select(p => p.Key).Should().Equal("good");
        diagnostics.Should().HaveCount(2);
        diagnostics.Should().OnlyContain(d => d.Code == "COLOR_UNPARSEABLE" && d.Severity == ImportDiagnosticSeverity.Warn);
        diagnostics.Select(d => d.Path).Should().BeEquivalentTo(["color.grad", "color.prose"]);
        diagnostics.Should().OnlyContain(d => d.Message.Length < 200);
        DesignDocumentValidator.Validate(doc, Context).IsValid.Should().BeTrue();
    }

    [Fact]
    public void A_secret_in_a_dropped_value_is_redacted_in_the_diagnostic()
    {
        const string secret = "sk-" + "ant-api03-abcdefghijklmnopqrstuvwxyz0123456789";
        JsonObject doc = Doc(("color", "bad", "color", JsonValue.Create("prose " + secret)!));
        var diagnostics = new List<ImportDiagnostic>();

        DesignTokenSanitizer.Sanitize(doc, Context, diagnostics);

        diagnostics.Should().ContainSingle();
        diagnostics[0].Message.Should().NotContain(secret);
    }

    [Fact]
    public void Bad_tokens_of_every_type_are_dropped_and_good_ones_kept()
    {
        JsonObject doc = Doc(
            ("spacing", "ok", "dimension", new JsonObject { ["value"] = 4, ["unit"] = "px" }),
            ("spacing", "huge", "dimension", new JsonObject { ["value"] = 99999, ["unit"] = "px" }),
            ("spacing", "words", "dimension", JsonValue.Create("clamp(1rem, 2vw, 2rem)")!),
            ("motion", "ok", "duration", new JsonObject { ["value"] = 200, ["unit"] = "ms" }),
            ("motion", "slow", "duration", new JsonObject { ["value"] = 9999999, ["unit"] = "ms" }),
            ("motion", "ease", "cubicBezier", new JsonArray(0.5, 0, 2, 1)),
            ("typography", "family", "fontFamily", new JsonArray()),
            ("typography", "weight", "fontWeight", JsonValue.Create("heavy")!),
            ("radius", "r", "number", JsonValue.Create("round")!),
            ("shadow", "s", "shadow", JsonValue.Create("0 1px 2px black")!),
            ("typography", "t", "typography", new JsonObject { ["fontSize"] = "huge" }));
        var diagnostics = new List<ImportDiagnostic>();

        int dropped = DesignTokenSanitizer.Sanitize(doc, Context, diagnostics);

        // "0 1px 2px black" and the string fontSize are not normalisable here; the others are dropped.
        DesignDocumentValidator.Validate(doc, Context).IsValid.Should().BeTrue();
        DesignDocument.FindToken(doc, "spacing.ok").Should().NotBeNull();
        DesignDocument.FindToken(doc, "motion.ok").Should().NotBeNull();
        DesignDocument.FindToken(doc, "spacing.huge").Should().BeNull();
        DesignDocument.FindToken(doc, "motion.slow").Should().BeNull();
        DesignDocument.FindToken(doc, "motion.ease").Should().BeNull();
        dropped.Should().BeGreaterThanOrEqualTo(6);
        diagnostics.Select(d => d.Code).Should().Contain(["DIMENSION_UNPARSEABLE", "DURATION_UNPARSEABLE", "CUBIC_BEZIER_UNPARSEABLE"]);
    }

    [Fact]
    public void Dropping_a_token_drops_an_alias_that_pointed_at_it()
    {
        JsonObject doc = Doc(
            ("color", "base", "color", JsonValue.Create("not-a-colour")!),
            ("color", "alias", "color", JsonValue.Create("{color.base}")!),
            ("color", "keep", "color", Hex("#000000")));
        var diagnostics = new List<ImportDiagnostic>();

        DesignTokenSanitizer.Sanitize(doc, Context, diagnostics);

        ((JsonObject)doc["color"]!).Select(p => p.Key).Should().Equal("keep");
        DesignDocumentValidator.Validate(doc, Context).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Document_level_violations_are_not_absorbed()
    {
        JsonObject doc = Doc(("color", "a", "color", Hex("#112233")));
        doc["schema"] = "nope";
        doc["bogus"] = new JsonObject();

        int dropped = DesignTokenSanitizer.Sanitize(doc, Context, []);

        dropped.Should().Be(0);
        DesignDocumentValidator.Validate(doc, Context).IsValid.Should().BeFalse();
        DesignDocument.FindToken(doc, "color.a").Should().NotBeNull();
    }

    [Fact]
    public void A_url_string_in_a_token_stays_a_fatal_violation()
    {
        JsonObject doc = Doc(("color", "a", "color", JsonValue.Create("https://evil.example/x")!));

        DesignTokenSanitizer.Sanitize(doc, Context, []);

        DesignDocumentValidator.Validate(doc, Context).Violations.Select(v => v.Code).Should().Contain("DS_V16");
    }

    [Fact]
    public void Mapper_reports_a_warning_for_every_dropped_input_with_path_and_clipped_value()
    {
        var diagnostics = new List<ImportDiagnostic>();

        JsonObject design = LegacyAnalyzerMapper.DesignFrom(StagingLikeStyleFixture.Build(), diagnostics)!;

        foreach (string key in StagingLikeStyleFixture.ExpectedDropped)
        {
            diagnostics.Should().Contain(d => d.Path == "color.extra." + key && d.Code == "COLOR_UNPARSEABLE", key);
            DesignDocument.FindToken(design, "color.extra." + key).Should().BeNull(key);
        }

        diagnostics.Should().Contain(d => d.Path == "color.text");
        diagnostics.Should().Contain(d => d.Path == "typography.size.bad" && d.Code == "DIMENSION_UNPARSEABLE");
        diagnostics.Should().Contain(d => d.Path == "spacing.bad");
        diagnostics.Should().OnlyContain(d => d.Message.Length < 200);
    }

    [Fact]
    public void Mapper_plus_sanitizer_turn_a_staging_shaped_style_into_a_valid_document()
    {
        var diagnostics = new List<ImportDiagnostic>();
        JsonObject design = LegacyAnalyzerMapper.DesignFrom(StagingLikeStyleFixture.Build(), diagnostics)!;

        int dropped = DesignTokenSanitizer.Sanitize(design, Context, diagnostics);

        DesignDocumentValidator.Validate(design, Context).Violations.Should().BeEmpty();
        dropped.Should().Be(0, "the mapper already omitted what it could not read; nothing is left for the sanitizer");
        foreach ((string key, string hex) in StagingLikeStyleFixture.ExpectedKept)
        {
            JsonObject token = DesignDocument.FindToken(design, "color.extra." + key)!;
            token["$value"]!["hex"]!.GetValue<string>().Should().Be(hex, key);
            token["$value"]!["colorSpace"]!.GetValue<string>().Should().Be("srgb");
        }

        design["color"]!["brand"]!["primary"]!["$value"]!["hex"]!.GetValue<string>().Should().Be("#7c3aed");
        design["color"]!["brand"]!["secondary"]!["$value"]!["hex"]!.GetValue<string>().Should().Be("#10b981");
        design["color"]!["background"]!["$value"]!["hex"]!.GetValue<string>().Should().Be("#ffffff");
        DesignDocument.FindToken(design, "typography.size.sm").Should().NotBeNull();
        DesignDocument.FindToken(design, "typography.size.lg").Should().NotBeNull();
        DesignDocument.FindToken(design, "typography.family.primary").Should().NotBeNull();
        DesignDocument.FindToken(design, "spacing.2")!["$value"]!["unit"]!.GetValue<string>().Should().Be("px");
        DesignDocument.FindToken(design, "typography.size.huge").Should().BeNull();
    }
}
