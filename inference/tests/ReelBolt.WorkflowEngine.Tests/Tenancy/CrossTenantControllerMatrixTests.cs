using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Amazon.S3;
using AwesomeAssertions;
using MassTransit;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Background;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.DesignSystems;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Inference.Api.Services.VectorSearch;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Tenancy;
using ReelBolt.WorkflowEngine.Tests.Compute;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Tenancy;

/// <summary>
/// WP A14, the behavioural surface matrix: <b>one project, every project-scoped controller, one
/// answer.</b>
///
/// <para>
/// Decision D5 has two halves and they are only meaningful together: a resource in another
/// organization is <c>404</c> (existence never leaks), and the same resource at home with too low a
/// role is <c>403</c> (the caller is told the truth about their own workspace). Asserting one without
/// the other is satisfiable by a controller that always 404s or always 403s, so every surface below
/// is driven twice with the SAME project id.
/// </para>
///
/// <para>
/// This is the seam the per-controller suites do not cover: <c>TimelinesControllerTests</c>,
/// <c>EditTimelinesControllerTests</c>, <c>StepResultArtifactsControllerTests</c>,
/// <c>ProjectVoicesControllerTests</c> and <c>ProjectAccessServiceTests</c> each prove their own
/// surface (and some of them prove it only for the cross-org half). What is asserted here is that the
/// rule is uniform across the whole surface, so a controller that stops consulting
/// <see cref="IProjectAccessService"/> — or consults it and inverts the failure — is caught even
/// though its own suite is not looking.
/// </para>
/// </summary>
public sealed class CrossTenantControllerMatrixTests : IDisposable
{
    private static readonly Guid HomeOrg = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid ForeignOrg = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    private readonly InferenceApiDbContext _db;
    private readonly Guid _ownerId = Guid.NewGuid();
    private readonly Guid _memberId = Guid.NewGuid();
    private readonly Guid _billingId = Guid.NewGuid();
    private readonly Guid _strangerId = Guid.NewGuid();
    private readonly Project _mine;
    private readonly Project _theirs;

