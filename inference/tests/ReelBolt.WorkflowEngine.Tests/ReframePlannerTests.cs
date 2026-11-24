#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// <see cref="SourceFitPlanner"/> (per-source <c>OutputFit</c> overrides) and
/// <see cref="ReframePlanner"/> (which spans are reframed or padded inside the subject safe area,
/// the time-varying geometry, the report) — docs/video-editing.md "Keyframed reframing".
/// </summary>
public class ReframePlannerTests
{
    private static readonly VideoAnalysisMedia Landscape = new(10, 30, 1, 1920, 1080);
    private static readonly VideoAnalysisMedia Vertical = new(10, 30, 1, 1080, 1920);

    private static VideoCompileStepConfig Config(
        VideoOutputFit fit = VideoOutputFit.Crop,
        VideoOutputFormat format = VideoOutputFormat.Vertical,
        IReadOnlyDictionary<int, VideoOutputFit>? sourceFits = null,
        double? safeArea = null,
        VideoCanvasFill fill = VideoCanvasFill.Black) =>
        new(1, new ExtractInputRef(ExtractInputSource.Previous), 1,
            OutputFormat: format, OutputFit: fit, SourceFits: sourceFits, SubjectSafeArea: safeArea, CanvasFill: fill);

    private static VideoAnalysisArtifact Artifact(IReadOnlyList<VideoAnalysisShot> shots, params (int Index, VideoAnalysisMedia Media, bool Composition)[] sources) => new(
        Version: 2,
        Media: sources[0].Media,
        Shots: shots,
        SilenceSpans: [],
        Segments: [],
        Words: [],
        OfferedIds: shots.Select(s => s.Id).ToList(),
        Provenance: new VideoAnalysisProvenance(VideoTranscriptionMode.Off, false, false),
        Sources: sources.Select(s => new VideoAnalysisSourceInfo(s.Index, $"projects/p/userFiles/clip-{s.Index}.mp4", s.Media, s.Composition)).ToList());

    private static VideoAnalysisShot Shot(string id, int source, double start, double end, params string[] onScreenText) =>
        new(id, start, end, SourceIndex: source,
            Caption: onScreenText.Length == 0 ? null : new VideoShotCaption(id, "s", [], "", "", "", "Wide", "", onScreenText, [], "Unknown", "", "", "", []));

    /// <summary>A shot the vision caption read and found no on-screen text on: the text question is answered.</summary>
    private static VideoAnalysisShot ReadShot(string id, int source, double start, double end, VideoAnalysisRect? subject = null) =>
        new(id, start, end, SourceIndex: source, Subject: subject,
            Caption: new VideoShotCaption(id, "s", [], "", "", "", "Wide", "", [], [], "Unknown", "", "", "", []));

    private static VideoCompileStepExecutor.ResolvedSpan Span(int source, double start, double end) =>
        new(start, end, start, end, (long)(start * 30), (long)(end * 30), source, ["x"]);

    private static SourceFitPlan Fits(VideoCompileStepConfig config, VideoAnalysisArtifact artifact, IReadOnlyList<VideoCompileStepExecutor.ResolvedSpan> spans) =>
        SourceFitPlanner.Plan(config, artifact, spans.Select(s => (s.SourceIndex, s.SnappedStart, s.SnappedEnd)).ToList(),
            idx => artifact.Sources!.Single(s => s.SourceIndex == idx).Media, Vertical, fixedFormat: true);

    private static ReframePlan Plan(
        VideoCompileStepConfig config, VideoAnalysisArtifact artifact, IReadOnlyList<VideoCompileStepExecutor.ResolvedSpan> spans,
        IReadOnlyList<VideoReframe>? reframes = null, IReadOnlyList<ReframeClamp>? clamps = null) =>
        ReframePlanner.Build(config, reframes ?? [], clamps ?? [], artifact, spans,
            idx => artifact.Sources!.Single(s => s.SourceIndex == idx).Media, Vertical, Fits(config, artifact, spans), _ => false);

    // ---- per-source fit ----

    [Theory]
    [InlineData(VideoOutputFit.Crop, true)]
    [InlineData(VideoOutputFit.Pad, false)]
    public void Without_overrides_the_fit_is_exactly_the_configured_one(VideoOutputFit fit, bool cover)
    {
        VideoAnalysisArtifact artifact = Artifact([Shot("s0", 0, 0, 10)], (0, Landscape, false));

        SourceFitPlan plan = Fits(Config(fit), artifact, [Span(0, 0, 4)]);

        plan.CoverFit.Should().Be(cover);
        plan.CropFocus.Should().BeEmpty();
        plan.FitOf(0).Should().Be(fit);
    }

