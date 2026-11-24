using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.DesignSystems;
using ReelBolt.WorkflowEngine.Agents.Tools;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Services.DesignSystems;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Engine;

/// <summary>
/// P2-19 coverage for <see cref="DesignSystemAgentTools"/> (SPEC §12): the DesignSystemRead tools
/// answer from the run's ambient pin, page through sections with a case-insensitive filter, report
/// an absent pin as <c>available:false</c> (never an error), cap their output and re-sanitize every
/// string that came out of the stored document.
/// </summary>
public class DesignSystemAgentToolsTests
{
    private static readonly TokenProvenance Prov = new("manual", null, null, null);

    private const string ProductJson =
        """
        {
          "schema": "reelbolt.product-map/1",
          "name": "Acme shop",
          "framework": { "name": "next", "version": "15.1", "language": "typescript", "styling": ["tailwind"], "packageManager": "pnpm", "projectType": "web app" },
          "architecture": "App Router with server components.",
          "directories": [{ "path": "app", "purpose": "Routes" }],
          "entryPoints": [{ "path": "app/layout.tsx", "role": "shell" }],
          "routes": [
            { "path": "/", "file": "app/page.tsx", "kind": "page", "requiresAuth": false, "title": "Home" },
            { "path": "/api/checkout", "file": "app/api/checkout/route.ts", "kind": "api", "method": "POST", "requiresAuth": true },
            { "path": "/pricing", "file": "app/pricing/page.tsx", "kind": "page", "requiresAuth": false, "title": "Pricing" }
          ],
          "components": [
            { "name": "Button", "path": "components/ui/Button.tsx", "responsibility": "Primary button.", "keyProps": ["variant"], "core": true },
            { "name": "Card", "path": "components/ui/Card.tsx", "responsibility": "Content surface.", "keyProps": [], "core": false }
          ],
          "dependencies": {
            "runtime": [{ "name": "next", "version": "15.1.0" }],
            "dev": [{ "name": "typescript", "version": "5.6.0" }],
            "ui": ["@mantine/core"]
          },
          "patterns": ["server components"]
        }
        """;

    [Fact]
    public async Task ReadDesignSystem_summary_reports_groups()
    {
        using IDisposable ambient = DesignSystemAmbient.Enter(State(DesignDoc()));
        DesignSystemAgentTools tools = NewTools();

        JsonObject result = Parse(await tools.ReadDesignSystem());

        result["available"]!.GetValue<bool>().Should().BeTrue();
        result["version"]!.GetValue<int>().Should().Be(7);
        result["section"]!.GetValue<string>().Should().Be("summary");
        result["note"]!.GetValue<string>().Should().Be(DesignSystemAgentTools.Note);

        JsonArray items = result["items"]!.AsArray();
        items.Count.Should().BeGreaterThan(0);
        items.Select(item => item!["path"]!.GetValue<string>()).Should().Contain("color");
    }

    [Fact]
    public async Task ReadDesignSystem_color_filters_and_pages()
    {
        using IDisposable ambient = DesignSystemAmbient.Enter(State(DesignDoc()));
        DesignSystemAgentTools tools = NewTools();

        JsonObject first = Parse(await tools.ReadDesignSystem("color", filter: "BRAND", offset: 0, limit: 1));
        first["section"]!.GetValue<string>().Should().Be("color");
        first["total"]!.GetValue<int>().Should().Be(2);
        first["offset"]!.GetValue<int>().Should().Be(0);
        first["returned"]!.GetValue<int>().Should().Be(1);
        first["items"]!.AsArray().Count.Should().Be(1);
        first["items"]!.AsArray()[0]!["path"]!.GetValue<string>().Should().Be("color.brand.primary");
        // DesignText.Sanitize drops '#', so the re-sanitized color value has no leading marker.
        first["items"]!.AsArray()[0]!["value"]!.GetValue<string>().Should().Be("6D28D9");

        JsonObject second = Parse(await tools.ReadDesignSystem("color", filter: "brand", offset: 1, limit: 1));
        second["items"]!.AsArray()[0]!["path"]!.GetValue<string>().Should().Be("color.brand.secondary");

        JsonObject unbounded = Parse(await tools.ReadDesignSystem("color"));
        unbounded["total"]!.GetValue<int>().Should().Be(3);
    }

