#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.FolderSync;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.FolderSync;

/// <summary>
/// The manifest reconciliation and its mass-delete guard (docs/local-folder-sync.md "The mass-delete
/// guard"). This is the one piece of folder sync that can destroy a customer's work, so the guard is
/// tested as behaviour: a folder that looks empty or mostly gone pauses the link and deletes NOTHING
/// until the person confirms from the desktop app; a link a person paused themselves is not acted on at
/// all; and a path that belongs to another source (an upload, an agent, a GitHub pull) is never taken
/// over by a folder link.
///
/// <para>
/// Paths follow the protocol: the manifest carries paths RELATIVE to the chosen folder, and the stored
/// project path is <c>{targetFolder}/{relative}</c> (<c>FolderSyncPaths.StoredPath</c>). A link
/// targeting <c>Footage</c> writing <c>clip.mp4</c> therefore owns the project file
/// <c>Footage/clip.mp4</c>, and every assertion below is written in those terms.
/// </para>
/// </summary>
public class FolderSyncGuardTests
{
    private const string HashA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string HashB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public async Task A_new_path_is_an_upload_and_a_changed_hash_is_an_upload_again()
    {
        Harness h = new();
        h.LinkRow("clip.mp4", HashA);
        h.LinkRow("same.mp4", HashA);

        // clip.mp4 changed on the computer; one file is new; one is byte-identical.
        ManifestResultResponse result = await h.Reconcile(
            File("clip.mp4", HashB), File("new.mp4", HashA), File("same.mp4", HashA));

        result.Upload.Should().BeEquivalentTo(["clip.mp4", "new.mp4"]);
        result.Upload.Should().NotContain("same.mp4", "an unchanged file is never re-uploaded");
        result.Delete.Should().BeEmpty();
        result.Paused.Should().BeFalse();
    }

    [Fact]
    public async Task An_unchanged_file_that_is_already_pulled_is_not_uploaded_again()
    {
        Harness h = new();
        h.LinkRow("clip.mp4", HashA);

        ManifestResultResponse result = await h.Reconcile(File("clip.mp4", HashA));

        result.Upload.Should().BeEmpty();
        result.Delete.Should().BeEmpty();
        result.Skipped.Should().BeEmpty();
    }

    [Fact]
    public async Task A_path_missing_from_the_manifest_is_deleted()
    {
        Harness h = new();
        h.LinkRow("gone.mp4", HashA);
        h.LinkRow("stays.mp4", HashA);

        ManifestResultResponse result = await h.Reconcile(File("stays.mp4", HashA));

        result.Delete.Should().BeEquivalentTo(["gone.mp4"]);
        result.Upload.Should().BeEmpty();
    }

    [Fact]
    public async Task A_sync_removal_skips_a_held_row_and_keeps_its_object()
    {
        Harness h = new();
        h.LinkRow("made.png", HashA);
        ProjectFile made = h.Rows.Single();
        made.OutputProducedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        h.Database.Projects.Single(p => p.Id == h.ProjectId).CsamOutputHoldAt = new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await h.Database.SaveChangesAsync();

        int deleted = await h.Service.DeleteFilesAsync(h.Link, ["made.png"], CancellationToken.None);

        deleted.Should().Be(0);
        h.Rows.Should().ContainSingle(r => r.Id == made.Id);
    }

    [Fact]
    public async Task Deleting_a_synced_photo_leaves_a_picture_that_used_it_its_own_copy()
    {
        Harness h = new();
        h.LinkRow("photo.png", HashA);
        ProjectFile photo = h.Rows.Single();
        string photoKey = photo.StorageKey;
        Guid pictureId = await PictureCopySeed.PictureOverAsync(h.Database, photo);

        int deleted = await h.Service.DeleteFilesAsync(h.Link, ["photo.png"], CancellationToken.None);

        deleted.Should().Be(1);
        await PictureCopySeed.AssertKeptOwnCopyAsync(h.Database, h.ProjectId, pictureId, photoKey);
    }