    [Fact]
    public void A_source_overridden_to_pad_is_padded_while_the_rest_are_cropped()
    {
        VideoAnalysisArtifact artifact = Artifact([Shot("s0", 0, 0, 10), Shot("s1", 1, 0, 10)], (0, Landscape, false), (1, Landscape, false));

        SourceFitPlan plan = Fits(Config(VideoOutputFit.Crop, sourceFits: new Dictionary<int, VideoOutputFit> { [1] = VideoOutputFit.Pad }),
            artifact, [Span(0, 0, 4), Span(1, 0, 4)]);

        plan.CoverFit.Should().BeTrue();
        plan.CropFocus.Should().NotContainKey(0);
        plan.CropFocus[1].Pad.Should().BeTrue();
        plan.FitOf(1).Should().Be(VideoOutputFit.Pad);
    }

    [Fact]
    public void A_source_overridden_to_crop_in_a_padded_edit_turns_cover_on_for_it_alone()
    {
        VideoAnalysisArtifact artifact = Artifact([Shot("s0", 0, 0, 10), Shot("s1", 1, 0, 10)], (0, Landscape, false), (1, Landscape, false));

        SourceFitPlan plan = Fits(Config(VideoOutputFit.Pad, sourceFits: new Dictionary<int, VideoOutputFit> { [0] = VideoOutputFit.Crop }),
            artifact, [Span(0, 0, 4), Span(1, 0, 4)]);

        plan.CoverFit.Should().BeTrue();
        plan.CropFocus.Should().NotContainKey(0);
        plan.CropFocus[1].Pad.Should().BeTrue("source 1 keeps the edit's own Pad");
    }

    [Fact]
    public void Source_fits_are_ignored_on_a_source_sized_canvas()
    {
        VideoAnalysisArtifact artifact = Artifact([Shot("s0", 0, 0, 10)], (0, Landscape, false));

        SourceFitPlan plan = Fits(Config(VideoOutputFit.Pad, VideoOutputFormat.Source, new Dictionary<int, VideoOutputFit> { [0] = VideoOutputFit.Crop }),
            artifact, [Span(0, 0, 4)]);

        plan.CoverFit.Should().BeFalse();
    }

    [Fact]
    public void A_source_fit_for_a_clip_that_does_not_exist_is_a_config_error()
    {
        VideoAnalysisArtifact artifact = Artifact([Shot("s0", 0, 0, 10)], (0, Landscape, false));

        SourceFitPlan plan = Fits(Config(sourceFits: new Dictionary<int, VideoOutputFit> { [3] = VideoOutputFit.Pad }), artifact, [Span(0, 0, 4)]);

        plan.ErrorCode.Should().Be("SOURCE_FIT_UNKNOWN_SOURCE");
    }

    // ---- subject safe area ----

    [Fact]
    public void Under_subject_a_span_over_a_shot_with_on_screen_text_is_padded_inside_the_safe_area()
    {
        VideoAnalysisArtifact artifact = Artifact(
            [ReadShot("s0", 0, 0, 4), Shot("s1", 0, 4, 8, "Launch day", "  ")],
            (0, Landscape, false));
        List<VideoCompileStepExecutor.ResolvedSpan> spans = [Span(0, 0, 4), Span(0, 4, 8)];

        ReframePlan plan = Plan(Config(VideoOutputFit.Subject), artifact, spans);

        plan.SafePads(spans[0]).Should().BeFalse();
        plan.SafePads(spans[1]).Should().BeTrue();
        plan.SafeMargin.Should().Be(0.05);
        plan.RequiresSegmented.Should().BeTrue();
        JsonObject shot = plan.ToNode()!["safeAreaShots"]!.AsArray().Single()!.AsObject();
        shot["shot"]!.GetValue<string>().Should().Be("s1");
        shot["fit"]!.GetValue<string>().Should().Be("Pad");
        shot["reason"]!.GetValue<string>().Should().Be("on_screen_text");
    }

    [Fact]
    public void Every_span_of_a_designed_composition_is_text_bearing()
    {
        VideoAnalysisArtifact artifact = Artifact([Shot("s0", 0, 0, 10)], (0, Landscape, true));
        List<VideoCompileStepExecutor.ResolvedSpan> spans = [Span(0, 0, 4)];

        ReframePlan plan = Plan(Config(VideoOutputFit.Subject), artifact, spans);

        plan.SafePads(spans[0]).Should().BeTrue();
        plan.ToNode()!["safeAreaShots"]![0]!["reason"]!.GetValue<string>().Should().Be("composition");
    }

