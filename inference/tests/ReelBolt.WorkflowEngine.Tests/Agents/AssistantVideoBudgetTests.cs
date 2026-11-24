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
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;
using ReelBolt.WorkflowEngine.Services.VideoGeneration;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Agents;

/// <summary>
/// T-017: <c>GetWorkflowExecution</c> tells the assistant how much video budget the run's project has
/// left today, from the same ledger accounting the budget gate uses.
/// </summary>
public class AssistantVideoBudgetTests
{
    [Fact]
    public void The_snapshot_has_only_the_async_per_run_entry_point()
    {
        typeof(VideoBudgetSnapshot).GetMethod("ForRun").Should().BeNull("the synchronous twin blocked a request thread on the entitlement read");
        typeof(VideoBudgetSnapshot).GetMethod("ForRunAsync").Should().NotBeNull();
    }

    private static readonly Guid Owner = Guid.NewGuid();
    private static readonly Guid ProviderId = Guid.NewGuid();

    private sealed record Scenario(InferenceApiDbContext Db, Project Project, WorkflowExecution Run);

    private static Scenario Seed(bool withVideoGenerate = true)
    {
        var db = new InferenceApiDbContext(
            new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var agent = new AgentDefinition { Id = Guid.NewGuid(), Name = "Agent", AgentType = AgentType.VideoTransform, IsBuiltIn = true };
        var project = new Project { Id = Guid.NewGuid(), Name = "Launch", OwnerId = Owner, OrganizationId = TestTenancy.OrgOf(Owner) };
        var workflow = new WorkflowDefinition { Id = Guid.NewGuid(), Name = "Film", ProjectId = project.Id };
        var step = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = workflow.Id,
            AgentDefinitionId = agent.Id,
            StepOrder = 1,
            Label = "Generate",
            StepType = withVideoGenerate ? StepType.VideoGenerate : StepType.VideoCompile
        };
        workflow.Steps.Add(step);
        var run = new WorkflowExecution
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            WorkflowDefinitionId = workflow.Id,
            Status = ExecutionStatus.Failed,
            StartedAt = DateTime.UtcNow.AddMinutes(-5),
        };
        run.StepResults.Add(new WorkflowStepResult
        {
            Id = Guid.NewGuid(),
            WorkflowExecutionId = run.Id,
            WorkflowStepId = step.Id,
            Status = StepStatus.Failed,
            ExecutedAt = DateTime.UtcNow.AddMinutes(-4),
        });
        db.Projects.Add(project);
        db.WorkflowDefinitions.Add(workflow);
        db.WorkflowExecutions.Add(run);
        db.SaveChanges();
        return new Scenario(db, project, run);
    }

    private static ExternalGenerationJob Job(
        Guid projectId, Guid? orgId, decimal estimate, decimal? actual = null,
        ExternalGenerationJobStatus status = ExternalGenerationJobStatus.Succeeded,
        string unit = "credits", DateTime? createdAt = null, Guid? executionId = null) => new()
        {
            Id = Guid.NewGuid(),
            ExecutionId = executionId ?? Guid.NewGuid(),
            StepResultId = Guid.NewGuid(),
            ProjectId = projectId,
            OrganizationId = orgId,
            ClipId = "c",
            RequestHash = Guid.NewGuid().ToString(),
            ProviderId = ProviderId,
            Status = status,
            CostEstimate = estimate,
            ActualCost = actual,
            CostUnit = unit,
            CreatedAt = createdAt ?? DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

    private static IConfiguration Config(decimal? project = null, decimal? global = null, decimal? gpu = null, decimal? gpuGlobal = null)
    {
        var values = new Dictionary<string, string?>();
        if (project is not null) values["VideoGeneration:DailyBudgetCredits"] = project.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (global is not null) values["VideoGeneration:GlobalDailyBudgetCredits"] = global.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (gpu is not null) values["VideoGeneration:DailyGpuMinutes"] = gpu.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (gpuGlobal is not null) values["VideoGeneration:GlobalDailyGpuMinutes"] = gpuGlobal.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static async Task<JsonElement> InvokeAsync(
        Scenario s, IConfiguration? config = null, IOrganizationEntitlements? plan = null)
    {
        var current = new FakeCurrentUser(Owner);
        var tools = new AssistantWorkflowTools(
            s.Db, ConfirmationTokenService.CreateEphemeral(), TestTenancy.Access(s.Db, current), current, config, plan);
        AIFunction tool = tools.GetTools(new AssistantToolContext(Owner, s.Project.Id)).Single(t => t.Name == "GetWorkflowExecution");
        object? raw = await tool.InvokeAsync(new AIFunctionArguments { ["executionId"] = s.Run.Id });
        string text = raw is JsonElement { ValueKind: JsonValueKind.String } str ? str.GetString()! : raw!.ToString()!;
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    [Fact]
    public async Task A_run_without_a_VideoGenerate_step_has_no_videoBudget()
    {
        Scenario s = Seed(withVideoGenerate: false);
        using (s.Db)
        {
            JsonElement run = await InvokeAsync(s, Config(project: 100m));
            run.TryGetProperty("videoBudget", out _).Should().BeFalse();
        }
    }

    [Fact]
    public async Task The_project_cap_shows_used_remaining_and_the_next_UTC_midnight()
    {
        Scenario s = Seed();
        using (s.Db)
        {
            s.Db.ExternalGenerationJobs.Add(Job(s.Project.Id, s.Project.OrganizationId, 30m));
            s.Db.ExternalGenerationJobs.Add(Job(Guid.NewGuid(), null, 20m)); // another project: counts only globally
            s.Db.SaveChanges();

            JsonElement budget = (await InvokeAsync(s, Config(project: 100m, global: 1000m))).GetProperty("videoBudget");

            budget.GetProperty("unit").GetString().Should().Be("credits");
            budget.GetProperty("dailyCapCredits").GetDecimal().Should().Be(100m);
            budget.GetProperty("usedTodayCredits").GetDecimal().Should().Be(30m);
            budget.GetProperty("remainingTodayCredits").GetDecimal().Should().Be(70m);
            budget.GetProperty("limitedBy").GetString().Should().Be("project");
            budget.GetProperty("resetsAt").GetDateTime().ToUniversalTime().Should().Be(DateTime.UtcNow.Date.AddDays(1));
        }
    }

    [Fact]
    public async Task A_reached_platform_cap_shows_only_who_limits_and_when_it_resets()
    {
        Scenario s = Seed();
        using (s.Db)
        {
            s.Db.ExternalGenerationJobs.Add(Job(Guid.NewGuid(), null, 100m)); // another tenant used the whole platform cap
            s.Db.SaveChanges();

            JsonElement budget = (await InvokeAsync(s, Config(project: 1000m, global: 100m))).GetProperty("videoBudget");

            budget.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(["unit", "limitedBy", "resetsAt"],
                "S-01: no cap, used or remaining figure may be derived from other tenants' spend");
            budget.GetProperty("limitedBy").GetString().Should().Be("platform");
            budget.GetProperty("resetsAt").GetDateTime().ToUniversalTime().Should().Be(DateTime.UtcNow.Date.AddDays(1));
        }
    }

    [Theory]
    [InlineData(10.0, 60.0)]   // the platform still has room either way
    [InlineData(100.0, 140.0)] // the platform cap is reached either way
    public async Task Another_tenants_spend_cannot_be_inferred_from_a_tenants_videoBudget(double tenantBSpend, double otherTenantBSpend)
    {
        async Task<string> TenantAView(decimal bSpend)
        {
            Scenario s = Seed();
            using (s.Db)
            {
                s.Db.ExternalGenerationJobs.Add(Job(s.Project.Id, s.Project.OrganizationId, 20m)); // tenant A's own spend
                s.Db.ExternalGenerationJobs.Add(Job(Guid.NewGuid(), Guid.NewGuid(), bSpend));      // tenant B, another org
                s.Db.SaveChanges();
                return (await InvokeAsync(s, Config(project: 1000m, global: 100m))).GetProperty("videoBudget").GetRawText();
            }
        }

        string first = await TenantAView((decimal)tenantBSpend);
        string second = await TenantAView((decimal)otherTenantBSpend);

        second.Should().Be(first, "tenant A's output must not move when only tenant B's spend changes");
    }

    [Fact]
    public void The_snapshot_reads_the_same_caps_the_engine_binds_from_the_same_configuration()
    {
        IConfiguration config = Config(project: 321.5m, global: 654m, gpu: 7m, gpuGlobal: 9m);
        VideoGenerationOptions engine = new();
        config.GetSection(VideoGenerationOptions.SectionName).Bind(engine);

        VideoBudgetSnapshot.ReadCaps(config, gpu: false).Should().Be((engine.DailyBudgetCredits, engine.GlobalDailyBudgetCredits));
        VideoBudgetSnapshot.ReadCaps(config, gpu: true).Should().Be((engine.DailyGpuMinutes * 60m, engine.GlobalDailyGpuMinutes * 60m));
        VideoBudgetSnapshot.ReadCaps(null, gpu: false).Should().Be(
            (new VideoGenerationOptions().DailyBudgetCredits, new VideoGenerationOptions().GlobalDailyBudgetCredits));
    }

    /// <summary>QA-P0-17: the snapshot's charge is SQL on Postgres (no server needed to translate), so its sums never pull rows into memory.</summary>
    [Fact]
    public void The_charge_expression_translates_to_Postgres_SQL()
    {
        using var db = new InferenceApiDbContext(
            new DbContextOptionsBuilder<InferenceApiDbContext>().UseNpgsql("Host=localhost;Database=none").Options);

        string sql = ReelBolt.Shared.Metering.VideoBudgetAccounting
            .TodayJobs(db.ExternalGenerationJobs.AsNoTracking(), ExternalGenerationJob.CreditsUnit, DateTime.UtcNow.Date)
            .Select(ReelBolt.Shared.Metering.VideoBudgetAccounting.ChargeOf)
            .ToQueryString();

        sql.Should().Contain("CASE").And.Contain("COALESCE", "the charge is computed by the database, row by row, for SUM to aggregate");
    }

    [Fact]
    public async Task The_async_snapshot_equals_the_one_the_tool_returns()
    {
        Scenario s = Seed();
        using (s.Db)
        {
            s.Db.ExternalGenerationJobs.Add(Job(Guid.NewGuid(), s.Project.OrganizationId, 45m));
            s.Db.ExternalGenerationJobs.Add(Job(s.Project.Id, s.Project.OrganizationId, 3m));
            s.Db.SaveChanges();
            IConfiguration config = Config(project: 50m, global: 1000m);

            IReadOnlyDictionary<string, object?> viaAsync = await VideoBudgetSnapshot.ForRunAsync(
                s.Db, config, s.Project, s.Run.Id, Owner, CancellationToken.None);
            JsonElement viaTool = (await InvokeAsync(s, config)).GetProperty("videoBudget");

            viaAsync["limitedBy"].Should().Be("project");
            viaAsync["remainingTodayCredits"].Should().Be(47m);
            viaTool.GetProperty("remainingTodayCredits").GetDecimal().Should().Be(47m);
        }
    }

    [Fact]
    public async Task No_plan_cap_binds_the_organization_so_sibling_spend_never_limits_a_run()
    {
        Scenario s = Seed();
        using (s.Db)
        {
            s.Db.ExternalGenerationJobs.Add(Job(Guid.NewGuid(), s.Project.OrganizationId, 900m)); // sibling project, same org
            s.Db.SaveChanges();

            JsonElement budget = (await InvokeAsync(s, Config(project: null, global: 100000m))).GetProperty("videoBudget");

            budget.GetProperty("dailyCapCredits").ValueKind.Should().Be(JsonValueKind.Null);
            budget.GetProperty("limitedBy").ValueKind.Should().Be(JsonValueKind.Null);
        }
    }

    [Fact]
    public async Task Released_yesterdays_and_other_unit_rows_are_not_counted_and_actual_cost_wins_when_higher()
    {
        Scenario s = Seed();
        using (s.Db)
        {
            Guid p = s.Project.Id;
            s.Db.ExternalGenerationJobs.AddRange(
                Job(p, null, 10m, actual: 25m),
                Job(p, null, 40m, status: ExternalGenerationJobStatus.Failed),
                Job(p, null, 40m, status: ExternalGenerationJobStatus.Cancelled),
                Job(p, null, 40m, createdAt: DateTime.UtcNow.Date.AddMinutes(-1)),
                Job(p, null, 40m, unit: "gpuseconds"));
            s.Db.SaveChanges();

            JsonElement budget = (await InvokeAsync(s, Config(project: 100m, global: 1000m))).GetProperty("videoBudget");

            budget.GetProperty("usedTodayCredits").GetDecimal().Should().Be(25m);
        }
    }

    [Fact]
    public async Task A_remainder_never_goes_below_zero()
    {
        Scenario s = Seed();
        using (s.Db)
        {
            s.Db.ExternalGenerationJobs.Add(Job(s.Project.Id, null, 150m));
            s.Db.SaveChanges();

            JsonElement budget = (await InvokeAsync(s, Config(project: 100m, global: 1000m))).GetProperty("videoBudget");

            budget.GetProperty("remainingTodayCredits").GetDecimal().Should().Be(0m);
        }
    }

    [Fact]
    public async Task The_GPU_platform_cap_also_hides_global_figures()
    {
        Scenario s = Seed();
        using (s.Db)
        {
            s.Db.ExternalGenerationJobs.Add(Job(Guid.NewGuid(), null, 175m, unit: "gpuseconds"));
            s.Db.ExternalGenerationJobs.Add(Job(s.Project.Id, null, 10m, unit: "gpuseconds", executionId: s.Run.Id));
            s.Db.SaveChanges();

            JsonElement budget = (await InvokeAsync(s, Config(gpu: 100m, gpuGlobal: 3m))).GetProperty("videoBudget");

            budget.GetProperty("limitedBy").GetString().Should().Be("platform");
            budget.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(["unit", "limitedBy", "resetsAt"],
                "S-01: the GPU variant carries no figure either when the platform binds");
            budget.GetProperty("unit").GetString().Should().Be("gpuMinutes");
        }
    }

    [Fact]
    public async Task A_ComfyUI_run_reports_the_GPU_minutes_variant()
    {
        Scenario s = Seed();
        using (s.Db)
        {
            s.Db.ExternalGenerationJobs.Add(Job(s.Project.Id, null, 90m, unit: "gpuseconds", executionId: s.Run.Id));
            s.Db.SaveChanges();

            JsonElement budget = (await InvokeAsync(s, Config(gpu: 2m, gpuGlobal: 100m))).GetProperty("videoBudget");

            budget.GetProperty("unit").GetString().Should().Be("gpuMinutes");
            budget.GetProperty("dailyCapGpuMinutes").GetDecimal().Should().Be(2m);
            budget.GetProperty("usedTodayGpuMinutes").GetDecimal().Should().Be(1.5m);
            budget.GetProperty("remainingTodayGpuMinutes").GetDecimal().Should().Be(0.5m);
            budget.TryGetProperty("dailyCapCredits", out _).Should().BeFalse();
        }
    }

    [Fact]
    public async Task Used_today_matches_what_the_budget_gate_counts_for_the_same_ledger()
    {
        Scenario s = Seed();
        using (s.Db)
        {
            Guid p = s.Project.Id;
            ExternalGenerationJob[] rows =
            [
                Job(p, null, 10m, actual: 25m),
                Job(p, null, 7m),
                Job(p, null, 40m, status: ExternalGenerationJobStatus.Failed),
                Job(p, null, 40m, createdAt: DateTime.UtcNow.Date.AddMinutes(-1)),
                Job(p, null, 40m, unit: "gpuseconds"),
            ];
            s.Db.ExternalGenerationJobs.AddRange(rows);
            s.Db.SaveChanges();

            var factory = new SharedInMemoryDbFactory();
            await using (var engineDb = factory.CreateDbContext())
            {
                engineDb.ExternalGenerationJobs.AddRange(rows.Select(r => Job(
                    r.ProjectId, r.OrganizationId, r.CostEstimate, r.ActualCost, r.Status, r.CostUnit, r.CreatedAt)));
                await engineDb.SaveChangesAsync();
            }

            var gate = new VideoGenerationBudgetGate(
                factory, Options.Create(new VideoGenerationOptions { DailyBudgetCredits = 1000m, GlobalDailyBudgetCredits = 1000m }));
            var reservation = await gate.ReserveAsync(
                new VideoGenerationReservationRequest(
                    p, Guid.NewGuid(), Guid.NewGuid(), ProviderId, 100m,
                    [new VideoGenerationTakeRequest("v1", "h", 1m)]),
                CancellationToken.None);

            JsonElement budget = (await InvokeAsync(s, Config(project: 1000m, global: 1000m))).GetProperty("videoBudget");

            budget.GetProperty("usedTodayCredits").GetDecimal().Should().Be(reservation.ProjectSpentToday);
        }
    }
}