    [Fact]
    public async Task ReadDesignSystem_reports_unavailable_without_ambient()
    {
        DesignSystemAgentTools tools = NewTools();

        JsonObject result = Parse(await tools.ReadDesignSystem());

        result["available"]!.GetValue<bool>().Should().BeFalse();
        result["reason"]!.GetValue<string>().Should().Be(DesignSystemAgentTools.UnavailableReason);
    }

    [Fact]
    public async Task ReadDesignSystem_unknown_section_lists_sections()
    {
        using IDisposable ambient = DesignSystemAmbient.Enter(State(DesignDoc()));
        DesignSystemAgentTools tools = NewTools();

        JsonObject result = Parse(await tools.ReadDesignSystem("nope"));

        result["available"]!.GetValue<bool>().Should().BeTrue();
        result["error"]!.GetValue<string>().Should().Be("unknown section");
        result["items"]!.AsArray().Count.Should().Be(0);

        string[] sections = result["sections"]!.AsArray().Select(node => node!.GetValue<string>()).ToArray();
        sections.Should().Contain("color");
        sections.Should().Contain("voice");
    }

    [Fact]
    public async Task ReadDesignSystem_sanitizes_strings()
    {
        using IDisposable ambient = DesignSystemAmbient.Enter(State(DesignDoc()));
        DesignSystemAgentTools tools = NewTools();

        JsonObject summary = Parse(await tools.ReadDesignSystem());
        summary["items"]!.AsArray()
            .Select(item => item!["value"]!.GetValue<string>())
            .Should().Contain("Acme Design");

        JsonObject voice = Parse(await tools.ReadDesignSystem("voice"));
        foreach (JsonNode? item in voice["items"]!.AsArray())
        {
            foreach (System.Collections.Generic.KeyValuePair<string, JsonNode?> field in item!.AsObject())
            {
                if (field.Value is JsonValue value && value.GetValueKind() == JsonValueKind.String)
                {
                    string text = value.GetValue<string>();
                    text.Should().NotContain("[").And.NotContain("]").And.NotContain("<").And.NotContain(">");
                }
            }
        }

        voice["items"]!.AsArray()
            .Select(item => item!["value"]!.GetValue<string>())
            .Should().Contain("Warm and human");
    }

    [Fact]
    public async Task ReadProductProfile_returns_summary_routes_and_components()
    {
        using IDisposable ambient = DesignSystemAmbient.Enter(State(null, Product()));
        DesignSystemAgentTools tools = NewTools();

        JsonObject summary = Parse(await tools.ReadProductProfile());
        summary["available"]!.GetValue<bool>().Should().BeTrue();
        summary["version"]!.GetValue<int>().Should().Be(3);
        summary["items"]!.AsArray()
            .Select(item => item!["path"]!.GetValue<string>())
            .Should().Contain("framework");

        JsonObject routes = Parse(await tools.ReadProductProfile("routes", filter: "/api"));
        routes["section"]!.GetValue<string>().Should().Be("routes");
        routes["total"]!.GetValue<int>().Should().Be(1);
        routes["items"]!.AsArray()[0]!["path"]!.GetValue<string>().Should().Be("/api/checkout");
        routes["items"]!.AsArray()[0]!["method"]!.GetValue<string>().Should().Be("POST");

        JsonObject components = Parse(await tools.ReadProductProfile("components", filter: "button"));
        components["total"]!.GetValue<int>().Should().Be(1);
        components["items"]!.AsArray()[0]!["name"]!.GetValue<string>().Should().Be("Button");

        JsonObject dependencies = Parse(await tools.ReadProductProfile("dependencies", filter: "next"));
        dependencies["total"]!.GetValue<int>().Should().Be(1);
    }

    [Fact]
    public async Task ReadProductProfile_reports_unavailable_without_ambient()
    {
        DesignSystemAgentTools tools = NewTools();

        JsonObject result = Parse(await tools.ReadProductProfile());

        result["available"]!.GetValue<bool>().Should().BeFalse();
        result["reason"]!.GetValue<string>().Should().Be(DesignSystemAgentTools.UnavailableReason);
    }

