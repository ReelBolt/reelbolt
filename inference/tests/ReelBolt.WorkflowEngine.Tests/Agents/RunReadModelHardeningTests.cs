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
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Billing;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Agents;

/// <summary>
/// T-024: <c>GetWorkflowExecution</c> and <c>GetExecutionStatus</c> read the stored run through
/// <see cref="StepOutputSanitizer"/> (QA-P0-8, S-13, S-14), GetExecutionStatus names the run from the first
/// poll (QA-P0-13), and per-step credits are omitted with billing off (QA-P0-15).
/// </summary>
public class RunReadModelHardeningTests
{
    private static readonly Guid Owner = Guid.NewGuid();

    private sealed record Scenario(InferenceApiDbContext Db, Project Project, WorkflowExecution Run, WorkflowStepResult Compile);

    private static Scenario Seed(Action<WorkflowExecution, WorkflowStepResult>? customise = null, bool withResults = true)
    {
        InferenceApiDbContext db = new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var project = new Project { Id = Guid.NewGuid(), Name = "Launch", OwnerId = Owner, OrganizationId = TestTenancy.OrgOf(Owner) };
        var workflow = new WorkflowDefinition { Id = Guid.NewGuid(), Name = "Film", ProjectId = project.Id };
        var compile = new WorkflowStep { Id = Guid.NewGuid(), WorkflowDefinitionId = workflow.Id, AgentDefinitionId = Guid.NewGuid(), StepOrder = 4, Label = "Compile", StepType = StepType.VideoCompile };
        workflow.Steps.Add(compile);
        var run = new WorkflowExecution
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            WorkflowDefinitionId = workflow.Id,
            Status = ExecutionStatus.Failed,
            StartedAt = new DateTime(2026, 10, 17, 10, 0, 0, DateTimeKind.Utc),
            CompletedAt = new DateTime(2026, 10, 17, 10, 5, 0, DateTimeKind.Utc),
        };
        string key = $"projects/{project.Id}/outputFiles/{run.Id}/final.mp4";
        var result = new WorkflowStepResult
        {
            Id = Guid.NewGuid(),
            WorkflowExecutionId = run.Id,
            WorkflowStepId = compile.Id,
            Status = StepStatus.Completed,
            ExecutedAt = new DateTime(2026, 10, 17, 10, 1, 0, DateTimeKind.Utc),
            CompletedAt = new DateTime(2026, 10, 17, 10, 2, 0, DateTimeKind.Utc),
            CreditsChargedMillis = 24_500,
            OutputStorageKey = key,
            OutputJson = $$"""{"status":"completed","outputStorageKey":"{{key}}","artifactStorageKey":"projects/{{project.Id}}/agentFiles/video-analysis/{{run.Id}}/step-4-edl.json","timelineStorageKey":"projects/{{project.Id}}/agentFiles/video-analysis/{{run.Id}}/step-4-timeline.json","segments":7,"outputDurationSec":61.2}""",
        };
        if (withResults)
            run.StepResults.Add(result);
        customise?.Invoke(run, result);
        db.Projects.Add(project);
        db.WorkflowDefinitions.Add(workflow);
        db.WorkflowExecutions.Add(run);
        db.SaveChanges();
        return new Scenario(db, project, run, result);
    }

    private static IConfiguration Billing(bool enabled) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["Billing:Enabled"] = enabled ? "true" : "false" })
        .Build();

    private static async Task<JsonElement> GetExecutionAsync(Scenario s, bool billing = true)
    {
        var current = new FakeCurrentUser(Owner);
        var tools = new AssistantWorkflowTools(s.Db, ConfirmationTokenService.CreateEphemeral(), TestTenancy.Access(s.Db, current), current, Billing(billing));
        AIFunction tool = tools.GetTools(new AssistantToolContext(Owner, s.Project.Id)).Single(t => t.Name == "GetWorkflowExecution");
        return Parse(await tool.InvokeAsync(new AIFunctionArguments { ["executionId"] = s.Run.Id }));
    }

    private sealed class NoBalance : ICreditBalanceReader
    {
        public Task<CreditBalanceSnapshot?> GetAsync(Guid organizationId, CancellationToken ct) => Task.FromResult<CreditBalanceSnapshot?>(null);
    }

    private static async Task<JsonElement> WaitAsync(Scenario s)
    {
        var current = new FakeCurrentUser(Owner);
        var tools = new AssistantRunTools(s.Db, TestTenancy.Access(s.Db, current), current, new NoBalance());
        AIFunction tool = tools.GetTools(new AssistantToolContext(Owner, s.Project.Id)).Single(t => t.Name == "GetExecutionStatus");
        return Parse(await tool.InvokeAsync(new AIFunctionArguments { ["executionId"] = s.Run.Id, ["timeoutSeconds"] = 5 }));
    }

    private static JsonElement Parse(object? raw)
    {
        string text = raw is JsonElement { ValueKind: JsonValueKind.String } str ? str.GetString()! : raw!.ToString()!;
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private const string RawFailure = """
        System.Net.Http.HttpRequestException: Connection refused (minio:9000) uploading /tmp/reelbolt/ws-1/out.mp4
           at System.Net.Http.HttpConnectionPool.ConnectAsync(HttpRequestMessage request)
        """;

    [Fact]
    public async Task The_output_preview_of_a_real_compile_summary_carries_no_storage_key()
    {
        Scenario s = Seed();
        using (s.Db)
        {
            JsonElement run = await GetExecutionAsync(s);

            string preview = run.GetProperty("steps")[0].GetProperty("outputPreview").GetString()!;
            preview.Should().Contain("\"segments\":7");
            run.GetRawText().Should().NotContain("projects/").And.NotContain("StorageKey");
        }
    }

    [Fact]
    public async Task Step_error_details_are_the_failure_code_and_a_cleaned_sentence()
    {
        Scenario s = Seed((_, r) =>
        {
            r.Status = StepStatus.Failed;
            r.FailureCode = "ENCODE_FAILED";
            r.ErrorDetails = RawFailure;
        });
        using (s.Db)
        {
            string details = (await GetExecutionAsync(s)).GetProperty("steps")[0].GetProperty("errorDetails").GetString()!;

            details.Should().StartWith("ENCODE_FAILED: ").And.Contain("Connection refused");
            details.Should().NotContain("minio:9000").And.NotContain("/tmp/").And.NotContain("HttpRequestException").And.NotContain("ConnectAsync");
        }
    }

    [Fact]
    public async Task The_run_error_message_is_cleaned_in_both_read_models()
    {
        Scenario s = Seed((run, _) => run.ErrorMessage = RawFailure);
        using (s.Db)
        {
            foreach (JsonElement run in new[] { await GetExecutionAsync(s), await WaitAsync(s) })
            {
                string message = run.GetProperty("errorMessage").GetString()!;
                message.Should().Contain("Connection refused");
                message.Should().NotContain("minio:9000").And.NotContain("/tmp/").And.NotContain("HttpRequestException");
            }
        }
    }

    [Fact]
    public async Task A_legacy_budget_failure_is_read_without_the_platform_figures()
    {
        Scenario s = Seed((_, r) =>
        {
            r.Status = StepStatus.Failed;
            r.FailureCode = "BUDGET_EXCEEDED";
            r.OutputStorageKey = null;
            r.ErrorDetails = "This needs about 40 credits of video generation but the platform's daily video budget is 5000 credits and 4980 are already used today; it resets at 00:00 UTC (in 3 h). Generate fewer clips or wait.";
            r.OutputJson = "{\"clips\":[],\"meta\":{\"failed\":\"BUDGET_EXCEEDED\",\"error\":\"" + r.ErrorDetails + "\"}}";
        });
        using (s.Db)
        {
            JsonElement step = (await GetExecutionAsync(s)).GetProperty("steps")[0];

            step.GetProperty("errorDetails").GetString().Should().Be("BUDGET_EXCEEDED: " + StepOutputSanitizer.LegacyBudgetSentence);
            step.GetProperty("outputPreview").GetString().Should().Contain(StepOutputSanitizer.LegacyBudgetSentence)
                .And.NotContain("5000").And.NotContain("4980");
        }
    }

    [Fact]
    public async Task GetExecutionStatus_names_the_run_before_any_step_has_a_result()
    {
        Scenario s = Seed((run, _) => run.Status = ExecutionStatus.Queued, withResults: false);
        using (s.Db)
        {
            JsonElement result = await WaitAsync(s);

            result.GetProperty("executionId").GetGuid().Should().Be(s.Run.Id);
            result.GetProperty("terminal").GetBoolean().Should().BeFalse();
        }
    }

    [Fact]
    public async Task With_billing_off_per_step_credits_are_omitted_and_the_run_says_billing_is_off()
    {
        Scenario s = Seed();
        using (s.Db)
        {
            JsonElement run = await GetExecutionAsync(s, billing: false);

            run.GetProperty("billingEnabled").GetBoolean().Should().BeFalse();
            run.GetProperty("steps")[0].TryGetProperty("creditsCharged", out _).Should().BeFalse();
            run.GetProperty("totals").TryGetProperty("creditsCharged", out _).Should().BeFalse("nothing is metered with billing off");
            run.GetProperty("costNote").ValueKind.Should().Be(JsonValueKind.Null, "no claim of cost with billing off");
        }
    }

    [Fact]
    public async Task With_billing_on_per_step_credits_are_reported_and_the_run_says_billing_is_on()
    {
        Scenario s = Seed();
        using (s.Db)
        {
            JsonElement run = await GetExecutionAsync(s, billing: true);

            run.GetProperty("billingEnabled").GetBoolean().Should().BeTrue();
            run.GetProperty("steps")[0].GetProperty("creditsCharged").GetDecimal().Should().Be(24.5m);
            run.GetProperty("totals").GetProperty("creditsCharged").GetDecimal().Should().BeGreaterThan(0m);
            run.GetProperty("costNote").GetString().Should().Contain("not the estimate");
        }
    }

    [Fact]
    public async Task A_swept_hold_is_counted_once_in_the_run_totals_and_no_longer_reads_as_unsettled()
    {
        Scenario s = Seed((_, r) => { r.CreditsChargedMillis = 0; r.CreditsUnsettledMillis = 6_000; });
        using (s.Db)
        {
            var reserve = new CreditLedgerEntry
            {
                Id = Guid.NewGuid(),
                AccountId = Guid.NewGuid(),
                OrganizationId = TestTenancy.OrgOf(Owner),
                Kind = CreditLedgerEntryKind.Reserve,
                ReservedDeltaMillis = 30_000,
                ExecutionId = s.Run.Id,
                StepResultId = s.Compile.Id,
                IdempotencyKey = "reserve-swept"
            };
            s.Db.CreditLedgerEntries.Add(reserve);
            s.Db.CreditLedgerEntries.Add(new CreditLedgerEntry
            {
                Id = Guid.NewGuid(),
                AccountId = reserve.AccountId,
                OrganizationId = reserve.OrganizationId,
                Kind = CreditLedgerEntryKind.Settle,
                RefEntryId = reserve.Id,
                ExecutionId = s.Run.Id,
                StepResultId = s.Compile.Id,
                IdempotencyKey = "settle-swept"
            });
            await s.Db.SaveChangesAsync();

            JsonElement run = await GetExecutionAsync(s, billing: true);

            run.GetProperty("totals").GetProperty("creditsCharged").GetDecimal().Should().Be(6m);
            run.GetProperty("totals").GetProperty("creditsHeldOrUnsettled").ValueKind.Should().Be(JsonValueKind.Null);
            run.GetProperty("costNote").GetString().Should().Contain("Final");
        }
    }
}
