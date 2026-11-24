#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Inference.Api;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.History;
using ReelBolt.Inference.Api.Services.Projects;
using ReelBolt.Inference.Api.Services.PublicApi;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Merges;

/// <summary>
/// The recipe-presets merge train resolved several conflicts by keeping both sides. These pin that both sides are
/// still wired from the real registrations (not only in hand-built test objects): the project-history ambient filter
/// beside the public API's invalid-model-state factory, the history eraser beside the project-creation service in
/// <see cref="ProjectsController"/>, and the assistant's video-enhance, marketplace, history and beat tools together.
/// </summary>
public sealed class RecipePresetsMergeWiringTests
{
    private static readonly Guid UserId = Guid.NewGuid();

    private static CompositionRootHarness Root(string name) =>
        CompositionRootHarness.Build(name, (services, configuration) => services.AddInferenceApiServices(configuration));

    private static ICurrentUser User()
    {
        Mock<ICurrentUser> u = new();
        u.SetupGet(x => x.UserId).Returns(UserId);
        u.SetupGet(x => x.Email).Returns("t@example.com");
        u.SetupGet(x => x.IsAuthenticated).Returns(true);
        u.SetupGet(x => x.EmailVerified).Returns(true);
        u.SetupGet(x => x.OrganizationId).Returns(TestTenancy.OrgOf(UserId));
        u.SetupGet(x => x.OrgRole).Returns(OrganizationRole.Owner);
        return u.Object;
    }

    private static T? Field<T>(object owner, string name) where T : class =>
        owner.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(owner) as T;

    [Fact]
    public void The_history_filter_and_the_public_invalid_model_state_factory_are_both_registered()
    {
        using CompositionRootHarness root = Root("merge-mvc-options");

        MvcOptions mvc = root.Services.GetRequiredService<IOptions<MvcOptions>>().Value;
        IEnumerable<Type> serviceFilters = mvc.Filters.OfType<ServiceFilterAttribute>().Select(f => f.ServiceType);
        serviceFilters.Should().Contain(typeof(ReelBolt.Inference.Api.Middleware.CreditsExhaustedResultFilter))
            .And.Contain(typeof(ProjectHistoryAmbientFilter), "the project-history side of the merge adds the ambient capture filter");

        ApiBehaviorOptions api = root.Services.GetRequiredService<IOptions<ApiBehaviorOptions>>().Value;
        api.InvalidModelStateResponseFactory.Method.DeclaringType.Should().Be(typeof(PublicApiAuthResponses),
            "the public-api side of the merge answers a bad public request in its own error envelope");
        api.InvalidModelStateResponseFactory.Method.Name.Should().Be(nameof(PublicApiAuthResponses.InvalidModelState));
    }

    [Fact]
    public void ProjectsController_from_the_real_registrations_gets_the_history_eraser_and_the_creation_service()
    {
        using CompositionRootHarness root = Root("merge-projects-controller");
        using IServiceScope scope = root.Services.CreateScope();

        ProjectsController controller = ActivatorUtilities.CreateInstance<ProjectsController>(scope.ServiceProvider);

        Field<IProjectHistoryEraser>(controller, "_historyEraser").Should().NotBeNull("project versioning's eraser is resolved, not defaulted to null");
        Field<IProjectCreationService>(controller, "_projectCreation").Should().NotBeNull("the public API's shared creation path is resolved");
        Field<ReelBolt.Inference.Api.Services.Storage.IObjectListing>(controller, "_objectListing").Should().NotBeNull();
        Field<object>(controller, "_logger").Should().NotBeOfType<NullLogger<ProjectsController>>("the real logger is injected");
    }

