using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Controllers.Moderation;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Moderation;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Moderation;

/// <summary>
/// The platform-admin review-only queue: non-admins are forbidden on every route, the list is the
/// pending band newest-first, and acknowledging flips only the audit row's review columns.
/// </summary>
public sealed class ContentModerationReviewQueueTests
{
    private static readonly Guid OrgId = Guid.NewGuid();

    private sealed class Admin(bool isAdmin) : ICurrentUser
    {
        public Guid UserId { get; } = Guid.NewGuid();
        public string Email => "admin@example.com";
        public bool IsAuthenticated => true;
        public bool IsPlatformAdmin => isAdmin;
        public Guid? OrganizationId => null;
        public OrganizationRole? OrgRole => null;
    }

    private static InferenceApiDbContext NewDb() =>
        new(new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static ContentModerationReviewController Controller(
        InferenceApiDbContext db,
        ICurrentUser user,
        ContentModerationOptions? options = null,
        TimeProvider? time = null) =>
        new(db, user, Options.Create(options ?? new ContentModerationOptions()), time);

    private static ContentModerationDecision Decision(
        ModerationReviewState state,
        DateTime? createdAt = null,
        Guid? projectId = null,
        string action = nameof(ModerationAction.Review)) => new()
        {
            Id = Guid.NewGuid(),
            OrganizationId = OrgId,
            ProjectId = projectId,
            Surface = nameof(ModerationSurface.GenerationPrompt),
            Action = action,
            ContentKind = nameof(ModerationContentKind.Text),
            ContentSha256 = new string('a', 64),
            ContentLength = 12,
            ProviderKind = nameof(InferenceProviderKind.OpenAICompatible),
            Model = "omni-moderation-latest",
            ModelSnapshot = "omni-moderation-2024-09-26",
            TopCategory = "harassment",
            TopScore = 0.61,
            FlaggedCategoriesJson = "{}",
            ScoresJson = "{\"harassment\":0.61}",
            InputTypesJson = "[\"text\"]",
            ReasonCode = "harassment",
            ReasonMessage = "This content could not be published as written.",
            ReviewState = state.ToString(),
            CreatedAt = createdAt ?? new DateTime(2026, 10, 22, 9, 0, 0, DateTimeKind.Utc),
            ExpiresAt = (createdAt ?? new DateTime(2026, 10, 22, 9, 0, 0, DateTimeKind.Utc)).AddDays(30)
        };

    [Fact]
    public async Task Non_admin_is_forbidden_on_every_route()
    {
        using InferenceApiDbContext db = NewDb();
        ContentModerationReviewController controller = Controller(db, new Admin(isAdmin: false));

        (await controller.GetReviewQueue(null, null, CancellationToken.None)).Result
            .Should().BeOfType<ForbidResult>();
        (await controller.Acknowledge(Guid.NewGuid(), CancellationToken.None))
            .Should().BeOfType<ForbidResult>();
        controller.GetSettings().Result.Should().BeOfType<ForbidResult>();

        (await db.ContentModerationDecisions.CountAsync()).Should().Be(0,
            "a forbidden caller must not reach the database");
    }

    [Fact]
    public async Task Pending_queue_returns_shape_with_project_name_newest_first()
    {
        using InferenceApiDbContext db = NewDb();
        Guid projectId = Guid.NewGuid();
        db.Projects.Add(new Project
        {
            Id = projectId,
            Name = "Launch video",
            OwnerId = Guid.NewGuid(),
            OrganizationId = OrgId
        });
        ContentModerationDecision older = Decision(
            ModerationReviewState.Pending, new DateTime(2026, 10, 22, 8, 0, 0, DateTimeKind.Utc), projectId);
        ContentModerationDecision newer = Decision(
            ModerationReviewState.Pending, new DateTime(2026, 10, 22, 10, 0, 0, DateTimeKind.Utc), projectId);
        ContentModerationDecision notRequired = Decision(
            ModerationReviewState.NotRequired, new DateTime(2026, 10, 22, 11, 0, 0, DateTimeKind.Utc), projectId,
            action: nameof(ModerationAction.Allow));
        db.ContentModerationDecisions.AddRange(older, newer, notRequired);
        await db.SaveChangesAsync();

        ActionResult<ModerationReviewQueueResponse> result =
            await Controller(db, new Admin(isAdmin: true)).GetReviewQueue("pending", 100, CancellationToken.None);

        ModerationReviewQueueResponse page = result.Result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<ModerationReviewQueueResponse>().Subject;
        page.Total.Should().Be(2);
        page.Limit.Should().Be(100);
        page.Items.Should().HaveCount(2);
        page.Items.Select(i => i.Id).Should().ContainInOrder(newer.Id, older.Id);

        ModerationReviewQueueItemResponse item = page.Items[0];
        item.Surface.Should().Be(nameof(ModerationSurface.GenerationPrompt));
        item.Action.Should().Be(nameof(ModerationAction.Review));
        item.ContentKind.Should().Be(nameof(ModerationContentKind.Text));
        item.TopCategory.Should().Be("harassment");
        item.TopScore.Should().Be(0.61);
        item.ReasonCode.Should().Be("harassment");
        item.ReviewState.Should().Be(nameof(ModerationReviewState.Pending));
        item.ProjectId.Should().Be(projectId);
        item.ProjectName.Should().Be("Launch video");
        item.CreatedAt.Should().Be(newer.CreatedAt);
        item.ExpiresAt.Should().Be(newer.ExpiresAt);
    }

    [Fact]
    public async Task State_all_includes_every_row_and_an_unknown_state_is_rejected()
    {
        using InferenceApiDbContext db = NewDb();
        db.ContentModerationDecisions.AddRange(
            Decision(ModerationReviewState.Pending),
            Decision(ModerationReviewState.NotRequired, action: nameof(ModerationAction.Allow)));
        await db.SaveChangesAsync();
        ContentModerationReviewController controller = Controller(db, new Admin(isAdmin: true));

        ModerationReviewQueueResponse all = (await controller.GetReviewQueue("all", 100, CancellationToken.None))
            .Result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<ModerationReviewQueueResponse>().Subject;
        all.Total.Should().Be(2);

        (await controller.GetReviewQueue("nonsense", 100, CancellationToken.None)).Result
            .Should().BeOfType<BadRequestObjectResult>();
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(5000, 500)]
    [InlineData(7, 7)]
    public async Task Limit_is_clamped_into_one_to_five_hundred(int requested, int expected)
    {
        using InferenceApiDbContext db = NewDb();
        ModerationReviewQueueResponse page =
            (await Controller(db, new Admin(isAdmin: true)).GetReviewQueue(null, requested, CancellationToken.None))
            .Result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<ModerationReviewQueueResponse>().Subject;

        page.Limit.Should().Be(expected);
    }

    [Fact]
    public async Task Acknowledge_is_idempotent_and_changes_only_the_audit_row()
    {
        using InferenceApiDbContext db = NewDb();
        Guid projectId = Guid.NewGuid();
        Project project = new()
        {
            Id = projectId,
            Name = "Launch video",
            OwnerId = Guid.NewGuid(),
            OrganizationId = OrgId,
            UpdatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };
        ProjectFile file = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            OriginalFileName = "clip.mp4",
            StorageKey = "projects/x/clip.mp4",
            MimeType = "video/mp4",
            UploadedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };
        WorkflowExecution execution = new()
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = Guid.NewGuid(),
            ProjectId = projectId,
            OrganizationId = OrgId,
            StartedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };
        ContentModerationDecision pending = Decision(ModerationReviewState.Pending, projectId: projectId);
        db.Projects.Add(project);
        db.ProjectFiles.Add(file);
        db.WorkflowExecutions.Add(execution);
        db.ContentModerationDecisions.Add(pending);
        await db.SaveChangesAsync();

