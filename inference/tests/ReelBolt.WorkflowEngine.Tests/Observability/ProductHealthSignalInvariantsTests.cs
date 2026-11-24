using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Moq;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Observability;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.DesignSystems;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Observability;

/// <summary>
/// T-020, the property the whole ticket exists for (plan Global Constraints, "the ambient health signals
/// are reporting only"; Review Focus 9): <b>no signal path can change a response</b>. Each test drives a
/// real response path with the detectors off, on, and failing, and asserts the answer is the same one.
/// </summary>
public class ProductHealthSignalInvariantsTests
{
    private sealed class RecordingSink : IProductHealthSignalSink
    {
        private readonly List<ProductHealthSignal> _signals = [];
        public IReadOnlyList<ProductHealthSignal> Signals { get { lock (_signals) return [.. _signals]; } }
        public void Emit(ProductHealthSignal signal) { lock (_signals) _signals.Add(signal); }
    }

    private sealed class ThrowingSink : IProductHealthSignalSink
    {
        public void Emit(ProductHealthSignal signal) => throw new InvalidOperationException("the sink is down");
    }

    private static ProductHealthSignalDetector On(RecordingSink sink) => new(sink);
    private static ProductHealthSignalDetector Broken() => new(new ThrowingSink());

    private sealed class FakeUser(Guid userId, Guid organizationId) : ICurrentUser
    {
        public Guid UserId { get; } = userId;
        public string Email => "test@example.com";
        public bool IsAuthenticated => true;
        public Guid? OrganizationId { get; } = organizationId;
        public OrganizationRole? OrgRole => OrganizationRole.Owner;
        public bool IsPlatformAdmin => false;
    }

    private static string BodyOf(ActionResult result) => JsonSerializer.Serialize(((ObjectResult)result).Value);

    // ---------------------------------------------------------------------
    // A refused run start still returns its own 402, detectors on or off
    // ---------------------------------------------------------------------

