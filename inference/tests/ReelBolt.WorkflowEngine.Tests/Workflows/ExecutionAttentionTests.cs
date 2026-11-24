using System.Linq;
using AwesomeAssertions;
using ReelBolt.Shared.Workflows;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Workflows;

public class ExecutionAttentionTests
{
    [Fact]
    public void The_message_is_the_exact_user_wording()
    {
        ExecutionAttention.BuildMessage(3, 5, 8).Should().Be(
            "The review stopped after 3 rounds at 5/10; the target was 8/10. The film is ready but did not reach the quality bar.");
    }

    [Fact]
    public void One_round_reads_as_singular()
    {
        ExecutionAttention.BuildMessage(1, 5, 8).Should().Be(
            "The review stopped after 1 round at 5/10; the target was 8/10. The film is ready but did not reach the quality bar.");
    }

    [Fact]
    public void Stored_json_parses_into_the_dto_with_the_message()
    {
        ExecutionAttention? a = ExecutionAttention.TryParse(
            """{"reason":"review_below_minimum","stepOrder":4,"score":5,"minScore":8,"iterations":3}""");

        a.Should().Be(new ExecutionAttention("review_below_minimum", 4, 5, 8, 3, ExecutionAttention.BuildMessage(3, 5, 8)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"reason":"x","stepOrder":1}""")]
    public void Missing_or_malformed_json_is_no_attention(string? json) =>
        ExecutionAttention.TryParse(json).Should().BeNull();

    // ==========================================================================================
    // narration_incomplete — a Passed run that shipped with narration missing
    // ==========================================================================================

    [Fact]
    public void The_narration_message_names_how_many_of_how_many_lines_were_spoken()
    {
        ExecutionAttention.BuildNarrationMessage(2, 4, 0).Should().Be(
            "Only 2 of 4 narration lines were spoken in the film. The film is ready but the narration is incomplete.");
        ExecutionAttention.BuildNarrationMessage(1, 3, 0).Should().Be(
            "Only 1 of 3 narration lines were spoken in the film. The film is ready but the narration is incomplete.");
    }

    [Fact]
    public void The_narration_message_names_the_reanchored_lines_too()
    {
        ExecutionAttention.BuildNarrationMessage(3, 4, 2).Should().Contain("2 of them were re-anchored to a nearby kept clip");
        ExecutionAttention.BuildNarrationMessage(3, 4, 1).Should().Contain("1 of them was re-anchored to a nearby kept clip");
    }

    [Fact]
    public void Narration_json_round_trips_with_the_review_fields_kept_and_the_counts_added()
    {
        string json = ExecutionAttention.ToNarrationJson(
            stepOrder: 4, score: 3, minScore: 8, iterations: 1,
            spokenLines: 2, declaredLines: 4, reanchoredLines: 1);

        json.Should().Be(
            """{"reason":"narration_incomplete","stepOrder":4,"score":3,"minScore":8,"iterations":1,"spokenLines":2,"declaredLines":4,"reanchoredLineCount":1}""");

        ExecutionAttention? a = ExecutionAttention.TryParse(json);
        a.Should().Be(new ExecutionAttention("narration_incomplete", 4, 3, 8, 1,
            ExecutionAttention.BuildNarrationMessage(2, 4, 1)));
        a!.Message.Should().Contain("Only 2 of 4 narration lines were spoken");
    }

    [Fact]
    public void Narration_json_with_nothing_reanchored_omits_that_count()
    {
        string json = ExecutionAttention.ToNarrationJson(4, 0, 0, 0, 2, 4, 0);

        json.Should().NotContain("reanchoredLineCount");
        ExecutionAttention.TryParse(json)!.Message.Should().Contain("Only 2 of 4 narration lines were spoken");
    }

    [Fact]
    public void Narration_json_without_the_counts_is_no_attention()
    {
        ExecutionAttention.TryParse(
            """{"reason":"narration_incomplete","stepOrder":4,"score":0,"minScore":0,"iterations":0}""")
            .Should().BeNull("the narration message cannot be built without the line counts");
    }

    [Fact]
    public void The_tool_shape_keeps_its_exact_keys_for_narration_attention()
    {
        ExecutionAttention? a = ExecutionAttention.TryParse(
            ExecutionAttention.ToNarrationJson(4, 3, 8, 1, 2, 4, 1));

        string shape = System.Text.Json.JsonSerializer.Serialize(a!.ToToolShape());
        using var doc = System.Text.Json.JsonDocument.Parse(shape);
        doc.RootElement.EnumerateObject().Select(p => p.Name).Should().Equal(
            "reason", "stepOrder", "score", "minScore", "iterations", "message");
        doc.RootElement.GetProperty("reason").GetString().Should().Be("narration_incomplete");
    }
}
