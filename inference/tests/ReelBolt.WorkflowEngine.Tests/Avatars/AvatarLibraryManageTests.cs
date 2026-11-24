#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Avatars;
using ReelBolt.Shared.Data.Models;
using Xunit;
using H = ReelBolt.WorkflowEngine.Tests.Avatars.AvatarTestHarness;
using L = ReelBolt.WorkflowEngine.Tests.Avatars.AvatarLibraryHarness;

namespace ReelBolt.WorkflowEngine.Tests.Avatars;

/// <summary>
/// Changing a workspace avatar library item (lane L9 of docs/design/avatar-objects.md): a rename and a reconfirmation
/// reach every import, withdrawing consent stamps the item and every import in one save, and a delete removes the
/// imports through the project delete routine, then the item and its objects, and reports what it removed.
/// </summary>
public class AvatarLibraryManageTests
{
    /// <summary>An item with the source import plus two more imports in two other projects.</summary>
    private static async Task<(L L, Guid ItemId, Guid Source, Guid[] Imports, Project[] Projects)> ThreeImportsAsync(bool isObject = false)
    {
        var l = new L();
        (Guid itemId, Guid source) = isObject ? await l.PublishObjectAsync("Machine") : await l.PublishPersonAsync("Anna");
        Project second = l.AddProject("Second");
        Project third = l.AddProject("Third");
        Guid a = await l.ImportAsync(itemId, second);
        Guid b = await l.ImportAsync(itemId, third);
        return (l, itemId, source, [source, a, b], [l.Project, second, third]);
    }

    // ------------------------------------------------------------------ rename, describe

    [Fact]
    public async Task A_rename_and_a_new_description_reach_every_import()
    {
        (L l, Guid itemId, _, Guid[] imports, _) = await ThreeImportsAsync();

        (int status, JsonElement body) = H.Read(await l.Controller().Update(
            itemId, new UpdateAvatarLibraryItemRequest(" Anna R. ", " Presenter "), CancellationToken.None));

        status.Should().Be(200, body.ToString());
        body.GetProperty("name").GetString().Should().Be("Anna R.");
        body.GetProperty("description").GetString().Should().Be("Presenter");
        body.GetProperty("importCount").GetInt32().Should().Be(3);
        using (InferenceApiDbContext db = l.NewDb())
        {
            (await db.ProjectAvatars.Where(a => imports.Contains(a.Id)).ToListAsync())
                .Should().OnlyContain(a => a.Name == "Anna R." && a.Description == "Presenter");
        }

        // "" clears the description everywhere; a null field is left as it is.
        H.Read(await l.Controller().Update(itemId, new UpdateAvatarLibraryItemRequest(Description: ""), CancellationToken.None)).Status.Should().Be(200);
        using InferenceApiDbContext read = l.NewDb();
        (await read.ProjectAvatars.Where(a => imports.Contains(a.Id)).ToListAsync())
            .Should().OnlyContain(a => a.Name == "Anna R." && a.Description == null);
        (await read.AvatarLibraryItems.SingleAsync()).Description.Should().BeNull();
    }

