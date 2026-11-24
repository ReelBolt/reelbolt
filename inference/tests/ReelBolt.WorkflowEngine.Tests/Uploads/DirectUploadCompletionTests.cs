#nullable enable
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Services.Uploads;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Uploads;

/// <summary>
/// Finalisation: the row is written from what storage actually holds, the plan's quota is re-checked
/// against that real size (a presigned PUT cannot carry a maximum length), and a refusal discards the
/// object instead of recording it. A retried completion is the same upload, not a second file.
/// </summary>
public class DirectUploadCompletionTests
{
    private static readonly DirectUploadCompletion NoParts = new([]);

    [Fact]
    public async Task Completion_records_the_file_from_the_bytes_that_actually_arrived()
    {
        DirectUploadHarness h = new();
        DirectUploadTicket ticket = await h.Create(sizeBytes: 12, fileName: "notes.txt", contentType: "text/plain");
        h.Storage.Put(ticket.Key, 12);

        ProjectFile row = await h.Service.CompleteAsync(ticket, NoParts, CancellationToken.None);

        row.Id.Should().Be(ticket.FileId);
        row.ProjectId.Should().Be(h.Project.Id);
        row.StorageKey.Should().NotBe(ticket.Key, "the presigned key is a staging key; the row records the relocated copy");
        row.StorageKey.Should().StartWith($"projects/{h.Project.Id}/userFiles/");
        row.StorageBucket.Should().Be(h.Storage.BucketName);
        row.StoragePrefix.Should().Be($"projects/{h.Project.Id}/userFiles/");
        row.SizeBytes.Should().Be(12);
        row.MimeType.Should().Be("text/plain");
        row.Category.Should().Be(DirectUploadService.Category);
        row.OriginalFileName.Should().Be("notes.txt");
        row.OriginalPath.Should().Be("notes.txt");
        row.SummaryStatus.Should().Be(SummaryStatus.Pending, "a text upload waits for the summarizer");
        row.IndexingStatus.Should().Be(FileIndexingStatus.Pending);
        row.UploadedAt.Should().Be(h.Clock.Now.UtcDateTime);
        h.Db.ProjectFiles.Should().ContainSingle();
    }

    [Fact]
    public async Task A_media_upload_is_never_handed_to_the_text_pipeline()
    {
        DirectUploadHarness h = new();
        DirectUploadTicket ticket = await h.Create(sizeBytes: 9, fileName: "clip.mp4", contentType: "video/mp4");
        h.Storage.Put(ticket.Key, 9);

        ProjectFile row = await h.Service.CompleteAsync(ticket, NoParts, CancellationToken.None);

        row.SummaryStatus.Should().Be(SummaryStatus.Done, "R22: a raw source video never reaches the summarizer");
        row.IndexingStatus.Should().Be(FileIndexingStatus.NotIndexed);
    }

    [Theory]
    [InlineData("video/vnd.dlna.mpeg-tts")]
    [InlineData("video/mp2t")]
    [InlineData("application/octet-stream")]
    public async Task A_typescript_source_the_browser_typed_as_video_is_recorded_as_text(string declared)
    {
        // Issue 8: Windows browsers and FileExtensionContentTypeProvider call every .ts a transport stream.
        DirectUploadHarness h = new();
        byte[] bytes = TransportStreamMimeSnifferTests.TypeScript();
        DirectUploadTicket ticket = await h.Create(sizeBytes: bytes.Length, fileName: "proxy.ts", contentType: declared);
        h.Storage.Put(ticket.Key, bytes);

        ProjectFile row = await h.Service.CompleteAsync(ticket, NoParts, CancellationToken.None);

        row.MimeType.Should().Be("text/plain");
        row.SummaryStatus.Should().Be(SummaryStatus.Pending, "a text upload waits for the summarizer, not the media path");
        row.IndexingStatus.Should().Be(FileIndexingStatus.Pending);
    }

    [Fact]
    public async Task A_real_transport_stream_named_ts_stays_video()
    {
        DirectUploadHarness h = new();
        byte[] bytes = TransportStreamMimeSnifferTests.MpegTs();
        DirectUploadTicket ticket = await h.Create(sizeBytes: bytes.Length, fileName: "broadcast.ts", contentType: "video/vnd.dlna.mpeg-tts");
        h.Storage.Put(ticket.Key, bytes);

        ProjectFile row = await h.Service.CompleteAsync(ticket, NoParts, CancellationToken.None);

        row.MimeType.Should().Be("video/vnd.dlna.mpeg-tts");
        row.SummaryStatus.Should().Be(SummaryStatus.Done);
    }

