using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using MassTransit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.DesignSystems;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Briefs;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Tenancy;
using ReelBolt.Shared.Workflows.Lint;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Workflows.Lint;

/// <summary>
/// The run-start side of the linter: the Workflows:Lint:Enforce flag on and off, the position in the gate order
/// (before credits, after the plan), Retry and Resume sharing it, the lint endpoints and tenant isolation.
/// </summary>
public sealed class WorkflowLintGateTests : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly List<(string Rule, bool Enforced)> _wouldBlock = [];

    public WorkflowLintGateTests()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == WorkflowLintDiagnostics.MeterName)
                listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
        {
            if (instrument.Name != "reelbolt.inference.lint_would_block")
                return;
            string rule = "";
            bool enforced = false;
            foreach (KeyValuePair<string, object?> tag in tags)
            {
                if (tag.Key == "rule") rule = (string)tag.Value!;
                if (tag.Key == "enforced") enforced = (bool)tag.Value!;
            }

            lock (_wouldBlock)
                _wouldBlock.Add((rule, enforced));
        });
        _listener.Start();
    }

    public void Dispose() => _listener.Dispose();

    // ------------------------------------------------------------------ run start

    [Fact]
    public async Task With_the_flag_off_a_failing_workflow_still_runs_and_the_would_block_is_counted()
    {
        using InferenceApiDbContext db = NewContext();
        Harness h = await HarnessAsync(db, enforce: false, broken: true);

        ActionResult<WorkflowExecutionResponse> result = await h.Controller.Execute(h.Project.Id, h.Workflow.Id, new ExecuteWorkflowRequest(), default);

        ((ObjectResult)result.Result!).StatusCode.Should().Be(201);
        (await db.WorkflowExecutions.CountAsync()).Should().Be(1);
        lock (_wouldBlock)
            _wouldBlock.Should().Contain(("WL003", false));
    }

    [Fact]
    public async Task With_the_flag_off_the_default_a_missing_setting_is_off()
    {
        using InferenceApiDbContext db = NewContext();
        Harness h = await HarnessAsync(db, enforce: null, broken: true);

        h.LintGate.Enforce.Should().BeFalse();
        ActionResult<WorkflowExecutionResponse> result = await h.Controller.Execute(h.Project.Id, h.Workflow.Id, new ExecuteWorkflowRequest(), default);

        ((ObjectResult)result.Result!).StatusCode.Should().Be(201);
    }

    [Fact]
    public async Task With_the_flag_on_an_error_finding_is_a_409_with_the_findings_and_nothing_is_spent()
    {
        using InferenceApiDbContext db = NewContext();
        Harness h = await HarnessAsync(db, enforce: true, broken: true, balanceMillis: 0);

        ActionResult<WorkflowExecutionResponse> result = await h.Controller.Execute(h.Project.Id, h.Workflow.Id, new ExecuteWorkflowRequest(), default);

        ObjectResult conflict = result.Result.Should().BeOfType<ConflictObjectResult>().Subject;
        conflict.StatusCode.Should().Be(409, "the lint answers before the credit check, which would have been a 402 for this empty account");
        string json = System.Text.Json.JsonSerializer.Serialize(conflict.Value);
        json.Should().Contain("WORKFLOW_LINT_ERRORS").And.Contain("WL003").And.Contain("\"error\"");
        (await db.WorkflowExecutions.CountAsync()).Should().Be(0);
        h.Publish.Verify(p => p.Publish(It.IsAny<It.IsAnyType>(), It.IsAny<CancellationToken>()), Times.Never);
        lock (_wouldBlock)
            _wouldBlock.Should().Contain(("WL003", true));
    }

    [Fact]
    public async Task With_the_flag_on_a_clean_workflow_goes_on_to_the_credit_check()
    {
        using InferenceApiDbContext broke = NewContext();
        Harness poor = await HarnessAsync(broke, enforce: true, broken: false, balanceMillis: 0);
        ActionResult<WorkflowExecutionResponse> refused = await poor.Controller.Execute(poor.Project.Id, poor.Workflow.Id, new ExecuteWorkflowRequest(), default);
        ((ObjectResult)refused.Result!).StatusCode.Should().Be(402, "a clean workflow is not stopped by the lint, the credit check answers");

        using InferenceApiDbContext funded = NewContext();
        Harness rich = await HarnessAsync(funded, enforce: true, broken: false);
        ActionResult<WorkflowExecutionResponse> ok = await rich.Controller.Execute(rich.Project.Id, rich.Workflow.Id, new ExecuteWorkflowRequest(), default);
        ((ObjectResult)ok.Result!).StatusCode.Should().Be(201);
    }

    [Fact]
    public async Task The_plan_gate_still_answers_before_the_lint()
    {
        using InferenceApiDbContext db = NewContext();
        Harness h = await HarnessAsync(db, enforce: true, broken: true, plan: new OrganizationEntitlementSet { Tier = "trial", MaxOutputHeight = 720 });

        ActionResult<WorkflowExecutionResponse> result = await h.Controller.Execute(
            h.Project.Id, h.Workflow.Id, new ExecuteWorkflowRequest(OutputResolution: "2160p"), default);

        ((ObjectResult)result.Result!).StatusCode.Should().Be(402);
        System.Text.Json.JsonSerializer.Serialize(((ObjectResult)result.Result!).Value).Should().Contain("PLAN_NOT_ENTITLED");
    }

    [Fact]
    public async Task Retry_and_resume_share_the_gate_and_a_resume_judges_only_the_steps_it_will_run()
    {
        using InferenceApiDbContext db = NewContext();
        Harness h = await HarnessAsync(db, enforce: true, broken: true);
        List<WorkflowStep> steps = [.. h.Workflow.Steps];
        RunStartGate gate = new(db, h.User, null, null, new DesignBuildEstimateGate(db, null, null), lintGate: h.LintGate);
        RunStartRun run = new(Guid.NewGuid(), null);

        // Retry: the run's own steps are judged.
        ActionResult? retry = await gate.RefuseAsync(h.Project, h.Workflow.Id, null, RunStartPricing.AllOf(steps), null, default, run);
        retry.Should().BeOfType<ConflictObjectResult>();

        // Resume from the frontier: the broken review loop (step 2) will run, so it is judged ...
        (await gate.RefuseAsync(h.Project, h.Workflow.Id, null, RunStartPricing.Charged(steps, [2]), null, default, run))
            .Should().BeOfType<ConflictObjectResult>();
        // ... but when only step 1 is left to run, a problem on step 2 is not this run's problem.
        (await gate.RefuseAsync(h.Project, h.Workflow.Id, null, RunStartPricing.Charged(steps, [1]), null, default, run))
            .Should().BeNull();
    }

    [Fact]
    public async Task A_lint_that_throws_or_overruns_lets_the_run_start()
    {
        using InferenceApiDbContext db = NewContext();
        Harness h = await HarnessAsync(db, enforce: true, broken: true);
        Mock<IWorkflowLinter> broken = new();
        broken.Setup(l => l.LintAsync(It.IsAny<WorkflowLintRequest>(), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("boom"));
        WorkflowLintGate gate = new(broken.Object, db, Config(true), NullLogger<WorkflowLintGate>.Instance);
        (await gate.CheckAsync(h.Project, h.Workflow.Id, h.Workflow.Steps.ToList(), null, null, default)).Should().BeNull();

        Mock<IWorkflowLinter> slow = new();
        slow.Setup(l => l.LintAsync(It.IsAny<WorkflowLintRequest>(), It.IsAny<CancellationToken>()))
            .Returns<WorkflowLintRequest, CancellationToken>(async (_, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return new WorkflowLintReport([]);
            });
        WorkflowLintGate warnOnly = new(slow.Object, db, Config(false), NullLogger<WorkflowLintGate>.Instance);
        System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        (await warnOnly.CheckAsync(h.Project, h.Workflow.Id, h.Workflow.Steps.ToList(), null, null, default)).Should().BeNull();
        clock.Elapsed.Should().BeLessThan(WorkflowLintGate.WarnOnlyBudget + TimeSpan.FromSeconds(3), "a warn-only lint is bounded");
    }

    [Fact]
    public async Task An_orphaned_app_input_never_blocks_a_run_start_and_a_snapshot_is_not_compared_with_the_live_manifest()
    {
        using InferenceApiDbContext db = NewContext();
        Harness h = await HarnessAsync(db, enforce: true, broken: false);
        h.Workflow.AppManifestJson = ReelBolt.Shared.Workflows.Apps.AppManifestJson.Serialize(new ReelBolt.Shared.Workflows.Apps.AppManifest(
            Inputs: [new ReelBolt.Shared.Workflows.Apps.AppManifestInput("brief", Field: "sourceLanguage", Label: "Source language")]));
        await db.SaveChangesAsync();
        WorkflowLinter withManifest = new(new LintKit.FixedFactsSource(UnknownLintFacts.Instance), new AppManifestOrphanSource(db));
        WorkflowLintGate gate = new(withManifest, db, Config(true), NullLogger<WorkflowLintGate>.Instance);

        // The lint itself sees the dead control (an error on the endpoints) ...
        (await withManifest.LintAsync(new WorkflowLintRequest(h.Project.OrganizationId, h.Project.Id, [.. h.Workflow.Steps], null, h.Workflow.Id), default))
            .Errors.Should().Contain(f => f.RuleId == "WL018");
        // ... but a run start is never refused for it, saved steps or a retry's snapshot alike.
        (await gate.CheckAsync(h.Project, h.Workflow.Id, null, null, null, default)).Should().BeNull();
        (await gate.CheckAsync(h.Project, h.Workflow.Id, h.Workflow.Steps.ToList(), null, null, default)).Should().BeNull();
    }

    [Fact]
    public async Task Retry_lints_with_the_brief_the_run_stored()
    {
        using InferenceApiDbContext db = NewContext();
        Harness h = await HarnessAsync(db, enforce: false, broken: false);
        RunBrief? seen = null;
        Mock<IWorkflowLinter> spy = new();
        spy.Setup(l => l.LintAsync(It.IsAny<WorkflowLintRequest>(), It.IsAny<CancellationToken>()))
            .Callback<WorkflowLintRequest, CancellationToken>((r, _) => seen = r.Brief)
            .ReturnsAsync(new WorkflowLintReport([]));
        WorkflowLintGate gate = new(spy.Object, db, Config(false), NullLogger<WorkflowLintGate>.Instance);
        WorkflowsController controller = new(
            db, h.User, TestTenancy.Access(db, h.User), h.Publish.Object,
            new WorkflowTemplateProvisioningService(db, NullLogger<WorkflowTemplateProvisioningService>.Instance, new ConfigurationBuilder().Build()),
            new WorkflowExecutionService(db, h.Publish.Object, NullLogger<WorkflowExecutionService>.Instance, null),
            NullLogger<WorkflowsController>.Instance, lintGate: gate);
        WorkflowExecution run = new()
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = h.Workflow.Id,
            ProjectId = h.Project.Id,
            OrganizationId = h.Project.OrganizationId,
            Status = ExecutionStatus.Failed,
            QueuedAt = DateTime.UtcNow,
            RunBriefJson = "{\"version\":1,\"language\":\"it\",\"lengthSec\":45}"
        };
        db.WorkflowExecutions.Add(run);
        await db.SaveChangesAsync();

        await controller.RetryExecution(h.Project.Id, h.Workflow.Id, run.Id, null, default);

        seen.Should().NotBeNull();
        seen!.Language.Should().Be("it");
        seen.LengthSec.Should().Be(45);
    }

    [Fact]
    public async Task The_lint_endpoint_refuses_a_draft_that_names_too_many_files_and_a_huge_one_does_no_more_than_one_lookup()
    {
        using InferenceApiDbContext db = NewContext();
        Harness h = await HarnessAsync(db, enforce: false, broken: false);
        string sources = string.Join(",", Enumerable.Range(0, 10_000).Select(_ => $"{{\"kind\":\"ProjectFile\",\"projectFileId\":\"{Guid.NewGuid()}\"}}"));
        List<CreateWorkflowStepRequest> draft =
        [
            new(h.Agent.Id, 1, null, "Analyze", StepType: "VideoAnalyze", VideoAnalyzeConfigJson: "{\"version\":1,\"sources\":[" + sources + "]}")
        ];

        ActionResult<WorkflowLintResponse> result = await h.Controller.Lint(h.Project.Id, h.Workflow.Id, new LintWorkflowRequest(Steps: draft), default);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        System.Text.Json.JsonSerializer.Serialize(((BadRequestObjectResult)result.Result!).Value).Should().Contain("LINT_TOO_LARGE");
    }

    [Theory]
    [InlineData("N")]
    [InlineData("B")]
    [InlineData("D")]
    [InlineData("P")]
    public async Task Ids_named_in_any_guid_form_count_toward_the_cap(string format)
    {
        using InferenceApiDbContext db = NewContext();
        Harness h = await HarnessAsync(db, enforce: false, broken: false);
        string sources = string.Join(",", Enumerable.Range(0, 600).Select(_ => "{\"kind\":\"ProjectFile\",\"projectFileId\":\"" + Guid.NewGuid().ToString(format) + "\"}"));
        List<CreateWorkflowStepRequest> draft =
        [
            new(h.Agent.Id, 1, null, "Analyze", StepType: "VideoAnalyze", VideoAnalyzeConfigJson: "{\"version\":1,\"sources\":[" + sources + "]}")
        ];

        ActionResult<WorkflowLintResponse> result = await h.Controller.Lint(h.Project.Id, h.Workflow.Id, new LintWorkflowRequest(Steps: draft), default);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        System.Text.Json.JsonSerializer.Serialize(((BadRequestObjectResult)result.Result!).Value).Should().Contain("LINT_TOO_LARGE");
    }

    [Fact]
    public async Task The_facts_cap_and_chunk_the_lookup_for_every_caller()
    {
        using InferenceApiDbContext db = NewContext();
        Harness h = await HarnessAsync(db, enforce: false, broken: false);
        ProjectFile real = new() { Id = Guid.NewGuid(), ProjectId = h.Project.Id, OriginalFileName = "a.mp4", MimeType = "video/mp4" };
        db.ProjectFiles.Add(real);
        await db.SaveChangesAsync();
        DbWorkflowLintFacts facts = new(db, Mock.Of<ReelBolt.Shared.Inference.IInferenceProviderResolver>(), Fixed(new OrganizationEntitlementSet()), h.Project.OrganizationId, h.Project.Id);
        List<Guid> ids = [real.Id, .. Enumerable.Range(0, DbWorkflowLintFacts.MaxFileLookups * 3).Select(_ => Guid.NewGuid())];

        await facts.WarmFilesAsync(ids, default);

        (await facts.ProjectFileExistsAsync(real.Id, default)).Should().BeTrue("the first ids are looked up, in chunks");
        (await facts.ProjectFileExistsAsync(ids[^1], default)).Should().BeNull("ids beyond the cap stay unknown, not looked up");
    }

    [Fact]
    public async Task The_batched_file_lookup_answers_for_many_ids_in_one_query()
    {
        using InferenceApiDbContext db = NewContext();
        Harness h = await HarnessAsync(db, enforce: false, broken: false);
        ProjectFile real = new() { Id = Guid.NewGuid(), ProjectId = h.Project.Id, OriginalFileName = "a.mp4", MimeType = "video/mp4" };
        db.ProjectFiles.Add(real);
        await db.SaveChangesAsync();
        DbWorkflowLintFacts facts = new(db, Mock.Of<ReelBolt.Shared.Inference.IInferenceProviderResolver>(), Fixed(new OrganizationEntitlementSet()), h.Project.OrganizationId, h.Project.Id);
        Guid missing = Guid.NewGuid();

        await facts.WarmFilesAsync([real.Id, missing], default);

        (await facts.ProjectFileExistsAsync(real.Id, default)).Should().BeTrue();
        (await facts.ProjectFileExistsAsync(missing, default)).Should().BeFalse();
    }

    [Fact]
    public void A_stored_brief_becomes_the_brief_a_retry_is_linted_with()
    {
        string json = RunBriefJson.Serialize(new StoredRunBrief(Language: "it", LengthSec: 30, Voice: new RunBriefVoice(RunBriefVoiceMode.Narrated)));
        RunBrief brief = WorkflowLintGate.BriefFromStored(json)!;
        brief.Language.Should().Be("it");
        brief.LengthSec.Should().Be(30);
        brief.Voice!.Mode.Should().Be(RunBriefVoiceMode.Narrated);
        WorkflowLintGate.BriefFromStored(null).Should().BeNull();
    }

    // ------------------------------------------------------------------ endpoints

    [Fact]
    public async Task The_lint_endpoint_lints_the_saved_workflow_and_a_draft_in_its_place()
    {
        using InferenceApiDbContext db = NewContext();
        Harness h = await HarnessAsync(db, enforce: false, broken: true);

        WorkflowLintResponse saved = Value(await h.Controller.Lint(h.Project.Id, h.Workflow.Id, null, default));
        saved.Errors.Should().Be(1);
        saved.Findings.Should().ContainSingle(f => f.RuleId == "WL003" && f.Severity == "error" && f.StepOrder == 2);
        saved.Findings.Single(f => f.RuleId == "WL003").Fix!.Kind.Should().Be("retargetReference");

        // The same workflow with the draft's own steps: the loop now goes back to step 1.
        List<CreateWorkflowStepRequest> draft =
        [
            new(h.Agent.Id, 1, null, "Author", StepType: "Agent"),
            new(h.Agent.Id, 2, null, "Review", StepType: "ReviewLoop", LoopTargetStepOrder: 1)
        ];
        WorkflowLintResponse fixedDraft = Value(await h.Controller.Lint(h.Project.Id, h.Workflow.Id, new LintWorkflowRequest(Steps: draft), default));
        fixedDraft.Errors.Should().Be(0);
    }

    [Fact]
    public async Task The_draft_endpoint_needs_steps_and_a_bad_brief_is_a_400()
    {
        using InferenceApiDbContext db = NewContext();
        Harness h = await HarnessAsync(db, enforce: false, broken: false);

        (await h.Controller.LintDraft(h.Project.Id, new LintWorkflowRequest(), default)).Result.Should().BeOfType<BadRequestObjectResult>();

        List<CreateWorkflowStepRequest> draft = [new(h.Agent.Id, 1, null, "Author", StepType: "Agent")];
        WorkflowLintResponse ok = Value(await h.Controller.LintDraft(h.Project.Id, new LintWorkflowRequest(Steps: draft), default));
        ok.Findings.Should().BeEmpty();

        (await h.Controller.LintDraft(h.Project.Id, new LintWorkflowRequest(new RunBrief(LengthSec: 2), draft), default))
            .Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task The_brief_rules_run_through_the_endpoint()
    {
        using InferenceApiDbContext db = NewContext();
        Harness h = await HarnessAsync(db, enforce: false, broken: false);

        WorkflowLintResponse response = Value(await h.Controller.Lint(
            h.Project.Id, h.Workflow.Id, new LintWorkflowRequest(new RunBrief(Voice: new RunBriefVoice(RunBriefVoiceMode.Narrated))), default));

        response.Findings.Should().ContainSingle(f => f.RuleId == "WL016")
            .Which.Fix!.PatchId.Should().Be("addVoiceover");
        response.Warnings.Should().Be(1);
    }

    [Fact]
    public async Task Another_organizations_project_and_a_workflow_of_another_project_are_a_404()
    {
        using InferenceApiDbContext db = NewContext();
        Harness mine = await HarnessAsync(db, enforce: false, broken: true);
        Harness theirs = await HarnessAsync(db, enforce: false, broken: true);

        // The caller of `mine` asks about the other organization's project and workflow.
        (await mine.Controller.Lint(theirs.Project.Id, theirs.Workflow.Id, null, default)).Result.Should().BeOfType<NotFoundResult>();
        (await mine.Controller.LintDraft(theirs.Project.Id, new LintWorkflowRequest(Steps: [new(mine.Agent.Id, 1, null, null, StepType: "Agent")]), default))
            .Result.Should().BeOfType<NotFoundResult>();
        // The caller's own project does not serve another project's workflow.
        (await mine.Controller.Lint(mine.Project.Id, theirs.Workflow.Id, null, default)).Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Agent_definitions_of_another_organization_are_not_read_for_a_draft()
    {
        using InferenceApiDbContext db = NewContext();
        Harness h = await HarnessAsync(db, enforce: false, broken: false);
        AgentDefinition foreign = new()
        {
            Id = Guid.NewGuid(),
            Name = "theirs",
            Description = "d",
            SystemPrompt = "p",
            AgentType = AgentType.ScriptwriterAgent,
            IsBuiltIn = false,
            OrganizationId = Guid.NewGuid(),
            CreatedAt = DateTime.UtcNow
        };
        db.AgentDefinitions.Add(foreign);
        await db.SaveChangesAsync();

        Dictionary<Guid, AgentDefinition> visible = await WorkflowLinterAgents.LoadAsync(db, h.Project.OrganizationId, [foreign.Id, h.Agent.Id], default);

        visible.Keys.Should().BeEquivalentTo([h.Agent.Id]);
    }

    // ------------------------------------------------------------------ harness

    private static WorkflowLintResponse Value(ActionResult<WorkflowLintResponse> result) =>
        (WorkflowLintResponse)((OkObjectResult)result.Result!).Value!;

    private sealed record Harness(
        WorkflowsController Controller, Project Project, WorkflowDefinition Workflow, AgentDefinition Agent,
        Mock<IPublishEndpoint> Publish, WorkflowLintGate LintGate, ICurrentUser User);

    private static InferenceApiDbContext NewContext() => new(
        new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static IConfiguration Config(bool? enforce) => new ConfigurationBuilder()
        .AddInMemoryCollection(enforce is null
            ? new Dictionary<string, string?>()
            : new Dictionary<string, string?> { ["Workflows:Lint:Enforce"] = enforce.Value ? "true" : "false" })
        .Build();

    private static IOrganizationEntitlements Fixed(OrganizationEntitlementSet set)
    {
        Mock<IOrganizationEntitlements> m = new();
        m.Setup(e => e.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).Returns(new ValueTask<OrganizationEntitlementSet>(set));
        return m.Object;
    }

    /// <summary>
    /// A project with an Author step and a review loop. <paramref name="broken"/> points the loop at step 9, which
    /// does not exist (WL003, an error); otherwise it points at step 1.
    /// </summary>
    private static async Task<Harness> HarnessAsync(
        InferenceApiDbContext db, bool? enforce, bool broken, long balanceMillis = 100_000_000, OrganizationEntitlementSet? plan = null)
    {
        Guid owner = Guid.NewGuid();
        Guid org = TestTenancy.OrgOf(owner);
        Project project = new() { Id = Guid.NewGuid(), Name = "p", OwnerId = owner, OrganizationId = org, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        AgentDefinition agent = new() { Id = Guid.NewGuid(), Name = "AuthorAgent", Description = "d", SystemPrompt = "p", AgentType = AgentType.AuthorAgent, IsBuiltIn = true, CreatedAt = DateTime.UtcNow };
        WorkflowDefinition workflow = new()
        {
            Id = Guid.NewGuid(),
            Name = "w",
            ProjectId = project.Id,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Steps =
            [
                new WorkflowStep { Id = Guid.NewGuid(), StepOrder = 1, StepType = StepType.Agent, AgentDefinitionId = agent.Id },
                new WorkflowStep { Id = Guid.NewGuid(), StepOrder = 2, StepType = StepType.ReviewLoop, AgentDefinitionId = agent.Id, LoopTargetStepOrder = broken ? 9 : 1 }
            ]
        };
        db.Projects.Add(project);
        if (!db.AgentDefinitions.Any(a => a.Id == agent.Id))
            db.AgentDefinitions.Add(agent);
        db.WorkflowDefinitions.Add(workflow);
        db.CreditAccounts.Add(new CreditAccount { Id = Guid.NewGuid(), OrganizationId = org, BalanceMillis = balanceMillis });
        await db.SaveChangesAsync();

        Mock<IPublishEndpoint> publish = new();
        PlanGateService gate = new(db, Fixed(plan ?? new OrganizationEntitlementSet { Tier = "pro", MaxOutputHeight = 2160 }));
        WorkflowExecutionService execution = new(db, publish.Object, NullLogger<WorkflowExecutionService>.Instance, gate);
        WorkflowCostEstimator estimator = new(
            new SingleContextScopes(db),
            Microsoft.Extensions.Options.Options.Create(new BillingOptions { Enabled = true }),
            Microsoft.Extensions.Options.Options.Create(new StepCreditEstimateOptions()),
            NullLogger<WorkflowCostEstimator>.Instance);
        FakeCurrentUser user = new(owner);
        WorkflowLinter linter = new(new LintKit.FixedFactsSource(UnknownLintFacts.Instance));
        WorkflowLintGate lintGate = new(linter, db, Config(enforce), NullLogger<WorkflowLintGate>.Instance);
        WorkflowsController controller = new(
            db, user, TestTenancy.Access(db, user), publish.Object,
            new WorkflowTemplateProvisioningService(db, NullLogger<WorkflowTemplateProvisioningService>.Instance, new ConfigurationBuilder().Build()),
            execution, NullLogger<WorkflowsController>.Instance, planGate: gate, costEstimator: estimator,
            lintGate: lintGate, linter: linter);
        return new Harness(controller, project, workflow, agent, publish, lintGate, user);
    }

    private sealed class FakeCurrentUser(Guid userId) : ICurrentUser
    {
        public Guid UserId { get; } = userId;
        public string Email { get; } = "test@example.com";
        public bool IsAuthenticated { get; } = true;
        public bool IsPlatformAdmin { get; } = true;
        public Guid? OrganizationId => TestTenancy.OrgOf(UserId);
        public OrganizationRole? OrgRole => OrganizationRole.Owner;
    }

    private sealed class SingleContextScopes(InferenceApiDbContext db) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new FixedScope(db);

        private sealed class FixedScope(InferenceApiDbContext db) : IServiceScope
        {
            public IServiceProvider ServiceProvider { get; } = new FixedProvider(db);
            public void Dispose()
            {
            }
        }

        private sealed class FixedProvider(InferenceApiDbContext db) : IServiceProvider
        {
            public object? GetService(Type serviceType) => serviceType == typeof(InferenceApiDbContext) ? db : null;
        }
    }
}
