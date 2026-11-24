#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Avatars;
using ReelBolt.Inference.Api.Services.Background;
using ReelBolt.Inference.Api.Services.Notifications;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Data.Models;
using Xunit;
using H = ReelBolt.WorkflowEngine.Tests.Avatars.AvatarTestHarness;

namespace ReelBolt.WorkflowEngine.Tests.Avatars;

/// <summary>
/// Lane A3 of docs/design/avatars.md: the append-only audit events (never media, names or keys), the retention sweep
/// (bounds, staging switch, revoked grace, the 7-day warning once per attestation, the 3-year event purge) and the
/// usage endpoint.
/// </summary>
public class ProjectAvatarRetentionAndEventsTests
{
    private static readonly DateTime Now = new(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc);

    // ------------------------------------------------------------------ events

    [Theory]
    [InlineData("Anna Rossi")]
    [InlineData("projects/x/userFiles/face.png")]
    [InlineData("https://example.com/face.png")]
    public void An_outcome_that_is_not_a_count_flag_or_outcome_word_is_refused(string value)
    {
        Action write = () => ProjectAvatarEvents.Serialize(new Dictionary<string, object> { ["note"] = value });
        write.Should().Throw<ArgumentException>();
        Action badKey = () => ProjectAvatarEvents.Serialize(new Dictionary<string, object> { ["Subject Name"] = 1 });
        badKey.Should().Throw<ArgumentException>();
        Action media = () => ProjectAvatarEvents.Serialize(new Dictionary<string, object> { ["bytes"] = new byte[] { 1 } });
        media.Should().Throw<ArgumentException>();
        ProjectAvatarEvents.Serialize(new Dictionary<string, object> { ["outcome"] = "vendor_refused", ["count"] = 2, ["ok"] = true })
            .Should().Be("{\"count\":2,\"ok\":true,\"outcome\":\"vendor_refused\"}");
    }

