#nullable enable
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Data;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Outputs;

/// <summary>The dashboard counts and shows only the outputs a project serves.</summary>
public sealed class DashboardSurfaceTests
{
    private static async Task<ProjectOverview> Overview(SurfaceWorld w, InferenceApiDbContext? db = null)
    {
        InferenceApiDbContext d = db ?? w.Db;
        ActionResult<DashboardOverviewResponse> r = await new DashboardController(d, w.User, TestTenancy.Access(d, w.User)).Overview(CancellationToken.None);
        return ((DashboardOverviewResponse)r.Result.Should().BeOfType<OkObjectResult>().Subject.Value!).Projects.Single(p => p.ProjectId == w.Project.Id);
    }

    [Fact]
    public async Task An_earlier_output_is_not_counted_and_is_never_the_latest()
    {
        SurfaceWorld w = new SurfaceWorld().In(SurfaceState.Held);
        w.EarlierResult.CompletedAt = SurfaceWorld.Marker.AddDays(3);
        w.EarlierResult.ExecutedAt = SurfaceWorld.Marker.AddHours(-2);
        w.Db.SaveChanges();
        ProjectOverview o = await Overview(w);
        o.RenderCount.Should().Be(1);
        o.LatestOutput!.StepResultId.Should().Be(w.LaterResult.Id);
    }

    [Theory]
    [MemberData(nameof(SurfaceWorld.ServedStates), MemberType = typeof(SurfaceWorld))]
    public async Task Both_outputs_are_counted_when_the_project_keeps_nothing(SurfaceState state)
    {
        SurfaceWorld w = new SurfaceWorld().In(state);
        (await Overview(w)).RenderCount.Should().Be(2);
    }

    [Fact]
    public async Task No_output_is_counted_when_the_marker_cannot_be_read()
    {
        SurfaceWorld w = new SurfaceWorld().In(SurfaceState.Clear);
        using MarkerReadFailsContext failing = w.MarkerFailingDb();
        ProjectOverview o = await Overview(w, failing);
        o.RenderCount.Should().Be(0);
        o.LatestOutput.Should().BeNull();
    }
}
