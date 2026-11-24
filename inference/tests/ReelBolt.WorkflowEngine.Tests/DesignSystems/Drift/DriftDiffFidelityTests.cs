using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using ReelBolt.Shared.DesignSystems;
using ReelBolt.Shared.DesignSystems.Diff;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Drift;

/// <summary>DSM-07: the diff covers every field a proposal can change and never trims silently.</summary>
public sealed class DriftDiffFidelityTests
{
    private static ProductMapDocument Map(
        string? architecture = null,
        string? routingStrategy = null,
        string[]? patterns = null,
        ProductMapDirectory[]? directories = null,
        ProductMapEntryPoint[]? entryPoints = null) =>
        new(
            ProductMapDocument.SchemaId,
            "app",
            new ProductMapFramework("next", "15", "ts", [], "pnpm", "web"),
            architecture,
            routingStrategy,
            directories ?? [],
            entryPoints ?? [],
            [],
            [],
            new ProductMapDependencies([], [], []),
            patterns ?? [],
            new Dictionary<string, ProductMapProvenance>());

    [Fact]
    public void Product_diff_reports_architecture_and_routing_strategy_changes()
    {
        ProductMapDiff diff = ProductMapDiffer.Diff(
            Map(architecture: "monolith", routingStrategy: "file"),
            Map(architecture: "microservices", routingStrategy: "file"));

        JsonObject json = diff.ToJson();
        json["fields"]!.AsArray().Select(n => n!["name"]!.GetValue<string>()).Should().Equal("architecture");
    }

    [Fact]
    public void Product_diff_reports_patterns_directories_and_entry_points()
    {
        ProductMapDiff diff = ProductMapDiffer.Diff(
            Map(patterns: ["repository"], directories: [new("src", "code")], entryPoints: [new("a.ts", "main")]),
            Map(patterns: ["cqrs"], directories: [new("lib", "code")], entryPoints: [new("b.ts", "main")]));

        diff.Patterns.Added.Should().Equal("cqrs");
        diff.Patterns.Removed.Should().Equal("repository");
        diff.Directories.Added.Should().Equal("lib");
        diff.Directories.Removed.Should().Equal("src");
        diff.EntryPoints.Added.Should().Equal("b.ts");
        diff.EntryPoints.Removed.Should().Equal("a.ts");
    }

    [Fact]
    public void Identical_maps_have_no_extra_field_changes()
    {
        ProductMapDiff diff = ProductMapDiffer.Diff(Map(architecture: "x", patterns: ["p"]), Map(architecture: "x", patterns: ["p"]));
        diff.Fields.Should().BeEmpty();
        diff.Patterns.Added.Should().BeEmpty();
        diff.ToJson().ContainsKey("fields").Should().BeFalse();
    }

    [Fact]
    public void Truncated_diff_keeps_the_true_counts_and_records_what_was_shown()
    {
        var added = new List<DesignDiffEntry>();
        var changed = new List<DesignDiffEntry>();
        for (int i = 0; i < 200; i++)
        {
            changed.Add(new DesignDiffEntry($"color.c{i}", JsonValue.Create(new string('a', 300)), JsonValue.Create(new string('b', 300)), false, false, null));
            added.Add(new DesignDiffEntry($"color.a{i}", null, JsonValue.Create(new string('c', 300)), false, false, null));
        }

        var diff = new DesignDiff(added, [], changed, new Dictionary<string, string>());
        JsonObject json = DiffJson.Build(diff, 0.5, null, maxBytes: 20_000);

        json["truncated"]!.GetValue<bool>().Should().BeTrue();
        json["counts"]!["changed"]!.GetValue<int>().Should().Be(200);
        json["counts"]!["added"]!.GetValue<int>().Should().Be(200);
        int shownChanged = json["changed"]!.AsArray().Count;
        shownChanged.Should().BeLessThan(200);
        json["shown"]!["changed"]!.GetValue<int>().Should().Be(shownChanged);
        json["shown"]!["added"]!.GetValue<int>().Should().Be(json["added"]!.AsArray().Count);
    }

    [Fact]
    public void Untruncated_diff_has_no_truncated_or_shown_marker()
    {
        JsonObject json = DiffJson.Build(new DesignDiff([], [], [], new Dictionary<string, string>()), 0, null);
        json.ContainsKey("truncated").Should().BeFalse();
        json.ContainsKey("shown").Should().BeFalse();
    }
}
