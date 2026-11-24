using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.Lint;
using ReelBolt.WorkflowEngine.Tests.Controllers;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Agents;

/// <summary>SEC-WAB-ASST-8: the footage-sharing consent is the customer's, given in the app; the assistant never turns it on.</summary>
public class AssistantEgressConsentGuardTests
{
    private const string Off = """{"model":"m","allowSourceMediaEgress":false}""";
    private const string On = """{"model":"m","allowSourceMediaEgress":true}""";
    private static readonly Guid Agent = Guid.NewGuid();

    private static CreateWorkflowStepRequest Req(int order, string? analyze = null, string? generate = null, string? type = null, Guid? agent = null) =>
        new(agent ?? Agent, order, null, "s" + order, StepType: type ?? "VideoAnalyze", VideoAnalyzeConfigJson: analyze, VideoGenerateConfigJson: generate);

    private static CreateWorkflowStepRequest Stored(int order, string? analyze = null) => Req(order, analyze);

    private static bool Refuses(IReadOnlyList<CreateWorkflowStepRequest> proposed, IReadOnlyList<CreateWorkflowStepRequest>? baseline) =>
        AssistantEgressConsentGuard.Evaluate(proposed, baseline).Refusal is not null;

    private static AssistantEgressConsentGuard.Outcome Eval(IReadOnlyList<CreateWorkflowStepRequest> proposed, IReadOnlyList<CreateWorkflowStepRequest>? baseline) =>
        AssistantEgressConsentGuard.Evaluate(proposed, baseline);

    private static bool On1(AssistantEgressConsentGuard.Outcome o, int index = 0) =>
        AssistantEgressConsentGuard.HasConsent(o.Steps[index].VideoAnalyzeConfigJson!) == true;

    [Fact]
    public void Never_consented_off_or_absent_to_on_is_refused_on_edit_and_on_create()
    {
        Refuses([Req(1, On)], [Stored(1, Off)]).Should().BeTrue();
        Refuses([Req(1, On)], [Stored(1, """{"model":"m"}""")]).Should().BeTrue();
        Refuses([Req(1, On)], [Stored(1, null)]).Should().BeTrue();
        Refuses([Req(1, On)], null).Should().BeTrue("a new workflow with the flag on is absent -> true");
        Refuses([Req(1, null, On)], [Stored(1, On)]).Should().BeTrue("a different config's grant is not the same grant");
    }

    [Fact]
    public void An_ImageGenerate_step_cannot_have_its_egress_consent_flipped_on_by_the_assistant()
    {
        static CreateWorkflowStepRequest Image(string json) =>
            new(Agent, 1, null, "img", StepType: "ImageGenerate", ImageGenerateConfigJson: json);
        const string off = """{"mode":"Edit","instruction":"x","allowSourceMediaEgress":false}""";
        const string on = """{"mode":"Edit","instruction":"x","allowSourceMediaEgress":true}""";

        Refuses([Image(on)], [Image(off)]).Should().BeTrue();
        Refuses([Image(on)], null).Should().BeTrue("a new step with the flag on was never consented to");
        Refuses([Image(on)], [Image(on)]).Should().BeFalse("an existing true that stays true is left alone");
        Refuses([Image(off)], [Image(on)]).Should().BeFalse("turning it off is always allowed");
    }

    [Fact]
    public void An_unchanged_proposal_keeps_true_and_off_is_allowed()
    {
        Eval([Req(1, On)], [Stored(1, On)]).Refusal.Should().BeNull();
        On1(Eval([Req(1, On)], [Stored(1, On)])).Should().BeTrue();
        Refuses([Req(1, Off)], [Stored(1, On)]).Should().BeFalse();
        Refuses([Req(1, Off), Req(2, null)], null).Should().BeFalse();
    }

    [Fact]
    public void Key_is_matched_case_insensitively_at_any_depth_and_unparseable_json_fails_closed()
    {
        Refuses([Req(1, """{"ALLOWSOURCEMEDIAEGRESS":true}""")], null).Should().BeTrue();
        Refuses([Req(1, """{"a":[{"b":{"AllowSourceMediaEgress":true}}]}""")], null).Should().BeTrue();
        Refuses([Req(1, """{"allowSourceMediaEgress":"true"}""")], null).Should().BeTrue();
        Refuses([Req(1, """{"allowSourceMediaEgress": tru""")], [Stored(1, On)]).Should().BeTrue();
    }

    [Fact]
    public void The_finding_returned_to_the_model_carries_no_AllowEgress_fix_but_the_app_still_does()
    {
        var finding = new LintFinding("WL015", LintSeverity.Error, 1, "message", new LintFix(LintFixKind.AllowEgress, "Allow"));
        LintFindingResponse.ForAssistant(finding).Fix.Should().BeNull();
        LintFindingResponse.ForAssistant(finding).RuleId.Should().Be("WL015");
        LintFindingResponse.From(finding).Fix!.Kind.Should().Be("allowEgress");
        var other = new LintFinding("WL010", LintSeverity.Error, 1, "m", new LintFix(LintFixKind.ChooseVoice, "Pick"));
        LintFindingResponse.ForAssistant(other).Fix.Should().NotBeNull();
        AssistantLintPolicy.ProjectStateRules.Should().Contain("WL015", "it stays reported as needed before running");
    }

    [Fact]
    public void Only_the_broll_recipe_declares_the_grant_and_the_assistant_path_resets_it()
    {
        Dictionary<AgentType, Guid> agents = Enum.GetValues<AgentType>().ToDictionary(t => t, _ => Agent);
        List<string> declaring = [];
        foreach (WorkflowTemplateDefinition template in WorkflowTemplateCatalog.GetAll())
        {
            var steps = RecipeInstance.Build(template, [], null, agents).Steps!;
            if (Refuses(steps, null))
            {
                declaring.Add(template.Key);
                var reset = AssistantEgressConsentGuard.ResetAll(steps, out bool changed);
                changed.Should().BeTrue();
                Refuses(reset, null).Should().BeFalse("the assistant creates it with the flag off");
            }
        }
        declaring.Should().Equal("video-derush-edit-broll");
    }

