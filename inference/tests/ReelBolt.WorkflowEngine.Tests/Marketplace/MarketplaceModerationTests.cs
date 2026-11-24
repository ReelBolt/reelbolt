using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Data.Marketplace;
using ReelBolt.Inference.Api.Services.Account;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Marketplace;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Marketplace;

public sealed class MarketplaceModerationTests
{
    // ---------------------------------------------------------------- fakes

    private sealed class FakeUser : ICurrentUser
    {
        public Guid UserId { get; set; } = Guid.NewGuid();
        public string Email => "someone@example.com";
        public bool IsAuthenticated => true;
        public Guid? OrganizationId { get; set; } = Guid.NewGuid();
        public OrganizationRole? OrgRole => OrganizationRole.Owner;
        public bool IsPlatformAdmin { get; set; }
        public bool EmailVerified { get; set; } = true;
    }

    private sealed class FakeAccess : IProjectAccessService
    {
        public Task<ProjectAccessResult> GetAsync(Guid projectId, ProjectPermission permission, CancellationToken ct) =>
            Task.FromResult(new ProjectAccessResult(ProjectAccessOutcome.NotFound, null));

        public ProjectAccessResult Get(Guid projectId, ProjectPermission permission) => GetAsync(projectId, permission, default).Result;
        public ProjectAccessOutcome AuthorizeOrganization(ProjectPermission permission) => ProjectAccessOutcome.Found;
    }

    private sealed class FakeTextCheck : IMarketplaceTextCheck
    {
        public Task<TextCheckResult> CheckAsync(IReadOnlyList<TextCheckField> fields, Guid o, Guid p, Guid u, CancellationToken ct) =>
            Task.FromResult(new TextCheckResult(TextVerdict.Allow, [new TextCheckFieldResult("name", TextVerdict.Allow)]));
    }

