using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Projects;
using ReelBolt.Inference.Api.Services.Telemetry;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Acquisition;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Tests.Services.Telemetry;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// <c>POST /api/v1/projects</c> with a <c>starterTemplateKey</c> (docs/design/new-user-funnel.md section 6):
/// the project starts with THAT recipe and the automatic code-promo starter is not created; a key that is
/// not a recipe this deployment lists answers 400 and creates nothing. Without the key nothing changes.
/// </summary>
public class ProjectStarterTemplateTests : IDisposable
{
    private static readonly Guid UserId = Guid.NewGuid();

    private readonly InferenceApiDbContext _db = new(new DbContextOptionsBuilder<InferenceApiDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private readonly RecordingProductEventLog _log = new();

    public ProjectStarterTemplateTests()
    {
        foreach (AgentType type in Enum.GetValues<AgentType>())
        {
            _db.AgentDefinitions.Add(new AgentDefinition
            {
                Id = Guid.NewGuid(),
                Name = type.ToString(),
                Description = "d",
                SystemPrompt = "p",
                AgentType = type,
                IsBuiltIn = true,
                CreatedAt = DateTime.UtcNow
            });
        }

        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    private static ICurrentUser User()
    {
        var u = new Mock<ICurrentUser>();
        u.SetupGet(x => x.UserId).Returns(UserId);
        u.SetupGet(x => x.Email).Returns("t@example.com");
        u.SetupGet(x => x.IsAuthenticated).Returns(true);
        u.SetupGet(x => x.EmailVerified).Returns(true);
        u.SetupGet(x => x.OrganizationId).Returns(TestTenancy.OrgOf(UserId));
        u.SetupGet(x => x.OrgRole).Returns(OrganizationRole.Owner);
        return u.Object;
    }

    private WorkflowTemplateProvisioningService Provisioning(bool avatars = false, bool advanced = true, bool autoStarter = true) => new(
        _db, NullLogger<WorkflowTemplateProvisioningService>.Instance,
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Avatars:Enabled"] = avatars ? "true" : "false",
            ["WorkflowTemplates:EnableAdvancedTemplates"] = advanced ? "true" : "false",
            ["WorkflowTemplates:EnableAutoStarter"] = autoStarter ? "true" : "false",
        }).Build());

    private ProjectsController Controller(WorkflowTemplateProvisioningService? provisioning = null)
    {
        ICurrentUser user = User();
        return new ProjectsController(
            _db, user, TestTenancy.Access(_db, user), provisioning ?? Provisioning(), null,
            new ProductEventEmitter(_log, NullLogger<ProductEventEmitter>.Instance));
    }

    private static ProjectResponse Created(ActionResult<ProjectResponse> result) =>
        (ProjectResponse)result.Result.Should().BeOfType<CreatedAtActionResult>().Subject.Value!;

    private List<WorkflowDefinition> Apps(Guid projectId) =>
        _db.WorkflowDefinitions.AsNoTracking().Include(w => w.Steps).Where(w => w.ProjectId == projectId).ToList();

    [Fact]
    public async Task Without_a_starter_key_the_project_gets_the_automatic_starter_exactly_as_before()
    {
        ProjectResponse project = Created(await Controller().Create(new CreateProjectRequest("My reel", null), CancellationToken.None));

        WorkflowDefinition app = Apps(project.Id).Single();
        app.TemplateKey.Should().Be(WorkflowTemplateCatalog.StarterTemplateKey);
        _log.Rows.Select(r => r.Event).Should().Equal(["activation.project_created"], "the automatic starter is not a choice");
        WorkflowTemplateCatalog.GetAutoCreateTemplates().Select(t => t.Key).Should().Equal(WorkflowTemplateCatalog.StarterTemplateKey);
    }

    [Fact]
    public async Task With_a_starter_key_the_project_gets_that_app_and_not_the_automatic_starter()
    {
        ProjectResponse project = Created(await Controller().Create(
            new CreateProjectRequest("Bakery opening", "Phone clips", StarterTemplateKey: "edit-my-video"), CancellationToken.None));

        WorkflowDefinition app = Apps(project.Id).Should().ContainSingle("the chosen recipe is the project's one app: no code-promo starter beside it").Subject;
        app.TemplateKey.Should().Be("edit-my-video");
        app.CreatedVia.Should().Be(WorkflowCreatedVia.Template);
        app.Kind.Should().Be(WorkflowDefinitionKind.Standard);
        WorkflowTemplateDefinition template = WorkflowTemplateCatalog.GetByKey("edit-my-video")!;
        app.Name.Should().Be(WorkflowTemplateCatalog.BuildVersionedWorkflowName(template));
        app.Steps.Should().HaveCount(template.Steps.Count, "it is the same app the apply route creates");

        Project row = _db.Projects.IgnoreQueryFilters().AsNoTracking().Single(p => p.Id == project.Id);
        (row.Name, row.Description, row.OwnerId, row.OrganizationId).Should().Be(("Bakery opening", "Phone clips", UserId, TestTenancy.OrgOf(UserId)));
    }

