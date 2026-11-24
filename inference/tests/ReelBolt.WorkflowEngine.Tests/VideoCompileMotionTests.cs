#nullable enable
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>EnableMotion + MotionPlan: a stub plan becomes the primitives' own lists; off is byte-identical.</summary>
public partial class VideoCompileNarrationAndFormatTests
{
    // The fixture serves one extra history entry (the Voiceover slot, step 3); the plan rides in it, which is all
    // ResolveDecisionJson needs. EnableVoiceover stays off, so nothing reads it as audio.
    private const int MotionPlanStepOrder = VoiceoverStepOrder;

    private static string MotionPlanJson(params (string Id, string Action, string Intensity)[] cues) => JsonSerializer.Serialize(new
    {
        cues = cues.Select(c => new { eventId = c.Id, action = c.Action, intensity = c.Intensity, length = "", colour = "", kind = "", text = "", reason = "stub" }),
        planRationale = "stub"
    });

    private static CompileFixture MotionFixture(string planJson, bool enable = true, int? maxCues = null) =>
        new(WithEvents(SingleSourceArtifact(10.0), ("e0", 4.0), ("e1", 6.5), ("e2", 12.0)))
        {
            Voiceover = planJson,
            Config = c => c with
            {
                PrePaddingMs = 0,
                PostPaddingMs = 0,
                EnableMotion = enable,
                MotionPlan = new ExtractInputRef(ExtractInputSource.Step, StepOrder: MotionPlanStepOrder),
                MaxMotionCues = maxCues
            }
        };

    [Fact]
    public async Task Motion_plan_applies_through_the_primitives_and_reports_each_cue()
    {
        var on = MotionFixture(MotionPlanJson(("e0", "Flash", "Normal"), ("e1", "PunchIn", "Normal"), ("e1", "SlowMo", "Normal"), ("e2", "Flash", "Normal"), ("e8", "Shake", "Normal")));

        StepExecutionResult result = await on.RunAsync(BuildDecisionJson("s0"));

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        JsonObject motion = on.Output!["motion"]!.AsObject();
        motion["cuesApplied"]!.AsArray().Should().HaveCount(4);
        motion["dropped"]!.AsArray().Select(d => d!["reason"]!.GetValue<string>()).Should().Equal("event_not_offered");
        on.Output.ContainsKey("eventMoves").Should().BeTrue();
        on.Output.ContainsKey("speedRamps").Should().BeTrue();
        on.Output!["flashCues"]!["dropped"]!.AsArray().Select(d => d!["reason"]!.GetValue<string>()).Should().Equal("anchor_cut_away");
        on.Edl!.ContainsKey("motion").Should().BeTrue();
    }

    [Fact]
    public async Task A_plan_flash_renders_the_same_graph_as_the_hand_written_flash()
    {
        var plan = MotionFixture(MotionPlanJson(("e0", "Flash", "Normal")));
        var hand = new CompileFixture(WithEvents(SingleSourceArtifact(10.0), ("e0", 4.0), ("e1", 6.5), ("e2", 12.0)))
        {
            Config = c => c with { PrePaddingMs = 0, PostPaddingMs = 0, FlashCues = [new FlashCueRequest("e0")] }
        };

        (await plan.RunAsync(BuildDecisionJson("s0"))).Status.Should().Be(StepStatus.Completed);
        (await hand.RunAsync(BuildDecisionJson("s0"))).Status.Should().Be(StepStatus.Completed);

        plan.FilterGraph.Should().Be(hand.FilterGraph).And.Contain("drawbox");
    }

    [Fact]
    public async Task The_cue_cap_drops_later_cues_with_a_reason()
    {
        var on = MotionFixture(MotionPlanJson(("e0", "Flash", "Normal"), ("e1", "PunchIn", "Normal")), maxCues: 1);

        (await on.RunAsync(BuildDecisionJson("s0"))).Status.Should().Be(StepStatus.Completed);

        JsonObject motion = on.Output!["motion"]!.AsObject();
        motion["cuesApplied"]!.AsArray().Should().HaveCount(1);
        motion["dropped"]!.AsArray().Single()!["reason"]!.GetValue<string>().Should().Be("max_motion_cues");
    }

    [Fact]
    public async Task Motion_off_is_byte_identical_even_with_a_plan_configured()
    {
        var off = MotionFixture(MotionPlanJson(("e0", "Flash", "Strong"), ("e1", "PunchIn", "Strong")), enable: false);
        var plain = new CompileFixture(WithEvents(SingleSourceArtifact(10.0), ("e0", 4.0), ("e1", 6.5), ("e2", 12.0)))
        {
            Config = c => c with { PrePaddingMs = 0, PostPaddingMs = 0 }
        };

        (await off.RunAsync(BuildDecisionJson("s0"))).Status.Should().Be(StepStatus.Completed);
        (await plain.RunAsync(BuildDecisionJson("s0"))).Status.Should().Be(StepStatus.Completed);

        off.FilterGraph.Should().Be(plain.FilterGraph);
        off.Output!.ContainsKey("motion").Should().BeFalse();
        string json = JsonSerializer.Serialize(new VideoCompileStepConfig(1, new ExtractInputRef(ExtractInputSource.Previous), 1));
        json.Should().NotContain("EnableMotion").And.NotContain("MotionPlan").And.NotContain("MaxMotionCues");
    }

    [Fact]
    public async Task An_unreadable_plan_applies_nothing_and_says_why()
    {
        foreach ((string plan, string reason) in new[] { ("not json at all", "plan_unresolved"), ("{\"cues\": 5}", "plan_invalid_json") })
        {
            var on = MotionFixture(plan);

            (await on.RunAsync(BuildDecisionJson("s0"))).Status.Should().Be(StepStatus.Completed);

            on.Output!["motion"]!["reason"]!.GetValue<string>().Should().Be(reason);
        }
    }
}
