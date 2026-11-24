using System;
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
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.DesignSystems;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Api;

/// <summary>
/// DS-3: a project that moves to another organization must not keep a design-system link to a library
/// of the organization it left (nothing in the database ties the two organizations together).
/// </summary>
public sealed class ProjectTransferLibraryLinkTests : IDisposable
{
    private static readonly Guid SourceOrg = Guid.Parse("11111111-0000-0000-0000-0000000000d3");
    private static readonly Guid TargetOrg = Guid.Parse("22222222-0000-0000-0000-0000000000d3");

    private readonly SqliteConnection _connection;
    private readonly InferenceApiDbContext _db;
    private readonly Guid _userId = Guid.NewGuid();

    public ProjectTransferLibraryLinkTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        using (SqliteCommand command = _connection.CreateCommand())
        {
            command.CommandText = "PRAGMA foreign_keys = OFF;";
            command.ExecuteNonQuery();
        }

        _db = new InferenceApiDbContext(new DbContextOptionsBuilder<InferenceApiDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        using var engineDb = new ReelBolt.WorkflowEngine.Data.WorkflowEngineDbContext(
            new DbContextOptionsBuilder<ReelBolt.WorkflowEngine.Data.WorkflowEngineDbContext>().UseSqlite(_connection).Options);
        engineDb.GetService<IRelationalDatabaseCreator>().CreateTables();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private sealed class FakeUser(Guid userId) : ICurrentUser
    {
        public Guid UserId { get; } = userId;
        public string Email { get; } = "u@example.com";
        public bool IsAuthenticated { get; } = true;
        public bool IsPlatformAdmin { get; } = false;
        public Guid? OrganizationId { get; } = SourceOrg;
        public OrganizationRole? OrgRole { get; } = OrganizationRole.Owner;
    }

    [Fact]
    public async Task Transfer_clears_the_projects_library_link()
    {
        var project = new Project { Id = Guid.NewGuid(), Name = "p", OwnerId = _userId, OrganizationId = SourceOrg };
        var container = new DesignSystem
        {
            Id = Guid.NewGuid(),
            ProjectId = null,
            Kind = DesignSystemKinds.Design,
            Name = "Brand",
            CreatedByUserId = _userId,
        };
        var library = new DesignSystemLibrary
        {
            Id = Guid.NewGuid(),
            OrganizationId = SourceOrg,
            Name = "Brand",
            DesignSystemId = container.Id,
            CreatedByUserId = _userId,
        };
        var projectSystem = new DesignSystem
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Kind = DesignSystemKinds.Design,
            Name = "p",
            ParentLibraryId = library.Id,
            CreatedByUserId = _userId,
        };
        _db.Organizations.Add(new Organization { Id = TargetOrg, Name = "Target", Slug = "target", Kind = OrganizationKind.Team });
        _db.OrganizationMemberships.Add(new OrganizationMembership
        {
            OrganizationId = TargetOrg,
            UserId = _userId,
            Role = OrganizationRole.Owner,
        });
        _db.Projects.Add(project);
        _db.DesignSystems.AddRange(container, projectSystem);
        _db.DesignSystemLibraries.Add(library);
        _db.SaveChanges();

        var user = new FakeUser(_userId);
        var controller = new ProjectsController(
            _db, user, new ProjectAccessService(_db, user),
            new WorkflowTemplateProvisioningService(
                _db, NullLogger<WorkflowTemplateProvisioningService>.Instance, new ConfigurationBuilder().Build()));

        IActionResult result = await controller.Transfer(project.Id, new TransferProjectRequest(TargetOrg), CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        _db.ChangeTracker.Clear();
        _db.DesignSystems.Single(s => s.Id == projectSystem.Id).ParentLibraryId.Should().BeNull();
        _db.DesignSystemLibraries.Should().ContainSingle("the source organization's library itself is untouched");
    }
}