    [Fact]
    public async Task A_rename_is_refused_when_the_name_is_invalid_taken_in_the_library_or_taken_in_an_importing_project()
    {
        (L l, Guid itemId, _, Guid[] imports, Project[] projects) = await ThreeImportsAsync();
        await l.PublishPersonAsync("Bea");
        ProjectFile other = l.Harness.AddFile("carla.png", "image/png", H.Png(1024, 1024, 71), projectId: projects[2].Id);
        using (InferenceApiDbContext db = l.NewDb())
            AvatarSeed.Attach(db, projects[2].Id, "Carla", other);

        (int empty, JsonElement emptyBody) = H.Read(await l.Controller().Update(itemId, new(Name: "  "), CancellationToken.None));
        empty.Should().Be(400);
        emptyBody.GetProperty("code").GetString().Should().Be("name_invalid");
        H.Read(await l.Controller().Update(itemId, new(Name: new string('x', 201)), CancellationToken.None)).Status.Should().Be(400);
        H.Read(await l.Controller().Update(itemId, new(Description: new string('x', 2001)), CancellationToken.None)).Body
            .GetProperty("code").GetString().Should().Be("description_too_long");

        (int taken, JsonElement takenBody) = H.Read(await l.Controller().Update(itemId, new(Name: "Bea"), CancellationToken.None));
        taken.Should().Be(409);
        takenBody.GetProperty("code").GetString().Should().Be("LIBRARY_NAME_TAKEN");

        (int clash, JsonElement clashBody) = H.Read(await l.Controller().Update(itemId, new(Name: "Carla"), CancellationToken.None));
        clash.Should().Be(409, "an import that could not take the name would no longer follow its item");
        clashBody.GetProperty("code").GetString().Should().Be("AVATAR_NAME_TAKEN");
        clashBody.GetProperty("projects").GetInt32().Should().Be(1);

        using InferenceApiDbContext read = l.NewDb();
        (await read.AvatarLibraryItems.SingleAsync(i => i.Id == itemId)).Name.Should().Be("Anna");
        (await read.ProjectAvatars.Where(a => imports.Contains(a.Id)).ToListAsync()).Should().OnlyContain(a => a.Name == "Anna");
    }

    // ------------------------------------------------------------------ reconfirm

    [Fact]
    public async Task A_reconfirmation_stamps_the_item_and_every_import_and_starts_the_period_again()
    {
        (L l, Guid itemId, _, Guid[] imports, _) = await ThreeImportsAsync();
        Guid admin = Guid.NewGuid();
        DateTime attestedBefore;
        using (InferenceApiDbContext db = l.NewDb())
            attestedBefore = (await db.AvatarLibraryItems.SingleAsync()).ConsentAttestedAt;
        await Task.Delay(5);

        (int status, JsonElement body) = H.Read(await l.Controller(user: new H.User(admin, l.OrganizationId, ReelBolt.Shared.Tenancy.OrganizationRole.Admin, true))
            .Reconfirm(itemId, new ReconfirmAvatarLibraryItemRequest(AvatarWorkspaceConsentStatement.Italian, "it", true, 30), CancellationToken.None));

        status.Should().Be(200, body.ToString());
        body.GetProperty("retentionUntil").GetDateTime().Should().BeCloseTo(DateTime.UtcNow.AddDays(30), TimeSpan.FromMinutes(1));
        using InferenceApiDbContext read = l.NewDb();
        AvatarLibraryItem item = await read.AvatarLibraryItems.SingleAsync();
        item.ConsentStatement.Should().Be(AvatarWorkspaceConsentStatement.Italian);
        item.ConsentLocale.Should().Be("it");
        item.ConsentAttestedByUserId.Should().Be(admin);
        item.ConsentAttestedAt.Should().BeAfter(attestedBefore);
        List<ProjectAvatar> rows = await read.ProjectAvatars.Where(a => imports.Contains(a.Id)).ToListAsync();
        rows.Should().HaveCount(3);
        rows.Should().OnlyContain(a => a.ConsentStatement == AvatarWorkspaceConsentStatement.Italian && a.ConsentLocale == "it"
                                       && a.ConsentAttestedByUserId == admin && a.ConsentAttestedAt == item.ConsentAttestedAt
                                       && a.RetentionUntil == item.RetentionUntil);
        (await read.ProjectAvatarReferences.CountAsync()).Should().Be(3, "no file changes: new files are a new item");
        ProjectAvatarEvent reconfirmed = await read.ProjectAvatarEvents.SingleAsync(e => e.Kind == AvatarEventKind.LibraryReconfirmed);
        reconfirmed.AvatarId.Should().Be(itemId);
        reconfirmed.OutcomesJson.Should().Contain("\"imports\":3").And.Contain("\"retention_days\":30");
    }