    [Fact]
    public async Task An_upload_outside_the_ts_family_never_reads_its_bytes()
    {
        DirectUploadHarness h = new();
        DirectUploadTicket ticket = await h.Create(sizeBytes: 9, fileName: "clip.mp4", contentType: "video/mp4");
        h.Storage.Put(ticket.Key, 9);

        ProjectFile row = await h.Service.CompleteAsync(ticket, NoParts, CancellationToken.None);

        row.MimeType.Should().Be("video/mp4");
        h.Storage.Downloads.Should().Be(0);
    }

    [Fact]
    public async Task A_head_that_cannot_be_read_keeps_the_declared_type()
    {
        DirectUploadHarness h = new();
        DirectUploadTicket ticket = await h.Create(sizeBytes: 9, fileName: "proxy.ts", contentType: "video/mp2t");
        h.Storage.Put(ticket.Key, 9); // no bytes: the download throws

        ProjectFile row = await h.Service.CompleteAsync(ticket, NoParts, CancellationToken.None);

        row.MimeType.Should().Be("video/mp2t", "a type hint never fails an upload whose bytes are stored");
    }

    [Fact]
    public async Task A_synced_file_is_not_summarized_either()
    {
        DirectUploadHarness h = new();
        DirectUploadTicket ticket = await h.Create(h.Request(
            sizeBytes: 12, fileName: "notes.txt", contentType: "text/plain", sourceKind: "github", sourceRevision: "abc"));
        h.Storage.Put(ticket.Key, 12);

        ProjectFile row = await h.Service.CompleteAsync(ticket, NoParts, CancellationToken.None);

        row.SourceKind.Should().Be("github");
        row.SourceRevision.Should().Be("abc");
        row.SummaryStatus.Should().Be(SummaryStatus.Done);
        row.IndexingStatus.Should().Be(FileIndexingStatus.NotIndexed);
    }

    [Fact]
    public async Task A_completion_with_no_object_behind_it_records_nothing()
    {
        DirectUploadHarness h = new();
        DirectUploadTicket ticket = await h.Create();

        Func<Task> act = () => h.Service.CompleteAsync(ticket, NoParts, CancellationToken.None);

        DirectUploadRefusedException refusal = (await act.Should().ThrowAsync<DirectUploadRefusedException>()).Which;
        refusal.Code.Should().Be("UPLOAD_OBJECT_MISSING");
        refusal.StatusCode.Should().Be(409);
        h.Db.ProjectFiles.Should().BeEmpty();
        h.Storage.DeletedKeys.Should().BeEmpty("there was nothing to delete");
    }

    [Theory]
    [InlineData(37L, "storage holds LESS than the client claimed")]
    [InlineData(5000L, "storage holds MORE than the client claimed — the claim was understated")]
    public async Task A_size_that_is_not_what_was_claimed_is_discarded_rather_than_recorded(long storedBytes, string because)
    {
        DirectUploadHarness h = new();
        DirectUploadTicket ticket = await h.Create(sizeBytes: 100);
        h.Storage.Put(ticket.Key, storedBytes);

        Func<Task> act = () => h.Service.CompleteAsync(ticket, NoParts, CancellationToken.None);

        DirectUploadRefusedException refusal = (await act.Should().ThrowAsync<DirectUploadRefusedException>()).Which;
        refusal.Code.Should().Be("UPLOAD_SIZE_MISMATCH", because);
        refusal.StatusCode.Should().Be(409);
        h.Storage.DeletedKeys.Should().Contain(ticket.Key).And.Contain(h.Storage.Moves.Single().To,
            "both the staging key and the relocated copy are discarded");
        h.Storage.Sizes.Should().BeEmpty();
        h.Db.ProjectFiles.Should().BeEmpty();
    }

    [Fact]
    public async Task The_quota_is_re_checked_against_the_stored_bytes_and_a_refusal_discards_them()
    {
        DirectUploadHarness h = new();
        DirectUploadTicket ticket = await h.Create(sizeBytes: 4096);
        h.Storage.Put(ticket.Key, 4096);
        h.PlanGate.Violations.Enqueue(DirectUploadHarness.OverQuota());

        Func<Task> act = () => h.Service.CompleteAsync(ticket, NoParts, CancellationToken.None);

        DirectUploadRefusedException refusal = (await act.Should().ThrowAsync<DirectUploadRefusedException>()).Which;
        refusal.Code.Should().Be(PlanGate.StorageCode);
        refusal.StatusCode.Should().Be(413);
        h.PlanGate.UploadChecks.Should().Equal([4096L, 4096L],
            "the claim is checked before the URL is issued and the real size again after the bytes land");
        h.Storage.DeletedKeys.Should().Contain(ticket.Key).And.Contain(h.Storage.Moves.Single().To,
            "both the staging key and the relocated copy are discarded");
        h.Storage.Sizes.Should().BeEmpty();
        h.Db.ProjectFiles.Should().BeEmpty();
    }

