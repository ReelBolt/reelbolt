#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReelBolt.Shared;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.IntegrationEvents;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;
using Xunit.Abstractions;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Real-ffmpeg coverage for the media-derivative WRITER — <see cref="MediaDerivativeDispatcher"/> and
/// the progress seam it reports through. The four things this file exists to prove are the ones the
/// wave is reviewed for plus the ticket's own idempotency requirement:
///
/// <list type="number">
/// <item><description><b>Each derivative is produced with the documented arguments</b> and its objects
/// land under <c>projects/{p}/agentFiles/media-derivatives/{sourceHash}/</c>.</description></item>
/// <item><description><b>A repeat request is a cache hit, asserted by invocation count</b> — zero
/// ffmpeg invocations and zero new rows.</description></item>
/// <item><description><b>No cache-identity collision:</b> two sources differing only by etag produce
/// two rows with two different cache keys.</description></item>
/// <item><description><b>The persisted key is byte-identical to <c>MediaDerivativeIdentity.Compute</c></b>
/// — the backend's mirror of the same identity, whose equality is the whole contract.</description></item>
/// </list>
///
/// <remarks>
/// Mocks are deliberately not used for the ffmpeg layer: the point of these tests is that real ffmpeg
/// accepts the argv this path builds and produces the artifacts the rows claim. The object store and
/// the progress sink <i>are</i> stubs, because reproducing S3 locally would test nothing this ticket
/// owns — but the workspace stub builds its storage keys through the production
/// <see cref="ProjectFilePath"/> helpers, so the layout asserted below is the real one.
/// </remarks>
public class MediaDerivativeCacheHitFfmpegTests
{
    private readonly ITestOutputHelper _output;

    public MediaDerivativeCacheHitFfmpegTests(ITestOutputHelper output) => _output = output;

