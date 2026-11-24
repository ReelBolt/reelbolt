using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.DesignSystems;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.DesignSystems;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Tenancy;

/// <summary>
/// P4-04 tenancy coverage for the org-scoped <see cref="OrgDesignLibrariesController"/> (SPEC 16.1):
/// the role×action matrix (Billing 403 everywhere, Member read-only, Owner/Admin write), the no-org
/// and foreign-org 404s, the platform-admin-with-Member refusal, the delete confirm/link-null pair and
/// the org-scoped library resolver. Direct controller construction on an InMemory context, the same
/// harness <c>CrossTenantControllerMatrixTests</c> uses; a library id from another organization is a
/// 404, never a 403.
/// </summary>
public sealed class OrgLibraryTenancyTests
{
    private static readonly Guid OrgA = Guid.Parse("aaaaaaaa-0000-0000-0000-0000000000a4");
    private static readonly Guid OrgB = Guid.Parse("bbbbbbbb-0000-0000-0000-0000000000b4");

    // ---------------------------------------------------------------------------------------------
    // Role matrix
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("list")]
    [InlineData("one")]
    [InlineData("versions")]
    [InlineData("version")]
    [InlineData("export")]
    public async Task Read_actions_allow_owner_admin_member_and_forbid_billing(string action)
    {
        (await StatusAsync(action, Owner())).Should().Be(200);
        (await StatusAsync(action, Admin())).Should().Be(200);
        (await StatusAsync(action, Member())).Should().Be(200);
        (await StatusAsync(action, Billing())).Should().Be(403);
    }

    [Theory]
    [InlineData("create", 201)]
    [InlineData("rename", 200)]
    [InlineData("delete", 204)]
    [InlineData("createVersion", 201)]
    [InlineData("approve", 200)]
    [InlineData("reject", 200)]
    [InlineData("restore", 200)]
    public async Task Write_actions_allow_owner_and_admin_and_forbid_member_and_billing(string action, int ownerStatus)
    {
        (await StatusAsync(action, Owner())).Should().Be(ownerStatus);
        (await StatusAsync(action, Admin())).Should().Be(ownerStatus);
        (await StatusAsync(action, Member())).Should().Be(403);
        (await StatusAsync(action, Billing())).Should().Be(403);
    }

    // ---------------------------------------------------------------------------------------------
    // No active organization / foreign organization / nonexistent library
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("list")]
    [InlineData("one")]
    [InlineData("versions")]
    [InlineData("version")]
    [InlineData("export")]
    [InlineData("create")]
    [InlineData("rename")]
    [InlineData("delete")]
    [InlineData("createVersion")]
    [InlineData("approve")]
    [InlineData("reject")]
    [InlineData("restore")]
    public async Task No_active_organization_is_404_on_every_action(string action)
    {
        (await StatusAsync(action, new FakeUser(Guid.NewGuid(), null, null))).Should().Be(404);
        (await StatusAsync(action, new FakeUser(Guid.NewGuid(), null, OrganizationRole.Owner))).Should().Be(404);
    }

    [Theory]
    [InlineData("one")]
    [InlineData("versions")]
    [InlineData("version")]
    [InlineData("export")]
    [InlineData("rename")]
    [InlineData("delete")]
    [InlineData("createVersion")]
    [InlineData("approve")]
    [InlineData("reject")]
    [InlineData("restore")]
    public async Task Foreign_library_of_another_org_is_404_never_403(string action)
    {
        // The caller's active org is B; the seeded library id belongs to org A.
        (await StatusAsync(action, OrgBUser())).Should().Be(404);
    }

    [Theory]
    [InlineData("one")]
    [InlineData("versions")]
    [InlineData("version")]
    [InlineData("export")]
    [InlineData("rename")]
    [InlineData("delete")]
    [InlineData("createVersion")]
    [InlineData("approve")]
    [InlineData("reject")]
    [InlineData("restore")]
    public async Task Nonexistent_library_id_is_404(string action)
    {
        (await StatusAsync(action, Owner(), libraryIdOverride: Guid.NewGuid())).Should().Be(404);
    }

    /// <summary>
    /// The library resolver is org-scoped by construction: the P4-05 <c>PUT …/library-link</c> route
    /// reuses it, so a project may never be linked to another organization's library.
    /// </summary>
    [Fact]
    public async Task Library_resolver_returns_not_found_for_a_foreign_org_library_id()
    {
        Seeded seeded = Seed();
        try
        {
            OrgDesignLibrariesController controller = Controller(seeded.Db, OrgBUser());
            ActionResult<LibrarySummaryResponse> response = await controller.Get(seeded.LibraryId, CancellationToken.None);

            StatusOf(response.Result!).Should().Be(404);
            ((NotFoundObjectResult)response.Result!).Value.Should().NotBeNull();

            // The org A project keeps its link; the foreign read changed nothing.
            seeded.Db.ChangeTracker.Clear();
            seeded.Db.DesignSystems.Single(s => s.Id == seeded.ProjectSystemId).ParentLibraryId
                .Should().Be(seeded.LibraryId);
        }
        finally
        {
            seeded.Db.Dispose();
        }
    }

    // ---------------------------------------------------------------------------------------------
    // The service is itself the tenant guard: a hand-built scope cannot reach another org's container
    // ---------------------------------------------------------------------------------------------

    private static DesignSystemService ServiceFor(Seeded seeded) => new(
        seeded.Db, Mock.Of<IFileStorageService>(), Mock.Of<IPlanGateService>(),
        NullLogger<DesignSystemService>.Instance);

    [Fact]
    public async Task A_library_scope_built_with_a_foreign_org_finds_nothing()
    {
        Seeded seeded = Seed();
        try
        {
            DesignSystemService service = ServiceFor(seeded);
            DesignSystemScope forged = DesignSystemScope.Library(seeded.ContainerId, OrgB);

            (await service.GetSummaryAsync(forged, CancellationToken.None)).Should().BeNull();
            (await service.GetVersionAsync(forged, seeded.ApprovedVersionId, CancellationToken.None)).Should().BeNull();
            (await service.ListVersionsAsync(forged, CancellationToken.None)).Should().BeEmpty();
            (await service.ApproveAsync(forged, seeded.DraftVersionId, Guid.NewGuid(), CancellationToken.None)).Status
                .Should().Be(DesignSystemWriteStatus.NotFound);
            (await service.RestoreAsync(forged, seeded.ApprovedVersionId, Guid.NewGuid(), CancellationToken.None)).Status
                .Should().Be(DesignSystemWriteStatus.NotFound);

            seeded.Db.ChangeTracker.Clear();
            seeded.Db.DesignSystemVersions.Single(v => v.Id == seeded.DraftVersionId).State
                .Should().Be(DesignVersionState.Draft);
        }
        finally
        {
            seeded.Db.Dispose();
        }
    }

    [Fact]
    public async Task A_library_scope_with_the_owning_org_still_resolves()
    {
        Seeded seeded = Seed();
        try
        {
            DesignSystemService service = ServiceFor(seeded);
            Tenants.TryGetValue(seeded.Db, out TestTenancy.MutableTenant? tenant);
            tenant!.ActAsOrganization(OrgA);

            (await service.GetSummaryAsync(DesignSystemScope.Library(seeded.ContainerId, OrgA), CancellationToken.None))
                .Should().NotBeNull();
        }
        finally
        {
            seeded.Db.Dispose();
        }
    }

    [Fact]
    public async Task A_library_scope_aimed_at_a_project_row_finds_nothing()
    {
        Seeded seeded = Seed();
        try
        {
            DesignSystemScope scope = DesignSystemScope.Library(seeded.ProjectSystemId, OrgA);

            (await ServiceFor(seeded).GetSummaryAsync(scope, CancellationToken.None)).Should().BeNull();
        }
        finally
        {
            seeded.Db.Dispose();
        }
    }

    [Fact]
    public async Task A_project_scope_naming_another_projects_row_finds_nothing()
    {
        Seeded seeded = Seed();
        try
        {
            DesignSystemService service = ServiceFor(seeded);

            (await service.GetSummaryAsync(
                    DesignSystemScope.Project(seeded.ProjectSystemId, DesignSystemKinds.Design, Guid.NewGuid()),
                    CancellationToken.None))
                .Should().BeNull();
            (await service.GetSummaryAsync(
                    DesignSystemScope.Project(seeded.ProjectSystemId, DesignSystemKinds.Design, seeded.ProjectId),
                    CancellationToken.None))
                .Should().NotBeNull();
            (await service.GetSummaryAsync(
                    DesignSystemScope.Project(seeded.ContainerId, DesignSystemKinds.Design, seeded.ProjectId),
                    CancellationToken.None))
                .Should().BeNull("a library container is never a project's row");
        }
        finally
        {
            seeded.Db.Dispose();
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Platform admin with a Member active role
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("create")]
    [InlineData("rename")]
    [InlineData("delete")]
    [InlineData("createVersion")]
    [InlineData("approve")]
    [InlineData("reject")]
    [InlineData("restore")]
    public async Task Platform_admin_with_member_role_is_forbidden_on_writes(string action)
    {
        ICurrentUser platformMember = new FakeUser(Guid.NewGuid(), OrgA, OrganizationRole.Member, isPlatformAdmin: true);
        (await StatusAsync(action, platformMember)).Should().Be(403);
    }

    [Fact]
    public async Task Platform_admin_with_member_role_may_still_read()
    {
        ICurrentUser platformMember = new FakeUser(Guid.NewGuid(), OrgA, OrganizationRole.Member, isPlatformAdmin: true);
        (await StatusAsync("list", platformMember)).Should().Be(200);
    }

    // ---------------------------------------------------------------------------------------------
    // Delete: confirm and link nulling
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Delete_without_confirm_is_400_and_changes_nothing()
    {
        Seeded seeded = Seed();
        try
        {
            IActionResult result = await InvokeAsync("deleteNoConfirm", Controller(seeded.Db, Owner()), seeded);
            StatusOf(result).Should().Be(400);

            seeded.Db.ChangeTracker.Clear();
            seeded.Db.DesignSystemLibraries.Any(l => l.Id == seeded.LibraryId).Should().BeTrue();
            seeded.Db.DesignSystems.Single(s => s.Id == seeded.ProjectSystemId).ParentLibraryId
                .Should().Be(seeded.LibraryId);
            seeded.Db.DesignSystemVersions.Count(v => v.DesignSystemId == seeded.ContainerId).Should().Be(2);
        }
        finally
        {
            seeded.Db.Dispose();
        }
    }

    [Fact]
    public async Task Delete_with_confirm_is_204_and_nulls_the_project_links()
    {
        Seeded seeded = Seed();
        try
        {
            StatusOf(await InvokeAsync("delete", Controller(seeded.Db, Owner()), seeded)).Should().Be(204);

            seeded.Db.ChangeTracker.Clear();
            seeded.Db.DesignSystemLibraries.Any(l => l.Id == seeded.LibraryId).Should().BeFalse();
            seeded.Db.DesignSystems.Any(s => s.Id == seeded.ContainerId).Should().BeFalse();
            seeded.Db.DesignSystems.Single(s => s.Id == seeded.ProjectSystemId).ParentLibraryId.Should().BeNull();
        }
        finally
        {
            seeded.Db.Dispose();
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Create: invalid body writes nothing; a valid body writes a Manual Draft
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Create_with_invalid_document_is_400_with_errors_and_writes_nothing()
    {
        Seeded seeded = Seed();
        try
        {
            var invalid = new JsonObject
            {
                ["schema"] = DesignDocument.Schema,
                ["not-a-known-group"] = true,
            };

            ActionResult<LibrarySummaryResponse> response = await Controller(seeded.Db, Owner())
                .Create(new CreateLibraryRequest("Broken", invalid), CancellationToken.None);

            IActionResult result = response.Result!;
            StatusOf(result).Should().Be(400);

            JsonObject body = Body(((ObjectResult)result).Value);
            body["errors"].Should().NotBeNull();
            body["errors"]!.AsArray().Count.Should().BeGreaterThan(0);

            seeded.Db.DesignSystemLibraries.IgnoreQueryFilters().Count().Should().Be(2); // only the two seeded rows
            seeded.Db.DesignSystemVersions.Count().Should().Be(3);  // only the three seeded versions
        }
        finally
        {
            seeded.Db.Dispose();
        }
    }

    [Fact]
    public async Task Create_with_valid_document_is_201_and_creates_one_manual_draft()
    {
        Seeded seeded = Seed();
        try
        {
            ActionResult<LibrarySummaryResponse> response = await Controller(seeded.Db, Owner())
                .Create(new CreateLibraryRequest("New Brand", ValidDocument()), CancellationToken.None);

            IActionResult result = response.Result!;
            StatusOf(result).Should().Be(201);

            LibrarySummaryResponse created = ((ObjectResult)result).Value
                .Should().BeOfType<LibrarySummaryResponse>().Subject;
            created.Name.Should().Be("New Brand");
            created.OrganizationId.Should().Be(OrgA);

            DesignSystemVersion draft = seeded.Db.DesignSystemVersions
                .Single(v => v.DesignSystemId == created.DesignSystemId);
            draft.State.Should().Be(DesignVersionState.Draft);
            draft.Origin.Should().Be(DesignVersionOrigin.Manual);
            draft.VersionNumber.Should().Be(1);
        }
        finally
        {
            seeded.Db.Dispose();
        }
    }

    // ---------------------------------------------------------------------------------------------
    // A version is only reachable through its own library
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Version_of_another_library_is_404()
    {
        Seeded seeded = Seed();
        try
        {
            ActionResult<DesignVersionResponse> response = await Controller(seeded.Db, Owner())
                .GetVersion(seeded.LibraryId, seeded.ForeignVersionId, CancellationToken.None);

            StatusOf(response.Result!).Should().Be(404);
        }
        finally
        {
            seeded.Db.Dispose();
        }
    }

    [Fact]
    public async Task Unknown_version_id_on_this_library_is_404()
    {
        Seeded seeded = Seed();
        try
        {
            ActionResult<DesignVersionResponse> response = await Controller(seeded.Db, Owner())
                .GetVersion(seeded.LibraryId, Guid.NewGuid(), CancellationToken.None);

            StatusOf(response.Result!).Should().Be(404);
        }
        finally
        {
            seeded.Db.Dispose();
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Harness
    // ---------------------------------------------------------------------------------------------

    private sealed record Seeded(
        InferenceApiDbContext Db,
        Guid LibraryId,
        Guid ContainerId,
        Guid ApprovedVersionId,
        Guid DraftVersionId,
        Guid ProjectId,
        Guid ProjectSystemId,
        Guid ForeignLibraryId,
        Guid ForeignVersionId);

    private sealed class FakeUser(
        Guid userId, Guid? organizationId, OrganizationRole? role, bool isPlatformAdmin = false) : ICurrentUser
    {
        public Guid UserId { get; } = userId;
        public string Email { get; } = "u@example.com";
        public bool IsAuthenticated { get; } = true;
        public bool IsPlatformAdmin { get; } = isPlatformAdmin;
        public Guid? OrganizationId { get; } = organizationId;
        public OrganizationRole? OrgRole { get; } = role;
    }

    private static ICurrentUser Owner() => new FakeUser(Guid.NewGuid(), OrgA, OrganizationRole.Owner);
    private static ICurrentUser Admin() => new FakeUser(Guid.NewGuid(), OrgA, OrganizationRole.Admin);
    private static ICurrentUser Member() => new FakeUser(Guid.NewGuid(), OrgA, OrganizationRole.Member);
    private static ICurrentUser Billing() => new FakeUser(Guid.NewGuid(), OrgA, OrganizationRole.Billing);
    private static ICurrentUser OrgBUser() => new FakeUser(Guid.NewGuid(), OrgB, OrganizationRole.Owner);

    /// <summary>
    /// Each context carries a real <see cref="ITenantContext"/> (system while seeding), so the named
    /// "Tenant" filter on design_system_libraries is live in every controller call below instead of inert.
    /// </summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<InferenceApiDbContext, TestTenancy.MutableTenant> Tenants = new();

    private static InferenceApiDbContext NewDb()
    {
        var tenant = new TestTenancy.MutableTenant();
        var db = new InferenceApiDbContext(
            new DbContextOptionsBuilder<InferenceApiDbContext>()
                .UseInMemoryDatabase($"org-library-{Guid.NewGuid():N}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options,
            tenant);
        Tenants.Add(db, tenant);
        return db;
    }

    private static OrgDesignLibrariesController Controller(InferenceApiDbContext db, ICurrentUser user)
    {
        Tenants.TryGetValue(db, out TestTenancy.MutableTenant? tenant);
        tenant!.ActAsOrganization(user.OrganizationId);
        return new(
            db,
            user,
            new DesignSystemService(
                db, Mock.Of<IFileStorageService>(), Mock.Of<IPlanGateService>(),
                NullLogger<DesignSystemService>.Instance),
            NullLogger<OrgDesignLibrariesController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
    }

    private static async Task<int> StatusAsync(string action, ICurrentUser user, Guid? libraryIdOverride = null)
    {
        Seeded seeded = Seed();
        try
        {
            Seeded target = libraryIdOverride is { } libraryId ? seeded with { LibraryId = libraryId } : seeded;
            return StatusOf(await InvokeAsync(action, Controller(seeded.Db, user), target));
        }
        finally
        {
            seeded.Db.Dispose();
        }
    }

    private static async Task<IActionResult> InvokeAsync(
        string action, OrgDesignLibrariesController controller, Seeded seeded)
    {
        CancellationToken ct = CancellationToken.None;
        return action switch
        {
            "list" => (await controller.List(ct)).Result!,
            "one" => (await controller.Get(seeded.LibraryId, ct)).Result!,
            "versions" => (await controller.ListVersions(seeded.LibraryId, ct)).Result!,
            "version" => (await controller.GetVersion(seeded.LibraryId, seeded.DraftVersionId, ct)).Result!,
            "export" => await controller.Export(seeded.LibraryId, "json", ct),
            "create" => (await controller.Create(new CreateLibraryRequest("Created", null), ct)).Result!,
            "rename" => (await controller.Rename(seeded.LibraryId, new RenameLibraryRequest("Renamed"), ct)).Result!,
            "delete" => await controller.Delete(seeded.LibraryId, confirm: true, ct),
            "deleteNoConfirm" => await controller.Delete(seeded.LibraryId, confirm: false, ct),
            "createVersion" => (await controller.CreateVersion(
                seeded.LibraryId, new CreateDesignVersionRequest("design", ValidDocument()), ct)).Result!,
            "approve" => (await controller.Approve(seeded.LibraryId, seeded.DraftVersionId, ct)).Result!,
            "reject" => (await controller.Reject(
                seeded.LibraryId, seeded.DraftVersionId, new ApproveRejectRequest(null), ct)).Result!,
            "restore" => (await controller.Restore(seeded.LibraryId, seeded.ApprovedVersionId, ct)).Result!,
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
        };
    }

    private static int StatusOf(IActionResult result) => result switch
    {
        ForbidResult => StatusCodes.Status403Forbidden,
        IStatusCodeActionResult status => status.StatusCode ?? StatusCodes.Status200OK,
        FileResult => StatusCodes.Status200OK,
        _ => throw new InvalidOperationException($"Unexpected action result {result.GetType().Name}"),
    };

    private static JsonObject Body(object value) => JsonNode
        .Parse(JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)))!
        .AsObject();

    private static JsonObject ValidDocument() => new()
    {
        ["schema"] = DesignDocument.Schema,
        ["color"] = new JsonObject
        {
            ["primary"] = new JsonObject
            {
                ["$type"] = "color",
                ["$value"] = new JsonObject { ["hex"] = "#6d28d9", ["colorSpace"] = "srgb" },
            },
        },
    };

    private static DesignSystemVersion Version(Guid systemId, int number, DesignVersionState state, Guid userId) => new()
    {
        Id = Guid.NewGuid(),
        DesignSystemId = systemId,
        VersionNumber = number,
        State = state,
        DocumentJson = CanonicalJson.Serialize(ValidDocument()),
        DocumentDigest = CanonicalJson.Digest(ValidDocument()),
        Origin = DesignVersionOrigin.Manual,
        CreatedByUserId = userId,
        CreatedAt = DateTime.UtcNow,
        ApprovedByUserId = state == DesignVersionState.Approved ? userId : null,
        ApprovedAt = state == DesignVersionState.Approved ? DateTime.UtcNow : null,
    };

    /// <summary>
    /// Seeds one org A library (approved + draft versions), one org A project linked to it and one org
    /// B library used as the foreign id. Every test owns its fresh context.
    /// </summary>
    private static Seeded Seed()
    {
        InferenceApiDbContext db = NewDb();
        DateTime now = DateTime.UtcNow;
        Guid ownerId = Guid.NewGuid();

        var containerA = new DesignSystem
        {
            Id = Guid.NewGuid(),
            ProjectId = null,
            Kind = DesignSystemKinds.Design,
            Name = "Brand A",
            CreatedByUserId = ownerId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var libraryA = new DesignSystemLibrary
        {
            Id = Guid.NewGuid(),
            OrganizationId = OrgA,
            Name = "Brand A",
            DesignSystemId = containerA.Id,
            CreatedByUserId = ownerId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        DesignSystemVersion approvedA = Version(containerA.Id, 1, DesignVersionState.Approved, ownerId);
        DesignSystemVersion draftA = Version(containerA.Id, 2, DesignVersionState.Draft, ownerId);
        containerA.CurrentVersionId = approvedA.Id;

        var project = new Project
        {
            Id = Guid.NewGuid(),
            Name = "Project A",
            OwnerId = ownerId,
            OrganizationId = OrgA,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var projectSystem = new DesignSystem
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Kind = DesignSystemKinds.Design,
            Name = "Project A",
            ParentLibraryId = libraryA.Id,
            CreatedByUserId = ownerId,
            CreatedAt = now,
            UpdatedAt = now,
        };

        var containerB = new DesignSystem
        {
            Id = Guid.NewGuid(),
            ProjectId = null,
            Kind = DesignSystemKinds.Design,
            Name = "Brand B",
            CreatedByUserId = ownerId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var libraryB = new DesignSystemLibrary
        {
            Id = Guid.NewGuid(),
            OrganizationId = OrgB,
            Name = "Brand B",
            DesignSystemId = containerB.Id,
            CreatedByUserId = ownerId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        DesignSystemVersion approvedB = Version(containerB.Id, 1, DesignVersionState.Approved, ownerId);
        containerB.CurrentVersionId = approvedB.Id;

        db.Projects.Add(project);
        db.DesignSystems.AddRange(containerA, projectSystem, containerB);
        db.DesignSystemLibraries.AddRange(libraryA, libraryB);
        db.DesignSystemVersions.AddRange(approvedA, draftA, approvedB);
        db.SaveChanges();

        return new Seeded(
            db,
            libraryA.Id,
            containerA.Id,
            approvedA.Id,
            draftA.Id,
            project.Id,
            projectSystem.Id,
            libraryB.Id,
            approvedB.Id);
    }
}
