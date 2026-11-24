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
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.ImportWorker;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.DesignSystems;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Inference.Api.Services.Uploads;
using ReelBolt.Shared.Archives;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.DesignSystems;
using ReelBolt.Shared.DesignSystems.Importers;
using ReelBolt.Shared.Tenancy;
using ReelBolt.WorkflowEngine.Tests.DesignSystems.Archives;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Api;

/// <summary>
/// P4-11 coverage for <c>POST /api/v1/projects/{p}/design-system/import-zip</c>. The fake worker
/// handler runs the REAL <see cref="ExtractZipHandler"/> (and therefore the REAL
/// <c>SafeZipReader</c>) over the request body, so path traversal, a bomb and a symlink exercise the
/// shipped parsing path. A successful import builds an Import Draft whose provenance names only the
/// allowed text entries; scripts and markup never reach an importer.
/// </summary>
public sealed class ImportZipRouteTests : IDisposable
{
    private static readonly Guid HomeOrg = Guid.Parse("cccccccc-0000-0000-0000-000000000014");
    private const string WorkerToken = "worker_secret_token_value";

    private static readonly WorkerOptions Options = new("test-token");

    private readonly InferenceApiDbContext _db;
    private readonly Guid _ownerId = Guid.NewGuid();

    public ImportZipRouteTests()
    {
        _db = new InferenceApiDbContext(new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase($"import-zip-{Guid.NewGuid():N}")
            .Options);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task A_zip_with_allowed_text_becomes_an_import_draft_with_provenance()
    {
        byte[] zip = ZipFixtures.Build(
            ("app/globals.css", Utf8(":root { --color-brand-primary: #6d28d9; }")),
            ("tokens.json", Utf8("{ \"color\": { \"background\": { \"$type\": \"color\", \"$value\": { \"hex\": \"#0b0b0f\", \"colorSpace\": \"srgb\" } } } }")),
            ("DESIGN.md", Utf8("---\nname: Acme\n---\n## Overview\nA clean brand.\n")),
            ("package.json", Utf8("{\"dependencies\":{\"react\":\"19.0.0\"},\"scripts\":{\"postinstall\":\"node evil\"}}")));

        var handler = new WorkerHandler();
        DesignSystemsController controller = Controller(handler);
        Project project = SeedProject();

        ActionResult<ZipImportResponse> response = await controller.ImportZip(
            project.Id, File(zip), CancellationToken.None);

        ObjectResult created = response.Result.Should().BeAssignableTo<ObjectResult>().Subject;
        created.StatusCode.Should().Be(StatusCodes.Status201Created);

        ZipImportResponse payload = created.Value.Should().BeOfType<ZipImportResponse>().Subject;
        payload.Version.Origin.Should().Be("Import");
        payload.Version.State.Should().Be("Draft");
        DesignDocument.FindToken(payload.Version.Document, "color.brand.primary").Should().NotBeNull();
        DesignDocument.FindToken(payload.Version.Document, "color.background").Should().NotBeNull();

        payload.Sources.Select(source => source.Path)
            .Should().BeEquivalentTo(new[] { "app/globals.css", "tokens.json", "DESIGN.md", "package.json" });
        payload.Diagnostics.Should().Contain(diagnostic =>
            diagnostic.Code == "BUNDLE_PACKAGE" && diagnostic.Message.Contains("1 dependency"));

        handler.Requests.Should().ContainSingle();
        handler.Requests[0].RequestUri!.AbsolutePath.Should().Be("/v1/extract-zip");
        handler.Requests[0].Headers.Authorization!.Parameter.Should().Be(WorkerToken);
        _db.DesignSystemVersions.Count().Should().Be(1);
        _db.ProjectFiles.Count().Should().Be(0);

        // Provenance is a database fact, not just a response field: drift (D-9) reads these rows.
        List<DesignSystemSource> rows = _db.DesignSystemSources.AsNoTracking().ToList();
        rows.Select(row => row.Path)
            .Should().BeEquivalentTo(new[] { "app/globals.css", "tokens.json", "DESIGN.md", "package.json" });
        rows.Should().OnlyContain(row => row.VersionId == payload.Version.Id
            && row.DesignSystemId == payload.Version.DesignSystemId
            && row.SourceKind == DesignSourceKind.Upload
            && row.Status == DesignSourceStatus.Current
            && row.ContentSha256.Length == 64);
    }

    [Fact]
    public async Task A_path_traversal_zip_is_400_IMPORT_REJECTED_with_reasons()
    {
        byte[] zip = ZipFixtures.PatchName(
            ZipFixtures.Build(("aa/bb.txt", Utf8("plain text"))),
            "aa/bb.txt",
            "../bb.txt");

        var handler = new WorkerHandler();
        DesignSystemsController controller = Controller(handler);
        Project project = SeedProject();

        ActionResult<ZipImportResponse> response = await controller.ImportZip(
            project.Id, File(zip), CancellationToken.None);

        ObjectResult failure = response.Result.Should().BeAssignableTo<ObjectResult>().Subject;
        failure.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        JsonObject body = Body(failure.Value);
        body["code"]!.GetValue<string>().Should().Be("IMPORT_REJECTED");
        body["reasons"]!.AsArray().Select(reason => reason!["reason"]!.GetValue<string>())
            .Should().Contain("PATH_TRAVERSAL");
        _db.DesignSystemVersions.Count().Should().Be(0);
    }

    [Fact]
    public async Task An_entry_count_bomb_is_400_IMPORT_REJECTED()
    {
        byte[] zip = ManyEntries(2001);

        var handler = new WorkerHandler();
        DesignSystemsController controller = Controller(handler);
        Project project = SeedProject();

        ActionResult<ZipImportResponse> response = await controller.ImportZip(
            project.Id, File(zip), CancellationToken.None);

        ObjectResult failure = response.Result.Should().BeAssignableTo<ObjectResult>().Subject;
        failure.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        JsonObject body = Body(failure.Value);
        body["code"]!.GetValue<string>().Should().Be("IMPORT_REJECTED");
        body["abortReason"]!.GetValue<string>().Should().Be("TOO_MANY_ENTRIES");
        _db.DesignSystemVersions.Count().Should().Be(0);
    }

    [Fact]
    public async Task Scripts_are_refused_and_markup_is_admitted_as_inert_text()
    {
        const string evil = "const cp = require('child_process'); cp.execSync('touch /tmp/pwned');";
        byte[] zip = ZipWithSymlink(
            ("app/globals.css", Utf8(":root { --color-brand-primary: #6d28d9; }")),
            ("package.json", Utf8("{\"scripts\":{\"postinstall\":\"node -e evil\"},\"dependencies\":{\"react\":\"19.0.0\"}}")),
            ("evil.js", Utf8(evil)),
            ("x.html", Utf8("<html><body>hi</body></html>")),
            ("link-to-elsewhere", Utf8("../../etc/passwd")));

        var handler = new WorkerHandler();
        DesignSystemsController controller = Controller(handler);
        Project project = SeedProject();

        ActionResult<ZipImportResponse> response = await controller.ImportZip(
            project.Id, File(zip), CancellationToken.None);

        ObjectResult created = response.Result.Should().BeAssignableTo<ObjectResult>().Subject;
        created.StatusCode.Should().Be(StatusCodes.Status201Created);

        ZipImportResponse payload = created.Value.Should().BeOfType<ZipImportResponse>().Subject;
        payload.Sources.Select(source => source.Path)
            .Should().BeEquivalentTo(new[] { "app/globals.css", "package.json", "x.html" });
        payload.Diagnostics.Should().Contain(d => d.Code == "BUNDLE_MARKUP_NOT_READ" && d.Path == "x.html");

        string serialized = JsonSerializer.Serialize(payload);
        serialized.Should().NotContain("child_process");
        serialized.Should().NotContain("execSync");
        serialized.Should().NotContain("<html>");
        serialized.Should().NotContain("etc/passwd");
    }

    [Fact]
    public async Task A_zip_with_only_an_image_is_400_IMPORT_NOTHING_RECOGNIZED()
    {
        byte[] png = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x01, 0x02, 0x03 };
        byte[] zip = ZipFixtures.Build(("logo.png", png));

        var handler = new WorkerHandler();
        DesignSystemsController controller = Controller(handler);
        Project project = SeedProject();

        ActionResult<ZipImportResponse> response = await controller.ImportZip(
            project.Id, File(zip), CancellationToken.None);

        ObjectResult failure = response.Result.Should().BeAssignableTo<ObjectResult>().Subject;
        failure.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        Body(failure.Value)["code"]!.GetValue<string>().Should().Be("IMPORT_NOTHING_RECOGNIZED");
        _db.DesignSystemVersions.Count().Should().Be(0);
    }