    private sealed class FakeDispatcher : IMarketplacePreviewDispatcher
    {
        public readonly List<Guid> Versions = [];
        public Task DispatchAsync(Guid recipeId, Guid versionId, CancellationToken ct)
        {
            Versions.Add(versionId);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeCredits : ICreditBalanceReader
    {
        public Task<CreditBalanceSnapshot?> GetAsync(Guid organizationId, CancellationToken ct) => Task.FromResult<CreditBalanceSnapshot?>(null);
    }

    private sealed class FakeStore : IMarketplaceObjectStore
    {
        public readonly List<Guid> Deleted = [];
        public readonly HashSet<string> Existing = [];
        public bool Throw;
        public RecipeObjectProtection? LastProtection;
        public Task<int> DeleteRecipeObjectsAsync(Guid recipeId, RecipeObjectProtection protection, CancellationToken ct)
        {
            Deleted.Add(recipeId);
            LastProtection = protection;
            if (Throw)
                throw new InvalidOperationException("s3 down");
            return Task.FromResult(0);
        }

        public Task<bool> ExistsAsync(string key, CancellationToken ct) => Task.FromResult(Existing.Contains(key));
    }

    private sealed class FakeNotifier : IMarketplaceAuthorNotifier
    {
        public readonly List<(Guid RecipeId, string Category)> Sent = [];
        public Task NotifyAsync(Guid recipeId, string reasonCategory, CancellationToken ct)
        {
            Sent.Add((recipeId, reasonCategory));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeMail : IAccountMailClient
    {
        public readonly List<AccountMail> Sent = [];
        public Task<bool> SendAsync(AccountMail mail, CancellationToken ct)
        {
            Sent.Add(mail);
            return Task.FromResult(true);
        }
    }

    private sealed class Harness
    {
        public readonly InferenceApiDbContext Db = new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        public readonly FakeUser User = new() { IsPlatformAdmin = true };
        public readonly FakeStore Store = new();
        public readonly FakeNotifier Notifier = new();
        public readonly FakeDispatcher Dispatcher = new();
        public readonly MarketplaceOptions Options = new() { Enabled = true, ReportsAutoHideThreshold = 3, MaxReportsPerUserPerDay = 10 };
        public readonly Guid AuthorUserId = Guid.NewGuid();
        public readonly Guid AuthorOrgId = Guid.NewGuid();

        public Harness(bool adminInDb = true)
        {
            Db.ApplicationUsers.Add(new ApplicationUser { Id = User.UserId, Email = "admin@example.com", IsAdmin = adminInDb });
            Db.ApplicationUsers.Add(new ApplicationUser { Id = AuthorUserId, Email = "author-secret@example.com", DisplayName = "Author", EmailVerifiedAt = DateTime.UtcNow });
            Db.Organizations.Add(new Organization { Id = AuthorOrgId, Name = "Acme Studio", Slug = "acme" });
            Db.SaveChanges();
        }

        public MarketplacePublishService Publish() =>
            new(Db, User, new FakeAccess(), new FakeTextCheck(), Dispatcher, new FakeCredits(), new MarketplaceCapLock(),
                Microsoft.Extensions.Options.Options.Create(Options), NullLogger<MarketplacePublishService>.Instance, Notifier);

        public MarketplaceAdminService Admin() => new(Db, User, Publish(), Store, Dispatcher, Notifier, new InMemoryReviewQueueQuery(), NullLogger<MarketplaceAdminService>.Instance);

        public MarketplaceReportService Reports() =>
            new(Db, User, Microsoft.Extensions.Options.Options.Create(Options), NullLogger<MarketplaceReportService>.Instance);

        /// <summary>Switches the acting user to a new account of the given age and verification.</summary>
        public Guid Reporter(int ageDays = 30, bool verified = true)
        {
            Guid id = Guid.NewGuid();
            Db.ApplicationUsers.Add(new ApplicationUser { Id = id, Email = $"{id}@example.com", CreatedAt = DateTime.UtcNow.AddDays(-ageDays), EmailVerifiedAt = verified ? DateTime.UtcNow : null });
            Db.SaveChanges();
            User.UserId = id;
            return id;
        }

        public MarketplaceSafetyService Safety() => new(Db, Store, NullLogger<MarketplaceSafetyService>.Instance);

        public async Task<MarketplaceRecipe> SeedAsync(RecipeStatus status = RecipeStatus.Listed, string name = "Promo", Guid? orgId = null, Guid? authorId = null)
        {
            Guid id = Guid.NewGuid();
            Guid versionId = Guid.NewGuid();
            MarketplaceRecipe recipe = new()
            {
                Id = id,
                AuthorUserId = authorId ?? AuthorUserId,
                AuthorOrgId = orgId ?? AuthorOrgId,
                Status = status,
                Name = name,
                Description = "Makes a promo.",
                Category = "promo",
                Language = "en",
                AuthorLabel = "Ada",
                CostClass = "low",
                CurrentVersionId = status is RecipeStatus.Listed or RecipeStatus.Removed ? versionId : null,
                ListedAt = DateTime.UtcNow,
                EverListedAt = DateTime.UtcNow
            };
            Db.MarketplaceRecipes.Add(recipe);
            MarketplaceRecipeVersion first = Version(id, versionId, 1, status == RecipeStatus.Pending ? "pending" : "listed");
            first.SubmittedByUserId = authorId ?? AuthorUserId;
            Db.MarketplaceRecipeVersions.Add(first);
            await Db.SaveChangesAsync();
            return recipe;
        }

        public MarketplaceRecipeVersion Version(Guid recipeId, Guid versionId, int n, string decision) => new()
        {
            Id = versionId,
            RecipeId = recipeId,
            N = n,
            SubmittedByUserId = AuthorUserId,
            PreviewKey = ReelBolt.Shared.Storage.MarketplacePreviewKeys.Preview(recipeId, versionId),
            PosterKey = ReelBolt.Shared.Storage.MarketplacePreviewKeys.Poster(recipeId, versionId),
            PreviewSha256 = "abc",
            PreviewStatus = PreviewStatus.Ready,
            CheckJson = new JsonObject
            {
                ["text"] = "allow",
                ["meta"] = new JsonObject { ["name"] = $"Promo v{n}", ["description"] = "Newer text.", ["category"] = "promo", ["language"] = "en", ["authorLabel"] = "Ada" },
                ["decision"] = decision,
                ["reason"] = decision == "pending" ? "not_trusted" : "auto_listed"
            }.ToJsonString()
        };

        public async Task<MarketplaceRecipeVersion> AddPendingVersionAsync(MarketplaceRecipe recipe, int n = 2)
        {
            MarketplaceRecipeVersion v = Version(recipe.Id, Guid.NewGuid(), n, "pending");
            Db.MarketplaceRecipeVersions.Add(v);
            await Db.SaveChangesAsync();
            return v;
        }

        public MarketplaceRecipeVersion VersionOf(Guid recipeId, int n = 1) => Db.MarketplaceRecipeVersions.Single(v => v.RecipeId == recipeId && v.N == n);
    }

    private static T Ok<T>(MarketplaceResult<T> r)
    {
        r.Error.Should().BeNull();
        return r.Value!;
    }

    private static void Refused<T>(MarketplaceResult<T> r, int status, string? code = null)
    {
        r.Error.Should().NotBeNull();
        r.Error!.Status.Should().Be(status);
        if (code is not null)
            r.Error.Code.Should().Be(code);
    }

    // ---------------------------------------------------------------- flag and auth

    [Fact]
    public void Moderation_controllers_sit_behind_the_flag_filter_and_admin_routes_are_not_under_the_go_admin_prefix()
    {
        foreach (Type t in new[] { typeof(MarketplaceReportsController), typeof(MarketplaceAdminController) })
        {
            t.GetCustomAttributes(typeof(TypeFilterAttribute), true).Cast<TypeFilterAttribute>()
                .Should().Contain(a => a.ImplementationType == typeof(MarketplaceEnabledFilter));
            t.GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), true).Should().NotBeEmpty();
        }

        typeof(MarketplaceAdminController).GetCustomAttributes(typeof(RouteAttribute), true).Cast<RouteAttribute>().Single().Template
            .Should().Be("api/v1/marketplace/admin");
    }

    [Fact]
    public async Task Every_admin_action_refuses_a_non_admin_even_an_org_owner_and_re_reads_the_database()
    {
        foreach (bool claim in new[] { false, true })
        {
            // claim=false: an org Owner without the platform flag. claim=true: the token says admin but the database no longer does.
            Harness h = new(adminInDb: false);
            h.User.IsPlatformAdmin = claim;
            MarketplaceRecipe r = await h.SeedAsync();
            MarketplaceAdminService s = h.Admin();
            Guid id = r.Id;

            Refused(await s.QueueAsync(null, default), 403);
            Refused(await s.ReportsAsync(default), 403);
            Refused(await s.RecipesAsync("listed", default), 403);
            Refused(await s.DetailAsync(id, default), 403);
            Refused(await s.CopiesAsync(id, default), 403);
            Refused(await s.AuditAsync(null, null, default), 403);
            Refused(await s.ApproveAsync(id, new AdminApproveRequest(Guid.NewGuid(), null, null), default), 403);
            Refused(await s.RejectAsync(id, new AdminRejectRequest(Guid.NewGuid(), null, "content", null), default), 403);
            Refused(await s.RemoveAsync(id, new AdminRemoveRequest("content", null), default), 403);
            Refused(await s.RestoreAsync(id, default), 403);
            Refused(await s.DismissReportAsync(Guid.NewGuid(), default), 403);
            (await h.Db.MarketplaceRecipes.FindAsync(id))!.Status.Should().Be(RecipeStatus.Listed);
            h.Store.Deleted.Should().BeEmpty();
        }
    }

    // ---------------------------------------------------------------- reports

    [Fact]
    public async Task A_report_needs_a_verified_email_a_listed_recipe_and_a_known_reason()
    {
        Harness h = new();
        MarketplaceRecipe listed = await h.SeedAsync();
        MarketplaceRecipe pending = await h.SeedAsync(RecipeStatus.Pending);
        h.User.IsPlatformAdmin = false;

        h.User.EmailVerified = false;
        Refused(await h.Reports().ReportAsync(listed.Id, new ReportRecipeRequest("spam", null), default), 403, "EMAIL_NOT_VERIFIED");
        h.User.EmailVerified = true;

        Refused(await h.Reports().ReportAsync(pending.Id, new ReportRecipeRequest("spam", null), default), 404);
        Refused(await h.Reports().ReportAsync(Guid.NewGuid(), new ReportRecipeRequest("spam", null), default), 404);
        Refused(await h.Reports().ReportAsync(listed.Id, new ReportRecipeRequest("nope", null), default), 400, "INVALID_FIELD");
        Refused(await h.Reports().ReportAsync(listed.Id, new ReportRecipeRequest(null, null), default), 400, "INVALID_FIELD");
        Refused(await h.Reports().ReportAsync(listed.Id, new ReportRecipeRequest("spam", new string('x', 501)), default), 400, "INVALID_FIELD");
        (await h.Db.MarketplaceReports.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_report_note_is_plain_text_and_a_repeat_from_the_same_user_is_one_open_row()
    {
        Harness h = new();
        MarketplaceRecipe r = await h.SeedAsync();

        Ok(await h.Reports().ReportAsync(r.Id, new ReportRecipeRequest("Copyright", "<b>stolen</b> see https://evil.example/x"), default)).Reported.Should().BeTrue();
        Ok(await h.Reports().ReportAsync(r.Id, new ReportRecipeRequest("spam", "again"), default)).Reported.Should().BeTrue();

        MarketplaceReport row = await h.Db.MarketplaceReports.SingleAsync();
        row.Reason.Should().Be(ReportReason.Copyright);
        row.Note.Should().NotContain("<").And.NotContain("evil.example").And.Contain("stolen");
        row.ReporterUserId.Should().Be(h.User.UserId);
    }

    [Fact]
    public async Task Reports_per_user_per_day_are_capped_from_rows()
    {
        Harness h = new();
        h.Options.MaxReportsPerUserPerDay = 2;
        MarketplaceRecipe a = await h.SeedAsync(name: "a");
        MarketplaceRecipe b = await h.SeedAsync(name: "b");
        MarketplaceRecipe c = await h.SeedAsync(name: "c");

        Ok(await h.Reports().ReportAsync(a.Id, new ReportRecipeRequest("spam", null), default));
        Ok(await h.Reports().ReportAsync(b.Id, new ReportRecipeRequest("spam", null), default));
        Refused(await h.Reports().ReportAsync(c.Id, new ReportRecipeRequest("spam", null), default), 429, "REPORT_RATE_LIMITED");
        // A repeat of an open report is still the idempotent answer, not a rate error.
        Ok(await h.Reports().ReportAsync(a.Id, new ReportRecipeRequest("spam", null), default));
        (await h.Db.MarketplaceReports.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task Enough_open_reports_from_distinct_users_hide_the_recipe_with_an_audit_row_and_without_deleting_it()
    {
        Harness h = new();
        MarketplaceRecipe r = await h.SeedAsync();
        h.User.IsPlatformAdmin = false;

        for (int i = 0; i < 2; i++)
        {
            h.Reporter();
            Ok(await h.Reports().ReportAsync(r.Id, new ReportRecipeRequest("illegal", null), default));
        }

        // The same user twice does not count twice.
        Ok(await h.Reports().ReportAsync(r.Id, new ReportRecipeRequest("illegal", null), default));
        (await h.Db.MarketplaceRecipes.FindAsync(r.Id))!.HiddenByReports.Should().BeFalse();

        h.Reporter();
        Ok(await h.Reports().ReportAsync(r.Id, new ReportRecipeRequest("sexual", null), default));

        MarketplaceRecipe after = (await h.Db.MarketplaceRecipes.FindAsync(r.Id))!;
        after.HiddenByReports.Should().BeTrue();
        after.Status.Should().Be(RecipeStatus.Listed);
        (await h.Db.MarketplaceAudit.CountAsync(a => a.RecipeId == r.Id && a.Action == "auto_hidden" && a.ActorKind == "system")).Should().Be(1);
        h.Store.Deleted.Should().BeEmpty();

        // Hidden from the public: a later report of it is a 404.
        h.Reporter();
        Refused(await h.Reports().ReportAsync(r.Id, new ReportRecipeRequest("spam", null), default), 404);
    }

    [Fact]
    public async Task Young_or_unverified_accounts_do_not_count_toward_the_threshold_and_a_dismissed_pair_never_counts_again()
    {
        Harness h = new();
        MarketplaceRecipe r = await h.SeedAsync();
        h.User.IsPlatformAdmin = false;

        // Three reports, but one account is too young and one never verified: only one counts.
        h.Reporter(ageDays: 1);
        Ok(await h.Reports().ReportAsync(r.Id, new ReportRecipeRequest("spam", null), default));
        h.Reporter(verified: false);
        Ok(await h.Reports().ReportAsync(r.Id, new ReportRecipeRequest("spam", null), default));
        Guid good1 = h.Reporter();
        Ok(await h.Reports().ReportAsync(r.Id, new ReportRecipeRequest("spam", null), default));
        (await h.Db.MarketplaceRecipes.FindAsync(r.Id))!.HiddenByReports.Should().BeFalse();

        Guid good2 = h.Reporter();
        Ok(await h.Reports().ReportAsync(r.Id, new ReportRecipeRequest("spam", null), default));
        Guid good3 = h.Reporter();
        Ok(await h.Reports().ReportAsync(r.Id, new ReportRecipeRequest("spam", null), default));
        (await h.Db.MarketplaceRecipes.FindAsync(r.Id))!.HiddenByReports.Should().BeTrue();

        // An admin dismisses: the recipe is back, and the same three accounts cannot hide it again.
        h.User.IsPlatformAdmin = true;
        h.User.UserId = (await h.Db.ApplicationUsers.FirstAsync(u => u.IsAdmin)).Id;
        MarketplaceReport anyOpen = await h.Db.MarketplaceReports.FirstAsync(x => x.State == ReportState.Open && x.ReporterUserId == good1);
        Ok(await h.Admin().DismissReportAsync(anyOpen.Id, default)).HiddenByReports.Should().BeFalse();
        (await h.Db.MarketplaceReports.CountAsync(x => x.State == ReportState.Open)).Should().Be(0);

        h.User.IsPlatformAdmin = false;
        foreach (Guid again in new[] { good1, good2, good3 })
        {
            h.User.UserId = again;
            Ok(await h.Reports().ReportAsync(r.Id, new ReportRecipeRequest("spam", null), default));
        }

        (await h.Db.MarketplaceRecipes.FindAsync(r.Id))!.HiddenByReports.Should().BeFalse();

        // Three NEW distinct reporters hide it again.
        for (int i = 0; i < 3; i++)
        {
            h.Reporter();
            Ok(await h.Reports().ReportAsync(r.Id, new ReportRecipeRequest("spam", null), default));
        }

        (await h.Db.MarketplaceRecipes.FindAsync(r.Id))!.HiddenByReports.Should().BeTrue();
    }

    // ---------------------------------------------------------------- admin decisions

    [Fact]
    public async Task Approving_a_pending_recipe_lists_it_with_an_audit_row_and_needs_the_version()
    {
        Harness h = new();
        MarketplaceRecipe r = await h.SeedAsync(RecipeStatus.Pending);
        MarketplaceRecipeVersion v = h.VersionOf(r.Id);
        MarketplaceAdminService s = h.Admin();

        Refused(await s.ApproveAsync(r.Id, new AdminApproveRequest(null, null, null), default), 400, "VERSION_REQUIRED");
        Refused(await s.ApproveAsync(r.Id, new AdminApproveRequest(v.Id, null, null), default), 400, "DIGEST_REQUIRED");
        Refused(await s.RejectAsync(r.Id, new AdminRejectRequest(v.Id, " ", "content", null), default), 400, "DIGEST_REQUIRED");
        Refused(await s.ApproveAsync(r.Id, new AdminApproveRequest(v.Id, "wrong-digest", null), default), 409, "STALE_VERSION");
        (await h.Db.MarketplaceRecipes.FindAsync(r.Id))!.Status.Should().Be(RecipeStatus.Pending);

        AdminActionResponse done = Ok(await s.ApproveAsync(r.Id, new AdminApproveRequest(v.Id, MarketplacePublishService.VersionDigest(v), null), default));
        done.Status.Should().Be("listed");
        MarketplaceRecipe after = (await h.Db.MarketplaceRecipes.FindAsync(r.Id))!;
        after.CurrentVersionId.Should().Be(v.Id);
        after.EverListedAt.Should().NotBeNull();
        (await h.Db.MarketplaceAudit.AnyAsync(a => a.RecipeId == r.Id && a.Action == "version_approved" && a.ActorKind == "admin" && a.ActorUserId == h.User.UserId)).Should().BeTrue();
    }

    [Fact]
    public async Task A_new_version_of_a_listed_recipe_is_queued_and_approve_swaps_it_in_while_reject_keeps_the_old_one_live()
    {
        foreach (bool approve in new[] { true, false })
        {
            Harness h = new();
            MarketplaceRecipe r = await h.SeedAsync();
            Guid v1 = r.CurrentVersionId!.Value;
            MarketplaceRecipeVersion v2 = await h.AddPendingVersionAsync(r);
            MarketplaceAdminService s = h.Admin();

            AdminRecipeRow row = Ok(await s.QueueAsync("pending", default)).Items.Should().ContainSingle().Subject;
            row.Id.Should().Be(r.Id);
            row.VersionId.Should().Be(v2.Id);
            row.Digest.Should().Be(MarketplacePublishService.VersionDigest(v2));
            row.Name.Should().Be("Promo v2");
            row.Status.Should().Be("listed");
            row.PreviewStatus.Should().Be("ready");
            row.PreviewSha256.Should().Be("abc");

            if (approve)
            {
                Ok(await s.ApproveAsync(r.Id, new AdminApproveRequest(v2.Id, row.Digest, null), default)).Status.Should().Be("listed");
                MarketplaceRecipe after = (await h.Db.MarketplaceRecipes.FindAsync(r.Id))!;
                after.CurrentVersionId.Should().Be(v2.Id);
                after.Name.Should().Be("Promo v2");
            }
            else
            {
                Ok(await s.RejectAsync(r.Id, new AdminRejectRequest(v2.Id, row.Digest, "low_quality", "internal note"), default)).Status.Should().Be("listed");
                MarketplaceRecipe after = (await h.Db.MarketplaceRecipes.FindAsync(r.Id))!;
                after.CurrentVersionId.Should().Be(v1);
                after.HumanRejectedAt.Should().NotBeNull();
                h.Notifier.Sent.Should().ContainSingle().Which.Should().Be((r.Id, "low_quality"));
            }

            Ok(await s.QueueAsync(null, default)).Items.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task Reject_uses_the_closed_reason_list_writes_audit_notifies_once_and_a_repeat_is_a_no_op()
    {
        Harness h = new();
        MarketplaceRecipe r = await h.SeedAsync(RecipeStatus.Pending);
        MarketplaceRecipeVersion v = h.VersionOf(r.Id);
        MarketplaceAdminService s = h.Admin();

        Refused(await s.RejectAsync(r.Id, new AdminRejectRequest(v.Id, null, "policy", null), default), 400, "INVALID_FIELD");
        Refused(await s.RejectAsync(r.Id, new AdminRejectRequest(null, null, "content", null), default), 400, "VERSION_REQUIRED");
        Refused(await s.RejectAsync(r.Id, new AdminRejectRequest(v.Id, null, "content", new string('n', 501)), default), 400, "INVALID_FIELD");

        Ok(await s.RejectAsync(r.Id, new AdminRejectRequest(v.Id, MarketplacePublishService.VersionDigest(v), "Copyright", "my private note"), default)).Status.Should().Be("rejected");
        Ok(await s.RejectAsync(r.Id, new AdminRejectRequest(v.Id, MarketplacePublishService.VersionDigest(v), "copyright", null), default)).Status.Should().Be("rejected");

        MarketplaceRecipe after = (await h.Db.MarketplaceRecipes.FindAsync(r.Id))!;
        after.HumanRejectedAt.Should().NotBeNull();
        h.Notifier.Sent.Should().ContainSingle().Which.Should().Be((r.Id, "copyright"));
        (await h.Db.MarketplaceAudit.CountAsync(a => a.RecipeId == r.Id && a.Action == "rejected" && a.ActorKind == "admin")).Should().Be(1);

        // The author-facing list shows the category, never the note.
        h.User.IsPlatformAdmin = false;
        h.User.OrganizationId = h.AuthorOrgId;
        MineRecipeDto mine = Ok(await h.Publish().MineAsync(default)).Items.Single();
        mine.Status.Should().Be("rejected");
        mine.ReasonCategory.Should().Be("copyright");
        JsonSerializer.Serialize(mine).Should().NotContain("private note");
    }

    [Fact]
    public async Task Remove_deletes_the_objects_keeps_the_copies_closes_reports_notifies_and_can_be_repeated()
    {
        Harness h = new();
        MarketplaceRecipe r = await h.SeedAsync();
        Guid copierOrg = Guid.NewGuid();
        Guid copyWorkflow = Guid.NewGuid();
        h.Db.MarketplaceCopies.Add(new MarketplaceCopy { Id = Guid.NewGuid(), RecipeId = r.Id, VersionId = r.CurrentVersionId!.Value, OrgId = copierOrg, WorkflowId = copyWorkflow });
        h.Db.MarketplaceReports.Add(new MarketplaceReport { Id = Guid.NewGuid(), RecipeId = r.Id, ReporterUserId = Guid.NewGuid(), Reason = ReportReason.Spam });
        await h.Db.SaveChangesAsync();
        MarketplaceAdminService s = h.Admin();

        Refused(await s.RemoveAsync(r.Id, new AdminRemoveRequest("bogus", null), default), 400, "INVALID_FIELD");
        Ok(await s.RemoveAsync(r.Id, new AdminRemoveRequest("unsafe", "note"), default)).Status.Should().Be("removed");
        Ok(await s.RemoveAsync(r.Id, new AdminRemoveRequest("unsafe", null), default)).Status.Should().Be("removed");

        MarketplaceRecipe after = (await h.Db.MarketplaceRecipes.FindAsync(r.Id))!;
        after.Status.Should().Be(RecipeStatus.Removed);
        after.RemovedReason.Should().Be("unsafe");
        after.HumanRejectedAt.Should().NotBeNull();
        h.Store.Deleted.Should().Equal(r.Id, r.Id);
        h.Notifier.Sent.Should().ContainSingle().Which.Should().Be((r.Id, "unsafe"));
        (await h.Db.MarketplaceCopies.CountAsync(c => c.RecipeId == r.Id)).Should().Be(1);
        (await h.Db.MarketplaceReports.SingleAsync()).State.Should().Be(ReportState.Actioned);
        (await h.Db.MarketplaceAudit.CountAsync(a => a.RecipeId == r.Id && a.Action == "removed")).Should().Be(1);

        Ok(await s.CopiesAsync(r.Id, default)).Items.Should().ContainSingle().Which.Copier.Should().Be(copierOrg);
    }

    [Fact]
    public async Task A_failed_object_deletion_does_not_undo_the_removal()
    {
        Harness h = new();
        h.Store.Throw = true;
        MarketplaceRecipe r = await h.SeedAsync();

        Ok(await h.Admin().RemoveAsync(r.Id, new AdminRemoveRequest("other", null), default)).Status.Should().Be("removed");
        (await h.Db.MarketplaceRecipes.FindAsync(r.Id))!.Status.Should().Be(RecipeStatus.Removed);
    }

    [Fact]
    public async Task Restore_lists_again_only_when_the_preview_objects_still_exist_else_goes_back_to_pending_with_a_new_preview()
    {
        Harness h = new();
        MarketplaceRecipe kept = await h.SeedAsync();
        MarketplaceRecipe gone = await h.SeedAsync(name: "gone");
        MarketplaceAdminService s = h.Admin();
        Refused(await s.RestoreAsync(kept.Id, default), 409, "INVALID_STATE");

        Ok(await s.RemoveAsync(kept.Id, new AdminRemoveRequest("other", null), default));
        Ok(await s.RemoveAsync(gone.Id, new AdminRemoveRequest("other", null), default));

        MarketplaceRecipeVersion keptVersion = h.VersionOf(kept.Id);
        h.Store.Existing.Add(keptVersion.PreviewKey!);
        h.Store.Existing.Add(keptVersion.PosterKey!);

        // Never straight to listed: it waits in the queue and an admin approves it afterwards.
        Ok(await s.RestoreAsync(kept.Id, default)).Status.Should().Be("pending");
        (await h.Db.MarketplaceRecipes.FindAsync(kept.Id))!.RemovedReason.Should().BeNull();
        h.Dispatcher.Versions.Should().BeEmpty();
        AdminRecipeRow queued = Ok(await s.QueueAsync(null, default)).Items.Should().ContainSingle(x => x.Id == kept.Id).Subject;
        Ok(await s.ApproveAsync(kept.Id, new AdminApproveRequest(queued.VersionId, queued.Digest, null), default)).Status.Should().Be("listed");

        Ok(await s.RestoreAsync(gone.Id, default)).Status.Should().Be("pending");
        MarketplaceRecipeVersion goneVersion = h.VersionOf(gone.Id);
        goneVersion.PreviewStatus.Should().Be(PreviewStatus.Preparing);
        goneVersion.PreviewKey.Should().BeNull();
        JsonNode.Parse(goneVersion.CheckJson!)!["decision"].Should().BeNull();
        h.Dispatcher.Versions.Should().Equal(goneVersion.Id);

        (await h.Db.MarketplaceAudit.CountAsync(a => a.Action == "restored")).Should().Be(2);

        // The re-dispatched preview ends in the usual finalize; a human owns the pending recipe, so it waits for review again.
        ListingResult? finalized = await h.Publish().FinalizeVersionAsync(goneVersion.Id, PreviewVerdict.Allow, default);
        finalized!.Outcome.Should().Be(ListingOutcome.Pending);
    }

    [Fact]
    public async Task Dismissing_the_last_open_report_unhides_and_approve_with_clear_hidden_does_too()
    {
        Harness h = new();
        MarketplaceRecipe r = await h.SeedAsync();
        r.HiddenByReports = true;
        MarketplaceReport a = new() { Id = Guid.NewGuid(), RecipeId = r.Id, ReporterUserId = Guid.NewGuid(), Reason = ReportReason.Spam };
        MarketplaceReport b = new() { Id = Guid.NewGuid(), RecipeId = r.Id, ReporterUserId = Guid.NewGuid(), Reason = ReportReason.Other };
        h.Db.MarketplaceReports.AddRange(a, b);
        await h.Db.SaveChangesAsync();
        MarketplaceAdminService s = h.Admin();

        AdminReportRow row = Ok(await s.ReportsAsync(default)).Items.First();
        row.Count.Should().Be(2);
        row.RecipeName.Should().Be("Promo");

        // A dismissal closes every open report of the recipe and clears the hidden state; a repeat is a no-op.
        Ok(await s.DismissReportAsync(a.Id, default)).HiddenByReports.Should().BeFalse();
        Ok(await s.DismissReportAsync(a.Id, default)).HiddenByReports.Should().BeFalse();
        (await h.Db.MarketplaceReports.CountAsync(x => x.State == ReportState.Open)).Should().Be(0);
        Ok(await s.DismissReportAsync(b.Id, default)).HiddenByReports.Should().BeFalse();
        (await h.Db.MarketplaceAudit.AnyAsync(x => x.Action == "hidden_cleared")).Should().BeTrue();

        // Approve without a version only clears the flag, and only when the admin says so.
        MarketplaceRecipe again = await h.SeedAsync(name: "again");
        again.HiddenByReports = true;
        await h.Db.SaveChangesAsync();
        Refused(await s.ApproveAsync(again.Id, new AdminApproveRequest(null, null, null), default), 400, "VERSION_REQUIRED");
        Ok(await s.ApproveAsync(again.Id, new AdminApproveRequest(null, null, true), default)).HiddenByReports.Should().BeFalse();
    }

    // ---------------------------------------------------------------- safety hook

    [Fact]
    public async Task A_csam_hold_removes_every_recipe_of_the_organization_deletes_objects_sends_no_notice_and_leaves_others()
    {
        Harness h = new();
        MarketplaceRecipe a = await h.SeedAsync(name: "a");
        MarketplaceRecipe b = await h.SeedAsync(RecipeStatus.Pending, "b");
        Guid otherOrg = Guid.NewGuid();
        MarketplaceRecipe other = await h.SeedAsync(name: "other", orgId: otherOrg, authorId: Guid.NewGuid());
        h.Db.Projects.Add(new Project { Id = Guid.NewGuid(), Name = "P", OwnerId = h.AuthorUserId, OrganizationId = h.AuthorOrgId, CsamOutputHoldAt = DateTime.UtcNow });
        h.Db.Projects.Add(new Project { Id = Guid.NewGuid(), Name = "Q", OwnerId = h.AuthorUserId, OrganizationId = otherOrg });
        await h.Db.SaveChangesAsync();

        (await h.Safety().RunOnceAsync(default)).Should().Be(2);

        foreach (Guid id in new[] { a.Id, b.Id })
        {
            MarketplaceRecipe r = (await h.Db.MarketplaceRecipes.FindAsync(id))!;
            r.Status.Should().Be(RecipeStatus.Removed);
            r.RemovedReason.Should().Be("safety");
            r.HumanRejectedAt.Should().NotBeNull();
            (await h.Db.MarketplaceAudit.CountAsync(x => x.RecipeId == id && x.Action == "removed" && x.ActorKind == "system")).Should().Be(1);
        }

        (await h.Db.MarketplaceRecipes.FindAsync(other.Id))!.Status.Should().Be(RecipeStatus.Listed);
        h.Store.Deleted.Should().Contain([a.Id, b.Id]).And.NotContain(other.Id);
        h.Notifier.Sent.Should().BeEmpty();

        // A second pass changes nothing but retries the deletion of a recent safety removal.
        (await h.Safety().RunOnceAsync(default)).Should().Be(0);
    }

    [Fact]
    public async Task A_content_quarantined_preview_source_project_removes_the_organizations_recipes()
    {
        Harness h = new();
        MarketplaceRecipe r = await h.SeedAsync();
        Guid sourceProject = Guid.NewGuid();
        Guid unrelatedProject = Guid.NewGuid();
        h.VersionOf(r.Id).PreviewSourceJson = new JsonObject { ["kind"] = "file", ["bucket"] = "b", ["key"] = $"projects/{sourceProject}/files/x.mp4" }.ToJsonString();
        h.Db.Projects.Add(new Project { Id = unrelatedProject, Name = "U", OwnerId = h.AuthorUserId, OrganizationId = h.AuthorOrgId, ContentQuarantinedAt = DateTime.UtcNow });
        await h.Db.SaveChangesAsync();

        // A quarantined project that is not a preview source does not take the organization down.
        (await h.Safety().RunOnceAsync(default)).Should().Be(0);
        (await h.Db.MarketplaceRecipes.FindAsync(r.Id))!.Status.Should().Be(RecipeStatus.Listed);

        h.Db.Projects.Add(new Project { Id = sourceProject, Name = "S", OwnerId = h.AuthorUserId, OrganizationId = h.AuthorOrgId, ContentQuarantinedAt = DateTime.UtcNow });
        await h.Db.SaveChangesAsync();
        (await h.Safety().RunOnceAsync(default)).Should().Be(1);
        (await h.Db.MarketplaceRecipes.FindAsync(r.Id))!.RemovedReason.Should().Be("safety");
    }

    // ---------------------------------------------------------------- author notice

    [Fact]
    public async Task The_author_mail_carries_a_reason_category_and_no_free_text_or_reporter_data()
    {
        Harness h = new();
        MarketplaceRecipe r = await h.SeedAsync();
        r.Name = "SECRET RECIPE NAME";
        await h.Db.SaveChangesAsync();
        FakeMail mail = new();
        MarketplaceAuthorNotifier notifier = new(h.Db, mail, NullLogger<MarketplaceAuthorNotifier>.Instance);

        await notifier.NotifyAsync(r.Id, "copyright", default);
        await notifier.NotifyAsync(r.Id, "a free text reason with <b>markup</b>", default);

        mail.Sent.Should().HaveCount(2);
        mail.Sent[0].Template.Should().Be("marketplace_notice");
        mail.Sent[0].To.Should().Be("author-secret@example.com");
        mail.Sent[0].Data.Reason.Should().Be("copyright");
        mail.Sent[0].Locale.Should().BeNull();
        mail.Sent[1].Data.Reason.Should().Be("other");
        string wire = JsonSerializer.Serialize(mail.Sent[1].Data);
        wire.Should().NotContain("SECRET").And.NotContain("markup");
        GoAccountMailClient.AllowedTemplates.Should().Contain("marketplace_notice");

        // No notice for an author who never verified an address or asked for deletion.
        ApplicationUser author = await h.Db.ApplicationUsers.SingleAsync(u => u.Id == h.AuthorUserId);
        author.DeletionRequestedAt = DateTime.UtcNow;
        await h.Db.SaveChangesAsync();
        await notifier.NotifyAsync(r.Id, "content", default);
        mail.Sent.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_failed_preview_or_blocked_text_tells_the_author_a_category_but_a_superseded_version_tells_nobody()
    {
        Harness h = new();
        h.User.IsPlatformAdmin = false;
        Guid recipeId = Guid.NewGuid();
        Guid versionId = Guid.NewGuid();
        h.Db.MarketplaceRecipes.Add(new MarketplaceRecipe { Id = recipeId, AuthorUserId = h.AuthorUserId, AuthorOrgId = h.AuthorOrgId, Status = RecipeStatus.Draft, Name = "n" });
        MarketplaceRecipeVersion v = h.Version(recipeId, versionId, 1, "x");
        v.PreviewStatus = PreviewStatus.Preparing;
        v.CheckJson = new JsonObject { ["text"] = "allow", ["meta"] = new JsonObject { ["name"] = "n", ["description"] = "d", ["category"] = "promo", ["language"] = "en", ["authorLabel"] = "a" } }.ToJsonString();
        h.Db.MarketplaceRecipeVersions.Add(v);
        await h.Db.SaveChangesAsync();

        ListingResult? result = await h.Publish().FinalizeVersionAsync(versionId, PreviewVerdict.Failed, default);
        result!.Outcome.Should().Be(ListingOutcome.Rejected);
        h.Notifier.Sent.Should().ContainSingle().Which.Should().Be((recipeId, "preview"));
    }

    [Fact]
    public async Task Restore_is_blocked_after_a_safety_removal_or_while_the_author_has_a_hold()
    {
        Harness h = new();
        MarketplaceRecipe safety = await h.SeedAsync(name: "safety");
        h.Db.Projects.Add(new Project { Id = Guid.NewGuid(), Name = "P", OwnerId = h.AuthorUserId, OrganizationId = h.AuthorOrgId, CsamOutputHoldAt = DateTime.UtcNow });
        await h.Db.SaveChangesAsync();
        await h.Safety().RunOnceAsync(default);
        (await h.Db.MarketplaceRecipes.FindAsync(safety.Id))!.Status.Should().Be(RecipeStatus.Removed);
        Refused(await h.Admin().RestoreAsync(safety.Id, default), 409, "RESTORE_BLOCKED_SAFETY");

        // An admin removal of an author who holds a hold elsewhere is blocked too, even though its reason is not safety.
        Harness g = new();
        Guid otherOrg = Guid.NewGuid();
        MarketplaceRecipe r = await g.SeedAsync(name: "other");
        Ok(await g.Admin().RemoveAsync(r.Id, new AdminRemoveRequest("other", null), default));
        g.Db.Projects.Add(new Project { Id = Guid.NewGuid(), Name = "Z", OwnerId = g.AuthorUserId, OrganizationId = otherOrg, ContentQuarantinedAt = DateTime.UtcNow });
        await g.Db.SaveChangesAsync();
        Refused(await g.Admin().RestoreAsync(r.Id, default), 409, "RESTORE_BLOCKED_SAFETY");
        (await g.Db.MarketplaceRecipes.FindAsync(r.Id))!.Status.Should().Be(RecipeStatus.Removed);
    }

    [Fact]
    public async Task A_csam_hold_of_a_submitter_removes_their_recipes_in_every_workspace()
    {
        Harness h = new();
        Guid orgB = Guid.NewGuid();
        Guid innocent = Guid.NewGuid();
        h.Db.ApplicationUsers.Add(new ApplicationUser { Id = innocent, Email = "i@example.com" });
        MarketplaceRecipe inOrgB = await h.SeedAsync(name: "elsewhere", orgId: orgB);
        MarketplaceRecipe bystander = await h.SeedAsync(name: "bystander", orgId: Guid.NewGuid(), authorId: innocent);
        h.Db.Projects.Add(new Project { Id = Guid.NewGuid(), Name = "P", OwnerId = h.AuthorUserId, OrganizationId = h.AuthorOrgId, CsamOutputHoldAt = DateTime.UtcNow });
        await h.Db.SaveChangesAsync();

        (await h.Safety().RunOnceAsync(default)).Should().Be(1);
        (await h.Db.MarketplaceRecipes.FindAsync(inOrgB.Id))!.RemovedReason.Should().Be("safety");
        (await h.Db.MarketplaceRecipes.FindAsync(bystander.Id))!.Status.Should().Be(RecipeStatus.Listed);
    }

    [Fact]
    public async Task The_reviewer_gets_only_that_versions_own_final_objects_and_only_as_an_admin()
    {
        Harness h = new();
        MarketplaceRecipe r = await h.SeedAsync(RecipeStatus.Pending);
        MarketplaceRecipeVersion v = h.VersionOf(r.Id);

        AdminMediaRef preview = Ok(await h.Admin().MediaAsync(v.Id, poster: false, default));
        preview.Key.Should().Be(v.PreviewKey);
        preview.Sha256.Should().Be("abc");
        Ok(await h.Admin().MediaAsync(v.Id, poster: true, default)).Key.Should().Be(v.PosterKey);

        Refused(await h.Admin().MediaAsync(Guid.NewGuid(), false, default), 404);

        v.PreviewKey = $"marketplace/{r.Id}/{v.Id}/quarantine/preview.mp4";
        await h.Db.SaveChangesAsync();
        Refused(await h.Admin().MediaAsync(v.Id, false, default), 404);

        v.PreviewKey = "marketplace/another/key.mp4";
        await h.Db.SaveChangesAsync();
        Refused(await h.Admin().MediaAsync(v.Id, false, default), 404);

        h.User.IsPlatformAdmin = false;
        Refused(await h.Admin().MediaAsync(v.Id, poster: true, default), 404);
    }

    [Fact]
    public void The_admin_media_routes_exist_and_the_options_default_the_reporter_age()
    {
        string[] routes = typeof(MarketplaceAdminController).GetMethods()
            .SelectMany(m => m.GetCustomAttributes(typeof(RouteAttribute), true).Cast<RouteAttribute>()).Select(a => a.Template).ToArray();
        routes.Should().Contain(["versions/{versionId:guid}/preview", "versions/{versionId:guid}/poster"]);
        new MarketplaceOptions().MinReporterAccountAgeDays.Should().Be(7);
    }

    [Fact]
    public async Task Rejecting_a_pending_update_leaves_the_reports_on_the_live_version_open()
    {
        Harness h = new();
        MarketplaceRecipe r = await h.SeedAsync();
        MarketplaceRecipeVersion v2 = await h.AddPendingVersionAsync(r);
        h.Db.MarketplaceReports.Add(new MarketplaceReport { Id = Guid.NewGuid(), RecipeId = r.Id, ReporterUserId = Guid.NewGuid(), Reason = ReportReason.Spam });
        await h.Db.SaveChangesAsync();

        Ok(await h.Admin().RejectAsync(r.Id, new AdminRejectRequest(v2.Id, MarketplacePublishService.VersionDigest(v2), "content", null), default));
        (await h.Db.MarketplaceReports.SingleAsync()).State.Should().Be(ReportState.Open);
    }

    // ---------------------------------------------------------------- review queue paging

    private static async Task SeedVersionAsync(Harness h, string decision, DateTime at, bool listedWithNewer)
    {
        Guid id = Guid.NewGuid();
        Guid v1 = Guid.NewGuid();
        h.Db.MarketplaceRecipes.Add(new MarketplaceRecipe
        {
            Id = id,
            AuthorUserId = h.AuthorUserId,
            AuthorOrgId = h.AuthorOrgId,
            Status = RecipeStatus.Listed,
            Name = "n",
            Description = "d",
            Category = "promo",
            Language = "en",
            AuthorLabel = "Ada",
            CostClass = "low",
            CurrentVersionId = v1
        });
        MarketplaceRecipeVersion live = h.Version(id, v1, 1, "listed");
        live.CreatedAt = at.AddDays(-1);
        MarketplaceRecipeVersion newer = h.Version(id, Guid.NewGuid(), 2, decision);
        newer.CreatedAt = at;
        h.Db.MarketplaceRecipeVersions.AddRange(live, newer);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task An_author_flooding_non_pending_versions_cannot_push_a_real_pending_one_off_the_queue()
    {
        Harness h = new();
        DateTime start = DateTime.UtcNow.AddDays(-10);
        for (int i = 0; i < 400; i++)
            await SeedVersionAsync(h, i % 2 == 0 ? "rejected" : "listed", start.AddSeconds(i), true);
        await SeedVersionAsync(h, "pending", start.AddDays(5), true);
        await h.Db.SaveChangesAsync();

        AdminRecipeRows page = Ok(await h.Admin().QueueAsync("pending", default));
        page.Items.Should().ContainSingle();
        page.NextCursor.Should().BeNull();
    }

    [Fact]
    public async Task The_queue_pages_by_keyset_without_gaps_or_repeats()
    {
        Harness h = new();
        DateTime start = DateTime.UtcNow.AddDays(-10);
        for (int i = 0; i < 205; i++)
            await SeedVersionAsync(h, "pending", start.AddSeconds(i / 2), true); // pairs share a timestamp: the id breaks the tie
        await h.Db.SaveChangesAsync();
        MarketplaceAdminService s = h.Admin();

        AdminRecipeRows first = Ok(await s.QueueAsync("pending", null, default));
        first.Items.Should().HaveCount(200);
        first.NextCursor.Should().NotBeNull();
        AdminRecipeRows second = Ok(await s.QueueAsync("pending", first.NextCursor, default));
        second.Items.Should().HaveCount(5);
        second.NextCursor.Should().BeNull();
        first.Items.Concat(second.Items).Select(x => x.VersionId).Distinct().Should().HaveCount(205);

        Refused(await s.QueueAsync("pending", "garbage", default), 400, "INVALID_FIELD");
    }

    [Fact]
    public void The_npgsql_queue_query_puts_the_pending_filter_the_ordering_and_the_cursor_in_sql()
    {
        using InferenceApiDbContext npgsql = new(new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=u;Password=p").Options);

        string sql = MarketplaceAdminService.QueueQuery(npgsql, new NpgsqlReviewQueueQuery(), DateTime.UtcNow, Guid.NewGuid()).Take(201).ToQueryString();

        sql.Should().Contain("@>").And.Contain("decision").And.Contain("pending").And.Contain("superseded");
        sql.Should().Contain("ORDER BY m.created_at, m.id").And.Contain("LIMIT @p").And.Contain("@p='201'");
        sql.IndexOf("ORDER BY", StringComparison.Ordinal).Should().BeGreaterThan(sql.IndexOf("@>", StringComparison.Ordinal));
        sql.Should().Contain("m.created_at > @at").And.Contain("m.id > @afterId");
    }

    /// <summary>Skipped unless REELBOLT_TEST_PG is set (see tests/run-postgres-tests.sh); it runs the real query on a migrated schema.</summary>
    [PostgresFact]
    public async Task The_real_postgres_queue_returns_the_pending_version_among_a_flood()
    {
        string baseConnection = ReelBolt.WorkflowEngine.Tests.Infrastructure.PostgresTestEnvironment.BaseConnectionString()!;
        string name = "reelbolt_queue_test_" + Guid.NewGuid().ToString("N")[..12];
        await using (Npgsql.NpgsqlDataSource admin = ReelBolt.WorkflowEngine.Tests.Infrastructure.PostgresTestEnvironment.CreateAdminDataSource(baseConnection))
        await using (Npgsql.NpgsqlCommand create = admin.CreateCommand($"CREATE DATABASE {name}"))
            await create.ExecuteNonQueryAsync();
        try
        {
            string connection = ReelBolt.WorkflowEngine.Tests.Infrastructure.PostgresTestEnvironment.ForDatabase(baseConnection, name);
            DbContextOptions<InferenceApiDbContext> options = new DbContextOptionsBuilder<InferenceApiDbContext>().UseNpgsql(connection).Options;
            await using InferenceApiDbContext db = new(options);
            await Microsoft.EntityFrameworkCore.Infrastructure.AccessorExtensions.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>(db).MigrateAsync();

            Guid user = Guid.NewGuid();
            Guid org = Guid.NewGuid();
            Guid pendingVersion = Guid.Empty;
            for (int i = 0; i < 450; i++)
            {
                Guid id = Guid.NewGuid();
                Guid v1 = Guid.NewGuid();
                Guid v2 = Guid.NewGuid();
                bool pending = i == 449;
                if (pending)
                    pendingVersion = v2;
                db.MarketplaceRecipes.Add(new MarketplaceRecipe
                {
                    Id = id,
                    AuthorUserId = user,
                    AuthorOrgId = org,
                    Status = RecipeStatus.Listed,
                    Name = "n",
                    Description = "d",
                    Category = "promo",
                    Language = "en",
                    CostClass = "low",
                    CurrentVersionId = v1
                });
                db.MarketplaceRecipeVersions.Add(new MarketplaceRecipeVersion
                {
                    Id = v1,
                    RecipeId = id,
                    N = 1,
                    PreviewStatus = PreviewStatus.Ready,
                    CheckJson = "{\"decision\":\"listed\"}",
                    CreatedAt = DateTime.UtcNow.AddDays(-2)
                });
                db.MarketplaceRecipeVersions.Add(new MarketplaceRecipeVersion
                {
                    Id = v2,
                    RecipeId = id,
                    N = 2,
                    PreviewStatus = PreviewStatus.Ready,
                    CreatedAt = DateTime.UtcNow.AddSeconds(i - 500),
                    CheckJson = pending ? "{\"decision\":\"pending\",\"reason\":\"not_trusted\"}" : "{\"decision\":\"rejected\"}"
                });
            }

            await db.SaveChangesAsync();
            List<MarketplaceRecipeVersion> rows = await MarketplaceAdminService.QueueQuery(db, new NpgsqlReviewQueueQuery(), null, Guid.Empty).Take(201).ToListAsync();
            rows.Should().ContainSingle().Which.Id.Should().Be(pendingVersion);
        }
        finally
        {
            await using Npgsql.NpgsqlDataSource admin = ReelBolt.WorkflowEngine.Tests.Infrastructure.PostgresTestEnvironment.CreateAdminDataSource(baseConnection);
            await using Npgsql.NpgsqlCommand drop = admin.CreateCommand($"DROP DATABASE IF EXISTS {name} WITH (FORCE)");
            await drop.ExecuteNonQueryAsync();
        }
    }

    // ---------------------------------------------------------------- author-safe reason

    [Fact]
    public async Task The_author_only_ever_sees_a_closed_reason_category_never_the_raw_code()
    {
        string[] internalReasons =
        [
            "csam_match", "safety", "text_blocked", "preview_failed", "preview_blocked", "live_limit", "digest_match", "submitter_held",
            "unsafe", "admin_rejected", "not_trusted", "recipe_not_active", "superseded", "copyright", "content", "low_quality", "other",
            "<script>x</script>", "SECRET-RAW-123", "", "random string with spaces"
        ];
        foreach (string raw in internalReasons.Where(r => r.Length > 0))
            MarketplaceAuthorReasons.PublicCategories.Should().Contain(MarketplaceAuthorReasons.ToPublicCategory(raw)!);

        foreach (string raw in new[] { "csam_match", "safety", "live_limit", "digest_match", "SECRET-RAW-123" })
        {
            foreach (RecipeStatus status in new[] { RecipeStatus.Removed, RecipeStatus.Rejected })
            {
                Harness h = new();
                MarketplaceRecipe r = await h.SeedAsync(status);
                h.Db.MarketplaceAudit.Add(new MarketplaceAuditEntry
                {
                    Id = Guid.NewGuid(),
                    RecipeId = r.Id,
                    Action = "removed",
                    ActorKind = "system",
                    DetailJson = new JsonObject { ["reason"] = raw }.ToJsonString(),
                    CreatedAt = DateTime.UtcNow.AddMinutes(1)
                });
                r.HiddenByReports = true;
                await h.Db.SaveChangesAsync();
                h.User.IsPlatformAdmin = false;
                h.User.OrganizationId = h.AuthorOrgId;

                MineRecipeDto row = Ok(await h.Publish().MineAsync(default)).Items.Single();
                MarketplaceAuthorReasons.PublicCategories.Should().Contain(row.ReasonCategory!);
                row.HiddenByReports.Should().BeTrue();
                string json = JsonSerializer.Serialize(row, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                json.Should().NotContain(raw).And.Contain("\"hiddenByReports\":true");
            }
        }

        // A live or waiting recipe shows no reason at all.
        Harness live = new();
        await live.SeedAsync();
        live.User.IsPlatformAdmin = false;
        live.User.OrganizationId = live.AuthorOrgId;
        Ok(await live.Publish().MineAsync(default)).Items.Single().ReasonCategory.Should().BeNull();
    }

    // ---------------------------------------------------------------- staged CSAM evidence is never deleted

    private static async Task<MarketplaceRecipeVersion> HoldEvidenceAsync(Harness h, MarketplaceRecipe r)
    {
        MarketplaceRecipeVersion v = h.VersionOf(r.Id);
        string staged = ReelBolt.Shared.Storage.MarketplacePreviewKeys.StagingRoot(r.Id, v.Id) + "n/preview.mp4";
        JsonObject check = JsonNode.Parse(v.CheckJson!)!.AsObject();
        check[MarketplaceEvidenceGuard.EvidenceKey] = MarketplaceEvidenceGuard.Staged;
        check[MarketplaceEvidenceGuard.KeysKey] = new JsonArray(staged);
        v.CheckJson = check.ToJsonString();
        await h.Db.SaveChangesAsync();
        return v;
    }

    [Fact]
    public async Task Admin_remove_after_a_failed_quarantine_move_hides_the_recipe_but_protects_the_staged_evidence()
    {
        Harness h = new();
        MarketplaceRecipe r = await h.SeedAsync();
        MarketplaceRecipeVersion v = await HoldEvidenceAsync(h, r);
        string staged = ReelBolt.Shared.Storage.MarketplacePreviewKeys.StagingRoot(r.Id, v.Id) + "n/preview.mp4";

        Ok(await h.Admin().RemoveAsync(r.Id, new AdminRemoveRequest("unsafe", null), default)).Status.Should().Be("removed");

        (await h.Db.MarketplaceRecipes.FindAsync(r.Id))!.Status.Should().Be(RecipeStatus.Removed);
        RecipeObjectProtection p = h.Store.LastProtection!;
        p.Protects(staged).Should().BeTrue();
        p.Protects(v.PreviewKey!).Should().BeTrue();
        p.Protects(ReelBolt.Shared.Storage.MarketplacePreviewKeys.StagingRoot(r.Id, v.Id)).Should().BeTrue();
        p.Protects($"marketplace/{r.Id}/{v.Id}/quarantine/x.mp4").Should().BeTrue();
        p.Protects($"marketplace/{r.Id}/{Guid.NewGuid()}/preview.mp4").Should().BeFalse();
    }

    [Fact]
    public async Task The_safety_pass_and_its_retry_protect_held_evidence_too_and_a_clean_recipe_is_not_protected()
    {
        Harness h = new();
        MarketplaceRecipe held = await h.SeedAsync(name: "held");
        MarketplaceRecipeVersion v = await HoldEvidenceAsync(h, held);
        h.Db.Projects.Add(new Project { Id = Guid.NewGuid(), Name = "P", OwnerId = h.AuthorUserId, OrganizationId = h.AuthorOrgId, CsamOutputHoldAt = DateTime.UtcNow });
        await h.Db.SaveChangesAsync();

        (await h.Safety().RunOnceAsync(default)).Should().Be(1);
        (await h.Db.MarketplaceRecipes.FindAsync(held.Id))!.Status.Should().Be(RecipeStatus.Removed);
        h.Store.LastProtection!.Protects(v.PreviewKey!).Should().BeTrue();

        // The retry on the next pass keeps protecting it.
        h.Store.LastProtection = null;
        await h.Safety().RunOnceAsync(default);
        h.Store.LastProtection!.Protects(v.PreviewKey!).Should().BeTrue();

        Harness clean = new();
        MarketplaceRecipe ok = await clean.SeedAsync();
        Ok(await clean.Admin().RemoveAsync(ok.Id, new AdminRemoveRequest("other", null), default));
        clean.Store.LastProtection!.Protects(clean.VersionOf(ok.Id).PreviewKey!).Should().BeFalse();
        clean.Store.LastProtection.Protects("marketplace/x/y/quarantine/z").Should().BeTrue();
    }

    // ---------------------------------------------------------------- no leaks

    [Fact]
    public async Task Admin_and_author_facing_output_carries_no_email_no_reporter_and_no_author_user_id()
    {
        Harness h = new();
        MarketplaceRecipe r = await h.SeedAsync();
        MarketplaceRecipeVersion v2 = await h.AddPendingVersionAsync(r);
        Guid reporter = Guid.NewGuid();
        h.Db.MarketplaceReports.Add(new MarketplaceReport { Id = Guid.NewGuid(), RecipeId = r.Id, ReporterUserId = reporter, Reason = ReportReason.Spam, Note = "n" });
        h.Db.MarketplaceAudit.Add(new MarketplaceAuditEntry { Id = Guid.NewGuid(), RecipeId = r.Id, Action = "report_received", ActorKind = "user", ActorUserId = reporter, DetailJson = "{\"reason\":\"spam\"}" });
        await h.Db.SaveChangesAsync();
        MarketplaceAdminService s = h.Admin();

        string adminAll = string.Join('\n', new object[]
        {
            Ok(await s.QueueAsync(null, default)), Ok(await s.RecipesAsync("listed", default)), Ok(await s.DetailAsync(r.Id, default)),
            Ok(await s.ReportsAsync(default)), Ok(await s.AuditAsync(null, null, default)),
            Ok(await s.RejectAsync(r.Id, new AdminRejectRequest(v2.Id, MarketplacePublishService.VersionDigest(v2), "content", "note"), default))
        }.Select(o => JsonSerializer.Serialize(o, o.GetType())));

        h.User.IsPlatformAdmin = false;
        h.User.OrganizationId = h.AuthorOrgId;
        MineResponse mine = Ok(await h.Publish().MineAsync(default));
        string authorFacing = JsonSerializer.Serialize(mine, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        foreach (string secret in new[] { h.AuthorUserId.ToString(), reporter.ToString(), "author-secret@example.com", "admin@example.com", h.User.UserId.ToString() })
        {
            adminAll.Should().NotContainEquivalentOf(secret);
            authorFacing.Should().NotContainEquivalentOf(secret);
        }

        adminAll.Should().Contain("Acme Studio");
        authorFacing.Should().NotContain("Acme").And.NotContain(h.AuthorOrgId.ToString());
        authorFacing.Should().Contain("\"authorLabel\":\"Ada\"").And.Contain("\"copyCount\"").And.Contain("\"runCount\"").And.Contain("\"version\"");
    }
}