    [FfmpegToolsFact]
    public async Task Every_derivative_is_produced_with_the_documented_argv_and_lands_under_the_sources_prefix()
    {
        string directory = MediaDerivativeFfmpegFixture.NewDirectory();
        using MediaDerivativeDispatchFixture fixture = MediaDerivativeDispatchFixture.Create(directory);
        try
        {
            // 35 seconds, not 3: ffmpeg's `fps=1/30` filter emits NOTHING for an input shorter than
            // the interval (measured: 0 frames for a 3 s source, 1 for a 35 s one), so a short fixture
            // would leave the 30 s filmstrip density legitimately unproducible and this test would be
            // asserting a gap rather than the pipeline.
            string source = MediaDerivativeFfmpegFixture.CreateSource(
                directory, "src.mp4", seconds: 35, fps: 30, width: 320, height: 240, withAudio: true);
            fixture.ServeAsSourceObject(source);

            Guid projectId = Guid.NewGuid();
            string storageKey = $"projects/{projectId:D}/userFiles/{Guid.NewGuid():N}.mp4";
            const string etag = "\"etag-1\"";

            await fixture.Dispatcher.RunAsync(
                fixture.Request(projectId, storageKey, etag, [
                    MediaDerivativeKind.Proxy,
                    MediaDerivativeKind.Filmstrip,
                    MediaDerivativeKind.WaveformPeaks,
                    MediaDerivativeKind.KeyframeIndex
                ]),
                CancellationToken.None);

            IReadOnlyList<MediaDerivative> rows = fixture.ReadRows();
            IReadOnlyList<IReadOnlyList<string>> ffmpeg = fixture.Runner.FfmpegArgvs;
            IReadOnlyList<IReadOnlyList<string>> ffprobe = fixture.Runner.FfprobeArgvs;

            // ---- the documented argv shapes, one per derivative family ----
            ffmpeg.Should().Contain(
                args => ContainsFilter(args, "scale=-2:540:flags=bicubic"),
                "the proxy must be 540p for a sub-UHD source, aspect-preserving via scale=-2");
            ffmpeg.Count(args => ContainsFilter(args, "scale=-2:540:flags=bicubic")).Should().Be(1);
            ffmpeg.Count(args => args.Any(t => t.Contains("fps=1/1,split=2", StringComparison.Ordinal))).Should().Be(1,
                "all filmstrip densities come from ONE ffmpeg run");
            ffmpeg.Should().Contain(args => args.Any(t =>
                t.Contains("[a1]scale=160:-2,tile=10x10", StringComparison.Ordinal) &&
                t.Contains("[v5]fps=1/5,split=2", StringComparison.Ordinal) &&
                t.Contains("[a5]scale=160:-2,tile=10x10", StringComparison.Ordinal)),
                "filmstrip densities 1 s and 5 s in the shared graph");
            ffmpeg.Should().Contain(args => ContainsInOrder(args, "-f", "s16le"),
                "waveform peaks decode to a raw s16le file rather than a pipe");
            ffmpeg.Should().Contain(args => ContainsInOrder(args, "-movflags", "+faststart"));

            ffmpeg.Should().Contain(args => ContainsInOrder(args, "-g", "15"),
                "a 30 fps source gets a 15-frame (half-second) GOP");

            ffprobe.Should().Contain(args => ContainsInOrder(args, "-show_entries", "packet=pts_time,flags"),
                "the keyframe index is a packet-level probe");

            // ---- the object layout ----
            string sourceHash = MediaDerivativeCacheKey.ComputeSourceHash(storageKey, etag);
            string expectedPrefix = $"projects/{projectId:D}/agentFiles/media-derivatives/{sourceHash}/";

            fixture.Workspace.UploadedKeys.Should().NotBeEmpty();
            fixture.Workspace.UploadedKeys.Should().OnlyContain(key => key.StartsWith(expectedPrefix, StringComparison.Ordinal));
            rows.Should().OnlyContain(row => row.StorageKey.StartsWith(expectedPrefix, StringComparison.Ordinal));

            // ---- one row per (kind, variant), eight in total ----
            rows.Should().HaveCount(8);
            rows.Count(r => r.Kind == MediaDerivativeKind.Proxy).Should().Be(1);
            rows.Count(r => r.Kind == MediaDerivativeKind.Filmstrip).Should().Be(3);
            rows.Count(r => r.Kind == MediaDerivativeKind.WaveformPeaks).Should().Be(3);
            rows.Count(r => r.Kind == MediaDerivativeKind.KeyframeIndex).Should().Be(1);

            rows.Select(r => r.CacheKey).Distinct(StringComparer.Ordinal).Should().HaveCount(8,
                "eight derivatives of one source must have eight distinct cache keys");
            rows.Should().OnlyContain(r => r.ProjectId == projectId);
            rows.Should().OnlyContain(r => r.SourceKind == MediaSourceKind.ProjectFile);
            rows.Should().OnlyContain(r => r.SourceStorageKey == storageKey);
            rows.Should().OnlyContain(r => r.SourceETag == etag);
            rows.Should().OnlyContain(r => r.BuilderVersion > 0);
            rows.Should().OnlyContain(r => r.ParamsJson != "{}");
            rows.Should().OnlyContain(r => r.DurationSec > 34.0 && r.DurationSec < 36.0);

            // Each row points at an object of its own: three peaks rows must not collapse onto the one
            // sidecar document, which is identical for every level of detail.
            rows.Select(r => r.StorageKey).Distinct(StringComparer.Ordinal).Should().HaveCount(8,
                "eight derivatives must name eight distinct objects, not one shared sidecar");

            // The three peaks rows address the three distinct LOD levels.
            rows.Where(r => r.Kind == MediaDerivativeKind.WaveformPeaks)
                .Select(r => r.StorageKey)
                .Should().OnlyContain(key => key.EndsWith(".raw", StringComparison.Ordinal));

            // The proxy row is the one that carries produced pixel dimensions.
            MediaDerivative proxy = rows.Single(r => r.Kind == MediaDerivativeKind.Proxy);
            proxy.Width.Should().Be(720, "320x240 scaled to 540 high is 720 wide, and -2 keeps it even");
            proxy.Height.Should().Be(540);

            // ---- the source was downloaded exactly once for eight derivatives ----
            fixture.Workspace.DownloadCount.Should().Be(1,
                "four derivative families and eight variants must not mean eight downloads");

            // ---- the terminal stage was Done, nothing failed ----
            fixture.Sink.Stages.Should().Contain(MediaDerivativeProgressStage.Done);
            fixture.Sink.Stages.Should().NotContain(MediaDerivativeProgressStage.Failed);

            _output.WriteLine(
                "MEASURED full set: {0} rows, {1} ffmpeg invocations, {2} ffprobe invocations, {3} downloads, " +
                "{4} uploaded objects under {5}",
                rows.Count, ffmpeg.Count, ffprobe.Count, fixture.Workspace.DownloadCount,
                fixture.Workspace.UploadedKeys.Count, expectedPrefix);
        }
        finally
        {
            MediaDerivativeFfmpegFixture.TryDelete(directory);
        }
    }