    [Fact]
    public async Task Create_reattest_revoke_and_delete_each_write_one_event_that_holds_no_name_file_or_key()
    {
        var h = new H();
        ProjectFile face = h.AddImage("anna-face.png", salt: 1);
        Guid avatarId = await h.CreateReadyAsync("Anna", face);
        ProjectFile second = h.AddImage("anna-2.png", salt: 2);
        H.Read(await h.Controller().ReplaceReferences(h.Project.Id, avatarId, ProjectAvatarsControllerTests.Reattest(face, second), CancellationToken.None)).Status.Should().Be(200);
        H.Read(await h.Controller().RevokeConsent(h.Project.Id, avatarId, CancellationToken.None)).Status.Should().Be(200);
        H.Read(await h.Controller().RevokeConsent(h.Project.Id, avatarId, CancellationToken.None)).Status.Should().Be(200);
        using (InferenceApiDbContext db = h.NewDb())
        {
            db.ProjectAvatarVendorAssets.Add(new ProjectAvatarVendorAsset { Id = Guid.NewGuid(), AvatarId = avatarId, RemoteAssetId = "mm_file://secret" });
            await db.SaveChangesAsync();
        }

        H.Read(await h.Controller().Delete(h.Project.Id, avatarId, CancellationToken.None)).Status.Should().Be(200);

        using InferenceApiDbContext read = h.NewDb();
        List<ProjectAvatarEvent> events = await read.ProjectAvatarEvents.OrderBy(e => e.OccurredAt).ToListAsync();
        events.Select(e => e.Kind).Should().Equal(
            AvatarEventKind.Created, AvatarEventKind.ReferencesChanged, AvatarEventKind.ConsentRevoked,
            AvatarEventKind.VendorAssetDeleted, AvatarEventKind.Deleted);
        events.Should().OnlyContain(e => e.AvatarId == avatarId && e.ProjectId == h.Project.Id && e.OrganizationId == h.OrganizationId
                                         && e.ActorUserId == h.OwnerId && e.ConsentStatementVersion == "avatar-consent-2026-12");
        string all = string.Join("\n", events.Select(e => e.OutcomesJson));
        foreach (string leak in new[] { "Anna Rossi", "anna-face", "anna-2", "userFiles", "projects/", "mm_file", face.Id.ToString(), "http" })
            all.Should().NotContain(leak);

        Outcomes(events[0]).GetProperty("images").GetInt32().Should().Be(1);
        Outcomes(events[0]).GetProperty("via").GetString().Should().Be("app");
        Outcomes(events[1]).GetProperty("added").GetInt32().Should().Be(1);
        Outcomes(events[2]).GetProperty("via").GetString().Should().Be("app", "the privacy@ runbook writes via=privacy_request");
        Outcomes(events[3]).GetProperty("outcome").GetString().Should().Be("deleted");
        Outcomes(events[4]).GetProperty("cause").GetString().Should().Be("user");
        Outcomes(events[4]).GetProperty("references_deleted").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task A_project_delete_leaves_a_deleted_event_per_avatar()
    {
        var h = new H();
        await h.CreateReadyAsync("Anna");
        await h.CreateReadyAsync("Bea");
        using InferenceApiDbContext db = h.NewDb();
        Project project = await db.Projects.SingleAsync();

        await ProjectAvatarEvents.AddForProjectDeleteAsync(db, project, h.OwnerId, CancellationToken.None);
        await db.SaveChangesAsync();

        List<ProjectAvatarEvent> deleted = await db.ProjectAvatarEvents.Where(e => e.Kind == AvatarEventKind.Deleted).ToListAsync();
        deleted.Should().HaveCount(2);
        deleted.Should().OnlyContain(e => Outcomes(e).GetProperty("cause").GetString() == "project_deleted");
    }

    // ------------------------------------------------------------------ retention sweep

    private sealed class RecordingNotifier : IUserActivityNotifier
    {
        public List<(Guid Recipient, string Name, DateTime On)> Warnings { get; } = [];
        public Task FileCreatedAsync(Guid projectId, Guid? actorUserId, string fileName, CancellationToken ct = default) => Task.CompletedTask;
        public Task GraphicsEditedAsync(Guid projectId, Guid? actorUserId, string subject, CancellationToken ct = default) => Task.CompletedTask;
        public Task GuestReviewActivityAsync(Guid organizationId, Guid projectId, Guid recipientUserId, string timelineName, string details, CancellationToken ct = default) => Task.CompletedTask;

        public Task<bool> AvatarRetentionWarningAsync(Guid organizationId, Guid projectId, Guid recipientUserId, string avatarName, DateTime deletedOn, CancellationToken ct = default)
        {
            Warnings.Add((recipientUserId, avatarName, deletedOn));
            return Task.FromResult(true);
        }
    }

    private sealed class Sweep
    {
        public H Harness { get; } = new();
        public RecordingNotifier Notifier { get; } = new();
        public ProjectAvatarRetentionService Service { get; }

        public Sweep(bool sweepEnabled = true)
        {
            var services = new ServiceCollection();
            string name = Harness.DatabaseName;
            services.AddDbContext<InferenceApiDbContext>(o => o.UseInMemoryDatabase(name));
            services.AddSingleton<IFileStorageService>(Harness.Storage.Object);
            services.AddSingleton<IAvatarVendorAssetDeleter>(Harness.Vendor);
            services.AddSingleton<IUserActivityNotifier>(Notifier);
            services.AddSingleton<IOptions<AvatarOptions>>(Options.Create(Harness.Options));
            services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
            services.AddScoped<ProjectAvatarDeletion>();
            IServiceProvider sp = services.BuildServiceProvider();
            IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ProjectAvatars:RetentionSweepEnabled"] = sweepEnabled ? "true" : "false"
            }).Build();
            Service = new ProjectAvatarRetentionService(sp.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<ProjectAvatarRetentionService>.Instance, config);
        }

