#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Avatars.Library;
using ReelBolt.Inference.Api.Services.Csam;
using ReelBolt.Shared.Avatars;
using ReelBolt.Shared.Data.Models;
using Xunit;
using H = ReelBolt.WorkflowEngine.Tests.Avatars.AvatarTestHarness;
using L = ReelBolt.WorkflowEngine.Tests.Avatars.AvatarLibraryHarness;

namespace ReelBolt.WorkflowEngine.Tests.Avatars;

/// <summary>
/// Holds and the workspace avatar library: publishing refuses a quarantined project and a held or unscanned source file
/// and copies nothing; a verdict that lands AFTER a publish holds the library copy (picture, list, import), judged by
/// content digest inside the organization only.
/// </summary>
public class AvatarLibraryHoldTests
{
    private sealed class NoopMatcher : ICsmaHashMatcher
    {
        public Task<CsamScanOutcome> ScanAsync(ProjectFile file, Guid? actorUserId, CancellationToken ct) =>
            Task.FromResult(new CsamScanOutcome(CsamScanStatus.Clean, null, null, false, null));
    }

    private static async Task Mutate(L l, Guid fileId, Action<ProjectFile> change)
    {
        using InferenceApiDbContext db = l.NewDb();
        change(await db.ProjectFiles.IgnoreQueryFilters().SingleAsync(f => f.Id == fileId));
        await db.SaveChangesAsync();
    }

    private static async Task<Guid> PictureIdAsync(L l, Guid itemId)
    {
        using InferenceApiDbContext db = l.NewDb();
        return (await db.AvatarLibraryReferences.SingleAsync(r => r.LibraryItemId == itemId && r.Kind == AvatarReferenceKind.Image)).Id;
    }

    private static async Task NothingPublishedAsync(L l)
    {
        using InferenceApiDbContext db = l.NewDb();
        (await db.AvatarLibraryItems.CountAsync()).Should().Be(0);
        (await db.AvatarLibraryReferences.CountAsync()).Should().Be(0);
        l.Store.Keys().Should().BeEmpty("a refused publish leaves no object in the library");
        l.Store.Deleted.Should().BeEmpty("a refused publish copies nothing, so it removes nothing");
    }

    // ------------------------------------------------------------------ fix 1: publish

