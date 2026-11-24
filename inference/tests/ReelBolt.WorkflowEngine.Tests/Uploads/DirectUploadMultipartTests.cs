#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Services.Uploads;
using ReelBolt.Shared.Data.Models;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Uploads;

/// <summary>
/// The multipart half of a large direct upload: presigned part URLs in batches, completion that
/// requires every part and carries the ETags to storage, and an abort that is safe to call twice.
/// </summary>
public class DirectUploadMultipartTests
{
    private const long MiB = 1024 * 1024;

    private static Task<DirectUploadTicket> Big(DirectUploadHarness h, long sizeBytes) =>
        h.Create(sizeBytes, fileName: "huge.mp4");

    [Fact]
    public async Task Part_urls_come_back_only_for_the_numbers_that_were_asked_for()
    {
        DirectUploadHarness h = new();
        DirectUploadTicket ticket = await Big(h, DirectUploadLimits.SinglePutMaxBytes + 1);
        ticket.PartCount.Should().Be(5);

        IReadOnlyList<DirectUploadPartUrl> urls = await h.Service.PresignPartsAsync(ticket, [1, 3, 5], CancellationToken.None);

        urls.Select(u => u.PartNumber).Should().Equal(1, 3, 5);
        urls.Should().OnlyContain(u => u.Url.Contains(ticket.UploadId!, StringComparison.Ordinal));
        urls.Should().OnlyContain(u => u.ExpiresAtUnix <= ticket.ExpiresAtUnix);
        urls.Should().OnlyContain(u => u.ExpiresAtUnix >= h.Clock.Now.ToUnixTimeSeconds());
        h.Multipart.Presigned.Select(p => p.PartNumber).Should().Equal(1, 3, 5);
        h.Multipart.Presigned.Should().OnlyContain(p => p.Ttl == DirectUploadService.PartUrlTtl);
    }

