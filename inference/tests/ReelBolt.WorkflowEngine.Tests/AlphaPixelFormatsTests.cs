using AwesomeAssertions;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

public class AlphaPixelFormatsTests
{
    [Theory]
    [InlineData("yuva420p")]
    [InlineData("yuva444p10le")]
    [InlineData("yuva422p")]
    [InlineData("rgba")]
    [InlineData("bgra")]
    [InlineData("argb")]
    [InlineData("gbrap")]
    [InlineData("ya8")]
    [InlineData("YUVA420P")]
    public void HasAlpha_recognizes_known_alpha_carrying_formats(string pixFmt)
    {
        AlphaPixelFormats.HasAlpha(pixFmt).Should().BeTrue();
    }

    [Theory]
    [InlineData("yuv420p")]
    [InlineData("yuv444p")]
    [InlineData("h264")]
    [InlineData("nv12")]
    [InlineData("")]
    [InlineData(null)]
    public void HasAlpha_rejects_opaque_or_unknown_formats(string? pixFmt)
    {
        AlphaPixelFormats.HasAlpha(pixFmt).Should().BeFalse();
    }

    [Fact]
    public void HasAlpha_recognizes_webm_vp9_alpha_via_the_alpha_mode_tag_despite_a_plain_pix_fmt()
    {
        // Regression test: found live. A genuinely transparent VP9/WebM render's pix_fmt reads
        // back as the plain yuv420p base-plane format — the real alpha signal lives only in
        // ffprobe's tags.alpha_mode="1" for that stream. Confirmed by round-tripping a rendered
        // asset back through ffmpeg with -pix_fmt rgba, which correctly reconstructed the alpha
        // channel despite pix_fmt alone saying "no alpha".
        AlphaPixelFormats.HasAlpha("yuv420p", alphaModeTag: "1").Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    public void HasAlpha_still_rejects_a_plain_pix_fmt_without_a_positive_alpha_mode_tag(string? alphaModeTag)
    {
        AlphaPixelFormats.HasAlpha("yuv420p", alphaModeTag).Should().BeFalse();
    }

    [Theory]
    [InlineData(" 1")]
    [InlineData("1 ")]
    public void HasAlpha_accepts_an_alpha_mode_tag_of_1_with_surrounding_whitespace(string alphaModeTag)
    {
        AlphaPixelFormats.HasAlpha("yuv420p", alphaModeTag).Should().BeTrue();
    }

    [Theory]
    [InlineData("0")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    [InlineData("true")]
    [InlineData("10")]
    public void HasAlpha_rejects_an_alpha_mode_tag_that_is_not_exactly_1_after_trimming(string? alphaModeTag)
    {
        AlphaPixelFormats.HasAlpha("yuv420p", alphaModeTag).Should().BeFalse();
    }
}
