#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;
using Xunit;
using H = ReelBolt.WorkflowEngine.Tests.Avatars.AvatarTestHarness;
using L = ReelBolt.WorkflowEngine.Tests.Avatars.AvatarLibraryHarness;

namespace ReelBolt.WorkflowEngine.Tests.Avatars;

/// <summary>
/// Variants in the workspace avatar library (lane L13 of docs/design/avatar-objects.md, "Avatar variants", the
/// "Library" paragraph): an item names the item it is a variant of with a label and a description; publishing a
/// project variant whose original is in the library links the two, publishing an original links the variants already
/// saved; importing a variant groups it under the original's import when the project holds one (the project's variant
/// rules apply and a clash is refused in words), importing an original groups the variants already imported; deleting an
/// original leaves its variants as items, ungrouped.
/// </summary>
public class AvatarLibraryVariantsTests
{
    private const string RedDescription = "matte red paint, black rims";

    private static L Library() => new();

    /// <summary>A variant of <paramref name="originalId"/> made from other files in <paramref name="project"/>; answers its id.</summary>
    private static async Task<Guid> VariantAsync(L l, Project project, Guid originalId, string label, string? name = null, byte salt = 60)
    {
        ProjectFile file = l.Harness.AddFile($"variant-{label}-{salt}.png", "image/png", H.Png(1024, 1024, salt), projectId: project.Id);
        CreateProjectAvatarRequest request = H.CreateObject((file.Id, "Front")) with
        {
            Name = name!,
            SubjectKind = null,
            VariantOfAvatarId = originalId,
            VariantLabel = label,
            VariantDescription = RedDescription
        };
        (int status, JsonElement body) = H.Read(await l.Harness.Controller().Create(project.Id, request, CancellationToken.None));
        if (status != 201) throw new InvalidOperationException("variant create failed: " + status + " " + body);
        return body.GetProperty("id").GetGuid();
    }

    private static async Task<(int Status, JsonElement Body)> PublishResultAsync(L l, Guid avatarId, Project? project = null) =>
        H.Read(await l.Controller().Publish(L.ObjectRequest((project ?? l.Project).Id, avatarId), CancellationToken.None));

    private static async Task<JsonElement> ItemAsync(L l, Guid itemId) =>
        H.Read(await l.Controller().Get(itemId, null, CancellationToken.None)).Body;

    private static async Task<JsonElement> AvatarAsync(L l, Project project, Guid avatarId) =>
        H.Read(await l.Harness.Controller().Get(project.Id, avatarId, CancellationToken.None)).Body;

    /// <summary>"Car" saved to the library, then its variant "Red" saved too: answers (car item, red item, car avatar, red avatar).</summary>
    private static async Task<(Guid CarItem, Guid RedItem, Guid Car, Guid Red)> CarAndRedAsync(L l)
    {
        (Guid carItem, Guid car) = await l.PublishObjectAsync("Car");
        Guid red = await VariantAsync(l, l.Project, car, "Red");
        Guid redItem = await l.PublishAsync(L.ObjectRequest(l.Project.Id, red));
        return (carItem, redItem, car, red);
    }

    // ------------------------------------------------------------------ schema