    [Fact]
    public async Task A_sync_removal_still_copies_for_every_picture_when_the_workspace_has_no_storage_left()
    {
        // A background removal cannot be refused (the file is already gone on the computer), so unlike a
        // user's delete it never consults the quota: the copies are counted afterwards.
        Harness h = new() { StorageLimitGb = 0 };
        h.LinkRow("photo.png", HashA, size: 1_000);
        ProjectFile photo = h.Rows.Single();
        string photoKey = photo.StorageKey;
        Guid[] pictures =
        [
            await PictureCopySeed.PictureOverAsync(h.Database, photo),
            await PictureCopySeed.PictureOverAsync(h.Database, photo),
            await PictureCopySeed.PictureOverAsync(h.Database, photo)
        ];

        int deleted = await h.Service.DeleteFilesAsync(h.Link, ["photo.png"], CancellationToken.None);

        deleted.Should().Be(1);
        foreach (Guid pictureId in pictures)
            await PictureCopySeed.AssertKeptOwnCopyAsync(h.Database, h.ProjectId, pictureId, photoKey);
    }

    [Fact]
    public async Task An_empty_manifest_over_a_link_that_holds_files_pauses_instead_of_deleting()
    {
        Harness h = new();
        h.LinkRow("a.mp4", HashA);
        h.LinkRow("b.mp4", HashA);

        // This is the case the guard exists for: a disconnected drive, a moved folder, or an unmounted
        // iCloud/OneDrive folder would otherwise look exactly like "the user deleted everything".
        ManifestResultResponse result = await h.Reconcile();

        result.Delete.Should().BeEmpty("nothing may be deleted before the person confirms");
        result.Paused.Should().BeTrue();
        result.PausedReason.Should().Be(ProjectLocalFolderLink.PauseReasonGuard);
        result.LastError.Should().Contain("paused so nothing is deleted");

        h.Link.PausedAt.Should().NotBeNull();
        h.Link.PauseReason.Should().Be(ProjectLocalFolderLink.PauseReasonGuard);
        h.Rows.Should().HaveCount(2, "the project files are untouched");
    }

    [Fact]
    public async Task Deleting_most_of_a_guarded_link_pauses_instead_of_deleting()
    {
        Harness h = new();
        // GuardMinimumFiles is 20: at that size, losing more than half at once is treated as an accident.
        for (int i = 0; i < 20; i++) h.LinkRow($"{i}.mp4", HashA);

        // 11 of 20 gone (delete * 2 > 20).
        List<ManifestFileEntry> manifest = [];
        for (int i = 11; i < 20; i++) manifest.Add(File($"{i}.mp4", HashA));

        ManifestResultResponse result = await h.Reconcile([.. manifest]);

        result.Delete.Should().BeEmpty();
        result.Paused.Should().BeTrue();
        h.Rows.Should().HaveCount(20);
    }

    [Fact]
    public async Task Losing_half_of_a_guarded_link_exactly_does_not_pause()
    {
        Harness h = new();
        for (int i = 0; i < 20; i++) h.LinkRow($"{i}.mp4", HashA);

        // 10 of 20 gone: exactly half, which is not "more than half" — the guard must not fire.
        List<ManifestFileEntry> manifest = [];
        for (int i = 10; i < 20; i++) manifest.Add(File($"{i}.mp4", HashA));

        ManifestResultResponse result = await h.Reconcile([.. manifest]);

        result.Delete.Should().HaveCount(10);
        result.Paused.Should().BeFalse();
    }

    [Fact]
    public async Task A_small_link_deletes_normally_when_only_some_files_left()
    {
        Harness h = new();
        h.LinkRow("a.mp4", HashA);
        h.LinkRow("b.mp4", HashA);

        // One of two gone: below GuardMinimumFiles, and not an empty folder, so it is an ordinary deletion.
        ManifestResultResponse result = await h.Reconcile(File("a.mp4", HashA));

        result.Delete.Should().BeEquivalentTo(["b.mp4"]);
        result.Paused.Should().BeFalse();
    }

