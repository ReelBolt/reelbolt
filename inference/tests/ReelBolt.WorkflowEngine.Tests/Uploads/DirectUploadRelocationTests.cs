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
/// S-05: an assistant ticket (<see cref="DirectUploadRequest.RelocateOnComplete"/>) has a short-lived
/// PUT URL, and its completion MOVES the object to a fresh key that was never presigned before the row
/// is written. A re-PUT to the old URL after completion therefore cannot change the recorded file, and
/// a second completion returns the same row (never a second one).
/// </summary>
public class DirectUploadRelocationTests
{
    private const long MiB = 1024 * 1024;
    private static readonly DirectUploadCompletion NoParts = new([]);

    private static Task<DirectUploadTicket> Relocating(DirectUploadHarness h, long sizeBytes = 4, string fileName = "clip.mp4") =>
        h.Create(h.Request(sizeBytes, fileName) with { RelocateOnComplete = true });

    [Fact]
    public async Task A_relocating_single_PUT_url_lives_at_most_15_minutes()
    {
        DirectUploadHarness h = new();

        DirectUploadTicket ticket = await Relocating(h);

        ticket.RelocateOnComplete.Should().BeTrue();
        h.Presign.BrowserPuts.Single().Ttl.Should().BeLessThanOrEqualTo(TimeSpan.FromMinutes(15));
        DateTimeOffset.FromUnixTimeSeconds(ticket.ExpiresAtUnix).Should()
            .BeOnOrBefore(h.Clock.Now + TimeSpan.FromMinutes(15) + DirectUploadService.CompletionGrace);
    }

    [Fact]
    public async Task A_relocating_multipart_ticket_presigns_parts_for_at_most_30_minutes()
    {
        DirectUploadHarness h = new();
        DirectUploadTicket ticket = await Relocating(h, 300 * MiB, "master.mov");

        await h.Service.PresignPartsAsync(ticket, Enumerable.Range(1, ticket.PartCount).ToList(), CancellationToken.None);

        h.Multipart.Presigned.Should().OnlyContain(p => p.Ttl <= TimeSpan.FromMinutes(30));
    }

    [Fact]
    public async Task A_browser_ticket_gets_the_one_hour_url()
    {
        DirectUploadHarness h = new();

        DirectUploadTicket ticket = await h.Create();

        ticket.RelocateOnComplete.Should().BeFalse();
        h.Presign.BrowserPuts.Single().Ttl.Should().Be(DirectUploadService.SinglePutUrlTtl);
    }

    [Fact]
    public async Task Completion_moves_the_object_to_a_fresh_key_before_recording_it()
    {
        DirectUploadHarness h = new();
        DirectUploadTicket ticket = await Relocating(h);
        h.Storage.Put(ticket.Key, 4);

        ProjectFile row = await h.Service.CompleteAsync(ticket, NoParts, CancellationToken.None);

        row.Id.Should().Be(ticket.FileId);
        row.StorageKey.Should().NotBe(ticket.Key, "the presigned key is never the recorded one");
        row.StorageKey.Should().StartWith($"projects/{h.Project.Id}/userFiles/");
        row.StorageKey.Should().EndWith(".mp4");
        row.StorageFileName.Should().Be(row.StorageKey[(row.StorageKey.LastIndexOf('/') + 1)..]);
        h.Storage.Sizes.Should().ContainKey(row.StorageKey).WhoseValue.Should().Be(4);
        h.Storage.Sizes.Should().NotContainKey(ticket.Key);
        h.Presign.BrowserPuts.Should().NotContain(p => p.Key == row.StorageKey);
    }

    [Fact]
    public async Task A_re_PUT_after_completion_cannot_change_the_file_and_a_second_completion_returns_the_same_row()
    {
        DirectUploadHarness h = new();
        DirectUploadTicket ticket = await Relocating(h);
        h.Storage.Put(ticket.Key, 4);
        ProjectFile first = await h.Service.CompleteAsync(ticket, NoParts, CancellationToken.None);
        string recordedKey = first.StorageKey;

        // The client keeps the (still live) URL and PUTs 999 bytes to it.
        h.Storage.Put(ticket.Key, 999);
        ProjectFile second = await h.Service.CompleteAsync(ticket, NoParts, CancellationToken.None);

        second.Id.Should().Be(first.Id);
        second.StorageKey.Should().Be(recordedKey);
        second.SizeBytes.Should().Be(4);
        h.Db.ProjectFiles.Should().ContainSingle();
        h.Storage.Sizes[recordedKey].Should().Be(4, "the recorded object was never presigned, so the re-PUT did not touch it");
        h.Storage.Sizes.Should().NotContainKey(ticket.Key, "a replayed completion removes the stray re-PUT object");
    }

