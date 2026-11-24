using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Moq;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.DesignSystems;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.DesignSystems;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Drift;

/// <summary>
/// Drift lows on <see cref="DesignSystemService"/>: two requests racing to create the first Draft of a
/// system must both succeed, and a lock change leaves an audit line naming who made it.
/// </summary>
public sealed class DesignSystemServiceDriftSupportTests
{
    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception));
    }

    /// <summary>On the first save, a competing request commits the system (and its first version) first.</summary>
    private sealed class RaceOnFirstSave(Func<Task> competitor) : SaveChangesInterceptor
    {
        private bool _fired;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!_fired)
            {
                _fired = true;
                await competitor();
            }

            return result;
        }
    }

    private static Mock<IPlanGateService> Gate()
    {
        var gate = new Mock<IPlanGateService>();
        gate.Setup(g => g.CheckUploadAsync(It.IsAny<Guid>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PlanGateViolation?)null);
        return gate;
    }

    private static JsonObject Document(string hex = "#6d28d9") => new()
    {
        ["schema"] = DesignDocument.Schema,
        ["color"] = new JsonObject
        {
            ["primary"] = new JsonObject
            {
                ["$type"] = "color",
                ["$value"] = new JsonObject { ["hex"] = hex, ["colorSpace"] = "srgb" },
                ["$extensions"] = new JsonObject
                {
                    ["com.reelbolt"] = new JsonObject
                    {
                        ["prov"] = new JsonObject { ["method"] = "manual" },
                        ["conf"] = 1.0,
                        ["locked"] = false,
                    },
                },
            },
        },
    };

    [Fact]
    public async Task Two_requests_creating_the_first_draft_both_succeed_under_one_system()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using (SqliteCommand pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys = OFF;";
            pragma.ExecuteNonQuery();
        }

        DbContextOptions<InferenceApiDbContext> plain = new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseSqlite(connection).Options;
        using (var create = new InferenceApiDbContext(plain))
            create.Database.EnsureCreated();

        Guid ownerId = Guid.NewGuid();
        Guid projectId = Guid.NewGuid();
        Guid winnerSystemId = Guid.NewGuid();
        using (var seed = new InferenceApiDbContext(plain))
        {
            seed.Projects.Add(new Project
            {
                Id = projectId,
                Name = "P",
                OwnerId = ownerId,
                OrganizationId = Guid.NewGuid(),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        async Task CompetitorAsync()
        {
            await using var other = new InferenceApiDbContext(plain);
            other.DesignSystems.Add(new DesignSystem
            {
                Id = winnerSystemId,
                ProjectId = projectId,
                Kind = DesignSystemKinds.Design,
                Name = "P",
                CreatedByUserId = ownerId,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            other.DesignSystemVersions.Add(new DesignSystemVersion
            {
                Id = Guid.NewGuid(),
                DesignSystemId = winnerSystemId,
                VersionNumber = 1,
                State = DesignVersionState.Draft,
                Origin = DesignVersionOrigin.Manual,
                DocumentJson = "{}",
                DocumentDigest = new string('a', 64),
                CreatedByUserId = ownerId,
                CreatedAt = DateTime.UtcNow,
            });
            await other.SaveChangesAsync();
        }

        DbContextOptions<InferenceApiDbContext> racing = new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(new RaceOnFirstSave(CompetitorAsync))
            .Options;
        await using var db = new InferenceApiDbContext(racing);
        var service = new DesignSystemService(
            db, Mock.Of<IFileStorageService>(), Gate().Object, new ListLogger<DesignSystemService>());

        DesignSystemWriteResult write = await service.CreateDraftAsync(
            projectId, ownerId, DesignSystemKinds.Design, Document(), DesignVersionOrigin.Manual,
            null, null, [], null, CancellationToken.None);

        write.Status.Should().Be(DesignSystemWriteStatus.Ok, "losing the race for the system row is not an error");
        await using var check = new InferenceApiDbContext(plain);
        check.DesignSystems.Count(s => s.ProjectId == projectId && s.Kind == DesignSystemKinds.Design).Should().Be(1);
        List<DesignSystemVersion> versions = check.DesignSystemVersions
            .Where(v => v.DesignSystemId == winnerSystemId).AsEnumerable().OrderBy(v => v.VersionNumber).ToList();
        versions.Select(v => v.VersionNumber).Should().Equal(1, 2);
    }

    [Fact]
    public async Task An_approval_committing_between_the_reject_read_and_write_is_not_overwritten()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using (SqliteCommand pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys = OFF;";
            pragma.ExecuteNonQuery();
        }

        DbContextOptions<InferenceApiDbContext> plain = new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseSqlite(connection).Options;
        using (var create = new InferenceApiDbContext(plain))
            create.Database.EnsureCreated();

        Guid ownerId = Guid.NewGuid();
        Guid systemId = Guid.NewGuid();
        Guid draftId = Guid.NewGuid();
        using (var seed = new InferenceApiDbContext(plain))
        {
            seed.DesignSystems.Add(new DesignSystem
            {
                Id = systemId,
                ProjectId = Guid.NewGuid(),
                Kind = DesignSystemKinds.Design,
                Name = "P",
                CreatedByUserId = ownerId,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            seed.DesignSystemVersions.Add(new DesignSystemVersion
            {
                Id = draftId,
                DesignSystemId = systemId,
                VersionNumber = 1,
                State = DesignVersionState.Draft,
                Origin = DesignVersionOrigin.StaticExtract,
                DocumentJson = "{}",
                DocumentDigest = new string('a', 64),
                CreatedByUserId = ownerId,
                CreatedAt = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        async Task ApproveElsewhereAsync()
        {
            await using var other = new InferenceApiDbContext(plain);
            DesignSystem system = await other.DesignSystems.SingleAsync(s => s.Id == systemId);
            DesignSystemVersion version = await other.DesignSystemVersions.SingleAsync(v => v.Id == draftId);
            version.State = DesignVersionState.Approved;
            system.CurrentVersionId = draftId;
            await other.SaveChangesAsync();
        }

        DbContextOptions<InferenceApiDbContext> racing = new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(new RaceOnFirstSave(ApproveElsewhereAsync))
            .Options;
        await using (var db = new InferenceApiDbContext(racing))
        {
            await DesignSystemService.RejectStillDraftsAsync(db, [draftId], CancellationToken.None);
        }

        await using var check = new InferenceApiDbContext(plain);
        check.DesignSystemVersions.Single(v => v.Id == draftId).State.Should().Be(DesignVersionState.Approved);
        check.DesignSystems.Single(s => s.Id == systemId).CurrentVersionId.Should().Be(draftId);
    }

    [Fact]
    public async Task A_lock_change_leaves_an_audit_line_naming_the_user_and_no_paths()
    {
        await using var db = new InferenceApiDbContext(
            new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase($"ds-lock-audit-{Guid.NewGuid():N}").Options);
        Guid ownerId = Guid.NewGuid();
        Guid projectId = Guid.NewGuid();
        db.Projects.Add(new Project
        {
            Id = projectId,
            Name = "P",
            OwnerId = ownerId,
            OrganizationId = Guid.NewGuid(),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var logger = new ListLogger<DesignSystemService>();
        var service = new DesignSystemService(db, Mock.Of<IFileStorageService>(), Gate().Object, logger);
        DesignSystemWriteResult draft = await service.CreateDraftAsync(
            projectId, ownerId, DesignSystemKinds.Design, Document(), DesignVersionOrigin.Manual,
            null, null, [], null, CancellationToken.None);
        Guid lockerId = Guid.NewGuid();

        DesignSystemLockResult result = await service.ApplyLocksAsync(
            projectId, draft.Version!.Id, ["color.primary"], null, lockerId, CancellationToken.None);

        result.Status.Should().Be(DesignSystemLockStatus.Ok);
        string line = logger.Lines.Single(l => l.Contains("design_system.locks", StringComparison.Ordinal));
        line.Should().Contain(lockerId.ToString());
        line.Should().NotContain("color.primary", "no log line carries token paths");
        line.Should().NotContain("#6d28d9");
    }
}
