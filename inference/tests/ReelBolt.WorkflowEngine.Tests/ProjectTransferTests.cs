using Microsoft.EntityFrameworkCore.Diagnostics;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// WP A10, transfer half: <c>POST /api/v1/projects/{id}/transfer</c> moves a project between
/// organizations only when the caller administers BOTH, and refuses with the list of provider
/// references the move would strand rather than silently repointing them.
/// </summary>
public sealed class ProjectTransferTests : IDisposable
{
    private static readonly Guid SourceOrg = Guid.Parse("11111111-0000-0000-0000-000000000001");
    private static readonly Guid TargetOrg = Guid.Parse("22222222-0000-0000-0000-000000000002");

    private readonly SqliteConnection _connection;
    private readonly InferenceApiDbContext _db;
    private readonly Guid _userId = Guid.NewGuid();
    private readonly FailProjectFileReads _readFailure = new();

    public ProjectTransferTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        using (SqliteCommand command = _connection.CreateCommand())
        {
            command.CommandText = "PRAGMA foreign_keys = OFF;";
            command.ExecuteNonQuery();
        }

        _db = new Avatars.SqliteLibraryDb(new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseSqlite(_connection)
            .EnableServiceProviderCaching(false)
            .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
            .AddInterceptors(_readFailure)
            .Options);
        _db.Database.EnsureCreated();

        // workflow_definitions/workflow_steps are ExcludeFromMigrations() references in this context,
        // so EnsureCreated does not create them — the engine context owns their schema. The transfer
        // path reads them (the dangling-reference sweep), so they must exist here.
        using var engineDb = new ReelBolt.WorkflowEngine.Data.WorkflowEngineDbContext(
            new DbContextOptionsBuilder<ReelBolt.WorkflowEngine.Data.WorkflowEngineDbContext>()
                .UseSqlite(_connection)
                .Options);
        engineDb.GetService<IRelationalDatabaseCreator>().CreateTables();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task Delete_is_refused_while_the_output_marker_is_set_and_allowed_once_it_is_cleared()
    {
        Project project = SeedProject();
        project.CsamOutputHoldAt = DateTime.UtcNow.AddHours(-1);
        _db.SaveChanges();

        IActionResult refused = await Controller(OrganizationRole.Owner).Delete(project.Id, CancellationToken.None);

        refused.Should().BeOfType<ConflictObjectResult>();
        object body = ((ConflictObjectResult)refused).Value!;
        body.GetType().GetProperty("code")!.GetValue(body).Should().Be("PROJECT_UNAVAILABLE");
        body.GetType().GetProperty("message")!.GetValue(body).Should().Be("This project cannot be deleted right now.");
        (await _db.Projects.IgnoreQueryFilters().CountAsync(p => p.Id == project.Id)).Should().Be(1);

        project.CsamOutputHoldAt = null;
        _db.SaveChanges();

        (await Controller(OrganizationRole.Owner).Delete(project.Id, CancellationToken.None)).Should().BeOfType<NoContentResult>();
        (await _db.Projects.IgnoreQueryFilters().CountAsync(p => p.Id == project.Id)).Should().Be(0);
    }

    [Fact]
    public async Task Delete_is_refused_when_the_output_marker_cannot_be_read()
    {
        Project project = SeedProject();
        ProjectsController controller = Controller(OrganizationRole.Owner);
        _readFailure.Armed = true;

        IActionResult refused = await controller.Delete(project.Id, CancellationToken.None);

        _readFailure.Armed = false;
        refused.Should().BeOfType<ConflictObjectResult>();
        (await _db.Projects.IgnoreQueryFilters().CountAsync(p => p.Id == project.Id)).Should().Be(1);
    }

