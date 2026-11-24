using ReelBolt.Shared.Inference;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using MassTransit;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Compositions;
using ReelBolt.Inference.Api.Services.Graphics;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Editing;
using ReelBolt.Shared.Tenancy;
using ReelBolt.WorkflowEngine.Tests.Agents;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Graphics;

/// <summary>
/// The request side of designed graphics (Inference API): validation, the hidden system workflow, the
/// refusals the endpoint renders (400/404/409/429/503/402), settling a finished run into ONE layer placed
/// where it was drawn (or where the revised layer now is), and the tenancy of the controller.
/// </summary>
public class GraphicDesignApiTests
{
    private sealed class Caller(Guid id, bool verified = true) : ICurrentUser
    {
        public Guid UserId { get; } = id;
        public string Email => "u@example.com";
        public bool IsAuthenticated => true;
        public bool IsPlatformAdmin => false;
        public Guid? OrganizationId => TestTenancy.OrgOf(UserId);
        public OrganizationRole? OrgRole => OrganizationRole.Member;
        public bool EmailVerified => verified;
    }

    /// <summary>An estimator whose preflight refuses (billing on, balance too low).</summary>
    private sealed class BrokeEstimator : IWorkflowCostEstimator
    {
        public bool BillingEnabled => true;

        public Task<WorkflowCostEstimate> EstimateAsync(Guid? organizationId, IReadOnlyList<WorkflowStep> steps, CancellationToken ct = default) =>
            Task.FromResult(new WorkflowCostEstimate(true, false, 4_000, 300_000, 300_000, []));

        public Task<WorkflowPreflightResult> PreflightAsync(Guid? organizationId, IReadOnlyList<WorkflowStep> steps, CancellationToken ct = default) =>
            Task.FromResult(new WorkflowPreflightResult(WorkflowPreflightOutcome.InsufficientCredits, new WorkflowCostEstimate(true, false, 4_000, 300_000, 300_000, []), 1_000));

        public Task<WorkflowCostEstimate> EstimateChargedAsync(Guid? organizationId, IReadOnlyList<WorkflowStep> runSteps, IReadOnlyCollection<int> chargedStepOrders, CancellationToken ct = default) =>
            EstimateAsync(organizationId, runSteps, ct);

        public Task<WorkflowPreflightResult> PreflightChargedAsync(Guid? organizationId, IReadOnlyList<WorkflowStep> runSteps, IReadOnlyCollection<int> chargedStepOrders, CancellationToken ct = default) =>
            PreflightAsync(organizationId, runSteps, ct);
    }

    private sealed class Fixture : IDisposable
    {
        public InferenceApiDbContext Db { get; } = new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase("graphics-" + Guid.NewGuid().ToString("N")).Options);
        public Guid User { get; } = Guid.NewGuid();
        public Project Project { get; }
        public EditImageDocument Document { get; }
        public EditImageLayer Source { get; }
        public FakeFileStorage Storage { get; } = new();

