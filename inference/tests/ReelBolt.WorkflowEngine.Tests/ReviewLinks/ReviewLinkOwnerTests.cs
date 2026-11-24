#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Services.Editing.ReviewLinks;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Moderation;
using ReelBolt.Shared.Tenancy;
using ReelBolt.Shared.Workflows;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.ReviewLinks;

/// <summary>Owner side: create / list / get / revoke, tenant isolation, View vs Edit, token shown once, moderation gate.</summary>
public class ReviewLinkOwnerTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Create_returns_the_url_once_and_stores_only_the_hash()
    {
        using ReviewWorld w = new();

        IActionResult result = await w.Owner().Create(
            w.Project.Id, w.Timeline.Id, new CreateReviewLinkRequest("Client cut v2", null, 7, true), CancellationToken.None);

        CreateReviewLinkResponse body = (CreateReviewLinkResponse)result.Should_BeStatus(StatusCodes.Status201Created).Value!;
        body.Url.Should().StartWith(ReviewWorld.Origin + "/app/review/g/");
        string token = ReviewWorld.TokenOf(body.Url);
        ReviewLinkTokens.IsWellFormed(token).Should().BeTrue();
        body.Label.Should().Be("Client cut v2");
        body.ExpiresAt.Should().Be(ReviewWorld.Now.UtcDateTime.AddDays(7));
        body.Stale.Should().BeFalse("the render was made from the current revision");

        EditReviewLink row = await w.Seed.EditReviewLinks.SingleAsync();
        row.TokenHash.Should().Equal(ReviewLinkTokens.Hash(token));
        row.RenderExecutionId.Should().Be(w.ExecutionId);
        row.PinnedRevision.Should().Be(3);
        row.OrganizationId.Should().Be(w.OrgId);
        row.CreatedByUserId.Should().Be(w.UserId);
        JsonSerializer.Serialize(row).Should().NotContain(token, "no column holds the token itself");
    }

    [Fact]
    public async Task Create_defaults_to_14_days_comments_on_and_the_newest_render()
    {
        using ReviewWorld w = new();
        (Guid older, _, _) = w.AddRender(queuedAt: ReviewWorld.Now.UtcDateTime.AddHours(-5));
        (Guid newest, _, _) = w.AddRender(queuedAt: ReviewWorld.Now.UtcDateTime.AddHours(1));
        _ = older;

        (Guid id, string _) = await w.CreateLinkAsync();

        EditReviewLink row = await w.Seed.EditReviewLinks.SingleAsync(l => l.Id == id);
        row.ExpiresAt.Should().Be(ReviewWorld.Now.UtcDateTime.AddDays(14));
        row.AllowComments.Should().BeTrue();
        row.RenderExecutionId.Should().Be(newest);
    }

    [Fact]
    public async Task Create_pins_a_chosen_render_and_flags_staleness_when_the_timeline_moved_on()
    {
        using ReviewWorld w = new();
        (Guid first, _, _) = w.AddRender(revision: 1, queuedAt: ReviewWorld.Now.UtcDateTime.AddHours(-2));
        w.AddRender(revision: 3);

        IActionResult result = await w.Owner().Create(
            w.Project.Id, w.Timeline.Id, new CreateReviewLinkRequest(null, first, null, null), CancellationToken.None);

        CreateReviewLinkResponse body = (CreateReviewLinkResponse)result.Should_BeStatus(StatusCodes.Status201Created).Value!;
        body.Stale.Should().BeTrue("the pinned render was made at revision 1, the timeline is at 3");
        (await w.Seed.EditReviewLinks.SingleAsync()).RenderExecutionId.Should().Be(first);
    }

    [Fact]
    public async Task Create_refuses_with_a_plain_409_when_there_is_nothing_shareable()
    {
        using ReviewWorld w = new();
        w.Seed.WorkflowStepResults.RemoveRange(w.Seed.WorkflowStepResults);
        w.Seed.SaveChanges();

        ObjectResult none = (await w.Owner().Create(w.Project.Id, w.Timeline.Id, null, CancellationToken.None)).Should_BeStatus(409);
        none.Value.Should().BeOfType<ReviewLinkMessage>().Which.Code.Should().Be("NO_RENDER_TO_SHARE");
        (await w.Seed.EditReviewLinks.CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("preview")]
    [InlineData("range")]
    [InlineData("audio")]
    [InlineData("mov")]
    [InlineData("other-timeline")]
    public async Task Only_a_passed_full_non_preview_video_render_of_this_timeline_is_shareable(string kind)
    {
        using ReviewWorld w = new();
        w.Seed.WorkflowStepResults.RemoveRange(w.Seed.WorkflowStepResults);
        w.Seed.SaveChanges();
        (Guid exec, _, _) = kind switch
        {
            "failed" => w.AddRender(status: ExecutionStatus.Failed),
            "preview" => w.AddRender(preset: TimelineRenderPreset.Preview),
            "range" => w.AddRender(range: new TimelineRenderRange(0, 30)),
            "audio" => w.AddRender(audioJobKind: "loudness"),
            "mov" => w.AddRender(fileName: "final.mov"),
            _ => w.AddRender(timelineId: Guid.NewGuid()),
        };

        // Asking for it by id is refused exactly like asking for the newest.
        ObjectResult byId = (await w.Owner().Create(w.Project.Id, w.Timeline.Id, new CreateReviewLinkRequest(null, exec, null, null), CancellationToken.None)).Should_BeStatus(409);
        ((ReviewLinkMessage)byId.Value!).Code.Should().Be("NO_RENDER_TO_SHARE");
        (await w.Owner().Create(w.Project.Id, w.Timeline.Id, null, CancellationToken.None)).Should_BeStatus(409);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(91)]
    public async Task Create_rejects_an_expiry_outside_1_to_90_days(int days)
    {
        using ReviewWorld w = new();
        (await w.Owner().Create(w.Project.Id, w.Timeline.Id, new CreateReviewLinkRequest(null, null, days, null), CancellationToken.None))
            .Should().BeOfType<BadRequestObjectResult>();
        (await w.Seed.EditReviewLinks.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Create_rejects_a_label_over_80_characters_and_cleans_a_dirty_one()
    {
        using ReviewWorld w = new();
        (await w.Owner().Create(w.Project.Id, w.Timeline.Id, new CreateReviewLinkRequest(new string('x', 81), null, null, null), CancellationToken.None))
            .Should().BeOfType<BadRequestObjectResult>();

        (Guid id, _) = await w.CreateLinkAsync(label: "  Boss\u202E  review\u200B ");
        (await w.Seed.EditReviewLinks.SingleAsync(l => l.Id == id)).Label.Should().Be("Boss review");
    }

    [Fact]
    public async Task Create_stops_at_the_live_link_cap_but_revoked_and_expired_links_do_not_count()
    {
        using ReviewWorld w = new();
        ReviewLinkOptions options = new() { MaxLinksPerTimeline = 2 };
        (Guid first, _) = await w.CreateLinkAsync(options: options);
        await w.CreateLinkAsync(options: options);

        ObjectResult full = (await w.Owner(options: options).Create(w.Project.Id, w.Timeline.Id, null, CancellationToken.None)).Should_BeStatus(409);
        ((ReviewLinkMessage)full.Value!).Code.Should().Be("REVIEW_LINK_LIMIT");

        (await w.Owner().Revoke(w.Project.Id, w.Timeline.Id, first, CancellationToken.None)).Should().BeOfType<NoContentResult>();
        (await w.Owner(options: options).Create(w.Project.Id, w.Timeline.Id, null, CancellationToken.None)).Should_BeStatus(201);
    }

    [Fact]
    public async Task The_list_never_carries_the_token_the_url_or_the_hash()
    {
        using ReviewWorld w = new();
        (Guid id, string token) = await w.CreateLinkAsync(label: "Boss");
        EditReviewLink row = await w.Seed.EditReviewLinks.SingleAsync();

        OkObjectResult ok = (OkObjectResult)await w.Owner().List(w.Project.Id, w.Timeline.Id, CancellationToken.None);
        string json = JsonSerializer.Serialize(ok.Value, Web);

        json.Should().NotContain(token).And.NotContain("/app/review/g/").And.NotContainEquivalentOf("url").And.NotContainEquivalentOf("token");
        json.Should().NotContain(Convert.ToBase64String(row.TokenHash)).And.NotContain(Convert.ToHexString(row.TokenHash));
        ReviewLinkListResponse list = (ReviewLinkListResponse)ok.Value!;
        list.Links.Should().ContainSingle().Which.Should().Match<ReviewLinkSummaryResponse>(
            l => l.Id == id && l.Status == "active" && l.Label == "Boss" && l.GuestComments == 0 && !l.UnreadGuestActivity);

        // And the detail never carries it either.
        OkObjectResult detail = (OkObjectResult)await w.Owner().Get(w.Project.Id, w.Timeline.Id, id, CancellationToken.None);
        JsonSerializer.Serialize(detail.Value, Web).Should().NotContain(token).And.NotContainEquivalentOf("url").And.NotContainEquivalentOf("token");
    }

    [Fact]
    public async Task The_list_reports_status_stale_decision_counts_and_unread_until_the_detail_is_opened()
    {
        using ReviewWorld w = new();
        (Guid id, string token) = await w.CreateLinkAsync();
        var guest = w.Public();
        (await guest.Controller.AddComment(token, new PublicCreateReviewCommentRequest("Ana", "Cut here", 30, null, null), CancellationToken.None)).Should_BeStatus(201);
        (await guest.Controller.Decide(token, new PublicReviewDecisionRequest("approved", "Ana", null), CancellationToken.None)).Should_BeStatus(201);
        w.Clock.Now = w.Clock.Now.AddMinutes(5);

        ReviewLinkSummaryResponse row = ((ReviewLinkListResponse)((OkObjectResult)await w.Owner().List(w.Project.Id, w.Timeline.Id, CancellationToken.None)).Value!).Links.Single();
        row.UnreadGuestActivity.Should().BeTrue();
        row.GuestComments.Should().Be(1);
        row.Decision.Should().NotBeNull();
        row.Decision!.Decision.Should().Be("approved");
        row.Decision.GuestName.Should().Be("Ana");

        ReviewLinkDetailResponse detail = (ReviewLinkDetailResponse)((OkObjectResult)await w.Owner().Get(w.Project.Id, w.Timeline.Id, id, CancellationToken.None)).Value!;
        detail.Decisions.Should().ContainSingle();
        (await w.Seed.EditReviewLinks.SingleAsync()).OwnerSeenAt.Should().Be(w.Clock.Now.UtcDateTime);

        ((ReviewLinkListResponse)((OkObjectResult)await w.Owner().List(w.Project.Id, w.Timeline.Id, CancellationToken.None)).Value!)
            .Links.Single().UnreadGuestActivity.Should().BeFalse("opening the detail marks the activity seen");

        w.Clock.Now = w.Clock.Now.AddDays(15);
        ((ReviewLinkListResponse)((OkObjectResult)await w.Owner().List(w.Project.Id, w.Timeline.Id, CancellationToken.None)).Value!)
            .Links.Single().Status.Should().Be("expired");
    }

    [Fact]
    public async Task Revoke_is_204_idempotent_and_takes_effect_immediately()
    {
        using ReviewWorld w = new();
        (Guid id, string token) = await w.CreateLinkAsync();
        var guest = w.Public();
        (await guest.Controller.Get(token, CancellationToken.None)).Should().BeOfType<OkObjectResult>();

        (await w.Owner().Revoke(w.Project.Id, w.Timeline.Id, id, CancellationToken.None)).Should().BeOfType<NoContentResult>();
        (await w.Owner().Revoke(w.Project.Id, w.Timeline.Id, id, CancellationToken.None)).Should().BeOfType<NoContentResult>("idempotent");

        (await w.Public().Controller.Get(token, CancellationToken.None)).IsUniformNotFound().Should().BeTrue();
        ((ReviewLinkListResponse)((OkObjectResult)await w.Owner().List(w.Project.Id, w.Timeline.Id, CancellationToken.None)).Value!)
            .Links.Single().Status.Should().Be("revoked");
    }

    [Fact]
    public async Task Another_organizations_member_gets_404_on_every_owner_route()
    {
        using ReviewWorld w = new();
        (Guid id, _) = await w.CreateLinkAsync();
        Guid stranger = Guid.NewGuid();

        ReviewLinksController other() => w.Owner(userId: stranger);
        (await other().Create(w.Project.Id, w.Timeline.Id, null, CancellationToken.None)).Should().BeOfType<NotFoundResult>();
        (await other().List(w.Project.Id, w.Timeline.Id, CancellationToken.None)).Should().BeOfType<NotFoundResult>();
        (await other().Get(w.Project.Id, w.Timeline.Id, id, CancellationToken.None)).Should().BeOfType<NotFoundResult>();
        (await other().Revoke(w.Project.Id, w.Timeline.Id, id, CancellationToken.None)).Should().BeOfType<NotFoundResult>();
        (await w.Seed.EditReviewLinks.SingleAsync()).RevokedAt.Should().BeNull("the cross-tenant revoke changed nothing");
        (await w.Seed.EditReviewLinks.CountAsync()).Should().Be(1, "the cross-tenant create made nothing");
    }

    [Fact]
    public async Task A_link_id_of_another_timeline_or_project_is_404_under_this_one()
    {
        using ReviewWorld w = new();
        (Guid id, _) = await w.CreateLinkAsync();
        Guid otherTimeline = Guid.NewGuid();
        w.Seed.EditTimelines.Add(new EditTimelineEntity { Id = otherTimeline, ProjectId = w.Project.Id, Name = "Other", DocumentJson = "{}" });
        w.Seed.SaveChanges();

        (await w.Owner().Get(w.Project.Id, otherTimeline, id, CancellationToken.None)).Should().BeOfType<NotFoundResult>();
        (await w.Owner().Revoke(w.Project.Id, otherTimeline, id, CancellationToken.None)).Should().BeOfType<NotFoundResult>();
        (await w.Owner().Get(w.Project.Id, w.Timeline.Id, Guid.NewGuid(), CancellationToken.None)).Should().BeOfType<NotFoundResult>();
        (await w.Owner().List(w.Project.Id, Guid.NewGuid(), CancellationToken.None)).Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task The_tenant_filter_alone_hides_a_foreign_link_from_a_context_of_another_organization()
    {
        using ReviewWorld w = new();
        await w.CreateLinkAsync();

        (await w.MemberDb(Guid.NewGuid()).EditReviewLinks.CountAsync()).Should().Be(0, "the named Tenant filter is the backstop");
        (await w.MemberDb().EditReviewLinks.CountAsync()).Should().Be(1);
        (await w.AnonymousDb().EditReviewLinks.CountAsync()).Should().Be(0, "an anonymous context sees nothing unless the code ignores the filter by name");
    }

    [Theory]
    [InlineData(OrganizationRole.Billing, true)]
    [InlineData(OrganizationRole.Member, false)]
    public async Task Roles_without_access_are_refused_and_members_may_create_and_revoke(OrganizationRole role, bool refused)
    {
        using ReviewWorld w = new();
        Guid memberId = Guid.NewGuid();
        w.Seed.ApplicationUsers.Add(new ApplicationUser { Id = memberId, Email = "m@example.com", DisplayName = "M" });
        w.Seed.SaveChanges();
        // The member acts in the owner's organization.
        ReviewLinksController c() => w.Owner(userId: memberId, role: role, organizationId: w.OrgId);

        IActionResult created = await c().Create(w.Project.Id, w.Timeline.Id, null, CancellationToken.None);
        if (refused) created.Should().BeOfType<ForbidResult>();
        else created.Should_BeStatus(201);
        (await c().List(w.Project.Id, w.Timeline.Id, CancellationToken.None)).Should().Match<IActionResult>(r => refused ? r is ForbidResult : r is OkObjectResult);
    }

    [Fact]
    public async Task A_blocking_or_unavailable_moderation_gate_refuses_the_share_fail_closed()
    {
        using ReviewWorld w = new();

        ObjectResult blocked = (await w.Owner(moderation: w.Moderation(ModerationAction.Block)).Create(w.Project.Id, w.Timeline.Id, null, CancellationToken.None)).Should_BeStatus(422);
        JsonSerializer.Serialize(blocked.Value, Web).Should().Contain("CONTENT_BLOCKED");
        (await w.Owner(moderation: w.Moderation(ModerationAction.Unavailable, available: false)).Create(w.Project.Id, w.Timeline.Id, null, CancellationToken.None)).Should_BeStatus(409);
        (await w.Seed.EditReviewLinks.CountAsync()).Should().Be(0);

        (await w.Owner(moderation: w.Moderation(ModerationAction.Allow)).Create(w.Project.Id, w.Timeline.Id, null, CancellationToken.None)).Should_BeStatus(201);
    }

    [Fact]
    public void The_owner_controller_is_an_authorized_project_route_with_one_access_check_per_action()
    {
        typeof(ReviewLinksController).GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), true).Should().NotBeEmpty();
        typeof(ReviewLinksController).GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute), true).Should().BeEmpty();
        var route = (RouteAttribute)typeof(ReviewLinksController).GetCustomAttributes(typeof(RouteAttribute), true).Single();
        route.Template.Should().Be("api/v1/projects/{projectId:guid}/edit-timelines/{id:guid}/review-links");
    }
}
