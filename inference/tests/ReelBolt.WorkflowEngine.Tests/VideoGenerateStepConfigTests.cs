using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Workflows;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Tests for <see cref="VideoGenerateStepConfig"/> serialization and validation.
/// Mirrors the round-trip and validation pattern established by VideoAnalyze/VideoCompile config tests.
/// </summary>
public class VideoGenerateStepConfigTests
{
    // Mirrors VideoAnalyzeStepExecutor.ConfigJsonOptions / VideoCompileStepExecutor.ConfigJsonOptions
    // / ExtractStepExecutor.ConfigJsonOptions exactly: JsonSerializerDefaults.Web (camelCase
    // properties) + JsonStringEnumConverter (PascalCase enum member names).
    private static readonly JsonSerializerOptions ConfigJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public void JsonRoundTrip_WithUnknownExtraFields_DoesNotThrow()
    {
        // Arrange
        var json = """
        {
            "planSource": "Inline",
            "inlinePrompt": "Create an engaging video",
            "model": "MiniMax-H3",
            "resolution": "768P",
            "durationSeconds": 6,
            "ratio": "16:9",
            "providerId": "550e8400-e29b-41d4-a716-446655440000",
            "takes": 2,
            "maxClips": 1,
            "maxSpendCredits": 10.0,
            "allowSourceMediaEgress": false,
            "pollIntervalSeconds": 10,
            "timeoutSeconds": 1800,
            "unknownField1": "should be ignored",
            "unknownNested": { "foo": "bar" },
            "unknownArray": [1, 2, 3]
        }
        """;

        // Act
        var config = JsonSerializer.Deserialize<VideoGenerateStepConfig>(json, ConfigJsonOptions);
        var json2 = JsonSerializer.Serialize(config, ConfigJsonOptions);
        var config2 = JsonSerializer.Deserialize<VideoGenerateStepConfig>(json2, ConfigJsonOptions);

        // Assert
        Assert.NotNull(config);
        Assert.NotNull(config2);
        Assert.Equal(config.PlanSource, config2.PlanSource);
        Assert.Equal(config.InlinePrompt, config2.InlinePrompt);
        Assert.Equal(config.Model, config2.Model);
        Assert.Equal(config.Takes, config2.Takes);
        Assert.Equal(config.MaxSpendCredits, config2.MaxSpendCredits);
    }

    [Fact]
    public void Validate_WithMaxSpendCreditsNegative_ReturnsError()
    {
        // Arrange
        var config = new VideoGenerateStepConfig { MaxSpendCredits = -1.0m };

        // Act
        var errors = config.Validate();

        // Assert
        Assert.NotEmpty(errors);
        Assert.Contains(errors, e => e.Contains("MaxSpendCredits") || e.Contains("positive"));
    }

    [Fact]
    public void Validate_WithMaxSpendCreditsZero_ReturnsError()
    {
        // Arrange
        var config = new VideoGenerateStepConfig { MaxSpendCredits = 0m };

        // Act
        var errors = config.Validate();

        // Assert
        Assert.NotEmpty(errors);
        Assert.Contains(errors, e => e.Contains("MaxSpendCredits") || e.Contains("positive"));
    }

    [Fact]
    public void Validate_WithTakesZero_ReturnsError()
    {
        // Arrange
        var config = new VideoGenerateStepConfig { Takes = 0, MaxSpendCredits = 10.0m };

        // Act
        var errors = config.Validate();

        // Assert
        Assert.NotEmpty(errors);
        Assert.Contains(errors, e => e.Contains("Takes") || e.Contains("between 1 and 64"));
    }

    [Fact]
    public void Validate_WithTakesAboveCeiling_ReturnsError()
    {
        // Arrange
        var config = new VideoGenerateStepConfig { Takes = 65, MaxClips = 65, MaxSpendCredits = 10.0m };

        // Act
        var errors = config.Validate();

        // Assert
        Assert.NotEmpty(errors);
        Assert.Contains(errors, e => e.Contains("Takes") || e.Contains("between 1 and 64"));
    }

