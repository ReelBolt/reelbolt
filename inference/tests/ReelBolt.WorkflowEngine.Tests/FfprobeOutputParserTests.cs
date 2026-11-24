using System.IO;
using System;
using AwesomeAssertions;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Covers <see cref="FfprobeOutputParser"/> against captured/constructed fixture JSON — no real
/// ffprobe process is required (plan §WS2 DoD).
/// </summary>
public class FfprobeOutputParserTests
{
    private const string FixtureJson = """
        {
          "streams": [
            {
              "codec_type": "video",
              "codec_name": "h264",
              "width": 1920,
              "height": 1080,
              "r_frame_rate": "30000/1001",
              "duration": "184.320000"
            },
            {
              "codec_type": "audio",
              "codec_name": "aac",
              "sample_rate": "48000"
            }
          ],
          "format": {
            "duration": "184.320000"
          }
        }
        """;

    [Fact]
    public void Parse_extracts_duration_dimensions_and_codecs()
    {
        MediaProbeResult result = FfprobeOutputParser.Parse(FixtureJson);

        result.DurationSec.Should().Be(184.32);
        result.Width.Should().Be(1920);
        result.Height.Should().Be(1080);
        result.VideoCodec.Should().Be("h264");
        result.AudioCodec.Should().Be("aac");
        result.AudioSampleRate.Should().Be(48000);
    }

    [Fact]
    public void Parse_reads_fps_as_exact_rational_without_collapsing_to_double()
    {
        MediaProbeResult result = FfprobeOutputParser.Parse(FixtureJson);

        // 30000/1001 (NTSC ~29.97fps) must survive as an exact numerator/denominator pair (R9) —
        // never rounded to a lossy double like 29.97.
        result.FpsNum.Should().Be(30000);
        result.FpsDen.Should().Be(1001);
    }

    [Fact]
    public void Parse_falls_back_to_stream_duration_when_format_duration_is_absent()
    {
        const string json = """
            {
              "streams": [
                { "codec_type": "video", "codec_name": "h264", "width": 640, "height": 480,
                  "r_frame_rate": "30/1", "duration": "10.5" }
              ],
              "format": {}
            }
            """;

        MediaProbeResult result = FfprobeOutputParser.Parse(json);

        result.DurationSec.Should().Be(10.5);
        result.FpsNum.Should().Be(30);
        result.FpsDen.Should().Be(1);
    }

    [Fact]
    public void Parse_handles_a_video_only_stream_with_no_audio()
    {
        const string json = """
            {
              "streams": [
                { "codec_type": "video", "codec_name": "vp9", "width": 1280, "height": 720,
                  "r_frame_rate": "24/1" }
              ],
              "format": { "duration": "5.0" }
            }
            """;

        MediaProbeResult result = FfprobeOutputParser.Parse(json);

        result.AudioCodec.Should().BeNull();
        result.AudioSampleRate.Should().BeNull();
        result.VideoCodec.Should().Be("vp9");
    }

    [Theory]
    [InlineData("30000/1001", 30000, 1001)]
    [InlineData("30/1", 30, 1)]
    [InlineData("25/1", 25, 1)]
    public void TryParseRational_parses_valid_rationals_exactly(string raw, int expectedNum, int expectedDen)
    {
        bool ok = FfprobeOutputParser.TryParseRational(raw, out int num, out int den);

        ok.Should().BeTrue();
        num.Should().Be(expectedNum);
        den.Should().Be(expectedDen);
    }

    [Theory]
    [InlineData("30/0")]
    [InlineData("not-a-rational")]
    [InlineData("30")]
    [InlineData("")]
    public void TryParseRational_rejects_malformed_or_zero_denominator_values(string raw)
    {
        bool ok = FfprobeOutputParser.TryParseRational(raw, out _, out _);

        ok.Should().BeFalse();
    }

    [Fact]
    public void Parse_extracts_pix_fmt_from_the_video_stream()
    {
        const string json = """
            {
              "streams": [
                { "codec_type": "video", "codec_name": "vp9", "pix_fmt": "yuva420p", "width": 640, "height": 360,
                  "r_frame_rate": "30/1" }
              ],
              "format": { "duration": "1.5" }
            }
            """;

        MediaProbeResult result = FfprobeOutputParser.Parse(json);

        result.PixFmt.Should().Be("yuva420p");
    }

    [Fact]
    public void Parse_leaves_pix_fmt_null_when_the_fixture_has_none()
    {
        // FixtureJson (above) never sets pix_fmt — the common case for an ffprobe JSON stream
        // object where alpha is not a concern (e.g. plain source footage).
        MediaProbeResult result = FfprobeOutputParser.Parse(FixtureJson);

        result.PixFmt.Should().BeNull();
    }

