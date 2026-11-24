#nullable enable
using System;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Services.Uploads;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Uploads;

/// <summary>
/// How <see cref="DirectUploadService.CreateAsync"/> chooses between one presigned PUT and an S3
/// multipart upload (docs/design/local-folder-sync-and-storage.md "Uploads without a size cap"), and
/// every size/path rule it refuses before a signed write capability exists.
/// </summary>
public class DirectUploadCreationTests
{
    private const long MiB = 1024 * 1024;

    [Fact]
    public async Task A_small_file_gets_one_presigned_put_and_no_upload_id()
    {
        DirectUploadHarness h = new();

        DirectUploadTicket ticket = await h.Create(sizeBytes: 4096, fileName: "clip.mov", contentType: "application/octet-stream");

        ticket.Mode.Should().Be(DirectUploadMode.SinglePut);
        ticket.UploadId.Should().BeNull("a single PUT has no multipart upload behind it");
        ticket.Url.Should().NotBeNullOrEmpty();
        ticket.PartSizeBytes.Should().Be(0);
        ticket.PartCount.Should().Be(0);
        ticket.SizeBytes.Should().Be(4096);
        ticket.ContentType.Should().Be("video/quicktime", "R22: a generic content type is resolved from the extension");

        h.Multipart.Created.Should().BeEmpty("the multipart store is never opened for a small file");
        h.Presign.BrowserPuts.Should().ContainSingle()
            .Which.Should().Be((h.Project.Id, ticket.Key, "video/quicktime", DirectUploadService.SinglePutUrlTtl));
        ticket.Headers.Should().ContainKey("Content-Type").WhoseValue.Should().Be("video/quicktime");
    }

    [Fact]
    public async Task A_file_above_the_single_put_ceiling_becomes_multipart_with_server_chosen_parts()
    {
        DirectUploadHarness h = new();
        long size = DirectUploadLimits.SinglePutMaxBytes + 1;

        DirectUploadTicket ticket = await h.Create(sizeBytes: size, fileName: "huge.mp4");

        ticket.Mode.Should().Be(DirectUploadMode.Multipart);
        ticket.Url.Should().BeNull("the client gets part URLs, never one PUT for the whole object");
        ticket.UploadId.Should().Be("upload-1");
        ticket.PartSizeBytes.Should().Be(64 * MiB, "256 MiB plus one byte still fits in five 64 MiB parts");
        ticket.PartCount.Should().Be(5);
        ticket.PartSizeBytes.Should().Be(DirectUploadLimits.PartSizeFor(size));

        h.Presign.BrowserPuts.Should().BeEmpty();
        h.Multipart.Created.Should().ContainSingle()
            .Which.Should().Be((h.Project.Id, ticket.Key, "video/mp4"));
    }

    [Fact]
    public async Task A_file_exactly_at_the_ceiling_stays_a_single_put()
    {
        DirectUploadHarness h = new();

        DirectUploadTicket ticket = await h.Create(sizeBytes: DirectUploadLimits.SinglePutMaxBytes);

        ticket.Mode.Should().Be(DirectUploadMode.SinglePut, "the boundary belongs to the simpler path");
        ticket.Url.Should().NotBeNullOrEmpty();
        h.Multipart.Created.Should().BeEmpty();
    }

    [Fact]
    public async Task A_file_above_the_object_ceiling_is_refused_before_any_storage_call()
    {
        DirectUploadHarness h = new();

        Func<Task> act = () => h.Create(sizeBytes: DirectUploadLimits.MaxObjectBytes + 1);

        DirectUploadRefusedException refusal = (await act.Should().ThrowAsync<DirectUploadRefusedException>()).Which;
        refusal.Code.Should().Be("FILE_TOO_LARGE");
        refusal.StatusCode.Should().Be(413);
        h.Presign.BrowserPuts.Should().BeEmpty();
        h.Multipart.Created.Should().BeEmpty();
        h.PlanGate.UploadChecks.Should().BeEmpty("the size ceiling is judged before the quota is consulted");
    }