    [Fact]
    public async Task A_repeated_completion_returns_the_first_row_without_reading_storage_again()
    {
        DirectUploadHarness h = new();
        DirectUploadTicket ticket = await h.Create(sizeBytes: 3, fileName: "notes.txt", contentType: "text/plain");
        h.Storage.Put(ticket.Key, 3);

        ProjectFile first = await h.Service.CompleteAsync(ticket, NoParts, CancellationToken.None);
        ProjectFile second = await h.Service.CompleteAsync(ticket, NoParts, CancellationToken.None);

        second.Id.Should().Be(first.Id, "a double submit is the same upload, not a second file");
        h.Db.ProjectFiles.Should().ContainSingle();
        h.Storage.MetadataReads.Should().Be(2,
            "the first call HEADs the staged object and then the relocated copy; the retry is answered from the row without reading storage");
    }

    [Fact]
    public async Task Replacing_a_file_rewrites_the_same_row_and_retires_the_old_object()
    {
        DirectUploadHarness h = new();
        ProjectFile replaced = h.SeedFile(sizeBytes: 100, storageKey: "projects/x/userFiles/old-object.mp4");
        string oldKey = replaced.StorageKey;
        h.Storage.Put(oldKey, 100);

        DirectUploadTicket ticket = await h.Create(h.Request(sizeBytes: 110, fileName: "old.mp4", replacesFileId: replaced.Id));
        h.Storage.Put(ticket.Key, 110);

        ProjectFile row = await h.Service.CompleteAsync(ticket, NoParts, CancellationToken.None);

        row.Id.Should().Be(replaced.Id, "the row keeps its id and is pointed at the new object");
        row.StorageKey.Should().NotBe(ticket.Key, "the staged object is relocated to a never-presigned key");
        row.SizeBytes.Should().Be(110);
        h.Db.ProjectFiles.Should().ContainSingle();
        h.Storage.DeletedKeys.Should().Equal([oldKey], "only after the new bytes are verified does the old object go");
        h.Storage.Sizes.Should().ContainKey(row.StorageKey);
    }

    /// <summary>
    /// S-80: the replacement's row is written and the request is cancelled between the save and the delete of the
    /// object it replaced. The delete ran on the request token, so the cancellation reached it before S3 did and the
    /// replaced bytes stayed in storage outside the quota while no row pointed at them. It now runs on a token of its
    /// own, bounded like the relocation cleanups (S-40/S-46), and the answer is the written row.
    /// </summary>
    [Fact]
    public async Task A_replacement_cancelled_after_the_save_still_retires_the_object_it_replaced()
    {
        DirectUploadHarness h = new();
        ProjectFile replaced = h.SeedFile(sizeBytes: 100, storageKey: "projects/x/userFiles/old-object.mp4");
        string oldKey = replaced.StorageKey;
        h.Storage.Put(oldKey, 100);

        DirectUploadTicket ticket = await h.Create(h.Request(sizeBytes: 110, fileName: "old.mp4", replacesFileId: replaced.Id));
        h.Storage.Put(ticket.Key, 110);
        using CancellationTokenSource cts = new();
        h.SaveCanceller.CancelAfterSaveQuietly = cts;

        ProjectFile row = await h.Service.CompleteAsync(ticket, NoParts, cts.Token);

        row.StorageKey.Should().NotBe(ticket.Key, "the replacement is relocated off the presigned key");
        h.Db.ProjectFiles.Single().StorageKey.Should().Be(row.StorageKey, "the write landed before the request died");
        h.Storage.DeletedKeys.Should().Contain(oldKey, "the replaced bytes are unreachable, so they go on a bounded token");
        h.Storage.Sizes.Should().NotContainKey(oldKey);
    }

