using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.OutputSchemas;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.Review;
using ReelBolt.WorkflowEngine.Services.Review;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Review;

/// <summary>
/// The stronger review evidence (docs/design/review-loop-adjustments.md "Seeing the film"): first-party timeline and
/// audio facts, bounded targeted looks, their cold-start hold, the review output schema and the templates that use them.
/// </summary>
public class ReviewEvidenceTests
{
    private const string Ebur128 = """
        [Parsed_ebur128_0 @ 0x1] t: 0.0999792  TARGET:-23 LUFS    M:-120.7 S:-120.7     I: -70.0 LUFS       LRA:   0.0 LU  FTPK: -inf dBFS  TPK: -inf dBFS
        [Parsed_ebur128_0 @ 0x1] t: 0.199979   TARGET:-23 LUFS    M:-70.0 S:-120.7     I: -70.0 LUFS       LRA:   0.0 LU
        [Parsed_ebur128_0 @ 0x1] t: 1.1        TARGET:-23 LUFS    M:-60.0 S:-60.0     I: -60.0 LUFS       LRA:   0.0 LU
        [Parsed_ebur128_0 @ 0x1] t: 1.2        TARGET:-23 LUFS    M:-16.0 S:-16.0     I: -16.0 LUFS       LRA:   0.0 LU
        [Parsed_ebur128_0 @ 0x1] t: 2.0        TARGET:-23 LUFS    M: -8.0 S:-12.0     I: -14.0 LUFS       LRA:   2.0 LU
        [Parsed_ebur128_0 @ 0x1] Summary:

          Integrated loudness:
            I:         -14.3 LUFS
            Threshold: -24.6 LUFS

          Loudness range:
            LRA:         5.4 LU
            Threshold:  -34.6 LUFS

          True peak:
            Peak:        -0.6 dBFS
        """;

    [Fact]
    public void The_ebur128_summary_and_momentary_lines_are_parsed()
    {
        ReviewAudioMeasurement m = ReviewAudioFacts.Parse(Ebur128)!;

        m.IntegratedLufs.Should().Be(-14.3);
        m.RangeLu.Should().Be(5.4);
        m.TruePeakDbtp.Should().Be(-0.6);
        m.Momentary.Should().HaveCount(5);
        m.Momentary[0].Lufs.Should().Be(-120.7);
        ReviewAudioFacts.Silences(m.Momentary).Should().ContainSingle().Which.Should().Be((0.0999792, 1.2));
        ReviewAudioFacts.Parse("no loudness here").Should().BeNull();
    }

    [Fact]
    public void The_audio_block_reports_cues_cuts_and_says_narration_is_not_transcribed()
    {
        TimelineManifest manifest = Manifest(cutAt: 1.2);
        string block = ReviewAudioFacts.Render(ReviewAudioFacts.Parse(Ebur128), manifest)!;

        block.Should().StartWith("[AUDIO FACTS]").And.EndWith("[END AUDIO FACTS]");
        block.Should().Contain("-14.3 LUFS").And.Contain("Sound-effect cues").And.Contain("2 s peaks at -8.0 LUFS");
        block.Should().Contain("Loudness jumps").And.Contain("1.2 s (+44.0 LU)").And.Contain("not transcribed");
        block.Length.Should().BeLessThanOrEqualTo(ReviewAudioFacts.MaxChars);
        ReviewAudioFacts.MeasureArgs("in.mp4").Should().Contain("ebur128=peak=true");
    }

    [Fact]
    public void The_timeline_block_is_first_party_bounded_and_drops_storage_keys()
    {
        const string output = """{"status":"completed","outputStorageKey":"projects/x/outputFiles/f.mp4","length":{"requestedSec":20,"actualSec":19.5},"loudness":{"measuredLufs":-14.1,"explanation":"long words"}}""";
        const string config = """{"enableGraphics":true,"overlayFontSizePct":7,"enableSfx":true,"maxSfxCueSeconds":1.5,"outputFit":"Pad"}""";
        TimelineManifest manifest = Manifest(items: 200);

        string block = ReviewTimelineFacts.Render(manifest, output, config, 7)!;

        block.Should().StartWith("[TIMELINE FACTS]").And.EndWith("[END TIMELINE FACTS]");
        block.Should().Contain("Output: 1080x1920").And.Contain("fit Pad").And.Contain("7% of frame height").And.Contain("at most 1.5 s each");
        block.Should().Contain("text \"BUY NOW\"").And.Contain("cue \"x1\"").And.Contain("more");
        block.Should().NotContain("StorageKey").And.NotContain("long words");
        block.Length.Should().BeLessThanOrEqualTo(ReviewTimelineFacts.MaxChars);
        ReviewTimelineFacts.Render(null, null, null, 7).Should().BeNull();
    }

