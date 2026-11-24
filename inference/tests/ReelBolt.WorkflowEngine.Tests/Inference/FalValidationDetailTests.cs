#nullable enable
using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.Shared.Inference;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Inference;

/// <summary>
/// magic-blur-fetch-fix: the prod failure read only "fal.ai returned HTTP 422 fetching the result of
/// 'fal-ai/sam-3-1/video-rle'", so the cause (fal could not download the clip) needed the fal dashboard.
/// The error now names the field, type and the vendor's sentence, and never the URL fal echoes back in
/// <c>input</c> (a presigned bearer URL to the customer's footage). No live call: a stub handler answers.
/// </summary>
public class FalValidationDetailTests
{
    private const string PresignedUrl =
        "https://acct.eu.r2.cloudflarestorage.com/reelbolt/projects/p/userFiles/c.mp4?X-Amz-Expires=600&X-Amz-Signature=deadbeef";

    /// <summary>The prod 422 body's shape (fal request 01a19d50..., 2026-11-02), our URL in <c>input</c>.</summary>
    private const string DownloadErrorBody =
        "{\"detail\":[{\"loc\":[\"body\",\"video_url\"],\"msg\":\"Failed to download the file. Please check if the URL is accessible and try again.\"," +
        "\"type\":\"file_download_error\",\"input\":\"" + PresignedUrl + "\",\"url\":\"https://docs.fal.ai/errors#file_download_error\"," +
        "\"ctx\":{\"url\":\"" + PresignedUrl + "\"}}]}";

    [Fact]
    public void The_download_error_names_field_type_and_sentence_but_no_url()
    {
        string detail = FalQueueClient.VendorValidationDetail(DownloadErrorBody);

        detail.Should().Be(
            "body.video_url file_download_error (Failed to download the file. Please check if the URL is accessible and try again.)");
        detail.Should().NotContain("r2.cloudflarestorage").And.NotContain("X-Amz").And.NotContain("deadbeef");
    }

    [Fact]
    public void A_url_inside_the_sentence_is_masked()
    {
        string body = "{\"detail\":[{\"loc\":[\"body\",\"video_url\"],\"msg\":\"Could not GET " + PresignedUrl + " (403)\",\"type\":\"file_download_error\"}]}";

        FalQueueClient.VendorValidationDetail(body).Should()
            .Be("body.video_url file_download_error (Could not GET <url> (403))");
    }

    [Fact]
    public void A_scheme_less_signed_url_and_a_url_in_type_are_masked()
    {
        string body = "{\"detail\":[{\"loc\":[\"body\",\"video_url\"],\"msg\":\"could not read acct.r2.cloudflarestorage.com/b/c.mp4?X-Amz-Signature=deadbeef now\"," +
            "\"type\":\"https://evil.example/t\"}]}";

        string detail = FalQueueClient.VendorValidationDetail(body);

        detail.Should().Be("body.video_url <url> (could not read <url> now)");
        detail.Should().NotContain("deadbeef").And.NotContain("evil.example");
    }

    [Fact]
    public void A_loc_element_that_is_not_a_string_or_number_is_skipped_not_thrown()
    {
        string body = "{\"detail\":[{\"loc\":[\"body\",true,{\"x\":1},[2],\"video_url\",null],\"msg\":\"bad\",\"type\":\"value_error\"}]}";

        FalQueueClient.VendorValidationDetail(body).Should().Be("body.video_url value_error (bad)");
    }

    [Fact]
    public void Several_entries_are_bounded()
    {
        string entry = "{\"loc\":[\"body\",\"x\",0],\"msg\":\"bad\",\"type\":\"value_error\"}";
        string body = "{\"detail\":[" + string.Join(',', entry, entry, entry, entry, entry) + "]}";

        FalQueueClient.VendorValidationDetail(body).Should()
            .Be("body.x.0 value_error (bad); body.x.0 value_error (bad); body.x.0 value_error (bad); +2 more");
    }

    [Theory]
    [InlineData("{\"detail\":\"Unauthorized\"}", "Unauthorized")]
    [InlineData("{\"error\":\"see https://evil.example/x?token=1\"}", "see <url>")]
    [InlineData("<html>gateway</html>", "")]
    [InlineData("", "")]
    [InlineData("[1,2]", "")]
    [InlineData("{\"detail\":[1,true]}", "")]
    public void Other_bodies_fall_back_to_the_vendor_sentence(string body, string expected)
    {
        FalQueueClient.VendorValidationDetail(body).Should().Be(expected);
    }

    private sealed class Stub(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            });
    }

    [Fact]
    public async Task A_refused_result_fetch_carries_the_detail_into_the_step_error()
    {
        FalQueueClient queue = new(
            new HttpClient(new Stub(HttpStatusCode.UnprocessableEntity, DownloadErrorBody)), "k", null, null, null, null);

        Func<Task> fetch = () => queue.FetchResultAsync(
            "https://queue.fal.run/fal-ai/sam-3-1/requests/r", "fal-ai/sam-3-1/video-rle", CancellationToken.None);

        (await fetch.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should()
            .Be("fal.ai returned HTTP 422 fetching the result of 'fal-ai/sam-3-1/video-rle': body.video_url file_download_error " +
                "(Failed to download the file. Please check if the URL is accessible and try again.).")
            .And.NotContain("X-Amz");
    }

    [Fact]
    public async Task A_refusal_with_no_readable_detail_keeps_the_old_sentence()
    {
        FalQueueClient queue = new(
            new HttpClient(new Stub(HttpStatusCode.BadGateway, "<html>bad gateway</html>")), "k", null, null, null, null);

        Func<Task> fetch = () => queue.FetchResultAsync(
            "https://queue.fal.run/fal-ai/sam-3-1/requests/r", "fal-ai/sam-3-1/video-rle", CancellationToken.None);

        (await fetch.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should()
            .Be("fal.ai returned HTTP 502 fetching the result of 'fal-ai/sam-3-1/video-rle'.");
    }
}