    [Fact]
    public async Task A_reconfirmation_needs_the_workspace_statement_and_is_final_after_a_revoke()
    {
        (L l, Guid itemId, _, Guid[] imports, _) = await ThreeImportsAsync();

        (int wrong, JsonElement wrongBody) = H.Read(await l.Controller().Reconfirm(
            itemId, new ReconfirmAvatarLibraryItemRequest(AvatarConsentStatement.English, "en", true, 30), CancellationToken.None));
        wrong.Should().Be(400);
        wrongBody.GetProperty("code").GetString().Should().Be("consent_statement_required");
        wrongBody.GetProperty("statements").GetProperty("en").GetString().Should().Be(AvatarWorkspaceConsentStatement.English);
        H.Read(await l.Controller().Reconfirm(
            itemId, new ReconfirmAvatarLibraryItemRequest(AvatarWorkspaceConsentStatement.English, "en", false, 30), CancellationToken.None))
            .Body.GetProperty("code").GetString().Should().Be("subject_must_be_adult");
        H.Read(await l.Controller(user: l.Harness.Owner(emailVerified: false)).Reconfirm(
            itemId, new ReconfirmAvatarLibraryItemRequest(AvatarWorkspaceConsentStatement.English, "en", true, 30), CancellationToken.None))
            .Status.Should().Be(403);
        using (InferenceApiDbContext db = l.NewDb())
        {
            (await db.ProjectAvatars.Where(a => imports.Contains(a.Id)).ToListAsync()).Should().OnlyContain(a => a.ConsentLocale == "en");
        }

        H.Read(await l.Controller().RevokeConsent(itemId, CancellationToken.None)).Status.Should().Be(200);
        (int revoked, JsonElement revokedBody) = H.Read(await l.Controller().Reconfirm(
            itemId, new ReconfirmAvatarLibraryItemRequest(AvatarWorkspaceConsentStatement.English, "en", true, 30), CancellationToken.None));
        revoked.Should().Be(409);
        revokedBody.GetProperty("code").GetString().Should().Be("AVATAR_CONSENT_REVOKED");
    }

    [Fact]
    public async Task An_object_is_reconfirmed_under_the_workspace_object_statement()
    {
        (L l, Guid itemId, _, Guid[] imports, _) = await ThreeImportsAsync(isObject: true);

        H.Read(await l.Controller().Reconfirm(
            itemId, new ReconfirmAvatarLibraryItemRequest(AvatarWorkspaceConsentStatement.English, "en"), CancellationToken.None))
            .Body.GetProperty("code").GetString().Should().Be("object_statement_required");
        (int status, JsonElement body) = H.Read(await l.Controller().Reconfirm(
            itemId, new ReconfirmAvatarLibraryItemRequest(AvatarWorkspaceObjectStatement.Italian, "it"), CancellationToken.None));

        status.Should().Be(200, body.ToString());
        body.GetProperty("retentionUntil").ValueKind.Should().Be(JsonValueKind.Null);
        using InferenceApiDbContext read = l.NewDb();
        (await read.ProjectAvatars.Where(a => imports.Contains(a.Id)).ToListAsync())
            .Should().OnlyContain(a => a.ConsentStatement == AvatarWorkspaceObjectStatement.Italian && a.RetentionUntil == ProjectAvatar.NoRetention);
    }

    // ------------------------------------------------------------------ revoke

