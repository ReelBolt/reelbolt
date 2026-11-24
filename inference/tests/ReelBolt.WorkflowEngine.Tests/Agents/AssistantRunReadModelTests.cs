using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Shared.Data.Models;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Agents;

/// <summary>
/// T-003: <c>GetWorkflowExecution</c> is the run read model the assistant and the MCP rely on to explain a
/// finished film: what each step cost, how long it took, why it failed, which output to look at, and
/// whether the render honoured the resolution the user asked for.
/// </summary>
public class AssistantRunReadModelTests
{
    private static readonly Guid Owner = Guid.NewGuid();
    private static readonly Guid Stranger = Guid.NewGuid();

    private static InferenceApiDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private sealed record Scenario(InferenceApiDbContext Db, Project Project, WorkflowExecution Run, WorkflowStep Compile, WorkflowStep Analyze);

    private static Scenario Seed(Action<WorkflowExecution, WorkflowStep, WorkflowStep>? customise = null)
    {
        InferenceApiDbContext db = CreateDb();
        var agent = new AgentDefinition { Id = Guid.NewGuid(), Name = "Agent", AgentType = AgentType.VideoTransform, IsBuiltIn = true };
        var project = new Project { Id = Guid.NewGuid(), Name = "Launch", OwnerId = Owner, OrganizationId = TestTenancy.OrgOf(Owner) };
        var workflow = new WorkflowDefinition { Id = Guid.NewGuid(), Name = "Film", ProjectId = project.Id };
        var analyze = new WorkflowStep { Id = Guid.NewGuid(), WorkflowDefinitionId = workflow.Id, AgentDefinitionId = agent.Id, StepOrder = 1, Label = "Analyze", StepType = StepType.VideoAnalyze };
        var compile = new WorkflowStep { Id = Guid.NewGuid(), WorkflowDefinitionId = workflow.Id, AgentDefinitionId = agent.Id, StepOrder = 4, Label = "Compile", StepType = StepType.VideoCompile };
        workflow.Steps.Add(analyze);
        workflow.Steps.Add(compile);
        var run = new WorkflowExecution
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            WorkflowDefinitionId = workflow.Id,
            Status = ExecutionStatus.Passed,
            StartedAt = new DateTime(2026, 10, 17, 10, 0, 0, DateTimeKind.Utc),
            CompletedAt = new DateTime(2026, 10, 17, 10, 5, 0, DateTimeKind.Utc),
        };
        run.StepResults.Add(new WorkflowStepResult
        {
            Id = Guid.NewGuid(),
            WorkflowExecutionId = run.Id,
            WorkflowStepId = analyze.Id,
            Status = StepStatus.Completed,
            DurationMs = 4_000,
            ExecutedAt = new DateTime(2026, 10, 17, 10, 0, 1, DateTimeKind.Utc),
            CompletedAt = new DateTime(2026, 10, 17, 10, 0, 5, DateTimeKind.Utc),
            CreditsChargedMillis = 1_250,
        });
        run.StepResults.Add(new WorkflowStepResult
        {
            Id = Guid.NewGuid(),
            WorkflowExecutionId = run.Id,
            WorkflowStepId = compile.Id,
            Status = StepStatus.Completed,
            DurationMs = 60_000,
            ExecutedAt = new DateTime(2026, 10, 17, 10, 1, 0, DateTimeKind.Utc),
            CompletedAt = new DateTime(2026, 10, 17, 10, 2, 0, DateTimeKind.Utc),
            CreditsChargedMillis = 24_500,
            OutputStorageKey = $"projects/{project.Id}/outputFiles/{run.Id}/final.mp4",
        });
        customise?.Invoke(run, analyze, compile);
        db.Projects.Add(project);
        db.WorkflowDefinitions.Add(workflow);
        db.WorkflowExecutions.Add(run);
        db.SaveChanges();
        return new Scenario(db, project, run, compile, analyze);
    }

    private static async Task<JsonElement> InvokeAsync(InferenceApiDbContext db, Guid user, Guid projectId, Guid executionId)
    {
        var current = new FakeCurrentUser(user);
        // Per-step credits are a billing fact: these runs read as on a metered install (T-024, QA-P0-15).
        IConfiguration billingOn = new ConfigurationBuilder()
            .AddInMemoryCollection(new System.Collections.Generic.Dictionary<string, string?> { ["Billing:Enabled"] = "true" })
            .Build();
        var tools = new AssistantWorkflowTools(db, ConfirmationTokenService.CreateEphemeral(), TestTenancy.Access(db, current), current, billingOn);
        AIFunction tool = tools.GetTools(new AssistantToolContext(user, projectId)).Single(t => t.Name == "GetWorkflowExecution");
        object? raw = await tool.InvokeAsync(new AIFunctionArguments { ["executionId"] = executionId });
        string text = raw is JsonElement { ValueKind: JsonValueKind.String } s ? s.GetString()! : raw!.ToString()!;
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    [Fact]
    public async Task Steps_carry_credits_durations_failure_fields_and_the_playable_output_id()
    {
        Scenario s = Seed((run, analyze, compile) =>
        {
            WorkflowStepResult failed = run.StepResults.Single(r => r.WorkflowStepId == analyze.Id);
            failed.Status = StepStatus.Failed;
            failed.FailureCode = "ENCODE_FAILED";
            failed.FailureClass = FailureClass.PlatformInternal;
        });
        using (s.Db)
        {
            JsonElement run = await InvokeAsync(s.Db, Owner, s.Project.Id, s.Run.Id);

            JsonElement first = run.GetProperty("steps")[0];
            first.GetProperty("creditsCharged").GetDecimal().Should().Be(1.25m);
            first.GetProperty("failureCode").GetString().Should().Be("ENCODE_FAILED");
            first.GetProperty("failureClass").GetString().Should().Be("PlatformInternal");
            first.GetProperty("startedAt").GetDateTime().Should().Be(new DateTime(2026, 10, 17, 10, 0, 1, DateTimeKind.Utc));
            first.GetProperty("completedAt").GetDateTime().Should().Be(new DateTime(2026, 10, 17, 10, 0, 5, DateTimeKind.Utc));
            first.GetProperty("outputId").ValueKind.Should().Be(JsonValueKind.Null);

            JsonElement second = run.GetProperty("steps")[1];
            second.GetProperty("creditsCharged").GetDecimal().Should().Be(24.5m);
            second.GetProperty("outputId").GetGuid().Should().Be(
                s.Run.StepResults.Single(r => r.WorkflowStepId == s.Compile.Id).Id);

            run.GetRawText().Should().NotContain("outputFiles", "storage keys never reach the model");
        }
    }

    [Theory]
    [InlineData(ExecutionStatus.Passed, true)]
    [InlineData(ExecutionStatus.Failed, false)]
    public async Task A_passed_run_with_attention_reads_as_NeedsAttention_and_no_other_run_does(ExecutionStatus status, bool expectAttention)
    {
        Scenario s = Seed((run, _, _) =>
        {
            run.Status = status;
            run.AttentionJson = """{"reason":"review_below_minimum","stepOrder":4,"score":5,"minScore":8,"iterations":3}""";
        });
        using (s.Db)
        {
            JsonElement run = await InvokeAsync(s.Db, Owner, s.Project.Id, s.Run.Id);

            if (expectAttention)
            {
                run.GetProperty("status").GetString().Should().Be("NeedsAttention");
                run.GetProperty("needsAttention").GetProperty("score").GetInt32().Should().Be(5);
            }
            else
            {
                run.GetProperty("status").GetString().Should().Be(status.ToString());
                run.GetProperty("needsAttention").ValueKind.Should().Be(JsonValueKind.Null);
            }
        }
    }

    [Fact]
    public async Task Credits_are_null_for_a_step_that_never_ran()
    {
        Scenario s = Seed((run, _, compile) =>
        {
            WorkflowStepResult pending = run.StepResults.Single(r => r.WorkflowStepId == compile.Id);
            pending.Status = StepStatus.Running;
            pending.CompletedAt = null;
            pending.CreditsChargedMillis = 0;
        });
        using (s.Db)
        {
            JsonElement run = await InvokeAsync(s.Db, Owner, s.Project.Id, s.Run.Id);
            run.GetProperty("steps")[1].GetProperty("creditsCharged").ValueKind.Should().Be(JsonValueKind.Null);
        }
    }

    [Fact]
    public async Task Totals_sum_credits_and_durations_and_count_passed_and_failed_steps()
    {
        Scenario s = Seed((run, analyze, _) =>
            run.StepResults.Single(r => r.WorkflowStepId == analyze.Id).Status = StepStatus.Failed);
        using (s.Db)
        {
            JsonElement totals = (await InvokeAsync(s.Db, Owner, s.Project.Id, s.Run.Id)).GetProperty("totals");

            totals.GetProperty("creditsCharged").GetDecimal().Should().Be(25.75m);
            totals.GetProperty("durationMs").GetInt64().Should().Be(300_000, "the run's own wall clock, StartedAt to CompletedAt");
            totals.GetProperty("steps").GetInt32().Should().Be(2);
            totals.GetProperty("passed").GetInt32().Should().Be(1);
            totals.GetProperty("failed").GetInt32().Should().Be(1);
        }
    }

    [Fact]
    public async Task Error_text_is_capped_at_8000_characters_not_1500()
    {
        Scenario s = Seed((run, analyze, _) =>
        {
            run.ErrorMessage = new string('e', 9_000);
            run.StepResults.Single(r => r.WorkflowStepId == analyze.Id).ErrorDetails = new string('d', 9_000);
        });
        using (s.Db)
        {
            JsonElement run = await InvokeAsync(s.Db, Owner, s.Project.Id, s.Run.Id);

            string details = run.GetProperty("steps")[0].GetProperty("errorDetails").GetString()!;
            details.Should().StartWith(new string('d', 8_000)).And.Contain("[truncated, 1000 more chars]");
            string message = run.GetProperty("errorMessage").GetString()!;
            message.Should().StartWith(new string('e', 8_000)).And.Contain("[truncated, 1000 more chars]");
        }
    }

    [Fact]
    public async Task Warnings_name_only_the_steps_whose_resolution_was_not_honoured()
    {
        Scenario s = Seed((run, analyze, compile) =>
        {
            run.StepResults.Single(r => r.WorkflowStepId == compile.Id).OutputJson =
                """{"outputResolution":{"requested":"2160p","width":1344,"height":768,"honoured":false,"reason":"the source clips are smaller than the request"}}""";
            run.StepResults.Single(r => r.WorkflowStepId == analyze.Id).OutputJson =
                """{"outputResolution":{"requested":"1080p","width":1920,"height":1080,"honoured":true}}""";
        });
        using (s.Db)
        {
            JsonElement run = await InvokeAsync(s.Db, Owner, s.Project.Id, s.Run.Id);

            run.GetProperty("warnings").GetArrayLength().Should().Be(1);
            run.GetProperty("warnings")[0].GetString().Should().Be(
                "Step 4 (Compile) produced 1344x768; the requested 2160p was not applied: the source clips are smaller than the request.");
        }
    }

    /// <summary>QA-P0-6: the over-quota note sits at the end of a large output, past the 1500-character preview, so
    /// the warning must come from the full OutputJson; a step registered within quota adds none.</summary>
    [Fact]
    public async Task Warnings_name_a_step_that_saved_outputs_while_the_workspace_was_over_its_storage_limit()
    {
        Scenario s = Seed((run, analyze, compile) =>
        {
            string padding = new('x', 4_000);
            run.StepResults.Single(r => r.WorkflowStepId == compile.Id).OutputJson =
                "{\"notes\":\"" + padding + "\",\"registration\":{\"registered\":2,\"overQuota\":true}}";
            run.StepResults.Single(r => r.WorkflowStepId == analyze.Id).OutputJson =
                """{"registration":{"registered":1,"overQuota":false}}""";
        });
        using (s.Db)
        {
            JsonElement run = await InvokeAsync(s.Db, Owner, s.Project.Id, s.Run.Id);

            run.GetProperty("warnings").GetArrayLength().Should().Be(1);
            run.GetProperty("warnings")[0].GetString().Should().Be(
                "Step 4 (Compile) saved its outputs as project files but the workspace is over its storage limit; "
                + "delete files or upgrade to keep adding files.");
        }
    }

    [Fact]
    public async Task Warnings_are_empty_and_unparseable_output_is_ignored_when_nothing_was_downgraded()
    {
        Scenario s = Seed((run, analyze, _) =>
            run.StepResults.Single(r => r.WorkflowStepId == analyze.Id).OutputJson = "not json at all");
        using (s.Db)
        {
            JsonElement run = await InvokeAsync(s.Db, Owner, s.Project.Id, s.Run.Id);
            run.GetProperty("warnings").GetArrayLength().Should().Be(0);
        }
    }

    [Fact]
    public async Task Someone_elses_run_is_refused_exactly_like_a_missing_one()
    {
        Scenario s = Seed();
        using (s.Db)
        {
            JsonElement run = await InvokeAsync(s.Db, Stranger, s.Project.Id, s.Run.Id);
            run.TryGetProperty("steps", out _).Should().BeFalse();
            run.TryGetProperty("error", out _).Should().BeTrue();
        }
    }
}
