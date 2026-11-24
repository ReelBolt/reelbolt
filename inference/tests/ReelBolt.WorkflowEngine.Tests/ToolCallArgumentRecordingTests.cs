using System.Collections.Generic;
using AwesomeAssertions;
using ReelBolt.WorkflowEngine.Agents;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

public class ToolCallArgumentRecordingTests
{
    [Fact]
    public void Arguments_are_recorded_as_json_not_the_dictionary_type_name()
    {
        string json = ReelBoltAgentBase.SerializeArguments(new Dictionary<string, object?> { ["fileReference"] = "Pages/Home.razor" });

        json.Should().Be("{\"fileReference\":\"Pages/Home.razor\"}");
    }

    [Fact]
    public void Long_argument_values_are_truncated()
    {
        string json = ReelBoltAgentBase.SerializeArguments(new Dictionary<string, object?> { ["content"] = new string('x', 10_000) });

        json.Length.Should().BeLessThan(2200);
        json.Should().Contain("[truncated]");
    }
}