    [Fact]
    public void The_guard_is_off_at_zero_for_crop_fits_and_for_a_clip_already_the_canvas_shape()
    {
        VideoAnalysisArtifact text = Artifact([Shot("s0", 0, 0, 10, "Title")], (0, Landscape, false));
        VideoAnalysisArtifact sameShape = Artifact([Shot("s0", 0, 0, 10, "Title")], (0, Vertical, false));
        List<VideoCompileStepExecutor.ResolvedSpan> spans = [Span(0, 0, 4)];

        Plan(Config(VideoOutputFit.Subject, safeArea: 0), text, spans).SafePads(spans[0]).Should().BeFalse();
        Plan(Config(VideoOutputFit.Crop), text, spans).SafePads(spans[0]).Should().BeFalse();
        Plan(Config(VideoOutputFit.Subject), sameShape, spans).SafePads(spans[0]).Should().BeFalse("nothing is cropped");
        Plan(Config(VideoOutputFit.Subject), Artifact([ReadShot("s0", 0, 0, 10)], (0, Landscape, false)), spans)
            .ToNode().Should().BeNull("a caption that read no text answers the question: the shot is cropped, output byte-identical");
    }

    [Fact]
    public void A_shot_whose_text_is_unknown_and_has_no_located_subject_is_padded_not_cropped()
    {
        // No caption (never captioned) and no Subject box (no SubjectLabel / LocateSubjects): a blind centre crop could cut text.
        VideoAnalysisArtifact artifact = Artifact([Shot("s0", 0, 0, 10)], (0, Landscape, false));
        List<VideoCompileStepExecutor.ResolvedSpan> spans = [Span(0, 0, 4)];

        ReframePlan plan = Plan(Config(VideoOutputFit.Subject), artifact, spans);

        plan.SafePads(spans[0]).Should().BeTrue();
        plan.ToNode()!["safeAreaShots"]![0]!["reason"]!.GetValue<string>().Should().Be("text_unknown");
    }

    [Fact]
    public void A_shot_with_unknown_text_but_a_located_subject_is_still_cropped_toward_it()
    {
        VideoAnalysisArtifact artifact = Artifact(
            [new VideoAnalysisShot("s0", 0, 10, SourceIndex: 0, Subject: new VideoAnalysisRect(0.4, 0.4, 0.2, 0.2))],
            (0, Landscape, false));
        List<VideoCompileStepExecutor.ResolvedSpan> spans = [Span(0, 0, 4)];

        Plan(Config(VideoOutputFit.Subject), artifact, spans).SafePads(spans[0]).Should().BeFalse();
    }

    [Theory]
    [InlineData(ReframeAnchor.Visible)]
    [InlineData(ReframeAnchor.Window)]
    public void A_reframed_span_is_never_padded_by_the_safe_area(ReframeAnchor anchor)
    {
        // Production run 180c2c62: a followed 16:9 clip into 9:16 under OutputFit=Subject came out as the
        // whole frame letterboxed, because its uncaptioned shot read as text_unknown and the pad won over
        // the follow. A reframe (followSubject's or hand-written) owns the framing of the spans it covers.
        VideoAnalysisArtifact artifact = Artifact(
            [Shot("s0", 0, 0, 4), Shot("s1", 0, 4, 8, "Launch day")],
            (0, Landscape, false));
        List<VideoCompileStepExecutor.ResolvedSpan> spans = [Span(0, 0, 4), Span(0, 4, 8)];
        VideoReframe reframe = new(0, 0, null, [new ReframeKeyframe(0, 1, 0.3, 0.5)]) { Anchor = anchor };

        ReframePlan plan = Plan(Config(VideoOutputFit.Subject), artifact, spans, [reframe]);

        plan.SafePads(spans[0]).Should().BeFalse("text_unknown, but the reframe owns this span");
        plan.SafePads(spans[1]).Should().BeFalse("on_screen_text, but the reframe owns this span");
        JsonObject node = plan.ToNode()!;
        node.ContainsKey("safeAreaShots").Should().BeFalse();
        JsonArray yielded = node["safeAreaReframed"]!.AsArray();
        yielded.Select(n => n!["shot"]!.GetValue<string>()).Should().Equal("s0", "s1");
        yielded[0]!["reason"]!.GetValue<string>().Should().Be("text_unknown");
        yielded[1]!["reason"]!.GetValue<string>().Should().Be("on_screen_text");
        var cover = VideoCompileStepExecutor.SourceCanvasFit.For(Landscape, Vertical, cover: true);
        plan.TimelineFor(0)!.Apply(cover, 2.0).Width.Should().BeGreaterThan(1080, "the clip stays cover-fitted, not padded into the safe box");
    }

