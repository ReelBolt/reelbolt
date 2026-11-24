#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
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
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Services.Avatars;
using ReelBolt.WorkflowEngine.Tests.Execution;
using Xunit;
using H = ReelBolt.WorkflowEngine.Tests.Avatars.AvatarTestHarness;
using L = ReelBolt.WorkflowEngine.Tests.Avatars.AvatarLibraryHarness;

namespace ReelBolt.WorkflowEngine.Tests.Avatars;

/// <summary>
/// Importing a workspace avatar library item into a project (<c>POST .../avatars/import</c>) and what a LINKED import
/// may do on the project's own avatar routes (lane L9 of docs/design/avatar-objects.md): the item's files are copied to
/// new project files, the digests and the consent are the item's, the import is an ordinary avatar for the one rule and
/// for the engine resolver, and it follows its item instead of being edited in the project.
/// </summary>
public class AvatarLibraryImportTests
{
    [Fact]
    public async Task Importing_a_person_copies_the_files_registers_them_and_makes_a_selectable_linked_avatar()
    {
        var l = new L();
        ProjectFile face = l.Harness.AddImage("anna-face.png", salt: 1);
        ProjectFile clip = l.Harness.AddProbedMedia("anna-walk.mp4", "video/mp4", 5, "h264");
        (Guid itemId, Guid sourceId) = await l.PublishPersonAsync("Anna", face, clip);
        Project second = l.AddProject();

        (int status, JsonElement body) = await l.ImportResultAsync(itemId, second);

        status.Should().Be(201, body.ToString());
        Guid importId = body.GetProperty("id").GetGuid();
        body.GetProperty("projectId").GetGuid().Should().Be(second.Id);
        body.GetProperty("fromLibrary").GetBoolean().Should().BeTrue();
        body.GetProperty("libraryItemId").GetGuid().Should().Be(itemId);
        body.GetProperty("name").GetString().Should().Be("Anna");
        body.GetProperty("subjectKind").GetString().Should().Be("Person");
        body.GetProperty("selectable").GetBoolean().Should().BeTrue("the import is judged by the one rule like any avatar");
        body.GetProperty("unselectableReason").ValueKind.Should().Be(JsonValueKind.Null);
        body.GetProperty("coversPictures").GetBoolean().Should().BeTrue();
        body.GetProperty("consent").GetProperty("statementVersion").GetString().Should().Be(AvatarWorkspaceConsentStatement.Version);
        body.GetProperty("consent").GetProperty("statement").GetString().Should().Be(AvatarWorkspaceConsentStatement.English);
        body.GetProperty("consent").GetProperty("subjectName").GetString().Should().Be("Anna Rossi");
        body.GetProperty("references").EnumerateArray().Select(r => r.GetProperty("state").GetString()).Should().Equal("ok", "ok");
        body.GetProperty("references").EnumerateArray().Select(r => r.GetProperty("role").GetString()).Should().Equal("Face", "Motion");

        using InferenceApiDbContext db = l.NewDb();
        AvatarLibraryItem item = await db.AvatarLibraryItems.SingleAsync();
        ProjectAvatar import = await db.ProjectAvatars.SingleAsync(a => a.Id == importId);
        import.ProjectId.Should().Be(second.Id);
        import.LibraryItemId.Should().Be(itemId);
        import.ConsentAttestedAt.Should().Be(item.ConsentAttestedAt);
        import.ConsentAttestedByUserId.Should().Be(item.ConsentAttestedByUserId);
        import.RetentionUntil.Should().Be(item.RetentionUntil);
        import.ProjectVoiceId.Should().BeNull();
        import.ConsentDocumentFileId.Should().BeNull();

        List<AvatarLibraryReference> master = await db.AvatarLibraryReferences.OrderBy(r => r.Ordinal).ToListAsync();
        List<ProjectAvatarReference> references = await db.ProjectAvatarReferences.Where(r => r.AvatarId == importId).OrderBy(r => r.Ordinal).ToListAsync();
        references.Select(r => r.ContentSha256).Should().Equal(master.Select(r => r.ContentSha256), "the reference rows carry the item's digests");
        references.Select(r => r.Kind).Should().Equal(master.Select(r => r.Kind));
        references.Select(r => r.DurationMs).Should().Equal(master.Select(r => r.DurationMs), "the probe facts come along");

        List<ProjectFile> files = l.FilesOf(importId);
        files.Should().HaveCount(2);
        files.Select(f => f.Id).Should().NotIntersectWith([face.Id, clip.Id], "the import has its OWN files");
        foreach ((ProjectFile file, ProjectAvatarReference reference) in files.Zip(references))
        {
            file.ProjectId.Should().Be(second.Id);
            file.Category.Should().Be("userFiles");
            file.DirectoryPath.Should().Be("avatars/Anna");
            file.StorageKey.Should().StartWith($"projects/{second.Id}/userFiles/avatars/Anna/{file.Id}");
            file.StorageBucket.Should().Be("test-bucket");
            file.SourceKind.Should().Be(ProjectAvatarReference.ProjectFileSourceKind, "a person's reference files are badged");
            file.SourceRevision.Should().Be(AvatarLibraryStorage.SourceRevision(itemId));
            file.CsamStatus.Should().Be(CsamScanStatus.Pending, "a copy is never pre-cleared: it takes the usual scan");
            file.ModerationStatus.Should().Be(FileModerationStatus.Unmoderated);
            file.SummaryStatus.Should().Be(SummaryStatus.Done, "an avatar's files are never summarized or indexed");
            file.IndexingStatus.Should().Be(FileIndexingStatus.NotIndexed);
            H.Sha256(l.Harness.Objects[file.StorageKey]).Should().Be(reference.ContentSha256, "the copied bytes are the attested ones");
        }

        files.Select(f => f.OriginalFileName).Should().Equal("anna-face.png", "anna-walk.mp4");

        ProjectAvatarEvent imported = await db.ProjectAvatarEvents.SingleAsync(e => e.AvatarId == importId);
        imported.Kind.Should().Be(AvatarEventKind.LibraryImported);
        imported.OutcomesJson.Should().Contain("\"via\":\"import\"").And.NotContain("Anna");

        // The library item now counts two imports, and the source avatar is untouched.
        JsonElement listed = H.Read(await l.Controller().Get(itemId, second.Id, CancellationToken.None)).Body;
        listed.GetProperty("importCount").GetInt32().Should().Be(2);
        listed.GetProperty("imported").GetBoolean().Should().BeTrue();
        (await db.ProjectAvatars.SingleAsync(a => a.Id == sourceId)).ProjectId.Should().Be(l.Project.Id);
    }

