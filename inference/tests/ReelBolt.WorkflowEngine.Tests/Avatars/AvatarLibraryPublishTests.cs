#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Avatars;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;
using Xunit;
using H = ReelBolt.WorkflowEngine.Tests.Avatars.AvatarTestHarness;
using L = ReelBolt.WorkflowEngine.Tests.Avatars.AvatarLibraryHarness;

namespace ReelBolt.WorkflowEngine.Tests.Avatars;

/// <summary>
/// Publishing a project avatar to the workspace avatar library (<c>POST /api/v1/avatar-library</c>, lane L9 of
/// docs/design/avatar-objects.md): a new attestation under the WORKSPACE statement, the references copied into
/// organization storage and judged by the one rule on the COPY, the limits, who may do it, and the source avatar
/// becoming a linked import. A refusal writes nothing and leaves no object behind.
/// </summary>
public class AvatarLibraryPublishTests
{
    [Fact]
    public async Task Publishing_a_person_copies_the_files_stores_the_workspace_consent_and_links_the_source()
    {
        var l = new L();
        ProjectFile face = l.Harness.AddImage("anna-face.png", salt: 1);
        ProjectFile clip = l.Harness.AddProbedMedia("anna-walk.mp4", "video/mp4", 5, "h264");
        Guid avatarId = await l.Harness.CreateReadyAsync("Anna", face, clip);
        DateTime before = DateTime.UtcNow;
        ProjectAvatar sourceBefore;
        using (InferenceApiDbContext pre = l.NewDb()) sourceBefore = await pre.ProjectAvatars.AsNoTracking().SingleAsync();

        (int status, JsonElement body) = H.Read(await l.Controller().Publish(L.PersonRequest(l.Project.Id, avatarId, 120), CancellationToken.None));

        status.Should().Be(201, body.ToString());
        Guid itemId = body.GetProperty("id").GetGuid();
        body.GetProperty("subjectKind").GetString().Should().Be("Person");
        body.GetProperty("name").GetString().Should().Be("Anna");
        body.GetProperty("usable").GetBoolean().Should().BeTrue();
        body.GetProperty("unusableReason").ValueKind.Should().Be(JsonValueKind.Null);
        body.GetProperty("references").GetProperty("images").GetInt32().Should().Be(1);
        body.GetProperty("references").GetProperty("videos").GetInt32().Should().Be(1);
        body.GetProperty("references").GetProperty("audio").GetInt32().Should().Be(0);
        body.GetProperty("importCount").GetInt32().Should().Be(1, "the avatar it was published from is its first import");
        body.GetProperty("imported").GetBoolean().Should().BeTrue();
        body.GetProperty("canManage").GetBoolean().Should().BeTrue();
        body.GetProperty("retentionUntil").GetDateTime().Should().BeCloseTo(before.AddDays(120), TimeSpan.FromMinutes(1));
        body.ToString().Should().NotContain("organizations/").And.NotContain("storageKey", "no response carries a storage key");

        using InferenceApiDbContext db = l.NewDb();
        AvatarLibraryItem item = await db.AvatarLibraryItems.SingleAsync();
        item.Id.Should().Be(itemId);
        item.OrganizationId.Should().Be(l.OrganizationId);
        item.ConsentStatement.Should().Be(AvatarWorkspaceConsentStatement.English);
        item.ConsentStatementVersion.Should().Be("avatar-consent-workspace-2026-11");
        item.ConsentLocale.Should().Be("en");
        item.ConsentSubjectName.Should().Be("Anna Rossi");
        item.ConsentSubjectAdult.Should().BeTrue();
        item.ConsentAttestedByUserId.Should().Be(l.Harness.OwnerId);
        item.ConsentRevokedAt.Should().BeNull();
        item.DisclosureLabel.Should().Be(AvatarDisclosure.Visible);

        List<AvatarLibraryReference> references = await db.AvatarLibraryReferences.OrderBy(r => r.Ordinal).ToListAsync();
        references.Select(r => r.Kind).Should().Equal(AvatarReferenceKind.Image, AvatarReferenceKind.Video);
        references.Select(r => r.Role).Should().Equal(AvatarReferenceRole.Face, AvatarReferenceRole.Motion);
        references[0].ContentSha256.Should().Be(H.Sha256(l.Harness.Objects[face.StorageKey]));
        references[0].FileName.Should().Be("anna-face.png");
        references[0].MimeType.Should().Be("image/png");
        references[0].SizeBytes.Should().Be(face.SizeBytes);
        references[1].DurationMs.Should().Be(5000);
        foreach (AvatarLibraryReference reference in references)
        {
            reference.StorageKey.Should().StartWith($"organizations/{l.OrganizationId:D}/avatar-library/{itemId:D}/{reference.Id:D}.");
            H.Sha256(l.Harness.Objects[reference.StorageKey]).Should().Be(reference.ContentSha256, "the library holds exactly the attested bytes");
        }

        // The source avatar is now a linked import under the workspace attestation; its own files stay where they are.
        ProjectAvatar source = await db.ProjectAvatars.SingleAsync();
        source.Id.Should().Be(avatarId);
        source.LibraryItemId.Should().Be(itemId);
        // Publishing leaves the source's whole consent set alone (the item holds the workspace attestation).
        source.ConsentStatement.Should().Be(sourceBefore.ConsentStatement);
        source.ConsentStatementVersion.Should().Be(sourceBefore.ConsentStatementVersion);
        source.ConsentLocale.Should().Be(sourceBefore.ConsentLocale);
        source.ConsentBasis.Should().Be(sourceBefore.ConsentBasis);
        source.ConsentSubjectName.Should().Be(sourceBefore.ConsentSubjectName);
        source.ConsentAttestedByUserId.Should().Be(sourceBefore.ConsentAttestedByUserId);
        source.ConsentAttestedAt.Should().Be(sourceBefore.ConsentAttestedAt);
        source.RetentionUntil.Should().Be(item.RetentionUntil, "the keep-until date follows the item, as for every linked import");
        source.DisclosureLabel.Should().Be(sourceBefore.DisclosureLabel);
        item.ConsentStatement.Should().Be(AvatarWorkspaceConsentStatement.English);
        item.ConsentAttestedAt.Should().BeOnOrAfter(before);
        source.CoversPictures.Should().BeTrue();
        l.Harness.Objects.Should().ContainKey(face.StorageKey).And.ContainKey(clip.StorageKey);
        (await db.ProjectAvatarReferences.Select(r => r.ProjectFileId).ToListAsync()).Should().BeEquivalentTo([face.Id, clip.Id]);

        List<ProjectAvatarEvent> events = await db.ProjectAvatarEvents
            .Where(e => e.Kind == AvatarEventKind.LibraryPublished || e.Kind == AvatarEventKind.LibraryImported).ToListAsync();
        events.Select(e => e.Kind).Should().BeEquivalentTo([AvatarEventKind.LibraryPublished, AvatarEventKind.LibraryImported]);
        ProjectAvatarEvent published = events.Single(e => e.Kind == AvatarEventKind.LibraryPublished);
        published.AvatarId.Should().Be(itemId);
        published.ProjectId.Should().Be(l.Project.Id);
        published.ConsentStatementVersion.Should().Be(AvatarWorkspaceConsentStatement.Version);
        string all = string.Join("\n", events.Select(e => e.OutcomesJson));
        foreach (string leak in new[] { "Anna", "anna-face", "organizations/", "projects/", face.Id.ToString() })
            all.Should().NotContain(leak);
    }