    [FfmpegToolsFact]
    public async Task A_repeat_request_is_a_cache_hit_with_zero_ffmpeg_invocations_and_zero_new_rows()
    {
        string directory = MediaDerivativeFfmpegFixture.NewDirectory();
        using MediaDerivativeDispatchFixture fixture = MediaDerivativeDispatchFixture.Create(directory);
        try
        {
            string source = MediaDerivativeFfmpegFixture.CreateSource(
                directory, "src.mp4", seconds: 2, fps: 30, width: 320, height: 240, withAudio: true);
            fixture.ServeAsSourceObject(source);

            Guid projectId = Guid.NewGuid();
            string storageKey = $"projects/{projectId:D}/userFiles/{Guid.NewGuid():N}.mp4";
            MediaDerivativesRequested request = fixture.Request(
                projectId, storageKey, "\"etag-1\"", [MediaDerivativeKind.Proxy]);

            // ---- first request: a real encode ----
            await fixture.Dispatcher.RunAsync(request, CancellationToken.None);

            int ffmpegAfterFirstRun = fixture.Runner.FfmpegCount;
            int rowsAfterFirstRun = fixture.ReadRows().Count;

            ffmpegAfterFirstRun.Should().BeGreaterThan(0, "the first request must actually produce the proxy");
            rowsAfterFirstRun.Should().Be(1);

            _output.WriteLine(
                "MEASURED first run: {0} ffmpeg invocations, {1} ffprobe invocations, {2} rows",
                ffmpegAfterFirstRun, fixture.Runner.FfprobeCount, rowsAfterFirstRun);

            // ---- second, identical request: not a single re-encode ----
            fixture.Runner.ResetInvocations();

            await fixture.Dispatcher.RunAsync(request, CancellationToken.None);

            int ffmpegAfterSecondRun = fixture.Runner.FfmpegCount;
            int rowsAfterSecondRun = fixture.ReadRows().Count;

            _output.WriteLine(
                "MEASURED repeat run: {0} ffmpeg invocations, {1} ffprobe invocations, {2} rows",
                ffmpegAfterSecondRun, fixture.Runner.FfprobeCount, rowsAfterSecondRun);

            ffmpegAfterSecondRun.Should().Be(0,
                "a second identical request must perform ZERO ffmpeg invocations — asserted by invocation count, " +
                "not by timing");
            rowsAfterSecondRun.Should().Be(rowsAfterFirstRun,
                "a second identical request must write ZERO new rows");
            fixture.Workspace.DownloadCount.Should().Be(2, "each request downloads once, and each request is one download");

            MediaDerivativeProgressUpdate hit = fixture.Sink.Updates
                .Last(u => u.Stage == MediaDerivativeProgressStage.Skipped && u.CacheKey is not null);
            hit.CacheKey.Should().NotBeNullOrEmpty();
            hit.Detail.Should().Contain("Cache hit");
        }
        finally
        {
            MediaDerivativeFfmpegFixture.TryDelete(directory);
        }
    }

    [FfmpegToolsFact]
    public async Task Two_sources_differing_only_by_etag_produce_two_rows_with_two_different_cache_keys()
    {
        string directory = MediaDerivativeFfmpegFixture.NewDirectory();
        using MediaDerivativeDispatchFixture fixture = MediaDerivativeDispatchFixture.Create(directory);
        try
        {
            string source = MediaDerivativeFfmpegFixture.CreateSource(
                directory, "src.mp4", seconds: 2, fps: 30, width: 320, height: 240, withAudio: true);
            fixture.ServeAsSourceObject(source);

            Guid projectId = Guid.NewGuid();
            string storageKey = $"projects/{projectId:D}/userFiles/{Guid.NewGuid():N}.mp4";

            // THE wave's review focus. Same project, same storage key — bytes that a user replaced
            // under the same key — and only the content identity differs. If the cache key did not
            // carry the etag, the second request would be served the first file's derivative and one
            // row would exist where two are required.
            await fixture.Dispatcher.RunAsync(
                fixture.Request(projectId, storageKey, "\"etag-A\"", [MediaDerivativeKind.KeyframeIndex]),
                CancellationToken.None);

            await fixture.Dispatcher.RunAsync(
                fixture.Request(projectId, storageKey, "\"etag-B\"", [MediaDerivativeKind.KeyframeIndex]),
                CancellationToken.None);

            IReadOnlyList<MediaDerivative> rows = fixture.ReadRows();

            rows.Should().HaveCount(2);
            rows.Select(r => r.SourceETag).Should().BeEquivalentTo(new[] { "\"etag-A\"", "\"etag-B\"" });
            rows.Should().OnlyContain(r => r.SourceStorageKey == storageKey);
            rows.Select(r => r.CacheKey).Distinct(StringComparer.Ordinal).Should().HaveCount(2,
                "two sources differing only by etag must never collide onto one media_derivatives row");

            // And the two landed in different storage directories, so the objects cannot collide
            // either — a shared row would have hidden that, a shared prefix would not.
            MediaDerivativeCacheKey.ComputeSourceHash(storageKey, "\"etag-A\"")
                .Should().NotBe(MediaDerivativeCacheKey.ComputeSourceHash(storageKey, "\"etag-B\""));

            _output.WriteLine(
                "MEASURED collision test: {0} rows, cache keys {1} and {2}, both for storage key {3}",
                rows.Count, rows[0].CacheKey[..16], rows[1].CacheKey[..16], storageKey);
        }
        finally
        {
            MediaDerivativeFfmpegFixture.TryDelete(directory);
        }
    }

