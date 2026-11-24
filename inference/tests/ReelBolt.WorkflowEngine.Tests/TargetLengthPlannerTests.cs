using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

public class TargetLengthPlannerTests
{
    [Fact]
    public void A_cut_that_fits_is_untouched()
    {
        TargetLengthPlanner.Plan([2.0, 3.0, 4.0], null, 15).Should().Equal(2.0, 3.0, 4.0);
        TargetLengthPlanner.Plan([2.0, 3.0, 4.0], null, 0).Should().Equal(2.0, 3.0, 4.0);
    }

    [Fact]
    public void The_longest_spans_are_shortened_first_and_the_last_span_is_kept_whole()
    {
        // 6 + 2 + 3 = 11 against 8: the 6 s span loses 3 s, the 2 s beat and the last span are untouched.
        TargetLengthPlanner.Plan([6.0, 2.0, 3.0], null, 8).Should().Equal(3.0, 2.0, 3.0);
    }

    [Fact]
    public void A_span_is_never_shortened_below_its_narration_or_the_minimum()
    {
        // Floors: span 0 carries 4.5 s of narration. The target 6 is out of reach: 4.5 + 1 + 2.
        TargetLengthPlanner.Plan([6.0, 5.0, 2.0], [4.5, 0, 0], 6).Should().Equal(4.5, 1.0, 2.0);
    }

    [Fact]
    public async Task The_compile_trims_a_long_cut_to_the_target_and_reports_it()
    {
        // s0 [0,2.5) · s2 [3.5,6.5) · s4 [7.5,11): 9 s kept against a 6 s target. The last span is
        // kept whole; s0 and s2 share the 2.5 s to lose under one ceiling of 1.25 s.
        var fixture = new ReelCompileFixture(ReelCompileFixture.ShotsArtifact(2.5, 1.0, 3.0, 1.0, 3.5));
        fixture.Config = c => c with { TargetDurationSec = 6, PrePaddingMs = 0, PostPaddingMs = 0 };

        StepExecutionResult result = await fixture.RunAsync(ReelCompileFixture.DecisionJson("s0", "s2", "s4"));

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        fixture.FilterGraph.Should().StartWith("[0:v]fps=30/1:start_time=0,tpad=stop=-1:stop_mode=clone,trim=end=11.066667,select='gte(t,0)*lt(t,1.2666666666666666)+gte(t,3.5)*lt(t,4.766666666666667)+gte(t,7.5)*lt(t,11)'", "cuts snap to whole frames at 30 fps");
        JsonObject length = fixture.Output!["length"]!.AsObject();
        length["met"]!.GetValue<bool>().Should().BeTrue();
        length["fitted"]!["trimmedSec"]!.GetValue<double>().Should().BeApproximately(3.0, 1e-6);
        length["fitted"]!["trimmedSpans"]!.GetValue<int>().Should().Be(2);
    }
}

public class NarrationToleranceTests
{
    [Fact]
    public void A_sub_frame_overrun_is_a_fit_and_a_real_one_is_not()
    {
        var shot = new TimelinePlacement(0, 0, 3.0);
        NarrationFitFacts.Measure(shot, 0, 3.019).Fits.Should().BeTrue("19 ms is the WAV's tail, not a word over the cut");
        NarrationFitFacts.Measure(shot, 0, 3.4).OverflowSec.Should().Be(0.4);
    }

    [Fact]
    public void A_recognisers_sound_alike_spelling_of_a_brand_is_not_a_mispronunciation()
    {
        NarrationIntelligibilityFacts.SoundsAlike("reelbolt", "realbold").Should().BeTrue();
        string[] reference = NarrationIntelligibilityFacts.Normalize("That's the whole edit. ReelBolt.");
        NarrationIntelligibilityFacts.AlignSpelling(reference, NarrationIntelligibilityFacts.Normalize("That's the whole edit. Real bold."))
            .Should().Equal(reference);
        NarrationIntelligibilityFacts.SoundsAlike("reelbolt", "realboat").Should().BeFalse();
        NarrationIntelligibilityFacts.SoundsAlike("battle", "bottle").Should().BeFalse("three consonants are too few to call a match");
    }
}

