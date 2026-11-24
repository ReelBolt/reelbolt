#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Services.Uploads;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Uploads;

/// <summary>
/// The ticket MAC: a ticket the service minted for one caller verifies for that caller only, and no
/// field the client echoes back can be changed without invalidating it. The token itself is a real
/// HMAC (<c>ConfirmationTokenService</c> with a process-local key), so these are assertions about the
/// service rather than about a stub that agrees with the test.
/// </summary>
public class DirectUploadTicketVerificationTests
{
    private static async Task<(DirectUploadHarness H, DirectUploadTicket Ticket)> MultipartTicket()
    {
        DirectUploadHarness h = new();
        DirectUploadTicket ticket = await h.Create(h.Request(
            sizeBytes: DirectUploadLimits.SinglePutMaxBytes + 1,
            fileName: "big.mp4",
            sourceKind: "local_folder",
            sourceRevision: "rev-1",
            sourceLinkId: Guid.NewGuid()));
        return (h, ticket);
    }

    [Fact]
    public async Task A_ticket_verifies_for_the_caller_it_was_minted_for()
    {
        (DirectUploadHarness h, DirectUploadTicket ticket) = await MultipartTicket();

        DirectUploadTicket? verified = h.Service.Verify(DirectUploadHarness.Echo(ticket), h.CallerKey);

        verified.Should().NotBeNull();
        verified!.UploadToken.Should().Be(ticket.UploadToken);
        verified.Mode.Should().Be(ticket.Mode);
        verified.ProjectId.Should().Be(ticket.ProjectId);
        verified.FileId.Should().Be(ticket.FileId);
        verified.Key.Should().Be(ticket.Key);
        verified.FileName.Should().Be(ticket.FileName);
        verified.SizeBytes.Should().Be(ticket.SizeBytes);
        verified.ExpiresAtUnix.Should().Be(ticket.ExpiresAtUnix);
        verified.UploadId.Should().Be(ticket.UploadId);
        verified.PartSizeBytes.Should().Be(ticket.PartSizeBytes);
        verified.PartCount.Should().Be(ticket.PartCount);
        verified.SourceKind.Should().Be("local_folder");
        verified.SourceRevision.Should().Be("rev-1");
        verified.SourceLinkId.Should().Be(ticket.SourceLinkId);
    }

    [Fact]
    public async Task A_single_put_ticket_round_trips_too()
    {
        DirectUploadHarness h = new();
        DirectUploadTicket ticket = await h.Create(sizeBytes: 4096, fileName: "notes.txt", contentType: "text/plain");

        DirectUploadTicket? verified = h.Service.Verify(DirectUploadHarness.Echo(ticket), h.CallerKey);

        verified.Should().NotBeNull();
        verified!.Mode.Should().Be(DirectUploadMode.SinglePut);
        verified.Url.Should().BeNull("the URL is not part of the ticket the client echoes back");
        verified.UploadId.Should().BeNull();
    }

    [Fact]
    public async Task A_ticket_minted_for_the_browser_does_not_verify_for_the_device()
    {
        (DirectUploadHarness h, DirectUploadTicket ticket) = await MultipartTicket();

        h.Service.Verify(DirectUploadHarness.Echo(ticket), h.DeviceCallerKey).Should().BeNull(
            "the MAC is bound to the caller key, so a device cannot spend the browser's ticket");
        h.Service.Verify(DirectUploadHarness.Echo(ticket), h.CallerKey).Should().NotBeNull(
            "and the positive control: the same ticket verifies for the caller it was minted for");
    }

    [Fact]
    public async Task A_device_ticket_does_not_verify_for_the_browser()
    {
        DirectUploadHarness h = new();
        DirectUploadTicket ticket = await h.Create(h.Request(callerKey: h.DeviceCallerKey));

        h.Service.Verify(DirectUploadHarness.Echo(ticket), h.CallerKey).Should().BeNull();
        h.Service.Verify(DirectUploadHarness.Echo(ticket), h.DeviceCallerKey).Should().NotBeNull();
    }

    [Fact]
    public async Task No_echoed_field_can_be_tampered_with()
    {
        (DirectUploadHarness h, DirectUploadTicket ticket) = await MultipartTicket();
        DirectUploadTicketEcho valid = DirectUploadHarness.Echo(ticket);

        Guid otherFileId = Guid.NewGuid();
        List<(string What, DirectUploadTicketEcho Echo)> tampered =
        [
            ("mode", valid with { Mode = DirectUploadMode.SinglePut }),
            ("upload id", valid with { UploadId = "upload-2" }),
            ("part size", valid with { PartSizeBytes = valid.PartSizeBytes * 2 }),
            ("part count", valid with { PartCount = valid.PartCount + 1 }),
            ("size", valid with { SizeBytes = valid.SizeBytes + 1 }),
            ("key", valid with { Key = valid.Key + "x" }),
            ("source revision", valid with { SourceRevision = "rev-2" }),
            ("source link id", valid with { SourceLinkId = Guid.NewGuid() }),
            ("replaces file id", valid with { ReplacesFileId = Guid.NewGuid() }),
            ("expiry", valid with { ExpiresAtUnix = valid.ExpiresAtUnix + 1 }),
            ("file id", valid with
            {
                FileId = otherFileId,
                Key = DirectUploadService.DeriveKey(valid.ProjectId, otherFileId, valid.FileName, valid.RelativePath)!
            }),
        ];

        foreach ((string what, DirectUploadTicketEcho echo) in tampered)
        {
            h.Service.Verify(echo, h.CallerKey).Should().BeNull($"tampering with the {what} must invalidate the ticket");
        }
    }

