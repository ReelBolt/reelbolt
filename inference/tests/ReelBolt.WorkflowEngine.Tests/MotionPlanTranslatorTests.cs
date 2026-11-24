using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.Shared.Data.OutputSchemas;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>MotionPlanTranslator: words map 1:1 onto the primitives' enums, ids are checked, caps and drops carry reasons.</summary>
public class MotionPlanTranslatorTests
{
    private static readonly string[] Offered = ["e0", "e1", "e2", "e3"];

    private static VideoCompileStepConfig Config(int? max = null, IReadOnlyList<EventMoveRequest>? moves = null) =>
        new(1, new ExtractInputRef(ExtractInputSource.Previous), 1, EventMoves: moves, MaxMotionCues: max);

    private static MotionPlanOutput Plan(params MotionCue[] cues) => new() { Cues = [.. cues] };

    private static MotionCue Cue(string id, string action, string intensity = "Normal", string length = "", string colour = "", string kind = "", string text = "") =>
        new() { EventId = id, Action = action, Intensity = intensity, Length = length, Colour = colour, Kind = kind, Text = text, Reason = "r" };

    [Fact]
    public void Every_action_word_maps_to_its_primitive()
    {
        MotionTranslation t = MotionPlanTranslator.Translate(Plan(
            Cue("e0", "PunchIn", "Strong", "Long"),
            Cue("e1", "panleft"),
            Cue("e0", "SlowMo", "Subtle", "Short"),
            Cue("e2", "FreezeHit"),
            Cue("e1", "Flash", "Strong", "Medium", "Warm"),
            Cue("e3", "Graphic", "Normal", "Long", kind: "Tag", text: "New")), Offered, Config(), graphicsEnabled: true);

        t.Moves.Should().Equal(
            new EventMoveRequest("e0", EventMoveKind.PunchIn, EventMoveIntensity.Strong, EventMoveHold.Long),
            new EventMoveRequest("e1", EventMoveKind.PanLeft, EventMoveIntensity.Normal, EventMoveHold.Medium));
        t.Ramps.Should().Equal(
            new SpeedRampRequest("e0", SpeedRampKind.SlowMo, SpeedRampStrength.Subtle, SpeedRampSpan.Short),
            new SpeedRampRequest("e2", SpeedRampKind.FreezeHit, SpeedRampStrength.Normal, SpeedRampSpan.Medium));
        t.Flashes.Should().Equal(new FlashCueRequest("e1", FlashColour.Warm, FlashStrength.Strong, FlashLength.Medium));
        MotionGraphicsOverlay g = t.Graphics.Should().ContainSingle().Subject;
        (g.EventId, g.Kind, g.Text, g.Duration, g.PlacementId).Should().Be(("e3", "Tag", "New", "Hold", ""));
        t.Report.Should().OnlyContain(r => r.Applied);
    }

    [Fact]
    public void Bad_cues_are_dropped_with_a_reason_and_never_relocated()
    {
        MotionTranslation t = MotionPlanTranslator.Translate(Plan(
            Cue("s2", "PunchIn"),
            Cue("e9", "PunchIn"),
            Cue("e0", "CutHere"),
            Cue("e0", "PunchIn", "Huge"),
            Cue("e0", "PunchIn", "1"),
            Cue("e1", "PunchIn"),
            Cue("e1", "PunchIn"),
            Cue("e2", "Graphic", text: "")), Offered, Config(), graphicsEnabled: true);

        t.Report.Select(r => r.Reason).Should().Equal(
            "unknown_anchor_id", "event_not_offered", "unknown_action", "unknown_word", "unknown_word", null, "duplicate_cue", "graphic_text_missing");
        t.Moves.Should().ContainSingle().Which.EventId.Should().Be("e1");
    }

    [Fact]
    public void Graphics_are_dropped_when_graphics_are_off()
    {
        MotionTranslation t = MotionPlanTranslator.Translate(Plan(Cue("e0", "Graphic", text: "Hi")), Offered, Config(), graphicsEnabled: false);

        t.Graphics.Should().BeEmpty();
        t.Report.Single().Reason.Should().Be("graphics_not_enabled");
    }

    [Fact]
    public void The_cue_cap_defaults_to_eight_and_is_clamped()
    {
        MotionCue[] many = Enumerable.Range(0, 12).Select(i => Cue("e" + (i % 4), i < 4 ? "PunchIn" : i < 8 ? "SlowMo" : "Flash")).ToArray();

        MotionTranslation def = MotionPlanTranslator.Translate(Plan(many), Offered, Config(), true);
        def.Report.Count(r => r.Applied).Should().Be(8);
        def.Report.Where(r => !r.Applied).Select(r => r.Reason).Should().OnlyContain(r => r == "max_motion_cues");

        MotionTranslation two = MotionPlanTranslator.Translate(Plan(many), Offered, Config(max: 2), true);
        two.Report.Count(r => r.Applied).Should().Be(2);
        MotionPlanTranslator.ClampMaxCues(0).Should().Be(1);
        MotionPlanTranslator.ClampMaxCues(99).Should().Be(16);
        MotionPlanTranslator.ClampMaxCues(null).Should().Be(8);
    }

    [Fact]
    public void A_primitive_list_full_of_hand_written_entries_drops_the_plans_with_a_reason()
    {
        IReadOnlyList<EventMoveRequest> hand = Enumerable.Range(0, 16).Select(i => new EventMoveRequest("e" + i)).ToList();

        MotionTranslation t = MotionPlanTranslator.Translate(Plan(Cue("e0", "Shake")), Offered, Config(moves: hand), true);

        t.Moves.Should().BeEmpty();
        t.Report.Single().Reason.Should().Be("primitive_cap");
    }

    [Fact]
    public void An_empty_plan_translates_to_nothing()
    {
        MotionTranslation t = MotionPlanTranslator.Translate(Plan(), Offered, Config(), true);

        (t.Moves.Count + t.Ramps.Count + t.Flashes.Count + t.Graphics.Count + t.Report.Count).Should().Be(0);
    }
}
