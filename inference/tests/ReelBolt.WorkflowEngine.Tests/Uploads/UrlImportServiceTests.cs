#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Services.Uploads;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Billing;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Uploads;

/// <summary>
/// Import from a public https URL into a project's userFiles (T-002). The network is a stub handler
/// below the importer, so the importer's own URL checks, redirect handling, caps and quota run for real;
/// only the connect-time IP check (GeneratedClipUrlGuard's handler) is replaced.
/// </summary>
public class UrlImportServiceTests
{
    private static readonly byte[] Clip = Enumerable.Range(0, 3000).Select(i => (byte)(i % 251)).ToArray();

    // ---------------------------------------------------------------- refused URLs (no network at all)

    [Theory]
    [InlineData("http://cdn.example.com/a.mp4")]
    [InlineData("ftp://cdn.example.com/a.mp4")]
    [InlineData("https://user:pass@cdn.example.com/a.mp4")]
    [InlineData("https://127.0.0.1/a.mp4")]
    [InlineData("https://10.0.0.5/a.mp4")]
    [InlineData("https://169.254.169.254/latest/meta-data/")]
    [InlineData("https://[::1]/a.mp4")]
    [InlineData("https://[fd00::1]/a.mp4")]
    [InlineData("https://192.168.1.1/a.mp4")]
    [InlineData("https://2130706433/a.mp4")]
    [InlineData("https://intranet/a.mp4")]
    [InlineData("https://localhost/a.mp4")]
    [InlineData("https://metadata.google.internal/a.mp4")]
    [InlineData("not a url")]
    [InlineData("")]
    public async Task A_url_that_is_not_a_public_https_url_is_refused_without_any_request(string url)
    {
        UrlImportHarness h = new();

        UrlImportResult result = await h.Import(url);

        result.Refusal!.Code.Should().Be(UrlImportCodes.UrlRefused);
        result.Refusal.StatusCode.Should().Be(400);
        h.Upstream.Requests.Should().BeEmpty();
        h.NothingStored.Should().BeTrue();
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../escape.mp4")]
    [InlineData("a/b.mp4")]
    public async Task A_traversing_or_nested_file_name_is_refused(string fileName)
    {
        UrlImportHarness h = new();

        UrlImportResult result = await h.Import("https://cdn.example.com/a.mp4", fileName);

        result.Refusal!.Code.Should().Be(UrlImportCodes.InvalidPath);
        h.Upstream.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_traversing_directory_is_refused()
    {
        UrlImportHarness h = new();

        UrlImportResult result = await h.Import("https://cdn.example.com/a.mp4", directoryPath: "../../other");

        result.Refusal!.Code.Should().Be(UrlImportCodes.InvalidPath);
        h.Upstream.Requests.Should().BeEmpty();
    }

    // ---------------------------------------------------------------- redirects

    [Theory]
    [InlineData("https://127.0.0.1/a.mp4")]
    [InlineData("https://169.254.169.254/latest/meta-data/iam")]
    [InlineData("http://cdn.example.com/a.mp4")]
    [InlineData("https://intranet/a.mp4")]
    public async Task A_redirect_to_a_private_or_downgraded_target_is_refused_and_never_followed(string target)
    {
        UrlImportHarness h = new();
        h.Upstream.Redirect("https://cdn.example.com/a.mp4", target);

        UrlImportResult result = await h.Import("https://cdn.example.com/a.mp4");

        result.Refusal!.Code.Should().Be(UrlImportCodes.UrlRefused);
        h.Upstream.Requests.Should().ContainSingle("the redirect target is validated before it is requested");
        h.NothingStored.Should().BeTrue();
    }

    [Fact]
    public async Task Up_to_three_public_redirects_are_followed()
    {
        UrlImportHarness h = new();
        h.Upstream.Redirect("https://a.example.com/x", "https://b.example.com/y");
        h.Upstream.Redirect("https://b.example.com/y", "/z", HttpStatusCode.MovedPermanently);
        h.Upstream.Redirect("https://b.example.com/z", "https://c.example.com/final.mp4", HttpStatusCode.TemporaryRedirect);
        h.Upstream.Serve("https://c.example.com/final.mp4", Clip);

        UrlImportResult result = await h.Import("https://a.example.com/x");

        result.File.Should().NotBeNull();
        result.File!.OriginalFileName.Should().Be("x", "the name comes from the URL the user gave, not a redirect hop");
        h.Upstream.Requests.Select(r => r.RequestUri!.Host).Should().Equal("a.example.com", "b.example.com", "b.example.com", "c.example.com");
    }

    [Fact]
    public async Task A_fourth_redirect_is_refused()
    {
        UrlImportHarness h = new();
        h.Upstream.Redirect("https://a.example.com/1", "https://a.example.com/2");
        h.Upstream.Redirect("https://a.example.com/2", "https://a.example.com/3");
        h.Upstream.Redirect("https://a.example.com/3", "https://a.example.com/4");
        h.Upstream.Redirect("https://a.example.com/4", "https://a.example.com/5");
        h.Upstream.Serve("https://a.example.com/5", Clip);

        UrlImportResult result = await h.Import("https://a.example.com/1");

        result.Refusal!.Code.Should().Be(UrlImportCodes.Failed);
        h.Upstream.Requests.Should().HaveCount(4);
        h.NothingStored.Should().BeTrue();
    }

    // ---------------------------------------------------------------- size and quota

    [Fact]
    public async Task A_declared_length_over_the_cap_is_refused_before_the_body_is_read()
    {
        UrlImportHarness h = new(maxBytes: 1000);
        WatchedStream body = new(Clip);
        h.Upstream.Serve("https://cdn.example.com/a.mp4", Clip, stream: body);

        UrlImportResult result = await h.Import("https://cdn.example.com/a.mp4");

        result.Refusal!.Code.Should().Be(UrlImportCodes.TooLarge);
        result.Refusal.StatusCode.Should().Be(413);
        result.Refusal.LimitBytes.Should().Be(1000);
        body.WasRead.Should().BeFalse();
        h.NothingStored.Should().BeTrue();
        h.PlanGate.UploadChecks.Should().Equal([1000L], "only the headroom probe at the start (S-06); the cap is judged before any byte is read");
    }

    [Fact]
    public async Task A_declared_length_over_the_quota_headroom_is_refused_before_the_body_is_read()
    {
        UrlImportHarness h = new();
        WatchedStream body = new(Clip);
        h.Upstream.Serve("https://cdn.example.com/a.mp4", Clip, stream: body);
        h.PlanGate.Violations.Enqueue(DirectUploadHarness.OverQuota());

        UrlImportResult result = await h.Import("https://cdn.example.com/a.mp4");

        result.Refusal!.Code.Should().Be("STORAGE_QUOTA_EXCEEDED");
        result.Refusal.StatusCode.Should().Be(413);
        h.PlanGate.UploadChecks.Should().Equal([h.Options.MaxBytes], "the headroom probe at the start finds none left (S-06)");
        body.WasRead.Should().BeFalse();
        h.NothingStored.Should().BeTrue();
    }

    [Theory]
    [InlineData(10L)]
    [InlineData(null)]
    public async Task A_body_longer_than_declared_is_aborted_at_the_cap_and_nothing_is_kept(long? declared)
    {
        UrlImportHarness h = new(maxBytes: 1000);
        h.Upstream.Serve("https://cdn.example.com/a.mp4", Clip, declaredLength: declared);

        UrlImportResult result = await h.Import("https://cdn.example.com/a.mp4");

        result.Refusal!.Code.Should().Be(UrlImportCodes.TooLarge);
        h.NothingStored.Should().BeTrue();
        h.Effects.Runs.Should().BeEmpty();
    }

    [Fact]
    public async Task The_quota_is_checked_on_the_bytes_actually_received_and_a_refusal_writes_nothing()
    {
        UrlImportHarness h = new();
        h.Upstream.Serve("https://cdn.example.com/a.mp4", Clip, declaredLength: null);
        h.PlanGate.Violations.Enqueue(null!); // the headroom probe at the start passes
        h.PlanGate.Violations.Enqueue(DirectUploadHarness.OverQuota());

        UrlImportResult result = await h.Import("https://cdn.example.com/a.mp4");

        result.Refusal!.Code.Should().Be("STORAGE_QUOTA_EXCEEDED");
        result.Refusal.UsedBytes.Should().Be(100);
        result.Refusal.LimitBytes.Should().Be(99);
        // Three questions now: the S-06 headroom probe (capped at MaxBytes), the pre-upload refusal on the
        // bytes actually received, and the authoritative check the same size gets under the storage guard
        // just before the row is written (T-106 finding 1). The last two ask about the same size.
        h.PlanGate.UploadChecks.Should().Equal([h.Options.MaxBytes, Clip.LongLength], "the refusal returns before the guarded check");
        h.NothingStored.Should().BeTrue();
        h.Storage.Uploads.Should().BeEmpty("the quota is judged before anything reaches storage");
    }

    /// <summary>
    /// T-106 finding 1: the authoritative quota check runs under the organization's storage guard, after the
    /// stream ends, so the byte streaming never holds one. A replica that loses the race there has already
    /// written the object - and deletes it, so a refusal still stores nothing.
    /// </summary>
    [Fact]
    public async Task A_refusal_at_the_guarded_recheck_removes_the_object_that_had_to_be_written_first()
    {
        UrlImportHarness h = new();
        h.Upstream.Serve("https://cdn.example.com/a.mp4", Clip, declaredLength: null);
        h.PlanGate.Violations.Enqueue(null!);                            // the S-06 headroom probe
        h.PlanGate.Violations.Enqueue(null!);                            // the pre-upload check on the real bytes
        h.PlanGate.Violations.Enqueue(DirectUploadHarness.OverQuota());  // the guarded re-check

        UrlImportResult result = await h.Import("https://cdn.example.com/a.mp4");

        result.Refusal!.Code.Should().Be("STORAGE_QUOTA_EXCEEDED");
        h.PlanGate.UploadChecks.Should().Equal([h.Options.MaxBytes, Clip.LongLength, Clip.LongLength]);
        h.Storage.Uploads.Should().ContainSingle("the upload precedes the guarded re-check; that is what keeps the guard short");
        h.Storage.DeletedKeys.Should().ContainSingle().Which.Should().Be(h.Storage.Uploads[0].Key, "the refusal removes what this call wrote");
        h.NothingStored.Should().BeTrue();
    }

    /// <summary>
    /// T-114 push-review finding 2: the cleanup delete of a refused import must not run while the
    /// organization's storage guard is held. The delete is one bounded S3 round trip, but it is network
    /// I/O; holding the advisory lock (shared with every delete and import of the organization) across
    /// it lets a slow object store serialize unrelated work.
    /// </summary>
    [Fact]
    public async Task A_refused_import_removes_its_object_after_the_storage_guard_is_released()
    {
        OrganizationStorageLock storageLock = new();
        UrlImportHarness h = new(storageLock: storageLock);
        h.Upstream.Serve("https://cdn.example.com/a.mp4", Clip, declaredLength: null);
        h.PlanGate.Violations.Enqueue(null!);                            // the S-06 headroom probe
        h.PlanGate.Violations.Enqueue(null!);                            // the pre-upload check
        h.PlanGate.Violations.Enqueue(DirectUploadHarness.OverQuota());  // the guarded re-check

        bool guardWasFree = false;
        h.Storage.OnDelete = async (_, _) =>
        {
            using CancellationTokenSource probe = new(TimeSpan.FromSeconds(2));
            try
            {
                await using OrganizationStorageGuard _ =
                    await storageLock.AcquireAsync(h.Db, h.Project.OrganizationId, probe.Token);
                guardWasFree = true;
            }
            catch (OperationCanceledException)
            {
                guardWasFree = false;
            }
        };

        UrlImportResult result = await h.Import("https://cdn.example.com/a.mp4");

        result.Refusal!.Code.Should().Be("STORAGE_QUOTA_EXCEEDED");
        h.Storage.DeletedKeys.Should().ContainSingle("the refusal still stores nothing");
        guardWasFree.Should().BeTrue("the cleanup delete runs after the organization guard is released");
    }

    /// <summary>
    /// SQCR-S-09: when the cleanup delete of a refused import fails, a durable mark is written, so the
    /// retention sweep removes the object instead of leaving an orphan in <c>userFiles</c> that nothing
    /// names.
    /// </summary>
    [Fact]
    public async Task A_refused_import_whose_cleanup_delete_fails_leaves_a_durable_mark()
    {
        UrlImportHarness h = new();
        h.Upstream.Serve("https://cdn.example.com/a.mp4", Clip, declaredLength: null);
        h.PlanGate.Violations.Enqueue(null!);                            // the S-06 headroom probe
        h.PlanGate.Violations.Enqueue(null!);                            // the pre-upload check
        h.PlanGate.Violations.Enqueue(DirectUploadHarness.OverQuota());  // the guarded re-check
        h.Storage.OnDelete = (_, _) => throw new IOException("the object store is unreachable");

        UrlImportResult result = await h.Import("https://cdn.example.com/a.mp4");

        result.Refusal!.Code.Should().Be("STORAGE_QUOTA_EXCEEDED");
        h.Storage.DeletedKeys.Should().ContainSingle("the delete was attempted");
        StorageRetentionMark mark = h.Db.StorageRetentionMarks.Should().ContainSingle().Subject;
        mark.StorageKey.Should().Be(h.Storage.Uploads[0].Key);
        mark.Category.Should().Be("userFiles");
        mark.PurgedAt.Should().BeNull("the sweep retries an unpurged mark");
    }

    /// <summary>
    /// T-106 follow-up: every failure after the object exists deletes it. Taking the guard can throw (a
    /// connection, a cancellation) and so can the re-check (a plan gate that is unreachable) - neither may
    /// leave the object the upload already wrote behind.
    /// </summary>
    [Fact]
    public async Task A_recheck_that_throws_still_removes_the_object_the_upload_wrote()
    {
        UrlImportHarness h = new();
        h.Upstream.Serve("https://cdn.example.com/a.mp4", Clip, declaredLength: null);
        h.PlanGate.Failures.Enqueue(null);   // the S-06 headroom probe
        h.PlanGate.Failures.Enqueue(null);   // the pre-upload check
        h.PlanGate.Failures.Enqueue(new InvalidOperationException("the plan gate is unreachable"));

        await FluentActions.Awaiting(() => h.Import("https://cdn.example.com/a.mp4"))
            .Should().ThrowAsync<InvalidOperationException>();

        h.Storage.Uploads.Should().ContainSingle("the upload precedes the guarded re-check");
        h.Storage.DeletedKeys.Should().ContainSingle().Which.Should().Be(h.Storage.Uploads[0].Key, "the failed re-check leaves no orphan");
        h.NothingStored.Should().BeTrue();
    }

    [Fact]
    public async Task A_storage_failure_mid_write_deletes_the_partial_object_and_records_nothing()
    {
        UrlImportHarness h = new();
        h.Upstream.Serve("https://cdn.example.com/a.mp4", Clip);
        h.Storage.FailAfterBytes = 100;

        UrlImportResult result = await h.Import("https://cdn.example.com/a.mp4");

        result.Refusal!.Code.Should().Be(UrlImportCodes.Failed);
        h.Storage.DeletedKeys.Should().ContainSingle().Which.Should().Be(h.Storage.Uploads.Single().Key);
        h.NothingStored.Should().BeTrue();
    }

    // ---------------------------------------------------------------- upstream failures

    [Theory]
    [InlineData(HttpStatusCode.NotFound, 404)]
    [InlineData(HttpStatusCode.Forbidden, 403)]
    [InlineData(HttpStatusCode.BadGateway, 502)]
    public async Task An_upstream_error_is_IMPORT_FAILED_with_its_status_and_never_a_gateway_status(HttpStatusCode upstream, int expected)
    {
        UrlImportHarness h = new();
        h.Upstream.Status("https://cdn.example.com/a.mp4", upstream);

        UrlImportResult result = await h.Import("https://cdn.example.com/a.mp4");

        result.Refusal!.Code.Should().Be(UrlImportCodes.Failed);
        result.Refusal.UpstreamStatus.Should().Be(expected);
        result.Refusal.StatusCode.Should().Be(422);
        h.NothingStored.Should().BeTrue();
    }

    [Fact]
    public async Task A_host_that_resolves_to_a_private_address_at_connect_time_is_IMPORT_URL_REFUSED()
    {
        UrlImportHarness h = new();
        h.Upstream.Routes["https://rebind.example.com/a.mp4"] = (_, _) => throw new HttpRequestException("connect failed", new ReelBolt.Shared.Inference.NonPublicAddressException());

        UrlImportResult result = await h.Import("https://rebind.example.com/a.mp4");

        result.Refusal!.Code.Should().Be(UrlImportCodes.UrlRefused);
        h.NothingStored.Should().BeTrue();
    }

    [Fact]
    public async Task An_upstream_that_stalls_past_the_timeout_is_IMPORT_TIMEOUT()
    {
        UrlImportHarness h = new(timeoutSeconds: 0.2);
        h.Upstream.Routes["https://slow.example.com/a.mp4"] = async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        };

        UrlImportResult result = await h.Import("https://slow.example.com/a.mp4");

        result.Refusal!.Code.Should().Be(UrlImportCodes.Timeout);
        result.Refusal.StatusCode.Should().Be(422);
        h.NothingStored.Should().BeTrue();
    }

    [Fact]
    public async Task A_cancelled_caller_is_not_reported_as_a_timeout()
    {
        UrlImportHarness h = new();
        h.Upstream.Routes["https://slow.example.com/a.mp4"] = async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        };
        using CancellationTokenSource cts = new(TimeSpan.FromMilliseconds(100));

        Func<Task> act = () => h.Service.ImportAsync(h.Project, h.UserId, "https://slow.example.com/a.mp4", null, null, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ---------------------------------------------------------------- success

    [Fact]
    public async Task A_public_url_creates_one_userFiles_row_with_the_mime_of_its_extension()
    {
        UrlImportHarness h = new();
        h.Upstream.Serve("https://cdn.example.com/media/Soundtrack%20v2.mp3?X-Amz-Signature=secret", Clip, contentType: "application/octet-stream");

        UrlImportResult result = await h.Import("https://cdn.example.com/media/Soundtrack%20v2.mp3?X-Amz-Signature=secret", directoryPath: "audio");

        ProjectFile file = h.Db.ProjectFiles.Should().ContainSingle().Subject;
        result.File!.Id.Should().Be(file.Id);
        file.ProjectId.Should().Be(h.Project.Id);
        file.Category.Should().Be("userFiles");
        file.OriginalFileName.Should().Be("Soundtrack v2.mp3");
        file.DirectoryPath.Should().Be("audio");
        file.OriginalPath.Should().Be("audio/Soundtrack v2.mp3");
        file.MimeType.Should().Be("audio/mpeg");
        file.SizeBytes.Should().Be(Clip.LongLength);
        file.SummaryStatus.Should().Be(SummaryStatus.Done, "media never reaches the summarizer (R22)");
        file.IndexingStatus.Should().Be(FileIndexingStatus.NotIndexed);
        file.StorageKey.Should().StartWith($"projects/{h.Project.Id}/userFiles/audio/");
        h.Storage.Objects[file.StorageKey].Should().Equal(Clip);
        h.Effects.Runs.Should().Equal((file.Id, h.UserId));
        // Three questions now: the S-06 headroom probe (capped at MaxBytes), the pre-upload refusal on the
        // bytes actually received, and the authoritative check the same size gets under the storage guard
        // just before the row is written (T-106 finding 1). The last two ask about the same size.
        h.PlanGate.UploadChecks.Should().Equal(h.Options.MaxBytes, Clip.LongLength, Clip.LongLength);
    }

    [Fact]
    public async Task The_given_file_name_wins_and_a_name_without_a_known_extension_takes_the_response_type()
    {
        UrlImportHarness h = new();
        h.Upstream.Serve("https://cdn.example.com/download?id=7", Clip, contentType: "image/png");

        UrlImportResult result = await h.Import("https://cdn.example.com/download?id=7", fileName: "plate");

        result.File!.OriginalFileName.Should().Be("plate");
        result.File.MimeType.Should().Be("image/png");
        result.File.SummaryStatus.Should().Be(SummaryStatus.Pending, "a non-media file is summarized and indexed like any upload");
    }

    [Fact]
    public async Task No_extension_and_no_content_type_is_octet_stream()
    {
        UrlImportHarness h = new();
        h.Upstream.Serve("https://cdn.example.com/blob", Clip, contentType: null);

        UrlImportResult result = await h.Import("https://cdn.example.com/blob");

        result.File!.MimeType.Should().Be("application/octet-stream");
    }

    [Fact]
    public async Task No_cookie_or_authorization_header_is_sent()
    {
        UrlImportHarness h = new();
        h.Upstream.Serve("https://cdn.example.com/a.mp4", Clip);

        await h.Import("https://cdn.example.com/a.mp4");

        HttpRequestMessage request = h.Upstream.Requests.Single();
        request.Method.Should().Be(HttpMethod.Get);
        request.Headers.Authorization.Should().BeNull();
        request.Headers.Contains("Cookie").Should().BeFalse();
    }

    /// <summary>S-08: every hop is marked so its OTel client span exports only the origin.</summary>
    [Fact]
    public async Task Every_request_is_marked_for_origin_only_telemetry()
    {
        UrlImportHarness h = new();
        h.Upstream.Redirect("https://cdn.example.com/a.mp4?sig=x", "https://media.example.org/b/a.mp4?sig=y");
        h.Upstream.Serve("https://media.example.org/b/a.mp4?sig=y", Clip);

        (await h.Import("https://cdn.example.com/a.mp4?sig=x")).File.Should().NotBeNull();

        h.Upstream.Requests.Select(r => r.Options.TryGetValue(ReelBolt.Shared.Observability.ReelBoltTelemetry.OriginOnlyUrl, out string? o) ? o : null)
            .Should().Equal("https://cdn.example.com", "https://media.example.org");
    }

    [Fact]
    public async Task Logs_carry_scheme_and_host_but_never_the_path_or_query()
    {
        UrlImportHarness h = new();
        const string signed = "https://cdn.example.com/private/path/clip.mp4?X-Amz-Signature=topsecret&token=abc";
        h.Upstream.Serve(signed, Clip);
        h.Upstream.Redirect("https://cdn.example.com/redir/hidden?sig=hush", "https://10.0.0.1/inside?sig=hush2");
        h.Upstream.Status("https://cdn.example.com/missing/secretpath?sig=gone", HttpStatusCode.NotFound);

        await h.Import(signed);
        await h.Import("https://cdn.example.com/redir/hidden?sig=hush");
        await h.Import("https://cdn.example.com/missing/secretpath?sig=gone");

        h.Logger.Messages.Should().NotBeEmpty();
        h.Logger.Messages.Should().Contain(m => m.Contains("https://cdn.example.com"));
        foreach (string secret in new[] { "topsecret", "token=abc", "private/path", "sig=", "hush", "secretpath", "/inside" })
            h.Logger.Messages.Should().NotContain(m => m.Contains(secret), $"\"{secret}\" is part of a path or query");
    }
}
