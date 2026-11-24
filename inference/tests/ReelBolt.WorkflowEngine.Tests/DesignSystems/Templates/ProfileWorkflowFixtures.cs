#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using ReelBolt.Shared.Data.Models;
using Models = ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Templates;

/// <summary>
/// Project copies of the "Build brand and product profile" template as a project holds them: the v1 layout exactly as
/// it was provisioned before the free-import change (a product-only fallback runs on into the style extractor at step 8), and the
/// current version from the catalog.
/// </summary>
internal static class ProfileWorkflowFixtures
{
    public static WorkflowTemplateDefinition Template
        => WorkflowTemplateCatalog.GetByKey(WorkflowTemplateCatalog.ProductProfileFromCodeTemplateKey)!;

    public static string NameOf(int version) => $"{Template.Name} v{version}";

    /// <summary>The v1 copy (9 steps, as provisioned before commit 62db0c47b).</summary>
    public static WorkflowDefinition V1(Guid projectId, Guid agentId)
    {
        WorkflowDefinition workflow = New(projectId, NameOf(1));
        Add(workflow, agentId, 1, Models.StepType.DesignSystem, config: """{"op":"Parse","version":1,"parse":{}}""");
        Add(workflow, agentId, 2, Models.StepType.Conditional, condition: "$.view.fallbackProduct", onTrue: "4", onFalse: "3");
        Add(workflow, agentId, 3, Models.StepType.Conditional, condition: "$.view.fallbackDesign", onTrue: "8", onFalse: "9");
        for (int order = 4; order <= 8; order++) Add(workflow, agentId, order, Models.StepType.Agent);
        Add(workflow, agentId, 9, Models.StepType.DesignSystem,
            config: """{"op":"Compose","version":1,"compose":{"parseStepOrder":1,"designFallbackStepOrders":[8],"productFallbackStepOrders":[4,5,6,7],"ingest":true}}""");
        return workflow;
    }

    /// <summary>The current version, step for step from the catalog.</summary>
    public static WorkflowDefinition Current(Guid projectId, Guid agentId)
    {
        WorkflowDefinition workflow = New(projectId, WorkflowTemplateCatalog.BuildVersionedWorkflowName(Template));
        IReadOnlyList<WorkflowTemplateStepDefinition> steps = Template.Steps;
        for (int i = 0; i < steps.Count; i++)
        {
            Add(workflow, agentId, i + 1, steps[i].StepType, steps[i].DesignSystemConfigJson, steps[i].ConditionExpression,
                steps[i].TrueBranchStepOrder, steps[i].FalseBranchStepOrder, steps[i].ConditionalConfigJson);
        }

        return workflow;
    }

    private static WorkflowDefinition New(Guid projectId, string name) => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = projectId,
        Name = name,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    private static void Add(
        WorkflowDefinition workflow, Guid agentId, int order, Models.StepType type, string? config = null, string? condition = null,
        string? onTrue = null, string? onFalse = null, string? conditional = null)
        => workflow.Steps.Add(new WorkflowStep
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = workflow.Id,
            AgentDefinitionId = agentId,
            StepOrder = order,
            StepType = type,
            DesignSystemConfigJson = config,
            ConditionExpression = condition,
            TrueBranchStepOrder = onTrue,
            FalseBranchStepOrder = onFalse,
            ConditionalConfigJson = conditional
        });

    /// <summary>The step orders a run walks when only the product profile is missing (the design files are complete).</summary>
    public static List<int> ProductOnlyFallbackPath(IEnumerable<WorkflowStep> steps)
    {
        List<WorkflowStep> ordered = [.. steps.OrderBy(s => s.StepOrder)];
        List<int> path = [];
        int i = 0;
        while (i < ordered.Count)
        {
            WorkflowStep step = ordered[i];
            path.Add(step.StepOrder);
            if (step.StepType == Models.StepType.Conditional)
            {
                bool value = step.ConditionExpression == "$.view.fallbackProduct";
                string? target = value ? step.TrueBranchStepOrder : step.FalseBranchStepOrder;
                int next = ordered.FindIndex(s => s.StepOrder.ToString() == target);
                i = next < 0 ? i + 1 : next;
                continue;
            }

            i++;
        }

        return path;
    }
}
