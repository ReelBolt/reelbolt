using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using ReelBolt.Shared.DesignSystems;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Core;

/// <summary>
/// One rule per <c>DS_Vxx</c> code: a document that breaks it (asserting the exact code) and the
/// valid fixture that does not. The fixture also proves the whole rule set accepts a realistic
/// document, and a seeded mutation run proves the validator never throws.
/// </summary>
public class DesignDocumentValidatorTests
{
    private const string MinimalJson =
        """
        {
          "schema": "reelbolt.design-system/1",
          "color": {
            "primary": { "$type": "color", "$value": { "hex": "#123456", "colorSpace": "srgb" } }
          },
          "typography": {
            "family": { "primary": { "$type": "fontFamily", "$value": ["Inter", "sans-serif"] } },
            "weight": { "bold": { "$type": "fontWeight", "$value": 700 } },
            "line": { "normal": { "$type": "number", "$value": 1.5 } },
            "heading": {
              "h1": {
                "$type": "typography",
                "$value": {
                  "fontFamily": ["Inter"],
                  "fontSize": { "value": 32, "unit": "px" },
                  "fontWeight": 700,
                  "lineHeight": 1.2,
                  "letterSpacing": { "value": 0, "unit": "px" }
                }
              }
            }
          },
          "spacing": {
            "md": { "$type": "dimension", "$value": { "value": 16, "unit": "px" } }
          },
          "shadow": {
            "sm": {
              "$type": "shadow",
              "$value": {
                "color": { "hex": "#00000033", "colorSpace": "srgb" },
                "offsetY": { "value": 1, "unit": "px" },
                "blur": { "value": 2, "unit": "px" }
              }
            }
          },
          "motion": {
            "fast": { "$type": "duration", "$value": { "value": 100, "unit": "ms" } },
            "ease": { "$type": "cubicBezier", "$value": [0.2, 0, 0, 1] }
          }
        }
        """;

    private static readonly Guid LogoGuid = Guid.Parse("0f9a6a80-1e2b-4c3d-8f10-9a7b6c5d4e3f");

    private static readonly HashSet<string> BundledFontNames = new(StringComparer.Ordinal)
    {
        "Inter", "Montserrat", "Poppins", "BebasNeue", "Anton", "DejaVu Sans", "Liberation Sans",
        "Liberation Serif", "Liberation Mono", "Roboto", "Open Sans", "Lato", "Oswald", "Raleway",
    };

    private static DesignValidationContext Context() =>
        new(projectFileId => projectFileId == LogoGuid, BundledFontNames);

    private static JsonObject Parse(string json) => (JsonObject)JsonNode.Parse(json)!;

    private static JsonNode Value(string json) => JsonNode.Parse(json)!;

    private static JsonObject Minimal() => Parse(MinimalJson);

    private static JsonObject Fixture()
    {
        string path = Path.Combine(CallerDirectory(), "..", "Fixtures", "design-valid.json");
        return (JsonObject)JsonNode.Parse(File.ReadAllText(path))!;
    }

    private static string CallerDirectory([CallerFilePath] string callerFilePath = "") =>
        Path.GetDirectoryName(callerFilePath) ?? string.Empty;

    private static DesignValidationResult Validate(JsonObject doc) =>
        DesignDocumentValidator.Validate(doc, Context());

    private static bool Has(DesignValidationResult result, string code) =>
        result.Violations.Any(violation => violation.Code == code);

    private static JsonObject ColorToken() => new()
    {
        ["$type"] = "color",
        ["$value"] = Value("""{ "hex": "#123456", "colorSpace": "srgb" }"""),
    };

    private static JsonObject Color(JsonObject root) => (JsonObject)root["color"]!;

    private static JsonObject Primary(JsonObject root) => (JsonObject)Color(root)["primary"]!;

    // ---- Fixture -------------------------------------------------------------------------------

    [Fact]
    public void ValidFixture_IsAccepted_AndHoldsAtLeastThirtyTokens()
    {
        JsonObject fixture = Fixture();
        DesignDocument.Tokens(fixture).Should().HaveCountGreaterThanOrEqualTo(30);

        DesignValidationResult result = Validate(fixture);
        result.IsValid.Should().BeTrue(
            string.Join(" | ", result.Violations.Select(v => $"{v.Code}@{v.Path}: {v.Message}")));
    }

