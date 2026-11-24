#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using ReelBolt.ImportWorker;
using ReelBolt.Inference.Api.Services.DesignSystems;
using ReelBolt.Shared.DesignSystems;
using ReelBolt.Shared.DesignSystems.Importers;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Importers;

/// <summary>
/// Real zips through the real worker pipeline (SafeZipReader -> ZipFileClassifier -> ExtractZipHandler) and on into
/// <see cref="BundleImporter"/>, with the same response-to-ImportFile mapping the import-zip controller applies. The
/// vendor fixtures elsewhere hand-build <see cref="ImportFile"/> lists and so never see what the worker drops (QA-6,
/// QA-7); these do.
/// </summary>
public sealed class BundleWorkerEndToEndTests
{
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

    private static async Task<(ExtractZipResponse Worker, BundleImportResult Imported)> RunAsync(byte[] zip)
    {
        WorkerResult result = await ExtractZipHandler.HandleAsync(
            new MemoryStream(zip), zip.Length, new WorkerOptions("t"), CancellationToken.None);
        result.Status.Should().Be(200);
        ExtractZipResponse worker = (ExtractZipResponse)result.Body!;

        // Mirrors DesignSystemsController.ImportZip: entries with inlined json/css/text/svg/markup become ImportFiles.
        List<ImportFile> files = new();
        foreach (WorkerFileDto entry in worker.Files)
        {
            if (entry.Text is null || entry.Kind is not ("json" or "css" or "text" or "svg" or "markup"))
                continue;

            files.Add(new ImportFile(entry.Path, Encoding.UTF8.GetBytes(entry.Text), entry.Sha256, null));
        }

        return (worker, new BundleImporter().ImportBundle(files));
    }

    // ------------------------------------------------------------------ QA-6: tailwind config

    [Fact]
    public async Task A_tailwind_config_in_a_zip_is_reported_as_not_read_and_contributes_no_tokens()
    {
        byte[] zip = Zip(
            ("src/index.css", Utf8(":root { --color-brand-primary: #6d28d9; --radius: 0.625rem; }")),
            ("tailwind.config.js", Utf8("module.exports = { theme: { extend: { colors: { accent: '#ff0000' } } } };")));

        (ExtractZipResponse worker, BundleImportResult imported) = await RunAsync(zip);

        worker.Dropped.Should().Contain(new WorkerDroppedDto("tailwind.config.js", "DROPPED_SCRIPT"));
        imported.Import.Diagnostics.Should().ContainSingle(d => d.Code == BundleImporter.TailwindConfigNotReadDiagnostic)
            .Which.Should().Match<ImportDiagnostic>(d => d.Severity == ImportDiagnosticSeverity.Info && d.Path == "tailwind.config.js");

        DesignDocument.FindToken(imported.Import.Design!, "color.brand.primary").Should().NotBeNull();
        DesignDocument.FindToken(imported.Import.Design!, "color.accent").Should().BeNull("the config never reaches the parser");
        imported.Import.Sources.Should().NotContain(s => s.Importer == "tailwind-v3");
        imported.Import.Sources.Select(s => s.Path).Should().NotContain("tailwind.config.js");
    }

    [Fact]
    public async Task A_nested_tailwind_config_ts_is_reported_with_its_path()
    {
        byte[] zip = Zip(
            ("app/globals.css", Utf8(":root { --color-brand-primary: #6d28d9; }")),
            ("app/tailwind.config.ts", Utf8("export default { theme: {} };")));

        (_, BundleImportResult imported) = await RunAsync(zip);

        imported.Import.Diagnostics.Should().ContainSingle(d => d.Code == BundleImporter.TailwindConfigNotReadDiagnostic)
            .Which.Path.Should().Be("app/tailwind.config.ts");
    }

