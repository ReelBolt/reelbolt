using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.AI;
using ReelBolt.Shared.Workflows;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.AssistantEval;

/// <summary>Marks the one test that calls a real model. Skipped unless REELBOLT_ASSISTANT_EVAL=1.</summary>
public sealed class EvalRunFactAttribute : FactAttribute
{
    public EvalRunFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("REELBOLT_ASSISTANT_EVAL") != "1")
            Skip = "assistant eval run: set REELBOLT_ASSISTANT_EVAL=1 (opt-in; calls a real model and costs money)";
    }
}

/// <summary>
/// The assistant eval harness (docs/design/workflow-apps/assistant-eval-results.md). The tests below drive the REAL
/// assistant agent, system prompt and tool registration with a scripted model, so they cost nothing and call no
/// provider; the single real-model run is <see cref="Run_the_baseline_against_a_real_model"/>, gated by environment.
/// </summary>
public class AssistantEvalHarnessTests
{
    private static readonly Lazy<Bm25DocsSearch> Docs = new(() => new Bm25DocsSearch(Bm25DocsSearch.LocateDocsRoot()));

    [Fact]
    public void Fixture_loads_fifty_goals_and_scores_a_planned_template_as_a_decline()
    {
        IReadOnlyList<EvalGoal> goals = EvalFixture.Load();
        goals.Should().HaveCount(50);
        goals.Select(g => g.Id).Should().OnlyHaveUniqueItems();
        EvalFixture.LoadPlannedKeys().Should().AllSatisfy(k => ReelBolt.Shared.Workflows.WorkflowTemplateCatalog.GetAll()
            .Select(t => t.Key).Should().NotContain(k, "a template that shipped must leave plannedTemplates"));

        // Real goals: planned only when the fixture says so. G40 (subtitles only) shipped and is a normal template goal.
        EvalGoal shipped = goals.Single(g => g.Id == "G40");
        shipped.IsPlanned.Should().BeFalse();
        shipped.EffectiveRoute.Should().Be("template");

        // The planned-template path, covered with a synthetic goal so it does not depend on the fixture.
        EvalGoal planned = shipped with { Expected = shipped.Expected with { Availability = "planned:video-future" } };
        planned.IsPlanned.Should().BeTrue();
        planned.EffectiveRoute.Should().Be("decline_with_alternative");
    }

    [Fact]
    public void Wilson_interval_and_majority_follow_the_documented_arithmetic()
    {
        (double lo, double hi) = EvalStats.Wilson(85, 100);
        lo.Should().BeApproximately(0.7672, 0.002);
        hi.Should().BeApproximately(0.9069, 0.002);
        EvalStats.Wilson(0, 0).Should().Be((0.0, 0.0));
        EvalStats.Majority(2, 3).Should().BeTrue();
        EvalStats.Majority(1, 3).Should().BeFalse();
        EvalStats.Majority(1, 1).Should().BeTrue();
    }

    [Fact]
    public async Task Docs_stand_in_retrieves_from_the_shipped_corpus()
    {
        Docs.Value.ChunkCount.Should().BeGreaterThan(100);
        var result = await Docs.Value.SearchAsync("dub my video into another language", 3, CancellationToken.None);
        result.Hits.Should().NotBeEmpty();
    }

    [Fact]
    public async Task A_template_proposal_through_the_real_pipeline_is_observed_scored_and_counted()
    {
        EvalGoal goal = EvalFixture.Load().Single(g => g.Id == "G13");

        // Turn 1: look the template up. Turn 2: propose its steps unchanged. Turn 3: answer.
        var model = new ScriptedChatClient((n, messages) => n switch
        {
            0 => Call("c1", "GetWorkflowTemplate", new() { ["templateKey"] = "quick-win-promo-ds" }),
            1 => Call("c2", "ProposeWorkflow", new()
            {
                ["name"] = "Italian version",
                ["steps"] = StepsFrom(messages, "c1"),
            }),
            _ => new ChatResponse(new ChatMessage(ChatRole.Assistant, "Done: it will cost about 12 credits. Want me to run it?")),
        });

        EvalSampleResult r = await EvalRunner.RunSampleAsync(goal, 1, model, Docs.Value, TimeSpan.FromMinutes(2), CancellationToken.None);

        r.Error.Should().BeNull();
        r.Calls.Select(c => c.Name).Should().Equal("GetWorkflowTemplate", "ProposeWorkflow");
        r.Calls[0].Result.Should().Contain("quick-win-promo-ds");
        r.Observation.Route.Should().Be("template");
        r.Observation.TemplateKey.Should().Be("quick-win-promo-ds");
        r.Score.RouteOk.Should().BeTrue();
        r.Score.UnneededQuestion.Should().BeFalse("\"Want me to run it?\" is an approval prompt, not a clarifying question");
        r.Score.TemplateOk.Should().BeTrue();
        r.Score.Hallucinated.Should().BeFalse();
        r.Score.UnrequestedPaid.Should().BeFalse();
        r.ModelRequests.Should().Be(3);
        r.InputTokens.Should().Be(300);
        r.OutputTokens.Should().Be(60);
    }