    [Fact]
    public async Task Part_urls_never_outlive_their_ticket()
    {
        DirectUploadHarness h = new();
        DirectUploadTicket ticket = await Big(h, DirectUploadLimits.SinglePutMaxBytes + 1);

        // One minute of ticket left: the batch is valid for a minute, not the usual four hours.
        h.Clock.Advance(DirectUploadService.MultipartTicketTtl - TimeSpan.FromMinutes(1));
        await h.Service.PresignPartsAsync(ticket, [1], CancellationToken.None);

        h.Multipart.Presigned.Should().ContainSingle().Which.Ttl.Should().Be(TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task A_part_number_above_the_part_count_is_refused()
    {
        DirectUploadHarness h = new();
        DirectUploadTicket ticket = await Big(h, DirectUploadLimits.SinglePutMaxBytes + 1);

        Func<Task> act = () => h.Service.PresignPartsAsync(ticket, [ticket.PartCount + 1], CancellationToken.None);

        DirectUploadRefusedException refusal = (await act.Should().ThrowAsync<DirectUploadRefusedException>()).Which;
        refusal.Code.Should().Be("INVALID_PART_NUMBERS");
        refusal.StatusCode.Should().Be(400);
        h.Multipart.Presigned.Should().BeEmpty();
    }

    [Fact]
    public async Task Part_numbers_must_be_non_empty_distinct_and_in_range()
    {
        DirectUploadHarness h = new();
        DirectUploadTicket big = await Big(h, 101 * 64 * MiB + 1);
        big.PartCount.Should().BeGreaterThan(DirectUploadService.MaxPartUrlsPerCall);

        List<(string What, IReadOnlyList<int> Numbers)> invalid =
        [
            ("an empty batch", []),
            ("a zero part number", [0]),
            ("a duplicate", [1, 1]),
            ("more than one call's worth", Enumerable.Range(1, DirectUploadService.MaxPartUrlsPerCall + 1).ToList()),
        ];

        foreach ((string what, IReadOnlyList<int> numbers) in invalid)
        {
            Func<Task> act = () => h.Service.PresignPartsAsync(big, numbers, CancellationToken.None);

            DirectUploadRefusedException refusal = (await act.Should().ThrowAsync<DirectUploadRefusedException>()).Which;
            refusal.Code.Should().Be("INVALID_PART_NUMBERS", $"{what} is not a valid batch");
        }

        h.Multipart.Presigned.Should().BeEmpty();
    }

    [Fact]
    public async Task A_single_put_ticket_has_no_parts()
    {
        DirectUploadHarness h = new();
        DirectUploadTicket ticket = await h.Create();

        Func<Task> act = () => h.Service.PresignPartsAsync(ticket, [1], CancellationToken.None);

        (await act.Should().ThrowAsync<DirectUploadRefusedException>()).Which.Code.Should().Be("NOT_MULTIPART");
    }

    [Fact]
    public async Task Completion_requires_every_part()
    {
        DirectUploadHarness h = new();
        DirectUploadTicket ticket = await Big(h, DirectUploadLimits.SinglePutMaxBytes + 1);
        h.Storage.Put(ticket.Key, ticket.SizeBytes);
        List<DirectUploadCompletedPart> missingOne = [.. Enumerable.Range(1, ticket.PartCount - 1).Select(n => new DirectUploadCompletedPart(n, $"etag-{n}"))];

        Func<Task> act = () => h.Service.CompleteAsync(ticket, new DirectUploadCompletion(missingOne), CancellationToken.None);

        DirectUploadRefusedException refusal = (await act.Should().ThrowAsync<DirectUploadRefusedException>()).Which;
        refusal.Code.Should().Be("UPLOAD_INCOMPLETE");
        refusal.StatusCode.Should().Be(409);
        h.Multipart.Completed.Should().BeEmpty("storage is not asked to complete a list the client already knows is short");
        h.Db.ProjectFiles.Should().BeEmpty();
    }

    [Fact]
    public async Task Completion_rejects_a_part_list_with_a_blank_etag()
    {
        DirectUploadHarness h = new();
        DirectUploadTicket ticket = await Big(h, DirectUploadLimits.SinglePutMaxBytes + 1);
        List<DirectUploadCompletedPart> parts = [.. Enumerable.Range(1, ticket.PartCount).Select(n => new DirectUploadCompletedPart(n, n == 2 ? "  " : $"etag-{n}"))];

        Func<Task> act = () => h.Service.CompleteAsync(ticket, new DirectUploadCompletion(parts), CancellationToken.None);

        (await act.Should().ThrowAsync<DirectUploadRefusedException>()).Which.Code.Should().Be("UPLOAD_INCOMPLETE");
        h.Multipart.Completed.Should().BeEmpty();
    }

    [Fact]
    public async Task Completion_passes_every_etag_through_to_storage()
    {
        DirectUploadHarness h = new();
        DirectUploadTicket ticket = await Big(h, DirectUploadLimits.SinglePutMaxBytes + 1);
        h.Storage.Put(ticket.Key, ticket.SizeBytes);
        List<DirectUploadCompletedPart> parts = [.. Enumerable.Range(1, ticket.PartCount).Select(n => new DirectUploadCompletedPart(n, $"etag-{n}"))];

        ProjectFile row = await h.Service.CompleteAsync(ticket, new DirectUploadCompletion(parts), CancellationToken.None);

        row.StorageKey.Should().Be(ticket.Key);
        row.SizeBytes.Should().Be(ticket.SizeBytes);
        h.Multipart.Completed.Should().ContainSingle();
        (Guid ProjectId, string Key, string UploadId, IReadOnlyList<DirectUploadCompletedPart> Parts) call = h.Multipart.Completed[0];
        call.ProjectId.Should().Be(ticket.ProjectId);
        call.Key.Should().Be(ticket.Key);
        call.UploadId.Should().Be(ticket.UploadId!);
        call.Parts.Should().BeEquivalentTo(parts);
    }

    [Fact]
    public async Task A_storage_rejection_of_the_part_list_is_an_incomplete_upload()
    {
        DirectUploadHarness h = new();
        DirectUploadTicket ticket = await Big(h, DirectUploadLimits.SinglePutMaxBytes + 1);
        h.Multipart.CompleteFailure = new DirectUploadRefusedException("UPLOAD_INCOMPLETE", "InvalidPart", 409);
        List<DirectUploadCompletedPart> parts = [.. Enumerable.Range(1, ticket.PartCount).Select(n => new DirectUploadCompletedPart(n, $"etag-{n}"))];

        Func<Task> act = () => h.Service.CompleteAsync(ticket, new DirectUploadCompletion(parts), CancellationToken.None);

        (await act.Should().ThrowAsync<DirectUploadRefusedException>()).Which.Code.Should().Be("UPLOAD_INCOMPLETE");
        h.Db.ProjectFiles.Should().BeEmpty();
    }

    [Fact]
    public async Task Storage_that_forgot_the_upload_id_still_completes_from_the_head()
    {
        DirectUploadHarness h = new();
        DirectUploadTicket ticket = await Big(h, DirectUploadLimits.SinglePutMaxBytes + 1);
        h.Storage.Put(ticket.Key, ticket.SizeBytes);
        h.Multipart.CompleteResult = false;   // NoSuchUpload: an earlier completion whose answer was lost
        List<DirectUploadCompletedPart> parts = [.. Enumerable.Range(1, ticket.PartCount).Select(n => new DirectUploadCompletedPart(n, $"etag-{n}"))];

        ProjectFile row = await h.Service.CompleteAsync(ticket, new DirectUploadCompletion(parts), CancellationToken.None);

        row.StorageKey.Should().Be(ticket.Key);
        h.Db.ProjectFiles.Should().ContainSingle();
    }

    [Fact]
    public async Task A_multipart_mismatch_discards_the_object()
    {
        DirectUploadHarness h = new();
        DirectUploadTicket ticket = await Big(h, DirectUploadLimits.SinglePutMaxBytes + 1);
        h.Storage.Put(ticket.Key, ticket.SizeBytes - 1);
        List<DirectUploadCompletedPart> parts = [.. Enumerable.Range(1, ticket.PartCount).Select(n => new DirectUploadCompletedPart(n, $"etag-{n}"))];

        Func<Task> act = () => h.Service.CompleteAsync(ticket, new DirectUploadCompletion(parts), CancellationToken.None);

        (await act.Should().ThrowAsync<DirectUploadRefusedException>()).Which.Code.Should().Be("UPLOAD_SIZE_MISMATCH");
        h.Storage.DeletedKeys.Should().ContainSingle().Which.Should().Be(ticket.Key);
        h.Db.ProjectFiles.Should().BeEmpty();
    }

    [Fact]
    public async Task Aborting_a_multipart_upload_never_throws_when_it_is_already_gone()
    {
        DirectUploadHarness h = new();
        DirectUploadTicket ticket = await Big(h, DirectUploadLimits.SinglePutMaxBytes + 1);
        h.Multipart.AbortFailure = new InvalidOperationException("NoSuchUpload");

        Func<Task> act = () => h.Service.AbortAsync(ticket, CancellationToken.None);

        await act.Should().NotThrowAsync();
        h.Multipart.Aborted.Should().ContainSingle()
            .Which.Should().Be((ticket.ProjectId, ticket.Key, ticket.UploadId!));
    }

    [Fact]
    public void The_multipart_store_refuses_a_key_outside_the_projects_prefix()
    {
        Guid projectId = Guid.NewGuid();
        Guid otherProject = Guid.NewGuid();
        string[] refused =
        [
            "",
            $"projects/{projectId}",
            $"projects/{projectId}/",
            $"projects/{otherProject}/userFiles/x.mp4",
            $"projects/{projectId}/userFiles/../x.mp4",
            $"projects/{projectId}/userFiles//x.mp4",
            $"projects/{projectId}/userFiles/x\\y.mp4",
        ];

        foreach (string key in refused)
        {
            Action act = () => S3MultipartObjectStore.RequireProjectKey(projectId, key);
            act.Should().Throw<ArgumentException>($"{key} is not a normalized key under the project's own prefix");
        }

        Action allowed = () => S3MultipartObjectStore.RequireProjectKey(projectId, $"projects/{projectId}/userFiles/{Guid.NewGuid()}.mp4");
        allowed.Should().NotThrow();
    }

    [Fact]
    public async Task Aborting_after_a_completed_upload_does_not_delete_the_recorded_object()
    {
        DirectUploadHarness h = new();
        DirectUploadTicket ticket = await Big(h, DirectUploadLimits.SinglePutMaxBytes + 1);
        h.Storage.Put(ticket.Key, ticket.SizeBytes);
        List<DirectUploadCompletedPart> parts = [.. Enumerable.Range(1, ticket.PartCount).Select(n => new DirectUploadCompletedPart(n, $"etag-{n}"))];
        await h.Service.CompleteAsync(ticket, new DirectUploadCompletion(parts), CancellationToken.None);

        await h.Service.AbortAsync(ticket, CancellationToken.None);

        h.Storage.DeletedKeys.Should().BeEmpty("a key a row points at is never deleted out from under it");
        h.Storage.Sizes.Should().ContainKey(ticket.Key);
    }
}
