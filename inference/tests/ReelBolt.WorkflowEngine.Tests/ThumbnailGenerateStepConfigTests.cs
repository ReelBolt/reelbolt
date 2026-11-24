using System;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.Shared.Workflows;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// <c>ThumbnailGenerateStepConfig.Validate</c> checks every field the ffmpeg argv uses and names the field,
/// so a bad value is a precise 400 / CONFIG_INVALID instead of an ffmpeg failure three layers later.
/// </summary>
public class ThumbnailGenerateStepConfigTests
{
    private static ThumbnailGenerateStepConfig Valid() => new(Version: 1, AnalysisStepOrder: 1);

    [Theory]
    [InlineData("#AABBCC", true)]
    [InlineData("#AABBCC\n", false)]
    public void Hex_font_colour_with_a_trailing_newline_is_refused(string color, bool allowed) =>
        ThumbnailGenerateStepConfig.IsAllowedFontColor(color).Should().Be(allowed);

    [Fact]
    public void The_reported_1920x1080_quality_2_none_box_config_is_valid()
    {
        ThumbnailGenerateStepConfig.Validate(Valid() with
        {
            Width = 1920,
            Height = 1080,
            JpegQuality = 2,
            OverlayText = "x",
            OverlayPosition = ThumbnailOverlayPosition.LowerThird,
            OverlayBoxColor = "none"
        }).Should().BeEmpty();
    }

    [Fact]
    public void Defaults_are_valid() => ThumbnailGenerateStepConfig.Validate(Valid()).Should().BeEmpty();

    [Theory]
    [InlineData("OverlayFontColor")]
    [InlineData("OverlayBoxColor")]
    public void A_colour_outside_the_allowlist_is_named(string field)
    {
        ThumbnailGenerateStepConfig config = Valid() with { OverlayPosition = ThumbnailOverlayPosition.Center };
        config = field == "OverlayFontColor"
            ? config with { OverlayFontColor = "red;format=null" }
            : config with { OverlayBoxColor = "black@0.45,format=null" };

        ThumbnailGenerateStepConfig.Validate(config).Should().ContainSingle().Which.Should().StartWith(field);
    }

    [Fact]
    public void Colours_are_not_checked_when_no_overlay_is_drawn()
        => ThumbnailGenerateStepConfig.Validate(Valid() with { OverlayBoxColor = "whatever" }).Should().BeEmpty();

    [Fact]
    public void Undefined_enum_values_and_negative_indexes_are_named()
    {
        var errors = ThumbnailGenerateStepConfig.Validate(Valid() with
        {
            Fit = (ThumbnailFit)9,
            OverlayPosition = (ThumbnailOverlayPosition)9,
            Selection = (ThumbnailSelection)9,
            AtTimeBasis = (ThumbnailAtTimeBasis)9,
            SourceIndex = -1
        });

        errors.Should().Contain(e => e.StartsWith("Fit"))
            .And.Contain(e => e.StartsWith("OverlayPosition"))
            .And.Contain(e => e.StartsWith("Selection"))
            .And.Contain(e => e.StartsWith("AtTimeBasis"))
            .And.Contain(e => e.StartsWith("SourceIndex"));
    }

    [Fact]
    public void A_non_finite_AtTimeSec_is_named()
        => ThumbnailGenerateStepConfig.Validate(Valid() with { Selection = ThumbnailSelection.AtTime, AtTimeSec = double.NaN })
            .Should().ContainSingle().Which.Should().StartWith("AtTimeSec");

    [Fact]
    public void An_AtTimeSec_past_the_longest_supported_film_is_named()
        => ThumbnailGenerateStepConfig.Validate(Valid() with { Selection = ThumbnailSelection.AtTime, AtTimeSec = 1e20 })
            .Should().ContainSingle().Which.Should().StartWith("AtTimeSec");