public class LoudnessHeadroomTests
{
    [Fact]
    public void Extra_headroom_lowers_only_the_limiter_ceiling()
    {
        var m = new LoudnessMeasurement(-20, -3, 6, -30, 0.5);
        string plain = string.Join(' ', LoudnessNormalizer.ApplyArgs("in.mp4", "out.mp4", (-14, -1.5, 11), m));
        string lowered = string.Join(' ', LoudnessNormalizer.ApplyArgs("in.mp4", "out.mp4", (-14, -1.5, 11), m, 1.5));
        plain.Should().Contain($"alimiter=limit={LoudnessNormalizer.LimiterCeiling(-1.5).ToString(System.Globalization.CultureInfo.InvariantCulture)}:");
        lowered.Should().Contain($"alimiter=limit={LoudnessNormalizer.LimiterCeiling(-3.0).ToString(System.Globalization.CultureInfo.InvariantCulture)}:");
        lowered.Replace("alimiter=limit=0.631", "").Should().Contain("loudnorm=I=-14:TP=-1.5", "the loudness target is unchanged");
    }
}

public class EditorTransitionTests
{
    [Fact]
    public void Preset_and_speed_words_resolve_and_nothing_else_does()
    {
        EditorTransitionCatalog.Resolve("SlideLeft", "Snap", out bool unknown)!.Should().BeEquivalentTo(
            new EditorTransitionCatalog.Choice("SlideLeft", SeamTreatment.Dissolve, "slideleft", "Snap", 0.2));
        unknown.Should().BeFalse();
        EditorTransitionCatalog.Resolve("dip to black", null, out _)!.Treatment.Should().Be(SeamTreatment.DipToBlack);
        EditorTransitionCatalog.Resolve("FlashWhite", "Glacial", out _)!.Speed.Should().Be("Quick", "an unknown speed takes the default");
        EditorTransitionCatalog.Resolve("Cut", "Slow", out _)!.Treatment.Should().Be(SeamTreatment.HardCut);
        EditorTransitionCatalog.Resolve("", "Slow", out unknown).Should().BeNull();
        unknown.Should().BeFalse("empty leaves the seam to the rules");
        EditorTransitionCatalog.Resolve("Starwipe", null, out unknown).Should().BeNull();
        unknown.Should().BeTrue();
    }

    [Fact]
    public void Every_catalog_preset_is_taught_to_the_editor()
    {
        string prompt = (string)typeof(ReelBolt.WorkflowEngine.Agents.Production.VideoStoryEditorAgent)
            .GetField("DefaultPrompt", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
        foreach ((string preset, _, _) in EditorTransitionCatalog.Presets)
        {
            string stem = new[] { "Left", "Right", "Up", "Down", "TopLeft", "TopRight", "BottomLeft", "BottomRight", "Open", "Close", "Horizontal", "Vertical" }
                .Where(preset.EndsWith).OrderByDescending(x => x.Length).Select(x => preset[..^x.Length]).FirstOrDefault() ?? preset;
            prompt.Should().Contain(stem, $"the editor must be told about {preset}");
        }
    }

    [Fact]
    public void The_planner_takes_the_editors_choice_for_the_span_after_the_seam()
    {
        var spans = new List<ReelBolt.WorkflowEngine.Execution.StepExecutors.VideoCompileStepExecutor.ResolvedSpan>
        {
            new(0, 3, 0, 3, 0, 90, 0, ["s0"]),
            new(5, 8, 5, 8, 150, 240, 0, ["s2"]),
            new(10, 13, 10, 13, 300, 390, 0, ["s4"]),
        };
        var config = new VideoCompileStepConfig(1, new ExtractInputRef(ExtractInputSource.Previous), 1) with { TransitionPolicy = VideoTransitionPolicy.Editor };
        var choices = new List<EditorTransitionCatalog.Choice?>
        {
            null,
            EditorTransitionCatalog.Resolve("WipeRight", "Smooth", out _),
            EditorTransitionCatalog.Resolve("Cut", null, out _),
        };

        IReadOnlyList<SeamPlan> plans = SeamTransitionPlanner.Plan([], spans, config, xfadeAvailable: true, out _, editorChoices: choices);

        plans[0].XfadeTransition.Should().Be("wiperight");
        plans[0].OverlapSec.Should().Be(0.6);
        plans[0].Rule.Should().Be("E:WipeRight:Smooth");
        plans[1].Treatment.Should().Be(SeamTreatment.HardCut);
    }
}

public class StillImageClipTests
{
    [Fact]
    public void Images_are_recognised_by_extension_or_by_a_durationless_image_codec()
    {
        StillImageClip.IsImagePath("projects/p/userFiles/photo.JPG").Should().BeTrue();
        StillImageClip.IsImagePath("projects/p/userFiles/clip.mp4").Should().BeFalse();
        StillImageClip.IsImageProbe(new MediaProbeResult(0, 25, 1, 800, 600, "png", null, null)).Should().BeTrue();
        StillImageClip.IsImageProbe(new MediaProbeResult(12, 25, 1, 800, 600, "mjpeg", "pcm_s16le", 48000))
            .Should().BeFalse("a motion-JPEG video has a duration");
    }

