using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using MassTransit;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Microsoft.Extensions.Options;
using ReelBolt.Shared.Drain;
using ReelBolt.WorkflowEngine.Consumers;
using ReelBolt.WorkflowEngine.Drain;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Drain;

public class DrainMonitorTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("zdd").FullName;
    private readonly FakeTime _time = new();

    public void Dispose() => Directory.Delete(_dir, true);

    private sealed class FakeTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Source(string name) : IDrainWorkSource
    {
        public string Name => name;

        public int ActiveCount { get; set; }
    }

    private sealed class Target : IDrainTarget
    {
        public int Begun;
        public bool Stopped { get; set; }
        public Task BeginDrainAsync(CancellationToken cancellationToken) { Begun++; return Task.CompletedTask; }

        public bool IsFullyStopped => Stopped;
    }

    private DrainMonitor Make(Source[] sources, Target[] targets, string? statusPath = null) => new(
        Options.Create(new DrainOptions { SignalPath = Path.Combine(_dir, "drain"), StatusPath = statusPath ?? Path.Combine(_dir, "status.json") }),
        sources, targets, _time, NullLogger<DrainMonitor>.Instance);

    private JsonElement Status() => JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, "status.json"))).RootElement;

    [Fact]
    public async Task Without_the_signal_it_reports_active_and_touches_no_target()
    {
        var t = new Target();
        var m = Make([new Source("runs")], [t]);
        await m.TickAsync(CancellationToken.None);
        m.IsDraining.Should().BeFalse();
        t.Begun.Should().Be(0);
        Status().GetProperty("state").GetString().Should().Be("active");
    }

    [Fact]
    public async Task Signal_moves_active_to_draining_to_drained_and_is_irreversible()
    {
        var src = new Source("runs") { ActiveCount = 2 };
        var t = new Target();
        var m = Make([src], [t]);
        await m.TickAsync(CancellationToken.None);

        File.WriteAllText(Path.Combine(_dir, "drain"), "");
        _time.Now += TimeSpan.FromSeconds(6);
        await m.TickAsync(CancellationToken.None);
        Status().GetProperty("state").GetString().Should().Be("draining");
        Status().GetProperty("activeWork").GetInt32().Should().Be(2);
        Status().GetProperty("detail").GetProperty("runs").GetInt32().Should().Be(2);

        src.ActiveCount = 0; // work done but endpoints not stopped yet
        _time.Now += TimeSpan.FromSeconds(6);
        await m.TickAsync(CancellationToken.None);
        Status().GetProperty("state").GetString().Should().Be("draining");

        t.Stopped = true;
        _time.Now += TimeSpan.FromSeconds(6);
        await m.TickAsync(CancellationToken.None);
        Status().GetProperty("state").GetString().Should().Be("drained");

        File.Delete(Path.Combine(_dir, "drain"));
        await m.TickAsync(CancellationToken.None);
        m.IsDraining.Should().BeTrue("drain is never reversible");
        t.Begun.Should().Be(1);
    }

    [Fact]
    public async Task Status_is_throttled_to_one_write_per_five_seconds()
    {
        var m = Make([new Source("runs")], []);
        await m.TickAsync(CancellationToken.None);
        string first = File.ReadAllText(Path.Combine(_dir, "status.json"));
        _time.Now += TimeSpan.FromSeconds(2);
        await m.TickAsync(CancellationToken.None);
        File.ReadAllText(Path.Combine(_dir, "status.json")).Should().Be(first);
        _time.Now += TimeSpan.FromSeconds(4);
        await m.TickAsync(CancellationToken.None);
        File.ReadAllText(Path.Combine(_dir, "status.json")).Should().NotBe(first);
        File.Exists(Path.Combine(_dir, "status.json.tmp")).Should().BeFalse("replaced atomically via rename");
    }

    [Fact]
    public async Task A_status_write_failure_never_throws()
    {
        var m = Make([new Source("runs")], [], Path.Combine(_dir, "no-such-dir", "status.json"));
        Func<Task> act = () => m.TickAsync(CancellationToken.None);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public void Analysis_in_flight_counts_enter_and_exit()
    {
        var c = new MediaAnalysisInFlight();
        c.Enter();
        c.Enter();
        c.Exit();
        c.ActiveCount.Should().Be(1);
    }

    private static (ReceiveEndpointReady Ready, Mock<IReceiveEndpoint> Endpoint) FakeReady(string name)
    {
        var endpoint = new Mock<IReceiveEndpoint>();
        var uri = new Uri("rabbitmq://localhost/" + name);
        endpoint.SetupGet(e => e.InputAddress).Returns(uri);
        endpoint.Setup(e => e.Stop(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var ready = new Mock<ReceiveEndpointReady>();
        ready.SetupGet(r => r.InputAddress).Returns(uri);
        ready.SetupGet(r => r.ReceiveEndpoint).Returns(endpoint.Object);
        return (ready.Object, endpoint);
    }

    private static async Task Eventually(Func<bool> condition)
    {
        for (int i = 0; i < 100 && !condition(); i++) await Task.Delay(20);
    }

    [Fact]
    public async Task Engine_target_cancels_the_drain_token_and_with_no_endpoints_is_not_fully_stopped_when_some_are_expected()
    {
        var t = new EngineDrainTarget(NullLogger<EngineDrainTarget>.Instance);
        t.Expect(new Mock<IReceiveEndpointConfigurator>().Object);
        t.DrainToken.IsCancellationRequested.Should().BeFalse();
        await t.BeginDrainAsync(CancellationToken.None);
        t.DrainToken.IsCancellationRequested.Should().BeTrue();
        t.IsFullyStopped.Should().BeFalse("an expected endpoint never reported Ready, so an empty list is not 'stopped'");
    }

    [Fact]
    public async Task Engine_target_stops_an_endpoint_that_becomes_ready_after_drain_began()
    {
        var t = new EngineDrainTarget(NullLogger<EngineDrainTarget>.Instance);
        t.Expect(new Mock<IReceiveEndpointConfigurator>().Object);
        t.Expect(new Mock<IReceiveEndpointConfigurator>().Object);
        var (r1, e1) = FakeReady("a");
        var (r2, e2) = FakeReady("b");
        await t.Ready(r1);
        await t.BeginDrainAsync(CancellationToken.None);
        await Eventually(() => e1.Invocations.Count > 0);
        t.IsFullyStopped.Should().BeFalse("the second expected endpoint is not Ready yet");

        await t.Ready(r2);
        await Eventually(() => t.IsFullyStopped);
        e2.Verify(e => e.Stop(It.IsAny<CancellationToken>()), Times.Once);
        e1.Verify(e => e.Stop(It.IsAny<CancellationToken>()), Times.Once);
        t.IsFullyStopped.Should().BeTrue();
    }

    [Fact]
    public async Task Engine_target_is_fully_stopped_after_a_repeat_ready_for_a_known_address()
    {
        var t = new EngineDrainTarget(NullLogger<EngineDrainTarget>.Instance);
        t.Expect(new Mock<IReceiveEndpointConfigurator>().Object);
        var (r1, e1) = FakeReady("a");
        await t.Ready(r1);
        await t.BeginDrainAsync(CancellationToken.None);
        await Eventually(() => t.IsFullyStopped);
        await t.Ready(r1);
        await Eventually(() => e1.Invocations.Count(i => i.Method.Name == "Stop") >= 2);
        await Eventually(() => t.IsFullyStopped);
        t.IsFullyStopped.Should().BeTrue("a repeat Ready replaces the stop for that address instead of adding a second one");
    }

    [Fact]
    public async Task Requeue_filter_pauses_before_releasing_so_a_failed_stop_cannot_hot_loop()
    {
        var filter = new DrainRequeueFilter();
        var ctx = new Mock<ExceptionReceiveContext>();
        ctx.SetupGet(c => c.Exception).Returns(new DrainReleasedException());
        ctx.SetupGet(c => c.CancellationToken).Returns(CancellationToken.None);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Func<Task> act = () => filter.Send(ctx.Object, new Mock<IPipe<ExceptionReceiveContext>>().Object);
        await act.Should().ThrowAsync<DrainReleasedException>();
        sw.Elapsed.Should().BeGreaterThan(TimeSpan.FromMilliseconds(800));
    }

    [Fact]
    public async Task Status_updatedAt_keeps_moving_during_a_long_drain_when_counts_do_not_change()
    {
        var m = Make([new Source("runs") { ActiveCount = 3 }], [new Target()]);
        File.WriteAllText(Path.Combine(_dir, "drain"), "");
        await m.TickAsync(CancellationToken.None);
        string first = Status().GetProperty("updatedAt").GetString()!;
        _time.Now += TimeSpan.FromSeconds(6);
        await m.TickAsync(CancellationToken.None);
        Status().GetProperty("updatedAt").GetString().Should().NotBe(first);
        Status().GetProperty("activeWork").GetInt32().Should().Be(3);
    }

    [Fact]
    public async Task Status_write_never_follows_a_planted_symlink_at_the_old_temp_name()
    {
        string victim = Path.Combine(_dir, "victim.txt");
        File.WriteAllText(victim, "keep");
        string status = Path.Combine(_dir, "status.json");
        File.CreateSymbolicLink(status + ".tmp", victim);
        var m = Make([new Source("runs")], []);
        await m.TickAsync(CancellationToken.None);
        File.ReadAllText(victim).Should().Be("keep");
        Status().GetProperty("state").GetString().Should().Be("active");
        Directory.GetFiles(_dir, "status.json.*.tmp").Should().BeEmpty("the unique temp file is renamed away");
    }
}