    [Fact]
    public async Task A_quota_refusal_after_the_move_deletes_the_moved_object_and_records_nothing()
    {
        DirectUploadHarness h = new();
        DirectUploadTicket ticket = await Relocating(h);
        h.Storage.Put(ticket.Key, 4);
        h.PlanGate.Violations.Enqueue(DirectUploadHarness.OverQuota());

        Func<Task> complete = () => h.Service.CompleteAsync(ticket, NoParts, CancellationToken.None);

        (await complete.Should().ThrowAsync<DirectUploadRefusedException>()).Which.Code.Should().Be("STORAGE_QUOTA_EXCEEDED");
        h.Db.ProjectFiles.Should().BeEmpty();
        h.Storage.Sizes.Should().BeEmpty();
    }

    /// <summary>S-31: a relocation that fails before the copy (e.g. above S3's 5 GiB CopyObject ceiling) deletes the
    /// ticket-key object, so nothing is left in storage outside the quota.</summary>
    [Fact]
    public async Task A_failed_relocation_deletes_the_ticket_key_object_and_records_nothing()
    {
        DirectUploadHarness h = new();
        DirectUploadTicket ticket = await Relocating(h);
        h.Storage.Put(ticket.Key, 4);
        h.Storage.MoveFailure = new InvalidOperationException("The specified copy source is larger than the maximum allowable size.");

        Func<Task> complete = () => h.Service.CompleteAsync(ticket, NoParts, CancellationToken.None);

        await complete.Should().ThrowAsync<DirectUploadRefusedException>();
        h.Db.ProjectFiles.Should().BeEmpty();
        h.Storage.Sizes.Should().BeEmpty("the unrecorded upload must not stay in storage outside the quota");
        h.Storage.DeletedKeys.Should().Contain(ticket.Key);
    }

    /// <summary>S-31: copy succeeded, source delete failed: both the old-key object and the copy are removed.</summary>
    [Fact]
    public async Task A_relocation_whose_source_delete_failed_removes_both_objects_and_records_nothing()
    {
        DirectUploadHarness h = new();
        DirectUploadTicket ticket = await Relocating(h);
        h.Storage.Put(ticket.Key, 4);
        h.Storage.MoveDeleteFails = true;

        Func<Task> complete = () => h.Service.CompleteAsync(ticket, NoParts, CancellationToken.None);

        await complete.Should().ThrowAsync<DirectUploadRefusedException>();
        h.Db.ProjectFiles.Should().BeEmpty();
        h.Storage.Sizes.Should().BeEmpty("neither the ticket-key object nor the copy may be left behind");
    }

    /// <summary>S-40: the request is cancelled mid-relocation (copy landed, source not yet deleted). The cancellation
    /// still surfaces, and both the ticket-key object and the fresh-key copy are removed on a non-request token.</summary>
    [Fact]
    public async Task A_cancelled_relocation_still_removes_both_objects_and_records_nothing()
    {
        DirectUploadHarness h = new();
        DirectUploadTicket ticket = await Relocating(h);
        h.Storage.Put(ticket.Key, 4);
        using CancellationTokenSource cts = new();
        h.Storage.CancelDuringMove = cts;

        Func<Task> complete = () => h.Service.CompleteAsync(ticket, NoParts, cts.Token);

        await complete.Should().ThrowAsync<OperationCanceledException>();
        h.Db.ProjectFiles.Should().BeEmpty();
        h.Storage.Sizes.Should().BeEmpty("a cancelled Apply must not leave either object in storage outside the quota");
        h.Storage.DeletedKeys.Should().Contain(ticket.Key);
    }

