using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows.Apps;
using ReelBolt.Shared.Workflows.ChangeRequests;
using Xunit;
using static ReelBolt.WorkflowEngine.Tests.AppInputs.AppInputsTestKit;

namespace ReelBolt.WorkflowEngine.Tests.AppInputs;

/// <summary>Lane L9 (docs/design/app-inputs.md 10.2, 25): the pure seeds and refusals of an app-input change.</summary>
public class AppInputChangePlannerTests
{
    private static readonly System.Guid Writer = System.Guid.NewGuid();

    private static readonly AppManifest App = Manifest(
        Input("prompt", "longText", "Prompt"),
        Input("audience", "shortText", "Audience"),
        Input("liteMode", "toggle", "Lite mode"),
        Input("length", "number", "Length"),
        Input("unused", "shortText", "Unused")) with
    {
        RequestTemplate = "For {{input.audience}}."
    };

    private static readonly IReadOnlyList<AppBindingStep> Steps =
    [
        Step(1, StepType.Agent, agent: Writer),
        Step(2, StepType.Conditional, condition: "input.liteMode", trueBranch: "5", falseBranch: "3"),
        Step(3, StepType.VideoGenerate, """{"inlinePrompt":"{{input.prompt}}"}"""),
        Step(4, StepType.Conditional, trueBranch: "end", falseBranch: "end"),
        Step(5, StepType.VideoGenerate, """{"inlinePrompt":"Lite {{input.prompt}}"}"""),
        Step(6, StepType.Conditional, condition: "input.length > 5 and previous.score > 3", trueBranch: "7", falseBranch: "7"),
        Step(7, StepType.VideoGenerate, """{"inlinePrompt":"x"}"""),
    ];

    private static Dictionary<string, JsonElement> Before => Values(("prompt", "\"a\""), ("audience", "\"b\""), ("liteMode", "false"), ("length", "4"));

    private static Dictionary<string, JsonElement> With(string id, string raw)
    {
        Dictionary<string, JsonElement> v = Before;
        v[id] = J(raw);
        return v;
    }

    [Fact]
    public void Nothing_changed_is_refused()
    {
        AppInputChangePlanner.Decide(App, Steps, Before, Before).RefusalCode.Should().Be(AppInputChangeCodes.Unchanged);
    }

    [Fact]
    public void A_placeholder_change_seeds_only_the_steps_on_the_path_that_use_it()
    {
        AppInputChangeDecision d = AppInputChangePlanner.Decide(App, Steps, Before, With("prompt", "\"c\""));

        d.Refused.Should().BeFalse();
        d.Changed.Should().Equal("prompt");
        d.Seeds.Should().BeEquivalentTo([3], "step 5 uses it too but is off the path");
    }

    [Fact]
    public void A_request_template_change_seeds_the_steps_that_read_the_request()
    {
        AppInputChangePlanner.Decide(App, Steps, Before, With("audience", "\"d\"")).Seeds.Should().BeEquivalentTo([1]);
    }

    [Fact]
    public void A_value_that_flips_an_input_decided_branch_is_refused()
    {
        AppInputChangeDecision d = AppInputChangePlanner.Decide(App, Steps, Before, With("liteMode", "true"));

        d.RefusalCode.Should().Be(AppInputChangeCodes.ChangesBranch);
        d.InputId.Should().Be("liteMode");
    }

    [Fact]
    public void A_value_read_by_a_condition_the_values_alone_do_not_decide_is_refused()
    {
        AppInputChangePlanner.Decide(App, Steps, Before, With("length", "9")).RefusalCode.Should().Be(AppInputChangeCodes.ChangesBranch);
    }

    [Fact]
    public void A_field_nothing_uses_is_refused()
    {
        AppInputChangeDecision d = AppInputChangePlanner.Decide(App, Steps, Before, With("unused", "\"x\""));

        d.RefusalCode.Should().Be(AppInputChangeCodes.NotUsed);
        d.InputId.Should().Be("unused");
    }

    [Fact]
    public void An_agent_model_field_seeds_that_agents_steps()
    {
        AppManifest app = App with { Inputs = [.. App.Inputs!, Input("scriptModel", "model", "Script model", capability: "Chat", agent: Writer)] };

        AppInputChangePlanner.Decide(app, Steps, Before, With("scriptModel", $"\"{System.Guid.NewGuid()}\"")).Seeds.Should().BeEquivalentTo([1]);
    }

    [Fact]
    public void Branch_changing_values_of_a_switch_are_listed()
    {
        AppManifestInput lite = App.Inputs!.Single(i => i.Id == "liteMode");
        AppInputChangePlanner.BranchChangingValues(lite, Steps, Before)!.Select(v => v.GetRawText()).Should().Equal("true");
        AppInputChangePlanner.BranchChangingValues(App.Inputs!.Single(i => i.Id == "prompt"), Steps, Before).Should().BeEmpty();
        AppInputChangePlanner.BranchChangingValues(App.Inputs!.Single(i => i.Id == "length"), Steps, Before).Should().BeNull();
    }

    [Fact]
    public void Stored_rows_round_trip_and_are_never_read_as_step_overrides()
    {
        string json = AppInputChangeRows.Serialize(Values(("b", "2"), ("a", "\"x\"")));

        json.Should().Be("""{"@appInputs":{"values":{"a":"x","b":2}}}""");
        AppInputChangeRows.IsAppInputRow(json).Should().BeTrue();
        AppInputChangeRows.Values(json)!.Keys.Should().Equal("a", "b");
        AppInputChangeRows.IsAppInputRow("""{"vo1":{"text":"hi"}}""").Should().BeFalse();
        AppInputChangeRows.IsAppInputRow(null).Should().BeFalse();
        AppInputChangeRows.IsAppInputRow("{not json @appInputs").Should().BeFalse();
    }
}
