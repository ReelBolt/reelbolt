using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using ReelBolt.Shared.Data.OutputSchemas;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Shared fixtures for the interleave guard: the bounded view of staging run
/// 1aa8d6f8-c626-4de1-8f14-c04115ff763b (an 18-scene authored composition as src0, a short portrait
/// clip as src1, four stills of six beats each as src2..src5) and an excerpt of that run's brief.
/// </summary>
internal static class InterleaveFixtures
{
    /// <summary>The brief's lines that matter here, verbatim from the staging run (Italian script brief).</summary>
    internal const string StagingBrief =
        "Build the animated film from script.md in this project — \"COME PERMETTERSI UNA M2 A 23 ANNI\".\n\n" +
        "WORDS\n" +
        "- Read script.md and keep the Italian exactly as written. Do not translate, rewrite or paraphrase.\n" +
        "- The two **bold** closing lines are the climax, not body copy.\n" +
        "- Only the script's own words appear as text: no extra headlines, no lower-thirds, no extra captions. " +
        "Numbers may appear alone as their own beat.\n\n" +
        "LOOK\n" +
        "- NO GRIDS, TIME BARS AT THE BOTTOM, SCENE COUNTERS IN THE CORNERS OR OTHER \"BEHIND THE SCENES\" ARTIFACTS, " +
        "JUST A STRAIGHT, PRODUCTION GRADE ANIMATION THAT FOLLOWS THE SCRIPT AND MAKES IT VISUALLY COMPELLING.\n\n" +
        "SHAPE AND LENGTH\n" +
        "- 9:16 PORTRAIT REEL, 1080x1920, one constant frame rate throughout.";

    /// <summary>
    /// The staging director's synthesized decision: one span s0→s17 (the whole composition) and an
    /// invented reason ("no added imagery" appears nowhere in the brief).
    /// </summary>
    internal static VideoEditDecisionOutput StagingDecision(string justification = "") => new()
    {
        Keep =
        [
            new VideoEditKeepSpan
            {
                FromId = "s0",
                ToId = "s17",
                Reason = "src0 is the authored composition. Kept whole from first scene to last: no scene trimmed, and no other " +
                         "clip inserted at a scene boundary, since the brief calls for a pure text animation."
            }
        ],
        EditRationale = "A composition cannot be trimmed inside a scene and its scenes must be retained in sequence, so the cut " +
                        "keeps it as one unbroken span. The brief asks for a straight, production-grade text animation in which " +
                        "only the script's own words appear, with no added imagery, so nothing is intercut between scenes.",
        SuggestedTitle = "COME PERMETTERSI UNA M2 A 23 ANNI",
        SingleSourceJustification = justification
    };

    /// <summary>The staging run's step-5 view shape: ids, "src", sources[].composition, orientation, meta.stillImages.</summary>
    internal static string StagingViewJson()
    {
        var shots = new JsonArray();
        int n = 0;
        void Add(int src, int count)
        {
            for (int i = 0; i < count; i++, n++)
                shots.Add(new JsonObject { ["id"] = $"s{n}", ["src"] = src, ["durationSec"] = 1.5 });
        }

        Add(0, 18);
        Add(1, 1);
        for (int still = 2; still <= 5; still++)
            Add(still, 6);

        var root = new JsonObject
        {
            ["view"] = new JsonObject
            {
                ["media"] = new JsonObject { ["width"] = 608, ["height"] = 1080 },
                ["shots"] = shots,
                ["silences"] = new JsonArray(),
                ["segments"] = new JsonArray(),
                ["sources"] = new JsonArray(
                    new JsonObject { ["src"] = 0, ["composition"] = true, ["durationSec"] = 121.5 },
                    new JsonObject { ["src"] = 1, ["durationSec"] = 4.0 },
                    new JsonObject { ["src"] = 2, ["durationSec"] = 9.0 },
                    new JsonObject { ["src"] = 3, ["durationSec"] = 9.0 },
                    new JsonObject { ["src"] = 4, ["durationSec"] = 9.0 },
                    new JsonObject { ["src"] = 5, ["durationSec"] = 9.0 }),
                ["orientation"] = new JsonObject
                {
                    ["target"] = new JsonObject { ["format"] = "Vertical", ["orientation"] = "portrait" },
                    ["sources"] = new JsonArray(
                        Orientation(0, "portrait", true), Orientation(1, "portrait", true),
                        Orientation(2, "landscape", false), Orientation(3, "portrait", true),
                        Orientation(4, "landscape", false), Orientation(5, "landscape", false)),
                    ["preferSrc"] = new JsonArray(0, 1, 3)
                }
            },
            ["meta"] = new JsonObject
            {
                ["operation"] = "videoAnalyze",
                ["stillImages"] = new JsonArray(2, 3, 4, 5),
                ["sourceCount"] = 6
            }
        };
        return root.ToJsonString();

        static JsonObject Orientation(int src, string shape, bool matches) =>
            new() { ["src"] = src, ["orientation"] = shape, ["matchesTarget"] = matches };
    }

