#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.PostTexts;
using ReelBolt.Inference.Api.Services.SocialPublishing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.SocialPublishing;

/// <summary>The posts routes: authority is edit on the project, accounts of the active organization only, a snapshot of the text, edits only while scheduled.</summary>
public class SocialPostsRoutesTests
{
    private static SocialPostsController Controller(
        EngineFixture f, Guid? userId = null, Guid? organizationId = null, OrganizationRole? role = OrganizationRole.Owner)
    {
        Guid user = userId ?? f.UserId;
        Guid org = organizationId ?? f.OrganizationId;
        f.Tenant.ActAsOrganization(org);
        var db = new ReelBolt.Inference.Api.Data.InferenceApiDbContext(f.DbOptions, f.Tenant);
        SocialFakeUser current = new(user, org, role);
        return new SocialPostsController(db, current, new ProjectAccessService(db, current), f.Outputs, f.Entitlements, f.Clock)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    private static int Status<T>(ActionResult<T> result) => result.Result switch
    {
        ObjectResult o => o.StatusCode ?? 200,
        StatusCodeResult s => s.StatusCode,
        ForbidResult => 403,
        _ => result.Value is not null ? 200 : 0
    };

    private static string? Error<T>(ActionResult<T> result) =>
        result.Result is ObjectResult { Value: { } v } ? v.GetType().GetProperty("error")?.GetValue(v) as string : null;

    private static T Body<T>(ActionResult<T> result) => result.Value ?? (T)((ObjectResult)result.Result!).Value!;

    private static CreateSocialPostRequest NewRequest(EngineFixture f, Action<Dictionary<string, object?>>? tweak = null) =>
        new(f.ProjectId, SocialOutputKinds.StepResult, f.OutputId, f.Account.Id, Title: "Hello", Body: "Body text", Hashtags: ["travel", "#reel"],
            Options: new SocialPostOptionsDto("public", AiGenerated: true, CommentsEnabled: true), PublishNow: true);

    // ------------------------------------------------------------------ create

    [Fact]
    public async Task Create_now_stores_a_scheduled_snapshot_with_the_ai_label_and_options()
    {
        using EngineFixture f = new();
        ActionResult<SocialPostResponse> result = await Controller(f).Create(NewRequest(f), CancellationToken.None);

        Status(result).Should().Be(201);
        SocialPostResponse post = Body(result);
        post.Status.Should().Be("scheduled");
        post.ScheduledAt.Should().Be(f.Clock.UtcNow);
        post.Hashtags.Should().Equal("#travel", "#reel");
        post.Options.AiGenerated.Should().BeTrue();
        post.Platform.Should().Be("youtube");
        post.CanCancel.Should().BeTrue();
        post.CanEdit.Should().BeTrue();
        SocialPost row = await f.ReloadAsync(post.Id);
        row.CreatedByUserId.Should().Be(f.UserId);
        row.OrganizationId.Should().Be(f.OrganizationId);
        row.OptionsJson.Should().Contain("\"aiGenerated\":true");
    }

    [Fact]
    public async Task Create_schedules_in_a_time_zone_and_keeps_the_zone_for_display()
    {
        using EngineFixture f = new();
        CreateSocialPostRequest request = NewRequest(f) with { PublishNow = false, ScheduledLocal = new DateTime(2026, 11, 20, 9, 30, 0), TimeZone = "Europe/Rome" };

        SocialPostResponse post = Body(await Controller(f).Create(request, CancellationToken.None));

        post.ScheduledAt.Should().Be(new DateTime(2026, 11, 20, 8, 30, 0, DateTimeKind.Utc), "Rome is UTC+1 in November");
        post.TimeZone.Should().Be("Europe/Rome");
        post.ScheduledLocal.Should().Be("2026-11-20T09:30:00");
    }

    [Fact]
    public async Task The_text_is_a_snapshot_copied_from_the_saved_post_text_and_later_edits_never_change_it()
    {
        using EngineFixture f = new();
        PostTextRow saved = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = f.ProjectId,
            OutputId = f.OutputId,
            Platform = PostTextPlatformLimits.YouTubeShorts,
            Language = "English",
            Title = "Saved title",
            Body = "Saved body",
            HashtagsJson = "[\"#saved\"]"
        };
        f.Db.PostTexts.Add(saved);
        await f.Db.SaveChangesAsync();
        CreateSocialPostRequest request = new(f.ProjectId, SocialOutputKinds.StepResult, f.OutputId, f.Account.Id, PostTextId: saved.Id, PublishNow: true);

        SocialPostResponse post = Body(await Controller(f).Create(request, CancellationToken.None));

        post.Title.Should().Be("Saved title");
        post.Body.Should().Be("Saved body");
        post.Hashtags.Should().Equal("#saved");
        post.PostTextId.Should().Be(saved.Id);

        await f.NewDb().PostTexts.Where(t => t.Id == saved.Id).ExecuteUpdateAsync(s => s.SetProperty(t => t.Body, "Edited later"));
        (await f.ReloadAsync(post.Id)).Body.Should().Be("Saved body");
    }

