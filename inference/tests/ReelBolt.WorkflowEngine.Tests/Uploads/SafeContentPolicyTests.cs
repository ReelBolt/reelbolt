#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Amazon.S3;
using Amazon.S3.Model;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Uploads;

/// <summary>
/// P0-01: one policy decides the Content-Type and the inline/attachment disposition of every stream of
/// customer bytes, and every streaming route applies it with anti-sniffing headers.
/// </summary>
public sealed class SafeContentPolicyTests
{
    // ---------------------------------------------------------------------------------------------------
    // Pure policy
    // ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("text/html", "a.html")]
    [InlineData("image/svg+xml", "a.svg")]
    [InlineData("application/xml", "a.xml")]
    [InlineData("application/javascript", "a.js")]
    [InlineData("application/json", "a.json")]
    [InlineData("application/pdf", "a.pdf")]
    [InlineData("text/plain", "a.txt")]
    [InlineData("", "a")]
    [InlineData(null, "a")]
    [InlineData("weird/type", "a.bin")]
    public void Decide_non_allowlisted_types_are_attachment_octet_stream(string? mime, string name)
    {
        SafeContentPolicy.Decide(mime, name).Should().Be(new ServedContent("application/octet-stream", false));
    }

    [Fact]
    public void Decide_png_named_png_is_inline()
    {
        SafeContentPolicy.Decide("image/png", "a.png").Should().Be(new ServedContent("image/png", true));
        SafeContentPolicy.Decide("IMAGE/PNG; charset=x", "a.PNG").Should().Be(new ServedContent("image/png", true));
    }

    [Theory]
    [InlineData("image/png", "evil.html")]
    [InlineData("text/html", "a.png")]
    [InlineData("video/mp4", "a.png")]
    public void Decide_mismatched_extension_is_attachment(string mime, string name)
    {
        SafeContentPolicy.Decide(mime, name).Should().Be(new ServedContent("application/octet-stream", false));
    }

    [Theory]
    [InlineData("image/png", "a.png")]
    [InlineData("image/jpeg", "a.jpg")]
    [InlineData("image/jpeg", "a.jpeg")]
    [InlineData("image/gif", "a.gif")]
    [InlineData("image/webp", "a.webp")]
    [InlineData("video/mp4", "a.mp4")]
    [InlineData("video/mp4", "a.m4v")]
    [InlineData("video/webm", "a.webm")]
    [InlineData("video/quicktime", "a.mov")]
    [InlineData("audio/mpeg", "a.mp3")]
    [InlineData("audio/wav", "a.wav")]
    [InlineData("audio/x-wav", "a.wav")]
    [InlineData("audio/ogg", "a.ogg")]
    [InlineData("audio/ogg", "a.oga")]
    [InlineData("audio/aac", "a.aac")]
    [InlineData("audio/mp4", "a.m4a")]
    [InlineData("audio/webm", "a.webm")]
    public void Decide_media_types_inline(string mime, string name)
    {
        ServedContent served = SafeContentPolicy.Decide(mime, name);
        served.Inline.Should().BeTrue();
        served.ContentType.Should().Be(mime);
    }

    [Theory]
    [InlineData("image/png", "image/png")]
    [InlineData("IMAGE/PNG; charset=x", "image/png")]
    [InlineData("video/mp4", "video/mp4")]
    [InlineData("text/html", "application/octet-stream")]
    [InlineData("image/svg+xml", "application/octet-stream")]
    [InlineData(null, "application/octet-stream")]
    public void DecideByMime_matches_allowlist(string? mime, string expectedType)
    {
        ServedContent served = SafeContentPolicy.DecideByMime(mime);
        served.ContentType.Should().Be(expectedType);
        served.Inline.Should().Be(expectedType != "application/octet-stream");
    }

    [Fact]
    public void SanitizeFileName_strips_quotes_crlf_and_controls()
    {
        string cleaned = SafeContentPolicy.SanitizeFileName("a\"b\r\nc\\d/e\u0001.png");
        cleaned.Should().NotContain("\"");
        cleaned.Should().NotContain("\\");
        cleaned.Should().NotContain("/");
        cleaned.Should().NotContain("\r");
        cleaned.Should().NotContain("\n");
        cleaned.Should().NotContain("\u0001");

        SafeContentPolicy.SanitizeFileName(null).Should().Be("file");
        SafeContentPolicy.SanitizeFileName(new string('a', 300)).Length.Should().BeLessThanOrEqualTo(150);
        SafeContentPolicy.SanitizeFileName("\"\r\n").Should().Be("file");
    }