    [Fact]
    public void Z_Group_And_Keyword_Type_Are_Canonical()
    {
        JsonObject doc = Minimal();
        doc["z"] = new JsonObject
        {
            ["drawer"] = new JsonObject { ["$type"] = "number", ["$value"] = 1100 },
        };
        ((JsonObject)doc["typography"]!)["transform"] = new JsonObject
        {
            ["button"] = new JsonObject { ["$type"] = "keyword", ["$value"] = "uppercase" },
        };

        DesignValidationResult result = Validate(doc);

        result.IsValid.Should().BeTrue(
            string.Join(" | ", result.Violations.Select(v => $"{v.Code}@{v.Path}: {v.Message}")));
    }

    [Fact]
    public void V09_Keyword_Value_Must_Match_The_Canonical_Form()
    {
        JsonObject bad = Minimal();
        ((JsonObject)bad["typography"]!)["transform"] = new JsonObject
        {
            ["button"] = new JsonObject { ["$type"] = "keyword", ["$value"] = "Uppercase!" },
        };
        Has(Validate(bad), "DS_V09").Should().BeTrue();

        JsonObject tooLong = Minimal();
        ((JsonObject)tooLong["typography"]!)["transform"] = new JsonObject
        {
            ["button"] = new JsonObject { ["$type"] = "keyword", ["$value"] = "a" + new string('b', 40) },
        };
        Has(Validate(tooLong), "DS_V09").Should().BeTrue();
    }

    // ---- V-1..V-22 -----------------------------------------------------------------------------

    [Fact]
    public void V01_Size()
    {
        JsonObject bad = Minimal();
        bad["name"] = new string('a', 262200);
        Has(Validate(bad), "DS_V01").Should().BeTrue();
        Has(Validate(Minimal()), "DS_V01").Should().BeFalse();
    }

    [Fact]
    public void V02_Schema()
    {
        JsonObject bad = Minimal();
        bad["schema"] = "reelbolt.design-system/2";
        Has(Validate(bad), "DS_V02").Should().BeTrue();

        JsonObject missing = Minimal();
        missing.Remove("schema");
        Has(Validate(missing), "DS_V02").Should().BeTrue();
        Has(Validate(Minimal()), "DS_V02").Should().BeFalse();
    }

    [Fact]
    public void V03_UnknownTopLevelKey()
    {
        JsonObject bad = Minimal();
        bad["mystery"] = 1;
        Has(Validate(bad), "DS_V03").Should().BeTrue();
        Has(Validate(Minimal()), "DS_V03").Should().BeFalse();
    }

    [Fact]
    public void V04_KeyForm()
    {
        JsonObject bad = Minimal();
        Color(bad).Add("Bad Key", ColorToken());
        Has(Validate(bad), "DS_V04").Should().BeTrue();
        Has(Validate(Minimal()), "DS_V04").Should().BeFalse();
    }

    [Fact]
    public void V05_UnknownDollarKey()
    {
        JsonObject bad = Minimal();
        Primary(bad).Add("$bogus", 1);
        Has(Validate(bad), "DS_V05").Should().BeTrue();
        Has(Validate(Minimal()), "DS_V05").Should().BeFalse();
    }

    [Fact]
    public void V06_Depth()
    {
        JsonObject bad = Minimal();
        var group = new JsonObject();
        Color(bad).Add("nested", group);
        for (int i = 0; i < 5; i++)
        {
            var next = new JsonObject();
            group.Add("g", next);
            group = next;
        }

        group.Add("leaf", ColorToken());

        Has(Validate(bad), "DS_V06").Should().BeTrue();
        Has(Validate(Minimal()), "DS_V06").Should().BeFalse();
    }

    [Fact]
    public void V07_TokenCount()
    {
        JsonObject bad = DesignDocument.New();
        var color = new JsonObject();
        bad["color"] = color;
        for (int i = 0; i < 1501; i++)
            color[$"t{i}"] = ColorToken();

        Has(Validate(bad), "DS_V07").Should().BeTrue();
        Has(Validate(Minimal()), "DS_V07").Should().BeFalse();
    }

    [Fact]
    public void V08_Type()
    {
        JsonObject bad = Minimal();
        Primary(bad)["$type"] = "palette";
        Has(Validate(bad), "DS_V08").Should().BeTrue();

        JsonObject missing = Minimal();
        Primary(missing).Remove("$type");
        Has(Validate(missing), "DS_V08").Should().BeTrue();
        Has(Validate(Minimal()), "DS_V08").Should().BeFalse();
    }

