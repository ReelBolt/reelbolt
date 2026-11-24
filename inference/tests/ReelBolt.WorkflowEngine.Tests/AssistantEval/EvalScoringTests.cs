using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.AssistantEval;

/// <summary>Scoring rules, one hand-built transcript at a time (QA review of the eval harness, findings 1 to 10).</summary>
public class EvalScoringTests
{
    private static readonly Dictionary<AgentType, Guid> AgentIds =
        Enum.GetValues<AgentType>().ToDictionary(t => t, _ => Guid.NewGuid());

    private static string? TypeOf(Guid id) => AgentIds.Where(p => p.Value == id).Select(p => p.Key.ToString()).FirstOrDefault();

    private static EvalGoal Goal(string id) => EvalFixture.Load().Single(g => g.Id == id);

    private static string StepsJson(string templateKey)
    {
        WorkflowTemplateDefinition t = WorkflowTemplateCatalog.GetAll().Single(x => x.Key == templateKey);
        var steps = new List<Dictionary<string, object?>>();
        for (int i = 0; i < t.Steps.Count; i++)
        {
            var d = new Dictionary<string, object?> { ["stepOrder"] = i + 1 };
            foreach (PropertyInfo p in t.Steps[i].GetType().GetProperties())
            {
                object? v = p.GetValue(t.Steps[i]);
                if (v is null) continue;
                string name = char.ToLowerInvariant(p.Name[0]) + p.Name[1..];
                if (p.Name == "AgentType") d["agentDefinitionId"] = AgentIds[(AgentType)v];
                else if (v is Enum e) d[name] = e.ToString();
                else if (v is string or int or double or bool) d[name] = v;
            }
            steps.Add(d);
        }
        return JsonSerializer.Serialize(new { name = "x", steps });
    }

    private static EvalTranscript Transcript(string reply, params (string Name, string Args, string? Result)[] calls)
    {
        var t = new EvalTranscript { Reply = reply };
        int i = 0;
        foreach ((string name, string args, string? result) in calls)
        {
            string id = "c" + i++;
            t.AddCall(id, name, args);
            if (result is not null) t.SetResult(id, result);
        }
        return t;
    }

    private static EvalTranscript Proposing(string templateKey, string reply = "Done. Shall I apply it?") =>
        Transcript(reply, ("ProposeWorkflow", StepsJson(templateKey), "{\"confirmationToken\":\"t\",\"diff\":{}}"));

    [Fact]
    public void Approval_prompts_and_offers_are_not_clarifying_questions_but_real_ones_are()
    {
        EvalScoring.CountClarifyingQuestions("Done. Shall I apply it?").Should().Be(0);
        EvalScoring.CountClarifyingQuestions("Want me to run it now?").Should().Be(0);
        EvalScoring.CountClarifyingQuestions("Which language should the video be in? How long should it be?").Should().Be(2);
        EvalScoring.CountClarifyingQuestions("Would you like a landscape or a vertical video?").Should().Be(1);
    }

    [Fact]
    public void A_proposal_with_an_approval_question_a_decline_with_an_offer_and_a_real_clarify_route_correctly()
    {
        EvalObservation proposal = EvalScoring.Observe(Proposing("quick-win-promo-ds"), TypeOf);
        proposal.Questions.Should().Be(0);
        proposal.Route.Should().Be("template");

        EvalObservation decline = EvalScoring.Observe(
            Transcript("ReelBolt doesn't resize a finished video. The closest is re-running the template in Landscape. Want me to set that up?"), TypeOf);
        decline.Route.Should().Be("decline_with_alternative");
        EvalScoring.Score(Goal("G44"), decline, "x").RouteOk.Should().BeTrue();

        EvalObservation clarify = EvalScoring.Observe(Transcript("Which product is it for? How long should it be?"), TypeOf);
        clarify.Route.Should().Be("clarify_first");
        clarify.Questions.Should().Be(2);
    }

    [Fact]
    public void Only_a_key_used_as_a_template_key_can_be_hallucinated()
    {
        EvalObservation prose = EvalScoring.Observe(Transcript("I'll do a green-screen insert, a quick-win promo style."), TypeOf);
        prose.HallucinatedKeys.Should().BeEmpty();

        EvalObservation arg = EvalScoring.Observe(
            Transcript("ok", ("GetWorkflowTemplate", "{\"templateKey\":\"video-derush-edit-turbo\"}", "{\"error\":\"template not found\"}")), TypeOf);
        arg.HallucinatedKeys.Should().Equal("video-derush-edit-turbo");

        EvalScoring.Observe(Transcript("Try `quick-win-promo-ultra` for this."), TypeOf).HallucinatedKeys.Should().Equal("quick-win-promo-ultra");
        EvalScoring.Observe(Transcript("Try `quick-win-promo-ds`."), TypeOf).HallucinatedKeys.Should().BeEmpty();
    }

    [Fact]
    public void A_rejected_proposal_is_not_a_proposal()
    {
        EvalObservation o = EvalScoring.Observe(
            Transcript("I could not save it.", ("ProposeWorkflow", StepsJson("quick-win-promo-ds"), "{\"error\":\"invalid step\"}")), TypeOf);
        o.HasProposal.Should().BeFalse();
        o.Route.Should().Be("decline_with_alternative");
    }

