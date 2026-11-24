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
/// Lane A1 of docs/design/avatars.md: who owns the avatar tables, what each migration touches, and the referential
/// actions the design relies on. Model-only Npgsql contexts, never a connection (the snapshot-drift half is
/// <see cref="InferenceApiMigrationSnapshotTests"/> and <see cref="WorkflowEngineMigrationSnapshotTests"/>).
/// </summary>
public class ProjectAvatarSchemaTests
{
    private const string Npgsql = "Npgsql.EntityFrameworkCore.PostgreSQL";

    private static readonly string[] AvatarTables =
        ["project_avatars", "project_avatar_references", "project_avatar_vendor_assets", "project_avatar_events"];

    private static InferenceApiDbContext Api() => new(new DbContextOptionsBuilder<InferenceApiDbContext>()
        .UseNpgsql("Host=localhost;Database=model-only", o => o.MigrationsHistoryTable("__EFMigrationsHistory_Api"))
        .Options);

    private static WorkflowEngineDbContext Engine() => new(new DbContextOptionsBuilder<WorkflowEngineDbContext>()
        .UseNpgsql("Host=localhost;Database=model-only", o => o.MigrationsHistoryTable("__EFMigrationsHistory_Workflow"))
        .Options);

    private static IModel DesignModel(DbContext db) => db.GetService<IDesignTimeModel>().Model;

    private static IEntityType Entity<T>(DbContext db) => DesignModel(db).FindEntityType(typeof(T))
        ?? throw new InvalidOperationException($"{typeof(T).Name} is not mapped");

    private static Migration CreateMigration(DbContext db, string name)
    {
        IMigrationsAssembly assembly = db.GetService<IMigrationsAssembly>();
        KeyValuePair<string, System.Reflection.TypeInfo> entry = assembly.Migrations
            .Single(m => m.Key.EndsWith("_" + name, StringComparison.Ordinal));
        return assembly.CreateMigration(entry.Value, Npgsql);
    }

    private static IEnumerable<string?> TablesTouched(IEnumerable<MigrationOperation> operations) =>
        operations.Select(op => op switch
        {
            CreateTableOperation c => c.Name,
            DropTableOperation d => d.Name,
            AddColumnOperation a => a.Table,
            DropColumnOperation d => d.Table,
            AlterColumnOperation a => a.Table,
            CreateIndexOperation i => i.Table,
            DropIndexOperation i => i.Table,
            AddForeignKeyOperation f => f.Table,
            DropForeignKeyOperation f => f.Table,
            AddCheckConstraintOperation c => c.Table,
            DropCheckConstraintOperation c => c.Table,
            _ => "<" + op.GetType().Name + ">"
        });

    [Fact]
    public void The_api_owns_and_migrates_all_four_avatar_tables()
    {
        using InferenceApiDbContext db = Api();

        foreach (IEntityType entity in new[]
                 {
                     Entity<ProjectAvatar>(db), Entity<ProjectAvatarReference>(db),
                     Entity<ProjectAvatarVendorAsset>(db), Entity<ProjectAvatarEvent>(db)
                 })
        {
            entity.IsTableExcludedFromMigrations().Should().BeFalse($"{entity.GetTableName()} is API-owned");
        }

        AvatarTables.Should().BeSubsetOf(DesignModel(db).GetEntityTypes().Select(e => e.GetTableName()));
    }

    [Fact]
    public void The_engine_maps_avatars_and_references_read_only_and_nothing_else_of_the_feature()
    {
        using WorkflowEngineDbContext db = Engine();

        Entity<ProjectAvatar>(db).IsTableExcludedFromMigrations().Should().BeTrue();
        Entity<ProjectAvatarReference>(db).IsTableExcludedFromMigrations().Should().BeTrue();
        DesignModel(db).FindEntityType(typeof(ProjectAvatarVendorAsset)).Should().BeNull("phase 1 gives the engine no vendor-asset mapping");
        DesignModel(db).FindEntityType(typeof(ProjectAvatarEvent)).Should().BeNull("the audit trail is written by the API only");
    }

