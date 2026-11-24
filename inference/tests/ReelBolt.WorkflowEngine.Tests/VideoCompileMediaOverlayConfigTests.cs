using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Workflows;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// T-001: the append-only pictures-and-videos-over-the-film contract
/// (docs/design/picture-overlay.md). Pure config checks — no media, no artifact.
/// </summary>
public class VideoCompileMediaOverlayConfigTests
{
    // Mirrors the engine/API config serializer: camelCase + enum member names.
    private static readonly JsonSerializerOptions ConfigJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private static VideoCompileStepConfig BaseConfig(params VideoMediaOverlay[] overlays) => new(
        Version: 1,
        Decision: new ExtractInputRef(ExtractInputSource.Previous),
        AnalysisStepOrder: 1,
        Mode: VideoCompileMode.Reencode,
        MediaOverlays: overlays.Length == 0 ? null : overlays);

    private static VideoMediaOverlay ProjectFileOverlay(
        MediaOverlayForm form = MediaOverlayForm.Card,
        int opacity = 100,
        double? start = null,
        double? end = null) => new(
        new InsertContentRef(InsertContentRef.ProjectFileKind, ProjectFileId: Guid.NewGuid()),
        Form: form,
        OpacityPercent: opacity,
        StartSec: start,
        EndSec: end);

    [Fact]
    public void An_old_config_without_the_overlay_members_deserializes_to_the_byte_identical_defaults()
    {
        // A stored payload from before this feature.
        const string oldJson = """
            {"version":1,"decision":{"from":"Previous"},"analysisStepOrder":1,"mode":"Reencode","crf":20}
            """;

        VideoCompileStepConfig config = JsonSerializer.Deserialize<VideoCompileStepConfig>(oldJson, ConfigJsonOptions)!;

        config.MediaOverlays.Should().BeNull();
        config.MediaOverlayPlan.Should().BeNull();
        config.MaxMediaOverlays.Should().BeNull();
        config.MediaOverlaySafeMargin.Should().BeNull();
        config.MediaOverlayCardAudioDefault.Should().BeNull();
        config.Validate().Should().BeEmpty("the new members default to byte-identical behaviour");
    }

    [Fact]
    public void The_new_members_are_omitted_when_their_defaults_are_null()
    {
        var omitting = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter() },
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        string json = JsonSerializer.Serialize(BaseConfig(), omitting);

