#nullable enable
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Amazon.S3;
using Amazon.S3.Model;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;
using Xunit;
using System;
using System.Collections.Generic;

namespace ReelBolt.WorkflowEngine.Tests.Outputs;

/// <summary>A step result's analysis file, timeline and subtitles follow the same rule as its video.</summary>
public sealed class StepResultArtifactsSurfaceTests
{
    public enum Route { Artifact, Timeline, Subtitles }

    private static (StepResultArtifactsController Controller, Mock<IAmazonS3> S3) NewController(SurfaceWorld w, InferenceApiDbContext? db = null)
    {
        Mock<IAmazonS3> s3 = new();
        s3.Setup(s => s.GetObjectAsync(It.IsAny<GetObjectRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new GetObjectResponse { ResponseStream = new MemoryStream([1, 2, 3]) });
        StepResultArtifactsController controller = new(
            db ?? w.Db, w.User, w.Access(), s3.Object,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["MinIO:BucketName"] = "reelforge" }).Build())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        return (controller, s3);
    }

    private static Task<IActionResult> Call(StepResultArtifactsController c, SurfaceWorld w, Route route, WorkflowStepResult r) => route switch
    {
        Route.Artifact => c.GetArtifact(w.Project.Id, r.Id, CancellationToken.None),
        Route.Timeline => c.GetTimeline(w.Project.Id, r.Id, CancellationToken.None),
        _ => c.GetSubtitles(w.Project.Id, r.Id, "srt", CancellationToken.None),
    };

    private static void ShouldBeRefused(IActionResult result)
    {
        ObjectResult refused = result.Should().BeOfType<ObjectResult>().Subject;
        refused.StatusCode.Should().Be(404);
        refused.Value!.ToString().Should().Contain("OUTPUT_UNAVAILABLE");
    }

    public static IEnumerable<object[]> Routes() => [[Route.Artifact], [Route.Timeline], [Route.Subtitles]];

    public static IEnumerable<object[]> ServedByRoute()
    {
        foreach (Route route in Enum.GetValues<Route>())
        {
            yield return [route, SurfaceState.Clear];
            yield return [route, SurfaceState.ModerationOnly];
        }
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task The_earlier_result_is_refused_before_the_store_is_read(Route route)
    {
        SurfaceWorld w = new SurfaceWorld().In(SurfaceState.Held);
        var (c, s3) = NewController(w);
        ShouldBeRefused(await Call(c, w, route, w.EarlierResult));
        s3.Verify(s => s.GetObjectAsync(It.IsAny<GetObjectRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task A_later_result_is_served_while_the_project_keeps_earlier_ones(Route route)
    {
        SurfaceWorld w = new SurfaceWorld().In(SurfaceState.Held);
        var (c, _) = NewController(w);
        (await Call(c, w, route, w.LaterResult)).Should().BeOfType<FileStreamResult>();
    }

    [Theory]
    [MemberData(nameof(ServedByRoute))]
    public async Task The_earlier_result_is_served_when_the_project_keeps_nothing(Route route, SurfaceState state)
    {
        SurfaceWorld w = new SurfaceWorld().In(state);
        var (c, _) = NewController(w);
        (await Call(c, w, route, w.EarlierResult)).Should().BeOfType<FileStreamResult>();
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task A_failed_read_refuses(Route route)
    {
        SurfaceWorld w = new SurfaceWorld().In(SurfaceState.Clear);
        using ProjectReadFailsContext failing = w.FailingDb();
        var (c, s3) = NewController(w, failing);
        ShouldBeRefused(await Call(c, w, route, w.LaterResult));
        s3.Verify(s => s.GetObjectAsync(It.IsAny<GetObjectRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