    private static InferenceApiDbContext NewDb() =>
        new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static Project Seed(InferenceApiDbContext db)
    {
        Project project = new()
        {
            Id = Guid.NewGuid(),
            Name = "Reel",
            OwnerId = UserId,
            OrganizationId = TestTenancy.OrgOf(UserId),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.Projects.Add(project);
        db.SaveChanges();
        return project;
    }

    private static WorkflowTemplateProvisioningService Provisioning(InferenceApiDbContext db) =>
        new(db, NullLogger<WorkflowTemplateProvisioningService>.Instance, new ConfigurationBuilder().Build());

    [Fact]
    public async Task Delete_erases_the_history_objects_first_and_a_failed_erase_keeps_the_project()
    {
        using InferenceApiDbContext db = NewDb();
        ICurrentUser user = User();
        Project kept = Seed(db);
        Project gone = Seed(db);

        Mock<IProjectHistoryEraser> failing = new();
        failing.Setup(e => e.DeleteProjectObjectsAsync(kept.Id, It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("s3 down"));
        ProjectsController refused = new(db, user, TestTenancy.Access(db, user), Provisioning(db),
            historyEraser: failing.Object, logger: NullLogger<ProjectsController>.Instance, projectCreation: null, objectListing: null);

        IActionResult result = await refused.Delete(kept.Id, CancellationToken.None);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
        (await db.Projects.AnyAsync(p => p.Id == kept.Id)).Should().BeTrue("the row stays so a retry can still find the history objects");

        Mock<IProjectHistoryEraser> eraser = new();
        ProjectsController deleting = new(db, user, TestTenancy.Access(db, user), Provisioning(db), historyEraser: eraser.Object);

        (await deleting.Delete(gone.Id, CancellationToken.None)).Should().BeOfType<NoContentResult>();
        eraser.Verify(e => e.DeleteProjectObjectsAsync(gone.Id, It.IsAny<CancellationToken>()), Times.Once);
        (await db.Projects.AnyAsync(p => p.Id == gone.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task Create_goes_through_the_injected_creation_service_beside_the_history_eraser()
    {
        using InferenceApiDbContext db = NewDb();
        ICurrentUser user = User();
        Mock<IProjectCreationService> creation = new();
        creation.Setup(c => c.CreateAsync(It.IsAny<ProjectCreationInput>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProjectCreationResult(null, new ConflictObjectResult(new { code = "FROM_THE_SERVICE" })));
        ProjectsController controller = new(db, user, TestTenancy.Access(db, user), Provisioning(db),
            historyEraser: new Mock<IProjectHistoryEraser>().Object, projectCreation: creation.Object);

        ActionResult<ProjectResponse> result = await controller.Create(new CreateProjectRequest("My reel", null), CancellationToken.None);

        result.Result.Should().BeOfType<ConflictObjectResult>("the refusal is the injected service's own");
        creation.Verify(c => c.CreateAsync(It.Is<ProjectCreationInput>(i => i.Name == "My reel"), It.IsAny<CancellationToken>()), Times.Once);
        (await db.Projects.CountAsync()).Should().Be(0, "the controller's fallback creation path was not used");
    }

    [Fact]
    public void The_assistant_tools_from_the_real_registrations_carry_every_merged_surface()
    {
        using CompositionRootHarness root = Root("merge-assistant-tools");
        using IServiceScope scope = root.Services.CreateScope();
        IServiceProvider sp = scope.ServiceProvider;

        AssistantToolProvider provider = AssistantToolProviderFactory.Create(
            sp.GetRequiredService<InferenceApiDbContext>(),
            sp.GetRequiredService<IWorkflowEditDiffService>(),
            sp.GetRequiredService<IWorkflowExecutionService>(),
            sp.GetRequiredService<IAssistantExecutionGuard>(),
            User(),
            sp);

        List<string> names = provider.GetTools(new AssistantToolContext(UserId, ProjectId: Guid.NewGuid(), ThreadId: Guid.NewGuid()))
            .Select(t => t.Name).ToList();

        names.Should().Contain(["ProposeEnhanceVideo", "ApplyEnhanceVideo"], "video enhance");
        names.Should().Contain(["SearchMarketplaceRecipes", "GetMarketplaceRecipe", "ProposeCopyMarketplaceRecipe", "ApplyCopyMarketplaceRecipe"], "community recipes");
        names.Should().Contain(["ListProjectVersions", "GetProjectChanges", "ProposeSaveProjectVersion", "ApplySaveProjectVersion",
            "ProposeRestoreProjectVersion", "ApplyRestoreProjectVersion"], "project history");
        names.Should().Contain(["ListSocialAccounts", "ListScheduledPosts", "GetPostPerformance", "ProposeSchedulePost", "ApplySchedulePost",
            "ProposeCancelScheduledPost", "ApplyCancelScheduledPost"], "social publishing");
        names.Should().Contain("GetMusicBeats", "cut to the music");
        names.Should().OnlyHaveUniqueItems();
        names.Should().Contain(["ListAvatarLibrary", "ProposeCreateObjectAvatar", "ApplyCreateObjectAvatar", "ProposeGenerateSubjectClip",
            "ApplyGenerateSubjectClip"], "avatars and objects");
        names.Should().HaveCount(153, "the documented chat surface (CLAUDE.md, docs/platform-assistant-mcp.md) with a thread");
    }
}
