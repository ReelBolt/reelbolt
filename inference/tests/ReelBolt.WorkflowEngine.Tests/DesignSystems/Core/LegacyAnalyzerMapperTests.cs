using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using ReelBolt.Shared.Data.OutputSchemas;
using ReelBolt.Shared.DesignSystems;
using Xunit;
using CodeDir = ReelBolt.Shared.Data.OutputSchemas.DirectoryInfo;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Core;

public class LegacyAnalyzerMapperTests
{
    [Fact]
    public void CodeStructure_MapsFrameworkDirectoriesEntryPointsAndArchitecture()
    {
        var cs = new CodeStructureOutput
        {
            ProjectType = "marketing site",
            Framework = "Next.js",
            Directories = [new CodeDir { Path = "app", Purpose = "routes", FileCount = 12 }],
            EntryPoints = ["app/layout.tsx", "app/page.tsx"],
            OverallArchitecture = "App Router marketing site",
        };

        ProductMapDocument doc = LegacyAnalyzerMapper.ProductMapFrom(cs, null, null, null);

        doc.Framework.Name.Should().Be("next");
        doc.Framework.ProjectType.Should().Be("marketing site");
        doc.Directories.Should().Equal(new ProductMapDirectory("app", "routes"));
        doc.EntryPoints.Should().HaveCount(2);
        doc.EntryPoints.Should().OnlyContain(entryPoint => entryPoint.Role == "");
        doc.Architecture.Should().Be("App Router marketing site");
    }

    [Fact]
    public void Dependencies_PutCoreFirstAndDropRecommendations()
    {
        var dep = new DependencyAnalysisOutput
        {
            Dependencies =
            [
                new Dependency { Name = "react", Version = "^18", IsCore = false },
                new Dependency { Name = "next", Version = "14.2.5", IsCore = true },
                new Dependency { Name = "framer-motion", Version = "11", IsCore = false },
            ],
            DevDependencies = ["typescript", "@types/react"],
            PackageManager = "pnpm",
            Recommendations = [new DependencyRecommendation { Type = "add", Description = "tests" }],
        };

        ProductMapDocument doc = LegacyAnalyzerMapper.ProductMapFrom(null, dep, null, null);

        doc.Dependencies.Runtime.Select(dependency => dependency.Name)
            .Should().Equal("next", "react", "framer-motion");
        doc.Dependencies.Dev[0].Should().Be(new ProductMapDependency("typescript", ""));
        doc.Framework.PackageManager.Should().Be("pnpm");
        ProductMapJson.Serialize(doc).Should().NotContain("recommend");
    }

    [Fact]
    public void Components_PutRequiredPropsFirstAndKeepCommonPatterns()
    {
        var comp = new ComponentInventoryOutput
        {
            Components =
            [
                new ComponentInfo
                {
                    Name = "Button",
                    FilePath = "components/ui/Button.tsx",
                    Responsibility = "primary button",
                    Props =
                    [
                        new PropInfo { Name = "onClick", Type = "() => void", Required = true },
                        new PropInfo { Name = "label", Type = "string", Required = true },
                        new PropInfo { Name = "variant", Type = "string", Required = false, DefaultValue = "'solid'" },
                        new PropInfo { Name = "disabled", Type = "boolean", Required = false },
                    ],
                    Dependencies = ["react"],
                },
            ],
            TotalComponents = 1,
            CommonPatterns = ["server components", "islands"],
        };

        ProductMapDocument doc = LegacyAnalyzerMapper.ProductMapFrom(null, null, comp, null);

        doc.Components[0].KeyProps.Should().Equal("onClick", "label", "variant", "disabled");
        doc.Components[0].Core.Should().BeFalse();
        doc.Patterns.Should().Equal("server components", "islands");
    }

    [Fact]
    public void RoutesAndApi_MapsKindsAndMethods()
    {
        var ra = new RouteAndApiOutput
        {
            ClientRoutes =
            [
                new RouteInfo { Path = "/pricing", RequiresAuth = false, ComponentName = "Pricing" },
                new RouteInfo { Path = "/dashboard", RequiresAuth = true, ComponentName = "Dashboard" },
            ],
            ApiEndpoints =
            [
                new ApiEndpoint { Method = "GET", Path = "/api/hello" },
                new ApiEndpoint { Method = "POST", Path = "/api/hello" },
            ],
            RoutingStrategy = "App Router file conventions",
        };

        ProductMapDocument doc = LegacyAnalyzerMapper.ProductMapFrom(null, null, null, ra);

        doc.Routes.Should().HaveCount(4);

        ProductMapRoute[] pages = doc.Routes.Where(route => route.Kind == "page").ToArray();
        pages.Should().OnlyContain(route => route.Method == null);
        pages.Select(route => route.RequiresAuth).Should().Equal(false, true);

        ProductMapRoute[] apis = doc.Routes.Where(route => route.Kind == "api").ToArray();
        apis.Select(route => route.Method).Should().BeEquivalentTo("GET", "POST");

        doc.RoutingStrategy.Should().Be("App Router file conventions");
    }