    /// <summary>Records, per save, which avatar rows and library items it changed.</summary>
    private sealed class SaveLog : SaveChangesInterceptor
    {
        public List<(int Items, int Avatars)> Saves { get; } = [];

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            DbContext db = eventData.Context!;
            int items = db.ChangeTracker.Entries<AvatarLibraryItem>().Count(e => e.State == EntityState.Modified);
            int avatars = db.ChangeTracker.Entries<ProjectAvatar>().Count(e => e.State == EntityState.Modified);
            if (items + avatars > 0) Saves.Add((items, avatars));
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    [Fact]
    public async Task Withdrawing_consent_stamps_the_item_and_every_import_in_one_save()
    {
        (L l, Guid itemId, _, Guid[] imports, Project[] projects) = await ThreeImportsAsync();
        var log = new SaveLog();
        using var tracked = new InferenceApiDbContext(new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase(l.Harness.DatabaseName).AddInterceptors(log).Options);

        (int status, JsonElement body) = H.Read(await l.Controller(tracked).RevokeConsent(itemId, CancellationToken.None));

        status.Should().Be(200, body.ToString());
        body.GetProperty("usable").GetBoolean().Should().BeFalse();
        body.GetProperty("unusableReason").GetString().Should().Be("consent_revoked");
        log.Saves.Should().Equal([(1, 3)], "the item and all three imports are written by ONE save, so no project keeps using the person");

        using InferenceApiDbContext read = l.NewDb();
        AvatarLibraryItem item = await read.AvatarLibraryItems.SingleAsync();
        item.ConsentRevokedAt.Should().NotBeNull();
        item.ConsentRevokedByUserId.Should().Be(l.Harness.OwnerId);
        List<ProjectAvatar> rows = await read.ProjectAvatars.Where(a => imports.Contains(a.Id)).ToListAsync();
        rows.Should().OnlyContain(a => a.ConsentRevokedAt == item.ConsentRevokedAt && a.ConsentRevokedByUserId == l.Harness.OwnerId);

        // Every import is unselectable at once, by the one rule, in its own project.
        foreach ((Guid import, Project project) in imports.Zip(projects))
        {
            JsonElement avatar = H.Read(await l.Harness.Controller().Get(project.Id, import, CancellationToken.None)).Body;
            avatar.GetProperty("selectable").GetBoolean().Should().BeFalse();
            avatar.GetProperty("unselectableReason").GetString().Should().Be("consent_revoked");
        }

        List<ProjectAvatarEvent> events = await read.ProjectAvatarEvents
            .Where(e => e.Kind == AvatarEventKind.LibraryConsentRevoked || e.Kind == AvatarEventKind.ConsentRevoked).ToListAsync();
        events.Count(e => e.Kind == AvatarEventKind.LibraryConsentRevoked).Should().Be(1);
        events.Where(e => e.Kind == AvatarEventKind.ConsentRevoked).Select(e => e.AvatarId).Should().BeEquivalentTo(imports);
        events.Where(e => e.Kind == AvatarEventKind.ConsentRevoked).Should().OnlyContain(e => e.OutcomesJson == "{\"via\":\"library\"}");

        // Idempotent: the first revoke's time and actor stay, and no second event is written.
        DateTime first = item.ConsentRevokedAt!.Value;
        H.Read(await l.Controller(user: l.Harness.InRole(ReelBolt.Shared.Tenancy.OrganizationRole.Admin)).RevokeConsent(itemId, CancellationToken.None))
            .Status.Should().Be(200);
        using InferenceApiDbContext again = l.NewDb();
        (await again.AvatarLibraryItems.SingleAsync()).ConsentRevokedAt.Should().Be(first);
        (await again.AvatarLibraryItems.SingleAsync()).ConsentRevokedByUserId.Should().Be(l.Harness.OwnerId);
        (await again.ProjectAvatarEvents.CountAsync(e => e.Kind == AvatarEventKind.LibraryConsentRevoked)).Should().Be(1);
    }

    [Fact]
    public async Task A_revoke_that_loses_a_race_is_retried_and_still_lands()
    {
        (L l, Guid itemId, _, Guid[] imports, _) = await ThreeImportsAsync();
        // The first save fails the way PostgreSQL's conditional UPDATE does when a row changed under it.
        l.Harness.Interceptor.Action = () => Task.CompletedTask;

        (int status, _) = H.Read(await l.Controller(l.NewDb(intercepted: true)).RevokeConsent(itemId, CancellationToken.None));

        status.Should().Be(200);
        using InferenceApiDbContext read = l.NewDb();
        (await read.AvatarLibraryItems.SingleAsync()).ConsentRevokedAt.Should().NotBeNull();
        (await read.ProjectAvatars.Where(a => imports.Contains(a.Id)).ToListAsync()).Should().OnlyContain(a => a.ConsentRevokedAt != null);
        (await read.ProjectAvatarEvents.CountAsync(e => e.Kind == AvatarEventKind.LibraryConsentRevoked)).Should().Be(1, "the lost attempt wrote nothing");
    }

