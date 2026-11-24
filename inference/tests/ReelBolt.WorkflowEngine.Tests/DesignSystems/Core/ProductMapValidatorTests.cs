using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.Shared.DesignSystems;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Core;

public class ProductMapValidatorTests
{
    [Fact]
    public void ValidDocument_HasNoViolations()
    {
        DesignValidationResult result = ProductMapValidator.Validate(ValidDocument());

        result.IsValid.Should().BeTrue();
        result.Violations.Should().BeEmpty();
    }

    [Fact]
    public void PM_V01_RejectsDocumentOver256KiB()
    {
        var routes = new List<ProductMapRoute>();
        for (int i = 0; i < 300; i++)
        {
            routes.Add(new ProductMapRoute(
                "/" + i.ToString("D3") + new string('a', 1000),
                "r.tsx",
                "page",
                null,
                null,
                false,
                null));
        }

        DesignValidationResult result = ProductMapValidator.Validate(ValidDocument() with { Routes = routes });

        result.Violations.Should().OnlyContain(v => v.Code == "PM_V01");
        result.Violations.Should().NotBeEmpty();
    }

    [Fact]
    public void PM_V02_RejectsWrongSchema()
    {
        DesignValidationResult result = ProductMapValidator.Validate(
            ValidDocument() with { Schema = "reelbolt.product-map/2" });

        result.Violations.Should().ContainSingle().Which.Code.Should().Be("PM_V02");
    }

    [Fact]
    public void PM_V03_ToNodeEmitsOnlyAllowedTopLevelKeys()
    {
        // A typed document cannot carry an unknown key, so this pins the writer instead: any key
        // ToNode invented would surface as a PM_V03 violation.
        string[] allowed =
        [
            "schema", "name", "framework", "architecture", "routingStrategy", "directories",
            "entryPoints", "routes", "components", "dependencies", "patterns", "provenance",
            "previewCards",
        ];

        ProductMapJson.ToNode(ValidDocument()).Select(property => property.Key)
            .Should().OnlyContain(key => allowed.Contains(key));
        ProductMapValidator.Validate(ValidDocument()).Violations.Should().NotContain(v => v.Code == "PM_V03");
    }

    [Fact]
    public void PM_V04_RejectsUnknownEnumValues()
    {
        ProductMapDocument doc = ValidDocument() with
        {
            Framework = ValidDocument().Framework with { Name = "django" },
            Routes = [new ProductMapRoute("/screen", "app/screen.tsx", "screen", null, null, false, null)],
            Provenance = new Dictionary<string, ProductMapProvenance>
            {
                ["framework"] = new("guess", []),
            },
        };

        DesignValidationResult result = ProductMapValidator.Validate(doc);

        result.Violations.Should().NotBeEmpty();
        result.Violations.Should().OnlyContain(v => v.Code == "PM_V04");
    }

    [Fact]
    public void PM_V05_RejectsOverLongStrings()
    {
        ProductMapDocument doc = ValidDocument() with
        {
            Name = new string('a', 121),
            Architecture = new string('a', 601),
            Routes =
            [
                new ProductMapRoute("/", "app/page.tsx", "page", null, new string('t', 81), false, null),
            ],
        };

        DesignValidationResult result = ProductMapValidator.Validate(doc);

        result.Violations.Should().NotBeEmpty();
        result.Violations.Should().OnlyContain(v => v.Code == "PM_V05");
    }

    [Fact]
    public void PM_V06_RejectsControlAndBidiCharacters()
    {
        ProductMapDocument doc = ValidDocument() with
        {
            Architecture = "arch\u0000itecture",
            Patterns = ["pattern\u202Ereversed"],
        };

        DesignValidationResult result = ProductMapValidator.Validate(doc);

        result.Violations.Should().NotBeEmpty();
        result.Violations.Should().OnlyContain(v => v.Code == "PM_V06");
    }

    [Fact]
    public void PM_V07_RejectsUrlLikeStrings()
    {
        ProductMapDocument doc = ValidDocument() with
        {
            Patterns = ["https://evil.example", "javascript:alert(1)", "data:text/plain,abc"],
        };

        DesignValidationResult result = ProductMapValidator.Validate(doc);

        result.Violations.Should().NotBeEmpty();
        result.Violations.Should().OnlyContain(v => v.Code == "PM_V07");
    }

    [Fact]
    public void PM_V08_RejectsOverCapLists()
    {
        var routes = new List<ProductMapRoute>();
        for (int i = 0; i < 301; i++)
            routes.Add(new ProductMapRoute("/r" + i, "r" + i + ".tsx", "page", null, null, false, null));

        var directories = new List<ProductMapDirectory>();
        for (int i = 0; i < 21; i++)
            directories.Add(new ProductMapDirectory("dir" + i, "purpose"));

        DesignValidationResult result = ProductMapValidator.Validate(
            ValidDocument() with { Routes = routes, Directories = directories });

        result.Violations.Should().NotBeEmpty();
        result.Violations.Should().OnlyContain(v => v.Code == "PM_V08");
        result.Violations.Should().Contain(v => v.Path == "directories");
        result.Violations.Should().Contain(v => v.Path == "routes");
    }

    [Fact]
    public void PM_V09_RejectsPagePathWithoutLeadingSlash()
    {
        DesignValidationResult result = ProductMapValidator.Validate(
            ValidDocument() with
            {
                Routes = [new ProductMapRoute("pricing", "app/pricing/page.tsx", "page", null, null, false, null)],
            });

        result.Violations.Should().ContainSingle().Which.Code.Should().Be("PM_V09");
    }