    /// <summary>A decision that places src3's still and src1's clip between composition scenes.</summary>
    internal static VideoEditDecisionOutput IntercutDecision() => new()
    {
        Keep =
        [
            new VideoEditKeepSpan { FromId = "s0", ToId = "s5", Reason = "opening beats" },
            new VideoEditKeepSpan { FromId = "s25", ToId = "s26", Reason = "the car at the hinge" },
            new VideoEditKeepSpan { FromId = "s6", ToId = "s14", Reason = "middle beats" },
            new VideoEditKeepSpan { FromId = "s18", ToId = "s18", Reason = "the portrait clip" },
            new VideoEditKeepSpan { FromId = "s15", ToId = "s17", Reason = "climax and end card" }
        ],
        EditRationale = "Composition as the spine, other material at scene boundaries.",
        SuggestedTitle = "M2"
    };
}

/// <summary>
/// The pure detection matrix of <see cref="InterleaveGuard"/> — docs/video-editing.md
/// "Using every clip offered".
/// </summary>
public class InterleaveGuardTests
{
    private static InterleaveOffer StagingOffer() =>
        InterleaveOffer.FromView(JsonNode.Parse(InterleaveFixtures.StagingViewJson()));

    private static VideoEditDecisionOutput Decision(params (string From, string To)[] spans) => new()
    {
        Keep = spans.Select(s => new VideoEditKeepSpan { FromId = s.From, ToId = s.To, Reason = "r" }).ToList()
    };

    private static string View(params (string Id, int? Src)[] shots)
    {
        var array = new JsonArray();
        foreach ((string id, int? src) in shots)
        {
            var node = new JsonObject { ["id"] = id };
            if (src is { } s)
                node["src"] = s;
            array.Add(node);
        }

        return new JsonObject { ["view"] = new JsonObject { ["shots"] = array } }.ToJsonString();
    }

    [Fact]
    public void The_staging_decision_one_whole_composition_span_with_an_invented_reason_fails_the_guard()
    {
        InterleaveVerdict verdict = InterleaveGuard.Evaluate(
            StagingOffer(), InterleaveFixtures.StagingDecision(), InterleaveFixtures.StagingBrief);

        verdict.Required.Should().BeTrue();
        verdict.Satisfied.Should().BeFalse();
        verdict.Reason.Should().Be(InterleaveGuard.ReasonSingleSourceUnjustified);
        verdict.OfferedSources.Should().Equal(0, 1, 2, 3, 4, 5);
        verdict.UsedSources.Should().Equal(0);
        verdict.UnusedSources.Should().Equal(1, 2, 3, 4, 5);
        verdict.UnbrokenCompositions.Should().Equal(0);
    }

    [Fact]
    public void The_staging_directors_invented_restriction_quoted_as_a_justification_is_rejected()
    {
        // "with no added imagery" is the director's own phrase; the brief never says it.
        InterleaveVerdict verdict = InterleaveGuard.Evaluate(
            StagingOffer(),
            InterleaveFixtures.StagingDecision("a straight text animation with no added imagery"),
            InterleaveFixtures.StagingBrief);

        verdict.Satisfied.Should().BeFalse();
        verdict.Reason.Should().Be(InterleaveGuard.ReasonJustificationNotInBrief);
        verdict.JustificationVerified.Should().BeFalse();
    }

