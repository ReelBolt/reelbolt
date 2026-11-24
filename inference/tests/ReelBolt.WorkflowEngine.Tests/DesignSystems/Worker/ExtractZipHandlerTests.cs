using System;
using System.IO;
using System.IO.Compression;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using ReelBolt.Inference.Api.Services.DesignSystems;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.ImportWorker;
using ReelBolt.Shared.Archives;
using ReelBolt.Shared.DesignSystems.Importers;
using ReelBolt.WorkflowEngine.Tests.DesignSystems.Archives;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Worker;

public class ExtractZipHandlerTests
{
    private static readonly WorkerOptions Options = new("test-token");

    private static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value);

    private static byte[] Zip(params (string Name, byte[] Data)[] entries)
    {
        using MemoryStream stream = new();
        using (ZipArchive archive = new(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string name, byte[] data) in entries)
            {
                ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Optimal);
                using Stream entryStream = entry.Open();
                entryStream.Write(data, 0, data.Length);
            }
        }

        return stream.ToArray();
    }

    private static byte[] RandomAsciiText(int length, int seed)
    {
        byte[] data = new byte[length];
        Random random = new(seed);
        for (int i = 0; i < length; i++)
            data[i] = (byte)('a' + random.Next(26));
        return data;
    }

    private static async Task<WorkerResult> HandleAsync(byte[] zip, long? contentLength = null, Stream? body = null)
    {
        using MemoryStream memory = body is null ? new MemoryStream(zip) : null!;
        Stream source = body ?? memory;
        return await ExtractZipHandler.HandleAsync(source, contentLength ?? zip.LongLength, Options, CancellationToken.None);
    }

    private static ExtractZipResponse Response(WorkerResult result) => (ExtractZipResponse)result.Body!;

    [Fact]
    public async Task Mixed_zip_inlines_text_and_drops_the_rest()
    {
        byte[] png = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x01, 0x02, 0x03 };
        byte[] zip = Zip(
            ("a.html", Utf8("<html><body>hi</body></html>")),
            ("b.svg", Utf8("<svg xmlns=\"http://www.w3.org/2000/svg\"></svg>")),
            ("c.js", Utf8("console.log(1);")),
            ("d.png", png),
            ("e.css", Utf8(".a{color:red}")),
            ("f.json", Utf8("{\"x\":1}")),
            ("g.md", Utf8("# heading\n\nplain")));

        ExtractZipResponse response = Response(await HandleAsync(zip));

        response.Aborted.Should().BeFalse();
        response.AbortReason.Should().BeNull();
        response.Files.Should().HaveCount(6);
        response.Files.Select(file => file.Path)
            .Should().Equal("a.html", "b.svg", "d.png", "e.css", "f.json", "g.md");

        WorkerFileDto markup = response.Files.Single(file => file.Path == "a.html");
        markup.Kind.Should().Be("markup");
        markup.Text.Should().Be("<html><body>hi</body></html>");

        WorkerFileDto svg = response.Files.Single(file => file.Path == "b.svg");
        svg.Kind.Should().Be("svg");
        svg.Text.Should().Contain("<svg");

        WorkerFileDto css = response.Files.Single(file => file.Path == "e.css");
        css.Kind.Should().Be("css");
        css.Text.Should().Be(".a{color:red}");

        WorkerFileDto json = response.Files.Single(file => file.Path == "f.json");
        json.Kind.Should().Be("json");
        json.Text.Should().Be("{\"x\":1}");

        WorkerFileDto markdown = response.Files.Single(file => file.Path == "g.md");
        markdown.Kind.Should().Be("text");
        markdown.Text.Should().Be("# heading\n\nplain");

        WorkerFileDto image = response.Files.Single(file => file.Path == "d.png");
        image.Kind.Should().Be("image");
        image.Text.Should().BeNull();
        image.Size.Should().Be(png.Length);

        // The root image is not under `assets`/`.thumbnail`, so it is reported rather than silently un-inlined.
        response.Dropped.Should().BeEquivalentTo(new[]
        {
            new WorkerDroppedDto("c.js", "DROPPED_SCRIPT"),
            new WorkerDroppedDto("d.png", "IMAGE_NOT_ASSET_PATH"),
        });
    }

    [Fact]
    public async Task An_svg_entry_crosses_as_text_and_is_not_dropped()
    {
        byte[] zip = Zip(("assets/mark.svg", Utf8("<svg xmlns='http://www.w3.org/2000/svg'><path d='M0 0'/></svg>")));

        ExtractZipResponse worker = Response(await HandleAsync(zip));

        WorkerFileDto file = worker.Files.Single(f => f.Path == "assets/mark.svg");
        file.Kind.Should().Be("svg");
        file.Text.Should().Contain("<svg");
        file.Base64.Should().BeNull();
        worker.Dropped.Should().NotContain(d => d.Reason == "DROPPED_SVG");
    }

    [Fact]
    public async Task An_html_entry_crosses_as_markup_text_never_as_executable_markup()
    {
        byte[] zip = Zip(("guidelines/colors.html", Utf8("<html><body><b>Primary</b><script>alert(1)</script></body></html>")));

        WorkerFileDto file = Response(await HandleAsync(zip)).Files.Single(f => f.Path == "guidelines/colors.html");
        file.Kind.Should().Be("markup");
        file.Text.Should().Contain("Primary");
        file.Base64.Should().BeNull();
    }

    [Fact]
    public async Task A_script_entry_is_still_dropped()
    {
        WorkerDroppedDto dropped = Response(await HandleAsync(Zip(("x.js", Utf8("alert(1)"))))).Dropped.Single();
        dropped.Reason.Should().Be("DROPPED_SCRIPT");
    }

    [Theory]
    [InlineData(0x77, 0x4F, 0x46, 0x46, "font/woff")]
    [InlineData(0x77, 0x4F, 0x46, 0x32, "font/woff2")]
    [InlineData(0x00, 0x01, 0x00, 0x00, "font/ttf")]
    [InlineData(0x4F, 0x54, 0x54, 0x4F, "font/otf")]
    public async Task A_font_entry_crosses_base64_with_its_type(byte a, byte b, byte c, byte d, string mime)
    {
        byte[] font = [a, b, c, d, .. new byte[64]];

        WorkerFileDto file = Response(await HandleAsync(Zip(("fonts/brand.woff2", font)))).Files.Single();

        file.Kind.Should().Be("font");
        Convert.FromBase64String(file.Base64!).Should().Equal(font);
        ZipFileClassifier.MediaType("fonts/brand.woff2", font).Should().Be(mime);
    }

    // A font the bounded channel cannot inline must never cross as `kind=font` with no bytes: the API
    // refuses such an answer as font_shape and the whole import fails with 502. It is dropped by name instead.
    [Fact]
    public async Task A_font_outside_fonts_or_assets_is_dropped_not_emitted_as_a_byteless_font()
    {
        byte[] font = [0x77, 0x4F, 0x46, 0x32, .. new byte[64]];

        ExtractZipResponse response = Response(await HandleAsync(Zip(("reference/brand.woff2", font))));

        response.Files.Should().NotContain(file => file.Kind == "font");
        response.Dropped.Should().ContainSingle()
            .Which.Should().Be(new WorkerDroppedDto("reference/brand.woff2", "FONT_NOT_ASSET_PATH"));
    }

    [Fact]
    public async Task An_oversized_font_is_dropped_as_FONT_TOO_LARGE_not_emitted_as_a_byteless_font()
    {
        byte[] font = [0x77, 0x4F, 0x46, 0x32, .. RandomAsciiText(ArchiveFontLimits.MaxEntryBytes, seed: 71)];

        ExtractZipResponse response = Response(await HandleAsync(
            Zip(("fonts/TooBig-Regular.woff2", font), ("app.css", Utf8(":root{--color-brand-primary:#6d28d9}")))));

        response.Files.Should().NotContain(file => file.Kind == "font");
        response.Dropped.Should().ContainSingle()
            .Which.Should().Be(new WorkerDroppedDto("fonts/TooBig-Regular.woff2", "FONT_TOO_LARGE"));
        response.Files.Should().ContainSingle(file => file.Path == "app.css");
    }

    [Fact]
    public async Task A_font_beyond_the_entry_cap_is_dropped_as_FONT_CAP_not_emitted_as_a_byteless_font()
    {
        byte[] font = [0x77, 0x4F, 0x46, 0x32, .. new byte[64]];
        (string, byte[])[] entries = Enumerable.Range(0, ArchiveFontLimits.MaxEntries + 1)
            .Select(i => ($"fonts/Family{i:D2}-Regular.woff2", font))
            .ToArray();

        ExtractZipResponse response = Response(await HandleAsync(Zip(entries)));

        response.Files.Should().HaveCount(ArchiveFontLimits.MaxEntries);
        response.Files.Should().OnlyContain(file => file.Kind == "font" && file.Base64 != null);
        response.Dropped.Should().ContainSingle(drop => drop.Reason == "FONT_CAP");
    }

    [Fact]
    public async Task An_image_outside_assets_is_dropped_with_IMAGE_NOT_ASSET_PATH()
    {
        byte[] png = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00 };

        ExtractZipResponse response = Response(await HandleAsync(Zip(("reference/shot.png", png))));

        response.Files.Single(file => file.Path == "reference/shot.png").Base64.Should().BeNull();
        response.Dropped.Should().ContainSingle()
            .Which.Should().Be(new WorkerDroppedDto("reference/shot.png", "IMAGE_NOT_ASSET_PATH"));
    }

    [Fact]
    public async Task An_oversized_image_is_dropped_with_IMAGE_TOO_LARGE()
    {
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. RandomAsciiText(ArchiveAssetLimits.MaxEntryBytes, seed: 73)];

        ExtractZipResponse response = Response(await HandleAsync(Zip(("assets/huge.png", png))));

        response.Files.Single(file => file.Path == "assets/huge.png").Base64.Should().BeNull();
        response.Dropped.Should().ContainSingle()
            .Which.Should().Be(new WorkerDroppedDto("assets/huge.png", "IMAGE_TOO_LARGE"));
    }

    [Fact]
    public async Task An_xml_declared_xhtml_whose_root_is_html_is_carried_as_markup()
    {
        string html = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n"
            + "<html xmlns=\"http://www.w3.org/1999/xhtml\"><body>Primary</body></html>";
        byte[] zip = Zip(("guidelines/colors.xhtml", Utf8(html)), ("tokens.css", Utf8(".a{color:red}")));

        ExtractZipResponse response = Response(await HandleAsync(zip));

        WorkerFileDto file = response.Files.Single(f => f.Path == "guidelines/colors.xhtml");
        file.Kind.Should().Be("markup");
        file.Text.Should().Contain("Primary");
        response.Dropped.Should().NotContain(drop => drop.Path == "guidelines/colors.xhtml");
    }

    [Fact]
    public async Task An_xml_declared_xhtml_whose_root_is_not_html_stays_dropped()
    {
        string xml = "<?xml version=\"1.0\"?>\n<root><item>data</item></root>";
        byte[] zip = Zip(("data.xhtml", Utf8(xml)));

        ExtractZipResponse response = Response(await HandleAsync(zip));

        response.Files.Should().BeEmpty();
        response.Dropped.Should().ContainSingle()
            .Which.Should().Be(new WorkerDroppedDto("data.xhtml", "DROPPED_HTML"));
    }

    [Fact]
    public async Task An_oversized_svg_is_listed_without_text_and_dropped_with_a_reason()
    {
        // Random ASCII keeps the compressed ratio under the reader's bomb guard.
        byte[] big = Utf8("<svg xmlns='http://www.w3.org/2000/svg'>")
            .Concat(RandomAsciiText(ImportLimits.SvgMaxBytes, seed: 42))
            .Concat(Utf8("</svg>"))
            .ToArray();

        ExtractZipResponse response = Response(await HandleAsync(Zip(("assets/big.svg", big))));
        WorkerFileDto file = response.Files.Single();
        file.Kind.Should().Be("svg");
        file.Text.Should().BeNull();
        response.Dropped.Should().ContainSingle(drop =>
            drop.Path == "assets/big.svg" && drop.Reason == "SVG_TOO_LARGE");
    }

    [Fact]
    public async Task An_oversized_markup_is_listed_without_text_and_dropped_with_a_reason()
    {
        byte[] big = Utf8("<html><body>")
            .Concat(RandomAsciiText(ImportLimits.MarkupMaxBytes, seed: 43))
            .Concat(Utf8("</body></html>"))
            .ToArray();

        ExtractZipResponse response = Response(await HandleAsync(Zip(("index.html", big))));
        WorkerFileDto file = response.Files.Single();
        file.Kind.Should().Be("markup");
        file.Text.Should().BeNull();
        response.Dropped.Should().ContainSingle(drop =>
            drop.Path == "index.html" && drop.Reason == "MARKUP_TOO_LARGE");
    }

    [Fact]
    public async Task The_root_thumbnail_webp_travels_on_the_image_channel()
    {
        byte[] webp = [0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x57, 0x45, 0x42, 0x50, 1, 2, 3];

        WorkerFileDto file = Response(await HandleAsync(Zip((".thumbnail", webp)))).Files.Single();

        file.Kind.Should().Be("image");
        Convert.FromBase64String(file.Base64!).Should().Equal(webp);
    }

    [Fact]
    public async Task Extension_cannot_make_a_binary_look_like_css()
    {
        byte[] elf = { 0x7F, 0x45, 0x4C, 0x46, 0x02, 0x01, 0x01, 0x00 };
        ExtractZipResponse response = Response(await HandleAsync(Zip(("x.css", elf))));

        response.Files.Should().BeEmpty();
        response.Dropped.Should().ContainSingle().Which.Should().Be(new WorkerDroppedDto("x.css", "DROPPED_BINARY"));
    }

    [Fact]
    public async Task Png_named_txt_is_still_an_image()
    {
        byte[] png = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00 };
        ExtractZipResponse response = Response(await HandleAsync(Zip(("x.txt", png))));

        WorkerFileDto file = response.Files.Should().ContainSingle().Which;
        file.Kind.Should().Be("image");
        file.Text.Should().BeNull();
    }

    [Fact]
    public async Task Non_utf8_text_is_dropped_binary()
    {
        byte[] bytes = { 0xFF, 0xFE, 0x41, 0x00 };
        ExtractZipResponse response = Response(await HandleAsync(Zip(("x.txt", bytes))));

        response.Files.Should().BeEmpty();
        response.Dropped.Should().ContainSingle().Which.Reason.Should().Be("DROPPED_BINARY");
    }

    [Fact]
    public async Task File_over_2MiB_listed_without_text()
    {
        byte[] big = RandomAsciiText((int)Options.ZipInlineFileMaxBytes + 1, seed: 7);
        ExtractZipResponse response = Response(await HandleAsync(Zip(("big.txt", big))));

        WorkerFileDto file = response.Files.Should().ContainSingle().Which;
        file.Kind.Should().Be("text");
        file.Text.Should().BeNull();
        file.Size.Should().Be(big.Length);
    }

    [Fact]
    public async Task Inline_total_cap_stops_inlining()
    {
        (string, byte[])[] entries = new (string, byte[])[13];
        for (int i = 0; i < entries.Length; i++)
            entries[i] = ($"f{i:D2}.txt", RandomAsciiText((int)Options.ZipInlineFileMaxBytes, seed: 100 + i));

        ExtractZipResponse response = Response(await HandleAsync(Zip(entries)));

        response.Files.Should().HaveCount(13);
        response.Files.Take(12).Should().OnlyContain(file => file.Text != null);
        response.Files[12].Text.Should().BeNull();
    }

    [Fact]
    public async Task Oversize_content_length_returns_413_without_reading()
    {
        ThrowingStream stream = new();
        WorkerResult result = await ExtractZipHandler.HandleAsync(
            stream,
            Options.MaxBodyBytes + 1,
            Options,
            CancellationToken.None);

        result.Status.Should().Be(413);
        result.Body.Should().BeNull();
        stream.ReadAttempted.Should().BeFalse();
    }

    [Fact]
    public async Task Body_without_content_length_over_cap_returns_413()
    {
        byte[] payload = new byte[(int)Options.MaxBodyBytes + 1];
        WorkerResult result = await ExtractZipHandler.HandleAsync(
            new MemoryStream(payload),
            null,
            Options,
            CancellationToken.None);

        result.Status.Should().Be(413);
    }

    [Fact]
    public async Task Too_many_entries_zip_is_aborted()
    {
        (string, byte[])[] entries = new (string, byte[])[2001];
        for (int i = 0; i < entries.Length; i++)
            entries[i] = ($"f{i:D4}.txt", Array.Empty<byte>());

        ExtractZipResponse response = Response(await HandleAsync(Zip(entries)));

        response.Aborted.Should().BeTrue();
        response.AbortReason.Should().Be("TOO_MANY_ENTRIES");
        response.Files.Should().BeEmpty();
    }

    // Rewritten (SEC-8): a body that is not a zip at all used to come back 200 + ARCHIVE_INVALID, which left
    // ImportWorkerStatus.NotZip (415) unreachable. The worker now answers 415 for a body without zip magic;
    // a body WITH zip magic that is still broken keeps aborting with ARCHIVE_INVALID.
    [Fact]
    public async Task Body_without_zip_magic_returns_415()
    {
        byte[] garbage = Utf8("this is definitely not a zip archive at all, not even close");

        WorkerResult result = await HandleAsync(garbage);

        result.Status.Should().Be(415);
        result.Body.Should().BeNull();
    }

    [Fact]
    public async Task Empty_body_returns_415()
    {
        WorkerResult result = await HandleAsync(Array.Empty<byte>());

        result.Status.Should().Be(415);
    }

    [Fact]
    public async Task Zip_magic_followed_by_garbage_aborts_with_ARCHIVE_INVALID()
    {
        byte[] bytes = new byte[200];
        bytes[0] = 0x50;
        bytes[1] = 0x4B;
        bytes[2] = 0x03;
        bytes[3] = 0x04;
        ExtractZipResponse response = Response(await HandleAsync(bytes));

        response.Aborted.Should().BeTrue();
        response.AbortReason.Should().Be("ARCHIVE_INVALID");
        response.Files.Should().BeEmpty();
    }

    [Fact]
    public async Task Empty_archive_is_accepted_not_415()
    {
        ExtractZipResponse response = Response(await HandleAsync(Zip()));

        response.Aborted.Should().BeFalse();
        response.Files.Should().BeEmpty();
    }

    // Rewritten (SEC-5): the old version passed on an empty Files list and never looked at the refusal code.
    [Fact]
    public async Task Zip_slip_entry_is_refused_with_PATH_TRAVERSAL_while_the_good_entry_is_kept()
    {
        string hostile = "../../evil.txt";
        string placeholder = new('x', hostile.Length);
        byte[] zip = ZipFixtures.PatchName(
            Zip(("ok.txt", Utf8("ok")), (placeholder, Utf8("evil"))),
            placeholder,
            hostile);

        ExtractZipResponse response = Response(await HandleAsync(zip));

        response.Aborted.Should().BeFalse();
        response.Files.Should().ContainSingle().Which.Path.Should().Be("ok.txt");
        response.Dropped.Should().ContainSingle().Which.Should().Be(new WorkerDroppedDto(hostile, "PATH_TRAVERSAL"));
    }

    [Theory]
    [InlineData(":")]
    [InlineData("a.txt:hidden")]
    [InlineData("C:evil.txt")]
    [InlineData("dir/:stream")]
    public async Task Bare_colon_and_alternate_data_stream_names_are_refused_as_PATH_TRAVERSAL(string name)
    {
        string placeholder = new('Q', name.Length);
        byte[] zip = ZipFixtures.PatchName(Zip(("ok.txt", Utf8("ok")), (placeholder, Utf8("evil"))), placeholder, name);

        ExtractZipResponse response = Response(await HandleAsync(zip));

        response.Files.Should().ContainSingle().Which.Path.Should().Be("ok.txt");
        response.Dropped.Should().ContainSingle().Which.Should().Be(new WorkerDroppedDto(name, "PATH_TRAVERSAL"));
    }

    private sealed class FixedResponse(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }

    /// <summary>Round-trips the worker's answer through the API client exactly as the wire would.</summary>
    private static async Task<ImportWorkerZipResult> ThroughClient(ExtractZipResponse response)
    {
        string json = JsonSerializer.Serialize(response, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Import:Worker:Token"] = "tok" })
            .Build();
        var client = new ImportWorkerClient(
            new HttpClient(new FixedResponse(json)) { BaseAddress = new Uri("http://import-worker") },
            config);
        return await client.ExtractZipAsync(new MemoryStream(new byte[] { 0x50, 0x4B, 3, 4 }), CancellationToken.None);
    }

    [IcuFact]
    public async Task Non_ascii_file_names_flow_through_and_nfc_nfd_duplicates_are_rejected()
    {
        byte[] zip = Zip(
            ("caf\u00e9.css", Utf8(".a{color:red}")),
            ("cafe\u0301.css", Utf8(".a{color:blue}")),
            ("\u65e5\u672c\u8a9e.json", Utf8("{\"x\":1}")));

        ExtractZipResponse response = Response(await HandleAsync(zip));

        response.Aborted.Should().BeFalse();
        response.Files.Select(file => file.Path).Should().Equal("caf\u00e9.css", "\u65e5\u672c\u8a9e.json");
        response.Files.Single(file => file.Path == "\u65e5\u672c\u8a9e.json").Kind.Should().Be("json");
        response.Dropped.Should().ContainSingle().Which.Reason.Should().Be("DUPLICATE_PATH");
    }

    [Fact]
    public async Task Clipping_a_dropped_name_counts_characters_and_never_splits_a_surrogate_pair()
    {
        // 64 emoji = 128 UTF-16 units. The old unit-wise clip kept 128 units (all 64 emoji, as 128 '?'); a clip by
        // characters keeps 128 characters, each non-ASCII one shown as a single '?'. A .js entry is still dropped.
        string name = string.Concat(Enumerable.Repeat("\U0001F600", 2)) + new string('a', 140) + ".js";
        ExtractZipResponse response = Response(await HandleAsync(Zip((name, Utf8("console.log(1)")))));

        string clipped = response.Dropped.Should().ContainSingle().Which.Path;
        clipped.Should().Be("??" + new string('a', 126));
    }

    [Fact]
    public async Task Hostile_rejected_names_are_clipped_to_printable_ascii_and_the_api_still_accepts_the_answer()
    {
        byte[] payload = Utf8("x");
        byte[] zip = RawZip.Build(new[]
        {
            new RawZip.Entry("../ctl\u0001\u0007\u007f.txt", payload, payload.Length, Method: 0),
            new RawZip.Entry("../" + new string('a', 5000), payload, payload.Length, Method: 0),
            new RawZip.Entry("ok.txt", payload, payload.Length, Method: 0),
        });

        ExtractZipResponse response = Response(await HandleAsync(zip));

        response.Dropped.Should().HaveCount(2);
        response.Dropped.Should().OnlyContain(d => d.Path.Length > 0 && d.Path.Length <= 128);
        response.Dropped.Should().OnlyContain(d => d.Path.All(c => c >= 0x20 && c < 0x7F));
        response.Dropped.Should().OnlyContain(d => d.Reason == "PATH_TRAVERSAL");
        ImportWorkerZipResult viaApi = await ThroughClient(response);
        viaApi.Status.Should().Be(ImportWorkerStatus.Ok);
        viaApi.Bundle!.Files.Should().ContainSingle().Which.Path.Should().Be("ok.txt");
    }

    [Fact]
    public async Task Thousands_of_rejected_names_are_bounded_to_200_plus_a_count_of_the_rest()
    {
        byte[] payload = Utf8("x");
        List<RawZip.Entry> entries = new();
        for (int i = 0; i < 1500; i++)
            entries.Add(new RawZip.Entry($"../n{i}.txt", payload, payload.Length, Method: 0));

        ExtractZipResponse response = Response(await HandleAsync(RawZip.Build(entries)));

        response.Dropped.Should().HaveCount(201);
        response.Dropped.Take(200).Should().OnlyContain(d => d.Reason == "PATH_TRAVERSAL");
        response.Dropped[200].Path.Should().Contain("1300");
        (await ThroughClient(response)).Status.Should().Be(ImportWorkerStatus.Ok);
    }

    [Fact]
    public async Task A_100k_entry_archive_aborts_with_a_bounded_dropped_list()
    {
        byte[] payload = Utf8("x");
        List<RawZip.Entry> entries = new(100_000);
        for (int i = 0; i < 100_000; i++)
            entries.Add(new RawZip.Entry($"../{i}", payload, payload.Length, Method: 0));

        ExtractZipResponse response = Response(await HandleAsync(RawZip.Build(entries), contentLength: null));

        response.Aborted.Should().BeTrue();
        response.Dropped.Count.Should().BeLessThanOrEqualTo(201);
        (await ThroughClient(response)).Status.Should().Be(ImportWorkerStatus.Ok);
    }

    [Fact]
    public async Task Encrypted_entry_is_refused_and_never_returned()
    {
        byte[] payload = Encoding.ASCII.GetBytes("ciphertext-looking-bytes");
        byte[] zip = RawZip.Build(new[]
        {
            new RawZip.Entry("secret.txt", payload, payload.Length, Method: 0, Flags: 1),
            new RawZip.Entry("open.txt", payload, payload.Length, Method: 0),
        });

        ExtractZipResponse response = Response(await HandleAsync(zip));

        response.Files.Select(file => file.Path).Should().Equal("open.txt");
        response.Dropped.Should().ContainSingle().Which.Should().Be(new WorkerDroppedDto("secret.txt", "ENTRY_UNREADABLE"));
    }

    [Fact]
    public async Task Zip64_archive_is_read_through_the_worker()
    {
        byte[] payload = Utf8("{\"zip64\":true}");
        byte[] zip = RawZip.Build(
            new[] { new RawZip.Entry("tokens.json", payload, payload.Length, Method: 0, Zip64: true) },
            zip64Eocd: true);

        ExtractZipResponse response = Response(await HandleAsync(zip));

        response.Aborted.Should().BeFalse();
        WorkerFileDto file = response.Files.Should().ContainSingle().Which;
        file.Path.Should().Be("tokens.json");
        file.Kind.Should().Be("json");
        file.Text.Should().Be("{\"zip64\":true}");
    }

    [Fact]
    public async Task Oversized_document_json_is_reported_as_DOCUMENT_TOO_LARGE_not_silently_skipped()
    {
        string padding = Encoding.ASCII.GetString(RandomAsciiText((int)Options.ZipInlineFileMaxBytes, seed: 11));
        byte[] document = Utf8("{\"sharedSwatches\":{},\"pad\":\"" + padding + "\"}");

        ExtractZipResponse response = Response(await HandleAsync(Zip(("document.json", document), ("meta.json", Utf8("{}")))));

        response.Dropped.Should().ContainSingle().Which.Should().Be(new WorkerDroppedDto("document.json", "DOCUMENT_TOO_LARGE"));
        response.Files.Single(f => f.Path == "document.json").Text.Should().BeNull();
        response.Files.Single(f => f.Path == "meta.json").Text.Should().Be("{}");
    }

    [Fact]
    public async Task Document_json_within_the_cap_is_inlined_and_not_reported()
    {
        ExtractZipResponse response = Response(await HandleAsync(Zip(("pages/x/document.json", Utf8("{\"a\":1}")))));

        response.Dropped.Should().BeEmpty();
        response.Files.Single().Text.Should().Be("{\"a\":1}");
    }

    [Fact]
    public async Task Tailwind_config_stays_dropped_as_script_but_is_listed_by_name_with_no_content()
    {
        byte[] config = Utf8("module.exports = { theme: { extend: { colors: { brand: '#6d28d9' } } } };");

        ExtractZipResponse response = Response(await HandleAsync(Zip(("tailwind.config.js", config), ("app/tailwind.config.ts", config))));

        response.Dropped.Should().BeEquivalentTo(new[]
        {
            new WorkerDroppedDto("app/tailwind.config.ts", "DROPPED_SCRIPT"),
            new WorkerDroppedDto("tailwind.config.js", "DROPPED_SCRIPT"),
        });

        // The script bytes never leave the worker: only the name is reported, with an empty inline text.
        response.Files.Should().HaveCount(2);
        response.Files.Should().OnlyContain(file => file.Kind == "text" && file.Text == string.Empty);
        response.Stats.Bytes.Should().Be(0);
    }

    [Fact]
    public async Task A_script_that_is_not_a_tailwind_config_is_not_listed_at_all()
    {
        ExtractZipResponse response = Response(await HandleAsync(Zip(("vite.config.js", Utf8("export default {}")))));

        response.Files.Should().BeEmpty();
        response.Dropped.Should().ContainSingle().Which.Reason.Should().Be("DROPPED_SCRIPT");
    }

    [Fact]
    public async Task A_d_ts_declaration_is_inert_text_while_other_typescript_stays_dropped()
    {
        const string Declaration = "export interface ButtonProps { variant?: string; startIcon?: unknown }";
        byte[] zip = Zip(
            ("components/Button.d.ts", Utf8(Declaration)),
            ("components/Button.ts", Utf8("export const x = 1;")),
            ("components/Button.jsx", Utf8("export const Button = () => null;")),
            ("components/sneaky.d.ts", Utf8("#!/usr/bin/env node\nexport {};")));

        ExtractZipResponse response = Response(await HandleAsync(zip));

        WorkerFileDto declaration = response.Files.Single(file => file.Path == "components/Button.d.ts");
        declaration.Kind.Should().Be("text");
        declaration.Text.Should().Be(Declaration);

        response.Dropped.Should().Contain(new WorkerDroppedDto("components/Button.ts", "DROPPED_SCRIPT"));
        response.Dropped.Should().Contain(new WorkerDroppedDto("components/Button.jsx", "DROPPED_SCRIPT"));
        response.Dropped.Should().Contain(new WorkerDroppedDto("components/sneaky.d.ts", "DROPPED_SCRIPT"));
    }

    private sealed class ThrowingStream : Stream
    {
        public bool ReadAttempted { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ReadAttempted = true;
            throw new InvalidOperationException("the body must not be read");
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            ReadAttempted = true;
            throw new InvalidOperationException("the body must not be read");
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadAttempted = true;
            throw new InvalidOperationException("the body must not be read");
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
