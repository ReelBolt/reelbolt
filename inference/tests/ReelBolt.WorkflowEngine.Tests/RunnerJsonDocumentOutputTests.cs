using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Services.Compute;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// A runner job that returns a JSON document (the analysis extract, a manifest) is not probed as
/// media: it is size-checked, key-checked and parsed as JSON, while real media keeps the ffprobe check.
/// Fake store and fake probe only; nothing here touches a runner, a provider or the network.
/// </summary>
public class RunnerJsonDocumentOutputTests
{
    private static readonly Guid JobId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid ProjectId = Guid.Parse("f19e2c5b-4620-44e4-9fdd-60de4be0b01a");

    private sealed class Store : IRunnerOutputStore
    {
        public byte[] Bytes { get; set; } = Encoding.UTF8.GetBytes("{\"version\":2,\"meta\":{}}");
        public bool Downloadable { get; set; } = true;
        public int Promotions { get; private set; }
        public int Discards { get; private set; }
        public int Downloads { get; private set; }

        public Task<long?> TryGetSizeAsync(string key, CancellationToken ct) => Task.FromResult<long?>(Bytes.Length);
        public Task<string> CreateStagingGetUrlAsync(string stagingKey, TimeSpan ttl, CancellationToken ct) => Task.FromResult("https://objects.example/x");
        public Task<long> PromoteAsync(string stagingKey, string finalKey, long maxBytes, CancellationToken ct) { Promotions++; return Task.FromResult((long)Bytes.Length); }
        public Task DiscardAsync(string key, CancellationToken ct) { Discards++; return Task.CompletedTask; }
        public Task<string?> TryGetSha256Async(string key, long sizeBytes, long maxBytes, CancellationToken ct) => Task.FromResult<string?>("aa");

        public async Task<byte[]?> TryDownloadAsync(string key, string destinationPath, long maxBytes, CancellationToken ct)
        {
            Downloads++;
            if (!Downloadable) return null;
            await System.IO.File.WriteAllBytesAsync(destinationPath, Bytes, ct);
            return Bytes[..Math.Min(16, Bytes.Length)];
        }
    }

