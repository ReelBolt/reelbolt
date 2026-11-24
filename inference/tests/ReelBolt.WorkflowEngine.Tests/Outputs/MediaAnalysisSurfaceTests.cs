#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using MassTransit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Inference;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Outputs;

/// <summary>The analysis routes of a media reference (state and stored data) refuse an output the project keeps back.</summary>
public sealed class MediaAnalysisSurfaceTests
{
    public enum Source { EarlierFile, LaterFile, Upload, EarlierResult, LaterResult }

    private static string RefOf(SurfaceWorld w, Source s) => s switch
    {
        Source.EarlierFile => $"file:{w.EarlierFile.Id:D}",
        Source.LaterFile => $"file:{w.LaterFile.Id:D}",
        Source.Upload => $"file:{w.Upload.Id:D}",
        Source.EarlierResult => $"step_result:{w.EarlierResult.Id:D}",
        _ => $"step_result:{w.LaterResult.Id:D}",
    };

    private static MediaAnalysisController Controller(SurfaceWorld w, InferenceApiDbContext? db = null)
    {
        InferenceApiDbContext d = db ?? w.Db;
        return new MediaAnalysisController(
            d, TestTenancy.Access(d, w.User), Mock.Of<IPublishEndpoint>(), w.User, Mock.Of<IInferenceProviderResolver>(),
            NullLogger<MediaAnalysisController>.Instance);
    }

    private static bool Refused(IActionResult? r) =>
        r is ObjectResult { StatusCode: 404, Value: { } v } && v.ToString()!.Contains("OUTPUT_UNAVAILABLE");

    public static IEnumerable<object[]> Withheld() { yield return [Source.EarlierFile]; yield return [Source.EarlierResult]; }
    public static IEnumerable<object[]> Kept() { yield return [Source.LaterFile]; yield return [Source.Upload]; yield return [Source.LaterResult]; }

    public static IEnumerable<object[]> Served()
    {
        foreach (Source s in Enum.GetValues<Source>())
        {
            yield return [s, SurfaceState.Clear];
            yield return [s, SurfaceState.ModerationOnly];
        }
    }

    [Theory]
    [MemberData(nameof(Withheld))]
    public async Task The_state_and_data_of_an_earlier_output_are_refused(Source source)
    {
        SurfaceWorld w = new SurfaceWorld().In(SurfaceState.Held);
        Refused(await Controller(w).GetStates(w.Project.Id, RefOf(w, source), CancellationToken.None)).Should().BeTrue();
        Refused(await Controller(w).GetData(w.Project.Id, RefOf(w, source), "transcript", CancellationToken.None)).Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(Kept))]
    public async Task The_state_of_a_later_output_or_an_upload_is_not_refused(Source source)
    {
        SurfaceWorld w = new SurfaceWorld().In(SurfaceState.Held);
        Refused(await Controller(w).GetStates(w.Project.Id, RefOf(w, source), CancellationToken.None)).Should().BeFalse();
        Refused(await Controller(w).GetData(w.Project.Id, RefOf(w, source), "transcript", CancellationToken.None)).Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(Served))]
    public async Task The_state_of_every_output_is_not_refused_when_the_project_keeps_nothing(Source source, SurfaceState state)
    {
        SurfaceWorld w = new SurfaceWorld().In(state);
        Refused(await Controller(w).GetStates(w.Project.Id, RefOf(w, source), CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task The_state_on_a_failed_read_is_refused()
    {
        SurfaceWorld w = new SurfaceWorld().In(SurfaceState.Clear);
        using MarkerReadFailsContext failing = w.MarkerFailingDb();
        Refused(await Controller(w, failing).GetStates(w.Project.Id, RefOf(w, Source.LaterFile), CancellationToken.None)).Should().BeTrue();
    }

    private static MediaAnalysisRequestBody Body() => new(["silence"], null);

    [Theory]
    [MemberData(nameof(Withheld))]
    public async Task Starting_analysis_of_an_earlier_output_is_refused(Source source)
    {
        SurfaceWorld w = new SurfaceWorld().In(SurfaceState.Held);
        Refused(await Controller(w).Start(w.Project.Id, RefOf(w, source), Body(), CancellationToken.None)).Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(Withheld))]
    public async Task Asking_the_cost_of_analysing_an_earlier_output_is_refused(Source source)
    {
        SurfaceWorld w = new SurfaceWorld().In(SurfaceState.Held);
        Refused(await Controller(w).Estimate(w.Project.Id, RefOf(w, source), Body(), CancellationToken.None)).Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(Kept))]
    public async Task Starting_analysis_of_a_later_output_or_an_upload_is_not_refused(Source source)
    {
        SurfaceWorld w = new SurfaceWorld().In(SurfaceState.Held);
        Refused(await Controller(w).Start(w.Project.Id, RefOf(w, source), Body(), CancellationToken.None)).Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(Kept))]
    public async Task Asking_the_cost_of_analysing_a_later_output_or_an_upload_is_not_refused(Source source)
    {
        SurfaceWorld w = new SurfaceWorld().In(SurfaceState.Held);
        Refused(await Controller(w).Estimate(w.Project.Id, RefOf(w, source), Body(), CancellationToken.None)).Should().BeFalse();
    }
}
