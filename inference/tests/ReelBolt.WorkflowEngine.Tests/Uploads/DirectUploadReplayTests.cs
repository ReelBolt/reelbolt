#nullable enable
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Services.Uploads;
using ReelBolt.Shared.Data.Models;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Uploads;

/// <summary>
/// upload-replay-fix: a single-PUT URL names a STAGING key. Completion pins the staged object's ETag, moves it with a
/// conditional copy to a fresh never-presigned key and records that copy, so a replay of the (still live) URL can
/// neither change a recorded file nor skip the size, quota, moderation and CSAM checks.
/// </summary>
public class DirectUploadReplayTests
{
    private static readonly DirectUploadCompletion NoParts = new([]);

    private static Task<ProjectFile> Complete(DirectUploadHarness h, DirectUploadTicket ticket) =>
        h.Service.CompleteAsync(ticket, NoParts, CancellationToken.None);

    [Fact]
    public async Task A_browser_single_PUT_is_recorded_at_a_fresh_key_and_a_replay_cannot_change_it()
    {
        DirectUploadHarness h = new();
        DirectUploadTicket ticket = await h.Create();
        ticket.RelocateOnComplete.Should().BeFalse("this is a plain browser ticket");
        h.Storage.Put(ticket.Key, 4);

        ProjectFile first = await Complete(h, ticket);
        string recordedKey = first.StorageKey;
        recordedKey.Should().NotBe(ticket.Key, "the presigned key is only ever a staging key");
        h.Presign.BrowserPuts.Should().NotContain(p => p.Key == recordedKey);
        first.StorageMetadataJson.Should().Contain("upload-object-id").And.Contain(ticket.FileId.ToString());

        // Moderation and CSAM have judged the row; the client then replays the live URL with other bytes.
        first.ModerationStatus = FileModerationStatus.Clean;
        first.CsamStatus = CsamScanStatus.Clean;
        h.Db.SaveChanges();
        h.Storage.Put(ticket.Key, 999);

        ProjectFile second = await Complete(h, ticket);

        second.Id.Should().Be(first.Id);
        second.StorageKey.Should().Be(recordedKey);
        second.SizeBytes.Should().Be(4);
        second.ModerationStatus.Should().Be(FileModerationStatus.Clean, "a relocated row's bytes cannot change, so its verdict stands");
        second.CsamStatus.Should().Be(CsamScanStatus.Clean);
        h.Db.ProjectFiles.Should().ContainSingle();
        h.Storage.Sizes[recordedKey].Should().Be(4);
        h.Storage.Sizes.Should().NotContainKey(ticket.Key, "the stray replay object is deleted");
        h.Storage.DeletedKeys.Should().Contain(ticket.Key);
    }

    [Fact]
    public async Task A_staged_object_that_changes_between_the_HEAD_and_the_copy_is_refused_and_both_keys_are_deleted()
    {
        DirectUploadHarness h = new();
        DirectUploadTicket ticket = await h.Create();
        h.Storage.Put(ticket.Key, 4);
        bool swapped = false;
        h.Storage.AfterHead = key =>
        {
            if (swapped || key != ticket.Key) return;
            swapped = true;
            h.Storage.Put(ticket.Key, 4); // same size, new ETag: the bytes were replaced after the HEAD
        };

        Func<Task> act = () => Complete(h, ticket);

        DirectUploadRefusedException refused = (await act.Should().ThrowAsync<DirectUploadRefusedException>()).Which;
        refused.Code.Should().Be("UPLOAD_CHANGED");
        refused.StatusCode.Should().Be(409);
        h.Db.ProjectFiles.Should().BeEmpty();
        h.Storage.Moves.Should().BeEmpty();
        h.Storage.Sizes.Should().BeEmpty("neither the staged object nor a copy may stay in storage");
        h.Storage.DeletedKeys.Should().Contain(ticket.Key);
        h.Storage.ConditionalMoves.Should().ContainSingle().Which.Moved.Should().BeFalse();
        h.Storage.DeletedKeys.Should().HaveCount(2, "the fresh key is discarded too");
    }