    [Fact]
    public async Task Zero_tolerance_rows_fire_on_a_made_up_key_a_shown_id_and_a_paid_step_nobody_asked_for()
    {
        EvalGoal goal = EvalFixture.Load().Single(g => g.Id == "G13"); // paid=false, no voice
        var model = new ScriptedChatClient((n, _) => n switch
        {
            0 => Call("c1", "GetWorkflowTemplate", new() { ["templateKey"] = "video-magic-localiser" }),
            _ => new ChatResponse(new ChatMessage(ChatRole.Assistant,
                $"Use `video-derush-edit-turbo`. Project {Guid.NewGuid()} is ready. Which language?")),
        });

        EvalSampleResult r = await EvalRunner.RunSampleAsync(goal, 1, model, Docs.Value, TimeSpan.FromMinutes(2), CancellationToken.None);

        r.Score.Hallucinated.Should().BeTrue();
        r.Observation.HallucinatedKeys.Should().Contain(["video-magic-localiser", "video-derush-edit-turbo"]);
        r.Score.IdShown.Should().BeTrue();
        r.Score.UnneededQuestion.Should().BeTrue("G13 is a template goal and the reply asked a question");
        r.Score.RouteOk.Should().BeFalse();

        // And the paid-step row, from a proposal that adds a Voiceover to a goal that wants no voice.
        var paid = new EvalObservation("template_with_patch", "quick-win-promo-ds", true, new Dictionary<string, string>(),
            0, [], [], new HashSet<string>(), false, ["Voiceover"]);
        EvalScoring.Score(goal, paid, "").UnrequestedPaid.Should().BeTrue();
    }

    [Fact]
    public void Brief_fields_score_exactly_with_tolerance_on_length_and_extras_count_as_one_miss()
    {
        EvalGoal g01 = EvalFixture.Load().Single(g => g.Id == "G01");
        var brief = new Dictionary<string, string>
        {
            ["language"] = "it",
            ["format"] = "16:9",
            ["resolution"] = "1080p",
            ["lengthSec"] = "64",
            ["designSystem"] = "project",
            ["voice"] = "narrated",
            ["stylePreset"] = "bold"
        };
        var obs = new EvalObservation("template_with_patch", "quick-win-promo-ds", true, brief, 0, [], [], new HashSet<string> { "WL016" }, false, []);
        EvalSampleScore s = EvalScoring.Score(g01, obs, "It costs 20 credits.");
        s.BriefFields.Values.Should().AllBeEquivalentTo(true);
        s.BriefExtras.Should().Be(1, "stylePreset is not in the expected brief");
        s.Lint["WL016"].Should().BeTrue();
        s.Lint["WL002"].Should().BeFalse();
        s.PaidDisclosed.Should().BeTrue();
    }

    [EvalRunFact]
    public async Task Run_the_baseline_against_a_real_model()
    {
        EvalModelSpec spec = EvalModelSpec.FromEnvironment();
        EvalSettings settings = EvalSettings.From(Environment.GetEnvironmentVariable, spec);
        string? only = Environment.GetEnvironmentVariable("REELBOLT_EVAL_GOAL_IDS");
        IReadOnlyList<EvalGoal> goals = EvalFixture.Load()
            .Where(g => string.IsNullOrWhiteSpace(only) || only.Split(',').Contains(g.Id)).ToList();
        // "baseline" is the assistant as merged today; after lane 3a run the same command with REELBOLT_EVAL_LABEL=after.
        string outDir = settings.OutDir;
        EvalRunResult run = await EvalRunner.RunAsync(goals, settings.Samples, spec, spec.CreateClient(), Docs.Value, settings.Budget,
            settings.Parallelism, TimeSpan.FromMinutes(4), outDir, CancellationToken.None);

        Directory.CreateDirectory(outDir);
        await File.WriteAllTextAsync(Path.Combine(outDir, "results.json"), EvalReport.ToJson(run));
        await File.WriteAllTextAsync(Path.Combine(outDir, "results.md"), EvalReport.ToMarkdown(run));
        run.Samples.Should().NotBeEmpty();
    }

    // ---- script helpers ------------------------------------------------------------------------------

    private static ChatResponse Call(string id, string name, Dictionary<string, object?> args) =>
        new(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(id, name, args)]));

    /// <summary>The "steps" array of a GetWorkflowTemplate result, handed back as a proposal argument.</summary>
    private static JsonElement StepsFrom(IReadOnlyList<ChatMessage> messages, string callId)
    {
        FunctionResultContent result = messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Single(r => r.CallId == callId);
        string text = result.Result is JsonElement { ValueKind: JsonValueKind.String } s ? s.GetString()! : result.Result!.ToString()!;
        using JsonDocument doc = JsonDocument.Parse(text);
        return doc.RootElement.GetProperty("steps").Clone();
    }
}
