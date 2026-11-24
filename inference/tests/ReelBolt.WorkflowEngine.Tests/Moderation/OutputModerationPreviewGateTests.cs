#nullable enable
using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Moderation;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Editing;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Moderation;
using ReelBolt.Shared.Tenancy;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.Inference.Api.Services.Styles;
using ReelBolt.Shared.Workflows;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Moderation;

/// <summary>
/// No preview before a verdict: a render preview is refused when a source clip is an output without a
/// current Allow or Review verdict. Real controller, moderation service and planner over an in-memory
/// context; fakes for storage and the workflow queue (a queue call is recorded by throwing).
/// </summary>
public sealed class OutputModerationPreviewGateTests
{
    private static async Task<(string Result, bool Queued)> Drive(string? verdict, string? brokenDocument = null, bool estimate = false)
    {
        Guid ownerId = Guid.NewGuid();
        Guid orgId = TestTenancy.OrgOf(ownerId);
        Project project = new() { Id = Guid.NewGuid(), Name = "P", OwnerId = ownerId, OrganizationId = orgId, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        InferenceApiDbContext db = new(new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase($"qa822prev-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options);
        db.Projects.Add(project);

        string name = "out.mp4";
        ProjectFile file = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            OriginalFileName = name,
            OriginalPath = name,
            Category = "outputFiles",
            StorageFileName = name,
            StorageKey = ReelBolt.Shared.ProjectFilePath.BuildStorageKey(project.Id, "outputFiles", null, name),
            MimeType = "video/mp4",
            SizeBytes = 1000,
            CsamStatus = CsamScanStatus.Clean
        };
        db.ProjectFiles.Add(file);
        if (verdict is not null)
        {
            db.OutputModerationVerdicts.Add(new OutputModerationVerdict
            {
                Id = Guid.NewGuid(),
                OrganizationId = orgId,
                ProjectId = project.Id,
                StorageKey = file.StorageKey,
                SourceVersion = "e1:1000",
                State = verdict,
                ModelSnapshot = "m",
                ReasonMessage = "no"
            });
        }

        Guid timelineId = Guid.NewGuid();
        EditTimeline document = new(
            id: timelineId, projectId: project.Id, name: "Cut",
            settings: new EditTimelineSettings(1920, 1080, new Rational(30, 1), 48000, new Rational(1, 1), AspectPolicy.Fit, 0),
            tracks: [new Track(Guid.NewGuid(), TrackKind.Video, "V1", null, 64, false, true, false, false, null,
                [new MediaClip(Guid.NewGuid(), "Clip", 0, 300, new AssetRef(AssetRefKind.ProjectFile, ProjectFileId: file.Id))])],
            markers: []);
        db.EditTimelines.Add(new EditTimelineEntity
        {
            Id = timelineId,
            ProjectId = project.Id,
            Name = "Cut",
            Revision = 1,
            DocumentJson = brokenDocument ?? JsonSerializer.Serialize(document, EditTimelineJson.Options),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        db.AgentDefinitions.Add(new AgentDefinition { Id = Guid.NewGuid(), Name = "VideoTransform", AgentType = AgentType.VideoTransform, IsBuiltIn = true, SystemPrompt = string.Empty });
        db.CreditAccounts.Add(new CreditAccount { Id = Guid.NewGuid(), OrganizationId = orgId, BalanceMillis = 0 });
        await db.SaveChangesAsync();

        Mock<IFileStorageService> storage = new();
        storage.Setup(s => s.GetMetadataAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StoredObjectMetadata(1000, "video/mp4", "\"e1\""));
        PublishModeration moderation = new(
            new Mock<IContentModerationGate>().Object, storage.Object, new Mock<IModerationMediaSampler>().Object,
            new Mock<IInferenceProviderResolver>().Object, new Mock<ITranscriptionClientFactory>().Object, db,
            Options.Create(new ContentModerationOptions { Enabled = true }), NullLogger<PublishModeration>.Instance);

        Mock<ICurrentUser> user = new();
        user.SetupGet(u => u.UserId).Returns(ownerId);
        user.SetupGet(u => u.Email).Returns("t@example.com");
        user.SetupGet(u => u.IsAuthenticated).Returns(true);
        user.SetupGet(u => u.OrganizationId).Returns(orgId);
        user.SetupGet(u => u.OrgRole).Returns(OrganizationRole.Member);
        user.SetupGet(u => u.EmailVerified).Returns(true);
        Mock<IWorkflowExecutionService> executions = new();
        executions.Setup(e => e.QueueAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<ExecutionInitiator>(), It.IsAny<CancellationToken>(),
                It.IsAny<bool>(), It.IsAny<RunChoices?>(), It.IsAny<WorkflowDefinitionKind>(), It.IsAny<Guid?>(), It.IsAny<Func<CancellationToken, Task>?>()))
            .ThrowsAsync(new InvalidOperationException("reached the queue"));
        Mock<IWorkflowCostEstimator> estimator = new();
        estimator.SetupGet(e => e.BillingEnabled).Returns(false);
        Mock<IOrganizationEntitlements> entitlements = new();
        entitlements.Setup(e => e.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(ValueTask.FromResult(new OrganizationEntitlementSet { MaxOutputHeight = int.MaxValue }));

        EditTimelinesController controller = new(
            db, user.Object, TestTenancy.Access(db, user.Object), storage.Object, executions.Object,
            new Mock<INonWorkflowChargeService>().Object, null, entitlements.Object, null, moderation,
            costEstimator: estimator.Object);

        try
        {
            if (estimate)
            {
                ActionResult<EditTimelineRenderEstimateResponse> e = await controller.EstimateRender(
                    project.Id, timelineId, new RenderEditTimelineRequest(Preview: new PreviewRenderRequest(0)), default);
                return (e.Result is ObjectResult eo ? $"{eo.StatusCode} {JsonSerializer.Serialize(eo.Value)}" : e.Result?.GetType().Name ?? "ok", false);
            }

            ActionResult<RenderEditTimelineResponse> r = await controller.RenderPreview(project.Id, timelineId, new PreviewRenderRequest(0), default);
            string body = r.Result is ObjectResult o ? $"{o.StatusCode} {JsonSerializer.Serialize(o.Value)}" : r.Result?.GetType().Name ?? "null";
            return (body, false);
        }
        catch (InvalidOperationException ex) when (ex.Message == "reached the queue")
        {
            return ("queue reached (would be 202)", true);
        }
    }

    [Fact]
    public async Task A_preview_with_a_pending_source_is_refused_and_not_queued()
    {
        (string result, bool queued) = await Drive(verdict: null);
        queued.Should().BeFalse();
        result.Should().Contain("409").And.Contain("CONTENT_MODERATION_PENDING");
    }

    [Fact]
    public async Task A_preview_with_a_blocked_source_is_refused_with_the_block_status()
    {
        (string result, bool queued) = await Drive(verdict: "Block");
        queued.Should().BeFalse();
        result.Should().Contain("422");
    }

    [Fact]
    public async Task A_preview_with_an_allowed_source_is_queued()
    {
        (_, bool queued) = await Drive(verdict: "Allow");
        queued.Should().BeTrue();
    }

    [Fact]
    public async Task A_preview_of_a_timeline_that_cannot_be_read_fails_closed()
    {
        // BuildPreviewRequest parses the document too and rejects an unreadable one before the source check;
        // either way nothing is queued.
        (_, bool queued) = await Drive(verdict: "Allow", brokenDocument: "{ not json");
        queued.Should().BeFalse();
    }

    [Fact]
    public async Task The_preview_estimate_refuses_a_pending_source_like_the_preview_itself()
    {
        (string result, _) = await Drive(verdict: null, estimate: true);
        result.Should().Contain("409").And.Contain("CONTENT_MODERATION_PENDING");
    }

    [Fact]
    public async Task The_preview_estimate_answers_for_an_allowed_source()
    {
        (string result, _) = await Drive(verdict: "Allow", estimate: true);
        result.Should().NotContain("409");
    }
}