    [Fact]
    public async Task No_tailwind_config_means_no_such_diagnostic()
    {
        (_, BundleImportResult imported) = await RunAsync(Zip(("a.css", Utf8(":root { --color-brand-primary: #6d28d9; }"))));

        imported.Import.Diagnostics.Should().NotContain(d => d.Code == BundleImporter.TailwindConfigNotReadDiagnostic);
    }

    [Fact]
    public async Task The_script_bytes_never_cross_the_worker_boundary()
    {
        const string Secret = "SCRIPT-BODY-MARKER-1234";
        byte[] zip = Zip(("tailwind.config.js", Utf8($"// {Secret}\nmodule.exports = {{}};")));

        (ExtractZipResponse worker, _) = await RunAsync(zip);

        System.Text.Json.JsonSerializer.Serialize(worker).Should().NotContain(Secret);
    }

    // ------------------------------------------------------------------ QA-7: penpot

    private static byte[] PenpotArchive()
    {
        // A .penpot export as a zip of JSON: manifest.json at the root, one file document under files/.
        // No entry is named *.penpot: the signal is the manifest.
        return Zip(
            ("manifest.json", Utf8("""{"type":"penpot/export-files","version":1,"generated-by":"penpot/2.4.0","files":[{"id":"6a1f","name":"Brand"}]}""")),
            ("files/6a1f.json", Utf8("""{"id":"6a1f","name":"Brand","data":{"colors":{"c1":{"id":"c1","name":"Primary","path":"Brand","color":"#6d28d9","opacity":1},"c2":{"id":"c2","name":"Surface","path":"Brand","color":"#0b0b0f","opacity":1}},"typographies":{"t1":{"id":"t1","name":"Body","path":"","font-family":"Inter","font-size":"16","font-weight":"400","line-height":"1.5","letter-spacing":"0"}}}}""")),
            ("files/6a1f/pages/p1.json", Utf8("""{"id":"p1","name":"Page 1","objects":{}}""")));
    }

    [Fact]
    public async Task A_real_penpot_layout_is_labelled_penpot_from_its_manifest_and_its_libraries_are_read()
    {
        (_, BundleImportResult imported) = await RunAsync(PenpotArchive());

        imported.Vendor.Should().Be(BundleVendors.Penpot);
        string diagnostics = string.Join("; ", imported.Import.Diagnostics.Select(d => $"{d.Code}@{d.Path}:{d.Message}"));
        imported.Import.Design.Should().NotBeNull(diagnostics);
        DesignDocument.FindToken(imported.Import.Design!, "color.penpot.brand.primary").Should().NotBeNull(imported.Import.Design!.ToJsonString() + diagnostics);
        DesignDocument.FindToken(imported.Import.Design!, "color.penpot.brand.surface").Should().NotBeNull();
        DesignDocument.FindToken(imported.Import.Design!, "typography.penpot.body").Should().NotBeNull();
        imported.Import.Sources.Should().Contain(s => s.Importer == PenpotImporter.ImporterId && s.Path == "files/6a1f.json");
    }

    [Fact]
    public void Manifest_marker_labels_penpot_even_without_a_penpot_name()
    {
        BundleImporter.DetectVendor(["manifest.json", "files/6a1f.json"], null, null, penpotManifest: true)
            .Should().Be(BundleVendors.Penpot);
    }

    [Fact]
    public async Task A_manifest_json_that_is_not_penpot_does_not_label_the_archive()
    {
        byte[] zip = Zip(
            ("manifest.json", Utf8("""{"name":"my extension","version":"1.0","manifest_version":3}""")),
            ("styles.css", Utf8(":root { --color-brand-primary: #6d28d9; }")));

        (_, BundleImportResult imported) = await RunAsync(zip);

        imported.Vendor.Should().NotBe(BundleVendors.Penpot);
    }

    // ------------------------------------------------------------------ QA-7: sketch

