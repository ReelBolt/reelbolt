using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using ReelBolt.WorkflowEngine.Agents.Rooms;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The dailies room's id vocabulary (issue #93 phase 4): the ONLY ids a selection may name are the
/// <c>v{n}</c>/<c>v{n}t{k}</c> clip ids a GeneratedClips-sourced <c>VideoAnalyze</c> view tagged its
/// sources with, and the only mentions that count toward convergence are those same ids — never a
/// real shot id the judges argue from.
/// </summary>
public class DailiesRoomOfferedIdsTests
{
    [Fact]
    public void Offered_ids_are_the_tagged_clip_ids_of_view_sources_and_nothing_else()
    {
        JsonNode view = JsonNode.Parse("""
            {"view":{"sources":[
                {"src":0,"durationSec":6.0,"clipId":"v1t1"},
                {"src":1,"durationSec":6.0,"clipId":"v1t2"},
                {"src":2,"durationSec":30.0},
                {"src":3,"durationSec":6.0,"clipId":"v2"},
                {"src":4,"durationSec":6.0,"clipId":"s3"}
            ],"shots":[{"id":"s0"}]}}
            """)!;

        DailiesRoomStepExecutor.ExtractOfferedTakeIds(view).Should().Equal("v1t1", "v1t2", "v2");
    }

    [Fact]
    public void A_view_with_no_sources_offers_nothing()
    {
        DailiesRoomStepExecutor.ExtractOfferedTakeIds(JsonNode.Parse("""{"view":{"shots":[]}}""")).Should().BeEmpty();
        DailiesRoomStepExecutor.ExtractOfferedTakeIds(null).Should().BeEmpty();
    }

    [Fact]
    public void Convergence_counts_only_offered_take_ids_not_the_shot_ids_judges_compare_against()
    {
        var offered = new HashSet<string>(["v1t1", "v1t2", "v2"]);

        IReadOnlyList<string> mentions = DailiesRoomGroupChatManager.ExtractOfferedIdMentions(
            "v1t2 matches s2's warm look better than v1t1; v9 is not a take we were offered, nor is p1.", offered);

        mentions.Should().BeEquivalentTo(["v1t2", "v1t1"]);
        mentions.Should().NotContain("s2").And.NotContain("v9").And.NotContain("p1");
    }
}