    [Fact]
    public void A_paraphrase_of_a_real_brief_line_is_rejected()
    {
        InterleaveVerdict verdict = InterleaveGuard.Evaluate(
            StagingOffer(),
            InterleaveFixtures.StagingDecision("only the words of the script appear on screen"),
            InterleaveFixtures.StagingBrief);

        verdict.Satisfied.Should().BeFalse();
        verdict.JustificationVerified.Should().BeFalse();
    }

    [Fact]
    public void A_verbatim_quote_is_accepted_whatever_its_case_quotes_and_whitespace()
    {
        var brief = "Edit the interview.\nUse ONLY the  “main interview” take — no b-roll, please.";
        string view = View(("s0", 0), ("s1", 0), ("s2", 1));

        InterleaveVerdict verdict = InterleaveGuard.Evaluate(
            InterleaveOffer.FromView(JsonNode.Parse(view)),
            new VideoEditDecisionOutput
            {
                Keep = [new VideoEditKeepSpan { FromId = "s0", ToId = "s1" }],
                SingleSourceJustification = "\"use only the \"main interview\" take - no b-roll\""
            },
            brief);

        verdict.Required.Should().BeTrue();
        verdict.Satisfied.Should().BeTrue();
        verdict.Reason.Should().Be(InterleaveGuard.ReasonJustified);
        verdict.JustificationVerified.Should().BeTrue();
    }

    [Fact]
    public void A_quote_dropping_markdown_emphasis_from_the_brief_still_matches()
    {
        InterleaveGuard.QuoteAppearsInBrief("The two bold closing lines are the climax", InterleaveFixtures.StagingBrief)
            .Should().BeTrue();
    }

    [Theory]
    [InlineData("only the")]          // too short
    [InlineData("script.md")]         // too few words
    [InlineData("")]
    [InlineData("   ")]
    public void A_trivial_quote_proves_nothing_even_when_it_occurs_in_the_brief(string quote)
    {
        InterleaveGuard.QuoteAppearsInBrief(quote, InterleaveFixtures.StagingBrief).Should().BeFalse();
    }

    [Fact]
    public void A_quote_with_no_brief_at_all_is_never_verified()
    {
        InterleaveGuard.QuoteAppearsInBrief("keep only the phone take", null).Should().BeFalse();
    }

    [Fact]
    public void A_single_offered_source_requires_nothing()
    {
        // No "src" keys: the single-source view shape.
        string view = View(("s0", null), ("s1", null), ("s2", null));

        InterleaveVerdict verdict = InterleaveGuard.Evaluate(
            InterleaveOffer.FromView(JsonNode.Parse(view)), Decision(("s0", "s2")), userRequest: null);

        verdict.Required.Should().BeFalse();
        verdict.Satisfied.Should().BeTrue();
        verdict.Reason.Should().Be(InterleaveGuard.ReasonSingleSourceOffered);
    }

    [Fact]
    public void A_single_composition_view_requires_nothing_even_when_kept_unbroken()
    {
        string view = new JsonObject
        {
            ["view"] = new JsonObject
            {
                ["media"] = new JsonObject { ["composition"] = true },
                ["shots"] = new JsonArray(new JsonObject { ["id"] = "s0" }, new JsonObject { ["id"] = "s1" })
            }
        }.ToJsonString();

        InterleaveVerdict verdict = InterleaveGuard.Evaluate(
            InterleaveOffer.FromView(JsonNode.Parse(view)), Decision(("s0", "s1")), null);

        verdict.Required.Should().BeFalse();
    }

    [Fact]
    public void A_source_listed_with_no_offered_ids_is_not_usable()
    {
        // src1 exists (sources[]) but the view's budget dropped all of its ids.
        string view = new JsonObject
        {
            ["view"] = new JsonObject
            {
                ["shots"] = new JsonArray(
                    new JsonObject { ["id"] = "s0", ["src"] = 0 }, new JsonObject { ["id"] = "s1", ["src"] = 0 }),
                ["sources"] = new JsonArray(new JsonObject { ["src"] = 0 }, new JsonObject { ["src"] = 1 })
            }
        }.ToJsonString();

        InterleaveVerdict verdict = InterleaveGuard.Evaluate(
            InterleaveOffer.FromView(JsonNode.Parse(view)), Decision(("s0", "s1")), null);

        verdict.Required.Should().BeFalse();
    }

