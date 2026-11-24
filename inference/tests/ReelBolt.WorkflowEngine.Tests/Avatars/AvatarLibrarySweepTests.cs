#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
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
using ReelBolt.Inference.Api.Services.Avatars.Library;
using ReelBolt.Inference.Api.Services.Background;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Notifications;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;
using Xunit;
using H = ReelBolt.WorkflowEngine.Tests.Avatars.AvatarTestHarness;
using L = ReelBolt.WorkflowEngine.Tests.Avatars.AvatarLibraryHarness;

namespace ReelBolt.WorkflowEngine.Tests.Avatars;

/// <summary>
/// The workspace avatar library in the avatar retention sweep and in the storage accounting (lane L9 of
/// docs/design/avatar-objects.md): an item past its keep-until date, or revoked longer than the grace, is deleted with
/// its imports through the library's one delete routine; the 7-day warning goes to the organization's Owners and
/// Admins, once; an object is never swept; and the library's own copies count as the organization's storage.
/// </summary>
public class AvatarLibrarySweepTests
{
    private sealed class RecordingNotifier : IUserActivityNotifier
    {
        public List<(Guid Recipient, string Name)> ProjectWarnings { get; } = [];
        public List<(Guid Organization, Guid Recipient, string Name, DateTime On)> LibraryWarnings { get; } = [];
        public Task FileCreatedAsync(Guid projectId, Guid? actorUserId, string fileName, CancellationToken ct = default) => Task.CompletedTask;
        public Task GraphicsEditedAsync(Guid projectId, Guid? actorUserId, string subject, CancellationToken ct = default) => Task.CompletedTask;
        public Task GuestReviewActivityAsync(Guid organizationId, Guid projectId, Guid recipientUserId, string timelineName, string details, CancellationToken ct = default) => Task.CompletedTask;

        public Task<bool> AvatarRetentionWarningAsync(Guid organizationId, Guid projectId, Guid recipientUserId, string avatarName, DateTime deletedOn, CancellationToken ct = default)
        {
            ProjectWarnings.Add((recipientUserId, avatarName));
            return Task.FromResult(true);
        }

        public Task<bool> AvatarLibraryRetentionWarningAsync(Guid organizationId, Guid recipientUserId, string itemName, DateTime deletedOn, CancellationToken ct = default)
        {
            LibraryWarnings.Add((organizationId, recipientUserId, itemName, deletedOn));
            return Task.FromResult(true);
        }
    }

    private sealed class Sweep
    {
        public L Library { get; } = new();
        public RecordingNotifier Notifier { get; } = new();
        public ProjectAvatarRetentionService Service { get; }

        public Sweep()
        {
            var services = new ServiceCollection();
            string name = Library.Harness.DatabaseName;
            services.AddDbContext<InferenceApiDbContext>(o => o.UseInMemoryDatabase(name));
            services.AddSingleton<IFileStorageService>(Library.Harness.Storage.Object);
            services.AddSingleton<IAvatarVendorAssetDeleter>(Library.Harness.Vendor);
            services.AddSingleton<IUserActivityNotifier>(Notifier);
            services.AddSingleton<IOptions<AvatarOptions>>(Options.Create(Library.Harness.Options));
            services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
            services.AddSingleton<IAvatarLibraryStore>(Library.Store);
            services.AddScoped<ProjectAvatarDeletion>();
            services.AddScoped<AvatarLibraryService>();
            IServiceProvider sp = services.BuildServiceProvider();
            IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
            Service = new ProjectAvatarRetentionService(sp.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<ProjectAvatarRetentionService>.Instance, config);
        }

        /// <summary>A member of the harness organization, with a user row (the recipients query reads it).</summary>
        public Guid AddMember(OrganizationRole role, bool serviceAccount = false)
        {
            Guid id = Guid.NewGuid();
            using InferenceApiDbContext db = Library.NewDb();
            db.ApplicationUsers.Add(new ApplicationUser { Id = id, Email = id + "@example.com", DisplayName = "U", IsServiceAccount = serviceAccount });
            db.OrganizationMemberships.Add(new OrganizationMembership { OrganizationId = Library.OrganizationId, UserId = id, Role = role });
            db.SaveChanges();
            return id;
        }
    }

    [Fact]
    public async Task An_item_past_its_keep_until_date_is_deleted_with_its_imports_and_no_actor()
    {
        var s = new Sweep();
        L l = s.Library;
        (Guid itemId, Guid source) = await l.PublishPersonAsync("Anna");
        Project second = l.AddProject();
        Guid import = await l.ImportAsync(itemId, second);
        (Guid machine, _) = await l.PublishObjectAsync("Machine");

        // One day before the date nothing goes; the day after, the item, both imports and every file go.
        AvatarRetentionSweepResult early = await s.Service.SweepOnceAsync(DateTime.UtcNow.AddDays(119), CancellationToken.None);
        early.LibraryItemsDeleted.Should().Be(0);
        early.Deleted.Should().Be(0);

        AvatarRetentionSweepResult result = await s.Service.SweepOnceAsync(DateTime.UtcNow.AddDays(121), CancellationToken.None);

        result.LibraryItemsDeleted.Should().Be(1);
        result.Deleted.Should().Be(0, "the imports went with their item, through the library's delete routine");
        using InferenceApiDbContext db = l.NewDb();
        (await db.AvatarLibraryItems.Select(i => i.Id).ToListAsync()).Should().Equal([machine], "an object is never swept");
        (await db.ProjectAvatars.Where(a => a.Id == source || a.Id == import).CountAsync()).Should().Be(0);
        (await db.ProjectFiles.CountAsync(f => f.ProjectId == second.Id)).Should().Be(0);
        l.Store.Keys().Should().HaveCount(1, "only the object's picture is left in the library");
        ProjectAvatarEvent deleted = await db.ProjectAvatarEvents.SingleAsync(e => e.Kind == AvatarEventKind.LibraryDeleted);
        deleted.AvatarId.Should().Be(itemId);
        deleted.ActorUserId.Should().BeNull();
        deleted.OutcomesJson.Should().Contain("\"cause\":\"retention_expired\"").And.Contain("\"imports_deleted\":2");
        (await db.ProjectAvatarEvents.Where(e => e.Kind == AvatarEventKind.Deleted).ToListAsync())
            .Should().HaveCount(2).And.OnlyContain(e => e.ActorUserId == null && e.OutcomesJson!.Contains("library_item_deleted"));
    }

