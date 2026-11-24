#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Services.Editing.ReviewLinks;
using ReelBolt.Inference.Api.Services.Notifications;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Moderation;
using ReelBolt.Shared.Tenancy;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.ReviewLinks;

/// <summary>Public side: the uniform 404 matrix, the pinned-render-only media route, guest writes, caps and limits.</summary>
public class ReviewLinkPublicTests
{
    private const string Valid = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    private static Task<IActionResult>[] AllRoutes(ReviewWorld w, string? token) =>
    [
        w.Public().Controller.Get(token!, CancellationToken.None),
        w.Public().Controller.Media(token!, CancellationToken.None),
        w.Public().Controller.AddComment(token!, new PublicCreateReviewCommentRequest("Ana", "hello", 1, null, null), CancellationToken.None),
        w.Public().Controller.Decide(token!, new PublicReviewDecisionRequest("approved", "Ana", null), CancellationToken.None),
    ];

    private static async Task ShouldAllBeTheUniform404(ReviewWorld w, string? token)
    {
        foreach (Task<IActionResult> call in AllRoutes(w, token))
            (await call).IsUniformNotFound().Should().BeTrue("every public route answers every failure with the same empty-object 404");
    }

    // ─── the route ────────────────────────────────────────────────────────────────

    [Fact]
    public void The_public_controller_is_anonymous_at_the_documented_path_with_the_header_filter_and_no_logger()
    {
        Type t = typeof(ReviewLinksPublicController);
        t.GetCustomAttributes(typeof(AllowAnonymousAttribute), true).Should().NotBeEmpty();
        ((RouteAttribute)t.GetCustomAttributes(typeof(RouteAttribute), true).Single()).Template.Should().Be("api/v1/review-links/{token}");
        t.GetCustomAttributes(typeof(ReviewLinkPublicResponseAttribute), true).Should().NotBeEmpty();
        t.GetConstructors().Single().GetParameters().Select(p => p.ParameterType.Name)
            .Should().NotContain(n => n.Contains("ILogger"), "the controller cannot log a token because it has no logger");
    }