    /// <summary>
    /// S-80: the write landed but the save threw the cancellation (the acknowledgement was lost). Nothing was left to
    /// delete the replaced object either, so the same bounded, no-row-checked cleanup runs on the way out — and it
    /// keeps the object when the row still points at it (the next test).
    /// </summary>
    [Fact]
    public async Task A_replacement_whose_save_threw_the_cancellation_still_retires_the_object_it_replaced()
    {
        DirectUploadHarness h = new();
        ProjectFile replaced = h.SeedFile(sizeBytes: 100, storageKey: "projects/x/userFiles/old-object.mp4");
        string oldKey = replaced.StorageKey;
        h.Storage.Put(oldKey, 100);

        DirectUploadTicket ticket = await h.Create(h.Request(sizeBytes: 110, fileName: "old.mp4", replacesFileId: replaced.Id));
        h.Storage.Put(ticket.Key, 110);
        using CancellationTokenSource cts = new();
        h.SaveCanceller.CancelAfterSave = cts;

        Func<Task> complete = () => h.Service.CompleteAsync(ticket, NoParts, cts.Token);

        await complete.Should().ThrowAsync<OperationCanceledException>();
        h.Db.ProjectFiles.Single().StorageKey.Should().Be(h.Storage.Moves.Single().To, "the row was written before the cancellation surfaced");
        h.Storage.DeletedKeys.Should().Contain(oldKey);
    }

    /// <summary>
    /// S-80's other half: a cancellation BEFORE the row is written must leave the replaced file exactly as it was.
    /// The cleanup is guarded by the same no-row check as every other discard, so a file the project still points at
    /// is never deleted by a request that died too early.
    /// </summary>
    [Fact]
    public async Task A_replacement_cancelled_before_the_save_keeps_the_file_it_meant_to_replace()
    {
        DirectUploadHarness h = new();
        ProjectFile replaced = h.SeedFile(sizeBytes: 100, storageKey: "projects/x/userFiles/old-object.mp4");
        string oldKey = replaced.StorageKey;
        h.Storage.Put(oldKey, 100);

        DirectUploadTicket ticket = await h.Create(h.Request(sizeBytes: 110, fileName: "old.mp4", replacesFileId: replaced.Id));
        h.Storage.Put(ticket.Key, 110);
        using CancellationTokenSource cts = new();
        h.Storage.HeadFaultsAfterMove = true; h.Storage.CancelDuringHead = cts;

        Func<Task> complete = () => h.Service.CompleteAsync(ticket, NoParts, cts.Token);

        await complete.Should().ThrowAsync<OperationCanceledException>();
        h.Db.ProjectFiles.Single().StorageKey.Should().Be(oldKey, "nothing was written, so the file is untouched");
        h.Storage.Sizes.Should().ContainKey(oldKey);
        h.Storage.DeletedKeys.Should().NotContain(oldKey);
    }

    [Fact]
    public async Task An_abandoned_replacement_never_destroys_the_file_it_meant_to_replace()
    {
        DirectUploadHarness h = new();
        ProjectFile replaced = h.SeedFile(sizeBytes: 100, storageKey: "projects/x/userFiles/old-object.mp4");
        h.Storage.Put(replaced.StorageKey, 100);

        await h.Create(h.Request(sizeBytes: 110, fileName: "old.mp4", replacesFileId: replaced.Id));

        h.Storage.DeletedKeys.Should().BeEmpty();
        h.Storage.Sizes.Should().ContainKey(replaced.StorageKey);
        h.Db.ProjectFiles.Single().StorageKey.Should().Be(replaced.StorageKey);
    }

    [Fact]
    public async Task A_replay_of_a_relocated_row_keeps_its_csam_verdict()
    {
        DirectUploadHarness h = new();
        DirectUploadTicket ticket = await h.Create(sizeBytes: 12, fileName: "notes.txt", contentType: "text/plain");
        h.Storage.Put(ticket.Key, 12);
        ProjectFile first = await h.Service.CompleteAsync(ticket, NoParts, CancellationToken.None);
        first.StorageKey.Should().NotBe(ticket.Key);
        first.CsamStatus = CsamScanStatus.Clean;
        first.CsamScannedAt = DateTime.UtcNow;
        await h.Db.SaveChangesAsync();

        // The presigned URL is replayed with different bytes; they land at the staging key, never at the row's key.
        h.Storage.Put(ticket.Key, 999);
        ProjectFile replay = await h.Service.CompleteAsync(ticket, NoParts, CancellationToken.None);

        replay.CsamStatus.Should().Be(CsamScanStatus.Clean,
            "the recorded copy was never presigned, so its bytes cannot have changed");
        replay.CsamScannedAt.Should().NotBeNull();
        h.Storage.Sizes.Should().NotContainKey(ticket.Key);
    }