    [Fact]
    public void Both_contexts_map_the_avatar_columns_identically()
    {
        using InferenceApiDbContext api = Api();
        using WorkflowEngineDbContext engine = Engine();

        foreach (Type type in new[] { typeof(ProjectAvatar), typeof(ProjectAvatarReference) })
        {
            static IEnumerable<string> Shape(IEntityType e) => e.GetProperties()
                .Select(p => $"{p.GetColumnName()}:{p.GetColumnType()}:{p.IsNullable}:{p.GetMaxLength()}")
                .OrderBy(s => s, StringComparer.Ordinal);

            Shape(DesignModel(engine).FindEntityType(type)!).Should().Equal(Shape(DesignModel(api).FindEntityType(type)!),
                $"the engine reads {type.Name} through the same columns the API migrates");
        }
    }

    [Fact]
    public void AddProjectAvatars_creates_exactly_the_four_tables_and_touches_nothing_else()
    {
        using InferenceApiDbContext db = Api();
        Migration migration = CreateMigration(db, "AddProjectAvatars");

        migration.UpOperations.OfType<CreateTableOperation>().Select(c => c.Name)
            .Should().BeEquivalentTo(AvatarTables);
        TablesTouched(migration.UpOperations).Distinct().Should().BeSubsetOf(AvatarTables,
            "an API migration must never alter a table it maps ExcludeFromMigrations (external_generation_jobs among them)");
        migration.DownOperations.OfType<DropTableOperation>().Select(d => d.Name)
            .Should().BeEquivalentTo(AvatarTables, "Down undoes exactly what Up created");
        TablesTouched(migration.DownOperations).Distinct().Should().BeSubsetOf(AvatarTables);
    }

    // ------------------------------------------------------------------ objects (docs/design/avatar-objects.md)

    [Fact]
    public void AddAvatarObjects_only_expands_three_api_owned_tables()
    {
        using InferenceApiDbContext db = Api();
        Migration migration = CreateMigration(db, "AddAvatarObjects");

        TablesTouched(migration.UpOperations).Distinct().Should().BeEquivalentTo(["project_avatars", "project_avatar_references", "projects"]);
        migration.UpOperations.Should().OnlyContain(
            op => op is AddColumnOperation || op is AddCheckConstraintOperation || op is DropCheckConstraintOperation,
            "expand only: new columns, a new CHECK on a new column and one CHECK replaced by a superset");

        AddColumnOperation kind = migration.UpOperations.OfType<AddColumnOperation>().Single(c => c.Table == "project_avatars");
        (kind.Name, kind.IsNullable, kind.DefaultValue, kind.MaxLength).Should().Be(("subject_kind", false, (object)"Person", (int?)50),
            "every existing avatar, and every row an older release inserts, is a person");

        AddColumnOperation[] consent = [.. migration.UpOperations.OfType<AddColumnOperation>().Where(c => c.Table == "projects")];
        consent.Select(c => (c.Name, c.ColumnType, c.IsNullable)).Should().BeEquivalentTo(
        [
            ("subject_media_consent_at", "timestamp with time zone", true),
            ("subject_media_consent_by_user_id", "uuid", true)
        ]);

        // The role CHECK is dropped and re-added with a superset; the kind CHECK is new.
        migration.UpOperations.OfType<DropCheckConstraintOperation>().Select(c => c.Name).Should().Equal("ck_project_avatar_references_role");
        Dictionary<string, string> added = migration.UpOperations.OfType<AddCheckConstraintOperation>().ToDictionary(c => c.Name, c => c.Sql);
        added.Keys.Should().BeEquivalentTo(["ck_project_avatar_references_role", "ck_project_avatars_subject_kind"]);
        added["ck_project_avatars_subject_kind"].Should().Be("subject_kind IN ('Person', 'Object')");
        added["ck_project_avatar_references_role"].Should().Be("role IN ('Face', 'HalfBody', 'FullBody', 'Motion', 'Speech', 'Front', 'Angle', 'Detail')");
        foreach (string role in new[] { "Face", "HalfBody", "FullBody", "Motion", "Speech" })
            added["ck_project_avatar_references_role"].Should().Contain($"'{role}'", "a person's roles stay allowed: the new CHECK is a superset");
    }