    [Fact]
    public void Every_response_gets_no_store_no_referrer_nosniff_and_no_cors_headers()
    {
        DefaultHttpContext http = new();
        ActionContext ac = new(http, new RouteData(), new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor());
        ResultExecutingContext ctx = new(ac, [], new NotFoundResult(), new object());

        new ReviewLinkPublicResponseAttribute().OnResultExecuting(ctx);

        IHeaderDictionary h = http.Response.Headers;
        h.CacheControl.ToString().Should().Be("private, no-store");
        h["Referrer-Policy"].ToString().Should().Be("no-referrer");
        h["X-Content-Type-Options"].ToString().Should().Be("nosniff");
        h["X-Robots-Tag"].ToString().Should().Contain("noindex");
        h.Keys.Should().NotContain(k => k.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase));
    }

    // ─── the uniform 404 matrix ───────────────────────────────────────────────────

    [Fact]
    public async Task Unknown_and_malformed_tokens_answer_the_uniform_404_on_every_route()
    {
        using ReviewWorld w = new();
        await w.CreateLinkAsync();

        foreach (string? token in new string?[] { Valid, ReviewLinkTokens.Generate(), null, "", "short", new string('A', 64), Valid[..42] + "+", Valid[..42] + ".", "..%2F..%2Fetc" })
            await ShouldAllBeTheUniform404(w, token);
    }

    public static TheoryData<string> Dead => new()
    {
        "expired", "revoked", "timeline-deleted", "render-result-deleted", "render-execution-deleted", "render-superseded",
        "project-deleted", "organization-deleted", "creator-removed", "creator-demoted", "creator-deleted", "link-deleted",
        "render-not-passed",
    };

    [Theory]
    [MemberData(nameof(Dead))]
    public async Task A_dead_link_answers_the_uniform_404_on_every_route(string how)
    {
        using ReviewWorld w = new();
        (Guid id, string token) = await w.CreateLinkAsync();
        (await w.Public().Controller.Get(token, CancellationToken.None)).Should().BeOfType<OkObjectResult>("alive before it is killed");

        InferenceApiDbContext_Kill(w, how, id);

        await ShouldAllBeTheUniform404(w, token);
    }

    [Fact]
    public async Task A_link_over_a_held_render_answers_exactly_like_an_ended_link()
    {
        using ReviewWorld held = new();
        (_, string heldToken) = await held.CreateLinkAsync();
        (await held.Public().Controller.Get(heldToken, CancellationToken.None)).IsUniformNotFound().Should().BeFalse("the link works before the marker exists");
        held.Seed.Projects.Single().CsamOutputHoldAt = new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await held.Seed.SaveChangesAsync();

        using ReviewWorld ended = new();
        (Guid endedId, string endedToken) = await ended.CreateLinkAsync();
        ended.Seed.EditReviewLinks.Single(l => l.Id == endedId).RevokedAt = ended.Clock.Now.UtcDateTime;
        await ended.Seed.SaveChangesAsync();

        Func<ReviewLinksPublicController, string, Task<IActionResult>>[] routes =
        [
            (c, t) => c.Get(t, CancellationToken.None),
            (c, t) => c.Media(t, CancellationToken.None),
            (c, t) => c.AddComment(t, new PublicCreateReviewCommentRequest("Ana", "hello", 1, null, null), CancellationToken.None),
            (c, t) => c.Decide(t, new PublicReviewDecisionRequest("approved", "Ana", null), CancellationToken.None),
        ];
        foreach (Func<ReviewLinksPublicController, string, Task<IActionResult>> route in routes)
        {
            WireAnswer a = await WireOf(held, heldToken, route);
            WireAnswer b = await WireOf(ended, endedToken, route);
            a.Status.Should().Be(404);
            a.Should().Be(b);
        }
    }

    private sealed record WireAnswer(string Type, int? Status, string? ContentType, string Body, string Headers);

    private static async Task<WireAnswer> WireOf(
        ReviewWorld w, string token, Func<ReviewLinksPublicController, string, Task<IActionResult>> route)
    {
        ReviewLinksPublicController controller = w.Public().Controller;
        IActionResult result = await route(controller, token);
        (int? status, string? contentType, string body) = result switch
        {
            ContentResult c => (c.StatusCode, c.ContentType, System.Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(c.Content ?? string.Empty))),
            ObjectResult o => (o.StatusCode, null, System.Text.Json.JsonSerializer.Serialize(o.Value)),
            IStatusCodeActionResult s => (s.StatusCode, null, string.Empty),
            _ => (null, null, string.Empty),
        };
        string headers = string.Join("|", controller.Response.Headers.Select(h => $"{h.Key}={h.Value}").Order());
        return new WireAnswer(result.GetType().Name, status, contentType, body, headers);
    }

    private static void InferenceApiDbContext_Kill(ReviewWorld w, string how, Guid linkId)
    {
        var db = w.Seed;
        switch (how)
        {
            case "expired": w.Clock.Now = w.Clock.Now.AddDays(15); break;
            case "revoked": db.EditReviewLinks.Single(l => l.Id == linkId).RevokedAt = w.Clock.Now.UtcDateTime; break;
            case "timeline-deleted": db.EditTimelines.Single(t => t.Id == w.Timeline.Id).DeletedAt = w.Clock.Now.UtcDateTime; break;
            case "render-result-deleted": db.WorkflowStepResults.RemoveRange(db.WorkflowStepResults); break;
            case "render-execution-deleted": db.WorkflowExecutions.RemoveRange(db.WorkflowExecutions); break;
            case "render-superseded": db.WorkflowStepResults.Single().SupersededAt = w.Clock.Now.UtcDateTime; break;
            case "render-not-passed": db.WorkflowExecutions.Single().Status = ExecutionStatus.Failed; break;
            case "project-deleted": db.Projects.RemoveRange(db.Projects); break;
            case "organization-deleted": db.Organizations.Single().DeletedAt = w.Clock.Now.UtcDateTime; break;
            case "creator-removed": db.OrganizationMemberships.RemoveRange(db.OrganizationMemberships); break;
            case "creator-demoted": db.OrganizationMemberships.Single().Role = OrganizationRole.Billing; break;
            case "creator-deleted": db.ApplicationUsers.Single().DeletionRequestedAt = w.Clock.Now.UtcDateTime; break;
            case "link-deleted": db.EditReviewLinks.RemoveRange(db.EditReviewLinks); break;
            default: throw new ArgumentOutOfRangeException(nameof(how));
        }

        db.SaveChanges();
    }

    [Fact]
    public async Task A_token_for_one_link_does_not_work_for_a_link_that_is_dead_and_works_for_the_live_one()
    {
        using ReviewWorld w = new();
        (Guid deadId, string dead) = await w.CreateLinkAsync();
        (_, string live) = await w.CreateLinkAsync();
        await w.Owner().Revoke(w.Project.Id, w.Timeline.Id, deadId, CancellationToken.None);

        await ShouldAllBeTheUniform404(w, dead);
        (await w.Public().Controller.Get(live, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
    }

    // ─── the page data ────────────────────────────────────────────────────────────

    private static Guid GuestRoot(ReviewWorld w, Guid link, string body = "guest note")
    {
        Guid id = Guid.NewGuid();
        w.Seed.EditTimelineComments.Add(new EditTimelineComment
        {
            Id = id,
            ProjectId = w.Project.Id,
            TimelineId = w.Timeline.Id,
            AuthorUserId = w.UserId,
            AuthorName = "Ana",
            GuestLinkId = link,
            GuestName = "Ana",
            Frame = 1,
            Body = body,
            CreatedAt = ReviewWorld.Now.UtcDateTime,
        });
        w.Seed.SaveChanges();
        return id;
    }

    [Fact]
    public async Task The_page_hides_member_notes_other_links_and_other_threads_but_shows_member_replies_to_its_guests()
    {
        using ReviewWorld w = new();
        (Guid mine, string token) = await w.CreateLinkAsync();
        (Guid theirs, _) = await w.CreateLinkAsync();
        Guid myRoot = GuestRoot(w, mine, "mine");
        Guid theirRoot = GuestRoot(w, theirs, "theirs");
        void Add(string body, Guid? parent) => w.Seed.EditTimelineComments.Add(new EditTimelineComment
        {
            Id = Guid.NewGuid(),
            ProjectId = w.Project.Id,
            TimelineId = w.Timeline.Id,
            AuthorUserId = w.UserId,
            AuthorName = "m",
            ParentId = parent,
            Frame = 2,
            Body = body,
            CreatedAt = ReviewWorld.Now.UtcDateTime,
        });
        Add("member top-level", null);
        Add("member reply to mine", myRoot);
        Add("member reply to theirs", theirRoot);
        w.Seed.SaveChanges();

        PublicReviewResponse page = (PublicReviewResponse)((OkObjectResult)await w.Public().Controller.Get(token, CancellationToken.None)).Value!;

        page.Comments.Select(c => c.Body).Should().BeEquivalentTo("mine", "member reply to mine");
    }

    [Fact]
    public async Task The_page_carries_only_what_a_guest_may_see_and_counts_one_view_per_minute()
    {
        using ReviewWorld w = new();
        (Guid link1, string token) = await w.CreateLinkAsync();
        // A member's reply in a guest thread, written under their account with their email as the stored name.
        Guid guestRoot = GuestRoot(w, link1);
        w.Seed.EditTimelineComments.Add(new EditTimelineComment
        {
            Id = Guid.NewGuid(),
            ParentId = guestRoot,
            ProjectId = w.Project.Id,
            TimelineId = w.Timeline.Id,
            AuthorUserId = w.UserId,
            AuthorName = "owner@example.com",
            Frame = 12,
            Body = "Tighten this",
            CreatedAt = ReviewWorld.Now.UtcDateTime,
        });
        w.Seed.SaveChanges();

        OkObjectResult ok = (OkObjectResult)await w.Public().Controller.Get(token, CancellationToken.None);
        PublicReviewResponse page = (PublicReviewResponse)ok.Value!;

        page.Title.Should().Be("Client cut");
        page.AllowComments.Should().BeTrue();
        page.DurationSec.Should().Be(10);
        page.Stale.Should().BeFalse();
        page.Comments.Single(c => c.ParentId != null).Should().Match<PublicReviewCommentResponse>(
            c => c.AuthorName == "Olivia Owner" && !c.Guest && c.Body == "Tighten this" && c.Frame == 12);
        string json = JsonSerializer.Serialize(page, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        json.Should().NotContain("owner@example.com").And.NotContain(w.UserId.ToString()).And.NotContain(w.Project.Id.ToString())
            .And.NotContain(w.OrgId.ToString()).And.NotContain(w.RenderKey).And.NotContain("outputFiles");

        (await w.Public().Controller.Get(token, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        (await w.Seed.EditReviewLinks.AsNoTracking().SingleAsync()).ViewCount.Should().Be(1, "a second view inside the minute is not counted");
        w.Clock.Now = w.Clock.Now.AddSeconds(61);
        (await w.Public().Controller.Get(token, CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        EditReviewLink row = await w.Seed.EditReviewLinks.AsNoTracking().SingleAsync();
        row.ViewCount.Should().Be(2);
        row.LastViewedAt.Should().Be(w.Clock.Now.UtcDateTime);
    }

    [Fact]
    public async Task A_member_whose_display_name_is_an_email_shows_as_team_member_and_stale_is_reported()
    {
        using ReviewWorld w = new();
        (Guid link2, string token) = await w.CreateLinkAsync();
        w.Seed.ApplicationUsers.Single().DisplayName = "someone@example.com";
        w.Seed.EditTimelines.Single().Revision = 9;
        Guid root2 = GuestRoot(w, link2);
        w.Seed.EditTimelineComments.Add(new EditTimelineComment
        {
            Id = Guid.NewGuid(),
            ParentId = root2,
            ProjectId = w.Project.Id,
            TimelineId = w.Timeline.Id,
            AuthorUserId = w.UserId,
            AuthorName = "x",
            Frame = 1,
            Body = "b",
        });
        w.Seed.SaveChanges();

        PublicReviewResponse page = (PublicReviewResponse)((OkObjectResult)await w.Public().Controller.Get(token, CancellationToken.None)).Value!;

        page.Stale.Should().BeTrue("the timeline moved past the pinned revision");
        page.Comments.Single(c => c.ParentId != null).AuthorName.Should().Be("Team member");
    }

    // ─── media ────────────────────────────────────────────────────────────────────

    private static async Task<byte[]> BodyOf(IActionResult result)
    {
        FileStreamResult file = result.Should().BeOfType<FileStreamResult>().Subject;
        using MemoryStream copy = new();
        await file.FileStream.CopyToAsync(copy);
        return copy.ToArray();
    }

    [Fact]
    public async Task Media_streams_the_pinned_render_with_range_206_inline_and_safe_headers()
    {
        using ReviewWorld w = new();
        (_, string token) = await w.CreateLinkAsync();
        ReviewWorld.PublicHarness h = w.Public(range: "bytes=10-109");

        IActionResult result = await h.Controller.Media(token, CancellationToken.None);

        (await BodyOf(result)).Should().Equal(ReviewWorld.Bytes(1000).Skip(10).Take(100));
        IHeaderDictionary headers = h.Controller.Response.Headers;
        h.Controller.Response.StatusCode.Should().Be(StatusCodes.Status206PartialContent);
        headers.ContentRange.ToString().Should().Be("bytes 10-109/1000");
        headers.ContentDisposition.ToString().Should().Be("inline", "no file name leaks the owner's naming");
        ((FileStreamResult)result).ContentType.Should().Be("video/mp4");
        headers["X-Content-Type-Options"].ToString().Should().Be("nosniff");
    }

    [Fact]
    public async Task Media_without_a_range_streams_the_whole_file()
    {
        using ReviewWorld w = new();
        (_, string token) = await w.CreateLinkAsync();
        (await BodyOf(await w.Public().Controller.Media(token, CancellationToken.None))).Length.Should().Be(1000);
    }

    [Fact]
    public async Task A_token_reaches_only_its_pinned_render_never_another_render_or_file_of_the_project()
    {
        using ReviewWorld w = new();
        string pinnedKey = w.RenderKey;
        (_, string token) = await w.CreateLinkAsync();
        // A newer render, another project file and a foreign project's render all exist in the same bucket.
        (_, _, string newerKey) = w.AddRender(queuedAt: ReviewWorld.Now.UtcDateTime.AddHours(2), objectBytes: 77);
        w.Objects[$"projects/{w.Project.Id}/userFiles/secret.mp4"] = ReviewWorld.Bytes(33);
        w.Objects[$"projects/{Guid.NewGuid()}/outputFiles/{Guid.NewGuid()}/theirs.mp4"] = ReviewWorld.Bytes(44);

        byte[] body = await BodyOf(await w.Public().Controller.Media(token, CancellationToken.None));

        body.Should().Equal(ReviewWorld.Bytes(1000), "the bytes of the render the link pinned");
        w.S3.Verify(s => s.GetObjectAsync(It.Is<Amazon.S3.Model.GetObjectRequest>(r => r.Key != pinnedKey), It.IsAny<CancellationToken>()), Times.Never);
        w.S3.Verify(s => s.GetObjectMetadataAsync(It.IsAny<string>(), It.Is<string>(k => k != pinnedKey), It.IsAny<CancellationToken>()), Times.Never);
        newerKey.Should().NotBe(pinnedKey);

        // The route has no way to name another file: the only inputs are the token and the Range header.
        typeof(ReviewLinksPublicController).GetMethod(nameof(ReviewLinksPublicController.Media))!
            .GetParameters().Select(p => p.Name).Should().Equal("token", "ct");
    }

    [Fact]
    public async Task Media_for_an_output_that_is_not_mp4_or_is_missing_is_the_uniform_404()
    {
        using ReviewWorld w = new();
        (_, string token) = await w.CreateLinkAsync();

        w.Seed.WorkflowStepResults.Single().OutputStorageKey = w.RenderKey[..^4] + ".mov";
        w.Seed.SaveChanges();
        w.Objects[w.RenderKey[..^4] + ".mov"] = ReviewWorld.Bytes(10);
        (await w.Public().Controller.Media(token, CancellationToken.None)).IsUniformNotFound().Should().BeTrue("mp4 only");

        w.Seed.WorkflowStepResults.Single().OutputStorageKey = w.RenderKey;
        w.Seed.SaveChanges();
        w.Objects.Remove(w.RenderKey);
        (await w.Public().Controller.Media(token, CancellationToken.None)).IsUniformNotFound().Should().BeTrue("the object is gone");
    }

    [Fact]
    public async Task Media_is_refused_as_the_uniform_404_when_the_moderation_gate_blocks_or_is_unavailable()
    {
        using ReviewWorld w = new();
        (_, string token) = await w.CreateLinkAsync();

        (await w.Public(moderation: w.Moderation(ModerationAction.Block)).Controller.Media(token, CancellationToken.None)).IsUniformNotFound().Should().BeTrue();
        (await w.Public(moderation: w.Moderation(ModerationAction.Unavailable, available: false)).Controller.Media(token, CancellationToken.None)).IsUniformNotFound().Should().BeTrue("fail closed");
        (await BodyOf(await w.Public(moderation: w.Moderation(ModerationAction.Allow)).Controller.Media(token, CancellationToken.None))).Length.Should().Be(1000);
    }

    // ─── guest comments ───────────────────────────────────────────────────────────

    [Fact]
    public async Task A_guest_comment_is_stored_as_a_guest_with_a_normalised_name_and_listed_for_members()
    {
        using ReviewWorld w = new();
        (_, string token) = await w.CreateLinkAsync();

        IActionResult result = await w.Public().Controller.AddComment(
            token, new PublicCreateReviewCommentRequest("  Ame\u0301lie\u202E  Client ", "Logo is  too small\u200B", 45, 10, null), CancellationToken.None);

        PublicReviewCommentResponse created = (PublicReviewCommentResponse)result.Should_BeStatus(201).Value!;
        created.Should().Match<PublicReviewCommentResponse>(c => c.Guest && c.AuthorName == "Amélie Client" && c.Body == "Logo is too small" && c.Frame == 45 && c.DurationFrames == 10);
        EditTimelineComment row = await w.Seed.EditTimelineComments.SingleAsync();
        row.AuthorUserId.Should().Be(Guid.Empty);
        row.GuestLinkId.Should().NotBeNull();
        row.GuestName.Should().Be("Amélie Client");
        row.ProjectId.Should().Be(w.Project.Id);
        row.TimelineId.Should().Be(w.Timeline.Id);

        PublicReviewResponse page = (PublicReviewResponse)((OkObjectResult)await w.Public().Controller.Get(token, CancellationToken.None)).Value!;
        page.Comments.Should().ContainSingle(c => c.Guest && c.AuthorName == "Amélie Client");
    }

    [Fact]
    public async Task Xss_ish_strings_are_stored_and_returned_as_plain_text()
    {
        using ReviewWorld w = new();
        (_, string token) = await w.CreateLinkAsync();
        const string payload = "<img src=x onerror=alert(1)><script>alert('x')</script>";

        await w.Public().Controller.AddComment(token, new PublicCreateReviewCommentRequest("<b>Eve</b>", payload, 5, null, null), CancellationToken.None);

        EditTimelineComment row = await w.Seed.EditTimelineComments.SingleAsync();
        row.Body.Should().Be(payload, "stored exactly as typed: it is only ever rendered as text");
        row.GuestName.Should().Be("<b>Eve</b>");
    }

    [Theory]
    [InlineData("", "hello", 1, "Enter your name.")]
    [InlineData("   ", "hello", 1, "Enter your name.")]
    [InlineData("\u200B\u202E", "hello", 1, "Enter your name.")]
    [InlineData("Ana", "", 1, "Write a note first.")]
    [InlineData("Ana", "\u200B\n", 1, "Write a note first.")]
    [InlineData("Ana", "hello", -1, "That moment is outside the video.")]
    [InlineData("Ana", "hello", 5000, "That moment is outside the video.")]
    public async Task Invalid_comments_are_400_with_a_plain_message_and_store_nothing(string name, string body, long frame, string message)
    {
        using ReviewWorld w = new();
        (_, string token) = await w.CreateLinkAsync();

        IActionResult result = await w.Public().Controller.AddComment(token, new PublicCreateReviewCommentRequest(name, body, frame, null, null), CancellationToken.None);

        BadRequestObjectResult bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        JsonSerializer.Serialize(bad.Value).Should().Contain(message);
        (await w.Seed.EditTimelineComments.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Name_and_body_length_limits_apply_after_normalisation()
    {
        using ReviewWorld w = new();
        (_, string token) = await w.CreateLinkAsync();

        (await w.Public().Controller.AddComment(token, new PublicCreateReviewCommentRequest(new string('n', 81), "ok", 1, null, null), CancellationToken.None)).Should().BeOfType<BadRequestObjectResult>();
        (await w.Public().Controller.AddComment(token, new PublicCreateReviewCommentRequest("Ana", new string('b', 2001), 1, null, null), CancellationToken.None)).Should().BeOfType<BadRequestObjectResult>();
        // Exactly at the limits, padded with characters that normalise away, is fine.
        (await w.Public().Controller.AddComment(token, new PublicCreateReviewCommentRequest(new string('n', 80) + "\u200B\u200B", new string('b', 2000) + "\u202E", 1, null, null), CancellationToken.None)).Should_BeStatus(201);
    }

    [Fact]
    public async Task A_reply_must_name_a_live_root_comment_of_this_timeline_and_takes_its_frame()
    {
        using ReviewWorld w = new();
        (_, string token) = await w.CreateLinkAsync();
        PublicReviewCommentResponse root = (PublicReviewCommentResponse)(await w.Public().Controller.AddComment(token, new PublicCreateReviewCommentRequest("Ana", "root", 40, 5, null), CancellationToken.None)).Should_BeStatus(201).Value!;

        PublicReviewCommentResponse reply = (PublicReviewCommentResponse)(await w.Public().Controller.AddComment(token, new PublicCreateReviewCommentRequest("Ben", "reply", 3, 9, root.Id), CancellationToken.None)).Should_BeStatus(201).Value!;
        reply.ParentId.Should().Be(root.Id);
        reply.Frame.Should().Be(40);
        reply.DurationFrames.Should().BeNull();

        (await w.Public().Controller.AddComment(token, new PublicCreateReviewCommentRequest("Ben", "x", 3, null, Guid.NewGuid()), CancellationToken.None)).Should().BeOfType<BadRequestObjectResult>("unknown parent");
        (await w.Public().Controller.AddComment(token, new PublicCreateReviewCommentRequest("Ben", "x", 3, null, reply.Id), CancellationToken.None)).Should().BeOfType<BadRequestObjectResult>("a reply is one level deep");

        // A comment of ANOTHER timeline of the same project is not a valid parent.
        Guid other = Guid.NewGuid();
        w.Seed.EditTimelines.Add(new EditTimelineEntity { Id = other, ProjectId = w.Project.Id, Name = "O", DocumentJson = "{}" });
        Guid foreign = Guid.NewGuid();
        w.Seed.EditTimelineComments.Add(new EditTimelineComment { Id = foreign, ProjectId = w.Project.Id, TimelineId = other, AuthorUserId = w.UserId, AuthorName = "m", Frame = 1, Body = "b" });
        w.Seed.SaveChanges();
        (await w.Public().Controller.AddComment(token, new PublicCreateReviewCommentRequest("Ben", "x", 3, null, foreign), CancellationToken.None)).Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task A_link_with_comments_off_answers_the_uniform_404_to_comments_but_still_allows_a_decision()
    {
        using ReviewWorld w = new();
        (_, string token) = await w.CreateLinkAsync(allowComments: false);

        (await w.Public().Controller.AddComment(token, new PublicCreateReviewCommentRequest("Ana", "hi", 1, null, null), CancellationToken.None)).IsUniformNotFound().Should().BeTrue();
        PublicReviewResponse page = (PublicReviewResponse)((OkObjectResult)await w.Public().Controller.Get(token, CancellationToken.None)).Value!;
        page.AllowComments.Should().BeFalse();
        (await w.Public().Controller.Decide(token, new PublicReviewDecisionRequest("approved", "Ana", null), CancellationToken.None)).Should_BeStatus(201);
    }

    [Fact]
    public async Task The_per_link_comment_cap_is_a_409_and_does_not_affect_another_link()
    {
        using ReviewWorld w = new();
        ReviewLinkOptions options = new() { MaxGuestCommentsPerLink = 3, GuestCommentsPerMinute = 100 };
        (_, string token) = await w.CreateLinkAsync();
        (_, string other) = await w.CreateLinkAsync();
        ReviewLinkWriteLimiter limiter = new();

        for (int i = 0; i < 3; i++)
            (await w.Public(limiter, options).Controller.AddComment(token, new PublicCreateReviewCommentRequest("Ana", $"n{i}", i, null, null), CancellationToken.None)).Should_BeStatus(201);
        ObjectResult capped = (await w.Public(limiter, options).Controller.AddComment(token, new PublicCreateReviewCommentRequest("Ana", "one more", 1, null, null), CancellationToken.None)).Should_BeStatus(409);
        ((ReviewLinkMessage)capped.Value!).Code.Should().Be("NOTE_LIMIT");
        (await w.Public(limiter, options).Controller.AddComment(other, new PublicCreateReviewCommentRequest("Ben", "fine", 1, null, null), CancellationToken.None)).Should_BeStatus(201);
    }

    [Fact]
    public async Task Guest_writes_are_limited_per_link_per_minute_and_recover_when_the_window_slides()
    {
        using ReviewWorld w = new();
        ReviewLinkOptions options = new() { GuestCommentsPerMinute = 3 };
        (_, string token) = await w.CreateLinkAsync();
        (_, string other) = await w.CreateLinkAsync();
        ReviewLinkWriteLimiter limiter = new();

        for (int i = 0; i < 3; i++)
            (await w.Public(limiter, options).Controller.AddComment(token, new PublicCreateReviewCommentRequest("Ana", $"n{i}", 1, null, null), CancellationToken.None)).Should_BeStatus(201);

        ReviewWorld.PublicHarness blocked = w.Public(limiter, options);
        (await blocked.Controller.AddComment(token, new PublicCreateReviewCommentRequest("Ana", "n3", 1, null, null), CancellationToken.None)).Should_BeStatus(429);
        blocked.Controller.Response.Headers.RetryAfter.ToString().Should().Be("60");
        (await w.Public(limiter, options).Controller.Decide(token, new PublicReviewDecisionRequest("approved", "Ana", null), CancellationToken.None)).Should_BeStatus(429);
        (await w.Public(limiter, options).Controller.AddComment(other, new PublicCreateReviewCommentRequest("Ben", "x", 1, null, null), CancellationToken.None)).Should_BeStatus(201);

        w.Clock.Now = w.Clock.Now.AddSeconds(61);
        (await w.Public(limiter, options).Controller.AddComment(token, new PublicCreateReviewCommentRequest("Ana", "later", 1, null, null), CancellationToken.None)).Should_BeStatus(201);
        (await w.Seed.EditTimelineComments.CountAsync()).Should().Be(5);
    }

    [Fact]
    public async Task Rate_limited_requests_do_not_reveal_a_dead_token()
    {
        using ReviewWorld w = new();
        ReviewLinkWriteLimiter limiter = new();
        ReviewLinkOptions options = new() { GuestCommentsPerMinute = 1 };

        for (int i = 0; i < 5; i++)
            (await w.Public(limiter, options).Controller.AddComment(ReviewLinkTokens.Generate(), new PublicCreateReviewCommentRequest("A", "b", 1, null, null), CancellationToken.None)).IsUniformNotFound().Should().BeTrue("an unknown token never reaches the limiter");
    }

    // ─── decisions ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_decision_is_append_only_and_the_latest_is_the_state()
    {
        using ReviewWorld w = new();
        (_, string token) = await w.CreateLinkAsync();
        ReviewLinkOptions options = new() { GuestCommentsPerMinute = 100 };

        ReviewLinkDecisionResponse first = (ReviewLinkDecisionResponse)(await w.Public(null, options).Controller.Decide(token, new PublicReviewDecisionRequest("changesRequested", " Ana ", "  Shorter intro\u200B "), CancellationToken.None)).Should_BeStatus(201).Value!;
        first.Should().Match<ReviewLinkDecisionResponse>(d => d.Decision == "changesRequested" && d.GuestName == "Ana" && d.Note == "Shorter intro");
        w.Clock.Now = w.Clock.Now.AddMinutes(1);
        await w.Public(null, options).Controller.Decide(token, new PublicReviewDecisionRequest("approved", "Ana", null), CancellationToken.None);

        (await w.Seed.EditReviewDecisions.CountAsync()).Should().Be(2, "history is kept");
        PublicReviewResponse page = (PublicReviewResponse)((OkObjectResult)await w.Public().Controller.Get(token, CancellationToken.None)).Value!;
        page.Decision.Should().Match<PublicReviewDecisionResponse>(d => d.Decision == "approved" && d.GuestName == "Ana");
    }

    [Theory]
    [InlineData(null, "Ana", "Choose approve or request changes.")]
    [InlineData("maybe", "Ana", "Choose approve or request changes.")]
    [InlineData("Approved", "Ana", "Choose approve or request changes.")]
    [InlineData("approved", "", "Enter your name.")]
    [InlineData("changesRequested", "Ana", "Say what to change, or leave a note on the video first.")]
    public async Task Invalid_decisions_are_400_with_a_plain_message(string? decision, string name, string message)
    {
        using ReviewWorld w = new();
        (_, string token) = await w.CreateLinkAsync();

        IActionResult result = await w.Public().Controller.Decide(token, new PublicReviewDecisionRequest(decision, name, null), CancellationToken.None);

        JsonSerializer.Serialize(result.Should().BeOfType<BadRequestObjectResult>().Subject.Value).Should().Contain(message);
        (await w.Seed.EditReviewDecisions.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Changes_requested_is_allowed_with_a_note_or_after_the_guest_commented()
    {
        using ReviewWorld w = new();
        ReviewLinkOptions options = new() { GuestCommentsPerMinute = 100 };
        (_, string token) = await w.CreateLinkAsync();

        (await w.Public(null, options).Controller.Decide(token, new PublicReviewDecisionRequest("changesRequested", "Ana", "fix the logo"), CancellationToken.None)).Should_BeStatus(201);
        (await w.Public(null, options).Controller.Decide(token, new PublicReviewDecisionRequest("changesRequested", "Ben", null), CancellationToken.None)).Should().BeOfType<BadRequestObjectResult>();
        await w.Public(null, options).Controller.AddComment(token, new PublicCreateReviewCommentRequest("Ben", "see here", 4, null, null), CancellationToken.None);
        (await w.Public(null, options).Controller.Decide(token, new PublicReviewDecisionRequest("changesRequested", "Ben", null), CancellationToken.None)).Should_BeStatus(201);
    }

    [Fact]
    public async Task A_link_records_at_most_20_decisions()
    {
        using ReviewWorld w = new();
        ReviewLinkOptions options = new() { GuestCommentsPerMinute = 1000 };
        (_, string token) = await w.CreateLinkAsync();
        ReviewLinkWriteLimiter limiter = new();

        for (int i = 0; i < ReviewLinkOptions.MaxDecisionsPerLink; i++)
            (await w.Public(limiter, options).Controller.Decide(token, new PublicReviewDecisionRequest("approved", "Ana", null), CancellationToken.None)).Should_BeStatus(201);
        ObjectResult capped = (await w.Public(limiter, options).Controller.Decide(token, new PublicReviewDecisionRequest("approved", "Ana", null), CancellationToken.None)).Should_BeStatus(409);
        ((ReviewLinkMessage)capped.Value!).Code.Should().Be("DECISION_LIMIT");
    }

    [Fact]
    public async Task A_decision_note_over_1000_characters_is_refused()
    {
        using ReviewWorld w = new();
        (_, string token) = await w.CreateLinkAsync();
        (await w.Public().Controller.Decide(token, new PublicReviewDecisionRequest("approved", "Ana", new string('n', 1001)), CancellationToken.None)).Should().BeOfType<BadRequestObjectResult>();
        (await w.Public().Controller.Decide(token, new PublicReviewDecisionRequest("approved", "Ana", null!), CancellationToken.None)).Should_BeStatus(201);
    }

    [Fact]
    public void The_write_actions_cap_the_request_body()
    {
        foreach (string action in new[] { nameof(ReviewLinksPublicController.AddComment), nameof(ReviewLinksPublicController.Decide) })
            typeof(ReviewLinksPublicController).GetMethod(action)!.GetCustomAttributes(typeof(RequestSizeLimitAttribute), true)
                .Should().ContainSingle();
    }
}