    [Fact]
    public async Task A_zero_byte_file_is_a_valid_upload()
    {
        DirectUploadHarness h = new();

        DirectUploadTicket ticket = await h.Create(sizeBytes: 0, fileName: "empty.txt", contentType: "text/plain");

        ticket.Mode.Should().Be(DirectUploadMode.SinglePut);
        ticket.SizeBytes.Should().Be(0);
        ticket.Url.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task A_negative_size_is_refused()
    {
        DirectUploadHarness h = new();

        Func<Task> act = () => h.Create(sizeBytes: -1);

        DirectUploadRefusedException refusal = (await act.Should().ThrowAsync<DirectUploadRefusedException>()).Which;
        refusal.Code.Should().Be("INVALID_SIZE");
        refusal.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task A_deployment_without_multipart_says_so_instead_of_pretending()
    {
        DirectUploadHarness h = new(withMultipart: false);

        Func<Task> act = () => h.Create(sizeBytes: DirectUploadLimits.SinglePutMaxBytes + 1);

        DirectUploadRefusedException refusal = (await act.Should().ThrowAsync<DirectUploadRefusedException>()).Which;
        refusal.Code.Should().Be("DIRECT_UPLOAD_DISABLED");
        refusal.StatusCode.Should().Be(501);
    }

    [Fact]
    public async Task A_direct_upload_can_only_add_project_files()
    {
        DirectUploadHarness h = new();

        Func<Task> act = () => h.Create(h.Request(category: "outputFiles"));

        DirectUploadRefusedException refusal = (await act.Should().ThrowAsync<DirectUploadRefusedException>()).Which;
        refusal.Code.Should().Be("INVALID_PATH");
        refusal.StatusCode.Should().Be(400);
    }

    [Theory]
    [InlineData("")]
    [InlineData("../../etc/passwd")]
    [InlineData("folder/clip.mp4")]
    [InlineData("..")]
    public async Task An_invalid_file_name_is_refused(string fileName)
    {
        DirectUploadHarness h = new();

        Func<Task> act = () => h.Create(h.Request(fileName: fileName));

        (await act.Should().ThrowAsync<DirectUploadRefusedException>()).Which.Code.Should().BeOneOf("INVALID_PATH");
    }

    [Fact]
    public async Task A_folder_path_is_normalized_into_the_key_and_the_relative_path()
    {
        DirectUploadHarness h = new();

        DirectUploadTicket ticket = await h.Create(sizeBytes: 4, fileName: "clip.mp4", relativePath: "broll/day1/clip.mp4");

        ticket.RelativePath.Should().Be("broll/day1/clip.mp4");
        ticket.FileName.Should().Be("clip.mp4");
        ticket.Key.Should().Be($"projects/{h.Project.Id}/userFiles/broll/day1/{ticket.FileId}.mp4");
    }

    // ---------------------------------------------------------------- part size maths

    [Fact]
    public void The_minimum_part_size_is_used_until_the_part_count_would_overflow()
    {
        DirectUploadLimits.PartSizeFor(0).Should().Be(DirectUploadLimits.MinPartBytes);
        DirectUploadLimits.PartSizeFor(DirectUploadLimits.MinPartBytes).Should().Be(DirectUploadLimits.MinPartBytes);
        DirectUploadLimits.PartSizeFor(DirectUploadLimits.MaxParts * DirectUploadLimits.MinPartBytes)
            .Should().Be(DirectUploadLimits.MinPartBytes, "exactly 10,000 minimum parts is still within the ceiling");

        // One byte past that and the part size has to grow, rounded up to a whole MiB.
        long partSize = DirectUploadLimits.PartSizeFor(DirectUploadLimits.MaxParts * DirectUploadLimits.MinPartBytes + 1);
        partSize.Should().BeGreaterThan(DirectUploadLimits.MinPartBytes);
        partSize.Should().Be(DirectUploadLimits.MinPartBytes + MiB);
    }

    [Fact]
    public void The_largest_object_still_fits_within_the_part_ceiling()
    {
        long partSize = DirectUploadLimits.PartSizeFor(DirectUploadLimits.MaxObjectBytes);
        long partCount = (DirectUploadLimits.MaxObjectBytes + partSize - 1) / partSize;

        partSize.Should().BeGreaterThan(DirectUploadLimits.MinPartBytes);
        (partSize % MiB).Should().Be(0, "the part size is rounded up to a whole MiB");
        partCount.Should().BeLessThanOrEqualTo(DirectUploadLimits.MaxParts);
        (partSize * DirectUploadLimits.MaxParts).Should().BeGreaterThanOrEqualTo(DirectUploadLimits.MaxObjectBytes);
    }

    [Fact]
    public async Task A_file_bigger_than_the_minimum_allows_gets_a_larger_part_size()
    {
        DirectUploadHarness h = new();
        long size = 700L * 1024 * 1024 * 1024;   // 700 GiB: 64 MiB parts would need 10,501 of them

        DirectUploadTicket ticket = await h.Create(sizeBytes: size);

        long expectedPartSize = DirectUploadLimits.PartSizeFor(size);
        expectedPartSize.Should().BeGreaterThan(DirectUploadLimits.MinPartBytes);
        ticket.PartSizeBytes.Should().Be(expectedPartSize);
        ticket.PartCount.Should().BeLessThanOrEqualTo(DirectUploadLimits.MaxParts);
        ticket.PartCount.Should().Be((int)((size + expectedPartSize - 1) / expectedPartSize));
    }

    // ---------------------------------------------------------------- replacements

    [Fact]
    public async Task A_replacement_gets_a_new_object_key_so_the_old_bytes_survive()
    {
        DirectUploadHarness h = new();
        ReelBolt.Shared.Data.Models.ProjectFile replaced = h.SeedFile(sizeBytes: 100);

        DirectUploadTicket ticket = await h.Create(h.Request(sizeBytes: 110, fileName: "old.mp4", replacesFileId: replaced.Id));

        ticket.FileId.Should().NotBe(replaced.Id, "the replacement lands beside the old object, never over it");
        ticket.Key.Should().NotBe(replaced.StorageKey);
        ticket.Key.Should().StartWith($"projects/{h.Project.Id}/userFiles/");
        h.Storage.DeletedKeys.Should().BeEmpty("nothing is destroyed before the new bytes are verified");
    }

    [Fact]
    public async Task Replacing_only_needs_room_for_the_net_growth()
    {
        DirectUploadHarness h = new();
        ReelBolt.Shared.Data.Models.ProjectFile replaced = h.SeedFile(sizeBytes: 100);

        await h.Create(h.Request(sizeBytes: 110, replacesFileId: replaced.Id));

        h.PlanGate.UploadChecks.Should().Equal([10L],
            "a 110-byte file replacing a 100-byte one asks the plan for 10 bytes, not 110");
    }

    [Fact]
    public async Task Replacing_a_library_sound_is_charged_the_full_new_size()
    {
        DirectUploadHarness h = new();
        ReelBolt.Shared.Data.Models.ProjectFile replaced = h.SeedFile(sizeBytes: 100);
        replaced.SourceKind = "sfx_library";
        h.Db.SaveChanges();

        await h.Create(h.Request(sizeBytes: 110, replacesFileId: replaced.Id));

        h.PlanGate.UploadChecks.Should().Equal([110L], "a quota-exempt library sound frees nothing to net off");
    }

    [Fact]
    public async Task A_replacement_target_that_does_not_exist_is_refused()
    {
        DirectUploadHarness h = new();

        Func<Task> act = () => h.Create(h.Request(sizeBytes: 10, replacesFileId: Guid.NewGuid()));

        DirectUploadRefusedException refusal = (await act.Should().ThrowAsync<DirectUploadRefusedException>()).Which;
        refusal.Code.Should().Be("REPLACED_FILE_NOT_FOUND");
        refusal.StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task A_replacement_of_a_file_in_another_project_is_not_found()
    {
        DirectUploadHarness h = new();
        ReelBolt.Shared.Data.Models.ProjectFile other = h.SeedFile(projectId: Guid.NewGuid());

        Func<Task> act = () => h.Create(h.Request(sizeBytes: 10, replacesFileId: other.Id));

        (await act.Should().ThrowAsync<DirectUploadRefusedException>()).Which.Code.Should().Be("REPLACED_FILE_NOT_FOUND");
    }

    // ---------------------------------------------------------------- the upload MIME fallback (QA-3)

    [Fact]
    public void ResolveContentType_falls_back_to_the_text_policy_without_overriding_media_or_a_client_type()
    {
        // Pins the fallback order in DirectUploadService.ResolveContentType: a media extension wins,
        // then the shared text policy maps a text extension, and a client's own specific type is
        // always kept. QA recorded that the brief's item 5 rested on a throwaway probe; this pins the
        // call site so a future reorder cannot regress it silently.
        DirectUploadService.ResolveContentType("application/octet-stream", "_icons.scss").Should().Be("text/plain");
        DirectUploadService.ResolveContentType("application/octet-stream", "Contoso.Portal.Client.csproj").Should().Be("application/xml");
        DirectUploadService.ResolveContentType("application/octet-stream", "song.mp3").Should().Be("audio/mpeg",
            "a media extension is resolved ahead of the text policy");
        DirectUploadService.ResolveContentType("image/png", "shot.png").Should().Be("image/png",
            "a specific client type is kept");
        DirectUploadService.ResolveContentType("application/octet-stream", "shot.png").Should().Be("application/octet-stream",
            "an unknown extension is left alone");
    }

    // ---------------------------------------------------------------- quota on the claim

    [Fact]
    public async Task Over_quota_refuses_the_claim_and_leaves_no_signed_capability_behind()
    {
        DirectUploadHarness h = new();
        h.PlanGate.Violations.Enqueue(DirectUploadHarness.OverQuota(used: 500, limit: 400));

        Func<Task> act = () => h.Create(sizeBytes: 500);

        DirectUploadRefusedException refusal = (await act.Should().ThrowAsync<DirectUploadRefusedException>()).Which;
        refusal.Code.Should().Be("STORAGE_QUOTA_EXCEEDED");
        refusal.StatusCode.Should().Be(413);
        refusal.UsedBytes.Should().Be(500);
        refusal.LimitBytes.Should().Be(400);

        h.PlanGate.UploadChecks.Should().Equal([500L]);
        h.Presign.BrowserPuts.Should().BeEmpty("a refusal must not leave a presigned PUT in the wild");
        h.Multipart.Created.Should().BeEmpty();
    }
}
