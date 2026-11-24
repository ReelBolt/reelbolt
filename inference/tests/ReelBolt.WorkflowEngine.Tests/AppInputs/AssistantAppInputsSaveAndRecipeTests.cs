using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ReelBolt.Inference.Api.Agents;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.Apps;
using ReelBolt.WorkflowEngine.Tests.Agents;
using ReelBolt.WorkflowEngine.Tests.Controllers;
using Xunit;
using static ReelBolt.WorkflowEngine.Tests.AppInputs.AppInputsTestKit;

namespace ReelBolt.WorkflowEngine.Tests.AppInputs;

/// <summary>
/// Lane L8 (docs/design/app-inputs.md 13.2, 13.3, 22): the workflow save with an app page, the recipe edition the assistant
/// reads, GetWorkflow's app inputs, and the prompt section, each with the switch on and off.
/// </summary>
public class AssistantAppInputsSaveAndRecipeTests
{
    private const string Generate =
        """{"planSource":"Inline","inlinePrompt":"{{input.videoPrompt}}","durationSeconds":6,"ratio":"16:9","takes":1,"maxClips":2,"maxSpendCredits":5}""";

    private static IConfiguration Switch(bool on) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["Avatars:Enabled"] = "true" })
        .Build();

    private static async Task<JsonElement> InvokeAsync(AssistantToolProvider provider, AssistantToolContext context, string tool, params (string, object?)[] args)
    {
        AIFunction function = provider.GetTools(context).Single(t => t.Name == tool);
        AIFunctionArguments arguments = new();
        foreach ((string key, object? value) in args)
            arguments[key] = value;
        object? raw = await function.InvokeAsync(arguments);
        string text = raw switch
        {
            JsonElement { ValueKind: JsonValueKind.String } e => e.GetString()!,
            JsonElement e => e.GetRawText(),
            _ => JsonSerializer.Serialize(raw)
        };
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<(Guid Owner, Project Project, AgentDefinition Agent)> SeedAsync(InferenceApiDbContext db)
    {
        Guid owner = Guid.NewGuid();
        Project project = new() { Id = Guid.NewGuid(), Name = "Café", OwnerId = owner, OrganizationId = TestTenancy.OrgOf(owner), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        AgentDefinition agent = new() { Id = Guid.NewGuid(), Name = "VideoTransform", Description = "d", SystemPrompt = "p", AgentType = AgentType.VideoTransform, IsBuiltIn = true, CreatedAt = DateTime.UtcNow };
        db.Projects.Add(project);
        db.AgentDefinitions.Add(agent);
        await db.SaveChangesAsync();
        return (owner, project, agent);
    }

    private static readonly JsonElement Page = J(
        """{"version":2,"title":"Latte clips","inputs":[{"kind":"input","id":"videoPrompt","type":"longText","label":"What happens in the video","required":true,"example":"A barista pours latte art"}]}""");

    [Fact]
    public async Task A_new_custom_app_is_saved_with_its_request_page_in_one_proposal()
    {
        (Microsoft.Data.Sqlite.SqliteConnection connection, InferenceApiDbContext db) = WorkflowAssistantControllerTests.CreateSqliteContext();
        using (connection)
        using (db)
        {
            (Guid owner, Project project, AgentDefinition agent) = await SeedAsync(db);
            AssistantToolProvider provider = AssistantToolTestFactory.CreateProvider(db, new FakeCurrentUser(owner), appInputs: new AppInputValueValidator(db));
            AssistantToolContext context = new(owner, project.Id);
            List<CreateWorkflowStepRequest> steps = [new(agent.Id, 1, null, "Make the clip", "VideoGenerate", VideoGenerateConfigJson: Generate)];

            JsonElement proposal = await InvokeAsync(provider, context, "ProposeWorkflow", ("name", "Latte clips"), ("steps", steps), ("appManifest", Page));
            proposal.TryGetProperty("error", out JsonElement error).Should().BeFalse(error.ToString());
            proposal.GetProperty("appChanges").EnumerateArray().Select(l => l.GetString())
                .Should().Contain("Adds the field \"What happens in the video\" (long text, required) to the app page.");
            provider.LastProposedDiff.Should().BeNull("the page's review tray applies steps only, so a page-bearing proposal is applied in the chat");

            // The token binds the page: another page with the same steps is stale.
            JsonElement other = J("""{"version":2,"inputs":[{"kind":"input","id":"videoPrompt","type":"shortText","label":"Idea"}]}""");
            JsonElement stale = await InvokeAsync(provider, context, "ApplyWorkflow",
                ("name", "Latte clips"), ("steps", steps), ("appManifest", other), ("confirmationToken", proposal.GetProperty("confirmationToken").GetString()));
            stale.GetProperty("error").GetString().Should().Contain("stale");

            JsonElement applied = await InvokeAsync(provider, context, "ApplyWorkflow",
                ("name", "Latte clips"), ("steps", steps), ("appManifest", Page), ("confirmationToken", proposal.GetProperty("confirmationToken").GetString()));
            applied.TryGetProperty("error", out JsonElement applyError).Should().BeFalse(applyError.ToString());

            WorkflowDefinition row = await db.WorkflowDefinitions.AsNoTracking().SingleAsync(w => w.Id == applied.GetProperty("workflowId").GetGuid());
            AppManifest stored = AppManifestJson.TryParse(row.AppManifestJson)!;
            stored.Inputs!.Single().Id.Should().Be("videoPrompt");
            stored.Title.Should().Be("Latte clips");
        }
    }

    [Fact]
    public async Task A_step_that_reads_a_field_nobody_declared_is_refused()
    {
        (Microsoft.Data.Sqlite.SqliteConnection connection, InferenceApiDbContext db) = WorkflowAssistantControllerTests.CreateSqliteContext();
        using (connection)
        using (db)
        {
            (Guid owner, Project project, AgentDefinition agent) = await SeedAsync(db);
            AssistantToolContext context = new(owner, project.Id);
            List<CreateWorkflowStepRequest> steps = [new(agent.Id, 1, null, "Make the clip", "VideoGenerate", VideoGenerateConfigJson: Generate)];

            AssistantToolProvider on = AssistantToolTestFactory.CreateProvider(db, new FakeCurrentUser(owner), appInputs: new AppInputValueValidator(db));
            JsonElement undeclared = await InvokeAsync(on, context, "ProposeWorkflow", ("name", "x"), ("steps", steps));
            undeclared.GetProperty("code").GetString().Should().Be(WorkflowAppSupport.InvalidCode);
            (await db.WorkflowDefinitions.CountAsync()).Should().Be(0);
        }
    }

    // ------------------------------------------------------------------ recipes

    private static AssistantKnowledgeTools Knowledge(InferenceApiDbContext db, bool on) =>
        new(db, new FakePlatformDocsSearch(), new WorkflowTemplateProvisioningService(db, NullLogger<WorkflowTemplateProvisioningService>.Instance, Switch(on)));

    [Fact]
    public async Task The_recipe_list_is_the_app_input_edition()
    {
        using InferenceApiDbContext db = new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        AIFunction list = Knowledge(db, true).GetTools(new AssistantToolContext(Guid.NewGuid(), null)).Single(t => t.Name == "ListWorkflowTemplates");

        object? raw = await list.InvokeAsync(new AIFunctionArguments());
        string text = raw is JsonElement { ValueKind: JsonValueKind.String } s ? s.GetString()! : raw!.ToString()!;
        string[] keys = [.. JsonDocument.Parse(text).RootElement.EnumerateArray().Select(r => r.GetProperty("key").GetString()!)];

        keys.Should().Contain(WorkflowTemplateCatalog.AvatarClipTemplateKey)
            .And.NotContain(WorkflowTemplateCatalog.SpokespersonClipTemplateKey)
            .And.NotContain(WorkflowTemplateCatalog.TalkingAvatarBudgetTemplateKey);
    }

    [Fact]
    public void A_hidden_avatar_key_resolves_to_the_merged_recipe()
    {
        IReadOnlyList<WorkflowTemplateDefinition> listed = WorkflowTemplateCatalog.GetListed();
        (WorkflowTemplateDefinition? merged, string asked) = AssistantRecipeTools.ResolveRecipe(listed, WorkflowTemplateCatalog.TalkingAvatarBudgetTemplateKey);
        merged!.Key.Should().Be(WorkflowTemplateCatalog.AvatarClipTemplateKey);
        asked.Should().Be(WorkflowTemplateCatalog.TalkingAvatarBudgetTemplateKey);
    }

    [Fact]
    public async Task GetWorkflowTemplate_shows_the_fields_a_recipe_asks_for()
    {
        using InferenceApiDbContext db = new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        foreach (AgentType type in Enum.GetValues<AgentType>())
            db.AgentDefinitions.Add(new AgentDefinition { Id = Guid.NewGuid(), Name = type.ToString(), Description = "d", SystemPrompt = "p", AgentType = type, IsBuiltIn = true, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        AIFunction get = Knowledge(db, on: true).GetTools(new AssistantToolContext(Guid.NewGuid(), null)).Single(t => t.Name == "GetWorkflowTemplate");

        object? raw = await get.InvokeAsync(new AIFunctionArguments { ["templateKey"] = WorkflowTemplateCatalog.TalkingAvatarBudgetTemplateKey });
        JsonElement template = JsonDocument.Parse(raw is JsonElement { ValueKind: JsonValueKind.String } s ? s.GetString()! : raw!.ToString()!).RootElement;

        template.GetProperty("key").GetString().Should().Be(WorkflowTemplateCatalog.AvatarClipTemplateKey);
        JsonElement lite = template.GetProperty("appManifest").GetProperty("inputs").EnumerateArray().Single(i => i.TryGetProperty("id", out JsonElement id) && id.GetString() == "liteMode");
        lite.GetProperty("default").GetBoolean().Should().BeTrue("the budget key creates the merged app in Lite mode");
    }

    [Fact]
    public async Task An_old_avatar_key_creates_the_merged_app_and_the_brief_inputs_pre_fill_its_fields()
    {
        (Microsoft.Data.Sqlite.SqliteConnection connection, InferenceApiDbContext db) = WorkflowAssistantControllerTests.CreateSqliteContext();
        using (connection)
        using (db)
        {
            (Guid owner, Project project, _) = await SeedAsync(db);
            foreach (AgentType type in Enum.GetValues<AgentType>().Where(t => t != AgentType.VideoTransform))
                db.AgentDefinitions.Add(new AgentDefinition { Id = Guid.NewGuid(), Name = type.ToString(), Description = "d", SystemPrompt = "p", AgentType = type, IsBuiltIn = true, CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
            FakeCurrentUser user = new(owner);
            ReelBolt.Inference.Api.Services.Assistant.IConfirmationTokenService tokens = ReelBolt.Inference.Api.Services.Assistant.ConfirmationTokenService.CreateEphemeral();
            AssistantRecipeTools recipes = new(db, TestTenancy.Access(db, user), new ProjectReadinessFacts(db),
                new WorkflowTemplateProvisioningService(db, NullLogger<WorkflowTemplateProvisioningService>.Instance, Switch(true)),
                new WorkflowEditDiffService(db, tokens), tokens, user, appInputs: new AppInputValueValidator(db));
            AssistantToolContext context = new(owner, project.Id);
            JsonElement brief = J("""{"inputs":{"whatTheyDo":"Waves and says hello"}}""");

            async Task<JsonElement> Call(string tool, params (string, object?)[] args)
            {
                AIFunction function = recipes.GetTools(context).Single(t => t.Name == tool);
                AIFunctionArguments arguments = new();
                foreach ((string key, object? value) in args)
                    arguments[key] = value;
                object? raw = await function.InvokeAsync(arguments);
                return JsonDocument.Parse(raw is JsonElement { ValueKind: JsonValueKind.String } t ? t.GetString()! : raw!.ToString()!).RootElement.Clone();
            }

            JsonElement proposal = await Call("ProposeCreateFromTemplate", ("templateKey", WorkflowTemplateCatalog.TalkingAvatarBudgetTemplateKey), ("brief", brief));
            proposal.TryGetProperty("error", out JsonElement error).Should().BeFalse(error.ToString());
            proposal.GetProperty("recipeKey").GetString().Should().Be(WorkflowTemplateCatalog.AvatarClipTemplateKey);

            JsonElement unknown = await Call("ProposeCreateFromTemplate", ("templateKey", WorkflowTemplateCatalog.AvatarClipTemplateKey), ("brief", J("""{"inputs":{"nope":"x"}}""")));
            unknown.GetProperty("error").GetString().Should().Contain("brief.inputs.nope is not a field");

            JsonElement created = await Call("ApplyCreateFromTemplate", ("templateKey", WorkflowTemplateCatalog.TalkingAvatarBudgetTemplateKey), ("brief", brief),
                ("confirmationToken", proposal.GetProperty("confirmationToken").GetString()));
            created.TryGetProperty("error", out JsonElement createError).Should().BeFalse(createError.ToString());

            WorkflowDefinition row = await db.WorkflowDefinitions.AsNoTracking().SingleAsync(w => w.Id == created.GetProperty("workflowId").GetGuid());
            row.TemplateKey.Should().Be(WorkflowTemplateCatalog.AvatarClipTemplateKey);
            AppManifest manifest = AppManifestJson.TryParse(row.AppManifestJson)!;
            manifest.Inputs!.Single(i => i.Id == "liteMode").Default!.Value.GetBoolean().Should().BeTrue();
            manifest.Inputs!.Single(i => i.Id == "whatTheyDo").Default!.Value.GetString().Should().Be("Waves and says hello");
        }
    }

    // ------------------------------------------------------------------ GetWorkflow

    [Fact]
    public async Task GetWorkflow_lists_an_apps_fields_and_where_each_is_used()
    {
        using InferenceApiDbContext db = new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        (Guid owner, Project project, AgentDefinition agent) = await SeedAsync(db);
        WorkflowDefinition workflow = new()
        {
            Id = Guid.NewGuid(),
            Name = "Latte clips",
            ProjectId = project.Id,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            AppManifestJson = AppManifestJson.Serialize(Manifest(Input("videoPrompt", "longText", "What happens in the video", required: true))),
        };
        workflow.Steps.Add(new WorkflowStep { Id = Guid.NewGuid(), StepOrder = 1, StepType = StepType.VideoGenerate, AgentDefinitionId = agent.Id, Label = "Make the clip", VideoGenerateConfigJson = Generate });
        db.WorkflowDefinitions.Add(workflow);
        await db.SaveChangesAsync();
        FakeCurrentUser user = new(owner);

        async Task<JsonElement> Read(bool on)
        {
            AssistantWorkflowTools tools = new(db, ReelBolt.Inference.Api.Services.Assistant.ConfirmationTokenService.CreateEphemeral(), TestTenancy.Access(db, user), user, Switch(on));
            object? raw = await tools.GetTools(new AssistantToolContext(owner, project.Id)).Single(t => t.Name == "GetWorkflow")
                .InvokeAsync(new AIFunctionArguments { ["workflowId"] = workflow.Id });
            return JsonDocument.Parse(raw is JsonElement { ValueKind: JsonValueKind.String } s ? s.GetString()! : raw!.ToString()!).RootElement.Clone();
        }

        JsonElement field = (await Read(on: true)).GetProperty("appInputs").GetProperty("inputs").EnumerateArray().Single();
        field.GetProperty("id").GetString().Should().Be("videoPrompt");
        field.GetProperty("required").GetBoolean().Should().BeTrue();
        field.GetProperty("usedBy").EnumerateArray().Select(u => u.GetString()).Should().Contain("video prompt of step 1 \"Make the clip\"");
    }

    // ------------------------------------------------------------------ the prompt

    private static string PromptFor(bool on)
    {
        MethodInfo method = typeof(AssistantAgent).GetMethod("PromptFor", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (string)method.Invoke(null, [Switch(on)])!;
    }

    [Fact]
    public void The_app_inputs_section_is_identical_in_the_agent_and_the_seeder_and_always_added()
    {
        string agent = AssistantAgent.AppInputsSection;
        string seeded = (string)typeof(DatabaseSeeder).GetField("AssistantAppInputsSection", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        seeded.Should().Be(agent);

        string on = PromptFor(on: true);
        on.Should().EndWith("\n\n" + agent);
        on.Length.Should().BeLessThan(38_700, "the section rides inside the recipe-first ceiling (+~190 the ImageGenerate step line, +~400 the social publishing tools, +~450 the sound-effect tools, +~1,500 the avatar and object tools)");
    }

    [Fact]
    public void The_app_inputs_section_says_fill_the_fields_never_edit_the_steps_and_names_only_real_tools()
    {
        string section = AssistantAgent.AppInputsSection;
        section.Should().Contain("Never edit the workflow's steps to change one run")
            .And.Contain("brief.inputs").And.Contain("setAppInput").And.Contain("bindAppInput").And.Contain("appManifest")
            .And.Contain("never become fields");
        Regex.IsMatch(section, @"\b(yet|will|going to|coming soon|planned)\b", RegexOptions.IgnoreCase).Should().BeFalse();

        HashSet<string> tools = [.. AssistantToolTestFactory.CreateProvider(
                new InferenceApiDbContext(new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options),
                new FakeCurrentUser(Guid.NewGuid()))
            .GetTools(new AssistantToolContext(Guid.NewGuid(), null, ThreadId: Guid.NewGuid())).Select(t => t.Name)];
        foreach (Match name in Regex.Matches(section, @"\b[A-Z][a-z0-9]+(?:[A-Z][A-Za-z0-9]*)+\b"))
            tools.Should().Contain(name.Value);
    }
}