        public Fixture(bool seedAgent = true, int maxPerHour = 20, IWorkflowCostEstimator? estimator = null, IInferenceProviderResolver? providers = null)
        {
            Project = new Project { Id = Guid.NewGuid(), Name = "P", OwnerId = User, OrganizationId = TestTenancy.OrgOf(User), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            ProjectFile file = new() { Id = Guid.NewGuid(), ProjectId = Project.Id, OriginalFileName = "cover.png", MimeType = "image/png", StorageKey = "k", Category = "userFiles" };
            Document = new EditImageDocument { Id = Guid.NewGuid(), ProjectId = Project.Id, SourceProjectFileId = file.Id, CanvasWidth = 1280, CanvasHeight = 720 };
            Source = new EditImageLayer { Id = Guid.NewGuid(), DocumentId = Document.Id, ZOrder = 1, Kind = EditImageLayerKind.Source, Label = "cover.png", StorageKey = "k", TransformJson = "{}" };
            Db.Projects.Add(Project);
            Db.ProjectFiles.Add(file);
            Db.EditImageDocuments.Add(Document);
            Db.EditImageLayers.Add(Source);
            if (seedAgent)
                Db.AgentDefinitions.Add(new AgentDefinition { Id = Guid.NewGuid(), Name = "GraphicDesigner", AgentType = AgentType.GraphicDesigner, IsBuiltIn = true });
            Db.SaveChanges();
            Db.ChangeTracker.Clear();

            Service = new GraphicDesignService(
                Db,
                new WorkflowExecutionService(Db, Mock.Of<IPublishEndpoint>(), NullLogger<WorkflowExecutionService>.Instance),
                Storage,
                new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["GraphicDesign:MaxPerHourPerUserProject"] = maxPerHour.ToString()
                }).Build(),
                NullLogger<GraphicDesignService>.Instance,
                estimator,
                providers);
        }

        public GraphicDesignService Service { get; }

        public Task<GraphicDesignRequestResult> Request(CreateImageGraphicRequest body, Guid? documentId = null) =>
            Service.RequestAsync(Project, documentId ?? Document.Id, User, Project.OrganizationId, body, ExecutionInitiator.Human, CancellationToken.None);

        /// <summary>What the engine writes when a run finished: the verified PNG's key, size and crop, and the run's status.</summary>
        public async Task FinishAsync(Guid graphicId, int width, int height, int cropX, int cropY, ExecutionStatus status = ExecutionStatus.Passed, string? key = null, Guid? versionId = null)
        {
            EditImageGraphicRequest row = await Db.EditImageGraphicRequests.SingleAsync(r => r.Id == graphicId);
            row.ResultStorageKey = key ?? GraphicDesignRules.GraphicKey(Project.Id, Document.Id, Guid.NewGuid());
            row.ResultWidth = width;
            row.ResultHeight = height;
            row.ResultOffsetX = cropX;
            row.ResultOffsetY = cropY;
            row.ResultVersionId = versionId;
            row.ResultSummary = "A red SALE badge.";
            WorkflowExecution run = await Db.WorkflowExecutions.SingleAsync(e => e.Id == row.ExecutionId);
            run.Status = status;
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
        }

        public void Dispose() => Db.Dispose();
    }

    // ------------------------------------------------------------------ no chat provider

    private static IInferenceProviderResolver Chat(Guid? providerId, string endpoint = "", string key = "")
    {
        Mock<IInferenceProviderResolver> resolver = new();
        resolver.Setup(r => r.ResolveAsync(AgentType.GraphicDesigner, It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResolvedInferenceProvider(providerId, providerId is null ? "config" : "Chat", InferenceProviderKind.AzureOpenAI, endpoint, "m", key, 60));
        return resolver.Object;
    }

    [Fact]
    public async Task With_no_chat_provider_the_list_says_designing_is_unavailable_and_a_request_is_refused_before_anything_is_queued()
    {
        using Fixture f = new(providers: Chat(null));

        ImageGraphicsResponse? list = await f.Service.ListAsync(f.Project, f.Document.Id, f.Project.OrganizationId, CancellationToken.None);
        list!.Available.Should().BeFalse();
        list.UnavailableReason.Should().Be(GraphicDesignService.NoChatProviderMessage);

        GraphicDesignRequestResult result = await f.Request(new CreateImageGraphicRequest("A red SALE badge"));
        result.StatusCode.Should().Be(503);
        result.Message.Should().Be(GraphicDesignService.NoChatProviderMessage);
        (await f.Db.EditImageGraphicRequests.CountAsync()).Should().Be(0);
        (await f.Db.WorkflowExecutions.CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData(true, "", "")]
    [InlineData(false, "https://example.test", "key")]
    public async Task A_configured_chat_provider_or_the_legacy_endpoint_and_key_leaves_designing_available(bool hasProviderId, string endpoint, string key)
    {
        using Fixture f = new(providers: Chat(hasProviderId ? Guid.NewGuid() : null, endpoint, key));

        (await f.Service.ListAsync(f.Project, f.Document.Id, f.Project.OrganizationId, CancellationToken.None))!.Available.Should().BeTrue();
        (await f.Request(new CreateImageGraphicRequest("A red SALE badge"))).StatusCode.Should().Be(201);
    }

    // ------------------------------------------------------------------ requesting

    [Fact]
    public async Task A_restore_in_progress_refuses_a_graphic_request_before_anything_is_queued()
    {
        using Fixture f = new();

        using IDisposable held = RestoreRunGate.HoldForTests(f.Project.Id);
        GraphicDesignRequestResult result = await f.Request(new CreateImageGraphicRequest("A red SALE badge"));

        result.StatusCode.Should().Be(409);
        result.Code.Should().Be("RESTORE_IN_PROGRESS");
        (await f.Db.EditImageGraphicRequests.CountAsync()).Should().Be(0);
        (await f.Db.WorkflowExecutions.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_request_creates_the_hidden_workflow_the_row_and_a_queued_run()
    {
        using Fixture f = new();

        GraphicDesignRequestResult result = await f.Request(new CreateImageGraphicRequest("A red SALE badge", "topRight", 400, 200));

        result.StatusCode.Should().Be(201, result.Message);
        ImageGraphicJobDto job = result.Created!;
        job.Status.Should().Be("Queued");
        (job.FrameWidth, job.FrameHeight).Should().Be((400, 200));
        job.Target.X.Should().BeGreaterThan(640);

        WorkflowDefinition hidden = await f.Db.WorkflowDefinitions.Include(w => w.Steps).SingleAsync();
        hidden.Kind.Should().Be(WorkflowDefinitionKind.GraphicDesign);
        hidden.Steps.Should().ContainSingle().Which.StepType.Should().Be(StepType.DesignGraphic);

        EditImageGraphicRequest row = await f.Db.EditImageGraphicRequests.SingleAsync();
        row.ExecutionId.Should().Be(job.ExecutionId);
        row.Sized.Should().BeTrue();
        WorkflowExecution run = await f.Db.WorkflowExecutions.SingleAsync();
        (run.Id, run.WorkflowDefinitionId, run.Status, run.Initiator).Should().Be((row.ExecutionId, hidden.Id, ExecutionStatus.Queued, ExecutionInitiator.Human));

        // A second request reuses the hidden workflow.
        (await f.Request(new CreateImageGraphicRequest("A blue arrow"))).StatusCode.Should().Be(201);
        (await f.Db.WorkflowDefinitions.CountAsync()).Should().Be(1);
    }

    [Theory]
    [InlineData("", null, null, null)]
    [InlineData("Badge", "sideways", null, null)]
    [InlineData("Badge", null, 400, null)]
    [InlineData("Badge", null, 10, 10)]
    public async Task Invalid_requests_are_400_and_write_nothing(string description, string? placement, int? width, int? height)
    {
        using Fixture f = new();

        GraphicDesignRequestResult result = await f.Request(new CreateImageGraphicRequest(description, placement, width, height));

        result.StatusCode.Should().Be(400);
        (await f.Db.EditImageGraphicRequests.CountAsync()).Should().Be(0);
        (await f.Db.WorkflowExecutions.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_document_of_another_project_is_404()
    {
        using Fixture f = new();

        (await f.Request(new CreateImageGraphicRequest("Badge"), Guid.NewGuid())).StatusCode.Should().Be(404);
        (await f.Service.ListAsync(f.Project, Guid.NewGuid(), null, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Only_a_designed_layer_can_be_changed()
    {
        using Fixture f = new();

        GraphicDesignRequestResult result = await f.Request(new CreateImageGraphicRequest("Bolder", RevisesLayerId: f.Source.Id));

        result.StatusCode.Should().Be(409);
        result.Code.Should().Be(GraphicDesignService.NotRevisableCode);
    }

    [Fact]
    public async Task The_rolling_hour_limit_answers_429_with_a_retry_after()
    {
        using Fixture f = new(maxPerHour: 1);
        (await f.Request(new CreateImageGraphicRequest("One"))).StatusCode.Should().Be(201);

        GraphicDesignRequestResult second = await f.Request(new CreateImageGraphicRequest("Two"));

        second.StatusCode.Should().Be(429);
        second.Code.Should().Be(GraphicDesignService.RateLimitedCode);
        second.Extra!["retryAfterSeconds"].Should().NotBeNull();
        second.Extra["used"].Should().Be(1);
        second.Extra["limit"].Should().Be(1);
        second.Extra["windowMinutes"].Should().Be(60);
        second.Message.Should().MatchRegex(@"^Graphic limit reached — 1 of 1 graphics in this project in the last hour; try again in \d+ minutes?\.$",
            "QA-P0-15: the sentence names the limit and the wait, like the run limit does");
    }

    [Fact]
    public async Task No_seeded_designer_is_503_and_unavailable_in_the_list()
    {
        using Fixture f = new(seedAgent: false);

        (await f.Request(new CreateImageGraphicRequest("Badge"))).StatusCode.Should().Be(503);
        ImageGraphicsResponse list = (await f.Service.ListAsync(f.Project, f.Document.Id, null, CancellationToken.None))!;
        list.Available.Should().BeFalse();
        list.UnavailableReason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task An_unaffordable_graphic_is_402_and_nothing_is_queued()
    {
        using Fixture f = new(estimator: new BrokeEstimator());

        GraphicDesignRequestResult result = await f.Request(new CreateImageGraphicRequest("Badge"));

        result.StatusCode.Should().Be(402);
        result.Code.Should().Be("INSUFFICIENT_CREDITS");
        result.Message.Should().Contain("more are needed").And.Contain("returned when the run finishes");
        result.Extra.Should().ContainKey("missing");
        ((double)result.Extra["missing"]!).Should().BeGreaterThan(0);
        (await f.Db.EditImageGraphicRequests.CountAsync()).Should().Be(0);
        (await f.Db.WorkflowExecutions.CountAsync()).Should().Be(0);
        ImageGraphicsResponse list = (await f.Service.ListAsync(f.Project, f.Document.Id, f.Project.OrganizationId, CancellationToken.None))!;
        list.Estimate!.Affordable.Should().BeFalse();
        list.Estimate.HighCredits.Should().Be(300);
    }

    // ------------------------------------------------------------------ settling

    [Fact]
    public async Task A_finished_graphic_becomes_one_layer_exactly_where_it_was_drawn()
    {
        using Fixture f = new();
        ImageGraphicJobDto job = (await f.Request(new CreateImageGraphicRequest("A red SALE badge", "topRight"))).Created!;
        await f.FinishAsync(job.Id, width: 204, height: 104, cropX: 898, cropY: 38);

        ImageGraphicsResponse list = (await f.Service.ListAsync(f.Project, f.Document.Id, null, CancellationToken.None))!;
        ImageGraphicJobDto settled = list.Graphics.Single();

        settled.Status.Should().Be("Passed");
        settled.Layer.Should().NotBeNull();
        EditImageLayer layer = await f.Db.EditImageLayers.AsNoTracking().SingleAsync(l => l.Id == settled.LayerId);
        layer.Kind.Should().Be(EditImageLayerKind.Generated);
        (layer.Width, layer.Height).Should().Be((204, 104));
        layer.ZOrder.Should().Be(2, "a new graphic goes on top");
        EditImageDocumentSupport.ReadTransform(layer.TransformJson).Should().Be(new EditImageLayerTransformDto(898, 38, 1));
        using JsonDocument provenance = JsonDocument.Parse(layer.ProvenanceJson!);
        provenance.RootElement.GetProperty("operation").GetString().Should().Be("designGraphic");
        provenance.RootElement.GetProperty("prompt").GetString().Should().Be("A red SALE badge");
        (await f.Db.EditImageDocuments.AsNoTracking().SingleAsync()).Revision.Should().Be(2);

        // Settling twice inserts nothing more.
        await f.Service.ListAsync(f.Project, f.Document.Id, null, CancellationToken.None);
        await f.Service.GetAsync(f.Project, f.Document.Id, job.Id, CancellationToken.None);
        (await f.Db.EditImageLayers.CountAsync()).Should().Be(2);
    }

    [Theory]
    [InlineData(-2, false)]
    [InlineData(2, true)]
    public async Task A_graphic_whose_run_started_before_the_output_marker_is_not_made_a_layer_now(int startHoursFromMarker, bool settles)
    {
        using Fixture f = new();
        DateTime marker = DateTime.UtcNow.AddDays(-1);
        (await f.Db.Projects.SingleAsync()).CsamOutputHoldAt = marker;
        ImageGraphicJobDto job = (await f.Request(new CreateImageGraphicRequest("A red SALE badge", "topRight"))).Created!;
        await f.FinishAsync(job.Id, width: 20, height: 10, cropX: 0, cropY: 0);

        // The run began (and recorded its result) either before or well after the marker; it is settled only now.
        DateTime start = marker.AddHours(startHoursFromMarker);
        EditImageGraphicRequest row = await f.Db.EditImageGraphicRequests.SingleAsync();
        row.ResultRecordedAt = start.AddMinutes(1);
        WorkflowExecution run = await f.Db.WorkflowExecutions.SingleAsync(e => e.Id == row.ExecutionId);
        run.StartedAt = start;
        run.FirstStartedAt = start;
        await f.Db.SaveChangesAsync();
        f.Db.ChangeTracker.Clear();

        int settled = await f.Service.SettleAsync(f.Project, f.Document.Id, CancellationToken.None);

        settled.Should().Be(settles ? 1 : 0);
        (await f.Db.EditImageLayers.CountAsync()).Should().Be(settles ? 2 : 1);
    }

    [Fact]
    public async Task A_run_that_has_not_passed_or_a_foreign_key_is_never_settled()
    {
        using Fixture f = new();
        ImageGraphicJobDto running = (await f.Request(new CreateImageGraphicRequest("One"))).Created!;
        await f.FinishAsync(running.Id, 10, 10, 0, 0, ExecutionStatus.Running);
        ImageGraphicJobDto foreign = (await f.Request(new CreateImageGraphicRequest("Two"))).Created!;
        await f.FinishAsync(foreign.Id, 10, 10, 0, 0, key: GraphicDesignRules.GraphicKey(Guid.NewGuid(), f.Document.Id, Guid.NewGuid()));

        int settled = await f.Service.SettleAsync(f.Project, f.Document.Id, CancellationToken.None);

        settled.Should().Be(0);
        (await f.Db.EditImageLayers.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Changing_a_graphic_puts_the_new_one_where_the_old_one_now_is_and_hides_the_old_one()
    {
        using Fixture f = new();
        ImageGraphicJobDto first = (await f.Request(new CreateImageGraphicRequest("Badge", "topLeft"))).Created!;
        CompositionWorkspace workspace = new() { Id = Guid.NewGuid(), ProjectId = f.Project.Id, OrganizationId = f.Project.OrganizationId, OriginExecutionId = first.ExecutionId, LatestVersionNumber = 1 };
        CompositionWorkspaceVersion version = new()
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspace.Id,
            ProjectId = f.Project.Id,
            VersionNumber = 1,
            Source = CompositionVersionSource.GraphicDesign,
            Width = 1280,
            Height = 720,
            CompositionId = "Graphic",
            StorageKey = "x",
            Sha256 = new string('a', 64)
        };
        f.Db.CompositionWorkspaces.Add(workspace);
        f.Db.CompositionWorkspaceVersions.Add(version);
        await f.Db.SaveChangesAsync();
        await f.FinishAsync(first.Id, 100, 50, cropX: 60, cropY: 40, versionId: version.Id);
        Guid oldLayerId = (await f.Service.GetAsync(f.Project, f.Document.Id, first.Id, CancellationToken.None))!.LayerId!.Value;

        // The person moves and enlarges the badge.
        EditImageLayer old = await f.Db.EditImageLayers.SingleAsync(l => l.Id == oldLayerId);
        old.TransformJson = EditImageDocumentSupport.WriteTransform(new EditImageLayerTransformDto(500, 300, 2));
        await f.Db.SaveChangesAsync();
        f.Db.ChangeTracker.Clear();

        GraphicDesignRequestResult revision = await f.Request(new CreateImageGraphicRequest("Make it bolder", RevisesLayerId: oldLayerId));
        revision.StatusCode.Should().Be(201, revision.Message);
        EditImageGraphicRequest row = await f.Db.EditImageGraphicRequests.AsNoTracking().SingleAsync(r => r.Id == revision.Created!.Id);
        row.BaseVersionId.Should().Be(version.Id);
        (row.FrameWidth, row.FrameHeight).Should().Be((1280, 720));

        await f.FinishAsync(revision.Created!.Id, 120, 60, cropX: 50, cropY: 30);
        ImageGraphicJobDto done = (await f.Service.GetAsync(f.Project, f.Document.Id, revision.Created.Id, CancellationToken.None))!;

        EditImageLayer replacement = await f.Db.EditImageLayers.AsNoTracking().SingleAsync(l => l.Id == done.LayerId);
        EditImageDocumentSupport.ReadTransform(replacement.TransformJson).Should().Be(
            new EditImageLayerTransformDto(500 + ((50 - 60) * 2), 300 + ((30 - 40) * 2), 2));
        (await f.Db.EditImageLayers.AsNoTracking().SingleAsync(l => l.Id == oldLayerId)).Visible.Should().BeFalse();
        replacement.ZOrder.Should().Be((await f.Db.EditImageLayers.AsNoTracking().SingleAsync(l => l.Id == oldLayerId)).ZOrder + 1,
            "the new graphic sits directly above the one it replaces");
    }

    private static async Task<(Guid OldLayerId, GraphicDesignRequestResult Revision)> SettledThenRevisedAsync(Fixture f)
    {
        ImageGraphicJobDto first = (await f.Request(new CreateImageGraphicRequest("Badge", "topLeft"))).Created!;
        CompositionWorkspace workspace = new() { Id = Guid.NewGuid(), ProjectId = f.Project.Id, OrganizationId = f.Project.OrganizationId, OriginExecutionId = first.ExecutionId, LatestVersionNumber = 1 };
        CompositionWorkspaceVersion version = new()
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspace.Id,
            ProjectId = f.Project.Id,
            VersionNumber = 1,
            Source = CompositionVersionSource.GraphicDesign,
            Width = 1280,
            Height = 720,
            CompositionId = "Graphic",
            StorageKey = "x",
            Sha256 = new string('a', 64)
        };
        f.Db.CompositionWorkspaces.Add(workspace);
        f.Db.CompositionWorkspaceVersions.Add(version);
        await f.Db.SaveChangesAsync();
        await f.FinishAsync(first.Id, 100, 50, cropX: 60, cropY: 40, versionId: version.Id);
        Guid oldLayerId = (await f.Service.GetAsync(f.Project, f.Document.Id, first.Id, CancellationToken.None))!.LayerId!.Value;
        f.Db.ChangeTracker.Clear();
        GraphicDesignRequestResult revision = await f.Request(new CreateImageGraphicRequest("Make it bolder", RevisesLayerId: oldLayerId));
        revision.StatusCode.Should().Be(201, revision.Message);
        return (oldLayerId, revision);
    }

    [Fact]
    public async Task A_change_whose_layer_was_deleted_before_it_landed_is_kept_hidden_and_reported_for_review()
    {
        using Fixture f = new();
        (Guid oldLayerId, GraphicDesignRequestResult revision) = await SettledThenRevisedAsync(f);

        EditImageLayer old = await f.Db.EditImageLayers.SingleAsync(l => l.Id == oldLayerId);
        f.Db.EditImageLayers.Remove(old);
        await f.Db.SaveChangesAsync();
        f.Db.ChangeTracker.Clear();

        await f.FinishAsync(revision.Created!.Id, 120, 60, cropX: 50, cropY: 30);
        ImageGraphicJobDto done = (await f.Service.GetAsync(f.Project, f.Document.Id, revision.Created.Id, CancellationToken.None))!;

        done.LayerId.Should().NotBeNull();
        done.Layer!.Visible.Should().BeFalse("the paid result is kept, but never silently shown over a layer that is gone");
        done.NeedsReview.Should().Be(new ImageNeedsReviewDto("layerGone", done.LayerId!.Value));
        (await f.Service.ListAsync(f.Project, f.Document.Id, null, CancellationToken.None))!
            .Graphics.Single(j => j.Id == done.Id).NeedsReview.Should().NotBeNull();
    }

    [Fact]
    public async Task A_change_whose_layer_was_moved_meanwhile_takes_the_fresh_transform_and_needs_no_review()
    {
        using Fixture f = new();
        (Guid oldLayerId, GraphicDesignRequestResult revision) = await SettledThenRevisedAsync(f);

        EditImageLayer old = await f.Db.EditImageLayers.SingleAsync(l => l.Id == oldLayerId);
        old.TransformJson = EditImageDocumentSupport.WriteTransform(new EditImageLayerTransformDto(700, 400, 2));
        await f.Db.SaveChangesAsync();
        f.Db.ChangeTracker.Clear();

        await f.FinishAsync(revision.Created!.Id, 120, 60, cropX: 60, cropY: 40);
        ImageGraphicJobDto done = (await f.Service.GetAsync(f.Project, f.Document.Id, revision.Created.Id, CancellationToken.None))!;

        done.Layer!.Transform.X.Should().Be(700);
        done.Layer.Visible.Should().BeTrue();
        done.NeedsReview.Should().BeNull();
    }

    [Fact]
    public async Task A_graphic_lineage_cannot_be_revised_as_a_film()
    {
        using Fixture f = new();
        ImageGraphicJobDto job = (await f.Request(new CreateImageGraphicRequest("Badge"))).Created!;
        CompositionWorkspace workspace = new() { Id = Guid.NewGuid(), ProjectId = f.Project.Id, OrganizationId = f.Project.OrganizationId, OriginExecutionId = job.ExecutionId, LatestVersionNumber = 1 };
        f.Db.CompositionWorkspaces.Add(workspace);
        f.Db.CompositionWorkspaceVersions.Add(new CompositionWorkspaceVersion
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspace.Id,
            ProjectId = f.Project.Id,
            VersionNumber = 1,
            Source = CompositionVersionSource.GraphicDesign,
            StorageKey = "x",
            Sha256 = new string('a', 64),
            CreatedByExecutionId = job.ExecutionId
        });
        WorkflowExecution run = await f.Db.WorkflowExecutions.SingleAsync();
        run.Status = ExecutionStatus.Passed;
        await f.Db.SaveChangesAsync();

        CompositionRevisionService revisions = new(
            f.Db, new WorkflowExecutionService(f.Db, Mock.Of<IPublishEndpoint>(), NullLogger<WorkflowExecutionService>.Instance),
            new ConfigurationBuilder().Build(), NullLogger<CompositionRevisionService>.Instance);
        CompositionRevisionHistoryResponse history = (await revisions.GetHistoryAsync(f.Project, job.ExecutionId, null, CancellationToken.None))!;

        history.Availability.Should().Be(CompositionRevisionService.AvailabilityUnavailable);
        history.CanRevise.Should().BeFalse();
    }

    // ------------------------------------------------------------------ the controller's tenancy and gates

    private static EditImageGraphicsController Controller(Fixture f, ICurrentUser user)
    {
        EditImageGraphicsController controller = new(TestTenancy.Access(f.Db, user), f.Service, user)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        return controller;
    }

    [Fact]
    public async Task Another_organization_gets_404_on_every_action_and_nothing_is_queued()
    {
        using Fixture f = new();
        Caller stranger = new(Guid.NewGuid());
        EditImageGraphicsController controller = Controller(f, stranger);

        (await controller.List(f.Project.Id, f.Document.Id, CancellationToken.None)).Result.Should().BeOfType<NotFoundResult>();
        (await controller.Get(f.Project.Id, f.Document.Id, Guid.NewGuid(), CancellationToken.None)).Result.Should().BeOfType<NotFoundResult>();
        (await controller.Create(f.Project.Id, f.Document.Id, new CreateImageGraphicRequest("Badge"), CancellationToken.None)).Result.Should().BeOfType<NotFoundResult>();
        (await f.Db.EditImageGraphicRequests.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task An_unverified_email_is_403_before_anything_else()
    {
        using Fixture f = new();
        ActionResult<ImageGraphicJobDto> result = await Controller(f, new Caller(f.User, verified: false))
            .Create(f.Project.Id, f.Document.Id, new CreateImageGraphicRequest("Badge"), CancellationToken.None);

        ((ObjectResult)result.Result!).StatusCode.Should().Be(403);
        (await f.Db.EditImageGraphicRequests.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task The_owner_gets_201_and_then_sees_the_job_in_the_list()
    {
        using Fixture f = new();
        EditImageGraphicsController controller = Controller(f, new Caller(f.User));

        ActionResult<ImageGraphicJobDto> created = await controller.Create(f.Project.Id, f.Document.Id, new CreateImageGraphicRequest("Badge"), CancellationToken.None);
        ((ObjectResult)created.Result!).StatusCode.Should().Be(201);

        ImageGraphicsResponse list = (ImageGraphicsResponse)((OkObjectResult)(await controller.List(f.Project.Id, f.Document.Id, CancellationToken.None)).Result!).Value!;
        list.Graphics.Should().ContainSingle().Which.Description.Should().Be("Badge");
        list.Available.Should().BeTrue();
    }
}