    [Fact]
    public async Task A_body_without_zip_magic_is_415_and_never_reaches_the_worker()
    {
        var handler = new WorkerHandler();
        DesignSystemsController controller = Controller(handler);
        Project project = SeedProject();

        ActionResult<ZipImportResponse> response = await controller.ImportZip(
            project.Id, File(Utf8("this is not a zip")), CancellationToken.None);

        ObjectResult failure = response.Result.Should().BeAssignableTo<ObjectResult>().Subject;
        failure.StatusCode.Should().Be(StatusCodes.Status415UnsupportedMediaType);
        Body(failure.Value)["code"]!.GetValue<string>().Should().Be("NOT_A_ZIP");
        handler.Requests.Should().BeEmpty();
        _db.DesignSystemVersions.Count().Should().Be(0);
    }

    [Fact]
    public async Task An_unreachable_worker_is_503_import_worker_unavailable()
    {
        DesignSystemsController controller = Controller(new ThrowingHandler(new HttpRequestException("refused")));
        Project project = SeedProject();

        ActionResult<ZipImportResponse> response = await controller.ImportZip(
            project.Id, File(ZipFixtures.Build(("app.css", Utf8(":root { --color-brand-primary: #6d28d9; }")))), CancellationToken.None);

        ObjectResult failure = response.Result.Should().BeAssignableTo<ObjectResult>().Subject;
        failure.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        Body(failure.Value)["code"]!.GetValue<string>().Should().Be("import_worker_unavailable");
        _db.DesignSystemVersions.Count().Should().Be(0);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("tok", false)]
    public async Task An_unset_worker_token_or_base_url_is_503_import_worker_unavailable_without_calling_the_worker(
        string? token, bool hasBaseAddress)
    {
        var handler = new WorkerHandler();
        DesignSystemsController controller = Controller(handler, workerToken: token, withBaseAddress: hasBaseAddress);
        Project project = SeedProject();

        ActionResult<ZipImportResponse> response = await controller.ImportZip(
            project.Id, File(ZipFixtures.Build(("app.css", Utf8(":root { --color-brand-primary: #6d28d9; }")))), CancellationToken.None);

        ObjectResult failure = response.Result.Should().BeAssignableTo<ObjectResult>().Subject;
        failure.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        Body(failure.Value)["code"]!.GetValue<string>().Should().Be("import_worker_unavailable");
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_worker_415_is_400_NOT_A_ZIP()
    {
        DesignSystemsController controller = Controller(new CannedHandler(HttpStatusCode.UnsupportedMediaType, "{\"code\":\"NOT_A_ZIP\"}"));
        Project project = SeedProject();

        ActionResult<ZipImportResponse> response = await controller.ImportZip(
            project.Id, File(ZipFixtures.Build(("app.css", Utf8("a")))), CancellationToken.None);

        ObjectResult failure = response.Result.Should().BeAssignableTo<ObjectResult>().Subject;
        failure.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        Body(failure.Value)["code"]!.GetValue<string>().Should().Be("NOT_A_ZIP");
    }

    [Fact]
    public async Task A_busy_worker_is_503_with_Retry_After_passed_through()
    {
        var handler = new CannedHandler(HttpStatusCode.ServiceUnavailable, "{\"code\":\"WORKER_BUSY\"}")
        {
            RetryAfter = TimeSpan.FromSeconds(9),
        };
        DesignSystemsController controller = Controller(handler);
        Project project = SeedProject();

        ActionResult<ZipImportResponse> response = await controller.ImportZip(
            project.Id, File(ZipFixtures.Build(("app.css", Utf8("a")))), CancellationToken.None);

        ObjectResult failure = response.Result.Should().BeAssignableTo<ObjectResult>().Subject;
        failure.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        Body(failure.Value)["code"]!.GetValue<string>().Should().Be("import_worker_unavailable");
        controller.Response.Headers.RetryAfter.ToString().Should().Be("9");
    }

    [Fact]
    public async Task A_hostile_worker_answer_is_502_IMPORT_WORKER_BAD_RESPONSE()
    {
        string hostile = "{\"files\":[{\"path\":\"../x.json\",\"sha256\":\"" + new string('a', 64)
            + "\",\"size\":2,\"kind\":\"json\",\"text\":\"{}\"}],\"dropped\":[],\"aborted\":false}";
        DesignSystemsController controller = Controller(new CannedHandler(HttpStatusCode.OK, hostile));
        Project project = SeedProject();

        ActionResult<ZipImportResponse> response = await controller.ImportZip(
            project.Id, File(ZipFixtures.Build(("app.css", Utf8("a")))), CancellationToken.None);

        ObjectResult failure = response.Result.Should().BeAssignableTo<ObjectResult>().Subject;
        failure.StatusCode.Should().Be(StatusCodes.Status502BadGateway);
        Body(failure.Value)["code"]!.GetValue<string>().Should().Be("IMPORT_WORKER_BAD_RESPONSE");
        _db.DesignSystemVersions.Count().Should().Be(0);
    }

    [Fact]
    public async Task Skipped_files_are_returned_by_reason_with_bounded_sanitised_examples_and_info_diagnostics()
    {
        byte[] zip = ZipFixtures.Build(
            ("app/globals.css", Utf8(":root { --color-brand-primary: #6d28d9; }")),
            ("a1.js", Utf8("console.log(1)")),
            ("a2.js", Utf8("console.log(2)")),
            ("a3.js", Utf8("console.log(3)")),
            ("a4.js", Utf8("console.log(4)")),
            ("index.html", Utf8("<html><body>x</body></html>")));

        DesignSystemsController controller = Controller(new WorkerHandler());
        Project project = SeedProject();

        ActionResult<ZipImportResponse> response = await controller.ImportZip(
            project.Id, File(zip), CancellationToken.None);

        ObjectResult created = response.Result.Should().BeAssignableTo<ObjectResult>().Subject;
        created.StatusCode.Should().Be(StatusCodes.Status201Created);
        JsonObject body = Body(created.Value);

        JsonArray skipped = body["skipped"]!.AsArray();
        JsonObject scripts = skipped.Select(n => n!.AsObject()).Single(n => n["reason"]!.GetValue<string>() == "DROPPED_SCRIPT");
        scripts["count"]!.GetValue<int>().Should().Be(4);
        scripts["examples"]!.AsArray().Should().HaveCount(3);
        // HTML now crosses the worker as inert markup text, not as a refusal.
        skipped.Select(n => n!["reason"]!.GetValue<string>()).Should().BeEquivalentTo(new[] { "DROPPED_SCRIPT" });

        body["diagnostics"]!.AsArray().Select(n => n!.AsObject())
            .Should().Contain(d => d["severity"]!.GetValue<string>() == "Info"
                && d["code"]!.GetValue<string>() == "FILES_SKIPPED"
                && d["message"]!.GetValue<string>().Contains("4"));
    }

    [Fact]
    public async Task Skipped_examples_show_each_non_ascii_character_as_one_question_mark_instead_of_dropping_the_example()
    {
        string canned = "{\"files\":[{\"path\":\"app.css\",\"sha256\":\"" + new string('a', 64)
            + "\",\"size\":2,\"kind\":\"css\",\"text\":\"a{\"}],\"dropped\":[{\"path\":\"\\ud83d\\ude00caf\\u00e9.js\",\"reason\":\"DROPPED_SCRIPT\"}],\"aborted\":false}";
        DesignSystemsController controller = Controller(new CannedHandler(HttpStatusCode.OK, canned));
        Project project = SeedProject();

        ActionResult<ZipImportResponse> response = await controller.ImportZip(
            project.Id, File(ZipFixtures.Build(("app.css", Utf8("a")))), CancellationToken.None);

        ObjectResult created = response.Result.Should().BeAssignableTo<ObjectResult>().Subject;
        JsonObject group = Body(created.Value)["skipped"]!.AsArray().Single()!.AsObject();
        group["examples"]!.AsArray().Select(n => n!.GetValue<string>()).Should().Equal("?caf?.js");
    }

    [Fact]
    public async Task A_clean_archive_reports_an_empty_skipped_list()
    {
        DesignSystemsController controller = Controller(new WorkerHandler());
        Project project = SeedProject();

        ActionResult<ZipImportResponse> response = await controller.ImportZip(
            project.Id, File(ZipFixtures.Build(("app/globals.css", Utf8(":root { --color-brand-primary: #6d28d9; }")))), CancellationToken.None);

        Body(response.Result.Should().BeAssignableTo<ObjectResult>().Subject.Value)["skipped"]!.AsArray().Should().BeEmpty();
    }

    // ---------------------------------------------------------------------------------------------
    // Task 6: Claude Design assets become project files and the product map becomes a Draft
    // ---------------------------------------------------------------------------------------------

    /// <summary>The PNG signature alone is enough for the magic sniffer; the bytes are opaque here.</summary>
    private static readonly byte[] TinyPng =
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
    ];

