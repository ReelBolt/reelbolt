using System.Collections.Generic;
using AwesomeAssertions;
using ReelBolt.WorkflowEngine.Execution;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Characterization tests for <see cref="ExpressionEvaluator"/>. Any NCalc exception is swallowed
/// into <c>false</c>, so an NCalc behaviour change (a major-version bump, say) would silently
/// reroute Conditional/ReviewLoop steps rather than fail loudly. These pin the observable results.
/// </summary>
public class ExpressionEvaluatorTests
{
    [Theory]
    [InlineData("$.score > 8", """{"score":9}""", true)]
    [InlineData("$.score > 8", """{"score":8}""", false)]
    [InlineData("$.score >= 8.5", """{"score":8.5}""", true)]
    [InlineData("$.score < 9", """{"score":7.25}""", true)]
    [InlineData("score > 8", """{"score":9}""", true)]
    [InlineData("$.score == 9", """{"score":9}""", true)]
    [InlineData("$.score != 9", """{"score":9}""", false)]
    [InlineData("$.score > 8 && $.passed", """{"score":9,"passed":true}""", true)]
    [InlineData("$.score > 8 AND $.passed", """{"score":9,"passed":false}""", false)]
    [InlineData("$.score > 8 or $.passed", """{"score":1,"passed":true}""", true)]
    [InlineData("$.status == 'done'", """{"status":"done"}""", true)]
    [InlineData("$.status == 'done'", """{"status":"pending"}""", false)]
    [InlineData("$.result.score > 5", """{"result":{"score":6}}""", true)]
    [InlineData("$.result.score.value > 5 && $.result.score > 1", """{"result":{"score":{"value":6}}}""", false)]
    [InlineData("$.flag", """{"flag":true}""", true)]
    [InlineData("$.flag", """{"flag":false}""", false)]
    [InlineData("($.a + $.b) * 2 > 10", """{"a":3,"b":3}""", true)]
    [InlineData("$.missing > 1", """{"score":9}""", false)]
    [InlineData("this is not an expression (", """{"score":9}""", false)]
    [InlineData("", """{"score":9}""", true)]
    [InlineData("$.ReviewAgent.score > 8", """[{"agentName":"ReviewAgent","output":"{\"score\":9}"}]""", true)]
    [InlineData("$.ReviewAgent.score > 8", """[{"agentName":"ReviewAgent","output":"{\"score\":3}"}]""", false)]
    public void Evaluates_conditions_against_json(string expression, string json, bool expected)
    {
        ExpressionEvaluator.Evaluate(expression, json).Should().Be(expected);
    }

    /// <summary>
    /// The two-argument overload is the one every existing caller uses, so it must keep behaving
    /// exactly as it did before the extra-parameter overload existed — for the whole spread above.
    /// </summary>
    [Theory]
    [InlineData("$.score > 8", """{"score":9}""")]
    [InlineData("$.score > 8", """{"score":8}""")]
    [InlineData("score > 8", """{"score":9}""")]
    [InlineData("$.score > 8 && $.passed", """{"score":9,"passed":true}""")]
    [InlineData("$.status == 'done'", """{"status":"done"}""")]
    [InlineData("$.result.score > 5", """{"result":{"score":6}}""")]
    [InlineData("$.flag", """{"flag":true}""")]
    [InlineData("($.a + $.b) * 2 > 10", """{"a":3,"b":3}""")]
    [InlineData("$.missing > 1", """{"score":9}""")]
    [InlineData("this is not an expression (", """{"score":9}""")]
    [InlineData("", """{"score":9}""")]
    [InlineData("$.ReviewAgent.score > 8", """[{"agentName":"ReviewAgent","output":"{\"score\":9}"}]""")]
    public void Two_argument_overload_is_unchanged_against_the_null_extra_parameter_overload(
        string expression, string json)
    {
        ExpressionEvaluator.Evaluate(expression, json)
            .Should().Be(ExpressionEvaluator.Evaluate(expression, json, null));
    }

    // ---------------------------------------------------------------------
    // Extra parameters (the Decision-mode decision.* set)
    // ---------------------------------------------------------------------

    private static readonly Dictionary<string, object?> DecisionParameters = new()
    {
        ["decision.p_yes"] = 0.9,
        ["decision.p_no"] = 0.1,
        ["decision.confidence"] = 0.9,
        ["decision.escalated"] = false,
    };

    [Theory]
    [InlineData("decision.p_yes >= 0.65", true)]
    [InlineData("$.decision.p_yes >= 0.65", true)]
    [InlineData("decision.p_yes >= 0.95", false)]
    [InlineData("decision.p_no < 0.2", true)]
    [InlineData("decision.confidence > 0.5 && decision.p_yes >= 0.65", true)]
    [InlineData("decision.escalated", false)]
    [InlineData("decision.p_yes >= 0.65 && $.score > 8", true)]
    public void Extra_parameters_are_reachable_by_their_dotted_names(string expression, bool expected)
    {
        ExpressionEvaluator.Evaluate(expression, """{"score":9}""", DecisionParameters)
            .Should().Be(expected);
    }

    [Fact]
    public void An_extra_parameter_colliding_with_a_json_key_resolves_to_the_json_value()
    {
        // The JSON-derived parameter set already owns "decision.p_yes" via the nested object, so
        // the decision parameter of the same name must NOT shadow it: the JSON value wins.
        ExpressionEvaluator.Evaluate(
                "decision.p_yes >= 0.65",
                """{"decision":{"p_yes":0.1}}""",
                new Dictionary<string, object?> { ["decision.p_yes"] = 0.9 })
            .Should().BeFalse();
    }

    [Fact]
    public void An_extra_parameter_absent_from_the_json_is_used()
    {
        ExpressionEvaluator.Evaluate(
                "decision.p_yes >= 0.65",
                """{"decision":{"p_no":0.1}}""",
                new Dictionary<string, object?> { ["decision.p_yes"] = 0.9 })
            .Should().BeTrue();
    }

    [Fact]
    public void A_longer_extra_name_wins_over_a_shorter_json_prefix()
    {
        // Same longest-key-first rewrite discipline the JSON keys already rely on: the dotted
        // extra must be substituted before any shorter JSON key that is its prefix.
        ExpressionEvaluator.Evaluate(
                "decision.p_yes >= 0.65",
                """{"decision":"not-an-object"}""",
                new Dictionary<string, object?> { ["decision.p_yes"] = 0.9 })
            .Should().BeTrue();
    }

    [Fact]
    public void An_empty_extra_parameter_dictionary_behaves_as_the_two_argument_overload()
    {
        ExpressionEvaluator.Evaluate("$.score > 8", """{"score":9}""", new Dictionary<string, object?>())
            .Should().Be(ExpressionEvaluator.Evaluate("$.score > 8", """{"score":9}"""));
    }

    [Fact]
    public void An_unresolvable_dotted_identifier_is_false_not_an_exception()
    {
        ExpressionEvaluator.Evaluate("decision.p_yes >= 0.65", """{"score":9}""")
            .Should().BeFalse();
    }
}