    [FfmpegToolsFact]
    public async Task The_persisted_cache_key_is_byte_identical_to_the_backends_MediaDerivativeIdentity()
    {
        string directory = MediaDerivativeFfmpegFixture.NewDirectory();
        using MediaDerivativeDispatchFixture fixture = MediaDerivativeDispatchFixture.Create(directory);
        try
        {
            string source = MediaDerivativeFfmpegFixture.CreateSource(
                directory, "src.mp4", seconds: 2, fps: 30, width: 320, height: 240, withAudio: false);
            fixture.ServeAsSourceObject(source);

            Guid projectId = Guid.NewGuid();
            string storageKey = $"projects/{projectId:D}/userFiles/{Guid.NewGuid():N}.mov";
            const string etag = "\"etag-identity\"";

            await fixture.Dispatcher.RunAsync(
                fixture.Request(projectId, storageKey, etag, [MediaDerivativeKind.Proxy]),
                CancellationToken.None);

            MediaDerivative row = fixture.ReadRows().Single();

            // The producer computes it, the consumer recomputes it, and the two are compared as one
            // value. MediaDerivativeIdentity.Compute is the backend's own mirror (T-104); a single
            // character of divergence would silently produce a cache that never hits rather than an
            // error, so it is asserted here rather than assumed.
            string identity = MediaDerivativeIdentity.Compute(
                row.SourceStorageKey,
                row.SourceETag,
                row.Kind,
                variant: 0,
                row.ParamsHash,
                row.BuilderVersion);

            row.CacheKey.Should().Be(identity);
            row.CacheKey.Should().HaveLength(MediaDerivativeIdentity.HexLength);
        }
        finally
        {
            MediaDerivativeFfmpegFixture.TryDelete(directory);
        }
    }

    [FfmpegToolsFact]
    public async Task The_dispatchers_proxy_spec_matches_the_services_own_resolution()
    {
        string directory = MediaDerivativeFfmpegFixture.NewDirectory();
        using MediaDerivativeDispatchFixture fixture = MediaDerivativeDispatchFixture.Create(directory);
        try
        {
            string source = MediaDerivativeFfmpegFixture.CreateSource(
                directory, "src.mp4", seconds: 2, fps: 25, width: 640, height: 360, withAudio: true);
            fixture.ServeAsSourceObject(source);

            Guid projectId = Guid.NewGuid();
            string storageKey = $"projects/{projectId:D}/userFiles/{Guid.NewGuid():N}.mp4";
            const string etag = "\"etag-spec\"";

            await fixture.Dispatcher.RunAsync(
                fixture.Request(projectId, storageKey, etag, [MediaDerivativeKind.Proxy]),
                CancellationToken.None);

            // The dispatcher probes the source itself (at Low priority) rather than going through
            // IMediaProbe, which has no priority overload. This pins that the two paths resolve the
            // SAME spec, so the duplication can never drift into producing bytes that do not match
            // their own cache key.
            MediaDerivativeSpec expected = await fixture.Service.ResolveSpecAsync(
                MediaDerivativeKindSlug.Proxy, 0, source, CancellationToken.None);

            MediaDerivative row = fixture.ReadRows().Single();

            row.ParamsJson.Should().Be(expected.ParamsJson);
            row.CacheKey.Should().Be(MediaDerivativeCacheKey.ComputeCacheKey(storageKey, etag, expected));
            row.SourceETag.Should().Be(etag);
        }
        finally
        {
            MediaDerivativeFfmpegFixture.TryDelete(directory);
        }
    }

