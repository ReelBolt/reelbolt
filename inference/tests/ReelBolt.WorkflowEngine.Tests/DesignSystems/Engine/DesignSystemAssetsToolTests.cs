using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Agents.Tools;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Services.DesignSystems;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Engine;

/// <summary><c>ReadDesignSystem(assets)</c> lists every slot and every variant with its subject, form and tone.</summary>
public class DesignSystemAssetsToolTests
{
    private static readonly Guid BaseId = Guid.NewGuid();
    private static readonly Guid DarkId = Guid.NewGuid();
    private static readonly Guid CompanyId = Guid.NewGuid();

    private static JsonObject Persisted() => new()
    {
        ["assets"] = new JsonObject
        {
            ["logo"] = new JsonObject
            {
                ["fileRef"] = "project-file:" + BaseId,
                ["file"] = "app-logo-light.svg",
                ["subject"] = "product",
                ["form"] = "mark",
                ["tone"] = "light",
                ["readableOn"] = new JsonArray("dark"),
                ["variants"] = new JsonArray(
                    new JsonObject
                    {
                        ["name"] = "dark",
                        ["fileRef"] = "project-file:" + DarkId,
                        ["file"] = "app-logo.svg",
                        ["subject"] = "product",
                        ["form"] = "mark",
                        ["tone"] = "dark",
                        ["readableOn"] = new JsonArray("light"),
                    },
                    new JsonObject
                    {
                        ["name"] = "publisher",
                        ["fileRef"] = "project-file:" + CompanyId,
                        ["file"] = "company.png",
                        ["subject"] = "publisher",
                        ["form"] = "mark",
                        ["tone"] = "mid",
                        ["readableOn"] = new JsonArray("light", "dark"),
                    }),
            },
        },
    };

    private static async Task<JsonObject> ReadAssets(JsonObject design)
    {
        var accessor = new WorkflowExecutionContextAccessor();
        accessor.BeginScope(Guid.NewGuid(), Guid.NewGuid(), "assets-tool-test");
        using IDisposable ambient = DesignSystemAmbient.Enter(
            new DesignSystemRunState(new LoadedProfile(design, 4, null, 0), new DesignSystemAmbientOptions()));
        string json = await new DesignSystemAgentTools(accessor).ReadDesignSystem("assets");
        json.Length.Should().BeLessThanOrEqualTo(DesignSystemAgentTools.MaxOutputChars);
        return JsonNode.Parse(json)!.AsObject();
    }

    [Fact]
    public async Task Lists_every_slot_and_variant_with_subject_tone_and_a_rules_line_first()
    {
        JsonObject result = await ReadAssets(Persisted());
        JsonArray items = result["items"]!.AsArray();

        items.Select(i => i!["path"]!.GetValue<string>()).Should().Equal(
            "assets.rules", "assets.logo", "assets.logo.variants[0]", "assets.logo.variants[1]");
        items[0]!["value"]!.GetValue<string>().Should().Contain("Product mark").And.Contain("by <company>");

        JsonObject baseItem = items[1]!.AsObject();
        baseItem["fileRef"]!.GetValue<string>().Should().Be("project-file:" + BaseId);
        baseItem["variants"]!.GetValue<int>().Should().Be(2);
        baseItem["file"]!.GetValue<string>().Should().Be("app-logo-light.svg");
        baseItem["tone"]!.GetValue<string>().Should().Be("light");
        baseItem["useFor"]!.GetValue<string>().Should().Contain("product logo").And.Contain("dark backgrounds only");

        JsonObject dark = items[2]!.AsObject();
        dark["subject"]!.GetValue<string>().Should().Be("product");
        dark["readableOn"]!.AsArray().Select(n => n!.GetValue<string>()).Should().Equal("light");

        JsonObject company = items[3]!.AsObject();
        company["subject"]!.GetValue<string>().Should().Be("publisher");
        company["useFor"]!.GetValue<string>().Should().Contain("not the product logo");
    }

    [Fact]
    public async Task Legacy_document_keeps_the_old_keys_and_gets_an_unmeasured_entry()
    {
        var design = new JsonObject
        {
            ["assets"] = new JsonObject
            {
                ["logo"] = new JsonObject
                {
                    ["fileRef"] = "project-file:" + BaseId,
                    ["variants"] = new JsonArray(new JsonObject { ["name"] = "publisher", ["fileRef"] = "project-file:" + DarkId }),
                },
                ["icon"] = new JsonObject { ["fileRef"] = "logo-file" },
            },
        };

        JsonArray items = (await ReadAssets(design))["items"]!.AsArray();

        JsonObject logo = items.Single(i => i!["path"]!.GetValue<string>() == "assets.logo")!.AsObject();
        logo["fileRef"]!.GetValue<string>().Should().Be("project-file:" + BaseId);
        logo["variants"]!.GetValue<int>().Should().Be(1);
        logo["subject"]!.GetValue<string>().Should().Be("unknown");
        logo.ContainsKey("tone").Should().BeFalse();
        logo["useFor"]!.GetValue<string>().Should().Contain("unmeasured");
        items.Single(i => i!["path"]!.GetValue<string>() == "assets.icon")!["fileRef"]!.GetValue<string>().Should().Be("logo-file");
    }

    [Fact]
    public async Task Output_stays_within_the_cap_with_many_assets()
    {
        var assets = new JsonObject();
        for (int i = 0; i < 12; i++)
        {
            assets["slot" + i] = new JsonObject
            {
                ["fileRef"] = "project-file:" + Guid.NewGuid(),
                ["file"] = new string('f', 150) + ".png",
                ["subject"] = "product",
                ["tone"] = "dark",
                ["readableOn"] = new JsonArray("light"),
            };
        }

        JsonObject result = await ReadAssets(new JsonObject { ["assets"] = assets });
        result["returned"]!.GetValue<int>().Should().BeGreaterThan(0);
    }
}
