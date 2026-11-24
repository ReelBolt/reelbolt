#nullable enable
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Compositions;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Data.Models;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Outputs;

/// <summary>A run with an output the project keeps back offers no change to its video.</summary>
public sealed class CompositionHistorySurfaceTests
{
    private const string NoVideo = "This run did not make an animated video, so there is nothing here to change.";

    private static async Task<CompositionRevisionHistoryResponse> History(SurfaceWorld w, bool earlier, InferenceApiDbContext? db = null)
    {
        InferenceApiDbContext d = db ?? w.Db;
        CompositionRevisionService service = new(
            d, new WorkflowExecutionService(d, Mock.Of<IPublishEndpoint>(), NullLogger<WorkflowExecutionService>.Instance),
            new ConfigurationBuilder().Build(), NullLogger<CompositionRevisionService>.Instance);
        return (await service.GetHistoryAsync(w.Project, (earlier ? w.EarlierRun : w.LaterRun).Id, w.Project.OrganizationId, CancellationToken.None))!;
    }

    private static SurfaceWorld FilmWorld()
    {
        SurfaceWorld w = new();
        // The run's render came from an agent step, so it would be offered for change if it were served.
        w.Step.StepType = StepType.Agent;
        w.Db.SaveChanges();
        return w;
    }

    [Fact]
    public async Task An_earlier_run_offers_no_change()
    {
        SurfaceWorld w = FilmWorld().In(SurfaceState.Held);
        CompositionRevisionHistoryResponse history = await History(w, earlier: true);
        history.Availability.Should().Be(CompositionRevisionService.AvailabilityUnavailable);
        history.UnavailableReason.Should().Be(NoVideo);
        history.CanRevise.Should().BeFalse();
    }

    [Fact]
    public async Task A_later_run_is_not_refused_while_earlier_outputs_are_kept()
    {
        SurfaceWorld w = FilmWorld().In(SurfaceState.Held);
        (await History(w, earlier: false)).UnavailableReason.Should().NotBe(NoVideo);
    }

    [Theory]
    [MemberData(nameof(SurfaceWorld.ServedStates), MemberType = typeof(SurfaceWorld))]
    public async Task The_earlier_run_is_not_refused_when_the_project_keeps_nothing(SurfaceState state)
    {
        SurfaceWorld w = FilmWorld().In(state);
        (await History(w, earlier: true)).UnavailableReason.Should().NotBe(NoVideo);
    }

    [Fact]
    public async Task A_run_offers_no_change_when_the_marker_cannot_be_read()
    {
        SurfaceWorld w = FilmWorld().In(SurfaceState.Clear);
        using MarkerReadFailsContext failing = w.MarkerFailingDb();
        (await History(w, earlier: false, failing)).UnavailableReason.Should().Be(NoVideo);
    }
}
