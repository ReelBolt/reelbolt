using System.Linq;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Workflows;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>T-105: every resolution key a price card has is discoverable from the VideoGenerate schema, generated from the card.</summary>
public sealed class VideoGenerationResolutionCatalogTests
{
    [Fact]
    public void Every_MiniMax_model_key_is_listed_exactly_as_its_price_card_keys_it()
    {
        string text = VideoGenerationResolutionCatalog.Describe();

        foreach (string model in MiniMaxVideoGenerationClient.ModelIds)
        {
            string keys = string.Join(", ", MiniMaxVideoGenerationClient.DescribeModel(model).PricePerSecondByResolution.Keys);
            text.Should().Contain($"{model}: {keys}");
        }

        text.Should().Contain("MiniMax-H3: 768P, 2K");
    }

    [Fact]
    public void Every_fal_endpoint_with_a_price_table_is_listed_with_its_own_keys()
    {
        string text = VideoGenerationResolutionCatalog.Describe();

        foreach (FalVideoEndpoint endpoint in FalVideoCatalog.Instance.All)
        {
            string keys = string.Join(", ", endpoint.PricePerSecondByResolution.Keys);
            text.Should().Contain($"{endpoint.Id}: {keys}");
        }
    }

    [Fact]
    public void The_MiniMax_model_list_matches_what_Describe_accepts()
    {
        MiniMaxVideoGenerationClient.ModelIds.Should().NotBeEmpty();
        foreach (string model in MiniMaxVideoGenerationClient.ModelIds)
        {
            MiniMaxVideoGenerationClient.DescribeModel(model).PricePerSecondByResolution.Should().NotBeEmpty();
        }
    }

    [Fact]
    public void The_VideoGenerate_resolution_schema_description_carries_the_generated_catalogue()
    {
        var exporter = new StepConfigSchemaExporter();

        string? description = exporter.GetSchema(StepType.VideoGenerate).Schema!["properties"]!["resolution"]!["description"]?.GetValue<string>();

        description.Should().NotBeNull();
        description.Should().Contain("MiniMax-H3: 768P, 2K").And.Contain("case-sensitive");
        description.Should().Contain(VideoGenerationResolutionCatalog.Describe());
    }

    [Fact]
    public void MiniMax_H3_2K_validates_and_prices_against_its_card()
    {
        VideoGenerationCapabilities card = MiniMaxVideoGenerationClient.DescribeModel("MiniMax-H3");
        var config = new VideoGenerateStepConfig { Model = "MiniMax-H3", Resolution = "2K", InlinePrompt = "x", MaxSpendCredits = 100, DurationSeconds = 6 };

        config.Validate(card).Should().BeEmpty();
        VideoGenerationPricing.EstimateClipCostUsd(card, "2K", 6).Should().Be(0.13m * 6);
    }

    [Fact]
    public void A_resolution_the_card_does_not_key_is_refused_naming_the_allowed_ones()
    {
        VideoGenerationCapabilities card = MiniMaxVideoGenerationClient.DescribeModel("MiniMax-H3");
        var config = new VideoGenerateStepConfig { Model = "MiniMax-H3", Resolution = "4K", InlinePrompt = "x", MaxSpendCredits = 100, DurationSeconds = 6 };

        config.Validate(card).Should().ContainSingle(e => e.Contains("768P, 2K"));
    }
}
