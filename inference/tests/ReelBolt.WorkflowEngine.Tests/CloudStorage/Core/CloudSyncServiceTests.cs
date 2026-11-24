#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.CloudStorage;
using ReelBolt.Shared.Data.Models;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.CloudStorage.Core;

/// <summary>
/// The generic reconcile (docs/design/cloud-drive-sync.md "Core services"), against a fake provider:
/// create / rewrite in place / unchanged / delete, occupancy, selection, the GitHub-failure lessons
/// (zero-byte files, per-file skips, incremental saves) and the limits.
/// </summary>
public class CloudSyncServiceTests
{
    private static async Task<(CloudFixture F, Project Project, ProjectCloudLink Link)> NewAsync(string? includePathsJson = null)
    {
        CloudFixture f = new();
        Project project = await f.AddProjectAsync();
        CloudStorageConnection connection = await f.AddConnectionAsync();
        ProjectCloudLink link = await f.AddLinkAsync(project, connection, includePathsJson);
        return (f, project, link);
    }

    [Fact]
    public async Task Sync_creates_rewrites_in_place_keeps_unchanged_and_deletes_vanished_files()
    {
        (CloudFixture f, Project project, ProjectCloudLink link) = await NewAsync();
        f.Provider.AddFile("intro.md", "hello", "r1");
        f.Provider.AddFile("scenes/one.txt", "one", "r1");

        CloudSyncOutcome first = await f.Sync.SyncAsync(link.Id, force: true, CancellationToken.None);
        first.Status.Should().Be("synced");
        first.Written.Should().Be(2);
        List<ProjectFile> afterFirst = await f.SyncedFilesAsync(project.Id);
        afterFirst.Select(r => r.OriginalPath).Should().Equal("intro.md", "scenes/one.txt");
        afterFirst.Should().OnlyContain(r => r.SummaryStatus == SummaryStatus.Done && r.IndexingStatus == FileIndexingStatus.NotIndexed);
        Guid introId = afterFirst[0].Id;

        // intro.md changes, one.txt does not, two.txt is new.
        f.Provider.AddFile("intro.md", "hello again", "r2");
        f.Provider.AddFile("scenes/two.txt", "two", "r1");
        f.Provider.Downloads.Clear();

        CloudSyncOutcome second = await f.Sync.SyncAsync(link.Id, force: true, CancellationToken.None);
        second.Written.Should().Be(2);
        second.Unchanged.Should().Be(1);
        f.Provider.Downloads.Should().BeEquivalentTo(["intro.md", "scenes/two.txt"], "an unchanged revision is never downloaded");
        ProjectFile intro = (await f.SyncedFilesAsync(project.Id)).Single(r => r.OriginalPath == "intro.md");
        intro.Id.Should().Be(introId, "a changed file is rewritten in place, keeping its id");
        intro.SourceRevision.Should().Be("r2");
        Encoding.UTF8.GetString(f.Storage.Objects[intro.StorageKey]).Should().Be("hello again");

        // two.txt leaves the folder.
        string twoKey = (await f.SyncedFilesAsync(project.Id)).Single(r => r.OriginalPath == "scenes/two.txt").StorageKey;
        f.Provider.RemoveFile("scenes/two.txt");
        CloudSyncOutcome third = await f.Sync.SyncAsync(link.Id, force: true, CancellationToken.None);
        third.Deleted.Should().Be(1);
        f.Storage.Objects.Should().NotContainKey(twoKey);
        (await f.SyncedFilesAsync(project.Id)).Select(r => r.OriginalPath).Should().Equal("intro.md", "scenes/one.txt");

        ProjectCloudLink stored = await f.Db.ProjectCloudLinks.SingleAsync(l => l.Id == link.Id);
        stored.LastSyncedAt.Should().Be(f.Clock.UtcNow);
        stored.LastSyncedFileCount.Should().Be(2);
        stored.LastError.Should().BeNull();
    }