    [Fact]
    public async Task A_post_text_of_another_output_or_platform_is_not_found()
    {
        using EngineFixture f = new();
        PostTextRow other = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = f.ProjectId,
            OutputId = Guid.NewGuid(),
            Platform = PostTextPlatformLimits.YouTubeShorts,
            Language = "English",
            Body = "x"
        };
        f.Db.PostTexts.Add(other);
        await f.Db.SaveChangesAsync();
        CreateSocialPostRequest request = new(f.ProjectId, SocialOutputKinds.StepResult, f.OutputId, f.Account.Id, PostTextId: other.Id, PublishNow: true);

        ActionResult<SocialPostResponse> result = await Controller(f).Create(request, CancellationToken.None);

        Status(result).Should().Be(404);
        Error(result).Should().Be("post_text_not_found");
    }

    [Fact]
    public async Task Create_is_refused_for_every_wrong_actor_or_target()
    {
        using EngineFixture f = new();

        // Another organization's account: the same 404 as one that does not exist.
        SocialAccount foreign = f.AddAccount(Guid.NewGuid());
        ActionResult<SocialPostResponse> foreignAccount = await Controller(f).Create(NewRequest(f) with { AccountId = foreign.Id }, CancellationToken.None);
        (Status(foreignAccount), Error(foreignAccount)).Should().Be((404, "account_not_found"));
        Error(await Controller(f).Create(NewRequest(f) with { AccountId = Guid.NewGuid() }, CancellationToken.None)).Should().Be("account_not_found");

        // Another organization's project and a caller with no edit access.
        Status(await Controller(f, userId: Guid.NewGuid(), organizationId: Guid.NewGuid()).Create(NewRequest(f), CancellationToken.None)).Should().Be(404);
        Status(await Controller(f, role: OrganizationRole.Billing).Create(NewRequest(f), CancellationToken.None)).Should().Be(403);

        // An output that is not this project's.
        ActionResult<SocialPostResponse> noOutput = await Controller(f).Create(NewRequest(f) with { OutputId = Guid.NewGuid() }, CancellationToken.None);
        (Status(noOutput), Error(noOutput)).Should().Be((404, "output_not_found"));
        Status(await Controller(f).Create(NewRequest(f) with { OutputKind = "bogus" }, CancellationToken.None)).Should().Be(400);

        (await f.NewDb().SocialPosts.CountAsync()).Should().Be(0, "nothing was stored by a refused request");
    }

    [Fact]
    public async Task Create_is_refused_when_the_plan_does_not_include_publishing_or_the_account_needs_reconnecting()
    {
        using EngineFixture f = new();
        f.Entitlements.SocialAllowed = false;
        ActionResult<SocialPostResponse> trial = await Controller(f).Create(NewRequest(f), CancellationToken.None);
        Status(trial).Should().Be(402);
        trial.Result.Should().BeOfType<ObjectResult>().Which.Value!.ToString().Should().Contain("social_publishing");

        f.Entitlements.SocialAllowed = true;
        SocialAccount broken = f.AddAccount(status: SocialAccountStatus.ReconnectNeeded);
        ActionResult<SocialPostResponse> reconnect = await Controller(f).Create(NewRequest(f) with { AccountId = broken.Id }, CancellationToken.None);
        (Status(reconnect), Error(reconnect)).Should().Be((409, "account_not_active"));
    }

    [Fact]
    public async Task Create_runs_the_preflight_and_offers_the_vertical_rerender_for_a_wrong_shape()
    {
        using EngineFixture f = new();
        f.SetOutput(f.GoodOutput() with { Width = 1920, Height = 1080 });

        ActionResult<SocialPostResponse> result = await Controller(f).Create(NewRequest(f), CancellationToken.None);

        (Status(result), Error(result)).Should().Be((422, "preflight_failed"));
        string json = JsonSerializer.Serialize(((ObjectResult)result.Result!).Value);
        json.Should().Contain("aspect_out_of_range").And.Contain("RerenderVertical\":true");
        (await f.NewDb().SocialPosts.CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData("scheduled_in_past")]
    [InlineData("both")]
    [InlineData("neither")]
    [InlineData("bad_zone")]
    public async Task Create_refuses_unusable_times(string kind)
    {
        using EngineFixture f = new();
        CreateSocialPostRequest request = kind switch
        {
            "scheduled_in_past" => NewRequest(f) with { PublishNow = false, ScheduledAt = f.Clock.GetUtcNow().AddHours(-2) },
            "both" => NewRequest(f) with { ScheduledAt = f.Clock.GetUtcNow().AddHours(2) },
            "neither" => NewRequest(f) with { PublishNow = false },
            _ => NewRequest(f) with { PublishNow = false, ScheduledLocal = new DateTime(2026, 12, 1, 9, 0, 0), TimeZone = "Mars/Olympus" }
        };
        Status(await Controller(f).Create(request, CancellationToken.None)).Should().Be(400);
    }

    // ------------------------------------------------------------------ preflight route

    [Fact]
    public async Task The_preflight_route_refuses_hostile_hashtags_and_linkedin_unlisted()
    {
        using EngineFixture f = new(kind: SocialPlatformKind.LinkedIn);
        PreflightSocialPostRequest hostile = new(f.ProjectId, SocialOutputKinds.StepResult, f.OutputId, f.Account.Id, "T", "B", ["#x]{urn:li:person:9}"]);
        (await Controller(f).Preflight(hostile, CancellationToken.None)).Result.Should().BeOfType<BadRequestObjectResult>();

        PreflightSocialPostRequest unlisted = new(f.ProjectId, SocialOutputKinds.StepResult, f.OutputId, f.Account.Id, "T", "B", null, new SocialPostOptionsDto(Privacy: "unlisted"));
        SocialPreflightResponse body = Body(await Controller(f).Preflight(unlisted, CancellationToken.None));
        body.Ok.Should().BeFalse();
        body.Issues.Should().Contain(i => i.Code == "privacy_unsupported");
    }

    [Fact]
    public async Task The_preflight_route_reports_issues_without_storing_anything()
    {
        using EngineFixture f = new();
        f.SetOutput(f.GoodOutput() with { DurationSeconds = 400 });
        ActionResult<SocialPreflightResponse> result = await Controller(f).Preflight(
            new PreflightSocialPostRequest(f.ProjectId, SocialOutputKinds.StepResult, f.OutputId, f.Account.Id, "Title", "Body"), CancellationToken.None);

        SocialPreflightResponse body = Body(result);
        body.Ok.Should().BeFalse();
        body.Issues.Should().Contain(i => i.Code == "video_too_long");
        body.LimitsVersion.Should().Be(SocialPlatformLimits.Version);
        body.LimitsLastChecked.Should().Be(SocialPlatformLimits.LastChecked);
        (await f.NewDb().SocialPosts.CountAsync()).Should().Be(0);

        Status(await Controller(f, role: OrganizationRole.Billing).Preflight(
            new PreflightSocialPostRequest(f.ProjectId, SocialOutputKinds.StepResult, f.OutputId, f.Account.Id), CancellationToken.None)).Should().Be(403);
    }

    // ------------------------------------------------------------------ list / get / tenancy

    [Fact]
    public async Task List_and_get_see_only_the_active_organizations_posts()
    {
        using EngineFixture f = new();
        SocialPost mine = f.AddPost();
        Guid otherOrg = Guid.NewGuid();
        SocialAccount otherAccount = f.AddAccount(otherOrg);
        SocialPost theirs = f.AddPost(account: otherAccount);
        await f.NewDb().SocialPosts.Where(p => p.Id == theirs.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.OrganizationId, otherOrg));

        SocialPostListResponse list = Body(await Controller(f).List(null, null, null, 0, 50, CancellationToken.None));
        list.Items.Select(i => i.Id).Should().Equal(mine.Id);
        list.Total.Should().Be(1);

        Status(await Controller(f).Get(mine.Id, CancellationToken.None)).Should().Be(200);
        ActionResult<SocialPostResponse> foreign = await Controller(f).Get(theirs.Id, CancellationToken.None);
        (Status(foreign), Error(foreign)).Should().Be((404, "post_not_found"));
        Status(await Controller(f).Get(Guid.NewGuid(), CancellationToken.None)).Should().Be(404);

        // The same rows seen by the other organization.
        SocialPostListResponse others = Body(await Controller(f, userId: Guid.NewGuid(), organizationId: otherOrg).List(null, null, null, 0, 50, CancellationToken.None));
        others.Items.Select(i => i.Id).Should().Equal(theirs.Id);

        // The Tenant filter itself hides the rows from a scoped context.
        f.Tenant.ActAsOrganization(f.OrganizationId);
        await using var scoped = new ReelBolt.Inference.Api.Data.InferenceApiDbContext(f.DbOptions, f.Tenant);
        (await scoped.SocialPosts.Select(p => p.Id).ToListAsync()).Should().Equal(mine.Id);
    }

    [Fact]
    public async Task List_filters_by_status_and_is_closed_to_billing_members()
    {
        using EngineFixture f = new();
        f.AddPost(); f.AddPost(SocialPostStatus.Failed);
        SocialPostListResponse failed = Body(await Controller(f).List(SocialPostStatus.Failed, null, null, 0, 50, CancellationToken.None));
        failed.Items.Should().ContainSingle().Which.Status.Should().Be("failed");
        Status(await Controller(f, role: OrganizationRole.Billing).List(null, null, null, 0, 50, CancellationToken.None)).Should().Be(403);
    }

    // ------------------------------------------------------------------ patch

    [Fact]
    public async Task Patch_changes_text_options_and_time_only_while_the_post_is_scheduled()
    {
        using EngineFixture f = new();
        SocialPost post = f.AddPost(dueIn: TimeSpan.FromHours(3));

        ActionResult<SocialPostResponse> ok = await Controller(f).Update(post.Id, new UpdateSocialPostRequest(
            Title: "New title", Hashtags: ["fresh"], Options: new SocialPostOptionsDto("unlisted"),
            ScheduledLocal: new DateTime(2026, 12, 24, 18, 0, 0), TimeZone: "America/New_York"), CancellationToken.None);

        Status(ok).Should().Be(200);
        SocialPost row = await f.ReloadAsync(post.Id);
        row.Title.Should().Be("New title");
        row.Body.Should().Be("A body", "a field left out stays");
        row.HashtagsJson.Should().Contain("#fresh");
        row.OptionsJson.Should().Contain("unlisted");
        row.ScheduledAt.Should().Be(new DateTime(2026, 12, 24, 23, 0, 0, DateTimeKind.Utc));
        row.TimeZone.Should().Be("America/New_York");

        foreach (string state in new[] { SocialPostStatus.Publishing, SocialPostStatus.Published, SocialPostStatus.Failed, SocialPostStatus.Cancelled, SocialPostStatus.Unknown })
        {
            SocialPost other = f.AddPost(state);
            ActionResult<SocialPostResponse> refused = await Controller(f).Update(other.Id, new UpdateSocialPostRequest(Title: "x"), CancellationToken.None);
            Status(refused).Should().Be(409, state);
            (await f.ReloadAsync(other.Id)).Title.Should().Be("A title", state);
        }
    }

    [Fact]
    public async Task Patch_cannot_change_a_post_the_engine_claimed_in_the_meantime()
    {
        using EngineFixture f = new();
        SocialPost post = f.AddPost();
        SocialPostsController controller = Controller(f);
        // The sweeper claims between the controller's read and its UPDATE: simulate by claiming first, then sending a stale-looking patch.
        await f.NewEngine().TryClaimAsync(post.Id, CancellationToken.None);
        ActionResult<SocialPostResponse> result = await controller.Update(post.Id, new UpdateSocialPostRequest(Title: "late edit"), CancellationToken.None);
        (Status(result), Error(result)).Should().Be((409, "already_sending"));
        (await f.ReloadAsync(post.Id)).Title.Should().Be("A title");
    }

    [Fact]
    public async Task Patch_revalidates_the_text_against_the_platform_limits()
    {
        using EngineFixture f = new();
        SocialPost post = f.AddPost();
        ActionResult<SocialPostResponse> result = await Controller(f).Update(post.Id, new UpdateSocialPostRequest(Title: new string('t', 101)), CancellationToken.None);
        (Status(result), Error(result)).Should().Be((422, "preflight_failed"));
        (await f.ReloadAsync(post.Id)).Title.Should().Be("A title");
    }

    // ------------------------------------------------------------------ cancel

    [Fact]
    public async Task Cancel_stops_a_scheduled_post_for_good_and_a_claimed_one_cannot_be_cancelled()
    {
        using EngineFixture f = new();
        SocialPost post = f.AddPost();
        Status(await Controller(f).Cancel(post.Id, CancellationToken.None)).Should().Be(200);
        (await f.ReloadAsync(post.Id)).Status.Should().Be(SocialPostStatus.Cancelled);
        (await f.NewEngine().TryClaimAsync(post.Id, CancellationToken.None)).Should().BeNull();
        (await f.NewEngine().ClaimDueAsync(CancellationToken.None)).Claims.Should().BeEmpty();

        SocialPost running = f.AddPost();
        await f.NewEngine().TryClaimAsync(running.Id, CancellationToken.None);
        ActionResult<SocialPostResponse> refused = await Controller(f).Cancel(running.Id, CancellationToken.None);
        (Status(refused), Error(refused)).Should().Be((409, "already_sending"));
        (await f.ReloadAsync(running.Id)).Status.Should().Be(SocialPostStatus.Publishing);

        SocialPost done = f.AddPost(SocialPostStatus.Published);
        Status(await Controller(f).Cancel(done.Id, CancellationToken.None)).Should().Be(409);
    }

    // ------------------------------------------------------------------ retry

    [Fact]
    public async Task Retry_requeues_a_failed_post_and_makes_an_unknown_one_wait_for_the_users_confirmation()
    {
        using EngineFixture f = new();
        SocialPost failed = f.AddPost(SocialPostStatus.Failed, attempts: 3);
        await f.NewDb().SocialPosts.Where(p => p.Id == failed.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.FailureCode, "platform_refused"));
        SocialPostResponse queued = Body(await Controller(f).Retry(failed.Id, null, CancellationToken.None));
        queued.Status.Should().Be("scheduled");
        queued.Attempts.Should().Be(0);
        queued.FailureCode.Should().BeNull();

        SocialPost unknown = f.AddPost(SocialPostStatus.Unknown);
        ActionResult<SocialPostResponse> unconfirmed = await Controller(f).Retry(unknown.Id, new RetrySocialPostRequest(), CancellationToken.None);
        (Status(unconfirmed), Error(unconfirmed)).Should().Be((409, "confirm_not_posted"));
        (await f.ReloadAsync(unknown.Id)).Status.Should().Be(SocialPostStatus.Unknown);
        Status(await Controller(f).Retry(unknown.Id, new RetrySocialPostRequest(ConfirmNotPosted: true), CancellationToken.None)).Should().Be(200);

        foreach (string state in new[] { SocialPostStatus.Scheduled, SocialPostStatus.Published, SocialPostStatus.Publishing, SocialPostStatus.Cancelled })
            Status(await Controller(f).Retry(f.AddPost(state).Id, null, CancellationToken.None)).Should().Be(409, state);
    }

    [Fact]
    public async Task Retry_rechecks_the_plan_and_the_account()
    {
        using EngineFixture f = new();
        SocialPost failed = f.AddPost(SocialPostStatus.Failed);
        f.Entitlements.SocialAllowed = false;
        Status(await Controller(f).Retry(failed.Id, null, CancellationToken.None)).Should().Be(402);
        f.Entitlements.SocialAllowed = true;

        SocialAccount broken = f.AddAccount(status: SocialAccountStatus.ReconnectNeeded);
        SocialPost onBroken = f.AddPost(SocialPostStatus.Failed, account: broken);
        ActionResult<SocialPostResponse> result = await Controller(f).Retry(onBroken.Id, null, CancellationToken.None);
        (Status(result), Error(result)).Should().Be((409, "account_not_active"));
        (await f.ReloadAsync(failed.Id)).Status.Should().Be(SocialPostStatus.Failed);
    }

    // ------------------------------------------------------------------ authority on every mutating route

    [Fact]
    public async Task Every_mutating_route_re_asks_for_edit_access_and_hides_other_organizations_posts()
    {
        using EngineFixture f = new();
        SocialPost scheduled = f.AddPost();
        SocialPost failed = f.AddPost(SocialPostStatus.Failed);
        Guid stranger = Guid.NewGuid();

        // A billing-only member of the same organization is refused outright.
        SocialPostsController billing = Controller(f, role: OrganizationRole.Billing);
        Status(await billing.Update(scheduled.Id, new UpdateSocialPostRequest(Title: "x"), CancellationToken.None)).Should().Be(403);
        Status(await billing.Cancel(scheduled.Id, CancellationToken.None)).Should().Be(403);
        Status(await billing.Retry(failed.Id, null, CancellationToken.None)).Should().Be(403);

        // Someone from another organization gets the plain 404.
        SocialPostsController outsider = Controller(f, userId: stranger, organizationId: Guid.NewGuid());
        Status(await outsider.Update(scheduled.Id, new UpdateSocialPostRequest(Title: "x"), CancellationToken.None)).Should().Be(404);
        Status(await outsider.Cancel(scheduled.Id, CancellationToken.None)).Should().Be(404);
        Status(await outsider.Retry(failed.Id, null, CancellationToken.None)).Should().Be(404);

        (await f.ReloadAsync(scheduled.Id)).Status.Should().Be(SocialPostStatus.Scheduled);
        (await f.ReloadAsync(failed.Id)).Status.Should().Be(SocialPostStatus.Failed);
    }
}