    [Fact]
    public async Task An_imported_object_keeps_its_files_ordinary_and_the_engine_resolver_resolves_the_import()
    {
        var l = new L();
        ProjectFile front = l.Harness.AddImage("machine-front.png", salt: 8);
        ProjectFile side = l.Harness.AddImage("machine-side.png", salt: 9);
        Guid avatarId = await l.Harness.CreateObjectAsync("Espresso machine", front);
        using (InferenceApiDbContext seed = l.NewDb())
        {
            // A second picture, attested like the first (the create helper takes one role per kind).
            seed.ProjectAvatarReferences.Add(new ProjectAvatarReference
            {
                Id = Guid.NewGuid(),
                AvatarId = avatarId,
                ProjectFileId = side.Id,
                Kind = AvatarReferenceKind.Image,
                Role = AvatarReferenceRole.Angle,
                Ordinal = 1,
                ContentSha256 = H.Sha256(l.Harness.Objects[side.StorageKey])
            });
            await seed.SaveChangesAsync();
        }

        Guid itemId = await l.PublishAsync(L.ObjectRequest(l.Project.Id, avatarId));
        Project second = l.AddProject();

        (int status, JsonElement body) = await l.ImportResultAsync(itemId, second);

        status.Should().Be(201, body.ToString());
        Guid importId = body.GetProperty("id").GetGuid();
        body.GetProperty("subjectKind").GetString().Should().Be("Object");
        body.GetProperty("selectable").GetBoolean().Should().BeTrue();
        body.GetProperty("retentionUntil").ValueKind.Should().Be(JsonValueKind.Null);
        body.GetProperty("consent").GetProperty("statementVersion").GetString().Should().Be(AvatarWorkspaceObjectStatement.Version);
        List<ProjectFile> files = l.FilesOf(importId);
        files.Should().HaveCount(2);
        files.Should().OnlyContain(f => f.SourceKind == null && f.SourceRevision == AvatarLibraryStorage.SourceRevision(itemId)
                                        && f.DirectoryPath == "avatars/Espresso machine");

        // The engine: the same rows through the engine's own resolver, which reads the object store by key.
        string engineName = "avatar-library-engine-" + Guid.NewGuid();
        WorkflowEngineDbContext Engine() => new(new DbContextOptionsBuilder<WorkflowEngineDbContext>().UseInMemoryDatabase(engineName).Options);
        using (InferenceApiDbContext api = l.NewDb())
        using (WorkflowEngineDbContext engine = Engine())
        {
            engine.Projects.Add(new Project { Id = second.Id, Name = second.Name, OwnerId = second.OwnerId, OrganizationId = second.OrganizationId });
            engine.ProjectFiles.AddRange(await api.ProjectFiles.AsNoTracking().Where(f => f.ProjectId == second.Id).ToListAsync());
            engine.ProjectAvatars.Add(await api.ProjectAvatars.AsNoTracking().SingleAsync(a => a.Id == importId));
            engine.ProjectAvatarReferences.AddRange(await api.ProjectAvatarReferences.AsNoTracking().Where(r => r.AvatarId == importId).ToListAsync());
            await engine.SaveChangesAsync();
        }

        var resolver = new AvatarResolver(new VoiceReuseGuardTests.DbScopes(Engine), new StoreDigests(l.Harness.Objects), TimeProvider.System);
        ResolvedAvatar resolved = await resolver.ResolveRequiredAsync(second.Id, importId, CancellationToken.None);
        resolved.Usable.Should().BeTrue();
        resolved.Name.Should().Be("Espresso machine");
        resolved.References.Should().HaveCount(2);
        resolved.References.Should().OnlyContain(r => r.StorageKey.StartsWith($"projects/{second.Id}/userFiles/avatars/"));
    }

