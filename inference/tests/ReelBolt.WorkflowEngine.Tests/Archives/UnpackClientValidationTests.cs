#nullable enable
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using ReelBolt.Inference.Api.Services.Archives;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Archives;

/// <summary>The client treats the worker's answer as untrusted: crafted answers must map to BadResponse.</summary>
public class UnpackClientValidationTests
{
    private sealed class Canned : HttpMessageHandler
    {
        private readonly Func<HttpResponseMessage> _make;

        public Canned(Func<HttpResponseMessage> make) => _make = make;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_make());
    }

    private sealed class ForwardOnly : MemoryStream
    {
        public ForwardOnly(byte[] data) : base(data) { }

        public override bool CanSeek => false;
    }

    private static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static byte[] Frame(string manifestJson, byte[]? body = null, string magic = "RBU1", uint? declaredLength = null)
    {
        byte[] manifest = Encoding.UTF8.GetBytes(manifestJson);
        using MemoryStream ms = new();
        ms.Write(Encoding.ASCII.GetBytes(magic));
        byte[] len = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(len, declaredLength ?? (uint)manifest.Length);
        ms.Write(len);
        ms.Write(manifest);
        if (body is not null)
            ms.Write(body);
        return ms.ToArray();
    }

    private static string Manifest(params (string Path, byte[] Data)[] files)
    {
        List<object> list = new();
        foreach ((string path, byte[] data) in files)
            list.Add(new { path, size = data.Length, sha256 = Sha(data) });
        return JsonSerializer.Serialize(new { files = list, skipped = new { counts = new { }, samples = Array.Empty<object>() }, aborted = false, abortReason = (string?)null });
    }

    private static async Task<(ArchiveUnpackSession Session, string? ReadViolation)> RunAsync(byte[] answer, string contentType = "application/vnd.reelbolt.unpack.v1", bool unsized = false)
    {
        HttpClient http = new(new Canned(() =>
        {
            HttpResponseMessage response = new(HttpStatusCode.OK) { Content = unsized ? new StreamContent(new ForwardOnly(answer)) : new ByteArrayContent(answer) };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
            return response;
        }))
        { BaseAddress = new Uri("http://worker.invalid") };
        ArchiveUnpackClient client = new(http, new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Import:Worker:Token"] = "t" }).Build());

        ArchiveUnpackSession session = await client.OpenAsync(new MemoryStream(new byte[] { 1 }), 1, CancellationToken.None);
        string? violation = null;
        if (session.Status == ArchiveUnpackStatus.Ok)
        {
            try
            {
                await foreach ((UnpackManifestEntry _, Stream content) in session.ReadFilesAsync())
                    content.Dispose();
            }
            catch (ArchiveUnpackViolationException ex)
            {
                violation = ex.Violation;
            }
        }

        return (session, violation);
    }

    [Fact]
    public async Task Good_answer_is_ok()
    {
        byte[] data = { 1, 2, 3 };
        (ArchiveUnpackSession session, string? violation) = await RunAsync(Frame(Manifest(("a.txt", data)), data));
        session.Status.Should().Be(ArchiveUnpackStatus.Ok);
        violation.Should().BeNull();
    }

    [Fact]
    public async Task Bad_magic_is_bad_response()
    {
        (ArchiveUnpackSession session, _) = await RunAsync(Frame(Manifest(), magic: "NOPE"));
        session.Status.Should().Be(ArchiveUnpackStatus.BadResponse);
    }

    [Fact]
    public async Task Wrong_content_type_is_bad_response()
    {
        (ArchiveUnpackSession session, _) = await RunAsync(Frame(Manifest()), "application/json");
        session.Status.Should().Be(ArchiveUnpackStatus.BadResponse);
    }

    [Fact]
    public async Task Oversized_manifest_length_is_bad_response()
    {
        (ArchiveUnpackSession session, _) = await RunAsync(Frame(Manifest(), declaredLength: 2 * 1024 * 1024));
        session.Status.Should().Be(ArchiveUnpackStatus.BadResponse);
    }

    [Theory]
    [InlineData("../x.txt")]
    [InlineData("/x.txt")]
    [InlineData("a\\b.txt")]
    [InlineData("a/./b.txt")]
    public async Task Unsafe_path_in_manifest_is_bad_response(string path)
    {
        byte[] data = { 1 };
        (ArchiveUnpackSession session, _) = await RunAsync(Frame(Manifest((path, data)), data));
        session.Status.Should().Be(ArchiveUnpackStatus.BadResponse);
    }

    [Fact]
    public async Task Case_duplicate_paths_are_bad_response()
    {
        byte[] data = { 1 };
        (ArchiveUnpackSession session, _) = await RunAsync(Frame(Manifest(("A.txt", data), ("a.txt", data)), new byte[] { 1, 1 }));
        session.Status.Should().Be(ArchiveUnpackStatus.BadResponse);
    }

    [Fact]
    public async Task File_and_folder_clash_is_bad_response()
    {
        byte[] data = { 1 };
        (ArchiveUnpackSession session, _) = await RunAsync(Frame(Manifest(("a", data), ("a/b.txt", data)), new byte[] { 1, 1 }));
        session.Status.Should().Be(ArchiveUnpackStatus.BadResponse);
    }

    [Fact]
    public async Task Digest_mismatch_fails_before_the_entry_is_yielded()
    {
        byte[] claimed = { 1, 2, 3 };
        (ArchiveUnpackSession session, string? violation) = await RunAsync(Frame(Manifest(("a.txt", claimed)), new byte[] { 9, 9, 9 }));
        violation.Should().Be("digest_mismatch");
        session.Status.Should().Be(ArchiveUnpackStatus.BadResponse);
    }

    [Fact]
    public async Task Trailing_bytes_fail()
    {
        byte[] data = { 1, 2, 3 };
        (ArchiveUnpackSession session, string? violation) = await RunAsync(Frame(Manifest(("a.txt", data)), new byte[] { 1, 2, 3, 4 }), unsized: true);
        session.Status.Should().Be(ArchiveUnpackStatus.BadResponse, session.Violation);
        violation.Should().Be("body_trailing", session.Violation);
        session.Status.Should().Be(ArchiveUnpackStatus.BadResponse);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(2)]
    public async Task Length_not_matching_the_manifest_is_refused_up_front(int bodyLength)
    {
        byte[] data = { 1, 2, 3 };
        (ArchiveUnpackSession session, _) = await RunAsync(Frame(Manifest(("a.txt", data)), new byte[bodyLength]));
        session.Status.Should().Be(ArchiveUnpackStatus.BadResponse);
        session.Violation.Should().Be("length_mismatch");
    }

    [Fact]
    public async Task Short_body_fails()
    {
        byte[] data = { 1, 2, 3 };
        (ArchiveUnpackSession session, string? violation) = await RunAsync(Frame(Manifest(("a.txt", data)), new byte[] { 1, 2 }), unsized: true);
        violation.Should().Be("body_short", session.Violation);
        session.Status.Should().Be(ArchiveUnpackStatus.BadResponse);
    }

    [Fact]
    public async Task Aborted_manifest_with_files_is_bad_response()
    {
        byte[] data = { 1 };
        string json = JsonSerializer.Serialize(new
        {
            files = new[] { new { path = "a.txt", size = 1, sha256 = Sha(data) } },
            skipped = new { counts = new { }, samples = Array.Empty<object>() },
            aborted = true,
            abortReason = "TOO_MANY_FILES",
        });
        (ArchiveUnpackSession session, _) = await RunAsync(Frame(json, data));
        session.Status.Should().Be(ArchiveUnpackStatus.BadResponse);
    }

    [Fact]
    public async Task Bad_sha_shape_is_bad_response()
    {
        string json = JsonSerializer.Serialize(new
        {
            files = new[] { new { path = "a.txt", size = 1, sha256 = "XYZ" } },
            skipped = new { counts = new { }, samples = Array.Empty<object>() },
            aborted = false,
            abortReason = (string?)null,
        });
        (ArchiveUnpackSession session, _) = await RunAsync(Frame(json, new byte[] { 1 }));
        session.Status.Should().Be(ArchiveUnpackStatus.BadResponse);
    }

    [Fact]
    public async Task Entry_over_the_cap_is_bad_response()
    {
        string json = JsonSerializer.Serialize(new
        {
            files = new[] { new { path = "a.bin", size = 33L * 1024 * 1024, sha256 = Sha(new byte[1]) } },
            skipped = new { counts = new { }, samples = Array.Empty<object>() },
            aborted = false,
            abortReason = (string?)null,
        });
        (ArchiveUnpackSession session, _) = await RunAsync(Frame(json));
        session.Status.Should().Be(ArchiveUnpackStatus.BadResponse);
    }

    [Theory]
    [InlineData(503, ArchiveUnpackStatus.Busy)]
    [InlineData(413, ArchiveUnpackStatus.TooLarge)]
    [InlineData(415, ArchiveUnpackStatus.NotZip)]
    [InlineData(500, ArchiveUnpackStatus.Unavailable)]
    public async Task Status_codes_map(int code, ArchiveUnpackStatus expected)
    {
        HttpClient http = new(new Canned(() => new HttpResponseMessage((HttpStatusCode)code))) { BaseAddress = new Uri("http://worker.invalid") };
        ArchiveUnpackClient client = new(http, new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Import:Worker:Token"] = "t" }).Build());
        ArchiveUnpackSession session = await client.OpenAsync(new MemoryStream(new byte[] { 1 }), 1, CancellationToken.None);
        session.Status.Should().Be(expected);
    }
}
