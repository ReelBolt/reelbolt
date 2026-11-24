using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.AssistantEval;

/// <summary>
/// The app-input eval scorer (docs/design/app-inputs.md 29): the five goals of app-input-eval-goals.json, each with a
/// hand-built passing transcript and a failing one. No model is called; the pinned 50-goal recipe set is untouched.
/// </summary>
public class AppInputEvalScoringTests
{
    private static AppInputEvalGoal Goal(string id) => AppInputEvalScoring.Load().Single(g => g.Id == id);

    private static EvalTranscript Transcript(string reply, params (string Name, string Args, string? Result)[] calls)
    {
        EvalTranscript t = new() { Reply = reply };
        int i = 0;
        foreach ((string name, string args, string? result) in calls)
        {
            string id = "c" + i++;
            t.AddCall(id, name, args);
            if (result is not null) t.SetResult(id, result);
        }
        return t;
    }

    private const string Ok = "{\"confirmationToken\":\"t\"}";

    [Fact]
    public void The_fixture_has_the_five_goals_each_with_a_score_and_the_recipe_set_stays_at_fifty()
    {
        IReadOnlyList<AppInputEvalGoal> goals = AppInputEvalScoring.Load();
        goals.Select(g => g.Id).Should().Equal("AI01", "AI02", "AI03", "AI04", "AI05");
        goals.Should().AllSatisfy(g => g.Score.Proposal.Should().BeOneOf("patch", "patchOrSave", "execute"));
        EvalFixture.Load().Should().HaveCount(50, "the recipe-first gate is pinned and never reads the app-input goals");
    }

    [Fact]
    public void AI01_a_long_text_idea_and_a_shape_each_bound_to_the_clip()
    {
        const string ops = """
            {"workflowId":"w","operations":[
              {"op":"setAppInput","input":{"id":"idea","type":"longText","label":"Video idea","required":true,"example":"A barista pours latte art"}},
              {"op":"bindAppInput","stepOrder":1,"field":"inlinePrompt","inputId":"idea","mode":"placeholder","template":"{{input.idea}}"},
              {"op":"setAppInput","input":{"id":"shape","type":"shape","label":"Shape","choices":["9:16","16:9"]}},
              {"op":"bindAppInput","stepOrder":1,"field":"ratio","inputId":"shape","mode":"value"}]}
            """;
        AppInputEvalScore pass = AppInputEvalScoring.Score(Goal("AI01"),
            Transcript("Done: your team types the idea and picks the shape.", ("ProposePatchWorkflow", ops, Ok), ("ApplyPatchWorkflow", ops, "{}")));
        pass.Passed.Should().BeTrue(string.Join("; ", pass.Misses));

        AppInputEvalScore fail = AppInputEvalScoring.Score(Goal("AI01"),
            Transcript("Done 0c3a1f9e-1111-4222-8333-944455556666.", ("ProposePatchWorkflow", ops.Replace("\"field\":\"ratio\"", "\"field\":\"durationSeconds\""), Ok)));
        fail.Passed.Should().BeFalse();
        fail.Misses.Should().Contain("the proposal was not applied").And.Contain("shows an id in the reply")
            .And.Contain(m => m.StartsWith("missing bindAppInput (ratio"));
    }