    [Fact]
    public void Validate_WithValidDefaultsAndPositiveMaxSpendCredits_ReturnsEmpty()
    {
        // Arrange
        var config = new VideoGenerateStepConfig { MaxSpendCredits = 10.0m };

        // Act
        var errors = config.Validate();

        // Assert
        Assert.Empty(errors);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(8)]
    [InlineData(64)]
    public void Validate_WithTakesInValidRange_ReturnsEmpty(int takes)
    {
        // Arrange (MaxClips is a ceiling on Takes, so it must admit them)
        var config = new VideoGenerateStepConfig { Takes = takes, MaxClips = takes, MaxSpendCredits = 10.0m };

        // Act
        var errors = config.Validate();

        // Assert
        Assert.Empty(errors);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65)]
    public void Validate_WithMaxClipsOutOfRange_ReturnsErrorNamingMaxClips(int maxClips)
    {
        var config = new VideoGenerateStepConfig { Takes = 1, MaxClips = maxClips, MaxSpendCredits = 10.0m };

        var errors = config.Validate();

        Assert.Contains(errors, e => e.Contains("MaxClips") && e.Contains("between 1 and 64"));
    }

    [Fact]
    public void Validate_WithTakesAboveMaxClips_ReturnsErrorNamingBothFields()
    {
        var config = new VideoGenerateStepConfig { Takes = 3, MaxClips = 2, MaxSpendCredits = 10.0m };

        var errors = config.Validate();

        var error = Assert.Single(errors);
        Assert.Contains("Takes", error);
        Assert.Contains("MaxClips", error);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(8)]
    [InlineData(64)]
    public void Validate_WithMaxClipsAboveFormerCap_ReturnsEmpty(int maxClips)
    {
        var config = new VideoGenerateStepConfig { Takes = 1, MaxClips = maxClips, MaxSpendCredits = 10.0m };

        Assert.Empty(config.Validate());
    }

    [Fact]
    public void Validate_FourShotsTimesTwoTakesWithMaxClipsEight_ReturnsEmpty()
    {
        var config = new VideoGenerateStepConfig
        {
            PlanSource = VideoGeneratePlanSource.StepRef,
            Plan = new ExtractInputRef(ExtractInputSource.Previous, null),
            Takes = 2,
            MaxClips = 8,
            MaxSpendCredits = 10.0m
        };

        Assert.Empty(config.Validate());
    }

    [Fact]
    public void Validate_WithTakesEqualToMaxClips_ReturnsEmpty()
    {
        var config = new VideoGenerateStepConfig { Takes = 2, MaxClips = 2, MaxSpendCredits = 10.0m };

        Assert.Empty(config.Validate());
    }

    [Fact]
    public void DefaultValues_AreProperlySerialized()
    {
        // Arrange
        var config = new VideoGenerateStepConfig { MaxSpendCredits = 10.0m };

        // Act
        var json = JsonSerializer.Serialize(config, ConfigJsonOptions);

        // Assert
        Assert.Contains("\"planSource\":\"Inline\"", json);
        Assert.Contains("\"model\":\"MiniMax-H3\"", json);
        Assert.Contains("\"resolution\":\"768P\"", json);
        Assert.Contains("\"durationSeconds\":6", json);
        Assert.Contains("\"ratio\":\"16:9\"", json);
        Assert.Contains("\"takes\":1", json);
        Assert.Contains("\"maxClips\":1", json);
        Assert.Contains("\"allowSourceMediaEgress\":false", json);
        Assert.Contains("\"pollIntervalSeconds\":10", json);
        Assert.Contains("\"timeoutSeconds\":1800", json);
    }
    // Mirrors MiniMaxVideoGenerationClient.Describe for the two supported models.
    private static readonly VideoGenerationCapabilities H3 = new(
        new Dictionary<string, decimal> { { "768P", 0.08m }, { "2K", 0.13m } },
        4, 15, new[] { "21:9", "16:9", "4:3", "1:1", "3:4", "9:16" });

    private static readonly VideoGenerationCapabilities H3Max = new(
        new Dictionary<string, decimal> { { "480P", 0.05m }, { "768P", 0.08m } },
        5, 15, new[] { "21:9", "16:9", "4:3", "1:1", "3:4", "9:16" });

    [Theory]
    [InlineData(-5)]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(16)]
    public void ValidateCaps_WithDurationOutsideModelRange_ReturnsError(int duration)
    {
        var config = new VideoGenerateStepConfig { MaxSpendCredits = 10m, DurationSeconds = duration };

        var errors = config.Validate(H3);

        Assert.Contains(errors, e => e.Contains("DurationSeconds"));
    }

    [Fact]
    public void ValidateCaps_WithDurationBelowH3MaxMinimum_ReturnsError()
    {
        var config = new VideoGenerateStepConfig { MaxSpendCredits = 10m, Model = "MiniMax-H3-Max", DurationSeconds = 4 };

        var errors = config.Validate(H3Max);

        Assert.Contains(errors, e => e.Contains("DurationSeconds"));
    }

    [Theory]
    [InlineData(-5)]
    [InlineData(0)]
    public void Validate_WithNonPositiveDuration_ReturnsError(int duration)
    {
        var config = new VideoGenerateStepConfig { MaxSpendCredits = 10m, DurationSeconds = duration };

        var errors = config.Validate();

        Assert.Contains(errors, e => e.Contains("DurationSeconds"));
    }

    [Theory]
    [InlineData("768p")]
    [InlineData("1080P")]
    [InlineData("2K")]
    [InlineData("")]
    public void ValidateCaps_WithResolutionNotOfferedOrMiscased_ReturnsError(string resolution)
    {
        var config = new VideoGenerateStepConfig { MaxSpendCredits = 10m, Model = "MiniMax-H3-Max", Resolution = resolution };

        var errors = config.Validate(H3Max);

        Assert.Contains(errors, e => e.Contains("Resolution"));
    }

    [Theory]
    [InlineData("adaptive")]
    [InlineData("16:10")]
    [InlineData(" 16:9")]
    public void ValidateCaps_WithRatioNotOffered_ReturnsError(string ratio)
    {
        var config = new VideoGenerateStepConfig { MaxSpendCredits = 10m, Ratio = ratio };

        var errors = config.Validate(H3);

        Assert.Contains(errors, e => e.Contains("Ratio"));
    }

    [Theory]
    [InlineData(4, "768P", "16:9")]
    [InlineData(15, "2K", "9:16")]
    public void ValidateCaps_WithinModelCapabilities_ReturnsEmpty(int duration, string resolution, string ratio)
    {
        var config = new VideoGenerateStepConfig
        {
            MaxSpendCredits = 10m,
            DurationSeconds = duration,
            Resolution = resolution,
            Ratio = ratio
        };

        Assert.Empty(config.Validate(H3));
    }

    [Fact]
    public void ValidateCaps_AlsoIncludesBaseValidationErrors()
    {
        var config = new VideoGenerateStepConfig { MaxSpendCredits = 0m };

        Assert.Contains(config.Validate(H3), e => e.Contains("MaxSpendCredits"));
    }

    // ---- T-025: PollIntervalSeconds / TimeoutSeconds bounds ----

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-300)]
    [InlineData(301)]
    [InlineData(int.MaxValue)]
    public void Validate_WithPollIntervalOutOfRange_ReturnsErrorNamingFieldAndRange(int pollIntervalSeconds)
    {
        var config = new VideoGenerateStepConfig { MaxSpendCredits = 10m, PollIntervalSeconds = pollIntervalSeconds };

        var errors = config.Validate();

        var error = Assert.Single(errors);
        Assert.Contains("PollIntervalSeconds", error);
        Assert.Contains("between 1 and 300", error);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(60)]
    public void Validate_WithPollIntervalAtOrInsideBounds_ReturnsEmpty(int pollIntervalSeconds)
    {
        var config = new VideoGenerateStepConfig { MaxSpendCredits = 10m, PollIntervalSeconds = pollIntervalSeconds };

        Assert.Empty(config.Validate());
    }

    [Fact]
    public void Validate_WithPollIntervalAtUpperEdge_ReturnsEmpty()
    {
        // 300 is only valid alongside a TimeoutSeconds >= 300 (the default 1800 is).
        var config = new VideoGenerateStepConfig { MaxSpendCredits = 10m, PollIntervalSeconds = 300 };

        Assert.Empty(config.Validate());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(59)]
    [InlineData(7201)]
    [InlineData(int.MaxValue)]
    public void Validate_WithTimeoutOutOfRange_ReturnsErrorNamingFieldAndRange(int timeoutSeconds)
    {
        var config = new VideoGenerateStepConfig { MaxSpendCredits = 10m, TimeoutSeconds = timeoutSeconds };

        var errors = config.Validate();

        var error = Assert.Single(errors);
        Assert.Contains("TimeoutSeconds", error);
        Assert.Contains("between 60 and 7200", error);
    }

    [Theory]
    [InlineData(60)]
    [InlineData(1800)]
    [InlineData(7200)]
    public void Validate_WithTimeoutAtOrInsideBounds_ReturnsEmpty(int timeoutSeconds)
    {
        var config = new VideoGenerateStepConfig { MaxSpendCredits = 10m, TimeoutSeconds = timeoutSeconds };

        Assert.Empty(config.Validate());
    }

    [Fact]
    public void Validate_WithTimeoutBelowPollInterval_ReturnsErrorNamingBothValues()
    {
        var config = new VideoGenerateStepConfig { MaxSpendCredits = 10m, PollIntervalSeconds = 300, TimeoutSeconds = 299 };

        var errors = config.Validate();

        var error = Assert.Single(errors);
        Assert.Contains("TimeoutSeconds (299)", error);
        Assert.Contains("PollIntervalSeconds (300)", error);
    }

    [Fact]
    public void Validate_WithTimeoutEqualToPollInterval_ReturnsEmpty()
    {
        var config = new VideoGenerateStepConfig { MaxSpendCredits = 10m, PollIntervalSeconds = 120, TimeoutSeconds = 120 };

        Assert.Empty(config.Validate());
    }

    [Fact]
    public void Validate_WithBothOutOfRange_ReportsEachRangeError_NotTheCrossCheck()
    {
        var config = new VideoGenerateStepConfig { MaxSpendCredits = 10m, PollIntervalSeconds = 0, TimeoutSeconds = 0 };

        var errors = config.Validate();

        Assert.Equal(2, errors.Count);
        Assert.Contains(errors, e => e.Contains("PollIntervalSeconds") && e.Contains("between 1 and 300"));
        Assert.Contains(errors, e => e.Contains("TimeoutSeconds") && e.Contains("between 60 and 7200"));
    }

    [Fact]
    public void Validate_WithDefaultPollAndTimeout_ReturnsEmpty()
    {
        var config = new VideoGenerateStepConfig { MaxSpendCredits = 10m };

        Assert.Equal(10, config.PollIntervalSeconds);
        Assert.Equal(1800, config.TimeoutSeconds);
        Assert.Empty(config.Validate());
    }
}
