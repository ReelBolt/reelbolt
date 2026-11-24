using System;
using System.Text.Json;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>#772 QA: stored source probes of old rotated uploads are refreshed, unrotated rows do not churn.</summary>
public sealed class StoredProbeRevisionTests
{
    private static MediaProbeResult Probe(int w, int h) =>
        new(2, 30, 1, w, h, "h264", "aac", 48000);

    [Fact]
    public void A_fresh_stored_probe_carries_the_revision_and_still_parses_as_a_media_source_probe()
    {
        string json = MediaDerivativeDispatcher.ToStoredProbeJson(Probe(1080, 1920));

        JsonDocument.Parse(json).RootElement.GetProperty("probeRev").GetInt32().Should().Be(MediaDerivativeDispatcher.ProbeRevision);
        MediaSourceProbe.TryParse(json)!.Width.Should().Be(1080);
    }

    [Fact]
    public void A_row_without_the_marker_is_replaced_even_at_the_same_size()
    {
        string fresh = MediaDerivativeDispatcher.ToStoredProbeJson(Probe(1920, 1080));
        string legacy = MediaDerivativeDispatcher.ToStoredProbe(Probe(1920, 1080)).ToJson();

        MediaDerivativeDispatcher.ShouldReplaceStoredProbe(legacy, fresh).Should().BeTrue();
    }

    [Fact]
    public void An_old_rotated_row_keeping_the_coded_size_is_replaced_by_the_display_size()
    {
        string legacy = MediaDerivativeDispatcher.ToStoredProbe(Probe(1920, 1080)).ToJson();
        string fresh = MediaDerivativeDispatcher.ToStoredProbeJson(Probe(1080, 1920));

        MediaDerivativeDispatcher.ShouldReplaceStoredProbe(legacy, fresh).Should().BeTrue();
    }

    [Fact]
    public void A_current_row_with_the_same_size_is_not_rewritten()
    {
        string fresh = MediaDerivativeDispatcher.ToStoredProbeJson(Probe(1920, 1080));

        MediaDerivativeDispatcher.ShouldReplaceStoredProbe(fresh, fresh).Should().BeFalse();
    }

    [Fact]
    public void A_current_row_with_a_different_size_is_replaced()
    {
        string stored = MediaDerivativeDispatcher.ToStoredProbeJson(Probe(1920, 1080));
        string fresh = MediaDerivativeDispatcher.ToStoredProbeJson(Probe(1080, 1920));

        MediaDerivativeDispatcher.ShouldReplaceStoredProbe(stored, fresh).Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"probeRev\":\"x\"}")]
    public void A_missing_or_unreadable_row_is_replaced(string? stored)
    {
        MediaDerivativeDispatcher.ShouldReplaceStoredProbe(stored, MediaDerivativeDispatcher.ToStoredProbeJson(Probe(10, 10)))
            .Should().BeTrue();
    }

    [Fact]
    public void The_reader_tolerates_probeRev_and_returns_the_same_fields()
    {
        MediaProbeResult probe = Probe(1080, 1920);
        MediaSourceProbe plain = MediaSourceProbe.TryParse(MediaDerivativeDispatcher.ToStoredProbe(probe).ToJson())!;
        MediaSourceProbe withRev = MediaSourceProbe.TryParse(MediaDerivativeDispatcher.ToStoredProbeJson(probe))!;

        withRev.Should().Be(plain);
    }

    [Fact]
    public void A_current_revision_video_probe_without_the_colour_facts_is_replaced_but_a_fresh_one_never_churns()
    {
        string fresh = MediaDerivativeDispatcher.ToStoredProbeJson(Probe(1920, 1080));
        var old = System.Text.Json.Nodes.JsonNode.Parse(fresh)!.AsObject();
        foreach (string key in new[] { "make", "model", "bitDepth", "pixFmt" })
            old.Remove(key);

        MediaDerivativeDispatcher.ShouldReplaceStoredProbe(old.ToJsonString(), fresh).Should().BeTrue("detection is unknown without make/model/bit depth");
        MediaDerivativeDispatcher.ShouldReplaceStoredProbe(fresh, fresh).Should().BeFalse();

        var audioOnly = System.Text.Json.Nodes.JsonNode.Parse(MediaDerivativeDispatcher.ToStoredProbeJson(new(2, 0, 0, 0, 0, null, "aac", 48000)))!.AsObject();
        audioOnly.Remove("make");
        MediaDerivativeDispatcher.ShouldReplaceStoredProbe(audioOnly.ToJsonString(), audioOnly.ToJsonString()).Should().BeFalse("an audio probe has no colour facts to refresh");
    }
}
