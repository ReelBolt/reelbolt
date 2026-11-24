using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using ReelBolt.Shared.DesignSystems;
using ReelBolt.Shared.DesignSystems.Importers;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.ProductMap;

public class ProseClampTests
{
    private const string Ellipsis = "…";

    private static string Sentenceish(int length) =>
        string.Concat(Enumerable.Repeat("Routes follow the file system layout. ", 40))[..length];

    [Fact]
    public void Prod_case_150_char_routingStrategy_is_trimmed_validates_and_warns_once()
    {
        ProductMapDocument raw = Doc() with { RoutingStrategy = Sentenceish(150) };
        ProductMapValidator.Validate(raw).IsValid.Should().BeFalse();

        var diagnostics = new List<ImportDiagnostic>();
        ProductMapDocument fixedDoc = ProseClamp.Normalize(raw, diagnostics);

        fixedDoc.RoutingStrategy!.Length.Should().BeLessThanOrEqualTo(120);
        fixedDoc.RoutingStrategy.Should().EndWith(Ellipsis);
        ProductMapValidator.Validate(fixedDoc).IsValid.Should().BeTrue();
        diagnostics.Should().ContainSingle();
        diagnostics[0].Severity.Should().Be(ImportDiagnosticSeverity.Warn);
        diagnostics[0].Path.Should().Be("routingStrategy");
        diagnostics[0].Message.Should().Contain("150").And.Contain("120");
    }

    [Fact]
    public void Exactly_at_cap_is_untouched_and_cap_plus_one_is_trimmed()
    {
        ProseClamp.Trim(new string('a', 120), 120).Should().HaveLength(120).And.NotContain(Ellipsis);
        var diagnostics = new List<ImportDiagnostic>();
        ProseClamp.Normalize(Doc() with { RoutingStrategy = new string('a', 120) }, diagnostics);
        diagnostics.Should().BeEmpty();
        ProseClamp.Trim(new string('a', 121), 120).Should().HaveLength(120).And.EndWith(Ellipsis);
    }

    [Fact]
    public void No_spaces_gets_a_hard_cut()
    {
        string r = ProseClamp.Trim(new string('x', 500), 100);
        r.Should().Be(new string('x', 99) + Ellipsis);
    }

    [Fact]
    public void Never_splits_a_surrogate_pair()
    {
        string text = new string('a', 98) + "\U0001F600\U0001F600\U0001F600";
        string r = ProseClamp.Trim(text, 100);
        r.Length.Should().BeLessThanOrEqualTo(100);
        r.Should().EndWith(Ellipsis);
        for (int i = 0; i < r.Length; i++)
        {
            if (char.IsHighSurrogate(r[i]))
                char.IsLowSurrogate(r[i + 1]).Should().BeTrue();
        }
    }

    [Fact]
    public void Prefers_a_sentence_boundary_in_the_last_part_of_the_window()
    {
        string text = new string('a', 70) + " b. " + new string('c', 10) + " dddd eeee ffff gggg hhhh iiii";
        ProseClamp.Trim(text, 100).Should().Be(new string('a', 70) + " b." + Ellipsis);
    }

    [Fact]
    public void Uses_a_word_boundary_when_there_is_no_sentence()
    {
        string r = ProseClamp.Trim(string.Join(' ', Enumerable.Repeat("word", 40)), 50);
        r.Length.Should().BeLessThanOrEqualTo(50);
        r.Should().EndWith("word" + Ellipsis);
    }

    [Fact]
    public void Control_characters_are_not_trimmed_away()
    {
        string text = new string('a', 200) + "\u0007";
        ProseClamp.Trim(text, 100).Should().Be(text);
        ProductMapValidator.Validate(Doc() with { RoutingStrategy = text }).Violations
            .Should().Contain(v => v.Code == "PM_V06");
    }

    [Fact]
    public void Every_prose_family_in_the_map_is_trimmed_not_rejected()
    {
        string Long(int cap) => Sentenceish(cap + 40);
        ProductMapDocument doc = Doc() with
        {
            Architecture = Long(ProseLimits.Architecture),
            RoutingStrategy = Long(ProseLimits.RoutingStrategy),
            Directories = [new ProductMapDirectory("app", Long(ProseLimits.DirectoryPurpose))],
            EntryPoints = [new ProductMapEntryPoint("app/layout.tsx", Long(ProseLimits.EntryPointRole))],
            Routes = [new ProductMapRoute("/", "app/page.tsx", "page", null, Long(ProseLimits.RouteTitle), false, "llm")],
            Components = [new ProductMapComponent("Button", "components/Button.tsx", Long(ProseLimits.ComponentResponsibility), [], true, "llm")],
            Patterns = [Long(ProseLimits.Pattern)],
        };
        ProductMapValidator.Validate(doc).IsValid.Should().BeFalse();

        var diagnostics = new List<ImportDiagnostic>();
        ProductMapDocument fixedDoc = ProseClamp.Normalize(doc, diagnostics);

        ProductMapValidator.Validate(fixedDoc).IsValid.Should().BeTrue();
        diagnostics.Should().HaveCount(7);
    }