    [Fact]
    public void V09_CanonicalValue()
    {
        JsonObject bad = Minimal();
        Primary(bad)["$value"] = Value("""{ "hex": "#GGGGGG", "colorSpace": "srgb" }""");
        Has(Validate(bad), "DS_V09").Should().BeTrue();
        Has(Validate(Minimal()), "DS_V09").Should().BeFalse();
    }

    [Fact]
    public void V10_Description()
    {
        JsonObject bad = Minimal();
        Primary(bad)["$description"] = new string('a', 201);
        Has(Validate(bad), "DS_V10").Should().BeTrue();

        JsonObject markup = Minimal();
        Primary(markup)["$description"] = "Hello [World]";
        Has(Validate(markup), "DS_V10").Should().BeTrue();
        Has(Validate(Minimal()), "DS_V10").Should().BeFalse();
    }

    [Fact]
    public void V11_Extensions()
    {
        JsonObject bad = Minimal();
        Primary(bad)["$extensions"] = Value("""{ "other": {} }""");
        Has(Validate(bad), "DS_V11").Should().BeTrue();

        JsonObject unknownInner = Minimal();
        Primary(unknownInner)["$extensions"] = Value("""{ "com.reelbolt": { "mystery": 1 } }""");
        Has(Validate(unknownInner), "DS_V11").Should().BeTrue();
        Has(Validate(Minimal()), "DS_V11").Should().BeFalse();
    }

    [Fact]
    public void V12_Alias()
    {
        JsonObject bad = Minimal();
        bad["component"] = Value("""{ "button": { "bg": { "$type": "color", "$value": "{color.missing}" } } }""");
        Has(Validate(bad), "DS_V12").Should().BeTrue();

        JsonObject good = Minimal();
        good["component"] = Value("""{ "button": { "bg": { "$type": "color", "$value": "{color.primary}" } } }""");
        Has(Validate(good), "DS_V12").Should().BeFalse();
    }

    [Fact]
    public void V13_Confidence()
    {
        JsonObject bad = Minimal();
        Primary(bad)["$extensions"] = Value("""{ "com.reelbolt": { "conf": 1.5 } }""");
        Has(Validate(bad), "DS_V13").Should().BeTrue();

        JsonObject good = Minimal();
        Primary(good)["$extensions"] = Value("""{ "com.reelbolt": { "conf": 0.5 } }""");
        Has(Validate(good), "DS_V13").Should().BeFalse();
    }

    [Fact]
    public void V14_Provenance()
    {
        JsonObject bad = Minimal();
        Primary(bad)["$extensions"] = Value("""{ "com.reelbolt": { "prov": { "method": "magic" } } }""");
        Has(Validate(bad), "DS_V14").Should().BeTrue();

        JsonObject longLocation = Minimal();
        Primary(longLocation)["$extensions"] =
            Value($$"""{ "com.reelbolt": { "prov": { "method": "static", "loc": "{{new string('x', 201)}}" } } }""");
        Has(Validate(longLocation), "DS_V14").Should().BeTrue();
        Has(Validate(Minimal()), "DS_V14").Should().BeFalse();
    }

    [Fact]
    public void V15_Assets()
    {
        JsonObject bad = Minimal();
        bad["assets"] = Value($$"""{ "logo": { "fileRef": "project-file:{{Guid.NewGuid():D}}" } }""");
        Has(Validate(bad), "DS_V15").Should().BeTrue();

        JsonObject malformed = Minimal();
        malformed["assets"] = Value("""{ "logo": { "fileRef": "https://example.com/logo.svg" } }""");
        Has(Validate(malformed), "DS_V15").Should().BeTrue();

        JsonObject good = Minimal();
        good["assets"] = Value($$"""{ "logo": { "fileRef": "project-file:{{LogoGuid:D}}" } }""");
        Has(Validate(good), "DS_V15").Should().BeFalse();
    }

    [Fact]
    public void V15_BrandMetadata_is_accepted_when_valid()
    {
        JsonObject good = Minimal();
        good["assets"] = Value($$"""
            { "logo": { "fileRef": "project-file:{{LogoGuid:D}}", "file": "mark.svg", "subject": "product", "form": "mark",
              "tone": "mid", "readableOn": ["light", "dark"], "luminance": 0.25,
              "variants": [ { "name": "publisher", "fileRef": "project-file:{{LogoGuid:D}}", "file": "co.png",
                "subject": "publisher", "form": "lockup", "tone": "light", "readableOn": ["dark"], "luminance": 0.9 } ] } }
            """);
        Has(Validate(good), "DS_V15").Should().BeFalse();
    }

