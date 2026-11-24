#nullable enable
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using ReelBolt.Inference.Api.Services.Archives;
using ReelBolt.Shared.Archives;
using ReelBolt.WorkflowEngine.Tests.DesignSystems.Archives;
using ReelBolt.WorkflowEngine.Tests.DesignSystems.Worker;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Archives;

/// <summary>The unpack route through the real worker pipeline, answered to the real API client, on loopback.</summary>
[Collection(ProcessWideAllocationCollection.Name)]
public class UnpackZipEndToEndTests
{
    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    private static ArchiveUnpackClient ClientFor(WorkerServer server) =>
        new(server.Client, new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Import:Worker:Token"] = WorkerServer.Token })
            .Build());

    private static async Task<(ArchiveUnpackSession Session, Dictionary<string, byte[]> Files)> OpenAsync(WorkerServer server, byte[] zip)
    {
        ArchiveUnpackSession session = await ClientFor(server).OpenAsync(new MemoryStream(zip), zip.Length, CancellationToken.None);
        Dictionary<string, byte[]> files = new();
        if (session.Status == ArchiveUnpackStatus.Ok)
        {
            await foreach ((UnpackManifestEntry entry, Stream content) in session.ReadFilesAsync())
            {
                using MemoryStream copy = new();
                await content.CopyToAsync(copy);
                files[entry.Path] = copy.ToArray();
            }
        }

        return (session, files);
    }

    [Fact]
    public async Task Round_trip_returns_the_files_and_counts_exclusions()
    {
        byte[] zip = ZipFixtures.Build(
            ("src/app.ts", Bytes("export const a = 1;")),
            ("README.md", Bytes("hi")),
            ("node_modules/x/index.js", Bytes("x")),
            (".git/config", Bytes("g")),
            ("dist/out.js", Bytes("built")));

        await using WorkerServer server = await WorkerServer.StartAsync();
        (ArchiveUnpackSession session, Dictionary<string, byte[]> files) = await OpenAsync(server, zip);

        session.Status.Should().Be(ArchiveUnpackStatus.Ok);
        files.Keys.Should().BeEquivalentTo(new[] { "src/app.ts", "README.md" });
        files["src/app.ts"].Should().Equal(Bytes("export const a = 1;"));
        session.Manifest!.SkippedCounts["EXCLUDED_DEPENDENCIES"].Should().Be(1);
        session.Manifest.SkippedCounts["EXCLUDED_VCS"].Should().Be(1);
        session.Manifest.SkippedCounts["EXCLUDED_BUILD_OUTPUT"].Should().Be(1);
    }

    [Fact]
    public async Task Dist_only_zip_keeps_its_files()
    {
        byte[] zip = ZipFixtures.Build(("dist/index.js", Bytes("a")), ("dist/b.js", Bytes("b")));
        await using WorkerServer server = await WorkerServer.StartAsync();
        (ArchiveUnpackSession session, Dictionary<string, byte[]> files) = await OpenAsync(server, zip);
        session.Status.Should().Be(ArchiveUnpackStatus.Ok);
        files.Keys.Should().BeEquivalentTo(new[] { "dist/index.js", "dist/b.js" });
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("a/../../evil.txt")]
    [InlineData("/abs/evil.txt")]
    [InlineData("C:/evil.txt")]
    [InlineData("a\\..\\evil.txt")]
    public async Task Hostile_paths_never_reach_the_files(string name)
    {
        byte[] zip = ZipFixtures.Build(("ok.txt", Bytes("ok")), (name, Bytes("bad")));
        await using WorkerServer server = await WorkerServer.StartAsync();
        (ArchiveUnpackSession session, Dictionary<string, byte[]> files) = await OpenAsync(server, zip);

        session.Status.Should().Be(ArchiveUnpackStatus.Ok);
        files.Should().ContainKey("ok.txt", "the safe member arrives; only the hostile path is refused");
        files["ok.txt"].Should().Equal(Bytes("ok"));
        foreach (string path in files.Keys)
        {
            path.Should().NotContain("..");
            path.Should().NotStartWith("/");
            path.Should().NotContain("\\");
            path.Should().NotContain(":");
        }
    }

    [Fact]
    public async Task A_slow_store_between_reads_does_not_time_out_the_session()
    {
        byte[] zip = ZipFixtures.Build(("a.txt", Bytes("1")), ("b.txt", Bytes("2")));
        await using WorkerServer server = await WorkerServer.StartAsync();
        ArchiveUnpackClient client = ClientFor(server);
        client.RoundTripBudget = TimeSpan.FromMilliseconds(400);

        using ArchiveUnpackSession session = await client.OpenAsync(new MemoryStream(zip), zip.Length, CancellationToken.None);
        session.Status.Should().Be(ArchiveUnpackStatus.Ok);
        List<string> seen = [];
        await foreach ((UnpackManifestEntry entry, Stream content) in session.ReadFilesAsync())
        {
            await using Stream _ = content;
            seen.Add(entry.Path);
            await Task.Delay(900); // the caller's slow storage write, longer than the whole budget
        }

        seen.Should().BeEquivalentTo(new[] { "a.txt", "b.txt" });
        session.Status.Should().Be(ArchiveUnpackStatus.Ok);
    }