    [Fact]
    public async Task An_object_has_no_consent_to_withdraw()
    {
        var l = new L();
        (Guid itemId, _) = await l.PublishObjectAsync("Machine");

        (int status, JsonElement body) = H.Read(await l.Controller().RevokeConsent(itemId, CancellationToken.None));

        status.Should().Be(409);
        body.GetProperty("code").GetString().Should().Be("AVATAR_NOT_A_PERSON");
        using InferenceApiDbContext read = l.NewDb();
        (await read.AvatarLibraryItems.SingleAsync()).ConsentRevokedAt.Should().BeNull();
    }

    // ------------------------------------------------------------------ delete

    [Fact]
    public async Task Deleting_an_item_removes_its_imports_with_their_files_then_the_item_and_its_objects_and_reports_it()
    {
        (L l, Guid itemId, Guid source, Guid[] imports, Project[] projects) = await ThreeImportsAsync();
        List<string> importKeys = [.. imports.SelectMany(i => l.FilesOf(i)).Select(f => f.StorageKey)];
        List<string> libraryKeys = l.Store.Keys();
        libraryKeys.Should().HaveCount(1);

        (int status, JsonElement body) = H.Read(await l.Controller().Delete(itemId, CancellationToken.None));

        status.Should().Be(200, body.ToString());
        body.GetRawText().Should().Be("{\"imports\":{\"deleted\":3,\"failed\":0},\"objects\":{\"deleted\":1,\"failed\":0}}");
        using InferenceApiDbContext read = l.NewDb();
        (await read.AvatarLibraryItems.CountAsync()).Should().Be(0);
        (await read.AvatarLibraryReferences.CountAsync()).Should().Be(0);
        (await read.ProjectAvatars.CountAsync()).Should().Be(0);
        (await read.ProjectAvatarReferences.CountAsync()).Should().Be(0);
        (await read.ProjectFiles.CountAsync()).Should().Be(0, "a person's import goes with all its reference files, in every project");
        l.Harness.Objects.Keys.Should().NotContain(importKeys).And.NotContain(libraryKeys);
        l.Store.Keys().Should().BeEmpty();

        ProjectAvatarEvent deleted = await read.ProjectAvatarEvents.SingleAsync(e => e.Kind == AvatarEventKind.LibraryDeleted);
        deleted.AvatarId.Should().Be(itemId);
        deleted.ActorUserId.Should().Be(l.Harness.OwnerId);
        deleted.OutcomesJson.Should().Contain("\"cause\":\"user\"").And.Contain("\"imports_deleted\":3").And.NotContain("Anna");
        List<ProjectAvatarEvent> importEvents = await read.ProjectAvatarEvents.Where(e => e.Kind == AvatarEventKind.Deleted).ToListAsync();
        importEvents.Select(e => e.AvatarId).Should().BeEquivalentTo(imports);
        importEvents.Should().OnlyContain(e => e.OutcomesJson!.Contains("\"cause\":\"library_item_deleted\""));

        H.Read(await l.Controller().Delete(itemId, CancellationToken.None)).Status.Should().Be(404);
    }