    [Theory]
    [InlineData("\"subject\": \"owner\"")]
    [InlineData("\"form\": \"banner\"")]
    [InlineData("\"tone\": \"grey\"")]
    [InlineData("\"readableOn\": [\"light\", \"light\"]")]
    [InlineData("\"readableOn\": [\"light\", \"dark\", \"mid\"]")]
    [InlineData("\"readableOn\": \"light\"")]
    [InlineData("\"luminance\": 1.5")]
    [InlineData("\"luminance\": \"0.5\"")]
    [InlineData("\"file\": \"<b>x</b>\"")]
    public void V15_BrandMetadata_rejects_invalid_values_on_the_slot_and_on_a_variant(string field)
    {
        JsonObject onSlot = Minimal();
        onSlot["assets"] = Value($$"""{ "logo": { "fileRef": "project-file:{{LogoGuid:D}}", {{field}} } }""");
        Has(Validate(onSlot), "DS_V15").Should().BeTrue();

        JsonObject onVariant = Minimal();
        onVariant["assets"] = Value($$"""
            { "logo": { "fileRef": "project-file:{{LogoGuid:D}}",
              "variants": [ { "name": "v", "fileRef": "project-file:{{LogoGuid:D}}", {{field}} } ] } }
            """);
        Has(Validate(onVariant), "DS_V15").Should().BeTrue();
    }

    [Fact]
    public void V15_LibraryAssetRefs()
    {
        Guid assetId = Guid.NewGuid();
        var libraryContext = new DesignValidationContext(
            _ => false, BundledFontNames, id => id == assetId);

        JsonObject own = Minimal();
        own["assets"] = Value($$"""
            { "logo": { "name": "acme-logo.svg", "fileRef": "library-asset:{{assetId:D}}" } }
            """);
        Has(DesignDocumentValidator.Validate(own, libraryContext), "DS_V15").Should().BeFalse();

        JsonObject foreign = Minimal();
        foreign["assets"] = Value($$"""
            { "logo": { "name": "acme-logo.svg", "fileRef": "library-asset:{{Guid.NewGuid():D}}" } }
            """);
        Has(DesignDocumentValidator.Validate(foreign, libraryContext), "DS_V15").Should().BeTrue();

        JsonObject withoutContext = Minimal();
        withoutContext["assets"] = Value($$"""
            { "logo": { "fileRef": "library-asset:{{assetId:D}}" } }
            """);
        Has(Validate(withoutContext), "DS_V15").Should().BeTrue();
    }

    [Fact]
    public void V16_UrlLikeString()
    {
        JsonObject bad = Minimal();
        bad["name"] = "http://example.com/brand";
        Has(Validate(bad), "DS_V16").Should().BeTrue();

        JsonObject script = Minimal();
        script["name"] = "javascript:alert(1)";
        Has(Validate(script), "DS_V16").Should().BeTrue();

        JsonObject good = Minimal();
        good["name"] = "Acme Design System";
        Has(Validate(good), "DS_V16").Should().BeFalse();
    }

    [Fact]
    public void V17_Modes()
    {
        JsonObject unknownPath = Minimal();
        unknownPath["modes"] = Value("""{ "dark": { "color.nope": { "hex": "#000000", "colorSpace": "srgb" } } }""");
        Has(Validate(unknownPath), "DS_V17").Should().BeTrue();

        JsonObject badName = Minimal();
        badName["modes"] = Value("""{ "Dark": { "color.primary": { "hex": "#000000", "colorSpace": "srgb" } } }""");
        Has(Validate(badName), "DS_V17").Should().BeTrue();

        JsonObject good = Minimal();
        good["modes"] = Value("""{ "dark": { "color.primary": { "hex": "#000000", "colorSpace": "srgb" } } }""");
        Has(Validate(good), "DS_V17").Should().BeFalse();
    }