        Admin admin = new(isAdmin: true);
        ContentModerationReviewController controller = Controller(db, admin);

        (await controller.Acknowledge(pending.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();

        db.ChangeTracker.Clear();
        ContentModerationDecision acknowledged = await db.ContentModerationDecisions.AsNoTracking()
            .SingleAsync(d => d.Id == pending.Id);
        acknowledged.ReviewState.Should().Be(nameof(ModerationReviewState.Acknowledged));
        acknowledged.ReviewedByUserId.Should().Be(admin.UserId);
        acknowledged.ReviewedAt.Should().NotBeNull();
        DateTime firstReviewedAt = acknowledged.ReviewedAt!.Value;

        // Second acknowledgement: still OK, and the audit timestamp is not rewritten.
        (await controller.Acknowledge(pending.Id, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        db.ChangeTracker.Clear();
        ContentModerationDecision again = await db.ContentModerationDecisions.AsNoTracking()
            .SingleAsync(d => d.Id == pending.Id);
        again.ReviewState.Should().Be(nameof(ModerationReviewState.Acknowledged));
        again.ReviewedAt.Should().Be(firstReviewedAt);

        Project storedProject = await db.Projects.AsNoTracking().SingleAsync(p => p.Id == projectId);
        storedProject.UpdatedAt.Should().Be(project.UpdatedAt);
        storedProject.Name.Should().Be("Launch video");

        ProjectFile storedFile = await db.ProjectFiles.AsNoTracking().SingleAsync(f => f.Id == file.Id);
        storedFile.UploadedAt.Should().Be(file.UploadedAt);
        storedFile.StorageKey.Should().Be("projects/x/clip.mp4");
        storedFile.SummaryStatus.Should().Be(SummaryStatus.Pending);
        storedFile.IndexingStatus.Should().Be(FileIndexingStatus.NotIndexed);

        WorkflowExecution storedExecution = await db.WorkflowExecutions.AsNoTracking().SingleAsync(e => e.Id == execution.Id);
        storedExecution.Status.Should().Be(ExecutionStatus.Queued);
        storedExecution.StartedAt.Should().Be(execution.StartedAt);
        storedExecution.ErrorMessage.Should().BeNull();

        (await db.ContentModerationDecisions.CountAsync()).Should().Be(1, "acknowledgement never creates rows");
    }

    [Fact]
    public async Task Acknowledge_of_an_unknown_id_is_a_404()
    {
        using InferenceApiDbContext db = NewDb();

        (await Controller(db, new Admin(isAdmin: true)).Acknowledge(Guid.NewGuid(), CancellationToken.None))
            .Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public void Settings_returns_the_configured_values_read_only()
    {
        var options = new ContentModerationOptions
        {
            Enabled = true,
            Model = "omni-moderation-2024-09-26",
            BlockThreshold = 0.9,
            ReviewThreshold = 0.4,
            HardBlockCategories = ["sexual/minors"],
            TimeoutSeconds = 6,
            AuditRetentionDays = 14,
            FailClosedAtPublish = false
        };

        using InferenceApiDbContext db = NewDb();
        ActionResult<ModerationSettingsResponse> result =
            Controller(db, new Admin(isAdmin: true), options).GetSettings();

        ModerationSettingsResponse settings = result.Result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<ModerationSettingsResponse>().Subject;
        settings.Enabled.Should().BeTrue();
        settings.Model.Should().Be("omni-moderation-2024-09-26");
        settings.BlockThreshold.Should().Be(0.9);
        settings.ReviewThreshold.Should().Be(0.4);
        settings.HardBlockCategories.Should().Equal("sexual/minors");
        settings.TimeoutSeconds.Should().Be(6);
        settings.AuditRetentionDays.Should().Be(14);
        settings.FailClosedAtPublish.Should().BeFalse();
    }
}