    [Theory]
    [InlineData("cannot-condition")]
    [InlineData("no-etag")]
    public async Task A_store_that_cannot_pin_or_condition_the_copy_never_vouches_for_the_bytes(string mode)
    {
        DirectUploadHarness h = new();
        DirectUploadTicket ticket = await h.Create();
        h.Storage.Put(ticket.Key, 4);
        if (mode == "no-etag") h.Storage.OmitETag = true;
        else h.Storage.CanConditionCopy = false;

        Func<Task> act = () => Complete(h, ticket);

        (await act.Should().ThrowAsync<DirectUploadRefusedException>()).Which.Code.Should().Be("UPLOAD_CHANGED");
        h.Db.ProjectFiles.Should().BeEmpty();
        h.Storage.Moves.Should().BeEmpty();
        h.Storage.Sizes.Should().BeEmpty();
        h.Storage.DeletedKeys.Should().Contain(ticket.Key);
    }

    [Fact]
    public async Task A_default_interface_store_that_does_not_override_the_conditional_move_refuses()
    {
        // MoveIfMatchAsync's default answers false: nothing is recorded for a store that cannot condition the copy.
        DirectUploadHarness h = new();
        DirectUploadTicket ticket = await h.Create();
        h.Storage.Put(ticket.Key, 4);
        h.Storage.CanConditionCopy = false;

        Func<Task> act = () => Complete(h, ticket);

        (await act.Should().ThrowAsync<DirectUploadRefusedException>()).Which.Code.Should().Be("UPLOAD_CHANGED");
        h.Db.ProjectFiles.Should().BeEmpty();
    }

    [Fact]
    public async Task Size_is_judged_on_the_relocated_copy()
    {
        DirectUploadHarness h = new();
        DirectUploadTicket ticket = await h.Create(sizeBytes: 100);
        h.Storage.Put(ticket.Key, 37);

        Func<Task> act = () => Complete(h, ticket);

        (await act.Should().ThrowAsync<DirectUploadRefusedException>()).Which.Code.Should().Be("UPLOAD_SIZE_MISMATCH");
        h.Storage.Moves.Should().ContainSingle("the copy is what was measured");
        h.Storage.Sizes.Should().BeEmpty("the moved copy is deleted with the refusal");
        h.Db.ProjectFiles.Should().BeEmpty();
    }

    [Fact]
    public async Task A_quota_refusal_deletes_the_moved_object_and_records_nothing()
    {
        DirectUploadHarness h = new();
        DirectUploadTicket ticket = await h.Create();
        h.Storage.Put(ticket.Key, 4);
        h.PlanGate.Violations.Enqueue(DirectUploadHarness.OverQuota());

        Func<Task> act = () => Complete(h, ticket);

        (await act.Should().ThrowAsync<DirectUploadRefusedException>()).Which.Code.Should().Be("STORAGE_QUOTA_EXCEEDED");
        string moved = h.Storage.Moves.Single().To;
        h.Storage.DeletedKeys.Should().Contain(moved);
        h.Storage.Sizes.Should().BeEmpty();
        h.Db.ProjectFiles.Should().BeEmpty();
    }