    [FfmpegToolsFact]
    public async Task An_empty_published_etag_falls_back_to_the_content_hash_and_stays_stable()
    {
        string directory = MediaDerivativeFfmpegFixture.NewDirectory();
        using MediaDerivativeDispatchFixture fixture = MediaDerivativeDispatchFixture.Create(directory);
        try
        {
            string source = MediaDerivativeFfmpegFixture.CreateSource(
                directory, "src.mp4", seconds: 2, fps: 30, width: 320, height: 240, withAudio: true);
            fixture.ServeAsSourceObject(source);

            Guid projectId = Guid.NewGuid();
            string storageKey = $"projects/{projectId:D}/userFiles/{Guid.NewGuid():N}.mp4";

            // "A failed best-effort HEAD must never fail an upload" — so an empty ETag is a legitimate
            // message, and the row must still be keyed on real content identity rather than on "".
            MediaDerivativesRequested request = fixture.Request(
                projectId, storageKey, sourceEtag: string.Empty, [MediaDerivativeKind.Proxy]);

            await fixture.Dispatcher.RunAsync(request, CancellationToken.None);

            MediaDerivative row = fixture.ReadRows().Single();

            row.SourceETag.Should().NotBeNullOrEmpty();
            row.SourceETag.Should().StartWith("sha256:",
                "the fallback is a content hash, and it must be distinguishable from a store-reported ETag");
            row.SourceETag.Should().Be(MediaDerivativeDispatcher.ResolveEffectiveEtag(string.Empty, source));

            // The fallback is stable, so a second request for the same bytes is still a cache hit
            // rather than a second row under a second hash.
            fixture.Runner.ResetInvocations();
            await fixture.Dispatcher.RunAsync(request, CancellationToken.None);

            fixture.ReadRows().Should().HaveCount(1);
            fixture.Runner.FfmpegCount.Should().Be(0);
        }
        finally
        {
            MediaDerivativeFfmpegFixture.TryDelete(directory);
        }
    }

    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// T-307 at the writer level: a density the source cannot support must be left out of the run
    /// entirely — no ffmpeg argv for it, no row for it, a <c>Skipped</c> transition rather than a
    /// <c>Failed</c> one, and a terminal stage that is not dragged to <c>Failed</c> by a derivative
    /// that was never runnable.
    /// </summary>
    /// <remarks>
    /// Before the fix this exact run logged a <c>Failed</c> stage and an exception for the 30 s
    /// density on <b>every</b> request, forever, for a pass that could never have produced a frame.
    /// The density that remains unsupported is asserted on the recorded argv, so "not attempted" is
    /// proven rather than inferred from the absence of an error.
    /// </remarks>
    [FfmpegToolsFact]
    public async Task A_density_the_short_source_cannot_support_is_skipped_and_never_reaches_ffmpeg()
    {
        string directory = MediaDerivativeFfmpegFixture.NewDirectory();
        using MediaDerivativeDispatchFixture fixture = MediaDerivativeDispatchFixture.Create(directory);
        try
        {
            // 6 s: density 1 and density 5 produce, density 30 needs >= 15 s and cannot.
            string source = MediaDerivativeFfmpegFixture.CreateSource(
                directory, "short.mp4", seconds: 6, fps: 30, width: 320, height: 240, withAudio: false);
            fixture.ServeAsSourceObject(source);

            Guid projectId = Guid.NewGuid();
            string storageKey = $"projects/{projectId:D}/userFiles/{Guid.NewGuid():N}.mp4";

            await fixture.Dispatcher.RunAsync(
                fixture.Request(projectId, storageKey, "\"etag-short\"", [MediaDerivativeKind.Filmstrip]),
                CancellationToken.None);

            IReadOnlyList<MediaDerivative> rows = fixture.ReadRows();

            rows.Should().HaveCount(2,
                "the 1 s and 5 s densities produce; the 30 s density is arithmetically impossible for a 6 s source");
            rows.Should().OnlyContain(r => r.Kind == MediaDerivativeKind.Filmstrip);
            rows.Select(r => r.ParamsJson).Should().BeEquivalentTo(new[]
            {
                "{\"cols\":10,\"densitySec\":1,\"quality\":5,\"rows\":10,\"thumbWidth\":160,\"videoCodec\":\"mjpeg\"}",
                "{\"cols\":10,\"densitySec\":5,\"quality\":5,\"rows\":10,\"thumbWidth\":160,\"videoCodec\":\"mjpeg\"}"
            }, "the two rows are the 1 s and 5 s densities, and nothing claims the 30 s one");

            fixture.Runner.FfmpegArgvs.Should().NotContain(
                args => ContainsFilter(args, "fps=1/30,scale=160:-2,tile=10x10"),
                "the unsupportable density must never reach ffmpeg — asserted on the recorded argv, not on timing");

            fixture.Sink.Stages.Should().NotContain(MediaDerivativeProgressStage.Failed,
                "a derivative the source cannot produce is a skip, not a failure");

            MediaDerivativeProgressUpdate skipped = fixture.Sink.Updates
                .Last(u => u.Stage == MediaDerivativeProgressStage.Skipped && u.CacheKey is not null);
            skipped.Detail.Should().Contain("unsupported");
            skipped.Detail.Should().Contain("15");

            MediaDerivativeProgressUpdate terminal = fixture.Sink.Updates.Last(u => u.CacheKey is null);
            terminal.Stage.Should().Be(MediaDerivativeProgressStage.Done,
                "the run produced everything this source can support, so it did not fail");
            terminal.Detail.Should().Contain("1 unsupported by this source");

            _output.WriteLine(
                "MEASURED short-source run: {0} rows, {1} ffmpeg invocations, terminal stage {2} ({3})",
                rows.Count, fixture.Runner.FfmpegCount, terminal.Stage, terminal.Detail);
        }
        finally
        {
            MediaDerivativeFfmpegFixture.TryDelete(directory);
        }
    }

    // -----------------------------------------------------------------------------------------

    private static bool ContainsFilter(IReadOnlyList<string> args, string filter) =>
        args.Any(token => string.Equals(token, filter, StringComparison.Ordinal));

    private static bool ContainsInOrder(IReadOnlyList<string> args, string first, string second)
    {
        for (int i = 0; i < args.Count - 1; i++)
        {
            if (string.Equals(args[i], first, StringComparison.Ordinal) &&
                string.Equals(args[i + 1], second, StringComparison.Ordinal))
                return true;
        }

        return false;
    }
}

/// <summary>
/// Counts ffmpeg and ffprobe invocations by method, and records each argv — the whole basis of the
/// "asserted by invocation count" requirement. Wrapping the real runner (rather than mocking it)
/// keeps the priority-carrying overloads genuinely exercised.
/// </summary>
internal sealed class CountingVideoToolRunner : IVideoToolRunner
{
    private readonly IVideoToolRunner _inner;
    private readonly ConcurrentQueue<IReadOnlyList<string>> _ffmpeg = new();
    private readonly ConcurrentQueue<IReadOnlyList<string>> _ffprobe = new();

    public CountingVideoToolRunner(IVideoToolRunner inner) => _inner = inner;