    [Fact]
    public void Only_the_spans_a_reframe_covers_yield_the_safe_area()
    {
        VideoAnalysisArtifact artifact = Artifact([Shot("s0", 0, 0, 10, "Title")], (0, Landscape, false));
        List<VideoCompileStepExecutor.ResolvedSpan> spans = [Span(0, 0, 3), Span(0, 6, 9)];
        VideoReframe reframe = new(0, 0, 4, [new ReframeKeyframe(0, 2, 0.5, 0.5)]);

        ReframePlan plan = Plan(Config(VideoOutputFit.Subject), artifact, spans, [reframe]);

        plan.SafePads(spans[0]).Should().BeFalse();
        plan.SafePads(spans[1]).Should().BeTrue("no reframe covers 6..9 s");
        plan.ToNode()!["safeAreaShots"]![0]!["spans"]!.AsArray().Select(n => n!.GetValue<int>()).Should().Equal(1);
        plan.ToNode()!["safeAreaReframed"]![0]!["spans"]!.AsArray().Select(n => n!.GetValue<int>()).Should().Equal(0);
    }

    [Fact]
    public void A_subject_no_single_crop_keeps_is_still_cropped_when_a_reframe_owns_the_source()
    {
        // A subject far wider than the 9:16 window: no single crop keeps it, so Subject pads the clip, unless a
        // reframe (a follow) owns the source and moves the crop with it.
        VideoAnalysisArtifact artifact = Artifact(
            [new VideoAnalysisShot("s0", 0, 10, SourceIndex: 0, Subject: new VideoAnalysisRect(0.0, 0.3, 0.95, 0.3))],
            (0, Landscape, false));
        List<(int, double, double)> windows = [(0, 0.0, 10.0)];

        SourceFitPlan alone = SourceFitPlanner.Plan(Config(VideoOutputFit.Subject), artifact, windows, _ => Landscape, Vertical, fixedFormat: true);
        SourceFitPlan owned = SourceFitPlanner.Plan(Config(VideoOutputFit.Subject), artifact, windows, _ => Landscape, Vertical, fixedFormat: true,
            reframedSources: new HashSet<int> { 0 });

        alone.CropFocus[0].Pad.Should().BeTrue();
        owned.CropFocus[0].Pad.Should().BeFalse();
        // An explicit Pad is the customer's own choice and still pads.
        SourceFitPlanner.Plan(Config(VideoOutputFit.Crop, sourceFits: new Dictionary<int, VideoOutputFit> { [0] = VideoOutputFit.Pad }),
            artifact, windows, _ => Landscape, Vertical, fixedFormat: true, reframedSources: new HashSet<int> { 0 })
            .FitOf(0).Should().Be(VideoOutputFit.Pad);
    }

    [Fact]
    public void A_safe_padded_span_maps_geometry_through_the_inset_pad()
    {
        VideoAnalysisArtifact artifact = Artifact([Shot("s0", 0, 0, 10, "Title")], (0, Landscape, false));
        List<VideoCompileStepExecutor.ResolvedSpan> spans = [Span(0, 2, 4)];
        ReframePlan plan = Plan(Config(VideoOutputFit.Subject), artifact, spans);
        var cover = VideoCompileStepExecutor.SourceCanvasFit.For(Landscape, Vertical, cover: true);

        VideoCompileStepExecutor.SourceCanvasFit inside = plan.TimelineFor(0)!.Apply(cover, 3.0);
        VideoCompileStepExecutor.SourceCanvasFit outside = plan.TimelineFor(0)!.Apply(cover, 5.0);

        // 1920x1080 into the 972x1728 safe box: 972 wide, centred on 1080x1920.
        inside.Width.Should().BeApproximately(972, 1e-9);
        inside.OffsetX.Should().BeApproximately(54, 1e-9);
        inside.OffsetY.Should().BeApproximately((1920 - 546.75) / 2, 1e-9);
        outside.Should().Be(cover, "outside the padded span the clip keeps its crop");
    }

    // ---- reframes ----

