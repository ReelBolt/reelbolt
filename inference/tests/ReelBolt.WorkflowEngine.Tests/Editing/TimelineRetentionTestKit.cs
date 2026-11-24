#nullable enable
using System;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Editing;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Editing;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.WorkflowEngine.Tests.Editing;

/// <summary>A clock a test moves by hand.</summary>
internal sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
    public DateTime Utc => _now.UtcDateTime;
}

/// <summary>Shared harness of the trash / version-history tests (in-memory db, real controller).</summary>
internal static class TimelineRetentionTestKit
{
    public static readonly JsonSerializerOptions DocumentJsonOptions = new(JsonSerializerDefaults.Web);

    public static InferenceApiDbContext NewContext(string? name = null) =>
        new(new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase(name ?? Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    public static ICurrentUser User(Guid userId, OrganizationRole role = OrganizationRole.Member)
    {
        Mock<ICurrentUser> user = new();
        user.SetupGet(u => u.UserId).Returns(userId);
        user.SetupGet(u => u.Email).Returns("retention@example.com");
        user.SetupGet(u => u.IsAuthenticated).Returns(true);
        user.SetupGet(u => u.EmailVerified).Returns(true);
        user.SetupGet(u => u.IsPlatformAdmin).Returns(false);
        user.SetupGet(u => u.OrganizationId).Returns(TestTenancy.OrgOf(userId));
        user.SetupGet(u => u.OrgRole).Returns(role);
        return user.Object;
    }

    public static EditTimelinesController Controller(
        InferenceApiDbContext db,
        Guid userId,
        TimeProvider? clock = null,
        OrganizationRole role = OrganizationRole.Member,
        EditingRetentionOptions? retention = null,
        IWorkflowExecutionService? executions = null)
    {
        ICurrentUser user = User(userId, role);
        return new EditTimelinesController(
            db,
            user,
            TestTenancy.Access(db, user),
            new Mock<IFileStorageService>().Object,
            executions ?? new Mock<IWorkflowExecutionService>().Object,
            retention: Options.Create(retention ?? new EditingRetentionOptions()),
            clock: clock);
    }

    public static async Task<(Project Project, EditTimelineEntity Row)> SeedAsync(
        InferenceApiDbContext db, int revision = 1, DateTime? updatedAt = null, string name = "Seeded")
    {
        Guid ownerId = Guid.NewGuid();
        Project project = new()
        {
            Id = Guid.NewGuid(),
            Name = "Retention",
            OwnerId = ownerId,
            OrganizationId = TestTenancy.OrgOf(ownerId),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        EditTimelineEntity row = await AddTimelineAsync(db, project, revision, updatedAt, name);
        return (project, row);
    }

    public static async Task<EditTimelineEntity> AddTimelineAsync(
        InferenceApiDbContext db, Project project, int revision = 1, DateTime? updatedAt = null, string name = "Seeded")
    {
        Guid timelineId = Guid.NewGuid();
        DateTime at = updatedAt ?? DateTime.UtcNow;
        EditTimelineEntity row = new()
        {
            Id = timelineId,
            ProjectId = project.Id,
            Name = name,
            DocumentJson = JsonSerializer.Serialize(Blank(project.Id, timelineId, name), DocumentJsonOptions),
            Revision = revision,
            CreatedAt = at,
            UpdatedAt = at
        };
        if (db.Projects.Find(project.Id) is null) db.Projects.Add(project);
        db.EditTimelines.Add(row);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return row;
    }

    public static EditTimeline Blank(Guid projectId, Guid timelineId, string name) =>
        new(
            id: timelineId,
            projectId: projectId,
            name: name,
            settings: new EditTimelineSettings(1920, 1080, new Rational(30, 1), 48000, new Rational(1, 1), AspectPolicy.Fit, 0),
            tracks:
            [
                new Track(EditTimelineSeeder.TrackId("V1"), TrackKind.Video, "Video", null, 100, false, true, false, false, null, Array.Empty<Item>()),
                new Track(EditTimelineSeeder.TrackId("A1"), TrackKind.Audio, "Audio", null, 100, false, true, false, false, null, Array.Empty<Item>())
            ],
            markers: Array.Empty<Marker>());
}
