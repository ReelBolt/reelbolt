using AwesomeAssertions;
using ReelBolt.Shared.Inference;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>The ledger string a restarted attempt resumes from: round trip, the 200-character column, and no guessed handle.</summary>
public sealed class VideoEnhanceHandleCodecTests
{
    private static readonly VideoEnhanceJobHandle Normal = new(
        "1f4c9e6a-0000-4000-8000-123456789abc",
        "https://queue.fal.run/fal-ai/topaz/upscale/video/requests/1f4c9e6a-0000-4000-8000-123456789abc/status",
        "https://queue.fal.run/fal-ai/topaz/upscale/video/requests/1f4c9e6a-0000-4000-8000-123456789abc");

    [Fact]
    public void A_handle_round_trips_and_fits_the_ledger_column()
    {
        string? stored = VideoEnhanceHandleCodec.Encode(Normal);
        stored.Should().NotBeNull();
        stored!.Length.Should().BeLessThanOrEqualTo(VideoEnhanceHandleCodec.MaxLength);
        VideoEnhanceHandleCodec.Decode(stored).Should().Be(Normal);
    }

    [Fact]
    public void A_response_url_that_is_not_the_status_url_without_its_suffix_is_carried_in_full()
    {
        VideoEnhanceJobHandle odd = new("r1", "https://queue.fal.run/a/requests/r1/status", "https://queue.fal.run/other/r1");
        string stored = VideoEnhanceHandleCodec.Encode(odd)!;
        VideoEnhanceHandleCodec.Decode(stored).Should().Be(odd);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("just-an-id")]
    [InlineData("id|http://insecure.example/status")]
    [InlineData("id|https://queue.fal.run/a/requests/id")]
    [InlineData("|https://queue.fal.run/a/requests/id/status")]
    [InlineData("id|https://queue.fal.run/a|https://x|extra")]
    public void A_string_that_is_not_a_handle_decodes_to_null(string? stored) =>
        VideoEnhanceHandleCodec.Decode(stored).Should().BeNull();

    [Fact]
    public void A_handle_with_no_https_url_or_too_long_does_not_encode()
    {
        VideoEnhanceHandleCodec.Encode(new VideoEnhanceJobHandle("r", "http://x/status", "http://x")).Should().BeNull();
        VideoEnhanceHandleCodec.Encode(new VideoEnhanceJobHandle("", "https://x/status", "https://x")).Should().BeNull();
        VideoEnhanceHandleCodec.Encode(new VideoEnhanceJobHandle("a|b", "https://x/status", "https://x")).Should().BeNull();
        string longUrl = "https://queue.fal.run/" + new string('p', 200);
        VideoEnhanceHandleCodec.Encode(new VideoEnhanceJobHandle("r", longUrl + "/status", longUrl)).Should().BeNull();
    }
}