    [Fact]
    public async Task A_sketch_swatch_whose_value_is_the_colour_itself_is_read()
    {
        // The public Sketch file format: Swatch.value IS a Color ({ _class: "color", red, green, blue, alpha }).
        byte[] zip = Zip(
            ("document.json", Utf8("""{"_class":"document","sharedSwatches":{"_class":"swatchContainer","objects":[{"_class":"swatch","do_objectID":"A1","name":"Brand/Primary","value":{"_class":"color","alpha":1,"red":0.427,"green":0.157,"blue":0.851}}]}}""")),
            ("meta.json", Utf8("""{"version":"2024"}""")),
            ("pages/0.json", Utf8("""{"_class":"page","layers":[]}""")));

        (_, BundleImportResult imported) = await RunAsync(zip);

        imported.Vendor.Should().Be(BundleVendors.Sketch);
        DesignDocument.FindToken(imported.Import.Design!, "color.sketch.brand-primary").Should().NotBeNull();
    }

    [Fact]
    public async Task A_sketch_document_over_the_inline_cap_is_reported_not_silently_skipped()
    {
        byte[] padding = new byte[(int)new WorkerOptions("t").ZipInlineFileMaxBytes];
        new Random(5).NextBytes(padding);
        string pad = Convert.ToBase64String(padding);
        byte[] zip = Zip(
            ("document.json", Utf8("{\"_class\":\"document\",\"pad\":\"" + pad + "\"}")),
            ("meta.json", Utf8("""{"version":"2024"}""")));

        (ExtractZipResponse worker, _) = await RunAsync(zip);

        worker.Dropped.Should().Contain(new WorkerDroppedDto("document.json", "DOCUMENT_TOO_LARGE"));
    }

    // ------------------------------------------------------------------ Task 4: bounded binary asset channel

