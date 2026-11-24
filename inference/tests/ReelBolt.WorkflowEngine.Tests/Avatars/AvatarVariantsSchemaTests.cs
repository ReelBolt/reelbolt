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
/// Lane L12 of docs/design/avatar-objects.md: the schema of avatar variants. Migration <c>AddAvatarVariants</c> only
/// expands (three nullable columns on <c>project_avatars</c> and the new table <c>avatar_build_jobs</c>), the engine
/// reads the new columns through the shared mapping with no migration of its own, and the job table has exactly the
/// columns of the design. Model-only Npgsql contexts, never a connection.
/// </summary>
public class AvatarVariantsSchemaTests
{
    private const string Npgsql = "Npgsql.EntityFrameworkCore.PostgreSQL";

    private static InferenceApiDbContext Api() => new(new DbContextOptionsBuilder<InferenceApiDbContext>()
        .UseNpgsql("Host=localhost;Database=model-only", o => o.MigrationsHistoryTable("__EFMigrationsHistory_Api"))
        .Options);

    private static WorkflowEngineDbContext Engine() => new(new DbContextOptionsBuilder<WorkflowEngineDbContext>()
        .UseNpgsql("Host=localhost;Database=model-only", o => o.MigrationsHistoryTable("__EFMigrationsHistory_Workflow"))
        .Options);

    private static IModel DesignModel(DbContext db) => db.GetService<IDesignTimeModel>().Model;

    private static IEntityType Entity<T>(DbContext db) => DesignModel(db).FindEntityType(typeof(T))
        ?? throw new InvalidOperationException($"{typeof(T).Name} is not mapped");

    private static Migration AddAvatarVariants(DbContext db)
    {
        IMigrationsAssembly assembly = db.GetService<IMigrationsAssembly>();
        KeyValuePair<string, System.Reflection.TypeInfo> entry = assembly.Migrations
            .Single(m => m.Key.EndsWith("_AddAvatarVariants", StringComparison.Ordinal));
        return assembly.CreateMigration(entry.Value, Npgsql);
    }

    [Fact]
    public void AddAvatarVariants_only_expands_the_avatar_table_and_creates_the_job_table()
    {
        using InferenceApiDbContext db = Api();
        Migration migration = AddAvatarVariants(db);

        migration.UpOperations.Should().OnlyContain(
            op => op is AddColumnOperation || op is CreateTableOperation || op is CreateIndexOperation || op is AddForeignKeyOperation,
            "expand only: new nullable columns, a new table, new indexes and a new foreign key");
        migration.UpOperations.OfType<CreateTableOperation>().Select(t => t.Name).Should().Equal("avatar_build_jobs");
        migration.UpOperations.OfType<AddColumnOperation>().Should().OnlyContain(c => c.Table == "project_avatars" && c.IsNullable,
            "every existing avatar is an original: the new columns are null on it");
        migration.UpOperations.OfType<AddColumnOperation>().Select(c => (c.Name, c.ColumnType, c.MaxLength)).Should().BeEquivalentTo(
        [
            ("variant_of_avatar_id", "uuid", (int?)null),
            ("variant_label", "character varying(60)", (int?)60),
            ("variant_description", "character varying(500)", (int?)500)
        ]);

        AddForeignKeyOperation self = migration.UpOperations.OfType<AddForeignKeyOperation>().Single();
        (self.Table, self.PrincipalTable, self.Columns.Single(), self.OnDelete).Should().Be(
            ("project_avatars", "project_avatars", "variant_of_avatar_id", ReferentialAction.SetNull),
            "deleting the original leaves its variants, ungrouped");

        CreateIndexOperation label = migration.UpOperations.OfType<CreateIndexOperation>().Single(i => i.Name == "ux_project_avatars_variant_live_label");
        (label.Table, label.IsUnique, label.Filter).Should().Be(
            ("project_avatars", true, "variant_of_avatar_id IS NOT NULL AND consent_revoked_at IS NULL"));
        label.Columns.Should().Equal("variant_of_avatar_id", "variant_label");

        // Down undoes exactly what Up did.
        migration.DownOperations.OfType<DropTableOperation>().Select(t => t.Name).Should().Equal("avatar_build_jobs");
        migration.DownOperations.OfType<DropColumnOperation>().Select(c => c.Name)
            .Should().BeEquivalentTo(["variant_of_avatar_id", "variant_label", "variant_description"]);
    }

