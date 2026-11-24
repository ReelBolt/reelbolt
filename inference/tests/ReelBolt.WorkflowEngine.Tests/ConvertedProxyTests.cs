#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Editing.Color;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// P5a: the converted proxy derivative (a camera profile's conversion baked into the 8-bit proxy). Pure rules (variant
/// mapping, argv, spec identity, dispatcher plan) run everywhere; the real-ffmpeg test skips, with the reason, on a host
/// whose ffmpeg lacks <c>zscale</c> / <c>lut3d</c>.
/// </summary>
public sealed class ConvertedProxyTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "conv-proxy-" + Guid.NewGuid().ToString("N"));

    public ConvertedProxyTests() => Directory.CreateDirectory(_work);

    public void Dispose()
    {
        try { Directory.Delete(_work, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // ── variant <-> profile ──────────────────────────────────────────────────

    [Fact]
    public void The_profile_id_order_is_frozen_because_it_is_the_variant_index()
    {
        CameraProfiles.Ids.Should().Equal(
            "appleLog", "djiDLog", "djiDLogM", "sonySLog3", "panasonicVLog", "canonCLog3",
            "arriLogC3", "arriLogC4", "fujiFLog", "fujiFLog2", "nikonNLog", "redLog3G10");
    }

    [Fact]
    public void Variant_zero_is_plain_and_one_to_twelve_follow_the_catalogue_order()
    {
        CameraProfileVariants.VariantOf(null).Should().Be(0);
        CameraProfileVariants.VariantOf("nope").Should().Be(0);
        CameraProfileVariants.TryProfileOf(0, out _).Should().BeFalse();
        CameraProfileVariants.TryProfileOf(13, out _).Should().BeFalse();
        for (int i = 0; i < CameraProfiles.Ids.Count; i++)
        {
            CameraProfileVariants.VariantOf(CameraProfiles.Ids[i]).Should().Be(i + 1);
            CameraProfileVariants.TryProfileOf(i + 1, out string? id).Should().BeTrue();
            id.Should().Be(CameraProfiles.Ids[i]);
        }
    }

    // ── argv ─────────────────────────────────────────────────────────────────

    [Fact]
    public void The_plain_argv_is_unchanged_and_has_no_conversion_stage()
    {
        string[] plain = ProxyArgvBuilder.Build("/in/src.mp4", "/out/p.mp4", 540, 15, true, 4);
        string vf = plain[Array.IndexOf(plain, "-vf") + 1];
        vf.Should().Be("scale=-2:540:flags=bicubic");
        plain.Should().NotContain("-colorspace");
        string.Join(' ', plain).Should().NotContain("lut3d");
    }

    [Fact]
    public void The_converted_argv_scales_first_then_runs_the_stage_on_the_small_frame_and_tags()
    {
        string[] args = ProxyArgvBuilder.Build("/in/src.mp4", "/out/p.mp4", 540, 15, true, 4, false, null, "sonySLog3", "/scratch/camera-profile.cube");
        string vf = args[Array.IndexOf(args, "-vf") + 1];
        vf.Should().Be(
            "scale=-2:540:flags=bicubic," +
            "zscale=rin=pc:min=709:tin=709:pin=709:t=709:p=709,format=gbrpf32le," +
            "lut3d=file='/scratch/camera-profile.cube':interp=tetrahedral," +
            "scale=out_range=tv:out_color_matrix=bt709:flags=accurate_rnd+full_chroma_int,format=yuv420p," +
            "setparams=range=tv:colorspace=bt709:color_primaries=bt709:color_trc=bt709");
        vf.Should().NotContain("format=yuv420p,zscale", "the 10-bit source depth is kept through the scale");
        args.Should().ContainInOrder("-colorspace", "bt709", "-color_primaries", "bt709", "-color_trc", "bt709");
    }

    [Fact]
    public void The_cube_path_is_quoted_like_the_grade_filter_quotes_a_lut_path()
    {
        string[] args = ProxyArgvBuilder.Build("/in", "/out", 540, 15, false, 2, false, null, "appleLog", "/tmp/it's:here/c.cube");
        args[Array.IndexOf(args, "-vf") + 1].Should().Contain(@"lut3d=file='/tmp/it'\''s\:here/c.cube':interp=tetrahedral");
    }

    [Fact]
    public void Range_and_matrix_come_from_each_profile_never_from_the_file()
    {
        foreach (CameraProfileDefinition profile in CameraProfiles.All)
        {
            string[] args = ProxyArgvBuilder.Build("/in", "/out", 540, 15, false, 2, false, null, profile.Id, "/s/c.cube");
            string vf = args[Array.IndexOf(args, "-vf") + 1];
            string range = profile.ExpectedRange == "full" ? "pc" : "tv";
            string matrix = profile.ExpectedYuvMatrix == "bt2020nc" ? "2020_ncl" : "709";
            vf.Should().Contain($",zscale=rin={range}:min={matrix}:tin=709:pin=709:t=709:p=709,format=gbrpf32le,", profile.Id);
            vf.Should().StartWith("scale=", profile.Id);
        }
    }

    [Fact]
    public void Without_zscale_the_prefix_is_a_plain_scale_with_the_profiles_range_and_matrix()
    {
        string[] args = ProxyArgvBuilder.Build("/in", "/out", 540, 15, false, 2, false, null, "appleLog", "/s/c.cube", zscaleAvailable: false);
        args[Array.IndexOf(args, "-vf") + 1].Should().Contain(
            ",scale=in_range=tv:in_color_matrix=bt2020:flags=accurate_rnd+full_chroma_int,format=gbrpf32le,lut3d=file='/s/c.cube'");
    }

    [Fact]
    public void An_hdr_source_or_an_unknown_profile_is_refused_by_the_builder()
    {
        Action hdr = () => ProxyArgvBuilder.Build("/in", "/out", 540, 15, false, 2, false, "hlg", "appleLog", "/s/c.cube");
        hdr.Should().Throw<ArgumentException>();
        Action unknown = () => ProxyArgvBuilder.Build("/in", "/out", 540, 15, false, 2, false, null, "nope", "/s/c.cube");
        unknown.Should().Throw<ArgumentException>();
    }

    // ── spec identity ────────────────────────────────────────────────────────

    [Fact]
    public void A_converted_spec_differs_from_plain_and_between_profiles_and_carries_the_recipe_token()
    {
        MediaDerivativeSpec plain = MediaDerivativeSpec.ForProxy(1080, 30, 1, true, 4);
        MediaDerivativeSpec a = MediaDerivativeSpec.ForProxy(1080, 30, 1, true, 4, cameraProfileVariant: 1);
        MediaDerivativeSpec b = MediaDerivativeSpec.ForProxy(1080, 30, 1, true, 4, cameraProfileVariant: 4);

        plain.Variant.Should().Be(0);
        plain.ParamsJson.Should().NotContain("camera-profile");
        plain.ParamsJson.Should().Be(MediaDerivativeSpec.ForProxy(1080, 30, 1, true, 4, 0, 0).ParamsJson);
        a.Variant.Should().Be(1);
        b.Variant.Should().Be(4);
        a.ParamsJson.Should().Contain("\"recipe\":\"camera-profile-v1:appleLog:legal:bt2020nc\"");
        new[] { plain, a, b }.Select(MediaDerivativeCacheKey.ComputeParamsHash).Distinct().Should().HaveCount(3);
        MediaDerivativeSpec.ForProxy(1920, 30, 1, true, 4, 1080, 1).ParamsJson.Should().Contain("camera-profile-v1:appleLog:legal:bt2020nc");
    }

    // ── dispatcher plan ──────────────────────────────────────────────────────

    private static readonly MediaProbeResult Sdr = new(5, 30, 1, 1920, 1080, "h264", "aac", 48000);
    private static readonly MediaProbeResult Hlg = Sdr with { ColorTransfer = "arib-std-b67" };

    private static IReadOnlyList<(MediaDerivativeKindSlug Slug, int Variant)> Plan() =>
        MediaDerivativeDispatcher.ResolveSpecPlan("video/mp4", null);

    [Fact]
    public void The_plan_is_unchanged_without_a_request_or_a_detector()
    {
        MediaDerivativeDispatcher.WithConvertedProxy(Plan(), Sdr, 0, null).Should().Equal(Plan());
        MediaDerivativeDispatcher.WithConvertedProxy(Plan(), Sdr, 0, _ => null).Should().Equal(Plan());
    }

    [Fact]
    public void A_requested_variant_adds_the_converted_proxy_right_after_the_plain_one()
    {
        var plan = MediaDerivativeDispatcher.WithConvertedProxy(Plan(), Sdr, 4, null);
        plan.Should().HaveCount(Plan().Count + 1);
        int plain = plan.ToList().IndexOf((MediaDerivativeKindSlug.Proxy, 0));
        plan[plain + 1].Should().Be((MediaDerivativeKindSlug.Proxy, 4));
    }

    [Fact]
    public void A_detector_hint_adds_the_converted_proxy_and_a_fake_is_enough_to_test_it()
    {
        var plan = MediaDerivativeDispatcher.WithConvertedProxy(Plan(), Sdr, 0, _ => "arriLogC3");
        plan.Should().Contain((MediaDerivativeKindSlug.Proxy, CameraProfileVariants.VariantOf("arriLogC3")));
        MediaDerivativeDispatcher.WithConvertedProxy(Plan(), Sdr, 0, _ => "unknownId").Should().Equal(Plan());
    }

    [Fact]
    public void An_hdr_source_or_a_plan_without_a_plain_proxy_never_gets_one()
    {
        MediaDerivativeDispatcher.WithConvertedProxy(Plan(), Hlg, 4, _ => "arriLogC3").Should().Equal(Plan());
        var peaksOnly = MediaDerivativeDispatcher.ResolveSpecPlan("video/mp4", [MediaDerivativeKind.WaveformPeaks]);
        MediaDerivativeDispatcher.WithConvertedProxy(peaksOnly, Sdr, 4, null).Should().Equal(peaksOnly);
    }

    /// <summary>The High signature from the detector's own tests: Apple, iPhone 15 Pro class, ProRes, BT.2020, untagged transfer.</summary>
    private static readonly MediaProbeResult AppleLogHigh = Sdr with
    {
        VideoCodec = "prores",
        PixFmt = "yuv422p10le",
        BitDepth = 10,
        ColorPrimaries = "bt2020",
        ColorSpace = "bt2020nc",
        Make = "Apple",
        Model = "iPhone 15 Pro"
    };

    [Fact]
    public void The_real_detector_auto_builds_the_converted_proxy_only_at_high_confidence()
    {
        var plan = MediaDerivativeDispatcher.WithConvertedProxy(
            Plan(), AppleLogHigh, 0, MediaDerivativeDispatcher.AutoProfileFromProbe);
        plan.Should().Contain((MediaDerivativeKindSlug.Proxy, CameraProfileVariants.VariantOf("appleLog")));

        // The detector's Apple HEVC rule is Medium: a suggestion the user accepts, never an unrequested encode.
        MediaProbeResult medium = AppleLogHigh with { VideoCodec = "hevc" };
        MediaDerivativeDispatcher.WithConvertedProxy(
                Plan(), medium, 0, MediaDerivativeDispatcher.AutoProfileFromProbe)
            .Should().Equal(Plan());

        MediaDerivativeDispatcher.WithConvertedProxy(
                Plan(), AppleLogHigh with { ColorTransfer = "arib-std-b67" }, 0, MediaDerivativeDispatcher.AutoProfileFromProbe)
            .Should().Equal(Plan());
    }

    [Fact]
    public void An_explicit_request_wins_over_the_detector()
    {
        var plan = MediaDerivativeDispatcher.WithConvertedProxy(
            Plan(), AppleLogHigh, 4, MediaDerivativeDispatcher.AutoProfileFromProbe);
        plan.Should().Contain((MediaDerivativeKindSlug.Proxy, 4));
        plan.Should().NotContain((MediaDerivativeKindSlug.Proxy, CameraProfileVariants.VariantOf("appleLog")));
    }

    [Fact]
    public void The_production_registration_supplies_the_detector_to_the_dispatcher()
    {
        var services = new ServiceCollection();
        services.AddMediaDerivativeAutoProfile();
        using ServiceProvider provider = services.BuildServiceProvider();

        provider.GetRequiredService<Func<MediaProbeResult, string?>>()
            .Should().Be(MediaDerivativeDispatcher.AutoProfileFromProbe);
    }

    // ── real ffmpeg ──────────────────────────────────────────────────────────

    [ZscaleLutFact]
    public void A_log_ramp_through_the_converted_proxy_lands_on_the_expected_grey_and_keeps_the_levels()
    {
        const string id = "sonySLog3";
        CameraProfiles.TryGet(id, out CameraProfileDefinition? profile).Should().BeTrue();

        // 10-bit full-range 4:4:4, UNTAGGED: the top half is an S-Log3 18% grey patch (code 420/1023), the bottom half a
        // ramp over the code range 95..795 (black .. well above the shoulder). The profile, not the file, says "full".
        string source = Path.Combine(_work, "slog3.mkv");
        Run("ffmpeg", ["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i",
            "color=c=black:s=1024x576:r=10:d=1,format=yuv444p10le,geq=lum='if(lt(Y,H/2),420,95+X/W*700)':cb=512:cr=512",
            "-c:v", "ffv1", source]);

        string cube = Path.Combine(_work, "camera-profile.cube");
        File.WriteAllText(cube, CameraProfileCube.Write(id));
        string output = Path.Combine(_work, "proxy.mp4");
        string[] args = ProxyArgvBuilder.Build(source, output, 540, 5, false, 2, false, null, id, cube);
        Run("ffmpeg", args);

        byte[] grey = Row(output, 135);
        byte[] ramp = Row(output, 405);

        (double r, _, _) = CameraDisplayTransform.ToRec709(profile!, 420 / 1023.0, 420 / 1023.0, 420 / 1023.0);
        double expected = 16 + 219 * r;
        double measured = grey.Skip(grey.Length / 4).Take(grey.Length / 2).Average(b => (double)b);
        measured.Should().BeInRange(expected - 6, expected + 6, "the mid-grey patch lands on the profile's legal 8-bit value");

        int levels = ramp.Where(b => b is >= 64 and <= 192).Distinct().Count();
        levels.Should().BeGreaterThanOrEqualTo(100, "baking the conversion keeps the 64..192 band populated");
        Console.WriteLine($"converted proxy: grey measured {measured:F1} expected {expected:F1}; distinct levels in 64..192: {levels}");

        // The plain proxy of the same clip is untouched by all of this: its argv has no conversion stage (pinned above).
        string plainOut = Path.Combine(_work, "plain.mp4");
        Run("ffmpeg", ProxyArgvBuilder.Build(source, plainOut, 540, 5, false, 2));
        Row(plainOut, 405).Should().NotEqual(ramp, "the plain proxy carries no conversion");
    }

    private byte[] Row(string video, int y)
    {
        string raw = Path.Combine(_work, "row-" + Guid.NewGuid().ToString("N") + ".gray");
        Run("ffmpeg", ["-hide_banner", "-loglevel", "error", "-y", "-i", video, "-vf", $"format=gray,crop=iw:1:0:{y}", "-frames:v", "1", "-f", "rawvideo", raw]);
        return File.ReadAllBytes(raw);
    }

    private static string Run(string tool, IEnumerable<string> args)
    {
        ProcessStartInfo info = new(tool) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string arg in args) info.ArgumentList.Add(arg);
        using Process process = Process.Start(info)!;
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        string stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit(120_000);
        process.ExitCode.Should().Be(0, $"{tool} failed: {stderr.Result}");
        return stdout;
    }
}

/// <summary>A fact that needs an ffmpeg with <c>zscale</c>, <c>lut3d</c> and libx264 (skipped, with the reason, without).</summary>
public sealed class ZscaleLutFactAttribute : FactAttribute
{
    public ZscaleLutFactAttribute()
    {
        try
        {
            ProcessStartInfo info = new("ffmpeg") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            info.ArgumentList.Add("-hide_banner");
            info.ArgumentList.Add("-filters");
            using Process process = Process.Start(info)!;
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(30_000);
            HashSet<string> names = output.Split('\n')
                .Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .Where(p => p.Length >= 2).Select(p => p[1]).ToHashSet(StringComparer.Ordinal);
            if (!names.Contains("zscale") || !names.Contains("lut3d") || !names.Contains("geq"))
                Skip = "This host's ffmpeg lacks the zscale/lut3d/geq filters.";
        }
        catch (Exception)
        {
            Skip = "Requires an ffmpeg binary on PATH.";
        }
    }
}