    /// <summary>The PNG signature alone is enough for the magic sniffer; the bytes are opaque to every reader here.</summary>
    private static readonly byte[] TinyPng =
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
    ];

    [Fact]
    public async Task An_assets_folder_image_travels_as_base64_with_its_digest()
    {
        byte[] png = TinyPng;
        byte[] zip = Zip(
            ("assets/logo.png", png),
            ("reference/big.png", png),
            ("assets/mark.svg", Utf8("<svg/>")));

        WorkerResult result = await ExtractZipHandler.HandleAsync(
            new MemoryStream(zip), zip.Length, new WorkerOptions("t"), CancellationToken.None);
        ExtractZipResponse worker = (ExtractZipResponse)result.Body!;

        WorkerFileDto logo = worker.Files.Single(f => f.Path == "assets/logo.png");
        logo.Base64.Should().NotBeNull();
        logo.Sha256.Should().Be(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(png)).ToLowerInvariant());
        Convert.FromBase64String(logo.Base64!).Should().Equal(png);
        worker.Files.Single(f => f.Path == "reference/big.png").Base64.Should().BeNull();

        WorkerFileDto svg = worker.Files.Single(f => f.Path == "assets/mark.svg");
        svg.Kind.Should().Be("svg");
        svg.Text.Should().Contain("<svg");
        svg.Base64.Should().BeNull();
    }

    [Fact]
    public async Task A_worker_answer_with_a_bad_asset_digest_is_refused()
    {
        // Build the real answer, then flip one byte of the Base64 while keeping its SHA-256: the API must
        // refuse the whole answer, and must name the bound with a fixed code (never the content or a path).
        (ExtractZipResponse _, ImportWorkerZipResult clientResult) = await RunThroughClient(tamper: true);

        clientResult.Status.Should().Be(ImportWorkerStatus.BadResponse);
        clientResult.Violation.Should().Be("asset_digest");
    }

    [Fact]
    public async Task A_good_asset_answer_reaches_the_client_with_its_bytes()
    {
        (_, ImportWorkerZipResult clientResult) = await RunThroughClient(tamper: false);

        clientResult.Status.Should().Be(ImportWorkerStatus.Ok);
        ZipEntry logo = clientResult.Bundle!.Files.Single(f => f.Path == "assets/logo.png");
        logo.Kind.Should().Be("image");
        logo.Bytes.Should().Equal(TinyPng);
        clientResult.Bundle.Files.Single(f => f.Path == "reference/big.png").Bytes.Should().BeNull();
    }

    private static async Task<(ExtractZipResponse Worker, ImportWorkerZipResult Client)> RunThroughClient(bool tamper)
    {
        byte[] zip = Zip(("assets/logo.png", TinyPng), ("reference/big.png", TinyPng));

        WorkerResult result = await ExtractZipHandler.HandleAsync(
            new MemoryStream(zip), zip.Length, new WorkerOptions("t"), CancellationToken.None);
        ExtractZipResponse worker = (ExtractZipResponse)result.Body!;

        JsonObject body = JsonNode.Parse(JsonSerializer.Serialize(worker))!.AsObject();
        if (tamper)
        {
            JsonObject logo = body["Files"]!.AsArray()
                .Single(f => f!["Path"]!.GetValue<string>() == "assets/logo.png")!.AsObject();
            byte[] bytes = Convert.FromBase64String(logo["Base64"]!.GetValue<string>());
            bytes[^1] ^= 0xFF;
            logo["Base64"] = Convert.ToBase64String(bytes);
        }

        var client = new ImportWorkerClient(
            new HttpClient(new JsonHandler(body.ToJsonString())) { BaseAddress = new Uri("http://import-worker") },
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Import:Worker:Token"] = "t" })
                .Build());

        ImportWorkerZipResult payload = await client.ExtractZipAsync(new MemoryStream(zip), CancellationToken.None);
        return (worker, payload);
    }

    // ------------------------------------------------------------------ Task 2: fonts on the bounded binary channel

    private static readonly byte[] Woff2Font = [0x77, 0x4F, 0x46, 0x32, .. new byte[64]];

    [Fact]
    public async Task A_tampered_font_digest_is_refused()
    {
        (_, ImportWorkerZipResult client) = await RunFontThroughClient(tamper: true);

        client.Status.Should().Be(ImportWorkerStatus.BadResponse);
        client.Violation.Should().Be("font_digest");
    }

    [Fact]
    public async Task A_good_font_answer_reaches_the_client_with_its_bytes()
    {
        (_, ImportWorkerZipResult client) = await RunFontThroughClient(tamper: false);

        client.Status.Should().Be(ImportWorkerStatus.Ok);
        ZipEntry font = client.Bundle!.Files.Single();
        font.Kind.Should().Be("font");
        font.Bytes.Should().Equal(Woff2Font);
    }

    private static async Task<(ExtractZipResponse Worker, ImportWorkerZipResult Client)> RunFontThroughClient(bool tamper)
    {
        byte[] zip = Zip(("fonts/brand.woff2", Woff2Font));

        WorkerResult result = await ExtractZipHandler.HandleAsync(
            new MemoryStream(zip), zip.Length, new WorkerOptions("t"), CancellationToken.None);
        ExtractZipResponse worker = (ExtractZipResponse)result.Body!;

        JsonObject body = JsonNode.Parse(JsonSerializer.Serialize(worker))!.AsObject();
        if (tamper)
        {
            JsonObject font = body["Files"]!.AsArray()
                .Single(f => f!["Path"]!.GetValue<string>() == "fonts/brand.woff2")!.AsObject();
            byte[] bytes = Convert.FromBase64String(font["Base64"]!.GetValue<string>());
            bytes[^1] ^= 0xFF;
            font["Base64"] = Convert.ToBase64String(bytes);
        }

        var client = new ImportWorkerClient(
            new HttpClient(new JsonHandler(body.ToJsonString())) { BaseAddress = new Uri("http://import-worker") },
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Import:Worker:Token"] = "t" })
                .Build());

        ImportWorkerZipResult payload = await client.ExtractZipAsync(new MemoryStream(zip), CancellationToken.None);
        return (worker, payload);
    }

    private sealed class JsonHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }
}
