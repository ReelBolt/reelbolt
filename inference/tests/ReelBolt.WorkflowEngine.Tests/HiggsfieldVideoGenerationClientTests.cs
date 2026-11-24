using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Pins the shipped Higgsfield wire dialect, entirely offline.
/// <para>
/// There is no live Higgsfield credential anywhere and none is to be requested: the credential is a
/// runtime, admin-entered value on the provider row, never a build or test prerequisite. Every test
/// below therefore drives the client through an <see cref="HttpMessageHandler"/> stub, and the
/// <c>env:</c> branch is exercised through the <c>environmentLookup</c> delegate seam rather than by
/// mutating the process environment (a process-global write is racy under xUnit's parallel
/// collections, and a test that reads a real variable would pass or fail depending on the machine).
/// </para>
/// </summary>
public class HiggsfieldVideoGenerationClientTests
{
    private const string BaseUrl = "https://api.higgsfield.ai";
    private const string KeyId = "hf-key-id-abc";
    private const string KeySecret = "hf-key-secret-xyz";

    /// <summary>Records every request the client sends; the response comes from <c>respond</c>.</summary>
    private sealed class RecordingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, string Auth, string Body)> Requests { get; } = new();

        /// <summary>
        /// The URI as HttpClient finally resolved it — the only thing that can prove which HOST a
        /// request went to, which is exactly what an authority-override bug would change.
        /// </summary>
        public List<Uri> ResolvedUris { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            ResolvedUris.Add(request.RequestUri!);
            Requests.Add((
                request.Method,
                request.RequestUri!.AbsolutePath,
                request.Headers.Authorization?.ToString() ?? string.Empty,
                body));
            return await respond(request, cancellationToken);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string body) =>
        new(code) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private static (HiggsfieldVideoGenerationClient Client, RecordingHandler Handler) ClientThatSendsNothing(
        string credential = KeyId + ":" + KeySecret)
    {
        RecordingHandler handler = new((_, _) =>
            throw new InvalidOperationException("No HTTP request was expected, but one was sent."));
        HttpClient http = new(handler) { BaseAddress = new Uri(BaseUrl) };
        return (new HiggsfieldVideoGenerationClient(http, credential), handler);
    }

    private static (HiggsfieldVideoGenerationClient Client, RecordingHandler Handler) ClientFor(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond,
        string credential = KeyId + ":" + KeySecret,
        TimeSpan? probeTimeout = null)
    {
        RecordingHandler handler = new(respond);
        HttpClient http = new(handler) { BaseAddress = new Uri(BaseUrl) };
        return (new HiggsfieldVideoGenerationClient(http, credential, probeTimeout), handler);
    }

    private static (HiggsfieldVideoGenerationClient Client, RecordingHandler Handler) ClientReturning(
        HttpStatusCode code, string body) =>
        ClientFor((_, _) => Task.FromResult(Json(code, body)));

    private static VideoGenerationRequest SampleRequest(
        string model = "kling-video/v3.0/std/text-to-video",
        IReadOnlyList<VideoGenerationImageInput>? images = null) =>
        new(
            Model: model,
            PromptText: "A lighthouse at dusk",
            DurationSeconds: 5,
            Resolution: "1080P",
            Ratio: "16:9",
            Images: images);

    /// <summary><see cref="ClientThatSendsNothing"/> but every request is recorded first.</summary>
    private static (HiggsfieldVideoGenerationClient Client, RecordingHandler Handler) ClientThatRecordsEverything(
        string credential = KeyId + ":" + KeySecret)
    {
        RecordingHandler handler = new((_, _) =>
            throw new InvalidOperationException("No HTTP request was expected, but one was sent."));
        HttpClient http = new(handler) { BaseAddress = new Uri(BaseUrl) };
        return (new HiggsfieldVideoGenerationClient(http, credential), handler);
    }

    /// <summary>An HttpClient whose BaseAddress is set but which refuses every request.</summary>
    private static HttpClient UnusedHttpClient() => new(ClientThatSendsNothing().Handler) { BaseAddress = new Uri(BaseUrl) };

    // --- Credentials: ResolveCredentials and the empty-key throw ---

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no-colon-here")]
    [InlineData("id:")]
    [InlineData(":secret")]
    [InlineData("   :secret   ")]
    public void ResolveCredentials_throws_for_a_credential_that_is_not_a_usable_pair(string? storedCredential)
    {
        Action act = () => HiggsfieldVideoGenerationClient.ResolveCredentials(storedCredential!);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ResolveCredentials_returns_the_pair_for_id_colon_secret()
    {
        (string keyId, string keySecret) = HiggsfieldVideoGenerationClient.ResolveCredentials("id:secret");

        keyId.Should().Be("id");
        keySecret.Should().Be("secret");
    }

    [Fact]
    public void ResolveCredentials_splits_on_the_FIRST_colon_so_a_secret_may_contain_one()
    {
        // A secret is opaque and may legitimately carry a colon; splitting on the last one would
        // truncate the key id instead, and the vendor would reject the credential.
        (string keyId, string keySecret) = HiggsfieldVideoGenerationClient.ResolveCredentials("id:sec:ret");

        keyId.Should().Be("id");
        keySecret.Should().Be("sec:ret");
    }

    [Fact]
    public void ResolveCredentials_env_sentinel_reads_HF_CREDENTIALS_first()
    {
        var seen = new List<string>();

        (string keyId, string keySecret) = HiggsfieldVideoGenerationClient.ResolveCredentials(
            HiggsfieldVideoGenerationClient.EnvironmentSentinel,
            name =>
            {
                seen.Add(name);
                return name switch
                {
                    "HF_CREDENTIALS" => "env-id:env-secret",
                    // Deliberately also set, to prove HF_CREDENTIALS WINS rather than merely being read.
                    "HF_API_KEY" => "ignored-id",
                    "HF_API_SECRET" => "ignored-secret",
                    _ => null
                };
            });

        keyId.Should().Be("env-id");
        keySecret.Should().Be("env-secret");
        seen.Should().Contain("HF_CREDENTIALS");
        seen.Should().NotContain("HF_API_KEY");
    }

    [Fact]
    public void ResolveCredentials_env_sentinel_falls_back_to_HF_API_KEY_and_HF_API_SECRET()
    {
        (string keyId, string keySecret) = HiggsfieldVideoGenerationClient.ResolveCredentials(
            HiggsfieldVideoGenerationClient.EnvironmentSentinel,
            name => name switch
            {
                "HF_API_KEY" => "split-id",
                "HF_API_SECRET" => "split-secret",
                _ => null
            });

        keyId.Should().Be("split-id");
        keySecret.Should().Be("split-secret");
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData("", "", "")]
    [InlineData("   ", "   ", "   ")]
    [InlineData(null, "only-id", null)]
    [InlineData(null, null, "only-secret")]
    [InlineData(null, "   ", "secret")]
    [InlineData("env-colonless", null, null)]
    public void ResolveCredentials_env_sentinel_with_no_usable_environment_throws_and_never_falls_back(
        string? credentials, string? apiKey, string? apiSecret)
    {
        // "No ambient fallback": the sentinel is the ONLY path that reads the environment at all, and
        // when it finds nothing usable it throws instead of silently resolving something else. This
        // test asserts a throw, never a fallback — and it passes no real environment source, so it
        // cannot accidentally succeed from the machine's own variables.
        Action act = () => HiggsfieldVideoGenerationClient.ResolveCredentials(
            HiggsfieldVideoGenerationClient.EnvironmentSentinel,
            name => name switch
            {
                "HF_CREDENTIALS" => credentials,
                "HF_API_KEY" => apiKey,
                "HF_API_SECRET" => apiSecret,
                _ => null
            });

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no-colon-here")]
    [InlineData("id:")]
    public void Constructing_the_client_with_an_unusable_credential_throws(string storedCredential)
    {
        Action act = () => new HiggsfieldVideoGenerationClient(UnusedHttpClient(), storedCredential);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructing_the_client_with_a_null_HttpClient_throws()
    {
        Action act = () => new HiggsfieldVideoGenerationClient(null!, KeyId + ":" + KeySecret);

        act.Should().Throw<ArgumentNullException>();
    }

    // --- Redaction ---

    [Fact]
    public void ToString_contains_neither_half_of_the_credential()
    {
        var (client, _) = ClientThatSendsNothing();

        string rendered = client.ToString();

        rendered.Should().NotContain(KeyId);
        rendered.Should().NotContain(KeySecret);
    }

    // --- Describe: fail-closed, and offline ---

    private static readonly string[] NeverOfferedModelNames =
    {
        "kling-video/v3.0/std/text-to-video",
        "kling-video/v3.0/pro/image-to-video",
        "higgsfield-ai/soul/standard",
        "wan-video/2.2/text-to-video",
        "seedance-1-0-pro",
        "MiniMax-H3",
        "",
        "   ",
        "not-a-real-endpoint-id"
    };

    [Theory]
    [MemberData(nameof(NeverOfferedModelNamesData))]
    public void Describe_throws_NotSupportedException_for_every_model_name(string model)
    {
        var (client, handler) = ClientThatSendsNothing();

        Action act = () => client.Describe(model);

        act.Should().Throw<NotSupportedException>();
        // Fail-closed is also OFFLINE: an unpriced model cannot be discovered by asking the vendor.
        handler.Requests.Should().BeEmpty();
    }

    public static TheoryData<string> NeverOfferedModelNamesData()
    {
        TheoryData<string> data = new();
        foreach (string name in NeverOfferedModelNames)
        {
            data.Add(name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(NeverOfferedModelNamesData))]
    public void Describe_never_returns_a_capability_with_an_empty_or_non_positive_price_table(string model)
    {
        // The headline fail-closed criterion, written as an assertion that cannot be satisfied by a
        // returned capability at all: a Higgsfield step must fail closed, never generate for free.
        // If Describe ever starts RETURNING capabilities, it has to return priced ones — a
        // VideoGenerationCapabilities with an empty or zero/negative price table is the "$0" failure
        // mode VideoGenerationPricing exists to prevent.
        var (client, _) = ClientThatSendsNothing();

        VideoGenerationCapabilities? returned = null;
        try
        {
            returned = client.Describe(model);
        }
        catch (NotSupportedException)
        {
            // The verified current behaviour.
        }

        if (returned is not null)
        {
            returned.PricePerSecondByResolution.Should().NotBeEmpty(
                "an unpriced Higgsfield model must not be offered at all");
            returned.PricePerSecondByResolution.Values.Should().OnlyContain(price => price > 0m);
        }

        returned.Should().BeNull();
    }

    // --- Submit ---

    [Fact]
    public async Task SubmitAsync_POSTs_the_flat_documented_body_with_no_input_wrapper()
    {
        var (client, handler) = ClientFor((_, _) =>
            Task.FromResult(Json(HttpStatusCode.OK, """{"request_id":"req-777"}""")));

        ExternalJobHandle handle = await client.SubmitAsync(SampleRequest(), CancellationToken.None);

        handle.RemoteJobId.Should().Be("req-777");

        handler.Requests.Should().ContainSingle();
        (HttpMethod method, string path, string auth, string body) = handler.Requests[0];
        method.Should().Be(HttpMethod.Post);
        path.Should().Be("/kling-video/v3.0/std/text-to-video");
        // The vendor's two-part credential scheme, NOT a Bearer token.
        auth.Should().Be($"Key {KeyId}:{KeySecret}");

        // The exact body, asserted as a STRING rather than field-by-field, because the defect this
        // pins is a SHAPE defect: reading "prompt" out of the document would pass just as happily
        // with or without the wrapper. The vendor's own cURL for this endpoint prints
        //   --data '{ "prompt": "A slow cinematic tracking shot along a sunlit coastal road." }'
        // (https://docs.higgsfield.ai/docs/models/kling-3/standard-text-to-video.md) — flat, with no
        // "input" envelope. The wrapped shape came from the TypeScript snippet on that same page,
        // where "input" is a sibling of "withPolling" in the SDK's CLIENT-SIDE options object.
        body.Should().Be(
            """{"prompt":"A lighthouse at dusk","duration":5,"aspect_ratio":"16:9"}""",
            "the vendor's cURL documents a FLAT submit body with the model parameters at the TOP "
            + "LEVEL and NO 'input' wrapper; the wrapper was the TypeScript SDK's client-side option "
            + "('input' sits beside 'withPolling' in subscribe(endpoint, { input, withPolling }))");
    }

    [Theory]
    [InlineData("kling-video/v3.0/std/text-to-video", "/kling-video/v3.0/std/text-to-video")]
    [InlineData("/kling-video/v3.0/std/text-to-video", "/kling-video/v3.0/std/text-to-video")]
    [InlineData("///kling-video/v3.0/std/text-to-video", "/kling-video/v3.0/std/text-to-video")]
    public async Task SubmitAsync_normalises_leading_slashes_to_exactly_one(string model, string expectedPath)
    {
        var (client, handler) = ClientFor((_, _) =>
            Task.FromResult(Json(HttpStatusCode.OK, """{"request_id":"r"}""")));

        await client.SubmitAsync(SampleRequest(model), CancellationToken.None);

        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Path.Should().Be(expectedPath);
    }

    // A security fix that must stay pinned. request.Model is step-config-authored (an LLM-authored
    // or imported workflow can set it), not admin-only, and HttpClient treats a path beginning with
    // '//' as an AUTHORITY OVERRIDE — so an unvalidated "//evil.com/steal" resolves against the base
    // address to https://evil.com/steal carrying 'Authorization: Key <id>:<secret>', handing the
    // credential and the prompt to an attacker-controlled host. Two separate arms are pinned below:
    // the shapes that are REJECTED, and the shapes that are SANITISED onto the vendor host.
    [Theory]
    [InlineData("https://evil.example/steal")]
    [InlineData("http://evil.example/steal")]
    [InlineData("kling-video//v3.0/text-to-video")]
    [InlineData("kling-video/v3.0/../../admin")]
    [InlineData("..")]
    [InlineData("../etc/passwd")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/")]
    [InlineData("//")]
    public async Task SubmitAsync_rejects_a_model_that_is_not_a_relative_endpoint_id(string model)
    {
        var (client, handler) = ClientThatSendsNothing();

        Func<Task> act = async () => await client.SubmitAsync(SampleRequest(model), CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
        handler.Requests.Should().BeEmpty("the endpoint id is validated before a request is built");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/")]
    [InlineData("//")]
    [InlineData("https://evil.example/steal")]
    [InlineData("http://evil.example/steal")]
    [InlineData("kling-video//v3.0/text-to-video")]
    [InlineData("..")]
    [InlineData("../etc/passwd")]
    [InlineData("kling-video/v3.0/../../admin")]
    public async Task SubmitAsync_rejects_the_endpoint_id_before_uploading_a_supplied_image(string model)
    {
        // The ordering pin, and the reason this case exists separately from the no-images test above.
        // SubmitAsync has TWO egress legs: the presigned upload of the user's own frame bytes, and the
        // endpoint-id POST that submits the job. Everything else in this file supplies no Images, so
        // the upload loop never runs and those tests would stay GREEN even if NormalizeEndpointId were
        // moved below the loop. This one supplies a real image, so it fails the moment that ordering
        // regresses.
        //
        // What it pins: the Endpoint ID is validated BEFORE anything leaves the process. A submission
        // that can never be addressed must not spend the user's egress consent by PUTting their frame
        // bytes to the vendor's CDN — an upload is user data leaving the host, not an internal
        // request, and the vendor's presigned URL is a one-way door (it cannot be un-sent).
        var (client, handler) = ClientThatRecordsEverything();

        VideoGenerationImageInput frame = new("image/jpeg", new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10 });
        VideoGenerationRequest request = SampleRequest(model, images: new[] { frame });

        Func<Task> act = async () => await client.SubmitAsync(request, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
        // Zero requests of ANY kind: neither the presign POST nor the storage PUT nor the endpoint-id
        // POST. A handler that only refused requests could not tell "nothing was sent" apart from
        // "something was sent and then failed", so this one records first and asserts on the record.
        handler.Requests.Should().BeEmpty(
            "the endpoint id is validated before the image upload leg, so a rejected model never " +
            "uploads the user's frame bytes: " +
            string.Join(", ", handler.Requests.Select(r => $"{r.Method} {r.Path}")));
    }

    [Theory]
    [InlineData("//evil.example/steal", "/evil.example/steal")]
    [InlineData("///evil.example/steal", "/evil.example/steal")]
    [InlineData("evil.example/steal", "/evil.example/steal")]
    public async Task SubmitAsync_sanitises_a_hostile_model_onto_the_vendor_host(
        string model, string expectedPath)
    {
        // "//evil.example/steal" keeps its '//' only until the leading slashes are stripped; the
        // remainder carries no '//' or '://', so it is a perfectly valid relative endpoint id and is
        // ACCEPTED — which is safe only because exactly one leading slash is then prefixed. The
        // assertion that matters is the HOST: the request must still go to the configured vendor, and
        // never to the host the model names.
        var (client, handler) = ClientThatSendsNothing();

        Func<Task> act = async () => await client.SubmitAsync(SampleRequest(model), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        handler.Requests.Should().ContainSingle();
        (HttpMethod method, string path, string auth, _) = handler.Requests[0];
        method.Should().Be(HttpMethod.Post);
        path.Should().Be(expectedPath);

        // The recording handler sees the resolved absolute URI; it must still be the vendor's.
        handler.ResolvedUris.Should().ContainSingle();
        handler.ResolvedUris[0].Host.Should().Be("api.higgsfield.ai");
        handler.ResolvedUris[0].Scheme.Should().Be("https");
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"request_id":""}""")]
    [InlineData("""{"request_id":null}""")]
    [InlineData("""{"request_id":123}""")]
    [InlineData("[]")]
    public async Task SubmitAsync_throws_when_the_response_carries_no_usable_request_id(string body)
    {
        var (client, _) = ClientReturning(HttpStatusCode.OK, body);

        Func<Task> act = async () => await client.SubmitAsync(SampleRequest(), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // --- Image input: the documented FLAT shape, per verified endpoint -----------------------------
    //
    // The image-input model page's own cURL is the primary source (fetched and re-read 2026-10-09,
    // https://docs.higgsfield.ai/docs/models/kling-3/standard-image-to-video.md):
    //
    //   curl --request POST \
    //     --url https://api.higgsfield.ai/kling-video/v3.0/std/image-to-video \
    //     --data '{ "prompt": "A slow cinematic tracking shot…",
    //               "image_url": "https://example.com/first-frame.jpg" }'
    //
    // with image_url a REQUIRED string and last_image_url an optional string — FLAT top-level members,
    // NOT the {"type","image_url"} array this client used to send (that array is the vendor's
    // JavaScript SDK options surface, the same client-side surface whose "input" wrapper was already
    // corrected above). The page documents no aspect_ratio, so none is sent with an image. The vendor
    // is explicit that "Endpoint fields vary by model and workflow"
    // (https://docs.higgsfield.ai/docs/models), so the shape is mapped per endpoint: the four Kling
    // 3.0 image-to-video endpoints, of which only the turbo one lacks last_image_url.

    [Theory]
    [InlineData("kling-video/v3.0/std/image-to-video", VideoImageInputSupport.FirstAndLastFrame)]
    [InlineData("kling-video/v3.0/pro/image-to-video", VideoImageInputSupport.FirstAndLastFrame)]
    [InlineData("kling-video/v3.0/4k/image-to-video", VideoImageInputSupport.FirstAndLastFrame)]
    [InlineData("kling-video/v3.0-turbo/image-to-video", VideoImageInputSupport.FirstFrame)]
    [InlineData("kling-video/v3.0/std/text-to-video", VideoImageInputSupport.None)]
    [InlineData("kling-video/v3.0-turbo/text-to-video", VideoImageInputSupport.None)]
    [InlineData("wan-video/2.2/text-to-video", VideoImageInputSupport.None)]
    public void ImageInputSupport_is_answered_offline_from_the_verified_endpoint_mapping(
        string model, VideoImageInputSupport expected)
    {
        var (client, handler) = ClientThatSendsNothing();

        client.ImageInputSupport(model).Should().Be(expected);
        handler.Requests.Should().BeEmpty("the declaration is pure: it never asks the vendor");
    }

    [Fact]
    public async Task SubmitAsync_with_a_first_frame_sends_the_flat_image_url_and_no_aspect_ratio()
    {
        // The exact three-request sequence: presign, PUT the bytes, then the endpoint POST referencing
        // the returned public_url with the page's own flat member name.
        var (client, handler) = ClientFor((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/files/generate-upload-url")
            {
                return Task.FromResult(Json(HttpStatusCode.OK,
                    """{"public_url":"https://cdn.higgsfield.ai/pub-1.jpg","upload_url":"https://storage.higgsfield.ai/put-1?sig=x","upload_headers":{"Content-Type":"image/jpeg"},"content_type":"image/jpeg"}"""));
            }

            if (request.Method == HttpMethod.Put)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            }

            return Task.FromResult(Json(HttpStatusCode.OK, """{"request_id":"req-img"}"""));
        });

        VideoGenerationImageInput image = new("image/jpeg", new byte[] { 1, 2, 3, 4 });
        ExternalJobHandle handle = await client.SubmitAsync(
            SampleRequest("kling-video/v3.0/std/image-to-video", images: new[] { image }), CancellationToken.None);

        handle.RemoteJobId.Should().Be("req-img");
        handler.Requests.Should().HaveCount(3);
        handler.Requests[0].Method.Should().Be(HttpMethod.Post);
        handler.Requests[0].Path.Should().Be("/files/generate-upload-url");
        handler.Requests[1].Method.Should().Be(HttpMethod.Put);
        handler.Requests[2].Method.Should().Be(HttpMethod.Post);
        handler.Requests[2].Path.Should().Be("/kling-video/v3.0/std/image-to-video");

        // The exact body: flat, with the documented image member and WITHOUT aspect_ratio, which the
        // image-to-video page does not document at all.
        handler.Requests[2].Body.Should().Be(
            """{"prompt":"A lighthouse at dusk","duration":5,"image_url":"https://cdn.higgsfield.ai/pub-1.jpg"}""");
    }

    [Fact]
    public async Task SubmitAsync_with_a_first_and_a_last_frame_sends_image_url_and_last_image_url()
    {
        int presigns = 0;
        var (client, handler) = ClientFor((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/files/generate-upload-url")
            {
                presigns++;
                return Task.FromResult(Json(HttpStatusCode.OK,
                    $$"""{"public_url":"https://cdn.higgsfield.ai/pub-{{presigns}}.jpg","upload_url":"https://storage.higgsfield.ai/put-{{presigns}}?sig=x","upload_headers":{"Content-Type":"image/jpeg"},"content_type":"image/jpeg"}"""));
            }

            if (request.Method == HttpMethod.Put)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            }

            return Task.FromResult(Json(HttpStatusCode.OK, """{"request_id":"req-two"}"""));
        });

        VideoGenerationImageInput first = new("image/jpeg", new byte[] { 1, 2, 3, 4 });
        VideoGenerationImageInput last = new("image/jpeg", new byte[] { 5, 6, 7, 8 });
        await client.SubmitAsync(
            SampleRequest("kling-video/v3.0/pro/image-to-video", images: new[] { first, last }), CancellationToken.None);

        // presign + PUT per image, then the one POST: Images[0] is the first frame and Images[1] the
        // last, which is the only role information the request carries.
        presigns.Should().Be(2);
        handler.Requests.Should().HaveCount(5);
        handler.Requests[4].Path.Should().Be("/kling-video/v3.0/pro/image-to-video");
        handler.Requests[4].Body.Should().Be(
            """{"prompt":"A lighthouse at dusk","duration":5,"image_url":"https://cdn.higgsfield.ai/pub-1.jpg","last_image_url":"https://cdn.higgsfield.ai/pub-2.jpg"}""");
    }

    [Fact]
    public async Task SubmitAsync_refuses_an_image_on_a_text_to_video_endpoint_before_any_upload()
    {
        var (client, handler) = ClientThatRecordsEverything();
        VideoGenerationImageInput frame = new("image/jpeg", new byte[] { 1, 2, 3, 4 });

        Func<Task> act = () => client.SubmitAsync(
            SampleRequest("kling-video/v3.0/std/text-to-video", images: new[] { frame }), CancellationToken.None);

        // The endpoint's documented schema carries no image member at all, so the refusal names that
        // rather than uploading the caller's frame bytes to a request the vendor would reject.
        (await act.Should().ThrowAsync<NotSupportedException>()).WithMessage("*TEXT-TO-VIDEO*");
        handler.Requests.Should().BeEmpty(
            "an endpoint that cannot carry an image must not receive the frame bytes first: " +
            string.Join(", ", handler.Requests.Select(r => $"{r.Method} {r.Path}")));
    }

    [Fact]
    public async Task SubmitAsync_refuses_an_image_on_an_endpoint_whose_image_schema_is_not_verified()
    {
        var (client, handler) = ClientThatRecordsEverything();
        VideoGenerationImageInput frame = new("image/jpeg", new byte[] { 1, 2, 3, 4 });

        Func<Task> act = () => client.SubmitAsync(
            SampleRequest("wan-video/2.2/image-to-video", images: new[] { frame }), CancellationToken.None);

        // Fail-closed rather than a blind rename: the vendor states endpoint fields vary per model and
        // workflow, so an endpoint nobody has mapped is refused with the gap named, not guessed at.
        (await act.Should().ThrowAsync<NotSupportedException>()).WithMessage("*not verified*");
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task SubmitAsync_refuses_a_last_frame_on_the_endpoint_that_documents_none()
    {
        var (client, handler) = ClientThatRecordsEverything();
        VideoGenerationImageInput frame = new("image/jpeg", new byte[] { 1, 2, 3, 4 });

        Func<Task> act = () => client.SubmitAsync(
            SampleRequest("kling-video/v3.0-turbo/image-to-video", images: new[] { frame, frame }), CancellationToken.None);

        // The turbo image-to-video page documents image_url and no last_image_url, so the second image
        // is refused rather than sent to a field the vendor never published.
        (await act.Should().ThrowAsync<NotSupportedException>()).WithMessage("*first frame only*");
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task SubmitAsync_with_no_image_keeps_the_legacy_body_on_an_image_to_video_endpoint()
    {
        // The no-image path is untouched by the per-endpoint mapping, on EVERY endpoint: the body is
        // exactly what this client has always sent (the endpoint's own required image_url is then
        // missing and the vendor answers 4xx, exactly as it did before this change).
        var (client, handler) = ClientFor((_, _) =>
            Task.FromResult(Json(HttpStatusCode.OK, """{"request_id":"r"}""")));

        await client.SubmitAsync(
            SampleRequest("kling-video/v3.0/std/image-to-video"), CancellationToken.None);

        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Body.Should().Be(
            """{"prompt":"A lighthouse at dusk","duration":5,"aspect_ratio":"16:9"}""");
    }

    [Theory]
    [InlineData(null)]
    [InlineData(new object[] { new string[0] })]
    public async Task SubmitAsync_with_no_images_omits_the_input_images_key_entirely(object? images)
    {
        IReadOnlyList<VideoGenerationImageInput>? supplied = images switch
        {
            null => null,
            string[] => Array.Empty<VideoGenerationImageInput>(),
            _ => throw new InvalidOperationException("unexpected test data")
        };

        var (client, handler) = ClientFor((_, _) =>
            Task.FromResult(Json(HttpStatusCode.OK, """{"request_id":"r"}""")));

        await client.SubmitAsync(SampleRequest(images: supplied), CancellationToken.None);

        handler.Requests.Should().ContainSingle();
        using JsonDocument doc = JsonDocument.Parse(handler.Requests[0].Body);
        doc.RootElement.TryGetProperty("input_images", out _)
            .Should().BeFalse("an absent image list must not send an empty input_images array");
    }

    // --- Poll ---

    [Theory]
    [InlineData("queued", ExternalJobState.Queued)]
    [InlineData("in_progress", ExternalJobState.Running)]
    [InlineData("completed", ExternalJobState.Succeeded)]
    [InlineData("failed", ExternalJobState.Failed)]
    [InlineData("nsfw", ExternalJobState.Failed)]
    [InlineData("canceled", ExternalJobState.Cancelled)]
    public async Task PollAsync_maps_every_verified_lowercase_status_and_GETs_the_status_endpoint(
        string vendorStatus, ExternalJobState expected)
    {
        string body = vendorStatus == "completed"
            ? """{"status":"completed","video":{"url":"https://cdn.higgsfield.ai/out.mp4"}}"""
            : $$"""{"status":"{{vendorStatus}}"}""";
        var (client, handler) = ClientReturning(HttpStatusCode.OK, body);

        ExternalJobStatus status = await client.PollAsync(new ExternalJobHandle("req-1"), CancellationToken.None);

        status.State.Should().Be(expected);
        status.UsageTotalSeconds.Should().BeNull();

        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Method.Should().Be(HttpMethod.Get);
        handler.Requests[0].Path.Should().Be("/requests/req-1/status");
    }

    [Theory]
    [InlineData("Queued")]
    [InlineData("Completed")]
    [InlineData("IN_PROGRESS")]
    [InlineData("bogus")]
    [InlineData("succeeded")]
    public async Task PollAsync_throws_NotSupportedException_for_an_unrecognised_status(string vendorStatus)
    {
        // Case-sensitivity is load-bearing: the mapper is an exhaustive switch over the six verified
        // literals, not a case-insensitive lookup with a silent default. "Queued" is the casing the
        // vendor does NOT use, and it must throw rather than quietly become Queued.
        var (client, _) = ClientReturning(HttpStatusCode.OK, $$"""{"status":"{{vendorStatus}}"}""");

        Func<Task> act = async () => await client.PollAsync(new ExternalJobHandle("req-1"), CancellationToken.None);

        await act.Should().ThrowAsync<NotSupportedException>();
    }

    [Fact]
    public async Task PollAsync_throws_when_a_completed_response_carries_no_video()
    {
        var (client, _) = ClientReturning(HttpStatusCode.OK, """{"status":"completed"}""");

        Func<Task> act = async () => await client.PollAsync(new ExternalJobHandle("req-1"), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Theory]
    [InlineData("""{"status":"completed","video":{}}""")]
    [InlineData("""{"status":"completed","video":{"url":""}}""")]
    [InlineData("""{"status":"completed","video":{"url":null}}""")]
    [InlineData("""{"status":"completed","video":"https://cdn.higgsfield.ai/out.mp4"}""")]
    public async Task PollAsync_throws_when_a_completed_response_has_no_usable_video_url(string body)
    {
        var (client, _) = ClientReturning(HttpStatusCode.OK, body);

        Func<Task> act = async () => await client.PollAsync(new ExternalJobHandle("req-1"), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task PollAsync_completed_yields_the_video_url()
    {
        var (client, _) = ClientReturning(HttpStatusCode.OK,
            """{"status":"completed","video":{"url":"https://cdn.higgsfield.ai/out.mp4"}}""");

        ExternalJobStatus status = await client.PollAsync(new ExternalJobHandle("req-1"), CancellationToken.None);

        status.State.Should().Be(ExternalJobState.Succeeded);
        status.DownloadUrl.Should().Be("https://cdn.higgsfield.ai/out.mp4");
        status.FailureReason.Should().BeNull();
    }

    [Fact]
    public async Task PollAsync_failed_surfaces_the_top_level_error_string()
    {
        var (client, _) = ClientReturning(HttpStatusCode.OK,
            """{"status":"failed","error":"Model refused the prompt"}""");

        ExternalJobStatus status = await client.PollAsync(new ExternalJobHandle("req-1"), CancellationToken.None);

        status.State.Should().Be(ExternalJobState.Failed);
        status.DownloadUrl.Should().BeNull();
        status.FailureReason.Should().Be("Model refused the prompt");
    }

    [Fact]
    public async Task PollAsync_nsfw_maps_to_Failed_with_a_reason_naming_moderation()
    {
        // nsfw is a terminal, non-billable moderation rejection with credits refunded. ExternalJobState
        // has no Nsfw member, so the distinction has to ride in FailureReason or it is lost — and an
        // operator seeing a bare Failed would reasonably re-run the same rejected prompt.
        var (client, _) = ClientReturning(HttpStatusCode.OK, """{"status":"nsfw"}""");

        ExternalJobStatus status = await client.PollAsync(new ExternalJobHandle("req-1"), CancellationToken.None);

        status.State.Should().Be(ExternalJobState.Failed);
        status.FailureReason.Should().NotBeNullOrWhiteSpace();
        status.FailureReason!.Should().Contain("moderation");
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task PollAsync_auth_rejection_throws_HttpRequestException_with_StatusCode(HttpStatusCode code)
    {
        var (client, _) = ClientReturning(code,
            $$"""{"detail":"denied for {{KeyId}}:{{KeySecret}}"}""");

        Func<Task> act = async () => await client.PollAsync(new ExternalJobHandle("req-1"), CancellationToken.None);

        var ex = await act.Should().ThrowAsync<HttpRequestException>();
        ex.And.StatusCode.Should().Be(code);
        ex.And.Message.Should().NotContain(KeyId);
        ex.And.Message.Should().NotContain(KeySecret);
    }

    // --- Probe: no-cost, one GET, never a submit ---

    private static (HiggsfieldVideoGenerationClient Client, RecordingHandler Handler) ProbeClientReturning(
        HttpStatusCode code, string body, string mediaType = "application/json") =>
        ClientFor((_, _) => Task.FromResult(new HttpResponseMessage(code)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, mediaType)
        }));

    private static void AssertExactlyOneStatusGetAndNeverASubmit(RecordingHandler handler)
    {
        handler.Requests.Should().ContainSingle();
        (HttpMethod method, string path, string auth, _) = handler.Requests[0];
        method.Should().Be(HttpMethod.Get);
        path.Should().StartWith("/requests/");
        path.Should().EndWith("/status");
        path.Length.Should().BeGreaterThan("/requests//status".Length);
        auth.Should().Be($"Key {KeyId}:{KeySecret}");
        handler.Requests.Should().NotContain(r => r.Method == HttpMethod.Post);
    }

    private static void AssertNoCredentialInReason(VideoGenerationProbeResult result)
    {
        (result.Reason ?? string.Empty).Should().NotContain(KeyId);
        (result.Reason ?? string.Empty).Should().NotContain(KeySecret);
    }

    [Fact]
    public async Task ProbeAsync_404_with_the_documented_detail_body_is_ok()
    {
        // 404 is documented as "the request does not exist or belongs to another account" — which
        // proves BOTH that this is the vendor's status endpoint and that the credential was accepted,
        // since an unauthenticated call is 401, not 404.
        var (client, handler) = ProbeClientReturning(HttpStatusCode.NotFound, """{"detail":"Request not found"}""");

        VideoGenerationProbeResult result = await client.ProbeAsync(CancellationToken.None);

        result.Ok.Should().BeTrue();
        result.AuthRejected.Should().BeFalse();
        AssertExactlyOneStatusGetAndNeverASubmit(handler);
        AssertNoCredentialInReason(result);
    }

    [Fact]
    public async Task ProbeAsync_200_with_a_request_status_envelope_is_ok()
    {
        Guid uuid = Guid.NewGuid();
        var (client, handler) = ProbeClientReturning(HttpStatusCode.OK,
            $$"""{"status":"queued","request_id":"{{uuid}}"}""");

        VideoGenerationProbeResult result = await client.ProbeAsync(CancellationToken.None);

        result.Ok.Should().BeTrue();
        result.AuthRejected.Should().BeFalse();
        AssertExactlyOneStatusGetAndNeverASubmit(handler);
        AssertNoCredentialInReason(result);
    }

    [Fact]
    public async Task ProbeAsync_401_is_auth_rejected()
    {
        var (client, handler) = ProbeClientReturning(HttpStatusCode.Unauthorized,
            """{"detail":"Invalid credentials"}""");

        VideoGenerationProbeResult result = await client.ProbeAsync(CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.AuthRejected.Should().BeTrue();
        AssertExactlyOneStatusGetAndNeverASubmit(handler);
        AssertNoCredentialInReason(result);
    }

    [Fact]
    public async Task ProbeAsync_403_is_not_ok_and_deliberately_NOT_reported_as_an_auth_failure()
    {
        // These docs define 403 as INSUFFICIENT CREDITS, not bad credentials. Folding it into
        // AuthRejected (as MiniMax's client does, because MiniMax documents no separate meaning)
        // would send an admin to re-issue a perfectly good key.
        var (client, handler) = ProbeClientReturning(HttpStatusCode.Forbidden,
            """{"detail":"Insufficient credits"}""");

        VideoGenerationProbeResult result = await client.ProbeAsync(CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.AuthRejected.Should().BeFalse();
        result.Reason.Should().Contain("403");
        AssertExactlyOneStatusGetAndNeverASubmit(handler);
        AssertNoCredentialInReason(result);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    [InlineData(HttpStatusCode.Locked)]
    public async Task ProbeAsync_5xx_408_429_and_other_non_proving_codes_are_not_ok(HttpStatusCode code)
    {
        var (client, handler) = ProbeClientReturning(code, """{"detail":"something went wrong"}""");

        VideoGenerationProbeResult result = await client.ProbeAsync(CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.AuthRejected.Should().BeFalse();
        result.Reason.Should().Contain(((int)code).ToString());
        AssertExactlyOneStatusGetAndNeverASubmit(handler);
        AssertNoCredentialInReason(result);
    }

    [Fact]
    public async Task ProbeAsync_non_json_body_is_not_ok()
    {
        var (client, handler) = ProbeClientReturning(HttpStatusCode.OK,
            "<html><body>Welcome to nginx!</body></html>", "text/html");

        VideoGenerationProbeResult result = await client.ProbeAsync(CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.AuthRejected.Should().BeFalse();
        result.Reason.Should().Contain("JSON");
        AssertExactlyOneStatusGetAndNeverASubmit(handler);
    }

    [Theory]
    [InlineData("""[1,2,3]""")]
    [InlineData("""{"unexpected":"shape"}""")]
    [InlineData("""{"status":123}""")]
    public async Task ProbeAsync_unrecognised_json_is_not_ok(string body)
    {
        var (client, handler) = ProbeClientReturning(HttpStatusCode.OK, body);

        VideoGenerationProbeResult result = await client.ProbeAsync(CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.AuthRejected.Should().BeFalse();
        AssertExactlyOneStatusGetAndNeverASubmit(handler);
    }

    [Fact]
    public async Task ProbeAsync_network_error_is_not_ok()
    {
        var (client, handler) = ClientFor((_, _) =>
            throw new HttpRequestException("No such host is known. (api.higgsfield.ai:443)"));

        VideoGenerationProbeResult result = await client.ProbeAsync(CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.AuthRejected.Should().BeFalse();
        result.Reason.Should().Contain("Network error");
        AssertExactlyOneStatusGetAndNeverASubmit(handler);
        AssertNoCredentialInReason(result);
    }

    [Fact]
    public async Task ProbeAsync_timeout_is_not_ok_and_bounded()
    {
        var (client, handler) = ClientFor(
            async (_, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return new HttpResponseMessage(HttpStatusCode.OK);
            },
            probeTimeout: TimeSpan.FromMilliseconds(200));

        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
        VideoGenerationProbeResult result = await client.ProbeAsync(CancellationToken.None);
        watch.Stop();

        result.Ok.Should().BeFalse();
        result.AuthRejected.Should().BeFalse();
        result.Reason.Should().Contain("did not respond");
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
        AssertExactlyOneStatusGetAndNeverASubmit(handler);
    }

    [Fact]
    public async Task ProbeAsync_caller_cancellation_propagates_rather_than_becoming_a_not_ok()
    {
        using CancellationTokenSource cts = new();
        var (client, _) = ClientFor(async (_, ct) =>
        {
            await cts.CancelAsync();
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        Func<Task> act = async () => await client.ProbeAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ProbeAsync_reason_is_scrubbed_even_when_the_handler_echoes_the_credential()
    {
        var (client, handler) = ProbeClientReturning(HttpStatusCode.BadGateway,
            $$"""{"detail":"key {{KeyId}} secret {{KeySecret}} rejected"}""");

        VideoGenerationProbeResult result = await client.ProbeAsync(CancellationToken.None);

        result.Ok.Should().BeFalse();
        AssertExactlyOneStatusGetAndNeverASubmit(handler);
        AssertNoCredentialInReason(result);
    }

    [Fact]
    public async Task ProbeAsync_uses_a_fresh_random_request_id_each_call()
    {
        var (client, handler) = ProbeClientReturning(HttpStatusCode.NotFound, """{"detail":"Request not found"}""");

        await client.ProbeAsync(CancellationToken.None);
        await client.ProbeAsync(CancellationToken.None);

        handler.Requests.Should().HaveCount(2);
        handler.Requests[0].Path.Should().NotBe(handler.Requests[1].Path);
        handler.Requests.Should().NotContain(r => r.Method == HttpMethod.Post);
    }

    // --- Presigned upload ---

    private const string PresignResponse =
        """{"public_url":"https://cdn.higgsfield.ai/pub-9.jpg","upload_url":"https://storage.higgsfield.ai/put-9?sig=abc","content_type":"image/jpeg","upload_headers":{"Content-Type":"image/jpeg","x-amz-meta-token":"tok-9"}}""";

    [Fact]
    public async Task UploadInputImageAsync_presigns_then_PUTs_every_returned_header_and_no_Authorization()
    {
        var (client, handler) = ClientFor((request, _) => request.Method == HttpMethod.Put
            ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))
            : Task.FromResult(Json(HttpStatusCode.OK, PresignResponse)));

        string publicUrl = await client.UploadInputImageAsync(
            new VideoGenerationImageInput("image/jpeg", new byte[] { 9, 8, 7 }), CancellationToken.None);

        publicUrl.Should().Be("https://cdn.higgsfield.ai/pub-9.jpg");

        handler.Requests.Should().HaveCount(2);
        (HttpMethod presignMethod, string presignPath, string presignAuth, string presignBody) = handler.Requests[0];
        presignMethod.Should().Be(HttpMethod.Post);
        presignPath.Should().Be("/files/generate-upload-url");
        presignAuth.Should().Be($"Key {KeyId}:{KeySecret}");

        using (JsonDocument doc = JsonDocument.Parse(presignBody))
        {
            doc.RootElement.GetProperty("content_type").GetString().Should().Be("image/jpeg");
        }

        handler.Requests[1].Method.Should().Be(HttpMethod.Put);
        // "Do not send Higgsfield API credentials to the presigned storage URL."
        handler.Requests[1].Auth.Should().BeEmpty();
    }

    [Fact]
    public async Task UploadInputImageAsync_applies_a_content_header_to_the_content_not_the_request()
    {
        HttpRequestMessage? captured = null;
        var (client, _) = ClientFor((request, _) =>
        {
            if (request.Method == HttpMethod.Put)
            {
                captured = request;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            }

            return Task.FromResult(Json(HttpStatusCode.OK, PresignResponse));
        });

        await client.UploadInputImageAsync(
            new VideoGenerationImageInput("image/jpeg", new byte[] { 1 }), CancellationToken.None);

        captured.Should().NotBeNull();
        // Content-* belong to HttpContentHeaders: TryAddWithoutValidation on the REQUEST collection
        // returns false silently, which sent the PUT as application/octet-stream and failed the
        // Content-* belong to HttpContentHeaders: TryAddWithoutValidation on the REQUEST collection
        // returns false SILENTLY, which sent the PUT as application/octet-stream and failed the
        // vendor's presigned policy. Content-Type landing on the CONTENT is the pin for that.
        captured!.Content!.Headers.ContentType!.MediaType.Should().Be("image/jpeg");
        // A non-content header (x-amz-*) rides on the REQUEST collection instead — same verbatim rule.
        captured.Headers.TryGetValues("x-amz-meta-token", out IEnumerable<string>? meta).Should().BeTrue();
        meta!.Should().ContainSingle().Which.Should().Be("tok-9");
    }

    [Theory]
    [InlineData("image/tiff")]
    [InlineData("application/octet-stream")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task UploadInputImageAsync_rejects_an_unsupported_content_type_with_zero_requests(string contentType)
    {
        var (client, handler) = ClientThatSendsNothing();

        Func<Task> act = async () => await client.UploadInputImageAsync(
            new VideoGenerationImageInput(contentType, new byte[] { 1 }), CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
        handler.Requests.Should().BeEmpty("the content type is validated before the presign endpoint is called");
    }

    [Fact]
    public async Task UploadInputImageAsync_rejects_empty_bytes_with_zero_requests()
    {
        var (client, handler) = ClientThatSendsNothing();

        Func<Task> act = async () => await client.UploadInputImageAsync(
            new VideoGenerationImageInput("image/png", Array.Empty<byte>()), CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
        handler.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("""{"public_url":"https://cdn.higgsfield.ai/p.jpg"}""")]
    [InlineData("""{"upload_url":"https://storage.higgsfield.ai/put?sig=x"}""")]
    [InlineData("""{"public_url":"","upload_url":"https://storage.higgsfield.ai/put?sig=x"}""")]
    [InlineData("""{"public_url":"https://cdn.higgsfield.ai/p.jpg","upload_url":""}""")]
    public async Task UploadInputImageAsync_throws_when_the_presign_response_is_missing_a_required_url(string body)
    {
        var (client, _) = ClientReturning(HttpStatusCode.OK, body);

        Func<Task> act = async () => await client.UploadInputImageAsync(
            new VideoGenerationImageInput("image/jpeg", new byte[] { 1 }), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task UploadInputImageAsync_refuses_a_presigned_url_that_is_not_https()
    {
        // The PUT carries the user's own frame bytes to a vendor-named host, so a plaintext or
        // non-http(s) scheme fails closed rather than being followed.
        var (client, handler) = ClientReturning(HttpStatusCode.OK,
            """{"public_url":"https://cdn.higgsfield.ai/p.jpg","upload_url":"http://storage.higgsfield.ai/put?sig=x"}""");

        Func<Task> act = async () => await client.UploadInputImageAsync(
            new VideoGenerationImageInput("image/jpeg", new byte[] { 1 }), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        handler.Requests.Should().ContainSingle("the PUT is never attempted");
        handler.Requests.Should().NotContain(r => r.Method == HttpMethod.Put);
    }

    [Fact]
    public async Task UploadInputImageAsync_refuses_an_Authorization_upload_header()
    {
        var (client, handler) = ClientReturning(HttpStatusCode.OK,
            """{"public_url":"https://cdn.higgsfield.ai/p.jpg","upload_url":"https://storage.higgsfield.ai/put?sig=x","upload_headers":{"Authorization":"Key leaked:leaked"}}""");

        Func<Task> act = async () => await client.UploadInputImageAsync(
            new VideoGenerationImageInput("image/jpeg", new byte[] { 1 }), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        handler.Requests.Should().ContainSingle("the PUT is refused, never sent with the credential");
    }

    [Fact]
    public async Task UploadInputImageAsync_throws_when_the_PUT_fails()
    {
        var (client, _) = ClientFor((request, _) => request.Method == HttpMethod.Put
            ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden))
            : Task.FromResult(Json(HttpStatusCode.OK, PresignResponse)));

        Func<Task> act = async () => await client.UploadInputImageAsync(
            new VideoGenerationImageInput("image/jpeg", new byte[] { 1 }), CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

}

/// <summary>
/// The factory arm for <see cref="InferenceProviderKind.Higgsfield"/> — the same cache the MiniMax
/// arm already had, asserted for the newly appended kind.
/// </summary>
public class HiggsfieldVideoGenerationClientFactoryTests
{
    private static ResolvedVideoGenerationProvider Provider(
        InferenceProviderKind kind, string apiKey = "hf-id:hf-secret") =>
        new(
            ProviderId: Guid.NewGuid(),
            Name: "provider",
            Kind: kind,
            Endpoint: "https://api.higgsfield.ai",
            ModelName: "kling-video/v3.0/std/text-to-video",
            ApiKey: apiKey,
            TimeoutSeconds: 300);

    [Fact]
    public void Get_returns_a_HiggsfieldVideoGenerationClient_for_a_Higgsfield_provider()
    {
        VideoGenerationClientFactory factory = new();

        IVideoGenerationClient client = factory.Get(Provider(InferenceProviderKind.Higgsfield));

        client.Should().BeOfType<HiggsfieldVideoGenerationClient>();
    }

    [Theory]
    [InlineData(InferenceProviderKind.AzureOpenAI)]
    [InlineData(InferenceProviderKind.OpenAICompatible)]
    [InlineData(InferenceProviderKind.Anthropic)]
    [InlineData(InferenceProviderKind.Gemini)]
    [InlineData(InferenceProviderKind.DeepSeek)]
    [InlineData(InferenceProviderKind.TypeSafe)]
    [InlineData(InferenceProviderKind.OpenJev)]
    public void Get_throws_NotSupportedException_naming_both_supported_kinds_for_any_other_kind(
        InferenceProviderKind kind)
    {
        VideoGenerationClientFactory factory = new();

        Action act = () => factory.Get(Provider(kind));

        var ex = act.Should().Throw<NotSupportedException>();
        ex.And.Message.Should().Contain("MiniMax");
        ex.And.Message.Should().Contain("Higgsfield");
    }

    [Fact]
    public void Get_caches_by_CacheKey_so_two_calls_for_the_same_provider_return_the_same_instance()
    {
        VideoGenerationClientFactory factory = new();
        ResolvedVideoGenerationProvider provider = Provider(InferenceProviderKind.Higgsfield);
        ResolvedVideoGenerationProvider sameSettings = new(
            ProviderId: Guid.NewGuid(),
            Name: provider.Name,
            Kind: provider.Kind,
            Endpoint: provider.Endpoint,
            ModelName: provider.ModelName,
            ApiKey: provider.ApiKey,
            TimeoutSeconds: provider.TimeoutSeconds);

        IVideoGenerationClient first = factory.Get(provider);
        IVideoGenerationClient second = factory.Get(sameSettings);

        first.Should().BeSameAs(second);
    }

    [Fact]
    public void Get_throws_when_the_Higgsfield_credential_is_not_a_usable_pair()
    {
        // Surfaces at resolution rather than at first use, so a misconfigured provider row fails the
        // step visibly instead of half-way through a paid submission.
        VideoGenerationClientFactory factory = new();

        Action act = () => factory.Get(Provider(InferenceProviderKind.Higgsfield, apiKey: "no-colon-here"));

        act.Should().Throw<ArgumentException>();
    }
}