    [Fact]
    public void AddAvatarLibraryVariants_only_expands_the_library_table()
    {
        using var db = new InferenceApiDbContext(new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseNpgsql("Host=localhost;Database=model-only", o => o.MigrationsHistoryTable("__EFMigrationsHistory_Api"))
            .Options);
        IMigrationsAssembly assembly = db.GetService<IMigrationsAssembly>();
        KeyValuePair<string, System.Reflection.TypeInfo> entry = assembly.Migrations
            .Single(m => m.Key.EndsWith("_AddAvatarLibraryVariants", StringComparison.Ordinal));
        string.CompareOrdinal(entry.Key, assembly.Migrations.Keys.Single(k => k.EndsWith("_AddAvatarLibrary", StringComparison.Ordinal)))
            .Should().BePositive("the variant columns are added to a table AddAvatarLibrary created");
        Migration migration = assembly.CreateMigration(entry.Value, "Npgsql.EntityFrameworkCore.PostgreSQL");

        migration.UpOperations.Should().OnlyContain(
            op => op is AddColumnOperation || op is CreateIndexOperation || op is AddForeignKeyOperation,
            "expand only: three nullable columns, their index and their foreign key");
        migration.UpOperations.OfType<AddColumnOperation>().Should().OnlyContain(c => c.Table == "avatar_library_items" && c.IsNullable);
        migration.UpOperations.OfType<AddColumnOperation>().Select(c => (c.Name, c.ColumnType, c.MaxLength)).Should().BeEquivalentTo(
        [
            ("variant_of_item_id", "uuid", (int?)null),
            ("variant_label", "character varying(60)", (int?)60),
            ("variant_description", "character varying(500)", (int?)500)
        ]);

        AddForeignKeyOperation self = migration.UpOperations.OfType<AddForeignKeyOperation>().Single();
        (self.Table, self.PrincipalTable, self.Columns.Single(), self.OnDelete).Should().Be(
            ("avatar_library_items", "avatar_library_items", "variant_of_item_id", ReferentialAction.SetNull),
            "deleting the original leaves its variants as items, ungrouped");

        CreateIndexOperation label = migration.UpOperations.OfType<CreateIndexOperation>().Single();
        (label.Name, label.Table, label.IsUnique, label.Filter).Should().Be(
            ("ux_avatar_library_items_variant_live_label", "avatar_library_items", true,
                "variant_of_item_id IS NOT NULL AND consent_revoked_at IS NULL"));
        label.Columns.Should().Equal("variant_of_item_id", "variant_label");

        IEntityType item = db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(AvatarLibraryItem))!;
        item.GetForeignKeys().Single(fk => fk.Properties.Single().Name == nameof(AvatarLibraryItem.VariantOfItemId)).DeleteBehavior
            .Should().Be(DeleteBehavior.SetNull);
    }

    // ------------------------------------------------------------------ publish

    [Fact]
    public async Task Publishing_a_variant_whose_original_is_in_the_library_links_the_two()
    {
        L l = Library();
        (Guid carItem, Guid car) = await l.PublishObjectAsync("Car");
        Guid red = await VariantAsync(l, l.Project, car, "Red");

        (int status, JsonElement body) = await PublishResultAsync(l, red);

        status.Should().Be(201, body.ToString());
        Guid redItem = body.GetProperty("id").GetGuid();
        body.GetProperty("name").GetString().Should().Be("Car · Red");
        body.GetProperty("variantOfItemId").GetGuid().Should().Be(carItem);
        body.GetProperty("variantLabel").GetString().Should().Be("Red");
        body.GetProperty("variantDescription").GetString().Should().Be(RedDescription);
        body.GetProperty("variantCount").GetInt32().Should().Be(0);

        JsonElement original = await ItemAsync(l, carItem);
        original.GetProperty("variantCount").GetInt32().Should().Be(1);
        original.GetProperty("variantOfItemId").ValueKind.Should().Be(JsonValueKind.Null);
        original.GetProperty("variantLabel").ValueKind.Should().Be(JsonValueKind.Null);

        JsonElement listed = H.Read(await l.Controller().List(null, CancellationToken.None)).Body;
        listed.GetProperty("items").EnumerateArray().Select(i => (i.GetProperty("id").GetGuid(), i.GetProperty("variantCount").GetInt32()))
            .Should().BeEquivalentTo([(carItem, 1), (redItem, 0)]);

        using InferenceApiDbContext db = l.NewDb();
        ProjectAvatar source = await db.ProjectAvatars.SingleAsync(a => a.Id == red);
        source.LibraryItemId.Should().Be(redItem, "the published variant becomes a linked import of its item");
        source.VariantOfAvatarId.Should().Be(car, "and stays grouped under its original in the project");
        ProjectAvatarEvent published = await db.ProjectAvatarEvents.SingleAsync(e => e.AvatarId == redItem && e.Kind == AvatarEventKind.LibraryPublished);
        JsonDocument.Parse(published.OutcomesJson!).RootElement.GetProperty("variant").GetBoolean().Should().BeTrue();
        published.OutcomesJson.Should().NotContain("Red").And.NotContain("matte", "the audit trail holds counts and outcome words only");
    }

    [Fact]
    public async Task A_variant_saved_before_its_original_keeps_its_label_and_is_linked_when_the_original_is_saved()
    {
        L l = Library();
        Guid car = await l.Harness.CreateObjectAsync("Car");
        Guid red = await VariantAsync(l, l.Project, car, "Red");

        (int status, JsonElement body) = await PublishResultAsync(l, red);
        status.Should().Be(201, body.ToString());
        Guid redItem = body.GetProperty("id").GetGuid();
        body.GetProperty("variantOfItemId").ValueKind.Should().Be(JsonValueKind.Null, "its original is not in the library yet");
        body.GetProperty("variantLabel").GetString().Should().Be("Red", "the label is kept so the item can be grouped later");

        Guid carItem = await l.PublishAsync(L.ObjectRequest(l.Project.Id, car));

        (await ItemAsync(l, redItem)).GetProperty("variantOfItemId").GetGuid().Should().Be(carItem);
        (await ItemAsync(l, carItem)).GetProperty("variantCount").GetInt32().Should().Be(1);
        using InferenceApiDbContext db = l.NewDb();
        ProjectAvatarEvent published = await db.ProjectAvatarEvents.SingleAsync(e => e.AvatarId == carItem && e.Kind == AvatarEventKind.LibraryPublished);
        JsonElement outcomes = JsonDocument.Parse(published.OutcomesJson!).RootElement;
        outcomes.GetProperty("variants_linked").GetInt32().Should().Be(1);
        outcomes.GetProperty("variants_not_linked").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task A_label_another_live_variant_of_the_same_library_item_uses_is_refused_and_nothing_is_written()
    {
        L l = Library();
        (Guid carItem, _, _, _) = await CarAndRedAsync(l);
        Project second = l.AddProject();
        Guid carThere = await l.ImportAsync(carItem, second);
        Guid otherRed = await VariantAsync(l, second, carThere, "Red", name: "Red car", salt: 61);
        int itemsBefore, objectsBefore = l.Store.Keys().Count;
        using (InferenceApiDbContext before = l.NewDb()) itemsBefore = await before.AvatarLibraryItems.CountAsync();

        (int status, JsonElement body) = await PublishResultAsync(l, otherRed, second);

        status.Should().Be(409, body.ToString());
        body.GetProperty("code").GetString().Should().Be("LIBRARY_VARIANT_LABEL_TAKEN");
        using InferenceApiDbContext db = l.NewDb();
        (await db.AvatarLibraryItems.CountAsync()).Should().Be(itemsBefore);
        (await db.ProjectAvatars.SingleAsync(a => a.Id == otherRed)).LibraryItemId.Should().BeNull("the source avatar is untouched");
        l.Store.Keys().Should().HaveCount(objectsBefore, "no copy is left behind");
    }

    [Fact]
    public async Task A_library_item_holds_at_most_ten_variants()
    {
        L l = Library();
        (Guid carItem, Guid car) = await l.PublishObjectAsync("Car");
        using (InferenceApiDbContext seed = l.NewDb())
        {
            for (int i = 0; i < AvatarLibraryLimits.MaxVariantsPerItem; i++)
            {
                seed.AvatarLibraryItems.Add(new AvatarLibraryItem
                {
                    Id = Guid.NewGuid(),
                    OrganizationId = l.OrganizationId,
                    SubjectKind = AvatarSubjectKind.Object,
                    Name = $"Car · V{i}",
                    ConsentStatement = "statement",
                    ConsentStatementVersion = "avatar-object-workspace-2026-11",
                    ConsentLocale = "en",
                    ConsentAttestedByUserId = l.Harness.OwnerId,
                    ConsentAttestedAt = DateTime.UtcNow,
                    RetentionUntil = ProjectAvatar.NoRetention,
                    VariantOfItemId = carItem,
                    VariantLabel = $"V{i}",
                    VariantDescription = "another colour"
                });
            }

            await seed.SaveChangesAsync();
        }

        Guid red = await VariantAsync(l, l.Project, car, "Red");
        (int status, JsonElement body) = await PublishResultAsync(l, red);

        status.Should().Be(409, body.ToString());
        body.GetProperty("code").GetString().Should().Be("LIBRARY_VARIANT_LIMIT_REACHED");
        (await ItemAsync(l, carItem)).GetProperty("variantCount").GetInt32().Should().Be(AvatarLibraryLimits.MaxVariantsPerItem);
    }

    // ------------------------------------------------------------------ import

    [Fact]
    public async Task Importing_a_variant_groups_it_under_the_original_import_and_it_does_not_count_toward_the_project_limit()
    {
        L l = Library();
        (Guid carItem, Guid redItem, _, _) = await CarAndRedAsync(l);
        Project second = l.AddProject();
        Guid carThere = await l.ImportAsync(carItem, second);
        using (InferenceApiDbContext seed = l.NewDb())
        {
            // The project is full of objects: a variant does not count, so the import still fits.
            for (int i = 1; i < ProjectAvatarLimits.MaxObjectsPerProject; i++)
            {
                seed.ProjectAvatars.Add(new ProjectAvatar
                {
                    Id = Guid.NewGuid(),
                    ProjectId = second.Id,
                    SubjectKind = AvatarSubjectKind.Object,
                    Name = $"Object {i}",
                    ConsentStatement = "statement",
                    ConsentStatementVersion = "avatar-object-2026-11",
                    ConsentLocale = "en",
                    ConsentAttestedByUserId = l.Harness.OwnerId,
                    ConsentAttestedAt = DateTime.UtcNow,
                    RetentionUntil = ProjectAvatar.NoRetention
                });
            }

            await seed.SaveChangesAsync();
        }

        (int status, JsonElement body) = await l.ImportResultAsync(redItem, second);

        status.Should().Be(201, body.ToString());
        body.GetProperty("fromLibrary").GetBoolean().Should().BeTrue();
        body.GetProperty("libraryItemId").GetGuid().Should().Be(redItem);
        body.GetProperty("variantOfAvatarId").GetGuid().Should().Be(carThere);
        body.GetProperty("variantLabel").GetString().Should().Be("Red");
        body.GetProperty("variantDescription").GetString().Should().Be(RedDescription);
        (await AvatarAsync(l, second, carThere)).GetProperty("variantCount").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Importing_a_variant_whose_label_the_project_already_uses_under_that_original_is_refused_in_words()
    {
        L l = Library();
        (Guid carItem, Guid redItem, _, _) = await CarAndRedAsync(l);
        Project second = l.AddProject();
        Guid carThere = await l.ImportAsync(carItem, second);
        await VariantAsync(l, second, carThere, "Red", name: "Our red car", salt: 62);
        int avatarsBefore;
        using (InferenceApiDbContext before = l.NewDb()) avatarsBefore = await before.ProjectAvatars.CountAsync(a => a.ProjectId == second.Id);

        (int status, JsonElement body) = await l.ImportResultAsync(redItem, second);

        status.Should().Be(409, body.ToString());
        body.GetProperty("code").GetString().Should().Be("VARIANT_LABEL_TAKEN");
        using InferenceApiDbContext db = l.NewDb();
        (await db.ProjectAvatars.CountAsync(a => a.ProjectId == second.Id)).Should().Be(avatarsBefore, "a clash is refused, never dropped or forced");
        (await db.ProjectFiles.CountAsync(f => f.ProjectId == second.Id && f.SourceRevision == AvatarLibraryStorage.SourceRevision(redItem)))
            .Should().Be(0, "no copy is left behind");
    }

    [Fact]
    public async Task A_variant_imported_before_its_original_is_grouped_when_the_original_is_imported()
    {
        L l = Library();
        (Guid carItem, Guid redItem, _, _) = await CarAndRedAsync(l);
        Project second = l.AddProject();

        (int status, JsonElement red) = await l.ImportResultAsync(redItem, second);
        status.Should().Be(201, red.ToString());
        Guid redThere = red.GetProperty("id").GetGuid();
        red.GetProperty("variantOfAvatarId").ValueKind.Should().Be(JsonValueKind.Null, "its original is not in this project yet");
        red.GetProperty("variantLabel").GetString().Should().Be("Red");

        (int carStatus, JsonElement car) = await l.ImportResultAsync(carItem, second);

        carStatus.Should().Be(201, car.ToString());
        Guid carThere = car.GetProperty("id").GetGuid();
        car.GetProperty("variantCount").GetInt32().Should().Be(1);
        (await AvatarAsync(l, second, redThere)).GetProperty("variantOfAvatarId").GetGuid().Should().Be(carThere);
        using InferenceApiDbContext db = l.NewDb();
        ProjectAvatarEvent imported = await db.ProjectAvatarEvents.SingleAsync(e => e.AvatarId == carThere && e.Kind == AvatarEventKind.LibraryImported);
        JsonDocument.Parse(imported.OutcomesJson!).RootElement.GetProperty("variants_grouped").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task A_linked_variant_takes_its_label_and_description_from_the_library()
    {
        L l = Library();
        (_, _, _, Guid red) = await CarAndRedAsync(l);

        (int status, JsonElement body) = H.Read(await l.Harness.Controller().Update(
            l.Project.Id, red, new UpdateProjectAvatarRequest(VariantLabel: "Crimson"), CancellationToken.None));

        status.Should().Be(409, body.ToString());
        body.GetProperty("code").GetString().Should().Be("AVATAR_FROM_LIBRARY");
    }

    // ------------------------------------------------------------------ delete

    [Fact]
    public async Task Deleting_an_original_item_leaves_its_variants_as_items_ungrouped()
    {
        L l = Library();
        (Guid carItem, Guid redItem, _, _) = await CarAndRedAsync(l);

        (int status, JsonElement body) = H.Read(await l.Controller().Delete(carItem, CancellationToken.None));

        status.Should().Be(200, body.ToString());
        H.Read(await l.Controller().Get(carItem, null, CancellationToken.None)).Status.Should().Be(404);
        JsonElement red = await ItemAsync(l, redItem);
        red.GetProperty("variantOfItemId").ValueKind.Should().Be(JsonValueKind.Null);
        red.GetProperty("variantLabel").GetString().Should().Be("Red", "the label stays, as on a project variant whose original was deleted");
        using InferenceApiDbContext db = l.NewDb();
        ProjectAvatarEvent deleted = await db.ProjectAvatarEvents.SingleAsync(e => e.AvatarId == carItem && e.Kind == AvatarEventKind.LibraryDeleted);
        JsonDocument.Parse(deleted.OutcomesJson!).RootElement.GetProperty("variants_ungrouped").GetInt32().Should().Be(1);
    }
}