    private sealed class StoreDigests(IReadOnlyDictionary<string, byte[]> objects) : IAvatarReferenceDigestReader
    {
        public Task<string?> ReadSha256Async(Guid projectId, string storageKey, CancellationToken ct) =>
            Task.FromResult(storageKey.StartsWith($"projects/{projectId}/", StringComparison.Ordinal) && objects.TryGetValue(storageKey, out byte[]? bytes)
                ? Convert.ToHexStringLower(SHA256.HashData(bytes))
                : null);
    }

    [Fact]
    public async Task An_import_is_refused_when_it_is_there_already_over_a_limit_over_quota_or_not_in_the_plan()
    {
        var l = new L();
        (Guid anna, _) = await l.PublishPersonAsync("Anna");
        Project second = l.AddProject();

        // Already imported: the source project holds the item since the publish.
        (int twice, JsonElement twiceBody) = await l.ImportResultAsync(anna, l.Project);
        twice.Should().Be(409);
        twiceBody.GetProperty("code").GetString().Should().Be("AVATAR_ALREADY_IMPORTED");
        twiceBody.GetProperty("error").GetString().Should().Be("AVATAR_ALREADY_IMPORTED");

        // The storage quota, before anything is copied.
        l.PlanGate.Storage = new PlanGateViolation(PlanGate.StorageCode, "storage", "This upload would exceed the plan's storage quota.", 413, 10, 9);
        (int quota, JsonElement quotaBody) = await l.ImportResultAsync(anna, second);
        quota.Should().Be(413);
        quotaBody.GetProperty("code").GetString().Should().Be("STORAGE_QUOTA_EXCEEDED");
        l.PlanGate.Storage = null;

        // The plan feature of the kind.
        l.Harness.Entitlements.Set = new OrganizationEntitlementSet { AvatarsAllowed = false };
        (int plan, JsonElement planBody) = await l.ImportResultAsync(anna, second);
        plan.Should().Be(402);
        planBody.GetProperty("code").GetString().Should().Be("PLAN_NOT_ENTITLED");
        planBody.GetProperty("feature").GetString().Should().Be("avatars");
        l.Harness.Entitlements.Set = new OrganizationEntitlementSet { AvatarsAllowed = true };

        // A name another live avatar of the project already uses.
        ProjectFile other = l.Harness.AddFile("other.png", "image/png", H.Png(1024, 1024, 77), projectId: second.Id);
        using (InferenceApiDbContext db = l.NewDb())
            AvatarSeed.Attach(db, second.Id, "Anna", other);
        (int name, JsonElement nameBody) = await l.ImportResultAsync(anna, second);
        name.Should().Be(409);
        nameBody.GetProperty("code").GetString().Should().Be("AVATAR_NAME_TAKEN");

        // The project's per-kind limit.
        Project full = l.AddProject("Full");
        using (InferenceApiDbContext db = l.NewDb())
        {
            for (int i = 0; i < ProjectAvatarLimits.MaxAvatarsPerProject; i++) AvatarSeed.Attach(db, full.Id, "Person " + i);
        }

        (int limit, JsonElement limitBody) = await l.ImportResultAsync(anna, full);
        limit.Should().Be(409);
        limitBody.GetProperty("code").GetString().Should().Be("AVATAR_LIMIT_REACHED");

        // The item's import cap.
        Project third = l.AddProject("Third");
        using (InferenceApiDbContext db = l.NewDb())
        {
            for (int i = 1; i < AvatarLibraryLimits.MaxImportsPerItem; i++)
            {
                ProjectAvatar row = AvatarSeed.Attach(db, Guid.NewGuid(), "Anna");
                row.LibraryItemId = anna;
            }

            await db.SaveChangesAsync();
        }

        (int cap, JsonElement capBody) = await l.ImportResultAsync(anna, third);
        cap.Should().Be(409);
        capBody.GetProperty("code").GetString().Should().Be("LIBRARY_IMPORT_LIMIT");

        // Nothing of any refusal stayed: no file row, no object, in any of the projects.
        using InferenceApiDbContext read = l.NewDb();
        (await read.ProjectFiles.CountAsync(f => f.SourceRevision != null)).Should().Be(0);
        l.Harness.Objects.Keys.Should().NotContain(k => k.Contains("/userFiles/avatars/"));
    }