    [Fact]
    public void A_big_photo_is_scaled_to_an_even_size_with_its_own_aspect()
    {
        StillImageClip.Size(6000, 4000).Should().Be((1920, 1280));
        StillImageClip.Size(1081, 1351).Should().Be((1082, 1352));
    }

    [Fact]
    public void Each_move_is_declared_beat_by_beat_with_what_the_photo_shows()
    {
        IReadOnlyList<DeclaredScene> wide = StillImageClip.Scenes(1600, 1066, "A red sports car on a mountain road.");
        wide.Select(s => s.Name).Should().Equal(
            "push in (1/2)", "push in (2/2)", "pull out (1/2)", "pull out (2/2)", "pan right (1/2)", "pan right (2/2)");
        wide.Should().OnlyContain(s => s.DurationInFrames == 45);
        wide[0].Description.Should().StartWith("A red sports car");
        wide.Skip(1).Should().OnlyContain(s => s.Description.StartsWith("Same photo") && !s.Description.Contains("sports car"),
            "one caption per photo, not six: repeated captions blew the view budget");
        wide.Select(s => s.FromFrame).Should().Equal(0, 45, 90, 135, 180, 225);
        StillImageClip.Scenes(1080, 1350, "x").Last().Name.Should().StartWith("pan down", "a tall photo pans along its height");

        (CompositionSceneManifest? manifest, IReadOnlyList<string> errors) = CompositionSceneManifest.FromDeclaration(null, StillImageClip.Fps, wide);
        errors.Should().BeEmpty();
        manifest!.ToShotSpans(StillImageClip.TotalSeconds).Spans.Should().HaveCount(6);
    }

    [Fact]
    public void The_clip_is_three_moves_long_with_no_audio()
    {
        IReadOnlyList<string> args = StillImageClip.BuildArgs("in.png", "out.mp4", 1600, 1066);
        args.Should().ContainInOrder("-frames:v", "270", "-an");
        string vf = args[args.ToList().IndexOf("-vf") + 1];
        vf.Should().Contain("zoompan=z='if(lt(on,90),1+0.08*on/90,if(lt(on,180),1+0.08-0.08*(on-90)/90,1.12))'");
        vf.Should().Contain("x='if(lt(on,180),iw/2-(iw/zoom/2),(iw-iw/zoom)*(on-180)/90)'");
    }
}

public class CaptionOverDesignedTextTests
{
    [Fact]
    public void Text_in_both_bands_leaves_a_caption_nowhere_to_go()
    {
        CaptionPlacementProbe.BothBusy(14, 11).Should().BeTrue("a Remotion card with titles top and subtitles bottom");
        CaptionPlacementProbe.BothBusy(14, 3).Should().BeFalse("the top is clean: the caption moves up instead");
        CaptionPlacementProbe.ShouldMoveUp(14, 3).Should().BeTrue();
    }
}

public class NarrationCarryAnchorTests
{
    [Fact]
    public async Task A_line_may_not_carry_over_the_shot_the_next_line_was_written_for_even_when_that_line_is_pushed_back()
    {
        // s0 [0,2.5) · s2 [3.5,6.5) · s4 [7.5,11) kept: output placements [0,2.5) [2.5,5.5) [5.5,9).
        // vo0 (anchored to s0) speaks 7 s; vo1 is anchored to s2 but pushed back to 7.0 (inside the
        // third shot) so the two do not overlap. vo1's audio starting in the third shot must not let
        // vo0 claim the second one, which is the picture vo1 was written for.
        var fixture = new ReelCompileFixture(ReelCompileFixture.ShotsArtifact(2.5, 1.0, 3.0, 1.0, 3.5));
        fixture.Voiceover = ReelCompileFixture.VoiceoverJson(("vo0", 0, 7.0, "s0", "Una."), ("vo1", 7.0, 1.0, "s2", "Due."));
        fixture.Config = c => c with
        {
            EnableVoiceover = true,
            VoiceoverStepOrder = VideoCompileReelFeatureTests.VoiceoverStepOrder,
            PrePaddingMs = 0,
            PostPaddingMs = 0
        };

        StepExecutionResult result = await fixture.RunAsync(ReelCompileFixture.DecisionJson("s0", "s2", "s4"));

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        JsonObject vo0 = fixture.Edl!["voiceover"]!["lines"]!.AsArray().OfType<JsonObject>().Single(l => l["lineId"]!.GetValue<string>() == "vo0");
        vo0["windowEndSec"]!.GetValue<double>().Should().BeApproximately(2.5, 1e-9, "the next shot belongs to vo1");
        vo0["fits"]!.GetValue<bool>().Should().BeFalse();
    }
}