    [Fact]
    public void A_reframe_is_reported_with_the_spans_it_applies_to_and_its_clamps()
    {
        VideoAnalysisArtifact artifact = Artifact([Shot("s0", 0, 0, 10), Shot("s1", 1, 0, 10)], (0, Landscape, false), (1, Landscape, false));
        List<VideoCompileStepExecutor.ResolvedSpan> spans = [Span(0, 0, 2), Span(1, 0, 3), Span(0, 5, 7)];
        VideoReframe applied = new(0, 1, 6, [new ReframeKeyframe(0, 2, 0.25)]);
        VideoReframe unused = new(1, 5, null, [new ReframeKeyframe(0, 2)]);

        ReframePlan plan = Plan(Config(), artifact, spans, [applied, unused], [new ReframeClamp(0, 0, "centerX", 0.1, 0.25)]);

        plan.RequiresSegmented.Should().BeTrue();
        plan.StageFor(spans[0], 30, 1).Should().StartWith("fps=30/1,zoompan=");
        plan.StageFor(spans[1], 30, 1).Should().BeNull();
        plan.StageFor(spans[2], 30, 1).Should().StartWith("fps=30/1,zoompan=");
        JsonArray reframes = plan.ToNode()!["reframes"]!.AsArray();
        reframes[0]!["applied"]!.GetValue<bool>().Should().BeTrue();
        reframes[0]!["spans"]!.AsArray().Select(n => n!.GetValue<int>()).Should().Equal(0, 2);
        reframes[0]!["clamped"]![0]!["axis"]!.GetValue<string>().Should().Be("centerX");
        reframes[1]!["applied"]!.GetValue<bool>().Should().BeFalse();
        reframes[1]!["reason"]!.GetValue<string>().Should().Be("not_in_edit");
    }

    [Fact]
    public void A_reframe_of_a_clip_that_is_not_in_the_analysis_is_a_config_error()
    {
        VideoAnalysisArtifact artifact = Artifact([Shot("s0", 0, 0, 10)], (0, Landscape, false));

        Plan(Config(), artifact, [Span(0, 0, 2)], [new VideoReframe(2, null, null, [new ReframeKeyframe(0, 2)])])
            .ErrorCode.Should().Be("REFRAME_UNKNOWN_SOURCE");
    }

    [Fact]
    public void Nothing_configured_is_a_plan_that_changes_nothing()
    {
        VideoAnalysisArtifact artifact = Artifact([Shot("s0", 0, 0, 10)], (0, Landscape, false));
        List<VideoCompileStepExecutor.ResolvedSpan> spans = [Span(0, 0, 2)];

        ReframePlan plan = Plan(Config(), artifact, spans);

        plan.RequiresSegmented.Should().BeFalse();
        plan.TimelineFor(0).Should().BeNull();
        plan.ToNode().Should().BeNull();
    }

    [Fact]
    public void Geometry_under_a_reframe_lands_on_the_window_the_filter_crops_at_every_sampled_time()
    {
        // Property: for random reframes and sample times, the fit the geometry uses maps the
        // filter's crop window edges exactly onto the edges of the fitted picture.
        var random = new Random(104);
        VideoAnalysisArtifact artifact = Artifact([Shot("s0", 0, 0, 10)], (0, Landscape, false));
        var cover = VideoCompileStepExecutor.SourceCanvasFit.For(Landscape, Vertical, cover: true);
        for (int trial = 0; trial < 200; trial++)
        {
            var keys = new List<ReframeKeyframe>();
            double at = random.NextDouble();
            for (int k = 0; k < 1 + random.Next(5); k++)
            {
                double zoom = 1 + random.NextDouble() * 7;
                keys.Add(new ReframeKeyframe(at, zoom, random.NextDouble(), random.NextDouble(), (ReframeEasing)random.Next(5)));
                at += 0.2 + random.NextDouble();
            }

            VideoReframeValidation v = VideoReframe.Validate([new VideoReframe(0, 0.5, null, keys)]);
            ReframePlan plan = Plan(Config(), artifact, [Span(0, 0, 10)], v.Normalized, v.Clamps);
            double t = random.NextDouble() * 10;

            ReframePixelWindow px = ReframeGeometry.PixelWindow(ReframeGeometry.WindowAt(v.Normalized, t), 1920, 1080);
            VideoCompileStepExecutor.SourceCanvasFit fit = plan.TimelineFor(0)!.Apply(cover, t);

            (fit.OffsetX + px.X / 1920.0 * fit.Width).Should().BeApproximately(cover.OffsetX, 1e-6);
            (fit.OffsetX + (px.X + px.Width) / 1920.0 * fit.Width).Should().BeApproximately(cover.OffsetX + cover.Width, 1e-6);
            (fit.OffsetY + px.Y / 1080.0 * fit.Height).Should().BeApproximately(cover.OffsetY, 1e-6);
            (fit.OffsetY + (px.Y + px.Height) / 1080.0 * fit.Height).Should().BeApproximately(cover.OffsetY + cover.Height, 1e-6);
        }
    }
}
