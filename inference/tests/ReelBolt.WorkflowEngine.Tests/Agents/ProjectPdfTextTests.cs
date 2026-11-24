#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Moq;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.DesignSystems;
using ReelBolt.Inference.Api.Services.Indexing;
using ReelBolt.Shared.Data.Models;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Agents;

/// <summary>
/// Reading a project PDF's text through the import-worker: the shared reader (caps, cache, every failure message) and
/// the assistant tools that use it. The worker is a stub HTTP handler; no PDF library is involved.
/// </summary>
public sealed class ProjectPdfTextTests
{
    private static readonly byte[] PdfBytes = "%PDF-1.7\n%binary\n"u8.ToArray();

    private sealed class StubWorker(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(respond());
        }
    }

    private static HttpResponseMessage Ok(string text, bool truncated = false, int? totalPages = null, int? pagesRead = null) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { text, truncated, totalPages, pagesRead }), Encoding.UTF8, "application/json")
        };

    private static HttpResponseMessage Status(HttpStatusCode code) => new(code);

    private sealed class Rig
    {
        public readonly StubWorker Worker;
        public readonly FakeFileStorage Storage = new();
        public readonly IProjectFilePdfText Reader;
        public int Downloads;

        public Rig(Func<HttpResponseMessage> respond, bool configured = true, bool? enabled = null, TimeSpan? gateTimeout = null, HttpMessageHandler? handler = null)
        {
            Worker = new StubWorker(respond);
            IConfiguration config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Import:Worker:Token"] = configured ? "tok" : null, ["Import:Pdf:ProjectText:Enabled"] = enabled?.ToString() }).Build();
            var client = new ImportWorkerClient(
                new HttpClient(handler ?? Worker) { BaseAddress = configured ? new Uri("http://import-worker") : null }, config);
            Reader = new ProjectFilePdfText(Storage, client, new ProjectPdfTextCache { GateTimeout = gateTimeout ?? TimeSpan.FromSeconds(30) }, config);
        }

        public ProjectFile Add(byte[]? bytes = null, long? size = null, string name = "report.pdf", string mime = "application/pdf")
        {
            bytes ??= PdfBytes;
            var file = new ProjectFile
            {
                Id = Guid.NewGuid(),
                ProjectId = Guid.NewGuid(),
                OriginalFileName = name,
                OriginalPath = name,
                StorageKey = $"k/{Guid.NewGuid():N}",
                MimeType = mime,
                SizeBytes = size ?? bytes.Length,
                UploadedAt = DateTime.UtcNow,
            };
            Storage.Objects[file.StorageKey] = bytes;
            return file;
        }
    }

    [Fact]
    public async Task Text_is_read_cleaned_and_extracted_once_per_version()
    {
        var rig = new Rig(() => Ok("Revenue\u0000 grew\r\n12%\u0007."));
        ProjectFile file = rig.Add();

        ProjectPdfText first = await rig.Reader.ReadAsync(file, PdfReadPurpose.Interactive, CancellationToken.None);
        ProjectPdfText second = await rig.Reader.ReadAsync(file, PdfReadPurpose.Interactive, CancellationToken.None);

        first.Ok.Should().BeTrue();
        first.Text.Should().Be("Revenue grew\n12%.");
        second.Should().BeSameAs(first);
        rig.Worker.Calls.Should().Be(1);

        file.UploadedAt = file.UploadedAt.AddSeconds(1); // an overwrite is a new version
        await rig.Reader.ReadAsync(file, PdfReadPurpose.Interactive, CancellationToken.None);
        rig.Worker.Calls.Should().Be(2);
    }

    [Fact]
    public async Task The_worker_truncation_flag_and_the_text_cap_are_reported()
    {
        var capped = new Rig(() => Ok("some text", truncated: true));
        (await capped.Reader.ReadAsync(capped.Add(), PdfReadPurpose.Interactive, CancellationToken.None)).Truncated.Should().BeTrue();

        var huge = new Rig(() => Ok(new string('a', ProjectFilePdf.MaxTextChars + 500)));
        ProjectPdfText result = await huge.Reader.ReadAsync(huge.Add(), PdfReadPurpose.Interactive, CancellationToken.None);
        result.Text.Length.Should().Be(ProjectFilePdf.MaxTextChars);
        result.Truncated.Should().BeTrue();
    }

    [Theory]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, ProjectPdfTextFailure.TooLarge)]
    [InlineData(HttpStatusCode.UnsupportedMediaType, ProjectPdfTextFailure.NotPdf)]
    [InlineData(HttpStatusCode.UnprocessableEntity, ProjectPdfTextFailure.Unreadable)]
    [InlineData(HttpStatusCode.ServiceUnavailable, ProjectPdfTextFailure.Busy)]
    [InlineData(HttpStatusCode.InternalServerError, ProjectPdfTextFailure.BadResponse)]
    public async Task Each_worker_answer_maps_to_a_named_failure_with_a_message(HttpStatusCode code, ProjectPdfTextFailure expected)
    {
        var rig = new Rig(() => Status(code));

        ProjectPdfText result = await rig.Reader.ReadAsync(rig.Add(), PdfReadPurpose.Interactive, CancellationToken.None);

        result.Failure.Should().Be(expected);
        result.Text.Should().BeEmpty();
        result.Message.Should().NotBeNullOrWhiteSpace();
        result.Code.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task A_pdf_with_no_text_says_it_is_a_scan_and_that_there_is_no_ocr()
    {
        var rig = new Rig(() => Ok(" \n\u0000 \f "));

        ProjectPdfText result = await rig.Reader.ReadAsync(rig.Add(), PdfReadPurpose.Interactive, CancellationToken.None);

        result.Failure.Should().Be(ProjectPdfTextFailure.NoText);
        result.Code.Should().Be("PDF_NO_TEXT");
        result.Message.Should().Be("This PDF has no selectable text (it looks like a scan or an image-only PDF) and ReelBolt does not do OCR. Upload a text-based export, or paste the text into a text file.");
    }

    [Fact]
    public async Task An_unreachable_or_unconfigured_worker_is_unavailable_and_not_cached()
    {
        var down = new Rig(() => throw new HttpRequestException("refused"));
        ProjectFile file = down.Add();
        (await down.Reader.ReadAsync(file, PdfReadPurpose.Interactive, CancellationToken.None)).Failure.Should().Be(ProjectPdfTextFailure.Unavailable);
        await down.Reader.ReadAsync(file, PdfReadPurpose.Interactive, CancellationToken.None);
        down.Worker.Calls.Should().Be(2, "a worker failure is retried, never remembered");

        var unconfigured = new Rig(() => Ok("x"), configured: false);
        (await unconfigured.Reader.ReadAsync(unconfigured.Add(), PdfReadPurpose.Interactive, CancellationToken.None)).Failure.Should().Be(ProjectPdfTextFailure.Unavailable);
        unconfigured.Worker.Calls.Should().Be(0);
    }

    [Fact]
    public async Task An_over_size_pdf_is_refused_before_download_and_a_non_pdf_body_never_reaches_the_worker()
    {
        var rig = new Rig(() => Ok("x"));

        ProjectPdfText big = await rig.Reader.ReadAsync(rig.Add(size: ProjectFilePdf.MaxPdfBytes + 1), PdfReadPurpose.Interactive, CancellationToken.None);
        big.Failure.Should().Be(ProjectPdfTextFailure.TooLarge);
        big.Message.Should().Be("This PDF is larger than 20 MB, which is the most ReelBolt reads text from. Split it or export a smaller PDF and upload that.");

        ProjectPdfText fake = await rig.Reader.ReadAsync(rig.Add(bytes: "not a pdf at all"u8.ToArray()), PdfReadPurpose.Interactive, CancellationToken.None);
        fake.Failure.Should().Be(ProjectPdfTextFailure.NotPdf);
        rig.Worker.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Carriage_returns_become_line_breaks_before_control_characters_are_dropped()
    {
        var rig = new Rig(() => Ok("one\rtwo\r\nthree\rfour"));

        ProjectPdfText result = await rig.Reader.ReadAsync(rig.Add(), PdfReadPurpose.Interactive, CancellationToken.None);

        result.Text.Should().Be("one\ntwo\nthree\nfour");
    }

    [Fact]
    public async Task A_pdf_of_exactly_20_MiB_is_sent_and_one_byte_more_is_not()
    {
        var rig = new Rig(() => Ok("edge text"));
        byte[] exact = new byte[ProjectFilePdf.MaxPdfBytes];
        PdfBytes.CopyTo(exact, 0);

        (await rig.Reader.ReadAsync(rig.Add(exact), PdfReadPurpose.Interactive, CancellationToken.None)).Text.Should().Be("edge text");
        rig.Worker.Calls.Should().Be(1);

        ProjectPdfText over = await rig.Reader.ReadAsync(rig.Add(bytes: new byte[ProjectFilePdf.MaxPdfBytes + 1], size: ProjectFilePdf.MaxPdfBytes), PdfReadPurpose.Interactive, CancellationToken.None);
        over.Failure.Should().Be(ProjectPdfTextFailure.TooLarge);
        rig.Worker.Calls.Should().Be(1, "the read loop stops at 20 MiB even when the recorded size says less");
    }

    [Fact]
    public async Task Clean_keeps_ZWNJ_and_ZWJ_words_emoji_sequences_combining_marks_and_CJK()
    {
        string persian = "می\u200Cخواهم";
        string devanagari = "क्\u200Dष";
        string emoji = "👨\u200D👩\u200D👧";
        string combining = "e\u0301 日本語 한국어";
        var rig = new Rig(() => Ok($"{persian}\n{devanagari}\n{emoji}\n{combining}"));

        ProjectPdfText result = await rig.Reader.ReadAsync(rig.Add(), PdfReadPurpose.Interactive, CancellationToken.None);

        result.Text.Should().Be($"{persian}\n{devanagari}\n{emoji}\n{combining}");
    }

    [Fact]
    public async Task Clean_still_drops_every_bidi_control_zero_width_space_word_joiner_bom_and_soft_hyphen()
    {
        const string Dropped = "\u202A\u202B\u202C\u202D\u202E\u2066\u2067\u2068\u2069\u200E\u200F\u061C\u200B\u2060\uFEFF\u00AD";
        var rig = new Rig(() => Ok($"a{Dropped}b"));

        ProjectPdfText result = await rig.Reader.ReadAsync(rig.Add(), PdfReadPurpose.Interactive, CancellationToken.None);

        result.Text.Should().Be("ab");
    }

    [Fact]
    public async Task A_cut_with_unknown_page_counts_says_only_part_was_read_and_known_counts_do_not()
    {
        var unknown = new Rig(() => Ok("text", truncated: true));
        ProjectPdfText cut = await unknown.Reader.ReadAsync(unknown.Add(), PdfReadPurpose.Interactive, CancellationToken.None);
        ProjectFilePdf.PageNote(cut).Should().Be("Only part of this document was read; the rest is not included.");

        var whole = new Rig(() => Ok("text"));
        ProjectFilePdf.PageNote(await whole.Reader.ReadAsync(whole.Add(), PdfReadPurpose.Interactive, CancellationToken.None)).Should().BeEmpty();

        var tools = new Tools(() => Ok("text", truncated: true));
        (await tools.Call("ReadProjectFile", ("fileId", tools.Seed().Id))).GetProperty("note").GetString().Should().Contain("Only part of this document was read");
    }

    [Fact]
    public async Task A_background_read_waits_for_a_slot_past_the_gate_timeout_and_completes_but_an_interactive_one_answers_busy()
    {
        var blocking = new BlockingWorker();
        var rig = new Rig(() => Ok("x"), gateTimeout: TimeSpan.FromMilliseconds(100), handler: blocking);
        Task<ProjectPdfText> first = rig.Reader.ReadAsync(rig.Add(), PdfReadPurpose.Background, CancellationToken.None);
        Task<ProjectPdfText> second = rig.Reader.ReadAsync(rig.Add(), PdfReadPurpose.Background, CancellationToken.None);
        while (Volatile.Read(ref blocking.Started) < 2)
            await Task.Delay(10);

        Task<ProjectPdfText> background = rig.Reader.ReadAsync(rig.Add(), PdfReadPurpose.Background, CancellationToken.None);
        ProjectPdfText interactive = await rig.Reader.ReadAsync(rig.Add(), PdfReadPurpose.Interactive, CancellationToken.None);
        await Task.Delay(400);

        interactive.Failure.Should().Be(ProjectPdfTextFailure.Busy);
        background.IsCompleted.Should().BeFalse("a background read is bounded only by its own cancellation, never by the gate timeout");
        blocking.Release.SetResult();
        ProjectPdfText done = await background;
        done.Ok.Should().BeTrue();
        (await first).Ok.Should().BeTrue();
        (await second).Ok.Should().BeTrue();
    }

    [Fact]
    public async Task Clean_drops_bidi_zero_width_and_c1_characters()
    {
        var rig = new Rig(() => Ok("a\u202Eb\u200Bc\u0085d line\ttab\nnext"));

        ProjectPdfText result = await rig.Reader.ReadAsync(rig.Add(), PdfReadPurpose.Interactive, CancellationToken.None);

        result.Text.Should().Be("abcd line\ttab\nnext");
    }

    [Fact]
    public async Task The_off_switch_answers_unavailable_and_never_calls_the_worker()
    {
        var rig = new Rig(() => Ok("x"), enabled: false);
        ProjectFile file = rig.Add();

        ProjectPdfText result = await rig.Reader.ReadAsync(file, PdfReadPurpose.Interactive, CancellationToken.None);

        result.Failure.Should().Be(ProjectPdfTextFailure.Disabled);
        result.Code.Should().Be("PDF_READER_UNAVAILABLE");
        result.Message.Should().Be(ProjectFilePdf.Message(ProjectPdfTextFailure.Disabled)).And.Contain("switched off");
        result.Message.Should().NotContain("Import:").And.NotContain("could not be reached");
        result.Transient.Should().BeFalse();
        rig.Worker.Calls.Should().Be(0);

        var t = new Tools(() => Ok("x"), enabled: false);
        ProjectFile seeded = t.Seed();
        (await t.Call("ReadProjectFile", ("fileId", seeded.Id))).GetProperty("code").GetString().Should().Be("PDF_READER_UNAVAILABLE");
        (await t.Call("ReadProjectFileLines", ("fileId", seeded.Id))).GetProperty("code").GetString().Should().Be("PDF_READER_UNAVAILABLE");
        (await t.Call("GetProjectFileOutline", ("fileId", seeded.Id))).GetProperty("code").GetString().Should().Be("PDF_READER_UNAVAILABLE");
        t.Rig.Worker.Calls.Should().Be(0);
    }

    private sealed class BlockingWorker : HttpMessageHandler
    {
        public readonly TaskCompletionSource Release = new();
        public int Started;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Started);
            await Release.Task;
            return Ok("text");
        }
    }

    [Fact]
    public async Task A_reader_waits_at_most_the_gate_timeout_for_a_slot_then_answers_busy()
    {
        var blocking = new BlockingWorker();
        var rig = new Rig(() => Ok("x"), gateTimeout: TimeSpan.FromMilliseconds(200), handler: blocking);
        Task<ProjectPdfText> first = rig.Reader.ReadAsync(rig.Add(), PdfReadPurpose.Interactive, CancellationToken.None);
        Task<ProjectPdfText> second = rig.Reader.ReadAsync(rig.Add(), PdfReadPurpose.Interactive, CancellationToken.None);
        while (Volatile.Read(ref blocking.Started) < 2)
            await Task.Delay(10);

        ProjectPdfText third = await rig.Reader.ReadAsync(rig.Add(), PdfReadPurpose.Interactive, CancellationToken.None);

        third.Failure.Should().Be(ProjectPdfTextFailure.Busy);
        third.Transient.Should().BeTrue();
        blocking.Release.SetResult();
        (await first).Ok.Should().BeTrue();
        (await second).Ok.Should().BeTrue();
    }

    [Theory]
    [InlineData("application/pdf", "a.bin", true)]
    [InlineData("application/pdf; charset=binary", "a", true)]
    [InlineData("application/octet-stream", "A.PDF", true)]
    [InlineData("", "a.pdf", true)]
    [InlineData("text/plain", "a.pdf", false)]
    [InlineData("image/png", "a.pdf", false)]
    [InlineData("application/zip", "a.zip", false)]
    public void Only_a_pdf_mime_or_a_pdf_name_on_a_generic_mime_is_a_pdf(string mime, string name, bool expected) =>
        ProjectFilePdf.IsPdf(mime, name).Should().Be(expected);

    // ---------------------------------------------------------------- assistant tools

    private sealed class Tools
    {
        public readonly InferenceApiDbContext Db = new(new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        public readonly Guid Owner = Guid.NewGuid();
        public readonly Project Project;
        public readonly Rig Rig;
        public readonly AssistantFileTools FileTools;
        public readonly AssistantToolContext Context;

        public Tools(Func<HttpResponseMessage> respond, bool withReader = true, bool? enabled = null)
        {
            Rig = new Rig(respond, enabled: enabled);
            Project = new Project
            {
                Id = Guid.NewGuid(),
                Name = "Launch",
                Description = "d",
                OwnerId = Owner,
                OrganizationId = TestTenancy.OrgOf(Owner),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            Db.Projects.Add(Project);
            Db.SaveChanges();
            FileTools = new AssistantFileTools(
                Db, ConfirmationTokenService.CreateEphemeral(), Rig.Storage, new Mock<IPublishEndpoint>().Object,
                new RecordingSummarizationQueue(), TestTenancy.Access(Db, new FakeCurrentUser(Owner)),
                pdfText: withReader ? Rig.Reader : null);
            Context = new AssistantToolContext(Owner, Project.Id);
        }

        public ProjectFile Seed(string name = "report.pdf", byte[]? bytes = null, string mime = "application/pdf")
        {
            ProjectFile file = Rig.Add(bytes, name: name, mime: mime);
            file.ProjectId = Project.Id;
            file.Category = "userFiles";
            Db.ProjectFiles.Add(file);
            Db.SaveChanges();
            return file;
        }

        public async Task<JsonElement> Call(string tool, params (string, object?)[] args)
        {
            AIFunction function = FileTools.GetTools(Context).Single(t => t.Name == tool);
            var arguments = new AIFunctionArguments();
            foreach ((string key, object? value) in args)
                arguments[key] = value;
            object? raw = await function.InvokeAsync(arguments);
            string json = raw is JsonElement { ValueKind: JsonValueKind.String } s ? s.GetString()! : raw?.ToString() ?? "{}";
            return JsonDocument.Parse(json).RootElement.Clone();
        }
    }

    [Fact]
    public async Task ReadProjectFile_returns_a_pdfs_text_with_the_page_note()
    {
        var t = new Tools(() => Ok("Chapter one.\nThe numbers are good."));
        ProjectFile file = t.Seed();

        JsonElement result = await t.Call("ReadProjectFile", ("fileId", file.Id));

        result.GetProperty("content").GetString().Should().Be("Chapter one.\nThe numbers are good.");
        result.GetProperty("source").GetString().Should().Be("pdf-text");
        result.GetProperty("truncated").GetBoolean().Should().BeFalse();
        result.GetProperty("note").GetString().Should().BeEmpty("a PDF that was read in full says nothing about pages");
    }

    [Fact]
    public async Task ReadProjectFile_names_the_pages_that_were_really_left_out_from_the_workers_counts()
    {
        var t = new Tools(() => Ok("Chapter one.", totalPages: 30, pagesRead: 7));
        ProjectFile file = t.Seed();

        JsonElement result = await t.Call("ReadProjectFile", ("fileId", file.Id));

        result.GetProperty("note").GetString().Should().Be("Only the first 7 of 30 pages of this PDF were read; text from later pages is not included.");
        (await t.Call("ReadProjectFileLines", ("fileId", file.Id))).GetProperty("note").GetString().Should().Contain("first 7 of 30 pages");
        (await t.Call("GetProjectFileOutline", ("fileId", file.Id))).GetProperty("note").GetString().Should().Contain("first 7 of 30 pages").And.Contain("ReadProjectFileLines");
    }

    [Theory]
    [InlineData(null, null, false)]
    [InlineData(12, 12, false)]
    [InlineData(5, 5, false)]
    [InlineData(120, 50, true)]
    [InlineData(3, 5, false)] // more read than exist is an impossible answer: ignored, never trusted
    [InlineData(-1, -1, false)]
    public async Task Pages_are_reported_cut_only_when_the_worker_says_it_read_fewer_than_exist(int? total, int? read, bool cut)
    {
        var rig = new Rig(() => Ok("text", totalPages: total, pagesRead: read));

        ProjectPdfText result = await rig.Reader.ReadAsync(rig.Add(), PdfReadPurpose.Interactive, CancellationToken.None);

        result.PagesCut.Should().Be(cut);
        string note = ProjectFilePdf.PageNote(result);
        if (cut)
            note.Should().Contain($"first {read} of {total} pages");
        else
            note.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadProjectFile_returns_exactly_60000_characters_without_flagging_a_cut()
    {
        var t = new Tools(() => Ok(new string('x', ProjectFileTargetedEdits.MaxReadChars)));
        ProjectFile file = t.Seed();

        JsonElement result = await t.Call("ReadProjectFile", ("fileId", file.Id));

        result.GetProperty("content").GetString()!.Length.Should().Be(ProjectFileTargetedEdits.MaxReadChars);
        result.GetProperty("truncated").GetBoolean().Should().BeFalse();
        result.GetProperty("note").GetString().Should().BeEmpty();
    }

    [Fact]
    public async Task ReadProjectFile_caps_a_pdfs_text_at_60000_characters_and_flags_it()
    {
        var t = new Tools(() => Ok(new string('x', 150_000)));
        ProjectFile file = t.Seed();

        JsonElement result = await t.Call("ReadProjectFile", ("fileId", file.Id));

        result.GetProperty("content").GetString()!.Length.Should().Be(ProjectFileTargetedEdits.MaxReadChars);
        result.GetProperty("truncated").GetBoolean().Should().BeTrue();
        result.GetProperty("note").GetString().Should().Contain("ReadProjectFileLines");
    }

    [Fact]
    public async Task ReadProjectFile_on_a_scanned_pdf_names_the_reason_and_what_to_do()
    {
        var t = new Tools(() => Ok(""));
        ProjectFile file = t.Seed();

        JsonElement result = await t.Call("ReadProjectFile", ("fileId", file.Id));

        result.GetProperty("code").GetString().Should().Be("PDF_NO_TEXT");
        result.GetProperty("error").GetString().Should().Contain("scan").And.Contain("OCR").And.Contain("Upload a text-based export");
        result.TryGetProperty("content", out _).Should().BeFalse();
    }

    [Fact]
    public async Task ReadProjectFile_with_the_worker_down_is_a_message_not_an_exception()
    {
        var t = new Tools(() => throw new HttpRequestException("refused"));
        ProjectFile file = t.Seed();

        JsonElement result = await t.Call("ReadProjectFile", ("fileId", file.Id));

        result.GetProperty("code").GetString().Should().Be("PDF_READER_UNAVAILABLE");
        result.GetProperty("error").GetString().Should().Contain("Try again");
    }

    [Fact]
    public async Task Other_binaries_and_a_pdf_without_the_reader_are_still_refused()
    {
        var t = new Tools(() => Ok("x"));
        ProjectFile video = t.Seed("clip.mp4", [0, 0, 0, 0x18], "video/mp4");
        (await t.Call("ReadProjectFile", ("fileId", video.Id))).GetProperty("error").GetString().Should().Contain("not a text-editable asset");

        var bare = new Tools(() => Ok("x"), withReader: false);
        ProjectFile pdf = bare.Seed();
        (await bare.Call("ReadProjectFile", ("fileId", pdf.Id))).GetProperty("error").GetString().Should().Contain("not a text-editable asset");
    }

    [Fact]
    public async Task ReadProjectFileLines_slices_a_pdfs_text_and_the_outline_reports_its_size()
    {
        var t = new Tools(() => Ok("line one\nline two\nline three"));
        ProjectFile file = t.Seed();

        JsonElement lines = await t.Call("ReadProjectFileLines", ("fileId", file.Id), ("startLine", 2), ("lineCount", 1));
        lines.GetProperty("content").GetString().Should().Contain("line two").And.NotContain("line three");
        lines.GetProperty("totalLines").GetInt32().Should().Be(3);
        lines.GetProperty("note").GetString().Should().BeEmpty();

        JsonElement outline = await t.Call("GetProjectFileOutline", ("fileId", file.Id));
        outline.GetProperty("kind").GetString().Should().Be("pdf");
        outline.GetProperty("totalChars").GetInt32().Should().Be("line one\nline two\nline three".Length);
        t.Rig.Worker.Calls.Should().Be(1, "the three reads share one extraction");
    }

    [Fact]
    public async Task Write_and_edit_tools_still_refuse_a_pdf()
    {
        var t = new Tools(() => Ok("hello world"));
        ProjectFile file = t.Seed();
        JsonElement edits = JsonSerializer.SerializeToElement(new[] { new { oldText = "hello", newText = "bye", replaceAll = false } });

        JsonElement result = await t.Call("ProposeEditProjectFile", ("fileId", file.Id), ("edits", edits));

        result.GetProperty("error").GetString().Should().Contain("not a text-editable asset");
        t.Rig.Storage.Objects[file.StorageKey].Should().Equal(PdfBytes);
    }
}
