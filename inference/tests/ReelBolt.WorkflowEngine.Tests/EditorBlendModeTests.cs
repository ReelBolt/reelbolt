using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Editing;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Services.Video;
using ReelBolt.WorkflowEngine.Services.Video.TimelineRender;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// editor-wysiwyg L5 — <c>MediaClip.blendMode</c>: the closed word set, video-track-only validation, the
/// flattener carry, the region recipe hash (unchanged for normal, different per mode) and the RGB blend
/// stage in the filter graph (normal stays byte-identical to the plain overlay).
/// </summary>
public class EditorBlendModeTests
{
    private static readonly Guid ProjectId = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly AssetRef AssetA = new(AssetRefKind.ProjectFile, Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"), null);
    private static readonly EditTimelineSettings Settings = new(320, 180, new Rational(30, 1), 48000, new Rational(1, 1), AspectPolicy.Fit, 0);

    private static readonly Dictionary<AssetRef, LocalAsset> Assets = new()
    {
        [AssetA] = new LocalAsset("/tmp/a.mp4", "a|100", new MediaProbeResult(10, 30, 1, 320, 180, "h264", "aac", 48000)),
    };

    private static MediaClip Clip(string id, long recordIn, long recordOut, string? blend = null, ShapeMask? mask = null, long fadeIn = 0) =>
        new(Guid.Parse($"{id}{id}{id}{id}{id}{id}{id}{id}-{id}{id}{id}{id}-4{id}{id}{id}-8{id}{id}{id}-{id}{id}{id}{id}{id}{id}{id}{id}{id}{id}{id}{id}"), $"clip-{id}", recordIn, recordOut, AssetA, 0, 1.0, true, null,
            null, null, null, false, false, fadeIn, 0, null, mask, null, null, false, null, "frame", null, "stereo", null, null, null, BlendMode: blend);

    private static Track TrackOf(TrackKind kind, string id, params Item[] items) =>
        new(Guid.Parse($"{id}{id}{id}{id}{id}{id}{id}{id}-0000-4000-8000-000000000000"), kind, $"T{id}", null, 64, false, true, false, false, null, items);

    private static EditTimeline Doc(params Track[] tracks) =>
        new(Guid.NewGuid(), ProjectId, "doc", Settings, tracks, []);

    private static EditTimelineValidationContext Context() =>
        new(ProjectId, new Dictionary<AssetRef, long>(), new Dictionary<Guid, Guid> { [AssetA.ProjectFileId!.Value] = ProjectId }, new Dictionary<Guid, Guid>());

    private static ResolvedRenderTarget Target() => TimelineRenderPresets.Resolve(new TimelineRenderStepConfig(Guid.NewGuid(), Preset: TimelineRenderPreset.Master), Settings);

    private static RegionFilterGraphBuilder.RegionArgv Build(EditTimeline doc, int regionIndex = 0) =>
        RegionFilterGraphBuilder.Build(SegmentPlanner.Plan(doc, null).Regions[regionIndex], Target(), Assets, "/scratch/r.mp4", "/fonts/f.ttf", n => $"/scratch/t-{n}.txt", 2,
            lutFilePathFor: n => $"/scratch/lut-{n}.cube", stabilizePathFor: n => $"/scratch/stab-{n}.trf");

    private static string Graph(RegionFilterGraphBuilder.RegionArgv argv) => argv.Args[argv.Args.ToList().IndexOf("-filter_complex") + 1];

    private static string Hash(EditTimeline doc) => RegionRecipeHasher.Hash(SegmentPlanner.Plan(doc, null).Regions[0], Target(), Assets, "f");

    private const string P4Layer = "[0:v]trim=duration=1,setpts=PTS-STARTPTS,fps=30/1,scale=320:180:force_original_aspect_ratio=decrease,pad=320:180:(ow-iw)/2:(oh-ih)/2,setsar=1,format=yuv420p";

    // ── Validator ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("normal")]
    [InlineData("multiply")]
    [InlineData("screen")]
    [InlineData("overlay")]
    [InlineData("softLight")]
    [InlineData("hardLight")]
    [InlineData("darken")]
    [InlineData("lighten")]
    [InlineData("add")]
    [InlineData("difference")]
    public void Validator_accepts_every_blend_word_on_a_video_track(string word)
    {
        EditTimeline doc = Doc(TrackOf(TrackKind.Video, "1", Clip("1", 0, 30, blend: word)));
        EditTimelineValidator.Validate(doc, Context()).Violations.Should().BeEmpty();
    }

    [Fact]
    public void Validator_accepts_an_absent_blend_mode()
    {
        EditTimelineValidator.Validate(Doc(TrackOf(TrackKind.Video, "1", Clip("1", 0, 30))), Context()).Violations.Should().BeEmpty();
    }

    [Theory]
    [InlineData("sparkle")]
    [InlineData("Multiply")]
    [InlineData("softlight")]
    [InlineData("")]
    public void Validator_refuses_an_unknown_blend_word_and_names_the_item(string word)
    {
        EditTimeline doc = Doc(TrackOf(TrackKind.Video, "1", Clip("1", 0, 30, blend: word)));
        EditTimelineValidator.Validate(doc, Context()).Violations
            .Should().Contain(v => v.Path == "tracks[0].items[0].blendMode" && v.Message.Contains("blendMode"));
    }

    [Fact]
    public void An_integer_blend_mode_does_not_bind()
    {
        string json = JsonSerializer.Serialize<Item>(Clip("1", 0, 30), EditTimelineJson.Options).Replace("\"assetRef\"", "\"blendMode\":3,\"assetRef\"");
        Action bind = () => JsonSerializer.Deserialize<Item>(json, EditTimelineJson.Options);
        bind.Should().Throw<JsonException>();
    }

    [Fact]
    public void A_blend_word_binds_by_name_under_the_blendMode_key()
    {
        string json = JsonSerializer.Serialize<Item>(Clip("1", 0, 30, blend: "multiply"), EditTimelineJson.Options);
        json.Should().Contain("\"blendMode\":\"multiply\"");
        ((MediaClip)JsonSerializer.Deserialize<Item>(json, EditTimelineJson.Options)!).BlendMode.Should().Be("multiply");
    }

    [Fact]
    public void Validator_refuses_a_blend_mode_on_an_audio_track_but_not_normal()
    {
        EditTimeline blended = Doc(TrackOf(TrackKind.Audio, "1", Clip("1", 0, 30, blend: "screen")));
        EditTimelineValidator.Validate(blended, Context()).Violations
            .Should().Contain(v => v.Path == "tracks[0].items[0].blendMode" && v.Message.Contains("video track"));

        EditTimeline plain = Doc(TrackOf(TrackKind.Audio, "1", Clip("1", 0, 30, blend: "normal")));
        EditTimelineValidator.Validate(plain, Context()).Violations.Should().BeEmpty();
    }

    // ── Flattener ────────────────────────────────────────────────────────────

    [Fact]
    public void Flatten_carries_the_blend_mode_of_a_clip_inside_a_compound_sequence()
    {
        MediaClip inner = Clip("2", 0, 30, blend: "multiply");
        Guid sequenceId = Guid.Parse("33333333-3333-4333-8333-333333333333");
        EditSequence sequence = new(sequenceId, "seq", [TrackOf(TrackKind.Video, "2", inner)]);
        CompoundClip compound = new(Guid.Parse("44444444-4444-4444-8444-444444444444"), "compound", 0, 30, sequenceId);
        EditTimeline doc = new(Guid.NewGuid(), ProjectId, "doc", Settings, [TrackOf(TrackKind.Video, "1", compound)], [], null, null, null, [sequence]);

        EditTimeline flat = TimelineFlattener.Flatten(doc);

        flat.Tracks.SelectMany(t => t.Items).OfType<MediaClip>().Should().ContainSingle().Which.BlendMode.Should().Be("multiply");
    }

    // ── Recipe hash ──────────────────────────────────────────────────────────

    [Fact]
    public void The_recipe_hash_is_unchanged_for_normal_and_changes_for_each_mode()
    {
        string none = Hash(Doc(TrackOf(TrackKind.Video, "1", Clip("1", 0, 30))));
        Hash(Doc(TrackOf(TrackKind.Video, "1", Clip("1", 0, 30, blend: "normal")))).Should().Be(none);

        HashSet<string> seen = [none];
        foreach (string word in BlendModes.Words.Where(w => w != "normal"))
        {
            string hash = Hash(Doc(TrackOf(TrackKind.Video, "1", Clip("1", 0, 30, blend: word))));
            hash.Should().NotBe(none, word);
            seen.Add(hash).Should().BeTrue($"{word} must hash differently from every other mode");
        }
    }

    // ── Filter graph ─────────────────────────────────────────────────────────

    [Fact]
    public void Normal_blend_keeps_the_plain_overlay_bytes()
    {
        string expected = "color=c=black:s=320x180:r=30/1:d=1[base];" + P4Layer + "[l0];[base][l0]overlay=eof_action=pass[v0]";
        foreach (string? word in new string?[] { null, "normal" })
        {
            Graph(Build(Doc(TrackOf(TrackKind.Video, "1", Clip("1", 0, 30, blend: word))))).Should().StartWith(expected);
        }
    }

    [Fact]
    public void A_multiply_layer_blends_in_rgb_against_the_composite_and_mixes_back_by_alpha()
    {
        string graph = Graph(Build(Doc(TrackOf(TrackKind.Video, "1", Clip("1", 0, 30, blend: "multiply")))));

        graph.Should().Be(
            "color=c=black:s=320x180:r=30/1:d=1[base];" + P4Layer + "[l0];" +
            "color=c=black@0:s=320x180:r=30/1:d=1,format=rgba[b0c];" +
            "[b0c][l0]overlay=format=rgb:eof_action=pass,format=rgba,split=2[b0f][b0a];" +
            "[b0f]format=gbrp[b0rgb];" +
            "[b0a]alphaextract[b0al];" +
            "[base]split=2[b0k][b0u];" +
            "[b0k]format=gbrp[b0bk];" +
            "[b0bk][b0rgb]blend=all_mode=multiply[b0m];" +
            "[b0m][b0al]alphamerge,format=rgba[b0x];" +
            "[b0u][b0x]overlay=eof_action=pass[v0];" +
            "[v0]format=yuv420p[vout]");
    }

    [Fact]
    public void Blend_stages_are_never_in_yuv_and_each_mode_uses_its_pinned_ffmpeg_option()
    {
        (string Word, string Option)[] pinned =
        [
            ("multiply", "all_mode=multiply"), ("screen", "all_mode=screen"), ("overlay", "all_mode=overlay"),
            ("hardLight", "all_mode=hardlight"), ("darken", "all_mode=darken"), ("lighten", "all_mode=lighten"),
            ("add", "all_mode=addition"), ("difference", "all_mode=difference"),
            ("softLight", "all_expr=A*A/255+2*B*A*(255-A)/65025"),
        ];
        foreach ((string word, string option) in pinned)
        {
            string graph = Graph(Build(Doc(TrackOf(TrackKind.Video, "1", Clip("1", 0, 30, blend: word)))));
            graph.Should().Contain($"[b0bk][b0rgb]blend={option}[b0m]", word);
            graph.Should().Contain("[b0f]format=gbrp[b0rgb]");
            graph.Should().Contain("[b0k]format=gbrp[b0bk]");
        }
    }

    [Fact]
    public void A_masked_and_faded_blend_layer_keeps_its_alpha_and_mixes_it_back()
    {
        ShapeMask mask = new("rect", 0.5, 0.5, 0.4, 0.4, 0.1, false);
        string graph = Graph(Build(Doc(TrackOf(TrackKind.Video, "1", Clip("1", 0, 30, blend: "screen", mask: mask, fadeIn: 10)))));

        // The layer keeps the alpha it carries (mask + fade) through rgba on the transparent canvas ...
        graph.Should().Contain("format=rgba[l0]");
        // (the layer's own placement options stay; its format=auto is replaced by the blend overlay's format=rgb, never doubled)
        graph.Should().Contain("[b0c][l0]overlay=x='(W-w)/2':y='(H-h)/2':eval=frame:format=rgb:eof_action=pass,format=rgba,split=2[b0f][b0a]");
        graph.Should().NotContain("format=auto:format=rgb");
        // ... blend runs in RGB, and the blended picture is mixed back by that alpha before it lands on the base.
        graph.Should().Contain("[b0bk][b0rgb]blend=all_mode=screen[b0m]");
        graph.Should().Contain("[b0a]alphaextract[b0al];");
        graph.Should().Contain("[b0m][b0al]alphamerge,format=rgba[b0x];[b0u][b0x]overlay=eof_action=pass[v0]");
    }

    [Fact]
    public void A_second_blending_layer_blends_over_the_composite_of_the_first()
    {
        string graph = Graph(Build(Doc(
            TrackOf(TrackKind.Video, "1", Clip("1", 0, 30)),
            TrackOf(TrackKind.Video, "2", Clip("2", 0, 30, blend: "difference")))));

        graph.Should().Contain("[base][l0]overlay=eof_action=pass[v0];");
        graph.Should().Contain("[v0]split=2[b1k][b1u];");
        graph.Should().Contain("[b1u][b1x]overlay=eof_action=pass[v1]");
    }
}