    public Task<VideoToolResult> RunFfmpegAsync(
        IMediaWorkspace workspace, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
    {
        _ffmpeg.Enqueue(args);
        return _inner.RunFfmpegAsync(workspace, args, timeout, ct);
    }

    public Task<VideoToolResult> RunFfmpegAsync(
        IMediaWorkspace workspace, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct,
        Action<string> onStdOutLine)
    {
        _ffmpeg.Enqueue(args);
        return _inner.RunFfmpegAsync(workspace, args, timeout, ct, onStdOutLine);
    }

    public int FfmpegCount => _ffmpeg.Count;

    public int FfprobeCount => _ffprobe.Count;

    public IReadOnlyList<IReadOnlyList<string>> FfmpegArgvs => _ffmpeg.ToArray();

    public IReadOnlyList<IReadOnlyList<string>> FfprobeArgvs => _ffprobe.ToArray();

    public void ResetInvocations()
    {
        _ffmpeg.Clear();
        _ffprobe.Clear();
    }

    public Task<VideoToolResult> RunFfmpegAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct) =>
        RunFfmpegAsync(args, timeout, ct, VideoToolPriority.Normal);

    public Task<VideoToolResult> RunFfmpegAsync(
        IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, Action<string> onStdOutLine) =>
        RunFfmpegAsync(args, timeout, ct, onStdOutLine, VideoToolPriority.Normal);

    public Task<VideoToolResult> RunFfmpegAsync(
        IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, VideoToolPriority priority)
    {
        _ffmpeg.Enqueue(args);
        return _inner.RunFfmpegAsync(args, timeout, ct, priority);
    }

    public Task<VideoToolResult> RunFfmpegAsync(
        IReadOnlyList<string> args,
        TimeSpan timeout,
        CancellationToken ct,
        Action<string> onStdOutLine,
        VideoToolPriority priority)
    {
        _ffmpeg.Enqueue(args);
        return _inner.RunFfmpegAsync(args, timeout, ct, onStdOutLine, priority);
    }

    public Task<VideoToolResult> RunFfprobeAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct) =>
        RunFfprobeAsync(args, timeout, ct, VideoToolPriority.Normal);

    public Task<VideoToolResult> RunFfprobeAsync(
        IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, VideoToolPriority priority)
    {
        _ffprobe.Enqueue(args);
        return _inner.RunFfprobeAsync(args, timeout, ct, priority);
    }
}

/// <summary>
/// A local-directory stand-in for the object store. It builds storage keys through the production
/// <see cref="ProjectFilePath"/> helpers, so the layout this test asserts is the one the real
/// workspace would produce rather than a re-invented approximation.
/// </summary>
internal sealed class LocalObjectStoreWorkspace : IProjectFileWorkspace
{
    private readonly string _objectRoot;
    private readonly ConcurrentQueue<string> _uploadedKeys = new();
    private int _downloadCount;

    public LocalObjectStoreWorkspace(string objectRoot) => _objectRoot = objectRoot;

    /// <summary>The local file every download serves. Set by <c>ServeAsSourceObject</c>.</summary>
    public string? SourceObjectPath { get; set; }

    /// <summary>When set, a download blocks on this task instead of copying — used to prove the handoff is immediate.</summary>
    public Task? DownloadGate { get; set; }

    /// <summary>When set, a download throws it — used to prove every failure path is swallowed.</summary>
    public Exception? DownloadFailure { get; set; }

    public int DownloadCount => Volatile.Read(ref _downloadCount);

    public IReadOnlyList<string> UploadedKeys => _uploadedKeys.ToArray();

    public async Task DownloadStorageKeyToFileAsync(
        Guid projectId, string storageKey, string destinationPath, CancellationToken ct)
    {
        Interlocked.Increment(ref _downloadCount);

        if (DownloadGate is { } gate)
        {
            await gate.WaitAsync(ct);
        }

        if (DownloadFailure is { } failure)
            throw failure;

        if (SourceObjectPath is null)
            throw new InvalidOperationException("No source object was configured for this workspace stub.");

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        File.Copy(SourceObjectPath, destinationPath, overwrite: true);
    }

    /// <summary>The bounded twin (S-67): the source file's own length is known before it is copied.</summary>
    public Task DownloadStorageKeyToFileAsync(Guid projectId, string storageKey, string destinationPath, long maxBytes, CancellationToken ct)
    {
        if (DownloadFailure is null && SourceObjectPath is { } source && new FileInfo(source).Length > maxBytes)
            throw new StorageObjectTooLargeException(maxBytes, new FileInfo(source).Length);
        return DownloadStorageKeyToFileAsync(projectId, storageKey, destinationPath, ct);
    }

    public Task<string> UploadArtifactAsync(
        Guid projectId,
        string localFilePath,
        string fileName,
        string contentType,
        CancellationToken ct,
        string category = "agentFiles")
    {
        string storageKey = ProjectFilePath.BuildStorageKey(
            projectId, category, directoryPath: null, storageFileName: ProjectFilePath.NormalizeRelativePath(fileName));

        string destination = ResolveObjectPath(projectId, storageKey);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(localFilePath, destination, overwrite: true);
        _uploadedKeys.Enqueue(storageKey);

        return Task.FromResult(storageKey);
    }