    [Fact]
    public void Voice_prose_is_trimmed_not_rejected()
    {
        var context = new DesignValidationContext(_ => true, new HashSet<string>());
        JsonObject design = MinimalDesign();
        design["voice"] = new JsonObject
        {
            ["summary"] = Sentenceish(640),
            ["tone"] = new JsonArray(Sentenceish(120)),
            ["do"] = new JsonArray(Sentenceish(180)),
            ["dont"] = new JsonArray(Sentenceish(180)),
            ["taglines"] = new JsonArray(Sentenceish(160)),
            ["vocabulary"] = new JsonArray(Sentenceish(80)),
        };
        DesignDocumentValidator.Validate(design, context).IsValid.Should().BeFalse();

        var diagnostics = new List<ImportDiagnostic>();
        ProseClamp.NormalizeVoice(design, diagnostics);

        DesignDocumentValidator.Validate(design, context).IsValid.Should().BeTrue();
        diagnostics.Should().HaveCount(6);
    }

    [Fact]
    public void Structural_violations_still_fail_validation()
    {
        var diagnostics = new List<ImportDiagnostic>();
        ProductMapDocument doc = Doc() with
        {
            Framework = new ProductMapFramework("not-a-framework", "14.2.5", "typescript", [], "pnpm", "site"),
            Directories = [new ProductMapDirectory(new string('p', 300), "routes")],
        };
        ProductMapDocument normalized = ProseClamp.Normalize(doc, diagnostics);
        diagnostics.Should().BeEmpty();
        DesignValidationResult result = ProductMapValidator.Validate(normalized);
        result.Violations.Should().Contain(v => v.Code == "PM_V04");
        result.Violations.Should().Contain(v => v.Code == "PM_V05" && v.Path == "directories[0].path");
    }

    private static string Family(string path) =>
        System.Text.RegularExpressions.Regex.Replace(path, @"\[\d+\]", "[]");

    [Fact]
    public void Every_capped_map_field_is_prose_normalised_or_listed_structural()
    {
        string big = new string('x', 1000);
        ProductMapDocument doc = new(
            ProductMapDocument.SchemaId, big,
            new ProductMapFramework("next", big, "typescript", [big], "pnpm", big),
            big, big,
            [new ProductMapDirectory(big, big)],
            [new ProductMapEntryPoint(big, big)],
            [new ProductMapRoute("/", big, "page", null, big, false, "llm")],
            [new ProductMapComponent(big, big, big, [big], true, "llm")],
            new ProductMapDependencies([new ProductMapDependency(big, big)], [new ProductMapDependency(big, big)], [big]),
            [big],
            new Dictionary<string, ProductMapProvenance>(),
            [new ProductMapPreviewCard(big, big, big, big, big, new string('x', 50000), [big], false)]);

        HashSet<string> capped = ProductMapValidator.Validate(doc).Violations
            .Where(v => v.Code == "PM_V05").Select(v => Family(v.Path)).ToHashSet();

        capped.Should().NotBeEmpty();
        foreach (string family in capped)
        {
            (ProseLimits.ProductMapProse.ContainsKey(family) || ProseLimits.ProductMapStructural.Contains(family))
                .Should().BeTrue($"{family} is capped by the validator but is neither prose nor listed structural");
        }

        ProseLimits.ProductMapProse.Keys.Except(capped).Should().BeEmpty("a prose entry must be a real capped field");
        ProseLimits.ProductMapStructural.Except(capped).Should().BeEmpty("a structural entry must be a real capped field");

        ProductMapDocument normalized = ProseClamp.Normalize(doc, new List<ImportDiagnostic>());
        HashSet<string> after = ProductMapValidator.Validate(normalized).Violations
            .Where(v => v.Code == "PM_V05").Select(v => Family(v.Path)).ToHashSet();
        after.Should().BeEquivalentTo(ProseLimits.ProductMapStructural);
    }