        json.Should().NotContain("mediaOverlays");
        json.Should().NotContain("mediaOverlayPlan");
        json.Should().NotContain("maxMediaOverlays");
        json.Should().NotContain("mediaOverlaySafeMargin");
        json.Should().NotContain("mediaOverlayCardAudioDefault");
    }

    [Fact]
    public void A_valid_card_and_inset_config_has_no_errors()
    {
        VideoMediaOverlay card = ProjectFileOverlay(MediaOverlayForm.Card, start: 1.5, end: 6);
        VideoMediaOverlay inset = ProjectFileOverlay(
            MediaOverlayForm.PictureInPicture,
            opacity: 80,
            start: 7,
            end: 13) with
        {
            Anchor = MediaOverlayAnchor.BottomRight,
            Size = MediaOverlaySize.Medium,
            Border = MediaOverlayBorder.Light,
            Audio = MediaOverlayAudio.Mixed,
            Volume = MediaOverlayVolume.Balanced,
            CardAudio = MediaOverlayCardAudio.Mute
        };

        BaseConfig(card, inset).Validate().Should().BeEmpty();
    }

    [Fact]
    public void A_step_reference_and_a_step_result_reference_are_valid_content()
    {
        VideoCompileStepConfig config = BaseConfig(
            new VideoMediaOverlay(new InsertContentRef(InsertContentRef.StepKind, StepOrder: 2)),
            new VideoMediaOverlay(new InsertContentRef(InsertContentRef.StepResultKind, StepResultId: Guid.NewGuid())));

        config.Validate().Should().BeEmpty();
    }

    [Fact]
    public void StreamCopy_refuses_a_config_overlay_and_a_plan()
    {
        VideoCompileStepConfig config = BaseConfig(ProjectFileOverlay()) with
        {
            Mode = VideoCompileMode.StreamCopy,
            MediaOverlayPlan = new ExtractInputRef(ExtractInputSource.Previous)
        };

        config.Validate().Should().Contain(e => e.StartsWith("MEDIA_OVERLAY_REQUIRES_REENCODE"));
    }

    [Fact]
    public void A_plan_from_a_source_other_than_previous_or_step_is_refused()
    {
        VideoCompileStepConfig config = BaseConfig() with
        {
            MediaOverlayPlan = new ExtractInputRef(ExtractInputSource.Accumulated)
        };

        config.Validate().Should().Contain(e => e.StartsWith("MEDIA_OVERLAY_PLAN_INVALID"));
    }

    [Fact]
    public void Safe_margin_outside_the_range_is_refused()
    {
        BaseConfig().WithMargin(-0.01).Validate().Should().Contain(e => e.Contains("MediaOverlaySafeMargin"));
        BaseConfig().WithMargin(0.16).Validate().Should().Contain(e => e.Contains("MediaOverlaySafeMargin"));
        BaseConfig().WithMargin(double.NaN).Validate().Should().Contain(e => e.Contains("MediaOverlaySafeMargin"));
    }

    [Fact]
    public void More_overlays_than_the_cap_is_refused()
    {
        VideoMediaOverlay[] many = Enumerable.Range(0, 9).Select(_ => ProjectFileOverlay()).ToArray();

        BaseConfig(many).Validate().Should().Contain(e => e.Contains("MaxMediaOverlays"));
    }

    [Fact]
    public void A_malformed_content_reference_is_refused()
    {
        VideoCompileStepConfig config = BaseConfig(
            new VideoMediaOverlay(new InsertContentRef(InsertContentRef.StepKind)));

        config.Validate().Should().Contain(e => e.StartsWith("MEDIA_OVERLAY_REF_INVALID"));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void Opacity_outside_zero_to_one_hundred_is_refused(int opacity)
    {
        BaseConfig(ProjectFileOverlay(opacity: opacity)).Validate()
            .Should().Contain(e => e.StartsWith("MEDIA_OVERLAY_OPACITY_INVALID"));
    }

    [Fact]
    public void Opacity_zero_is_allowed_and_means_not_drawn()
    {
        BaseConfig(ProjectFileOverlay(opacity: 0)).Validate().Should().BeEmpty();
    }

    [Fact]
    public void A_reversed_or_negative_or_unbounded_span_is_refused()
    {
        BaseConfig(ProjectFileOverlay(start: 5, end: 5)).Validate()
            .Should().Contain(e => e.StartsWith("MEDIA_OVERLAY_SPAN_INVALID"));
        BaseConfig(ProjectFileOverlay(start: -1, end: 5)).Validate()
            .Should().Contain(e => e.StartsWith("MEDIA_OVERLAY_SPAN_INVALID"));
        BaseConfig(ProjectFileOverlay(start: 0, end: VideoSourceRef.MaxWindowSec + 1)).Validate()
            .Should().Contain(e => e.StartsWith("MEDIA_OVERLAY_SPAN_INVALID"));
        BaseConfig(ProjectFileOverlay(start: double.NaN)).Validate()
            .Should().Contain(e => e.StartsWith("MEDIA_OVERLAY_SPAN_INVALID"));
    }

    [Fact]
    public void An_open_start_and_end_are_valid()
    {
        BaseConfig(ProjectFileOverlay(start: null, end: null)).Validate().Should().BeEmpty();
        BaseConfig(ProjectFileOverlay(start: null, end: 4)).Validate().Should().BeEmpty();
    }

    [Fact]
    public void A_picture_or_video_overlay_blocks_encode_once()
    {
        BaseConfig(ProjectFileOverlay()).EncodeOnceBlockers()
            .Should().Contain("pictures and videos over the film");
        BaseConfig().EncodeOnceBlockers()
            .Should().NotContain("pictures and videos over the film");
    }

    [Fact]
    public void The_save_gate_runs_the_overlay_config_checks()
    {
        const string json = """
            {"version":1,"mediaOverlays":[{"content":{"from":"ProjectFile","projectFileId":"00000000-0000-0000-0000-000000000001"},"startSec":5,"endSec":5}]}
            """;

        StepConfigSaveValidator.ValidateVideoCompileReframing(json)
            .Should().Contain(e => e.Contains("MEDIA_OVERLAY_SPAN_INVALID"));
    }

    [Fact]
    public void The_save_gate_reports_a_stream_copy_overlay()
    {
        const string json = """
            {"version":1,"mode":"StreamCopy","mediaOverlays":[{"content":{"from":"ProjectFile","projectFileId":"00000000-0000-0000-0000-000000000001"}}]}
            """;

        StepConfigSaveValidator.ValidateVideoCompileReframing(json)
            .Should().Contain(e => e.Contains("MEDIA_OVERLAY_REQUIRES_REENCODE"));
    }
}

internal static class VideoCompileMediaOverlayConfigTestExtensions
{
    public static VideoCompileStepConfig WithMargin(this VideoCompileStepConfig config, double margin) =>
        config with { MediaOverlaySafeMargin = margin };
}