    [Fact]
    public void An_errored_sample_earns_nothing_even_where_a_decline_would_have_passed()
    {
        var t = Transcript("");
        t.Error = "HttpRequestException (HTTP 429)";
        EvalObservation o = EvalScoring.Observe(t, TypeOf);
        o.Failed.Should().BeTrue();
        EvalSampleScore s = EvalScoring.Score(Goal("G44"), o, "");
        s.Failed.Should().BeTrue();
        s.RouteOk.Should().BeFalse();
        s.QuestionPolicyOk.Should().BeFalse();
    }

    [Theory]
    [InlineData("G39", "green-screen-insert")]
    [InlineData("G19", "video-derush-edit-grade-room")]
    public void Templates_that_share_an_agent_sequence_are_told_apart_by_step_type_and_configuration(string goalId, string key)
    {
        EvalObservation o = EvalScoring.Observe(Proposing(key), TypeOf);
        o.Route.Should().Be("template");
        o.TemplateKey.Should().Be(key, "catalog order must not decide a tie");
        EvalScoring.Score(Goal(goalId), o, "x").TemplateOk.Should().BeTrue();
    }

    private static string FillInputs(string stepsJson, Func<string, string> fill)
    {
        System.Text.Json.Nodes.JsonNode root = System.Text.Json.Nodes.JsonNode.Parse(stepsJson)!;
        foreach (System.Text.Json.Nodes.JsonNode? step in root["steps"]!.AsArray())
            foreach (string key in step!.AsObject().Select(p => p.Key).Where(k => k.EndsWith("ConfigJson", StringComparison.Ordinal)).ToList())
                step[key] = fill(step[key]!.GetValue<string>());
        return root.ToJsonString();
    }

    [Fact]
    public void Filling_a_templates_declared_inputs_is_still_the_template_but_changing_a_setting_is_a_patch()
    {
        // G39: the clip and the content are the template's declared inputs (placeholders are replaced by real values).
        string filled = FillInputs(StepsJson("green-screen-insert"), c => c
            .Replace("\"source\":{\"kind\":\"ProjectFile\"}", "\"source\":{\"kind\":\"ProjectFile\",\"projectFileId\":\"" + Guid.NewGuid() + "\"}")
            .Replace("\"enableInserts\":true", "\"enableInserts\":true,\"insertContentRef\":{\"from\":\"ProjectFile\",\"projectFileId\":\"" + Guid.NewGuid() + "\"}"));
        filled.Should().Contain("insertContentRef");
        EvalObservation g39 = EvalScoring.Observe(
            Transcript("ok", ("ProposeWorkflow", filled, "{\"confirmationToken\":\"t\"}")), TypeOf);
        g39.Route.Should().Be("template");
        g39.TemplateKey.Should().Be("green-screen-insert");

        // G37 / G38: the object to blur is typed by the customer (tracked label and effect target).
        string blurred = FillInputs(StepsJson("video-region-effect"), c => c.Replace("the speedometer", "the licence plate"));
        EvalObservation g37 = EvalScoring.Observe(
            Transcript("ok", ("ProposeWorkflow", blurred, "{\"confirmationToken\":\"t\"}")), TypeOf);
        g37.Route.Should().Be("template");
        g37.TemplateKey.Should().Be("video-region-effect");
        EvalScoring.Score(Goal("G37"), g37, "x").TemplateOk.Should().BeTrue();

        // A real setting change (not a declared input) is still a patch.
        string reencode = FillInputs(StepsJson("video-region-effect"), c => c.Replace("\"Blur\"", "\"Pixelate\""));
        EvalScoring.Observe(Transcript("ok", ("ProposeWorkflow", reencode, "{\"confirmationToken\":\"t\"}")), TypeOf)
            .Route.Should().Be("template_with_patch");
    }

    [Fact]
    public void Rewiring_a_source_is_a_patch_even_though_the_clip_it_points_at_is_a_declared_input()
    {
        string rewired = FillInputs(StepsJson("green-screen-insert"), c => c.Replace("\"source\":{\"kind\":\"ProjectFile\"}", "\"source\":{\"kind\":\"PreviousStepOutput\"}"));
        rewired.Should().Contain("PreviousStepOutput");
        EvalScoring.Observe(Transcript("ok", ("ProposeWorkflow", rewired, "{\"confirmationToken\":\"t\"}")), TypeOf)
            .Route.Should().Be("template_with_patch");
    }

    [Fact]
    public void The_shipped_subtitles_template_scores_as_a_template_goal()
    {
        EvalGoal g40 = Goal("G40");
        g40.IsPlanned.Should().BeFalse();
        EvalObservation o = EvalScoring.Observe(Proposing("video-subtitles"), TypeOf);
        o.TemplateKey.Should().Be("video-subtitles");
        EvalScoring.Score(g40, o, "x").TemplateOk.Should().BeTrue();
    }

