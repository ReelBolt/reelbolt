#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Avatars;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Billing;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Avatars;

/// <summary>
/// Avatars phase 3, lane C3: the stills made from an avatar (bare objects under its stills folder, no rows) are removed
/// with it on every path that removes the avatar: the one delete routine (user delete and the retention sweep) and a
/// project delete. The storage sweep's orphan pass is pinned in <c>StorageRetentionServiceTests</c>.
/// </summary>
public class AvatarStillsDeletionTests
{
    /// <summary>Lists the harness's objects under a category prefix, as the S3 store does.</summary>
    private sealed class Listing(AvatarTestHarness h) : IObjectListing
    {
        public bool Throws { get; set; }

        public Task<IReadOnlyList<StoredObjectEntry>> ListAsync(Guid projectId, string category, CancellationToken ct)
        {
            if (Throws) throw new InvalidOperationException("store cannot list");
            string prefix = $"projects/{projectId}/{category}/";
            return Task.FromResult<IReadOnlyList<StoredObjectEntry>>(
                [.. h.Objects.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).Select(k => new StoredObjectEntry(k, DateTime.UtcNow))]);
        }
    }

    private static ProjectAvatarDeletion Deletion(AvatarTestHarness h, InferenceApiDbContext db, IObjectListing? listing) =>
        new(db, h.Storage.Object, h.Vendor, NullLogger<ProjectAvatarDeletion>.Instance, publish: null, listing: listing);

    private static string Still(AvatarTestHarness h, Guid avatarId, string name)
    {
        string key = AvatarStillStorage.ObjectPrefix(h.Project.Id, avatarId) + name + ".img";
        h.Objects[key] = [1, 2, 3];
        return key;
    }

    [Fact]
    public async Task Deleting_an_object_avatar_removes_its_stills_folder_and_keeps_its_files()
    {
        // Several subjects per clip (docs/design/avatar-objects.md): a scene still that shows only objects is kept under
        // the first object's stills folder, so an object's delete must clear that folder exactly as a person's does.
        var h = new AvatarTestHarness();
        ProjectAvatar machine, anna;
        ProjectFile picture = h.AddImage("machine.png", salt: 3);
        using (InferenceApiDbContext db = h.NewDb())
        {
            machine = AvatarSeed.Attach(db, h.Project.Id, "Espresso machine", picture);
            machine.SubjectKind = AvatarSubjectKind.Object;
            machine.RetentionUntil = ProjectAvatar.NoRetention;
            anna = AvatarSeed.Attach(db, h.Project.Id, "Anna", h.AddImage("anna.png", salt: 4));
            db.SaveChanges();
        }

        string scene1 = Still(h, machine.Id, "scene1"), scene2 = Still(h, machine.Id, "scene2"), annas = Still(h, anna.Id, "a1");

        AvatarDeleteReport? report;
        using (InferenceApiDbContext db = h.NewDb())
        {
            report = await Deletion(h, db, new Listing(h))
                .DeleteAsync(h.Project.Id, machine.Id, h.OrganizationId, h.OwnerId, AvatarDeleteCause.User, CancellationToken.None);
        }

        report!.StillsDeleted.Should().Be(2);
        report.StillsFailed.Should().Be(0);
        h.DeletedKeys.Should().Contain([scene1, scene2]).And.NotContain(annas);
        report.ReferencesKept.Should().Be(1, "an object's reference files are ordinary project files and stay");
        using InferenceApiDbContext check = h.NewDb();
        (await check.ProjectFiles.FindAsync(picture.Id)).Should().NotBeNull();
        (await check.ProjectAvatars.AnyAsync(a => a.Id == machine.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task Deleting_an_avatar_removes_its_stills_and_no_other_avatars()
    {
        var h = new AvatarTestHarness();
        ProjectAvatar anna, marco;
        using (InferenceApiDbContext db = h.NewDb())
        {
            anna = AvatarSeed.Attach(db, h.Project.Id, "Anna", h.AddImage("anna.png", salt: 1));
            marco = AvatarSeed.Attach(db, h.Project.Id, "Marco", h.AddImage("marco.png", salt: 2));
        }

        string a1 = Still(h, anna.Id, "a1"), a2 = Still(h, anna.Id, "a2"), m1 = Still(h, marco.Id, "m1");

        AvatarDeleteReport? report;
        using (InferenceApiDbContext db = h.NewDb())
        {
            report = await Deletion(h, db, new Listing(h))
                .DeleteAsync(h.Project.Id, anna.Id, h.OrganizationId, h.OwnerId, AvatarDeleteCause.User, CancellationToken.None);
        }

        report!.StillsDeleted.Should().Be(2);
        report.StillsFailed.Should().Be(0);
        h.DeletedKeys.Should().Contain([a1, a2]).And.NotContain(m1);
        h.Objects.Should().ContainKey(m1, "another avatar's still is not touched");

        using InferenceApiDbContext check = h.NewDb();
        (await check.StorageRetentionMarks.IgnoreQueryFilters().CountAsync()).Should().Be(0, "a removed still drops its mark");
        ProjectAvatarEvent deleted = await check.ProjectAvatarEvents.SingleAsync(e => e.AvatarId == anna.Id && e.Kind == AvatarEventKind.Deleted);
        JsonDocument.Parse(deleted.OutcomesJson!).RootElement.GetProperty("stills").GetInt32().Should().Be(2);
        deleted.OutcomesJson.Should().NotContain("stills/", "an event never holds a storage key");
    }

    [Fact]
    public async Task A_still_that_cannot_be_removed_yet_is_reported_and_keeps_its_mark_for_the_sweep()
    {
        var h = new AvatarTestHarness();
        ProjectAvatar anna;
        using (InferenceApiDbContext db = h.NewDb())
        {
            anna = AvatarSeed.Attach(db, h.Project.Id, "Anna", h.AddImage("anna.png"));
        }

        string stuck = Still(h, anna.Id, "stuck");
        h.FailingDeletes.Add(stuck);

        AvatarDeleteReport? report;
        using (InferenceApiDbContext db = h.NewDb())
        {
            report = await Deletion(h, db, new Listing(h))
                .DeleteAsync(h.Project.Id, anna.Id, h.OrganizationId, null, AvatarDeleteCause.Retention, CancellationToken.None);
        }

        report!.StillsFailed.Should().Be(1);
        using InferenceApiDbContext check = h.NewDb();
        StorageRetentionMark mark = await check.StorageRetentionMarks.IgnoreQueryFilters().SingleAsync();
        mark.StorageKey.Should().Be(stuck);
        mark.Category.Should().Be("agentFiles");
        (await check.ProjectAvatars.AnyAsync(a => a.Id == anna.Id)).Should().BeFalse("the row goes regardless");
    }

    [Fact]
    public async Task A_store_that_cannot_list_still_deletes_the_avatar_and_says_the_stills_are_left_to_the_sweep()
    {
        var h = new AvatarTestHarness();
        ProjectAvatar anna;
        using (InferenceApiDbContext db = h.NewDb())
        {
            anna = AvatarSeed.Attach(db, h.Project.Id, "Anna", h.AddImage("anna.png"));
        }

        Still(h, anna.Id, "s");
        foreach (IObjectListing? listing in new IObjectListing?[] { null, new Listing(h) { Throws = true } })
        {
            using InferenceApiDbContext db = h.NewDb();
            if (!await db.ProjectAvatars.AnyAsync(a => a.Id == anna.Id))
            {
                anna = AvatarSeed.Attach(db, h.Project.Id, "Anna", h.AddImage("again.png", salt: 9));
            }

            AvatarDeleteReport? report = await Deletion(h, db, listing)
                .DeleteAsync(h.Project.Id, anna.Id, h.OrganizationId, h.OwnerId, AvatarDeleteCause.User, CancellationToken.None);
            report!.StillsFailed.Should().Be(-1);
        }
    }

    [Fact]
    public async Task A_project_delete_marks_the_stills_of_every_avatar_in_the_same_save()
    {
        var h = new AvatarTestHarness();
        ProjectAvatar anna, marco;
        using (InferenceApiDbContext db = h.NewDb())
        {
            anna = AvatarSeed.Attach(db, h.Project.Id, "Anna", h.AddImage("anna.png", salt: 1));
            marco = AvatarSeed.Attach(db, h.Project.Id, "Marco", h.AddImage("marco.png", salt: 2));
        }

        string a1 = Still(h, anna.Id, "a1"), m1 = Still(h, marco.Id, "m1");
        h.Objects[$"projects/{h.Project.Id}/agentFiles/avatars/notes.txt"] = [1];

        using (InferenceApiDbContext db = h.NewDb())
        {
            Project project = await db.Projects.IgnoreQueryFilters().SingleAsync(p => p.Id == h.Project.Id);
            await ProjectAvatarEvents.AddForProjectDeleteAsync(db, project, h.OwnerId, CancellationToken.None, new Listing(h));
            db.Projects.Remove(project);
            await db.SaveChangesAsync();
        }

        using InferenceApiDbContext check = h.NewDb();
        List<StorageRetentionMark> marks = await check.StorageRetentionMarks.IgnoreQueryFilters().ToListAsync();
        marks.Select(m => m.StorageKey).Should().BeEquivalentTo([a1, m1], "only stills are marked, never another shape");
        marks.Should().AllSatisfy(m => m.OrganizationId.Should().Be(h.OrganizationId));
    }

    [Fact]
    public async Task A_project_delete_is_never_refused_because_the_stills_cannot_be_listed()
    {
        var h = new AvatarTestHarness();
        using InferenceApiDbContext db = h.NewDb();
        AvatarSeed.Attach(db, h.Project.Id, "Anna", h.AddImage("anna.png"));
        Project project = await db.Projects.IgnoreQueryFilters().SingleAsync(p => p.Id == h.Project.Id);

        Func<Task> act = () => ProjectAvatarEvents.AddForProjectDeleteAsync(
            db, project, h.OwnerId, CancellationToken.None, new Listing(h) { Throws = true });

        await act.Should().NotThrowAsync();
    }
}
