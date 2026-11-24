using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.ImportWorker;
using Xunit;
using Xunit.Abstractions;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Worker;

public class PdfTextHandlerTests
{
    private static readonly WorkerOptions Options = new("test-token");

    // /Length MUST equal the stream line length (45); a mismatch makes poppler drop the content.
    private const string MinimalPdf =
        "%PDF-1.4\n" +
        "1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj\n" +
        "2 0 obj<</Type/Pages/Kids[3 0 R]/Count 1>>endobj\n" +
        "3 0 obj<</Type/Page/Parent 2 0 R/MediaBox[0 0 300 100]/Contents 4 0 R/Resources<</Font<</F1 5 0 R>>>>>>endobj\n" +
        "4 0 obj<</Length 45>>stream\n" +
        "BT /F1 18 Tf 20 50 Td (Primary #6D28D9) Tj ET\n" +
        "endstream\n" +
        "endobj\n" +
        "5 0 obj<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>endobj\n" +
        "trailer<</Root 1 0 R/Size 6>>\n" +
        "%%EOF\n";

    private readonly ITestOutputHelper _output;

    public PdfTextHandlerTests(ITestOutputHelper output) => _output = output;

    private static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value);

    [Fact]
    public async Task Non_pdf_returns_415()
    {
        FakeRunner runner = new(new PdfTextRunResult(true, "text", false, null));
        byte[] body = Utf8("this is not a pdf file at all");

        WorkerResult result = await PdfTextHandler.HandleAsync(
            new MemoryStream(body),
            body.Length,
            Options,
            runner,
            CancellationToken.None);

        result.Status.Should().Be(415);
        runner.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Oversize_returns_413()
    {
        FakeRunner runner = new(new PdfTextRunResult(true, "text", false, null));

        WorkerResult result = await PdfTextHandler.HandleAsync(
            new MemoryStream(),
            Options.PdfMaxBytes + 1,
            Options,
            runner,
            CancellationToken.None);

        result.Status.Should().Be(413);
        runner.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Runner_failure_returns_422_PDF_UNREADABLE()
    {
        FakeRunner runner = new(new PdfTextRunResult(false, null, false, "PDF_UNREADABLE"));
        byte[] body = Utf8(MinimalPdf);

        WorkerResult result = await PdfTextHandler.HandleAsync(
            new MemoryStream(body),
            body.Length,
            Options,
            runner,
            CancellationToken.None);

        result.Status.Should().Be(422);
        ((ErrorResponse)result.Body!).Error.Should().Be("PDF_UNREADABLE");
        runner.ExistedDuringCall.Should().BeTrue();
        runner.ObservedPath.Should().NotBeNull();
        Path.GetFileName(runner.ObservedPath!).Should().MatchRegex("^import-[0-9a-f]{32}\\.pdf$");
        File.Exists(runner.ObservedPath!).Should().BeFalse();
    }

    [Fact]
    public async Task Success_returns_text()
    {
        FakeRunner runner = new(new PdfTextRunResult(true, "Primary #6D28D9", false, null));
        byte[] body = Utf8(MinimalPdf);

        WorkerResult result = await PdfTextHandler.HandleAsync(
            new MemoryStream(body),
            body.Length,
            Options,
            runner,
            CancellationToken.None);

        result.Status.Should().Be(200);
        PdfTextResponse response = (PdfTextResponse)result.Body!;
        response.Text.Should().Be("Primary #6D28D9");
        response.Truncated.Should().BeFalse();
        runner.ExistedDuringCall.Should().BeTrue();
        File.Exists(runner.ObservedPath!).Should().BeFalse();
    }

    [Theory]
    [InlineData(120, 50, 50)]
    [InlineData(50, 50, 50)]
    [InlineData(8, 50, 8)]
    [InlineData(null, 50, null)]
    public async Task The_response_reports_the_page_count_and_how_many_pages_the_limit_let_through(int? total, int limit, int? expectedRead)
    {
        FakeRunner runner = new(new PdfTextRunResult(true, "text", false, null, total));
        byte[] body = Utf8(MinimalPdf);

        WorkerResult result = await PdfTextHandler.HandleAsync(
            new MemoryStream(body), body.Length, Options with { PdfMaxPages = limit }, runner, CancellationToken.None);

        PdfTextResponse response = (PdfTextResponse)result.Body!;
        response.TotalPages.Should().Be(total);
        response.PagesRead.Should().Be(expectedRead);
    }

    // Was a [Fact] that returned early (green, zero assertions) when pdftotext was missing; now a visible skip.
    [PdfToTextFact]
    public async Task Real_pdftotext_extracts_text()
    {
        byte[] body = Utf8(MinimalPdf);

        WorkerResult result = await PdfTextHandler.HandleAsync(
            new MemoryStream(body),
            body.Length,
            Options,
            new PopplerPdfTextRunner(),
            CancellationToken.None);

        result.Status.Should().Be(200);
        PdfTextResponse response = (PdfTextResponse)result.Body!;
        response.Text.Should().Contain("Primary #6D28D9");
    }

    private sealed class FakeRunner : IPdfTextRunner
    {
        private readonly Func<Task<PdfTextRunResult>> _result;

        public FakeRunner(PdfTextRunResult result)
            => _result = () => Task.FromResult(result);

        public int Calls { get; private set; }

        public string? ObservedPath { get; private set; }

        public bool ExistedDuringCall { get; private set; }

        public async Task<PdfTextRunResult> RunAsync(string path, WorkerOptions options, CancellationToken ct)
        {
            Calls++;
            ObservedPath = path;
            ExistedDuringCall = File.Exists(path);
            return await _result();
        }
    }
}