    [Fact]
    public async Task A_claude_design_zip_imports_modes_assets_and_a_product_map()
    {
        byte[] zip = ClaudeDesignZip(
            ("assets/acme-logo.png", TinyPng),
            ("assets/acme-app-icon-maskable.png", TinyPng));

        Mock<IFileStorageService> storage = MockStorage();
        Mock<IProjectFileAddedEffects> effects = MockEffects();
        DesignSystemsController controller = Controller(new WorkerHandler(), storage: storage.Object, effects: effects.Object);
        Project project = SeedProject();

        ActionResult<ZipImportResponse> response = await controller.ImportZip(
            project.Id, File(zip), CancellationToken.None);

        ObjectResult created = response.Result.Should().BeAssignableTo<ObjectResult>().Subject;
        created.StatusCode.Should().Be(StatusCodes.Status201Created);
        ZipImportResponse payload = created.Value.Should().BeOfType<ZipImportResponse>().Subject;

        payload.Vendor.Should().Be("claude-design");
        payload.Version.Document["modes"]!["dark"]!.AsObject().Should().NotBeEmpty();
        payload.Version.Document["assets"]!["logo"]!["fileRef"]!.GetValue<string>()
            .Should().StartWith("project-file:");

        payload.ProductMapVersion.Should().NotBeNull();
        payload.ProductMapVersion!.Document["components"]!.AsArray().Should().HaveCount(1);

        payload.Assets.Should().NotBeNull();
        payload.Assets!.Should().Contain(a => a.Role == "logo" && a.Name == "acme-logo.png");
        payload.Assets.Should().Contain(a => a.Role == "icon" && a.Name == "acme-app-icon-maskable.png");

        // Two assets under `assets/` plus the two references the helper export carries
        // (SKILL.md and components/core/Button.prompt.md).
        _db.ProjectFiles.Count().Should().Be(4);
        _db.DesignSystemSources.Where(row => row.VersionId == payload.ProductMapVersion!.Id)
            .Should().NotBeEmpty();
    }