    [Fact]
    public async Task An_item_that_cannot_be_used_or_read_is_not_imported_and_leaves_nothing_behind()
    {
        var l = new L();
        ProjectFile face = l.Harness.AddImage("anna.png", salt: 1);
        ProjectFile side = l.Harness.AddImage("anna-2.png", salt: 2);
        Guid avatarId = await l.Harness.CreateReadyAsync("Anna", face);
        using (InferenceApiDbContext seed = l.NewDb())
        {
            seed.ProjectAvatarReferences.Add(new ProjectAvatarReference
            {
                Id = Guid.NewGuid(),
                AvatarId = avatarId,
                ProjectFileId = side.Id,
                Kind = AvatarReferenceKind.Image,
                Role = AvatarReferenceRole.HalfBody,
                Ordinal = 1,
                ContentSha256 = H.Sha256(l.Harness.Objects[side.StorageKey])
            });
            await seed.SaveChangesAsync();
        }

        Guid itemId = await l.PublishAsync(L.PersonRequest(l.Project.Id, avatarId));
        Project second = l.AddProject();

        // A library object is gone: the first copy lands, the second fails, and the first is removed again.
        string lost;
        using (InferenceApiDbContext db = l.NewDb())
            lost = (await db.AvatarLibraryReferences.SingleAsync(r => r.Ordinal == 1)).StorageKey;
        byte[] kept = l.Harness.Objects[lost];
        l.Harness.Objects.TryRemove(lost, out _);
        (int damaged, JsonElement damagedBody) = await l.ImportResultAsync(itemId, second);
        damaged.Should().Be(409);
        damagedBody.GetProperty("code").GetString().Should().Be("LIBRARY_ITEM_NOT_USABLE");
        damagedBody.GetProperty("reason").GetString().Should().Be("reference_missing");
        l.Harness.Objects.Keys.Should().NotContain(k => k.StartsWith($"projects/{second.Id}/"), "a refused import leaves no copy");
        l.Harness.Objects[lost] = kept;

        // Consent withdrawn.
        H.Read(await l.Controller().RevokeConsent(itemId, CancellationToken.None)).Status.Should().Be(200);
        (int revoked, JsonElement revokedBody) = await l.ImportResultAsync(itemId, second);
        revoked.Should().Be(409);
        revokedBody.GetProperty("code").GetString().Should().Be("LIBRARY_ITEM_NOT_USABLE");
        revokedBody.GetProperty("reason").GetString().Should().Be("consent_revoked");

        using InferenceApiDbContext read = l.NewDb();
        (await read.ProjectAvatars.CountAsync(a => a.ProjectId == second.Id)).Should().Be(0);
        (await read.ProjectFiles.CountAsync(f => f.ProjectId == second.Id)).Should().Be(0);
    }

