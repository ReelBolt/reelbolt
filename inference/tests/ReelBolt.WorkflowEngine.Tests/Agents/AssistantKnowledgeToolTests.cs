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
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.PlatformDocs;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Agents;

/// <summary>
/// The knowledge and read-back tools: documentation search, the template catalog, a workflow's full
/// steps and its run history. All read-only, and every project-scoped one re-checks ownership —
/// another user's workflow or run reads exactly like one that does not exist.
/// </summary>
public class AssistantKnowledgeToolTests
{
    private static readonly Guid Owner = Guid.NewGuid();
    private static readonly Guid Stranger = Guid.NewGuid();

    private static InferenceApiDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static async Task<JsonElement> InvokeAsync(
        AssistantToolProvider provider,
        AssistantToolContext context,
        string toolName,
        params (string Key, object? Value)[] arguments)
    {
        AIFunction tool = provider.GetTools(context).Single(t => t.Name == toolName);
        var args = new AIFunctionArguments();
        foreach ((string key, object? value) in arguments)
            args[key] = value;

        object? raw = await tool.InvokeAsync(args);
        string text = raw is JsonElement { ValueKind: JsonValueKind.String } s ? s.GetString()! : raw!.ToString()!;
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static (Project Project, WorkflowDefinition Workflow, AgentDefinition Agent) Seed(InferenceApiDbContext db)
    {
        var agent = new AgentDefinition { Id = Guid.NewGuid(), Name = "Video Analyzer", AgentType = AgentType.VideoTransform, IsBuiltIn = true };
        var project = new Project { Id = Guid.NewGuid(), Name = "Launch", OwnerId = Owner, OrganizationId = TestTenancy.OrgOf(Owner) };
        var workflow = new WorkflowDefinition { Id = Guid.NewGuid(), Name = "Phone tracking", ProjectId = project.Id };
        workflow.Steps.Add(new WorkflowStep
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = workflow.Id,
            AgentDefinitionId = agent.Id,
            StepOrder = 1,
            Label = "Analyze",
            StepType = StepType.VideoAnalyze,
            VideoAnalyzeConfigJson = "{\"detectInsertRegions\":true}",
            CacheMode = StepCacheMode.Never
        });
        db.AgentDefinitions.Add(agent);
        db.Projects.Add(project);
        db.WorkflowDefinitions.Add(workflow);
        db.SaveChanges();
        return (project, workflow, agent);
    }

    [Fact]
    public async Task GetWorkflow_returns_complete_steps_in_the_proposable_shape()
    {
        using InferenceApiDbContext db = CreateDb();
        (Project project, WorkflowDefinition workflow, AgentDefinition agent) = Seed(db);
        AssistantToolProvider provider = AssistantToolTestFactory.CreateProvider(db, new FakeCurrentUser(Owner));

        JsonElement result = await InvokeAsync(provider, new AssistantToolContext(Owner, project.Id, workflow.Id), "GetWorkflow");

        result.GetProperty("name").GetString().Should().Be("Phone tracking");
        JsonElement step = result.GetProperty("steps")[0];
        step.GetProperty("videoAnalyzeConfigJson").GetString().Should().Be("{\"detectInsertRegions\":true}");
        step.GetProperty("stepType").GetString().Should().Be("VideoAnalyze");
        step.GetProperty("cacheMode").GetString().Should().Be("Never");
        result.GetProperty("agents")[0].GetProperty("agentName").GetString().Should().Be("Video Analyzer");

        // The shape round-trips into what ProposeWorkflow takes.
        var roundTrip = step.Deserialize<CreateWorkflowStepRequest>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
        roundTrip!.AgentDefinitionId.Should().Be(agent.Id);
        roundTrip.StepOrder.Should().Be(1);
    }

    [Fact]
    public async Task GetWorkflow_of_someone_elses_project_reads_as_not_found()
    {
        using InferenceApiDbContext db = CreateDb();
        (Project project, WorkflowDefinition workflow, _) = Seed(db);
        AssistantToolProvider provider = AssistantToolTestFactory.CreateProvider(db, new FakeCurrentUser(Stranger));

        JsonElement result = await InvokeAsync(
            provider, new AssistantToolContext(Stranger, project.Id), "GetWorkflow", ("workflowId", workflow.Id));

        result.GetProperty("error").GetString().Should().Be("no project in context");
    }