    [Fact]
    public async Task A_revoked_item_is_deleted_after_the_grace_and_not_before()
    {
        var s = new Sweep();
        L l = s.Library;
        (Guid itemId, _) = await l.PublishPersonAsync("Anna");
        H.Read(await l.Controller().RevokeConsent(itemId, CancellationToken.None)).Status.Should().Be(200);

        (await s.Service.SweepOnceAsync(DateTime.UtcNow.AddDays(29), CancellationToken.None)).LibraryItemsDeleted.Should().Be(0);
        using (InferenceApiDbContext db = l.NewDb())
            (await db.AvatarLibraryItems.CountAsync()).Should().Be(1);

        AvatarRetentionSweepResult result = await s.Service.SweepOnceAsync(DateTime.UtcNow.AddDays(31), CancellationToken.None);

        result.LibraryItemsDeleted.Should().Be(1);
        using InferenceApiDbContext read = l.NewDb();
        (await read.AvatarLibraryItems.CountAsync()).Should().Be(0);
        (await read.ProjectAvatars.CountAsync()).Should().Be(0);
        (await read.ProjectAvatarEvents.SingleAsync(e => e.Kind == AvatarEventKind.LibraryDeleted)).OutcomesJson
            .Should().Contain("\"cause\":\"revoked_grace_ended\"");
        l.Store.Keys().Should().BeEmpty();
    }

    [Fact]
    public async Task The_seven_day_warning_goes_to_the_owners_and_admins_once_and_an_import_gets_none_of_its_own()
    {
        var s = new Sweep();
        L l = s.Library;
        Guid owner = s.AddMember(OrganizationRole.Owner);
        Guid admin = s.AddMember(OrganizationRole.Admin);
        s.AddMember(OrganizationRole.Member);
        s.AddMember(OrganizationRole.Billing);
        s.AddMember(OrganizationRole.Admin, serviceAccount: true);
        (Guid itemId, _) = await l.PublishPersonAsync("Anna");
        await l.ImportAsync(itemId, l.AddProject());
        await l.PublishObjectAsync("Machine");

        (await s.Service.SweepOnceAsync(DateTime.UtcNow.AddDays(100), CancellationToken.None)).LibraryItemsWarned.Should().Be(0, "more than seven days left");
        DateTime now = DateTime.UtcNow.AddDays(117);
        AvatarRetentionSweepResult result = await s.Service.SweepOnceAsync(now, CancellationToken.None);

        result.LibraryItemsWarned.Should().Be(1);
        result.Warned.Should().Be(0, "a linked import follows its item: no warning of its own");
        s.Notifier.ProjectWarnings.Should().BeEmpty();
        s.Notifier.LibraryWarnings.Select(w => w.Recipient).Should().BeEquivalentTo([owner, admin]);
        s.Notifier.LibraryWarnings.Should().OnlyContain(w => w.Organization == l.OrganizationId && w.Name == "Anna");
        using (InferenceApiDbContext db = l.NewDb())
        {
            ProjectAvatarEvent sent = await db.ProjectAvatarEvents.SingleAsync(e => e.Kind == AvatarEventKind.LibraryRetentionWarningSent);
            sent.AvatarId.Should().Be(itemId);
            sent.OutcomesJson.Should().Be("{\"days_left\":3,\"recipients\":2}");
        }

        // Once per attestation.
        (await s.Service.SweepOnceAsync(now.AddHours(6), CancellationToken.None)).LibraryItemsWarned.Should().Be(0);
        s.Notifier.LibraryWarnings.Should().HaveCount(2);

        // With avatars of people switched off nobody is invited to confirm again.
        using (InferenceApiDbContext db = l.NewDb())
        {
            db.ProjectAvatarEvents.RemoveRange(await db.ProjectAvatarEvents.Where(e => e.Kind == AvatarEventKind.LibraryRetentionWarningSent).ToListAsync());
            await db.SaveChangesAsync();
        }

        l.Harness.Options.Enabled = false;
        (await s.Service.SweepOnceAsync(now.AddHours(12), CancellationToken.None)).LibraryItemsWarned.Should().Be(0);
    }

    [Fact]
    public async Task The_librarys_own_copies_count_as_the_organizations_storage()
    {
        var l = new L();
        ProjectFile face = l.Harness.AddImage("anna.png", salt: 1);
        using InferenceApiDbContext db = l.NewDb();
        var usage = new StorageUsageService(db, l.Harness.Entitlements);
        long before = await usage.GetUsedBytesAsync(l.OrganizationId, CancellationToken.None);
        before.Should().Be(face.SizeBytes);

        await l.PublishPersonAsync("Anna", face);

        (await usage.GetUsedBytesAsync(l.OrganizationId, CancellationToken.None)).Should().Be(2 * face.SizeBytes, "the project file and the library's copy");
        (await usage.GetUsedBytesAsync(Guid.NewGuid(), CancellationToken.None)).Should().Be(0);
    }
}