    [Fact]
    public async Task A_replay_of_a_legacy_row_recorded_at_the_presigned_key_resets_the_csam_verdict()
    {
        DirectUploadHarness h = new();
        DirectUploadTicket ticket = await h.Create(sizeBytes: 12, fileName: "notes.txt", contentType: "text/plain");
        h.Storage.Put(ticket.Key, 12);
        ProjectFile first = await h.Service.CompleteAsync(ticket, NoParts, CancellationToken.None);

        // A row written before staging existed sits AT the presigned key, whose bytes a live URL can replace.
        first.StorageKey = ticket.Key;
        first.CsamStatus = CsamScanStatus.Clean;
        first.CsamScannedAt = DateTime.UtcNow;
        await h.Db.SaveChangesAsync();

        ProjectFile replay = await h.Service.CompleteAsync(ticket, NoParts, CancellationToken.None);

        replay.CsamStatus.Should().Be(CsamScanStatus.Pending,
            "the bytes at the presigned key may be new, so the stored Clean verdict cannot apply");
        replay.CsamScannedAt.Should().BeNull();
    }

    [Fact]
    public async Task A_replacement_of_a_quarantined_file_is_refused_and_keeps_the_quarantine_object()
    {
        DirectUploadHarness h = new();
        ProjectFile old = h.SeedFile(sizeBytes: 10, storageKey: "projects/placeholder/old.mp4");
        string quarantineKey = $"projects/{h.Project.Id}/quarantine/{old.Id}/old.mp4";
        old.StorageKey = quarantineKey;
        old.CsamQuarantineKey = quarantineKey;
        old.CsamStatus = CsamScanStatus.Quarantined;
        await h.Db.SaveChangesAsync();
        h.Storage.Put(quarantineKey, 10);

        DirectUploadTicket ticket = await h.Create(h.Request(sizeBytes: 12, replacesFileId: old.Id));
        h.Storage.Put(ticket.Key, 12);

        Func<Task> act = () => h.Service.CompleteAsync(ticket, NoParts, CancellationToken.None);

        DirectUploadRefusedException refused = (await act.Should().ThrowAsync<DirectUploadRefusedException>()).Which;
        refused.Code.Should().Be("FILE_UNAVAILABLE");
        h.Storage.Sizes.Should().ContainKey(quarantineKey, "the quarantine object is evidence");
        h.Storage.DeletedKeys.Should().NotContain(quarantineKey);
        h.Db.ProjectFiles.Single(f => f.Id == old.Id).StorageKey.Should().Be(quarantineKey);
        h.Db.ProjectFiles.Single(f => f.Id == old.Id).CsamStatus.Should().Be(CsamScanStatus.Quarantined);
    }

    [Fact]
    public async Task A_replacement_whose_row_vanished_is_refused_and_its_object_discarded()
    {
        DirectUploadHarness h = new();
        ProjectFile replaced = h.SeedFile();
        DirectUploadTicket ticket = await h.Create(h.Request(sizeBytes: 110, replacesFileId: replaced.Id));
        h.Storage.Put(ticket.Key, 110);
        h.Db.ProjectFiles.Remove(replaced);
        h.Db.SaveChanges();

        Func<Task> act = () => h.Service.CompleteAsync(ticket, NoParts, CancellationToken.None);

        DirectUploadRefusedException refusal = (await act.Should().ThrowAsync<DirectUploadRefusedException>()).Which;
        refusal.Code.Should().Be("REPLACED_FILE_NOT_FOUND");
        refusal.StatusCode.Should().Be(404);
        h.Storage.DeletedKeys.Should().ContainSingle().Which.Should().Be(ticket.Key);
    }

    [Fact]
    public async Task A_project_that_vanished_is_a_not_found()
    {
        DirectUploadHarness h = new();
        DirectUploadTicket ticket = await h.Create();
        h.Storage.Put(ticket.Key, 4);
        h.Db.Projects.Remove(h.Project);
        h.Db.SaveChanges();

        Func<Task> act = () => h.Service.CompleteAsync(ticket, NoParts, CancellationToken.None);

        (await act.Should().ThrowAsync<DirectUploadRefusedException>()).Which.Code.Should().Be("PROJECT_NOT_FOUND");
    }

    [Fact]
    public async Task Completion_never_touches_a_key_the_ticket_did_not_name()
    {
        DirectUploadHarness h = new();
        DirectUploadTicket ticket = await h.Create();
        string attackerKey = $"projects/{h.Project.Id}/userFiles/{Guid.NewGuid()}.mp4";
        h.Storage.Put(attackerKey, 4);

        // The ticket's own object was never stored; the attacker's is. Finalisation must report the
        // missing object rather than crediting the unrelated one.
        Func<Task> act = () => h.Service.CompleteAsync(ticket, NoParts, CancellationToken.None);

        (await act.Should().ThrowAsync<DirectUploadRefusedException>()).Which.Code.Should().Be("UPLOAD_OBJECT_MISSING");
        h.Storage.DeletedKeys.Should().BeEmpty();
        h.Db.ProjectFiles.Should().BeEmpty();
    }
}
