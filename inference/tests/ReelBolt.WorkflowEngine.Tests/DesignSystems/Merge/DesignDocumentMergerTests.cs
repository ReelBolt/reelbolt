using System.Collections.Generic;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using ReelBolt.Shared.DesignSystems;
using ReelBolt.Shared.DesignSystems.Merge;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Merge;

public sealed class DesignDocumentMergerTests
{
    [Fact]
    public void Locked_base_token_beats_imported_static_and_llm()
    {
        JsonObject current = Doc(("color.primary", Token("static", 0.4, true, "#111111")));
        JsonObject[] candidates =
        [
            Doc(("color.primary", Token("imported", 0.99, false, "#222222"))),
            Doc(("color.primary", Token("static", 0.99, false, "#333333"))),
            Doc(("color.primary", Token("llm", 0.99, false, "#444444"))),
        ];

        JsonObject result = DesignDocumentMerger.Merge(current, candidates);

        HexOf(result, "color.primary").Should().Be("#111111");
    }

    [Fact]
    public void Locked_child_survives_a_candidate_leaf_at_its_parent_path()
    {
        JsonObject current = Doc(("color.brand.primary", Token("static", 0.4, true, "#111111")));
        JsonObject candidate = Doc(("color.brand", Token("imported", 0.99, false, "#222222")));

        JsonObject result = DesignDocumentMerger.Merge(current, [candidate]);

        HexOf(result, "color.brand.primary").Should().Be("#111111");
        DesignDocument.FindToken(result, "color.brand.primary")!["$extensions"]!["com.reelbolt"]!["locked"]!
            .GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public void Locked_leaf_is_not_given_children_by_a_candidate_below_it()
    {
        JsonObject current = Doc(("color.brand", Token("static", 0.4, true, "#111111")));
        JsonObject candidate = Doc(("color.brand.primary", Token("imported", 0.99, false, "#222222")));

        JsonObject result = DesignDocumentMerger.Merge(current, [candidate]);

        HexOf(result, "color.brand").Should().Be("#111111");
        result["color"]!["brand"]!.AsObject().ContainsKey("primary").Should().BeFalse();
    }

    [Fact]
    public void Imported_beats_static_beats_llm()
    {
        JsonObject imported = Doc(("color.primary", Token("imported", 0.9, false, "#222222")));
        JsonObject staticDoc = Doc(("color.primary", Token("static", 0.9, false, "#333333")));
        JsonObject llm = Doc(("color.primary", Token("llm", 0.9, false, "#444444")));

        JsonObject result = DesignDocumentMerger.Merge(null, [imported, staticDoc, llm]);
        HexOf(result, "color.primary").Should().Be("#222222");

        JsonObject staticWins = DesignDocumentMerger.Merge(null, [staticDoc, llm]);
        HexOf(staticWins, "color.primary").Should().Be("#333333");
    }

    [Fact]
    public void Assets_brand_metadata_survives_a_merge()
    {
        JsonObject child = Doc(("color.primary", Token("imported", 0.9, false, "#222222")));
        child["assets"] = System.Text.Json.Nodes.JsonNode.Parse("""
            {"logo":{"fileRef":"project-file:6f1c1f1e-0000-4000-8000-000000000001","file":"m.svg","subject":"product","tone":"dark","readableOn":["light"],"luminance":0.01,
             "variants":[{"name":"publisher","fileRef":"project-file:6f1c1f1e-0000-4000-8000-000000000002","subject":"publisher","readableOn":["dark"]}]}}
            """)!;

        JsonObject result = DesignDocumentMerger.Merge(null, [child]);

        JsonObject logo = result["assets"]!["logo"]!.AsObject();
        logo["subject"]!.GetValue<string>().Should().Be("product");
        logo["readableOn"]!.AsArray()[0]!.GetValue<string>().Should().Be("light");
        logo["variants"]!.AsArray()[0]!["subject"]!.GetValue<string>().Should().Be("publisher");
    }

    [Fact]
    public void Tie_broken_by_conf()
    {
        JsonObject low = Doc(("color.primary", Token("imported", 0.5, false, "#aaaaaa")));
        JsonObject high = Doc(("color.primary", Token("imported", 0.9, false, "#bbbbbb")));

        JsonObject result = DesignDocumentMerger.Merge(null, [low, high]);

        HexOf(result, "color.primary").Should().Be("#bbbbbb");
    }

    [Fact]
    public void Tie_broken_by_earlier_layer()
    {
        JsonObject first = Doc(("color.primary", Token("imported", 0.9, false, "#aaaaaa")));
        JsonObject second = Doc(("color.primary", Token("imported", 0.9, false, "#bbbbbb")));

        JsonObject result = DesignDocumentMerger.Merge(null, [first, second]);

        HexOf(result, "color.primary").Should().Be("#aaaaaa");
    }

    [Fact]
    public void Voice_list_replaces_as_whole()
    {
        JsonObject earlier = Doc();
        earlier["voice"] = new JsonObject { ["tone"] = new JsonArray(JsonValue.Create("c")) };
        JsonObject later = Doc();
        later["voice"] = new JsonObject { ["tone"] = new JsonArray(JsonValue.Create("a"), JsonValue.Create("b")) };

        JsonObject result = DesignDocumentMerger.Merge(null, [earlier, later]);

        JsonArray tone = result["voice"]!.AsObject()["tone"]!.AsArray();
        tone.Count.Should().Be(1);
        tone[0]!.GetValue<string>().Should().Be("c");
    }

    [Fact]
    public void Non_token_sections_merge_keywise()
    {
        JsonObject earlier = Doc();
        earlier["voice"] = new JsonObject { ["tone"] = new JsonArray(JsonValue.Create("c")) };
        JsonObject later = Doc();
        later["voice"] = new JsonObject { ["summary"] = "clean brand" };

        JsonObject result = DesignDocumentMerger.Merge(null, [earlier, later]);

        JsonObject voice = result["voice"]!.AsObject();
        voice["tone"]!.AsArray()[0]!.GetValue<string>().Should().Be("c");
        voice["summary"]!.GetValue<string>().Should().Be("clean brand");
    }

    [Fact]
    public void Unlocked_base_token_loses_to_any_candidate_but_fills_gaps()
    {
        JsonObject current = Doc(("color.background", Token("static", 0.5, false, "#0b0b0f")));
        JsonObject candidate = Doc(("color.primary", Token("imported", 0.9, false, "#6d28d9")));

        JsonObject result = DesignDocumentMerger.Merge(current, [candidate]);

        HexOf(result, "color.primary").Should().Be("#6d28d9");
        HexOf(result, "color.background").Should().Be("#0b0b0f");
    }

    [Fact]
    public void Merge_output_is_canonical_and_valid()
    {
        JsonObject current = Doc(("color.background", Token("static", 0.7, true, "#0b0b0f")));
        JsonObject candidate = Doc(("color.primary", Token("imported", 0.95, false, "#6d28d9")));
        candidate["name"] = "Acme";

        JsonObject result = DesignDocumentMerger.Merge(current, [candidate]);

        var context = new DesignValidationContext(_ => true, new HashSet<string>());
        DesignDocumentValidator.Validate(result, context).IsValid.Should().BeTrue();

        string first = CanonicalJson.Serialize(result);
        string second = CanonicalJson.Serialize(JsonNode.Parse(first)!);
        first.Should().Be(second);
        CanonicalJson.Digest(result).Should().Be(CanonicalJson.Sha256Hex(second));
    }

    [Fact]
    public void Inherit_child_wins_parent_fills_rest()
    {
        JsonObject parent = Doc(
            ("color.primary", Token("static", 0.9, false, "#111111")),
            ("color.background", Token("static", 0.9, false, "#0b0b0f")));
        parent["name"] = "Parent";
        JsonObject child = Doc(("color.primary", Token("static", 0.9, false, "#6d28d9")));
        child["name"] = "Child";

        JsonObject result = DesignDocumentMerger.Inherit(parent, child);

        HexOf(result, "color.primary").Should().Be("#6d28d9");
        HexOf(result, "color.background").Should().Be("#0b0b0f");
        result["name"]!.GetValue<string>().Should().Be("Child");
    }

    [Fact]
    public void Inherit_modes_merged_per_mode_and_path()
    {
        JsonObject parent = Doc(
            ("color.background", Token("static", 0.9, false, "#0b0b0f")),
            ("color.text", Token("static", 0.9, false, "#111111")));
        parent["modes"] = new JsonObject
        {
            ["dark"] = new JsonObject
            {
                ["color.background"] = "#000000",
                ["color.text"] = "#111111",
            },
        };
        JsonObject child = Doc(("color.text", Token("static", 0.9, false, "#222222")));
        child["modes"] = new JsonObject
        {
            ["dark"] = new JsonObject
            {
                ["color.text"] = "#222222",
            },
        };

        JsonObject result = DesignDocumentMerger.Inherit(parent, child);

        JsonObject dark = result["modes"]!.AsObject()["dark"]!.AsObject();
        dark["color.background"]!.GetValue<string>().Should().Be("#000000");
        dark["color.text"]!.GetValue<string>().Should().Be("#222222");
    }

    [Fact]
    public void Inherit_no_tombstones()
    {
        JsonObject parent = Doc(("color.background", Token("static", 0.9, false, "#0b0b0f")));
        JsonObject child = Doc(("color.primary", Token("static", 0.9, false, "#6d28d9")));

        JsonObject result = DesignDocumentMerger.Inherit(parent, child);

        HexOf(result, "color.primary").Should().Be("#6d28d9");
        HexOf(result, "color.background").Should().Be("#0b0b0f");
    }

    [Fact]
    public void Locked_token_keeps_its_base_dark_override_against_a_different_candidate_value()
    {
        JsonObject current = Doc(("color.bg", Token("static", 0.4, true, "#111111")));
        current["modes"] = Modes(("dark", "color.bg", "#000000"));
        JsonObject candidate = Doc(("color.bg", Token("imported", 0.9, false, "#222222")));
        candidate["modes"] = Modes(("dark", "color.bg", "#ffffff"));

        JsonObject result = DesignDocumentMerger.Merge(current, [candidate]);

        ModeOf(result, "dark", "color.bg").Should().Be("#000000");
    }

    [Fact]
    public void Locked_token_keeps_its_base_modes_when_the_candidate_has_no_modes()
    {
        JsonObject current = Doc(("color.bg", Token("static", 0.4, true, "#111111")));
        current["modes"] = Modes(("dark", "color.bg", "#000000"));
        JsonObject candidate = Doc(("color.bg", Token("imported", 0.9, false, "#222222")));

        JsonObject result = DesignDocumentMerger.Merge(current, [candidate]);

        ModeOf(result, "dark", "color.bg").Should().Be("#000000");
    }

    [Fact]
    public void Locked_token_ignores_a_candidate_override_in_an_extra_mode()
    {
        JsonObject current = Doc(("color.bg", Token("static", 0.4, true, "#111111")));
        current["modes"] = Modes(("dark", "color.bg", "#000000"));
        JsonObject candidate = Doc(("color.bg", Token("imported", 0.9, false, "#222222")));
        candidate["modes"] = Modes(("dark", "color.bg", "#ffffff"), ("contrast", "color.bg", "#eeeeee"));

        JsonObject result = DesignDocumentMerger.Merge(current, [candidate]);

        ModeOf(result, "dark", "color.bg").Should().Be("#000000");
        result["modes"]!["contrast"]?.AsObject().ContainsKey("color.bg").Should().NotBe(true);
    }

    [Fact]
    public void Unlocked_token_still_takes_candidate_modes_while_locked_one_is_preserved()
    {
        JsonObject current = Doc(
            ("color.bg", Token("static", 0.4, true, "#111111")),
            ("color.text", Token("static", 0.4, false, "#222222")));
        current["modes"] = Modes(("dark", "color.bg", "#000000"), ("dark", "color.text", "#aaaaaa"));
        JsonObject candidate = Doc(
            ("color.bg", Token("imported", 0.9, false, "#333333")),
            ("color.text", Token("imported", 0.9, false, "#444444")));
        candidate["modes"] = Modes(("dark", "color.bg", "#ffffff"), ("dark", "color.text", "#bbbbbb"));

        JsonObject result = DesignDocumentMerger.Merge(current, [candidate]);

        ModeOf(result, "dark", "color.bg").Should().Be("#000000");
        ModeOf(result, "dark", "color.text").Should().Be("#bbbbbb");
    }

    private static JsonObject Modes(params (string Mode, string Path, string Hex)[] overrides)
    {
        var modes = new JsonObject();
        foreach ((string mode, string path, string hex) in overrides)
        {
            if (modes[mode] is not JsonObject target)
            {
                target = new JsonObject();
                modes[mode] = target;
            }

            target[path] = new JsonObject { ["hex"] = hex, ["colorSpace"] = "srgb" };
        }

        return modes;
    }

    private static string? ModeOf(JsonObject doc, string mode, string path) =>
        doc["modes"]?[mode]?[path]?["hex"]?.GetValue<string>();

    private static string HexOf(JsonObject doc, string path)
    {
        JsonObject token = DesignDocument.FindToken(doc, path)!;
        return token["$value"]!.AsObject()["hex"]!.GetValue<string>();
    }

    private static JsonObject Token(string method, double conf, bool locked, string hex) => new()
    {
        ["$type"] = "color",
        ["$value"] = new JsonObject { ["hex"] = hex, ["colorSpace"] = "srgb" },
        ["$extensions"] = new JsonObject
        {
            ["com.reelbolt"] = new JsonObject
            {
                ["prov"] = new JsonObject { ["method"] = method },
                ["conf"] = conf,
                ["locked"] = locked,
            },
        },
    };

    private static JsonObject Doc(params (string Path, JsonObject Token)[] tokens)
    {
        var doc = new JsonObject { ["schema"] = DesignDocument.Schema };
        foreach ((string path, JsonObject token) in tokens)
            SetAt(doc, path, token);
        return doc;
    }

    private static void SetAt(JsonObject doc, string dotPath, JsonObject value)
    {
        string[] segments = dotPath.Split('.');
        JsonObject current = doc;
        for (int i = 0; i < segments.Length - 1; i++)
        {
            if (current[segments[i]] is JsonObject next)
            {
                current = next;
            }
            else
            {
                var created = new JsonObject();
                current[segments[i]] = created;
                current = created;
            }
        }

        current[segments[^1]] = value;
    }
}