    [Fact]
    public void The_check_constraints_name_exactly_the_enum_members()
    {
        using InferenceApiDbContext db = Api();

        static string In<T>() where T : struct, Enum => "IN (" + string.Join(", ", Enum.GetNames<T>().Select(n => $"'{n}'")) + ")";
        string Check(IEntityType entity, string name) => entity.GetCheckConstraints().Single(c => c.Name == name).Sql;

        Check(Entity<ProjectAvatar>(db), "ck_project_avatars_subject_kind").Should().Be("subject_kind " + In<AvatarSubjectKind>());
        Check(Entity<ProjectAvatarReference>(db), "ck_project_avatar_references_role").Should().Be("role " + In<AvatarReferenceRole>(),
            "a role the enum gains must be added to the CHECK in the same change, or the first insert fails");
    }

    [Fact]
    public void The_subject_kind_is_stored_by_name_with_a_person_default_in_both_contexts()
    {
        using InferenceApiDbContext api = Api();
        using WorkflowEngineDbContext engine = Engine();

        foreach (DbContext db in new DbContext[] { api, engine })
        {
            IProperty kind = Entity<ProjectAvatar>(db).FindProperty(nameof(ProjectAvatar.SubjectKind))!;
            kind.GetColumnName().Should().Be("subject_kind");
            kind.GetProviderClrType().Should().Be(typeof(string));
            kind.GetMaxLength().Should().Be(50);
            kind.IsNullable.Should().BeFalse();
            kind.GetDefaultValue().Should().Be(AvatarSubjectKind.Person);
        }

        // Computed members are not columns.
        Entity<ProjectAvatar>(api).FindProperty(nameof(ProjectAvatar.CoversPictures)).Should().BeNull();
        Entity<ProjectAvatar>(api).FindProperty(nameof(ProjectAvatar.HasObjectAttestation)).Should().BeNull();
    }

    [Fact]
    public void The_media_consent_columns_are_the_apis_and_the_engine_does_not_map_them()
    {
        using InferenceApiDbContext api = Api();
        using WorkflowEngineDbContext engine = Engine();

        IEntityType project = Entity<Project>(api);
        project.FindProperty(nameof(Project.SubjectMediaConsentAt))!.GetColumnName().Should().Be("subject_media_consent_at");
        project.FindProperty(nameof(Project.SubjectMediaConsentByUserId))!.GetColumnName().Should().Be("subject_media_consent_by_user_id");
        project.FindProperty(nameof(Project.SubjectMediaConsentAt))!.IsNullable.Should().BeTrue();
        project.IsTableExcludedFromMigrations().Should().BeFalse("projects is API-owned");

        IEntityType engineProject = Entity<Project>(engine);
        engineProject.IsTableExcludedFromMigrations().Should().BeTrue();
        engineProject.FindProperty(nameof(Project.SubjectMediaConsentAt)).Should().BeNull("the engine needs neither column");
        engineProject.FindProperty(nameof(Project.SubjectMediaConsentByUserId)).Should().BeNull();
    }

    [Fact]
    public void No_engine_migration_carries_the_object_columns()
    {
        using WorkflowEngineDbContext db = Engine();

        db.GetService<IMigrationsAssembly>().Migrations.Keys
            .Should().NotContain(id => id.EndsWith("_AddAvatarObjects", StringComparison.Ordinal), "the three tables are API-owned");
    }

    [Fact]
    public void The_row_version_is_the_xmin_system_column_and_never_a_real_column()
    {
        using InferenceApiDbContext db = Api();
        IProperty rowVersion = Entity<ProjectAvatar>(db).FindProperty(nameof(ProjectAvatar.RowVersion))!;

        rowVersion.GetColumnName().Should().Be("xmin");
        rowVersion.GetColumnType().Should().Be("xid");
        rowVersion.IsConcurrencyToken.Should().BeTrue();

        // Npgsql's SQL generator skips the xmin system column; the generated CREATE TABLE must not carry it.
        string sql = string.Join("\n", db.GetService<IMigrationsSqlGenerator>()
            .Generate(CreateMigration(db, "AddProjectAvatars").UpOperations, DesignModel(db))
            .Select(c => c.CommandText));
        sql.Should().Contain("CREATE TABLE project_avatars").And.NotContain("xmin");
    }