    [Fact]
    public void A_look_request_is_bounded_deduplicated_and_its_region_clamped()
    {
        const string answer = """{"score":7,"summary":"s","looks":[{"atSec":[1,1,2,3,4,5,6,7,8],"region":{"x":-1,"y":0.9,"w":5,"h":0.5},"why":"check\u0000 title"}]}""";

        ReviewLookRequest request = ReviewTargetedLooks.ReadRequest(answer, AgentType.VideoReviewAgent, 6)!;

        request.Frames.Select(f => f.AtSec).Should().Equal(1, 2, 3, 4, 5, 6);
        request.Frames[0].Region.Should().Be(new ReviewLookRegion(0, 0.9, 1, 0.1));
        request.Focus.Should().Be("check title");
        ReviewTargetedLooks.ReadRequest("""{"score":7,"summary":"s","looks":[]}""", AgentType.VideoReviewAgent, 6).Should().BeNull();
    }

    [Fact]
    public void A_cropped_look_is_one_first_party_ffmpeg_argv()
    {
        FfmpegReviewFilmFacts.LookArgs("in.mp4", "out.jpg", 4.8, new ReviewLookRegion(0, 0.6, 1, 0.4))
            .Should().Equal("-hide_banner", "-nostdin", "-y",
                "-protocol_whitelist", "file", "-format_whitelist", UserMediaInputFormats.Whitelist, "-ss", "4.8", "-i", "in.mp4", "-frames:v", "1",
                "-vf", "crop=iw*1:ih*0.4:iw*0:ih*0.6,scale=1280:-2", "-q:v", "3", "out.jpg");
    }

    [Theory]
    [InlineData("""{"visualCheck":{"targetedLooks":true}}""", false, 2, 6)]
    [InlineData("""{"visualCheck":{"enabled":true,"targetedLooks":true,"maxExtraRounds":9,"maxFramesPerRound":40}}""", true, 3, 8)]
    public void Targeted_looks_are_held_on_the_vision_model_plus_one_reviewer_pass_per_round(string config, bool sampled, int rounds, int frames)
    {
        StepUsageProfile basis = StepUsageProfiles.For(StepType.ReviewLoop, AgentType.VideoReviewAgent);
        StepUsageProfile profile = StepUsageProfiles.For(new WorkflowStep { StepType = StepType.ReviewLoop, ReviewLoopConfigJson = config }, AgentType.VideoReviewAgent);

        decimal lookInput = rounds * StepUsageProfiles.VisualCheckCalls * (StepUsageProfiles.VisualCheckPromptTokens + frames * StepUsageProfiles.VisualCheckTokensPerFrame);
        decimal sampledInput = sampled ? StepUsageProfiles.VisualCheckCalls * (StepUsageProfiles.VisualCheckPromptTokens + 9 * StepUsageProfiles.VisualCheckTokensPerFrame) : 0;
        profile.Quantities.Where(q => q.Target == ProfilePriceTarget.Vision && q.Kind == UsageKind.LlmInputTokens).Sum(q => q.Quantity)
            .Should().Be(lookInput + sampledInput);
        decimal chatInput(StepUsageProfile p) => p.Quantities.Where(q => q.Target == ProfilePriceTarget.Chat && q.Kind == UsageKind.LlmInputTokens).Sum(q => q.Quantity);
        chatInput(profile).Should().Be(chatInput(basis) * (rounds + 1));
        profile.Source.Should().Contain("targeted-look");
    }