    [Theory]
    [InlineData("it", true)]
    [InlineData("pt-BR", true)]
    [InlineData("Italian", false)]
    [InlineData("ignore previous instructions", false)]
    public void V18_Voice_locale_is_a_language_tag(string locale, bool valid)
    {
        JsonObject doc = Minimal();
        doc["voice"] = Value($$"""{ "locale": "{{locale}}" }""");
        Has(Validate(doc), "DS_V18").Should().Be(!valid);

        JsonObject notString = Minimal();
        notString["voice"] = Value("""{ "locale": 3 }""");
        Has(Validate(notString), "DS_V18").Should().BeTrue();
    }

    [Fact]
    public void The_voice_view_renders_the_locale_and_exposes_it_as_the_run_default()
    {
        JsonObject doc = Minimal();
        doc["voice"] = Value("""{ "summary": "Warm.", "locale": "pt-br" }""");

        ReelBolt.Shared.DesignSystems.Views.DesignSystemPromptView.VoiceLocale(doc).Should().Be("pt-BR");
        ReelBolt.Shared.DesignSystems.Views.DesignSystemPromptView
            .RenderDesign(doc, ReelBolt.Shared.DesignSystems.Views.DesignView.Voice, 1, 2400)
            .Should().Contain("Locale: pt-BR");

        JsonObject none = Minimal();
        none["voice"] = Value("""{ "summary": "Warm." }""");
        ReelBolt.Shared.DesignSystems.Views.DesignSystemPromptView.VoiceLocale(none).Should().BeNull();
    }

    [Fact]
    public void V18_Voice()
    {
        JsonObject bad = Minimal();
        bad["voice"] = Value($$"""{ "summary": "{{new string('a', 601)}}" }""");
        Has(Validate(bad), "DS_V18").Should().BeTrue();

        JsonObject tooMany = Minimal();
        string items = string.Join(",", Enumerable.Range(0, 13).Select(_ => "\"x\""));
        tooMany["voice"] = Value($$"""{ "tone": [{{items}}] }""");
        Has(Validate(tooMany), "DS_V18").Should().BeTrue();

        JsonObject good = Minimal();
        good["voice"] = Value("""{ "summary": "Short.", "tone": ["warm"] }""");
        Has(Validate(good), "DS_V18").Should().BeFalse();
    }

    [Fact]
    public void V19_Fonts()
    {
        JsonObject badLicence = Minimal();
        badLicence["fonts"] = Value("""{ "licence": { "Inter": "maybe" } }""");
        Has(Validate(badLicence), "DS_V19").Should().BeTrue();

        JsonObject badResolved = Minimal();
        badResolved["fonts"] = Value("""{ "resolved": { "Inter": "Comic Sans" } }""");
        Has(Validate(badResolved), "DS_V19").Should().BeTrue();

        JsonObject good = Minimal();
        good["fonts"] = Value("""{ "licence": { "Inter": "attested" }, "resolved": { "Inter": "Inter" } }""");
        Has(Validate(good), "DS_V19").Should().BeFalse();
    }

    [Fact]
    public void V19_FontFiles()
    {
        JsonObject missingFile = Minimal();
        missingFile["fonts"] = Value($$"""{ "files": { "Inter": "project-file:{{Guid.NewGuid():D}}" } }""");
        Has(Validate(missingFile), "DS_V19").Should().BeTrue();

        JsonObject badValue = Minimal();
        badValue["fonts"] = Value("""{ "files": { "Inter": "https://example.test/i.woff2" } }""");
        Has(Validate(badValue), "DS_V19").Should().BeTrue();

        JsonObject notAnObject = Minimal();
        notAnObject["fonts"] = Value("""{ "files": [] }""");
        Has(Validate(notAnObject), "DS_V19").Should().BeTrue();

        JsonObject longKey = Minimal();
        longKey["fonts"] = Value($$"""{ "files": { "{{new string('a', 81)}}": "project-file:{{LogoGuid:D}}" } }""");
        Has(Validate(longKey), "DS_V19").Should().BeTrue();

        string many = string.Join(",", Enumerable.Range(0, 21)
            .Select(i => $"\"Family{i}\": \"project-file:{LogoGuid:D}\""));
        JsonObject tooMany = Minimal();
        tooMany["fonts"] = Value($$"""{ "files": { {{many}} } }""");
        Has(Validate(tooMany), "DS_V19").Should().BeTrue();

        JsonObject good = Minimal();
        good["fonts"] = Value($$"""{ "files": { "Inter": "project-file:{{LogoGuid:D}}" } }""");
        Has(Validate(good), "DS_V19").Should().BeFalse();
    }

