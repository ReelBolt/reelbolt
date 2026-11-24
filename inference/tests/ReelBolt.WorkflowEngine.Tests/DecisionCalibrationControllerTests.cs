using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.WorkflowEngine.Controllers;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Services.Decision;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Coverage for <see cref="DecisionCalibrationController"/> — the read-only calibration-report
/// endpoint (route <c>api/v1/workflow-engine/decision-calibration</c>).
///
/// <para>
/// There is no <c>WebApplicationFactory</c> harness anywhere in this repo (<c>docs/ci.md:74-76</c>),
/// so the controller is constructed directly and assertions are made on the returned
/// <see cref="ActionResult"/>, exactly as <c>InferenceProvidersControllerDecisionTests</c> does.
/// </para>
///
/// <para>
/// The admin check is read from <see cref="ControllerBase.User"/>, in the exact shape
/// <c>AdminController</c> uses, so these tests supply a real <see cref="ClaimsPrincipal"/> through
/// <c>ControllerContext</c> rather than an <c>ICurrentUser</c> fake: the admin-gated controllers in
/// the Inference API inject <c>ICurrentUser</c>, but that interface has no WorkflowEngine
/// registration (its only implementation, <c>CurrentUser</c>, lives in the Inference API project),
/// so a WorkflowEngine controller that depended on it could never be resolved at runtime.
/// </para>
/// </summary>
public sealed class DecisionCalibrationControllerTests
{
    private const string TestAuthenticationType = "TestAuth";

    [Fact]
    public async Task Get_returns_403_and_never_calls_the_service_for_a_non_admin_principal()
    {
        var service = new Mock<IDecisionCalibrationService>();
        DecisionCalibrationController controller = CreateController(service.Object, isAdmin: false);

        ActionResult<DecisionCalibrationReport> result =
            await controller.Get(null, null, null, null, CancellationToken.None);

        result.Result.Should().BeOfType<ForbidResult>();
        VerifyServiceNeverCalled(service);
    }

    [Fact]
    public async Task Get_returns_403_when_the_principal_carries_no_isAdmin_claim()
    {
        var service = new Mock<IDecisionCalibrationService>();
        DecisionCalibrationController controller = CreateController(
            service.Object,
            new ClaimsPrincipal(new ClaimsIdentity(Array.Empty<Claim>(), TestAuthenticationType)));

        ActionResult<DecisionCalibrationReport> result =
            await controller.Get(null, null, null, null, CancellationToken.None);

        result.Result.Should().BeOfType<ForbidResult>();
        VerifyServiceNeverCalled(service);
    }

