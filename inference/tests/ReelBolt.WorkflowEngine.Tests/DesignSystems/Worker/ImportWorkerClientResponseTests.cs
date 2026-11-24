using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using ReelBolt.Inference.Api.Services.DesignSystems;
using ReelBolt.Shared.Archives;
using ReelBolt.Shared.DesignSystems.Importers;
using ReelBolt.WorkflowEngine.Tests.DesignSystems.Archives;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Worker;

/// <summary>
/// SEC-6: the API must not trust the worker's output. A fake handler returns hostile or broken
/// <c>extract-zip</c> bodies and the client must refuse each as <see cref="ImportWorkerStatus.BadResponse"/>;
/// a well-formed answer still parses. Also covers the status mapping (busy, not-a-zip) and configuration.
/// </summary>
public sealed class ImportWorkerClientResponseTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static JsonObject File(string path = "tokens.json", string? sha = Sha, long size = 12, string kind = "json", string? text = "{\"a\":1}")
    {
        var node = new JsonObject { ["path"] = path, ["sha256"] = sha, ["size"] = size, ["kind"] = kind };
        node["text"] = text;
        return node;
    }

    private static string Body(IEnumerable<JsonObject> files, IEnumerable<JsonObject>? dropped = null) =>
        new JsonObject
        {
            ["files"] = new JsonArray(files.Select(f => (JsonNode?)f).ToArray()),
            ["dropped"] = new JsonArray((dropped ?? []).Select(d => (JsonNode?)d).ToArray()),
            ["stats"] = new JsonObject { ["entries"] = 1, ["bytes"] = 12 },
            ["aborted"] = false,
            ["abortReason"] = null,
        }.ToJsonString();

    private static async Task<ImportWorkerZipResult> Call(HttpResponseMessage response)
    {
        var client = new ImportWorkerClient(
            new HttpClient(new Stub(response)) { BaseAddress = new Uri("http://import-worker") },
            Config("tok"));
        return await client.ExtractZipAsync(new MemoryStream([0x50, 0x4B, 3, 4]), CancellationToken.None);
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static IConfiguration Config(string? token) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Import:Worker:Token"] = token }).Build();

    [Fact]
    public async Task A_well_formed_answer_still_parses()
    {
        ImportWorkerZipResult result = await Call(Json(Body([File()], [new JsonObject { ["path"] = "a.js", ["reason"] = "DROPPED_SCRIPT" }])));

        result.Status.Should().Be(ImportWorkerStatus.Ok);
        result.Bundle!.Files.Should().ContainSingle();
        result.Bundle.Dropped.Should().ContainSingle();
    }

    [Theory]
    [InlineData("a‮b.js")] // U+202E right-to-left override
    [InlineData("a​b.js")] // U+200B zero-width space
    public async Task A_bidi_or_zero_width_dropped_path_fails_the_whole_answer(string path)
    {
        ImportWorkerZipResult result = await Call(
            Json(Body([File()], [new JsonObject { ["path"] = path, ["reason"] = "DROPPED_SCRIPT" }])));

        result.Status.Should().Be(ImportWorkerStatus.BadResponse,
            "IMPORT_REJECTED echoes the path, so a hidden rune must be refused like a control character");
        result.Bundle.Should().BeNull();
    }

    [Theory]
    [InlineData("../etc/passwd")]
    [InlineData("a/../../b.json")]
    [InlineData("/abs/path.json")]
    [InlineData("a\\b.json")]
    [InlineData("C:file.json")]
    [InlineData("a\u202eb.json")]
    [InlineData("a\u200bb.json")]
    [InlineData("a\u0085b.json")]
    [InlineData("a//b.json")]
    [InlineData("a/b/c/d/e/f/g/h/i/j/k/l/m.json")]
    [InlineData("")]
    public async Task A_hostile_path_fails_the_whole_answer(string path)
    {
        ImportWorkerZipResult result = await Call(Json(Body([File("ok.json"), File(path)])));

        result.Status.Should().Be(ImportWorkerStatus.BadResponse);
        result.Bundle.Should().BeNull();
    }

    [Theory]
    [InlineData("caf\u00e9.css")]
    [InlineData("\u65e5\u672c\u8a9e/tokens.json")]
    [InlineData("\u0442\u0435\u043c\u0430.css")]
    [InlineData("\U0001F600.txt")]
    public async Task Unicode_paths_the_worker_allows_are_accepted(string path)
    {
        ImportWorkerZipResult result = await Call(Json(Body([File(path)])));

        result.Status.Should().Be(ImportWorkerStatus.Ok);
        result.Bundle!.Files.Should().ContainSingle().Which.Path.Should().Be(path);
    }

    [Fact]
    public async Task Paths_that_differ_only_by_non_ascii_case_are_duplicates_without_any_normalisation()
    {
        // Ordinal ignore-case needs no ICU, so this holds in every globalization mode.
        (await Call(Json(Body([File("\u00c9.css"), File("\u00e9.CSS")])))).Violation.Should().Be("path_duplicate");
    }

    [Fact]
    public async Task Nfc_and_nfd_spellings_are_left_to_the_worker_the_api_does_no_normalisation()
    {
        // The worker is the NFC authority; the API must not depend on ICU (it runs invariant), so it only checks
        // character classes and ordinal-ignore-case duplicates.
        ImportWorkerZipResult result = await Call(Json(Body([File("caf\u00e9.css"), File("cafe\u0301.css")])));

        result.Status.Should().Be(ImportWorkerStatus.Ok);
    }

    [Fact]
    public async Task Turkish_i_variants_are_distinct_paths()
    {
        ImportWorkerZipResult result = await Call(Json(Body([File("\u0131.css"), File("i.css"), File("\u0130.css")])));

        result.Status.Should().Be(ImportWorkerStatus.Ok);
        result.Bundle!.Files.Should().HaveCount(3);
    }

    [Fact]
    public async Task A_huge_text_fails()
    {
        string huge = new('x', 9 * 1024 * 1024);
        ImportWorkerZipResult result = await Call(Json(Body([File(size: huge.Length, kind: "text", text: huge)])));

        result.Status.Should().Be(ImportWorkerStatus.BadResponse);
    }

    [Theory]
    [InlineData("Tokens.json", "tokens.json")]
    [InlineData("tokens.json", "tokens.json")]
    [InlineData("a/B.css", "A/b.CSS")]
    public async Task Paths_that_differ_only_by_case_are_duplicates_and_fail(string first, string second)
    {
        ImportWorkerZipResult result = await Call(Json(Body([File(first), File(second)])));

        result.Status.Should().Be(ImportWorkerStatus.BadResponse);
        result.Violation.Should().Be("path_duplicate");
    }

    [Fact]
    public async Task Text_longer_than_its_declared_size_fails()
    {
        ImportWorkerZipResult result = await Call(Json(Body([File(size: 3, text: "{\"a\":1,\"b\":2}")])));

        result.Status.Should().Be(ImportWorkerStatus.BadResponse);
    }

    [Fact]
    public async Task Total_inlined_text_over_64_MiB_fails()
    {
        string chunk = new('y', 7 * 1024 * 1024);
        IEnumerable<JsonObject> files = Enumerable.Range(0, 10)
            .Select(i => File($"f{i}.txt", size: chunk.Length, kind: "text", text: chunk));

        ImportWorkerZipResult result = await Call(Json(Body(files)));

        result.Status.Should().Be(ImportWorkerStatus.BadResponse);
    }

    [Fact]
    public async Task A_hundred_thousand_entries_fail()
    {
        IEnumerable<JsonObject> files = Enumerable.Range(0, 100_000).Select(i => File($"d/f{i}.json", size: 2, text: "{}"));

        ImportWorkerZipResult result = await Call(Json(Body(files)));

        result.Status.Should().Be(ImportWorkerStatus.BadResponse);
        result.ObservedEntries.Should().BeGreaterThan(2000);
    }

    [Theory]
    [InlineData("not-hex")]
    [InlineData("0123456789abcdef")]
    [InlineData("zz23456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData(null)]
    public async Task A_wrong_sha_fails(string? sha)
    {
        ImportWorkerZipResult result = await Call(Json(Body([File(sha: sha)])));

        result.Status.Should().Be(ImportWorkerStatus.BadResponse);
    }

    [Fact]
    public async Task An_unknown_kind_fails()
    {
        ImportWorkerZipResult result = await Call(Json(Body([File(kind: "executable")])));

        result.Status.Should().Be(ImportWorkerStatus.BadResponse);
    }

    [Fact]
    public async Task A_negative_or_over_cap_size_fails()
    {
        (await Call(Json(Body([File(size: -1)])))).Status.Should().Be(ImportWorkerStatus.BadResponse);
        (await Call(Json(Body([File(size: 9L * 1024 * 1024, text: null)])))).Status.Should().Be(ImportWorkerStatus.BadResponse);
    }

    [Fact]
    public async Task A_truncated_json_body_fails_as_a_bad_response()
    {
        string full = Body([File()]);
        ImportWorkerZipResult result = await Call(Json(full[..(full.Length / 2)]));

        result.Status.Should().Be(ImportWorkerStatus.BadResponse);
    }

    [Fact]
    public async Task An_oversized_response_body_fails_as_a_bad_response()
    {
        ImportWorkerZipResult result = await Call(Json(new string(' ', 65 * 1024 * 1024)));

        result.Status.Should().Be(ImportWorkerStatus.BadResponse);
    }

    [Fact]
    public async Task A_worker_415_is_NotZip()
    {
        (await Call(Json("{\"code\":\"NOT_A_ZIP\"}", HttpStatusCode.UnsupportedMediaType))).Status
            .Should().Be(ImportWorkerStatus.NotZip);
    }

    [Fact]
    public async Task A_worker_503_is_Busy_and_carries_Retry_After()
    {
        HttpResponseMessage busy = Json("{\"code\":\"WORKER_BUSY\"}", HttpStatusCode.ServiceUnavailable);
        busy.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(7));

        ImportWorkerZipResult result = await Call(busy);

        result.Status.Should().Be(ImportWorkerStatus.Busy);
        result.RetryAfterSeconds.Should().Be(7);
    }

    [Theory]
    [InlineData(null, "http://import-worker", false)]
    [InlineData("", "http://import-worker", false)]
    [InlineData("   ", "http://import-worker", false)]
    [InlineData("tok", null, false)]
    [InlineData("tok", "http://import-worker", true)]
    public void IsConfigured_needs_a_token_and_a_base_address(string? token, string? baseUrl, bool expected)
    {
        var http = new HttpClient(new Stub(Json("{}")));
        if (baseUrl is not null)
            http.BaseAddress = new Uri(baseUrl);

        new ImportWorkerClient(http, Config(token)).IsConfigured.Should().Be(expected);
    }

    // ------------------------------------------------------------------ Task 4: bounded binary asset channel

    private static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static JsonObject AssetFile(string path, byte[] bytes, string? sha = null, long? size = null, string kind = "image") =>
        new()
        {
            ["path"] = path,
            ["sha256"] = sha ?? Digest(bytes),
            ["size"] = size ?? bytes.Length,
            ["kind"] = kind,
            ["base64"] = Convert.ToBase64String(bytes),
        };

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    [Fact]
    public async Task A_well_formed_asset_answer_exposes_its_bytes()
    {
        ImportWorkerZipResult result = await Call(Json(Body([AssetFile("assets/logo.png", Png)])));

        result.Status.Should().Be(ImportWorkerStatus.Ok);
        result.Bundle!.Files.Single().Bytes.Should().Equal(Png);
    }

    [Fact]
    public async Task An_asset_outside_an_assets_segment_is_refused_as_asset_shape()
    {
        ImportWorkerZipResult result = await Call(Json(Body([AssetFile("reference/logo.png", Png)])));

        result.Status.Should().Be(ImportWorkerStatus.BadResponse);
        result.Violation.Should().Be("asset_shape");
    }

    [Fact]
    public async Task An_asset_whose_bytes_do_not_match_its_declared_size_is_refused_as_asset_shape()
    {
        ImportWorkerZipResult result = await Call(Json(Body([AssetFile("assets/logo.png", Png, size: Png.Length + 1)])));

        result.Status.Should().Be(ImportWorkerStatus.BadResponse);
        result.Violation.Should().Be("asset_shape");
    }

    [Fact]
    public async Task Base64_on_a_non_image_entry_is_refused_as_asset_shape()
    {
        JsonObject entry = AssetFile("assets/tokens.json", Utf8("{}"), kind: "json");
        ImportWorkerZipResult result = await Call(Json(Body([entry])));

        result.Status.Should().Be(ImportWorkerStatus.BadResponse);
        result.Violation.Should().Be("asset_shape");
    }

    [Fact]
    public async Task An_asset_over_two_MiB_is_refused_as_asset_size()
    {
        byte[] oversized = new byte[(2 * 1024 * 1024) + 1];
        ImportWorkerZipResult result = await Call(Json(Body([AssetFile("assets/huge.png", oversized)])));

        result.Status.Should().Be(ImportWorkerStatus.BadResponse);
        result.Violation.Should().Be("asset_size");
    }

    [Fact]
    public async Task An_asset_with_the_wrong_digest_is_refused_as_asset_digest()
    {
        ImportWorkerZipResult result = await Call(Json(Body([AssetFile("assets/logo.png", Png, sha: new string('0', 64))])));

        result.Status.Should().Be(ImportWorkerStatus.BadResponse);
        result.Violation.Should().Be("asset_digest");
    }

    [Fact]
    public async Task More_than_sixteen_inlined_assets_is_refused_as_asset_limit()
    {
        IEnumerable<JsonObject> files = Enumerable.Range(0, 17)
            .Select(i => AssetFile($"assets/{i}.png", Png));

        ImportWorkerZipResult result = await Call(Json(Body(files)));

        result.Status.Should().Be(ImportWorkerStatus.BadResponse);
        result.Violation.Should().Be("asset_limit");
    }

    // ------------------------------------------------------------------ Task 2: SVG/markup text and font bytes

    private static JsonObject TextFile(string path, string text, string kind) =>
        new()
        {
            ["path"] = path,
            ["sha256"] = Digest(Utf8(text)),
            ["size"] = Utf8(text).Length,
            ["kind"] = kind,
            ["text"] = text,
        };

    private static readonly byte[] Woff2 = [0x77, 0x4F, 0x46, 0x32, .. new byte[60]];

    private static JsonObject FontFile(string path, byte[] bytes, string? sha = null, long? size = null) =>
        new()
        {
            ["path"] = path,
            ["sha256"] = sha ?? Digest(bytes),
            ["size"] = size ?? bytes.Length,
            ["kind"] = "font",
            ["base64"] = Convert.ToBase64String(bytes),
        };

    [Fact]
    public async Task An_svg_entry_with_text_is_accepted()
    {
        ImportWorkerZipResult result = await Call(Json(Body([TextFile("assets/mark.svg", "<svg/>", "svg")])));

        result.Status.Should().Be(ImportWorkerStatus.Ok);
        result.Bundle!.Files.Single().Kind.Should().Be("svg");
    }

    [Fact]
    public async Task A_markup_entry_with_text_is_accepted()
    {
        ImportWorkerZipResult result = await Call(Json(Body([TextFile("guidelines/a.html", "<b>x</b>", "markup")])));

        result.Status.Should().Be(ImportWorkerStatus.Ok);
        result.Bundle!.Files.Single().Kind.Should().Be("markup");
    }

    [Fact]
    public async Task An_svg_entry_over_its_cap_is_refused()
    {
        string big = new('x', ImportLimits.SvgMaxBytes + 1);
        ImportWorkerZipResult result = await Call(Json(Body([TextFile("assets/mark.svg", big, "svg")])));

        result.Status.Should().Be(ImportWorkerStatus.BadResponse);
    }

    [Fact]
    public async Task A_markup_entry_over_its_cap_is_refused()
    {
        string big = new('x', ImportLimits.MarkupMaxBytes + 1);
        ImportWorkerZipResult result = await Call(Json(Body([TextFile("guidelines/a.html", big, "markup")])));

        result.Status.Should().Be(ImportWorkerStatus.BadResponse);
    }

    [Fact]
    public async Task Base64_on_an_svg_or_markup_entry_is_refused_as_asset_shape()
    {
        JsonObject svg = TextFile("assets/mark.svg", "<svg/>", "svg");
        svg["base64"] = Convert.ToBase64String(Utf8("<svg/>"));
        JsonObject markup = TextFile("guidelines/a.html", "<b>x</b>", "markup");
        markup["base64"] = Convert.ToBase64String(Utf8("<b>x</b>"));

        (await Call(Json(Body([svg])))).Violation.Should().Be("asset_shape");
        (await Call(Json(Body([markup])))).Violation.Should().Be("asset_shape");
    }

    [Fact]
    public async Task A_font_answer_exposes_its_bytes()
    {
        ImportWorkerZipResult result = await Call(Json(Body([FontFile("fonts/brand.woff2", Woff2)])));

        result.Status.Should().Be(ImportWorkerStatus.Ok);
        ZipEntry font = result.Bundle!.Files.Single();
        font.Kind.Should().Be("font");
        font.Bytes.Should().Equal(Woff2);
    }

    [Fact]
    public async Task A_font_outside_fonts_or_assets_is_refused_as_font_shape()
    {
        ImportWorkerZipResult result = await Call(Json(Body([FontFile("reference/brand.woff2", Woff2)])));

        result.Status.Should().Be(ImportWorkerStatus.BadResponse);
        result.Violation.Should().Be("font_shape");
    }

    [Fact]
    public async Task A_font_without_base64_is_refused_as_font_shape()
    {
        JsonObject font = new()
        {
            ["path"] = "fonts/brand.woff2",
            ["sha256"] = Digest(Woff2),
            ["size"] = Woff2.Length,
            ["kind"] = "font",
        };

        ImportWorkerZipResult result = await Call(Json(Body([font])));

        result.Status.Should().Be(ImportWorkerStatus.BadResponse);
        result.Violation.Should().Be("font_shape");
    }

    [Fact]
    public async Task A_font_whose_bytes_do_not_match_its_declared_size_is_refused_as_font_shape()
    {
        ImportWorkerZipResult result = await Call(Json(Body([FontFile("fonts/brand.woff2", Woff2, size: Woff2.Length + 1)])));

        result.Status.Should().Be(ImportWorkerStatus.BadResponse);
        result.Violation.Should().Be("font_shape");
    }

    [Fact]
    public async Task An_oversized_font_is_refused_as_font_size()
    {
        byte[] oversized = [0x77, 0x4F, 0x46, 0x32, .. new byte[ArchiveFontLimits.MaxEntryBytes]];
        ImportWorkerZipResult result = await Call(Json(Body([FontFile("fonts/huge.woff2", oversized)])));

        result.Status.Should().Be(ImportWorkerStatus.BadResponse);
        result.Violation.Should().Be("font_size");
    }

    [Fact]
    public async Task A_font_with_the_wrong_digest_is_refused_as_font_digest()
    {
        ImportWorkerZipResult result = await Call(Json(Body([FontFile("fonts/brand.woff2", Woff2, sha: new string('0', 64))])));

        result.Status.Should().Be(ImportWorkerStatus.BadResponse);
        result.Violation.Should().Be("font_digest");
    }

    [Fact]
    public async Task More_than_eight_inlined_fonts_is_refused_as_font_limit()
    {
        IEnumerable<JsonObject> files = Enumerable.Range(0, 9)
            .Select(i => FontFile($"fonts/{i}.woff2", Woff2));

        ImportWorkerZipResult result = await Call(Json(Body(files)));

        result.Status.Should().Be(ImportWorkerStatus.BadResponse);
        result.Violation.Should().Be("font_limit");
    }

    [Fact]
    public async Task A_root_thumbnail_image_is_accepted_as_an_asset()
    {
        ImportWorkerZipResult result = await Call(Json(Body([AssetFile(".thumbnail", Png)])));

        result.Status.Should().Be(ImportWorkerStatus.Ok);
        result.Bundle!.Files.Single().Bytes.Should().Equal(Png);
    }

    private static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value);

    private sealed class Stub(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response);
    }
}
