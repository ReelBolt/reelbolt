using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows.Replay;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.Caching;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Compositions;

/// <summary>"Keep the same cut": when a kept edit decision still means what the owner approved.</summary>
public class CutCompatibilityTests
{
    /// <summary>The shape of the staging analysis view (1e9c441f step 5), trimmed: a composition plus a photo.</summary>
    internal static string View(params (string Id, int Src, double Start, double End)[] shots) =>
        "{\"meta\":{\"offeredIdCount\":" + shots.Length + "},\"view\":{\"media\":{\"durationSec\":124},\"shots\":[" +
        string.Join(",", shots.Select(s => $"{{\"id\":\"{s.Id}\",\"src\":{s.Src},\"startSec\":{s.Start.ToString(System.Globalization.CultureInfo.InvariantCulture)},\"endSec\":{s.End.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}")) +
        "],\"sources\":[{\"src\":0,\"composition\":true},{\"src\":1}],\"segments\":[],\"silences\":[]}}";

    internal static readonly string Original = View(("s0", 0, 0, 4.3), ("s1", 0, 4.3, 10.8), ("s2", 0, 10.8, 13.3), ("s3", 1, 0, 4));

    [Fact]
    public void The_same_scenes_at_the_same_places_keep_the_cut_even_with_new_captions()
    {
        string revised = Original.Replace("\"segments\":[]", "\"segments\":[],\"captions\":[{\"shotId\":\"s0\",\"summary\":\"new blueprint grid\"}]");

        CutCompatibilityResult result = CutCompatibility.CompareAnalysisViews(Original, revised);

        result.Compatible.Should().BeTrue(result.Reason);
    }

    [Fact]
    public void A_boundary_moved_within_tolerance_keeps_the_cut()
    {
        string retimed = View(("s0", 0, 0, 4.4), ("s1", 0, 4.4, 10.8), ("s2", 0, 10.8, 13.3), ("s3", 1, 0, 4));

        CutCompatibility.CompareAnalysisViews(Original, retimed).Compatible.Should().BeTrue();
    }

    [Fact]
    public void A_scene_re_timed_beyond_tolerance_is_re_edited()
    {
        string retimed = View(("s0", 0, 0, 6.5), ("s1", 0, 6.5, 10.8), ("s2", 0, 10.8, 13.3), ("s3", 1, 0, 4));

        CutCompatibilityResult result = CutCompatibility.CompareAnalysisViews(Original, retimed);

        result.Compatible.Should().BeFalse();
        result.Reason.Should().Contain("s0 moved");
    }

    [Fact]
    public void A_scene_added_to_the_film_renumbers_the_photo_and_is_re_edited()
    {
        // A fourth composition scene takes s3; the photo becomes s4.
        string added = View(("s0", 0, 0, 4.3), ("s1", 0, 4.3, 10.8), ("s2", 0, 10.8, 13.3), ("s3", 0, 13.3, 16), ("s4", 1, 0, 4));

        CutCompatibilityResult result = CutCompatibility.CompareAnalysisViews(Original, added);

        result.Compatible.Should().BeFalse();
        result.Reason.Should().Contain("s4");
    }

    [Fact]
    public void A_scene_removed_from_the_film_is_re_edited()
    {
        string removed = View(("s0", 0, 0, 4.3), ("s1", 0, 4.3, 13.3), ("s2", 1, 0, 4));

        CutCompatibility.CompareAnalysisViews(Original, removed).Compatible.Should().BeFalse();
    }

    [Fact]
    public void An_unreadable_analysis_is_never_compatible()
    {
        CutCompatibility.CompareAnalysisViews(Original, "{oops").Compatible.Should().BeFalse();
        CutCompatibility.CompareAnalysisViews(null, Original).Compatible.Should().BeFalse();
    }

    [Fact]
    public void Same_json_ignores_key_order_and_whitespace()
    {
        CutCompatibility.SameJson("{\"a\":1,\"b\":[1,2]}", "{ \"b\": [1, 2], \"a\": 1 }").Should().BeTrue();
        CutCompatibility.SameJson("{\"a\":1}", "{\"a\":2}").Should().BeFalse();
    }

    [Fact]
    public void The_upstream_digest_moves_when_a_compile_reads_a_new_analysis_behind_the_same_decision()
    {
        // The exact bug a replay would hit without schema v5: the compile's previous step (the kept edit
        // decision) is identical, but the analysis artifact and the film it read changed.
        List<WorkflowStep> steps = ReplayShapes.AnimatedFilmEditRoomCut();
        WorkflowStepDependencyGraph graph = WorkflowStepDependencyGraph.Build(steps);
        WorkflowStep compile = steps.Single(s => s.StepOrder == 7);
        string decision = "{\"keep\":[{\"fromId\":\"s0\",\"toId\":\"s2\"}]}";

        List<StepOutputHistoryEntry> original =
        [
            new(4, "Author", "render manifest", "projects/p/outputFiles/run1/film.mp4"),
            new(5, "Analyze", Original, null, "projects/p/agentFiles/video-analysis/run1/step-5-analysis.json"),
            new(6, "Edit room", decision)
        ];
        List<StepOutputHistoryEntry> revised =
        [
            new(4, "Revise", "{\"succeeded\":true}", "projects/p/outputFiles/run2/revision.mp4"),
            new(5, "Analyze", Original, null, "projects/p/agentFiles/video-analysis/run2/step-5-analysis.json"),
            new(6, "Edit room", decision)
        ];

        StepUpstreamDigest.Compute(graph, compile, original).Should().NotBe(StepUpstreamDigest.Compute(graph, compile, revised));
        StepUpstreamDigest.Compute(graph, compile, original).Should().Be(StepUpstreamDigest.Compute(graph, compile, [.. original]),
            "an identical re-run still hits the cache");
    }
}
