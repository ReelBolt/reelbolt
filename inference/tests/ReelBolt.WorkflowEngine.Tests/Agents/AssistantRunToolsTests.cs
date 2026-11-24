using Moq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Billing;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Agents;

/// <summary>T-003: the assistant's run-management tools, <c>GetExecutionStatus</c> and <c>GetCreditBalance</c>.</summary>
public class AssistantRunToolsTests
{
    private static readonly Guid Owner = Guid.NewGuid();
    private static readonly Guid Stranger = Guid.NewGuid();

    private static InferenceApiDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private sealed record Scenario(InferenceApiDbContext Db, Project Project, WorkflowExecution Run, WorkflowStep Analyze, WorkflowStep Compile);

    private static Scenario Seed(ExecutionStatus status)
    {
        InferenceApiDbContext db = CreateDb();
        var agent = new AgentDefinition { Id = Guid.NewGuid(), Name = "Agent", AgentType = AgentType.VideoTransform, IsBuiltIn = true };
        var project = new Project { Id = Guid.NewGuid(), Name = "Launch", OwnerId = Owner, OrganizationId = TestTenancy.OrgOf(Owner) };
        var workflow = new WorkflowDefinition { Id = Guid.NewGuid(), Name = "Film", ProjectId = project.Id };
        var analyze = new WorkflowStep { Id = Guid.NewGuid(), WorkflowDefinitionId = workflow.Id, AgentDefinitionId = agent.Id, StepOrder = 1, Label = "Analyze", StepType = StepType.VideoAnalyze };
        var compile = new WorkflowStep { Id = Guid.NewGuid(), WorkflowDefinitionId = workflow.Id, AgentDefinitionId = agent.Id, StepOrder = 2, Label = "Compile", StepType = StepType.VideoCompile };
        workflow.Steps.Add(analyze);
        workflow.Steps.Add(compile);
        var run = new WorkflowExecution
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            WorkflowDefinitionId = workflow.Id,
            Status = status,
            StartedAt = DateTime.UtcNow.AddMinutes(-3),
        };
        run.StepResults.Add(new WorkflowStepResult
        {
            Id = Guid.NewGuid(),
            WorkflowExecutionId = run.Id,
            WorkflowStepId = analyze.Id,
            Status = StepStatus.Completed,
            CreditsChargedMillis = 2_000,
            ExecutedAt = DateTime.UtcNow.AddMinutes(-2),
            CompletedAt = DateTime.UtcNow.AddMinutes(-1),
        });
        run.StepResults.Add(new WorkflowStepResult
        {
            Id = Guid.NewGuid(),
            WorkflowExecutionId = run.Id,
            WorkflowStepId = compile.Id,
            Status = status == ExecutionStatus.Passed ? StepStatus.Completed : StepStatus.Running,
            CreditsChargedMillis = status == ExecutionStatus.Passed ? 25_000 : 0,
            ExecutedAt = DateTime.UtcNow.AddSeconds(-30),
            CompletedAt = status == ExecutionStatus.Passed ? DateTime.UtcNow : null,
            OutputStorageKey = status == ExecutionStatus.Passed ? $"projects/{project.Id}/outputFiles/{run.Id}/final.mp4" : null,
        });
        if (status == ExecutionStatus.Failed)
            run.ErrorMessage = "Compile failed: " + new string('x', 9_000);
        db.Projects.Add(project);
        db.WorkflowDefinitions.Add(workflow);
        db.WorkflowExecutions.Add(run);
        db.SaveChanges();
        return new Scenario(db, project, run, analyze, compile);
    }

    private sealed class FixedBalance(CreditBalanceSnapshot? snapshot) : ICreditBalanceReader
    {
        public Guid? AskedFor { get; private set; }
        public Task<CreditBalanceSnapshot?> GetAsync(Guid organizationId, CancellationToken ct)
        {
            AskedFor = organizationId;
            return Task.FromResult(snapshot);
        }
    }

    private static AssistantRunTools Tools(
        InferenceApiDbContext db, Guid user, ICreditBalanceReader? balance = null, ICreditAccountProvisioner? trialCredits = null, IConfiguration? configuration = null)
    {
        var current = new FakeCurrentUser(user);
        return new AssistantRunTools(db, TestTenancy.Access(db, current), current, balance ?? new FixedBalance(null), trialCredits, configuration);
    }

    private static IConfiguration Billing(bool enabled) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Billing:Enabled"] = enabled ? "true" : "false" }).Build();

    private static async Task<JsonElement> InvokeAsync(
        AssistantRunTools tools, Guid user, Guid? projectId, string name, CancellationToken ct = default, params (string Key, object? Value)[] args)
    {
        AIFunction tool = tools.GetTools(new AssistantToolContext(user, projectId)).Single(t => t.Name == name);
        var arguments = new AIFunctionArguments();
        foreach ((string key, object? value) in args)
            arguments[key] = value;
        object? raw = await tool.InvokeAsync(arguments, ct);
        string text = raw is JsonElement { ValueKind: JsonValueKind.String } s ? s.GetString()! : raw!.ToString()!;
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    [Fact]
    public async Task GetExecutionStatus_says_whether_billing_is_on_so_a_zero_is_not_read_as_free()
    {
        // QA-P0-30: GetWorkflowExecution carries billingEnabled; GetExecutionStatus must too, or a run's 0
        // creditsCharged under billing-off reads as "this was free".
        Scenario s = Seed(ExecutionStatus.Passed);
        using (s.Db)
        {
            AssistantRunTools off = Tools(s.Db, Owner, configuration: Billing(false));
            AssistantRunTools on = Tools(s.Db, Owner, configuration: Billing(true));

            JsonElement offResult = await InvokeAsync(off, Owner, s.Project.Id, "GetExecutionStatus", args: ("executionId", s.Run.Id));
            JsonElement onResult = await InvokeAsync(on, Owner, s.Project.Id, "GetExecutionStatus", args: ("executionId", s.Run.Id));

            offResult.GetProperty("billingEnabled").GetBoolean().Should().BeFalse();
            onResult.GetProperty("billingEnabled").GetBoolean().Should().BeTrue();
        }
    }

    [Fact]
    public async Task GetExecutionStatus_finished_run_reports_the_settled_figure_and_labels_it_final()
    {
        Scenario s = Seed(ExecutionStatus.Passed);
        using (s.Db)
        {
            JsonElement r = await InvokeAsync(Tools(s.Db, Owner, configuration: Billing(true)), Owner, s.Project.Id, "GetExecutionStatus", args: ("executionId", s.Run.Id));

            r.GetProperty("creditsCharged").GetDecimal().Should().Be(27m);
            r.GetProperty("creditsHeldOrUnsettled").ValueKind.Should().Be(JsonValueKind.Null);
            r.GetProperty("costNote").GetString().Should().Contain("Final").And.Contain("not the estimate");
        }
    }

    [Theory]
    [InlineData("INSERT_TRACKING_FAILED", "our side")]
    [InlineData("INSERT_NOT_APPLIED", "the clip")]
    public async Task GetExecutionStatus_failed_insert_carries_the_code_and_a_next_step_hint(string code, string cause)
    {
        Scenario s = Seed(ExecutionStatus.Failed);
        using (s.Db)
        {
            WorkflowStepResult compile = s.Db.WorkflowStepResults.Single(r => r.WorkflowStepId == s.Compile.Id);
            compile.Status = StepStatus.Failed;
            compile.FailureCode = code;
            s.Db.SaveChanges();

            JsonElement r = await InvokeAsync(Tools(s.Db, Owner), Owner, s.Project.Id, "GetExecutionStatus", args: ("executionId", s.Run.Id));

            r.GetProperty("failureCode").GetString().Should().Be(code);
            r.GetProperty("nextStepHint").GetString().Should().Contain("ProposeSetMediaOverlay").And.Contain("Do not retry").And.Contain(cause);
        }
    }

    [Fact]
    public async Task GetExecutionStatus_other_statuses_carry_no_failureCode_or_hint()
    {
        Scenario s = Seed(ExecutionStatus.Passed);
        using (s.Db)
        {
            JsonElement r = await InvokeAsync(Tools(s.Db, Owner), Owner, s.Project.Id, "GetExecutionStatus", args: ("executionId", s.Run.Id));

            r.TryGetProperty("failureCode", out _).Should().BeFalse();
            r.TryGetProperty("nextStepHint", out _).Should().BeFalse();
        }
    }

    [Theory]
    [InlineData(ExecutionStatus.Passed)]
    [InlineData(ExecutionStatus.Running)]
    public async Task GetExecutionStatus_superseded_failed_insert_row_gives_no_failureCode_or_hint(ExecutionStatus status)
    {
        Scenario s = Seed(status);
        using (s.Db)
        {
            s.Db.WorkflowStepResults.Add(new WorkflowStepResult
            {
                Id = Guid.NewGuid(),
                WorkflowExecutionId = s.Run.Id,
                WorkflowStepId = s.Compile.Id,
                Status = StepStatus.Failed,
                FailureCode = "INSERT_NOT_APPLIED",
                ExecutedAt = DateTime.UtcNow.AddMinutes(-5),
                SupersededAt = DateTime.UtcNow.AddMinutes(-4),
            });
            s.Db.SaveChanges();

            JsonElement r = await InvokeAsync(Tools(s.Db, Owner), Owner, s.Project.Id, "GetExecutionStatus", args: ("executionId", s.Run.Id));

            r.TryGetProperty("failureCode", out _).Should().BeFalse();
            r.TryGetProperty("nextStepHint", out _).Should().BeFalse();
        }
    }

    [Fact]
    public async Task GetExecutionStatus_re_failed_run_picks_the_newest_unsuperseded_failure()
    {
        Scenario s = Seed(ExecutionStatus.Failed);
        using (s.Db)
        {
            WorkflowStepResult old = s.Db.WorkflowStepResults.Single(r => r.WorkflowStepId == s.Compile.Id);
            old.Status = StepStatus.Failed;
            old.FailureCode = "INSERT_NOT_APPLIED";
            old.ExecutedAt = DateTime.UtcNow.AddMinutes(-10);
            s.Db.WorkflowStepResults.Add(new WorkflowStepResult
            {
                Id = Guid.NewGuid(),
                WorkflowExecutionId = s.Run.Id,
                WorkflowStepId = s.Compile.Id,
                Status = StepStatus.Failed,
                FailureCode = "INSERT_TRACKING_FAILED",
                ExecutedAt = DateTime.UtcNow.AddMinutes(-1),
            });
            s.Db.SaveChanges();

            JsonElement r = await InvokeAsync(Tools(s.Db, Owner), Owner, s.Project.Id, "GetExecutionStatus", args: ("executionId", s.Run.Id));

            r.GetProperty("failureCode").GetString().Should().Be("INSERT_TRACKING_FAILED");
        }
    }

    private static void AddReserve(Scenario s, WorkflowStepResult step, bool closed, CreditLedgerEntryKind closing = CreditLedgerEntryKind.Settle)
    {
        var reserve = new CreditLedgerEntry
        {
            Id = Guid.NewGuid(),
            AccountId = Guid.NewGuid(),
            OrganizationId = TestTenancy.OrgOf(Owner),
            Kind = CreditLedgerEntryKind.Reserve,
            ReservedDeltaMillis = 30_000,
            ExecutionId = s.Run.Id,
            StepResultId = step.Id,
            IdempotencyKey = "reserve-" + step.Id
        };
        s.Db.CreditLedgerEntries.Add(reserve);
        if (closed)
        {
            s.Db.CreditLedgerEntries.Add(new CreditLedgerEntry
            {
                Id = Guid.NewGuid(),
                AccountId = reserve.AccountId,
                OrganizationId = reserve.OrganizationId,
                Kind = closing,
                RefEntryId = reserve.Id,
                ExecutionId = s.Run.Id,
                StepResultId = step.Id,
                IdempotencyKey = "close-" + step.Id
            });
        }
    }

    [Fact]
    public async Task GetExecutionStatus_run_with_a_step_whose_hold_is_still_open_says_the_figure_is_not_final()
    {
        Scenario s = Seed(ExecutionStatus.Passed);
        using (s.Db)
        {
            WorkflowStepResult step = s.Db.WorkflowStepResults.First(r => r.WorkflowExecutionId == s.Run.Id && r.CreditsChargedMillis == 25_000);
            step.CreditsUnsettledMillis = 4_500;
            AddReserve(s, step, closed: false);
            await s.Db.SaveChangesAsync();

            JsonElement r = await InvokeAsync(Tools(s.Db, Owner, configuration: Billing(true)), Owner, s.Project.Id, "GetExecutionStatus", args: ("executionId", s.Run.Id));

            r.GetProperty("creditsHeldOrUnsettled").GetDecimal().Should().Be(4.5m);
            r.GetProperty("costNote").GetString().Should().Contain("still being settled");
        }
    }

    [Fact]
    public async Task GetExecutionStatus_once_the_sweep_closed_the_hold_the_figure_is_final_again()
    {
        Scenario s = Seed(ExecutionStatus.Passed);
        using (s.Db)
        {
            // The step settled nothing itself (charge 0) and recorded 4.5 credits; the sweep then settled the hold at that amount.
            WorkflowStepResult step = s.Db.WorkflowStepResults.First(r => r.WorkflowExecutionId == s.Run.Id && r.CreditsChargedMillis == 25_000);
            step.CreditsChargedMillis = 0;
            step.CreditsUnsettledMillis = 4_500;
            step.Status = StepStatus.Completed;
            AddReserve(s, step, closed: true);
            await s.Db.SaveChangesAsync();

            JsonElement r = await InvokeAsync(Tools(s.Db, Owner, configuration: Billing(true)), Owner, s.Project.Id, "GetExecutionStatus", args: ("executionId", s.Run.Id));

            r.GetProperty("creditsHeldOrUnsettled").ValueKind.Should().Be(JsonValueKind.Null);
            r.GetProperty("costNote").GetString().Should().Contain("Final");
            r.GetProperty("creditsCharged").GetDecimal().Should().Be(6.5m, "2 credits from the other step plus the swept 4.5");
        }
    }

    [Fact]
    public async Task GetExecutionStatus_running_run_says_so_far_and_billing_off_claims_no_cost()
    {
        Scenario s = Seed(ExecutionStatus.Running);
        using (s.Db)
        {
            JsonElement on = await InvokeAsync(Tools(s.Db, Owner, configuration: Billing(true)), Owner, s.Project.Id, "GetExecutionStatus", args: ("executionId", s.Run.Id));
            JsonElement off = await InvokeAsync(Tools(s.Db, Owner, configuration: Billing(false)), Owner, s.Project.Id, "GetExecutionStatus", args: ("executionId", s.Run.Id));

            on.GetProperty("costNote").GetString().Should().Contain("so far");
            off.GetProperty("creditsCharged").ValueKind.Should().Be(JsonValueKind.Null);
            off.GetProperty("costNote").ValueKind.Should().Be(JsonValueKind.Null);
        }
    }

    [Fact]
    public async Task GetExecutionStatus_refunded_failed_step_contributes_zero_to_the_net_figure()
    {
        Scenario s = Seed(ExecutionStatus.Failed);
        using (s.Db)
        {
            WorkflowStepResult failed = s.Db.WorkflowStepResults.First(r => r.WorkflowExecutionId == s.Run.Id && r.CreditsChargedMillis == 0);
            failed.Status = StepStatus.Failed;
            failed.CompletedAt = DateTime.UtcNow;
            failed.FailureClass = FailureClass.PlatformInternal;
            failed.CreditsUnsettledMillis = 7_000; // the step recorded what it metered; the sweep then RELEASED the hold (platform failure), so nothing was charged
            AddReserve(s, failed, closed: true, CreditLedgerEntryKind.Release);
            await s.Db.SaveChangesAsync();

            JsonElement r = await InvokeAsync(Tools(s.Db, Owner, configuration: Billing(true)), Owner, s.Project.Id, "GetExecutionStatus", args: ("executionId", s.Run.Id));

            r.GetProperty("creditsCharged").GetDecimal().Should().Be(2m, "the refunded step charged nothing");
            r.GetProperty("costNote").GetString().Should().Contain("Final");
        }
    }

    [Fact]
    public async Task GetExecutionStatus_another_organisation_gets_no_cost_fields()
    {
        Scenario s = Seed(ExecutionStatus.Passed);
        using (s.Db)
        {
            JsonElement foreign = await InvokeAsync(Tools(s.Db, Stranger, configuration: Billing(true)), Stranger, s.Project.Id, "GetExecutionStatus", args: ("executionId", s.Run.Id));

            foreign.TryGetProperty("creditsCharged", out _).Should().BeFalse();
            foreign.TryGetProperty("costNote", out _).Should().BeFalse();
            foreign.TryGetProperty("creditsHeldOrUnsettled", out _).Should().BeFalse();
        }
    }

    [Fact]
    public async Task GetExecutionStatus_returns_at_once_for_a_run_that_is_already_terminal()
    {
        Scenario s = Seed(ExecutionStatus.Passed);
        using (s.Db)
        {
            AssistantRunTools tools = Tools(s.Db, Owner, configuration: Billing(true));

            JsonElement result = await InvokeAsync(tools, Owner, s.Project.Id, "GetExecutionStatus", args: ("executionId", s.Run.Id));

            result.GetProperty("retryAfterSeconds").ValueKind.Should().Be(JsonValueKind.Null);
            result.GetProperty("status").GetString().Should().Be("Passed");
            result.GetProperty("terminal").GetBoolean().Should().BeTrue();
            result.GetProperty("creditsCharged").GetDecimal().Should().Be(27m);
            JsonElement progress = result.GetProperty("progress");
            progress.GetProperty("stepsTotal").GetInt32().Should().Be(2);
            progress.GetProperty("stepsDone").GetInt32().Should().Be(2);
            JsonElement output = result.GetProperty("outputs").EnumerateArray().Single();
            output.GetProperty("outputId").GetGuid().Should().Be(s.Run.StepResults.Single(r => r.WorkflowStepId == s.Compile.Id).Id);
            output.GetProperty("stepOrder").GetInt32().Should().Be(2);
            output.GetProperty("label").GetString().Should().Be("Compile");
            result.GetRawText().Should().NotContain("outputFiles");
        }
    }

    [Fact]
    public async Task GetExecutionStatus_on_a_running_run_returns_at_once_with_progress_and_a_retry_hint()
    {
        Scenario s = Seed(ExecutionStatus.Running);
        using (s.Db)
        {
            JsonElement result = await InvokeAsync(Tools(s.Db, Owner), Owner, s.Project.Id, "GetExecutionStatus", args: ("executionId", s.Run.Id));

            result.GetProperty("terminal").GetBoolean().Should().BeFalse();
            result.GetProperty("progress").GetProperty("stepsDone").GetInt32().Should().Be(1);
            result.GetProperty("progress").GetProperty("currentStep").GetProperty("label").GetString().Should().Be("Compile");
            result.GetProperty("retryAfterSeconds").GetInt32().Should().Be(30, "the seeded run is 3 minutes old");
            result.GetProperty("hint").GetString().Should().Be("check again in 30 seconds");
        }
    }

    [Theory]
    [InlineData(0, 15)]
    [InlineData(119, 15)]
    [InlineData(120, 30)]
    [InlineData(599, 30)]
    [InlineData(600, 60)]
    [InlineData(7200, 60)]
    public void Retry_hint_follows_the_age_of_the_run(int ageSeconds, int expected) =>
        AssistantRunTools.RetryAfterSeconds(TimeSpan.FromSeconds(ageSeconds)).Should().Be(expected);

    [Fact]
    public async Task GetExecutionStatus_on_a_failed_run_is_terminal_with_no_retry_and_the_capped_error()
    {
        Scenario s = Seed(ExecutionStatus.Failed);
        using (s.Db)
        {
            JsonElement result = await InvokeAsync(Tools(s.Db, Owner), Owner, s.Project.Id, "GetExecutionStatus", args: ("executionId", s.Run.Id));

            result.GetProperty("status").GetString().Should().Be("Failed");
            result.GetProperty("terminal").GetBoolean().Should().BeTrue();
            result.GetProperty("retryAfterSeconds").ValueKind.Should().Be(JsonValueKind.Null);
            result.GetProperty("hint").GetString().Should().Be("finished");
            result.GetProperty("errorMessage").GetString().Should().StartWith("Compile failed: ").And.Contain("[truncated");
        }
    }

    [Fact]
    public async Task GetExecutionStatus_reports_NeedsAttention_for_a_Passed_run_that_stopped_below_its_minimum()
    {
        Scenario s = Seed(ExecutionStatus.Passed);
        using (s.Db)
        {
            s.Run.AttentionJson = """{"reason":"review_below_minimum","stepOrder":4,"score":5,"minScore":8,"iterations":3}""";
            s.Db.SaveChanges();

            JsonElement result = await InvokeAsync(Tools(s.Db, Owner, configuration: Billing(true)), Owner, s.Project.Id, "GetExecutionStatus", args: ("executionId", s.Run.Id));

            result.GetProperty("status").GetString().Should().Be("NeedsAttention");
            result.GetProperty("terminal").GetBoolean().Should().BeTrue();
            result.GetProperty("message").GetString().Should().Be(
                "The review stopped after 3 rounds at 5/10; the target was 8/10. The film is ready but did not reach the quality bar.");
            JsonElement attention = result.GetProperty("needsAttention");
            attention.GetProperty("score").GetInt32().Should().Be(5);
            attention.GetProperty("minScore").GetInt32().Should().Be(8);
            attention.GetProperty("iterations").GetInt32().Should().Be(3);
            result.GetProperty("creditsCharged").GetDecimal().Should().Be(27m, "credits are unchanged");
        }
    }

    [Fact]
    public async Task GetExecutionStatus_without_attention_keeps_status_Passed_and_a_null_needsAttention()
    {
        Scenario s = Seed(ExecutionStatus.Passed);
        using (s.Db)
        {
            JsonElement result = await InvokeAsync(Tools(s.Db, Owner), Owner, s.Project.Id, "GetExecutionStatus", args: ("executionId", s.Run.Id));

            result.GetProperty("status").GetString().Should().Be("Passed");
            result.GetProperty("needsAttention").ValueKind.Should().Be(JsonValueKind.Null);
        }
    }

    [Fact]
    public async Task GetExecutionStatus_on_a_cancelled_run_is_terminal()
    {
        Scenario s = Seed(ExecutionStatus.Cancelled);
        using (s.Db)
        {
            JsonElement result = await InvokeAsync(Tools(s.Db, Owner), Owner, s.Project.Id, "GetExecutionStatus", args: ("executionId", s.Run.Id));
            result.GetProperty("terminal").GetBoolean().Should().BeTrue();
            result.GetProperty("status").GetString().Should().Be("Cancelled");
        }
    }

    [Fact]
    public async Task GetExecutionStatus_refuses_a_run_of_another_organisation_like_a_missing_one()
    {
        Scenario s = Seed(ExecutionStatus.Passed);
        using (s.Db)
        {
            JsonElement foreign = await InvokeAsync(Tools(s.Db, Stranger), Stranger, s.Project.Id, "GetExecutionStatus", args: ("executionId", s.Run.Id));
            JsonElement missing = await InvokeAsync(Tools(s.Db, Owner), Owner, s.Project.Id, "GetExecutionStatus", args: ("executionId", Guid.NewGuid()));

            foreign.TryGetProperty("status", out _).Should().BeFalse();
            foreign.TryGetProperty("error", out _).Should().BeTrue();
            missing.GetProperty("error").GetString().Should().Be(AssistantWorkflowTools.ExecutionNotFoundMessage);
        }
    }

    [Fact]
    public async Task GetCreditBalance_with_billing_off_says_so_and_never_fails()
    {
        using InferenceApiDbContext db = CreateDb();
        var balance = new FixedBalance(null);

        JsonElement result = await InvokeAsync(Tools(db, Owner, balance), Owner, null, "GetCreditBalance");

        result.GetProperty("billingEnabled").GetBoolean().Should().BeFalse();
        result.TryGetProperty("balance", out _).Should().BeFalse();
    }

    [Fact]
    public async Task GetCreditBalance_with_billing_on_returns_whole_credits_for_the_callers_organisation()
    {
        using InferenceApiDbContext db = CreateDb();
        DateTime renews = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
        var balance = new FixedBalance(new CreditBalanceSnapshot(150_500, 20_000, "creator", renews));

        JsonElement result = await InvokeAsync(Tools(db, Owner, balance), Owner, null, "GetCreditBalance");

        balance.AskedFor.Should().Be(TestTenancy.OrgOf(Owner));
        result.GetProperty("billingEnabled").GetBoolean().Should().BeTrue();
        result.GetProperty("balance").GetDecimal().Should().Be(150.5m);
        result.GetProperty("reserved").GetDecimal().Should().Be(20m);
        result.GetProperty("available").GetDecimal().Should().Be(130.5m);
        result.GetProperty("plan").GetString().Should().Be("creator");
        result.GetProperty("renewsAt").GetDateTime().Should().Be(renews);
    }

    [Fact]
    public async Task GetCreditBalance_runs_the_lazy_trial_grant_before_reading_so_a_new_workspace_does_not_read_zero()
    {
        using InferenceApiDbContext db = CreateDb();
        List<string> order = [];
        var provisioner = new Mock<ICreditAccountProvisioner>();
        provisioner.Setup(p => p.EnsureTrialGrantAsync(Owner, TestTenancy.OrgOf(Owner), It.IsAny<CancellationToken>()))
            .Callback(() => order.Add("grant"))
            .ReturnsAsync(default(TrialProvisioningOutcome));
        var balance = new OrderedBalance(order);

        await InvokeAsync(Tools(db, Owner, balance, trialCredits: provisioner.Object), Owner, null, "GetCreditBalance");

        order.Should().Equal("grant", "read");
    }

    private sealed class OrderedBalance(List<string> order) : ICreditBalanceReader
    {
        public Task<CreditBalanceSnapshot?> GetAsync(Guid organizationId, CancellationToken ct)
        {
            order.Add("read");
            return Task.FromResult<CreditBalanceSnapshot?>(null);
        }
    }

    // ---- WatchExecution ----

    private static IConfiguration Wake(params (string Key, string Value)[] kv) =>
        new ConfigurationBuilder().AddInMemoryCollection(kv.ToDictionary(x => x.Key, x => (string?)x.Value)).Build();

    private static async Task<(JsonElement Result, AssistantRunTools Tools)> WatchAsync(
        Scenario s, Guid user, Guid? thread, Guid? runId = null, IConfiguration? configuration = null)
    {
        AssistantRunTools tools = Tools(s.Db, user, configuration: configuration);
        AIFunction tool = tools.GetTools(new AssistantToolContext(user, s.Project.Id, ThreadId: thread)).Single(t => t.Name == "WatchExecution");
        object? raw = await tool.InvokeAsync(new AIFunctionArguments { ["executionId"] = runId ?? s.Run.Id });
        string text = raw is JsonElement { ValueKind: JsonValueKind.String } e ? e.GetString()! : raw!.ToString()!;
        return (JsonDocument.Parse(text).RootElement.Clone(), tools);
    }

    private static Guid NewThread(Scenario s)
    {
        var t = new AssistantThread { Id = Guid.NewGuid(), UserId = Owner, OrganizationId = s.Project.OrganizationId };
        s.Db.AssistantThreads.Add(t);
        s.Db.SaveChanges();
        return t.Id;
    }

    [Fact]
    public async Task WatchExecution_arms_one_pending_row_and_is_idempotent()
    {
        Scenario s = Seed(ExecutionStatus.Running);
        using (s.Db)
        {
            Guid thread = NewThread(s);
            (JsonElement first, _) = await WatchAsync(s, Owner, thread);
            (JsonElement second, _) = await WatchAsync(s, Owner, thread);

            first.GetProperty("watching").GetBoolean().Should().BeTrue();
            first.TryGetProperty("runName", out _).Should().BeFalse("no name from the database is returned to the model");
            first.GetProperty("status").GetString().Should().Be("Running");
            first.GetProperty("note").GetString().Should().Contain("End your turn now");
            second.GetProperty("watching").GetBoolean().Should().BeTrue();
            AssistantRunWatch row = s.Db.AssistantRunWatches.Single();
            row.Status.Should().Be(AssistantRunWatchStatus.Pending);
            row.ExecutionId.Should().Be(s.Run.Id);
            row.ThreadId.Should().Be(thread);
            row.ProjectId.Should().Be(s.Project.Id);
        }
    }

    [Fact]
    public async Task WatchExecution_on_a_terminal_run_writes_no_row()
    {
        Scenario s = Seed(ExecutionStatus.Passed);
        using (s.Db)
        {
            (JsonElement r, _) = await WatchAsync(s, Owner, NewThread(s));

            r.GetProperty("watching").GetBoolean().Should().BeFalse();
            r.GetProperty("terminal").GetBoolean().Should().BeTrue();
            r.GetProperty("hint").GetString().Should().Contain("GetWorkflowExecution");
            s.Db.AssistantRunWatches.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task WatchExecution_is_refused_over_the_per_thread_cap_and_when_disabled()
    {
        Scenario s = Seed(ExecutionStatus.Running);
        using (s.Db)
        {
            Guid thread = NewThread(s);
            IConfiguration cap1 = Wake(("Assistant:RunWake:MaxPendingPerThread", "1"));
            (JsonElement a, _) = await WatchAsync(s, Owner, thread, configuration: cap1);

            WorkflowExecution other = new() { Id = Guid.NewGuid(), ProjectId = s.Project.Id, WorkflowDefinitionId = s.Run.WorkflowDefinitionId, Status = ExecutionStatus.Running };
            s.Db.WorkflowExecutions.Add(other);
            s.Db.SaveChanges();
            (JsonElement b, _) = await WatchAsync(s, Owner, thread, other.Id, cap1);
            (JsonElement off, _) = await WatchAsync(s, Owner, thread, other.Id, Wake(("Assistant:RunWake:Enabled", "false")));

            a.GetProperty("watching").GetBoolean().Should().BeTrue();
            b.GetProperty("error").GetString().Should().Contain("GetExecutionStatus");
            off.GetProperty("error").GetString().Should().Contain("GetExecutionStatus");
            s.Db.AssistantRunWatches.Count().Should().Be(1);
        }
    }

    [Fact]
    public async Task WatchExecution_is_refused_over_the_per_organisation_cap()
    {
        Scenario s = Seed(ExecutionStatus.Running);
        using (s.Db)
        {
            IConfiguration cap1 = Wake(("Assistant:RunWake:MaxPendingPerOrganization", "1"));
            await WatchAsync(s, Owner, NewThread(s), configuration: cap1);
            (JsonElement b, _) = await WatchAsync(s, Owner, NewThread(s), configuration: cap1);

            b.GetProperty("error").GetString().Should().Contain("GetExecutionStatus");
            s.Db.AssistantRunWatches.Count().Should().Be(1);
        }
    }

    [Fact]
    public void WatchExecution_without_a_thread_is_absent_from_the_catalog_and_refuses_if_forced()
    {
        Scenario s = Seed(ExecutionStatus.Running);
        using (s.Db)
        {
            AssistantRunTools tools = Tools(s.Db, Owner);

            tools.GetTools(new AssistantToolContext(Owner, s.Project.Id)).Select(t => t.Name)
                .Should().BeEquivalentTo("GetExecutionStatus", "GetCreditBalance");
            tools.GetTools(new AssistantToolContext(Owner, s.Project.Id, ThreadId: Guid.NewGuid())).Select(t => t.Name)
                .Should().Contain("WatchExecution");
        }
    }

    [Fact]
    public async Task WatchExecution_refuses_a_foreign_project_and_a_foreign_run_like_a_missing_one()
    {
        Scenario s = Seed(ExecutionStatus.Running);
        using (s.Db)
        {
            (JsonElement foreign, _) = await WatchAsync(s, Stranger, Guid.NewGuid());
            (JsonElement missing, _) = await WatchAsync(s, Owner, NewThread(s), Guid.NewGuid());

            foreign.TryGetProperty("watching", out _).Should().BeFalse();
            foreign.TryGetProperty("error", out _).Should().BeTrue();
            missing.GetProperty("error").GetString().Should().Be(AssistantWorkflowTools.ExecutionNotFoundMessage);
            s.Db.AssistantRunWatches.Should().BeEmpty();
        }
    }

    [Fact]
    public void Captions_cover_all_tools()
    {
        AssistantRunTools.Captions.Keys.Should().BeEquivalentTo("GetExecutionStatus", "WatchExecution", "GetCreditBalance");
        AssistantRunTools.Captions.Values.Should().OnlyContain(c => !string.IsNullOrWhiteSpace(c));
    }
}

public class CreditBalanceReaderTests
{
    private static InferenceApiDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static CreditBalanceReader Reader(InferenceApiDbContext db, bool enabled) =>
        new(db, Options.Create(new BillingOptions { Enabled = enabled }));

    [Fact]
    public async Task Billing_off_yields_no_snapshot()
    {
        using InferenceApiDbContext db = CreateDb();
        (await Reader(db, enabled: false).GetAsync(Guid.NewGuid(), CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Billing_on_reads_the_account_and_the_current_subscription_plan()
    {
        using InferenceApiDbContext db = CreateDb();
        Guid org = Guid.NewGuid();
        var plan = new Plan { Id = Guid.NewGuid(), Key = "creator", Name = "Creator", IsActive = true };
        db.Plans.Add(plan);
        db.Set<CreditAccount>().Add(new CreditAccount { Id = Guid.NewGuid(), OrganizationId = org, BalanceMillis = 90_000, ReservedMillis = 10_000 });
        db.Subscriptions.Add(new Subscription
        {
            Id = Guid.NewGuid(),
            OrganizationId = org,
            PlanId = plan.Id,
            Status = SubscriptionStatus.Active,
            PeriodEnd = new DateTime(2099, 11, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        await db.SaveChangesAsync();

        CreditBalanceSnapshot? snapshot = await Reader(db, enabled: true).GetAsync(org, CancellationToken.None);

        snapshot.Should().NotBeNull();
        snapshot!.BalanceMillis.Should().Be(90_000);
        snapshot.ReservedMillis.Should().Be(10_000);
        snapshot.Plan.Should().Be("creator");
        snapshot.RenewsAt.Should().Be(new DateTime(2099, 11, 1, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public async Task An_organisation_without_an_account_or_subscription_reads_as_zero_on_the_trial_plan()
    {
        using InferenceApiDbContext db = CreateDb();
        db.Plans.Add(new Plan { Id = Guid.NewGuid(), Key = "trial", Name = "Trial", Kind = PlanKind.Trial, IsActive = true });
        await db.SaveChangesAsync();

        CreditBalanceSnapshot? snapshot = await Reader(db, enabled: true).GetAsync(Guid.NewGuid(), CancellationToken.None);

        snapshot!.BalanceMillis.Should().Be(0);
        snapshot.Plan.Should().Be("trial");
        snapshot.RenewsAt.Should().BeNull();
    }
}
