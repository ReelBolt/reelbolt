#nullable enable
using System;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Services.Video;
using ReelBolt.WorkflowEngine.Services.Video.TimelineRender;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The stabilised preview proxy (editor-wysiwyg): the render's two-pass vidstab chain, at proxy size, so the
/// viewer plays the stabilised picture. The vidstab parameters live in ONE place (<see cref="StabilizeFilter"/>).
/// </summary>
public class StabilizedProxyTests
{
    [Fact]
    public void The_shared_recipe_is_the_exact_chain_the_render_writes()
    {
        // The same literals EditorP7BackendTests pins for the timeline render.
        StabilizeFilter.Detect("/scratch/stab-0.trf")
            .Should().Be("vidstabdetect=shakiness=5:accuracy=15:result='/scratch/stab-0.trf'");
        StabilizeFilter.Transform("/scratch/stab-0.trf")
            .Should().Be("vidstabtransform=input='/scratch/stab-0.trf':smoothing=10:optzoom=1:interpol=bilinear");
    }

    [Fact]
    public void Both_passes_scale_first_and_use_the_shared_recipe()
    {
        string[] detect = StabilizedProxyArgvBuilder.BuildDetect("/w/source.mp4", "/w/s.trf", 540, scaleWidth: false, threads: 2);
        string[] encode = StabilizedProxyArgvBuilder.BuildEncode("/w/source.mp4", "/w/s.trf", "/w/out.mp4", 540, 15, 2);

        detect[Array.IndexOf(detect, "-vf") + 1]
            .Should().Be("scale=-2:540:flags=bicubic," + StabilizeFilter.Detect("/w/s.trf"));
        detect.Should().ContainInOrder("-f", "null", "-");
        encode[Array.IndexOf(encode, "-vf") + 1]
            .Should().Be("scale=-2:540:flags=bicubic," + StabilizeFilter.Transform("/w/s.trf"));
    }

    [Fact]
    public void The_encode_matches_the_playback_proxy_and_drops_the_audio()
    {
        string[] encode = StabilizedProxyArgvBuilder.BuildEncode("/w/source.mp4", "/w/s.trf", "/w/out.mp4", 540, 15, 2);

        encode.Should().ContainInOrder("-c:v", "libx264", "-preset", "veryfast", "-crf", "23", "-profile:v", "main", "-pix_fmt", "yuv420p");
        encode.Should().ContainInOrder("-g", "15", "-keyint_min", "15", "-sc_threshold", "0");
        encode.Should().Contain("-an").And.NotContain("-c:a");
        encode.Should().ContainInOrder("-protocol_whitelist", "file", "-format_whitelist", MediaDerivativeInputFormats.Whitelist);
        encode[^1].Should().Be("/w/out.mp4");
    }

    [Fact]
    public void A_portrait_source_scales_by_width()
    {
        string[] encode = StabilizedProxyArgvBuilder.BuildEncode("/w/s.mp4", "/w/s.trf", "/w/o.mp4", 540, 15, 2, scaleWidth: true);
        encode[Array.IndexOf(encode, "-vf") + 1].Should().StartWith("scale=540:-2:flags=bicubic,");
    }

    [Fact]
    public void The_spec_is_keyed_by_the_recipe_version_and_one_variant()
    {
        MediaDerivativeSpec spec = MediaDerivativeSpec.ForStabilizedProxy(1080, 30, 1, threads: 2, sourceWidth: 1920);

        spec.Slug.Should().Be(MediaDerivativeKindSlug.StabilizedProxy);
        spec.Variant.Should().Be(0);
        spec.ParamsJson.Should().Contain($"\"stabilizeRecipe\":{StabilizeFilter.RecipeVersion}");
        spec.ParamsJson.Should().NotContain("audio");
        spec.ParamsJson.Should().NotBe(MediaDerivativeSpec.ForProxy(1080, 30, 1, hasAudio: false, threads: 2, sourceWidth: 1920).ParamsJson);
        MediaDerivativeSpec.ForStabilizedProxy(1080, 30, 1, 2, 1920).ParamsJson.Should().Be(spec.ParamsJson);
    }

    [Fact]
    public void It_is_on_demand_only_and_maps_to_its_persisted_kind()
    {
        MediaDerivativeDispatcher.ToPersistedKind(MediaDerivativeKindSlug.StabilizedProxy)
            .Should().Be(MediaDerivativeKind.StabilizedProxy);

        MediaDerivativeDispatcher.ResolveSpecPlan("video/mp4", null).Select(p => p.Slug)
            .Should().NotContain(MediaDerivativeKindSlug.StabilizedProxy);
        MediaDerivativeDispatcher.ResolveSpecPlan("video/mp4", [MediaDerivativeKind.StabilizedProxy])
            .Should().Equal([(MediaDerivativeKindSlug.StabilizedProxy, 0)]);
        MediaDerivativeDispatcher.ResolveSpecPlan("audio/mpeg", [MediaDerivativeKind.StabilizedProxy]).Should().BeEmpty();
        MediaDerivativeRequests.DefaultKinds.Should().NotContain(MediaDerivativeKind.StabilizedProxy);
    }
}