        public Guid Seed(string name, DateTime retentionUntil, DateTime? revokedAt = null, DateTime? attestedAt = null)
        {
            ProjectFile face = Harness.AddImage(name + ".png", salt: (byte)name.Length);
            using InferenceApiDbContext db = Harness.NewDb();
            ProjectAvatar avatar = AvatarSeed.Attach(db, Harness.Project.Id, name, face);
            avatar.RetentionUntil = retentionUntil;
            avatar.ConsentRevokedAt = revokedAt;
            avatar.ConsentRevokedByUserId = revokedAt is null ? null : Harness.OwnerId;
            avatar.ConsentAttestedAt = attestedAt ?? Now.AddDays(-100);
            avatar.ConsentAttestedByUserId = Harness.OwnerId;
            db.SaveChanges();
            return avatar.Id;
        }
    }

    [Fact]
    public async Task Expired_and_long_revoked_avatars_are_deleted_through_the_one_routine_with_no_actor()
    {
        var s = new Sweep();
        Guid expired = s.Seed("expired", Now.AddMinutes(-1));
        Guid revokedLong = s.Seed("revokedlong", Now.AddDays(100), revokedAt: Now.AddDays(-31));
        Guid revokedRecent = s.Seed("revokedrecent", Now.AddDays(100), revokedAt: Now.AddDays(-29));
        Guid live = s.Seed("live", Now.AddDays(100));

        AvatarRetentionSweepResult result = await s.Service.SweepOnceAsync(Now, CancellationToken.None);

        result.Deleted.Should().Be(2);
        using InferenceApiDbContext db = s.Harness.NewDb();
        (await db.ProjectAvatars.Select(a => a.Id).ToListAsync()).Should().BeEquivalentTo([revokedRecent, live]);
        List<ProjectAvatarEvent> events = await db.ProjectAvatarEvents.Where(e => e.Kind == AvatarEventKind.RetentionDeleted).ToListAsync();
        events.Select(e => e.AvatarId).Should().BeEquivalentTo([expired, revokedLong]);
        events.Should().OnlyContain(e => e.ActorUserId == null);
        events.Select(e => Outcomes(e).GetProperty("cause").GetString()).Should().BeEquivalentTo(["retention_expired", "revoked_grace_ended"]);
        s.Harness.DeletedKeys.Should().HaveCount(2, "the reference files go with the avatars");
    }

    [Fact]
    public async Task An_object_avatar_is_never_swept_and_never_warned_about()
    {
        // docs/design/avatar-objects.md: an object has no retention period and no revoke. Even a row whose columns look
        // expired, long revoked or about to expire (they are never read for an object) stays, and nobody is warned.
        var s = new Sweep();
        Guid person = s.Seed("expired", Now.AddMinutes(-1));
        Guid looksExpired = s.Seed("machine", Now.AddDays(-400), revokedAt: Now.AddDays(-400));
        Guid looksDueSoon = s.Seed("lamp", Now.AddDays(3));
        Guid realObject = s.Seed("tripod", ProjectAvatar.NoRetention);
        using (InferenceApiDbContext db = s.Harness.NewDb())
        {
            foreach (ProjectAvatar row in await db.ProjectAvatars.Where(a => a.Id != person).ToListAsync())
                row.SubjectKind = AvatarSubjectKind.Object;
            await db.SaveChangesAsync();
        }

        AvatarRetentionSweepResult result = await s.Service.SweepOnceAsync(Now, CancellationToken.None);

        result.Deleted.Should().Be(1, "only the person is past its keep-until date");
        result.Warned.Should().Be(0);
        s.Notifier.Warnings.Should().BeEmpty();
        using InferenceApiDbContext after = s.Harness.NewDb();
        (await after.ProjectAvatars.Select(a => a.Id).ToListAsync()).Should().BeEquivalentTo([looksExpired, looksDueSoon, realObject]);
    }

    [Fact]
    public async Task A_sweep_deletes_at_most_two_hundred_avatars()
    {
        var s = new Sweep();
        using (InferenceApiDbContext db = s.Harness.NewDb())
        {
            for (int i = 0; i < ProjectAvatarRetentionService.MaxPerSweep + 3; i++)
            {
                db.ProjectAvatars.Add(new ProjectAvatar
                {
                    Id = Guid.NewGuid(),
                    ProjectId = s.Harness.Project.Id,
                    Name = $"a{i}",
                    ConsentStatement = "x",
                    ConsentAttestedAt = Now.AddDays(-400),
                    ConsentAttestedByUserId = s.Harness.OwnerId,
                    RetentionUntil = Now.AddDays(-1)
                });
            }

            await db.SaveChangesAsync();
        }

        (await s.Service.SweepOnceAsync(Now, CancellationToken.None)).Deleted.Should().Be(ProjectAvatarRetentionService.MaxPerSweep);
        (await s.Service.SweepOnceAsync(Now, CancellationToken.None)).Deleted.Should().Be(3);
    }

    [Fact]
    public async Task The_staging_switch_stops_the_whole_sweep()
    {
        var s = new Sweep(sweepEnabled: false);
        s.Seed("expired", Now.AddDays(-1));

        AvatarRetentionSweepResult result = await s.Service.SweepOnceAsync(Now, CancellationToken.None);

        result.Should().Be(new AvatarRetentionSweepResult(0, 0, 0));
        using InferenceApiDbContext db = s.Harness.NewDb();
        (await db.ProjectAvatars.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task The_product_kill_switch_does_not_stop_deletion_but_does_stop_the_warning()
    {
        var s = new Sweep();
        s.Harness.Options.Enabled = false;
        s.Seed("expired", Now.AddDays(-1));
        s.Seed("soon", Now.AddDays(3));

        AvatarRetentionSweepResult result = await s.Service.SweepOnceAsync(Now, CancellationToken.None);

        result.Deleted.Should().Be(1);
        result.Warned.Should().Be(0);
        s.Notifier.Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task The_seven_day_warning_goes_once_per_attestation_to_the_attesting_member()
    {
        var s = new Sweep();
        Guid soon = s.Seed("soon", Now.AddDays(6));
        s.Seed("later", Now.AddDays(8));
        s.Seed("revoked", Now.AddDays(5), revokedAt: Now.AddDays(-1));

        (await s.Service.SweepOnceAsync(Now, CancellationToken.None)).Warned.Should().Be(1);
        (await s.Service.SweepOnceAsync(Now.AddHours(6), CancellationToken.None)).Warned.Should().Be(0, "one warning per attestation");
        s.Notifier.Warnings.Should().ContainSingle().Which.Should().Be((s.Harness.OwnerId, "soon", Now.AddDays(6)));

        // A re-attestation starts a new period: the next approach warns again.
        using (InferenceApiDbContext db = s.Harness.NewDb())
        {
            ProjectAvatar avatar = await db.ProjectAvatars.SingleAsync(a => a.Id == soon);
            avatar.ConsentAttestedAt = Now.AddHours(7);
            avatar.RetentionUntil = Now.AddDays(5);
            await db.SaveChangesAsync();
        }

        (await s.Service.SweepOnceAsync(Now.AddHours(8), CancellationToken.None)).Warned.Should().Be(1);
        using InferenceApiDbContext read = s.Harness.NewDb();
        (await read.ProjectAvatarEvents.CountAsync(e => e.Kind == AvatarEventKind.RetentionWarningSent)).Should().Be(2);
    }

    [Fact]
    public async Task Audit_events_older_than_three_years_are_purged_and_newer_ones_kept()
    {
        var s = new Sweep();
        using (InferenceApiDbContext db = s.Harness.NewDb())
        {
            db.ProjectAvatarEvents.Add(new ProjectAvatarEvent { Id = Guid.NewGuid(), AvatarId = Guid.NewGuid(), Kind = AvatarEventKind.Deleted, OccurredAt = Now.AddYears(-3).AddDays(-1) });
            db.ProjectAvatarEvents.Add(new ProjectAvatarEvent { Id = Guid.NewGuid(), AvatarId = Guid.NewGuid(), Kind = AvatarEventKind.Deleted, OccurredAt = Now.AddYears(-3).AddDays(1) });
            await db.SaveChangesAsync();
        }

        (await s.Service.SweepOnceAsync(Now, CancellationToken.None)).EventsPurged.Should().Be(1);
        using InferenceApiDbContext read = s.Harness.NewDb();
        (await read.ProjectAvatarEvents.CountAsync()).Should().Be(1);
    }

    // ------------------------------------------------------------------ usage

    [Fact]
    public async Task Usage_lists_the_ledger_takes_that_named_the_avatar_and_the_pictures_using_its_photos()
    {
        var h = new H();
        Guid avatarId = await h.CreateReadyAsync();
        Guid execution = Guid.NewGuid();
        using (InferenceApiDbContext db = h.NewDb())
        {
            db.ExternalGenerationJobs.Add(new ExternalGenerationJob
            {
                Id = Guid.NewGuid(),
                ExecutionId = execution,
                StepResultId = Guid.NewGuid(),
                ProjectId = h.Project.Id,
                ClipId = "v1t1",
                AvatarId = avatarId,
                Status = ExternalGenerationJobStatus.Succeeded,
                StorageKey = "k",
                CreatedAt = Now
            });
            db.ExternalGenerationJobs.Add(new ExternalGenerationJob
            {
                Id = Guid.NewGuid(),
                ExecutionId = Guid.NewGuid(),
                StepResultId = Guid.NewGuid(),
                ProjectId = h.Project.Id,
                ClipId = "other",
                Status = ExternalGenerationJobStatus.Succeeded,
                CreatedAt = Now
            });
            await db.SaveChangesAsync();
        }

        (int status, JsonElement body) = H.Read(await h.Controller().Usage(h.Project.Id, avatarId, CancellationToken.None));

        status.Should().Be(200);
        body.GetProperty("takeCount").GetInt32().Should().Be(1);
        JsonElement run = body.GetProperty("runs")[0];
        run.GetProperty("executionId").GetGuid().Should().Be(execution);
        run.GetProperty("takes")[0].GetProperty("clipId").GetString().Should().Be("v1t1");
        run.GetProperty("takes")[0].GetProperty("hasOutput").GetBoolean().Should().BeTrue();
        body.GetProperty("picturesUsingReferences").GetInt32().Should().Be(0);
        H.Read(await h.Controller().Usage(h.Project.Id, Guid.NewGuid(), CancellationToken.None)).Status.Should().Be(404);
    }

    private static JsonElement Outcomes(ProjectAvatarEvent e) => JsonDocument.Parse(e.OutcomesJson!).RootElement;
}