    [Fact]
    public void Mixed_usage_of_raw_footage_satisfies_the_guard()
    {
        string view = View(("s0", 0), ("s1", 0), ("s2", 1), ("s3", 2));

        InterleaveVerdict verdict = InterleaveGuard.Evaluate(
            InterleaveOffer.FromView(JsonNode.Parse(view)), Decision(("s0", "s1"), ("s2", "s2")), null);

        verdict.Required.Should().BeTrue();
        verdict.Satisfied.Should().BeTrue();
        verdict.Reason.Should().Be(InterleaveGuard.ReasonInterleaved);
        verdict.UnusedSources.Should().Equal(new[] { 2 }, "not every offered clip has to be used — only more than one");
    }

    [Fact]
    public void The_staging_offer_intercut_at_scene_boundaries_satisfies_the_guard()
    {
        InterleaveVerdict verdict = InterleaveGuard.Evaluate(
            StagingOffer(), InterleaveFixtures.IntercutDecision(), InterleaveFixtures.StagingBrief);

        verdict.Satisfied.Should().BeTrue();
        verdict.Reason.Should().Be(InterleaveGuard.ReasonInterleaved);
        verdict.UsedSources.Should().Equal(0, 1, 3);
        verdict.UnbrokenCompositions.Should().BeEmpty();
    }

    [Fact]
    public void A_composition_kept_unbroken_with_a_still_only_tacked_on_the_end_fails()
    {
        InterleaveVerdict verdict = InterleaveGuard.Evaluate(
            StagingOffer(), Decision(("s0", "s17"), ("s25", "s26")), InterleaveFixtures.StagingBrief);

        verdict.Satisfied.Should().BeFalse();
        verdict.Reason.Should().Be(InterleaveGuard.ReasonCompositionUnbroken);
        verdict.UnbrokenCompositions.Should().Equal(0);
    }

    [Fact]
    public void A_composition_split_into_consecutive_runs_with_nothing_between_them_is_still_unbroken()
    {
        InterleaveVerdict verdict = InterleaveGuard.Evaluate(
            StagingOffer(), Decision(("s25", "s25"), ("s0", "s8"), ("s9", "s17")), InterleaveFixtures.StagingBrief);

        verdict.Satisfied.Should().BeFalse();
        verdict.Reason.Should().Be(InterleaveGuard.ReasonCompositionUnbroken);
    }

    [Fact]
    public void Spans_that_were_not_offered_or_bridge_two_clips_neither_satisfy_nor_trip_the_guard()
    {
        // s0→s18 bridges src0 and src1; s99 was never offered. Only s0→s17 counts.
        InterleaveVerdict verdict = InterleaveGuard.Evaluate(
            StagingOffer(), Decision(("s0", "s17"), ("s0", "s18"), ("s99", "s99")), null);

        verdict.UsedSources.Should().Equal(0);
        verdict.Satisfied.Should().BeFalse();
    }

    [Fact]
    public void A_decision_with_no_valid_span_is_reported_as_nothing_kept()
    {
        InterleaveVerdict verdict = InterleaveGuard.Evaluate(StagingOffer(), Decision(("s99", "s99")), null);

        verdict.Required.Should().BeTrue();
        verdict.Satisfied.Should().BeFalse();
        verdict.Reason.Should().Be(InterleaveGuard.ReasonNothingKept);
    }

    [Fact]
    public void An_orientation_mismatch_is_no_excuse_the_mismatched_stills_still_count_as_unused()
    {
        InterleaveVerdict verdict = InterleaveGuard.Evaluate(
            StagingOffer(), InterleaveFixtures.StagingDecision(), InterleaveFixtures.StagingBrief);

        verdict.UnusedSources.Should().Contain(new[] { 2, 4, 5 }, "landscape stills in a 9:16 reel are a preference, not an exclusion");
    }