    [Fact]
    public async Task Case_duplicate_paths_never_both_arrive()
    {
        byte[] zip = ZipFixtures.Build(("A.txt", Bytes("1")), ("a.txt", Bytes("2")));
        await using WorkerServer server = await WorkerServer.StartAsync();
        (ArchiveUnpackSession session, Dictionary<string, byte[]> files) = await OpenAsync(server, zip);

        session.Status.Should().BeOneOf(ArchiveUnpackStatus.Ok, ArchiveUnpackStatus.Aborted);
        files.Keys.Select(k => k.ToUpperInvariant()).Distinct().Count().Should().Be(files.Count);
    }

    [Fact]
    public async Task Nested_zip_is_skipped_and_counted()
    {
        byte[] inner = ZipFixtures.Build(("i.txt", Bytes("i")));
        byte[] zip = ZipFixtures.Build(("keep.txt", Bytes("k")), ("inner.zip", inner));
        await using WorkerServer server = await WorkerServer.StartAsync();
        (ArchiveUnpackSession session, Dictionary<string, byte[]> files) = await OpenAsync(server, zip);

        session.Status.Should().Be(ArchiveUnpackStatus.Ok);
        files.Keys.Should().BeEquivalentTo(new[] { "keep.txt" });
        session.Manifest!.SkippedCounts.Should().ContainKey("NESTED_ARCHIVE");
    }

    [Fact]
    public async Task Bomb_ratio_yields_no_oversized_file()
    {
        byte[] zeros = new byte[8 * 1024 * 1024];
        byte[] zip = ZipFixtures.Build(("zeros.bin", zeros), ("ok.txt", Bytes("ok")));
        await using WorkerServer server = await WorkerServer.StartAsync();
        (ArchiveUnpackSession session, Dictionary<string, byte[]> files) = await OpenAsync(server, zip);

        session.Status.Should().BeOneOf(ArchiveUnpackStatus.Ok, ArchiveUnpackStatus.Aborted);
        files.Should().NotContainKey("zeros.bin");
        if (session.Status == ArchiveUnpackStatus.Aborted)
            files.Should().BeEmpty();
    }

    [Fact]
    public async Task More_than_2000_included_files_aborts_with_zero_files()
    {
        (string, byte[])[] many = Enumerable.Range(0, 2001).Select(i => ($"f/{i}.txt", new byte[] { 1 })).ToArray();
        byte[] zip = ZipFixtures.Build(many);
        await using WorkerServer server = await WorkerServer.StartAsync();
        (ArchiveUnpackSession session, Dictionary<string, byte[]> files) = await OpenAsync(server, zip);

        session.Status.Should().Be(ArchiveUnpackStatus.Aborted);
        session.Manifest!.AbortReason.Should().Be("TOO_MANY_FILES");
        session.Manifest.Files.Should().BeEmpty();
        files.Should().BeEmpty();
    }

    [Fact]
    public async Task Not_a_zip_is_415()
    {
        await using WorkerServer server = await WorkerServer.StartAsync();
        (ArchiveUnpackSession session, _) = await OpenAsync(server, Bytes("this is plainly not a zip archive"));
        session.Status.Should().Be(ArchiveUnpackStatus.NotZip);
    }

    [Fact]
    public async Task Oversize_declared_length_is_413()
    {
        await using WorkerServer server = await WorkerServer.StartAsync();
        using HttpRequestMessage request = new(HttpMethod.Post, "/v1/unpack-zip");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", WorkerServer.Token);
        request.Content = new ByteArrayContent(new byte[(int)UnpackLimits.MaxArchiveBytes + 1]);
        try
        {
            using HttpResponseMessage response = await server.Client.SendAsync(request);
            ((int)response.StatusCode).Should().Be(413);
        }
        catch (HttpRequestException)
        {
            // The server may cut the connection while the oversized body is still uploading: also a refusal.
        }
    }

    [Fact]
    public async Task Unpack_requires_the_bearer()
    {
        await using WorkerServer server = await WorkerServer.StartAsync();
        using HttpRequestMessage request = new(HttpMethod.Post, "/v1/unpack-zip") { Content = new ByteArrayContent(new byte[] { 1 }) };
        using HttpResponseMessage response = await server.Client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Wire_format_is_magic_length_manifest_then_bytes()
    {
        byte[] zip = ZipFixtures.Build(("a.txt", Bytes("hello")));
        await using WorkerServer server = await WorkerServer.StartAsync();
        using HttpRequestMessage request = new(HttpMethod.Post, "/v1/unpack-zip") { Content = new ByteArrayContent(zip) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", WorkerServer.Token);
        using HttpResponseMessage response = await server.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/vnd.reelbolt.unpack.v1");
        byte[] body = await response.Content.ReadAsByteArrayAsync();
        Encoding.ASCII.GetString(body, 0, 4).Should().Be("RBU1");
        uint manifestLength = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(4, 4));
        using JsonDocument manifest = JsonDocument.Parse(body.AsMemory(8, (int)manifestLength));
        manifest.RootElement.GetProperty("files")[0].GetProperty("size").GetInt64().Should().Be(5);
        body.AsSpan(8 + (int)manifestLength).ToArray().Should().Equal(Bytes("hello"));
    }

    [Fact]
    public async Task Extract_zip_route_still_exists_and_is_independent()
    {
        await using WorkerServer server = await WorkerServer.StartAsync();
        using HttpRequestMessage request = new(HttpMethod.Post, "/v1/extract-zip") { Content = new ByteArrayContent(ZipFixtures.Build(("a.txt", Bytes("x")))) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", WorkerServer.Token);
        using HttpResponseMessage response = await server.Client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().NotBe("application/vnd.reelbolt.unpack.v1");
    }
}