    [Fact]
    public async Task A_quarantined_asset_is_not_referenced()
    {
        byte[] zip = ClaudeDesignZip(("assets/acme-logo.png", TinyPng));

        Mock<IProjectFileAddedEffects> effects = MockEffects();
        int calls = 0;
        effects
            .Setup(e => e.RunAsync(It.IsAny<ProjectFile>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Callback<ProjectFile, Guid, CancellationToken>((file, _, _) =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                    file.CsamStatus = CsamScanStatus.Quarantined;
            })
            .Returns(Task.CompletedTask);

        DesignSystemsController controller = Controller(
            new WorkerHandler(), storage: MockStorage().Object, effects: effects.Object);
        Project project = SeedProject();

        ActionResult<ZipImportResponse> response = await controller.ImportZip(
            project.Id, File(zip), CancellationToken.None);

        ZipImportResponse payload = response.Result.Should().BeAssignableTo<ObjectResult>().Subject
            .Value.Should().BeOfType<ZipImportResponse>().Subject;
        payload.Version.Document.ContainsKey("assets").Should().BeFalse();
        payload.Diagnostics.Should().Contain(d => d.Code == "ASSET_QUARANTINED");
    }

    [Fact]
    public async Task Without_storage_assets_are_reported_not_uploaded()
    {
        byte[] zip = ClaudeDesignZip(("assets/acme-logo.png", TinyPng));

        DesignSystemsController controller = Controller(new WorkerHandler(), storage: null, effects: null);
        Project project = SeedProject();

        ActionResult<ZipImportResponse> response = await controller.ImportZip(
            project.Id, File(zip), CancellationToken.None);

        ZipImportResponse payload = response.Result.Should().BeAssignableTo<ObjectResult>().Subject
            .Value.Should().BeOfType<ZipImportResponse>().Subject;
        payload.Version.Document.ContainsKey("assets").Should().BeFalse();
        payload.Diagnostics.Should().Contain(d => d.Code == "ASSETS_NOT_UPLOADED");
        _db.ProjectFiles.Count().Should().Be(0);
    }

    [Fact]
    public async Task A_claude_design_zip_imports_sanitized_svg_marks_and_the_preview_name()
    {
        byte[] zip = ClaudeDesignZip(
            ("assets/acme-logo.svg", Utf8(
                "<svg xmlns='http://www.w3.org/2000/svg'><script>alert(1)</script><path d='M1 1'/></svg>")),
            ("assets/acme-logo-light.svg", Utf8(
                "<svg xmlns='http://www.w3.org/2000/svg'><path d='M2 2'/></svg>")),
            (".thumbnail", TinyWebp));

        Mock<IFileStorageService> storage = MockStorage();
        DesignSystemsController controller = Controller(
            new WorkerHandler(), storage: storage.Object, effects: MockEffects().Object);
        Project project = SeedProject();

        ActionResult<ZipImportResponse> response = await controller.ImportZip(
            project.Id, File(zip), CancellationToken.None);

        ObjectResult created = response.Result.Should().BeAssignableTo<ObjectResult>().Subject;
        created.StatusCode.Should().Be(StatusCodes.Status201Created);
        ZipImportResponse payload = created.Value.Should().BeOfType<ZipImportResponse>().Subject;

        JsonObject logo = payload.Version.Document["assets"]!["logo"]!.AsObject();
        logo["fileRef"]!.GetValue<string>().Should().StartWith("project-file:");
        logo["name"]!.GetValue<string>().Should().Be("acme-logo.svg");
        logo["variants"]!.AsArray().Single()!["name"]!.GetValue<string>().Should().Be("light");

        // The measured/classified description rides on the slot and each variant; tone is never client input.
        logo["file"]!.GetValue<string>().Should().Be("acme-logo.svg");
        logo["subject"]!.GetValue<string>().Should().Be("product");
        logo["tone"]!.GetValue<string>().Should().Be("dark");
        logo["readableOn"]!.AsArray().Select(n => n!.GetValue<string>()).Should().Equal("light");
        JsonObject lightVariant = logo["variants"]!.AsArray().Single()!.AsObject();
        lightVariant["file"]!.GetValue<string>().Should().Be("acme-logo-light.svg");
        lightVariant["subject"]!.GetValue<string>().Should().Be("product");
        lightVariant["readableOn"]!.AsArray().Should().NotBeEmpty();
        lightVariant["luminance"]!.GetValue<double>().Should().BeInRange(0, 1);

        JsonObject preview = payload.Version.Document["assets"]!["preview"]!.AsObject();
        preview["fileRef"]!.GetValue<string>().Should().StartWith("project-file:");
        preview["name"]!.GetValue<string>().Should().Be(".thumbnail");

        payload.Assets.Should().Contain(a => a.Role == "logo" && a.Variant == "default"
            && a.Name == "acme-logo.svg");
        payload.Assets.Should().Contain(a => a.Role == "preview" && a.Variant == "thumbnail"
            && a.Name == ".thumbnail");

        // Three assets plus SKILL.md and the Button prompt.
        _db.ProjectFiles.Count().Should().Be(5);
        _db.ProjectFiles.Should().OnlyContain(f =>
            f.OriginalPath!.StartsWith("design-system/assets/", StringComparison.Ordinal)
            || f.OriginalPath!.StartsWith("design-system/references/", StringComparison.Ordinal));
    }

    /// <summary>The WOFF2 magic alone is enough for the sniffer; the bytes are opaque here.</summary>
    private static readonly byte[] Woff2Bytes =
        [0x77, 0x4F, 0x46, 0x32, .. new byte[32]];

    [Fact]
    public async Task A_zip_route_writes_fonts_files_only_for_uploaded_files()
    {
        byte[] zip = ClaudeDesignZip(("fonts/Inter-Regular.woff2", Woff2Bytes));

        DesignSystemsController controller = Controller(
            new WorkerHandler(), storage: MockStorage().Object, effects: MockEffects().Object);
        Project project = SeedProject();

        ActionResult<ZipImportResponse> response = await controller.ImportZip(
            project.Id, File(zip), CancellationToken.None);

        ObjectResult created = response.Result.Should().BeAssignableTo<ObjectResult>().Subject;
        created.StatusCode.Should().Be(StatusCodes.Status201Created);
        ZipImportResponse payload = created.Value.Should().BeOfType<ZipImportResponse>().Subject;

        payload.Version.Document["fonts"]!["files"]!["Inter"]!.GetValue<string>()
            .Should().StartWith("project-file:");
        payload.Version.Document["fonts"]!["licence"]!["Inter"]!.GetValue<string>().Should().Be("unknown");

        // A font is a font file in its own map, never folded into the logo/icon/preview slots.
        payload.Version.Document.ContainsKey("assets").Should().BeFalse();
        payload.Assets.Should().Contain(a => a.Role == "font" && a.Name == "Inter-Regular.woff2");

        List<ProjectFile> rows = _db.ProjectFiles.AsNoTracking().ToList();
        rows.Should().Contain(row => row.MimeType == "font/woff2"
            && row.OriginalPath == "design-system/assets/Inter-Regular.woff2"
            && row.SummaryStatus == SummaryStatus.Done
            && row.IndexingStatus == FileIndexingStatus.NotIndexed);
    }

    [Fact]
    public async Task Without_storage_fonts_files_are_not_written()
    {
        byte[] zip = ClaudeDesignZip(("fonts/Inter-Regular.woff2", Woff2Bytes));

        DesignSystemsController controller = Controller(new WorkerHandler(), storage: null, effects: null);
        Project project = SeedProject();

        ActionResult<ZipImportResponse> response = await controller.ImportZip(
            project.Id, File(zip), CancellationToken.None);

        ZipImportResponse payload = response.Result.Should().BeAssignableTo<ObjectResult>().Subject
            .Value.Should().BeOfType<ZipImportResponse>().Subject;
        payload.Version.Document["fonts"]!.AsObject().ContainsKey("files").Should().BeFalse();
        payload.Diagnostics.Should().Contain(d => d.Code == "ASSETS_NOT_UPLOADED");
    }

    // ---------------------------------------------------------------------------------------------
    // Task 4: reference documents become project files and come back in the response
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Claude_references_are_uploaded_as_pending_project_files_and_returned()
    {
        byte[] zip = ClaudeDesignZip(
            ("readme.md", Utf8("# Acme\n\nBrand prose beyond the first paragraph.")),
            ("guidelines/colors.html", Utf8(
                "<html><head><script>alert(1)</script></head><body><b>Primary</b> #7e6fff</body></html>")),
            ("reference/notes.md", Utf8("Reference notes.")));

        Mock<IFileStorageService> storage = MockStorage();
        Mock<IProjectFileAddedEffects> effects = MockEffects();
        DesignSystemsController controller = Controller(
            new WorkerHandler(), storage: storage.Object, effects: effects.Object);
        Project project = SeedProject();

        ActionResult<ZipImportResponse> response = await controller.ImportZip(
            project.Id, File(zip), CancellationToken.None);

        ObjectResult created = response.Result.Should().BeAssignableTo<ObjectResult>().Subject;
        created.StatusCode.Should().Be(StatusCodes.Status201Created);
        ZipImportResponse payload = created.Value.Should().BeOfType<ZipImportResponse>().Subject;

        payload.References.Should().NotBeNull();
        payload.References!.Select(reference => reference.Name).Should().Contain(new[]
        {
            "readme.md",
            "guidelines/colors.txt",
            "reference/notes.md",
            "SKILL.md",
            "components/prompts/Button.prompt.md",
        });

        List<ProjectFile> rows = _db.ProjectFiles.AsNoTracking()
            .Where(file => file.OriginalPath!.StartsWith("design-system/references/", StringComparison.Ordinal))
            .ToList();
        rows.Should().HaveCount(5);
        rows.Should().OnlyContain(row => row.Category == "userFiles"
            && row.SummaryStatus == SummaryStatus.Pending
            && row.IndexingStatus == FileIndexingStatus.Pending);
        rows.Select(row => row.OriginalPath)
            .Should().BeEquivalentTo(payload.References!.Select(reference => $"design-system/references/{reference.Name}"));

        // The guideline is reduced to its visible text before it is stored: no script body ever lands.
        rows.Single(row => row.OriginalFileName == "colors.txt").SizeBytes.Should().Be(15);
        rows.Single(row => row.OriginalFileName == "colors.txt").MimeType.Should().Be("text/plain");

        effects.Verify(
            e => e.RunAsync(It.IsAny<ProjectFile>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Exactly(5));
    }

    [Fact]
    public async Task A_quarantined_reference_is_omitted_with_ASSET_QUARANTINED()
    {
        byte[] zip = ClaudeDesignZip(("readme.md", Utf8("Brand prose.")));

        Mock<IProjectFileAddedEffects> effects = MockEffects();
        effects
            .Setup(e => e.RunAsync(It.IsAny<ProjectFile>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Callback<ProjectFile, Guid, CancellationToken>((file, _, _) =>
            {
                if (file.OriginalPath?.StartsWith("design-system/references/", StringComparison.Ordinal) == true)
                    file.CsamStatus = CsamScanStatus.Quarantined;
            })
            .Returns(Task.CompletedTask);

        DesignSystemsController controller = Controller(
            new WorkerHandler(), storage: MockStorage().Object, effects: effects.Object);
        Project project = SeedProject();

        ActionResult<ZipImportResponse> response = await controller.ImportZip(
            project.Id, File(zip), CancellationToken.None);

        ZipImportResponse payload = response.Result.Should().BeAssignableTo<ObjectResult>().Subject
            .Value.Should().BeOfType<ZipImportResponse>().Subject;
        payload.References.Should().BeNull();
        payload.Diagnostics.Should().Contain(d => d.Code == "ASSET_QUARANTINED");
    }

    [Fact]
    public async Task References_add_one_quota_check_for_their_total_bytes()
    {
        byte[] zip = ClaudeDesignZip(("readme.md", Utf8("Brand prose.")));

        var gate = new Mock<IPlanGateService>();
        gate.Setup(g => g.CheckUploadAsync(It.IsAny<Guid>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PlanGateViolation?)null);

        DesignSystemsController controller = Controller(
            new WorkerHandler(), planGate: gate.Object,
            storage: MockStorage().Object, effects: MockEffects().Object);
        Project project = SeedProject();

        ActionResult<ZipImportResponse> response = await controller.ImportZip(
            project.Id, File(zip), CancellationToken.None);

        response.Result.Should().BeAssignableTo<ObjectResult>();
        gate.Verify(
            g => g.CheckUploadAsync(HomeOrg, It.IsAny<long>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));

        var sizes = gate.Invocations
            .Where(invocation => invocation.Method.Name == "CheckUploadAsync")
            .Select(invocation => (long)invocation.Arguments[1]!)
            .ToList();
        sizes[0].Should().Be(zip.Length);
        sizes[1].Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task An_svg_in_a_non_claude_zip_is_reported_not_read()
    {
        byte[] zip = ZipFixtures.Build(
            ("app/globals.css", Utf8(":root { --color-brand-primary: #6d28d9; }")),
            ("assets/mark.svg", Utf8("<svg xmlns='http://www.w3.org/2000/svg'><path d='M0 0'/></svg>")));

        DesignSystemsController controller = Controller(new WorkerHandler());
        Project project = SeedProject();

        ActionResult<ZipImportResponse> response = await controller.ImportZip(
            project.Id, File(zip), CancellationToken.None);

        ZipImportResponse payload = response.Result.Should().BeAssignableTo<ObjectResult>().Subject
            .Value.Should().BeOfType<ZipImportResponse>().Subject;
        payload.Diagnostics.Should().Contain(d => d.Code == "BUNDLE_MARKUP_NOT_READ");
    }

    [Fact]
    public async Task An_oversized_svg_is_named_in_the_skipped_list()
    {
        byte[] big = Utf8("<svg xmlns='http://www.w3.org/2000/svg'>")
            .Concat(RandomAsciiText(ImportLimits.SvgMaxBytes, seed: 42))
            .Concat(Utf8("</svg>"))
            .ToArray();
        byte[] zip = ZipFixtures.Build(
            ("app/globals.css", Utf8(":root { --color-brand-primary: #6d28d9; }")),
            ("assets/big.svg", big));

        DesignSystemsController controller = Controller(new WorkerHandler());
        Project project = SeedProject();

        ActionResult<ZipImportResponse> response = await controller.ImportZip(
            project.Id, File(zip), CancellationToken.None);

        ObjectResult created = response.Result.Should().BeAssignableTo<ObjectResult>().Subject;
        created.StatusCode.Should().Be(StatusCodes.Status201Created);
        JsonArray skipped = Body(created.Value)["skipped"]!.AsArray();
        skipped.Select(node => node!["reason"]!.GetValue<string>())
            .Should().Contain("SVG_TOO_LARGE", "an over-cap SVG is reported, never silently dropped");
    }

    [Fact]
    public async Task Imported_asset_bytes_get_their_own_quota_check_before_upload()
    {
        byte[] zip = ClaudeDesignZip(
            ("assets/acme-logo.png", TinyPng),
            ("assets/acme-app-icon-maskable.png", TinyPng));

        var gate = new Mock<IPlanGateService>();
        gate.SetupSequence(g => g.CheckUploadAsync(It.IsAny<Guid>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PlanGateViolation?)null)
            .ReturnsAsync(new PlanGateViolation(
                PlanGate.StorageCode, "storage", "This upload would exceed the plan.",
                StatusCodes.Status413PayloadTooLarge));

        DesignSystemsController controller = Controller(
            new WorkerHandler(), planGate: gate.Object,
            storage: MockStorage().Object, effects: MockEffects().Object);
        Project project = SeedProject();

        ActionResult<ZipImportResponse> response = await controller.ImportZip(
            project.Id, File(zip), CancellationToken.None);

        ObjectResult refused = response.Result.Should().BeOfType<ObjectResult>().Subject;
        refused.StatusCode.Should().Be(StatusCodes.Status413PayloadTooLarge);
        Body(refused.Value)["code"]!.GetValue<string>().Should().Be(PlanGate.StorageCode);

        // The compressed archive first, then the uncompressed asset bytes it would store.
        gate.Invocations
            .Where(invocation => invocation.Method.Name == "CheckUploadAsync")
            .Select(invocation => (long)invocation.Arguments[1]!)
            .Should().Equal(zip.Length, TinyPng.Length * 2);
        _db.ProjectFiles.Count().Should().Be(0, "nothing is stored when the asset quota refuses it");
    }

    // ---------------------------------------------------------------------------------------------
    // H1 + M4 + M5: a drop is local to the entry, counted and named in the report
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_uncarriable_font_is_reported_and_never_fails_the_whole_import_with_502()
    {
        // (a) a ninth valid font beyond the 8-entry channel cap, (b) a font over the 4 MiB per-entry cap,
        // (c) a font outside fonts/assets. The first eight are carried; the rest are dropped by reason.
        byte[] fontMagic = [0x77, 0x4F, 0x46, 0x32];
        byte[] tinyFont = [.. fontMagic, .. new byte[64]];

        var entries = new List<(string Name, byte[] Data)>();
        for (int i = 0; i < ArchiveFontLimits.MaxEntries + 1; i++)
            entries.Add(($"fonts/Family{i}-Regular.woff2", tinyFont));
        entries.Add(("fonts/TooBig-Regular.woff2", [.. fontMagic, .. RandomAsciiText(ArchiveFontLimits.MaxEntryBytes, seed: 91)]));
        entries.Add(("reference/Outside-Regular.woff2", tinyFont));

        byte[] zip = ClaudeDesignZip(entries.ToArray());

        DesignSystemsController controller = Controller(
            new WorkerHandler(), storage: MockStorage().Object, effects: MockEffects().Object);
        Project project = SeedProject();

        ActionResult<ZipImportResponse> response = await controller.ImportZip(
            project.Id, File(zip), CancellationToken.None);

        ObjectResult created = response.Result.Should().BeAssignableTo<ObjectResult>().Subject;
        created.StatusCode.Should().Be(
            StatusCodes.Status201Created,
            "a font the worker cannot inline must be dropped and reported, never a 502 for the whole archive");
        ZipImportResponse payload = created.Value.Should().BeOfType<ZipImportResponse>().Subject;

        // The rest of the import is intact: the design document, the fonts that fitted, and the product map.
        DesignDocument.FindToken(payload.Version.Document, "color.background").Should().NotBeNull();
        payload.Version.Document["fonts"]!["files"]!.AsObject().Should().HaveCount(ArchiveFontLimits.MaxEntries);
        payload.ProductMapVersion.Should().NotBeNull();

        payload.Skipped.Select(group => group.Reason).Should().Contain(
            ["FONT_CAP", "FONT_TOO_LARGE", "FONT_NOT_ASSET_PATH"]);

        payload.Report.Should().NotBeNull();
        ImportAxisReportResponse fonts = payload.Report!.Axes.Single(axis => axis.Axis == "Fonts");
        fonts.Dropped.Should().Be(3);
        fonts.Reasons.Select(reason => reason.Code)
            .Should().Contain(["FONT_CAP", "FONT_TOO_LARGE", "FONT_NOT_ASSET_PATH"]);
    }

    [Fact]
    public async Task An_image_outside_assets_appears_in_the_skipped_list_and_the_report()
    {
        byte[] zip = ZipFixtures.Build(
            ("app/globals.css", Utf8(":root { --color-brand-primary: #6d28d9; }")),
            ("reference/shot.png", TinyPng));

        DesignSystemsController controller = Controller(new WorkerHandler());
        Project project = SeedProject();

        ActionResult<ZipImportResponse> response = await controller.ImportZip(
            project.Id, File(zip), CancellationToken.None);

        ObjectResult created = response.Result.Should().BeAssignableTo<ObjectResult>().Subject;
        created.StatusCode.Should().Be(StatusCodes.Status201Created);
        ZipImportResponse payload = created.Value.Should().BeOfType<ZipImportResponse>().Subject;

        payload.Skipped.Should().Contain(group => group.Reason == "IMAGE_NOT_ASSET_PATH");
        payload.Report!.Axes.Single(axis => axis.Axis == "Assets")
            .Reasons.Select(reason => reason.Code).Should().Contain("IMAGE_NOT_ASSET_PATH");
    }

    [Fact]
    public async Task A_skipped_script_reaches_the_report_on_the_reference_documents_axis()
    {
        byte[] zip = ZipFixtures.Build(
            ("app/globals.css", Utf8(":root { --color-brand-primary: #6d28d9; }")),
            ("_ds_bundle.js", Utf8("console.log('never executed');")));

        DesignSystemsController controller = Controller(new WorkerHandler());
        Project project = SeedProject();

        ActionResult<ZipImportResponse> response = await controller.ImportZip(
            project.Id, File(zip), CancellationToken.None);

        ZipImportResponse payload = response.Result.Should().BeAssignableTo<ObjectResult>().Subject
            .Value.Should().BeOfType<ZipImportResponse>().Subject;

        ImportAxisReportResponse references = payload.Report!.Axes
            .Single(axis => axis.Axis == "ReferenceDocuments");
        references.Dropped.Should().Be(1);
        references.Reasons.Select(reason => reason.Code).Should().Contain("DROPPED_SCRIPT");
    }

    [Fact]
    public async Task A_quarantined_asset_reaches_the_report_after_the_final_diagnostics_exist()
    {
        byte[] zip = ClaudeDesignZip(("assets/acme-logo.png", TinyPng));

        Mock<IProjectFileAddedEffects> effects = MockEffects();
        int calls = 0;
        effects
            .Setup(e => e.RunAsync(It.IsAny<ProjectFile>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Callback<ProjectFile, Guid, CancellationToken>((file, _, _) =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                    file.CsamStatus = CsamScanStatus.Quarantined;
            })
            .Returns(Task.CompletedTask);

        DesignSystemsController controller = Controller(
            new WorkerHandler(), storage: MockStorage().Object, effects: effects.Object);
        Project project = SeedProject();

        ActionResult<ZipImportResponse> response = await controller.ImportZip(
            project.Id, File(zip), CancellationToken.None);

        ZipImportResponse payload = response.Result.Should().BeAssignableTo<ObjectResult>().Subject
            .Value.Should().BeOfType<ZipImportResponse>().Subject;

        payload.Diagnostics.Should().Contain(d => d.Code == "ASSET_QUARANTINED");
        payload.Report!.Axes.Single(axis => axis.Axis == "Assets")
            .Reasons.Select(reason => reason.Code).Should().Contain("ASSET_QUARANTINED");
    }

    [Fact]
    public async Task Without_storage_the_route_level_drop_reaches_the_report()
    {
        byte[] zip = ClaudeDesignZip(("assets/acme-logo.png", TinyPng));

        DesignSystemsController controller = Controller(new WorkerHandler(), storage: null, effects: null);
        Project project = SeedProject();

        ActionResult<ZipImportResponse> response = await controller.ImportZip(
            project.Id, File(zip), CancellationToken.None);

        ZipImportResponse payload = response.Result.Should().BeAssignableTo<ObjectResult>().Subject
            .Value.Should().BeOfType<ZipImportResponse>().Subject;

        payload.Diagnostics.Should().Contain(d => d.Code == "ASSETS_NOT_UPLOADED");
        payload.Report!.Axes.Single(axis => axis.Axis == "Assets")
            .Reasons.Select(reason => reason.Code).Should().Contain("ASSETS_NOT_UPLOADED");
    }

    // ---------------------------------------------------------------------------------------------
    // Task 9: the axis-tagged report travels on the zip response
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_zip_response_carries_the_report()
    {
        ZipImportResponse payload = await ImportZipWithNorthwindLikeFixture();

        payload.Report.Should().NotBeNull();
        payload.Report!.ImporterId.Should().Be("claude-design");
        payload.Report.Axes.Should().Contain(a => a.Axis == "PreviewCards" && a.Imported == 3);

        // The wire shape is the T-008 shape: enum names as strings, never integers.
        JsonObject body = Body(payload);
        JsonArray axes = body["report"]!["axes"]!.AsArray();
        axes.Select(axis => axis!["axis"]!.GetValueKind())
            .Should().OnlyContain(kind => kind == JsonValueKind.String);
        axes.Select(axis => axis!["axis"]!.GetValue<string>()).Should().Contain("Assets");
        axes.Select(axis => axis!["support"]!.GetValue<string>()).Should().Contain("Full");
    }

    private async Task<ZipImportResponse> ImportZipWithNorthwindLikeFixture()
    {
        DesignSystemsController controller = Controller(new WorkerHandler());
        Project project = SeedProject();

        ActionResult<ZipImportResponse> response = await controller.ImportZip(
            project.Id, File(NorthwindLikeZip()), CancellationToken.None);

        ObjectResult created = response.Result.Should().BeAssignableTo<ObjectResult>().Subject;
        created.StatusCode.Should().Be(StatusCodes.Status201Created);
        return created.Value.Should().BeOfType<ZipImportResponse>().Subject;
    }

    /// <summary>
    /// A synthetic Northwind-like archive: three manifest cards, a stylesheet with an unresolved colour
    /// mix and a gradient, and a script the worker drops. No client archive is involved.
    /// </summary>
    private static byte[] NorthwindLikeZip() => ZipFixtures.Build(
        ("_ds_manifest.json", Utf8("""
            {
              "namespace": "NorthwindDesignSystem",
              "components": [ { "name": "Button", "sourcePath": "components/core/Button.jsx" } ],
              "globalCssPaths": [ "tokens/colors.css" ],
              "themes": [],
              "cards": [
                { "path": "guidelines/colors-brand.html", "group": "Colors", "name": "Brand" },
                { "path": "components/core/core.card.html", "group": "Components", "name": "Core" },
                { "path": "components/overview/overview.card.html", "group": "Components", "name": "Overview" }
              ]
            }
            """)),
        ("tokens/colors.css", Utf8(
            ":root{--dhr-primary:#7e6fff;"
            + "--dhr-primary-tint-4:color-mix(in srgb,#7e6fff 4%,var(--dhr-surface));"
            + "--dhr-gradient-primary:linear-gradient(82deg,#594ae2 0%,#7467ef 100%)}")),
        ("_ds_bundle.js", Utf8("console.log('never executed');")));

    /// <summary>A WebP RIFF header: enough for the magic sniffer, opaque to every reader.</summary>
    private static readonly byte[] TinyWebp =
    [
        0x52, 0x49, 0x46, 0x46, 0x1A, 0x00, 0x00, 0x00,
        0x57, 0x45, 0x42, 0x50, 0x56, 0x50, 0x38, 0x20,
    ];

    private static byte[] ClaudeDesignZip(params (string Name, byte[] Data)[] assets)
    {
        var entries = new List<(string Name, byte[] Data)>
        {
            ("_ds_manifest.json", Utf8(ManifestWithOneComponent())),
            ("SKILL.md", Utf8("---\nname: acme-design\ndescription: A calm brand.\n---\n")),
            ("tokens/colors.css", Utf8(
                ":root{--nws-background:#ffffff;--nws-text-primary:#111111}" +
                ".theme-dark{--nws-background:#101018;--nws-text-primary:#eeeeee}")),
            ("components/core/Button.d.ts", Utf8(
                "export interface ButtonProps { variant?: string; startIcon?: string }")),
            ("components/core/Button.prompt.md", Utf8("# Button\nA MudBlazor-style button.")),
        };
        entries.AddRange(assets);
        return ZipFixtures.Build(entries.ToArray());
    }

    private static string ManifestWithOneComponent() => """
        {
          "namespace": "AcmeDesignSystem_1",
          "components": [ { "name": "Button", "sourcePath": "components/core/Button.jsx" } ],
          "globalCssPaths": [ "tokens/colors.css" ],
          "themes": [ { "selector": ".theme-dark", "label": "Dark" } ],
          "tokens": [
            { "name": "--nws-background", "value": "#ffffff", "kind": "color", "definedIn": "tokens/colors.css" },
            { "name": "--nws-text-primary", "value": "#111111", "kind": "color", "definedIn": "tokens/colors.css" }
          ]
        }
        """;

    private static Mock<IFileStorageService> MockStorage()
    {
        var storage = new Mock<IFileStorageService>();
        storage.Setup(s => s.UploadAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Stream>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, string>?>(),
                It.IsAny<CancellationToken>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync((
                Guid projectId, Guid fileId, Stream stream, string name, string mime,
                IReadOnlyDictionary<string, string>? metadata, CancellationToken token,
                string category, string? originalPath) => new StoredFileObject(
                    $"projects/{projectId}/userFiles/{fileId}", "test-bucket", "projects", null,
                    category, name, originalPath ?? name, null,
                    $"{fileId}{Path.GetExtension(name)}"));
        return storage;
    }

    private static Mock<IProjectFileAddedEffects> MockEffects()
    {
        var effects = new Mock<IProjectFileAddedEffects>();
        effects.Setup(e => e.RunAsync(It.IsAny<ProjectFile>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return effects;
    }

    private sealed class CannedHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public TimeSpan? RetryAfter { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            if (RetryAfter is { } delta)
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(delta);
            return Task.FromResult(response);
        }
    }

    // ---------------------------------------------------------------------------------------------------
    // Harness
    // ---------------------------------------------------------------------------------------------------

    private DesignSystemsController Controller(
        HttpMessageHandler handler,
        IPlanGateService? planGate = null,
        string? workerToken = WorkerToken,
        bool withBaseAddress = true,
        IFileStorageService? storage = null,
        IProjectFileAddedEffects? effects = null)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Import:Worker:Token"] = workerToken,
            })
            .Build();

        IPlanGateService gate = planGate ?? PassingGate();
        var user = new FakeUser(_ownerId, HomeOrg, OrganizationRole.Owner);

        var controller = new DesignSystemsController(
            _db,
            user,
            new ProjectAccessService(_db, user),
            new DesignSystemService(
                _db, Mock.Of<IFileStorageService>(), gate,
                NullLogger<DesignSystemService>.Instance),
            Mock.Of<IDesignSystemIngestService>(),
            NullLogger<DesignSystemsController>.Instance,
            configuration,
            planGate: gate,
            importWorker: new ImportWorkerClient(
                withBaseAddress
                    ? new HttpClient(handler) { BaseAddress = new Uri("http://import-worker") }
                    : new HttpClient(handler),
                configuration),
            fileStorage: storage,
            fileAddedEffects: effects);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return controller;
    }

    private static IPlanGateService PassingGate()
    {
        var mock = new Mock<IPlanGateService>();
        mock.Setup(g => g.CheckUploadAsync(It.IsAny<Guid>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PlanGateViolation?)null);
        return mock.Object;
    }

    private Project SeedProject()
    {
        var project = new Project
        {
            Id = Guid.NewGuid(),
            Name = "Project",
            OwnerId = _ownerId,
            OrganizationId = HomeOrg,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.Projects.Add(project);
        _db.SaveChanges();
        return project;
    }

    private static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value);

    private static byte[] RandomAsciiText(int length, int seed)
    {
        byte[] data = new byte[length];
        Random random = new(seed);
        for (int i = 0; i < length; i++)
            data[i] = (byte)('a' + random.Next(26));
        return data;
    }

    private static FormFile File(byte[] bytes, string name = "bundle.zip")
    {
        var stream = new MemoryStream(bytes);
        return new FormFile(stream, 0, bytes.Length, "file", name);
    }

    private static byte[] ManyEntries(int count)
    {
        using MemoryStream stream = new();
        using (ZipArchive archive = new(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            for (int i = 0; i < count; i++)
                archive.CreateEntry($"e{i:D5}.txt", CompressionLevel.NoCompression);
        }

        return stream.ToArray();
    }

    private static byte[] ZipWithSymlink(params (string Name, byte[] Data)[] entries)
    {
        using MemoryStream stream = new();
        using (ZipArchive archive = new(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string name, byte[] data) in entries)
            {
                ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Optimal);
                if (name == "link-to-elsewhere")
                    entry.ExternalAttributes = unchecked((int)0xA1FF0000);

                using Stream entryStream = entry.Open();
                entryStream.Write(data, 0, data.Length);
            }
        }

        return stream.ToArray();
    }

    private static JsonObject Body(object? value) => JsonNode
        .Parse(JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)))!
        .AsObject();

    /// <summary>The fake worker: runs the shipped handler over the request body and echoes its answer.</summary>
    private sealed class WorkerHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);

            await using Stream body = await request.Content!.ReadAsStreamAsync(cancellationToken);
            WorkerResult result = await ExtractZipHandler.HandleAsync(
                body, request.Content.Headers.ContentLength, Options, cancellationToken);

            var response = new HttpResponseMessage((HttpStatusCode)result.Status);
            if (result.Body is not null)
            {
                string json = JsonSerializer.Serialize(result.Body, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                response.Content = new StringContent(json, Encoding.UTF8, "application/json");
            }

            return response;
        }
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(exception);
    }

    private sealed class FakeUser(Guid userId, Guid? organizationId, OrganizationRole? role) : ICurrentUser
    {
        public Guid UserId { get; } = userId;
        public string Email { get; } = "u@example.com";
        public bool IsAuthenticated { get; } = true;
        public bool IsPlatformAdmin { get; } = false;
        public Guid? OrganizationId { get; } = organizationId;
        public OrganizationRole? OrgRole { get; } = role;
    }
}