    [Fact]
    public async Task Get_returns_200_with_the_report_the_service_returned_for_an_admin()
    {
        DecisionCalibrationReport report = new()
        {
            Totals = new DecisionCalibrationTotals { ObservationCount = 12, AcceptAt = 0.85, MinMargin = 0.25 },
            SampleSizeWarning = "Only 12 observations recorded; thresholds tuned here are not yet meaningful."
        };

        var service = new Mock<IDecisionCalibrationService>();
        service
            .Setup(s => s.GetCalibrationAsync(
                It.IsAny<double>(), It.IsAny<double>(), It.IsAny<Guid?>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(report);

        DecisionCalibrationController controller = CreateController(service.Object, isAdmin: true);

        ActionResult<DecisionCalibrationReport> result =
            await controller.Get(null, null, null, null, CancellationToken.None);

        OkObjectResult ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeSameAs(report);
    }

    [Theory]
    [InlineData(1.5)]
    [InlineData(-0.1)]
    [InlineData(double.NaN)]
    public async Task Get_returns_400_for_an_out_of_range_acceptAt(double acceptAt)
    {
        var service = new Mock<IDecisionCalibrationService>();
        DecisionCalibrationController controller = CreateController(service.Object, isAdmin: true);

        ActionResult<DecisionCalibrationReport> result =
            await controller.Get(acceptAt, null, null, null, CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        VerifyServiceNeverCalled(service);
    }

    [Theory]
    [InlineData(1.5)]
    [InlineData(-0.1)]
    [InlineData(double.NaN)]
    public async Task Get_returns_400_for_an_out_of_range_minMargin(double minMargin)
    {
        var service = new Mock<IDecisionCalibrationService>();
        DecisionCalibrationController controller = CreateController(service.Object, isAdmin: true);

        ActionResult<DecisionCalibrationReport> result =
            await controller.Get(null, minMargin, null, null, CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        VerifyServiceNeverCalled(service);
    }

    [Fact]
    public async Task Get_does_not_clamp_an_out_of_range_acceptAt_onto_the_boundary()
    {
        var service = new Mock<IDecisionCalibrationService>();
        DecisionCalibrationController controller = CreateController(service.Object, isAdmin: true);

        ActionResult<DecisionCalibrationReport> result =
            await controller.Get(2.0, null, null, null, CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        VerifyServiceNeverCalled(service);
    }

    [Fact]
    public async Task Get_passes_the_default_thresholds_unchanged_when_no_query_parameter_is_supplied()
    {
        var service = new Mock<IDecisionCalibrationService>();
        service
            .Setup(s => s.GetCalibrationAsync(
                It.IsAny<double>(), It.IsAny<double>(), It.IsAny<Guid?>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DecisionCalibrationReport());

        DecisionCalibrationController controller = CreateController(service.Object, isAdmin: true);

        await controller.Get(null, null, null, null, CancellationToken.None);

        service.Verify(
            s => s.GetCalibrationAsync(0.85, 0.25, null, null, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Get_treats_a_supplied_zero_acceptAt_as_a_value_not_as_an_absent_parameter()
    {
        var service = new Mock<IDecisionCalibrationService>();
        service
            .Setup(s => s.GetCalibrationAsync(
                It.IsAny<double>(), It.IsAny<double>(), It.IsAny<Guid?>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DecisionCalibrationReport());

        DecisionCalibrationController controller = CreateController(service.Object, isAdmin: true);

        await controller.Get(0.0, null, null, null, CancellationToken.None);

        service.Verify(
            s => s.GetCalibrationAsync(0.0, 0.25, null, null, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Get_accepts_the_inclusive_boundaries_zero_and_one()
    {
        var service = new Mock<IDecisionCalibrationService>();
        service
            .Setup(s => s.GetCalibrationAsync(
                It.IsAny<double>(), It.IsAny<double>(), It.IsAny<Guid?>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DecisionCalibrationReport());

        DecisionCalibrationController controller = CreateController(service.Object, isAdmin: true);

        ActionResult<DecisionCalibrationReport> result =
            await controller.Get(1.0, 0.0, null, null, CancellationToken.None);

        result.Result.Should().BeOfType<OkObjectResult>();
        service.Verify(
            s => s.GetCalibrationAsync(1.0, 0.0, null, null, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Get_passes_a_supplied_providerId_and_site_through_unchanged()
    {
        Guid providerId = Guid.NewGuid();

        var service = new Mock<IDecisionCalibrationService>();
        service
            .Setup(s => s.GetCalibrationAsync(
                It.IsAny<double>(), It.IsAny<double>(), It.IsAny<Guid?>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DecisionCalibrationReport());

        DecisionCalibrationController controller = CreateController(service.Object, isAdmin: true);

        await controller.Get(null, null, providerId, "Colorist", CancellationToken.None);

        service.Verify(
            s => s.GetCalibrationAsync(0.85, 0.25, providerId, "Colorist", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ------------------------------------------------ non-finite stored values (endpoint level)

    // The failure these reproduce is in SERIALIZATION, not in the aggregation: System.Text.Json
    // refuses to write a non-finite double, and the offending row is stored, so an unguarded
    // figure 500s this endpoint on every later request. Asserting "the service returned an object"
    // therefore proves nothing — the report has to go through the serializer the API uses.
    //
    // The rows are seeded for real and served by a real DecisionCalibrationService, so nothing
    // here can be satisfied by a mock's kindness.

    [Fact]
    public async Task Get_returns_200_and_serialises_a_row_carrying_positive_Infinity_in_its_probabilities()
    {
        // An overflowing JSON number is the stored-data route to +Infinity. Confidence is null —
        // as it is for a Noul row and for any provider that reported no scalar — so the top-two
        // stands in, which is what carries the non-finite value into the diagram and the mean.
        (ActionResult<DecisionCalibrationReport> result, string json) =
            await GetOverRealServiceAsync(new[] { Row("{\"a\":1e400,\"b\":0.1}", confidence: null) });

        OkObjectResult ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        json.Should().NotContain("Infinity").And.NotContain("NaN");

        // Counted, not silently dropped.
        ((DecisionCalibrationReport)ok.Value!).Totals.ObservationCount.Should().Be(1);
    }

    [Fact]
    public async Task Get_returns_200_and_serialises_a_row_whose_stored_confidence_is_NaN()
    {
        (ActionResult<DecisionCalibrationReport> result, string json) =
            await GetOverRealServiceAsync(new[] { Row("{\"a\":0.9,\"b\":0.1}", confidence: double.NaN) });

        OkObjectResult ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        json.Should().NotContain("Infinity").And.NotContain("NaN");

        ((DecisionCalibrationReport)ok.Value!).Totals.ObservationCount.Should().Be(1);
    }

    [Fact]
    public async Task Get_returns_200_and_serialises_a_Noul_row_whose_answer_parses_to_Infinity()
    {
        // "Infinity" parses under NumberStyles.Float, so the Noul answer path is the second route
        // from stored text to a non-finite double.
        (ActionResult<DecisionCalibrationReport> result, string json) = await GetOverRealServiceAsync(
            new[] { Row("{}", confidence: null, kind: DecisionKind.Noul, answer: "Infinity") });

        OkObjectResult ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        json.Should().NotContain("Infinity").And.NotContain("NaN");

        ((DecisionCalibrationReport)ok.Value!).Totals.ObservationCount.Should().Be(1);
    }

    [Fact]
    public async Task Get_returns_200_and_serialises_a_Noul_row_whose_answer_parses_to_NaN()
    {
        (ActionResult<DecisionCalibrationReport> result, string json) = await GetOverRealServiceAsync(
            new[] { Row("{}", confidence: null, kind: DecisionKind.Noul, answer: "NaN") });

        OkObjectResult ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        json.Should().NotContain("Infinity").And.NotContain("NaN");

        ((DecisionCalibrationReport)ok.Value!).Totals.ObservationCount.Should().Be(1);
    }

    [Fact]
    public async Task Get_returns_200_when_one_non_finite_row_sits_beside_ordinary_ones()
    {
        // The realistic shape: one hand-edited row among healthy traffic. The endpoint must not be
        // permanently poisoned by it, and the healthy rows must still be aggregated.
        (ActionResult<DecisionCalibrationReport> result, string json) = await GetOverRealServiceAsync(new[]
        {
            Row("{\"a\":1e400,\"b\":0.1}", confidence: null),
            Row("{\"a\":0.9,\"b\":0.1}", confidence: 0.9),
        });

        OkObjectResult ok = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        json.Should().NotContain("Infinity").And.NotContain("NaN");

        DecisionCalibrationReport report = (DecisionCalibrationReport)ok.Value!;

        // Both rows are observations, but the mean is computed from the one finite row alone —
        // 0.9, not 0.95 and never infinity. What the unscoreable row cannot inform is excluded;
        // the row itself is not.
        report.Totals.ObservationCount.Should().Be(2);
        report.Sites.Single().MeanConfidence.Should().BeApproximately(0.9, 1e-9);
        report.Sites.Single().EscalationRate.Should().Be(0);
    }

    /// <summary>
    /// The options ASP.NET Core MVC installs for <c>AddControllers()</c> when nobody calls
    /// <c>AddJsonOptions</c> — which is the case in this service, and is precisely why the defect
    /// is reachable. Using the real <see cref="JsonOptions"/> rather than a hand-rolled
    /// <see cref="JsonSerializerOptions"/> is what makes this an endpoint-level assertion.
    /// </summary>
    private static readonly JsonSerializerOptions ApiJsonOptions =
        new JsonOptions().JsonSerializerOptions;

    /// <summary>
    /// Seeds <paramref name="rows"/> into a real in-memory table, runs the real
    /// <see cref="DecisionCalibrationService"/> through the controller, and returns the response
    /// together with the report serialized exactly as the API would write it. The serialization
    /// step is the assertion that matters: it is where the exception is raised.
    /// </summary>
    private static async Task<(ActionResult<DecisionCalibrationReport> Result, string Json)>
        GetOverRealServiceAsync(IEnumerable<DecisionObservation> rows)
    {
        await using WorkflowEngineDbContext db = new(
            new DbContextOptionsBuilder<WorkflowEngineDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options);

        db.DecisionObservations.AddRange(rows);
        await db.SaveChangesAsync();

        DecisionCalibrationController controller = CreateController(
            new DecisionCalibrationService(db), isAdmin: true);

        ActionResult<DecisionCalibrationReport> result =
            await controller.Get(null, null, null, null, CancellationToken.None);

        string json = result.Result is OkObjectResult ok
            ? JsonSerializer.Serialize((DecisionCalibrationReport)ok.Value!, ApiJsonOptions)
            : "";

        return (result, json);
    }

    private static DecisionObservation Row(
        string probabilitiesJson,
        double? confidence,
        DecisionKind kind = DecisionKind.Choice,
        string answer = "a") =>
        new()
        {
            Id = Guid.NewGuid(),
            ExecutionId = Guid.NewGuid(),
            StepOrder = 1,
            AgentType = AgentType.Colorist,
            Site = "Colorist",
            Question = "Look",
            OptionsHash = "hash",
            Kind = kind,
            Answer = answer,
            Confidence = confidence,
            ProbabilitiesJson = probabilitiesJson,
            Mode = "Shadow",
            Escalated = false,
            DownstreamOutcomeJson = "{\"agreed\":true}",
            CreatedAt = DateTime.UtcNow,
        };

    private static DecisionCalibrationController CreateController(
        IDecisionCalibrationService service,
        bool isAdmin)
        => CreateController(
            service,
            new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim("isAdmin", isAdmin ? "true" : "false") },
                TestAuthenticationType)));

    private static DecisionCalibrationController CreateController(
        IDecisionCalibrationService service,
        ClaimsPrincipal principal)
        => new(service)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = principal }
            }
        };

    private static void VerifyServiceNeverCalled(Mock<IDecisionCalibrationService> service)
        => service.Verify(
            s => s.GetCalibrationAsync(
                It.IsAny<double>(), It.IsAny<double>(), It.IsAny<Guid?>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
}