    /// <summary>S-46: the move succeeded (the ticket key is gone), then the request is cancelled during the HEAD, the
    /// quota check or SaveChanges. The cancellation surfaces, nothing is recorded, and the fresh-key copy is removed on
    /// a non-request token (the harness delete refuses a cancelled one).</summary>
    [Theory]
    [InlineData("head")]
    [InlineData("quota")]
    [InlineData("save")]
    public async Task A_cancellation_after_the_relocation_still_removes_the_fresh_key_copy(string window)
    {
        DirectUploadHarness h = new();
        DirectUploadTicket ticket = await Relocating(h);
        h.Storage.Put(ticket.Key, 4);
        using CancellationTokenSource cts = new();
        switch (window)
        {
            case "head": h.Storage.HeadFaultsAfterMove = true; h.Storage.CancelDuringHead = cts; break;
            case "quota": h.PlanGate.CancelDuringCheck = cts; break;
            default: h.SaveCanceller.CancelDuringSave = cts; break;
        }

        Func<Task> complete = () => h.Service.CompleteAsync(ticket, NoParts, cts.Token);

        await complete.Should().ThrowAsync<OperationCanceledException>();
        h.Storage.Moves.Should().ContainSingle("the relocation itself succeeded");
        string freshKey = h.Storage.Moves.Single().To;
        h.Db.ProjectFiles.Should().BeEmpty();
        h.Storage.Sizes.Should().BeEmpty("a cancelled Apply must not leave the fresh-key copy outside the quota");
        h.Storage.DeletedKeys.Should().Contain(freshKey);
    }

    /// <summary>S-46: the row was written and then the acknowledgement was lost to a cancellation; the fresh key is the
    /// recorded file, so the cleanup keeps it.</summary>
    [Fact]
    public async Task A_cancellation_after_the_row_is_saved_keeps_the_fresh_key_it_points_at()
    {
        DirectUploadHarness h = new();
        DirectUploadTicket ticket = await Relocating(h);
        h.Storage.Put(ticket.Key, 4);
        using CancellationTokenSource cts = new();
        h.SaveCanceller.CancelAfterSave = cts;

        Func<Task> complete = () => h.Service.CompleteAsync(ticket, NoParts, cts.Token);

        await complete.Should().ThrowAsync<OperationCanceledException>();
        string freshKey = h.Storage.Moves.Single().To;
        h.Db.ProjectFiles.Single().StorageKey.Should().Be(freshKey);
        h.Storage.Sizes.Should().ContainKey(freshKey);
        h.Storage.DeletedKeys.Should().NotContain(freshKey);
    }

    /// <summary>QA-P0-28: a HEAD, a quota check or a save that throws something that is neither a refusal nor a
    /// <c>DbUpdateException</c> orphans the fresh-key copy exactly as a cancellation does. The widened cleanup removes
    /// it on a non-request token (the harness delete refuses a cancelled one), keeps the no-row check, and the original
    /// failure surfaces unchanged.</summary>
    [Theory]
    [InlineData("head")]
    [InlineData("quota")]
    [InlineData("save")]
    public async Task A_non_cancellation_failure_after_the_relocation_still_removes_the_fresh_key_copy(string window)
    {
        DirectUploadHarness h = new();
        DirectUploadTicket ticket = await Relocating(h);
        h.Storage.Put(ticket.Key, 4);
        InvalidOperationException failure = new("the database connection dropped");
        switch (window)
        {
            case "head": h.Storage.HeadFaultsAfterMove = true; h.Storage.HeadFailure = failure; break;
            case "quota": h.PlanGate.CheckFailure = failure; break;
            default: h.SaveCanceller.SaveFailure = failure; break;
        }

        Func<Task> complete = () => h.Service.CompleteAsync(ticket, NoParts, CancellationToken.None);

        (await complete.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(failure);
        h.Storage.Moves.Should().ContainSingle("the relocation itself succeeded");
        string freshKey = h.Storage.Moves.Single().To;
        h.Db.ProjectFiles.Should().BeEmpty();
        h.Storage.Sizes.Should().BeEmpty("a failed Apply must not leave the fresh-key copy outside the quota");
        h.Storage.DeletedKeys.Should().Contain(freshKey);
    }

    [Fact]
    public async Task The_relocation_flag_is_covered_by_the_ticket_mac()
    {
        DirectUploadHarness h = new();
        DirectUploadTicket ticket = await Relocating(h);

        h.Service.Verify(DirectUploadHarness.Echo(ticket), h.CallerKey).Should().NotBeNull();
        h.Service.Verify(DirectUploadHarness.Echo(ticket) with { RelocateOnComplete = false }, h.CallerKey)
            .Should().BeNull("stripping the flag would turn the ticket into a four-hour, in-place one");
    }

    [Fact]
    public async Task A_relocating_ticket_above_the_copy_ceiling_is_refused_before_anything_is_opened()
    {
        DirectUploadHarness h = new();

        Func<Task> create = () => Relocating(h, DirectUploadService.RelocateMaxBytes + 1, "huge.mov");

        (await create.Should().ThrowAsync<DirectUploadRefusedException>()).Which.Code.Should().Be("FILE_TOO_LARGE");
        h.Multipart.Created.Should().BeEmpty();
    }
}