    public CrossTenantControllerMatrixTests()
    {
        _db = new InferenceApiDbContext(new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase($"cross-tenant-matrix-{Guid.NewGuid():N}")
            .Options);

        _mine = Project(HomeOrg, _ownerId, "Mine");
        _theirs = Project(ForeignOrg, _strangerId, "Theirs");
        _db.Projects.AddRange(_mine, _theirs);
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    // ---------------------------------------------------------------------------------------------------
    // Harness
    // ---------------------------------------------------------------------------------------------------

    private sealed class FakeUser(Guid userId, Guid? organizationId, OrganizationRole? role) : ICurrentUser
    {
        public Guid UserId { get; } = userId;
        public string Email { get; } = "u@example.com";
        public bool IsAuthenticated { get; } = true;
        public bool IsPlatformAdmin { get; } = false;
        public Guid? OrganizationId { get; } = organizationId;
        public OrganizationRole? OrgRole { get; } = role;
    }

    private static Project Project(Guid organizationId, Guid ownerId, string name) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        OwnerId = ownerId,
        OrganizationId = organizationId,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    private static IConfiguration Config() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["MinIO:BucketName"] = "reelforge" })
            .Build();

    private WorkflowTemplateProvisioningService Templates() =>
        new(_db, NullLogger<WorkflowTemplateProvisioningService>.Instance, Config());

    /// <summary>
    /// A project-scoped controller action, reduced to "call it with this caller". The project id is
    /// supplied by the test so the foreign and home halves differ in exactly one value.
    /// </summary>
    private sealed record Surface(string Controller, Func<ICurrentUser, Guid, Task<object?>> Invoke);

    private ICurrentUser Member() => new FakeUser(_memberId, HomeOrg, OrganizationRole.Member);
    private ICurrentUser Billing() => new FakeUser(_billingId, HomeOrg, OrganizationRole.Billing);
    private ICurrentUser Stranger() => new FakeUser(_strangerId, ForeignOrg, OrganizationRole.Member);

    private IReadOnlyList<Surface> Surfaces() =>
    [
        new("Timelines.ListTimelines", (user, projectId) =>
            Wrap(new TimelinesController(_db, user, new ProjectAccessService(_db, user))
                .ListTimelines(projectId, CancellationToken.None))),

        new("Outputs.ListOutputs", (user, projectId) =>
            Wrap(new OutputsController(
                    _db, user, new ProjectAccessService(_db, user), Mock.Of<IAmazonS3>(),
                    Mock.Of<ReelBolt.Inference.Api.Services.Storage.IFileStorageService>(), Config())
                .ListOutputs(projectId, CancellationToken.None))),

        new("ProjectFiles.List", (user, projectId) =>
        {
            var controller = new ProjectFilesController(
                _db,
                user,
                new ProjectAccessService(_db, user),
                Mock.Of<IFileStorageService>(),
                new ChannelBackgroundTaskQueue<FileSummarizationTask>(10),
                Mock.Of<IPublishEndpoint>(),
                new VectorSearchQueryService(
                    Mock.Of<IEmbeddingGenerator<string, Embedding<float>>>(),
                    Mock.Of<IVectorIndexService>()))
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
            };
            return Wrap(controller.List(projectId, CancellationToken.None));
        }),

        // The delete dialog's "N pictures use this photo" count (decision D10): a View-role read, so the
        // D5 pair must hold for it like for the listing (a count is still information about the project).
        new("ProjectFiles.GetPictureUsage", (user, projectId) =>
            Wrap(new ProjectFilesController(
                    _db,
                    user,
                    new ProjectAccessService(_db, user),
                    Mock.Of<IFileStorageService>(),
                    new ChannelBackgroundTaskQueue<FileSummarizationTask>(10),
                    Mock.Of<IPublishEndpoint>(),
                    new VectorSearchQueryService(
                        Mock.Of<IEmbeddingGenerator<string, Embedding<float>>>(),
                        Mock.Of<IVectorIndexService>()))
                {
                    ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
                }
                .GetPictureUsage(projectId, Guid.NewGuid(), CancellationToken.None))),

        // The direct upload's two halves (docs/direct-upload.md). They are on this matrix because a
        // presigned URL is a capability to WRITE, so the same D5 pair has to hold for them as for every
        // other project-scoped surface: an upload ticket may not be minted for a project the caller
        // cannot edit, and a completion may not record a file against one.
        new("ProjectFiles.CreateUploadUrl", (user, projectId) =>
            Wrap(new ProjectFilesController(
                    _db,
                    user,
                    new ProjectAccessService(_db, user),
                    Mock.Of<IFileStorageService>(),
                    new ChannelBackgroundTaskQueue<FileSummarizationTask>(10),
                    Mock.Of<IPublishEndpoint>(),
                    new VectorSearchQueryService(
                        Mock.Of<IEmbeddingGenerator<string, Embedding<float>>>(),
                        Mock.Of<IVectorIndexService>()),
                    null,
                    new FakePresignedUrlService(),
                    ConfirmationTokenService.CreateEphemeral())
                {
                    ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
                }
                .CreateUploadUrl(projectId, new CreateDirectUploadRequest("clip.mp4", 4, "video/mp4"), CancellationToken.None))),

        new("ProjectFiles.CompleteUpload", (user, projectId) =>
            Wrap(new ProjectFilesController(
                    _db,
                    user,
                    new ProjectAccessService(_db, user),
                    Mock.Of<IFileStorageService>(),
                    new ChannelBackgroundTaskQueue<FileSummarizationTask>(10),
                    Mock.Of<IPublishEndpoint>(),
                    new VectorSearchQueryService(
                        Mock.Of<IEmbeddingGenerator<string, Embedding<float>>>(),
                        Mock.Of<IVectorIndexService>()),
                    null,
                    new FakePresignedUrlService(),
                    ConfirmationTokenService.CreateEphemeral())
                {
                    ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
                }
                .CompleteUpload(
                    projectId,
                    new CompleteDirectUploadRequest("token", "key", Guid.NewGuid(), "clip.mp4", 4, long.MaxValue),
                    CancellationToken.None))),

        new("ProjectFileMedia.Stream", (user, projectId) =>
            Wrap(new ProjectFileMediaController(
                    _db, user, new ProjectAccessService(_db, user), Mock.Of<IAmazonS3>(), Config())
                .Stream(projectId, Guid.NewGuid(), CancellationToken.None))),

        new("EditTimelines.ListEditTimelines", (user, projectId) =>
            Wrap(new EditTimelinesController(
                    _db, user, new ProjectAccessService(_db, user), Mock.Of<IFileStorageService>(),
                    Mock.Of<IWorkflowExecutionService>())
                .ListEditTimelines(projectId, null, CancellationToken.None))),

        new("ProjectPronunciation.List", (user, projectId) =>
            Wrap(new ProjectPronunciationController(
                    new ProjectAccessService(_db, user),
                    new ReelBolt.Inference.Api.Services.Pronunciation.ProjectPronunciationService(_db, new ReelBolt.WorkflowEngine.Tests.PronunciationTestGate()))
                .List(projectId, CancellationToken.None))),

        new("ProjectVoices.List", (user, projectId) =>
            Wrap(new ProjectVoicesController(
                    _db,
                    user,
                    new ProjectAccessService(_db, user),
                    Mock.Of<IFileStorageService>(),
                    Mock.Of<IInferenceProviderResolver>(),
                    Mock.Of<ISpeechSynthesisClientFactory>(),
                    Mock.Of<ITranscriptionClientFactory>(),
                    NullLogger<ProjectVoicesController>.Instance)
                .List(projectId, CancellationToken.None))),

        new("StepResultArtifacts.GetArtifact", (user, projectId) =>
            Wrap(new StepResultArtifactsController(
                    _db, user, new ProjectAccessService(_db, user), Mock.Of<IAmazonS3>(), Config())
                .GetArtifact(projectId, Guid.NewGuid(), CancellationToken.None))),

        new("Workflows.List", (user, projectId) =>
            Wrap(new WorkflowsController(
                    _db,
                    user,
                    new ProjectAccessService(_db, user),
                    Mock.Of<IPublishEndpoint>(),
                    Templates(),
                    new WorkflowExecutionService(_db, Mock.Of<IPublishEndpoint>(), NullLogger<WorkflowExecutionService>.Instance),
                    NullLogger<WorkflowsController>.Instance)
                .List(projectId, CancellationToken.None))),

        new("Projects.Get", (user, projectId) =>
            Wrap(new ProjectsController(
                    _db, user, new ProjectAccessService(_db, user), Templates())
                .Get(projectId, CancellationToken.None))),

        new("Styles.List", (user, projectId) =>
            Wrap(new StylesController(_db, user, new ProjectAccessService(_db, user))
                .List(projectId, CancellationToken.None))),

        // Design systems (SPEC 9): the version list resolves the project through IProjectAccessService
        // first (View), so a foreign project is 404 and a home Member is served an empty list.
        new("DesignSystems.ListVersions", (user, projectId) =>
            Wrap(new DesignSystemsController(
                    _db, user, new ProjectAccessService(_db, user),
                    new DesignSystemService(
                        _db, Mock.Of<IFileStorageService>(), Mock.Of<IPlanGateService>(),
                        NullLogger<DesignSystemService>.Instance),
                    Mock.Of<IDesignSystemIngestService>(),
                    NullLogger<DesignSystemsController>.Instance)
                .ListVersions(projectId, "design", CancellationToken.None)))
    ];

    /// <summary>
    /// Normalizes the two action-result shapes (<c>ActionResult</c> and <c>ActionResult&lt;T&gt;</c>)
    /// down to the <see cref="IActionResult"/> underneath, so one assertion serves every surface.
    /// </summary>
    private static async Task<object?> Wrap<T>(Task<T> action) => Unwrap(await action);

    private static object? Unwrap(object? result)
    {
        if (result is null)
        {
            return null;
        }

        System.Reflection.PropertyInfo? inner = result.GetType().GetProperty("Result");
        return inner?.GetValue(result) is { } value ? value : result;
    }

    private static bool IsNotFound(object? result) =>
        result is NotFoundResult
        || result is NotFoundObjectResult
        || (result is ObjectResult notFound && notFound.StatusCode == StatusCodes.Status404NotFound);

    private static bool IsForbidden(object? result) =>
        result is ForbidResult
        || (result is ObjectResult forbidden && forbidden.StatusCode == StatusCodes.Status403Forbidden);

    private static void ShouldBeNotFound(object? result, string surface) =>
        IsNotFound(result).Should().BeTrue(
            $"'{surface}' must answer a project in another organization with 404 — never 403, and never "
            + $"anything that confirms the project exists, but got {result?.GetType().Name ?? "null"}");

    private static void ShouldBeForbidden(object? result, string surface) =>
        IsForbidden(result).Should().BeTrue(
            $"'{surface}' must answer the caller's OWN project with 403 when their role is too low, so "
            + "the cross-organization 404 above is a real distinction and not a blanket refusal; got "
            + $"{result?.GetType().Name ?? "null"}");

    // ---------------------------------------------------------------------------------------------------
    // The matrix
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Every_project_scoped_controller_is_on_the_matrix()
    {
        string[] onTheMatrix = Surfaces().Select(s => s.Controller.Split('.')[0]).Distinct().ToArray();

        onTheMatrix.Should().BeEquivalentTo(
            [
                "DesignSystems", "EditTimelines", "Outputs", "ProjectFileMedia", "ProjectFiles", "ProjectPronunciation", "ProjectVoices",
                "Projects", "StepResultArtifacts", "Styles", "Timelines", "Workflows"
            ],
            "a project-scoped controller added without a row here would be invisible to this matrix");
    }

    [Fact]
    public async Task A_project_in_another_organization_is_404_on_every_surface()
    {
        foreach (Surface surface in Surfaces())
        {
            ShouldBeNotFound(await surface.Invoke(Stranger(), _mine.Id), surface.Controller);
            ShouldBeNotFound(await surface.Invoke(Member(), _theirs.Id), surface.Controller);
        }
    }

    /// <summary>
    /// The D5 pair, on one project id, driven twice. This is the assertion that cannot be satisfied by
    /// a controller that always refuses: the SAME surface, the SAME project, two callers.
    /// </summary>
    [Fact]
    public async Task The_same_project_is_403_at_home_and_404_across_the_organization()
    {
        foreach (Surface surface in HomeForbiddenSurfaces())
        {
            // Home, role too low: the caller learns the project is theirs to see but not to touch.
            ShouldBeForbidden(await surface.Invoke(Billing(), _mine.Id), surface.Controller);

            // Somewhere else, role irrelevant: they learn nothing at all.
            ShouldBeNotFound(await surface.Invoke(Stranger(), _mine.Id), surface.Controller);
        }
    }

    /// <summary>
    /// Every project-scoped surface except the two styles routes, which are covered on their own
    /// terms by <see cref="Styles_pin_collapses_an_insufficient_role_into_404_rather_than_403"/>.
    /// </summary>
    private IReadOnlyList<Surface> HomeForbiddenSurfaces() =>
        Surfaces().Where(s => s.Controller != "Styles.List").ToList();

    /// <summary>
    /// The one place on the project surface where the answer to "your own organization, your role is
    /// too low" is <c>404</c> rather than the <c>403</c> decision D5 prescribes. <c>StylesController</c>
    /// resolves the project through a boolean (<c>OwnsProjectAsync</c> tests for
    /// <see cref="ProjectAccessOutcome.Found"/>) instead of propagating the failure, so <c>Forbidden</c>
    /// and <c>NotFound</c> collapse into the same answer.
    ///
    /// <para>
    /// <b>This is a deviation, not a leak, and the difference matters.</b> A 404 is strictly stricter
    /// than a 403 — it tells a same-organization Billing caller less than the rule says they are
    /// entitled to know, and it never reveals a project across the organization boundary. It is pinned
    /// here rather than fixed: the change is a behaviour change to a controller outside this work
    /// package's scope, and asserting the current answer truthfully is worth more than asserting the
    /// answer the rule predicts and being wrong.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Styles_pin_collapses_an_insufficient_role_into_404_rather_than_403()
    {
        Surface styles = Surfaces().Single(s => s.Controller == "Styles.List");

        ShouldBeNotFound(await styles.Invoke(Billing(), _mine.Id), styles.Controller);
        ShouldBeNotFound(await styles.Invoke(Stranger(), _mine.Id), styles.Controller);

        // The role is not what is being refused here: a Member with the same project is served, so
        // the 404 above is about the role collapsing into the lookup, not about an always-refusing route.
        ShouldNotBeNotFound(await styles.Invoke(Member(), _mine.Id), styles.Controller);
    }

    /// <summary>
    /// Billing is the one role with no project access at all, and it must be refused uniformly — a
    /// single controller that forgot the role row would leak a project's contents to a role the matrix
    /// (docs/design/tenancy.md) says never sees one. The organization-level read is included because
    /// it is the surface with no project to hang the role check on.
    /// </summary>
    [Fact]
    public async Task Billing_has_no_project_access_on_any_surface()
    {
        foreach (Surface surface in HomeForbiddenSurfaces())
        {
            ShouldBeForbidden(await surface.Invoke(Billing(), _mine.Id), surface.Controller);
        }

        DashboardController dashboard = new(_db, Billing(), new ProjectAccessService(_db, Billing()));
        object? overview = Unwrap(await dashboard.Overview(CancellationToken.None));
        ShouldBeForbidden(overview, "Dashboard.Overview");

        DashboardController memberDashboard = new(_db, Member(), new ProjectAccessService(_db, Member()));
        Unwrap(await memberDashboard.Overview(CancellationToken.None)).Should().NotBeOfType<ForbidResult>(
            "a Member has project access, so the organization-level read must not be refused for them — "
            + "otherwise the Billing refusal above proves only that the endpoint always refuses");
    }

    /// <summary>
    /// The positive control: for the surfaces that need no seeded child row, the owner's own project
    /// is served and the stranger's is not, in one run. Without this, every assertion above would pass
    /// against a controller hard-wired to 404.
    /// </summary>
    [Fact]
    public async Task An_owned_project_is_served_and_a_foreign_one_is_not()
    {
        var ownedOnly = new HashSet<string>(StringComparer.Ordinal)
        {
            "DesignSystems.ListVersions",
            "Timelines.ListTimelines",
            "Outputs.ListOutputs",
            "ProjectFiles.List",
            "ProjectFiles.CreateUploadUrl",
            "ProjectFiles.CompleteUpload",
            "EditTimelines.ListEditTimelines",
            "ProjectVoices.List",
            "ProjectPronunciation.List",
            "Workflows.List",
            "Projects.Get",
            "Styles.List"
        };

        foreach (Surface surface in Surfaces().Where(s => ownedOnly.Contains(s.Controller)))
        {
            object? mine = await surface.Invoke(Member(), _mine.Id);
            ShouldNotBeNotFound(mine, surface.Controller);

            object? theirs = await surface.Invoke(Member(), _theirs.Id);
            ShouldBeNotFound(theirs, surface.Controller);
        }
    }

    private static void ShouldNotBeNotFound(object? result, string surface) =>
        IsNotFound(result).Should().BeFalse(
            $"'{surface}' must serve a project the caller's organization owns, but got {result?.GetType().Name ?? "null"}");
}
