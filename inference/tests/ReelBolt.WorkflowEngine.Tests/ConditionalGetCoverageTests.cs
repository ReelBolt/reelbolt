using System;
using System.Reflection;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Filters;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>The read-only JSON GETs the run, app, project and dashboard pages revalidate all carry [ConditionalGet].</summary>
public sealed class ConditionalGetCoverageTests
{
    [Theory]
    [InlineData(typeof(ProjectsController), "List")]
    [InlineData(typeof(ProjectsController), "Get")]
    [InlineData(typeof(ProjectFilesController), "List")]
    [InlineData(typeof(WorkflowsController), "List")]
    [InlineData(typeof(WorkflowsController), "Get")]
    [InlineData(typeof(WorkflowsController), "ListExecutions")]
    [InlineData(typeof(WorkflowAppsController), "List")]
    [InlineData(typeof(WorkflowAppsController), "Get")]
    [InlineData(typeof(ProjectVoicesController), "List")]
    [InlineData(typeof(StylesController), "List")]
    [InlineData(typeof(StorageUsageController), "Usage")]
    [InlineData(typeof(SkillsController), "List")]
    [InlineData(typeof(OrgAgentModelsController), "List")]
    [InlineData(typeof(OrgAgentOverridesController), "List")]
    [InlineData(typeof(InferenceProvidersController), "Selectable")]
    [InlineData(typeof(InferenceProvidersController), "Status")]
    public void ReadAction_IsConditionalGet(Type controller, string action)
    {
        MethodInfo method = controller.GetMethod(action)!;
        method.GetCustomAttribute<ConditionalGetAttribute>().Should().NotBeNull();
    }
}
