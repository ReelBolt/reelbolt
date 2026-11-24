using System.Text.Json.Nodes;
using AwesomeAssertions;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>Per-insert <c>audio {mixed, gainDb}</c> on <c>inserts.appliedInserts[]</c> (docs/video-editing.md "Sound from an inserted asset").</summary>
public class InsertAudioReportTests
{
    private static JsonObject Inserts() => new()
    {
        ["appliedInserts"] = new JsonArray(
            new JsonObject { ["regionId"] = "r1" },
            new JsonObject { ["regionId"] = "r2" },
            new JsonObject { ["regionId"] = "r3" })
    };

    private static JsonObject Audio() => new()
    {
        ["gainDb"] = -3,
        ["regions"] = new JsonArray(
            new JsonObject { ["regionId"] = "r1", ["mixed"] = true },
            new JsonObject { ["regionId"] = "r2", ["mixed"] = false, ["reason"] = "no_audio_stream" })
    };

    [Fact]
    public void Each_applied_insert_reports_whether_its_sound_was_mixed_and_at_what_gain()
    {
        JsonObject inserts = Inserts();

        InsertAudioReport.Annotate(inserts, Audio());

        JsonArray applied = inserts["appliedInserts"]!.AsArray();
        applied[0]!["audio"]!["mixed"]!.GetValue<bool>().Should().BeTrue();
        applied[0]!["audio"]!["gainDb"]!.GetValue<int>().Should().Be(-3);
        applied[1]!["audio"]!["mixed"]!.GetValue<bool>().Should().BeFalse();
        applied[1]!["audio"]!["reason"]!.GetValue<string>().Should().Be("no_audio_stream");
    }

    [Fact]
    public void An_insert_the_audio_report_does_not_mention_is_reported_not_mixed()
    {
        JsonObject inserts = Inserts();

        InsertAudioReport.Annotate(inserts, Audio());

        inserts["appliedInserts"]![2]!["audio"]!["mixed"]!.GetValue<bool>().Should().BeFalse();
        inserts["appliedInserts"]![2]!["audio"]!["reason"]!.GetValue<string>().Should().Be("not_reported");
    }

    [Fact]
    public void An_unavailable_mix_is_reported_on_every_insert()
    {
        JsonObject inserts = Inserts();
        var unavailable = new JsonObject { ["gainDb"] = 0, ["unavailable"] = true, ["regions"] = new JsonArray() };

        InsertAudioReport.Annotate(inserts, unavailable);

        inserts["appliedInserts"]![0]!["audio"]!["reason"]!.GetValue<string>().Should().Be("unavailable");
    }
}
