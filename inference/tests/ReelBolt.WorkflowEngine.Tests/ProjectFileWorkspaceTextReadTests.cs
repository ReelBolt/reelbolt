#nullable enable

using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Amazon.S3;
using Amazon.S3.Model;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Storage;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Services.Storage;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The engine reads a project file as text by its bytes, not by its extension. A text file whose
/// extension nobody listed is read; a genuine binary is refused even when it wears an allowlisted
/// extension (an .mp3 renamed .md); and a UTF-16 or Windows-1252 source is decoded in the encoding
/// the sniff picked rather than forced through UTF-8.
/// </summary>
public class ProjectFileWorkspaceTextReadTests
{
    private static readonly Guid ProjectId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static readonly byte[] Mp3Bytes = [0x49, 0x44, 0x33, 0x00, 0x00, 0xFF, 0xFB, 0x90, 0x00];
    private static readonly byte[] PngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x01];

    /// <summary>
    /// A real <see cref="ProjectFileWorkspace"/> over a mocked <see cref="IAmazonS3"/> whose object
    /// stream holds <paramref name="body"/>, and a real <see cref="IServiceScopeFactory"/> over an
    /// InMemory <see cref="WorkflowEngineDbContext"/> that already holds the file row
    /// <see cref="ProjectFileWorkspace.ReadFileAsync"/> resolves.
    /// </summary>
    private sealed class Fixture : IDisposable
    {
        private readonly ServiceProvider _provider;

        public Fixture(string fileName, string mime, byte[] body)
        {
            string dbName = "project-file-read-" + Guid.NewGuid().ToString("N");
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddDbContext<WorkflowEngineDbContext>(
                o => o.UseInMemoryDatabase(dbName),
                ServiceLifetime.Scoped);
            _provider = services.BuildServiceProvider();

            using (IServiceScope scope = _provider.CreateScope())
            {
                WorkflowEngineDbContext db = scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();
                db.ProjectFiles.Add(new ProjectFile
                {
                    Id = Guid.NewGuid(),
                    ProjectId = ProjectId,
                    OriginalFileName = fileName,
                    OriginalPath = "src/" + fileName,
                    Category = "userFiles",
                    MimeType = mime,
                    StorageKey = $"projects/{ProjectId}/userFiles/{fileName}",
                    StorageFileName = fileName,
                    StorageBucket = "reelforge",
                    SizeBytes = body.Length,
                    UploadedAt = DateTime.UtcNow
                });
                db.SaveChanges();
            }

            Mock<IAmazonS3> s3 = new();
            s3.Setup(c => c.GetObjectAsync(It.IsAny<GetObjectRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new GetObjectResponse { ResponseStream = new MemoryStream(body) });

            Workspace = new ProjectFileWorkspace(
                s3.Object,
                _provider.GetRequiredService<IServiceScopeFactory>(),
                new ConfigurationBuilder().Build(),
                NullLogger<ProjectFileWorkspace>.Instance);
        }

        public ProjectFileWorkspace Workspace { get; }

        public Task<string> ReadAsync(string fileName) =>
            Workspace.ReadFileAsync(ProjectId, fileName, CancellationToken.None);

        public void Dispose() => _provider.Dispose();
    }

    [Theory]
    [InlineData("_icons.scss", "application/octet-stream", "$brand: #5b4bff;\n.btn { color: $brand; }\n")]
    [InlineData("Contoso.Portal.Client.csproj", "application/octet-stream", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>")]
    [InlineData("widget.unknownext", "application/octet-stream", "just text nobody listed\n")] // sniff wins
    public async Task Reads_a_text_file_the_extension_list_never_heard_of(string name, string mime, string body)
    {
        using var fixture = new Fixture(name, mime, Encoding.UTF8.GetBytes(body));

        string content = await fixture.ReadAsync(name);

        content.Should().Contain(body.Split('\n')[0]);
    }

    [Theory]
    [InlineData("song.mp3", "audio/mpeg")]
    [InlineData("shot.png", "image/png")]
    public async Task Still_refuses_a_genuine_binary(string name, string mime)
    {
        using var fixture = new Fixture(name, mime, name.EndsWith(".mp3") ? Mp3Bytes : PngBytes);

        Func<Task> act = () => fixture.ReadAsync(name);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*binary file and cannot be read as text*");
    }

    [Fact]
    public async Task Refuses_a_binary_that_wears_an_allowlisted_extension()
    {
        // An .mp3 renamed .md is stored with a text/markdown MIME and an allowlisted extension, so
        // the extension alone would admit it. The sniff must not be short-circuited by the extension.
        using var fixture = new Fixture("song.md", "text/markdown", Mp3Bytes);

        Func<Task> act = () => fixture.ReadAsync("song.md");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*binary file and cannot be read as text*");
    }

    [Fact]
    public async Task Decodes_a_utf16_source_in_the_encoding_the_sniff_picked()
    {
        const string body = "Hello, café!\r\nSecond line.\r\n";
        using var fixture = new Fixture("legacy.unknownext", "application/octet-stream", Encoding.Unicode.GetBytes(body));

        string content = await fixture.ReadAsync("legacy.unknownext");

        content.Should().Be(body);
    }

    [Fact]
    public async Task Decodes_a_windows_1252_source_in_the_encoding_the_sniff_picked()
    {
        const string body = "Café “quoted” costs €5 — naïve.\r\n";
        Encoding windows1252 = CodePagesEncodingProvider.Instance.GetEncoding(1252)!;
        using var fixture = new Fixture("legacy.data", "application/octet-stream", windows1252.GetBytes(body));

        string content = await fixture.ReadAsync("legacy.data");

        content.Should().Be(body);
    }

    [Theory]
    [InlineData("song.mp3", "audio/mpeg")]
    [InlineData("song.mp3", "application/octet-stream")]
    [InlineData("shot.png", "image/png")]
    [InlineData("shot.png", "application/octet-stream")]
    public async Task Refuses_a_known_binary_even_when_its_first_bytes_are_text(string name, string mime)
    {
        // The declaration gate refuses an unambiguous binary family before a single byte is fetched, so
        // an ASCII prefix (all the head sniff would see) cannot rescue an .mp3 or a .png.
        byte[] body = Encoding.UTF8.GetBytes(new string('A', ProjectFileTextPolicy.SniffBytes) + "\n");
        using var fixture = new Fixture(name, mime, body);

        Func<Task> act = () => fixture.ReadAsync(name);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*binary file and cannot be read as text*");
    }

    [Fact]
    public async Task Reads_a_scss_stored_as_a_generic_mime()
    {
        const string body = "$brand: #5b4bff;\n.btn { color: $brand; }\n";
        using var fixture = new Fixture("_icons.scss", "application/octet-stream", Encoding.UTF8.GetBytes(body));

        string content = await fixture.ReadAsync("_icons.scss");

        content.Should().Be(body);
    }

    [Fact]
    public async Task Refuses_a_text_head_with_a_binary_body()
    {
        // A full sniff window of ASCII followed by a binary tail the sniff never looks at. Only the
        // whole-body validation catches this, so the file is refused rather than returned as mojibake.
        byte[] body = new byte[ProjectFileTextPolicy.SniffBytes + 4096];
        Array.Fill(body, (byte)'A');
        Array.Fill(body, (byte)0xFF, ProjectFileTextPolicy.SniffBytes, 4096);
        using var fixture = new Fixture("sneaky.unknownext", "application/octet-stream", body);

        Func<Task> act = () => fixture.ReadAsync("sneaky.unknownext");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*binary file and cannot be read as text*");
    }

    [Fact]
    public async Task Refuses_a_file_above_the_read_limit_and_names_the_limit()
    {
        byte[] body = new byte[ProjectFileTextPolicy.MaxReadBytes + 1];
        Array.Fill(body, (byte)'A');
        using var fixture = new Fixture("huge.unknownext", "application/octet-stream", body);

        Func<Task> act = () => fixture.ReadAsync("huge.unknownext");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"*huge.unknownext*{ProjectFileTextPolicy.MaxReadBytes}*");
    }
}
