using System;
using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Data;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Avatars;

/// <summary>
/// The schema of the workspace avatar library (lane L9 of docs/design/avatar-objects.md): who owns the two tables, what
/// migration <c>AddAvatarLibrary</c> touches, the referential actions and unique indexes the design relies on, and that
/// the engine reads the link column without owning anything. Model-only Npgsql contexts, never a connection.
/// </summary>
public class AvatarLibrarySchemaTests
{
    private const string Npgsql = "Npgsql.EntityFrameworkCore.PostgreSQL";

    private static readonly string[] LibraryTables = ["avatar_library_items", "avatar_library_references"];

    private static InferenceApiDbContext Api() => new(new DbContextOptionsBuilder<InferenceApiDbContext>()
        .UseNpgsql("Host=localhost;Database=model-only", o => o.MigrationsHistoryTable("__EFMigrationsHistory_Api"))
        .Options);

    private static WorkflowEngineDbContext Engine() => new(new DbContextOptionsBuilder<WorkflowEngineDbContext>()
        .UseNpgsql("Host=localhost;Database=model-only", o => o.MigrationsHistoryTable("__EFMigrationsHistory_Workflow"))
        .Options);

    private static IModel DesignModel(DbContext db) => db.GetService<IDesignTimeModel>().Model;

    private static IEntityType Entity<T>(DbContext db) => DesignModel(db).FindEntityType(typeof(T))
        ?? throw new InvalidOperationException($"{typeof(T).Name} is not mapped");

    [Fact]
    public void The_api_owns_both_library_tables_and_the_engine_maps_neither()
    {
        using InferenceApiDbContext api = Api();
        using WorkflowEngineDbContext engine = Engine();

        Entity<AvatarLibraryItem>(api).GetTableName().Should().Be("avatar_library_items");
        Entity<AvatarLibraryReference>(api).GetTableName().Should().Be("avatar_library_references");
        Entity<AvatarLibraryItem>(api).IsTableExcludedFromMigrations().Should().BeFalse();
        Entity<AvatarLibraryReference>(api).IsTableExcludedFromMigrations().Should().BeFalse();

        DesignModel(engine).FindEntityType(typeof(AvatarLibraryItem)).Should().BeNull("the engine sees an import as an ordinary project avatar");
        DesignModel(engine).FindEntityType(typeof(AvatarLibraryReference)).Should().BeNull();
        IEntityType engineAvatar = Entity<ProjectAvatar>(engine);
        engineAvatar.IsTableExcludedFromMigrations().Should().BeTrue("so the new column needs no engine migration");
        IProperty link = engineAvatar.FindProperty(nameof(ProjectAvatar.LibraryItemId))!;
        link.GetColumnName().Should().Be("library_item_id");
        link.IsNullable.Should().BeTrue();
        engineAvatar.GetForeignKeys().Should().NotContain(fk => fk.Properties.Any(p => p.Name == nameof(ProjectAvatar.LibraryItemId)),
            "the engine reads the link as a plain column");
    }

    [Fact]
    public void AddAvatarLibrary_creates_the_two_tables_and_only_expands_project_avatars()
    {
        using InferenceApiDbContext db = Api();
        IMigrationsAssembly assembly = db.GetService<IMigrationsAssembly>();
        Migration migration = assembly.CreateMigration(
            assembly.Migrations.Single(m => m.Key.EndsWith("_AddAvatarLibrary", StringComparison.Ordinal)).Value, Npgsql);

        migration.UpOperations.OfType<CreateTableOperation>().Select(c => c.Name).Should().BeEquivalentTo(LibraryTables);

        IEnumerable<string> touched = migration.UpOperations.Select(op => op switch
        {
            CreateTableOperation c => c.Name,
            AddColumnOperation a => a.Table,
            CreateIndexOperation i => i.Table,
            AddForeignKeyOperation f => f.Table,
            _ => "<" + op.GetType().Name + ">"
        });
        touched.Distinct().Should().BeEquivalentTo([.. LibraryTables, "project_avatars"],
            "an expand-only migration: new tables, one new nullable column, its indexes and its foreign key");

        AddColumnOperation column = migration.UpOperations.OfType<AddColumnOperation>().Single();
        column.Table.Should().Be("project_avatars");
        column.Name.Should().Be("library_item_id");
        column.IsNullable.Should().BeTrue("every existing avatar is not an import");

        AddForeignKeyOperation link = migration.UpOperations.OfType<AddForeignKeyOperation>().Single();
        link.Table.Should().Be("project_avatars");
        link.PrincipalTable.Should().Be("avatar_library_items");
        link.OnDelete.Should().Be(ReferentialAction.NoAction);

        migration.UpOperations.OfType<CreateIndexOperation>().Where(i => i.Table == "project_avatars" && i.IsUnique)
            .Should().ContainSingle().Which.Columns.Should().Equal("project_id", "library_item_id");
    }