    [Fact]
    public void A_changed_setting_is_a_patch_and_a_graph_unlike_any_template_is_custom()
    {
        string json = StepsJson("quick-win-promo-ds").Replace("\"stepOrder\":1", "\"stepOrder\":1,\"videoAnalyzeConfigJson\":\"{\\\"x\\\":1}\"");
        EvalObservation patched = EvalScoring.Observe(
            Transcript("ok", ("ProposeWorkflow", json, "{\"confirmationToken\":\"t\"}")), TypeOf);
        patched.Route.Should().Be("template_with_patch");

        var one = JsonSerializer.Serialize(new { steps = new[] { new { stepOrder = 1, agentDefinitionId = AgentIds[AgentType.Assistant], stepType = "Agent" } } });
        EvalScoring.Observe(Transcript("ok", ("ProposeWorkflow", one, "{\"confirmationToken\":\"t\"}")), TypeOf).Route.Should().Be("custom_graph");
    }

    [Fact]
    public void Formats_voice_and_invented_fields_are_scored_as_the_spec_says()
    {
        EvalScoring.NormalizeFormat("Portrait").Should().NotBe(EvalScoring.NormalizeFormat("Vertical"));
        EvalScoring.NormalizeFormat("9:16").Should().Be(EvalScoring.NormalizeFormat("Vertical"));

        EvalGoal voiceGoal = EvalFixture.Load().Single(g => g.Expected.Brief.TryGetValue("voice", out var v) && v.GetString() == "project-voice");
        var brief = new Dictionary<string, string> { ["voice"] = "narrated" };
        var withoutId = new EvalObservation("template", null, true, brief, 0, [], [], new HashSet<string>(), false, []);
        EvalScoring.Score(voiceGoal, withoutId, "x").BriefFields["voice"].Should().BeFalse();
        var withId = withoutId with { Brief = new Dictionary<string, string> { ["voice"] = "narrated", ["voiceId"] = Guid.NewGuid().ToString() } };
        EvalScoring.Score(voiceGoal, withId, "x").BriefFields["voice"].Should().BeTrue();
    }

    [Fact]
    public async Task The_real_linter_runs_on_the_first_proposal_with_the_goals_providers_and_plan()
    {
        var agents = AgentIds.ToDictionary(p => p.Value, p => new AgentDefinition { Id = p.Value, AgentType = p.Key, Name = p.Key.ToString(), IsBuiltIn = true });
        string steps = StepsJson("video-derush-edit-broll");
        var none = new Dictionary<string, string>();

        var g30 = await EvalLinting.LintAsync(Goal("G30").Project, null, steps, agents, none);
        var g28 = await EvalLinting.LintAsync(Goal("G28").Project, null, steps, agents, none);
        var g29 = await EvalLinting.LintAsync(Goal("G29").Project, null, steps, agents, none);

        g30.Should().Contain("WL006", "G30 has no video-generation provider");
        g28.Should().NotContain("WL006");
        g29.Should().Contain("WL007", "the Trial plan does not allow video generation");
        g28.Should().NotContain("WL007");
    }

    [Fact]
    public void Majority_aggregation_report_rates_and_the_budget_stop_work_together()
    {
        EvalGoal goal = Goal("G44");
        EvalSampleScore Score(bool route, bool failed = false) => new(route, null, new Dictionary<string, bool>(), 0,
            new Dictionary<string, bool>(), true, false, null, false, false, false, failed);
        EvalObservation Obs(string route) => new(route, null, false, new Dictionary<string, string>(), 0, [], [], new HashSet<string>(), false, []);
        EvalSampleResult Sample(int n, bool route, bool failed = false) =>
            new(goal.Id, n, Obs(route ? goal.EffectiveRoute : "template"), Score(route, failed), "r", [], 100, 10, 1, 1, failed ? "HttpRequestException" : null);

        var run = new EvalRunResult([goal], [Sample(1, true), Sample(2, false), Sample(3, true)], 3, "DeepSeek", "m", "basis",
            0.000001m, 0.000002m, true, "token cap of 10 would be exceeded", DateTime.UtcNow, 1, "BM25");
        IReadOnlyList<EvalGoalResult> goals = EvalReport.PerGoal(run);
        goals.Single().RouteOk.Should().BeTrue("two of three samples passed: one flake is not a miss");

        var flaky = run with { Samples = [Sample(1, true), Sample(2, false), Sample(3, false, failed: true)] };
        EvalReport.PerGoal(flaky).Single().RouteOk.Should().BeFalse();
        EvalReport.PerGoal(flaky).Single().Errors.Should().Be(1);

        EvalRate route = EvalReport.Rates(goals).Single(r => r.Name == "Route accuracy");
        (route.Passed, route.Total).Should().Be((1, 1));
        route.Low.Should().BeLessThan(route.Value);

        string md = EvalReport.ToMarkdown(run);
        md.Should().Contain("STOPPED EARLY").And.Contain("Route accuracy").And.Contain("G44");
        JsonDocument.Parse(EvalReport.ToJson(run)).RootElement.GetProperty("meta").GetProperty("stoppedEarly").GetBoolean().Should().BeTrue();
    }
}
