using System.Text.Json.Nodes;
using AwesomeAssertions;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The dailies rule, isolated from the compile (issue #93 phase 4): a clip whose id the room selected
/// is admitted; a clip belonging to a shot the room DID choose for, but not the chosen take, is dropped
/// with <c>take_not_selected</c>; a shot the room left alone keeps its FIRST delivered take only. The
/// filter numbers a shot by the one-based <c>v{n}</c> of the clip id (what the selection is keyed by) and falls back to the
/// manifest's own one-based <c>shotIndex</c> (as VideoGenerate writes it) when the id has no such number.
/// </summary>
public class TakeSelectionFilterTests
{
    private static JsonObject Row(string clipId, int? shotIndex, string status = "succeeded")
    {
        var o = new JsonObject { ["clipId"] = clipId, ["status"] = status };
        if (shotIndex is not null)
            o["shotIndex"] = shotIndex;
        return o;
    }

    [Fact]
    public void A_writer_manifest_with_one_based_shotIndex_drops_the_unselected_take_of_the_last_shot()
    {
        // VideoGenerate writes shotIndex one-based (v4t1 -> 4); the room selected v4t2 only.
        var filter = new VideoCompileStepExecutor.TakeSelectionFilter(["v1t1", "v2t1", "v3t1", "v4t2"]);

        filter.Admits(Row("v4t1", 4), "v4t1", out string reason).Should().BeFalse();
        reason.Should().Be("take_not_selected");
        filter.Admits(Row("v4t2", 4), "v4t2", out _).Should().BeTrue();
    }

    [Fact]
    public void A_selected_take_is_admitted_and_its_siblings_are_dropped()
    {
        var filter = new VideoCompileStepExecutor.TakeSelectionFilter(["v1t2"]);

        filter.Admits(Row("v1t1", 1), "v1t1", out string r1).Should().BeFalse();
        r1.Should().Be("take_not_selected");
        filter.Admits(Row("v1t2", 1), "v1t2", out _).Should().BeTrue();
        filter.Admits(Row("v1t3", 1), "v1t3", out string r3).Should().BeFalse();
        r3.Should().Be("take_not_selected");
    }

    [Fact]
    public void An_unselected_shot_keeps_only_its_first_delivered_take()
    {
        var filter = new VideoCompileStepExecutor.TakeSelectionFilter(["v1t2"]);

        // A failed take does not claim the slot; the first SUCCEEDED one does.
        filter.Admits(Row("v2t1", 2, status: "failed"), "v2t1", out _).Should().BeTrue("a failed row keeps its own, more specific reason downstream");
        filter.Admits(Row("v2t2", 2), "v2t2", out _).Should().BeTrue();
        filter.Admits(Row("v2t3", 2), "v2t3", out string reason).Should().BeFalse();
        reason.Should().Be("take_not_selected");
    }

    [Fact]
    public void A_single_take_shot_without_a_selection_is_admitted_as_before()
    {
        var filter = new VideoCompileStepExecutor.TakeSelectionFilter(["v1t2"]);

        filter.Admits(Row("v3", 3), "v3", out _).Should().BeTrue();
    }

    [Fact]
    public void An_id_without_a_v_number_falls_back_to_the_one_based_shotIndex()
    {
        var filter = new VideoCompileStepExecutor.TakeSelectionFilter(["v2t1"]);

        // shotIndex 2 is shot 2 (one-based): it has a selection, so the unselected take drops.
        filter.Admits(Row("take-b", 2), "take-b", out string reason).Should().BeFalse();
        reason.Should().Be("take_not_selected");
        // shotIndex 3 is shot 3: no selection, first delivered take is kept.
        filter.Admits(Row("take-c", 3), "take-c", out _).Should().BeTrue();
    }

    [Fact]
    public void The_shot_number_falls_back_to_the_id_when_the_row_has_no_shotIndex()
    {
        var filter = new VideoCompileStepExecutor.TakeSelectionFilter(["v1t2"]);

        filter.Admits(Row("v1t1", null), "v1t1", out string reason).Should().BeFalse();
        reason.Should().Be("take_not_selected");
        filter.Admits(Row("v1t2", null), "v1t2", out _).Should().BeTrue();
    }

    [Fact]
    public void A_generated_manifest_with_one_based_shotIndex_selects_the_right_shot()
    {
        // The real manifest of run 35b5ac16: v5t1 and v5t2 both carry shotIndex 5. Only v5t2 was selected; v5t1 used to leak through
        // because shotIndex was read as zero-based (shot 6, which had no selection).
        var filter = new VideoCompileStepExecutor.TakeSelectionFilter(["v1t1", "v2t1", "v5t2"]);

        filter.Admits(Row("v5t1", 5), "v5t1", out string reason).Should().BeFalse();
        reason.Should().Be("take_not_selected");
        filter.Admits(Row("v5t2", 5), "v5t2", out _).Should().BeTrue();
        filter.Admits(Row("v1t2", 1), "v1t2", out _).Should().BeFalse("shot 1 has a selection");
        // Shot 3 has no selection: its first delivered take stays, the second goes.
        filter.Admits(Row("v3t1", 3), "v3t1", out _).Should().BeTrue();
        filter.Admits(Row("v3t2", 3), "v3t2", out _).Should().BeFalse();
    }

    [Fact]
    public void Selected_ids_are_trimmed_and_blanks_ignored()
    {
        var filter = new VideoCompileStepExecutor.TakeSelectionFilter([" v1t2 ", "", "  "]);

        filter.SelectedTakeIds.Should().BeEquivalentTo(["v1t2"]);
    }
}
