#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Tenancy;
using Xunit;
using H = ReelBolt.WorkflowEngine.Tests.Avatars.AvatarTestHarness;

namespace ReelBolt.WorkflowEngine.Tests.Avatars;

/// <summary>
/// A variant of a PERSON shows the same person under the same consent: withdrawing or deleting the original takes its
/// variants with it, and the one selectability rule refuses a variant whose original is revoked, expired, deleted or
/// missing even when the variant's own row was never touched. Objects keep their behaviour (variants are ungrouped).
/// </summary>
public class PersonVariantWithdrawalTests
{
    private static H Both()
    {
        var h = new H();
        h.Options.Enabled = true;
        h.Options.Objects.Enabled = true;
        return h;
    }

    private static async Task<Guid> PersonVariantAsync(H h, Guid original, string label = "Suit", byte salt = 90)
    {
        ProjectFile photo = h.AddImage($"variant-{label}.png", salt: salt);
        (int status, JsonElement body) = H.Read(await h.Controller().Create(h.Project.Id, H.Create((photo.Id, "Face")) with
        {
            Name = null!,
            ConsentSubjectName = null,
            ConsentBasis = null,
            VariantOfAvatarId = original,
            VariantLabel = label,
            VariantDescription = "in a dark suit"
        }, CancellationToken.None));
        status.Should().Be(201, body.ToString());
        return body.GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> GetAsync(H h, Guid id)
    {
        (int status, JsonElement body) = H.Read(await h.Controller().Get(h.Project.Id, id, CancellationToken.None));
        status.Should().Be(200);
        return body;
    }

    private static async Task<(bool Selectable, string? Reason)> ListedAsync(H h, Guid id)
    {
        (int status, JsonElement list) = H.Read(await h.Controller().List(h.Project.Id, CancellationToken.None));
        status.Should().Be(200);
        JsonElement row = list.EnumerateArray().Single(e => e.GetProperty("id").GetGuid() == id);
        return (row.GetProperty("selectable").GetBoolean(), row.GetProperty("unselectableReason").GetString());
    }

    [Fact]
    public async Task Revoking_the_original_revokes_every_variant_in_the_same_save_with_the_same_stamp_and_an_event_each()
    {
        H h = Both();
        Guid anna = await h.CreateReadyAsync("Anna");
        Guid suit = await PersonVariantAsync(h, anna, "Suit", 90);
        Guid beach = await PersonVariantAsync(h, anna, "Beach", 91);

        (int status, _) = H.Read(await h.Controller().RevokeConsent(h.Project.Id, anna, CancellationToken.None));
        status.Should().Be(200);

        using InferenceApiDbContext db = h.NewDb();
        List<ProjectAvatar> rows = await db.ProjectAvatars.Where(a => a.Id == anna || a.Id == suit || a.Id == beach).ToListAsync();
        rows.Should().OnlyContain(a => a.ConsentRevokedAt != null && a.ConsentRevokedByUserId == h.OwnerId);
        rows.Select(a => a.ConsentRevokedAt).Distinct().Should().ContainSingle("one stamp for the original and its variants");
        (await db.ProjectAvatarEvents.CountAsync(e => e.Kind == AvatarEventKind.ConsentRevoked)).Should().Be(3);
        (await ListedAsync(h, suit)).Should().Be((false, "consent_revoked"));
        (await GetAsync(h, beach)).GetProperty("unselectableReason").GetString().Should().Be("consent_revoked");
    }

    [Fact]
    public async Task Revoking_one_variant_leaves_the_original_and_the_others_usable()
    {
        H h = Both();
        Guid anna = await h.CreateReadyAsync("Anna");
        Guid suit = await PersonVariantAsync(h, anna, "Suit", 90);
        Guid beach = await PersonVariantAsync(h, anna, "Beach", 91);
        H.Read(await h.Controller().RevokeConsent(h.Project.Id, suit, CancellationToken.None)).Status.Should().Be(200);

        (await ListedAsync(h, suit)).Selectable.Should().BeFalse();
        (await ListedAsync(h, anna)).Selectable.Should().BeTrue();
        (await ListedAsync(h, beach)).Selectable.Should().BeTrue();
    }

    [Fact]
    public async Task A_variant_whose_original_was_revoked_by_any_path_is_not_selectable_in_the_list_by_id_and_the_linter_fact()
    {
        H h = Both();
        Guid anna = await h.CreateReadyAsync("Anna");
        Guid suit = await PersonVariantAsync(h, anna);
        // The original alone is stamped (as a path that does not know about variants would): the variant row is untouched.
        using (InferenceApiDbContext db = h.NewDb())
        {
            ProjectAvatar original = await db.ProjectAvatars.SingleAsync(a => a.Id == anna);
            original.ConsentRevokedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        (await ListedAsync(h, suit)).Should().Be((false, "consent_revoked"));
        (await GetAsync(h, suit)).GetProperty("selectable").GetBoolean().Should().BeFalse();

        using InferenceApiDbContext facts = h.NewDb();
        var lint = new DbWorkflowLintFacts(facts, Mock.Of<IInferenceProviderResolver>(), Mock.Of<IOrganizationEntitlements>(), h.OrganizationId, h.Project.Id);
        LintAvatarFact? fact = await lint.ProjectAvatarAsync(suit, CancellationToken.None);
        (fact!.Exists, fact.Usable, fact.UnusableReason).Should().Be((true, false, "consent_revoked"));
    }

    [Fact]
    public async Task A_variant_whose_original_expired_or_is_missing_is_not_selectable()
    {
        H h = Both();
        Guid anna = await h.CreateReadyAsync("Anna");
        Guid suit = await PersonVariantAsync(h, anna);
        using (InferenceApiDbContext db = h.NewDb())
        {
            (await db.ProjectAvatars.SingleAsync(a => a.Id == anna)).RetentionUntil = DateTime.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }

        (await ListedAsync(h, suit)).Should().Be((false, "retention_expired"));

        // Ungrouped and missing: the original row is gone behind the variant's back (a database-level SET NULL).
        using (InferenceApiDbContext db = h.NewDb())
        {
            ProjectAvatar variant = await db.ProjectAvatars.SingleAsync(a => a.Id == suit);
            variant.VariantOfAvatarId = Guid.NewGuid();
            await db.SaveChangesAsync();
        }

        (await ListedAsync(h, suit)).Should().Be((false, "consent_revoked"));
    }

    [Fact]
    public async Task Re_confirming_the_original_moves_its_variants_keep_until_date_with_it()
    {
        H h = Both();
        Guid anna = await h.CreateReadyAsync("Anna");
        Guid suit = await PersonVariantAsync(h, anna);
        ProjectFile photo = h.AddImage("anna-again.png", salt: 120);
        (int status, JsonElement body) = H.Read(await h.Controller().ReplaceReferences(
            h.Project.Id, anna, ProjectAvatarsControllerTests.Reattest(photo) with { RetentionDays = 30 }, CancellationToken.None));
        status.Should().Be(200, body.ToString());

        using InferenceApiDbContext db = h.NewDb();
        ProjectAvatar original = await db.ProjectAvatars.SingleAsync(a => a.Id == anna);
        ProjectAvatar variant = await db.ProjectAvatars.SingleAsync(a => a.Id == suit);
        variant.RetentionUntil.Should().Be(original.RetentionUntil);
        (await ListedAsync(h, suit)).Selectable.Should().BeTrue();
    }

    [Fact]
    public async Task Deleting_a_person_deletes_its_variants_and_their_files()
    {
        H h = Both();
        Guid anna = await h.CreateReadyAsync("Anna");
        Guid suit = await PersonVariantAsync(h, anna, "Suit", 90);
        Guid beach = await PersonVariantAsync(h, anna, "Beach", 91);
        List<string> keys;
        using (InferenceApiDbContext db = h.NewDb())
        {
            Guid[] fileIds = [.. db.ProjectAvatarReferences.Where(r => r.AvatarId == suit || r.AvatarId == beach).Select(r => r.ProjectFileId)];
            keys = [.. db.ProjectFiles.Where(f => fileIds.Contains(f.Id)).Select(f => f.StorageKey)];
        }

        keys.Should().HaveCount(2);
        (int status, _) = H.Read(await h.Controller().Delete(h.Project.Id, anna, CancellationToken.None));
        status.Should().Be(200);

        using InferenceApiDbContext after = h.NewDb();
        (await after.ProjectAvatars.CountAsync()).Should().Be(0, "no variant is left behind as a usable avatar");
        (await after.ProjectAvatarReferences.CountAsync()).Should().Be(0);
        (await after.ProjectFiles.CountAsync(f => keys.Contains(f.StorageKey))).Should().Be(0);
        h.DeletedKeys.Should().Contain(keys);
        (await after.ProjectAvatarEvents.CountAsync(e => e.Kind == AvatarEventKind.Deleted)).Should().Be(3);
    }

    [Fact]
    public async Task Deleting_an_object_still_leaves_its_variants_ungrouped_and_selectable()
    {
        H h = Both();
        Guid car = await h.CreateObjectAsync("Car");
        ProjectFile image = h.AddImage("car-red.png", salt: 80);
        (_, JsonElement red) = H.Read(await h.Controller().Create(h.Project.Id, H.CreateObject((image.Id, "Front")) with
        {
            Name = null!,
            SubjectKind = null,
            VariantOfAvatarId = car,
            VariantLabel = "Red",
            VariantDescription = "matte red"
        }, CancellationToken.None));
        Guid redId = red.GetProperty("id").GetGuid();

        H.Read(await h.Controller().Delete(h.Project.Id, car, CancellationToken.None)).Status.Should().Be(200);

        JsonElement after = await GetAsync(h, redId);
        after.GetProperty("variantOfAvatarId").ValueKind.Should().Be(JsonValueKind.Null);
        after.GetProperty("selectable").GetBoolean().Should().BeTrue();
    }
}
