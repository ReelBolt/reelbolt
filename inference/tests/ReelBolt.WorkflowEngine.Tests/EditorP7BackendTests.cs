#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Editing;
using ReelBolt.Shared.Editing.Color;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Services.Video;
using ReelBolt.WorkflowEngine.Services.Video.TimelineRender;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Editor v2, P7 (colour + compose) — the server half: the validator's new ranges and catalogues,
/// the grade/effects/mask/keyer/reframe/corner-pin builders, adjustment clips, title templates,
/// stabilisation pre-passes, optical-flow retime, the LUT parser/rewriter, and the recipe hash.
/// </summary>
public class EditorP7BackendTests
{
    private static readonly Guid ProjectId = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly AssetRef AssetA = new(AssetRefKind.ProjectFile, Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"), null);
    private static readonly AssetRef LutRef = new(AssetRefKind.ProjectFile, Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc"), null);
    private static readonly EditTimelineSettings Settings = new(320, 180, new Rational(30, 1), 48000, new Rational(1, 1), AspectPolicy.Fit, 0);

    private static MediaClip Clip(string id, long recordIn, long recordOut, GradeStack? grade = null, ShapeMask? mask = null, ChromaKeyer? keyer = null,
        IReadOnlyList<ClipEffect>? effects = null, bool stabilize = false, ReframeTarget? reframe = null, string retimeQuality = "frame", QuadCorners? pin = null, double rate = 1.0) =>
        new(Guid.Parse($"{id}{id}{id}{id}{id}{id}{id}{id}-{id}{id}{id}{id}-4{id}{id}{id}-8{id}{id}{id}-{id}{id}{id}{id}{id}{id}{id}{id}{id}{id}{id}{id}"), $"clip-{id}", recordIn, recordOut, AssetA, 0, rate, true, null,
            null, null, null, false, false, 0, 0, grade, mask, keyer, effects, stabilize, reframe, retimeQuality, pin);

    private static Track VideoTrack(string id, params Item[] items) =>
        new(Guid.Parse($"{id}{id}{id}{id}{id}{id}{id}{id}-0000-4000-8000-000000000000"), TrackKind.Video, $"V{id}", null, 64, false, true, false, false, null, items);

    private static EditTimeline Doc(IReadOnlyList<ColorGroup>? groups = null, params Track[] tracks) =>
        new(Guid.NewGuid(), ProjectId, "doc", Settings, tracks, [], null, null, groups);

    private static readonly Dictionary<AssetRef, LocalAsset> Assets = new()
    {
        [AssetA] = new LocalAsset("/tmp/a.mp4", "a|100", new MediaProbeResult(10, 30, 1, 320, 180, "h264", "aac", 48000)),
        [LutRef] = new LocalAsset("/tmp/look.cube", "lut|7", new MediaProbeResult(0, 0, 1, 0, 0, null, null, null)),
    };

    private static ResolvedRenderTarget Target() => TimelineRenderPresets.Resolve(new TimelineRenderStepConfig(Guid.NewGuid(), Preset: TimelineRenderPreset.Master), Settings);

    private static RegionFilterGraphBuilder.RegionArgv Build(EditTimeline doc, int regionIndex = 0) =>
        RegionFilterGraphBuilder.Build(SegmentPlanner.Plan(doc, null).Regions[regionIndex], Target(), Assets, "/scratch/r.mp4", "/fonts/f.ttf", n => $"/scratch/t-{n}.txt", 2,
            lutFilePathFor: n => $"/scratch/lut-{n}.cube", stabilizePathFor: n => $"/scratch/stab-{n}.trf");

    private static string Graph(RegionFilterGraphBuilder.RegionArgv argv) => argv.Args[argv.Args.ToList().IndexOf("-filter_complex") + 1];

    private static GradeStack Stack(params GradeNode[] nodes) => new(true, nodes);

    private const string P4Layer = "[0:v]trim=duration=1,setpts=PTS-STARTPTS,fps=30/1,scale=320:180:force_original_aspect_ratio=decrease,pad=320:180:(ow-iw)/2:(oh-ih)/2,setsar=1,format=yuv420p";

    // ── Renderer ─────────────────────────────────────────────────────────────

    [Fact]
    public void A_clip_whose_grade_is_bypassed_or_at_identity_keeps_the_P4_bytes()
    {
        GradeStack identity = Stack(new GradeNode(Guid.NewGuid(), Primaries: new GradePrimaries()));
        GradeStack bypassed = new(false, [new GradeNode(Guid.NewGuid(), Primaries: new GradePrimaries(Saturation: 2))]);
        foreach (GradeStack stack in new[] { identity, bypassed })
        {
            string graph = Graph(Build(Doc(null, VideoTrack("1", Clip("1", 0, 30, grade: stack)))));
            graph.Should().StartWith("color=c=black:s=320x180:r=30/1:d=1[base];" + P4Layer + "[l0];[base][l0]overlay=eof_action=pass[v0]");
            RegionFilterGraphBuilder.HasPictureDepth(Clip("1", 0, 30, grade: stack)).Should().BeFalse();
        }
    }

    [Fact]
    public void Primaries_map_to_one_lutrgb_plus_colorbalance_temperature_and_hue_in_that_order()
    {
        GradePrimaries p = new(
            Lift: new GradeWheel(R: 0.1, Master: 0.05),
            Gain: new GradeWheel(B: -0.2, Master: 0.1),
            Offset: new GradeWheel(G: 0.02),
            Contrast: 1.2, Pivot: 0.45, Saturation: 1.3, Hue: 10, Temperature: 0.4, Tint: -0.2, Exposure: 0.5);
        string graph = Graph(Build(Doc(null, VideoTrack("1", Clip("1", 0, 30, grade: Stack(new GradeNode(Guid.NewGuid(), Primaries: p)))))));
        graph.Should().Contain(P4Layer + ",lutrgb=r='clip(((((val/255*1.4142135623730951+0)*1.1+0.05*(1-(val/255*1.4142135623730951+0)))-0.45)*1.2+0.45),0,1)*255':g='clip(((((val/255*1.4142135623730951+0.02)*1.1+0.05*(1-(val/255*1.4142135623730951+0.02)))-0.45)*1.2+0.45),0,1)*255':b='clip(((((val/255*1.4142135623730951+0)*1.1+0.05*(1-(val/255*1.4142135623730951+0)))-0.45)*1.2+0.45),0,1)*255'");
        graph.Should().Contain(",colorbalance=rs=0.1:bh=-0.2:gm=0.1,colortemperature=temperature=5500:mix=1,hue=h=10:s=1.3[l0]");
        graph.Should().Contain("[base][l0]overlay=eof_action=pass[v0]", "a graded clip without a transform keeps the P4 overlay");
    }

    [Fact]
    public void Curves_and_a_LUT_follow_the_primaries_and_the_LUT_is_requested_for_the_renderer_to_write()
    {
        GradeNode node = new(Guid.NewGuid(),
            Curves: new GradeCurves(Y: [new CurvePoint(0.25, 0.2), new CurvePoint(0.75, 0.8)], R: [new CurvePoint(0, 0.1), new CurvePoint(1, 1)]),
            Lut: new GradeLut(LutRef, 0.5));
        RegionFilterGraphBuilder.RegionArgv argv = Build(Doc(null, VideoTrack("1", Clip("1", 0, 30, grade: Stack(node)))));
        Graph(argv).Should().Contain(",curves=m='0/0 0.25/0.2 0.75/0.8 1/1':r='0/0.1 1/1',lut3d=file='/scratch/lut-0.cube':interp=tetrahedral[l0]");
        argv.LutFiles.Should().ContainSingle().Which.Should().Be(new GradeFilterBuilder.LutRequest(node.Lut!, "/scratch/lut-0.cube"));
    }

    [Fact]
    public void A_colour_group_grade_is_applied_before_the_clips_own_and_only_to_its_members()
    {
        MediaClip member = Clip("1", 0, 30, grade: Stack(new GradeNode(Guid.NewGuid(), Primaries: new GradePrimaries(Saturation: 0.5))));
        MediaClip other = Clip("2", 30, 60);
        ColorGroup group = new(Guid.NewGuid(), "Interview", [member.Id], Stack(new GradeNode(Guid.NewGuid(), Primaries: new GradePrimaries(Temperature: -0.2))));
        EditTimeline doc = Doc([group], VideoTrack("1", member, other));
        RenderPlan plan = SegmentPlanner.Plan(doc, null);
        plan.Regions[0].Layers[0].GroupGrade.Should().BeSameAs(group.Grade);
        plan.Regions[1].Layers[0].GroupGrade.Should().BeNull();
        Graph(Build(doc, 0)).Should().Contain(",colortemperature=temperature=7000:mix=1,hue=s=0.5[l0]");
        Graph(Build(doc, 1)).Should().Contain(P4Layer + "[l0]");
        // The hash sees the group grade: the member's recipe differs from the same clip ungrouped.
        string grouped = RegionRecipeHasher.Hash(plan.Regions[0], Target(), Assets, "font");
        string ungrouped = RegionRecipeHasher.Hash(SegmentPlanner.Plan(Doc(null, VideoTrack("1", member, other)), null).Regions[0], Target(), Assets, "font");
        grouped.Should().NotBe(ungrouped);
    }

    [Fact]
    public void Keyer_mask_effects_and_corner_pin_build_in_order_and_keep_alpha_to_the_overlay()
    {
        MediaClip clip = Clip("1", 0, 30,
            keyer: new ChromaKeyer("#00FF00", 0.25, 0.1, 0.6),
            mask: new ShapeMask("ellipse", 0.5, 0.5, 0.6, 0.8, 0.05, Invert: false),
            effects: [new ClipEffect("blur", 0.5), new ClipEffect("grain", 0.25), new ClipEffect("mono", 1), new ClipEffect("sharpen", 0, true)],
            pin: new QuadCorners(new QuadPoint(0.1, 0.1), new QuadPoint(0.9, 0.2), new QuadPoint(0.8, 0.9), new QuadPoint(0.2, 0.8)));
        string graph = Graph(Build(Doc(null, VideoTrack("1", clip))));
        graph.Should().Contain("fps=30/1,format=rgba,colorkey=color=0x00FF00:similarity=0.25:blend=0.1,despill=type=green:mix=0.6,scale=320:180:force_original_aspect_ratio=decrease,format=rgba,pad=320:180:(ow-iw)/2:(oh-ih)/2:color=0x00000000,setsar=1,gblur=sigma=10,noise=alls=10:allf=t+u,hue=s=0,");
        graph.Should().Contain("format=gbrap,geq=r='r(X,Y)':g='g(X,Y)':b='b(X,Y)':a='alpha(X,Y)*clip((1-sqrt(pow((X/W-0.5)/0.3,2)+pow((Y/H-0.5)/0.4,2)))/0.05+0.5,0,1)',format=rgba,");
        graph.Should().Contain("pad=320:180:(ow-iw)/2:(oh-ih)/2:color=0x00000000,format=rgba,drawbox=x=0:y=0:w=iw:h=ih:t=1:color=black@0:replace=1,perspective=x0=32:y0=18:x1=288:y1=36:x2=64:y2=144:x3=256:y3=162:sense=destination,format=rgba[l0]");
        graph.Should().NotContain(",format=yuv420p,", "the closing yuv420p would drop the alpha the overlay needs");
        graph.Should().Contain("[base][l0]overlay=eof_action=pass[v0]");
    }

    [Fact]
    public void Reframe_crops_the_source_to_the_canvas_aspect_around_the_subject_before_the_fit()
    {
        string graph = Graph(Build(Doc(null, VideoTrack("1", Clip("1", 0, 30, reframe: new ReframeTarget(0.7, 0.4))))));
        graph.Should().Contain("fps=30/1,crop=w='min(iw,ih*1.777778)':h='min(ih,iw/1.777778)':x='clip(iw*0.7-ow/2,0,iw-ow)':y='clip(ih*0.4-oh/2,0,ih-oh)',scale=320:180:");
    }

    [Fact]
    public void Stabilise_adds_a_detection_pre_pass_and_the_transform_filter_reading_its_result()
    {
        RegionFilterGraphBuilder.RegionArgv argv = Build(Doc(null, VideoTrack("1", Clip("1", 0, 30, stabilize: true))));
        RegionFilterGraphBuilder.PrePass pass = argv.PrePasses.Should().ContainSingle().Subject;
        pass.ResultPath.Should().Be("/scratch/stab-0.trf");
        pass.Args.Should().ContainInOrder("-i", "/tmp/a.mp4", "-vf", "trim=duration=1,setpts=PTS-STARTPTS,fps=30/1,vidstabdetect=shakiness=5:accuracy=15:result='/scratch/stab-0.trf'", "-f", "null", "-");
        Graph(argv).Should().Contain("fps=30/1,vidstabtransform=input='/scratch/stab-0.trf':smoothing=10:optzoom=1:interpol=bilinear,scale=320:180:");
    }

    [Fact]
    public void Optical_flow_retime_interpolates_only_a_slowed_clip()
    {
        Graph(Build(Doc(null, VideoTrack("1", Clip("1", 0, 60, retimeQuality: "opticalFlow", rate: 0.5))))).Should().Contain("setpts=PTS/0.5,minterpolate=fps=30/1:mi_mode=mci:mc_mode=aobmc:vsbmc=1,fps=30/1");
        Graph(Build(Doc(null, VideoTrack("1", Clip("1", 0, 15, retimeQuality: "opticalFlow", rate: 2))))).Should().Contain("setpts=PTS/2,fps=30/1").And.NotContain("minterpolate");
        RegionFilterGraphBuilder.HasPictureDepth(Clip("1", 0, 15, retimeQuality: "opticalFlow", rate: 2)).Should().BeFalse();
    }

    [Fact]
    public void An_adjustment_clip_grades_the_composite_below_it_and_a_disabled_one_is_inert()
    {
        AdjustmentClip adjustment = new(Guid.NewGuid(), "adj", 0, 30, null, Stack(new GradeNode(Guid.NewGuid(), Primaries: new GradePrimaries(Saturation: 0))), [new ClipEffect("vignette", 0.5)]);
        EditTimeline doc = Doc(null, VideoTrack("1", Clip("1", 0, 30)), VideoTrack("2", adjustment));
        RenderPlan plan = SegmentPlanner.Plan(doc, null);
        plan.SkippedItems.Should().BeEmpty();
        string graph = Graph(Build(doc));
        graph.Should().Contain("[base][l0]overlay=eof_action=pass[v0];[v0]hue=s=0,vignette=a=0.6283,format=yuv420p[v1];[v1]format=yuv420p[vout]");

        AdjustmentClip off = new(Guid.NewGuid(), "adj", 0, 30, null, adjustment.Grade, adjustment.Effects, Enabled: false);
        Graph(Build(Doc(null, VideoTrack("1", Clip("1", 0, 30)), VideoTrack("2", off)))).Should().EndWith("[base][l0]overlay=eof_action=pass[v0];[v0]format=yuv420p[vout]");
    }

    [Fact]
    public void Title_templates_change_decoration_and_position_and_plain_keeps_the_P4_drawtext()
    {
        TitleClip plain = new(Guid.NewGuid(), "t", 0, 30, "Hello");
        TitleClip lower = new(Guid.NewGuid(), "t", 0, 30, "Hello", Template: "lowerThird");
        TitleClip card = new(Guid.NewGuid(), "t", 0, 30, "Hello", Template: "card");
        Graph(Build(Doc(null, VideoTrack("1", plain)))).Should().Contain(":fontsize=48:fontcolor=#FFFFFF:borderw=2:bordercolor=black@0.6:x=(w-text_w)/2:y=(h-text_h)/2[t0]");
        Graph(Build(Doc(null, VideoTrack("1", lower)))).Should().Contain(":fontsize=48:fontcolor=#FFFFFF:box=1:boxcolor=black@0.55:boxborderw=8:x=w/12:y=h-text_h-h/8[t0]");
        Graph(Build(Doc(null, VideoTrack("1", card)))).Should().Contain(":box=1:boxcolor=black@0.8:boxborderw=24:x=(w-text_w)/2:y=(h-text_h)/2[t0]");
        // The template is part of the recipe.
        RegionRecipeHasher.Hash(SegmentPlanner.Plan(Doc(null, VideoTrack("1", lower)), null).Regions[0], Target(), Assets, "f")
            .Should().NotBe(RegionRecipeHasher.Hash(SegmentPlanner.Plan(Doc(null, VideoTrack("1", plain)), null).Regions[0], Target(), Assets, "f"));
    }

    [Fact]
    public void The_recipe_hash_covers_every_P7_field_and_is_v4()
    {
        RegionRecipeHasher.RendererVersion.Should().Be("timeline-render-region-v7");
        string Hash(MediaClip clip) => RegionRecipeHasher.Hash(SegmentPlanner.Plan(Doc(null, VideoTrack("1", clip)), null).Regions[0], Target(), Assets, "f");
        string baseline = Hash(Clip("1", 0, 30));
        MediaClip[] variants =
        [
            Clip("1", 0, 30, grade: Stack(new GradeNode(Guid.NewGuid(), Primaries: new GradePrimaries(Exposure: 0.1)))),
            Clip("1", 0, 30, grade: Stack(new GradeNode(Guid.NewGuid(), Lut: new GradeLut(LutRef, 0.3)))),
            Clip("1", 0, 30, mask: new ShapeMask()),
            Clip("1", 0, 30, keyer: new ChromaKeyer()),
            Clip("1", 0, 30, effects: [new ClipEffect("blur", 0.2)]),
            Clip("1", 0, 30, stabilize: true),
            Clip("1", 0, 30, reframe: new ReframeTarget()),
            Clip("1", 0, 30, pin: new QuadCorners(new QuadPoint(0, 0), new QuadPoint(1, 0), new QuadPoint(1, 1), new QuadPoint(0, 1))),
        ];
        string[] hashes = variants.Select(Hash).ToArray();
        hashes.Should().OnlyHaveUniqueItems();
        hashes.Should().NotContain(baseline);
        // A LUT at a different intensity, or a different file, is a different recipe.
        Hash(Clip("1", 0, 30, grade: Stack(new GradeNode(Guid.NewGuid(), Lut: new GradeLut(LutRef, 0.6))))).Should().NotBe(hashes[1]);
    }

    // ── CubeLut ──────────────────────────────────────────────────────────────

    [Fact]
    public void CubeLut_parses_clamps_rewrites_and_blends_toward_identity()
    {
        const string text = "TITLE \"Punchy\"\nLUT_3D_SIZE 2\nDOMAIN_MIN 0 0 0\nDOMAIN_MAX 1 1 1\n0 0 0\n1.2 0 0\n0 1 0\n1 1 0\n0 0 1\n1 0 1\n0 1 1\n1 1 -0.5\n";
        CubeLut lut = CubeLut.Parse(text);
        lut.Size.Should().Be(2);
        lut.Title.Should().Be("Punchy");
        lut.ToCubeText().Should().Be("TITLE \"ReelBolt\"\nLUT_3D_SIZE 2\nDOMAIN_MIN 0 0 0\nDOMAIN_MAX 1 1 1\n0 0 0\n1 0 0\n0 1 0\n1 1 0\n0 0 1\n1 0 1\n0 1 1\n1 1 0\n", "values are clamped into the domain and re-serialised");
        // White maps to yellow (blue clamped to 0); at half intensity it maps halfway.
        lut.Sample(1, 1, 1).Should().Be((1f, 1f, 0f));
        CubeLut half = lut.WithIntensity(0.5);
        half.Sample(1, 1, 1).B.Should().BeApproximately(0.5f, 1e-6f);
        half.Sample(0, 0, 0).Should().Be((0f, 0f, 0f));
        lut.WithIntensity(1).Should().BeSameAs(lut);
    }

    [Theory]
    [InlineData("LUT_3D_SIZE 2\n0 0 0\n", "wrong_row_count")]
    [InlineData("LUT_3D_SIZE 1\n0 0 0\n", "size_out_of_range")]
    [InlineData("LUT_3D_SIZE 66\n", "size_out_of_range")]
    [InlineData("LUT_1D_SIZE 4\n0 0 0\n", "wrong_row_count")]
    [InlineData("LUT_1D_SIZE 65537\n", "size_out_of_range")]
    [InlineData("0 0 0\n1 1 1\n", "size_missing")]
    [InlineData("LUT_3D_SIZE 2\n0 0 0\n1 0 0\n0 1 0\n1 1 0\n0 0 1\n1 0 1\n0 1 1\nnan 1 1\n", "not_finite")]
    [InlineData("LUT_3D_SIZE 2\n0 0 0\n1 0 0\n0 1 0\n1 1 0\n0 0 1\n1 0 1\n0 1 1\n1 1 1 1\n", "malformed")]
    [InlineData("LUT_1D_SIZE 2\nLUT_1D_INPUT_RANGE 1 0\n0 0 0\n1 1 1\n", "bad_domain")]
    [InlineData("LUT_1D_SIZE 2\nLUT_1D_SIZE_X 1\n0 0 0\n1 1 1\n", "unsupported_keyword")]
    [InlineData("LUT_1D_SIZE 2\nLUT_3D_SIZE 2\nDOMAIN_MIN 0 0 0\n0 0 0\n1 1 1\n", "unsupported_keyword")]
    public void CubeLut_refuses_hostile_or_malformed_files(string text, string code)
    {
        Action act = () => CubeLut.Parse(text);
        act.Should().Throw<CubeLutException>().Which.Code.Should().Be(code);
    }

    [Fact]
    public void CubeLut_unknown_keyword_is_bounded_before_it_is_echoed()
    {
        string head = new string('X', 500) + "<script>";
        Action act = () => CubeLut.Parse($"{head} 1\nLUT_3D_SIZE 2\n");
        CubeLutException ex = act.Should().Throw<CubeLutException>().Which;
        ex.Code.Should().Be("unsupported_keyword");
        ex.Keyword.Should().Be(new string('X', 40));
        ex.Message.Should().NotContain("<script>");
    }

    [Fact]
    public void CubeLut_rewritten_text_never_carries_the_file_title()
    {
        // ffmpeg reads .cube lines into a 512-byte buffer: a long title used to spill into lines of its own.
        string title = new string('x', 600) + " LUT_3D_SIZE 2 1 0 0";
        CubeLut threeD = CubeLut.Parse($"TITLE \"{title}\"\nLUT_3D_SIZE 2\n0 0 0\n1 0 0\n0 1 0\n1 1 0\n0 0 1\n1 0 1\n0 1 1\n1 1 1\n");
        threeD.ToCubeText().Should().StartWith("TITLE \"ReelBolt\"\nLUT_3D_SIZE 2\n").And.NotContain("xxxx");
        CubeLut oneD = CubeLut.Parse($"TITLE \"{title}\"\nLUT_1D_SIZE 2\n0 0 0\n1 1 1\n");
        oneD.ToCubeText().Should().StartWith("TITLE \"ReelBolt\"\n").And.NotContain("xxxx");
        CubeLut shaper = CubeLut.Parse($"TITLE \"{title}\"\nLUT_1D_SIZE 2\nLUT_3D_SIZE 2\n0 0 0\n1 1 1\n0 0 0\n1 0 0\n0 1 0\n1 1 0\n0 0 1\n1 0 1\n0 1 1\n1 1 1\n");
        shaper.ToShaperCubeText().Should().StartWith("TITLE \"ReelBolt\"\n").And.NotContain("xxxx");
        shaper.WithIntensity(0.5).ToCubeText().Should().StartWith("TITLE \"ReelBolt\"\n").And.NotContain("xxxx");
    }

    [Fact]
    public void CubeLut_3D_canonical_text_is_pinned()
    {
        // Pinned from the pre-1D parser: a 3D-only file's rewrite and its half-intensity blend must never change.
        const string text = "TITLE \"Pin\"\nLUT_3D_SIZE 3\nDOMAIN_MIN -1 -1 -1\nDOMAIN_MAX 1 1 1\n" +
            "-1 -1 -1\n0 -1 -1\n1 -1 -1\n-1 0 -1\n0 0 -1\n1 0 -1\n-1 1 -1\n0 1 -1\n1 1 -1\n" +
            "-1 -1 0\n0 -1 0\n1 -1 0\n-1 0 0\n0 0 0\n1 0 0\n-1 1 0\n0 1 0\n1 1 0\n" +
            "-1 -1 1\n0 -1 1\n1 -1 1\n-1 0 1\n0 0 1\n1 0 1\n-1 1 1\n0 1 1\n1 1 1.5\n";
        CubeLut lut = CubeLut.Parse(text);
        lut.Kind.Should().Be(CubeLutKind.ThreeD);
        lut.ToCubeText().Should().StartWith("TITLE \"ReelBolt\"\nLUT_3D_SIZE 3\nDOMAIN_MIN 0 0 0\nDOMAIN_MAX 1 1 1\n0 0 0\n0.5 0 0\n1 0 0\n0 0.5 0\n");
        lut.ToCubeText().Should().EndWith("0.5 1 1\n1 1 1\n");
        lut.WithIntensity(0.5).Sample(0.5f, 0.5f, 0.5f).R.Should().BeApproximately(0.5f, 1e-6f);
    }

    [Fact]
    public void Lut_failures_are_user_input_and_deterministic_codes_are_not_retried()
    {
        ReelBolt.WorkflowEngine.Services.Billing.FailureClassifier.ForCode("LUT_INVALID").Should().Be(ReelBolt.Shared.Data.Models.FailureClass.UserInput);
        ReelBolt.WorkflowEngine.Services.Billing.FailureClassifier.ForCode("LUT_UNSUPPORTED").Should().Be(ReelBolt.Shared.Data.Models.FailureClass.UserInput);
        foreach (string code in new[] { "INVALID_CONFIG", "TIMELINE_NOT_FOUND", "REVISION_NOT_RESTORABLE", "DOCUMENT_INVALID", "ASSET_UNRESOLVED", "ASSET_OUTSIDE_PROJECT", "CONTENT_BLOCKED", "LUT_INVALID", "LUT_UNSUPPORTED", "SOURCE_INVALID" })
            ReelBolt.WorkflowEngine.Execution.StepExecutors.TimelineRenderStepExecutor.IsDeterministicUserCode(code).Should().BeTrue(code);
        foreach (string code in new[] { "RENDER_FAILED", "UNEXPECTED" })
            ReelBolt.WorkflowEngine.Execution.StepExecutors.TimelineRenderStepExecutor.IsDeterministicUserCode(code).Should().BeFalse(code);
    }

    [Fact]
    public void CubeLut_reads_one_d_and_shaper_files()
    {
        CubeLut oneD = CubeLut.Parse("LUT_1D_SIZE 3\n0 0 0\n0.25 0.5 1\n1 1 1\n");
        oneD.Kind.Should().Be(CubeLutKind.OneD);
        oneD.ToCubeText().Should().Be("TITLE \"ReelBolt\"\nLUT_1D_SIZE 3\nDOMAIN_MIN 0 0 0\nDOMAIN_MAX 1 1 1\n0 0 0\n0.25 0.5 1\n1 1 1\n");
        oneD.Sample(0.25f, 0.5f, 1).Should().Be((0.125f, 0.5f, 1f));
        oneD.WithIntensity(0).ToCubeText().Should().Contain("0.5 0.5 0.5");

        // A range -1..1 is resampled onto 0..1 (x=0.5 -> table position 0.75*2).
        CubeLut ranged = CubeLut.Parse("LUT_1D_SIZE 3\nLUT_1D_INPUT_RANGE -1 1\n0 0 0\n0.5 0.5 0.5\n1 1 1\n");
        ranged.Sample(0.5f, 0.5f, 0.5f).R.Should().BeApproximately(0.75f, 1e-6f);

        string rows3 = string.Join("", Enumerable.Range(0, 8).Select(i => $"{i & 1} {(i >> 1) & 1} {(i >> 2) & 1}\n"));
        CubeLut shaper = CubeLut.Parse("LUT_1D_SIZE 2\nLUT_3D_SIZE 2\n0 0 0\n1 1 1\n" + rows3);
        shaper.Kind.Should().Be(CubeLutKind.ShaperThreeD);
        shaper.KindAtIntensity(1).Should().Be(CubeLutKind.ShaperThreeD);
        shaper.KindAtIntensity(0.5).Should().Be(CubeLutKind.ThreeD);
        shaper.WithIntensity(0.5).Size.Should().Be(65);
        shaper.ToShaperCubeText().Should().Contain("LUT_1D_SIZE 2");
    }

    // ── Validator ────────────────────────────────────────────────────────────

    private static EditTimelineValidationContext Context(Guid ownedFile) => new(ProjectId, new Dictionary<AssetRef, long>(), new Dictionary<Guid, Guid> { [ownedFile] = ProjectId, [LutRef.ProjectFileId!.Value] = ProjectId }, new Dictionary<Guid, Guid>());

    [Fact]
    public void Validator_accepts_the_whole_P7_surface_and_names_every_out_of_range_value()
    {
        Guid file = AssetA.ProjectFileId!.Value;
        MediaClip good = Clip("1", 0, 30,
            grade: Stack(new GradeNode(Guid.NewGuid(), Primaries: new GradePrimaries(Lift: new GradeWheel(0.1, 0, 0, 0), Contrast: 1.5, Exposure: -1), Curves: new GradeCurves(Y: [new CurvePoint(0.2, 0.1), new CurvePoint(0.8, 0.9)]), Lut: new GradeLut(LutRef, 0.7))),
            mask: new ShapeMask("rect", 0.5, 0.5, 0.4, 0.4, 0.1, true), keyer: new ChromaKeyer("#1E3F0A", 0.4, 0.2, 0.5),
            effects: [new ClipEffect("sharpen", 0.3)], stabilize: true, reframe: new ReframeTarget(0.3, 0.6), retimeQuality: "opticalFlow",
            pin: new QuadCorners(new QuadPoint(0, 0), new QuadPoint(1, 0), new QuadPoint(1, 1), new QuadPoint(0, 1)));
        TitleClip title = new(Guid.NewGuid(), "t", 30, 60, "Hi", Template: "boxed");
        AdjustmentClip adjustment = new(Guid.NewGuid(), "adj", 0, 60, null, Stack(new GradeNode(Guid.NewGuid(), Primaries: new GradePrimaries(Saturation: 1.2))), [new ClipEffect("grain", 0.1)]);
        ColorGroup group = new(Guid.NewGuid(), "G", [good.Id], Stack(new GradeNode(Guid.NewGuid(), Primaries: new GradePrimaries(Hue: 5))));
        EditTimeline valid = Doc([group], VideoTrack("1", good, title), VideoTrack("2", adjustment));
        EditTimelineValidator.Validate(valid, Context(file)).Violations.Should().BeEmpty();

        MediaClip bad = Clip("1", 0, 30,
            grade: Stack(new GradeNode(Guid.NewGuid(), Primaries: new GradePrimaries(Gain: new GradeWheel(Master: 1.5), Contrast: 4, Pivot: 2, Saturation: -1, Hue: 200, Temperature: 2, Tint: -3, Exposure: 9),
                Curves: new GradeCurves(R: [new CurvePoint(0.5, 0.5), new CurvePoint(0.5, 0.6), new CurvePoint(2, 0)]), Lut: new GradeLut(new AssetRef(AssetRefKind.ProjectFile, Guid.NewGuid()), 1.5))),
            mask: new ShapeMask("star", 2, -1, 0.5, 0.5, 0.9), keyer: new ChromaKeyer("green", 0, 2, -1),
            effects: [new ClipEffect("sparkle", 3)], reframe: new ReframeTarget(1.5, 0.5), retimeQuality: "magic",
            pin: new QuadCorners(new QuadPoint(5, 0), new QuadPoint(1, 0), new QuadPoint(1, 1), new QuadPoint(0, double.NaN)));
        TitleClip badTitle = new(Guid.NewGuid(), "t", 30, 60, "Hi", Template: "neon");
        ColorGroup badGroup = new(Guid.NewGuid(), "", [bad.Id, Guid.NewGuid()]);
        ColorGroup dupGroup = new(Guid.NewGuid(), "D", [bad.Id]);
        List<string> paths = EditTimelineValidator.Validate(Doc([badGroup, dupGroup], VideoTrack("1", bad, badTitle)), Context(file)).Violations.Select(v => v.Path).ToList();
        string clip = "tracks[0].items[0]";
        paths.Should().Contain([
            $"{clip}.grade.nodes[0].primaries.gain.master", $"{clip}.grade.nodes[0].primaries.contrast", $"{clip}.grade.nodes[0].primaries.pivot",
            $"{clip}.grade.nodes[0].primaries.saturation", $"{clip}.grade.nodes[0].primaries.hue", $"{clip}.grade.nodes[0].primaries.temperature",
            $"{clip}.grade.nodes[0].primaries.tint", $"{clip}.grade.nodes[0].primaries.exposure",
            $"{clip}.grade.nodes[0].curves.r[1].x", $"{clip}.grade.nodes[0].curves.r[2]",
            $"{clip}.grade.nodes[0].lut.file", $"{clip}.grade.nodes[0].lut.intensity",
            $"{clip}.mask.kind", $"{clip}.mask.x", $"{clip}.mask.y", $"{clip}.mask.feather",
            $"{clip}.keyer.color", $"{clip}.keyer.similarity", $"{clip}.keyer.blend", $"{clip}.keyer.spill",
            $"{clip}.effects[0].type", $"{clip}.effects[0].amount", $"{clip}.reframe.x", $"{clip}.retimeQuality",
            $"{clip}.cornerPin.topLeft", $"{clip}.cornerPin.bottomLeft",
            "tracks[0].items[1].template",
            "colorGroups[0].name", "colorGroups[1].clipIds[0]",
        ]);
        // Too many nodes, effects and groups are each named.
        GradeStack tooMany = Stack(Enumerable.Range(0, 9).Select(_ => new GradeNode(Guid.NewGuid())).ToArray());
        List<string> more = EditTimelineValidator.Validate(
            Doc(Enumerable.Range(0, 33).Select(i => new ColorGroup(Guid.NewGuid(), $"g{i}", [])).ToList(), VideoTrack("1", Clip("1", 0, 30, grade: tooMany, effects: Enumerable.Range(0, 9).Select(_ => new ClipEffect("blur", 0.1)).ToList()))), Context(file)).Violations.Select(v => v.Path).ToList();
        more.Should().Contain(["tracks[0].items[0].grade.nodes", "tracks[0].items[0].effects", "colorGroups"]);
    }

    [Fact]
    public void Lut_files_are_collected_from_clips_groups_and_adjustments_once_each()
    {
        GradeStack withLut = Stack(new GradeNode(Guid.NewGuid(), Lut: new GradeLut(LutRef)));
        AssetRef other = new(AssetRefKind.ProjectFile, Guid.NewGuid());
        EditTimeline doc = Doc(
            [new ColorGroup(Guid.NewGuid(), "g", [], Stack(new GradeNode(Guid.NewGuid(), Lut: new GradeLut(other))))],
            VideoTrack("1", Clip("1", 0, 30, grade: withLut)),
            VideoTrack("2", new AdjustmentClip(Guid.NewGuid(), "a", 0, 30, null, withLut)));
        TimelineAssetReferences.LutFiles(doc).Distinct().Should().BeEquivalentTo([LutRef, other]);
    }

    private static MediaClip TransformedClip(ClipTransform transform) =>
        new(Guid.Parse("11111111-1111-4111-8111-111111111111"), "clip-1", 0, 30, AssetA, 0, 1.0, true, null, Transform: transform);

    private static string RegionHash(params Item[] items) =>
        RegionRecipeHasher.Hash(SegmentPlanner.Plan(Doc(null, VideoTrack("1", items)), null).Regions[0], Target(), Assets, "f");

    [Fact]
    public void ScaleY_absent_keeps_the_uniform_scale_bytes_and_the_recipe_hash()
    {
        MediaClip uniform = TransformedClip(new ClipTransform(Scale: new Keyframed<double>(2)));
        Graph(Build(Doc(null, VideoTrack("1", uniform)))).Should().Contain("scale=w='trunc(iw*(2)/2)*2':h='trunc(ih*(2)/2)*2':eval=frame");
        RegionHash(uniform).Should().Be(RegionHash(TransformedClip(new ClipTransform(Scale: new Keyframed<double>(2), ScaleY: null))));
    }

    [Fact]
    public void A_static_scaleY_scales_width_and_height_separately_and_changes_the_hash()
    {
        MediaClip free = TransformedClip(new ClipTransform(Scale: new Keyframed<double>(2), ScaleY: new Keyframed<double>(0.5)));
        Graph(Build(Doc(null, VideoTrack("1", free)))).Should().Contain("scale=w='trunc(iw*(2)/2)*2':h='trunc(ih*(0.5)/2)*2':eval=frame");
        RegionHash(free).Should().NotBe(RegionHash(TransformedClip(new ClipTransform(Scale: new Keyframed<double>(2)))));

        MediaClip onlyY = TransformedClip(new ClipTransform(ScaleY: new Keyframed<double>(0.5)));
        Graph(Build(Doc(null, VideoTrack("1", onlyY)))).Should().Contain("scale=w='trunc(iw*(1)/2)*2':h='trunc(ih*(0.5)/2)*2':eval=frame");
    }

    [Fact]
    public void A_keyed_scaleY_is_a_time_expression_on_the_height_only()
    {
        MediaClip keyed = TransformedClip(new ClipTransform(
            ScaleY: new Keyframed<double>(1, [new Keyframe<double>(0, 1, InterpKind.Linear), new Keyframe<double>(15, 2, InterpKind.Linear)])));
        string graph = Graph(Build(Doc(null, VideoTrack("1", keyed))));
        graph.Should().Contain("scale=w='trunc(iw*(1)/2)*2':h='trunc(ih*(");
        graph.Should().Contain(")/2)*2':eval=frame");
    }

    [Fact]
    public void A_title_without_a_transform_is_the_unchanged_drawtext_line_and_hash()
    {
        TitleClip plain = new(Guid.NewGuid(), "t", 0, 30, "Hello");
        string graph = Graph(Build(Doc(null, VideoTrack("1", plain))));
        graph.Should().Contain("[t0]").And.NotContain("rgba,drawtext").And.NotContain("rotate=");
        RegionHash(plain).Should().Be(RegionHash(new TitleClip(plain.Id, "t", 0, 30, "Hello", Transform: null)));
    }

    [Fact]
    public void A_title_with_a_transform_is_drawn_on_its_own_layer_rotated_scaled_and_overlaid()
    {
        TitleClip moved = new(Guid.NewGuid(), "t", 0, 30, "Hello",
            Transform: new ClipTransform(PositionX: new Keyframed<double>(0.1), Scale: new Keyframed<double>(2), Rotation: new Keyframed<double>(45), Opacity: new Keyframed<double>(0.5)));
        string graph = Graph(Build(Doc(null, VideoTrack("1", moved))));
        graph.Should().Contain("color=c=black@0:s=320x180:");
        graph.Should().Contain("format=rgba,drawtext=fontfile='/fonts/f.ttf'");
        graph.Should().Contain("[t0s]");
        graph.Should().Contain("rotate=a='(45)*PI/180'");
        graph.Should().Contain("colorchannelmixer=aa=0.5");
        graph.Should().Contain("[t0l]overlay=x='(W-w)/2+W*(0.1)':y='(H-h)/2':eval=frame:format=auto:eof_action=pass[t0]");
        RegionHash(moved).Should().NotBe(RegionHash(new TitleClip(moved.Id, "t", 0, 30, "Hello")));
    }

    // ── Insert clips are rendered (T-017) ────────────────────────────────────

    private static QuadCorners Quad(double x0, double y0, double x1, double y1, double x2, double y2, double x3, double y3) =>
        new(new QuadPoint(x0, y0), new QuadPoint(x1, y1), new QuadPoint(x2, y2), new QuadPoint(x3, y3));

    private static InsertClip Insert(Keyframed<QuadCorners>? quad, bool enabled = true) =>
        new(Guid.Parse("22222222-2222-4222-8222-222222222222"), "insert", 0, 30, AssetA, quad, enabled);

    [Fact]
    public void The_planner_renders_an_insert_with_media_and_a_quad_and_skips_an_incomplete_one()
    {
        QuadCorners q = Quad(0.1, 0.1, 0.9, 0.2, 0.8, 0.9, 0.2, 0.8);
        SegmentPlanner.Plan(Doc(null, VideoTrack("1", Insert(new Keyframed<QuadCorners>(q)))), null)
            .Regions[0].Layers.Select(l => l.Item).Should().ContainSingle().Which.Should().BeOfType<InsertClip>();
        SegmentPlanner.Plan(Doc(null, VideoTrack("1", Insert(new Keyframed<QuadCorners>(q), enabled: false))), null).SkippedItems.Should().BeEmpty();

        RenderPlan incomplete = SegmentPlanner.Plan(Doc(null, VideoTrack("1", Insert(null))), null);
        incomplete.SkippedItems.Should().ContainSingle().Which.Reason.Should().Be("insert_incomplete");
    }

    [Fact]
    public void A_static_fraction_quad_is_pinned_with_perspective_in_destination_sense_and_overlaid()
    {
        InsertClip insert = Insert(new Keyframed<QuadCorners>(Quad(0.1, 0.1, 0.9, 0.2, 0.8, 0.9, 0.2, 0.8)));
        string graph = Graph(Build(Doc(null, VideoTrack("1", insert))));
        graph.Should().Contain("scale=320:180,setsar=1,format=rgba,drawbox=x=0:y=0:w=iw:h=ih:t=1:color=black@0:replace=1,perspective=x0=32:y0=18:x1=288:y1=36:x2=64:y2=144:x3=256:y3=162:sense=destination[l0]");
        graph.Should().Contain("tpad=stop=-1:stop_mode=clone");
        graph.Should().Contain("[base][l0]overlay=eof_action=pass[v0]");

        // The same quad in canvas pixels reads as the same warp.
        string pixels = Graph(Build(Doc(null, VideoTrack("1", Insert(new Keyframed<QuadCorners>(Quad(32, 18, 288, 36, 256, 162, 64, 144)))))));
        pixels.Should().Contain("perspective=x0=32:y0=18:x1=288:y1=36:x2=64:y2=144:x3=256:y3=162:sense=destination[l0]");
    }

    [Fact]
    public void A_keyed_quad_is_per_frame_expressions_over_the_one_based_in_variable()
    {
        QuadCorners a = Quad(0.1, 0.1, 0.9, 0.1, 0.9, 0.9, 0.1, 0.9);
        QuadCorners b = Quad(0.2, 0.2, 0.8, 0.2, 0.8, 0.8, 0.2, 0.8);
        InsertClip insert = Insert(new Keyframed<QuadCorners>(a, [new Keyframe<QuadCorners>(0, a, InterpKind.Linear), new Keyframe<QuadCorners>(30, b, InterpKind.Linear)]));
        string graph = Graph(Build(Doc(null, VideoTrack("1", insert))));
        graph.Should().Contain("perspective=x0=");
        graph.Should().Contain("((in-1)/(30/1))");
        graph.Should().Contain("sense=destination:eval=frame[l0]");
    }

    [Fact]
    public void The_insert_is_part_of_the_recipe_hash_asset_and_quad_keys()
    {
        MediaClip clip = Clip("1", 0, 30);
        string Hash(params Item[] items) => RegionRecipeHasher.Hash(SegmentPlanner.Plan(Doc(null, VideoTrack("1", items)), null).Regions[0], Target(), Assets, "f");
        QuadCorners q = Quad(0.1, 0.1, 0.9, 0.2, 0.8, 0.9, 0.2, 0.8);
        QuadCorners moved = Quad(0.1, 0.1, 0.9, 0.3, 0.8, 0.9, 0.2, 0.8);

        string without = Hash(clip);
        string withInsert = Hash(clip, Insert(new Keyframed<QuadCorners>(q)));
        withInsert.Should().NotBe(without);
        Hash(clip, Insert(new Keyframed<QuadCorners>(moved))).Should().NotBe(withInsert);
        Hash(clip, Insert(new Keyframed<QuadCorners>(q))).Should().Be(withInsert);
    }

    [Fact]
    public void An_insert_matte_is_keyed_before_the_warp_and_is_part_of_the_hash()
    {
        Keyframed<QuadCorners> quad = new(Quad(0.1, 0.1, 0.9, 0.2, 0.8, 0.9, 0.2, 0.8));
        InsertClip keyed = new(Guid.Parse("22222222-2222-4222-8222-222222222222"), "insert", 0, 30, AssetA, quad, true, null, new ChromaKeyer("#00FF00", 0.2));
        string graph = Graph(Build(Doc(null, VideoTrack("1", keyed))));
        graph.Should().Contain("scale=320:180,setsar=1,format=rgba,colorkey=color=0x00FF00:similarity=0.2");
        graph.IndexOf("colorkey", StringComparison.Ordinal).Should().BeLessThan(graph.IndexOf("perspective", StringComparison.Ordinal));
        RegionRecipeHasher.Hash(SegmentPlanner.Plan(Doc(null, VideoTrack("1", keyed)), null).Regions[0], Target(), Assets, "f")
            .Should().NotBe(RegionRecipeHasher.Hash(SegmentPlanner.Plan(Doc(null, VideoTrack("1", Insert(quad))), null).Regions[0], Target(), Assets, "f"));
    }

    [Fact]
    public void Quad_units_are_decided_once_across_the_base_and_every_key()
    {
        // Base in 0..1 but one key in pixels: the whole quad is pixels, so the base (0.1, 0.1 ...) is read as pixels too.
        QuadCorners fractionBase = Quad(0.1, 0.1, 0.9, 0.1, 0.9, 0.9, 0.1, 0.9);
        QuadCorners pixelKey = Quad(32, 18, 288, 18, 288, 162, 32, 162);
        Keyframed<QuadCorners> mixed = new(fractionBase, [new Keyframe<QuadCorners>(0, pixelKey, InterpKind.Linear)]);
        InsertLayerBuilder.Warp(mixed, 0, Target()).Should().Contain("32");

        Keyframed<QuadCorners> allFractions = new(fractionBase);
        InsertLayerBuilder.Warp(allFractions, 0, Target()).Should().Contain("perspective=x0=32:y0=18:x1=288:y1=18");
    }
}