    private sealed class Probe : IVideoToolRunner
    {
        public int ProbeCalls { get; private set; }
        public Task<VideoToolResult> RunFfprobeAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, VideoToolPriority priority) => RunFfprobeAsync(args, timeout, ct);
        public Task<VideoToolResult> RunFfprobeAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
        {
            ProbeCalls++;
            return Task.FromResult(new VideoToolResult(1, string.Empty, "Invalid data found", false));
        }
        public Task<VideoToolResult> RunFfmpegAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, VideoToolPriority priority) => throw new NotSupportedException();
        public Task<VideoToolResult> RunFfmpegAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, Action<string> onStdOutLine, VideoToolPriority priority) => throw new NotSupportedException();
        public Task<VideoToolResult> RunFfmpegAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct) => throw new NotSupportedException();
        public Task<VideoToolResult> RunFfmpegAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, Action<string> onStdOutLine) => throw new NotSupportedException();
        public Task<VideoToolResult> RunFfmpegAsync(IMediaWorkspace workspace, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct) => throw new NotSupportedException();
        public Task<VideoToolResult> RunFfmpegAsync(IMediaWorkspace workspace, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, Action<string> onStdOutLine) => throw new NotSupportedException();
    }

    private static OutputVerifier Verifier(Store store, Probe probe) =>
        new(store, probe, Options.Create(new OutputVerificationOptions()), NullLogger<OutputVerifier>.Instance);

    private static RunnerOutputRequest Request(string contentType, string? finalKey = null, long maxBytes = 1_000_000) => new(
        ProjectId, JobId, 0,
        finalKey ?? $"projects/{ProjectId:D}/agentFiles/video-analysis/e/extract.json",
        RunnerJobTarget.CloudPool,
        RemoteMediaWorkspace.SpecForContentType(new RunnerOutputSpec(MaxBytes: maxBytes), contentType));

    [Theory]
    [InlineData("application/json")]
    [InlineData("application/json; charset=utf-8")]
    [InlineData("application/vnd.reelbolt+json")]
    public async Task A_json_document_is_promoted_without_being_probed_as_media(string contentType)
    {
        var store = new Store();
        var probe = new Probe();

        OutputVerificationResult result = await Verifier(store, probe).VerifyAndPromoteAsync(Request(contentType));

        result.Verified.Should().BeTrue(result.Message);
        probe.ProbeCalls.Should().Be(0);
        store.Promotions.Should().Be(1);
    }

    [Fact]
    public async Task A_json_array_is_accepted()
    {
        var store = new Store { Bytes = Encoding.UTF8.GetBytes("[1,2,3]") };
        (await Verifier(store, new Probe()).VerifyAndPromoteAsync(Request("application/json"))).Verified.Should().BeTrue();
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{\"a\":1} trailing")]
    [InlineData("{\"a\":")]
    [InlineData("42")]
    [InlineData("\"string\"")]
    public async Task A_json_output_that_is_not_one_object_or_array_is_refused_and_deleted(string body)
    {
        var store = new Store { Bytes = Encoding.UTF8.GetBytes(body) };

        OutputVerificationResult result = await Verifier(store, new Probe()).VerifyAndPromoteAsync(Request("application/json"));

        result.Verified.Should().BeFalse();
        result.FailureCode.Should().Be(OutputVerifier.InvalidDocumentCode);
        result.FailureClass.Should().Be(FailureClass.Runner);
        store.Discards.Should().Be(1);
        store.Promotions.Should().Be(0);
    }

    [Fact]
    public async Task A_large_json_document_with_a_bom_is_accepted_by_the_streaming_check()
    {
        var big = new StringBuilder("{\"items\":[");
        for (int n = 0; n < 50_000; n++) big.Append(n == 0 ? "" : ",").Append("{\"id\":").Append(n).Append(",\"t\":\"x\"}");
        big.Append("]}");
        var store = new Store { Bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(big.ToString())).ToArray() };

        OutputVerificationResult result = await Verifier(store, new Probe()).VerifyAndPromoteAsync(Request("application/json", maxBytes: 10_000_000));

        result.Verified.Should().BeTrue(result.Message);
    }

    [Fact]
    public async Task A_json_output_nested_past_the_depth_bound_is_refused()
    {
        string deep = new string('[', 200) + new string(']', 200);
        var store = new Store { Bytes = Encoding.UTF8.GetBytes(deep) };

        OutputVerificationResult result = await Verifier(store, new Probe()).VerifyAndPromoteAsync(Request("application/json"));

        result.FailureCode.Should().Be(OutputVerifier.InvalidDocumentCode);
    }

    [Fact]
    public async Task A_json_output_over_the_size_cap_is_refused_before_it_is_read()
    {
        var store = new Store();

        OutputVerificationResult result = await Verifier(store, new Probe()).VerifyAndPromoteAsync(Request("application/json", maxBytes: 4));

        result.FailureCode.Should().Be(OutputVerifier.TooLargeCode);
        store.Promotions.Should().Be(0);
    }

    [Theory]
    [InlineData("projects/00000000-0000-0000-0000-000000000000/agentFiles/x.json")]
    [InlineData("other/x.json")]
    [InlineData("projects/f19e2c5b-4620-44e4-9fdd-60de4be0b01a/../x.json")]
    public async Task A_json_output_whose_key_is_outside_the_project_is_refused(string key)
    {
        var store = new Store();

        OutputVerificationResult result = await Verifier(store, new Probe()).VerifyAndPromoteAsync(Request("application/json", key));

        result.FailureCode.Should().Be(OutputVerifier.InvalidDocumentCode);
        store.Promotions.Should().Be(0);
    }

    [Fact]
    public async Task An_undownloadable_json_output_is_refused()
    {
        var store = new Store { Downloadable = false };
        (await Verifier(store, new Probe()).VerifyAndPromoteAsync(Request("application/json"))).FailureCode
            .Should().Be(OutputVerifier.InvalidDocumentCode);
    }

    [Theory]
    [InlineData("video/mp4")]
    [InlineData("audio/wav")]
    [InlineData("image/jpeg")]
    [InlineData("application/octet-stream")]
    public async Task Media_and_unknown_content_types_keep_the_media_check(string contentType)
    {
        var store = new Store { Bytes = Encoding.UTF8.GetBytes("{\"version\":2}") };
        var probe = new Probe();

        OutputVerificationResult result = await Verifier(store, probe).VerifyAndPromoteAsync(Request(contentType));

        result.FailureCode.Should().Be(OutputVerifier.UnreadableCode);
        probe.ProbeCalls.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Plain_text_documents_skip_the_media_probe()
    {
        var store = new Store { Bytes = Encoding.UTF8.GetBytes("WEBVTT\n") };
        var probe = new Probe();

        OutputVerificationResult result = await Verifier(store, probe).VerifyAndPromoteAsync(Request("text/vtt"));

        result.Verified.Should().BeTrue(result.Message);
        probe.ProbeCalls.Should().Be(0);
    }

    [Fact]
    public async Task The_analysis_artifact_of_a_remote_VideoAnalyze_is_not_ffprobed()
    {
        // Production 2026-11-13: a 52637-byte analysis.json starting {"version":2,"me... was ffprobed and
        // refused RUNNER_OUTPUT_UNREADABLE. The probe here always fails, so any ffprobe call would refuse it.
        string pad = new('x', 52_000);
        var store = new Store { Bytes = Encoding.UTF8.GetBytes("{\"version\":2,\"meta\":{\"pad\":\"" + pad + "\"}}") };
        var probe = new Probe();

        OutputVerificationResult result = await Verifier(store, probe).VerifyAndPromoteAsync(
            Request("application/json", $"projects/{ProjectId:D}/agentFiles/video-analysis/e/step-1-analysis.json"));

        result.Verified.Should().BeTrue(result.Message);
        result.FailureCode.Should().BeNull();
        probe.ProbeCalls.Should().Be(0);
    }

    [Theory]
    [InlineData("application/x-subrip", "1\n00:00:00,000 --> 00:00:01,000\nHi\n")]
    [InlineData("text/vtt", "WEBVTT\n\n00:00.000 --> 00:01.000\nHi\n")]
    [InlineData("text/plain", "hello")]
    [InlineData("text/srt", "1\n00:00:00,000 --> 00:00:01,000\nHi\n")]
    [InlineData("application/x-subtitle", "1\n00:00:00,000 --> 00:00:01,000\nHi\n")]
    [InlineData("application/ttml+xml", "<tt/>")]
    public async Task Text_sidecars_are_checked_as_text_and_never_probed_as_media(string contentType, string body)
    {
        var store = new Store { Bytes = Encoding.UTF8.GetBytes(body) };
        var probe = new Probe();

        OutputVerificationResult result = await Verifier(store, probe).VerifyAndPromoteAsync(Request(contentType));

        result.Verified.Should().BeTrue(result.Message);
        probe.ProbeCalls.Should().Be(0);
        store.Promotions.Should().Be(1);
    }

    [Theory]
    [InlineData("application/x-subrip")]
    [InlineData("text/vtt")]
    public async Task A_text_sidecar_that_is_binary_or_not_utf8_is_refused_and_deleted(string contentType)
    {
        foreach (byte[] bad in new[] { new byte[] { 0x31, 0x00, 0x32 }, new byte[] { 0xFF, 0xFE, 0xC3, 0x28 } })
        {
            var store = new Store { Bytes = bad };

            OutputVerificationResult result = await Verifier(store, new Probe()).VerifyAndPromoteAsync(Request(contentType));

            result.Verified.Should().BeFalse();
            result.FailureCode.Should().Be(OutputVerifier.InvalidDocumentCode);
            store.Promotions.Should().Be(0);
            store.Discards.Should().Be(1);
        }
    }

    [Fact]
    public async Task An_empty_text_sidecar_and_one_outside_the_project_are_refused()
    {
        var empty = new Store { Bytes = [] };
        (await Verifier(empty, new Probe()).VerifyAndPromoteAsync(Request("application/x-subrip"))).Verified.Should().BeFalse();

        var store = new Store { Bytes = Encoding.UTF8.GetBytes("1") };
        (await Verifier(store, new Probe()).VerifyAndPromoteAsync(Request("text/vtt", "other/x.vtt"))).Verified.Should().BeFalse();
    }

    [Theory]
    [InlineData("video/mp4")]
    [InlineData("audio/mpeg")]
    [InlineData("image/jpeg")]
    [InlineData("application/octet-stream")]
    [InlineData("application/x-subrip-fake")]
    public void Real_media_and_unknown_types_still_get_the_media_check(string contentType)
    {
        RunnerOutputSpec spec = RemoteMediaWorkspace.SpecForContentType(new RunnerOutputSpec(MaxBytes: 10), contentType);

        spec.IsMedia.Should().BeTrue();
        spec.IsText.Should().BeFalse();
        spec.IsJson.Should().BeFalse();
    }

    // ---- text validation is chunked and capped ----------------------------------------------

    private static string? ValidateText(byte[] bytes)
    {
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rb-text-" + Guid.NewGuid().ToString("N"));
        try
        {
            System.IO.File.WriteAllBytes(path, bytes);
            return OutputVerifier.ValidateTextFile(path);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    private static byte[] Chunked(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    private static byte[] A(int n) => Enumerable.Repeat((byte)'a', n).ToArray();

    [Fact]
    public void A_multibyte_character_split_across_the_chunk_boundary_passes()
    {
        // The read chunk is 64 KiB: the 2-byte "e acute" starts on the last byte of the first chunk.
        ValidateText(Chunked(A(65535), [0xC3, 0xA9], A(10))).Should().BeNull();
        ValidateText(Chunked(A(65534), [0xE2, 0x82, 0xAC], A(10))).Should().BeNull("a 3-byte character split 2/1 passes too");
    }

    [Fact]
    public void Invalid_utf8_at_the_chunk_boundary_is_refused()
    {
        ValidateText(Chunked(A(65535), [0xC3], A(10))).Should().Contain("not valid UTF-8");
        ValidateText(Chunked(A(65535), [0xC3])).Should().Contain("not valid UTF-8", "a truncated sequence at the very end is invalid");
        ValidateText(Chunked(A(65536), [0xFF])).Should().Contain("not valid UTF-8");
    }

    [Fact]
    public void A_nul_byte_in_a_later_chunk_is_refused()
        => ValidateText(Chunked(A(70000), [0x00], A(5))).Should().Contain("binary");

    [Fact]
    public void A_bom_and_crlf_pass()
    {
        ValidateText(Chunked([0xEF, 0xBB, 0xBF], Encoding.UTF8.GetBytes("1\r\n00:00:00,000 --> 00:00:01,000\r\nHi\r\n\r\n"))).Should().BeNull();
    }

    [Fact]
    public async Task A_text_document_over_the_cap_is_refused_before_it_is_downloaded()
    {
        var store = new Store { Bytes = new byte[OutputVerifier.MaxTextDocumentBytes + 1] };
        var probe = new Probe();

        OutputVerificationResult result = await Verifier(store, probe).VerifyAndPromoteAsync(Request("application/x-subrip", maxBytes: 1L << 30));

        result.Verified.Should().BeFalse();
        result.FailureCode.Should().Be(OutputVerifier.InvalidDocumentCode);
        store.Downloads.Should().Be(0, "the size alone refuses it");
        probe.ProbeCalls.Should().Be(0);
    }

    [Fact]
    public void ValidateTextFile_refuses_an_oversized_file_from_its_length()
    {
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rb-big-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var f = System.IO.File.Create(path))
                f.SetLength(OutputVerifier.MaxTextDocumentBytes + 1);
            OutputVerifier.ValidateTextFile(path).Should().Contain("too large");
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }
}