    [Fact]
    public async Task A_folder_sync_replacement_repoints_the_row_and_a_retry_is_idempotent()
    {
        DirectUploadHarness h = new();
        ProjectFile replaced = h.SeedFile(sizeBytes: 100, storageKey: "projects/x/userFiles/old-object.mp4");
        string oldKey = replaced.StorageKey;
        h.Storage.Put(oldKey, 100);
        DirectUploadTicket ticket = await h.Create(h.Request(
            sizeBytes: 110, fileName: "old.mp4", replacesFileId: replaced.Id, sourceKind: "local_folder"));
        h.Storage.Put(ticket.Key, 110);

        ProjectFile first = await Complete(h, ticket);

        first.Id.Should().Be(replaced.Id);
        first.StorageKey.Should().NotBe(ticket.Key).And.NotBe(oldKey);
        first.SizeBytes.Should().Be(110);
        h.Storage.DeletedKeys.Should().Contain(oldKey, "the replaced object is retired once the new bytes are verified");
        h.Storage.Sizes.Should().NotContainKey(oldKey);
        int moves = h.Storage.Moves.Count;
        int conditional = h.Storage.ConditionalMoves.Count;

        ProjectFile retry = await Complete(h, ticket);

        retry.Id.Should().Be(first.Id);
        retry.StorageKey.Should().Be(first.StorageKey);
        h.Storage.Moves.Should().HaveCount(moves, "a retried completion moves nothing");
        h.Storage.ConditionalMoves.Should().HaveCount(conditional);
        h.Db.ProjectFiles.Should().ContainSingle();
    }

    [Fact]
    public async Task A_second_replacement_ticket_whose_PUT_never_happened_is_refused_and_the_row_is_unchanged()
    {
        DirectUploadHarness h = new();
        ProjectFile replaced = h.SeedFile(sizeBytes: 100, storageKey: "projects/x/userFiles/old-object.mp4");
        h.Storage.Put(replaced.StorageKey, 100);
        DirectUploadTicket first = await h.Create(h.Request(
            sizeBytes: 110, fileName: "old.mp4", replacesFileId: replaced.Id, sourceKind: "local_folder"));
        h.Storage.Put(first.Key, 110);
        ProjectFile done = await Complete(h, first);
        string recordedKey = done.StorageKey;

        DirectUploadTicket other = await h.Create(h.Request(
            sizeBytes: 50, fileName: "old.mp4", replacesFileId: replaced.Id, sourceKind: "local_folder"));
        Func<Task> act = () => Complete(h, other);

        (await act.Should().ThrowAsync<DirectUploadRefusedException>()).Which.Code.Should().Be("UPLOAD_OBJECT_MISSING");
        ProjectFile row = h.Db.ProjectFiles.Single();
        row.StorageKey.Should().Be(recordedKey);
        row.SizeBytes.Should().Be(110);
        h.Storage.Sizes.Should().ContainKey(recordedKey);
    }

    [Fact]
    public async Task A_non_assistant_multipart_upload_is_still_recorded_at_the_ticket_key()
    {
        DirectUploadHarness h = new();
        const long size = 300L * 1024 * 1024;
        DirectUploadTicket ticket = await h.Create(sizeBytes: size, fileName: "master.mov", contentType: "video/quicktime");
        ticket.Mode.Should().Be(DirectUploadMode.Multipart);
        h.Storage.Put(ticket.Key, size);
        DirectUploadCompletion parts = new(Enumerable.Range(1, ticket.PartCount)
            .Select(n => new DirectUploadCompletedPart(n, $"etag-{n}")).ToList());

        ProjectFile row = await h.Service.CompleteAsync(ticket, parts, CancellationToken.None);

        row.StorageKey.Should().Be(ticket.Key, "a multipart upload's part URLs die with the upload id, so it needs no relocation");
        h.Storage.Moves.Should().BeEmpty();
        h.Storage.ConditionalMoves.Should().BeEmpty();
    }

    [Fact]
    public async Task The_single_PUT_url_lives_one_hour_and_is_minted_with_it()
    {
        DirectUploadService.SinglePutUrlTtl.Should().Be(TimeSpan.FromHours(1));
        DirectUploadHarness h = new();

        DirectUploadTicket ticket = await h.Create();

        h.Presign.BrowserPuts.Single().Ttl.Should().Be(TimeSpan.FromHours(1));
        DateTimeOffset.FromUnixTimeSeconds(ticket.ExpiresAtUnix).Should()
            .BeOnOrBefore(h.Clock.Now + TimeSpan.FromHours(1) + DirectUploadService.CompletionGrace);
    }
}