    [Fact]
    public async Task An_empty_manifest_always_pauses_even_for_a_small_link()
    {
        Harness h = new();
        h.LinkRow("a.mp4", HashA);
        h.LinkRow("b.mp4", HashA);

        // An empty listing is never taken at face value, however few files the link holds: the size
        // threshold only governs a PARTIAL deletion. A folder that reads empty is the one case that is
        // far more likely to be a disconnected drive than a real deletion.
        ManifestResultResponse result = await h.Reconcile();

        result.Delete.Should().BeEmpty();
        result.Paused.Should().BeTrue();
    }

    [Fact]
    public async Task Confirming_from_the_computer_applies_the_deletions_and_lifts_the_guard()
    {
        Harness h = new();
        h.LinkRow("a.mp4", HashA);
        h.LinkRow("b.mp4", HashA);
        (await h.Reconcile()).Paused.Should().BeTrue();

        // The desktop app asks the person, then re-sends the manifest with the confirmation.
        ManifestResultResponse confirmed = await h.Reconcile([], confirmLargeDelete: true);

        confirmed.Delete.Should().BeEquivalentTo(["a.mp4", "b.mp4"]);
        confirmed.Paused.Should().BeFalse();
        h.Link.PausedAt.Should().BeNull("the guard pause is lifted once the person confirmed");
    }

    [Fact]
    public async Task A_folder_that_looks_whole_again_lifts_a_guard_pause_by_itself()
    {
        Harness h = new();
        h.LinkRow("a.mp4", HashA);
        h.LinkRow("b.mp4", HashA);
        (await h.Reconcile()).Paused.Should().BeTrue();

        // The drive came back: the same files are there, so there is nothing to confirm.
        ManifestResultResponse result = await h.Reconcile(File("a.mp4", HashA), File("b.mp4", HashA));

        result.Paused.Should().BeFalse();
        h.Link.PausedAt.Should().BeNull();
    }

    [Fact]
    public async Task A_link_paused_by_a_person_is_not_acted_on_at_all()
    {
        Harness h = new();
        h.LinkRow("a.mp4", HashA);
        h.Link.PausedAt = DateTime.UtcNow;
        h.Link.PauseReason = ProjectLocalFolderLink.PauseReasonUser;

        ManifestResultResponse result = await h.Reconcile(File("new.mp4", HashA));

        result.Upload.Should().BeEmpty("a paused link uploads nothing");
        result.Delete.Should().BeEmpty();
        result.Paused.Should().BeTrue();
        result.PausedReason.Should().Be(ProjectLocalFolderLink.PauseReasonUser);
        h.Rows.Should().HaveCount(1);
    }

    [Fact]
    public async Task A_path_owned_by_another_source_is_skipped_not_taken_over()
    {
        Harness h = new();
        // A file a person uploaded (no source kind) sits at the same path the folder is pushing.
        h.ForeignRow("mine.mp4", sourceKind: null, linkId: null);
        // And a file another folder link owns.
        h.ForeignRow("theirs.mp4", sourceKind: LocalFolderSyncService.SourceKind, linkId: Guid.NewGuid());

        ManifestResultResponse result = await h.Reconcile(File("mine.mp4", HashB), File("theirs.mp4", HashB));

        result.Upload.Should().BeEmpty("an occupied path is never overwritten");
        result.Skipped.Should().HaveCount(2);
        result.Skipped.Should().OnlyContain(s => s.Reason == "occupied");
        h.Rows.Should().HaveCount(2);
        h.Database.ChangeTracker.Clear();
        (await h.Database.ProjectFiles.SingleAsync(f => f.OriginalPath == "Footage/mine.mp4"))
            .SourceKind.Should().BeNull("the upload is still an upload");
    }

    [Fact]
    public async Task An_invalid_path_or_hash_is_skipped_with_a_reason()
    {
        Harness h = new();

        ManifestResultResponse result = await h.Reconcile(
            new ManifestFileEntry("../escape.mp4", 10, HashA),
            new ManifestFileEntry("back\\slash.mp4", 10, HashA),
            new ManifestFileEntry("hash.mp4", 10, "not-a-sha256"),
            new ManifestFileEntry("ok.mp4", 10, HashA));

        result.Upload.Should().BeEquivalentTo(["ok.mp4"]);
        result.Skipped.Should().HaveCount(3);
        result.Skipped.Should().OnlyContain(s => s.Reason == "invalid_path");
    }

