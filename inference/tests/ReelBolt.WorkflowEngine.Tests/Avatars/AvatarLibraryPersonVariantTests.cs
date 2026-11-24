#nullable enable
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Avatars.Library;
using ReelBolt.Shared.Avatars;
using ReelBolt.Shared.Data.Models;
using Xunit;
using H = ReelBolt.WorkflowEngine.Tests.Avatars.AvatarTestHarness;
using L = ReelBolt.WorkflowEngine.Tests.Avatars.AvatarLibraryHarness;

namespace ReelBolt.WorkflowEngine.Tests.Avatars;

/// <summary>A person's library variants are that person: withdrawing, deleting, expiring or re-confirming the original reaches them.</summary>
public class AvatarLibraryPersonVariantTests
{
    /// <summary>An original "Anna" and a variant "Anna - Beach" in the library; the variant has one import in another project.</summary>
    private static async Task<(Guid Original, Guid Variant, Guid VariantImport, Project Other)> SeedAsync(L l)
    {
        ProjectFile face = l.Harness.AddImage("anna.png", salt: 1);
        ProjectFile beach = l.Harness.AddImage("beach.png", salt: 2);
        (Guid original, _) = await l.PublishPersonAsync("Anna", face);
        (Guid variant, _) = await l.PublishPersonAsync("Anna - Beach", beach);
        using (InferenceApiDbContext db = l.NewDb())
        {
            AvatarLibraryItem item = await db.AvatarLibraryItems.SingleAsync(i => i.Id == variant);
            item.VariantOfItemId = original;
            item.VariantLabel = "Beach";
            await db.SaveChangesAsync();
        }

        Project other = l.AddProject("Other");
        Guid import = await l.ImportAsync(variant, other);
        return (original, variant, import, other);
    }

    [Fact]
    public async Task Withdrawing_the_original_withdraws_its_variants_and_their_imports_in_the_same_save()
    {
        var l = new L();
        (Guid original, Guid variant, Guid import, _) = await SeedAsync(l);

        H.Read(await l.Controller().RevokeConsent(original, CancellationToken.None)).Status.Should().Be(200);

        using InferenceApiDbContext db = l.NewDb();
        AvatarLibraryItem item = await db.AvatarLibraryItems.SingleAsync(i => i.Id == variant);
        item.ConsentRevokedAt.Should().NotBeNull();
        (await db.ProjectAvatars.SingleAsync(a => a.Id == import)).ConsentRevokedAt.Should().NotBeNull("the variant's import is unusable too");
        (await db.ProjectAvatarEvents.CountAsync(e => e.AvatarId == import && e.Kind == AvatarEventKind.ConsentRevoked)).Should().Be(1);
        (await l.ImportResultAsync(variant, l.AddProject("Third"))).Status.Should().Be(409);
    }

    [Fact]
    public async Task Deleting_the_original_deletes_its_variants_and_their_imports()
    {
        var l = new L();
        (Guid original, Guid variant, Guid import, _) = await SeedAsync(l);

        AvatarLibraryDeleteReport? report = await l.Library().DeleteAsync(l.OrganizationId, original, l.Harness.OwnerId, AvatarLibraryDeleteCause.User, CancellationToken.None);

        report.Should().NotBeNull();
        report!.ItemDeleted.Should().BeTrue();
        using InferenceApiDbContext db = l.NewDb();
        (await db.AvatarLibraryItems.AnyAsync(i => i.Id == variant || i.Id == original)).Should().BeFalse();
        (await db.ProjectAvatars.AnyAsync(a => a.Id == import)).Should().BeFalse("the variant's import went with it");
    }

    [Fact]
    public async Task Deleting_an_object_keeps_its_variants_ungrouped_as_before()
    {
        var l = new L();
        (Guid car, _) = await l.PublishObjectAsync("Car", l.Harness.AddImage("car.png", salt: 3));
        (Guid red, _) = await l.PublishObjectAsync("Red car", l.Harness.AddImage("red.png", salt: 4));
        using (InferenceApiDbContext db = l.NewDb())
        {
            AvatarLibraryItem item = await db.AvatarLibraryItems.SingleAsync(i => i.Id == red);
            item.VariantOfItemId = car;
            item.VariantLabel = "Red";
            await db.SaveChangesAsync();
        }

        // The object's own import (its source avatar) is removed by the delete routine as for any item.
        AvatarLibraryDeleteReport? report = await l.Library().DeleteAsync(l.OrganizationId, car, l.Harness.OwnerId, AvatarLibraryDeleteCause.User, CancellationToken.None);

        report!.ItemDeleted.Should().BeTrue();
        using InferenceApiDbContext check = l.NewDb();
        AvatarLibraryItem survivor = await check.AvatarLibraryItems.SingleAsync(i => i.Id == red);
        survivor.VariantOfItemId.Should().BeNull();
        survivor.VariantLabel.Should().Be("Red");
    }

    [Fact]
    public async Task A_variant_of_an_expired_original_is_unusable_in_the_list_the_import_and_the_picture()
    {
        var l = new L();
        (Guid original, Guid variant, _, _) = await SeedAsync(l);
        using (InferenceApiDbContext db = l.NewDb())
        {
            (await db.AvatarLibraryItems.SingleAsync(i => i.Id == original)).RetentionUntil = DateTime.UtcNow.AddDays(-1);
            await db.SaveChangesAsync();
        }

        AvatarLibraryItemView view = (await l.Library().ListAsync(l.OrganizationId, null, CancellationToken.None)).Single(i => i.Item.Id == variant);
        view.UnusableReason.Should().Be("retention_expired");
        (await l.ImportResultAsync(variant, l.AddProject("Third"))).Status.Should().Be(409);
        Guid picture;
        using (InferenceApiDbContext db = l.NewDb())
            picture = (await db.AvatarLibraryReferences.SingleAsync(r => r.LibraryItemId == variant)).Id;
        H.Read(await l.Controller().Picture(variant, picture, CancellationToken.None)).Status.Should().Be(404);
    }

    [Fact]
    public async Task Re_confirming_the_original_carries_the_new_keep_for_date_to_its_variants_and_their_imports()
    {
        var l = new L();
        (Guid original, Guid variant, Guid import, _) = await SeedAsync(l);

        H.Read(await l.Controller().Reconfirm(original,
            new ReelBolt.Inference.Api.Controllers.Dto.ReconfirmAvatarLibraryItemRequest(AvatarWorkspaceConsentStatement.English, "en", true, 400),
            CancellationToken.None)).Status.Should().Be(200);

        using InferenceApiDbContext db = l.NewDb();
        AvatarLibraryItem originalRow = await db.AvatarLibraryItems.SingleAsync(i => i.Id == original);
        (await db.AvatarLibraryItems.SingleAsync(i => i.Id == variant)).RetentionUntil.Should().Be(originalRow.RetentionUntil);
        (await db.ProjectAvatars.SingleAsync(a => a.Id == import)).RetentionUntil.Should().Be(originalRow.RetentionUntil);
    }
}