    [Fact]
    public void V20_ControlCharacters()
    {
        JsonObject bidi = Minimal();
        bidi["name"] = "a\u202Eb";
        Has(Validate(bidi), "DS_V20").Should().BeTrue();

        JsonObject control = Minimal();
        control["name"] = "a\u0000b";
        Has(Validate(control), "DS_V20").Should().BeTrue();
        Has(Validate(Minimal()), "DS_V20").Should().BeFalse();
    }

    [Theory]
    [InlineData("a\U000E0041b")]
    [InlineData("a\U000E0001b")]
    [InlineData("a\U000E007Fb")]
    [InlineData("a\uFE0Fb")]
    [InlineData("a\U000E0100b")]
    public void V20_RejectsSurrogateEncodedTagCharactersAndVariationSelectors(string value)
    {
        JsonObject doc = Minimal();
        doc["name"] = value;

        Has(Validate(doc), "DS_V20").Should().BeTrue();
    }

    [Fact]
    public void V21_FontFamilyCharacters()
    {
        JsonObject bad = Minimal();
        JsonObject family = (JsonObject)((JsonObject)((JsonObject)bad["typography"]!)["family"]!)["primary"]!;
        family["$value"] = Value("""["Inter;"]""");
        Has(Validate(bad), "DS_V21").Should().BeTrue();
        Has(Validate(Minimal()), "DS_V21").Should().BeFalse();
    }

    [Fact]
    public void V21_CompositeTypographyFontFamilyCharacters()
    {
        JsonObject bad = Minimal();
        JsonObject typography = (JsonObject)bad["typography"]!;
        typography["heading"] = new JsonObject
        {
            ["h1"] = new JsonObject
            {
                ["$type"] = "typography",
                ["$value"] = Value("""{ "fontFamily": ["Inter</style><script>"], "fontWeight": 700 }"""),
            },
        };

        Has(Validate(bad), "DS_V21").Should().BeTrue();

        ((JsonObject)((JsonObject)typography["heading"]!)["h1"]!)["$value"] =
            Value("""{ "fontFamily": ["Inter", "sans-serif"], "fontWeight": 700 }""");
        Has(Validate(bad), "DS_V21").Should().BeFalse();
    }

    [Fact]
    public void V22_DuplicateTokenPath()
    {
        JsonObject bad = Minimal();
        Color(bad).Add("Primary", ColorToken());
        Has(Validate(bad), "DS_V22").Should().BeTrue();
        Has(Validate(Minimal()), "DS_V22").Should().BeFalse();
    }

    // ---- Property-style -------------------------------------------------------------------------

    [Fact]
    public void Validate_NeverThrows_OnTwoHundredMutatedDocuments()
    {
        var random = new Random(20260202);
        for (int i = 0; i < 200; i++)
        {
            JsonObject document = Fixture();
            Mutate(document, random, 0);

            DesignValidationResult result = DesignDocumentValidator.Validate(document, Context());
            result.Should().NotBeNull();
            result.Violations.Should().NotBeNull();
        }
    }

    private static void Mutate(JsonNode? node, Random random, int depth)
    {
        if (node is null || depth > 6)
            return;

        switch (node)
        {
            case JsonObject obj:
                foreach (string key in obj.Select(property => property.Key).ToList())
                {
                    int roll = random.Next(10);
                    if (roll == 0)
                    {
                        obj.Remove(key);
                        continue;
                    }

                    if (roll == 1)
                    {
                        obj[key] = random.Next(5) switch
                        {
                            0 => null,
                            1 => JsonValue.Create(random.Next(-1000, 1000)),
                            2 => JsonValue.Create("mutation"),
                            3 => JsonValue.Create("https://example.com/x"),
                            _ => JsonValue.Create(true),
                        };
                        continue;
                    }

                    if (roll == 2)
                    {
                        var array = new JsonArray();
                        int count = random.Next(4) == 0 ? 5000 : random.Next(5);
                        for (int i = 0; i < count; i++)
                            array.Add(JsonValue.Create(random.Next()));
                        obj[key] = array;
                        continue;
                    }

                    Mutate(obj[key], random, depth + 1);
                }

                break;

            case JsonArray array:
                for (int i = 0; i < array.Count; i++)
                    Mutate(array[i], random, depth + 1);
                break;
        }
    }
}
