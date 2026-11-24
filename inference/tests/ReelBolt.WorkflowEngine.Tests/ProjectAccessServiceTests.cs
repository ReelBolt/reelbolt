using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>A7a: the project access service's role matrix, the tenant query filters and the controller guard.</summary>
public class ProjectAccessServiceTests
{
    private static readonly Guid OrgA = Guid.NewGuid();
    private static readonly Guid OrgB = Guid.NewGuid();

    private sealed class User(Guid userId, Guid? org, OrganizationRole? role) : ICurrentUser
    {
        public Guid UserId { get; } = userId;
        public string Email => "u@example.com";
        public bool IsAuthenticated => true;
        public bool IsPlatformAdmin { get; init; }
        public Guid? OrganizationId { get; } = org;
        public OrganizationRole? OrgRole { get; } = role;
    }

    private static InferenceApiDbContext CreateDb(TestTenancy.MutableTenant? tenant = null) =>
        new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, tenant);

    private static Project AddProject(InferenceApiDbContext db, Guid org, Guid owner)
    {
        Project p = new() { Id = Guid.NewGuid(), Name = "P", OwnerId = owner, OrganizationId = org, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.Projects.Add(p);
        db.SaveChanges();
        return p;
    }

    [Theory]
    [InlineData(OrganizationRole.Owner, ProjectPermission.View, false, true)]
    [InlineData(OrganizationRole.Owner, ProjectPermission.Edit, false, true)]
    [InlineData(OrganizationRole.Owner, ProjectPermission.Delete, false, true)]
    [InlineData(OrganizationRole.Admin, ProjectPermission.View, false, true)]
    [InlineData(OrganizationRole.Admin, ProjectPermission.Edit, false, true)]
    [InlineData(OrganizationRole.Admin, ProjectPermission.Delete, false, true)]
    [InlineData(OrganizationRole.Member, ProjectPermission.View, false, true)]
    [InlineData(OrganizationRole.Member, ProjectPermission.Edit, false, true)]
    [InlineData(OrganizationRole.Member, ProjectPermission.Delete, false, false)]
    [InlineData(OrganizationRole.Member, ProjectPermission.Delete, true, true)]
    [InlineData(OrganizationRole.Billing, ProjectPermission.View, false, false)]
    [InlineData(OrganizationRole.Billing, ProjectPermission.Edit, true, false)]
    [InlineData(OrganizationRole.Billing, ProjectPermission.Delete, true, false)]
    public void RoleMatrix(OrganizationRole role, ProjectPermission permission, bool isCreator, bool expected) =>
        ProjectAccessService.Allowed(role, permission, isCreator).Should().Be(expected);

    [Fact]
    public async Task OtherOrganization_IsNotFound_EvenForAnOwner()
    {
        using InferenceApiDbContext db = CreateDb();
        Project p = AddProject(db, OrgB, Guid.NewGuid());
        var svc = new ProjectAccessService(db, new User(Guid.NewGuid(), OrgA, OrganizationRole.Owner) { IsPlatformAdmin = true });

        (await svc.GetAsync(p.Id, ProjectPermission.View, CancellationToken.None)).Outcome.Should().Be(ProjectAccessOutcome.NotFound);
    }

    [Fact]
    public async Task Missing_And_NoActiveOrganization_AreNotFound()
    {
        using InferenceApiDbContext db = CreateDb();
        Project p = AddProject(db, OrgA, Guid.NewGuid());

        (await new ProjectAccessService(db, new User(Guid.NewGuid(), OrgA, OrganizationRole.Owner))
            .GetAsync(Guid.NewGuid(), ProjectPermission.View, CancellationToken.None)).Outcome.Should().Be(ProjectAccessOutcome.NotFound);
        (await new ProjectAccessService(db, new User(Guid.NewGuid(), null, null))
            .GetAsync(p.Id, ProjectPermission.View, CancellationToken.None)).Outcome.Should().Be(ProjectAccessOutcome.NotFound);
    }

    [Fact]
    public async Task SameOrganization_BillingIsForbidden_NotNotFound()
    {
        using InferenceApiDbContext db = CreateDb();
        Project p = AddProject(db, OrgA, Guid.NewGuid());
        var svc = new ProjectAccessService(db, new User(Guid.NewGuid(), OrgA, OrganizationRole.Billing));

        ProjectAccessResult r = await svc.GetAsync(p.Id, ProjectPermission.View, CancellationToken.None);
        r.Outcome.Should().Be(ProjectAccessOutcome.Forbidden);
        r.Failure.Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public async Task Member_MayDeleteOnlyProjectsTheyCreated_AdminMayDeleteAny()
    {
        using InferenceApiDbContext db = CreateDb();
        Guid member = Guid.NewGuid();
        Project mine = AddProject(db, OrgA, member);
        Project theirs = AddProject(db, OrgA, Guid.NewGuid());

        var asMember = new ProjectAccessService(db, new User(member, OrgA, OrganizationRole.Member));
        (await asMember.GetAsync(mine.Id, ProjectPermission.Delete, CancellationToken.None)).IsFound.Should().BeTrue();
        (await asMember.GetAsync(theirs.Id, ProjectPermission.Delete, CancellationToken.None)).Outcome.Should().Be(ProjectAccessOutcome.Forbidden);
        (await asMember.GetAsync(theirs.Id, ProjectPermission.Edit, CancellationToken.None)).IsFound.Should().BeTrue();

        var asAdmin = new ProjectAccessService(db, new User(Guid.NewGuid(), OrgA, OrganizationRole.Admin));
        (await asAdmin.GetAsync(theirs.Id, ProjectPermission.Delete, CancellationToken.None)).IsFound.Should().BeTrue();
    }

    [Fact]
    public void AuthorizeOrganization_DeniesBilling_AndRequiresAnOrganization()
    {
        using InferenceApiDbContext db = CreateDb();
        new ProjectAccessService(db, new User(Guid.NewGuid(), OrgA, OrganizationRole.Billing)).AuthorizeOrganization(ProjectPermission.View)
            .Should().Be(ProjectAccessOutcome.Forbidden);
        new ProjectAccessService(db, new User(Guid.NewGuid(), null, null)).AuthorizeOrganization(ProjectPermission.Edit)
            .Should().Be(ProjectAccessOutcome.NotFound);
        new ProjectAccessService(db, new User(Guid.NewGuid(), OrgA, OrganizationRole.Member)).AuthorizeOrganization(ProjectPermission.Edit)
            .Should().Be(ProjectAccessOutcome.Found);
    }

    // ---- the EF tenant filters ----

    [Fact]
    public void TenantFilters_ScopeToTheActiveOrganization_FailClosedWithoutOne_AndOpenInASystemScope()
    {
        var tenant = new TestTenancy.MutableTenant(); // system while seeding
        using InferenceApiDbContext db = CreateDb(tenant);

        Project a = AddProject(db, OrgA, Guid.NewGuid());
        Project b = AddProject(db, OrgB, Guid.NewGuid());
        db.ProjectFiles.AddRange(
            new ProjectFile { Id = Guid.NewGuid(), ProjectId = a.Id, OriginalFileName = "a", MimeType = "x" },
            new ProjectFile { Id = Guid.NewGuid(), ProjectId = b.Id, OriginalFileName = "b", MimeType = "x" });
        db.AgentDefinitions.AddRange(
            new AgentDefinition { Id = Guid.NewGuid(), Name = "builtin", IsBuiltIn = true },
            new AgentDefinition { Id = Guid.NewGuid(), Name = "a-custom", OrganizationId = OrgA },
            new AgentDefinition { Id = Guid.NewGuid(), Name = "b-custom", OrganizationId = OrgB });
        db.InferenceProviders.AddRange(
            new InferenceProvider { Id = Guid.NewGuid(), Name = "platform" },
            new InferenceProvider { Id = Guid.NewGuid(), Name = "a-byo", OrganizationId = OrgA },
            new InferenceProvider { Id = Guid.NewGuid(), Name = "b-byo", OrganizationId = OrgB });
        db.AssistantThreads.AddRange(
            new AssistantThread { Id = Guid.NewGuid(), UserId = Guid.NewGuid(), OrganizationId = OrgA },
            new AssistantThread { Id = Guid.NewGuid(), UserId = Guid.NewGuid(), OrganizationId = OrgB });
        db.EditTimelines.AddRange(
            new EditTimelineEntity { Id = Guid.NewGuid(), ProjectId = a.Id, Name = "live" },
            new EditTimelineEntity { Id = Guid.NewGuid(), ProjectId = a.Id, Name = "gone", DeletedAt = DateTime.UtcNow });
        db.SaveChanges();

        // System scope: everything (and the soft-delete filter still hides the deleted timeline).
        db.Projects.Count().Should().Be(2);
        db.EditTimelines.Select(t => t.Name).Should().Equal("live");

        tenant.ActAsOrganization(Guid.NewGuid()); // an org with nothing in it
        db.Projects.Count().Should().Be(0);

        tenant.ActAsOrganization(OrgA);
        db.Projects.Select(p => p.Id).Should().Equal(a.Id);
        db.ProjectFiles.Select(f => f.OriginalFileName).Should().Equal("a");
        db.AgentDefinitions.Select(x => x.Name).OrderBy(n => n).Should().Equal("a-custom", "builtin");
        db.InferenceProviders.Select(x => x.Name).OrderBy(n => n).Should().Equal("a-byo", "platform");
        db.AssistantThreads.Count().Should().Be(1);
        db.EditTimelines.Select(t => t.Name).Should().Equal("live");

        // No organization and no system scope: nothing tenant-owned, only shared rows.
        tenant.ActAsOrganization(null);
        db.Projects.Count().Should().Be(0);
        db.ProjectFiles.Count().Should().Be(0);
        db.AssistantThreads.Count().Should().Be(0);
        db.AgentDefinitions.Select(x => x.Name).Should().Equal("builtin");
        db.InferenceProviders.Select(x => x.Name).Should().Equal("platform");
    }

    // ---- guard: no owner-only checks left in the A7a controllers ----

    [Theory]
    [InlineData("ProjectsController.cs")]
    [InlineData("DashboardController.cs")]
    [InlineData("ProjectFilesController.cs")]
    [InlineData("ProjectFileMediaController.cs")]
    [InlineData("OutputsController.cs")]
    [InlineData("StepResultArtifactsController.cs")]
    public void A7aControllers_DoNotCompareTheOwnerToTheCaller(string file)
    {
        string dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "src", "ReelBolt.Inference.Api")))
            dir = Path.GetDirectoryName(dir)!;
        dir.Should().NotBeNull();

        string text = File.ReadAllText(Path.Combine(dir!, "src", "ReelBolt.Inference.Api", "Controllers", file));
        text.Should().NotContain("OwnerId != _currentUser.UserId");
        text.Should().NotContain("OwnerId == _currentUser.UserId");
        text.Should().NotContain("PersonalWorkspaceBridge");
    }
}