    [Fact]
    public async Task A_key_the_ticket_never_derived_is_refused_before_the_mac_is_consulted()
    {
        (DirectUploadHarness h, DirectUploadTicket ticket) = await MultipartTicket();

        DirectUploadTicketEcho swapped = DirectUploadHarness.Echo(ticket) with
        {
            Key = $"projects/{ticket.ProjectId}/userFiles/{Guid.NewGuid()}.mp4"
        };

        h.Service.Verify(swapped, h.CallerKey).Should().BeNull();
    }

    [Fact]
    public void An_echo_that_is_missing_its_identity_verifies_nothing()
    {
        DirectUploadHarness h = new();

        h.Service.Verify(null!, h.CallerKey).Should().BeNull();
        h.Service.Verify(new DirectUploadTicketEcho("", DirectUploadMode.SinglePut, Guid.NewGuid(), Guid.NewGuid(), "k", "f.mp4", null, "video/mp4", 1, 0, null, 0, 0, null, null, null, null), h.CallerKey).Should().BeNull();
        h.Service.Verify(new DirectUploadTicketEcho("t", DirectUploadMode.SinglePut, Guid.Empty, Guid.NewGuid(), "k", "f.mp4", null, "video/mp4", 1, 0, null, 0, 0, null, null, null, null), h.CallerKey).Should().BeNull();
        h.Service.Verify(new DirectUploadTicketEcho("t", DirectUploadMode.SinglePut, Guid.NewGuid(), Guid.Empty, "k", "f.mp4", null, "video/mp4", 1, 0, null, 0, 0, null, null, null, null), h.CallerKey).Should().BeNull();
        h.Service.Verify(new DirectUploadTicketEcho("t", DirectUploadMode.SinglePut, Guid.NewGuid(), Guid.NewGuid(), "", "f.mp4", null, "video/mp4", 1, 0, null, 0, 0, null, null, null, null), h.CallerKey).Should().BeNull();
        h.Service.Verify(new DirectUploadTicketEcho("t", (DirectUploadMode)7, Guid.NewGuid(), Guid.NewGuid(), "k", "f.mp4", null, "video/mp4", 1, 0, null, 0, 0, null, null, null, null), h.CallerKey).Should().BeNull();
        h.Service.Verify(new DirectUploadTicketEcho("t", DirectUploadMode.SinglePut, Guid.NewGuid(), Guid.NewGuid(), "k", "f.mp4", null, "video/mp4", -1, 0, null, 0, 0, null, null, null, null), h.CallerKey).Should().BeNull();
        h.Service.Verify(new DirectUploadTicketEcho("t", DirectUploadMode.SinglePut, Guid.NewGuid(), Guid.NewGuid(), "k", "f.mp4", null, "video/mp4", 1, 0, null, 0, 0, null, null, null, null), "").Should().BeNull();
    }

    // ---------------------------------------------------------------- expiry

    /// <summary>
    /// The interface is explicit that <c>Verify</c> judges forgery, not expiry: a null there could not
    /// delete the object an expired ticket left behind. Expiry is therefore enforced where the ticket is
    /// USED, and that enforcement discards what was uploaded — which is what this asserts.
    /// </summary>
    [Fact]
    public async Task An_expired_ticket_is_refused_when_it_is_used_and_its_object_is_discarded()
    {
        DirectUploadHarness h = new();
        DirectUploadTicket ticket = await h.Create(sizeBytes: 4096, fileName: "clip.mp4");
        DirectUploadTicketEcho echo = DirectUploadHarness.Echo(ticket);
        h.Clock.Advance(DirectUploadService.SinglePutTicketTtl + TimeSpan.FromMinutes(1));

        h.Service.Verify(echo, h.CallerKey).Should().NotBeNull(
            "Verify answers 'this ticket is authentic', which it still is");
        h.Storage.Put(ticket.Key, ticket.SizeBytes);

        Func<Task> act = () => h.Service.CompleteAsync(ticket, new DirectUploadCompletion([]), CancellationToken.None);

        DirectUploadRefusedException refusal = (await act.Should().ThrowAsync<DirectUploadRefusedException>()).Which;
        refusal.Code.Should().Be("UPLOAD_TICKET_EXPIRED");
        refusal.StatusCode.Should().Be(410);
        h.Storage.DeletedKeys.Should().ContainSingle().Which.Should().Be(ticket.Key);
        h.Db.ProjectFiles.Should().BeEmpty();
    }

    [Fact]
    public async Task An_expired_multipart_ticket_aborts_the_upload_instead_of_presigning_parts()
    {
        (DirectUploadHarness h, DirectUploadTicket ticket) = await MultipartTicket();
        h.Clock.Advance(DirectUploadService.MultipartTicketTtl + TimeSpan.FromMinutes(1));

        Func<Task> act = () => h.Service.PresignPartsAsync(ticket, [1], CancellationToken.None);

        (await act.Should().ThrowAsync<DirectUploadRefusedException>()).Which.Code.Should().Be("UPLOAD_TICKET_EXPIRED");
        h.Multipart.Presigned.Should().BeEmpty();
        h.Multipart.Aborted.Should().ContainSingle()
            .Which.Should().Be((ticket.ProjectId, ticket.Key, ticket.UploadId!));
    }
}