    [Fact]
    public async Task The_manifest_reports_storage_usage_so_the_app_can_explain_a_full_workspace()
    {
        Harness h = new();
        h.StorageLimitGb = 1;
        h.LinkRow("existing.mp4", HashA, size: 1024L * 1024 * 1024);

        ManifestResultResponse result = await h.Reconcile(
            File("existing.mp4", HashA), File("huge.mp4", HashB, size: 2L * 1024 * 1024 * 1024));

        // Every manifest answer carries the workspace's usage, which is what the desktop app shows as
        // "Storage is full (X of Y used)".
        result.Storage.LimitBytes.Should().Be(1024L * 1024 * 1024);
        result.Storage.UsedBytes.Should().Be(1024L * 1024 * 1024);

        // The over-quota file is still LISTED as an upload, and that is deliberate: the server is not the
        // one writing these bytes, so the quota is enforced where the upload ticket is minted
        // (IDirectUploadService.CreateAsync answers 413 STORAGE_QUOTA_EXCEEDED, which the device reports
        // instead of uploading). Asserting it here pins the split — the manifest is a diff, not a gate.
        result.Upload.Should().Contain("huge.mp4");
    }

    // ------------------------------------------------------------------ fixture

    private sealed class Harness
    {
        /// <summary>The chosen folder's name inside the project: the link's target folder.</summary>
        private const string Target = "Footage";

