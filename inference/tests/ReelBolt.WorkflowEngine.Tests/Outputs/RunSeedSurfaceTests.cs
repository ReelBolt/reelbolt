#nullable enable
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.ChangeRequests;
using ReelBolt.Inference.Api.Services.Compositions;
using ReelBolt.Shared.Data.Models;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Outputs;

/// <summary>A run with an output the project keeps back cannot seed a change request or a composition replay.</summary>
public sealed class RunSeedSurfaceTests
{
    private const string RunNotAvailable = "The run that made this video is no longer available, so only the animation itself is made again.";

    private static Task<StepChangeSourceResolution> Change(SurfaceWorld w, bool earlier, InferenceApiDbContext? db = null) =>
        StepChangeRequestSources.LoadAsync(db ?? w.Db, w.Project.Id, (earlier ? w.EarlierRun : w.LaterRun).Id, CancellationToken.None);

    private static async Task<string?> Replay(SurfaceWorld w, bool earlier, InferenceApiDbContext? db = null)
    {
        InferenceApiDbContext d = db ?? w.Db;
        WorkflowExecution run = d.WorkflowExecutions.Find((earlier ? w.EarlierRun : w.LaterRun).Id)!;
        (_, string? reason) = await CompositionReplaySources.ResolveAsync(d, w.Project.Id, run, null, [], null, CancellationToken.None);
        return reason;
    }

    [Fact]
    public async Task A_change_cannot_be_seeded_from_an_earlier_run()
    {
        SurfaceWorld w = new SurfaceWorld().In(SurfaceState.Held);
        StepChangeSourceResolution r = await Change(w, earlier: true);
        r.Found.Should().BeFalse();
        r.StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task A_change_can_be_seeded_from_a_later_run_while_earlier_outputs_are_kept()
    {
        SurfaceWorld w = new SurfaceWorld().In(SurfaceState.Held);
        (await Change(w, earlier: false)).Found.Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(SurfaceWorld.ServedStates), MemberType = typeof(SurfaceWorld))]
    public async Task A_change_can_be_seeded_from_the_earlier_run_when_the_project_keeps_nothing(SurfaceState state)
    {
        SurfaceWorld w = new SurfaceWorld().In(state);
        (await Change(w, earlier: true)).Found.Should().BeTrue();
    }

    [Fact]
    public async Task A_change_cannot_be_seeded_when_the_marker_cannot_be_read()
    {
        SurfaceWorld w = new SurfaceWorld().In(SurfaceState.Clear);
        using MarkerReadFailsContext failing = w.MarkerFailingDb();
        (await Change(w, earlier: false, failing)).Found.Should().BeFalse();
    }

    [Fact]
    public async Task A_replay_cannot_reuse_an_earlier_run()
    {
        SurfaceWorld w = new SurfaceWorld().In(SurfaceState.Held);
        (await Replay(w, earlier: true)).Should().Be(RunNotAvailable);
    }

    [Fact]
    public async Task A_replay_can_reuse_a_later_run_while_earlier_outputs_are_kept()
    {
        SurfaceWorld w = new SurfaceWorld().In(SurfaceState.Held);
        (await Replay(w, earlier: false)).Should().NotBe(RunNotAvailable);
    }

    [Theory]
    [MemberData(nameof(SurfaceWorld.ServedStates), MemberType = typeof(SurfaceWorld))]
    public async Task A_replay_can_reuse_the_earlier_run_when_the_project_keeps_nothing(SurfaceState state)
    {
        SurfaceWorld w = new SurfaceWorld().In(state);
        (await Replay(w, earlier: true)).Should().NotBe(RunNotAvailable);
    }

    [Fact]
    public async Task A_replay_cannot_reuse_a_run_when_the_marker_cannot_be_read()
    {
        SurfaceWorld w = new SurfaceWorld().In(SurfaceState.Clear);
        using MarkerReadFailsContext failing = w.MarkerFailingDb();
        (await Replay(w, earlier: false, failing)).Should().Be(RunNotAvailable);
    }
}
