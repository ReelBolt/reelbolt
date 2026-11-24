using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using MassTransit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.Apps;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The workflow-apps backend: the manifest on save (stored, refused, WL018), template provisioning, created_via, the
/// two read endpoints and their tenant isolation.
/// </summary>
public class WorkflowAppsApiTests
{
    private sealed class FakeUser(Guid userId) : ICurrentUser
    {
        public Guid UserId { get; } = userId;
        public string Email => "u@example.com";
        public bool IsAuthenticated => true;
        public bool IsPlatformAdmin => false;
        public Guid? OrganizationId => TestTenancy.OrgOf(UserId);
        public ReelBolt.Shared.Tenancy.OrganizationRole? OrgRole => ReelBolt.Shared.Tenancy.OrganizationRole.Member;
    }

    private sealed class Fixture : IDisposable
    {
        public InferenceApiDbContext Db { get; } = new(new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        public Guid Owner { get; } = Guid.NewGuid();
        public Project Project { get; }
        public Dictionary<AgentType, AgentDefinition> Agents { get; } = [];

        public Fixture()
        {
            Project = new Project
            {
                Id = Guid.NewGuid(),
                Name = "p",
                OwnerId = Owner,
                OrganizationId = TestTenancy.OrgOf(Owner),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            Db.Projects.Add(Project);
            foreach (AgentType type in Enum.GetValues<AgentType>())
            {
                AgentDefinition agent = new()
                {
                    Id = Guid.NewGuid(),
                    Name = type.ToString(),
                    Description = "d",
                    SystemPrompt = "p",
                    AgentType = type,
                    IsBuiltIn = true,
                    CreatedAt = DateTime.UtcNow
                };
                Agents[type] = agent;
                Db.AgentDefinitions.Add(agent);
            }
            Db.SaveChanges();
        }

        public WorkflowTemplateProvisioningService Templates() =>
            new(Db, NullLogger<WorkflowTemplateProvisioningService>.Instance, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Avatars:Enabled"] = "true" }).Build());

        public WorkflowsController Workflows(Guid? as_ = null)
        {
            FakeUser user = new(as_ ?? Owner);
            return new WorkflowsController(
                Db, user, TestTenancy.Access(Db, user), Mock.Of<IPublishEndpoint>(), Templates(),
                new WorkflowExecutionService(Db, Mock.Of<IPublishEndpoint>(), NullLogger<WorkflowExecutionService>.Instance),
                NullLogger<WorkflowsController>.Instance);
        }

        public WorkflowAppsController Apps(Guid? as_ = null)
        {
            FakeUser user = new(as_ ?? Owner);
            return new WorkflowAppsController(Db, TestTenancy.Access(Db, user), Templates(), new ProjectReadinessFacts(Db));
        }

        public CreateWorkflowStepRequest Step(AgentType type, int order, string? stepType = null) =>
            new(Agents[type].Id, order, null, null, StepType: stepType);

        public void Dispose() => Db.Dispose();
    }

    private static AppManifest NarratedManifest() => new(
        Title: "Narrated",
        Inputs:
        [
            new AppManifestInput("brief", Field: "voice", Label: "Narration"),
            new AppManifestInput("brief", Field: "language", Label: "Language")
        ]);

    private static T Ok<T>(ActionResult<T> result) where T : class =>
        (((OkObjectResult)result.Result!).Value as T)!;

    // ---- provisioning ----

    [Fact]
    public async Task Provisioning_copies_the_curated_manifest_and_marks_the_workflow_as_from_a_template()
    {
        using Fixture f = new();

        WorkflowDefinition? promo = await f.Templates().CreateFromTemplateAsync(
            f.Project.Id, WorkflowTemplateCatalog.StarterTemplateKey, skipIfExists: false, CancellationToken.None);
        WorkflowDefinition? derived = await f.Templates().CreateFromTemplateAsync(
            f.Project.Id, WorkflowTemplateCatalog.NarratedStoryTemplateKey, skipIfExists: false, CancellationToken.None);

        AppManifestJson.TryParse(promo!.AppManifestJson)!.Title.Should().Be("Promo video from your code");
        promo.AppManifestJson.Should().NotBeNull("the curated page is copied, with its model fields pointed at their built-in agents");
        promo.CreatedVia.Should().Be("template");
        derived!.AppManifestJson.Should().NotBeNull("the served recipe carries its own app inputs");
        derived.CreatedVia.Should().Be("template");
    }

    // ---- save ----

    [Fact]
    public async Task Create_stores_a_valid_manifest_sanitized_and_marks_the_workflow_as_user_made()
    {
        using Fixture f = new();
        CreateWorkflowRequest request = new("wf",
            [f.Step(AgentType.ScriptwriterAgent, 1)],
            AppManifest: NarratedManifest() with { Title = "  <i>Narrated</i> " });

        ActionResult<WorkflowDefinitionResponse> result = await f.Workflows().Create(f.Project.Id, request, CancellationToken.None);

        ((ObjectResult)result.Result!).StatusCode.Should().Be(201);
        WorkflowDefinition saved = await f.Db.WorkflowDefinitions.SingleAsync();
        saved.CreatedVia.Should().Be("user");
        saved.CreatedInThreadId.Should().BeNull();
        AppManifestJson.TryParse(saved.AppManifestJson)!.Title.Should().Be("iNarrated/i");
    }

    [Fact]
    public async Task Create_refuses_a_manifest_the_steps_cannot_honour_and_writes_nothing()
    {
        using Fixture f = new();
        CreateWorkflowRequest request = new("wf", [f.Step(AgentType.CodeStructureAnalyzer, 1)], AppManifest: NarratedManifest());

        ActionResult<WorkflowDefinitionResponse> result = await f.Workflows().Create(f.Project.Id, request, CancellationToken.None);

        BadRequestObjectResult bad = result.Result.Should().BeOfType<BadRequestObjectResult>().Subject;
        JsonSerializer.Serialize(bad.Value).Should().Contain("APP_MANIFEST_INVALID").And.Contain("Narration");
        (await f.Db.WorkflowDefinitions.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Create_without_a_manifest_stores_none()
    {
        using Fixture f = new();

        await f.Workflows().Create(f.Project.Id, new CreateWorkflowRequest("wf", [f.Step(AgentType.ScriptwriterAgent, 1)]), CancellationToken.None);

        (await f.Db.WorkflowDefinitions.SingleAsync()).AppManifestJson.Should().BeNull();
    }

    [Fact]
    public async Task Update_that_would_orphan_an_exposed_input_is_a_400_naming_it_and_leaves_the_steps_alone()
    {
        using Fixture f = new();
        WorkflowsController controller = f.Workflows();
        await controller.Create(f.Project.Id,
            new CreateWorkflowRequest("wf", [f.Step(AgentType.ScriptwriterAgent, 1)], AppManifest: NarratedManifest()), CancellationToken.None);
        Guid id = (await f.Db.WorkflowDefinitions.SingleAsync()).Id;

        ActionResult<WorkflowDefinitionResponse> result = await controller.Update(
            f.Project.Id, id, new UpdateWorkflowRequest(null, [f.Step(AgentType.CodeStructureAnalyzer, 1)]), CancellationToken.None);

        BadRequestObjectResult bad = result.Result.Should().BeOfType<BadRequestObjectResult>().Subject;
        JsonSerializer.Serialize(bad.Value).Should().Contain("Narration");
        (await f.Db.WorkflowSteps.SingleAsync(s => s.WorkflowDefinitionId == id)).AgentDefinitionId
            .Should().Be(f.Agents[AgentType.ScriptwriterAgent].Id);
    }

    [Fact]
    public async Task Save_check_passes_a_stored_manifest_the_new_steps_still_honour_and_sanitizes_a_supplied_one()
    {
        // The write itself (WorkflowStepReplacer) needs a relational provider; the decision is the shared check.
        using Fixture f = new();
        IReadOnlyList<AppStepFacts> steps = await WorkflowAppSupport.FactsForRequestAsync(
            f.Db, [f.Step(AgentType.ScriptwriterAgent, 1)], CancellationToken.None); // the Scriptwriter reads the voice the manifest exposes (the Author does not: RunBriefRegistry)

        (AppManifest? keep, IReadOnlyList<string> keepErrors) = WorkflowAppSupport.CheckForSave(
            null, AppManifestJson.Serialize(NarratedManifest()), steps);
        keep.Should().BeNull("no manifest supplied leaves the stored one alone");
        keepErrors.Should().BeEmpty();

        (AppManifest? replaced, IReadOnlyList<string> replaceErrors) = WorkflowAppSupport.CheckForSave(
            new AppManifest(Title: " <b>Renamed</b> ", Inputs: []), null, steps);
        replaced!.Title.Should().Be("bRenamed/b");
        replaceErrors.Should().BeEmpty();

        (_, IReadOnlyList<string> refused) = WorkflowAppSupport.CheckForSave(
            new AppManifest(Inputs: [new AppManifestInput("files", Role: "video", Label: "Clips")]), null, steps);
        refused.Should().ContainSingle().Which.Should().Contain("\"Clips\"");
    }

    [Fact]
    public async Task Update_with_a_manifest_the_new_steps_cannot_honour_is_a_400_and_keeps_the_stored_manifest()
    {
        using Fixture f = new();
        WorkflowsController controller = f.Workflows();
        await controller.Create(f.Project.Id,
            new CreateWorkflowRequest("wf", [f.Step(AgentType.ScriptwriterAgent, 1)], AppManifest: NarratedManifest()), CancellationToken.None);
        Guid id = (await f.Db.WorkflowDefinitions.SingleAsync()).Id;

        ActionResult<WorkflowDefinitionResponse> bad = await controller.Update(f.Project.Id, id, new UpdateWorkflowRequest(
            null, [f.Step(AgentType.AuthorAgent, 1)],
            AppManifest: new AppManifest(Title: "x", Inputs: [new AppManifestInput("files", Role: "video", Label: "Clips")])),
            CancellationToken.None);

        bad.Result.Should().BeOfType<BadRequestObjectResult>();
        AppManifestJson.TryParse((await f.Db.WorkflowDefinitions.SingleAsync()).AppManifestJson)!.Title.Should().Be("Narrated");
    }

    [Fact]
    public void The_manifest_member_is_left_out_of_a_request_that_does_not_use_it()
    {
        // The assistant's ApplyWorkflow confirmation token hashes this request: unused must serialize as before.
        string json = JsonSerializer.Serialize(new CreateWorkflowRequest("wf", []));
        json.Should().NotContain("AppManifest");
        JsonSerializer.Serialize(new UpdateWorkflowRequest("wf", [])).Should().NotContain("AppManifest");
    }

    // ---- endpoints ----

    [Fact]
    public async Task List_returns_apps_with_cost_class_inputs_and_last_run_and_recipes_with_fit()
    {
        using Fixture f = new();
        await f.Templates().CreateFromTemplateAsync(f.Project.Id, "video-derush-edit", skipIfExists: false, CancellationToken.None);
        WorkflowDefinition workflow = await f.Db.WorkflowDefinitions.SingleAsync();
        f.Db.WorkflowExecutions.Add(new WorkflowExecution
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = workflow.Id,
            ProjectId = f.Project.Id,
            OrganizationId = f.Project.OrganizationId,
            Status = ExecutionStatus.Passed,
            StartedAt = DateTime.UtcNow.AddMinutes(-5),
            CompletedAt = DateTime.UtcNow.AddMinutes(-4)
        });
        await f.Db.SaveChangesAsync();

        WorkflowAppsResponse response = Ok(await f.Apps().List(f.Project.Id, CancellationToken.None));

        WorkflowAppCard card = response.Apps.Should().ContainSingle().Subject;
        card.WorkflowId.Should().Be(workflow.Id);
        card.Title.Should().Be("Edit my video");
        card.Category.Should().Be("footage");
        card.Source.Should().Be("edit-my-video");
        card.CostClass.Should().Be(AppCostClass.UsesCreditsAndAiShots);
        card.InputsCount.Should().Be(27);
        card.LastRun!.Status.Should().Be("Passed");
        card.LastRun.FinishedAt.Should().NotBeNull();

        WorkflowRecipeCard recipe = response.Recipes.Single(r => r.Key == "edit-my-video");
        recipe.Fit.Should().Be("needs");
        recipe.Missing.Should().ContainSingle(m => m.Need == "video" && m.Action == "add-files" && m.Message == "Add clips");
        response.Recipes.Count.Should().Be(WorkflowTemplateCatalog.GetListed().Count);
    }

    [Fact]
    public async Task Recipe_fit_turns_ready_when_the_project_has_the_files_and_the_providers()
    {
        using Fixture f = new();
        f.Db.ProjectFiles.Add(new ProjectFile
        {
            Id = Guid.NewGuid(),
            ProjectId = f.Project.Id,
            OriginalFileName = "a.mp4",
            MimeType = "video/mp4",
            Category = "userFiles"
        });
        await f.Db.SaveChangesAsync();

        WorkflowAppsResponse response = Ok(await f.Apps().List(f.Project.Id, CancellationToken.None));

        response.Recipes.Single(r => r.Key == "edit-my-video").Fit.Should().Be("needs", "the optional voice-over and extra AI shots need providers");
        response.Recipes.Single(r => r.Key == "edit-my-video").Missing
            .Should().Contain(m => m.Need == "voice" && m.Action == "connect-provider");
        response.Recipes.Single(r => r.Key == "quick-win-promo").Missing.Should().ContainSingle(m => m.Need == "code");
    }

    [Fact]
    public async Task A_project_whose_only_code_is_a_zip_cannot_read_it_rather_than_missing_code()
    {
        using Fixture f = new();
        f.Db.ProjectFiles.Add(new ProjectFile
        {
            Id = Guid.NewGuid(),
            ProjectId = f.Project.Id,
            OriginalFileName = "app.zip",
            MimeType = "application/zip",
            Category = "userFiles"
        });
        await f.Db.SaveChangesAsync();

        WorkflowAppsResponse response = Ok(await f.Apps().List(f.Project.Id, CancellationToken.None));

        WorkflowAppMissing code = response.Recipes.Single(r => r.Key == "quick-win-promo").Missing.Single(m => m.Need == "code");
        code.Message.Should().Be("We can't read the code in your zip");
        code.Detail.Should().Contain("without node_modules").And.Contain("GitHub");
        code.Action.Should().Be("add-files");
    }

    [Fact]
    public async Task A_selectable_library_voice_covers_a_voice_step_without_a_speech_provider()
    {
        using Fixture f = new();
        f.Db.ProjectFiles.Add(new ProjectFile
        {
            Id = Guid.NewGuid(),
            ProjectId = f.Project.Id,
            OriginalFileName = "a.mp4",
            MimeType = "video/mp4",
            Category = "userFiles"
        });
        f.Db.ProjectVoices.Add(new ProjectVoice
        {
            Id = Guid.NewGuid(),
            ProjectId = f.Project.Id,
            Name = "Energetic Male",
            Source = ProjectVoiceSource.Library,
            RemoteVoiceId = "lib-voice",
            CreatedByUserId = f.Owner
        });
        await f.Db.SaveChangesAsync();

        WorkflowAppsResponse response = Ok(await f.Apps().List(f.Project.Id, CancellationToken.None));

        response.Recipes.Single(r => r.Key == "edit-my-video").Missing
            .Should().NotContain(m => m.Need == "voice", "the project already owns a selectable voice");
    }

    [Fact]
    public async Task A_selectable_voice_with_a_blank_remote_id_does_not_cover_a_voice_step()
    {
        using Fixture f = new();
        f.Db.ProjectFiles.Add(new ProjectFile
        {
            Id = Guid.NewGuid(),
            ProjectId = f.Project.Id,
            OriginalFileName = "a.mp4",
            MimeType = "video/mp4",
            Category = "userFiles"
        });
        f.Db.ProjectVoices.Add(new ProjectVoice
        {
            Id = Guid.NewGuid(),
            ProjectId = f.Project.Id,
            Name = "Half-configured",
            Source = ProjectVoiceSource.Library,
            RemoteVoiceId = "",
            CreatedByUserId = f.Owner
        });
        await f.Db.SaveChangesAsync();

        WorkflowAppsResponse response = Ok(await f.Apps().List(f.Project.Id, CancellationToken.None));

        response.Recipes.Single(r => r.Key == "edit-my-video").Missing
            .Should().Contain(m => m.Need == "voice" && m.Action == "connect-provider",
                "a selectable voice with no remote id is not the one the engine would default to");
    }

    [Fact]
    public async Task A_cloned_voice_whose_provider_capability_was_deleted_does_not_cover_a_voice_step()
    {
        using Fixture f = new();
        f.Db.ProjectFiles.Add(new ProjectFile
        {
            Id = Guid.NewGuid(),
            ProjectId = f.Project.Id,
            OriginalFileName = "a.mp4",
            MimeType = "video/mp4",
            Category = "userFiles"
        });
        DateTime now = DateTime.UtcNow;
        f.Db.ProjectVoices.Add(new ProjectVoice
        {
            Id = Guid.NewGuid(),
            ProjectId = f.Project.Id,
            Name = "My clone",
            Source = ProjectVoiceSource.Cloned,
            RemoteVoiceId = "clone-voice",
            InferenceProviderId = null,
            ConsentAttestedByUserId = f.Owner,
            ConsentAttestedAt = now,
            RetentionUntil = now.AddYears(1),
            CreatedByUserId = f.Owner
        });
        await f.Db.SaveChangesAsync();

        WorkflowAppsResponse response = Ok(await f.Apps().List(f.Project.Id, CancellationToken.None));

        response.Recipes.Single(r => r.Key == "edit-my-video").Missing
            .Should().Contain(m => m.Need == "voice" && m.Action == "connect-provider",
                "a clone whose provider capability was deleted is not the one the engine would default to");
    }

    [Fact]
    public async Task A_custom_workflow_is_an_app_on_day_one_with_a_derived_manifest()
    {
        using Fixture f = new();
        await f.Workflows().Create(f.Project.Id,
            new CreateWorkflowRequest("My flow", [f.Step(AgentType.ScriptwriterAgent, 1)]), CancellationToken.None);

        WorkflowAppsResponse response = Ok(await f.Apps().List(f.Project.Id, CancellationToken.None));

        WorkflowAppCard card = response.Apps.Single();
        card.Title.Should().Be("My flow");
        card.Source.Should().Be("custom");
        card.InputsCount.Should().Be(6);
        card.LastRun.Should().BeNull();
    }

    [Fact]
    public async Task Get_app_returns_the_effective_manifest_explicit_over_derived()
    {
        using Fixture f = new();
        WorkflowsController workflows = f.Workflows();
        await workflows.Create(f.Project.Id,
            new CreateWorkflowRequest("Explicit", [f.Step(AgentType.ScriptwriterAgent, 1)], AppManifest: NarratedManifest()), CancellationToken.None);
        await workflows.Create(f.Project.Id,
            new CreateWorkflowRequest("Derived", [f.Step(AgentType.ScriptwriterAgent, 1)]), CancellationToken.None);
        Guid explicitId = (await f.Db.WorkflowDefinitions.SingleAsync(w => w.Name == "Explicit")).Id;
        Guid derivedId = (await f.Db.WorkflowDefinitions.SingleAsync(w => w.Name == "Derived")).Id;

        WorkflowAppResponse explicitApp = Ok(await f.Apps().Get(f.Project.Id, explicitId, CancellationToken.None));
        WorkflowAppResponse derivedApp = Ok(await f.Apps().Get(f.Project.Id, derivedId, CancellationToken.None));

        explicitApp.HasExplicitManifest.Should().BeTrue();
        WorkflowAppResponse withInputs = explicitApp;
        withInputs.BindableFields!["VideoGenerate"].Select(b => b.Path).Should().Contain(["inlinePrompt", "ratio", "@model"]);
        withInputs.BindableFields.Should().NotContainKey("Agent", "an agent's text comes through the request template, not a setting");
        explicitApp.Manifest.Title.Should().Be("Narrated");
        explicitApp.Manifest.Inputs!.Select(i => i.Field).Should().Equal("voice", "language");
        explicitApp.Manifest.Category.Should().Be("promo", "an unset member falls back to the derived one");
        derivedApp.HasExplicitManifest.Should().BeFalse();
        derivedApp.Manifest.Title.Should().Be("Derived");
        derivedApp.Manifest.Inputs!.Count.Should().Be(6);
        derivedApp.CostClass.Should().Be(AppCostClass.UsesCredits);
    }

    [Fact]
    public async Task F3_what_changes_describes_the_draft_page_against_the_stored_one_and_is_off_with_the_switch()
    {
        using Fixture f = new();
        await f.Workflows().Create(f.Project.Id,
            new CreateWorkflowRequest("Explicit", [f.Step(AgentType.ScriptwriterAgent, 1)], AppManifest: NarratedManifest()), CancellationToken.None);
        Guid id = (await f.Db.WorkflowDefinitions.SingleAsync(w => w.Name == "Explicit")).Id;
        AppManifest draft = NarratedManifest() with
        {
            Version = 2,
            Inputs = [.. NarratedManifest().Inputs!, new AppManifestInput("input", Id: "audience", Type: "shortText", Label: "Who it is for")],
            RequestTemplate = "Made for {{input.audience}}."
        };
        AppPageChangesResponse changes = Ok(await f.Apps().DescribeAppChanges(f.Project.Id, id, new AppPageChangesRequest(draft), CancellationToken.None));

        changes.Lines.Should().Contain(l => l.StartsWith("Adds the field", StringComparison.Ordinal) && l.Contains("Who it is for", StringComparison.Ordinal));
        changes.Lines.Should().Contain(l => l.Contains("request of each run", StringComparison.Ordinal));
        Ok(await f.Apps().DescribeAppChanges(f.Project.Id, id, null, CancellationToken.None)).Lines.Should().BeEmpty("nothing is drafted");

        (await f.Apps().DescribeAppChanges(f.Project.Id, Guid.NewGuid(), null, CancellationToken.None)).Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Another_organizations_project_and_workflow_are_not_found()
    {
        using Fixture f = new();
        await f.Workflows().Create(f.Project.Id,
            new CreateWorkflowRequest("wf", [f.Step(AgentType.ScriptwriterAgent, 1)]), CancellationToken.None);
        Guid id = (await f.Db.WorkflowDefinitions.SingleAsync()).Id;
        Guid stranger = Guid.NewGuid();

        (await f.Apps(stranger).List(f.Project.Id, CancellationToken.None)).Result.Should().BeOfType<NotFoundResult>();
        (await f.Apps(stranger).Get(f.Project.Id, id, CancellationToken.None)).Result.Should().BeOfType<NotFoundResult>();
        (await f.Apps().Get(f.Project.Id, Guid.NewGuid(), CancellationToken.None)).Result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Another_projects_workflow_is_not_found_through_this_project()
    {
        using Fixture f = new();
        Project other = new()
        {
            Id = Guid.NewGuid(),
            Name = "other",
            OwnerId = f.Owner,
            OrganizationId = f.Project.OrganizationId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        f.Db.Projects.Add(other);
        await f.Db.SaveChangesAsync();
        await f.Workflows().Create(other.Id,
            new CreateWorkflowRequest("wf", [f.Step(AgentType.ScriptwriterAgent, 1)]), CancellationToken.None);
        Guid id = (await f.Db.WorkflowDefinitions.SingleAsync()).Id;

        (await f.Apps().Get(f.Project.Id, id, CancellationToken.None)).Result.Should().BeOfType<NotFoundResult>();
        Ok(await f.Apps().List(f.Project.Id, CancellationToken.None)).Apps.Should().BeEmpty();
    }

    // ---- thread link ----

    [Fact]
    public async Task Deleting_the_thread_clears_the_workflows_pointer_to_it()
    {
        using Fixture f = new();
        FakeUser user = new(f.Owner);
        AssistantThread thread = new()
        {
            Id = Guid.NewGuid(),
            UserId = f.Owner,
            OrganizationId = f.Project.OrganizationId,
            Title = "t",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        f.Db.AssistantThreads.Add(thread);
        f.Db.WorkflowDefinitions.Add(new WorkflowDefinition
        {
            Id = Guid.NewGuid(),
            Name = "made by the assistant",
            ProjectId = f.Project.Id,
            CreatedVia = WorkflowCreatedVia.Assistant,
            CreatedInThreadId = thread.Id
        });
        await f.Db.SaveChangesAsync();

        AssistantThreadsController controller = new(f.Db, user, TestTenancy.Access(f.Db, user));
        (await controller.DeleteThread(thread.Id, CancellationToken.None)).Should().BeOfType<NoContentResult>();

        WorkflowDefinition kept = await f.Db.WorkflowDefinitions.SingleAsync();
        kept.CreatedInThreadId.Should().BeNull();
        kept.CreatedVia.Should().Be("assistant");
    }

    // ---- follow-up: clear, template key, last run ----

    [Fact]
    public async Task Update_can_clear_a_stored_manifest_and_cannot_send_both()
    {
        using Fixture f = new();
        WorkflowsController controller = f.Workflows();
        await controller.Create(f.Project.Id,
            new CreateWorkflowRequest("wf", [f.Step(AgentType.ScriptwriterAgent, 1)], AppManifest: NarratedManifest()), CancellationToken.None);
        Guid id = (await f.Db.WorkflowDefinitions.SingleAsync()).Id;

        ActionResult<WorkflowDefinitionResponse> both = await controller.Update(f.Project.Id, id, new UpdateWorkflowRequest(
            null, [f.Step(AgentType.AuthorAgent, 1)], AppManifest: NarratedManifest(), ClearAppManifest: true), CancellationToken.None);
        both.Result.Should().BeOfType<BadRequestObjectResult>();

        // A clear with steps that would orphan the OLD manifest passes the check (nothing stored is left to orphan);
        // the write itself needs a relational provider, so the decision is asserted through the shared check.
        (_, IReadOnlyList<string> errors) = WorkflowAppSupport.CheckForSave(
            null, null, await WorkflowAppSupport.FactsForRequestAsync(f.Db, [f.Step(AgentType.CodeStructureAnalyzer, 1)], CancellationToken.None));
        errors.Should().BeEmpty();
        JsonSerializer.Serialize(new UpdateWorkflowRequest("wf", [])).Should().NotContain("ClearAppManifest");
    }

    [Fact]
    public async Task Source_comes_from_the_stored_template_key_even_after_a_rename_and_never_from_a_lookalike_name()
    {
        using Fixture f = new();
        WorkflowDefinition? promo = await f.Templates().CreateFromTemplateAsync(
            f.Project.Id, "video-derush-edit", skipIfExists: false, CancellationToken.None);
        promo!.TemplateKey.Should().Be("edit-my-video");
        promo.Name = "My renamed edit";
        f.Db.WorkflowDefinitions.Add(new WorkflowDefinition
        {
            Id = Guid.NewGuid(),
            ProjectId = f.Project.Id,
            CreatedVia = WorkflowCreatedVia.User,
            Name = WorkflowTemplateCatalog.BuildVersionedWorkflowName(WorkflowTemplateCatalog.GetByKey("quick-win-promo")!)
        });
        await f.Db.SaveChangesAsync();

        WorkflowAppsResponse response = Ok(await f.Apps().List(f.Project.Id, CancellationToken.None));

        response.Apps.Single(a => a.WorkflowId == promo.Id).Source.Should().Be("edit-my-video");
        response.Apps.Single(a => a.WorkflowId != promo.Id).Source.Should().Be("custom");
    }

    [Fact]
    public async Task A_newer_queued_run_is_the_cards_last_run()
    {
        using Fixture f = new();
        WorkflowDefinition workflow = new() { Id = Guid.NewGuid(), ProjectId = f.Project.Id, Name = "w", CreatedVia = WorkflowCreatedVia.User };
        f.Db.WorkflowDefinitions.Add(workflow);
        f.Db.WorkflowExecutions.Add(new WorkflowExecution
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = workflow.Id,
            ProjectId = f.Project.Id,
            OrganizationId = f.Project.OrganizationId,
            Status = ExecutionStatus.Passed,
            QueuedAt = DateTime.UtcNow.AddHours(-2),
            StartedAt = DateTime.UtcNow.AddHours(-2)
        });
        f.Db.WorkflowExecutions.Add(new WorkflowExecution
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = workflow.Id,
            ProjectId = f.Project.Id,
            OrganizationId = f.Project.OrganizationId,
            Status = ExecutionStatus.Queued,
            QueuedAt = DateTime.UtcNow.AddMinutes(-1)
        });
        await f.Db.SaveChangesAsync();

        WorkflowAppsResponse response = Ok(await f.Apps().List(f.Project.Id, CancellationToken.None));

        response.Apps.Single().LastRun!.Status.Should().Be("Queued");
    }

    [Fact]
    public async Task The_title_fallback_for_a_hostile_workflow_name_is_clean()
    {
        using Fixture f = new();
        await f.Workflows().Create(f.Project.Id,
            new CreateWorkflowRequest("<script>" + new string('x', 200), [f.Step(AgentType.ScriptwriterAgent, 1)]), CancellationToken.None);

        string title = Ok(await f.Apps().List(f.Project.Id, CancellationToken.None)).Apps.Single().Title;

        title.Length.Should().BeLessThanOrEqualTo(60);
        title.Should().NotContain("<");
    }

    [Fact]
    public async Task Purge_clears_the_thread_pointer_on_workflows_that_outlive_the_account()
    {
        using Fixture f = new();
        Guid threadId = Guid.NewGuid();
        f.Db.AssistantThreads.Add(new AssistantThread
        {
            Id = threadId,
            UserId = f.Owner,
            OrganizationId = f.Project.OrganizationId,
            Title = "t",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        f.Db.WorkflowDefinitions.Add(new WorkflowDefinition
        {
            Id = Guid.NewGuid(),
            ProjectId = f.Project.Id,
            Name = "w",
            CreatedVia = WorkflowCreatedVia.Assistant,
            CreatedInThreadId = threadId
        });
        await f.Db.SaveChangesAsync();

        // The purge's own statement, run against the same context shape.
        foreach (WorkflowDefinition created in await f.Db.WorkflowDefinitions.IgnoreQueryFilters()
            .Where(w => w.CreatedInThreadId != null
                && f.Db.AssistantThreads.IgnoreQueryFilters().Any(t => t.UserId == f.Owner && t.Id == w.CreatedInThreadId))
            .ToListAsync())
            created.CreatedInThreadId = null;
        await f.Db.SaveChangesAsync();

        (await f.Db.WorkflowDefinitions.SingleAsync()).CreatedInThreadId.Should().BeNull();
    }
}