    private static string Text(object? raw) => raw switch
    {
        JsonElement { ValueKind: JsonValueKind.String } e => e.GetString()!,
        JsonElement e => e.GetRawText(),
        _ => raw?.ToString() ?? string.Empty
    };

    private static async Task<string> InvokeAsync(IReadOnlyList<AIFunction> tools, string tool, params (string, object?)[] args)
    {
        var arguments = new AIFunctionArguments();
        foreach ((string key, object? value) in args)
            arguments[key] = value;
        return Text(await tools.Single(t => t.Name == tool).InvokeAsync(arguments));
    }

    private static async Task<(Guid owner, Project project, AgentDefinition agent, WorkflowDefinition workflow)> SeedAsync(ReelBolt.Inference.Api.Data.InferenceApiDbContext db)
    {
        Guid owner = Guid.NewGuid();
        var project = new Project { Id = Guid.NewGuid(), Name = "Launch Teaser", OwnerId = owner, OrganizationId = TestTenancy.OrgOf(owner), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        var agent = new AgentDefinition { Id = Guid.NewGuid(), Name = "A", Description = "d", SystemPrompt = "p", AgentType = AgentType.AuthorAgent, IsBuiltIn = true, CreatedAt = DateTime.UtcNow };
        var workflow = new WorkflowDefinition { Id = Guid.NewGuid(), Name = "Product reel", ProjectId = project.Id, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        workflow.Steps.Add(new WorkflowStep { Id = Guid.NewGuid(), WorkflowDefinitionId = workflow.Id, AgentDefinitionId = agent.Id, StepOrder = 1, Label = "Write", StepType = StepType.Agent });
        db.Projects.Add(project);
        db.AgentDefinitions.Add(agent);
        db.WorkflowDefinitions.Add(workflow);
        await db.SaveChangesAsync();
        return (owner, project, agent, workflow);
    }

    private static List<CreateWorkflowStepRequest> Steps(Guid agentId, string? analyzeConfig) =>
        [new(agentId, 1, null, "Write", StepType: "Agent", VideoAnalyzeConfigJson: analyzeConfig)];

    private static bool Refused(string result) =>
        JsonDocument.Parse(result).RootElement.TryGetProperty("error", out JsonElement e) && e.GetString()!.Contains("switch on the option that allows sending your footage");

    [Fact]
    public async Task ProposeWorkflow_refuses_the_flag_for_new_and_edit_without_a_token_and_apply_rechecks_a_tampered_payload()
    {
        var (connection, db) = WorkflowAssistantControllerTests.CreateSqliteContext();
        using (connection)
        using (db)
        {
            var (owner, project, agent, workflow) = await SeedAsync(db);
            AssistantToolProvider provider = AssistantToolTestFactory.CreateProvider(db, new FakeCurrentUser(owner));
            var context = new AssistantToolContext(owner, project.Id);
            IReadOnlyList<AIFunction> tools = provider.GetTools(context);

            string created = await InvokeAsync(tools, "ProposeWorkflow", ("workflowId", null), ("name", "New"), ("steps", Steps(agent.Id, On)));
            Refused(created).Should().BeTrue();
            created.Should().NotContain("confirmationToken");
            string edited = await InvokeAsync(tools, "ProposeWorkflow", ("workflowId", workflow.Id), ("name", "Product reel"), ("steps", Steps(agent.Id, On)));
            Refused(edited).Should().BeTrue();
            edited.Should().NotContain("confirmationToken");

            // A token minted for the safe payload cannot apply the egress one.
            string safe = await InvokeAsync(tools, "ProposeWorkflow", ("workflowId", null), ("name", "New"), ("steps", Steps(agent.Id, Off)));
            string token = JsonDocument.Parse(safe).RootElement.GetProperty("confirmationToken").GetString()!;
            string tampered = await InvokeAsync(tools, "ApplyWorkflow", ("workflowId", null), ("name", "New"), ("steps", Steps(agent.Id, On)), ("confirmationToken", token));
            Refused(tampered).Should().BeTrue();
            (await db.WorkflowDefinitions.CountAsync()).Should().Be(1, "nothing was written");
        }
    }

    [Fact]
    public async Task An_existing_true_stays_true_through_ProposeWorkflow()
    {
        var (connection, db) = WorkflowAssistantControllerTests.CreateSqliteContext();
        using (connection)
        using (db)
        {
            var (owner, project, agent, workflow) = await SeedAsync(db);
            WorkflowStep step = await db.WorkflowSteps.SingleAsync(s => s.WorkflowDefinitionId == workflow.Id);
            step.StepType = StepType.VideoAnalyze;
            step.VideoAnalyzeConfigJson = On;
            await db.SaveChangesAsync();
            AssistantToolProvider provider = AssistantToolTestFactory.CreateProvider(db, new FakeCurrentUser(owner));
            IReadOnlyList<AIFunction> tools = provider.GetTools(new AssistantToolContext(owner, project.Id));

            List<CreateWorkflowStepRequest> same = [new(agent.Id, 1, null, "Write", StepType: "VideoAnalyze", VideoAnalyzeConfigJson: On)];
            string result = await InvokeAsync(tools, "ProposeWorkflow", ("workflowId", workflow.Id), ("name", "Product reel"), ("steps", same));
            Refused(result).Should().BeFalse(result);
        }
    }

    [Fact]
    public async Task ProposePatchWorkflow_and_ApplyPatchWorkflow_refuse_a_patch_that_turns_the_flag_on()
    {
        var (connection, db) = WorkflowAssistantControllerTests.CreateSqliteContext();
        using (connection)
        using (db)
        {
            var (owner, project, _, workflow) = await SeedAsync(db);
            WorkflowStep only = await db.WorkflowSteps.SingleAsync(x => x.WorkflowDefinitionId == workflow.Id);
            only.StepType = StepType.Extract;
            only.ExtractConfigJson = """{"prompt":"a"}""";
            await db.SaveChangesAsync();
            IConfirmationTokenService tokens = ConfirmationTokenService.CreateEphemeral();
            var patchTools = new AssistantWorkflowPatchTools(
                db, tokens, TestTenancy.Access(db, new FakeCurrentUser(owner)),
                new WorkflowEditDiffService(db, tokens), new StepProviderOwnershipValidator(db));
            IReadOnlyList<AIFunction> tools = patchTools.GetTools(new AssistantToolContext(owner, project.Id));
            JsonElement ops = JsonDocument.Parse("""[{"op":"setStepConfig","stepOrder":1,"patch":{"allowSourceMediaEgress":true}}]""").RootElement.Clone();

            string proposed = await InvokeAsync(tools, "ProposePatchWorkflow", ("workflowId", workflow.Id), ("operations", ops));
            Refused(proposed).Should().BeTrue(proposed);
            proposed.Should().NotContain("confirmationToken");
            string applied = await InvokeAsync(tools, "ApplyPatchWorkflow", ("workflowId", workflow.Id), ("operations", ops), ("confirmationToken", "anything"));
            Refused(applied).Should().BeTrue(applied);
        }
    }

    // ---- SEC-WAB-EG-1: a true is kept only for the same step in an otherwise unchanged workflow ----

    private const string OnSrcA = """{"source":{"kind":"ProjectFile","fileId":"a"},"model":"m1","trackObjects":[{"label":"cat"}],"allowSourceMediaEgress":true}""";
    private static CreateWorkflowStepRequest Typed(int order, string cfg, Guid? agent = null, string type = "VideoAnalyze") => Req(order, cfg, null, type, agent);
    private static CreateWorkflowStepRequest Plain(int order, string model = "x") => Req(order, "{\"model\":\"" + model + "\"}");

    [Fact]
    public void Keys_in_another_order_are_the_same_config()
    {
        On1(Eval([Typed(1, """{"allowSourceMediaEgress":true,"trackObjects":[{"label":"cat"}],"model":"m1","source":{"fileId":"a","kind":"ProjectFile"}}""")], [Typed(1, OnSrcA)]))
            .Should().BeTrue();
    }

    [Theory]
    [InlineData("""{"source":{"kind":"ProjectFile","fileId":"b"},"model":"m1","trackObjects":[{"label":"cat"}],"allowSourceMediaEgress":true}""")]
    [InlineData("""{"source":{"kind":"ProjectFile","fileId":"a"},"model":"m2","trackObjects":[{"label":"cat"}],"allowSourceMediaEgress":true}""")]
    [InlineData("""{"source":{"kind":"ProjectFile","fileId":"a"},"model":"m1","trackObjects":[{"label":"dog"}],"allowSourceMediaEgress":true}""")]
    public void A_source_model_or_tracking_change_on_a_consented_step_resets_it_with_a_notice(string changed)
    {
        var o = Eval([Typed(1, changed)], [Typed(1, OnSrcA)]);
        o.Refusal.Should().BeNull();
        On1(o).Should().BeFalse();
        o.Notes.Should().ContainSingle().Which.Should().Contain("s1").And.Contain("allow the footage to be sent again");
    }

    [Fact]
    public void A_consented_step_moved_to_another_order_is_reset()
    {
        var o = Eval([Typed(2, OnSrcA)], [Typed(1, OnSrcA)]);
        o.Refusal.Should().BeNull();
        On1(o).Should().BeFalse();
    }

    [Fact]
    public void Another_agent_or_step_type_resets_and_a_recreated_step_is_refused()
    {
        On1(Eval([Typed(1, OnSrcA, Guid.NewGuid())], [Typed(1, OnSrcA)])).Should().BeFalse();
        On1(Eval([Typed(1, OnSrcA, type: "VideoGenerate")], [Typed(1, OnSrcA)])).Should().BeFalse();
        Refuses([Typed(1, OnSrcA)], []).Should().BeTrue("deleted and recreated");
    }

    [Fact]
    public void An_empty_agent_id_never_matches_as_a_wildcard()
    {
        On1(Eval([Typed(1, OnSrcA, Guid.Empty)], [Typed(1, OnSrcA)])).Should().BeFalse();
        On1(Eval([Typed(1, OnSrcA)], [Typed(1, OnSrcA, Guid.Empty)])).Should().BeFalse();
    }

    [Fact]
    public void Insert_before_a_consented_step_resets_it_with_a_notice_instead_of_refusing()
    {
        var o = Eval([Plain(1), Typed(2, OnSrcA)], [Typed(1, OnSrcA)]);
        o.Refusal.Should().BeNull();
        On1(o, 1).Should().BeFalse();
        o.Notes.Should().ContainSingle();
    }

    [Fact]
    public void An_upstream_step_swapped_or_changed_two_hops_away_resets_the_consent()
    {
        // source -> analysis -> consented step: step 3 sends step 2's output, which reads step 1's.
        const string reads2 = """{"source":{"kind":"StepOutput","stepOrder":2},"model":"m1","allowSourceMediaEgress":true}""";
        static CreateWorkflowStepRequest Reads1(string model) => Typed(2, "{\"source\":{\"kind\":\"StepOutput\",\"stepOrder\":1},\"model\":\"" + model + "\"}");
        CreateWorkflowStepRequest[] baseline = [Plain(1, "src"), Reads1("analysis"), Typed(3, reads2)];
        On1(Eval([Plain(1, "src"), Reads1("analysis"), Typed(3, reads2)], baseline), 2).Should().BeTrue("nothing changed");
        On1(Eval([Plain(1, "src"), Reads1("SWAPPED"), Typed(3, reads2)], baseline), 2).Should().BeFalse("the step it reads was swapped");
        On1(Eval([Plain(1, "OTHER"), Reads1("analysis"), Typed(3, reads2)], baseline), 2).Should().BeFalse("two hops upstream changed");
    }

    [Fact]
    public void A_change_to_a_step_the_consented_step_does_not_read_keeps_it()
    {
        // OnSrcA reads its own project file: a change to steps 1 and 2 does not change what it sends.
        CreateWorkflowStepRequest[] baseline = [Plain(1, "src"), Plain(2, "analysis"), Typed(3, OnSrcA)];
        var o = Eval([Plain(1, "OTHER"), Plain(2, "SWAPPED"), Typed(3, OnSrcA)], baseline);
        On1(o, 2).Should().BeTrue();
        o.Notes.Should().BeEmpty();
    }

    // ---- Issue 7 (2026-11-03): a consent survives edits that do not touch what THAT step sends ----

    private const string CompileFit = """{"analysisStepOrder":1,"decision":{"from":"Step","stepOrder":1},"mode":"Reencode","outputFit":"Contain"}""";

    private static CreateWorkflowStepRequest Compile(int order, string cfg) =>
        new(Agent, order, null, "c" + order, StepType: "VideoCompile", VideoCompileConfigJson: cfg);

    private static string AnalyzeFile(string file, string extra = "") =>
        "{\"source\":{\"kind\":\"ProjectFile\",\"projectFileId\":\"" + file + "\"}" + extra + ",\"allowSourceMediaEgress\":true}";

    private static CreateWorkflowStepRequest[] ReelWorkflow(string fit2) =>
    [
        Typed(1, AnalyzeFile("a")), Compile(2, CompileFit.Replace("Contain", fit2)),
        Typed(3, AnalyzeFile("b")), Compile(4, CompileFit),
        Typed(5, AnalyzeFile("c")), Compile(6, CompileFit),
    ];

    [Fact]
    public void Changing_only_a_compile_step_keeps_every_consented_analyze_step_and_reports_nothing()
    {
        var o = Eval(ReelWorkflow("Cover"), ReelWorkflow("Contain"));
        o.Refusal.Should().BeNull();
        On1(o, 0).Should().BeTrue();
        On1(o, 2).Should().BeTrue();
        On1(o, 4).Should().BeTrue();
        o.Notes.Should().BeEmpty("no consented step was affected");
        o.Steps[1].VideoCompileConfigJson.Should().Contain("Cover", "the edit itself goes through");
    }

    [Fact]
    public void A_consented_step_that_reads_the_changed_compile_is_reset_and_only_it_is_reported()
    {
        const string readsCompile = """{"source":{"kind":"StepOutput","stepOrder":2},"allowSourceMediaEgress":true}""";
        CreateWorkflowStepRequest[] Wf(string fit) => [Typed(1, AnalyzeFile("a")), Compile(2, CompileFit.Replace("Contain", fit)), Typed(3, readsCompile)];
        var o = Eval(Wf("Cover"), Wf("Contain"));
        o.Refusal.Should().BeNull();
        On1(o, 0).Should().BeTrue("step 1 reads its own file");
        On1(o, 2).Should().BeFalse("step 3 sends the film step 2 makes, which changed");
        o.Notes.Should().ContainSingle().Which.Should().Contain("s3");
    }

    [Fact]
    public void Previous_step_output_reads_every_earlier_step()
    {
        const string previous = """{"source":{"kind":"PreviousStepOutput"},"allowSourceMediaEgress":true}""";
        CreateWorkflowStepRequest[] Wf(string fit) => [Typed(1, AnalyzeFile("a")), Compile(2, CompileFit.Replace("Contain", fit)), Typed(3, previous)];
        On1(Eval(Wf("Cover"), Wf("Contain")), 2).Should().BeFalse();
        On1(Eval(Wf("Contain"), Wf("Contain")), 2).Should().BeTrue();
    }

    [Fact]
    public void The_same_workflow_saved_twice_keeps_the_consent_even_with_defaults_left_out()
    {
        CreateWorkflowStepRequest stored = Typed(1, AnalyzeFile("a")) with { MaxIterations = 3, CacheMode = "Auto" };
        CreateWorkflowStepRequest echoed = Typed(1, AnalyzeFile("a")) with { Label = "renamed" };
        var o = Eval([echoed, Compile(2, CompileFit)], [stored, Compile(2, CompileFit)]);
        o.Refusal.Should().BeNull();
        On1(o).Should().BeTrue();
        o.Notes.Should().BeEmpty();
    }

    [Fact]
    public void Appending_or_deleting_a_step_after_the_consented_step_keeps_it()
    {
        On1(Eval([Typed(1, AnalyzeFile("a")), Compile(2, CompileFit), Compile(3, CompileFit)], [Typed(1, AnalyzeFile("a")), Compile(2, CompileFit)])).Should().BeTrue("appended after");
        On1(Eval([Typed(1, AnalyzeFile("a"))], [Typed(1, AnalyzeFile("a")), Compile(2, CompileFit)])).Should().BeTrue("deleted after");
    }

    [Fact]
    public void Swapping_two_consented_steps_resets_both()
    {
        var o = Eval([Typed(1, AnalyzeFile("b")), Typed(2, AnalyzeFile("a"))], [Typed(1, AnalyzeFile("a")), Typed(2, AnalyzeFile("b"))]);
        o.Refusal.Should().BeNull();
        On1(o, 0).Should().BeFalse();
        On1(o, 1).Should().BeFalse();
        o.Notes.Should().HaveCount(2);
    }

    [Theory]
    [InlineData(",\"trackObjects\":[{\"label\":\"face\",\"method\":\"Segmentation\",\"prompt\":\"the face\"}]", ",\"trackObjects\":[{\"label\":\"face\",\"method\":\"Segmentation\",\"prompt\":\"the whole body\"}]")]
    [InlineData(",\"segmentationProviderId\":\"11111111-1111-1111-1111-111111111111\"", ",\"segmentationProviderId\":\"22222222-2222-2222-2222-222222222222\"")]
    [InlineData(",\"maxSegmentationSeconds\":30", ",\"maxSegmentationSeconds\":600")]
    [InlineData(",\"visionProviderId\":\"11111111-1111-1111-1111-111111111111\"", ",\"visionProviderId\":\"22222222-2222-2222-2222-222222222222\"")]
    [InlineData(",\"sources\":[{\"kind\":\"BriefFiles\",\"role\":\"footage\"}]", ",\"sources\":[{\"kind\":\"BriefFiles\",\"role\":\"broll\"}]")]
    public void A_change_to_what_the_consented_step_itself_sends_resets_it(string before, string after)
    {
        var o = Eval([Typed(1, AnalyzeFile("a", after)), Compile(2, CompileFit)], [Typed(1, AnalyzeFile("a", before)), Compile(2, CompileFit)]);
        o.Refusal.Should().BeNull();
        On1(o).Should().BeFalse();
        o.Notes.Should().ContainSingle();
    }

    [Fact]
    public void A_changed_review_loop_anywhere_resets_the_consent()
    {
        CreateWorkflowStepRequest Loop(int min) =>
            new(Agent, 3, null, "loop", StepType: "ReviewLoop", LoopTargetStepOrder: 1, MinScore: min, ReviewLoopConfigJson: "{}");
        On1(Eval([Typed(1, AnalyzeFile("a")), Compile(2, CompileFit), Loop(9)], [Typed(1, AnalyzeFile("a")), Compile(2, CompileFit), Loop(8)])).Should().BeFalse();
        On1(Eval([Typed(1, AnalyzeFile("a")), Compile(2, CompileFit), Loop(8)], [Typed(1, AnalyzeFile("a")), Compile(2, CompileFit), Loop(8)])).Should().BeTrue();
        On1(Eval([Typed(1, AnalyzeFile("a")), Compile(2, CompileFit), Loop(8)], [Typed(1, AnalyzeFile("a")), Compile(2, CompileFit)])).Should().BeFalse("a loop was added");
    }

    [Fact]
    public void A_video_generate_step_keeps_its_consent_unless_its_keyframe_or_plan_upstream_changed()
    {
        static CreateWorkflowStepRequest Frame(string at) =>
            new(Agent, 1, null, "frame", StepType: "ExtractFrame", ExtractFrameConfigJson: "{\"source\":{\"kind\":\"ProjectFile\",\"projectFileId\":\"a\"},\"at\":\"" + at + "\"}");
        const string gen = """{"planSource":"Inline","inlinePrompt":"p","firstFrame":{"from":"Step","stepOrder":1},"allowSourceMediaEgress":true}""";
        static bool GenOn(AssistantEgressConsentGuard.Outcome o, int index = 1) => AssistantEgressConsentGuard.HasConsent(o.Steps[index].VideoGenerateConfigJson!) == true;
        CreateWorkflowStepRequest g = Req(2, null, gen, "VideoGenerate");

        GenOn(Eval([Frame("First"), g, Compile(3, CompileFit.Replace("Contain", "Cover"))], [Frame("First"), g, Compile(3, CompileFit)])).Should().BeTrue("a later step changed");
        GenOn(Eval([Frame("Last"), g], [Frame("First"), g])).Should().BeFalse("the keyframe it sends changed");

        // Plan mode: the plan comes from an agent step, which reads the whole workflow before it.
        const string planned = """{"planSource":"StepRef","plan":{"from":"Step","stepOrder":2},"allowSourceMediaEgress":true}""";
        CreateWorkflowStepRequest shots = new(Agent, 2, null, "shots", StepType: "Agent");
        CreateWorkflowStepRequest pg = Req(3, null, planned, "VideoGenerate");
        GenOn(Eval([Frame("Last"), shots, pg], [Frame("First"), shots, pg]), 2).Should().BeFalse("the planning agent reads step 1, which changed");
        GenOn(Eval([Frame("First"), shots, pg], [Frame("First"), shots, pg]), 2).Should().BeTrue();
    }

    [Fact]
    public void An_audio_cleanup_step_keeps_its_consent_unless_its_target_or_source_changed()
    {
        static CreateWorkflowStepRequest Clean(string target, string file = "a") =>
            new(Agent, 1, null, "clean", StepType: "AudioCleanup",
                AudioCleanupConfigJson: "{\"source\":{\"kind\":\"ProjectFile\",\"projectFileId\":\"" + file + "\"},\"target\":\"" + target + "\",\"allowSourceMediaEgress\":true}");
        static bool CleanOn(AssistantEgressConsentGuard.Outcome o) => AssistantEgressConsentGuard.HasConsent(o.Steps[0].AudioCleanupConfigJson!) == true;

        CleanOn(Eval([Clean("IsolateVoice"), Compile(2, CompileFit.Replace("Contain", "Cover"))], [Clean("IsolateVoice"), Compile(2, CompileFit)])).Should().BeTrue();
        CleanOn(Eval([Clean("RemoveMusic")], [Clean("IsolateVoice")])).Should().BeFalse("another target");
        CleanOn(Eval([Clean("IsolateVoice", "b")], [Clean("IsolateVoice")])).Should().BeFalse("another source");
    }

    [Fact]
    public void A_flip_next_to_an_unrelated_edit_is_still_refused()
    {
        // Step 3 never had the consent: the compile edit elsewhere does not make the flip acceptable.
        CreateWorkflowStepRequest[] baseline = [Typed(1, AnalyzeFile("a")), Compile(2, CompileFit), Typed(3, """{"source":{"kind":"ProjectFile","projectFileId":"b"}}""")];
        Refuses([Typed(1, AnalyzeFile("a")), Compile(2, CompileFit.Replace("Contain", "Cover")), Typed(3, AnalyzeFile("b"))], baseline).Should().BeTrue();
    }

    [Fact]
    public async Task ProposePatchWorkflow_and_ApplyPatchWorkflow_keep_the_consent_when_only_a_compile_step_changes()
    {
        var (connection, db) = WorkflowAssistantControllerTests.CreateSqliteContext();
        using (connection)
        using (db)
        {
            var (owner, project, agent, workflow) = await SeedAsync(db);
            WorkflowStep only = await db.WorkflowSteps.SingleAsync(x => x.WorkflowDefinitionId == workflow.Id);
            only.StepType = StepType.VideoAnalyze;
            only.Label = "Analyze";
            only.VideoAnalyzeConfigJson = """{"source":{"kind":"ProjectFile","projectFileId":"00000000-0000-0000-0000-00000000000a"},"allowSourceMediaEgress":true}""";
            db.WorkflowSteps.Add(new WorkflowStep
            {
                Id = Guid.NewGuid(),
                WorkflowDefinitionId = workflow.Id,
                AgentDefinitionId = agent.Id,
                StepOrder = 2,
                Label = "Compile",
                StepType = StepType.VideoCompile,
                VideoCompileConfigJson = """{"analysisStepOrder":1,"keepWholeSources":true,"mode":"Reencode","outputFit":"Contain"}"""
            });
            await db.SaveChangesAsync();
            IConfirmationTokenService tokens = ConfirmationTokenService.CreateEphemeral();
            var patchTools = new AssistantWorkflowPatchTools(
                db, tokens, TestTenancy.Access(db, new FakeCurrentUser(owner)),
                new WorkflowEditDiffService(db, tokens), new StepProviderOwnershipValidator(db));
            IReadOnlyList<AIFunction> tools = patchTools.GetTools(new AssistantToolContext(owner, project.Id));
            JsonElement ops = JsonDocument.Parse("""[{"op":"setStepConfig","stepOrder":2,"patch":{"outputFit":"Cover"}}]""").RootElement.Clone();

            string proposed = await InvokeAsync(tools, "ProposePatchWorkflow", ("workflowId", workflow.Id), ("operations", ops));
            JsonElement p = JsonDocument.Parse(proposed).RootElement;
            p.TryGetProperty("error", out _).Should().BeFalse(proposed);
            p.TryGetProperty("needsBeforeRunning", out _).Should().BeFalse("the consented step was not affected: " + proposed);

            string applied = await InvokeAsync(tools, "ApplyPatchWorkflow", ("workflowId", workflow.Id), ("operations", ops), ("confirmationToken", p.GetProperty("confirmationToken").GetString()));
            JsonDocument.Parse(applied).RootElement.TryGetProperty("error", out _).Should().BeFalse(applied);
            WorkflowStep analyze = await db.WorkflowSteps.AsNoTracking().SingleAsync(x => x.WorkflowDefinitionId == workflow.Id && x.StepOrder == 1);
            AssistantEgressConsentGuard.HasConsent(analyze.VideoAnalyzeConfigJson!).Should().BeTrue();
            WorkflowStep compile = await db.WorkflowSteps.AsNoTracking().SingleAsync(x => x.WorkflowDefinitionId == workflow.Id && x.StepOrder == 2);
            compile.VideoCompileConfigJson.Should().Contain("Cover");
        }
    }

    [Fact]
    public async Task ProposeWorkflow_keeps_the_consent_of_an_unaffected_step_and_reports_only_the_reset_one()
    {
        var (connection, db) = WorkflowAssistantControllerTests.CreateSqliteContext();
        using (connection)
        using (db)
        {
            var (owner, project, agent, workflow) = await SeedAsync(db);
            WorkflowStep only = await db.WorkflowSteps.SingleAsync(x => x.WorkflowDefinitionId == workflow.Id);
            only.StepType = StepType.VideoAnalyze;
            only.Label = "Analyze";
            only.VideoAnalyzeConfigJson = """{"source":{"kind":"ProjectFile","projectFileId":"00000000-0000-0000-0000-00000000000a"},"allowSourceMediaEgress":true}""";
            await db.SaveChangesAsync();
            AssistantToolProvider provider = AssistantToolTestFactory.CreateProvider(db, new FakeCurrentUser(owner));
            IReadOnlyList<AIFunction> tools = provider.GetTools(new AssistantToolContext(owner, project.Id));
            List<CreateWorkflowStepRequest> appended =
            [
                AssistantStepShapes.FromStep(only),
                new(agent.Id, 2, null, "Compile", StepType: "VideoCompile", VideoCompileConfigJson: """{"analysisStepOrder":1,"keepWholeSources":true,"mode":"Reencode"}"""),
            ];

            string proposed = await InvokeAsync(tools, "ProposeWorkflow", ("workflowId", workflow.Id), ("name", "Product reel"), ("steps", appended));
            JsonElement p = JsonDocument.Parse(proposed).RootElement;
            p.TryGetProperty("error", out _).Should().BeFalse(proposed);
            p.TryGetProperty("needsBeforeRunning", out JsonElement needs).Should().BeFalse(proposed);
            string applied = await InvokeAsync(tools, "ApplyWorkflow", ("workflowId", workflow.Id), ("name", "Product reel"), ("steps", appended), ("confirmationToken", p.GetProperty("confirmationToken").GetString()));
            JsonDocument.Parse(applied).RootElement.TryGetProperty("error", out _).Should().BeFalse(applied);
            WorkflowStep saved = await db.WorkflowSteps.AsNoTracking().SingleAsync(x => x.WorkflowDefinitionId == workflow.Id && x.StepOrder == 1);
            AssistantEgressConsentGuard.HasConsent(saved.VideoAnalyzeConfigJson!).Should().BeTrue();
        }
    }

    [Fact]
    public void A_never_consented_step_flipped_next_to_a_consented_one_is_refused()
    {
        Refuses([Typed(1, OnSrcA), Typed(2, OnSrcA)], [Typed(1, OnSrcA), Plain(2)]).Should().BeTrue();
    }

    [Fact]
    public void Two_identical_steps_with_one_grant_keep_one_and_the_other_is_refused_by_name()
    {
        AssistantEgressConsentGuard.Evaluate([Typed(1, OnSrcA), Typed(2, OnSrcA)], [Typed(1, OnSrcA)]).Refusal!.Should().Contain("s2");
    }

    [Fact]
    public void Repeated_step_orders_are_refused_in_the_proposal_and_in_the_baseline()
    {
        Refuses([Typed(1, OnSrcA), Typed(1, OnSrcA)], [Typed(1, OnSrcA)]).Should().BeTrue();
        Refuses([Typed(1, OnSrcA)], [Typed(1, OnSrcA), Typed(1, OnSrcA)]).Should().BeTrue();
    }

    [Fact]
    public void Only_the_top_level_flag_of_the_baseline_counts()
    {
        const string nested = """{"source":{"allowSourceMediaEgress":true},"model":"m1"}""";
        Refuses([Typed(1, nested)], [Typed(1, nested)]).Should().BeTrue("a nested true in the proposal still needs a top-level baseline grant");
    }

    [Fact]
    public async Task A_cross_project_clone_starts_with_the_permission_off_and_says_so_and_a_same_project_clone_keeps_it()
    {
        var (connection, db) = WorkflowAssistantControllerTests.CreateSqliteContext();
        using (connection)
        using (db)
        {
            var (owner, project, _, workflow) = await SeedAsync(db);
            WorkflowStep only = await db.WorkflowSteps.SingleAsync(x => x.WorkflowDefinitionId == workflow.Id);
            only.StepType = StepType.VideoAnalyze;
            only.VideoAnalyzeConfigJson = """{"allowSourceMediaEgress":true}""";
            var sibling = new Project { Id = Guid.NewGuid(), Name = "Sibling", OwnerId = owner, OrganizationId = project.OrganizationId, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            db.Projects.Add(sibling);
            await db.SaveChangesAsync();
            IConfirmationTokenService tokens = ConfirmationTokenService.CreateEphemeral();
            var patchTools = new AssistantWorkflowPatchTools(
                db, tokens, TestTenancy.Access(db, new FakeCurrentUser(owner)),
                new WorkflowEditDiffService(db, tokens), new StepProviderOwnershipValidator(db));
            IReadOnlyList<AIFunction> tools = patchTools.GetTools(new AssistantToolContext(owner, project.Id));

            string proposed = await InvokeAsync(tools, "ProposeCloneWorkflow", ("workflowId", workflow.Id), ("name", "Copy"), ("targetProjectId", sibling.Id));
            JsonElement p = JsonDocument.Parse(proposed).RootElement;
            p.TryGetProperty("error", out JsonElement err).Should().BeFalse(proposed);
            p.GetProperty("footageSharingPermissionTurnedOff").GetBoolean().Should().BeTrue();
            p.GetProperty("note").GetString().Should().Contain("switch on the option that allows sending your footage");
            await InvokeAsync(tools, "ApplyCloneWorkflow", ("workflowId", workflow.Id), ("name", "Copy"), ("targetProjectId", sibling.Id), ("confirmationToken", p.GetProperty("confirmationToken").GetString()));
            WorkflowStep copied = await db.WorkflowSteps.SingleAsync(x => x.WorkflowDefinition.ProjectId == sibling.Id);
            AssistantEgressConsentGuard.HasConsent(copied.VideoAnalyzeConfigJson!).Should().BeFalse();

            string same = await InvokeAsync(tools, "ProposeCloneWorkflow", ("workflowId", workflow.Id), ("name", "Copy2"));
            JsonDocument.Parse(same).RootElement.GetProperty("footageSharingPermissionTurnedOff").GetBoolean().Should().BeFalse();
        }
    }

    [Fact]
    public void The_run_start_lint_findings_lose_the_AllowEgress_fix()
    {
        using JsonDocument doc = JsonDocument.Parse("""[{"ruleId":"WL015","fix":{"kind":"allowEgress","label":"x"}},{"ruleId":"WL010","fix":{"kind":"chooseVoice","label":"y"}}]""");
        string stripped = AssistantEgressConsentGuard.StripEgressFix(doc.RootElement)!.ToJsonString();
        stripped.Should().NotContain("allowEgress").And.Contain("chooseVoice");
    }

    [Fact]
    public void ResetAll_turns_the_top_level_flag_off_and_reports_it()
    {
        var steps = AssistantEgressConsentGuard.ResetAll([Typed(1, OnSrcA), Typed(2, """{"model":"m"}""")], out bool changed);
        changed.Should().BeTrue();
        AssistantEgressConsentGuard.HasConsent(steps[0].VideoAnalyzeConfigJson!).Should().BeFalse();
        steps[0].VideoAnalyzeConfigJson.Should().Contain("\"model\":\"m1\"");
        steps[1].VideoAnalyzeConfigJson.Should().Be("""{"model":"m"}""");
    }

    [Fact]
    public async Task A_changed_context_resets_the_consent_at_propose_with_a_notice_and_apply_writes_the_same_reset_while_a_tampered_apply_is_stale()
    {
        var (connection, db) = WorkflowAssistantControllerTests.CreateSqliteContext();
        using (connection)
        using (db)
        {
            var (owner, project, agent, workflow) = await SeedAsync(db);
            WorkflowStep only = await db.WorkflowSteps.SingleAsync(x => x.WorkflowDefinitionId == workflow.Id);
            only.StepType = StepType.VideoAnalyze;
            only.VideoAnalyzeConfigJson = """{"allowSourceMediaEgress":true}""";
            await db.SaveChangesAsync();
            AssistantToolProvider provider = AssistantToolTestFactory.CreateProvider(db, new FakeCurrentUser(owner));
            IReadOnlyList<AIFunction> tools = provider.GetTools(new AssistantToolContext(owner, project.Id));
            List<CreateWorkflowStepRequest> inserted =
            [
                new(agent.Id, 1, null, "Write", StepType: "Agent"),
                new(agent.Id, 2, null, "Analyze", StepType: "VideoAnalyze", VideoAnalyzeConfigJson: """{"allowSourceMediaEgress":true}""")
            ];

            string proposed = await InvokeAsync(tools, "ProposeWorkflow", ("workflowId", workflow.Id), ("name", "Product reel"), ("steps", inserted));
            JsonElement p = JsonDocument.Parse(proposed).RootElement;
            p.TryGetProperty("error", out _).Should().BeFalse(proposed);
            p.GetProperty("needsBeforeRunning")[0].GetProperty("message").GetString().Should().Contain("Analyze");
            string token = p.GetProperty("confirmationToken").GetString()!;

            string applied = await InvokeAsync(tools, "ApplyWorkflow", ("workflowId", workflow.Id), ("name", "Product reel"), ("steps", inserted), ("confirmationToken", token));
            JsonDocument.Parse(applied).RootElement.TryGetProperty("error", out _).Should().BeFalse(applied);
            WorkflowStep saved = await db.WorkflowSteps.AsNoTracking().SingleAsync(x => x.WorkflowDefinitionId == workflow.Id && x.StepOrder == 2);
            AssistantEgressConsentGuard.HasConsent(saved.VideoAnalyzeConfigJson!).Should().BeFalse();
        }
    }

    [Fact]
    public void Values_containing_a_pipe_cannot_shift_a_field_boundary_to_carry_a_consent_over()
    {
        CreateWorkflowStepRequest Make(string? mapping, string? mode) =>
            Req(1, OnSrcA) with { InputMappingJson = mapping, AgentInputContextMode = mode };
        On1(Eval([Make("x", "y|z")], [Make("x|y", "z")])).Should().BeFalse("boundary shifted");
        On1(Eval([Make("x|y", "z")], [Make("x", "y|z")])).Should().BeFalse("boundary shifted the other way");
        On1(Eval([Make("x|y", "z")], [Make("x|y", "z")])).Should().BeTrue("identical stays");
    }

    [Fact]
    public async Task A_tampered_apply_after_a_propose_that_reset_the_consent_is_stale_and_writes_nothing()
    {
        var (connection, db) = WorkflowAssistantControllerTests.CreateSqliteContext();
        using (connection)
        using (db)
        {
            var (owner, project, agent, workflow) = await SeedAsync(db);
            WorkflowStep only = await db.WorkflowSteps.SingleAsync(x => x.WorkflowDefinitionId == workflow.Id);
            only.StepType = StepType.VideoAnalyze;
            only.VideoAnalyzeConfigJson = """{"allowSourceMediaEgress":true}""";
            await db.SaveChangesAsync();
            AssistantToolProvider provider = AssistantToolTestFactory.CreateProvider(db, new FakeCurrentUser(owner));
            IReadOnlyList<AIFunction> tools = provider.GetTools(new AssistantToolContext(owner, project.Id));
            List<CreateWorkflowStepRequest> inserted =
            [
                new(agent.Id, 1, null, "Write", StepType: "Agent"),
                new(agent.Id, 2, null, "Analyze", StepType: "VideoAnalyze", VideoAnalyzeConfigJson: """{"allowSourceMediaEgress":true}""")
            ];
            string token = JsonDocument.Parse(await InvokeAsync(tools, "ProposeWorkflow", ("workflowId", workflow.Id), ("name", "Product reel"), ("steps", inserted)))
                .RootElement.GetProperty("confirmationToken").GetString()!;

            // The same token with a different payload (a swapped feed on the consented step) must not apply.
            List<CreateWorkflowStepRequest> swapped =
            [
                new(agent.Id, 1, null, "Write", StepType: "Agent", InputMappingJson: """{"swapped":1}"""),
                inserted[1]
            ];
            string result = await InvokeAsync(tools, "ApplyWorkflow", ("workflowId", workflow.Id), ("name", "Product reel"), ("steps", swapped), ("confirmationToken", token));
            JsonDocument.Parse(result).RootElement.GetProperty("error").GetString().Should().Contain("stale");
            (await db.WorkflowSteps.CountAsync(x => x.WorkflowDefinitionId == workflow.Id)).Should().Be(1);
        }
    }
}