    [Fact]
    public async Task Publishing_an_object_takes_the_object_statement_and_never_expires()
    {
        var l = new L();
        ProjectFile front = l.Harness.AddImage("machine.png", salt: 7);
        Guid avatarId = await l.Harness.CreateObjectAsync("Espresso machine", front);
        ProjectAvatar before;
        using (InferenceApiDbContext pre = l.NewDb()) before = await pre.ProjectAvatars.AsNoTracking().SingleAsync();

        (int status, JsonElement body) = H.Read(await l.Controller().Publish(L.ObjectRequest(l.Project.Id, avatarId), CancellationToken.None));

        status.Should().Be(201, body.ToString());
        body.GetProperty("subjectKind").GetString().Should().Be("Object");
        using (InferenceApiDbContext post = l.NewDb())
        {
            // Publishing restates consent: what a running step fingerprints on the source must not move.
            ProjectAvatar after = await post.ProjectAvatars.AsNoTracking().SingleAsync();
            after.ConsentAttestedAt.Should().Be(before.ConsentAttestedAt);
            after.DisclosureLabel.Should().Be(before.DisclosureLabel);
        }

        body.GetProperty("retentionUntil").ValueKind.Should().Be(JsonValueKind.Null, "an object has no keep-until date");
        body.GetProperty("usable").GetBoolean().Should().BeTrue();

        using InferenceApiDbContext db = l.NewDb();
        AvatarLibraryItem item = await db.AvatarLibraryItems.SingleAsync();
        item.SubjectKind.Should().Be(AvatarSubjectKind.Object);
        item.ConsentStatement.Should().Be(AvatarWorkspaceObjectStatement.English);
        item.ConsentStatementVersion.Should().Be("avatar-object-workspace-2026-11");
        item.ConsentSubjectName.Should().BeEmpty();
        item.ConsentSubjectAdult.Should().BeFalse();
        item.RetentionUntil.Should().Be(ProjectAvatar.NoRetention);
        item.DisclosureLabel.Should().Be(AvatarDisclosure.MetadataOnly);
        ProjectAvatar source = await db.ProjectAvatars.SingleAsync();
        source.LibraryItemId.Should().Be(item.Id);
        source.ConsentStatementVersion.Should().Be(before.ConsentStatementVersion, "the source keeps its own consent record");
        source.RetentionUntil.Should().Be(ProjectAvatar.NoRetention);
    }