    public Task<ProjectWorkspaceFile> RegisterStoredObjectAsync(
        Guid projectId, string storageKey, string fileName, string contentType, long sizeBytes,
        SummaryStatus summaryStatus, FileIndexingStatus indexingStatus, CancellationToken ct,
        string category = "outputFiles")
    {
        _ = ResolveObjectPath(projectId, storageKey);
        return Task.FromResult(new ProjectWorkspaceFile(
            Guid.NewGuid(), projectId, fileName, fileName, category, storageKey, contentType,
            sizeBytes, DateTime.UtcNow, null));
    }

    /// <summary>
    /// Answers "is the bare artifact already there?" by consulting THIS double's own object store —
    /// the local directory <see cref="_objectRoot"/>, exactly the one <see cref="UploadArtifactAsync"/>
    /// writes into. It is deliberately not a constant: a double that always answered <c>true</c> would
    /// let a cache-hit test pass because the stub said so rather than because a previous upload really
    /// landed, which is the one thing such a test must not be able to do.
    ///
    /// <para>
    /// The two semantics that matter are mirrored from
    /// <see cref="ProjectFileWorkspace.ArtifactExistsAsync"/> rather than invented: a key outside
    /// <c>projects/{projectId}/</c> throws <see cref="UnauthorizedAccessException"/> instead of
    /// answering, and "genuinely absent" is <c>false</c> while any other failure propagates —
    /// <c>File.Exists</c> yields exactly that split (it returns <c>false</c> for a missing file and
    /// does not swallow an unreadable path). Like the real HEAD request, this never reads the
    /// object's bytes.
    /// </para>
    /// </summary>
    public Task<bool> ArtifactExistsAsync(Guid projectId, string storageKey, CancellationToken ct)
    {
        string path = ResolveObjectPath(projectId, storageKey);
        return Task.FromResult(File.Exists(path));
    }

    /// <summary>
    /// Maps a storage key onto this store's directory, applying the same project-scope check the
    /// production workspace applies, so a key and its existence answer cannot disagree about where
    /// the object lives.
    /// </summary>
    private string ResolveObjectPath(Guid projectId, string storageKey)
    {
        if (!storageKey.StartsWith($"projects/{projectId}/", StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Requested file is not in the project's storage scope.");

        return Path.Combine(_objectRoot, storageKey.Replace('/', Path.DirectorySeparatorChar));
    }

    public Task<IReadOnlyList<ProjectWorkspaceFile>> ListFilesAsync(Guid projectId, CancellationToken ct) =>
        throw new NotSupportedException("Not part of the derivative write path.");

    public Task<string> ReadFileAsync(Guid projectId, string fileReference, CancellationToken ct) =>
        throw new NotSupportedException("Not part of the derivative write path.");

    public Task<ProjectWorkspaceFile> WriteTextFileAsync(
        Guid projectId,
        string fileName,
        string content,
        string contentType,
        CancellationToken ct,
        string category = "agentFiles",
        string? originalPath = null) =>
        throw new NotSupportedException("Derivatives are bare artifacts; they never create project files.");

    public Task<ProjectWorkspaceFile> UploadBinaryFileAsync(
        Guid projectId,
        string localFilePath,
        string fileName,
        string contentType,
        SummaryStatus summaryStatus,
        FileIndexingStatus indexingStatus,
        CancellationToken ct,
        string category = "outputFiles",
        string? originalPath = null) =>
        throw new NotSupportedException(
            "UploadArtifactAsync is the correct call here: a derivative is index-only and must not create a ProjectFile row.");
}

/// <summary>Records every progress transition, so a test can assert what the seam actually reported.</summary>
internal sealed class RecordingProgressSink : IMediaDerivativeProgressSink
{
    private readonly ConcurrentQueue<MediaDerivativeProgressUpdate> _updates = new();

    public IReadOnlyList<MediaDerivativeProgressUpdate> Updates => _updates.ToArray();

    public IReadOnlyList<MediaDerivativeProgressStage> Stages => _updates.Select(u => u.Stage).ToArray();

    public void Report(MediaDerivativeProgressUpdate update) => _updates.Enqueue(update);
}

/// <summary>
/// Wires the real dispatcher exactly the way the DI container does — real service, real tool runner,
/// real probe — over an InMemory database, a local object store and a recording sink.
/// </summary>
internal sealed class MediaDerivativeDispatchFixture : IDisposable
{
    private MediaDerivativeDispatchFixture(
        string scratchRoot,
        SharedInMemoryDbFactory db,
        CountingVideoToolRunner runner,
        LocalObjectStoreWorkspace workspace,
        MediaDerivativeService service,
        MediaDerivativeDispatcher dispatcher,
        RecordingProgressSink sink)
    {
        ScratchRoot = scratchRoot;
        Db = db;
        Runner = runner;
        Workspace = workspace;
        Service = service;
        Dispatcher = dispatcher;
        Sink = sink;
    }

    public string ScratchRoot { get; }

    public SharedInMemoryDbFactory Db { get; }

    public CountingVideoToolRunner Runner { get; }

    public LocalObjectStoreWorkspace Workspace { get; }

    public MediaDerivativeService Service { get; }

    public MediaDerivativeDispatcher Dispatcher { get; }

    public RecordingProgressSink Sink { get; }

    public static MediaDerivativeDispatchFixture Create(string root) => Create(root, videoEditing: null);

    public static MediaDerivativeDispatchFixture Create(string root, VideoEditingOptions? videoEditing) =>
        Create(root, videoEditing, warmUp: null);

    /// <summary>
    /// The same, with a pass cache warm-up built over the fixture's own tool runner and options and handed to the
    /// dispatcher as its optional dependency (null = none, the dispatcher as it was).
    /// </summary>
    public static MediaDerivativeDispatchFixture Create(
        string root, VideoEditingOptions? videoEditing,
        Func<IVideoToolRunner, VideoEditingOptions, ReelBolt.WorkflowEngine.Services.Video.Caching.SourcePassWarmUp>? warmUp,
        IScratchDiskGuard? scratchGuard = null,
        ReelBolt.WorkflowEngine.Drain.EngineDrainTarget? drain = null)
    {
        string scratchRoot = Path.Combine(root, "scratch");
        string objectRoot = Path.Combine(root, "objects");
        Directory.CreateDirectory(scratchRoot);
        Directory.CreateDirectory(objectRoot);

        VideoEditingOptions options = videoEditing ?? MediaDerivativeFfmpegFixture.BuildOptions(scratchRoot);

        var runner = new CountingVideoToolRunner(MediaDerivativeFfmpegFixture.BuildRunner(options));
        var probe = new FfprobeMediaProbe(runner, Options.Create(options), NullLogger<FfprobeMediaProbe>.Instance);

        var db = new SharedInMemoryDbFactory();
        var workspace = new LocalObjectStoreWorkspace(objectRoot);
        var sink = new RecordingProgressSink();

        var service = new MediaDerivativeService(
            runner, probe, Options.Create(options), NullLogger<MediaDerivativeService>.Instance);

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["MinIO:BucketName"] = "reelforge-test" })
            .Build();

        var dispatcher = new MediaDerivativeDispatcher(
            db,
            workspace,
            service,
            runner,
            sink,
            Options.Create(options),
            configuration,
            NullLogger<MediaDerivativeDispatcher>.Instance,
            scratchGuard: scratchGuard,
            warmUp: warmUp?.Invoke(runner, options),
            drain: drain);

        return new MediaDerivativeDispatchFixture(scratchRoot, db, runner, workspace, service, dispatcher, sink);
    }