    [Fact]
    public void The_job_table_has_exactly_the_columns_of_the_design()
    {
        using InferenceApiDbContext db = Api();
        CreateTableOperation table = AddAvatarVariants(db).UpOperations.OfType<CreateTableOperation>().Single();

        // The operation lists the xmin system column (the concurrency token); the SQL generator skips it (asserted below).
        table.Columns.Where(c => c.Name != "xmin").Select(c => c.Name).Should().BeEquivalentTo(
        [
            "id", "project_id", "organization_id", "requested_by_user_id", "kind", "source_avatar_id", "source_media_ref",
            "instruction", "object_description", "name", "variant_label", "params_json", "state", "stage", "done", "total",
            "execution_id", "avatar_id", "statement_locale", "allowed_by", "failure_code", "message", "hold_id",
            "created_at", "updated_at"
        ], "docs/design/avatar-objects.md \"Avatar variants\" names these and no other (the concurrency token is the xmin system column)");
        table.Columns.Single(c => c.Name == "params_json").ColumnType.Should().Be("jsonb");
        table.Columns.Where(c => !c.IsNullable && c.Name != "xmin").Select(c => c.Name).Should().BeEquivalentTo(
        [
            "id", "project_id", "organization_id", "requested_by_user_id", "kind", "name", "state", "done", "total",
            "statement_locale", "created_at", "updated_at"
        ]);

        Dictionary<string, ReferentialAction> foreignKeys = table.ForeignKeys.ToDictionary(f => f.Columns.Single(), f => f.OnDelete);
        foreignKeys.Should().BeEquivalentTo(new Dictionary<string, ReferentialAction>
        {
            ["project_id"] = ReferentialAction.Cascade,
            ["source_avatar_id"] = ReferentialAction.SetNull,
            ["avatar_id"] = ReferentialAction.SetNull
        }, "a job goes with its project and outlives the avatars it names; the run it started is the engine's (no key)");

        Dictionary<string, string> checks = table.CheckConstraints.ToDictionary(c => c.Name, c => c.Sql);
        checks["ck_avatar_build_jobs_kind"].Should().Be("kind IN (" + string.Join(", ", AvatarBuildKinds.All.Select(k => $"'{k}'")) + ")");
        checks["ck_avatar_build_jobs_state"].Should().Be("state IN (" + string.Join(", ", AvatarBuildStates.All.Select(s => $"'{s}'")) + ")",
            "a state the list gains must be added to the CHECK in the same change, or the first write fails");
        checks["ck_avatar_build_jobs_allowed_by"].Should().Contain($"'{AvatarBuildJob.AllowedByProjectConsent}'").And.Contain($"'{AvatarBuildJob.AllowedByCall}'");

        string sql = string.Join("\n", db.GetService<IMigrationsSqlGenerator>()
            .Generate(AddAvatarVariants(db).UpOperations, DesignModel(db)).Select(c => c.CommandText));
        sql.Should().Contain("CREATE TABLE avatar_build_jobs").And.NotContain("xmin");
    }

    [Fact]
    public void The_job_table_is_the_apis_alone_and_carries_no_tenant_filter()
    {
        using InferenceApiDbContext api = Api();
        using WorkflowEngineDbContext engine = Engine();

        IEntityType job = Entity<AvatarBuildJob>(api);
        job.GetTableName().Should().Be("avatar_build_jobs");
        job.IsTableExcludedFromMigrations().Should().BeFalse("API-owned");
        job.GetDeclaredQueryFilters().Should().BeEmpty("reached only through an authorized project (TenantBoundaryInventoryTests)");
        IProperty rowVersion = job.FindProperty(nameof(AvatarBuildJob.RowVersion))!;
        (rowVersion.GetColumnName(), rowVersion.GetColumnType(), rowVersion.IsConcurrencyToken).Should().Be(("xmin", "xid", true));
        DesignModel(engine).FindEntityType(typeof(AvatarBuildJob)).Should().BeNull("the engine never reads a build job");
    }

    [Fact]
    public void The_engine_reads_the_variant_columns_through_the_shared_mapping_and_migrates_nothing()
    {
        using InferenceApiDbContext api = Api();
        using WorkflowEngineDbContext engine = Engine();

        foreach (DbContext db in new DbContext[] { api, engine })
        {
            IEntityType avatar = Entity<ProjectAvatar>(db);
            avatar.FindProperty(nameof(ProjectAvatar.VariantOfAvatarId))!.GetColumnName().Should().Be("variant_of_avatar_id");
            avatar.FindProperty(nameof(ProjectAvatar.VariantLabel))!.GetMaxLength().Should().Be(ProjectAvatarLimits.MaxVariantLabelLength);
            avatar.FindProperty(nameof(ProjectAvatar.VariantDescription))!.GetMaxLength().Should().Be(ProjectAvatarLimits.MaxVariantDescriptionLength);
            avatar.FindProperty(nameof(ProjectAvatar.VariantOfAvatarId))!.IsNullable.Should().BeTrue();
            avatar.FindProperty(nameof(ProjectAvatar.IsVariant)).Should().BeNull("a computed member is not a column");
            avatar.GetForeignKeys().Single(fk => fk.Properties.Single().GetColumnName() == "variant_of_avatar_id").DeleteBehavior
                .Should().Be(DeleteBehavior.SetNull);
        }

        Entity<ProjectAvatar>(engine).IsTableExcludedFromMigrations().Should().BeTrue();
        engine.GetService<IMigrationsAssembly>().Migrations.Keys
            .Should().NotContain(id => id.EndsWith("_AddAvatarVariants", StringComparison.Ordinal), "the avatar tables are API-owned");
    }
}