    [Fact]
    public void Parse_extracts_the_alpha_mode_tag_from_a_real_vp9_webm_fixture()
    {
        // Captured verbatim from a real ffprobe run against a genuinely transparent VP9/WebM
        // render — pix_fmt reads back as the plain yuv420p base-plane format, and the real alpha
        // signal lives only in tags.alpha_mode. See AlphaPixelFormats.HasAlpha.
        const string json = """
            {
              "streams": [
                { "codec_type": "video", "codec_name": "vp9", "pix_fmt": "yuv420p", "width": 400, "height": 400,
                  "r_frame_rate": "30/1",
                  "tags": { "alpha_mode": "1", "ENCODER": "Lavc61.19.100 libvpx-vp9" } }
              ],
              "format": { "duration": "0.166" }
            }
            """;

        MediaProbeResult result = FfprobeOutputParser.Parse(json);

        result.PixFmt.Should().Be("yuv420p");
        result.AlphaModeTag.Should().Be("1");
    }

    [Fact]
    public void Parse_leaves_alpha_mode_tag_null_when_the_video_stream_has_no_tags_object()
    {
        MediaProbeResult result = FfprobeOutputParser.Parse(FixtureJson);

        result.AlphaModeTag.Should().BeNull();
    }

    [Theory]
    [InlineData("ALPHA_MODE")]
    [InlineData("alpha_mode")]
    [InlineData("Alpha_Mode")]
    public void Parse_reads_the_alpha_mode_tag_case_insensitively(string tagKey)
    {
        // Regression (#340): stream tags are muxer-written free text. A plain stream-copy remux of
        // a transparent VP9/WebM (`ffmpeg -i a.webm -c copy b.webm`) reports ONLY
        // tags.ALPHA_MODE="1"; the exact lowercase lookup missed it and the overlay was dropped.
        string json = $$"""
            {
              "streams": [
                { "codec_type": "video", "codec_name": "vp9", "pix_fmt": "yuv420p", "width": 64, "height": 64,
                  "r_frame_rate": "25/1",
                  "tags": { "{{tagKey}}": "1", "ENCODER": "Lavf62.3.100" } }
              ],
              "format": { "duration": "0.5" }
            }
            """;

        MediaProbeResult result = FfprobeOutputParser.Parse(json);

        result.AlphaModeTag.Should().Be("1");
    }

    [Fact]
    public void Parse_prefers_the_exact_lowercase_alpha_mode_key_when_both_cases_are_present()
    {
        const string json = """
            {
              "streams": [
                { "codec_type": "video", "codec_name": "vp9", "pix_fmt": "yuv420p", "width": 64, "height": 64,
                  "r_frame_rate": "25/1",
                  "tags": { "ALPHA_MODE": "1", "alpha_mode": "0" } }
              ],
              "format": { "duration": "0.5" }
            }
            """;

        MediaProbeResult result = FfprobeOutputParser.Parse(json);

        result.AlphaModeTag.Should().Be("0");
    }

    [Fact]
    public void Parse_ignores_a_non_object_tags_value()
    {
        const string json = """
            {
              "streams": [
                { "codec_type": "video", "codec_name": "vp9", "pix_fmt": "yuv420p", "width": 64, "height": 64,
                  "r_frame_rate": "25/1",
                  "tags": [] }
              ],
              "format": { "duration": "0.5" }
            }
            """;

        MediaProbeResult result = FfprobeOutputParser.Parse(json);

        result.AlphaModeTag.Should().BeNull();
        result.PixFmt.Should().Be("yuv420p");
    }

    [Fact]
    public void Parse_ignores_a_non_string_alpha_mode_tag_value_in_any_case()
    {
        const string json = """
            {
              "streams": [
                { "codec_type": "video", "codec_name": "vp9", "pix_fmt": "yuv420p", "width": 64, "height": 64,
                  "r_frame_rate": "25/1",
                  "tags": { "ALPHA_MODE": 1 } }
              ],
              "format": { "duration": "0.5" }
            }
            """;

        MediaProbeResult result = FfprobeOutputParser.Parse(json);

        result.AlphaModeTag.Should().BeNull();
    }

    // ---------------------------------------------------------------------------------------------
    // Container / audio channels / bitrate (the probed-media payload's three additional fields)
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Parse_extracts_container_bitrate_and_audio_channels()
    {
        const string json = """
            {
              "streams": [
                { "codec_type": "video", "codec_name": "h264", "width": 1920, "height": 1080,
                  "r_frame_rate": "30/1" },
                { "codec_type": "audio", "codec_name": "aac", "sample_rate": "48000", "channels": 2 }
              ],
              "format": {
                "format_name": "mov,mp4,m4a,3gp,3g2,mj2",
                "duration": "12.5",
                "bit_rate": "3541200"
              }
            }
            """;

        MediaProbeResult result = FfprobeOutputParser.Parse(json);

        // Verbatim: a comma-separated format_name is ffprobe's PROBE list (the container was
        // sniffed, not read from a header). Splitting it here would invent a canonical name this
        // parser has no authority to choose, and the list cannot be recovered once lost.
        result.Container.Should().Be("mov,mp4,m4a,3gp,3g2,mj2");
        result.BitrateBps.Should().Be(3541200L);
        result.AudioChannels.Should().Be(2);
    }