    [Fact]
    public async Task An_object_that_could_not_be_removed_is_reported_as_failed()
    {
        var l = new L();
        (Guid itemId, _) = await l.PublishObjectAsync("Machine");
        string stuck = l.Store.Keys().Single();
        l.Store.FailingDeletes.Add(stuck);

        (int status, JsonElement body) = H.Read(await l.Controller().Delete(itemId, CancellationToken.None));

        status.Should().Be(200);
        body.GetProperty("imports").GetProperty("deleted").GetInt32().Should().Be(1);
        body.GetProperty("objects").GetProperty("deleted").GetInt32().Should().Be(0);
        body.GetProperty("objects").GetProperty("failed").GetInt32().Should().Be(1);
        using InferenceApiDbContext read = l.NewDb();
        (await read.AvatarLibraryItems.CountAsync()).Should().Be(0, "the item goes even when an object could not be removed yet");
        l.Store.Keys().Should().Equal(stuck);

        // The storage sweep's orphan pass removes an object whose item is gone, once it is old enough.
        l.Store.FailingDeletes.Clear();
        (await l.Library().SweepOrphanObjectsAsync(DateTime.UtcNow, TimeSpan.FromHours(24), 100, CancellationToken.None)).Should().Be(0, "too young");
        l.Store.Modified[stuck] = DateTime.UtcNow.AddHours(-25);
        (await l.Library().SweepOrphanObjectsAsync(DateTime.UtcNow, TimeSpan.FromHours(24), 100, CancellationToken.None)).Should().Be(1);
        l.Store.Keys().Should().BeEmpty();
    }

    [Fact]
    public async Task When_an_import_cannot_be_removed_the_item_is_kept_and_the_answer_says_so()
    {
        (L l, Guid itemId, _, _, _) = await ThreeImportsAsync();
        // The first save (the first import's delete) throws.
        l.Harness.Interceptor.ThenConflict = false;
        l.Harness.Interceptor.Action = () => throw new InvalidOperationException("database unavailable");

        (int status, JsonElement body) = H.Read(await l.Controller(l.NewDb(intercepted: true)).Delete(itemId, CancellationToken.None));

        // The design's report, as the web dialog reads it: the failed import is counted and nothing of the item went.
        status.Should().Be(200, body.ToString());
        body.GetRawText().Should().Be("{\"imports\":{\"deleted\":2,\"failed\":1},\"objects\":{\"deleted\":0,\"failed\":0}}");
        using (InferenceApiDbContext read = l.NewDb())
        {
            (await read.AvatarLibraryItems.CountAsync()).Should().Be(1, "the master is never deleted from under an import");
            (await read.ProjectAvatars.CountAsync()).Should().Be(1);
            l.Store.Keys().Should().HaveCount(1);
        }

        // Asked again, it finishes.
        (int again, JsonElement report) = H.Read(await l.Controller().Delete(itemId, CancellationToken.None));
        again.Should().Be(200);
        report.GetProperty("imports").GetProperty("deleted").GetInt32().Should().Be(1);
        l.Store.Keys().Should().BeEmpty();
    }

    [Fact]
    public async Task The_orphan_pass_keeps_everything_an_item_names()
    {
        var l = new L();
        ProjectFile face = l.Harness.AddImage("carla.png", salt: 3);
        ProjectFile release = l.Harness.AddFile("release.pdf", "application/pdf", [1, 2, 3, 4]);
        (int created, JsonElement avatar) = H.Read(await l.Harness.Controller().Create(
            l.Project.Id, H.Create((face.Id, "Face")) with { Name = "Carla", ConsentDocumentFileId = release.Id }, CancellationToken.None));
        created.Should().Be(201);
        Guid itemId = await l.PublishAsync(L.PersonRequest(l.Project.Id, avatar.GetProperty("id").GetGuid()));
        // A copy a failed publish left in the item's folder, and an object of an item that no longer exists.
        string stray = AvatarLibraryStorage.ReferenceKey(l.OrganizationId, itemId, Guid.NewGuid(), "png");
        string gone = AvatarLibraryStorage.ReferenceKey(l.OrganizationId, Guid.NewGuid(), Guid.NewGuid(), "png");
        l.Harness.Objects[stray] = [1];
        l.Harness.Objects[gone] = [2];
        foreach (string key in l.Store.Keys()) l.Store.Modified[key] = DateTime.UtcNow.AddDays(-2);

        int removed = await l.Library().SweepOrphanObjectsAsync(DateTime.UtcNow, TimeSpan.FromHours(24), 100, CancellationToken.None);

        removed.Should().Be(2);
        l.Store.Keys().Should().HaveCount(2, "the reference and the signed release of the live item stay");
        l.Store.Keys().Should().NotContain(stray).And.NotContain(gone);
    }
}
