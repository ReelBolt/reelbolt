using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.DesignSystems;
using System;
using System.Linq;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Data;

/// <summary>
/// SPEC 22 data-layer coverage for the four Phase-2 design-system entities. A relational provider
/// is used rather than InMemory on purpose: InMemory enforces neither unique indexes, foreign keys
/// nor check constraints, so it cannot answer the questions this file asks. Sqlite is an in-memory
/// database with real constraints, so the unique index, the cascade and the check constraint are
/// asserted against actual enforcement, and the column types are asserted against the model
/// metadata the migration is generated from.
/// </summary>
public class DesignSystemModelTests
{
    private static InferenceApiDbContext CreateContext()
    {
        var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        connection.Open();
        using (var cmd = connection.CreateCommand()) { cmd.CommandText = "PRAGMA foreign_keys = ON;"; cmd.ExecuteNonQuery(); }
        var options = new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseSqlite(connection)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        var db = new InferenceApiDbContext(options);
        db.Database.EnsureCreated();
        return db;
    }

    /// <summary>Sqlite enforces the user/organization foreign keys a project carries.</summary>
    private static Guid AddUserAndOrganization(InferenceApiDbContext db)
    {
        ApplicationUser user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            Email = $"owner-{Guid.NewGuid():N}@example.com",
            DisplayName = "Owner"
        };
        db.ApplicationUsers.Add(user);
        db.Organizations.Add(new Organization
        {
            Id = user.Id,
            Name = "Personal",
            Slug = "personal-" + user.Id.ToString("N"),
            Kind = ReelBolt.Shared.Tenancy.OrganizationKind.Personal,
            CreatedByUserId = user.Id
        });
        db.SaveChanges();
        return user.Id;
    }

    private static Project AddProject(InferenceApiDbContext db, Guid ownerId)
    {
        Project project = new Project
        {
            Id = Guid.NewGuid(),
            Name = "Design system project",
            OwnerId = ownerId,
            OrganizationId = ownerId,
        };
        db.Projects.Add(project);
        db.SaveChanges();
        return project;
    }

    [Fact]
    public void Inserting_a_second_system_for_the_same_project_and_kind_violates_the_unique_constraint()
    {
        using InferenceApiDbContext db = CreateContext();
        Guid ownerId = AddUserAndOrganization(db);
        Project project = AddProject(db, ownerId);

        db.DesignSystems.Add(new DesignSystem
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Kind = DesignSystemKinds.Design,
            Name = "First",
            CreatedByUserId = ownerId,
        });
        db.SaveChanges();

        DesignSystem second = new DesignSystem
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Kind = DesignSystemKinds.Design,
            Name = "Second",
            CreatedByUserId = ownerId,
        };

        Action save = () => { db.DesignSystems.Add(second); db.SaveChanges(); };
        save.Should().Throw<DbUpdateException>();
    }

    [Fact]
    public void Inserting_two_versions_with_the_same_number_violates_the_unique_constraint()
    {
        using InferenceApiDbContext db = CreateContext();
        Guid ownerId = AddUserAndOrganization(db);
        Project project = AddProject(db, ownerId);

        DesignSystem system = new DesignSystem
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Kind = DesignSystemKinds.Design,
            Name = "System",
            CreatedByUserId = ownerId,
        };
        db.DesignSystems.Add(system);
        db.DesignSystemVersions.Add(new DesignSystemVersion
        {
            Id = Guid.NewGuid(),
            DesignSystemId = system.Id,
            VersionNumber = 1,
            State = DesignVersionState.Draft,
            DocumentJson = "{}",
            DocumentDigest = new string('0', 64),
            Origin = DesignVersionOrigin.Manual,
            CreatedByUserId = ownerId,
        });
        db.SaveChanges();

        db.DesignSystemVersions.Add(new DesignSystemVersion
        {
            Id = Guid.NewGuid(),
            DesignSystemId = system.Id,
            VersionNumber = 1,
            State = DesignVersionState.Draft,
            DocumentJson = "{}",
            DocumentDigest = new string('1', 64),
            Origin = DesignVersionOrigin.Manual,
            CreatedByUserId = ownerId,
        });

        Action save = () => db.SaveChanges();
        save.Should().Throw<DbUpdateException>();
    }

    [Fact]
    public void Deleting_a_project_cascades_to_its_system_versions_sources_and_proposals()
    {
        using InferenceApiDbContext db = CreateContext();
        Guid ownerId = AddUserAndOrganization(db);
        Project project = AddProject(db, ownerId);

        DesignSystem system = new DesignSystem
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Kind = DesignSystemKinds.Design,
            Name = "System",
            CreatedByUserId = ownerId,
        };
        DesignSystemVersion version = new DesignSystemVersion
        {
            Id = Guid.NewGuid(),
            DesignSystemId = system.Id,
            VersionNumber = 1,
            State = DesignVersionState.Draft,
            DocumentJson = "{}",
            DocumentDigest = new string('0', 64),
            Origin = DesignVersionOrigin.Manual,
            CreatedByUserId = ownerId,
        };
        // EF refuses a single SaveChanges holding both halves of the circular FK (it cannot order
        // the two inserts), so the system is inserted first with no current version and the pointer
        // is set immediately after the version exists.
        db.DesignSystems.Add(system);
        db.DesignSystemVersions.Add(version);
        db.SaveChanges();

        system.CurrentVersionId = version.Id;
        db.DesignSystemSources.Add(new DesignSystemSource
        {
            Id = Guid.NewGuid(),
            DesignSystemId = system.Id,
            VersionId = version.Id,
            SourceKind = DesignSourceKind.Manual,
            ProjectFileId = null,
            Path = "design/tokens.json",
            Importer = "manual",
            ContentSha256 = new string('a', 64),
            SizeBytes = 42,
            Status = DesignSourceStatus.Current,
            LastCheckedAt = DateTime.UtcNow,
        });
        db.DesignSystemChangeProposals.Add(new DesignSystemChangeProposal
        {
            Id = Guid.NewGuid(),
            DesignSystemId = system.Id,
            BaseVersionId = version.Id,
            ProposedVersionId = version.Id,
            Trigger = DesignProposalTrigger.Manual,
            DriftScore = 0.5,
            DiffJson = "{}",
            Status = DesignProposalStatus.Pending,
            ProposedDigest = new string('0', 64),
        });
        db.SaveChanges();

        db.DesignSystems.Count().Should().Be(1);
        db.DesignSystemVersions.Count().Should().Be(1);
        db.DesignSystemSources.Count().Should().Be(1);
        db.DesignSystemChangeProposals.Count().Should().Be(1);

        // The same circular FK blocks EF's delete batch, so the pointer is cleared first.
        system.CurrentVersionId = null;
        db.SaveChanges();

        db.Projects.Remove(project);
        db.SaveChanges();

        db.DesignSystems.Count().Should().Be(0);
        db.DesignSystemVersions.Count().Should().Be(0);
        db.DesignSystemSources.Count().Should().Be(0);
        db.DesignSystemChangeProposals.Count().Should().Be(0);
    }

    [Fact]
    public void Design_system_columns_are_mapped_with_the_expected_types()
    {
        var options = new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        using InferenceApiDbContext db = new InferenceApiDbContext(options);

        // Check constraints live on the design-time model, not the read-optimized runtime one.
        var model = db.GetService<IDesignTimeModel>().Model;

        var versionType = model.FindEntityType(typeof(DesignSystemVersion));
        versionType.Should().NotBeNull();
        versionType!.FindProperty(nameof(DesignSystemVersion.DocumentDigest))!
            .GetColumnType().Should().Be("char(64)");
        versionType.FindProperty(nameof(DesignSystemVersion.DocumentJson))!
            .GetColumnType().Should().Be("jsonb");
        versionType.FindProperty(nameof(DesignSystemVersion.State))!
            .GetProviderClrType().Should().Be(typeof(string));

        var systemType = model.FindEntityType(typeof(DesignSystem));
        systemType.Should().NotBeNull();
        // Phase 4 (P4-03) removed the Phase-2 Ignore, so parent_library_id is now part of the model;
        // design_system_libraries is covered by DesignSystems/Tenancy/LibraryModelTests.
        systemType!.FindProperty(nameof(DesignSystem.ParentLibraryId)).Should().NotBeNull();

        systemType.GetCheckConstraints()
            .Should().Contain(c => c.Sql.Contains("kind IN ('design','product_map')"));
        systemType.GetCheckConstraints()
            .Should().Contain(c => c.Name == "ck_design_systems_parent_library_project");
    }
}