    [Fact]
    public void Parse_reads_channels_from_a_string_valued_field_as_well_as_a_number()
    {
        // ffprobe writes `channels` as a JSON number and `sample_rate`/`bit_rate` as JSON strings,
        // but the shape of any one field is not contractual across ffprobe versions — both
        // encodings of the same text must read identically.
        const string json = """
            {
              "streams": [
                { "codec_type": "audio", "codec_name": "aac", "sample_rate": "44100", "channels": "1" }
              ],
              "format": { "duration": "3.0", "bit_rate": 128000 }
            }
            """;

        MediaProbeResult result = FfprobeOutputParser.Parse(json);

        result.AudioChannels.Should().Be(1);
        result.BitrateBps.Should().Be(128000L);
    }

    [Fact]
    public void Parse_leaves_the_three_new_fields_null_when_ffprobe_omits_them()
    {
        // FixtureJson (above) carries neither format.format_name, format.bit_rate nor a stream
        // `channels` — the pre-existing probe shape. Absent must read as NOT KNOWN (null), never as
        // 0 channels / 0 bit/s, which would state something ffprobe never measured.
        MediaProbeResult result = FfprobeOutputParser.Parse(FixtureJson);

        result.Container.Should().BeNull();
        result.AudioChannels.Should().BeNull();
        result.BitrateBps.Should().BeNull();
    }

    [Theory]
    [InlineData("N/A")]
    [InlineData("")]
    [InlineData("unknown")]
    [InlineData("1.5e6")]     // not an integer bit rate
    [InlineData("0")]         // a non-positive reading is not a fact worth carrying
    [InlineData("-1")]
    public void Parse_treats_a_malformed_bit_rate_as_null_and_never_throws(string rawBitRate)
    {
        string json = $$"""
            {
              "streams": [ { "codec_type": "video", "codec_name": "h264", "width": 640, "height": 360,
                             "r_frame_rate": "30/1" } ],
              "format": { "duration": "2.0", "format_name": "matroska,webm", "bit_rate": "{{rawBitRate}}" }
            }
            """;

        MediaProbeResult result = FfprobeOutputParser.Parse(json);

        result.BitrateBps.Should().BeNull();
        result.Container.Should().Be("matroska,webm");
    }

    [Theory]
    [InlineData("N/A")]
    [InlineData("")]
    [InlineData("stereo")]
    [InlineData("0")]
    public void Parse_treats_a_malformed_channels_value_as_null_and_never_throws(string rawChannels)
    {
        string json = $$"""
            {
              "streams": [
                { "codec_type": "audio", "codec_name": "aac", "sample_rate": "48000", "channels": "{{rawChannels}}" }
              ],
              "format": { "duration": "2.0" }
            }
            """;

        MediaProbeResult result = FfprobeOutputParser.Parse(json);

        result.AudioChannels.Should().BeNull();
        // The neighbours on the SAME stream stay intact — a malformed new field must not knock out
        // the fields that already worked.
        result.AudioCodec.Should().Be("aac");
        result.AudioSampleRate.Should().Be(48000);
    }

    [Fact]
    public void Parse_leaves_container_null_when_format_name_is_absent_or_blank()
    {
        const string json = """
            {
              "streams": [ { "codec_type": "video", "codec_name": "h264", "width": 640, "height": 360,
                             "r_frame_rate": "30/1" } ],
              "format": { "duration": "2.0", "format_name": "   " }
            }
            """;

        MediaProbeResult result = FfprobeOutputParser.Parse(json);

        result.Container.Should().BeNull();
        result.BitrateBps.Should().BeNull();
        result.AudioChannels.Should().BeNull();
    }