    [Fact]
    public void Referential_actions_and_unique_indexes_match_the_design()
    {
        using InferenceApiDbContext db = Api();

        static DeleteBehavior OnDelete(IEntityType e, string column) =>
            e.GetForeignKeys().Single(fk => fk.Properties.Single().GetColumnName() == column).DeleteBehavior;

        IEntityType item = Entity<AvatarLibraryItem>(db);
        IEntityType reference = Entity<AvatarLibraryReference>(db);
        IEntityType avatar = Entity<ProjectAvatar>(db);

        OnDelete(item, "organization_id").Should().Be(DeleteBehavior.Cascade);
        OnDelete(reference, "library_item_id").Should().Be(DeleteBehavior.Cascade, "an item's media rows go with it");
        OnDelete(avatar, "library_item_id").Should().Be(DeleteBehavior.NoAction,
            "the library delete routine removes the imports first; an item is never deleted from under an import");
        reference.GetForeignKeys().Single().GetConstraintName().Should().Be("fk_avatar_library_references_library_item");

        IIndex importOnce = avatar.GetIndexes().Single(i => i.GetDatabaseName() == "ux_project_avatars_project_library_item");
        importOnce.IsUnique.Should().BeTrue("a project holds at most one import of an item");
        importOnce.Properties.Select(p => p.GetColumnName()).Should().Equal("project_id", "library_item_id");

        IIndex liveName = item.GetIndexes().Single(i => i.GetDatabaseName() == "ux_avatar_library_items_org_live_name");
        liveName.IsUnique.Should().BeTrue();
        liveName.GetFilter().Should().Be("consent_revoked_at IS NULL", "a revoked item does not hold its name");
        liveName.Properties.Select(p => p.GetColumnName()).Should().Equal("organization_id", "name");
    }

    [Fact]
    public void The_item_is_tenant_filtered_stores_enums_by_name_and_versions_on_xmin()
    {
        using InferenceApiDbContext db = Api();
        IEntityType item = Entity<AvatarLibraryItem>(db);
        IEntityType reference = Entity<AvatarLibraryReference>(db);

        item.GetDeclaredQueryFilters().Should().ContainSingle(f => f.Key == InferenceApiDbContext.TenantFilterName,
            "a library item is addressable by a caller-chosen id and owned by one organization");
        reference.GetDeclaredQueryFilters().Should().BeEmpty("it is reached only through its item");

        foreach ((IEntityType entity, string property) in new[]
                 {
                     (item, nameof(AvatarLibraryItem.SubjectKind)), (item, nameof(AvatarLibraryItem.ConsentBasis)),
                     (item, nameof(AvatarLibraryItem.DisclosureLabel)), (reference, nameof(AvatarLibraryReference.Kind)),
                     (reference, nameof(AvatarLibraryReference.Role))
                 })
        {
            entity.FindProperty(property)!.GetColumnType().Should().Be("character varying(50)", property);
        }

        IProperty version = item.FindProperty(nameof(AvatarLibraryItem.RowVersion))!;
        version.GetColumnName().Should().Be("xmin");
        version.IsConcurrencyToken.Should().BeTrue();

        string[] checks = [.. item.GetCheckConstraints().Select(c => c.Name!).Concat(reference.GetCheckConstraints().Select(c => c.Name!))];
        checks.Should().BeEquivalentTo(
        [
            "ck_avatar_library_items_subject_kind", "ck_avatar_library_items_consent_basis",
            "ck_avatar_library_items_disclosure_label", "ck_avatar_library_items_revoked_pair",
            "ck_avatar_library_references_kind", "ck_avatar_library_references_role"
        ]);
        string roles = reference.GetCheckConstraints().Single(c => c.Name == "ck_avatar_library_references_role").Sql;
        foreach (string role in Enum.GetNames<AvatarReferenceRole>()) roles.Should().Contain($"'{role}'");
        string kinds = item.GetCheckConstraints().Single(c => c.Name == "ck_avatar_library_items_subject_kind").Sql;
        foreach (string kind in Enum.GetNames<AvatarSubjectKind>()) kinds.Should().Contain($"'{kind}'");
    }
}
