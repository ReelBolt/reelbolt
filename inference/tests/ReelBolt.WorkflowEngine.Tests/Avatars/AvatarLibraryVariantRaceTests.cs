#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Avatars.Library;
using ReelBolt.Shared.Avatars;
using ReelBolt.Shared.Data.Models;
using Xunit;
using L = ReelBolt.WorkflowEngine.Tests.Avatars.AvatarLibraryHarness;

namespace ReelBolt.WorkflowEngine.Tests.Avatars;

/// <summary>A person's variant that appears while the original is being deleted or withdrawn is never left usable on its own.</summary>
public class AvatarLibraryVariantRaceTests
{
    [Fact]
    public async Task A_person_variant_published_during_a_delete_is_deleted_not_ungrouped()
    {
        var l = new L();
        ProjectFile face = l.Harness.AddImage("anna.png", salt: 1);
        ProjectFile beach = l.Harness.AddImage("beach.png", salt: 2);
        (Guid original, _) = await l.PublishPersonAsync("Anna", face);
        (Guid late, _) = await l.PublishPersonAsync("Anna - Beach", beach);
        bool fired = false;
        // The seam: after the delete has read its variants (none yet) and before its guarded section.
        l.Store.OnList = () =>
        {
            if (fired) return;
            fired = true;
            using InferenceApiDbContext db = l.NewDb();
            AvatarLibraryItem item = db.AvatarLibraryItems.Single(i => i.Id == late);
            item.VariantOfItemId = original;
            item.VariantLabel = "Beach";
            db.SaveChanges();
        };

        AvatarLibraryDeleteReport? report = await l.Library().DeleteAsync(l.OrganizationId, original, l.Harness.OwnerId, AvatarLibraryDeleteCause.User, CancellationToken.None);

        fired.Should().BeTrue();
        report!.ItemDeleted.Should().BeTrue();
        using InferenceApiDbContext check = l.NewDb();
        (await check.AvatarLibraryItems.AnyAsync(i => i.Id == late)).Should().BeFalse("it is the person's variant: deleted, never left as an independent usable item");
    }

    [Fact]
    public async Task A_person_variant_that_appears_after_a_withdrawal_is_unusable_on_every_read_path()
    {
        var l = new L();
        ProjectFile face = l.Harness.AddImage("anna.png", salt: 1);
        ProjectFile beach = l.Harness.AddImage("beach.png", salt: 2);
        (Guid original, _) = await l.PublishPersonAsync("Anna", face);
        (Guid late, _) = await l.PublishPersonAsync("Anna - Beach", beach);
        await l.Library().RevokeAsync(l.OrganizationId, original, l.Harness.OwnerId, CancellationToken.None);
        using (InferenceApiDbContext db = l.NewDb())
        {
            AvatarLibraryItem item = await db.AvatarLibraryItems.SingleAsync(i => i.Id == late);
            item.VariantOfItemId = original; // linked after the revoke already stamped the variants
            item.VariantLabel = "Beach";
            await db.SaveChangesAsync();
        }

        (await l.Library().ListAsync(l.OrganizationId, null, CancellationToken.None)).Single(i => i.Item.Id == late).UnusableReason.Should().NotBeNull();
        (await l.ImportResultAsync(late, l.AddProject("Other"))).Status.Should().Be(409);
    }

    private static void MoveProject(L l, Guid projectId)
    {
        using InferenceApiDbContext db = l.NewDb();
        db.Projects.IgnoreQueryFilters().Single(p => p.Id == projectId).OrganizationId = Guid.NewGuid();
        db.SaveChanges();
    }

    [Fact]
    public async Task A_publish_that_races_a_transfer_of_its_project_is_answered_missing_and_leaves_no_copy()
    {
        var l = new L();
        ProjectFile face = l.Harness.AddImage("anna.png", salt: 1);
        Guid avatarId = await l.Harness.CreateReadyAsync("Anna", face);
        // The seam: the project is transferred after the publish started and before its guarded section.
        l.Store.OnCopy = () => { l.Store.OnCopy = null; MoveProject(l, l.Project.Id); };

        (int status, _) = AvatarTestHarness.Read(await l.Controller().Publish(L.PersonRequest(l.Project.Id, avatarId), CancellationToken.None));

        status.Should().Be(404);
        using InferenceApiDbContext db = l.NewDb();
        (await db.AvatarLibraryItems.IgnoreQueryFilters().CountAsync()).Should().Be(0);
        l.Store.Keys().Should().BeEmpty();
    }

    [Fact]
    public async Task An_import_into_a_project_that_was_transferred_meanwhile_is_answered_missing_and_writes_nothing()
    {
        var l = new L();
        ProjectFile face = l.Harness.AddImage("anna.png", salt: 1);
        (Guid item, _) = await l.PublishPersonAsync("Anna", face);
        Project other = l.AddProject("Other");
        l.Store.OnCopy = () => { l.Store.OnCopy = null; MoveProject(l, other.Id); };

        (int status, _) = await l.ImportResultAsync(item, other);

        status.Should().Be(404);
        using InferenceApiDbContext db = l.NewDb();
        (await db.ProjectAvatars.IgnoreQueryFilters().CountAsync(a => a.ProjectId == other.Id)).Should().Be(0);
    }
}