    [Fact]
    public async Task Transfer_is_refused_while_a_library_copy_of_the_projects_avatar_exists_and_allowed_once_it_is_removed()
    {
        Project project = SeedProject();
        const string digest = "aa11bb22cc33dd44ee55ff6600778899aa11bb22cc33dd44ee55ff6600778899";
        _db.ProjectFiles.Add(new ProjectFile
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            OriginalFileName = "a.png",
            OriginalPath = "a.png",
            MimeType = "image/png",
            StorageKey = $"projects/{project.Id}/userFiles/a.png",
            StorageBucket = "b",
            Category = "userFiles",
            ContentSha256 = digest,
            CsamStatus = CsamScanStatus.Clean,
            ModerationStatus = FileModerationStatus.Quarantined
        });
        var item = Avatars.AvatarLibraryPublishTests.SeedItem(SourceOrg, AvatarSubjectKind.Person, "Anna");
        item.RowVersion = 1; // SQLite has no xmin
        var reference = new AvatarLibraryReference
        {
            Id = Guid.NewGuid(),
            LibraryItemId = item.Id,
            Kind = AvatarReferenceKind.Image,
            Role = AvatarReferenceRole.Face,
            ContentSha256 = digest,
            StorageKey = $"organizations/{SourceOrg}/avatar-library/{item.Id}/x.png",
            MimeType = "image/png",
            FileName = "a.png",
            SizeBytes = 1
        };
        _db.AvatarLibraryItems.Add(item);
        _db.AvatarLibraryReferences.Add(reference);
        _db.SaveChanges();
        SeedMembership(TargetOrg, OrganizationRole.Admin);

        IActionResult refused = await Controller(OrganizationRole.Owner)
            .Transfer(project.Id, new TransferProjectRequest(TargetOrg), CancellationToken.None);

        ConflictObjectResult conflict = refused.Should().BeOfType<ConflictObjectResult>().Subject;
        conflict.Value!.GetType().GetProperty("code")!.GetValue(conflict.Value).Should().Be("PROJECT_HAS_LIBRARY_AVATAR");
        ((string)conflict.Value.GetType().GetProperty("message")!.GetValue(conflict.Value)!).Should().Contain("Remove that avatar from the workspace library");
        _db.ChangeTracker.Clear();
        (await _db.Projects.IgnoreQueryFilters().SingleAsync(p => p.Id == project.Id))
            .OrganizationId.Should().Be(SourceOrg, "a refused transfer writes nothing");

        _db.AvatarLibraryReferences.Remove(await _db.AvatarLibraryReferences.IgnoreQueryFilters().SingleAsync(r => r.Id == reference.Id));
        _db.AvatarLibraryItems.Remove(await _db.AvatarLibraryItems.IgnoreQueryFilters().SingleAsync(i => i.Id == item.Id));
        _db.SaveChanges();

