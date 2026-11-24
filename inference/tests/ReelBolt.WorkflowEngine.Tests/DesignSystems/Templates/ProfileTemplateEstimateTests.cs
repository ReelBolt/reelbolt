using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Workflows;
using Xunit;
using StepTypeEnum = ReelBolt.Shared.Data.Models.StepType;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Templates;

/// <summary>
/// "estimate before it runs": the pre-run estimate of the "Build brand and product profile"
/// template is a range from the free path (no fallback: Parse, the Conditionals and Compose hold and
/// cost nothing) to every fallback step, and the 402 preflight requires the holds of every fallback
/// step, because a run cannot know before Parse which branch it will take.
/// </summary>
public sealed class ProfileTemplateEstimateTests
{
    [Fact]
    public void The_low_bound_is_the_free_path_and_the_high_bound_is_every_fallback_step()
    {
        WorkflowStep[] steps = TemplateSteps();
        WorkflowStep[] fallbackOnly = [.. steps.Where(s => s.StepType == StepTypeEnum.Agent)];
        fallbackOnly.Should().HaveCount(5, "four product analyzers and the style extractor");

        WorkflowCostEstimate estimate = WorkflowCostEstimator.EstimateFrom([], steps);
        WorkflowCostEstimate allFallback = WorkflowCostEstimator.EstimateFrom([], fallbackOnly);

        allFallback.LowMillis.Should().BePositive("the precondition: running every fallback step costs something");
        estimate.LowMillis.Should().Be(0, "with no fallback only free steps run");
        estimate.HighMillis.Should().Be(allFallback.HighMillis, "the top of the range is every fallback step");
        estimate.TypicalMillis.Should().BeInRange(estimate.LowMillis, estimate.HighMillis);

        estimate.Steps.Where(s => s.StepType is nameof(StepTypeEnum.DesignSystem) or nameof(StepTypeEnum.Conditional))
            .Should().OnlyContain(s => s.HoldMillis == 0 && s.LowMillis == 0 && s.HighMillis == 0 && s.Basis == nameof(CreditEstimateBasis.NotCharged));
    }

    [Fact]
    public void A_workflow_without_branches_keeps_its_low_bound()
    {
        WorkflowStep[] linear = [.. TemplateSteps().Where(s => s.StepType == StepTypeEnum.Agent)];

        WorkflowCostEstimate estimate = WorkflowCostEstimator.EstimateFrom([], linear);

        estimate.LowMillis.Should().BePositive("every step of a workflow without a Conditional always runs");
    }

    [Fact]
    public async Task The_402_preflight_requires_the_holds_of_every_fallback_step()
    {
        using var db = new InferenceApiDbContext(new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        Guid org = Guid.NewGuid();
        WorkflowStep[] steps = TemplateSteps();
        WorkflowCostEstimator estimator = new(
            new SingleContextScopes(db),
            Options.Create(new ReelBolt.Shared.Billing.BillingOptions { Enabled = true }),
            Options.Create(new StepCreditEstimateOptions()),
            NullLogger<WorkflowCostEstimator>.Instance);

        WorkflowCostEstimate estimate = await estimator.EstimateAsync(org, steps, CancellationToken.None);
        WorkflowCostEstimate allFallback = await estimator.EstimateAsync(
            org, [.. steps.Where(s => s.StepType == StepTypeEnum.Agent)], CancellationToken.None);

        estimate.LowMillis.Should().Be(0);
        estimate.RequiredMillis.Should().Be(allFallback.RequiredMillis).And.BePositive();
        estimate.RequiredMillis.Should().Be(estimate.Steps.Where(s => s.StepType == nameof(StepTypeEnum.Agent)).Sum(s => s.HoldMillis));

        CreditAccount account = new() { Id = Guid.NewGuid(), OrganizationId = org, BalanceMillis = estimate.RequiredMillis - 1 };
        db.CreditAccounts.Add(account);
        await db.SaveChangesAsync();

        WorkflowPreflightResult refused = await estimator.PreflightAsync(org, steps, CancellationToken.None);
        refused.Refused.Should().BeTrue("a balance one millicredit short of every fallback hold is a 402");
        refused.Estimate.RequiredMillis.Should().Be(estimate.RequiredMillis);

        account.BalanceMillis = estimate.RequiredMillis;
        await db.SaveChangesAsync();
        (await estimator.PreflightAsync(org, steps, CancellationToken.None)).Refused.Should().BeFalse();
    }

    private static WorkflowStep[] TemplateSteps()
    {
        WorkflowTemplateDefinition template = WorkflowTemplateCatalog.GetByKey(WorkflowTemplateCatalog.ProductProfileFromCodeTemplateKey)!;
        Guid workflowId = Guid.NewGuid();
        return [.. template.Steps.Select((t, i) =>
        {
            AgentDefinition agent = new() { Id = Guid.NewGuid(), Name = t.AgentType.ToString(), AgentType = t.AgentType };
            return new WorkflowStep
            {
                Id = Guid.NewGuid(),
                WorkflowDefinitionId = workflowId,
                StepOrder = i + 1,
                StepType = t.StepType,
                Label = t.Label,
                AgentDefinitionId = agent.Id,
                AgentDefinition = agent,
                ConditionExpression = t.ConditionExpression,
                TrueBranchStepOrder = t.TrueBranchStepOrder,
                FalseBranchStepOrder = t.FalseBranchStepOrder,
                ConditionalConfigJson = t.ConditionalConfigJson,
                DesignSystemConfigJson = t.DesignSystemConfigJson,
                AgentInputContextMode = t.AgentInputContextMode ?? AgentInputContextMode.PreviousStepOnly,
                MaxIterations = t.MaxIterations
            };
        })];
    }

    private sealed class SingleContextScopes(InferenceApiDbContext db) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new FixedScope(db);

        private sealed class FixedScope(InferenceApiDbContext db) : IServiceScope
        {
            public IServiceProvider ServiceProvider { get; } = new FixedProvider(db);
            public void Dispose() { }
        }

        private sealed class FixedProvider(InferenceApiDbContext db) : IServiceProvider
        {
            public object? GetService(Type serviceType) => serviceType == typeof(InferenceApiDbContext) ? db : null;
        }
    }
}