    [Fact]
    public void The_retry_feedback_names_the_unused_clips_the_scene_boundaries_the_room_proposal_and_the_opt_out()
    {
        InterleaveOffer offer = StagingOffer();
        InterleaveVerdict verdict = InterleaveGuard.Evaluate(
            offer, InterleaveFixtures.StagingDecision("with no added imagery at all"), InterleaveFixtures.StagingBrief);

        string? proposal = InterleaveGuard.FindIntercutProposal(
            [
                ("Pacing editor", "Keep s0 through s17 as written."),
                ("Story editor", "Use src0 as the spine with src3 at the narrative hinges and a still at the close.")
            ],
            offer, verdict);
        string feedback = InterleaveGuard.BuildRetryFeedback(offer, verdict, proposal);

        proposal.Should().StartWith("Story editor:");
        feedback.Should().Contain("src1 (video clip, ids s18, portrait — matches the target format)");
        feedback.Should().Contain("src3 (still photo, ids s25..s30");
        feedback.Should().Contain("src2 (still photo, ids s19..s24, landscape — cropped to fit");
        feedback.Should().Contain("s0|s1").And.Contain("s16|s17");
        feedback.Should().Contain("src3 at the narrative hinges");
        feedback.Should().Contain("\"with no added imagery at all\" was REJECTED");
        feedback.Should().Contain("singleSourceJustification");
        feedback.Should().Contain("Never invent a restriction");
    }

    [Fact]
    public void A_room_turn_that_never_mentions_other_material_is_not_a_proposal()
    {
        InterleaveOffer offer = StagingOffer();
        InterleaveVerdict verdict = InterleaveGuard.Evaluate(offer, InterleaveFixtures.StagingDecision(), null);

        InterleaveGuard.FindIntercutProposal([("Director", "Keep s0 through s17. ROOM_DECIDED")], offer, verdict)
            .Should().BeNull();
    }

    [Fact]
    public void The_artifact_and_the_view_describe_the_same_offer()
    {
        var shots = new List<VideoAnalysisShot>();
        for (int i = 0; i < 18; i++)
            shots.Add(new VideoAnalysisShot($"s{i}", i, i + 1, SourceIndex: 0));
        shots.Add(new VideoAnalysisShot("s18", 0, 4, SourceIndex: 1));
        for (int i = 19; i < 25; i++)
            shots.Add(new VideoAnalysisShot($"s{i}", 0, 1.5, SourceIndex: 2));

        var media = new VideoAnalysisMedia(10, 30, 1, 1080, 1920);
        var artifact = new VideoAnalysisArtifact(
            2, media, shots, [], [], [], shots.Select(s => s.Id).ToList(), null!)
        {
            Sources =
            [
                new VideoAnalysisSourceInfo(0, "k0", media, IsComposition: true),
                new VideoAnalysisSourceInfo(1, "k1", media),
                new VideoAnalysisSourceInfo(2, "k2", media)
            ]
        };

        InterleaveOffer offer = InterleaveOffer.FromArtifact(artifact);
        InterleaveVerdict verdict = InterleaveGuard.Evaluate(offer, InterleaveFixtures.StagingDecision(), InterleaveFixtures.StagingBrief);

        offer.Source(0)!.IsComposition.Should().BeTrue();
        offer.Source(0)!.SceneIds.Should().HaveCount(18);
        verdict.Satisfied.Should().BeFalse();
        verdict.UnbrokenCompositions.Should().Equal(0);
        verdict.UnusedSources.Should().Equal(1, 2);
    }

    [Fact]
    public void The_recorded_finding_carries_every_fact_the_reviewer_is_told_about()
    {
        InterleaveVerdict verdict = InterleaveGuard.Evaluate(
            StagingOffer(), InterleaveFixtures.StagingDecision(), InterleaveFixtures.StagingBrief);

        JsonObject node = verdict.ToJson(retried: true);

        node.Select(kv => kv.Key).Should().BeEquivalentTo(
            "required", "satisfied", "reason", "offeredSources", "usedSources", "unusedSources",
            "unbrokenCompositions", "justification", "justificationVerified", "retried");
        verdict.ToJson().ContainsKey("retried").Should().BeFalse("the compile step's fact carries no retry flag");
        JsonSerializer.Serialize(node).Should().Contain("\"satisfied\":false");
    }
}