        (await Controller(OrganizationRole.Owner).Transfer(project.Id, new TransferProjectRequest(TargetOrg), CancellationToken.None))
            .Should().BeOfType<OkObjectResult>();
        (await _db.Projects.IgnoreQueryFilters().SingleAsync(p => p.Id == project.Id)).OrganizationId.Should().Be(TargetOrg);
    }

    [Fact]
    public async Task Transfer_keeps_the_output_marker()
    {
        Project project = SeedProject();
        DateTime marker = DateTime.UtcNow.AddHours(-2);
        project.CsamOutputHoldAt = marker;
        _db.SaveChanges();
        SeedMembership(TargetOrg, OrganizationRole.Admin);

        await Controller(OrganizationRole.Owner).Transfer(project.Id, new TransferProjectRequest(TargetOrg), CancellationToken.None);

        Project moved = await _db.Projects.IgnoreQueryFilters().SingleAsync(p => p.Id == project.Id);
        moved.OrganizationId.Should().Be(TargetOrg);
        moved.CsamOutputHoldAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Transfer_moves_the_project_and_reports_the_new_organization()
    {
        Project project = SeedProject();
        SeedMembership(TargetOrg, OrganizationRole.Admin);

        IActionResult result = await Controller(OrganizationRole.Owner)
            .Transfer(project.Id, new TransferProjectRequest(TargetOrg), CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        ((OkObjectResult)result).Value.Should().BeOfType<TransferProjectResponse>()
            .Which.OrganizationId.Should().Be(TargetOrg);

        (await _db.Projects.IgnoreQueryFilters().SingleAsync(p => p.Id == project.Id))
            .OrganizationId.Should().Be(TargetOrg);
    }

    [Fact]
    public async Task Transfer_refuses_and_lists_a_provider_the_move_would_strand()
    {
        Project project = SeedProject();
        SeedMembership(TargetOrg, OrganizationRole.Owner);
        ModelCapabilityView stranded = SeedProvider("source-only", SourceOrg);
        SeedWorkflowWithVoiceoverStep(project.Id, stranded.Id);

        IActionResult result = await Controller(OrganizationRole.Admin)
            .Transfer(project.Id, new TransferProjectRequest(TargetOrg), CancellationToken.None);

        ConflictObjectResult conflict = result.Should().BeOfType<ConflictObjectResult>().Subject;
        conflict.Value!.ToString().Should().Contain("Transfer refused");

        IReadOnlyList<DanglingProviderReference> listed = DanglingOf(conflict);
        listed.Should().ContainSingle();
        listed[0].ProviderId.Should().Be(stranded.Id);
        listed[0].ProviderName.Should().Be("source-only");
        listed[0].Field.Should().Be("providerId");

        (await _db.Projects.IgnoreQueryFilters().SingleAsync(p => p.Id == project.Id))
            .OrganizationId.Should().Be(SourceOrg, "a refused transfer writes nothing");
    }

    [Fact]
    public async Task Transfer_ignores_a_platform_provider_and_one_the_target_already_owns()
    {
        Project project = SeedProject();
        SeedMembership(TargetOrg, OrganizationRole.Owner);
        ModelCapabilityView platform = SeedProvider("platform", organizationId: null);
        ModelCapabilityView targetOwned = SeedProvider("target-owned", TargetOrg);
        SeedWorkflowWithVoiceoverStep(project.Id, platform.Id);
        SeedWorkflowWithVoiceoverStep(project.Id, targetOwned.Id, stepOrderOffset: 10);

        IActionResult result = await Controller(OrganizationRole.Owner)
            .Transfer(project.Id, new TransferProjectRequest(TargetOrg), CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Transfer_is_refused_for_a_member_of_the_source_organization()
    {
        Project project = SeedProject();
        SeedMembership(TargetOrg, OrganizationRole.Owner);

        IActionResult result = await Controller(OrganizationRole.Member)
            .Transfer(project.Id, new TransferProjectRequest(TargetOrg), CancellationToken.None);

        result.Should().BeOfType<ForbidResult>();
        (await _db.Projects.IgnoreQueryFilters().SingleAsync(p => p.Id == project.Id))
            .OrganizationId.Should().Be(SourceOrg);
    }

    [Fact]
    public async Task Transfer_is_refused_when_the_caller_cannot_administer_the_target_organization()
    {
        Project project = SeedProject();
        // Membership exists but the role is too low, and the organization the caller is not a member
        // of at all must answer the same way — 404 either way (D5: no existence oracle).
        SeedMembership(TargetOrg, OrganizationRole.Member);

        (await Controller(OrganizationRole.Owner)
            .Transfer(project.Id, new TransferProjectRequest(TargetOrg), CancellationToken.None))
            .Should().BeOfType<NotFoundResult>();

        Project otherProject = SeedProject();
        SeedMembership(TargetOrg, OrganizationRole.Admin);

        (await Controller(OrganizationRole.Owner)
            .Transfer(otherProject.Id, new TransferProjectRequest(Guid.NewGuid()), CancellationToken.None))
            .Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Transfer_to_the_projects_current_organization_is_a_no_op_bad_request()
    {
        Project project = SeedProject();

        (await Controller(OrganizationRole.Owner)
            .Transfer(project.Id, new TransferProjectRequest(SourceOrg), CancellationToken.None))
            .Should().BeOfType<BadRequestObjectResult>();
    }

    // ------------------------------------------------------------------------------------------

    private sealed class FakeCurrentUser(Guid userId, Guid organizationId, OrganizationRole role) : ICurrentUser
    {
        public Guid UserId { get; } = userId;
        public string Email { get; } = "test@example.com";
        public bool IsAuthenticated { get; } = true;
        public bool IsPlatformAdmin { get; } = false;
        public Guid? OrganizationId { get; } = organizationId;
        public OrganizationRole? OrgRole { get; } = role;
    }

    private ProjectsController Controller(OrganizationRole role)
    {
        var user = new FakeCurrentUser(_userId, SourceOrg, role);

        return new ProjectsController(
            _db,
            user,
            new ReelBolt.Inference.Api.Services.Auth.ProjectAccessService(_db, user),
            new WorkflowTemplateProvisioningService(_db, NullLogger<WorkflowTemplateProvisioningService>.Instance, new ConfigurationBuilder().Build()));
    }

    private static IReadOnlyList<DanglingProviderReference> DanglingOf(ConflictObjectResult conflict)
    {
        var property = conflict.Value!.GetType().GetProperty("danglingProviderReferences");
        property.Should().NotBeNull();
        return ((IEnumerable<DanglingProviderReference>)property!.GetValue(conflict.Value)!).ToList();
    }

    private Project SeedProject()
    {
        Project project = new()
        {
            Id = Guid.NewGuid(),
            Name = "p",
            OwnerId = _userId,
            OrganizationId = SourceOrg,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _db.Projects.Add(project);
        if (!_db.Organizations.Any(o => o.Id == TargetOrg))
        {
            _db.Organizations.Add(new Organization
            {
                Id = TargetOrg,
                Name = "Target",
                Slug = "target",
                Kind = OrganizationKind.Team,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
        }

        _db.SaveChanges();
        return project;
    }

    private void SeedMembership(Guid organizationId, OrganizationRole role)
    {
        _db.ChangeTracker.Clear();
        OrganizationMembership? existing = _db.OrganizationMemberships
            .FirstOrDefault(m => m.OrganizationId == organizationId && m.UserId == _userId);

        if (existing is not null)
        {
            existing.Role = role;
            _db.SaveChanges();
            return;
        }

        _db.OrganizationMemberships.Add(new OrganizationMembership
        {
            OrganizationId = organizationId,
            UserId = _userId,
            Role = role,
            CreatedAt = DateTime.UtcNow
        });
        _db.SaveChanges();
    }

    private ModelCapabilityView SeedProvider(string name, Guid? organizationId)
    {
        ModelCapabilityView provider = new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            Kind = InferenceProviderKind.FishAudio,
            Capability = InferenceProviderCapability.SpeechSynthesis,
            OrganizationId = organizationId,
            Endpoint = "https://provider.example",
            ModelName = "m1",
            IsEnabled = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _db.AddFlat(provider);
        _db.SaveChanges();
        return provider;
    }

    private void SeedWorkflowWithVoiceoverStep(Guid projectId, Guid providerId, int stepOrderOffset = 0)
    {
        Guid workflowId = Guid.NewGuid();
        _db.WorkflowDefinitions.Add(new WorkflowDefinition
        {
            Id = workflowId,
            Name = "wf",
            ProjectId = projectId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        _db.WorkflowSteps.Add(new WorkflowStep
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = workflowId,
            AgentDefinitionId = Guid.NewGuid(),
            StepOrder = 1 + stepOrderOffset,
            StepType = StepType.Voiceover,
            VoiceoverConfigJson = $$"""{"version":1,"source":{"kind":"Inline"},"providerId":"{{providerId}}"}"""
        });
        _db.SaveChanges();
    }
}