    [Theory]
    [InlineData(double.NaN, null)]
    [InlineData(-5.0, null)]
    [InlineData(1e20, null)]
    [InlineData(0.0, double.PositiveInfinity)]
    [InlineData(10.0, 5.0)]
    public void An_AtTime_Source_window_that_is_not_a_finite_forward_range_is_named(double inSec, double? outSec)
    {
        var film = new VideoSourceRef(VideoSourceKind.StepOutput, StepOrder: 3, InSec: inSec, OutSec: outSec);

        ThumbnailGenerateStepConfig.Validate(new ThumbnailGenerateStepConfig(
                Version: 1, AnalysisStepOrder: 0, Selection: ThumbnailSelection.AtTime, AtTimeSec: 1, Source: film,
                AtTimeBasis: ThumbnailAtTimeBasis.Output))
            .Should().ContainSingle().Which.Should().StartWith("Source.InSec/OutSec");
    }

    [Fact]
    public void An_empty_font_file_id_is_named()
        => ThumbnailGenerateStepConfig.Validate(Valid() with { FontProjectFileId = Guid.Empty })
            .Should().ContainSingle().Which.Should().StartWith("FontProjectFileId");

    [Fact]
    public void AtTime_on_a_film_needs_no_analysis_step()
    {
        var film = new VideoSourceRef(VideoSourceKind.StepOutput, StepOrder: 3);
        ThumbnailGenerateStepConfig.Validate(new ThumbnailGenerateStepConfig(
            Version: 1, AnalysisStepOrder: 0, Selection: ThumbnailSelection.AtTime, AtTimeSec: 12.5, Source: film,
            AtTimeBasis: ThumbnailAtTimeBasis.Output)).Should().BeEmpty();
    }

    [Fact]
    public void Auto_on_a_compiled_film_is_refused_because_its_shots_are_not_in_the_analysis()
        => ThumbnailGenerateStepConfig.Validate(Valid() with { Source = new VideoSourceRef(VideoSourceKind.StepOutput, StepOrder: 3) })
            .Should().ContainSingle().Which.Should().StartWith("Source").And.Contain("AtTime");

    [Fact]
    public void Auto_on_a_single_project_file_is_allowed()
        => ThumbnailGenerateStepConfig.Validate(Valid() with { Source = new VideoSourceRef(VideoSourceKind.ProjectFile, ProjectFileId: Guid.NewGuid()) })
            .Should().BeEmpty();

    [Fact]
    public void Output_basis_without_a_source_is_named()
        => ThumbnailGenerateStepConfig.Validate(Valid() with { Selection = ThumbnailSelection.AtTime, AtTimeBasis = ThumbnailAtTimeBasis.Output })
            .Should().ContainSingle().Which.Should().StartWith("AtTimeBasis");

    [Fact]
    public void A_malformed_source_is_named()
        => ThumbnailGenerateStepConfig.Validate(Valid() with
        {
            Selection = ThumbnailSelection.AtTime,
            Source = new VideoSourceRef(VideoSourceKind.StepResult)
        })
            .Should().ContainSingle().Which.Should().StartWith("Source");

    [Fact]
    public void Every_range_error_names_its_field()
    {
        var errors = ThumbnailGenerateStepConfig.Validate(Valid() with
        {
            Width = 1,
            Height = 99999,
            JpegQuality = 1,
            OverlayFontSizePct = 1
        });

        errors.Select(e => e.Split(' ')[0]).Should().BeEquivalentTo("Width", "Height", "JpegQuality", "OverlayFontSizePct");
    }

    [Fact]
    public void The_save_validator_names_the_bad_field_at_save_time()
    {
        var errors = ReelBolt.Inference.Api.Services.Workflows.StepConfigSaveValidator.ValidateThumbnailGenerateConfig(
            "{\"version\":1,\"analysisStepOrder\":1,\"width\":99999,\"overlayPosition\":\"LowerThird\",\"overlayBoxColor\":\"red\"}");

        errors.Should().Contain(e => e.StartsWith("Width")).And.Contain(e => e.StartsWith("OverlayBoxColor"));
    }

    [Fact]
    public void The_save_validator_accepts_the_reported_none_box_config()
        => ReelBolt.Inference.Api.Services.Workflows.StepConfigSaveValidator.ValidateThumbnailGenerateConfig(
            "{\"version\":1,\"analysisStepOrder\":1,\"width\":1920,\"height\":1080,\"jpegQuality\":2,\"overlayText\":\"Hi\",\"overlayPosition\":\"LowerThird\",\"overlayBoxColor\":\"none\"}")
            .Should().BeEmpty();
}