    [Fact]
    public async Task The_signed_release_is_copied_into_the_library_and_never_listed()
    {
        var l = new L();
        ProjectFile face = l.Harness.AddImage("carla.png", salt: 3);
        ProjectFile release = l.Harness.AddFile("release.pdf", "application/pdf", [1, 2, 3, 4]);
        CreateProjectAvatarRequest create = H.Create((face.Id, "Face")) with { Name = "Carla", ConsentDocumentFileId = release.Id };
        (int created, JsonElement avatar) = H.Read(await l.Harness.Controller().Create(l.Project.Id, create, CancellationToken.None));
        created.Should().Be(201, avatar.ToString());

        Guid itemId = await l.PublishAsync(L.PersonRequest(l.Project.Id, avatar.GetProperty("id").GetGuid()));

        using InferenceApiDbContext db = l.NewDb();
        AvatarLibraryItem item = await db.AvatarLibraryItems.SingleAsync();
        item.ConsentDocumentStorageKey.Should().Be($"organizations/{l.OrganizationId:D}/avatar-library/{itemId:D}/consent.pdf");
        l.Harness.Objects[item.ConsentDocumentStorageKey!].Should().Equal(1, 2, 3, 4);
        (await db.AvatarLibraryReferences.CountAsync()).Should().Be(1, "the release is not a reference");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_statement_that_is_not_the_workspace_text_is_refused_with_the_texts_and_nothing_is_copied(bool isObject)
    {
        var l = new L();
        Guid avatarId = isObject ? await l.Harness.CreateObjectAsync("Machine") : await l.Harness.CreateReadyAsync("Anna");
        // The PROJECT statement of the kind: valid for the avatar, not for the workspace.
        PublishToAvatarLibraryRequest request = isObject
            ? L.ObjectRequest(l.Project.Id, avatarId) with { ConsentStatement = AvatarObjectStatement.English }
            : L.PersonRequest(l.Project.Id, avatarId) with { ConsentStatement = AvatarConsentStatement.English };

        (int status, JsonElement body) = H.Read(await l.Controller().Publish(request, CancellationToken.None));

        status.Should().Be(400);
        string code = isObject ? "object_statement_required" : "consent_statement_required";
        body.GetProperty("code").GetString().Should().Be(code);
        body.GetProperty("error").GetString().Should().Be(code);
        body.GetProperty("message").GetString().Should().NotBeNullOrWhiteSpace();
        body.GetProperty("locale").GetString().Should().Be("en");
        body.GetProperty("statementVersion").GetString().Should().Be(
            isObject ? AvatarWorkspaceObjectStatement.Version : AvatarWorkspaceConsentStatement.Version);
        body.GetProperty("requiredStatement").GetString().Should().Be(
            isObject ? AvatarWorkspaceObjectStatement.English : AvatarWorkspaceConsentStatement.English);
        body.GetProperty("statements").GetProperty("it").GetString().Should().Be(
            isObject ? AvatarWorkspaceObjectStatement.Italian : AvatarWorkspaceConsentStatement.Italian);
        await NothingPublishedAsync(l);
    }

    [Fact]
    public async Task A_person_needs_the_adult_tick_and_a_keep_for_period_in_range_and_an_object_takes_neither()
    {
        var l = new L();
        Guid person = await l.Harness.CreateReadyAsync("Anna");
        Guid machine = await l.Harness.CreateObjectAsync("Machine");

        (int noTick, JsonElement noTickBody) = H.Read(await l.Controller().Publish(
            L.PersonRequest(l.Project.Id, person) with { SubjectIsAdult = false }, CancellationToken.None));
        noTick.Should().Be(400);
        noTickBody.GetProperty("code").GetString().Should().Be("subject_must_be_adult");

        (int tooLong, JsonElement tooLongBody) = H.Read(await l.Controller().Publish(L.PersonRequest(l.Project.Id, person, 731), CancellationToken.None));
        tooLong.Should().Be(400);
        tooLongBody.GetProperty("code").GetString().Should().Be("retention_out_of_range");

        (int days, JsonElement daysBody) = H.Read(await l.Controller().Publish(
            L.ObjectRequest(l.Project.Id, machine) with { RetentionDays = 30 }, CancellationToken.None));
        days.Should().Be(400);
        daysBody.GetProperty("code").GetString().Should().Be("field_not_for_objects");
        daysBody.GetProperty("field").GetString().Should().Be("retentionDays");

        (int tick, JsonElement tickBody) = H.Read(await l.Controller().Publish(
            L.ObjectRequest(l.Project.Id, machine) with { SubjectIsAdult = true }, CancellationToken.None));
        tick.Should().Be(400);
        tickBody.GetProperty("field").GetString().Should().Be("subjectIsAdult");
        await NothingPublishedAsync(l);

        // The default keep-for period is the avatar default.
        Guid itemId = await l.PublishAsync(L.PersonRequest(l.Project.Id, person, retentionDays: null));
        using InferenceApiDbContext db = l.NewDb();
        (await db.AvatarLibraryItems.SingleAsync(i => i.Id == itemId)).RetentionUntil
            .Should().BeCloseTo(DateTime.UtcNow.AddDays(ProjectAvatarLimits.DefaultRetentionDays), TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task Only_an_owner_or_admin_of_the_organization_with_a_verified_email_may_publish()
    {
        var l = new L();
        Guid avatarId = await l.Harness.CreateReadyAsync("Anna");
        PublishToAvatarLibraryRequest request = L.PersonRequest(l.Project.Id, avatarId);

        H.Read(await l.Controller(user: l.Harness.Member()).Publish(request, CancellationToken.None)).Status
            .Should().Be(403, "a member of the organization may not manage its library");
        H.Read(await l.Controller(user: l.Harness.InRole(OrganizationRole.Billing)).Publish(request, CancellationToken.None)).Status.Should().Be(403);
        H.Read(await l.Controller(user: H.Stranger()).Publish(request, CancellationToken.None)).Status
            .Should().Be(404, "another organization's project does not exist, whatever the caller's role there");
        (int unverified, JsonElement body) = H.Read(await l.Controller(user: l.Harness.Owner(emailVerified: false)).Publish(request, CancellationToken.None));
        unverified.Should().Be(403);
        body.GetProperty("code").GetString().Should().Be("EMAIL_NOT_VERIFIED");
        H.Read(await l.Controller().Publish(request with { AvatarId = Guid.NewGuid() }, CancellationToken.None)).Status.Should().Be(404);
        await NothingPublishedAsync(l);

        H.Read(await l.Controller(user: l.Harness.InRole(OrganizationRole.Admin)).Publish(request, CancellationToken.None)).Status.Should().Be(201);
    }

    [Fact]
    public async Task The_plan_feature_of_the_kind_is_needed()
    {
        var l = new L();
        Guid person = await l.Harness.CreateReadyAsync("Anna");
        Guid machine = await l.Harness.CreateObjectAsync("Machine");
        l.Harness.Entitlements.Set = new OrganizationEntitlementSet { AvatarsAllowed = false, AvatarObjectsAllowed = true };

        (int status, JsonElement body) = H.Read(await l.Controller().Publish(L.PersonRequest(l.Project.Id, person), CancellationToken.None));
        status.Should().Be(402);
        body.GetProperty("code").GetString().Should().Be("PLAN_NOT_ENTITLED");
        body.GetProperty("feature").GetString().Should().Be("avatars");
        H.Read(await l.Controller().Publish(L.ObjectRequest(l.Project.Id, machine), CancellationToken.None)).Status.Should().Be(201);

        l.Harness.Entitlements.Set = new OrganizationEntitlementSet { AvatarsAllowed = true, AvatarObjectsAllowed = false };
        Guid lamp = await CreateObjectDirectAsync(l, "Lamp");
        (int objectStatus, JsonElement objectBody) = H.Read(await l.Controller().Publish(L.ObjectRequest(l.Project.Id, lamp), CancellationToken.None));
        objectStatus.Should().Be(402);
        objectBody.GetProperty("feature").GetString().Should().Be("avatar_objects");
    }

    [Fact]
    public async Task The_library_holds_one_hundred_items_of_a_kind_and_one_live_item_per_name()
    {
        var l = new L();
        Guid anna = await l.Harness.CreateReadyAsync("Anna");
        Guid machine = await l.Harness.CreateObjectAsync("Machine");
        using (InferenceApiDbContext db = l.NewDb())
        {
            for (int i = 0; i < AvatarLibraryLimits.MaxItemsPerKind; i++)
                db.AvatarLibraryItems.Add(SeedItem(l.OrganizationId, AvatarSubjectKind.Person, "Person " + i));
            db.AvatarLibraryItems.Add(SeedItem(l.OrganizationId, AvatarSubjectKind.Object, "Machine"));
            // Another organization's items never count.
            db.AvatarLibraryItems.Add(SeedItem(Guid.NewGuid(), AvatarSubjectKind.Object, "Machine"));
            await db.SaveChangesAsync();
        }

        (int full, JsonElement fullBody) = H.Read(await l.Controller().Publish(L.PersonRequest(l.Project.Id, anna), CancellationToken.None));
        full.Should().Be(409);
        fullBody.GetProperty("code").GetString().Should().Be("LIBRARY_LIMIT_REACHED");

        (int taken, JsonElement takenBody) = H.Read(await l.Controller().Publish(L.ObjectRequest(l.Project.Id, machine), CancellationToken.None));
        taken.Should().Be(409, "people and objects are counted apart, but the name is taken");
        takenBody.GetProperty("code").GetString().Should().Be("LIBRARY_NAME_TAKEN");
        l.Store.Keys().Should().BeEmpty("a refused publish leaves no object behind");

        // A revoked item does not hold its name.
        using (InferenceApiDbContext db = l.NewDb())
        {
            AvatarLibraryItem holder = await db.AvatarLibraryItems.SingleAsync(i => i.OrganizationId == l.OrganizationId && i.Name == "Machine");
            holder.ConsentRevokedAt = DateTime.UtcNow;
            holder.ConsentRevokedByUserId = l.Harness.OwnerId;
            await db.SaveChangesAsync();
        }

        H.Read(await l.Controller().Publish(L.ObjectRequest(l.Project.Id, machine), CancellationToken.None)).Status.Should().Be(201);
    }

    [Fact]
    public async Task An_avatar_that_is_not_usable_or_is_itself_an_import_cannot_be_published()
    {
        var l = new L();
        Guid revoked = await l.Harness.CreateReadyAsync("Anna");
        H.Read(await l.Harness.Controller().RevokeConsent(l.Project.Id, revoked, CancellationToken.None)).Status.Should().Be(200);

        (int status, JsonElement body) = H.Read(await l.Controller().Publish(L.PersonRequest(l.Project.Id, revoked), CancellationToken.None));
        status.Should().Be(409);
        body.GetProperty("code").GetString().Should().Be("AVATAR_NOT_USABLE");
        body.GetProperty("reason").GetString().Should().Be("consent_revoked");
        l.Store.Keys().Should().BeEmpty("consent is judged before any file is copied");

        (Guid _, Guid linked) = await l.PublishPersonAsync("Bea");
        (int again, JsonElement againBody) = H.Read(await l.Controller().Publish(L.PersonRequest(l.Project.Id, linked), CancellationToken.None));
        again.Should().Be(409);
        againBody.GetProperty("code").GetString().Should().Be("AVATAR_NOT_USABLE");
        againBody.GetProperty("reason").GetString().Should().Be("from_library");
        using InferenceApiDbContext db = l.NewDb();
        (await db.AvatarLibraryItems.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task A_reference_whose_bytes_are_not_the_attested_ones_refuses_the_publish_and_leaves_no_copy()
    {
        var l = new L();
        ProjectFile face = l.Harness.AddImage("anna.png", salt: 1);
        ProjectFile side = l.Harness.AddImage("anna-side.png", salt: 2);
        Guid avatarId = await l.Harness.CreateReadyAsync("Anna", face, side);
        // The bytes of one file changed after the attestation (a background path, never a user route).
        l.Harness.Objects[side.StorageKey] = H.Png(1024, 1024, salt: 99);

        (int status, JsonElement body) = H.Read(await l.Controller().Publish(L.PersonRequest(l.Project.Id, avatarId), CancellationToken.None));

        status.Should().Be(409);
        body.GetProperty("code").GetString().Should().Be("AVATAR_NOT_USABLE");
        body.GetProperty("reason").GetString().Should().Be("reference_missing");
        await NothingPublishedAsync(l);
        l.Store.Deleted.Should().NotBeEmpty("the copies made on the way were removed");
    }

    [Theory]
    [InlineData("revoked", "consent_revoked")]
    [InlineData("expired", "retention_expired")]
    public async Task A_person_variant_whose_original_is_revoked_or_expired_cannot_be_published(string state, string reason)
    {
        var l = new L();
        ProjectFile originalFace = l.Harness.AddImage("anna.png", salt: 1);
        ProjectFile variantFace = l.Harness.AddImage("anna-beach.png", salt: 2);
        Guid original = await l.Harness.CreateReadyAsync("Anna", originalFace);
        Guid variant = await l.Harness.CreateReadyAsync("Anna - Beach", variantFace);
        using (InferenceApiDbContext db = l.NewDb())
        {
            ProjectAvatar v = await db.ProjectAvatars.SingleAsync(a => a.Id == variant);
            v.VariantOfAvatarId = original;
            v.VariantLabel = "Beach";
            ProjectAvatar o = await db.ProjectAvatars.SingleAsync(a => a.Id == original);
            if (state == "revoked")
            {
                o.ConsentRevokedAt = DateTime.UtcNow;
                o.ConsentRevokedByUserId = Guid.NewGuid();
            }
            else
            {
                o.RetentionUntil = DateTime.UtcNow.AddDays(-1);
            }

            await db.SaveChangesAsync();
        }

        (int status, JsonElement body) = H.Read(await l.Controller().Publish(L.PersonRequest(l.Project.Id, variant), CancellationToken.None));
        status.Should().Be(409);
        body.GetProperty("code").GetString().Should().Be("AVATAR_NOT_USABLE");
        body.GetProperty("reason").GetString().Should().Be(reason);
        await NothingPublishedAsync(l);
    }

    [Fact]
    public async Task A_held_reference_is_never_copied()
    {
        var l = new L();
        ProjectFile face = l.Harness.AddImage("anna.png", salt: 1);
        Guid avatarId = await l.Harness.CreateReadyAsync("Anna", face);
        using (InferenceApiDbContext db = l.NewDb())
        {
            (await db.ProjectFiles.SingleAsync(f => f.Id == face.Id)).CsamStatus = CsamScanStatus.Quarantined;
            await db.SaveChangesAsync();
        }

        (int status, JsonElement body) = H.Read(await l.Controller().Publish(L.PersonRequest(l.Project.Id, avatarId), CancellationToken.None));

        // A CSAM quarantine also marks the project, which publish now refuses first (the per-file path is covered in AvatarLibraryHoldTests).
        status.Should().Be(409);
        body.ToString().Should().Contain("PROJECT_CONTENT_QUARANTINED");
        body.GetProperty("code").GetString().Should().Be("PROJECT_CONTENT_QUARANTINED");
        l.Store.Keys().Should().BeEmpty();
        l.Store.Deleted.Should().BeEmpty("the held bytes were never read, so nothing was copied");
    }

    [Fact]
    public async Task The_library_copies_count_against_the_storage_quota()
    {
        var l = new L();
        ProjectFile face = l.Harness.AddImage("anna.png", salt: 1);
        Guid avatarId = await l.Harness.CreateReadyAsync("Anna", face);
        l.PlanGate.Storage = new PlanGateViolation(PlanGate.StorageCode, "storage", "This upload would exceed the plan's storage quota.", 413, 10, 9);

        (int status, JsonElement body) = H.Read(await l.Controller().Publish(L.PersonRequest(l.Project.Id, avatarId), CancellationToken.None));

        status.Should().Be(413);
        body.GetProperty("code").GetString().Should().Be("STORAGE_QUOTA_EXCEEDED");
        l.PlanGate.Asked.Should().Contain(face.SizeBytes);
        await NothingPublishedAsync(l);
    }

    // ------------------------------------------------------------------ helpers

    internal static AvatarLibraryItem SeedItem(Guid organizationId, AvatarSubjectKind kind, string name) => new()
    {
        Id = Guid.NewGuid(),
        OrganizationId = organizationId,
        SubjectKind = kind,
        Name = name,
        ConsentStatement = kind == AvatarSubjectKind.Object ? AvatarWorkspaceObjectStatement.English : AvatarWorkspaceConsentStatement.English,
        ConsentStatementVersion = kind == AvatarSubjectKind.Object ? AvatarWorkspaceObjectStatement.Version : AvatarWorkspaceConsentStatement.Version,
        ConsentLocale = "en",
        ConsentSubjectName = kind == AvatarSubjectKind.Object ? string.Empty : "Subject",
        ConsentSubjectAdult = kind == AvatarSubjectKind.Person,
        ConsentAttestedByUserId = Guid.NewGuid(),
        ConsentAttestedAt = DateTime.UtcNow,
        RetentionUntil = kind == AvatarSubjectKind.Object ? ProjectAvatar.NoRetention : DateTime.UtcNow.AddDays(100),
        DisclosureLabel = kind == AvatarSubjectKind.Object ? AvatarDisclosure.MetadataOnly : AvatarDisclosure.Visible,
        CreatedByUserId = Guid.NewGuid()
    };

    /// <summary>An object avatar written straight to the store (the plan no longer lets the route create one).</summary>
    private static async Task<Guid> CreateObjectDirectAsync(L l, string name)
    {
        ProjectFile front = l.Harness.AddImage(name + ".png", salt: 55);
        using InferenceApiDbContext db = l.NewDb();
        var avatar = new ProjectAvatar
        {
            Id = Guid.NewGuid(),
            ProjectId = l.Project.Id,
            SubjectKind = AvatarSubjectKind.Object,
            Name = name,
            ConsentAttestedByUserId = l.Harness.OwnerId,
            ConsentAttestedAt = DateTime.UtcNow,
            ConsentStatement = AvatarObjectStatement.English,
            ConsentStatementVersion = AvatarObjectStatement.Version,
            ConsentLocale = "en",
            RetentionUntil = ProjectAvatar.NoRetention,
            DisclosureLabel = AvatarDisclosure.MetadataOnly,
            CreatedByUserId = l.Harness.OwnerId
        };
        db.ProjectAvatars.Add(avatar);
        db.ProjectAvatarReferences.Add(new ProjectAvatarReference
        {
            Id = Guid.NewGuid(),
            AvatarId = avatar.Id,
            ProjectFileId = front.Id,
            Kind = AvatarReferenceKind.Image,
            Role = AvatarReferenceRole.Front,
            ContentSha256 = H.Sha256(l.Harness.Objects[front.StorageKey])
        });
        await db.SaveChangesAsync();
        return avatar.Id;
    }

    private static async Task NothingPublishedAsync(L l)
    {
        using InferenceApiDbContext db = l.NewDb();
        (await db.AvatarLibraryItems.CountAsync()).Should().Be(0);
        (await db.AvatarLibraryReferences.CountAsync()).Should().Be(0);
        (await db.ProjectAvatars.CountAsync(a => a.LibraryItemId != null)).Should().Be(0, "no avatar was linked");
        l.Store.Keys().Should().BeEmpty("a refused publish leaves no object in the library");
    }
}
