using System.Text.Json;
using System.Text.Json.Serialization;
using AwesomeAssertions;
using ReelBolt.Shared.Workflows;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Tests for <see cref="VideoCompileStepConfig"/> backward compatibility.
/// </summary>
public class VideoCompileStepConfigTests
{
    // Mirrors VideoCompileStepExecutor.ConfigJsonOptions exactly: JsonSerializerDefaults.Web
    // (camelCase properties) + JsonStringEnumConverter (PascalCase enum member names).
    private static readonly JsonSerializerOptions ConfigJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public void Deserializing_old_json_blob_without_voiceover_key_yields_default_values()
    {
        // An old JSON blob from before voiceover flags were added to VideoCompileStepConfig.
        // This represents an existing serialized config that must continue to deserialize
        // without error, with the new voiceover flags taking their defaults: EnableVoiceover=false,
        // VoiceoverStepOrder=null.
        string oldJson = """
            {
              "version": 1,
              "decision": {
                "from": "Previous"
              },
              "analysisStepOrder": 1,
              "mode": "Reencode",
              "prePaddingMs": 80,
              "postPaddingMs": 120,
              "minSegmentMs": 250,
              "maxSegments": 200,
              "allowKeyframeSnapping": false,
              "outputFileName": "edited.mp4",
              "videoCodec": "libx264",
              "audioCodec": "aac",
              "crf": 20,
              "preset": "veryfast",
              "registerProjectFile": true,
              "enableGraphics": false,
              "maxOverlays": 20,
              "overlayShortMs": 1500,
              "overlayMediumMs": 3000,
              "overlayHoldMs": 6000,
              "overlayFadeMs": 300,
              "overlayFontSizePct": 5,
              "overlayBoxHeightPct": 16,
              "overlayBoxWidthPct": 82,
              "enableMusic": false,
              "musicTrackProjectFileId": null,
              "musicTrackName": null,
              "musicIntensity": "Normal",
              "musicDuckingMode": "DialogueDelay",
              "musicFitMode": "Stretch",
              "musicGain": 0.0,
              "musicDialogueHeadroomDb": -14.0,
              "musicFadeInMs": 500,
              "musicFadeOutMs": 500,
              "enableColorGrade": false,
              "colorGradePlan": null,
              "enableSfx": false,
              "sfxPlan": null,
              "maxSfxCues": 8,
              "maxSfxCueSeconds": 4.0,
              "sfxSubtleDb": -18,
              "sfxNormalDb": -12,
              "sfxStrongDb": -6,
              "sfxLeadMs": 150,
              "sfxLagMs": 150,
              "sfxFadeOutMs": 120
            }
            """;

        VideoCompileStepConfig? config = JsonSerializer.Deserialize<VideoCompileStepConfig>(
            oldJson, ConfigJsonOptions);

        config.Should().NotBeNull();
        config!.EnableVoiceover.Should().BeFalse();
        config.VoiceoverStepOrder.Should().BeNull();
    }

    [Fact]
    public void MixInsertAudio_and_InsertAudioGainDb_round_trip_and_default_to_off()
    {
        // The two append-only members of "Sound from an inserted asset" (docs/video-editing.md): both
        // survive a serialize/deserialize round trip, and a blob written before they existed
        // deserializes to off/zero — the byte-identical default, where an insert is picture only.
        var configured = new VideoCompileStepConfig(
            Version: 1, Decision: new ExtractInputRef(ExtractInputSource.Previous), AnalysisStepOrder: 1)
        {
            MixInsertAudio = true,
            InsertAudioGainDb = -6
        };

        string json = JsonSerializer.Serialize(configured, ConfigJsonOptions);
        json.Should().Contain("\"mixInsertAudio\":true");
        json.Should().Contain("\"insertAudioGainDb\":-6");

        VideoCompileStepConfig? back = JsonSerializer.Deserialize<VideoCompileStepConfig>(json, ConfigJsonOptions);
        back.Should().NotBeNull();
        back!.MixInsertAudio.Should().BeTrue();
        back.InsertAudioGainDb.Should().Be(-6);

        VideoCompileStepConfig legacy = JsonSerializer.Deserialize<VideoCompileStepConfig>(
            """{ "version": 1, "decision": { "from": "Previous" }, "analysisStepOrder": 1 }""",
            ConfigJsonOptions)!;
        legacy.MixInsertAudio.Should().BeFalse();
        legacy.InsertAudioGainDb.Should().Be(0);
    }

    // QA-P1-19 item 1: the MusicGainDb range used to be checked only in the executor (a run-time
    // CONFIG_INVALID). It now lives on the config itself, so the save path can report it as a 400
    // with the other config errors and the executor re-checks with the same method.
    [Theory]
    [InlineData(null, true)]
    [InlineData(0.0, true)]
    [InlineData(-40.0, true)]
    [InlineData(6.0, true)]
    [InlineData(6.5, false)]
    [InlineData(-40.5, false)]
    [InlineData(double.NaN, false)]
    [InlineData(double.PositiveInfinity, false)]
    public void Validate_reports_only_the_music_gain_range(double? gainDb, bool valid)
    {
        var config = new VideoCompileStepConfig(
            Version: 1, Decision: new ExtractInputRef(ExtractInputSource.Previous), AnalysisStepOrder: 1, MusicGainDb: gainDb);

        if (valid)
        {
            config.Validate().Should().BeEmpty();
        }
        else
        {
            config.Validate().Should().ContainSingle().Which
                .Should().Contain("-40").And.Contain("6").And.Contain("MusicGainDb");
        }
    }

    [Fact]
    public void A_config_with_no_music_gain_and_no_other_bad_value_validates_clean()
    {
        var config = new VideoCompileStepConfig(Version: 1, Decision: new ExtractInputRef(ExtractInputSource.Previous), AnalysisStepOrder: 1);

        config.Validate().Should().BeEmpty();
    }
}
