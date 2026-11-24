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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Inference.Api.Agents;
using ReelBolt.Inference.Api.Consumers;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Background;
using ReelBolt.Inference.Api.Services.DesignSystems;
using ReelBolt.Inference.Api.Services.Indexing;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Inference.Api.Services.VectorSearch;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.IntegrationEvents;
using ReelBolt.Shared.Metering;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.InferenceApiReliability;

/// <summary>
/// Only human-readable text is summarized and embedded; images are described once by the Vision
/// provider and that description is the summary and the embedded text; every other kind is skipped
/// without any model call.
/// </summary>
public class ProjectFileContentIndexingTests
{
    // ---------------------------------------------------------------- byte fixtures

    private static readonly byte[] PngBytes =
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52, 0x00, 0x00, 0x00, 0x01];

    private static readonly byte[] JpegBytes = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01];

    private static readonly byte[] WebpBytes =
        [.. "RIFF"u8.ToArray(), 0x24, 0x00, 0x00, 0x00, .. "WEBPVP8 "u8.ToArray(), 0x00, 0x00, 0x00, 0x00];

    private static readonly byte[] Mp4Bytes =
        [0x00, 0x00, 0x00, 0x18, .. "ftypmp42"u8.ToArray(), 0x00, 0x00, 0x00, 0x00, .. "mp42isom"u8.ToArray()];

    private static byte[] RandomBinary()
    {
        byte[] bytes = new byte[512];
        new Random(42).NextBytes(bytes);
        bytes[3] = 0x00;
        return bytes;
    }

    private static readonly Encoding Windows1252 = CodePagesEncodingProvider.Instance.GetEncoding(1252)!;

    /// <summary>No NUL, invalid UTF-8, and mostly C0/C1 control bytes in every single-byte reading.</summary>
    private static byte[] ControlHeavy()
    {
        byte[] bytes = new byte[256];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = (byte)(i % 4 == 0 ? 0x81 : 1 + i % 0x1F);
        return bytes;
    }

    // ---------------------------------------------------------------- classifier

    public static TheoryData<string, string?, string?, byte[], ProjectFileContentKind> ClassifierCases()
    {
        byte[] truncatedMultiByte = new byte[ProjectFileContentClassifier.SniffBytes];
        Array.Fill(truncatedMultiByte, (byte)'a');
        truncatedMultiByte[^1] = 0xC3; // first byte of "é", its continuation byte lies past the head

        return new TheoryData<string, string?, string?, byte[], ProjectFileContentKind>
        {
            // .ts collides with MPEG transport streams: OS / FileExtensionContentTypeProvider MIME maps call
            // a TypeScript file video/mp2t or video/vnd.dlna.mpeg-tts, so for those two the sniff decides.
            { "ts ascii labelled video/mp2t", "video/mp2t", "index.ts", "export const x = 1;\n"u8.ToArray(), ProjectFileContentKind.Text },
            { "ts ascii labelled mpeg-tts", "video/vnd.dlna.mpeg-tts", "index.ts", "export const x = 1;\n"u8.ToArray(), ProjectFileContentKind.Text },
            { "real mpeg-ts", "video/mp2t", "stream.ts", [0x47, 0x40, 0x00, 0x10, 0x00, 0x00, 0xB0, 0x0D], ProjectFileContentKind.Other },
            { "ts ascii as text", "text/plain", "index.ts", "export const x = 1;\n"u8.ToArray(), ProjectFileContentKind.Text },
            { "ts ascii no mime", null, "index.ts", "export const x = 1;\n"u8.ToArray(), ProjectFileContentKind.Text },
            { "md utf8 emoji", "text/markdown", "README.md", Encoding.UTF8.GetBytes("# Launch \U0001F680\n\nCafé ready.\n"), ProjectFileContentKind.Text },
            { "empty", "text/plain", "empty.txt", Array.Empty<byte>(), ProjectFileContentKind.Text },
            { "head ends mid multi-byte char", "text/plain", "long.txt", truncatedMultiByte, ProjectFileContentKind.Text },
            { "txt with NUL", "text/plain", "notes.txt", "abc\0def"u8.ToArray(), ProjectFileContentKind.Other },
            { "json invalid utf8 reads as windows-1252", "application/json", "data.json", [(byte)'{', (byte)'"', 0xC3, 0x28, (byte)'"', (byte)'}'], ProjectFileContentKind.Text },
            { "utf16 le bom", "text/plain", "wide.txt", [0xFF, 0xFE, (byte)'h', 0x00, (byte)'i', 0x00], ProjectFileContentKind.Text },
            { "utf16 be bom", "text/plain", "wide.txt", [0xFE, 0xFF, 0x00, (byte)'h', 0x00, (byte)'i'], ProjectFileContentKind.Text },
            { "utf16 le without bom", "text/plain", "wide.txt", Encoding.Unicode.GetBytes("Hello, café!\r\n"), ProjectFileContentKind.Text },
            { "utf16 be without bom", null, "wide.csv", Encoding.BigEndianUnicode.GetBytes("a,b,c\n1,2,3\n"), ProjectFileContentKind.Text },
            { "utf32 le bom", "text/plain", "wide.txt", [.. Encoding.UTF32.GetPreamble(), .. Encoding.UTF32.GetBytes("hi there")], ProjectFileContentKind.Text },
            { "windows-1252", "text/plain", "legacy.txt", Windows1252.GetBytes("Caf\u00e9 \u201cquoted\u201d costs \u20ac5 \u2014 na\u00efve.\r\n"), ProjectFileContentKind.Text },
            { "utf16 le bom over binary", "text/plain", "wide.txt", [0xFF, 0xFE, .. RandomBinary()], ProjectFileContentKind.Other },
            { "control-heavy without NUL", "application/octet-stream", "blob", ControlHeavy(), ProjectFileContentKind.Other },
            { "random binary", "application/octet-stream", "blob.bin", RandomBinary(), ProjectFileContentKind.Other },
            { "octet-stream that sniffs as text", "application/octet-stream", "Makefile", "all:\n\tbuild\n"u8.ToArray(), ProjectFileContentKind.Text },
            { "png", "image/png", "logo.png", PngBytes, ProjectFileContentKind.Image },
            { "jpeg as octet-stream + .jpg", "application/octet-stream", "photo.jpg", JpegBytes, ProjectFileContentKind.Image },
            { "webp", "image/webp", "hero.webp", WebpBytes, ProjectFileContentKind.Image },
            { "image/png on ascii bytes", "image/png", "fake.png", "not really a picture"u8.ToArray(), ProjectFileContentKind.Other },
            { "svg", "image/svg+xml", "icon.svg", "<svg xmlns=\"http://www.w3.org/2000/svg\"></svg>"u8.ToArray(), ProjectFileContentKind.Other },
            { "mp4", "video/mp4", "clip.mp4", Mp4Bytes, ProjectFileContentKind.Other },
            { "pdf", "application/pdf", "deck.pdf", "%PDF-1.7\n%âã\n"u8.ToArray(), ProjectFileContentKind.Other },
            { "zip", "application/zip", "bundle.zip", [0x50, 0x4B, 0x03, 0x04, 0x14, 0x00, 0x00, 0x00], ProjectFileContentKind.Other },
        };
    }

    [Theory]
    [MemberData(nameof(ClassifierCases))]
    public void The_classifier_decides_text_image_or_other(
        string because, string? mime, string? fileName, byte[] head, ProjectFileContentKind expected)
    {
        ProjectFileContentClassifier.Classify(mime, fileName, head).Should().Be(expected, because);
    }

    [Theory]
    [InlineData("utf8", 65001)]
    [InlineData("utf8-bom", 65001)]
    [InlineData("utf16le-bom", 1200)]
    [InlineData("utf16be-bom", 1201)]
    [InlineData("utf16le", 1200)]
    [InlineData("utf16be", 1201)]
    [InlineData("windows-1252", 1252)]
    public void Text_encoding_is_detected_from_the_head(string sample, int codePage)
    {
        const string text = "Caf\u00e9 launch notes\n";
        byte[] head = sample switch
        {
            "utf8" => Encoding.UTF8.GetBytes(text),
            "utf8-bom" => [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(text)],
            "utf16le-bom" => [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(text)],
            "utf16be-bom" => [.. Encoding.BigEndianUnicode.GetPreamble(), .. Encoding.BigEndianUnicode.GetBytes(text)],
            "utf16le" => Encoding.Unicode.GetBytes(text),
            "utf16be" => Encoding.BigEndianUnicode.GetBytes(text),
            _ => Windows1252.GetBytes(text),
        };

        ProjectFileContentClassifier.DetectTextEncoding(head).Should().NotBeNull()
            .And.Subject.As<Encoding>().CodePage.Should().Be(codePage);
    }

    [Fact]
    public void Binary_heads_get_no_text_encoding()
    {
        ProjectFileContentClassifier.DetectTextEncoding(RandomBinary()).Should().BeNull();
        ProjectFileContentClassifier.DetectTextEncoding(Mp4Bytes).Should().BeNull();
        ProjectFileContentClassifier.DetectTextEncoding(ControlHeavy()).Should().BeNull();
        ProjectFileContentClassifier.DetectTextEncoding("abc\0def"u8).Should().BeNull();
        for (int seed = 0; seed < 50; seed++)
        {
            byte[] noise = new byte[4096];
            new Random(seed).NextBytes(noise);
            ProjectFileContentClassifier.DetectTextEncoding([0xFF, 0xFE, .. noise]).Should().BeNull("UTF-16 LE BOM over noise, seed {0}", seed);
            ProjectFileContentClassifier.DetectTextEncoding([0xFE, 0xFF, .. noise]).Should().BeNull("UTF-16 BE BOM over noise, seed {0}", seed);
            ProjectFileContentClassifier.DetectTextEncoding(noise).Should().BeNull("plain noise, seed {0}", seed);
        }
    }

    [Fact]
    public void Magic_bytes_decide_the_mime_sent_to_vision()
    {
        ProjectFileContentClassifier.VisionMimeType(PngBytes).Should().Be("image/png");
        ProjectFileContentClassifier.VisionMimeType(JpegBytes).Should().Be("image/jpeg");
        ProjectFileContentClassifier.VisionMimeType("GIF89a\x01\x00"u8.ToArray()).Should().Be("image/gif");
        ProjectFileContentClassifier.VisionMimeType(WebpBytes).Should().Be("image/webp");
        ProjectFileContentClassifier.VisionMimeType("plain"u8.ToArray()).Should().BeNull();
    }

    [Fact]
    public async Task ReadHead_fills_the_head_from_a_stream_that_returns_short_reads()
    {
        byte[] data = Enumerable.Range(0, 20_000).Select(i => (byte)'a').ToArray();
        using var stream = new TrickleStream(data, 100);

        byte[] head = await ProjectFileContentClassifier.ReadHeadAsync(stream, CancellationToken.None);

        head.Length.Should().Be(ProjectFileContentClassifier.SniffBytes);
        (await ProjectFileContentClassifier.ReadHeadAsync(new MemoryStream([1, 2, 3]), CancellationToken.None))
            .Should().Equal(1, 2, 3);
    }

    private sealed class TrickleStream(byte[] data, int maxRead) : MemoryStream(data)
    {
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, maxRead));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, maxRead)], cancellationToken);
    }

    // ---------------------------------------------------------------- shared harness

    private sealed class CapturingChatClient(string reply) : IChatClient
    {
        public List<ChatMessage> Messages { get; } = new();
        public int Calls { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            Messages.AddRange(messages);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class CapturingEmbeddings : IEmbeddingGenerator<string, Embedding<float>>
    {
        public List<string> Inputs { get; } = new();

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
        {
            List<string> list = values.ToList();
            Inputs.AddRange(list);
            return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(list.Select(_ => new Embedding<float>(new float[] { 1, 2, 3 }))));
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class CapturingChunker : IFileChunker
    {
        public List<string> FileNames { get; } = new();

        public IReadOnlyList<FileChunk> Chunk(string content, string fileName)
        {
            FileNames.Add(fileName);
            return content.Length == 0 ? [] : [new FileChunk(0, 1, "text", content)];
        }
    }

    private sealed class Harness
    {
        public string DbName { get; } = Guid.NewGuid().ToString();
        public Project Project { get; }
        public Mock<IFileStorageService> Storage { get; } = new();
        public Mock<IPublishEndpoint> Publish { get; } = new();
        public Mock<IAgentRegistry> Agents { get; } = new();
        public Mock<IReelBoltAgent> SummarizerAgent { get; } = new();
        public Mock<IInferenceProviderResolver> Resolver { get; } = new();
        public Mock<IChatClientFactory> ChatClients { get; } = new();
        public CapturingChatClient Vision { get; }
        public CapturingEmbeddings Embeddings { get; } = new();
        public CapturingChunker Chunker { get; } = new();
        public Mock<IVectorIndexService> Vectors { get; } = new();
        public IProjectFilePdfText? Pdf { get; set; }

        public static readonly ResolvedInferenceProvider VisionProvider =
            new(Guid.NewGuid(), "vision", InferenceProviderKind.OpenAICompatible, "https://vision.example", "gpt-vision", "key", 30);

        public Harness(string visionReply = "A red rocket launching over a city skyline, with the words \"Ship it\".")
        {
            Vision = new CapturingChatClient(visionReply);
            using InferenceApiDbContext db = NewContext();
            Project = new Project { Id = Guid.NewGuid(), Name = "P", OwnerId = Guid.NewGuid(), OrganizationId = Guid.NewGuid() };
            db.Projects.Add(Project);
            db.SaveChanges();

            Agents.Setup(a => a.GetByType(AgentType.FileSummarizerAgent)).Returns(SummarizerAgent.Object);
            SummarizerAgent.Setup(a => a.RunAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new AgentRunResult { Output = "{\"fileType\":\"text\",\"summary\":\"text summary\"}" });
            ChatClients.Setup(f => f.Get(It.IsAny<ResolvedInferenceProvider>())).Returns(Vision);
        }

        public void WithVision() =>
            Resolver.Setup(r => r.ResolveVisionAsync(null, It.IsAny<CancellationToken>())).ReturnsAsync(VisionProvider);

        public void WithoutVision() =>
            Resolver.Setup(r => r.ResolveVisionAsync(null, It.IsAny<CancellationToken>())).ReturnsAsync((ResolvedInferenceProvider?)null);

        public InferenceApiDbContext NewContext() =>
            new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(DbName).Options);

        public ProjectFile AddFile(
            string name,
            string mime,
            byte[] bytes,
            SummaryStatus summary = SummaryStatus.Pending,
            string? agentSummary = null,
            long? sizeBytes = null)
        {
            var file = new ProjectFile
            {
                Id = Guid.NewGuid(),
                ProjectId = Project.Id,
                OriginalFileName = name,
                OriginalPath = $"assets/{name}",
                Category = "userFiles",
                MimeType = mime,
                StorageKey = $"projects/{Project.Id}/userFiles/{Guid.NewGuid():N}_{name}",
                StorageFileName = name,
                SizeBytes = sizeBytes ?? bytes.Length,
                SummaryStatus = summary,
                AgentSummary = agentSummary,
                IndexingStatus = FileIndexingStatus.Pending,
                UploadedAt = DateTime.UtcNow
            };
            using InferenceApiDbContext db = NewContext();
            db.ProjectFiles.Add(file);
            db.SaveChanges();
            Storage.Setup(s => s.DownloadAsync(Project.Id, file.StorageKey, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new MemoryStream(bytes));
            return file;
        }

        public ProjectFile Reload(Guid id)
        {
            using InferenceApiDbContext db = NewContext();
            return db.ProjectFiles.AsNoTracking().Single(f => f.Id == id);
        }

        public async Task SummarizeAsync(ProjectFile file)
        {
            var services = new ServiceCollection();
            services.AddScoped(_ => NewContext());
            services.AddSingleton(Agents.Object);
            services.AddSingleton(Storage.Object);
            services.AddSingleton(Resolver.Object);
            services.AddSingleton(ChatClients.Object);
            services.AddSingleton(Publish.Object);
            if (Pdf is not null)
                services.AddSingleton(Pdf);
            var service = new FileSummarizationService(
                new ChannelBackgroundTaskQueue<FileSummarizationTask>(10),
                services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
                NullLogger<FileSummarizationService>.Instance,
                Options.Create(new FileSummarizationOptions()));
            await service.ProcessAsync(new FileSummarizationTask(file.Id), CancellationToken.None);
        }

        public async Task IndexAsync(ProjectFile file, int? maxTextBytes = null)
        {
            using InferenceApiDbContext db = NewContext();
            var consumer = new ProjectFileIndexingConsumer(
                db, Storage.Object, Chunker, Embeddings, Vectors.Object, NullLogger<ProjectFileIndexingConsumer>.Instance,
                options: maxTextBytes is { } cap ? Options.Create(new FileIndexingOptions { MaxTextBytes = cap }) : null,
                pdfText: Pdf);
            Mock<ConsumeContext<ProjectFileIndexingRequested>> context = new();
            context.SetupGet(c => c.Message).Returns(new ProjectFileIndexingRequested
            {
                ProjectId = Project.Id,
                FileId = file.Id,
                Operation = "Upsert"
            });
            context.SetupGet(c => c.CancellationToken).Returns(CancellationToken.None);
            await consumer.Consume(context.Object);
        }

        public void VerifyNoSummarizerCall() =>
            SummarizerAgent.Verify(a => a.RunAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Never);

        public void VerifyUpserted(Times times) =>
            Vectors.Verify(v => v.UpsertFileChunksAsync(
                It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<VectorizedFileChunk>>(), It.IsAny<CancellationToken>()), times);
    }

    // ---------------------------------------------------------------- PDF text

    private static readonly byte[] PdfBytes = "%PDF-1.7\n%binary\n"u8.ToArray();

    private static IProjectFilePdfText PdfReturning(ProjectPdfText answer) => new FakePdfText(answer);

    private sealed class FakePdfText(ProjectPdfText answer) : IProjectFilePdfText
    {
        public Task<ProjectPdfText> ReadAsync(ProjectFile file, PdfReadPurpose purpose, CancellationToken ct) => Task.FromResult(answer);
    }

    private static ProjectPdfText PdfOk(string text, bool truncated = false) =>
        new(ProjectPdfTextFailure.None, text, truncated, null);

    private static ProjectPdfText PdfFail(ProjectPdfTextFailure failure) =>
        new(failure, string.Empty, false, ProjectFilePdf.Message(failure));

    [Fact]
    public async Task A_pdf_is_summarized_from_its_extracted_text()
    {
        var h = new Harness { Pdf = PdfReturning(PdfOk("Quarterly report: revenue grew 12%.")) };
        ProjectFile file = h.AddFile("report.pdf", "application/pdf", PdfBytes);

        await h.SummarizeAsync(file);

        h.SummarizerAgent.Verify(a => a.RunAsync(It.Is<string>(p => p.Contains("revenue grew 12%") && !p.Contains("[truncated]")), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Once);
        ProjectFile stored = h.Reload(file.Id);
        stored.SummaryStatus.Should().Be(SummaryStatus.Done);
        stored.AgentSummary.Should().Contain("text summary");
    }

    // 'Ξ' is two UTF-8 bytes and appears nowhere in the prompt template, so counting it counts the bytes kept.
    private const int SummarizerCapBytes = 524288;

    [Fact]
    public async Task A_long_pdf_text_is_cut_to_the_summarizer_cap_and_says_so()
    {
        var h = new Harness { Pdf = PdfReturning(PdfOk(new string('Ξ', SummarizerCapBytes / 2 + 1))) };
        ProjectFile file = h.AddFile("big.pdf", "application/pdf", PdfBytes);

        await h.SummarizeAsync(file);

        h.SummarizerAgent.Verify(a => a.RunAsync(
            It.Is<string>(p => p.Contains("[truncated]") && p.Count(c => c == 'Ξ') == SummarizerCapBytes / 2), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_pdf_text_exactly_at_the_summarizer_cap_is_sent_whole_without_the_truncated_marker()
    {
        var h = new Harness { Pdf = PdfReturning(PdfOk(new string('Ξ', SummarizerCapBytes / 2))) };
        ProjectFile file = h.AddFile("edge.pdf", "application/pdf", PdfBytes);

        await h.SummarizeAsync(file);

        h.SummarizerAgent.Verify(a => a.RunAsync(
            It.Is<string>(p => !p.Contains("[truncated]") && p.Count(c => c == 'Ξ') == SummarizerCapBytes / 2), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(ProjectPdfTextFailure.NoText)]
    [InlineData(ProjectPdfTextFailure.TooLarge)]
    [InlineData(ProjectPdfTextFailure.Unreadable)]
    [InlineData(ProjectPdfTextFailure.NotPdf)]
    public async Task A_pdf_the_document_cannot_yield_is_skipped_without_a_model_call(ProjectPdfTextFailure failure)
    {
        var h = new Harness { Pdf = PdfReturning(PdfFail(failure)) };
        ProjectFile file = h.AddFile("scan.pdf", "application/pdf", PdfBytes);

        await h.SummarizeAsync(file);

        h.VerifyNoSummarizerCall();
        ProjectFile stored = h.Reload(file.Id);
        stored.SummaryStatus.Should().Be(SummaryStatus.Done);
        // The plain reason is the stored summary, so a reader sent to agentSummary (the engine cannot open a PDF) learns why.
        using JsonDocument summary = JsonDocument.Parse(stored.AgentSummary!);
        summary.RootElement.GetProperty("summary").GetString().Should().Be(ProjectFilePdf.Message(failure));
        summary.RootElement.GetProperty("fileType").GetString().Should().Be("pdf");
    }

    [Theory]
    [InlineData(ProjectPdfTextFailure.Unavailable)]
    [InlineData(ProjectPdfTextFailure.Busy)]
    [InlineData(ProjectPdfTextFailure.BadResponse)]
    public async Task A_worker_failure_leaves_the_summary_retryable(ProjectPdfTextFailure failure)
    {
        var h = new Harness { Pdf = PdfReturning(PdfFail(failure)) };
        ProjectFile file = h.AddFile("report.pdf", "application/pdf", PdfBytes);

        await h.SummarizeAsync(file);

        h.VerifyNoSummarizerCall();
        h.Reload(file.Id).SummaryStatus.Should().Be(SummaryStatus.Failed);
    }

    private sealed class OkWorker(string text) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(new { text, truncated = false }), Encoding.UTF8, "application/json")
            });
    }

    /// <summary>The real reader against the real configuration key, with a worker stub that always answers with <paramref name="text"/>.</summary>
    private static IProjectFilePdfText RealPdfReader(Harness h, bool enabled, string text = "Quarterly report: revenue grew 12%.")
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Import:Worker:Token"] = "tok", ["Import:Pdf:ProjectText:Enabled"] = enabled.ToString() })
            .Build();
        var client = new ImportWorkerClient(new HttpClient(new OkWorker(text)) { BaseAddress = new Uri("http://import-worker") }, config);
        return new ProjectFilePdfText(h.Storage.Object, client, new ProjectPdfTextCache(), config);
    }

    [Fact]
    public async Task A_switched_off_pdf_path_stores_no_summary_leaves_it_retryable_and_a_reindex_after_switching_on_produces_it()
    {
        var h = new Harness();
        h.Pdf = RealPdfReader(h, enabled: false);
        ProjectFile file = h.AddFile("report.pdf", "application/pdf", PdfBytes);

        await h.SummarizeAsync(file);
        await h.IndexAsync(file);

        h.VerifyNoSummarizerCall();
        ProjectFile off = h.Reload(file.Id);
        off.AgentSummary.Should().BeNull("the switch-off reason is never stored as the file's summary");
        off.SummaryStatus.Should().Be(SummaryStatus.Failed);
        off.IndexingStatus.Should().Be(FileIndexingStatus.NotIndexed);
        h.VerifyUpserted(Times.Never());

        h.Pdf = RealPdfReader(h, enabled: true);
        await h.IndexAsync(file); // the reindex once the switch is back on
        ProjectFile on = h.Reload(file.Id);
        on.IndexingStatus.Should().Be(FileIndexingStatus.Indexed);
        on.SummaryStatus.Should().Be(SummaryStatus.Pending, "the summarization sweep picks it up again");

        await h.SummarizeAsync(file);
        ProjectFile summarized = h.Reload(file.Id);
        summarized.SummaryStatus.Should().Be(SummaryStatus.Done);
        summarized.AgentSummary.Should().Contain("text summary");
    }

    [Fact]
    public async Task A_pdf_without_the_reader_is_still_skipped_as_before()
    {
        var h = new Harness();
        ProjectFile file = h.AddFile("report.pdf", "application/pdf", PdfBytes);

        await h.SummarizeAsync(file);

        h.VerifyNoSummarizerCall();
        h.Reload(file.Id).SummaryStatus.Should().Be(SummaryStatus.Done);
    }

    [Fact]
    public async Task A_pdf_is_indexed_as_its_extracted_text()
    {
        var h = new Harness { Pdf = PdfReturning(PdfOk("Quarterly report: revenue grew 12%.")) };
        ProjectFile file = h.AddFile("report.pdf", "application/pdf", PdfBytes);

        await h.IndexAsync(file);

        h.Chunker.FileNames.Should().ContainSingle().Which.Should().Be("report.pdf.txt");
        h.Embeddings.Inputs.Should().ContainSingle().Which.Should().Contain("revenue grew 12%");
        h.Reload(file.Id).IndexingStatus.Should().Be(FileIndexingStatus.Indexed);
        h.VerifyUpserted(Times.Once());
    }

    [Theory]
    [InlineData(ProjectPdfTextFailure.NoText, FileIndexingStatus.NotIndexed)]
    [InlineData(ProjectPdfTextFailure.Unreadable, FileIndexingStatus.NotIndexed)]
    [InlineData(ProjectPdfTextFailure.Unavailable, FileIndexingStatus.Failed)]
    [InlineData(ProjectPdfTextFailure.Busy, FileIndexingStatus.Failed)]
    public async Task A_pdf_with_no_text_is_not_indexed_and_a_worker_failure_is_marked_failed(ProjectPdfTextFailure failure, FileIndexingStatus expected)
    {
        var h = new Harness { Pdf = PdfReturning(PdfFail(failure)) };
        ProjectFile file = h.AddFile("scan.pdf", "application/pdf", PdfBytes);

        await h.IndexAsync(file);

        h.Reload(file.Id).IndexingStatus.Should().Be(expected);
        h.VerifyUpserted(Times.Never());
    }

    [Fact]
    public async Task A_pdfs_extracted_text_is_cut_at_the_configured_indexing_cap()
    {
        var h = new Harness { Pdf = PdfReturning(PdfOk(new string('a', 60))) };
        ProjectFile file = h.AddFile("report.pdf", "application/pdf", PdfBytes);

        await h.IndexAsync(file, maxTextBytes: 20);

        h.Embeddings.Inputs.Should().ContainSingle().Which.Should().Be(new string('a', 20));
        h.Reload(file.Id).IndexingStatus.Should().Be(FileIndexingStatus.Indexed);
    }

    [Fact]
    public async Task A_pdfs_extracted_text_exactly_at_the_indexing_cap_is_indexed_whole()
    {
        var h = new Harness { Pdf = PdfReturning(PdfOk(new string('a', 20))) };
        ProjectFile file = h.AddFile("report.pdf", "application/pdf", PdfBytes);

        await h.IndexAsync(file, maxTextBytes: 20);

        h.Embeddings.Inputs.Should().ContainSingle().Which.Should().Be(new string('a', 20));
    }

    // ---------------------------------------------------------------- summarizer

    [Fact]
    public async Task A_text_file_is_summarized_by_the_summarizer_agent()
    {
        var h = new Harness();
        h.WithVision();
        ProjectFile file = h.AddFile("README.md", "text/markdown", "# Hello\nworld\n"u8.ToArray());

        await h.SummarizeAsync(file);

        h.SummarizerAgent.Verify(a => a.RunAsync(It.Is<string>(p => p.Contains("# Hello")), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Once);
        h.Vision.Calls.Should().Be(0);
        ProjectFile stored = h.Reload(file.Id);
        stored.SummaryStatus.Should().Be(SummaryStatus.Done);
        stored.AgentSummary.Should().Contain("text summary");
    }

    [Fact]
    public async Task A_png_is_described_by_the_vision_provider_and_requeued_for_indexing()
    {
        var h = new Harness();
        h.WithVision();
        ProjectFile file = h.AddFile("logo.png", "image/png", PngBytes);

        await h.SummarizeAsync(file);

        h.VerifyNoSummarizerCall();
        h.Vision.Calls.Should().Be(1);
        DataContent image = h.Vision.Messages.Should().ContainSingle().Subject.Contents.OfType<DataContent>().Should().ContainSingle().Subject;
        image.MediaType.Should().Be("image/png");
        image.Data.ToArray().Should().Equal(PngBytes);
        h.Vision.Messages[0].Contents.OfType<TextContent>().Single().Text.Should().StartWith("Describe this image for someone who cannot see it");

        ProjectFile stored = h.Reload(file.Id);
        stored.SummaryStatus.Should().Be(SummaryStatus.Done);
        stored.AgentSummary.Should().Contain("A red rocket launching over a city skyline");
        h.Publish.Verify(p => p.Publish(
            It.Is<ProjectFileIndexingRequested>(m => m.FileId == file.Id && m.ProjectId == h.Project.Id && m.Operation == "Upsert"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_png_without_a_vision_provider_is_skipped()
    {
        var h = new Harness();
        h.WithoutVision();
        ProjectFile file = h.AddFile("logo.png", "image/png", PngBytes);

        await h.SummarizeAsync(file);

        h.VerifyNoSummarizerCall();
        h.Vision.Calls.Should().Be(0);
        ProjectFile stored = h.Reload(file.Id);
        stored.SummaryStatus.Should().Be(SummaryStatus.Done);
        stored.AgentSummary.Should().BeNull();
    }

    [Theory]
    [InlineData("clip.mp4", "video/mp4")]
    [InlineData("blob.bin", "application/octet-stream")]
    public async Task Video_and_binary_files_get_no_model_call(string name, string mime)
    {
        var h = new Harness();
        h.WithVision();
        ProjectFile file = h.AddFile(name, mime, name.EndsWith(".mp4") ? Mp4Bytes : RandomBinary());

        await h.SummarizeAsync(file);

        h.VerifyNoSummarizerCall();
        h.Vision.Calls.Should().Be(0);
        ProjectFile stored = h.Reload(file.Id);
        stored.SummaryStatus.Should().Be(SummaryStatus.Done);
        stored.AgentSummary.Should().BeNull();
    }

    [Fact]
    public async Task An_image_larger_than_the_vision_cap_is_skipped()
    {
        var h = new Harness();
        h.WithVision();
        ProjectFile file = h.AddFile("huge.png", "image/png", PngBytes, sizeBytes: ProjectFileContentClassifier.MaxVisionImageBytes + 1);

        await h.SummarizeAsync(file);

        h.Vision.Calls.Should().Be(0);
        h.VerifyNoSummarizerCall();
        ProjectFile stored = h.Reload(file.Id);
        stored.SummaryStatus.Should().Be(SummaryStatus.Done);
        stored.AgentSummary.Should().BeNull();
    }

    // ---------------------------------------------------------------- consumer

    [Fact]
    public async Task A_text_file_is_embedded_and_indexed()
    {
        var h = new Harness();
        ProjectFile file = h.AddFile("notes.txt", "text/plain", "launch notes"u8.ToArray());

        await h.IndexAsync(file);

        h.Embeddings.Inputs.Should().ContainSingle().Which.Should().Be("launch notes");
        h.VerifyUpserted(Times.Once());
        h.Reload(file.Id).IndexingStatus.Should().Be(FileIndexingStatus.Indexed);
    }

    [Fact]
    public async Task An_image_with_a_description_embeds_the_description_not_the_pixels()
    {
        var h = new Harness();
        const string description = "A red rocket launching over a city skyline.";
        string summaryJson = $"{{\"fileType\":\"image\",\"summary\":\"{description}\",\"keyPoints\":[]}}";
        ProjectFile file = h.AddFile("logo.png", "image/png", PngBytes, SummaryStatus.Done, summaryJson);

        await h.IndexAsync(file);

        string input = h.Embeddings.Inputs.Should().ContainSingle().Subject;
        input.Should().Contain(description);
        input.Should().NotContain("PNG").And.NotContain("\u0089");
        h.Chunker.FileNames.Should().ContainSingle().Which.Should().EndWith(".txt");
        h.Vectors.Verify(v => v.UpsertFileChunksAsync(
            h.Project.Id, It.IsAny<string?>(), file.Id, "assets/logo.png", "logo.png",
            It.IsAny<IReadOnlyList<VectorizedFileChunk>>(), It.IsAny<CancellationToken>()), Times.Once);
        h.Reload(file.Id).IndexingStatus.Should().Be(FileIndexingStatus.Indexed);
    }

    [Fact]
    public async Task An_image_whose_description_is_pending_waits_for_the_summarizer()
    {
        var h = new Harness();
        ProjectFile file = h.AddFile("logo.png", "image/png", PngBytes, SummaryStatus.Pending);

        await h.IndexAsync(file);

        h.Embeddings.Inputs.Should().BeEmpty();
        h.VerifyUpserted(Times.Never());
        h.Reload(file.Id).IndexingStatus.Should().Be(FileIndexingStatus.Pending);
    }

    [Fact]
    public async Task An_image_without_a_description_is_not_indexed()
    {
        var h = new Harness();
        ProjectFile file = h.AddFile("logo.png", "image/png", PngBytes, SummaryStatus.Done, agentSummary: null);

        await h.IndexAsync(file);

        h.Embeddings.Inputs.Should().BeEmpty();
        h.VerifyUpserted(Times.Never());
        h.Reload(file.Id).IndexingStatus.Should().Be(FileIndexingStatus.NotIndexed);
    }

    [Theory]
    [InlineData("clip.mp4", "video/mp4")]
    [InlineData("blob.bin", "application/octet-stream")]
    public async Task Video_and_binary_files_are_not_embedded(string name, string mime)
    {
        var h = new Harness();
        ProjectFile file = h.AddFile(name, mime, name.EndsWith(".mp4") ? Mp4Bytes : RandomBinary());

        await h.IndexAsync(file);

        h.Embeddings.Inputs.Should().BeEmpty();
        h.VerifyUpserted(Times.Never());
        ProjectFile stored = h.Reload(file.Id);
        stored.IndexingStatus.Should().Be(FileIndexingStatus.NotIndexed);
        stored.IndexingError.Should().BeNull();
    }

    // ---------------------------------------------------------------- QA (T-003) regression pins

    private sealed class ScriptedChatClient(Func<ChatResponse> respond) : IChatClient
    {
        public int Calls { get; private set; }
        public UsageScopeState? ScopeAtCall { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            ScopeAtCall = UsageScope.Current;
            return Task.FromResult(respond());
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private static void SetCategory(Harness h, Guid fileId, string category)
    {
        using InferenceApiDbContext db = h.NewContext();
        db.ProjectFiles.Single(f => f.Id == fileId).Category = category;
        db.SaveChanges();
    }

    [Fact]
    public async Task Qa_the_vision_call_is_metered_to_the_project_org_as_file_summary()
    {
        var h = new Harness();
        h.WithVision();
        var client = new ScriptedChatClient(() => new ChatResponse(new ChatMessage(ChatRole.Assistant, "a logo")));
        h.ChatClients.Setup(f => f.Get(It.IsAny<ResolvedInferenceProvider>())).Returns(client);
        ProjectFile file = h.AddFile("logo.png", "image/png", PngBytes);

        await h.SummarizeAsync(file);

        client.Calls.Should().Be(1);
        client.ScopeAtCall.Should().NotBeNull();
        client.ScopeAtCall!.Source.Should().Be(UsageSource.FileSummary);
        client.ScopeAtCall.OrganizationId.Should().Be(h.Project.OrganizationId);
        client.ScopeAtCall.ProjectId.Should().Be(h.Project.Id);
        client.ScopeAtCall.UserId.Should().BeNull();
        UsageScope.Current.Should().BeNull("the scope must not leak past the call");
    }

    [Fact]
    public async Task Qa_an_empty_vision_reply_stores_no_description_and_the_requeued_index_embeds_nothing()
    {
        var h = new Harness(visionReply: "   \n ");
        h.WithVision();
        ProjectFile file = h.AddFile("logo.png", "image/png", PngBytes);

        await h.SummarizeAsync(file);

        h.Vision.Calls.Should().Be(1);
        ProjectFile stored = h.Reload(file.Id);
        stored.SummaryStatus.Should().Be(SummaryStatus.Done);
        stored.AgentSummary.Should().BeNull();

        // The image is still republished so the consumer settles its indexing status; with no
        // description it embeds nothing and marks the file NotIndexed.
        h.Publish.Verify(p => p.Publish(
            It.Is<ProjectFileIndexingRequested>(m => m.FileId == file.Id && m.ProjectId == h.Project.Id && m.Operation == "Upsert"),
            It.IsAny<CancellationToken>()), Times.Once);
        await h.IndexAsync(file);
        h.Embeddings.Inputs.Should().BeEmpty();
        h.Reload(file.Id).IndexingStatus.Should().Be(FileIndexingStatus.NotIndexed);
    }

    [Fact]
    public async Task A_throwing_vision_provider_still_requests_indexing_so_the_status_settles()
    {
        var h = new Harness();
        h.WithVision();
        h.ChatClients.Setup(f => f.Get(It.IsAny<ResolvedInferenceProvider>()))
            .Returns(new ScriptedChatClient(() => throw new InvalidOperationException("vision down")));
        ProjectFile file = h.AddFile("logo.png", "image/png", PngBytes);

        await h.IndexAsync(file);
        h.Reload(file.Id).IndexingStatus.Should().Be(FileIndexingStatus.Pending);

        await h.SummarizeAsync(file);

        h.Reload(file.Id).SummaryStatus.Should().Be(SummaryStatus.Failed);
        h.Publish.Verify(p => p.Publish(
            It.Is<ProjectFileIndexingRequested>(m => m.FileId == file.Id && m.ProjectId == h.Project.Id && m.Operation == "Upsert"),
            It.IsAny<CancellationToken>()), Times.Once);
        await h.IndexAsync(file);
        h.Reload(file.Id).IndexingStatus.Should().Be(FileIndexingStatus.NotIndexed);
    }

    [Fact]
    public async Task An_oversized_image_skip_still_requests_indexing()
    {
        var h = new Harness();
        h.WithVision();
        ProjectFile file = h.AddFile("huge.png", "image/png", PngBytes, sizeBytes: ProjectFileContentClassifier.MaxVisionImageBytes + 1);

        await h.SummarizeAsync(file);

        h.Vision.Calls.Should().Be(0);
        h.Publish.Verify(p => p.Publish(
            It.Is<ProjectFileIndexingRequested>(m => m.FileId == file.Id && m.Operation == "Upsert"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Text_and_binary_files_are_not_republished_by_the_summarizer()
    {
        var h = new Harness();
        h.WithVision();
        ProjectFile text = h.AddFile("README.md", "text/markdown", "# Hello\n"u8.ToArray());
        ProjectFile binary = h.AddFile("blob.bin", "application/octet-stream", RandomBinary());

        await h.SummarizeAsync(text);
        await h.SummarizeAsync(binary);

        h.Publish.Verify(p => p.Publish(It.IsAny<ProjectFileIndexingRequested>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Qa_a_throwing_vision_provider_marks_the_summary_failed_without_a_description()
    {
        var h = new Harness();
        h.WithVision();
        var client = new ScriptedChatClient(() => throw new InvalidOperationException("vision down"));
        h.ChatClients.Setup(f => f.Get(It.IsAny<ResolvedInferenceProvider>())).Returns(client);
        ProjectFile file = h.AddFile("logo.png", "image/png", PngBytes);

        await h.SummarizeAsync(file);

        client.Calls.Should().Be(1);
        ProjectFile stored = h.Reload(file.Id);
        stored.SummaryStatus.Should().Be(SummaryStatus.Failed);
        stored.AgentSummary.Should().BeNull();

        // And the consumer then neither embeds nor waits on it.
        await h.IndexAsync(file);
        h.Embeddings.Inputs.Should().BeEmpty();
        h.Reload(file.Id).IndexingStatus.Should().Be(FileIndexingStatus.NotIndexed);
    }

    [Fact]
    public async Task Qa_an_image_whose_stream_exceeds_the_cap_is_skipped_even_when_SizeBytes_under_reports()
    {
        var h = new Harness();
        h.WithVision();
        byte[] huge = new byte[ProjectFileContentClassifier.MaxVisionImageBytes + 1];
        PngBytes.CopyTo(huge, 0);
        ProjectFile file = h.AddFile("huge.png", "image/png", huge, sizeBytes: 1024);

        await h.SummarizeAsync(file);

        h.Vision.Calls.Should().Be(0);
        ProjectFile stored = h.Reload(file.Id);
        stored.SummaryStatus.Should().Be(SummaryStatus.Done);
        stored.AgentSummary.Should().BeNull();
    }

    [Theory]
    [InlineData("notes.txt", "text/plain")]
    [InlineData("data.json", "application/json")]
    public async Task Qa_a_text_named_file_with_binary_bytes_is_neither_summarized_nor_embedded(string name, string mime)
    {
        var h = new Harness();
        h.WithVision();
        byte[] bytes = [.. "header line\n"u8.ToArray(), 0x00, 0x01, 0xFF, 0xFE, .. "tail"u8.ToArray()];
        ProjectFile file = h.AddFile(name, mime, bytes);

        await h.SummarizeAsync(file);
        await h.IndexAsync(file);

        h.VerifyNoSummarizerCall();
        h.Vision.Calls.Should().Be(0);
        h.Embeddings.Inputs.Should().BeEmpty();
        h.VerifyUpserted(Times.Never());
        ProjectFile stored = h.Reload(file.Id);
        stored.AgentSummary.Should().BeNull();
        stored.SummaryStatus.Should().Be(SummaryStatus.Done);
        stored.IndexingStatus.Should().Be(FileIndexingStatus.NotIndexed);
    }

    [Fact]
    public async Task Qa_png_bytes_labelled_text_never_reach_vision_or_the_embedder()
    {
        var h = new Harness();
        h.WithVision();
        ProjectFile file = h.AddFile("logo.txt", "text/plain", PngBytes);

        await h.SummarizeAsync(file);
        await h.IndexAsync(file);

        h.Vision.Calls.Should().Be(0);
        h.VerifyNoSummarizerCall();
        h.Embeddings.Inputs.Should().BeEmpty();
        h.Reload(file.Id).IndexingStatus.Should().Be(FileIndexingStatus.NotIndexed);
    }

    [Fact]
    public async Task Qa_an_octet_stream_jpeg_with_a_jpg_name_is_described_as_jpeg()
    {
        var h = new Harness();
        h.WithVision();
        ProjectFile file = h.AddFile("photo.jpg", "application/octet-stream", JpegBytes);

        await h.SummarizeAsync(file);

        h.Vision.Messages.Should().ContainSingle().Subject.Contents.OfType<DataContent>()
            .Should().ContainSingle().Which.MediaType.Should().Be("image/jpeg");
    }

    [Fact]
    public async Task Qa_an_agent_written_image_never_waits_for_a_summary()
    {
        var h = new Harness();
        ProjectFile file = h.AddFile("render.png", "image/png", PngBytes, SummaryStatus.Pending);
        SetCategory(h, file.Id, "agentFiles");

        await h.IndexAsync(file);

        h.Embeddings.Inputs.Should().BeEmpty();
        h.Reload(file.Id).IndexingStatus.Should().Be(FileIndexingStatus.NotIndexed);
    }

    [Fact]
    public async Task Qa_text_decoding_and_chunk_name_are_unchanged_for_a_utf8_bom_file_longer_than_the_head()
    {
        var h = new Harness();
        string body = string.Concat(Enumerable.Repeat("Café launch \U0001F680 notes. ", 1000));
        byte[] bytes = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(body)];
        bytes.Length.Should().BeGreaterThan(ProjectFileContentClassifier.SniffBytes);
        ProjectFile file = h.AddFile("notes.md", "text/markdown", bytes);

        await h.IndexAsync(file);

        h.Embeddings.Inputs.Should().ContainSingle().Which.Should().Be(body);
        h.Chunker.FileNames.Should().ContainSingle().Which.Should().Be("notes.md");
        h.Reload(file.Id).IndexingStatus.Should().Be(FileIndexingStatus.Indexed);
    }

    [Theory]
    [InlineData("utf16le-bom")]
    [InlineData("utf16be-bom")]
    [InlineData("utf16le")]
    [InlineData("windows-1252")]
    public async Task A_non_utf8_text_file_longer_than_the_head_is_transcoded_and_embedded(string sample)
    {
        var h = new Harness();
        string body = string.Concat(Enumerable.Repeat("Caf\u00e9 \u201cnotes\u201d \u2014 \u20ac5 each. ", 600));
        byte[] bytes = sample switch
        {
            "utf16le-bom" => [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(body)],
            "utf16be-bom" => [.. Encoding.BigEndianUnicode.GetPreamble(), .. Encoding.BigEndianUnicode.GetBytes(body)],
            "utf16le" => Encoding.Unicode.GetBytes(body),
            _ => Windows1252.GetBytes(body),
        };
        bytes.Length.Should().BeGreaterThan(ProjectFileContentClassifier.SniffBytes);
        ProjectFile file = h.AddFile("notes.txt", "text/plain", bytes);

        await h.IndexAsync(file);

        h.Embeddings.Inputs.Should().ContainSingle().Which.Should().Be(body);
        h.Reload(file.Id).IndexingStatus.Should().Be(FileIndexingStatus.Indexed);
    }

    [Fact]
    public async Task A_windows_1252_text_file_is_summarized_as_decoded_text()
    {
        var h = new Harness();
        ProjectFile file = h.AddFile("legacy.txt", "text/plain", Windows1252.GetBytes("Caf\u00e9 \u201cquoted\u201d \u20ac5\n"));

        await h.SummarizeAsync(file);

        h.SummarizerAgent.Verify(
            a => a.RunAsync(It.Is<string>(p => p.Contains("Caf\u00e9 \u201cquoted\u201d \u20ac5")), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()),
            Times.Once);
        h.Reload(file.Id).AgentSummary.Should().Contain("text summary");
    }

    /// <summary>
    /// Upload publishes the index request and queues the summary at the same time, so the consumer
    /// usually sees the image first and parks it at IndexingStatus.Pending. When the summarizer then
    /// SKIPS the image (no vision provider here; the same holds for oversize / empty reply / Failed) it
    /// must either requeue indexing or settle the status itself — otherwise the row is Pending forever,
    /// and the bulk reindex (ProjectFilesController.ReindexProjectFiles) excludes Pending rows.
    /// </summary>
    [Fact]
    public async Task Qa_an_image_parked_pending_is_not_left_pending_when_the_summarizer_skips_it()
    {
        var h = new Harness();
        h.WithoutVision();
        ProjectFile file = h.AddFile("logo.png", "image/png", PngBytes, SummaryStatus.Pending);

        await h.IndexAsync(file);
        h.Reload(file.Id).IndexingStatus.Should().Be(FileIndexingStatus.Pending);

        await h.SummarizeAsync(file);

        bool requeued = h.Publish.Invocations.Any(i =>
            i.Arguments.Count > 0 && i.Arguments[0] is ProjectFileIndexingRequested m && m.FileId == file.Id);
        bool settled = h.Reload(file.Id).IndexingStatus != FileIndexingStatus.Pending;
        (requeued || settled).Should().BeTrue(
            "a skipped image must not stay IndexingStatus.Pending with nothing left to move it");
    }
}