    [Fact]
    public void Every_capped_voice_field_is_prose_normalised()
    {
        string big = new string('x', 1000);
        var context = new DesignValidationContext(_ => true, new HashSet<string>());
        JsonObject design = MinimalDesign();
        design["voice"] = new JsonObject
        {
            ["summary"] = big,
            ["tone"] = new JsonArray(big),
            ["do"] = new JsonArray(big),
            ["dont"] = new JsonArray(big),
            ["taglines"] = new JsonArray(big),
            ["vocabulary"] = new JsonArray(big),
        };
        string[] families = DesignDocumentValidator.Validate(design, context).Violations
            .Where(v => v.Code == "DS_V18").Select(v => v.Path.Split('.')[1].Split('[')[0]).Distinct().ToArray();
        families.Should().BeEquivalentTo(ProseLimits.VoiceProse.Keys);
    }

    [Fact]
    public void Word_boundary_outside_the_window_falls_back_to_a_hard_cut()
    {
        string r = ProseClamp.Trim("a " + new string('x', 500), 100);
        r.Should().Be("a " + new string('x', 97) + Ellipsis);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Tiny_caps_never_split_a_surrogate_pair(int cap)
    {
        string r = ProseClamp.Trim("\U0001F600\U0001F600\U0001F600", cap);
        r.Length.Should().BeLessThanOrEqualTo(cap);
        for (int i = 0; i < r.Length; i++)
        {
            if (char.IsHighSurrogate(r[i]))
                (i + 1 < r.Length && char.IsLowSurrogate(r[i + 1])).Should().BeTrue();
        }
    }

    [Fact]
    public void A_url_like_value_over_the_cap_is_still_refused_after_trimming()
    {
        string url = "https://example.com/" + new string('a', 200);
        var diagnostics = new List<ImportDiagnostic>();
        ProductMapDocument normalized = ProseClamp.Normalize(Doc() with { RoutingStrategy = url }, diagnostics);
        normalized.RoutingStrategy!.Length.Should().BeLessThanOrEqualTo(120);
        ProductMapValidator.Validate(normalized).Violations.Should().Contain(v => v.Code == "PM_V07");

        var context = new DesignValidationContext(_ => true, new HashSet<string>());
        JsonObject design = MinimalDesign();
        design["voice"] = new JsonObject { ["summary"] = url + new string('b', 700) };
        ProseClamp.NormalizeVoice(design, diagnostics);
        DesignDocumentValidator.Validate(design, context).IsValid.Should().BeFalse();
    }

    [Fact]
    public void A_bidi_character_over_the_cap_is_not_trimmed_and_still_fails_in_both_validators()
    {
        string bad = new string('a', 700) + "\u202E";
        var diagnostics = new List<ImportDiagnostic>();
        ProductMapDocument doc = ProseClamp.Normalize(Doc() with { Architecture = bad }, diagnostics);
        doc.Architecture.Should().Be(bad);
        diagnostics.Should().BeEmpty();
        ProductMapValidator.Validate(doc).Violations.Should().Contain(v => v.Code == "PM_V06");

        var context = new DesignValidationContext(_ => true, new HashSet<string>());
        JsonObject design = MinimalDesign();
        design["voice"] = new JsonObject { ["summary"] = bad };
        ProseClamp.NormalizeVoice(design, diagnostics);
        diagnostics.Should().BeEmpty();
        DesignDocumentValidator.Validate(design, context).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Wordy_framework_projectType_is_trimmed_not_fatal()
    {
        ProductMapDocument doc = Doc() with
        {
            Framework = new ProductMapFramework("next", "14.2.5", "typescript", [], "pnpm", Sentenceish(90)),
        };
        ProductMapDocument normalized = ProseClamp.Normalize(doc, new List<ImportDiagnostic>());
        ProductMapValidator.Validate(normalized).IsValid.Should().BeTrue();
    }

    private static JsonObject MinimalDesign() => new()
    {
        ["schema"] = DesignDocument.Schema,
    };

    private static ProductMapDocument Doc() => new(
        ProductMapDocument.SchemaId,
        "Acme",
        new ProductMapFramework("next", "14.2.5", "typescript", ["tailwind"], "pnpm", "marketing site"),
        "App Router marketing site",
        "file conventions",
        [new ProductMapDirectory("app", "routes")],
        [new ProductMapEntryPoint("app/layout.tsx", "root layout")],
        [new ProductMapRoute("/", "app/page.tsx", "page", null, "Home", false, "llm")],
        [new ProductMapComponent("Button", "components/ui/Button.tsx", "primary button", ["label"], true, "llm")],
        new ProductMapDependencies([new ProductMapDependency("next", "14.2.5")], [], []),
        ["server components"],
        new Dictionary<string, ProductMapProvenance>
        {
            ["framework"] = new("llm", []),
            ["routes"] = new("llm", []),
            ["components"] = new("llm", []),
            ["dependencies"] = new("llm", []),
            ["entryPoints"] = new("llm", []),
        });
}