    [Fact]
    public async Task ReadProductProfile_unknown_section_lists_sections()
    {
        using IDisposable ambient = DesignSystemAmbient.Enter(State(null, Product()));
        DesignSystemAgentTools tools = NewTools();

        JsonObject result = Parse(await tools.ReadProductProfile("nope"));

        result["error"]!.GetValue<string>().Should().Be("unknown section");
        result["sections"]!.AsArray()
            .Select(node => node!.GetValue<string>())
            .Should().Contain("routes")
            .And.Contain("entryPoints");
    }

    [Fact]
    public async Task ReadProductProfile_caps_output_and_marks_it_truncated()
    {
        ProductMapDocument product = ProductMapJson.Parse(LongProductJson())!;
        using IDisposable ambient = DesignSystemAmbient.Enter(State(null, product));
        DesignSystemAgentTools tools = NewTools();

        string json = await tools.ReadProductProfile("routes", limit: 50);

        json.Length.Should().BeLessThanOrEqualTo(DesignSystemAgentTools.MaxOutputChars);

        JsonObject result = Parse(json);
        result["total"]!.GetValue<int>().Should().Be(50);
        result["truncated"]!.GetValue<bool>().Should().BeTrue();
        result["returned"]!.GetValue<int>().Should().BeLessThan(50);
        result["items"]!.AsArray().Count.Should().Be(result["returned"]!.GetValue<int>());
    }

    // -------------------------------------------------------------------------------------------------

    private static DesignSystemAgentTools NewTools()
    {
        WorkflowExecutionContextAccessor accessor = new();
        accessor.BeginScope(Guid.NewGuid(), Guid.NewGuid(), "design-system-tools-test");
        return new DesignSystemAgentTools(accessor);
    }

    private static DesignSystemRunState State(
        JsonObject? design,
        ProductMapDocument? product = null,
        int designVersion = 7,
        int productVersion = 3) =>
        new(
            new LoadedProfile(
                design,
                design is null ? 0 : designVersion,
                product,
                product is null ? 0 : productVersion),
            new DesignSystemAmbientOptions());

    private static ProductMapDocument Product() => ProductMapJson.Parse(ProductJson)!;

    private static JsonObject Parse(string json) => JsonNode.Parse(json)!.AsObject();

    private static JsonObject DesignDoc()
    {
        JsonObject doc = DesignDocument.New("Acme [Design]");
        DesignDocument.SetToken(doc, "color.brand.primary", "color", new JsonObject { ["hex"] = "#6d28d9" }, Prov, 0.95);
        DesignDocument.SetToken(doc, "color.brand.secondary", "color", new JsonObject { ["hex"] = "#0ea5e9" }, Prov, 0.9);
        DesignDocument.SetToken(doc, "color.background", "color", new JsonObject { ["hex"] = "#ffffff" }, Prov, 0.8);
        DesignDocument.SetToken(doc, "spacing.sm", "dimension", new JsonObject { ["value"] = 8, ["unit"] = "px" }, Prov, 0.9);
        doc["voice"] = new JsonObject
        {
            ["summary"] = "Warm [and] human",
            ["tone"] = new JsonArray("friendly", "bold"),
        };
        doc["modes"] = new JsonObject
        {
            ["dark"] = new JsonObject { ["color.background"] = "#000000" },
        };
        doc["assets"] = new JsonObject
        {
            ["logo"] = new JsonObject { ["fileRef"] = "logo-file", ["variants"] = new JsonArray("light") },
        };
        return doc;
    }

    private static string LongProductJson()
    {
        var routes = new JsonArray();
        for (int i = 0; i < 50; i++)
        {
            routes.Add(new JsonObject
            {
                ["path"] = "/route/" + i,
                ["file"] = "app/routes/" + new string('x', 160) + i + ".tsx",
                ["kind"] = "page",
                ["requiresAuth"] = false,
                ["title"] = new string('t', 160) + i,
            });
        }

        var root = new JsonObject
        {
            ["schema"] = "reelbolt.product-map/1",
            ["framework"] = new JsonObject
            {
                ["name"] = "next",
                ["version"] = "15.1",
                ["language"] = "typescript",
                ["styling"] = new JsonArray(),
                ["packageManager"] = "pnpm",
                ["projectType"] = "web app",
            },
            ["dependencies"] = new JsonObject
            {
                ["runtime"] = new JsonArray(),
                ["dev"] = new JsonArray(),
                ["ui"] = new JsonArray(),
            },
            ["routes"] = routes,
        };

        return root.ToJsonString();
    }
}
