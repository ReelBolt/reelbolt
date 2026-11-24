using System.Collections.Generic;
using System.Text.Json;
using AwesomeAssertions;
using ReelBolt.WorkflowEngine.Agents;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

public class StructuredAnswerTruncationTests
{
    private const string Answer = "{\"cues\":[],\"planRationale\":\"r\"}";

    [Theory]
    [InlineData(Answer + "\nHope this helps! See [1] and {note}.")]
    [InlineData("Here it is:\n```json\n" + Answer + "\n```\nDone.")]
    [InlineData("Shot {3} and [x] noted.\n" + Answer)]
    public void valid_answer_with_prose_around_is_isolated(string text)
    {
        StructuredAnswer.Isolate(text, JsonValueKind.Object).Output.Should().Be(Answer);
    }

    [Theory]
    [InlineData("[{\"cues\":[],\"planRationale\":\"r\"}]")]
    [InlineData("[{\"cues\":[],\"planRationale\":\"r\"},{\"cues\":[")]
    public void object_schema_array_answer_is_not_mined_for_an_inner_object(string text)
    {
        StructuredAnswer.Isolate(text, JsonValueKind.Object).Output.Should().Be(text);
    }

    [Theory]
    [InlineData("{\"items\":[1,2]}")]
    [InlineData("{\"tags\":[\"a\"],\"items\":[{\"b\":")]
    public void array_schema_object_answer_is_not_mined_for_an_inner_array(string text)
    {
        StructuredAnswer.Isolate(text, JsonValueKind.Array).Output.Should().Be(text);
    }

    [Fact]
    public void prose_object_does_not_hide_a_truncated_answer()
    {
        string text = "Default {\"cues\":[]} applies.\n{\"cues\":[{\"clipId\":\"x1\",\"anch";
        StructuredAnswer.Isolate(text, JsonValueKind.Object).Output.Should().Be(text);
    }

    [Fact]
    public void expected_kind_for_a_dictionary_is_object()
    {
        StructuredAnswer.ExpectedKind(typeof(Dictionary<string, int>)).Should().Be(JsonValueKind.Object);
    }

    [Fact]
    public void many_unclosed_wrong_kind_brackets_after_a_valid_answer_stay_linear()
    {
        string text = Answer + " " + new string('[', 20_000);
        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();

        StructuredAnswer.Isolate(text, JsonValueKind.Object).Output.Should().Be(Answer);

        watch.ElapsedMilliseconds.Should().BeLessThan(500);
    }
}
