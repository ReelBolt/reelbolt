#nullable enable
using System;
using System.Collections.Generic;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Editing;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Services.Video.TimelineRender;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Workflows;

/// <summary>
/// The shared TimelineRender size table (<see cref="TimelineRenderSizing"/>) that both the API's
/// plan gate and the engine's plan clamp read, its parity with <see cref="TimelineRenderPresets"/>
/// (so the two cannot drift), and the aspect-preserving cap the engine applies.
/// </summary>
public class TimelineRenderSizingTests
{
    private static readonly Guid TimelineId = Guid.Parse("7e7e7e7e-0000-4000-8000-000000000001");

    private static TimelineRenderStepConfig Config(TimelineRenderPreset preset, TimelineRenderEncodeSettings? encode = null) =>
        new(TimelineId, Preset: preset, Encode: encode);

    [Theory]
    [InlineData(TimelineRenderPreset.Hd1080p16x9, 1080)]
    [InlineData(TimelineRenderPreset.Uhd4k16x9, 2160)]
    [InlineData(TimelineRenderPreset.Vertical9x16, 1080)]
    [InlineData(TimelineRenderPreset.Square1x1, 1080)]
    [InlineData(TimelineRenderPreset.Portrait4x5, 1080)]
    public void ShortSide_of_a_fixed_preset_is_its_table_size_whatever_the_timeline(TimelineRenderPreset preset, int expected)
    {
        TimelineRenderSizing.ShortSide(Config(preset), 640, 360).Should().Be(expected);
    }

    [Fact]
    public void ShortSide_of_Master_is_the_timelines_own_short_side_evened()
    {
        TimelineRenderSizing.ShortSide(Config(TimelineRenderPreset.Master), 3840, 2160).Should().Be(2160);
        TimelineRenderSizing.ShortSide(Config(TimelineRenderPreset.Master), 1081, 1921).Should().Be(1080);
    }

    [Fact]
    public void ShortSide_of_AudioOnly_is_null_even_with_an_encode_size()
    {
        TimelineRenderSizing.ShortSide(Config(TimelineRenderPreset.AudioOnly), 1920, 1080).Should().BeNull();
        TimelineRenderSizing.ShortSide(Config(TimelineRenderPreset.AudioOnly, new TimelineRenderEncodeSettings(3840, 2160)), 1920, 1080).Should().BeNull();
    }

    [Fact]
    public void An_explicit_encode_size_wins_over_the_preset()
    {
        TimelineRenderSizing.ShortSide(Config(TimelineRenderPreset.Hd1080p16x9, new TimelineRenderEncodeSettings(2560, 1440)), 1920, 1080).Should().Be(1440);
        TimelineRenderSizing.ShortSide(Config(TimelineRenderPreset.Uhd4k16x9, new TimelineRenderEncodeSettings(1280, 720)), 1920, 1080).Should().Be(720);
        TimelineRenderSizing.ShortSide(Config(TimelineRenderPreset.Master, new TimelineRenderEncodeSettings(720, 1280)), 3840, 2160).Should().Be(720);
    }

    [Fact]
    public void An_encode_without_a_size_keeps_the_preset_size()
    {
        TimelineRenderSizing.ShortSide(Config(TimelineRenderPreset.Uhd4k16x9, new TimelineRenderEncodeSettings(Crf: 20)), 1920, 1080).Should().Be(2160);
    }

    [Theory]
    [InlineData(TimelineRenderPreset.Uhd4k16x9, null, null, 2160)]
    [InlineData(TimelineRenderPreset.Hd1080p16x9, 2560, 1440, 1440)]
    [InlineData(TimelineRenderPreset.Master, 1440, 2560, 1440)]
    [InlineData(TimelineRenderPreset.Uhd4k16x9, 1280, 720, 720)]
    [InlineData(TimelineRenderPreset.Hd1080p16x9, null, null, null)]
    [InlineData(TimelineRenderPreset.Vertical9x16, null, null, null)]
    [InlineData(TimelineRenderPreset.Square1x1, null, null, null)]
    [InlineData(TimelineRenderPreset.Portrait4x5, null, null, null)]
    [InlineData(TimelineRenderPreset.Master, null, null, null)]
    [InlineData(TimelineRenderPreset.AudioOnly, 3840, 2160, null)]
    public void ExplicitShortSide_is_set_only_for_4K_or_an_explicit_encode_size(TimelineRenderPreset preset, int? width, int? height, int? expected)
    {
        TimelineRenderEncodeSettings? encode = width is null ? null : new TimelineRenderEncodeSettings(width, height);
        TimelineRenderSizing.ExplicitShortSide(Config(preset, encode)).Should().Be(expected);
    }

    [Theory]
    [InlineData(1920, 1080, 720, 1280, 720)]
    [InlineData(1080, 1920, 720, 720, 1280)]
    [InlineData(3840, 2160, 2160, 3840, 2160)]
    [InlineData(1280, 720, 1080, 1280, 720)] // never upscales
    [InlineData(1080, 1350, 720, 720, 900)]
    [InlineData(1000, 562, 500, 890, 500)] // 889.68 rounds to the nearest even
    [InlineData(1920, 1080, 715, 1270, 714)] // an odd cap still yields an even short side at or under it
    public void CapToShortSide_preserves_the_aspect_with_even_sides(int w, int h, int cap, int expectedW, int expectedH)
    {
        TimelineRenderSizing.CapToShortSide(w, h, cap).Should().Be((expectedW, expectedH));
    }

    public static IEnumerable<object?[]> ParityCases()
    {
        TimelineRenderEncodeSettings?[] encodes = [null, new TimelineRenderEncodeSettings(Crf: 22), new TimelineRenderEncodeSettings(2560, 1440)];
        (int W, int H)[] timelines = [(1920, 1080), (1081, 1921), (8000, 5000), (3840, 2160)];
        foreach (TimelineRenderPreset preset in Enum.GetValues<TimelineRenderPreset>())
            foreach (TimelineRenderEncodeSettings? encode in encodes)
                foreach ((int w, int h) in timelines)
                    yield return [preset, encode, w, h];
    }

    [Theory]
    [MemberData(nameof(ParityCases))]
    public void Dimensions_match_what_the_engine_preset_table_resolves(TimelineRenderPreset preset, TimelineRenderEncodeSettings? encode, int width, int height)
    {
        TimelineRenderStepConfig config = Config(preset, encode);
        EditTimelineSettings settings = new(width, height, new Rational(30, 1), 48000, new Rational(1, 1), AspectPolicy.Fit, 0);

        ResolvedRenderTarget target = TimelineRenderPresets.Resolve(config, settings);

        TimelineRenderSizing.Dimensions(config, width, height).Should().Be((target.Width, target.Height));
        int? shortSide = TimelineRenderSizing.ShortSide(config, width, height);
        if (target.VideoEnabled) shortSide.Should().Be(Math.Min(target.Width, target.Height));
        else shortSide.Should().BeNull();
    }
}