    private static RunStartGate RunGate(ICurrentUser user, Project project, IProductHealthSignalDetector? signals)
    {
        var planGate = new Mock<IPlanGateService>();
        planGate
            .Setup(g => g.CheckResolutionAsync(project.OrganizationId, It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlanGateViolation(PlanGate.NotEntitledCode, "resolution", "Your plan does not render 4K.", 402));
        var designBuild = new Mock<IDesignBuildEstimateGate>();
        var db = new InferenceApiDbContext(new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

        return new RunStartGate(db, user, planGate.Object, costEstimator: null, designBuild.Object, signals);
    }

    [Fact]
    public async Task A_402_refusal_is_byte_identical_with_the_detectors_off_on_and_broken()
    {
        Guid userId = Guid.NewGuid();
        Guid orgId = TestTenancy.OrgOf(userId);
        var user = new FakeUser(userId, orgId);
        var project = new Project { Id = Guid.NewGuid(), Name = "P", OwnerId = userId, OrganizationId = orgId };

        ActionResult? off = await RunGate(user, project, signals: null)
            .RefuseAsync(project, Guid.NewGuid(), 2160, RunStartPricing.SavedWorkflow, null, CancellationToken.None);

        var sink = new RecordingSink();
        ActionResult? on = await RunGate(user, project, On(sink))
            .RefuseAsync(project, Guid.NewGuid(), 2160, RunStartPricing.SavedWorkflow, null, CancellationToken.None);

        ActionResult? broken = await RunGate(user, project, Broken())
            .RefuseAsync(project, Guid.NewGuid(), 2160, RunStartPricing.SavedWorkflow, null, CancellationToken.None);

        ((ObjectResult)off!).StatusCode.Should().Be(402);
        ((ObjectResult)on!).StatusCode.Should().Be(402);
        ((ObjectResult)broken!).StatusCode.Should().Be(402);
        BodyOf(on).Should().Be(BodyOf(off), "the refusal the caller receives is the one it would have received");
        BodyOf(broken).Should().Be(BodyOf(off), "a broken detector cannot change the refusal");

        ProductHealthSignal signal = sink.Signals.Single();
        signal.Kind.Should().Be(ProductHealthSignalKinds.RequestRefused);
        signal.Code.Should().Be(PlanGate.NotEntitledCode);
        signal.ProjectId.Should().Be(project.Id);
        signal.UserId.Should().Be(userId);
    }

    // ---------------------------------------------------------------------
    // The approval gate is byte-identical with the detectors on and off
    // ---------------------------------------------------------------------

    private sealed class GateHarness : IDisposable
    {
        public InferenceApiDbContext Db { get; }
        public Guid UserId { get; } = Guid.NewGuid();
        public Guid OrgId => TestTenancy.OrgOf(UserId);
        public Project Project { get; }
        public AssistantApprovalBroker Broker { get; } = new();
        public AssistantToolContext Context { get; }
        private readonly ICurrentUser _user;

        public GateHarness(bool auto)
        {
            Db = new InferenceApiDbContext(new DbContextOptionsBuilder<InferenceApiDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
            Db.ApplicationUsers.Add(new ApplicationUser
            {
                Id = UserId,
                Email = "test@example.com",
                DisplayName = "Test",
                AssistantAutoApprove = auto
            });
            Project = new Project
            {
                Id = Guid.NewGuid(),
                Name = "Launch Teaser",
                OwnerId = UserId,
                OrganizationId = OrgId,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            Db.Projects.Add(Project);
            Db.SaveChanges();

            _user = new FakeUser(UserId, OrgId);
            Context = new AssistantToolContext(UserId, Project.Id);
        }

        public (AssistantApprovalGate Gate, List<AssistantActionFrame> Frames) NewGate(IProductHealthSignalDetector? signals)
        {
            var frames = new List<AssistantActionFrame>();
            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { [AssistantApprovalGate.TimeoutKey] = "30" })
                .Build();
            var gate = new AssistantApprovalGate(
                Context,
                OrgId,
                Db,
                Broker,
                new AssistantApprovalCardBuilder(Db, TestTenancy.Access(Db, _user), _user, null),
                configuration,
                emit: (frame, _) => { lock (frames) frames.Add(frame); return Task.CompletedTask; },
                caption: (_, _) => Task.CompletedTask,
                logger: null,
                signals: signals);
            return (gate, frames);
        }

        public void Dispose() => Db.Dispose();
    }

    private static AIFunctionArguments Args() => new() { ["confirmationToken"] = "t" };

    /// <summary>Every field the caller could branch on, with the server-generated id and clock removed.</summary>
    private static List<string> Decisions(List<AssistantActionFrame> frames) =>
        [.. frames.Select(f => string.Join("|", f.State, f.Mode, f.Kind, f.Risk, f.Title, f.Summary, f.ProjectName, f.Cost, f.Outcome,
            string.Join(",", f.Changes?.Select(c => $"{c.Kind}:{c.Text}") ?? [])))];

    [Fact]
    public async Task A_refused_tool_call_still_returns_its_own_ok_false_unchanged()
    {
        using var h = new GateHarness(auto: true);
        const string toolResult = "{\"ok\": false, \"error\": \"the tool could not do it\"}";

        var sink = new RecordingSink();
        (AssistantApprovalGate gateOn, List<AssistantActionFrame> framesOn) = h.NewGate(On(sink));
        (AssistantApprovalGate gateOff, List<AssistantActionFrame> framesOff) = h.NewGate(signals: null);

        object? returnedOff = await gateOff.RunAsync("ApplyDeleteProject", Args(), () => ValueTask.FromResult<object?>(toolResult), CancellationToken.None);
        object? returnedOn = await gateOn.RunAsync("ApplyDeleteProject", Args(), () => ValueTask.FromResult<object?>(toolResult), CancellationToken.None);

        ReferenceEquals(returnedOn, toolResult).Should().BeTrue("the tool's own result is handed back, not a copy the detector could touch");
        returnedOn.Should().Be(returnedOff);
        Decisions(framesOn).Should().Equal(Decisions(framesOff), "the card the user sees is the same with a detector attached");
        sink.Signals.Single().Kind.Should().Be(ProductHealthSignalKinds.ToolCallFailed);
    }

    [Fact]
    public async Task The_approval_gate_behaves_identically_with_the_detectors_off_on_and_broken()
    {
        using var h = new GateHarness(auto: true);
        var sink = new RecordingSink();

        (AssistantApprovalGate off, List<AssistantActionFrame> f1) = h.NewGate(signals: null);
        (AssistantApprovalGate on, List<AssistantActionFrame> f2) = h.NewGate(On(sink));
        (AssistantApprovalGate broken, List<AssistantActionFrame> f3) = h.NewGate(Broken());

        string[] results =
        [
            await off.RunAsync("ApplyDeleteProject", Args(), () => ValueTask.FromResult<object?>("{\"ok\":true}"), CancellationToken.None) as string ?? "",
            await on.RunAsync("ApplyDeleteProject", Args(), () => ValueTask.FromResult<object?>("{\"ok\":true}"), CancellationToken.None) as string ?? "",
            await broken.RunAsync("ApplyDeleteProject", Args(), () => ValueTask.FromResult<object?>("{\"ok\":true}"), CancellationToken.None) as string ?? ""
        ];

        results.Should().AllBe("{\"ok\":true}");
        Decisions(f2).Should().Equal(Decisions(f1));
        Decisions(f3).Should().Equal(Decisions(f1), "a broken detector cannot change what the gate decides");
    }

    [Fact]
    public async Task The_users_own_error_surfaces_unchanged_with_a_detector_attached()
    {
        using var h = new GateHarness(auto: true);
        var sink = new RecordingSink();

        (AssistantApprovalGate on, List<AssistantActionFrame> framesOn) = h.NewGate(On(sink));
        (AssistantApprovalGate off, List<AssistantActionFrame> framesOff) = h.NewGate(signals: null);

        Func<Task> withDetector = () => on.RunAsync("ApplyDeleteProject", Args(), () => throw new InvalidOperationException("boom"), CancellationToken.None);
        Func<Task> withoutDetector = () => off.RunAsync("ApplyDeleteProject", Args(), () => throw new InvalidOperationException("boom"), CancellationToken.None);

        (await withDetector.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Be("boom");
        await withoutDetector.Should().ThrowAsync<InvalidOperationException>();
        Decisions(framesOn).Should().Equal(Decisions(framesOff), "the failure card is the same with a detector attached");
        sink.Signals.Single().Kind.Should().Be(ProductHealthSignalKinds.ConversationAbandonedAfterError);
    }

    [Fact]
    public async Task A_detector_that_throws_never_fails_the_request()
    {
        using var h = new GateHarness(auto: true);
        (AssistantApprovalGate broken, List<AssistantActionFrame> frames) = h.NewGate(Broken());

        const string toolResult = "{\"ok\": false, \"error\": \"the tool could not do it\"}";
        object? result = await broken.RunAsync(
            "ApplyDeleteProject", Args(), () => ValueTask.FromResult<object?>(toolResult), CancellationToken.None);

        result.Should().Be(toolResult, "a sink that threw must not turn a tool's answer into a failed request");
        frames.Single().State.Should().Be(AssistantActionState.Failed);
    }
}