    [Theory]
    [InlineData("/abs/Button.tsx")]
    [InlineData("../Button.tsx")]
    [InlineData("components\\Button.tsx")]
    [InlineData("C:\\x\\Button.tsx")]
    public void PM_V10_RejectsNonRelativeComponentPaths(string path)
    {
        DesignValidationResult result = ProductMapValidator.Validate(
            ValidDocument() with
            {
                Components = [new ProductMapComponent("Button", path, "primary", [], false, null)],
            });

        result.Violations.Should().ContainSingle().Which.Code.Should().Be("PM_V10");
    }

    [Fact]
    public void PM_V11_RejectsMalformedDependencyName()
    {
        DesignValidationResult result = ProductMapValidator.Validate(
            ValidDocument() with
            {
                Dependencies = new ProductMapDependencies(
                    [new ProductMapDependency("Bad Name!", "1.0.0")],
                    [],
                    []),
            });

        result.Violations.Should().ContainSingle().Which.Code.Should().Be("PM_V11");
    }

    [Fact]
    public void PM_V12_RejectsDuplicates()
    {
        DesignValidationResult result = ProductMapValidator.Validate(
            ValidDocument() with
            {
                Components =
                [
                    new ProductMapComponent("Button", "components/ui/Button.tsx", "a", [], false, null),
                    new ProductMapComponent("Button", "components/ui/Button.tsx", "b", [], false, null),
                ],
                Routes =
                [
                    new ProductMapRoute("/api/hello", "a.ts", "api", "GET", null, false, null),
                    new ProductMapRoute("/api/hello", "b.ts", "api", "GET", null, false, null),
                ],
            });

        result.Violations.Should().NotBeEmpty();
        result.Violations.Should().OnlyContain(v => v.Code == "PM_V12");
    }

    [Fact]
    public void Preview_cards_are_an_allowed_top_level_key()
    {
        ProductMapDocument doc = ValidDocument() with
        {
            PreviewCards = [new ProductMapPreviewCard("Core", "Components", "Button", "700x360",
                "components/core/core.card.html", "Save", ["logo.svg"], false)],
        };

        ProductMapJson.ToNode(doc).Select(property => property.Key)
            .Should().Contain("previewCards");
        ProductMapValidator.Validate(doc).Violations.Should().NotContain(v => v.Code == "PM_V03");
    }

    [Fact]
    public void PM_V05_RejectsOverLongPreviewCardName()
    {
        ProductMapDocument doc = ValidDocument() with
        {
            PreviewCards = [Card() with { Name = new string('a', 81) }],
        };

        DesignValidationResult result = ProductMapValidator.Validate(doc);

        result.Violations.Should().ContainSingle().Which.Code.Should().Be("PM_V05");
    }

    [Fact]
    public void PM_V07_RejectsAbsolutePreviewCardAsset()
    {
        ProductMapDocument doc = ValidDocument() with
        {
            PreviewCards = [Card() with { Assets = ["https://evil.example/logo.png"] }],
        };

        DesignValidationResult result = ProductMapValidator.Validate(doc);

        result.Violations.Should().ContainSingle().Which.Code.Should().Be("PM_V07");
    }

    [Fact]
    public void PM_V10_RejectsPreviewCardPathWithParentTraversal()
    {
        ProductMapDocument doc = ValidDocument() with
        {
            PreviewCards = [Card() with { Path = "components/../secret.html" }],
        };

        DesignValidationResult result = ProductMapValidator.Validate(doc);

        result.Violations.Should().ContainSingle().Which.Code.Should().Be("PM_V10");
    }

    [Fact]
    public void PM_V08_RejectsOverCapPreviewCards()
    {
        var cards = new List<ProductMapPreviewCard>();
        for (int i = 0; i < 101; i++)
            cards.Add(Card() with { Name = "Card " + i, Path = $"cards/c{i}.card.html" });

        DesignValidationResult result = ProductMapValidator.Validate(
            ValidDocument() with { PreviewCards = cards });

        result.Violations.Should().NotBeEmpty();
        result.Violations.Should().OnlyContain(v => v.Code == "PM_V08");
        result.Violations.Should().Contain(v => v.Path == "previewCards");
    }

    [Fact]
    public void PM_V08_RejectsOverCapPreviewCardAssets()
    {
        var assets = new List<string>();
        for (int i = 0; i < 9; i++)
            assets.Add($"a{i}.png");

        DesignValidationResult result = ProductMapValidator.Validate(
            ValidDocument() with { PreviewCards = [Card() with { Assets = assets }] });

        result.Violations.Should().NotBeEmpty();
        result.Violations.Should().OnlyContain(v => v.Code == "PM_V08");
        result.Violations.Should().Contain(v => v.Path == "previewCards[0].assets");
    }

    private static ProductMapPreviewCard Card() => new(
        "Core",
        "Components",
        "Button",
        "700x360",
        "components/core/core.card.html",
        "Save",
        ["logo.svg"],
        false);

    private static ProductMapDocument ValidDocument() => new(
        ProductMapDocument.SchemaId,
        "Acme",
        new ProductMapFramework("next", "14.2.5", "typescript", ["tailwind"], "pnpm", "marketing site"),
        "App Router marketing site",
        "file conventions",
        [new ProductMapDirectory("app", "routes")],
        [new ProductMapEntryPoint("app/layout.tsx", "root layout")],
        [
            new ProductMapRoute("/", "app/page.tsx", "page", null, "Home", false, "llm"),
            new ProductMapRoute("/api/hello", "app/api/hello/route.ts", "api", "GET", null, false, "llm"),
        ],
        [new ProductMapComponent("Button", "components/ui/Button.tsx", "primary button", ["label"], true, "llm")],
        new ProductMapDependencies(
            [new ProductMapDependency("next", "14.2.5")],
            [new ProductMapDependency("typescript", "")],
            ["tailwind"]),
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