    [Fact]
    public async Task The_chosen_recipe_is_recorded_as_template_chosen_with_the_catalog_key()
    {
        ProjectResponse project = Created(await Controller().Create(
            new CreateProjectRequest("p", null, StarterTemplateKey: "edit-my-video"), CancellationToken.None));

        _log.Rows.Select(r => (r.Event, r.Detail, r.Source, r.UserId, r.OrganizationId)).Should().Equal(
            ("activation.project_created", null, "server", UserId, TestTenancy.OrgOf(UserId)),
            ("product.template_chosen", "edit-my-video", "server", UserId, TestTenancy.OrgOf(UserId)));
        project.Id.Should().NotBeEmpty();

        // A second project: project_created is once per user, the choice is recorded every time.
        Created(await Controller().Create(new CreateProjectRequest("q", null, StarterTemplateKey: "quick-win-promo"), CancellationToken.None));
        _log.Rows.Select(r => (r.Event, r.Detail)).Should().Equal(
            ("activation.project_created", null),
            ("product.template_chosen", "edit-my-video"),
            ("product.template_chosen", "quick-win-promo"));
    }

    [Fact]
    public async Task Asking_for_the_automatic_starter_by_key_creates_it_once_even_with_the_auto_starter_switched_off()
    {
        ProjectResponse on = Created(await Controller().Create(
            new CreateProjectRequest("p", null, StarterTemplateKey: WorkflowTemplateCatalog.StarterTemplateKey), CancellationToken.None));
        Apps(on.Id).Single().TemplateKey.Should().Be(WorkflowTemplateCatalog.StarterTemplateKey);

        // The switch stops the AUTOMATIC starter; a person who asks for the recipe still gets it.
        WorkflowTemplateProvisioningService noAuto = Provisioning(autoStarter: false);
        ProjectResponse off = Created(await Controller(noAuto).Create(
            new CreateProjectRequest("q", null, StarterTemplateKey: WorkflowTemplateCatalog.StarterTemplateKey), CancellationToken.None));
        Apps(off.Id).Single().TemplateKey.Should().Be(WorkflowTemplateCatalog.StarterTemplateKey);
        Apps(Created(await Controller(noAuto).Create(new CreateProjectRequest("r", null), CancellationToken.None)).Id).Should().BeEmpty();
    }

    [Fact]
    public void Each_material_has_its_own_starter_recipe_and_an_idea_needs_no_paid_generation()
    {
        AcquisitionVocabulary.StarterTemplateKeyFor("footage").Should().Be("edit-my-video");
        AcquisitionVocabulary.StarterTemplateKeyFor("code").Should().Be(WorkflowTemplateCatalog.StarterTemplateKey);
        AcquisitionVocabulary.StarterTemplateKeyFor("idea").Should().Be("animation-from-idea");
        AcquisitionVocabulary.StarterTemplateKeyFor("other").Should().BeNull();
    }

    [Fact]
    public async Task Every_starter_recipe_the_welcome_questions_can_return_is_accepted()
    {
        foreach (string material in AcquisitionVocabulary.Materials)
        {
            string key = AcquisitionVocabulary.StarterTemplateKeyFor(material)!;
            ProjectResponse project = Created(await Controller().Create(
                new CreateProjectRequest("for " + material, null, StarterTemplateKey: key), CancellationToken.None));
            Apps(project.Id).Single().TemplateKey.Should().Be(key);
        }
    }

    [Theory]
    [InlineData("no-such-recipe")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("Video-Derush-Edit")]
    [InlineData(" video-derush-edit")]
    [InlineData("video-derush-edit ")]
    [InlineData("market:6f1c0c2e-5c3b-4f0e-9a51-0d6c2e8a7b11")]
    // Hidden: superseded by avatar-clip, never listed, whether or not avatars are on.
    [InlineData("spokesperson-clip")]
    [InlineData("talking-avatar-budget")]
    // Listed only while Avatars:Enabled is on; it is off here.
    [InlineData("avatar-clip")]
    [InlineData("avatar-explainer")]
    public async Task A_key_that_is_not_a_listed_recipe_is_400_and_creates_nothing(string key)
    {
        ActionResult<ProjectResponse> result = await Controller().Create(
            new CreateProjectRequest("p", null, StarterTemplateKey: key), CancellationToken.None);

        BadRequestObjectResult refused = result.Result.Should().BeOfType<BadRequestObjectResult>().Subject;
        using JsonDocument body = JsonDocument.Parse(JsonSerializer.Serialize(refused.Value));
        body.RootElement.GetProperty("code").GetString().Should().Be(ProjectCreationService.UnknownStarterTemplateCode);
        body.RootElement.GetProperty("code").GetString().Should().Be("STARTER_TEMPLATE_UNKNOWN");
        body.RootElement.GetProperty("message").GetString().Should().Be(
            "That starting recipe is not available.", "a fixed sentence: the caller's text is never echoed");

        _db.Projects.IgnoreQueryFilters().Should().BeEmpty();
        _db.WorkflowDefinitions.Should().BeEmpty();
        _db.ApplicationUsers.Should().BeEmpty("not even the caller's user row is written before the key is checked");
        _log.Rows.Should().BeEmpty();
    }

