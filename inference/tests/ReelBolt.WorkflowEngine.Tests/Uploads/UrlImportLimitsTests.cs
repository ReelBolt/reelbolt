#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Uploads;
using ReelBolt.Shared.Billing;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Uploads;

/// <summary>
/// S-06: URL imports are bounded per organization (<c>FileImport:MaxConcurrentPerOrg</c>, 2) and
/// globally (<c>FileImport:MaxConcurrent</c>, 4) with an <c>IMPORT_BUSY</c> refusal; each import's
/// stream cap is min(MaxBytes, the quota headroom at its start), and that headroom stays reserved
/// while it runs, so parallel imports cannot overshoot the quota together. The spool directory is
/// configurable and its free space is checked first.
/// </summary>
public class UrlImportLimitsTests
{
    private const string Url = "https://cdn.example.com/a.mp4";
    private static readonly byte[] Clip = Enumerable.Range(0, 3000).Select(i => (byte)(i % 251)).ToArray();

    /// <summary>A quota of <paramref name="limit"/> bytes with <paramref name="used"/> used: the real rule, not a queue.</summary>
    private sealed class HeadroomPlanGate(long limit, long used) : IPlanGateService
    {
        public long Used { get; set; } = used;

        public List<long> UploadChecks { get; } = new();

        public Task<PlanGateViolation?> CheckUploadAsync(Guid organizationId, long incomingBytes, CancellationToken ct)
        {
            lock (UploadChecks) UploadChecks.Add(incomingBytes);
            return Task.FromResult(Used + incomingBytes > limit
                ? new PlanGateViolation(PlanGate.StorageCode, "storage", "over quota", 413, limit, Used)
                : null);
        }

        public Task<PlanGateViolation?> CheckStepsAsync(Guid organizationId, IEnumerable<ReelBolt.Inference.Api.Controllers.Dto.CreateWorkflowStepRequest> steps, CancellationToken ct) =>
            Task.FromResult<PlanGateViolation?>(null);

        public Task<PlanGateViolation?> CheckExecuteAsync(Guid organizationId, CancellationToken ct) => Task.FromResult<PlanGateViolation?>(null);

        public Task<PlanGateViolation?> CheckResolutionAsync(Guid organizationId, int? chosenShortSide, CancellationToken ct) =>
            Task.FromResult<PlanGateViolation?>(null);
    }

    /// <summary>Serves <paramref name="url"/> only after <paramref name="release"/> completes, signalling <paramref name="entered"/> on arrival.</summary>
    private static void ServeHeld(UrlImportHarness h, string url, byte[] body, SemaphoreSlim entered, Task release) =>
        h.Upstream.Routes[url] = async (_, ct) =>
        {
            entered.Release();
            await release.WaitAsync(ct);
            HttpResponseMessage response = new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
            response.Content.Headers.TryAddWithoutValidation("Content-Type", "video/mp4");
            return response;
        };