    [Fact]
    public async Task Execution_tools_report_status_steps_and_truncated_output()
    {
        using InferenceApiDbContext db = CreateDb();
        (Project project, WorkflowDefinition workflow, _) = Seed(db);
        WorkflowStep step = workflow.Steps.Single();
        var execution = new WorkflowExecution
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            WorkflowDefinitionId = workflow.Id,
            Status = ExecutionStatus.Failed,
            StartedAt = DateTime.UtcNow,
            ErrorMessage = "Step 1 failed"
        };
        execution.StepResults.Add(new WorkflowStepResult
        {
            Id = Guid.NewGuid(),
            WorkflowExecutionId = execution.Id,
            WorkflowStepId = step.Id,
            Status = StepStatus.Failed,
            ErrorDetails = "no plate found",
            OutputJson = new string('x', AssistantWorkflowTools.MaxStepOutputChars + 500)
        });
        db.WorkflowExecutions.Add(execution);
        db.SaveChanges();

        AssistantToolProvider provider = AssistantToolTestFactory.CreateProvider(db, new FakeCurrentUser(Owner));
        var context = new AssistantToolContext(Owner, project.Id);

        JsonElement list = await InvokeAsync(provider, context, "ListWorkflowExecutions");
        list.GetArrayLength().Should().Be(1);
        list[0].GetProperty("status").GetString().Should().Be("Failed");
        list[0].GetProperty("workflowName").GetString().Should().Be("Phone tracking");

        JsonElement detail = await InvokeAsync(provider, context, "GetWorkflowExecution", ("executionId", execution.Id));
        JsonElement stepResult = detail.GetProperty("steps")[0];
        stepResult.GetProperty("label").GetString().Should().Be("Analyze");
        stepResult.GetProperty("errorDetails").GetString().Should().Be("no plate found");
        stepResult.GetProperty("outputPreview").GetString().Should().Contain("[truncated, 500 more chars]");

        AssistantToolProvider strangerProvider = AssistantToolTestFactory.CreateProvider(db, new FakeCurrentUser(Stranger));
        JsonElement hidden = await InvokeAsync(
            strangerProvider, new AssistantToolContext(Stranger, project.Id), "GetWorkflowExecution", ("executionId", execution.Id));
        hidden.TryGetProperty("steps", out _).Should().BeFalse("another user's run must not be readable");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Templates_are_listed_and_returned_with_built_in_agent_ids_resolved(bool avatarsEnabled)
    {
        using InferenceApiDbContext db = CreateDb();
        WorkflowTemplateDefinition template = WorkflowTemplateCatalog.GetByKey("edit-my-video")!;
        var ids = new Dictionary<AgentType, Guid>();
        foreach (AgentType type in template.Steps.Select(s => s.AgentType).Distinct())
        {
            ids[type] = Guid.NewGuid();
            db.AgentDefinitions.Add(new AgentDefinition { Id = ids[type], Name = type.ToString(), AgentType = type, IsBuiltIn = true });
        }
        db.SaveChanges();

        AssistantToolProvider provider = AssistantToolTestFactory.CreateProvider(db, new FakeCurrentUser(Owner), avatarsEnabled: avatarsEnabled);
        var noProject = new AssistantToolContext(Owner);

        JsonElement list = await InvokeAsync(provider, noProject, "ListWorkflowTemplates");
        string?[] listed = [.. list.EnumerateArray().Select(t => t.GetProperty("key").GetString())];
        listed.Should().Contain(WorkflowTemplateCatalog.GetListed().Where(t => avatarsEnabled || !WorkflowTemplateCatalog.IsAvatarTemplate(t.Key)).Select(t => t.Key));
        if (avatarsEnabled)
            listed.Should().Contain(WorkflowTemplateCatalog.AvatarClipTemplateKey).And.NotContain([WorkflowTemplateCatalog.SpokespersonClipTemplateKey, WorkflowTemplateCatalog.TalkingAvatarBudgetTemplateKey]);
        else
            listed.Should().NotContain([WorkflowTemplateCatalog.SpokespersonClipTemplateKey, WorkflowTemplateCatalog.TalkingAvatarBudgetTemplateKey, WorkflowTemplateCatalog.AvatarClipTemplateKey]);

        JsonElement detail = await InvokeAsync(provider, noProject, "GetWorkflowTemplate", ("templateKey", "edit-my-video"));
        JsonElement steps = detail.GetProperty("steps");
        steps.GetArrayLength().Should().Be(template.Steps.Count);
        for (int i = 0; i < template.Steps.Count; i++)
        {
            steps[i].GetProperty("agentDefinitionId").GetGuid().Should().Be(ids[template.Steps[i].AgentType]);
            steps[i].GetProperty("stepOrder").GetInt32().Should().Be(i + 1);
            steps[i].GetProperty("stepType").GetString().Should().Be(template.Steps[i].StepType.ToString());
        }

        JsonElement unknown = await InvokeAsync(provider, noProject, "GetWorkflowTemplate", ("templateKey", "nope"));
        unknown.GetProperty("error").GetString().Should().Be("template not found");
    }

