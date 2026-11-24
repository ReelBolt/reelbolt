using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.DesignSystems;
using ReelBolt.Shared.Tenancy;
using System;
using System.Linq;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Tenancy;

/// <summary>
/// P4-03 (SPEC 16.1): the data-layer and tenancy halves of <c>design_system_libraries</c>. A library
/// is organization-owned and addressed by its own id, so unlike the design_systems children it
/// carries the named <c>"Tenant"</c> query filter — the only design-system table that does.
///
/// <para>
/// Sqlite is used rather than InMemory on purpose: InMemory enforces neither unique indexes nor
/// foreign keys, so it cannot answer the UNIQUE(org, name) or cascade questions. The CHECK constraint
/// is asserted against the design-time model metadata, because the enforcement that matters ships in
/// the migration rather than in the provider.
/// </para>
/// </summary>
public class LibraryModelTests
{
    private static InferenceApiDbContext CreateContext(TestTenancy.MutableTenant? tenant = null)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "PRAGMA foreign_keys = ON;";
            cmd.ExecuteNonQuery();
        }

        var options = new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseSqlite(connection)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;

        var db = new InferenceApiDbContext(options, tenant);
        db.Database.EnsureCreated();
        return db;
    }

    private static Guid AddOrganization(InferenceApiDbContext db, string name)
    {
        Guid id = Guid.NewGuid();
        db.Organizations.Add(new Organization
        {
            Id = id,
            Name = name,
            Slug = $"org-{id:N}",
            Kind = OrganizationKind.Team,
        });
        db.SaveChanges();
        return id;
    }

    /// <summary>The library's document container: a design_systems row with project_id NULL.</summary>
    private static DesignSystem NewContainer(Guid userId, string name) => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = null,
        Kind = DesignSystemKinds.Design,
        Name = name,
        CreatedByUserId = userId,
    };

    private static DesignSystemLibrary NewLibrary(Guid organizationId, Guid userId, DesignSystem container, string name) => new()
    {
        Id = Guid.NewGuid(),
        OrganizationId = organizationId,
        Name = name,
        DesignSystemId = container.Id,
        CreatedByUserId = userId,
    };

    private static DesignSystemLibrary AddLibrary(InferenceApiDbContext db, Guid organizationId, Guid userId, string name)
    {
        DesignSystem container = NewContainer(userId, $"{name} document");
        DesignSystemLibrary library = NewLibrary(organizationId, userId, container, name);
        db.DesignSystems.Add(container);
        db.DesignSystemLibraries.Add(library);
        db.SaveChanges();
        return library;
    }

    [Fact]
    public void Tenant_filter_scopes_libraries_to_the_active_organization_and_fails_closed()
    {
        var tenant = new TestTenancy.MutableTenant(); // system scope while seeding
        using InferenceApiDbContext db = CreateContext(tenant);

        Guid orgA = AddOrganization(db, "A");
        Guid orgB = AddOrganization(db, "B");
        Guid user = Guid.NewGuid();
        DesignSystemLibrary a = AddLibrary(db, orgA, user, "A library");
        DesignSystemLibrary b = AddLibrary(db, orgB, user, "B library");

        // System scope (and a context built with no tenant at all) sees every organization's rows.
        db.DesignSystemLibraries.Count().Should().Be(2);

        tenant.ActAsOrganization(orgA);
        db.DesignSystemLibraries.Select(l => l.Id).Should().Equal(a.Id);

        tenant.ActAsOrganization(orgB);
        db.DesignSystemLibraries.Select(l => l.Id).Should().Equal(b.Id);

        // An org with nothing in it, and no active organization, both fail closed.
        tenant.ActAsOrganization(Guid.NewGuid());
        db.DesignSystemLibraries.Count().Should().Be(0);

        tenant.ActAsOrganization(null);
        db.DesignSystemLibraries.Count().Should().Be(0);
    }

    [Fact]
    public void Duplicate_library_name_within_one_organization_violates_the_unique_constraint()
    {
        using InferenceApiDbContext db = CreateContext();
        Guid org = AddOrganization(db, "A");
        Guid user = Guid.NewGuid();
        AddLibrary(db, org, user, "Brand");

        DesignSystem container = NewContainer(user, "second document");
        db.DesignSystems.Add(container);
        db.DesignSystemLibraries.Add(NewLibrary(org, user, container, "Brand"));

        Action save = () => db.SaveChanges();
        save.Should().Throw<DbUpdateException>();
    }

    [Fact]
    public void Two_libraries_cannot_share_one_document_container()
    {
        using InferenceApiDbContext db = CreateContext();
        Guid org = AddOrganization(db, "A");
        Guid user = Guid.NewGuid();
        DesignSystemLibrary first = AddLibrary(db, org, user, "First");

        db.DesignSystemLibraries.Add(new DesignSystemLibrary
        {
            Id = Guid.NewGuid(),
            OrganizationId = org,
            Name = "Second",
            DesignSystemId = first.DesignSystemId,
            CreatedByUserId = user,
        });

        Action save = () => db.SaveChanges();
        save.Should().Throw<DbUpdateException>();
    }

    [Fact]
    public void Deleting_the_organization_cascades_to_its_libraries()
    {
        using InferenceApiDbContext db = CreateContext();
        Guid orgA = AddOrganization(db, "A");
        Guid orgB = AddOrganization(db, "B");
        Guid user = Guid.NewGuid();
        AddLibrary(db, orgA, user, "A library");
        AddLibrary(db, orgB, user, "B library");

        db.Organizations.Remove(db.Organizations.Single(o => o.Id == orgA));
        db.SaveChanges();

        db.DesignSystemLibraries.Select(l => l.OrganizationId).Should().Equal(orgB);
    }

    [Fact]
    public void Parent_library_column_and_check_are_mapped_and_parent_library_requires_a_project()
    {
        var options = new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        using InferenceApiDbContext db = new(options);

        // Check constraints live on the design-time model, not the read-optimized runtime one.
        var model = db.GetService<IDesignTimeModel>().Model;

        var systemType = model.FindEntityType(typeof(DesignSystem));
        systemType.Should().NotBeNull();

        // Phase 4 removed the Phase-2 Ignore: the FK column is now part of the model.
        systemType!.FindProperty(nameof(DesignSystem.ParentLibraryId)).Should().NotBeNull();

        systemType.GetCheckConstraints()
            .Should().Contain(c => c.Name == "ck_design_systems_parent_library_project"
                && c.Sql.Contains("parent_library_id IS NULL OR project_id IS NOT NULL"));

        var libraryType = model.FindEntityType(typeof(DesignSystemLibrary));
        libraryType.Should().NotBeNull();
        libraryType!.FindProperty(nameof(DesignSystemLibrary.Name))!
            .GetMaxLength().Should().Be(200);
        libraryType.GetIndexes()
            .Should().ContainSingle(i => i.IsUnique
                && i.Properties.Select(p => p.Name).SequenceEqual(new[]
                {
                    nameof(DesignSystemLibrary.OrganizationId),
                    nameof(DesignSystemLibrary.Name),
                }));
        libraryType.GetIndexes()
            .Should().ContainSingle(i => i.IsUnique
                && i.Properties.Select(p => p.Name).SequenceEqual(new[] { nameof(DesignSystemLibrary.DesignSystemId) }));
    }
}
