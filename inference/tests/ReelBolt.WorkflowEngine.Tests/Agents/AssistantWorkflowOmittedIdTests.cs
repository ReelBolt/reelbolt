using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Tests.Controllers;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Agents;

/// <summary>
/// A model creating a workflow leaves <c>workflowId</c> (and may leave <c>name</c>) out, as the tool's own description tells
/// it to. The call must reach the tool body, not die in argument binding. Arguments are passed as the JSON the model sends.
/// </summary>
public class AssistantWorkflowOmittedIdTests
{
    internal static AIFunctionArguments FromJson(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        var arguments = new AIFunctionArguments();
        foreach (JsonProperty p in doc.RootElement.EnumerateObject())
            arguments[p.Name] = p.Value.Clone();
        return arguments;
    }

    private static async Task<JsonElement> InvokeAsync(AssistantToolProvider provider, AssistantToolContext context, string tool, string json)
    {
        AIFunction function = provider.GetTools(context).Single(t => t.Name == tool);
        object? raw = await function.InvokeAsync(FromJson(json));
        string text = raw switch
        {
            JsonElement { ValueKind: JsonValueKind.String } e => e.GetString()!,
            JsonElement e => e.GetRawText(),
            _ => raw?.ToString() ?? string.Empty
        };
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static string Esc(string json) => JsonSerializer.Serialize(json);

    private static async Task CreateAsync(string stepsJson, int expectedSteps)
    {
        var (connection, db) = WorkflowAssistantControllerTests.CreateSqliteContext();
        using (connection)
        using (db)
        {
            Guid owner = Guid.NewGuid();
            var project = new Project { Id = Guid.NewGuid(), Name = "Launch", OwnerId = owner, OrganizationId = TestTenancy.OrgOf(owner), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            var agent = new AgentDefinition { Id = Guid.NewGuid(), Name = "A", Description = "d", SystemPrompt = "p", AgentType = AgentType.ShotDirector, IsBuiltIn = true, CreatedAt = DateTime.UtcNow };
            db.Projects.Add(project);
            db.AgentDefinitions.Add(agent);
            await db.SaveChangesAsync();

            AssistantToolProvider provider = AssistantToolTestFactory.CreateProvider(db, new FakeCurrentUser(owner));
            var context = new AssistantToolContext(owner, project.Id);
            string steps = stepsJson.Replace("AGENT", agent.Id.ToString());
            const string name = "\"name\":\"Teaser\",";

            JsonElement proposal = await InvokeAsync(provider, context, "ProposeWorkflow", $"{{{name}\"steps\":{steps}}}");
            proposal.TryGetProperty("error", out JsonElement err).Should().BeFalse(err.ToString());
            string token = proposal.GetProperty("confirmationToken").GetString()!;

            JsonElement applied = await InvokeAsync(provider, context, "ApplyWorkflow", $"{{{name}\"steps\":{steps},\"confirmationToken\":\"{token}\"}}");
            applied.TryGetProperty("error", out err).Should().BeFalse(err.ToString());
            Guid id = applied.GetProperty("workflowId").GetGuid();
            (await db.WorkflowSteps.CountAsync(s => s.WorkflowDefinitionId == id)).Should().Be(expectedSteps);
        }
    }

    private const string InlineVideo =
        """[{"agentDefinitionId":"AGENT","stepOrder":1,"label":"Clip","stepType":"VideoGenerate","videoGenerateConfigJson":"{\"planSource\":\"Inline\",\"inlinePrompt\":\"a red car\",\"model\":\"MiniMax-H3\",\"ratio\":\"16:9\",\"durationSeconds\":6,\"takes\":1,\"maxClips\":1,\"maxSpendCredits\":8}"}]""";

    private const string PlanVideo =
        """[{"agentDefinitionId":"AGENT","stepOrder":1,"label":"Plan","stepType":"Agent"},{"agentDefinitionId":"AGENT","stepOrder":2,"label":"Clips","stepType":"VideoGenerate","videoGenerateConfigJson":"{\"planSource\":\"StepRef\",\"plan\":{\"from\":\"Step\",\"stepOrder\":1},\"model\":\"MiniMax-H3\",\"ratio\":\"16:9\",\"durationSeconds\":6,\"takes\":1,\"maxClips\":3,\"maxSpendCredits\":8}"}]""";

    [Fact]
    public Task Creating_an_inline_VideoGenerate_workflow_with_workflowId_omitted_works() => CreateAsync(InlineVideo, 1);

    [Fact]
    public Task Creating_a_plan_mode_VideoGenerate_workflow_with_workflowId_omitted_works() => CreateAsync(PlanVideo, 2);

    [Fact]
    public async Task Creating_with_the_name_omitted_answers_a_tool_error_naming_it_instead_of_failing_in_binding()
    {
        var (connection, db) = WorkflowAssistantControllerTests.CreateSqliteContext();
        using (connection)
        using (db)
        {
            Guid owner = Guid.NewGuid();
            var project = new Project { Id = Guid.NewGuid(), Name = "Launch", OwnerId = owner, OrganizationId = TestTenancy.OrgOf(owner), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            var agent = new AgentDefinition { Id = Guid.NewGuid(), Name = "A", Description = "d", SystemPrompt = "p", AgentType = AgentType.ShotDirector, IsBuiltIn = true, CreatedAt = DateTime.UtcNow };
            db.Projects.Add(project);
            db.AgentDefinitions.Add(agent);
            await db.SaveChangesAsync();
            AssistantToolProvider provider = AssistantToolTestFactory.CreateProvider(db, new FakeCurrentUser(owner));

            var context = new AssistantToolContext(owner, project.Id);
            string steps = InlineVideo.Replace("AGENT", agent.Id.ToString());
            JsonElement proposal = await InvokeAsync(provider, context, "ProposeWorkflow", $"{{\"steps\":{steps}}}");
            string token = proposal.GetProperty("confirmationToken").GetString()!;

            JsonElement applied = await InvokeAsync(provider, context, "ApplyWorkflow", $"{{\"steps\":{steps},\"confirmationToken\":\"{token}\"}}");

            applied.GetProperty("error").GetString().Should().Contain("name");
        }
    }
}