    [Fact]
    public async Task The_avatar_switch_decides_whether_an_avatar_recipe_may_start_a_project()
    {
        ProjectResponse project = Created(await Controller(Provisioning(avatars: true)).Create(
            new CreateProjectRequest("p", null, StarterTemplateKey: WorkflowTemplateCatalog.AvatarClipTemplateKey), CancellationToken.None));
        Apps(project.Id).Single().TemplateKey.Should().Be(WorkflowTemplateCatalog.AvatarClipTemplateKey);

        // The hidden keys stay refused with avatars on: they are not listed, the apply route is where an old key is honoured.
        (await Controller(Provisioning(avatars: true)).Create(
            new CreateProjectRequest("q", null, StarterTemplateKey: WorkflowTemplateCatalog.SpokespersonClipTemplateKey), CancellationToken.None))
            .Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task With_advanced_templates_off_only_the_automatic_starter_may_start_a_project()
    {
        WorkflowTemplateProvisioningService basic = Provisioning(advanced: false);

        (await Controller(basic).Create(new CreateProjectRequest("p", null, StarterTemplateKey: "edit-my-video"), CancellationToken.None))
            .Result.Should().BeOfType<BadRequestObjectResult>("the apply route refuses a non-starter recipe with the switch off, and so does this");
        _db.Projects.IgnoreQueryFilters().Should().BeEmpty();

        ProjectResponse project = Created(await Controller(basic).Create(
            new CreateProjectRequest("q", null, StarterTemplateKey: WorkflowTemplateCatalog.StarterTemplateKey), CancellationToken.None));
        Apps(project.Id).Single().TemplateKey.Should().Be(WorkflowTemplateCatalog.StarterTemplateKey);
    }

    [Fact]
    public async Task Whatever_the_finder_returns_the_apply_path_creates()
    {
        foreach ((bool avatars, bool advanced) in new[] { (false, true), (true, true), (false, false), (true, false) })
        {
            WorkflowTemplateProvisioningService service = Provisioning(avatars, advanced);
            IReadOnlyList<WorkflowTemplateDefinition> listed = await service.ListRecipesAsync(CancellationToken.None);
            listed.Should().NotBeEmpty();
            foreach (WorkflowTemplateDefinition recipe in listed)
            {
                (await service.FindStarterRecipeAsync(recipe.Key, CancellationToken.None)).Should().BeSameAs(recipe);
                WorkflowDefinition? app = await service.CreateFromTemplateAsync(Guid.NewGuid(), recipe.Key, skipIfExists: true, CancellationToken.None);
                app!.TemplateKey.Should().Be(recipe.Key, "a listed recipe is never redirected to another one");
            }

            foreach (WorkflowTemplateDefinition other in WorkflowTemplateCatalog.GetAll().Where(t => listed.All(l => l.Key != t.Key)))
                (await service.FindStarterRecipeAsync(other.Key, CancellationToken.None)).Should().BeNull(other.Key);
        }

        (await Provisioning().FindStarterRecipeAsync(null, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task A_valid_starter_key_changes_no_other_refusal_and_the_public_api_input_carries_none()
    {
        // A folder the organization does not have: 404 as before, with or without a starter key.
        (await Controller().Create(
            new CreateProjectRequest("p", null, FolderId: Guid.NewGuid(), StarterTemplateKey: "edit-my-video"), CancellationToken.None))
            .Result.Should().BeOfType<NotFoundResult>();
        _db.Projects.IgnoreQueryFilters().Should().BeEmpty();
        _db.WorkflowDefinitions.Should().BeEmpty();

        // The public API's input carries no starter: its projects keep the automatic one.
        new ProjectCreationInput("n", null).StarterTemplateKey.Should().BeNull();
        JsonSerializer.Deserialize<CreateProjectRequest>(
            "{\"name\":\"n\",\"description\":null}", new JsonSerializerOptions(JsonSerializerDefaults.Web))!
            .StarterTemplateKey.Should().BeNull("a body without the field means no choice");
        JsonSerializer.Deserialize<CreateProjectRequest>(
            "{\"name\":\"n\",\"description\":null,\"starterTemplateKey\":\"edit-my-video\"}", new JsonSerializerOptions(JsonSerializerDefaults.Web))!
            .StarterTemplateKey.Should().Be("edit-my-video");
    }
}