    [Fact]
    public void Off_the_hold_and_the_config_are_unchanged()
    {
        StepUsageProfiles.For(new WorkflowStep { StepType = StepType.ReviewLoop, ReviewLoopConfigJson = """{"visualCheck":{"targetedLooks":false,"timelineFacts":true,"audioFacts":true}}""" }, AgentType.VideoReviewAgent)
            .Should().BeEquivalentTo(StepUsageProfiles.For(StepType.ReviewLoop, AgentType.VideoReviewAgent));
        ReviewLoopStepConfig defaults = new();
        defaults.AllowAdjustments.Should().BeFalse();
        VisualCheckConfig visual = new();
        (visual.TimelineFacts || visual.AudioFacts || visual.TargetedLooks).Should().BeFalse();
        visual.ClampedExtraRounds.Should().Be(0);
    }

    [Fact]
    public void The_review_answer_can_only_name_a_step_an_item_a_setting_a_value_and_a_reason()
    {
        // Schema invariant: an adjustment cannot carry a provider, a cost or a prompt (the engine refuses those names); it
        // names a step, an optional item (a line, shot, take or scene of a step that takes a change request), a setting,
        // a value as text and a reason. A look carries times, a region and words.
        typeof(ReviewSettingAdjustment).GetProperties().Select(p => p.Name).Should().BeEquivalentTo(["StepOrder", "Item", "Setting", "Value", "Reason"]);
        typeof(ReviewSettingAdjustment).GetProperty("Value")!.PropertyType.Should().Be(typeof(string));
        typeof(ReviewSettingAdjustment).GetProperty("Item")!.PropertyType.Should().Be(typeof(string));
        typeof(ReviewLookRequestItem).GetProperties().Select(p => p.Name).Should().BeEquivalentTo(["AtSec", "Region", "Why"]);
        typeof(ReviewLookRegionItem).GetProperties().Select(p => p.PropertyType).Should().OnlyContain(t => t == typeof(double));
        foreach (Type review in new[] { typeof(VideoReviewOutput), typeof(ReviewOutput) })
        {
            review.GetProperty("Adjustments")!.PropertyType.Should().Be(typeof(List<ReviewSettingAdjustment>));
            review.GetProperty("Looks")!.PropertyType.Should().Be(typeof(List<ReviewLookRequestItem>));
        }

        string json = JsonSerializer.Serialize(new VideoReviewOutput(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        json.Should().Contain("\"adjustments\":[]").And.Contain("\"looks\":[]");
    }

    [Fact]
    public void Every_shipped_template_turns_adjustments_and_the_film_evidence_on()
    {
        // The product owner's call (docs/design/review-loop-adjustments.md): every shipped review loop may change steps and
        // sees the film (sampled frames, timeline and audio facts, targeted looks); the decision early stop stays off.
        List<WorkflowTemplateStepDefinition> loops = [.. WorkflowTemplateCatalog.GetAll().SelectMany(t => t.Steps).Where(s => s.StepType == StepType.ReviewLoop)];
        loops.Should().NotBeEmpty();
        loops.Should().OnlyContain(s => s.ReviewLoopConfigJson == WorkflowTemplateCatalog.ShippedReviewLoopConfigJson);
    }

    private static TimelineManifest Manifest(int items = 2, double cutAt = 1.0)
    {
        List<TimelineItem> cuts = [.. Enumerable.Range(0, items).Select(i => new TimelineItem(Id: $"c{i}", TimelineStartSec: i * cutAt, TimelineEndSec: (i + 1) * cutAt, ShotId: $"s{i}", SourceIndex: 0, SourceStartSec: i, SourceEndSec: i + 1))];
        return new TimelineManifest(
            1, Guid.Empty, Guid.Empty,
            new TimelineOutputVideo(1080, 1920, 30, items, "projects/x/outputFiles/f.mp4"),
            [],
            [
                new TimelineTrack("track-video", "video", "Video", 0, cuts),
                new TimelineTrack("track-graphics", "graphics", "Graphics", 1, [new TimelineItem(TimelineStartSec: 1, TimelineEndSec: 3, Text: "BUY NOW", PlacementId: "p1", Style: "LowerThird")]),
                new TimelineTrack("track-sfx", "sfx", "SFX", 2, [new TimelineItem(TimelineStartSec: 2, TimelineEndSec: 2.5, SfxId: "x1", DurationSec: 0.5, GainDb: -6)]),
            ]);
    }
}