    [Fact]
    public void Style_MapsColorsFontsSizesAndSpacing()
    {
        var style = new StyleAndThemeOutput
        {
            Colors = new ColorPalette
            {
                Primary = "#6D28D9",
                Additional = new Dictionary<string, string> { ["warning"] = "#F59E0B" },
            },
            Typography = new Typography
            {
                PrimaryFont = "Inter",
                FontSizes = new Dictionary<string, string> { ["xl"] = "1.25rem", ["base"] = "16px" },
            },
            Spacing = new Spacing
            {
                Unit = "px",
                Scale = new Dictionary<string, string> { ["md"] = "16" },
            },
        };

        JsonObject? design = LegacyAnalyzerMapper.DesignFrom(style);

        design.Should().NotBeNull();
        design!["schema"]!.GetValue<string>().Should().Be("reelbolt.design-system/1");

        JsonNode primary = design["color"]!["brand"]!["primary"]!;
        primary["$value"]!["hex"]!.GetValue<string>().Should().Be("#6d28d9");
        primary["$extensions"]!["com.reelbolt"]!["prov"]!["method"]!.GetValue<string>().Should().Be("llm");
        primary["$extensions"]!["com.reelbolt"]!["conf"]!.GetValue<double>().Should().Be(0.5);
        design["color"]!["extra"]!["warning"]!["$value"]!["hex"]!.GetValue<string>().Should().Be("#f59e0b");

        design["typography"]!["family"]!["primary"]!["$type"]!.GetValue<string>().Should().Be("fontFamily");
        design["typography"]!["family"]!["primary"]!["$value"]!.AsArray()[0]!.GetValue<string>().Should().Be("Inter");

        design["typography"]!["size"]!["xl"]!["$value"]!["value"]!.GetValue<double>().Should().Be(1.25);
        design["typography"]!["size"]!["xl"]!["$value"]!["unit"]!.GetValue<string>().Should().Be("rem");
        design["typography"]!["size"]!["base"]!["$value"]!["value"]!.GetValue<double>().Should().Be(16);
        design["typography"]!["size"]!["base"]!["$value"]!["unit"]!.GetValue<string>().Should().Be("px");

        design["spacing"]!["md"]!["$value"]!["value"]!.GetValue<double>().Should().Be(16);
        design["spacing"]!["md"]!["$value"]!["unit"]!.GetValue<string>().Should().Be("px");
    }

    [Fact]
    public void Style_DropsUnsupportedColour()
    {
        var style = new StyleAndThemeOutput { Colors = new ColorPalette { Primary = "linear-gradient(red, blue)" } };

        JsonObject? design = LegacyAnalyzerMapper.DesignFrom(style);

        design.Should().NotBeNull();
        design!["color"].Should().BeNull();
    }

    [Fact]
    public void DesignFrom_NullStyle_ReturnsNull() =>
        LegacyAnalyzerMapper.DesignFrom(null).Should().BeNull();

    [Fact]
    public void ProductMapFrom_AllNullInputs_ReturnsEmptyDocument()
    {
        ProductMapDocument doc = LegacyAnalyzerMapper.ProductMapFrom(null, null, null, null);

        doc.Should().NotBeNull();
        doc.Schema.Should().Be(ProductMapDocument.SchemaId);
        doc.Name.Should().BeNull();
        doc.Directories.Should().BeEmpty();
        doc.EntryPoints.Should().BeEmpty();
        doc.Routes.Should().BeEmpty();
        doc.Components.Should().BeEmpty();
        doc.Patterns.Should().BeEmpty();
        doc.Dependencies.Runtime.Should().BeEmpty();
        doc.Dependencies.Dev.Should().BeEmpty();
        doc.Dependencies.Ui.Should().BeEmpty();
    }
}
