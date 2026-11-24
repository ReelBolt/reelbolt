using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows.Apps;
using ReelBolt.WorkflowEngine.Execution;
using Xunit;
using static ReelBolt.WorkflowEngine.Tests.AppInputs.AppInputsTestKit;

namespace ReelBolt.WorkflowEngine.Tests.AppInputs;

/// <summary>Input-decided conditions (docs/design/app-inputs.md 9): detection, evaluation parity with the engine, reach and "end".</summary>
public class StaticBranchResolverTests
{
    [Theory]
    [InlineData("input.liteMode", true)]
    [InlineData("$.input.liteMode", true)]
    [InlineData("input.quality == 'high' and input.takes > 1", true)]
    [InlineData("not(input.liteMode)", true)]
    [InlineData("if(input.takes > 2, true, false)", true)]
    [InlineData("", true)]
    [InlineData("score > 8", false)]
    [InlineData("input.liteMode and $.score > 8", false)]
    [InlineData("Evil(input.liteMode)", false)]
    public void Detects_conditions_that_read_only_inputs(string expression, bool decided)
    {
        StaticBranchResolver.IsInputDecided(expression).Should().Be(decided);
    }

    [Fact]
    public void Lists_the_inputs_an_expression_reads_ignoring_string_literals()
    {
        StaticBranchResolver.InputIds("input.quality == 'input.fake' and $.input.takes > input.takes")
            .Should().Equal("quality", "takes");
    }

    public static IEnumerable<object[]> Parity() =>
    [
        ["input.liteMode", """{"liteMode":true}"""],
        ["input.liteMode", """{"liteMode":false}"""],
        ["input.quality == 'high' and input.takes > 1", """{"quality":"high","takes":2}"""],
        ["input.quality == 'high' AND input.takes > 1", """{"quality":"high","takes":1}"""],
        ["$.input.gain < -6.5 or input.liteMode", """{"gain":-12.5,"liteMode":false}"""],
        ["input.takes >= 2", """{"takes":2}"""],
        ["input.missing", """{}"""],
    ];

    [Theory]
    [MemberData(nameof(Parity))]
    public void Evaluates_exactly_like_the_engine_evaluator(string expression, string valuesJson)
    {
        Dictionary<string, JsonElement> values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(valuesJson)!;
        bool engine = ExpressionEvaluator.Evaluate(expression, "{}", StaticBranchResolver.Parameters(values));
        bool? resolved = StaticBranchResolver.Decide(expression, values);

        if (StaticBranchResolver.InputIds(expression).All(id => values.ContainsKey(id)))
            resolved.Should().Be(engine);
        else
            resolved.Should().BeNull("a value it reads is absent: undecided, never guessed");
    }

    [Fact]
    public void A_value_is_a_parameter_never_part_of_the_expression()
    {
        StaticBranchResolver.Decide("input.word == 'yes'", Values(("word", "\"yes' or true or 'x\""))).Should().BeFalse();
    }

    private static readonly IReadOnlyList<AppBindingStep> AvatarClip =
    [
        Step(1, StepType.Conditional, condition: "input.liteMode", trueBranch: "2", falseBranch: "6"),
        Step(2, StepType.Agent),
        Step(3, StepType.Voiceover),
        Step(4, StepType.VideoGenerate),
        Step(5, StepType.Conditional, trueBranch: "end"),
        Step(6, StepType.VideoGenerate),
    ];

    [Fact]
    public void Reach_follows_the_decided_branch_and_stops_at_end()
    {
        StaticBranchResolver.ReachableStepOrders(AvatarClip, Values(("liteMode", "true"))).Should().BeEquivalentTo([1, 2, 3, 4, 5]);
        StaticBranchResolver.ReachableStepOrders(AvatarClip, Values(("liteMode", "false"))).Should().BeEquivalentTo([1, 6]);
        StaticBranchResolver.ReachableStepOrders(AvatarClip, null).Should().BeEquivalentTo([1, 2, 3, 4, 5, 6], "undecided takes both branches");
    }

    [Fact]
    public void A_condition_on_step_output_keeps_both_branches()
    {
        IReadOnlyList<AppBindingStep> steps =
        [
            Step(1, StepType.Conditional, condition: "score > 8", trueBranch: "3", falseBranch: "2"),
            Step(2, StepType.Agent),
            Step(3, StepType.Agent),
        ];
        StaticBranchResolver.ReachableStepOrders(steps, Values(("liteMode", "true"))).Should().BeEquivalentTo([1, 2, 3]);
    }

    [Fact]
    public void A_decision_mode_conditional_keeps_both_branches_whatever_the_inputs_say()
    {
        IReadOnlyList<AppBindingStep> steps =
        [
            Step(1, StepType.Conditional, config: """{"mode":"Decision"}""", condition: "input.liteMode", trueBranch: "2", falseBranch: "4"),
            Step(2, StepType.Agent),
            Step(3, StepType.Agent),
            Step(4, StepType.Agent),
        ];
        StaticBranchResolver.ReachableStepOrders(steps, Values(("liteMode", "true"))).Should().Contain([2, 4]);
        StaticBranchResolver.ReachableStepOrders(steps, Values(("liteMode", "false"))).Should().Contain([2, 4]);
        StaticBranchResolver.ReachableStepOrders(steps, null).Should().Contain([2, 4]);
    }

    [Fact]
    public void An_expression_mode_conditional_on_an_input_still_prunes()
    {
        IReadOnlyList<AppBindingStep> steps =
        [
            Step(1, StepType.Conditional, config: """{"mode":"Expression"}""", condition: "input.liteMode", trueBranch: "2", falseBranch: "4"),
            Step(2, StepType.Agent),
            Step(3, StepType.Agent),
            Step(4, StepType.Agent),
        ];
        StaticBranchResolver.ReachableStepOrders(steps, Values(("liteMode", "true"))).Should().BeEquivalentTo([1, 2, 3, 4]);
        StaticBranchResolver.ReachableStepOrders(steps, Values(("liteMode", "false"))).Should().BeEquivalentTo([1, 4]);
    }
}