    [Fact]
    public async Task Import_needs_edit_on_the_project_and_a_verified_email()
    {
        var l = new L();
        (Guid anna, _) = await l.PublishPersonAsync("Anna");
        Project second = l.AddProject();

        (await l.ImportResultAsync(anna, second, l.Harness.InRole(OrganizationRole.Billing))).Status.Should().Be(403);
        (int unverified, JsonElement body) = await l.ImportResultAsync(anna, second, l.Harness.Owner(emailVerified: false));
        unverified.Should().Be(403);
        body.GetProperty("code").GetString().Should().Be("EMAIL_NOT_VERIFIED");
        (await l.ImportResultAsync(Guid.NewGuid(), second)).Status.Should().Be(404);

        // A plain member with Edit on the project may import: managing the library is the Owner's and Admin's, using it is not.
        (await l.ImportResultAsync(anna, second, l.Harness.Member())).Status.Should().Be(201);
    }

    // ------------------------------------------------------------------ a linked import on the project routes

    [Fact]
    public async Task A_linked_import_takes_only_the_voice_link_and_is_changed_in_the_library()
    {
        var l = new L();
        (Guid itemId, _) = await l.PublishPersonAsync("Anna");
        Project second = l.AddProject();
        Guid importId = await l.ImportAsync(itemId, second);
        ProjectFile newFace = l.Harness.AddFile("new.png", "image/png", H.Png(1024, 1024, 42), projectId: second.Id);
        Guid voiceId = Guid.NewGuid();
        using (InferenceApiDbContext db = l.NewDb())
        {
            db.ProjectVoices.Add(new ProjectVoice { Id = voiceId, ProjectId = second.Id, Name = "Narrator", Source = ProjectVoiceSource.Library, RemoteVoiceId = "v1" });
            await db.SaveChangesAsync();
        }

        foreach (UpdateProjectAvatarRequest edit in new UpdateProjectAvatarRequest[]
                 {
                     new(Name: "Renamed"), new(Description: "x"), new(DisclosureLabel: "MetadataOnly", DisclosesOtherwise: true)
                 })
        {
            (int status, JsonElement body) = H.Read(await l.Harness.Controller().Update(second.Id, importId, edit, CancellationToken.None));
            status.Should().Be(409, edit.ToString());
            body.GetProperty("code").GetString().Should().Be("AVATAR_FROM_LIBRARY");
        }

        (int reattest, JsonElement reattestBody) = H.Read(await l.Harness.Controller().ReplaceReferences(
            second.Id, importId,
            new ReplaceAvatarReferencesRequest(AvatarConsentStatement.English, "en", true, [new AvatarReferenceRequest(newFace.Id, "Face")], 30),
            CancellationToken.None));
        reattest.Should().Be(409);
        reattestBody.GetProperty("code").GetString().Should().Be("AVATAR_FROM_LIBRARY");

        (int revoke, JsonElement revokeBody) = H.Read(await l.Harness.Controller().RevokeConsent(second.Id, importId, CancellationToken.None));
        revoke.Should().Be(409);
        revokeBody.GetProperty("code").GetString().Should().Be("AVATAR_FROM_LIBRARY");

        // The voice link is the project's own.
        (int voice, JsonElement voiceBody) = H.Read(await l.Harness.Controller().Update(
            second.Id, importId, new UpdateProjectAvatarRequest(ProjectVoiceId: voiceId), CancellationToken.None));
        voice.Should().Be(200, voiceBody.ToString());
        voiceBody.GetProperty("projectVoiceId").GetGuid().Should().Be(voiceId);
        voiceBody.GetProperty("name").GetString().Should().Be("Anna");
        H.Read(await l.Harness.Controller().Update(second.Id, importId, new UpdateProjectAvatarRequest(ClearProjectVoice: true), CancellationToken.None))
            .Status.Should().Be(200);

        using InferenceApiDbContext read = l.NewDb();
        ProjectAvatar import = await read.ProjectAvatars.SingleAsync(a => a.Id == importId);
        import.Name.Should().Be("Anna");
        import.ConsentRevokedAt.Should().BeNull();
        import.ConsentStatementVersion.Should().Be(AvatarWorkspaceConsentStatement.Version);
        (await read.ProjectAvatarReferences.CountAsync(r => r.AvatarId == importId)).Should().Be(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Deleting_an_import_removes_it_and_its_copied_files_and_leaves_the_item(bool isObject)
    {
        var l = new L();
        (Guid itemId, Guid sourceId) = isObject ? await l.PublishObjectAsync("Machine") : await l.PublishPersonAsync("Anna");
        Project second = l.AddProject();
        Guid importId = await l.ImportAsync(itemId, second);
        List<ProjectFile> copies = l.FilesOf(importId);
        List<ProjectFile> originals = l.FilesOf(sourceId);

        (int status, JsonElement body) = H.Read(await l.Harness.Controller().Delete(second.Id, importId, CancellationToken.None));

        status.Should().Be(200, body.ToString());
        body.GetProperty("references").GetProperty("deleted").GetInt32().Should().Be(copies.Count, "an import's copies are its own, also for an object");
        body.GetProperty("references").GetProperty("kept").GetInt32().Should().Be(0);
        using InferenceApiDbContext db = l.NewDb();
        (await db.ProjectAvatars.AnyAsync(a => a.Id == importId)).Should().BeFalse();
        (await db.ProjectFiles.CountAsync(f => f.ProjectId == second.Id)).Should().Be(0);
        l.Harness.Objects.Keys.Should().NotContain(k => k.StartsWith($"projects/{second.Id}/"));

        // The library item, its objects and the other import are untouched.
        (await db.AvatarLibraryItems.CountAsync()).Should().Be(1);
        l.Store.Keys().Should().HaveCount(originals.Count);
        (await db.ProjectAvatars.SingleAsync()).Id.Should().Be(sourceId);
        H.Read(await l.Controller().Get(itemId, second.Id, CancellationToken.None)).Body.GetProperty("importCount").GetInt32().Should().Be(1);

        // It can be imported again.
        (await l.ImportResultAsync(itemId, second)).Status.Should().Be(201);
    }

    [Fact]
    public async Task Deleting_the_object_avatar_an_item_was_published_from_keeps_the_projects_own_files()
    {
        var l = new L();
        (Guid itemId, Guid sourceId) = await l.PublishObjectAsync("Machine");
        List<ProjectFile> originals = l.FilesOf(sourceId);

        (int status, JsonElement body) = H.Read(await l.Harness.Controller().Delete(l.Project.Id, sourceId, CancellationToken.None));

        status.Should().Be(200);
        body.GetProperty("references").GetProperty("deleted").GetInt32().Should().Be(0);
        body.GetProperty("references").GetProperty("kept").GetInt32().Should().Be(originals.Count, "they are the project's ordinary media, not copies");
        l.Harness.Objects.Should().ContainKeys(originals.Select(f => f.StorageKey));
        using InferenceApiDbContext db = l.NewDb();
        (await db.ProjectFiles.CountAsync(f => f.ProjectId == l.Project.Id)).Should().Be(originals.Count);
        (await db.AvatarLibraryItems.SingleAsync()).Id.Should().Be(itemId);
    }

    [Fact]
    public async Task The_project_list_marks_an_import()
    {
        var l = new L();
        (Guid itemId, _) = await l.PublishPersonAsync("Anna");
        await l.Harness.CreateReadyAsync("Bea");

        JsonElement listed = H.Read(await l.Harness.Controller().List(l.Project.Id, CancellationToken.None)).Body;

        JsonElement[] avatars = [.. listed.EnumerateArray()];
        avatars.Single(a => a.GetProperty("name").GetString() == "Anna").GetProperty("fromLibrary").GetBoolean().Should().BeTrue();
        avatars.Single(a => a.GetProperty("name").GetString() == "Anna").GetProperty("libraryItemId").GetGuid().Should().Be(itemId);
        avatars.Single(a => a.GetProperty("name").GetString() == "Bea").GetProperty("fromLibrary").GetBoolean().Should().BeFalse();
        avatars.Single(a => a.GetProperty("name").GetString() == "Bea").GetProperty("libraryItemId").ValueKind.Should().Be(JsonValueKind.Null);
    }
}