    [Fact]
    public async Task Publishing_from_a_quarantined_project_is_refused_and_copies_nothing()
    {
        var l = new L();
        ProjectFile face = l.Harness.AddImage("anna.png", salt: 1);
        Guid avatarId = await l.Harness.CreateReadyAsync("Anna", face);
        using (InferenceApiDbContext db = l.NewDb())
        {
            (await db.Projects.IgnoreQueryFilters().SingleAsync(p => p.Id == l.Project.Id)).ContentQuarantinedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        (int status, JsonElement body) = H.Read(await l.Controller().Publish(L.PersonRequest(l.Project.Id, avatarId), CancellationToken.None));

        status.Should().Be(409);
        body.GetProperty("code").GetString().Should().Be("PROJECT_CONTENT_QUARANTINED");
        await NothingPublishedAsync(l);
    }

    [Fact]
    public async Task Publishing_with_one_quarantined_source_file_is_refused_and_copies_nothing()
    {
        var l = new L();
        ProjectFile face = l.Harness.AddImage("anna.png", salt: 1);
        ProjectFile side = l.Harness.AddImage("anna-side.png", salt: 2);
        Guid avatarId = await l.Harness.CreateReadyAsync("Anna", face, side);
        await Mutate(l, side.Id, f => f.ModerationStatus = FileModerationStatus.Quarantined);

        (int status, JsonElement body) = H.Read(await l.Controller().Publish(L.PersonRequest(l.Project.Id, avatarId), CancellationToken.None));

        status.Should().Be(409);
        body.GetProperty("reason").GetString().Should().Be("reference_held");
        await NothingPublishedAsync(l);
    }

    [Fact]
    public async Task A_source_file_whose_csam_scan_is_pending_is_never_published()
    {
        var l = new L();
        ProjectFile face = l.Harness.AddImage("anna.png", salt: 1);
        Guid avatarId = await l.Harness.CreateReadyAsync("Anna", face);
        await Mutate(l, face.Id, f => f.CsamStatus = CsamScanStatus.Pending);
        InferenceApiDbContext db = l.NewDb();
        var library = new AvatarLibraryService(
            db, l.Store, l.Harness.Storage.Object, l.Harness.Deletion(db), Microsoft.Extensions.Options.Options.Create(l.Harness.Options),
            NullLogger<AvatarLibraryService>.Instance, l.Harness.Entitlements, l.PlanGate, csam: new NoopMatcher());
        Project project = await db.Projects.SingleAsync(p => p.Id == l.Project.Id);

        AvatarLibraryResult<AvatarLibraryItemView> result = await library.PublishAsync(
            project, avatarId, new AvatarLibraryAttestation(
                AvatarWorkspaceConsentStatement.English, "en", SubjectIsAdult: true, RetentionDays: 120), l.Harness.OwnerId, CancellationToken.None);

        result.Refusal.Should().NotBeNull();
        result.Refusal!.Code.Should().Be("AVATAR_NOT_USABLE");
        await NothingPublishedAsync(l);
    }

    // ------------------------------------------------------------------ fix 2: later holds reach the copy

    [Fact]
    public async Task A_clean_item_is_served_listed_usable_and_importable()
    {
        var l = new L();
        ProjectFile face = l.Harness.AddImage("anna.png", salt: 1);
        (Guid itemId, _) = await l.PublishPersonAsync("Anna", face);
        Guid picture = await PictureIdAsync(l, itemId);

        (await l.Controller().Picture(itemId, picture, CancellationToken.None)).Should().BeOfType<FileStreamResult>();
        (await l.Library().ListAsync(l.OrganizationId, null, CancellationToken.None)).Single().UnusableReason.Should().BeNull();
        (await l.ImportResultAsync(itemId, l.AddProject("Other"))).Status.Should().Be(201);
    }

    [Fact]
    public async Task A_library_picture_is_404_once_its_source_is_quarantined_after_publishing()
    {
        var l = new L();
        ProjectFile face = l.Harness.AddImage("anna.png", salt: 1);
        (Guid itemId, _) = await l.PublishPersonAsync("Anna", face);
        Guid picture = await PictureIdAsync(l, itemId);
        await Mutate(l, face.Id, f => f.CsamStatus = CsamScanStatus.Quarantined);

        H.Read(await l.Controller().Picture(itemId, picture, CancellationToken.None)).Status.Should().Be(404);
    }

    [Fact]
    public async Task A_moderation_quarantine_on_a_file_with_the_same_digest_holds_the_copy_even_without_a_stored_file_digest()
    {
        var l = new L();
        ProjectFile face = l.Harness.AddImage("anna.png", salt: 1);
        (Guid itemId, _) = await l.PublishPersonAsync("Anna", face);
        // Another project of the SAME organization has the same bytes, and a verdict lands there.
        Project other = l.AddProject("Other");
        ProjectFile twin = l.Harness.AddFile("twin.png", "image/png", l.Harness.Objects[face.StorageKey], projectId: other.Id);
        await Mutate(l, twin.Id, f =>
        {
            f.ContentSha256 = H.Sha256(l.Harness.Objects[face.StorageKey]);
            f.ModerationStatus = FileModerationStatus.Quarantined;
        });

        (await l.Library().ListAsync(l.OrganizationId, null, CancellationToken.None)).Single().UnusableReason.Should().Be("reference_held");
    }

    [Fact]
    public async Task Importing_a_held_item_is_refused_and_the_list_marks_it_unusable()
    {
        var l = new L();
        ProjectFile face = l.Harness.AddImage("anna.png", salt: 1);
        (Guid itemId, _) = await l.PublishPersonAsync("Anna", face);
        await Mutate(l, face.Id, f => f.CsamStatus = CsamScanStatus.Quarantined);
        Project target = l.AddProject("Other");

        (int status, JsonElement body) = await l.ImportResultAsync(itemId, target);

        status.Should().Be(409);
        body.GetProperty("code").GetString().Should().Be("LIBRARY_ITEM_NOT_USABLE");
        body.GetProperty("reason").GetString().Should().Be("reference_held");
        using (InferenceApiDbContext db = l.NewDb())
            (await db.ProjectAvatars.CountAsync(a => a.ProjectId == target.Id)).Should().Be(0, "nothing was imported");

        AvatarLibraryItemView view = (await l.Library().ListAsync(l.OrganizationId, null, CancellationToken.None)).Single();
        view.UnusableReason.Should().Be("reference_held");
        (await l.Library().GetAsync(l.OrganizationId, itemId, null, CancellationToken.None))!.UnusableReason.Should().Be("reference_held");
    }

    [Fact]
    public async Task A_quarantined_file_with_the_same_digest_in_another_organization_does_not_hold_this_copy()
    {
        var l = new L();
        ProjectFile face = l.Harness.AddImage("anna.png", salt: 1);
        (Guid itemId, _) = await l.PublishPersonAsync("Anna", face);
        Guid picture = await PictureIdAsync(l, itemId);
        string digest = H.Sha256(l.Harness.Objects[face.StorageKey]);
        Project foreign = l.AddProject("Foreign", Guid.NewGuid(), Guid.NewGuid());
        ProjectFile twin = l.Harness.AddFile("twin.png", "image/png", l.Harness.Objects[face.StorageKey], projectId: foreign.Id);
        await Mutate(l, twin.Id, f =>
        {
            f.ContentSha256 = digest;
            f.CsamStatus = CsamScanStatus.Quarantined;
        });

        (await l.Controller().Picture(itemId, picture, CancellationToken.None)).Should().BeOfType<FileStreamResult>();
        (await l.Library().ListAsync(l.OrganizationId, null, CancellationToken.None)).Single().UnusableReason.Should().BeNull();
    }

    // ------------------------------------------------------------------ the gate cannot be forgotten

    [Fact]
    public void Every_library_read_path_goes_through_the_hold_gate()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "inference", "src", "ReelBolt.Inference.Api", "Services", "Avatars", "Library")))
            dir = Path.GetDirectoryName(dir);
        dir.Should().NotBeNull("the repository root is above the test binaries");
        string library = Path.Combine(dir!, "inference", "src", "ReelBolt.Inference.Api", "Services", "Avatars", "Library");
        Dictionary<string, string> sources = Directory.GetFiles(library, "*.cs").ToDictionary(Path.GetFileName, File.ReadAllText)!;

        // Every method that serves a library object, builds the item list or lets an item into a project names the gate.
        foreach ((string file, string method) in new[]
                 {
                     ("AvatarLibraryService.cs", "OpenPictureAsync"),
                     ("AvatarLibraryService.cs", "ToViewsAsync"),
                     ("AvatarLibraryService.Import.cs", "ImportRefusalAsync"),
                 })
        {
            Match m = Regex.Match(sources[file], @"\n    (public|private|internal)[^\n]*\b" + method + @"\(");
            m.Success.Should().BeTrue();
            string rest = sources[file][m.Index..];
            int next = Regex.Match(rest[m.Length..], @"\n    (public|private|internal)[^\n]*\(").Index;
            rest[..(next + m.Length)].Should().Contain("AvatarLibraryHoldGate.ReadAsync", $"{method} must judge holds");
        }

        // Any OTHER file that reads library objects or copies them into a project is a new read path: it must use the gate.
        string[] exempt = ["AvatarLibraryService.cs", "AvatarLibraryService.Import.cs", "AvatarLibraryStore.cs", "AvatarLibraryService.Publish.cs",
            "AvatarLibraryService.Manage.cs", "AvatarLibraryService.Sweep.cs", "AvatarLibraryHoldGate.cs"];
        foreach ((string file, string text) in sources.Where(s => !exempt.Contains(s.Key)))
        {
            text.Should().NotContain("_store.OpenReadAsync", $"{file} would serve library bytes without the hold gate");
            text.Should().NotContain("CopyIntoProjectAsync", $"{file} would copy library bytes without the hold gate");
        }

        // Publish and Manage read the store only for their own copies (digest check at publish; delete): no served bytes.
        sources["AvatarLibraryService.Manage.cs"].Should().NotContain("OpenReadAsync");
    }
}