    [Fact]
    public void AddExternalGenerationJobAvatar_adds_only_the_nullable_ledger_column_and_its_index()
    {
        using WorkflowEngineDbContext db = Engine();
        Migration migration = CreateMigration(db, "AddExternalGenerationJobAvatar");

        TablesTouched(migration.UpOperations).Distinct().Should().Equal("external_generation_jobs");
        AddColumnOperation column = migration.UpOperations.OfType<AddColumnOperation>().Single();
        column.Name.Should().Be("avatar_id");
        column.IsNullable.Should().BeTrue();
        column.ColumnType.Should().Be("uuid");
        migration.UpOperations.OfType<AddForeignKeyOperation>().Should().BeEmpty("the engine does not own avatars");
        migration.UpOperations.OfType<CreateIndexOperation>().Single().Filter.Should().Be("avatar_id IS NOT NULL");

        IProperty mapped = Entity<ExternalGenerationJob>(db).FindProperty(nameof(ExternalGenerationJob.AvatarId))!;
        mapped.GetColumnName().Should().Be("avatar_id");
        mapped.IsNullable.Should().BeTrue();
        Entity<ExternalGenerationJob>(db).IsTableExcludedFromMigrations().Should().BeFalse("the engine owns the ledger");
    }

    [Fact]
    public void Referential_actions_match_the_design()
    {
        using InferenceApiDbContext db = Api();

        static DeleteBehavior OnDelete(IEntityType e, string column) =>
            e.GetForeignKeys().Single(fk => fk.Properties.Single().GetColumnName() == column).DeleteBehavior;

        IEntityType avatar = Entity<ProjectAvatar>(db);
        OnDelete(avatar, "project_id").Should().Be(DeleteBehavior.Cascade, "an avatar is deleted with its project");
        OnDelete(avatar, "project_voice_id").Should().Be(DeleteBehavior.SetNull, "deleting the voice only unlinks it");
        OnDelete(avatar, "consent_document_file_id").Should().Be(DeleteBehavior.SetNull);

        IEntityType reference = Entity<ProjectAvatarReference>(db);
        OnDelete(reference, "avatar_id").Should().Be(DeleteBehavior.Cascade);
        OnDelete(reference, "project_file_id").Should().Be(DeleteBehavior.NoAction,
            "a reference holds its file against a standalone delete, yet a project delete cascading to both succeeds");

        IEntityType asset = Entity<ProjectAvatarVendorAsset>(db);
        OnDelete(asset, "avatar_id").Should().Be(DeleteBehavior.Cascade);
        OnDelete(asset, "reference_id").Should().Be(DeleteBehavior.Cascade);
        OnDelete(asset, "model_capability_id").Should().Be(DeleteBehavior.SetNull);

        Entity<ProjectAvatarEvent>(db).GetForeignKeys().Should().BeEmpty(
            "audit rows survive the avatar and the project so a deletion can be proven");
    }

    [Fact]
    public void Enums_are_stored_by_name()
    {
        using InferenceApiDbContext db = Api();

        foreach ((Type type, string property) in new[]
                 {
                     (typeof(ProjectAvatar), nameof(ProjectAvatar.SubjectKind)),
                     (typeof(ProjectAvatar), nameof(ProjectAvatar.ConsentBasis)),
                     (typeof(ProjectAvatar), nameof(ProjectAvatar.DisclosureLabel)),
                     (typeof(ProjectAvatarReference), nameof(ProjectAvatarReference.Kind)),
                     (typeof(ProjectAvatarReference), nameof(ProjectAvatarReference.Role)),
                     (typeof(ProjectAvatarVendorAsset), nameof(ProjectAvatarVendorAsset.DeleteOutcome)),
                     (typeof(ProjectAvatarEvent), nameof(ProjectAvatarEvent.Kind)),
                 })
        {
            DesignModel(db).FindEntityType(type)!.FindProperty(property)!.GetProviderClrType()
                .Should().Be(typeof(string), $"{type.Name}.{property} is append-only and persisted by name");
        }
    }

    [Fact]
    public void The_live_name_is_unique_per_project_except_for_revoked_rows()
    {
        using InferenceApiDbContext db = Api();
        IIndex index = Entity<ProjectAvatar>(db).GetIndexes()
            .Single(i => i.Properties.Select(p => p.GetColumnName()).SequenceEqual(["project_id", "name"]));

        index.IsUnique.Should().BeTrue();
        index.GetFilter().Should().Be("consent_revoked_at IS NULL");
    }

    [Fact]
    public void The_reference_source_kind_is_the_documented_value()
    {
        ProjectAvatarReference.ProjectFileSourceKind.Should().Be("avatar_reference");
    }
}