        public Harness()
        {
            Database = new InferenceApiDbContext(
                new DbContextOptionsBuilder<InferenceApiDbContext>()
                    .UseInMemoryDatabase(Guid.NewGuid().ToString())
                    .Options);

            ProjectId = Guid.NewGuid();
            OrganizationId = Guid.NewGuid();
            DeviceId = Guid.NewGuid();

            Link = new ProjectLocalFolderLink
            {
                Id = Guid.NewGuid(),
                ProjectId = ProjectId,
                OrganizationId = OrganizationId,
                DeviceId = DeviceId,
                TargetFolder = Target,
                DisplayPath = "D:\\Footage",
                CreatedByUserId = Guid.NewGuid()
            };

            // The Project row must exist as well as the link: project_files carries a tenant filter that
            // joins through it, so a file whose project does not exist is invisible to every read.
            Database.Projects.Add(new Project
            {
                Id = ProjectId,
                Name = "Promo film",
                OwnerId = Link.CreatedByUserId,
                OrganizationId = OrganizationId,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
            Database.ProjectLocalFolderLinks.Add(Link);
            Database.SaveChanges();

            Service = new LocalFolderSyncService(
                Database, new NoStorage(), new FixedEntitlements(() => StorageLimitGb),
                NullLogger<LocalFolderSyncService>.Instance, TimeProvider.System);

            Device = new DeviceContext(DeviceId, "Studio PC", OrganizationId, Link.CreatedByUserId);
        }

        public InferenceApiDbContext Database { get; }

        public ProjectLocalFolderLink Link { get; }

        public DeviceContext Device { get; }

        public LocalFolderSyncService Service { get; }

        public Guid ProjectId { get; }

        public Guid OrganizationId { get; }

        public Guid DeviceId { get; }

        public int StorageLimitGb { get; set; } = int.MaxValue;

        public List<ProjectFile> Rows => [.. Database.ProjectFiles.Where(f => f.ProjectId == ProjectId)];

        /// <summary>A file this link already pulled, at <c>{Target}/{relativePath}</c>.</summary>
        public void LinkRow(string relativePath, string hash, long size = 10) =>
            Add(relativePath, LocalFolderSyncService.SourceKind, hash, Link.Id, size);

        /// <summary>A file at the same path that belongs to a person's upload or another link.</summary>
        public void ForeignRow(string relativePath, string? sourceKind, Guid? linkId, long size = 10) =>
            Add(relativePath, sourceKind, HashA, linkId, size);

        private ProjectFile Add(string relativePath, string? sourceKind, string? revision, Guid? linkId, long size)
        {
            string stored = $"{Target}/{relativePath}";
            ProjectFile file = new()
            {
                Id = Guid.NewGuid(),
                ProjectId = ProjectId,
                OriginalFileName = relativePath.Split('/')[^1],
                OriginalPath = stored,
                DirectoryPath = stored.Contains('/') ? stored[..stored.LastIndexOf('/')] : null,
                Category = "userFiles",
                StorageKey = $"projects/{ProjectId}/userFiles/{Guid.NewGuid():N}",
                StorageFileName = relativePath.Split('/')[^1],
                MimeType = "video/mp4",
                SizeBytes = size,
                SourceKind = sourceKind,
                SourceRevision = revision,
                SourceLinkId = linkId,
                UploadedAt = DateTime.UtcNow
            };
            Database.ProjectFiles.Add(file);
            Database.SaveChanges();
            return file;
        }

        public Task<ManifestResultResponse> Reconcile(params ManifestFileEntry[] files) =>
            Reconcile(files, confirmLargeDelete: false);

        public async Task<ManifestResultResponse> Reconcile(ManifestFileEntry[] files, bool confirmLargeDelete)
        {
            ManifestResultResponse result =
                await Service.ReconcileAsync(Link, Device, files, confirmLargeDelete, CancellationToken.None);
            await Database.SaveChangesAsync();
            return result;
        }
    }

    private static ManifestFileEntry File(string relativePath, string hash, long size = 10) =>
        new(relativePath, size, hash);

    /// <summary>Folder sync only deletes objects; a delete is recorded so a test could assert on it.</summary>
    private sealed class NoStorage : IFileStorageService
    {
        public List<string> Deleted { get; } = [];

        public string BucketName => "test-bucket";

        public Task<StoredFileObject> UploadAsync(Guid projectId, Guid fileId, Stream content, string fileName,
            string contentType, IReadOnlyDictionary<string, string>? metadata, CancellationToken ct,
            string category = "userFiles", string? originalPath = null) =>
            throw new NotSupportedException("folder sync never uploads through the API's storage service");

        public Task<Stream> DownloadAsync(Guid projectId, string storageKey, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task DeleteAsync(Guid projectId, string storageKey, CancellationToken ct)
        {
            Deleted.Add(storageKey);
            return Task.CompletedTask;
        }

        public Task<StoredObjectMetadata?> GetMetadataAsync(Guid projectId, string storageKey, CancellationToken ct) =>
            Task.FromResult<StoredObjectMetadata?>(null);

        public Task<string?> GetObjectETagAsync(Guid projectId, string storageKey, CancellationToken ct) =>
            Task.FromResult<string?>(null);


        public Task<int> DeleteManyAsync(Guid projectId, IReadOnlyCollection<string> storageKeys, CancellationToken ct)
        {
            Deleted.AddRange(storageKeys);
            return Task.FromResult(storageKeys.Count);
        }

        public Task MoveAsync(Guid projectId, string sourceStorageKey, string destinationStorageKey, CancellationToken ct) =>
            Task.CompletedTask;

        public Task CopyAsync(Guid projectId, string sourceStorageKey, string destinationStorageKey, CancellationToken ct) =>
            Task.CompletedTask;
    }

    private sealed class FixedEntitlements(Func<int> storageGb) : IOrganizationEntitlements
    {
        public ValueTask<OrganizationEntitlementSet> GetAsync(Guid organizationId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new OrganizationEntitlementSet { StorageGb = storageGb() });
    }

    [Fact]
    public async Task A_sync_removal_keeps_a_row_an_avatar_lists()
    {
        Harness h = new();
        h.LinkRow("face.png", HashA);
        ProjectFile face = h.Rows.Single();
        ReelBolt.WorkflowEngine.Tests.Avatars.AvatarSeed.Attach(h.Database, h.ProjectId, "Anna", face);

        int deleted = await h.Service.DeleteFilesAsync(h.Link, ["face.png"], CancellationToken.None);

        deleted.Should().Be(0);
        h.Rows.Should().ContainSingle(r => r.Id == face.Id);
    }
}