    [Fact]
    public async Task A_third_concurrent_import_in_one_organization_is_refused_busy_without_touching_the_network()
    {
        UrlImportLimiter limiter = new();
        Guid org = Guid.NewGuid();
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        SemaphoreSlim entered = new(0);
        UrlImportHarness a = new(limiter: limiter, organizationId: org);
        UrlImportHarness b = new(limiter: limiter, organizationId: org);
        UrlImportHarness c = new(limiter: limiter, organizationId: org);
        ServeHeld(a, Url, Clip, entered, release.Task);
        ServeHeld(b, Url, Clip, entered, release.Task);
        c.Upstream.Serve(Url, Clip);

        Task<UrlImportResult> first = a.Import(Url);
        Task<UrlImportResult> second = b.Import(Url);
        (await entered.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeTrue();
        (await entered.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeTrue();

        UrlImportResult third = await c.Import(Url);

        third.Refusal!.Code.Should().Be(UrlImportCodes.Busy);
        third.Refusal.StatusCode.Should().Be(429);
        third.Refusal.Message.Should().Contain("try again");
        c.Upstream.Requests.Should().BeEmpty();
        c.NothingStored.Should().BeTrue();

        release.SetResult();
        (await first).File.Should().NotBeNull();
        (await second).File.Should().NotBeNull();

        // The slots are returned: the next import in the organization runs.
        (await c.Import(Url)).File.Should().NotBeNull();
    }

    [Fact]
    public async Task A_fifth_concurrent_import_across_organizations_is_refused_busy()
    {
        UrlImportLimiter limiter = new();
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        SemaphoreSlim entered = new(0);
        UrlImportHarness[] held = Enumerable.Range(0, 4).Select(_ => new UrlImportHarness(limiter: limiter)).ToArray();
        foreach (UrlImportHarness h in held) ServeHeld(h, Url, Clip, entered, release.Task);
        UrlImportHarness fifth = new(limiter: limiter);
        fifth.Upstream.Serve(Url, Clip);

        Task<UrlImportResult>[] running = held.Select(h => h.Import(Url)).ToArray();
        for (int i = 0; i < held.Length; i++)
            (await entered.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeTrue();

        UrlImportResult refused = await fifth.Import(Url);

        refused.Refusal!.Code.Should().Be(UrlImportCodes.Busy);
        fifth.Upstream.Requests.Should().BeEmpty();
        release.SetResult();
        foreach (Task<UrlImportResult> r in running) (await r).File.Should().NotBeNull();
    }

    [Fact]
    public async Task The_concurrency_limits_are_configurable()
    {
        UrlImportLimiter limiter = new();
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        SemaphoreSlim entered = new(0);
        Guid org = Guid.NewGuid();
        UrlImportHarness a = new(limiter: limiter, organizationId: org, configure: o => o.MaxConcurrentPerOrg = 1);
        UrlImportHarness b = new(limiter: limiter, organizationId: org, configure: o => o.MaxConcurrentPerOrg = 1);
        ServeHeld(a, Url, Clip, entered, release.Task);
        b.Upstream.Serve(Url, Clip);

        Task<UrlImportResult> first = a.Import(Url);
        (await entered.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeTrue();

        (await b.Import(Url)).Refusal!.Code.Should().Be(UrlImportCodes.Busy);
        release.SetResult();
        (await first).File.Should().NotBeNull();
    }

    [Fact]
    public void The_defaults_are_two_per_organization_and_four_in_all()
    {
        UrlImportOptions defaults = new();

        defaults.MaxConcurrentPerOrg.Should().Be(2);
        defaults.MaxConcurrent.Should().Be(4);
        defaults.SpoolDirectory.Should().BeNull("the system temp directory unless configured");
    }

    [Fact]
    public async Task The_stream_is_capped_at_the_quota_headroom_when_that_is_below_the_import_cap()
    {
        // 1 MiB import cap, but only 1000 bytes left on the plan; the body declares no length.
        HeadroomPlanGate gate = new(limit: 10_000, used: 9_000);
        UrlImportHarness h = new(maxBytes: 1024 * 1024, planGate: gate);
        WatchedStream body = new(Clip);
        h.Upstream.Serve(Url, Clip, declaredLength: null, stream: body);

        UrlImportResult result = await h.Import(Url);

        result.Refusal!.Code.Should().Be(PlanGate.StorageCode, "running out of quota is a quota refusal, not 'too large'");
        result.Refusal.LimitBytes.Should().Be(10_000);
        h.NothingStored.Should().BeTrue();
    }

    [Fact]
    public async Task A_declared_length_above_the_quota_headroom_is_refused_before_the_body_is_read()
    {
        HeadroomPlanGate gate = new(limit: 10_000, used: 9_000);
        UrlImportHarness h = new(planGate: gate);
        WatchedStream body = new(Clip);
        h.Upstream.Serve(Url, Clip, stream: body);

        UrlImportResult result = await h.Import(Url);

        result.Refusal!.Code.Should().Be(PlanGate.StorageCode);
        body.WasRead.Should().BeFalse();
        h.NothingStored.Should().BeTrue();
    }

    [Fact]
    public async Task No_headroom_left_is_refused_before_the_network_is_touched()
    {
        HeadroomPlanGate gate = new(limit: 10_000, used: 10_000);
        UrlImportHarness h = new(planGate: gate);
        h.Upstream.Serve(Url, Clip);

        UrlImportResult result = await h.Import(Url);

        result.Refusal!.Code.Should().Be(PlanGate.StorageCode);
        h.Upstream.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_running_import_reserves_its_headroom_so_a_parallel_one_in_the_organization_cannot_overshoot()
    {
        // 4000 bytes of headroom. The first import (3000-byte body, no declared length) reserves all of it
        // while it runs; the second, started meanwhile, has nothing left and is refused up front.
        UrlImportLimiter limiter = new();
        Guid org = Guid.NewGuid();
        HeadroomPlanGate gate = new(limit: 4_000, used: 0);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        SemaphoreSlim entered = new(0);
        UrlImportHarness a = new(limiter: limiter, organizationId: org, planGate: gate);
        UrlImportHarness b = new(limiter: limiter, organizationId: org, planGate: gate);
        ServeHeld(a, Url, Clip, entered, release.Task);
        b.Upstream.Serve(Url, Clip, declaredLength: null);

        Task<UrlImportResult> first = a.Import(Url);
        (await entered.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeTrue();

        UrlImportResult second = await b.Import(Url);

        second.Refusal!.Code.Should().Be(PlanGate.StorageCode);
        b.Upstream.Requests.Should().BeEmpty();
        b.NothingStored.Should().BeTrue();
        release.SetResult();
        (await first).File!.SizeBytes.Should().Be(Clip.LongLength);
        limiter.ReservedBytes(org).Should().Be(0, "a finished import returns its reservation");
    }

    /// <summary>QA-P0-27(a): an import whose stream cap is the whole <c>MaxBytes</c> ceiling reserves only a bounded
    /// quota floor while it runs, so a second import in the organization with less headroom than the ceiling still
    /// fetches. Before the fix the first import reserved its full 40 MiB cap and the second was refused up front.</summary>
    [Fact]
    public async Task A_running_import_reserves_only_a_bounded_floor_so_a_parallel_one_with_less_than_the_ceiling_headroom_still_runs()
    {
        const long MiB = 1024 * 1024;
        UrlImportLimiter limiter = new();
        Guid org = Guid.NewGuid();
        HeadroomPlanGate gate = new(limit: 40 * MiB, used: 0);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        SemaphoreSlim entered = new(0);
        UrlImportHarness a = new(maxBytes: 64 * MiB, limiter: limiter, organizationId: org, planGate: gate);
        UrlImportHarness b = new(maxBytes: 64 * MiB, limiter: limiter, organizationId: org, planGate: gate);
        ServeHeld(a, Url, Clip, entered, release.Task);
        b.Upstream.Serve(Url, Clip, declaredLength: null);

        Task<UrlImportResult> first = a.Import(Url);
        (await entered.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeTrue();

        UrlImportResult second = await b.Import(Url);

        second.Refusal.Should().BeNull("the first import holds only a bounded floor of the quota, not its 64 MiB ceiling");
        second.File.Should().NotBeNull();
        release.SetResult();
        (await first).File!.SizeBytes.Should().Be(Clip.LongLength);
        limiter.ReservedBytes(org).Should().Be(0, "a finished import returns its reservation");
    }

    [Fact]
    public async Task The_body_is_spooled_in_the_configured_directory()
    {
        string spool = Directory.CreateTempSubdirectory("reelbolt-import-spool-").FullName;
        try
        {
            UrlImportHarness h = new(configure: o => o.SpoolDirectory = spool);
            string[]? seen = null;
            ProbeStream body = new(Clip, () => seen ??= Directory.GetFiles(spool));
            h.Upstream.Serve(Url, Clip, stream: body);

            UrlImportResult result = await h.Import(Url);

            result.File.Should().NotBeNull();
            seen.Should().ContainSingle("the spool file sits in the configured directory while the body streams");
            Directory.GetFiles(spool).Should().BeEmpty("the spool is deleted on close");
        }
        finally
        {
            Directory.Delete(spool, recursive: true);
        }
    }

    [Fact]
    public async Task A_spool_directory_without_enough_free_space_is_refused_busy_before_the_network_is_touched()
    {
        UrlImportHarness h = new(configure: o => o.SpoolMinFreeBytes = long.MaxValue / 4);
        h.Upstream.Serve(Url, Clip);

        UrlImportResult result = await h.Import(Url);

        result.Refusal!.Code.Should().Be(UrlImportCodes.Busy);
        h.Upstream.Requests.Should().BeEmpty();
    }

    /// <summary>S-32: one organization holds at most half the process's import slots, whatever its per-org cap.</summary>
    [Fact]
    public void One_organization_holds_at_most_half_the_global_slots()
    {
        UrlImportLimiter limiter = new();
        Guid a = Guid.NewGuid();
        Guid b = Guid.NewGuid();

        using UrlImportLimiter.Lease? first = limiter.TryEnter(a, maxConcurrent: 2, maxPerOrg: 2);
        using UrlImportLimiter.Lease? second = limiter.TryEnter(a, maxConcurrent: 2, maxPerOrg: 2);
        using UrlImportLimiter.Lease? other = limiter.TryEnter(b, maxConcurrent: 2, maxPerOrg: 2);

        first.Should().NotBeNull();
        second.Should().BeNull("one organization may not take every slot of the process");
        other.Should().NotBeNull("the other half stays open to other tenants");
    }

    [Fact]
    public void The_default_stall_timeout_is_30_seconds_well_inside_the_whole_import_deadline()
    {
        UrlImportOptions defaults = new();

        defaults.StallTimeoutSeconds.Should().Be(30);
        defaults.StallTimeoutSeconds.Should().BeLessThan(defaults.TimeoutSeconds);
    }

    /// <summary>S-32: a body that sends some bytes and then nothing is refused IMPORT_TIMEOUT after the stall timeout,
    /// not after the 300 s whole-import deadline, and its slot is returned.</summary>
    [Fact]
    public async Task A_body_that_stops_sending_is_IMPORT_TIMEOUT_after_the_stall_timeout_and_frees_its_slot()
    {
        UrlImportHarness h = new(timeoutSeconds: 300, configure: o => o.StallTimeoutSeconds = 0.3);
        h.Upstream.Serve(Url, Clip, stream: new StallingStream(Clip[..100]), declaredLength: null);

        UrlImportResult result = await h.Import(Url).WaitAsync(TimeSpan.FromSeconds(20));

        result.Refusal!.Code.Should().Be(UrlImportCodes.Timeout);
        result.Refusal.StatusCode.Should().Be(422);
        h.NothingStored.Should().BeTrue();
        using UrlImportLimiter.Lease? again = h.Limiter.TryEnter(h.Project.OrganizationId, 2, 1);
        again.Should().NotBeNull("the stalled import gave its slot back");
    }

    [Fact]
    public async Task An_upstream_that_never_answers_is_IMPORT_TIMEOUT_after_the_stall_timeout()
    {
        UrlImportHarness h = new(timeoutSeconds: 300, configure: o => o.StallTimeoutSeconds = 0.3);
        h.Upstream.Routes[Url] = async (_, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        };

        UrlImportResult result = await h.Import(Url).WaitAsync(TimeSpan.FromSeconds(20));

        result.Refusal!.Code.Should().Be(UrlImportCodes.Timeout);
        h.NothingStored.Should().BeTrue();
    }

    [Fact]
    public void The_default_throughput_floor_is_32_KiB_per_second_after_a_30_second_grace()
    {
        UrlImportOptions defaults = new();

        defaults.MinBytesPerSecond.Should().Be(32768);
        defaults.ThroughputGraceSeconds.Should().Be(30);
        defaults.ThroughputGraceSeconds.Should().BeLessThan(defaults.TimeoutSeconds);
    }

    /// <summary>S-41: a body trickled one byte at a time re-arms the stall timer forever; once the grace period is over
    /// its average throughput is under the floor, so it is refused IMPORT_TIMEOUT long before the 300 s deadline and
    /// its slot is returned.</summary>
    [Fact]
    public async Task A_byte_trickle_is_IMPORT_TIMEOUT_once_its_average_throughput_is_under_the_floor_and_frees_its_slot()
    {
        UrlImportHarness h = new(timeoutSeconds: 300, configure: o =>
        {
            o.StallTimeoutSeconds = 30;
            o.ThroughputGraceSeconds = 0.3;
            o.MinBytesPerSecond = 32768;
        });
        h.Upstream.Serve(Url, Clip, stream: new TricklingStream(TimeSpan.FromMilliseconds(10)), declaredLength: null);

        UrlImportResult result = await h.Import(Url).WaitAsync(TimeSpan.FromSeconds(20));

        result.Refusal!.Code.Should().Be(UrlImportCodes.Timeout);
        result.Refusal.StatusCode.Should().Be(422);
        result.Refusal.Message.Should().Contain("too slowly");
        h.NothingStored.Should().BeTrue();
        using UrlImportLimiter.Lease? again = h.Limiter.TryEnter(h.Project.OrganizationId, 2, 1);
        again.Should().NotBeNull("the trickled import gave its slot back");
    }

    /// <summary>One byte every <paramref name="interval"/>, forever (until cancelled): never stalls, never finishes.</summary>
    private sealed class TricklingStream(TimeSpan interval) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("async only");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(interval, cancellationToken);
            buffer.Span[0] = 0x2A;
            return 1;
        }
    }

    /// <summary>Serves <paramref name="head"/>, then never another byte (until cancelled).</summary>
    private sealed class StallingStream(byte[] head) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("async only");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position < head.Length)
            {
                int n = Math.Min(buffer.Length, head.Length - _position);
                head.AsMemory(_position, n).CopyTo(buffer);
                _position += n;
                return n;
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }

    /// <summary>A body that runs <paramref name="onRead"/> on its first read.</summary>
    private sealed class ProbeStream(byte[] bytes, Action onRead) : MemoryStream(bytes)
    {
        public override int Read(Span<byte> buffer)
        {
            onRead();
            return base.Read(buffer);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            onRead();
            return base.Read(buffer, offset, count);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            onRead();
            return base.ReadAsync(buffer, cancellationToken);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            onRead();
            return base.ReadAsync(buffer, offset, count, cancellationToken);
        }
    }
}