    [Fact]
    public async Task A_path_occupied_by_an_upload_or_another_source_is_skipped_and_never_touched()
    {
        (CloudFixture f, Project project, ProjectCloudLink link) = await NewAsync();
        ProjectFile upload = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            OriginalFileName = "intro.md",
            OriginalPath = "intro.md",
            StorageKey = "projects/x/userFiles/upload",
            MimeType = "text/markdown",
            SourceKind = null
        };
        ProjectFile fromGitHub = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            OriginalFileName = "readme.md",
            OriginalPath = "readme.md",
            StorageKey = "projects/x/userFiles/gh",
            MimeType = "text/markdown",
            SourceKind = "github",
            SourceRevision = "sha"
        };
        f.Db.ProjectFiles.AddRange(upload, fromGitHub);
        await f.Db.SaveChangesAsync();
        f.Provider.AddFile("intro.md", "cloud intro");
        f.Provider.AddFile("readme.md", "cloud readme");
        f.Provider.AddFile("other.md", "other");

        CloudSyncOutcome outcome = await f.Sync.SyncAsync(link.Id, force: true, CancellationToken.None);

        outcome.Written.Should().Be(1);
        outcome.Skipped.Should().Be(2);
        f.Provider.Downloads.Should().Equal("other.md");
        List<ProjectFile> all = await f.Db.ProjectFiles.Where(r => r.ProjectId == project.Id).ToListAsync();
        all.Should().HaveCount(3);
        all.Single(r => r.Id == upload.Id).SourceKind.Should().BeNull();
        all.Single(r => r.Id == fromGitHub.Id).SourceRevision.Should().Be("sha");
    }

    [Fact]
    public async Task Narrowing_the_selection_deletes_the_files_that_fell_out_of_it()
    {
        (CloudFixture f, Project project, ProjectCloudLink link) = await NewAsync();
        f.Provider.AddFile("intro.md", "hello");
        f.Provider.AddFile("scenes/one.txt", "one");
        await f.Sync.SyncAsync(link.Id, force: true, CancellationToken.None);

        link.IncludePathsJson = "[\"scenes\"]";
        await f.Db.SaveChangesAsync();
        CloudSyncOutcome outcome = await f.Sync.SyncAsync(link.Id, force: true, CancellationToken.None);

        outcome.Deleted.Should().Be(1);
        (await f.SyncedFilesAsync(project.Id)).Select(r => r.OriginalPath).Should().Equal("scenes/one.txt");
    }

    [Fact]
    public async Task A_photo_that_fell_out_of_the_selection_leaves_a_picture_that_used_it_its_own_copy()
    {
        (CloudFixture f, Project project, ProjectCloudLink link) = await NewAsync();
        f.Provider.AddFile("intro.png", "pixels");
        f.Provider.AddFile("scenes/one.txt", "one");
        await f.Sync.SyncAsync(link.Id, force: true, CancellationToken.None);
        ProjectFile photo = (await f.SyncedFilesAsync(project.Id)).Single(r => r.OriginalPath == "intro.png");
        string photoKey = photo.StorageKey;
        Guid pictureId = await PictureCopySeed.PictureOverAsync(f.Db, photo);

        link.IncludePathsJson = "[\"scenes\"]";
        await f.Db.SaveChangesAsync();
        CloudSyncOutcome outcome = await f.Sync.SyncAsync(link.Id, force: true, CancellationToken.None);

        outcome.Deleted.Should().Be(1);
        await PictureCopySeed.AssertKeptOwnCopyAsync(f.Db, project.Id, pictureId, photoKey);
    }

    [Fact]
    public async Task A_typescript_source_is_stored_as_text_and_a_transport_stream_as_video()
    {
        // Issue 8: the extension map calls every .ts video/vnd.dlna.mpeg-tts; the bytes decide.
        (CloudFixture f, Project project, ProjectCloudLink link) = await NewAsync();
        f.Provider.AddFile("src/paint.ts", "export const paint = () => 0;\n");
        f.Provider.AddFile("footage/broadcast.ts", TransportStreamMimeSnifferTests.MpegTs());

        await f.Sync.SyncAsync(link.Id, force: true, CancellationToken.None);

        List<ProjectFile> rows = await f.SyncedFilesAsync(project.Id);
        rows.Single(r => r.OriginalPath == "src/paint.ts").MimeType.Should().Be("text/plain");
        ProjectFile video = rows.Single(r => r.OriginalPath == "footage/broadcast.ts");
        video.MimeType.Should().StartWith("video/");
        f.Storage.Objects[video.StorageKey].Should().Equal(TransportStreamMimeSnifferTests.MpegTs(), "the sniff rewinds before the bytes are stored");
    }

    [Fact]
    public async Task A_zero_byte_file_is_written_like_any_other()
    {
        (CloudFixture f, Project project, ProjectCloudLink link) = await NewAsync();
        f.Provider.AddFile("web/public/.gitkeep", Array.Empty<byte>(), "empty-rev");

        CloudSyncOutcome outcome = await f.Sync.SyncAsync(link.Id, force: true, CancellationToken.None);

        outcome.Status.Should().Be("synced");
        outcome.Written.Should().Be(1);
        ProjectFile row = (await f.SyncedFilesAsync(project.Id)).Single();
        row.OriginalPath.Should().Be("web/public/.gitkeep");
        row.SizeBytes.Should().Be(0);
        f.Storage.Objects[row.StorageKey].Should().BeEmpty();
    }

    [Fact]
    public async Task A_file_gone_or_forbidden_since_the_listing_is_skipped_and_the_sync_still_succeeds()
    {
        (CloudFixture f, Project project, ProjectCloudLink link) = await NewAsync();
        f.Provider.AddFile("a.txt", "a");
        f.Provider.AddFile("b.txt", "b");
        f.Provider.AddFile("c.txt", "c");
        f.Provider.DownloadFailures["id-a.txt"] = new CloudStorageApiException("404", CloudStorageErrorKind.NotFound, HttpStatusCode.NotFound);
        f.Provider.DownloadFailures["id-b.txt"] = new CloudStorageApiException("403", CloudStorageErrorKind.Forbidden, HttpStatusCode.Forbidden);

        CloudSyncOutcome outcome = await f.Sync.SyncAsync(link.Id, force: true, CancellationToken.None);

        outcome.Status.Should().Be("synced");
        outcome.Skipped.Should().Be(2);
        outcome.Written.Should().Be(1);
        ProjectCloudLink stored = await f.Db.ProjectCloudLinks.SingleAsync(l => l.Id == link.Id);
        stored.LastError.Should().BeNull();
        stored.LastSyncedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task A_server_failure_on_a_download_fails_the_sync_but_keeps_what_was_written()
    {
        (CloudFixture f, Project project, ProjectCloudLink link) = await NewAsync();
        f.Provider.AddFile("a.txt", "a");
        f.Provider.AddFile("b.txt", "b");
        f.Provider.DownloadFailures["id-b.txt"] = new CloudStorageApiException("503", CloudStorageErrorKind.ServerError, HttpStatusCode.ServiceUnavailable);

        CloudSyncOutcome outcome = await f.Sync.SyncAsync(link.Id, force: true, CancellationToken.None);

        outcome.Status.Should().Be("failed");
        await using InferenceApiDbContext observer = f.NewContext();
        ProjectCloudLink stored = await observer.ProjectCloudLinks.SingleAsync(l => l.Id == link.Id);
        stored.LastError.Should().Contain("Google Drive had a problem answering");
        stored.LastSyncedAt.Should().BeNull();
        (await observer.ProjectFiles.Where(r => r.ProjectId == project.Id).Select(r => r.OriginalPath).ToListAsync())
            .Should().Equal("a.txt");
    }

    [Fact]
    public async Task MaxFileBytes_is_enforced_while_streaming_not_only_on_the_declared_size()
    {
        (CloudFixture f, Project project, ProjectCloudLink link) = await NewAsync();
        f.Options.MaxFileBytes = 10;
        f.Provider.AddFile("declared-big.bin", new byte[11]);
        f.Provider.AddFile("lying.bin", new byte[3]);
        f.Provider.StreamOverrides["id-lying.bin"] = new byte[50];
        f.Provider.AddFile("fits.bin", new byte[10]);

        CloudSyncOutcome outcome = await f.Sync.SyncAsync(link.Id, force: true, CancellationToken.None);

        outcome.Written.Should().Be(1);
        outcome.Skipped.Should().Be(2);
        f.Provider.Downloads.Should().NotContain("declared-big.bin", "a declared size over the limit is not downloaded at all");
        (await f.SyncedFilesAsync(project.Id)).Select(r => r.OriginalPath).Should().Equal("fits.bin");
    }

    [Fact]
    public async Task A_truncated_listing_is_refused_as_too_many_files_and_changes_nothing()
    {
        (CloudFixture f, Project project, ProjectCloudLink link) = await NewAsync();
        f.Provider.AddFile("a.txt", "a");
        await f.Sync.SyncAsync(link.Id, force: true, CancellationToken.None);

        f.Provider.Truncated = true;
        f.Provider.RemoveFile("a.txt");
        CloudSyncOutcome outcome = await f.Sync.SyncAsync(link.Id, force: true, CancellationToken.None);

        outcome.Status.Should().Be("refused");
        outcome.Code.Should().Be("too_many_files");
        (await f.SyncedFilesAsync(project.Id)).Should().ContainSingle("a partial listing must never look like 'everything else was deleted'");
        (await f.Db.ProjectCloudLinks.SingleAsync(l => l.Id == link.Id)).LastError.Should().Contain("more than");
    }

    private static async Task<ProjectFile> HoldSyncedAsync(CloudFixture f, Project project)
    {
        ProjectFile row = (await f.SyncedFilesAsync(project.Id)).Single();
        ProjectFile tracked = await f.Db.ProjectFiles.SingleAsync(x => x.Id == row.Id);
        tracked.OutputProducedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        (await f.Db.Projects.SingleAsync(p => p.Id == project.Id)).CsamOutputHoldAt = new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await f.Db.SaveChangesAsync();
        return tracked;
    }

    [Fact]
    public async Task A_sync_does_not_remove_a_held_row_that_vanished_upstream()
    {
        (CloudFixture f, Project project, ProjectCloudLink link) = await NewAsync();
        f.Provider.AddFile("a.txt", "a", "r1");
        await f.Sync.SyncAsync(link.Id, force: true, CancellationToken.None);
        ProjectFile held = await HoldSyncedAsync(f, project);
        f.Provider.RemoveFile("a.txt");

        CloudSyncOutcome outcome = await f.Sync.SyncAsync(link.Id, force: true, CancellationToken.None);

        outcome.Skipped.Should().Be(1);
        (await f.SyncedFilesAsync(project.Id)).Should().ContainSingle(x => x.Id == held.Id && x.StorageKey == held.StorageKey);
    }

    [Fact]
    public async Task A_sync_does_not_overwrite_a_held_row_changed_upstream()
    {
        (CloudFixture f, Project project, ProjectCloudLink link) = await NewAsync();
        f.Provider.AddFile("a.txt", "a", "r1");
        await f.Sync.SyncAsync(link.Id, force: true, CancellationToken.None);
        ProjectFile held = await HoldSyncedAsync(f, project);
        string key = held.StorageKey;
        f.Provider.AddFile("a.txt", "changed", "r2");

        CloudSyncOutcome outcome = await f.Sync.SyncAsync(link.Id, force: true, CancellationToken.None);

        outcome.Skipped.Should().Be(1);
        outcome.Written.Should().Be(0);
        (await f.SyncedFilesAsync(project.Id)).Single().StorageKey.Should().Be(key);
    }

    [Fact]
    public async Task A_linked_folder_that_is_gone_fails_the_sync_and_deletes_nothing()
    {
        (CloudFixture f, Project project, ProjectCloudLink link) = await NewAsync();
        f.Provider.AddFile("a.txt", "a");
        f.Provider.AddFile("b.txt", "b");
        await f.Sync.SyncAsync(link.Id, force: true, CancellationToken.None);
        int objects = f.Storage.Objects.Count;

        f.Provider.ListFailure = new CloudStorageApiException("404", CloudStorageErrorKind.NotFound, HttpStatusCode.NotFound);
        CloudSyncOutcome outcome = await f.Sync.SyncAsync(link.Id, force: true, CancellationToken.None);

        outcome.Status.Should().Be("failed");
        (await f.SyncedFilesAsync(project.Id)).Should().HaveCount(2, "deletions only ever follow a successful listing");
        f.Storage.Objects.Should().HaveCount(objects);
        ProjectCloudLink stored = await f.Db.ProjectCloudLinks.SingleAsync(l => l.Id == link.Id);
        stored.LastError.Should().Contain("could not be found").And.Contain("Link another folder");
    }

    [Fact]
    public async Task A_name_with_a_backslash_is_skipped_never_turned_into_a_folder()
    {
        (CloudFixture f, Project project, ProjectCloudLink link) = await NewAsync();
        f.Provider.AddFile("a\\b.txt", "x");
        f.Provider.AddFile("ok.txt", "y");

        CloudSyncOutcome outcome = await f.Sync.SyncAsync(link.Id, force: true, CancellationToken.None);

        outcome.Skipped.Should().Be(1);
        (await f.SyncedFilesAsync(project.Id)).Select(r => r.OriginalPath).Should().Equal("ok.txt");
    }

    [Fact]
    public async Task Too_many_selected_files_is_refused_with_the_two_numbers()
    {
        (CloudFixture f, Project project, ProjectCloudLink link) = await NewAsync();
        f.Options.MaxFilesPerSync = 2;
        f.Provider.AddFile("a.txt", "a");
        f.Provider.AddFile("b.txt", "b");
        f.Provider.AddFile("c.txt", "c");

        CloudSyncOutcome outcome = await f.Sync.SyncAsync(link.Id, force: true, CancellationToken.None);

        outcome.Code.Should().Be("too_many_files");
        (await f.SyncedFilesAsync(project.Id)).Should().BeEmpty();
    }

    [Fact]
    public async Task OS_and_Office_litter_is_never_synced()
    {
        (CloudFixture f, Project project, ProjectCloudLink link) = await NewAsync();
        f.Provider.AddFile(".DS_Store", "x");
        f.Provider.AddFile("scenes/Thumbs.db", "x");
        f.Provider.AddFile("desktop.ini", "x");
        f.Provider.AddFile("docs/~$script.docx", "x");
        f.Provider.AddFile("docs/script.docx", "real");

        CloudSyncOutcome outcome = await f.Sync.SyncAsync(link.Id, force: true, CancellationToken.None);

        outcome.Written.Should().Be(1);
        (await f.SyncedFilesAsync(project.Id)).Select(r => r.OriginalPath).Should().Equal("docs/script.docx");
    }

    [Fact]
    public async Task Progress_is_saved_every_25_written_files()
    {
        (CloudFixture f, Project project, ProjectCloudLink link) = await NewAsync();
        for (int i = 0; i < 30; i++) f.Provider.AddFile($"file-{i:D2}.txt", $"content {i}");

        int? savedWhenFile26Started = null;
        f.Provider.OnDownload = async entry =>
        {
            if (entry.RelativePath != "file-25.txt") return;
            await using InferenceApiDbContext observer = f.NewContext();
            savedWhenFile26Started = await observer.ProjectFiles.CountAsync(r => r.ProjectId == project.Id);
        };

        CloudSyncOutcome outcome = await f.Sync.SyncAsync(link.Id, force: true, CancellationToken.None);

        outcome.Written.Should().Be(30);
        savedWhenFile26Started.Should().Be(25, "the first 25 written files are saved before the 26th is downloaded");
    }

    [Fact]
    public async Task An_interrupted_sync_keeps_what_it_wrote_and_the_next_one_resumes_without_downloading_it_again()
    {
        (CloudFixture f, Project project, ProjectCloudLink link) = await NewAsync();
        for (int i = 0; i < 5; i++) f.Provider.AddFile($"f{i}.txt", $"c{i}");
        using CancellationTokenSource stopping = new();
        f.Provider.OnDownload = entry =>
        {
            if (entry.RelativePath == "f3.txt") stopping.Cancel();
            return Task.CompletedTask;
        };

        Func<Task> interrupted = () => f.Sync.SyncAsync(link.Id, force: true, stopping.Token);
        await interrupted.Should().ThrowAsync<OperationCanceledException>();

        await using (InferenceApiDbContext observer = f.NewContext())
        {
            (await observer.ProjectFiles.CountAsync(r => r.ProjectId == project.Id)).Should().Be(3);
        }

        f.Provider.OnDownload = null;
        f.Provider.Downloads.Clear();
        CloudSyncOutcome resumed = await f.Sync.SyncAsync(link.Id, force: true, CancellationToken.None);
        resumed.Unchanged.Should().Be(3);
        f.Provider.Downloads.Should().BeEquivalentTo(["f3.txt", "f4.txt"]);
    }

    [Fact]
    public async Task An_unauthorized_call_refreshes_once_and_retries_once()
    {
        (CloudFixture f, Project project, ProjectCloudLink link) = await NewAsync();
        f.Provider.AddFile("a.txt", "a");
        f.Provider.RejectedTokens.Add("access-1");
        f.Provider.TokensToRefresh = new CloudTokenSet("access-2", null, f.Clock.UtcNow.AddHours(1), null);

        CloudSyncOutcome outcome = await f.Sync.SyncAsync(link.Id, force: true, CancellationToken.None);

        outcome.Status.Should().Be("synced");
        f.Provider.RefreshTokensUsed.Should().Equal("refresh-1");
        f.Provider.TokensSeen.Should().StartWith(["access-1", "access-2"]);
    }

    [Fact]
    public async Task A_refused_refresh_fails_the_sync_with_reconnect_and_marks_the_connection()
    {
        (CloudFixture f, Project project, ProjectCloudLink link) = await NewAsync();
        f.Provider.AddFile("a.txt", "a");
        f.Provider.RejectedTokens.Add("access-1");

        CloudSyncOutcome outcome = await f.Sync.SyncAsync(link.Id, force: true, CancellationToken.None);

        outcome.Status.Should().Be("failed");
        ProjectCloudLink stored = await f.Db.ProjectCloudLinks.SingleAsync(l => l.Id == link.Id);
        stored.LastError.Should().Contain("Reconnect");
        (await f.Db.CloudStorageConnections.SingleAsync()).LastError.Should().NotBeNull();
    }

    [Fact]
    public async Task A_link_without_a_connection_fails_with_link_it_again()
    {
        CloudFixture f = new();
        Project project = await f.AddProjectAsync();
        ProjectCloudLink link = await f.AddLinkAsync(project, connection: null);

        CloudSyncOutcome outcome = await f.Sync.SyncAsync(link.Id, force: true, CancellationToken.None);

        outcome.Code.Should().Be("connection_missing");
        (await f.Db.ProjectCloudLinks.SingleAsync()).LastError.Should().Contain("Link the folder again");
    }

    [Fact]
    public async Task A_non_forced_sync_of_a_link_synced_moments_ago_does_nothing()
    {
        (CloudFixture f, Project project, ProjectCloudLink link) = await NewAsync();
        f.Provider.AddFile("a.txt", "a");
        await f.Sync.SyncAsync(link.Id, force: true, CancellationToken.None);

        (await f.Sync.SyncAsync(link.Id, force: false, CancellationToken.None)).Status.Should().Be("skipped");
        f.Clock.Advance(TimeSpan.FromMinutes(2));
        (await f.Sync.SyncAsync(link.Id, force: false, CancellationToken.None)).Status.Should().Be("synced");
    }

    [Fact]
    public async Task Capped_copy_spills_large_files_to_disk_and_refuses_over_the_cap()
    {
        byte[] big = new byte[CloudSyncService.InMemoryBufferLimit + 10];
        big[^1] = 7;
        await using (Stream? copy = await CloudSyncService.CappedCopyAsync(new MemoryStream(big), big.Length, CancellationToken.None))
        {
            copy.Should().NotBeNull();
            copy!.Should().BeOfType<FileStream>();
            copy.Length.Should().Be(big.Length);
            copy.Position.Should().Be(0);
        }

        (await CloudSyncService.CappedCopyAsync(new MemoryStream(big), big.Length - 1, CancellationToken.None)).Should().BeNull();
        await using Stream? empty = await CloudSyncService.CappedCopyAsync(new MemoryStream(), 10, CancellationToken.None);
        empty.Should().NotBeNull();
        empty!.Length.Should().Be(0);
    }

    [Fact]
    public async Task A_sync_does_not_remove_a_row_an_avatar_lists_when_it_vanishes_upstream()
    {
        (CloudFixture f, Project project, ProjectCloudLink link) = await NewAsync();
        f.Provider.AddFile("a.txt", "a", "r1");
        await f.Sync.SyncAsync(link.Id, force: true, CancellationToken.None);
        ProjectFile row = (await f.SyncedFilesAsync(project.Id)).Single();
        ReelBolt.WorkflowEngine.Tests.Avatars.AvatarSeed.Attach(f.Db, project.Id, "Anna", row);
        f.Provider.RemoveFile("a.txt");

        CloudSyncOutcome outcome = await f.Sync.SyncAsync(link.Id, force: true, CancellationToken.None);

        outcome.Skipped.Should().Be(1);
        (await f.SyncedFilesAsync(project.Id)).Should().ContainSingle(x => x.Id == row.Id);
    }
}