    /// <summary>Makes every download serve <paramref name="localPath"/> as the source object.</summary>
    public void ServeAsSourceObject(string localPath) => Workspace.SourceObjectPath = localPath;

    public MediaDerivativesRequested Request(
        Guid projectId,
        string sourceStorageKey,
        string sourceEtag,
        IReadOnlyList<MediaDerivativeKind> kinds,
        Guid? sourceProjectFileId = null,
        string mimeType = "video/mp4")
    {
        Guid fileId = sourceProjectFileId ?? Guid.NewGuid();

        return new MediaDerivativesRequested(
            projectId,
            new MediaSourceRef(MediaSourceKind.ProjectFile, fileId),
            fileId,
            sourceStorageKey,
            sourceEtag,
            mimeType,
            kinds,
            "upload",
            DateTime.UtcNow);
    }

    public IReadOnlyList<MediaDerivative> ReadRows()
    {
        using WorkflowEngineDbContext db = Db.CreateDbContext();
        return db.MediaDerivatives.AsNoTracking().ToList();
    }

    /// <summary>
    /// Stores the <c>project_files</c> row <paramref name="request"/> names, as an upload would have: the consumer
    /// (and the dispatcher, before it replays a request it kept) only works for a file that still has it.
    /// </summary>
    public void AddSourceFile(MediaDerivativesRequested request)
    {
        using WorkflowEngineDbContext db = Db.CreateDbContext();
        db.ProjectFiles.Add(new ProjectFile
        {
            Id = request.SourceProjectFileId!.Value,
            ProjectId = request.ProjectId,
            OriginalFileName = "source.mp4",
            StorageKey = request.SourceStorageKey,
            StorageBucket = "reelforge-test",
            Category = "userFiles",
            MimeType = request.MimeType ?? "video/mp4",
            SizeBytes = 1024,
            UploadedAt = DateTime.UtcNow,
            CsamStatus = CsamScanStatus.Clean
        });
        db.SaveChanges();
    }

    /// <summary>Changes the stored row of <paramref name="fileId"/> (a move changes its storage key), or removes it when <paramref name="change"/> is null.</summary>
    public void ChangeSourceFile(Guid fileId, Action<ProjectFile>? change)
    {
        using WorkflowEngineDbContext db = Db.CreateDbContext();
        ProjectFile row = db.ProjectFiles.Single(f => f.Id == fileId);
        if (change is null)
            db.ProjectFiles.Remove(row);
        else
            change(row);
        db.SaveChanges();
    }

    public void Dispose()
    {
        Dispatcher.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        MediaDerivativeFfmpegFixture.TryDelete(ScratchRoot);
    }
}
