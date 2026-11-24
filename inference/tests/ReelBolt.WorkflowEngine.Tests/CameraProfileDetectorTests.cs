using AwesomeAssertions;
using ReelBolt.Shared.Editing.Color;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

public class CameraProfileDetectorTests
{
    private static CameraFacts Apple(string? transfer = null, string? codec = "prores", string? matrix = "bt2020nc", string? model = "iPhone 15 Pro") =>
        new(transfer, "bt2020", matrix, "tv", "yuv422p10le", 10, codec, "mov,mp4,m4a,3gp,3g2,mj2", "Apple", model);

    [Fact]
    public void Apple_prores_bt2020_unspecified_transfer_is_high()
    {
        CameraProfileHint h = CameraProfileDetector.Detect(Apple());
        h.Should().Be(new CameraProfileHint("appleLog", DetectionConfidence.High, "apple_make_model_bt2020_prores"));
    }

    [Fact]
    public void Apple_hevc_is_medium_and_prores_without_bt2020_matrix_is_medium()
    {
        CameraProfileDetector.Detect(Apple(codec: "hevc")).Confidence.Should().Be(DetectionConfidence.Medium);
        CameraProfileDetector.Detect(Apple(matrix: null)).Confidence.Should().Be(DetectionConfidence.Medium);
        CameraProfileDetector.Detect(Apple(codec: "hevc")).ReasonCode.Should().Be("apple_make_model_bt2020");
    }

    [Theory]
    [InlineData("iPhone 14 Pro")]
    [InlineData("iPhone 15")]
    [InlineData("iPad Pro")]
    [InlineData(null)]
    public void Apple_models_outside_15_pro_class_are_none(string? model) =>
        CameraProfileDetector.Detect(Apple(model: model)).Confidence.Should().Be(DetectionConfidence.None);

    [Theory]
    [InlineData("iPhone 16 Pro Max")]
    [InlineData("iPhone 17 Pro")]
    public void Apple_later_pro_models_match(string model) =>
        CameraProfileDetector.Detect(Apple(model: model)).ProfileId.Should().Be("appleLog");

    [Theory]
    [InlineData("DJI", null, "djiDLogM", "dji_make_10bit")]
    [InlineData("SONY", "ILCE-7SM3", "sonySLog3", "sony_make_model_10bit")]
    [InlineData("Panasonic", "DC-GH7", "panasonicVLog", "panasonic_make_model_10bit")]
    [InlineData("Canon", "Canon EOS R5 C", "canonCLog3", "canon_make_model_10bit")]
    [InlineData("ARRI", "ALEXA 35", "arriLogC4", "arri_alexa35_make_model")]
    [InlineData("ARRI", "ALEXA Mini", "arriLogC3", "arri_make_10bit")]
    [InlineData("FUJIFILM", "X-H2S", "fujiFLog", "fuji_make_model_10bit")]
    [InlineData("NIKON CORPORATION", "NIKON Z 8", "nikonNLog", "nikon_make_model_10bit")]
    public void Maker_rules_give_medium(string make, string? model, string profile, string reason)
    {
        CameraFacts f = new("bt709", "bt709", "bt709", "pc", "yuv420p10le", null, "hevc", "mov", make, model);
        CameraProfileDetector.Detect(f).Should().Be(new CameraProfileHint(profile, DetectionConfidence.Medium, reason));
    }

    [Theory]
    [InlineData("arib-std-b67")]
    [InlineData("smpte2084")]
    [InlineData("ARIB-STD-B67")]
    public void Hdr_transfer_never_matches(string transfer)
    {
        CameraProfileDetector.Detect(Apple(transfer)).ProfileId.Should().BeNull();
        CameraProfileDetector.Detect(new CameraFacts(transfer, Make: "Sony", Model: "ILCE-7M4", BitDepth: 10)).Confidence.Should().Be(DetectionConfidence.None);
    }

    [Theory]
    [InlineData("bt709")]
    [InlineData("smpte170m")]
    public void Plain_rec709_without_maker_is_none(string transfer) =>
        CameraProfileDetector.Detect(new CameraFacts(transfer, "bt709", "bt709", "tv", "yuv420p", 8, "h264")).Confidence.Should().Be(DetectionConfidence.None);

    [Fact]
    public void Apple_with_plain_bt709_transfer_is_none() =>
        CameraProfileDetector.Detect(Apple("bt709")).Confidence.Should().Be(DetectionConfidence.None);

    [Fact]
    public void Eight_bit_maker_footage_is_none() =>
        CameraProfileDetector.Detect(new CameraFacts(Make: "Sony", Model: "ILCE-7M4", PixFmt: "yuv420p", BitDepth: 8)).Confidence.Should().Be(DetectionConfidence.None);

    [Fact]
    public void Bit_depth_falls_back_to_pix_fmt() =>
        CameraProfileDetector.Detect(new CameraFacts(Make: "dji", PixFmt: "yuv420p10le")).ProfileId.Should().Be("djiDLogM");

    [Fact]
    public void Missing_facts_are_none()
    {
        CameraProfileDetector.Detect(null).Confidence.Should().Be(DetectionConfidence.None);
        CameraProfileDetector.Detect(new CameraFacts()).Confidence.Should().Be(DetectionConfidence.None);
        CameraProfileDetector.Detect(new CameraFacts(Make: "Sony")).Confidence.Should().Be(DetectionConfidence.None);
    }

    [Fact]
    public void Matching_is_case_insensitive_and_trims()
    {
        CameraProfileDetector.Detect(new CameraFacts("Unspecified", "BT2020", "BT2020NC", null, null, 10, "PRORES", null, "  APPLE ", "IPHONE 15 PRO"))
            .ProfileId.Should().Be("appleLog");
    }

    [Theory]
    [InlineData("\0\u0001\u0002")]
    [InlineData("   ")]
    [InlineData("((((((((((((((((((((((((((((((((((((((((((((((((((")]
    public void Junk_tag_values_are_none_and_do_not_throw(string junk)
    {
        CameraProfileDetector.Detect(new CameraFacts(junk, junk, junk, junk, junk, 10, junk, junk, junk, junk, junk)).Confidence.Should().Be(DetectionConfidence.None);
        CameraProfileDetector.Detect(new CameraFacts(BitDepth: 10, Make: new string('x', 5000), Model: junk)).Confidence.Should().Be(DetectionConfidence.None);
    }
}
