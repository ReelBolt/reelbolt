using System;
using AwesomeAssertions;
using ReelBolt.WorkflowEngine.Agents.Tools;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

public class StepKeyedStateTests
{
    [Fact]
    public void A_full_table_drops_the_oldest_entry_and_keeps_the_rest()
    {
        var state = new StepKeyedState<StepCallCounter>(3);
        Guid[] keys = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];
        foreach (Guid k in keys)
            state.GetOrAdd(k, () => new StepCallCounter()).Increment();

        state.Count.Should().Be(3);
        state.GetOrAdd(keys[3], () => new StepCallCounter()).Increment().Should().Be(2, "a recent step keeps its count");
        state.GetOrAdd(keys[0], () => new StepCallCounter()).Increment().Should().Be(1, "the oldest was dropped");
    }
}