    // Real ffprobe JSON (engine argv: -show_format -show_streams) of a coded 320x180 clip made with
    // ffmpeg 8.0.1. Each fixture has a .decoded sidecar: the frame size ffmpeg actually decodes
    // (ffprobe of `ffmpeg -i X -frames:v 1` output). The invariant under test: probe == decoded.
    // dm-rot-N = mp4 Display Matrix (-display_rotation N); tag-* = mkv/webm ROTATE tag (not autorotated).
    [Theory]
    [InlineData("dm-rot-0")]
    [InlineData("dm-rot-90")]
    [InlineData("dm-rot-m90")]
    [InlineData("dm-rot-180")]
    [InlineData("dm-rot-270")]
    [InlineData("dm-rot-90.4")]
    [InlineData("dm-rot-90.7")]
    [InlineData("dm-rot-1")]
    [InlineData("tag-mkv")]
    [InlineData("tag-webm")]
    public void Parse_reports_the_size_ffmpeg_decodes(string fixture)
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "TestData", "ffprobe-rotation");
        MediaProbeResult result = FfprobeOutputParser.Parse(File.ReadAllText(Path.Combine(dir, fixture + ".json")));
        string decoded = File.ReadAllText(Path.Combine(dir, fixture + ".decoded")).Trim();

        $"{result.Width}x{result.Height}".Should().Be(decoded);
    }

    [Theory]
    [InlineData("dm-rot-0", "320x180")]
    [InlineData("dm-rot-90", "180x320")]
    [InlineData("dm-rot-m90", "180x320")]
    [InlineData("dm-rot-270", "180x320")]
    [InlineData("dm-rot-90.4", "180x320")]
    [InlineData("dm-rot-180", "320x180")]
    [InlineData("dm-rot-90.7", "320x180")]
    [InlineData("tag-mkv", "320x180")]
    [InlineData("tag-webm", "320x180")]
    public void Parse_swaps_only_when_ffmpeg_transposes(string fixture, string expected)
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "TestData", "ffprobe-rotation");
        MediaProbeResult result = FfprobeOutputParser.Parse(File.ReadAllText(Path.Combine(dir, fixture + ".json")));

        $"{result.Width}x{result.Height}".Should().Be(expected);
    }

    // ffmpeg rotates whenever fabs(theta) > 1 after normalising: -display_rotation 1 is theta 359 and IS rotated
    // (size kept), 0.4 is not. Fixtures are real ffprobe output; the pixel change is ffmpeg 8.0.1's.
    [Theory]
    [InlineData("dm-rot-0", false)]
    [InlineData("dm-rot-1", true)]
    [InlineData("dm-rot-180", true)]
    [InlineData("dm-rot-hflip", true)]
    [InlineData("dm-rot-vflip", true)]
    [InlineData("dm-rot-90", true)]
    [InlineData("tag-mkv", false)]
    public void AltersPixelsOnDecode_follows_ffmpeg_autorotate(string fixture, bool expected)
    {
        string json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "ffprobe-rotation", fixture + ".json"));

        FfprobeOutputParser.AltersPixelsOnDecode(json).Should().Be(expected);
        FfprobeOutputParser.Parse(json).AltersPixelsOnDecode.Should().Be(expected);
    }

    [Fact]
    public void Parse_reads_range_bit_depth_and_sanitised_maker_tags()
    {
        string json = """
            {"streams":[{"codec_type":"video","codec_name":"prores","pix_fmt":"yuv422p10le","color_range":"tv","r_frame_rate":"24/1",
              "tags":{"COM.APPLE.QUICKTIME.MODEL":"  iPhone 15 Pro\u0007 "}}],
             "format":{"tags":{"com.apple.quicktime.make":"Apple","com.apple.quicktime.software":"17.2"}}}
            """;
        MediaProbeResult r = FfprobeOutputParser.Parse(json);
        r.ColorRange.Should().Be("tv");
        r.BitDepth.Should().Be(10);
        r.Make.Should().Be("Apple");
        r.Model.Should().Be("iPhone 15 Pro");
        r.Software.Should().Be("17.2");
    }

    [Fact]
    public void Parse_prefers_bits_per_raw_sample_and_caps_tag_length()
    {
        string json = "{\"streams\":[{\"codec_type\":\"video\",\"pix_fmt\":\"yuv420p\",\"bits_per_raw_sample\":\"12\"}],\"format\":{\"tags\":{\"make\":\"" + new string('A', 200) + "\",\"model\":\"\\u0001\"}}}";
        MediaProbeResult r = FfprobeOutputParser.Parse(json);
        r.BitDepth.Should().Be(12);
        r.Make!.Length.Should().Be(64);
        r.Model.Should().BeNull();
    }

    [Fact]
    public void Parse_without_new_facts_leaves_them_null_and_8bit_pix_fmt_is_8()
    {
        MediaProbeResult r = FfprobeOutputParser.Parse("{\"streams\":[{\"codec_type\":\"video\",\"pix_fmt\":\"yuv420p\"}]}");
        r.ColorRange.Should().BeNull();
        r.BitDepth.Should().Be(8);
        r.Make.Should().BeNull();
        r.Model.Should().BeNull();
        r.Software.Should().BeNull();
    }
}