    [Theory]
    [InlineData(true, "inline;")]
    [InlineData(false, "attachment;")]
    public void BuildContentDisposition_is_ascii_only(bool inline, string prefix)
    {
        string disposition = SafeContentPolicy.BuildContentDisposition(inline, "résumé.png");
        disposition.Should().StartWith(prefix);
        disposition.Should().Contain("filename*=UTF-8''");
        disposition.All(c => c < 0x80).Should().BeTrue("Kestrel throws on a non-ASCII header value");
    }

    [Fact]
    public void ApplySecurityHeaders_sets_the_four_headers()
    {
        HttpResponse response = new DefaultHttpContext().Response;
        SafeContentPolicy.ApplySecurityHeaders(response);
        AssertSecurityHeaders(response);
        response.Headers["X-Content-Type-Options"].Count.Should().Be(1);

        SafeContentPolicy.ApplySecurityHeaders(response);
        AssertSecurityHeaders(response);
        response.Headers["X-Content-Type-Options"].Count.Should().Be(1);
    }

    // ---------------------------------------------------------------------------------------------------
    // The streamer, through the S3 seam
    // ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("text/html", "a.html", "application/octet-stream", "attachment")]
    [InlineData("image/png", "a.png", "image/png", "inline")]
    public async Task RangedObjectStreamer_decides_type_and_disposition(
        string mime, string name, string expectedType, string expectedDisposition)
    {
        var controller = new StubController(new DefaultHttpContext());

        IActionResult result = await RangedObjectStreamer.StreamAsync(
            controller, S3WithObject().Object, "b", "k", mime, name, CancellationToken.None);

        var file = result.Should().BeOfType<FileStreamResult>().Subject;
        file.ContentType.Should().Be(expectedType);
        controller.Response.Headers["Content-Disposition"].ToString().Should().StartWith(expectedDisposition);
        AssertSecurityHeaders(controller.Response);
    }

    [Fact]
    public async Task ProjectFileMedia_Stream_serves_html_as_attachment_with_security_headers()
    {
        Guid userId = Guid.NewGuid();
        Guid organizationId = TestTenancy.OrgOf(userId);

        await using InferenceApiDbContext db = new(new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase($"safe-content-{Guid.NewGuid():N}")
            .Options);

        var project = new Project
        {
            Id = Guid.NewGuid(),
            Name = "P",
            OwnerId = userId,
            OrganizationId = organizationId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        var file = new ProjectFile
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            OriginalFileName = "x_a.html",
            StorageKey = $"projects/{project.Id}/userFiles/x_a.html",
            StorageBucket = "reelforge",
            MimeType = "text/html",
            SizeBytes = 4
        };
        db.Projects.Add(project);
        db.ProjectFiles.Add(file);
        await db.SaveChangesAsync();

        ICurrentUser user = new FakeUser(userId, organizationId, OrganizationRole.Member);
        var controller = new ProjectFileMediaController(
            db, user, TestTenancy.Access(db, user), S3WithObject().Object, Config())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        IActionResult result = await controller.Stream(project.Id, file.Id, CancellationToken.None);

        var served = result.Should().BeOfType<FileStreamResult>().Subject;
        served.ContentType.Should().Be("application/octet-stream");
        controller.Response.Headers["Content-Disposition"].ToString().Should().StartWith("attachment");
        AssertSecurityHeaders(controller.Response);
    }

    // ---------------------------------------------------------------------------------------------------
    // Harness
    // ---------------------------------------------------------------------------------------------------

    private static void AssertSecurityHeaders(HttpResponse response)
    {
        response.Headers["X-Content-Type-Options"].ToString().Should().Be("nosniff");
        response.Headers["Content-Security-Policy"].ToString().Should().Be("sandbox; default-src 'none'");
        response.Headers["Cross-Origin-Resource-Policy"].ToString().Should().Be("same-origin");
        response.Headers["Referrer-Policy"].ToString().Should().Be("no-referrer");
    }

    private static Mock<IAmazonS3> S3WithObject()
    {
        var metadata = new GetObjectMetadataResponse { ContentLength = 4 };
        metadata.Headers.ContentLength = 4;

        var s3 = new Mock<IAmazonS3>();
        s3.Setup(x => x.GetObjectMetadataAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(metadata);
        s3.Setup(x => x.GetObjectAsync(It.IsAny<GetObjectRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GetObjectResponse { ResponseStream = new MemoryStream(new byte[4]) });
        return s3;
    }

    private static IConfiguration Config() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["MinIO:BucketName"] = "reelforge" })
            .Build();

    private sealed class StubController : ControllerBase
    {
        public StubController(HttpContext context) =>
            ControllerContext = new ControllerContext { HttpContext = context };
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
