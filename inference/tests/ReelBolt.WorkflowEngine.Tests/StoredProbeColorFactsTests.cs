using AwesomeAssertions;
using ReelBolt.Inference.Api.Services.Editing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

public class StoredProbeColorFactsTests
{
    [Fact]
    public void Old_stored_json_without_new_fields_parses_with_nulls_and_no_hint()
    {
        MediaSourceProbe? p = MediaSourceProbe.TryParse("{\"durationSec\":1,\"fpsNum\":30,\"fpsDen\":1,\"width\":10,\"height\":10,\"hasAudio\":false}");
        p.Should().NotBeNull();
        p!.ColorSpace.Should().BeNull();
        p.Make.Should().BeNull();
        p.BitDepth.Should().BeNull();
        StoredMediaProbes.ToResponse(p).CameraProfileHint.Should().BeNull();
    }

    [Fact]
    public void Facts_round_trip_through_stored_json_and_the_response_carries_the_hint()
    {
        MediaProbeResult src = new(1, 24, 1, 3840, 2160, "prores", null, null, "yuv422p10le", null, "mov", null, null,
            null, "bt2020", "bt2020nc", false, "tv", 10, "Apple", "iPhone 15 Pro", "17.2");
        string json = MediaDerivativeDispatcher.ToStoredProbeJson(src);
        json.Should().Contain("\"make\":\"Apple\"").And.Contain("\"colorSpace\":\"bt2020nc\"").And.Contain("\"pixFmt\":\"yuv422p10le\"");

        MediaSourceProbe back = MediaSourceProbe.TryParse(json)!;
        back.Model.Should().Be("iPhone 15 Pro");
        back.BitDepth.Should().Be(10);
        back.ColorRange.Should().Be("tv");

        var resp = StoredMediaProbes.ToResponse(back);
        resp.Make.Should().Be("Apple");
        resp.CameraProfileHint.Should().NotBeNull();
        resp.CameraProfileHint!.Profile.Should().Be("appleLog");
        resp.CameraProfileHint.Confidence.Should().Be("high");
        resp.CameraProfileHint.Reason.Should().Be("apple_make_model_bt2020_prores");
    }
}
