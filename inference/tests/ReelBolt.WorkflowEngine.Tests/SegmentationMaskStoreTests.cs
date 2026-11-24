#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Services.Segmentation;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The content-addressed segmentation mask store (initiative "prompted region effects", plan §6): a
/// repeated prompt over the same footage must be free, and a missing/corrupt/mismatched entry must be a
/// MISS rather than a crash or a wrong mask.
/// </summary>
public sealed class SegmentationMaskStoreTests
{
    private static readonly Guid Project = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Fact]
    public void Every_field_of_the_key_changes_the_content_key()
    {
        SegmentationMaskKey baseline = new(
            SourceContentHash: "abc123",
            Prompt: "speedometer",
            Model: SegmentationCreditPricingModel,
            DetectionThreshold: 0.5,
            MaxObjects: 4,
            Seed: "-",
            ProviderFingerprint: "falai:kind:model");

        string key = Store().ContentKey(baseline);

        key.Should().MatchRegex("^[0-9a-f]{64}$");
        Store().ContentKey(baseline with { }).Should().Be(key, "the same inputs always hash the same");

        SegmentationMaskKey[] variants =
        [
            baseline with { SourceContentHash = "abc124" },
            baseline with { Prompt = "speedometeR" },
            baseline with { Model = "fal-ai/sam-3-1/video" },
            baseline with { DetectionThreshold = 0.51 },
            baseline with { MaxObjects = 5 },
            baseline with { Seed = "p:0.1,0.2@0" },
            baseline with { ProviderFingerprint = null },
            baseline with { ProviderFingerprint = "other:kind:model" },
            baseline with { MatchScope = ObjectMatchScope.SingleTrack }
        ];

        foreach (SegmentationMaskKey variant in variants)
        {
            Store().ContentKey(variant).Should().NotBe(key, "each field is part of what the vendor would answer");
        }
    }

    [Fact]
    public void A_keys_fields_cannot_be_smuggled_across_the_separators()
    {
        // Length prefixes, so moving characters between two fields cannot produce the same key.
        SegmentationMaskKey a = new("ab", "c", "m", 0.5, 4, "-", "f");
        SegmentationMaskKey b = new("a", "bc", "m", 0.5, 4, "-", "f");

        Store().ContentKey(a).Should().NotBe(Store().ContentKey(b));
    }

    [Fact]
    public async Task A_missing_mask_is_a_miss_and_writes_nothing()
    {
        var workspace = new SessionWorkspace();
        var files = new RecordingFiles(exists: false);

        SegmentationMaskEntry? entry = await Store(files).TryGetAsync(Project, "deadbeef", workspace, default);

        entry.Should().BeNull();
        files.HeadCalls.Should().ContainSingle().Which.Should().Be(
            SegmentationMaskStore.MaskStorageKeyFor(Project, "deadbeef"));
        workspace.Staged.Should().BeEmpty("a miss must not stage anything");
    }

    [Fact]
    public async Task A_put_then_a_get_round_trips_through_the_object_store()
    {
        var workspace = new SessionWorkspace();
        var files = new RecordingFiles(exists: false);
        ISegmentationMaskStore store = Store(files);
        string maskPath = Path.Combine(Path.GetTempPath(), "t004-mask-" + Guid.NewGuid().ToString("N") + ".mkv");
        await File.WriteAllBytesAsync(maskPath, [1, 2, 3, 4, 5]);

        try
        {
            SegmentationMaskEntry written = await store.PutAsync(
                Project,
                workspace,
                maskPath,
                "segmentation/mask.mkv",
                new SegmentationMaskEntry(
                    ContentKey: "cafe",
                    MaskStorageKey: string.Empty,
                    MaskContentHash: string.Empty,
                    Width: 4,
                    Height: 2,
                    FrameCount: 2,
                    Fps: 24,
                    Coverage: 0.25,
                    Confidence: 0.5,
                    StartSec: 0,
                    EndSec: 1,
                    Model: SegmentationCreditPricingModel,
                    ProviderFingerprint: "falai:model",
                    EmptyFrames: 1,
                    LongestGapFrames: 1,
                    DetectionThreshold: 0.5,
                    FpsMeasured: 24)
                {
                    Boxes = [[0, 0.25, 0.25, 0.5, 0.5]]
                },
                default);

            written.MaskStorageKey.Should().Be(SegmentationMaskStore.MaskStorageKeyFor(Project, "cafe"));
            written.MaskContentHash.Should().Be(Convert.ToHexStringLower(SHA256.HashData(new byte[] { 1, 2, 3, 4, 5 })));

            files.Exists = true;
            SegmentationMaskEntry? read = await store.TryGetAsync(Project, "cafe", workspace, default);

            read.Should().NotBeNull();
            read!.MaskContentHash.Should().Be(written.MaskContentHash);
            read.FrameCount.Should().Be(2);
            read.Boxes.Should().NotBeNull();
            read.Boxes!.Single().Should().Equal(0, 0.25, 0.25, 0.5, 0.5);
        }
        finally
        {
            File.Delete(maskPath);
        }
    }

    [Fact]
    public async Task Metadata_that_names_another_key_is_a_miss()
    {
        var workspace = new SessionWorkspace();
        var files = new RecordingFiles(exists: true);
        string metadataPath = SegmentationMaskStore.MetadataVirtualPath("cafe");
        workspace.Files[metadataPath] = JsonSerializer.SerializeToUtf8Bytes(new { contentKey = "other" });

        SegmentationMaskEntry? entry = await Store(files).TryGetAsync(Project, "cafe", workspace, default);

        entry.Should().BeNull("a stored path or key that disagrees with what was asked for must not be trusted");
    }

    [Fact]
    public async Task Unreadable_metadata_is_a_miss_not_a_crash()
    {
        var workspace = new SessionWorkspace();
        var files = new RecordingFiles(exists: true);
        workspace.Files[SegmentationMaskStore.MetadataVirtualPath("cafe")] = "{ not json"u8.ToArray();

        SegmentationMaskEntry? entry = await Store(files).TryGetAsync(Project, "cafe", workspace, default);

        entry.Should().BeNull();
    }

    [Fact]
    public async Task A_cancelled_get_throws_rather_than_reporting_a_miss()
    {
        var workspace = new SessionWorkspace();
        var files = new RecordingFiles(exists: true);
        using CancellationTokenSource cts = new();
        await cts.CancelAsync();

        Func<Task> get = async () => await Store(files).TryGetAsync(Project, "cafe", workspace, cts.Token);

        await get.Should().ThrowAsync<OperationCanceledException>();
    }

    private const string SegmentationCreditPricingModel = "fal-ai/sam-3-1/video-rle";

    private static SegmentationMaskStore Store(RecordingFiles? files = null) =>
        new(files ?? new RecordingFiles(exists: false), NullLogger<SegmentationMaskStore>.Instance);

    private sealed class RecordingFiles(bool exists) : IProjectFileWorkspace
    {
        public bool Exists { get; set; } = exists;

        public List<string> HeadCalls { get; } = [];

        public Task<bool> ArtifactExistsAsync(Guid projectId, string storageKey, CancellationToken ct)
        {
            // A real store surfaces cancellation; the store under test must not swallow it into a miss.
            ct.ThrowIfCancellationRequested();
            HeadCalls.Add(storageKey);
            return Task.FromResult(Exists);
        }

        public Task<IReadOnlyList<ProjectWorkspaceFile>> ListFilesAsync(Guid projectId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<string> ReadFileAsync(Guid projectId, string fileReference, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<ProjectWorkspaceFile> WriteTextFileAsync(
            Guid projectId, string fileName, string content, string contentType, CancellationToken ct,
            string category = "agentFiles", string? originalPath = null) => throw new NotSupportedException();

        public Task DownloadStorageKeyToFileAsync(Guid projectId, string storageKey, string destinationPath, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task DownloadStorageKeyToFileAsync(
            Guid projectId, string storageKey, string destinationPath, long maxBytes, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<ProjectWorkspaceFile> UploadBinaryFileAsync(
            Guid projectId, string localFilePath, string fileName, string contentType,
            SummaryStatus summaryStatus, FileIndexingStatus indexingStatus, CancellationToken ct,
            string category = "outputFiles", string? originalPath = null) => throw new NotSupportedException();

        public Task<ProjectWorkspaceFile> RegisterStoredObjectAsync(
            Guid projectId, string storageKey, string fileName, string contentType, long sizeBytes,
            SummaryStatus summaryStatus, FileIndexingStatus indexingStatus, CancellationToken ct,
            string category = "outputFiles") => throw new NotSupportedException();

        public Task<string> UploadArtifactAsync(
            Guid projectId, string localFilePath, string fileName, string contentType, CancellationToken ct,
            string category = "agentFiles") => throw new NotSupportedException();
    }

    private sealed class SessionWorkspace : IMediaWorkspace
    {
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);

        public List<string> Staged { get; } = [];

        public string RootPath => "session:/";

        public Task StageAsync(string storageKey, string virtualPath, CancellationToken ct)
        {
            Staged.Add(virtualPath);
            if (!Files.ContainsKey(virtualPath))
            {
                Files[virtualPath] = "{}"u8.ToArray();
            }

            return Task.CompletedTask;
        }

        public Task<VideoToolResult> RunFfmpegAsync(IReadOnlyList<string> argv, TimeSpan timeout, CancellationToken ct) =>
            Task.FromResult(new VideoToolResult(0, string.Empty, string.Empty, false));

        public Task<VideoToolResult> RunFfmpegAsync(
            IReadOnlyList<string> argv, TimeSpan timeout, CancellationToken ct, Action<string> onStdOutLine) =>
            Task.FromResult(new VideoToolResult(0, string.Empty, string.Empty, false));

        public Task<VideoToolResult> RunFfprobeAsync(IReadOnlyList<string> argv, TimeSpan timeout, CancellationToken ct) =>
            Task.FromResult(new VideoToolResult(0, string.Empty, string.Empty, false));

        public Task<string> MaterializeAsync(string virtualPath, CancellationToken ct) => Task.FromResult(virtualPath);

        public Task WriteBytesAsync(string virtualPath, byte[] content, CancellationToken ct)
        {
            Files[virtualPath] = content;
            return Task.CompletedTask;
        }

        public Task WriteTextAsync(string virtualPath, string content, Encoding encoding, CancellationToken ct) =>
            WriteBytesAsync(virtualPath, encoding.GetBytes(content), ct);

        public Task<byte[]> ReadAsync(string virtualPath, CancellationToken ct) =>
            ReadAsync(virtualPath, int.MaxValue, ct);

        public Task<byte[]> ReadAsync(string virtualPath, int maxBytes, CancellationToken ct) =>
            Files.TryGetValue(virtualPath, out byte[]? bytes)
                ? Task.FromResult(bytes)
                : throw new FileNotFoundException($"Media workspace file '{virtualPath}' does not exist.", virtualPath);

        public Task<MediaWorkspaceStat> StatAsync(string virtualPath, CancellationToken ct) =>
            Task.FromResult(Files.TryGetValue(virtualPath, out byte[]? bytes)
                ? new MediaWorkspaceStat(true, bytes.Length)
                : new MediaWorkspaceStat(false, 0));

        public Task<string> PublishAsync(string virtualPath, string finalStorageKey, string contentType, CancellationToken ct) =>
            Task.FromResult(finalStorageKey);

        public Task<string> UploadArtifactAsync(string virtualPath, string finalStorageKey, string contentType, CancellationToken ct) =>
            Task.FromResult(finalStorageKey);
    }
}