    [Fact]
    public void AI02_a_cheaper_mode_switch_read_by_a_condition_that_finishes_or_a_question_first()
    {
        const string save = """
            {"steps":[
              {"stepOrder":1,"stepType":"Conditional","conditionExpression":"input.lite","trueBranchStepOrder":"3","falseBranchStepOrder":"2"},
              {"stepOrder":2,"stepType":"VideoGenerate","videoGenerateConfigJson":"{\"inlinePrompt\":\"x\"}"},
              {"stepOrder":3,"stepType":"Conditional","trueBranchStepOrder":"end","falseBranchStepOrder":"end"}],
             "appManifest":{"version":2,"inputs":[{"kind":"input","id":"lite","type":"toggle","label":"Lite mode","default":false}]}}
            """;
        AppInputEvalScore pass = AppInputEvalScoring.Score(Goal("AI02"),
            Transcript("Added a Lite mode switch.", ("ProposeWorkflow", save, Ok), ("ApplyWorkflow", "{}", "{}")));
        pass.Passed.Should().BeTrue(string.Join("; ", pass.Misses));

        AppInputEvalScore asked = AppInputEvalScoring.Score(Goal("AI02"), Transcript("What may be cheaper: the model, the length or the takes?"));
        asked.Passed.Should().BeTrue();
        asked.Route.Should().Be("clarify_first");

        AppInputEvalScore fail = AppInputEvalScoring.Score(Goal("AI02"),
            Transcript("Added it.", ("ProposeWorkflow", save.Replace("input.lite", "true"), Ok), ("ApplyWorkflow", "{}", "{}")));
        fail.Misses.Should().ContainSingle().Which.Should().StartWith("no Conditional reading input.lite");
    }

    [Fact]
    public void AI03_a_model_field_under_more_options_recommending_the_cheapest_shown()
    {
        const string options = """{"options":[{"id":"m-dear","price":"about 2 times the price"},{"id":"m-cheap","price":"cheapest"}]}""";
        string Ops(string recommended) => $$$"""
            {"operations":[
              {"op":"setAppInput","input":{"id":"videoModel","type":"model","capability":"VideoGeneration","more":true,"recommended":"{{{recommended}}}","label":"Video model"}},
              {"op":"bindAppInput","stepOrder":1,"field":"@model","inputId":"videoModel","mode":"value"}]}
            """;
        AppInputEvalScore pass = AppInputEvalScoring.Score(Goal("AI03"),
            Transcript("People can now pick the video model; the cheapest is recommended.",
                ("GetWorkflow", "{}", options), ("ProposePatchWorkflow", Ops("m-cheap"), Ok), ("ApplyPatchWorkflow", Ops("m-cheap"), "{}")));
        pass.Passed.Should().BeTrue(string.Join("; ", pass.Misses));

        AppInputEvalScore fail = AppInputEvalScoring.Score(Goal("AI03"),
            Transcript("The dear one costs 12 credits.",
                ("GetWorkflow", "{}", options), ("ProposePatchWorkflow", Ops("m-dear"), Ok), ("ApplyPatchWorkflow", Ops("m-dear"), "{}")));
        fail.Misses.Should().Contain("quotes a price in credits or money").And.Contain(m => m.Contains("recommends the cheapest"));
    }

    [Fact]
    public void AI04_runs_lite_mode_with_the_exact_words_and_no_egress()
    {
        const string run = """{"workflowId":"w","brief":{"inputs":{"liteMode":true,"whatTheySay":"“Welcome to Caffè Lume.”"}}}""";
        AppInputEvalScoring.Score(Goal("AI04"), Transcript("Started.", ("ExecuteWorkflow", run, "{}"))).Passed.Should().BeTrue();

        AppInputEvalScore fail = AppInputEvalScoring.Score(Goal("AI04"), Transcript("Started.",
            ("ExecuteWorkflow", """{"brief":{"inputs":{"liteMode":false,"whatTheySay":"Welcome!"}},"allowSourceMediaEgress":true}""", "{}")));
        fail.Misses.Should().HaveCount(3);
    }

    [Fact]
    public void AI05_removes_the_music_volume_field_and_nothing_else()
    {
        const string ops = """{"operations":[{"op":"removeAppInput","id":"musicVolume"}]}""";
        AppInputEvalScoring.Score(Goal("AI05"), Transcript("Removed.", ("ProposePatchWorkflow", ops, Ok), ("ApplyPatchWorkflow", ops, "{}")))
            .Passed.Should().BeTrue();

        const string more = """{"operations":[{"op":"removeAppInput","id":"musicVolume"},{"op":"removeAppInput","id":"ending"}]}""";
        AppInputEvalScoring.Score(Goal("AI05"), Transcript("Removed.", ("ProposePatchWorkflow", more, Ok), ("ApplyPatchWorkflow", more, "{}")))
            .Misses.Should().Equal("also removes ending");
    }
}
