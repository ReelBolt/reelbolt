using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using ReelBolt.Inference.Api.Services.Media;
using ReelBolt.WorkflowEngine.Services.Video;

namespace ReelBolt.WorkflowEngine.Tests;

public class DerivativeSourceGateTests
{
    // Simulates a producer taking one tick per source: each started run is recorded, then released by hand.
    private static async Task<List<string>> DrainAsync(DerivativeSourceGate gate, List<(string Key, bool Interactive)> arrivals)
    {
        var started = new List<string>();
        var tasks = new List<Task>();
        foreach ((string key, bool interactive) in arrivals)
        {
            string k = key;
            Task t = gate.WaitAsync(k, interactive, CancellationToken.None);
            tasks.Add(t.ContinueWith(_ => started.Add(k), TaskScheduler.Default));
        }

        for (int i = 0; i < arrivals.Count; i++)
        {
            await Task.Delay(20);
            gate.Release();
        }

        await Task.WhenAll(tasks);
        return started;
    }

    [Fact]
    public async Task Interactive_queued_after_sweeps_starts_next_not_last()
    {
        var gate = new DerivativeSourceGate(1);
        await gate.WaitAsync("running", false, CancellationToken.None);
        var arrivals = new List<(string, bool)>();
        for (int i = 0; i < 5; i++) arrivals.Add(($"sweep{i}", false));
        arrivals.Add(("upload", true));

        List<string> order = await DrainAsync(gate, arrivals);

        // Before: position 5 (K*T wait). After: position 0 (<= T, the running source only).
        Assert.Equal("upload", order[0]);
        Assert.Equal(new[] { "sweep0", "sweep1", "sweep2", "sweep3", "sweep4" }, order.Skip(1));
    }

    [Fact]
    public async Task Fifo_within_each_class()
    {
        var gate = new DerivativeSourceGate(1);
        await gate.WaitAsync("running", false, CancellationToken.None);
        var arrivals = new List<(string, bool)> { ("s1", false), ("i1", true), ("s2", false), ("i2", true) };

        List<string> order = await DrainAsync(gate, arrivals);

        Assert.Equal(new[] { "i1", "i2", "s1", "s2" }, order);
    }

    [Fact]
    public async Task Promote_moves_queued_sweep_to_interactive_class()
    {
        var gate = new DerivativeSourceGate(1);
        await gate.WaitAsync("running", false, CancellationToken.None);
        var arrivals = new List<(string, bool)> { ("s1", false), ("s2", false), ("s3", false) };
        var started = new List<string>();
        var tasks = arrivals.Select(a => gate.WaitAsync(a.Item1, false, CancellationToken.None)
            .ContinueWith(_ => { lock (started) started.Add(a.Item1); }, TaskScheduler.Default)).ToList();

        Assert.True(gate.Promote("s3"));
        Assert.False(gate.Promote("missing"));
        for (int i = 0; i < 3; i++)
        {
            await Task.Delay(20);
            gate.Release();
        }

        await Task.WhenAll(tasks);
        Assert.Equal(new[] { "s3", "s1", "s2" }, started);
    }

    [Fact]
    public async Task Cancelled_waiter_does_not_swallow_the_slot()
    {
        var gate = new DerivativeSourceGate(1);
        await gate.WaitAsync("running", false, CancellationToken.None);
        using var cts = new CancellationTokenSource();
        Task cancelled = gate.WaitAsync("a", true, cts.Token);
        Task live = gate.WaitAsync("b", false, CancellationToken.None);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        gate.Release();
        await live.WaitAsync(TimeSpan.FromSeconds(2));
    }

    // Virtual clock: a producer takes T per source; the start time of a waiter is T times the number of releases it needed.
    private static async Task<int> StartLatencyAsync(bool classAware, int sweepsQueued, int tMs)
    {
        var gate = new DerivativeSourceGate(1);
        await gate.WaitAsync("running", false, CancellationToken.None);
        for (int i = 0; i < sweepsQueued; i++) _ = gate.WaitAsync($"sweep{i}", false, CancellationToken.None);
        Task upload = gate.WaitAsync("upload", classAware, CancellationToken.None);
        int releases = 0;
        while (!upload.IsCompleted)
        {
            gate.Release();
            releases++;
        }

        return releases * tMs;
    }

    [Fact]
    public async Task Interactive_start_latency_is_one_source_not_k_sources()
    {
        const int K = 20;
        const int T = 1000;
        int before = await StartLatencyAsync(classAware: false, K, T);
        int after = await StartLatencyAsync(classAware: true, K, T);

        Assert.Equal((K + 1) * T, before);
        Assert.True(after <= T, $"interactive waited {after} ms, expected <= {T}");
    }

    [Fact]
    public async Task Running_sweep_is_not_preempted_and_slot_is_not_leaked_by_cancel()
    {
        var gate = new DerivativeSourceGate(1);
        await gate.WaitAsync("sweepRunning", false, CancellationToken.None);
        using var cts = new CancellationTokenSource();
        Task cancelled = gate.WaitAsync("x", true, cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Task next = gate.WaitAsync("y", true, CancellationToken.None);
        Assert.False(next.IsCompleted);
        gate.Release();
        Assert.True(next.IsCompleted);
        gate.Release();
        Task free = gate.WaitAsync("z", false, CancellationToken.None);
        Assert.True(free.IsCompleted);
    }

    [Fact]
    public void Ts_filter_leaves_normal_ts_video_and_other_names_alone()
    {
        Assert.False(MediaDerivativeCoverageService.IsNonMediaTransportStreamName("video/mp2t", "p/clip.ts", 64 * 1024));
        Assert.False(MediaDerivativeCoverageService.IsNonMediaTransportStreamName("video/mp2t", "p/clip.ts", 5_000_000));
        Assert.False(MediaDerivativeCoverageService.IsNonMediaTransportStreamName("video/mp2t", "p/clip.mp4", 100));
    }

    [Theory]
    [InlineData("video/vnd.dlna.mpeg-tts", "p/a.ts", 5000L, true)]
    [InlineData("video/mp2t", "p/A.TS", 5000L, true)]
    [InlineData("video/mp2t", "p/a.ts", 70000L, false)]
    [InlineData("video/mp2t", "p/a.ts", null, false)]
    [InlineData("video/mp4", "p/a.ts", 5000L, false)]
    [InlineData("video/mp2t", "p/a.mts", 5000L, false)]
    public void NonMedia_ts_filter_is_narrow(string mime, string key, long? size, bool expected) =>
        Assert.Equal(expected, MediaDerivativeCoverageService.IsNonMediaTransportStreamName(mime, key, size));
}