    [Fact]
    public async Task SearchPlatformDocs_marks_developer_sections_internal_with_no_url_and_a_do_not_repeat_note()
    {
        using InferenceApiDbContext db = CreateDb();
        var user = new PlatformDocsHit("Tracked screen inserts", "What fills the screen", "/docs/recipes/tracked-screen-insert",
            PlatformDocsAudience.User, "Pick a clip and place it on the phone.", 0.81f);
        // Even a stale index entry that still carries a link must come out without one.
        var developer = new PlatformDocsHit("Video editing", "Compile internals", "/docs/developer/video-editing",
            PlatformDocsAudience.Developer, "argv is built from a typed cut list.", 0.74f);
        var search = new FakePlatformDocsSearch(new PlatformDocsSearchResult(PlatformDocsSearchStatus.Ok, [user, developer]));
        AssistantToolProvider provider = AssistantToolTestFactory.CreateProvider(db, new FakeCurrentUser(Owner), docsSearch: search);

        JsonElement result = await InvokeAsync(provider, new AssistantToolContext(Owner), "SearchPlatformDocs", ("query", "how is it cut"));

        JsonElement userResult = result.GetProperty("results")[0];
        userResult.GetProperty("audience").GetString().Should().Be("User");
        userResult.GetProperty("url").GetString().Should().Be("/docs/recipes/tracked-screen-insert");
        userResult.TryGetProperty("use", out _).Should().BeFalse();

        JsonElement internalResult = result.GetProperty("results")[1];
        internalResult.GetProperty("audience").GetString().Should().Be("Internal");
        internalResult.TryGetProperty("url", out _).Should().BeFalse("internal background is never linkable");
        internalResult.GetProperty("use").GetString().Should().Be(AssistantKnowledgeTools.InternalUseNote)
            .And.Contain("never quote, paraphrase or link")
            .And.Contain("no technical details");
        internalResult.GetProperty("content").GetString().Should().Contain("typed cut list");
        result.GetRawText().Should().NotContain("/docs/developer").And.NotContain("Developer");
    }

    [Fact]
    public void SearchPlatformDocs_description_does_not_advertise_a_developer_reference()
    {
        using InferenceApiDbContext db = CreateDb();
        AssistantToolProvider provider = AssistantToolTestFactory.CreateProvider(db, new FakeCurrentUser(Owner));

        string description = provider.GetTools(new AssistantToolContext(Owner))
            .Single(t => t.Name == "SearchPlatformDocs").Description;

        description.Should().NotContain("developer reference");
        description.Should().Contain("Internal").And.Contain("is background only and is not quoted, paraphrased, summarised, linked");
    }

    [Fact]
    public async Task SearchPlatformDocs_relays_hits_and_reports_unavailability_plainly()
    {
        using InferenceApiDbContext db = CreateDb();
        var hit = new PlatformDocsHit("Tracked screen inserts", "What fills the screen", "/docs/recipes/tracked-screen-insert",
            PlatformDocsAudience.User, "The motion graphics planner renders the clip.", 0.81f);
        var search = new FakePlatformDocsSearch(new PlatformDocsSearchResult(PlatformDocsSearchStatus.Ok, [hit]));
        AssistantToolProvider provider = AssistantToolTestFactory.CreateProvider(db, new FakeCurrentUser(Owner), docsSearch: search);

        JsonElement result = await InvokeAsync(provider, new AssistantToolContext(Owner), "SearchPlatformDocs",
            ("query", "put a clip on a phone screen"), ("limit", 50));

        result.GetProperty("available").GetBoolean().Should().BeTrue();
        result.GetProperty("results")[0].GetProperty("url").GetString().Should().Be("/docs/recipes/tracked-screen-insert");
        search.Queries.Single().Limit.Should().Be(AssistantKnowledgeTools.MaxSearchLimit, "the limit is clamped");

        var down = new FakePlatformDocsSearch(new PlatformDocsSearchResult(PlatformDocsSearchStatus.Unavailable, [], "no embedding provider"));
        AssistantToolProvider downProvider = AssistantToolTestFactory.CreateProvider(db, new FakeCurrentUser(Owner), docsSearch: down);
        JsonElement unavailable = await InvokeAsync(downProvider, new AssistantToolContext(Owner), "SearchPlatformDocs", ("query", "x"));
        unavailable.GetProperty("available").GetBoolean().Should().BeFalse();
        unavailable.GetProperty("reason").GetString().Should().Be("no embedding provider");
    }
}