public class SocialScheduleTimeTests
{
    private static readonly DateTime Now = new(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_wall_clock_time_uses_the_offset_in_force_on_that_day()
    {
        SocialScheduleResult winter = SocialScheduleTime.Resolve(false, null, new DateTime(2026, 3, 10, 9, 0, 0), "Europe/Rome", Now);
        SocialScheduleResult summer = SocialScheduleTime.Resolve(false, null, new DateTime(2026, 7, 10, 9, 0, 0), "Europe/Rome", Now);
        winter.Utc.Should().Be(new DateTime(2026, 3, 10, 8, 0, 0, DateTimeKind.Utc));
        summer.Utc.Should().Be(new DateTime(2026, 7, 10, 7, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void The_spring_forward_gap_is_refused_with_a_clear_code()
    {
        // 2026-03-29 02:30 does not exist in Rome (02:00 jumps to 03:00).
        SocialScheduleResult gap = SocialScheduleTime.Resolve(false, null, new DateTime(2026, 3, 29, 2, 30, 0), "Europe/Rome", Now);
        gap.Ok.Should().BeFalse();
        gap.ErrorCode.Should().Be("nonexistent_local_time");
        // 03:30 right after it is fine and is CEST (UTC+2).
        SocialScheduleTime.Resolve(false, null, new DateTime(2026, 3, 29, 3, 30, 0), "Europe/Rome", Now).Utc
            .Should().Be(new DateTime(2026, 3, 29, 1, 30, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void The_autumn_repeat_means_the_first_occurrence()
    {
        // 2026-10-25 02:30 happens twice in Rome (CEST then CET); the first is 00:30 UTC.
        SocialScheduleResult repeat = SocialScheduleTime.Resolve(false, null, new DateTime(2026, 10, 25, 2, 30, 0), "Europe/Rome", Now);
        repeat.Utc.Should().Be(new DateTime(2026, 10, 25, 0, 30, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void Other_zones_and_the_us_dst_change_are_handled_too()
    {
        SocialScheduleTime.Resolve(false, null, new DateTime(2026, 3, 8, 2, 30, 0), "America/New_York", Now).ErrorCode.Should().Be("nonexistent_local_time");
        SocialScheduleTime.Resolve(false, null, new DateTime(2026, 3, 8, 12, 0, 0), "America/New_York", Now).Utc
            .Should().Be(new DateTime(2026, 3, 8, 16, 0, 0, DateTimeKind.Utc));
        SocialScheduleTime.Resolve(false, null, new DateTime(2026, 6, 1, 8, 0, 0), "Asia/Tokyo", Now).Utc
            .Should().Be(new DateTime(2026, 5, 31, 23, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void An_instant_with_an_offset_is_converted_and_the_zone_is_only_kept_for_display()
    {
        SocialScheduleResult r = SocialScheduleTime.Resolve(false, new DateTimeOffset(2026, 6, 1, 10, 0, 0, TimeSpan.FromHours(2)), null, "Europe/Rome", Now);
        r.Utc.Should().Be(new DateTime(2026, 6, 1, 8, 0, 0, DateTimeKind.Utc));
        r.TimeZone.Should().Be("Europe/Rome");
        SocialScheduleTime.ToLocalText(r.Utc, r.TimeZone).Should().Be("2026-06-01T10:00:00");
    }

    [Fact]
    public void Past_far_future_missing_zone_and_ambiguous_choices_are_refused()
    {
        SocialScheduleTime.Resolve(false, null, new DateTime(2026, 2, 1, 9, 0, 0), "Europe/Rome", Now).ErrorCode.Should().Be("in_the_past");
        SocialScheduleTime.Resolve(false, null, new DateTime(2028, 2, 1, 9, 0, 0), "Europe/Rome", Now).ErrorCode.Should().Be("too_far_ahead");
        SocialScheduleTime.Resolve(false, null, new DateTime(2026, 4, 1, 9, 0, 0), null, Now).ErrorCode.Should().Be("time_zone_required");
        SocialScheduleTime.Resolve(false, null, new DateTime(2026, 4, 1, 9, 0, 0), "Nowhere/Land", Now).ErrorCode.Should().Be("invalid_time_zone");
        SocialScheduleTime.Resolve(true, new DateTimeOffset(Now.AddDays(1)), null, null, Now).ErrorCode.Should().Be("schedule_required");
        SocialScheduleTime.Resolve(false, null, null, null, Now).ErrorCode.Should().Be("schedule_required");
        SocialScheduleTime.Resolve(true, null, null, null, Now).Utc.Should().Be(Now);
    }
}

public class SocialPlatformLimitsTests
{
    private static SocialPreflightResult Check(SocialPlatformKind kind, string? title = "T", string? body = "B", int tags = 0, long? size = 1_000_000, double? seconds = 30, int? w = 1080, int? h = 1920) =>
        SocialPreflight.Check(new SocialPreflightInput(kind, title, body, Enumerable.Range(0, tags).Select(i => $"#t{i}").ToList(), size, seconds, w, h));

    [Fact]
    public void The_text_limits_are_the_post_captions_table_and_the_media_limits_carry_a_version_and_date()
    {
        SocialPlatformLimits.Text(SocialPlatformKind.YouTube).Should().BeSameAs(PostTextPlatformLimits.Find(PostTextPlatformLimits.YouTubeShorts));
        SocialPlatformLimits.Text(SocialPlatformKind.LinkedIn).Should().BeSameAs(PostTextPlatformLimits.Find(PostTextPlatformLimits.LinkedIn));
        SocialPlatformLimits.Version.Should().BeGreaterThan(0);
        DateTime.TryParse(SocialPlatformLimits.LastChecked, out _).Should().BeTrue();
        SocialPlatforms.All.Should().OnlyContain(k => SocialPlatformLimits.All.Any(l => l.Platform == k));
    }

    [Fact]
    public void A_good_vertical_short_passes_on_youtube_and_linkedin()
    {
        Check(SocialPlatformKind.YouTube).Ok.Should().BeTrue();
        Check(SocialPlatformKind.LinkedIn).Ok.Should().BeTrue();
    }

    [Fact]
    public void Text_length_title_and_hashtag_limits_are_enforced_per_platform()
    {
        Check(SocialPlatformKind.YouTube, title: new string('t', 101)).Issues.Should().Contain(i => i.Code == "title_too_long");
        Check(SocialPlatformKind.YouTube, title: " ").Issues.Should().Contain(i => i.Code == "title_required");
        Check(SocialPlatformKind.LinkedIn, body: new string('b', 3001)).Issues.Should().Contain(i => i.Code == "body_too_long");
        Check(SocialPlatformKind.LinkedIn, body: " ").Issues.Should().Contain(i => i.Code == "body_required");
        Check(SocialPlatformKind.LinkedIn, tags: 6).Issues.Should().Contain(i => i.Code == "too_many_hashtags");
        // Hashtags count against the text length.
        Check(SocialPlatformKind.LinkedIn, body: new string('b', 2995), tags: 2).Issues.Should().Contain(i => i.Code == "body_too_long");
    }

    [Fact]
    public void Size_duration_and_aspect_are_enforced_and_the_vertical_rerender_is_offered_for_shape()
    {
        Check(SocialPlatformKind.YouTube, size: 9L * 1024 * 1024 * 1024).Issues.Should().Contain(i => i.Code == "file_too_large");
        Check(SocialPlatformKind.YouTube, size: 0).Issues.Should().Contain(i => i.Code == "file_empty");
        Check(SocialPlatformKind.YouTube, seconds: 181).Issues.Should().Contain(i => i.Code == "video_too_long");
        Check(SocialPlatformKind.LinkedIn, seconds: 2).Issues.Should().Contain(i => i.Code == "video_too_short");
        SocialPreflightIssue wide = Check(SocialPlatformKind.YouTube, w: 1920, h: 1080).Issues.Single(i => i.Code == "aspect_out_of_range");
        wide.RerenderVertical.Should().BeTrue();
        Check(SocialPlatformKind.YouTube, w: 1080, h: 1080).Ok.Should().BeTrue("square is accepted");
        Check(SocialPlatformKind.LinkedIn, w: 1920, h: 1080).Ok.Should().BeTrue("LinkedIn takes landscape");
        Check(SocialPlatformKind.LinkedIn, w: 3000, h: 1000).Issues.Should().Contain(i => i.Code == "aspect_out_of_range");
    }

    [Fact]
    public void Facts_that_cannot_be_read_are_warnings_not_blockers()
    {
        SocialPreflightResult r = Check(SocialPlatformKind.YouTube, size: null, seconds: null, w: null, h: null);
        r.Ok.Should().BeTrue();
        r.Issues.Select(i => i.Code).Should().BeEquivalentTo("size_unknown", "duration_unknown", "aspect_unknown");
        r.Issues.Should().OnlyContain(i => i.Severity == "warning");
    }
}
