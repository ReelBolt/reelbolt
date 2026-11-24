using System.Linq;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using ReelBolt.Shared.DesignSystems;
using ReelBolt.Shared.DesignSystems.Diff;
using ReelBolt.Shared.DesignSystems.Merge;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Drift;

/// <summary>P3-01: the pure design-document diff and its SPEC 13.3 JSON shape.</summary>
public sealed class DesignDocumentDifferTests
{
    [Fact]
    public void Diff_reports_added_removed_and_changed_paths()
    {
        JsonObject before = Doc(
            ("color.brand.primary", "#6d28d9"),
            ("color.old", "#000000"));
        JsonObject after = Doc(
            ("color.brand.primary", "#7c3aed"),
            ("color.brand.accent", "#f59e0b"));

        DesignDiff diff = DesignDocumentDiffer.Diff(before, after);

        diff.Added.Select(e => e.Path).Should().Equal("color.brand.accent");
        diff.Removed.Select(e => e.Path).Should().Equal("color.old");
        diff.Changed.Select(e => e.Path).Should().Equal("color.brand.primary");

        DesignDiffEntry changed = diff.Changed[0];
        changed.Before!["hex"]!.GetValue<string>().Should().Be("#6d28d9");
        changed.After!["hex"]!.GetValue<string>().Should().Be("#7c3aed");
        changed.Locked.Should().BeFalse();
        changed.Conflict.Should().BeFalse();
        changed.Prov!["method"]!.GetValue<string>().Should().Be("static");
    }

    [Fact]
    public void Diff_marks_a_locked_contradiction_and_the_merge_keeps_the_base_value()
    {
        JsonObject before = LockedDoc("color.brand.primary", "#6d28d9");
        JsonObject after = Doc(("color.brand.primary", "#7c3aed"));

        DesignDiff diff = DesignDocumentDiffer.Diff(before, after);

        diff.Changed.Should().ContainSingle();
        diff.Changed[0].Locked.Should().BeTrue();
        diff.Changed[0].Conflict.Should().BeTrue();

        JsonObject proposed = DesignDocumentMerger.Merge(before, [after]);
        JsonObject token = DesignDocument.FindToken(proposed, "color.brand.primary")!;
        token["$value"]!["hex"]!.GetValue<string>().Should().Be("#6d28d9");
        token["$extensions"]!["com.reelbolt"]!["locked"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public void Diff_summarizes_the_plain_sections()
    {
        JsonObject before = Doc(("color.brand.primary", "#6d28d9"));
        before["voice"] = new JsonObject { ["tone"] = "calm" };
        JsonObject after = Doc(("color.brand.primary", "#6d28d9"));
        after["voice"] = new JsonObject { ["tone"] = "bold" };

        DesignDiff diff = DesignDocumentDiffer.Diff(before, after);

        diff.Sections["voice"].Should().Be("changed");
        diff.Sections["assets"].Should().Be("unchanged");
        diff.Sections["fonts"].Should().Be("unchanged");
        diff.Sections["modes"].Should().Be("unchanged");
    }

    [Fact]
    public void ToJson_matches_the_spec_shape()
    {
        JsonObject before = Doc(("color.brand.primary", "#6d28d9"), ("spacing.4", "#ignored"));
        JsonObject after = Doc(("color.brand.primary", "#7c3aed"));

        DesignDiff diff = DesignDocumentDiffer.Diff(before, after);
        JsonObject json = diff.ToJson();

        json["counts"]!["added"]!.GetValue<int>().Should().Be(0);
        json["counts"]!["removed"]!.GetValue<int>().Should().Be(1);
        json["counts"]!["changed"]!.GetValue<int>().Should().Be(1);
        json["changed"]!.AsArray()[0]!["path"]!.GetValue<string>().Should().Be("color.brand.primary");
    }

    private static JsonObject Doc(params (string Path, string Hex)[] tokens) => Build(tokens, locked: false);

    private static JsonObject LockedDoc(string path, string hex) =>
        Build([(path, hex)], locked: true);

    private static JsonObject Build((string Path, string Hex)[] tokens, bool locked)
    {
        JsonObject root = DesignDocument.New();
        foreach ((string path, string hex) in tokens)
        {
            DesignDocument.SetToken(
                root,
                path,
                "color",
                new JsonObject { ["hex"] = hex, ["colorSpace"] = "srgb" },
                new TokenProvenance("static", "css-theme", null, "app/globals.css:1"),
                1.0,
                locked);
        }

        return root;
    }
}
